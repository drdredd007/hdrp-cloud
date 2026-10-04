using System;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>A conservative local interval with an optional physical-metre derivative bound.</summary>
    public readonly struct SurfaceLandformHeightInterval
    {
        public readonly double MinimumHeight, MaximumHeight;
        /// <summary>
        /// Height metres per reference-sphere metre for a constant footprint. Infinity means the optional
        /// proof was unavailable; a spatially varying parent morph cannot inherit this derivative directly.
        /// </summary>
        public readonly double MaximumSlope;
        /// <summary>
        /// Absolute PU evaluation roundoff in metres, independent of the spatial cell diameter.
        /// This reserve survives signed cancellation and interval tightening. Infinity denotes an
        /// unavailable arithmetic proof; callers then retain the complete height-range fallback.
        /// </summary>
        public readonly double ArithmeticErrorMetres;
        public double Width => MaximumHeight - MinimumHeight;
        internal SurfaceLandformHeightInterval(double minimum, double maximum, double maximumSlope = double.PositiveInfinity,
            double arithmeticErrorMetres = double.PositiveInfinity)
        { MinimumHeight = minimum; MaximumHeight = maximum; MaximumSlope = maximumSlope; ArithmeticErrorMetres = arithmeticErrorMetres; }
        public double MaximumDifference(in SurfaceLandformHeightInterval other) =>
            math.max(math.abs(MinimumHeight - other.MaximumHeight), math.abs(MaximumHeight - other.MinimumHeight));
    }

    /// <summary>
    /// Worker-only spherical-cap bounds for captured PU controls. All influential supports are queried,
    /// including supports in other KD leaves/faces. Positive normalized weights enclose the result in the
    /// min/max of the bounded Taylor terms. Derived data is borrowed explicitly; this never builds a filter.
    /// </summary>
    public static class SurfaceLandformBounds
    {
        const double Epsilon = 2.2204460492503130808472633361816e-16;
        const double UnitPadding = 64 * Epsilon;
        const double ArithmeticGamma = 4096 * Epsilon / (1 - 4096 * Epsilon);
        readonly struct Visit
        {
            public readonly int Node, Depth;
            public Visit(int node, int depth) { Node = node; Depth = depth; }
        }
        readonly struct WeightedControl
        {
            public readonly int Encoded;
            public readonly double Minimum, Maximum, QLow, QHigh;
            public WeightedControl(int encoded, double minimum, double maximum, double qLow, double qHigh)
            { Encoded = encoded; Minimum = minimum; Maximum = maximum; QLow = qLow; QHigh = qHigh; }
        }

        /// <summary>
        /// Complete means the entire cap query was visited. Exhaustion returns the finite global envelope
        /// with MeasurementBudgetExceeded; missing/mismatched derived data returns MissingData, never Full.
        /// Work debits every visited node and reference, including duplicated references and rejected supports.
        /// </summary>
        public static SurfaceErrorStatus TryBound(in NativeSurfaceLandformView full,
            in NativeSurfaceLandformFilterView filter, double3 center, double angularRadius,
            SurfaceSamplingFootprint footprint, int maximumWork, out SurfaceLandformHeightInterval interval, out int work,
            Func<bool> cancelled = null)
        {
            work = 0; interval = default;
            if (!CubeSurface.TryNormalize(center, out center) || !math.isfinite(angularRadius) || angularRadius < 0 ||
                angularRadius > Math.PI || !footprint.IsValid || maximumWork < 0)
                throw new ArgumentException("Local landform bounds require a finite spherical cap, footprint and work budget.");
            if (!full.Enabled || !full.Controls.IsCreated || !full.Index.IsCreated || !full.References.IsCreated ||
                full.Controls.Length == 0 || full.Index.Length == 0 || !(full.Radius > 0) || !math.isfinite(full.Radius) ||
                !math.isfinite(full.MinimumHeight) || !math.isfinite(full.MaximumHeight) || full.MinimumHeight > full.MaximumHeight)
                return SurfaceErrorStatus.MissingData;
            interval = Global(full);
            if (!math.isfinite(interval.MinimumHeight) || !math.isfinite(interval.MaximumHeight)) return SurfaceErrorStatus.MissingData;
            if (footprint.Metres > 0 && !filter.Matches(full)) return SurfaceErrorStatus.MissingData;
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            double chord = math.min(2, 2 * math.sin(angularRadius * .5) + UnitPadding);
            if (footprint.Metres == 0 || filter.MaximumLevel == 0 ||
                footprint.Metres <= full.Radius / (2 * (1 << filter.MaximumLevel)))
                return Level(full, filter, center, chord, -1, maximumWork, ref work, out interval, cancelled);
            for (int upper = filter.MaximumLevel; upper >= 1; upper--)
            {
                if (footprint.Metres > full.Radius / (2 * (1 << (upper - 1)))) continue;
                double t = math.clamp(footprint.Metres / (full.Radius / (2 * (1 << upper))) - 1, 0, 1);
                t = t * t * (3 - 2 * t);
                var status = Level(full, filter, center, chord, upper, maximumWork, ref work, out var a, cancelled);
                if (status != SurfaceErrorStatus.Complete) { interval = Global(full); return status; }
                if (t == 0) { interval = a; return status; }
                status = Level(full, filter, center, chord, upper - 1, maximumWork, ref work, out var b, cancelled);
                if (status != SurfaceErrorStatus.Complete) { interval = Global(full); return status; }
                interval = Padded(math.lerp(a.MinimumHeight, b.MinimumHeight, t), math.lerp(a.MaximumHeight, b.MaximumHeight, t),
                    Product(1 - t, a.MaximumSlope) + Product(t, b.MaximumSlope), BlendArithmetic(a, b, t));
                return status;
            }
            return Level(full, filter, center, chord, 0, maximumWork, ref work, out interval, cancelled);
        }

        static SurfaceErrorStatus Level(in NativeSurfaceLandformView full, in NativeSurfaceLandformFilterView filter,
            double3 center, double chord, int level, int budget, ref int work, out SurfaceLandformHeightInterval interval, Func<bool> cancelled)
        {
            interval = Global(full);
            bool original = level < 0 || level == filter.MaximumLevel;
            var nodes = original ? full.Index : filter.Index;
            var references = original ? full.References : filter.References;
            int root = 0, firstNode = 0, nodeCount = nodes.Length, firstReference = 0, referenceCount = references.Length;
            if (!original)
            {
                if (level < 0 || level >= filter.Levels.Length) return SurfaceErrorStatus.MissingData;
                var header = filter.Levels[level]; root = header.Root; firstNode = header.FirstIndex; nodeCount = header.IndexCount;
                firstReference = header.FirstReference; referenceCount = header.ReferenceCount;
                if (header.Level != level || firstNode < 0 || nodeCount <= 0 || (long)firstNode + nodeCount > nodes.Length ||
                    firstReference < 0 || referenceCount < 0 || (long)firstReference + referenceCount > references.Length)
                    return SurfaceErrorStatus.MissingData;
            }
            var stack = new FixedList512Bytes<Visit>(); stack.Add(new Visit(root, 0));
            var candidates = new FixedList4096Bytes<int>(); bool canTighten = true;
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity, arithmeticMagnitude = 0;
            while (stack.Length > 0)
            {
                if ((work & 63) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException();
                if (work >= budget) return SurfaceErrorStatus.MeasurementBudgetExceeded;
                work++;
                var visit = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                if (visit.Node < firstNode || visit.Node >= (long)firstNode + nodeCount || visit.Depth > SurfaceLandformField.MaximumIndexDepth)
                    return SurfaceErrorStatus.MissingData;
                var node = nodes[visit.Node];
                if (!node.IsLeaf)
                {
                    if (node.Axis < 0 || node.Axis > 2 || !math.isfinite(node.Split) ||
                        visit.Depth == SurfaceLandformField.MaximumIndexDepth) return SurfaceErrorStatus.MissingData;
                    // Closed AABB contains the entire spherical cap; split-boundary contacts query both leaves.
                    if (center[node.Axis] - chord <= node.Split) stack.Add(new Visit(node.Left, visit.Depth + 1));
                    if (center[node.Axis] + chord >= node.Split) stack.Add(new Visit(node.Right, visit.Depth + 1));
                    continue;
                }
                if (node.Count < 0 || node.Count > SurfaceLandformField.MaximumLeafCandidates || node.First < firstReference ||
                    (long)node.First + node.Count > (long)firstReference + referenceCount) return SurfaceErrorStatus.MissingData;
                for (int i = 0; i < node.Count; i++)
                {
                    if ((work & 63) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException();
                    if (work >= budget) return SurfaceErrorStatus.MeasurementBudgetExceeded;
                    work++;
                    int encoded = references[node.First + i]; SurfaceLandformControl control;
                    if (original || encoded < 0)
                    {
                        int id = original ? encoded : -(encoded + 1);
                        if (id < 0 || id >= full.Controls.Length) return SurfaceErrorStatus.MissingData;
                        control = full.Controls[id];
                    }
                    else
                    {
                        if (encoded >= filter.DerivedControls.Length) return SurfaceErrorStatus.MissingData;
                        control = filter.DerivedControls[encoded];
                    }
                    double separation = math.length(center - control.Direction);
                    if (math.max(0, separation - chord - UnitPadding) > control.SupportMetres / full.Radius) continue;
                    if (canTighten)
                    {
                        if (candidates.Length < candidates.Capacity) candidates.Add(encoded);
                        else canTighten = false;
                    }
                    // |dot(g, R*(d-c)) - dot(g, R*(center-c))| <= |g|*R*capChord.
                    // The saturating Taylor correction x/sqrt(1+(x/V)^2) is monotonic, so its endpoint
                    // interval is exact in real arithmetic. Padding covers normalization/dot roundoff.
                    double gradientRadius = math.length(control.Gradient) * full.Radius;
                    double x = math.dot(control.Gradient, (center - control.Direction) * full.Radius);
                    double reach = gradientRadius * (chord + UnitPadding);
                    if (!math.isfinite(x) || !math.isfinite(reach)) return SurfaceErrorStatus.SupportBudgetExceeded;
                    double lo = control.Height, hi = control.Height;
                    if (control.VariationLimit > 0)
                    {
                        lo += Saturate(x - reach, control.VariationLimit);
                        hi += Saturate(x + reach, control.VariationLimit);
                    }
                    if (!math.isfinite(lo) || !math.isfinite(hi)) return SurfaceErrorStatus.MissingData;
                    minimum = math.min(minimum, lo); maximum = math.max(maximum, hi);
                    // Use the uncancelled operand magnitude, not the final signed Taylor term or the
                    // tightened weighted result. |dot(g,offset)| may be tiny after large products cancel.
                    double operandMagnitude = math.abs(control.Height);
                    if (control.VariationLimit > 0)
                        operandMagnitude += math.dot(math.abs(control.Gradient),
                            (math.abs(center - control.Direction) + chord + UnitPadding) * full.Radius);
                    arithmeticMagnitude = math.max(arithmeticMagnitude, operandMagnitude);
                }
            }
            if (!math.isfinite(minimum) || !math.isfinite(maximum)) return SurfaceErrorStatus.MissingData;
            interval = Padded(minimum, maximum, double.PositiveInfinity,
                ArithmeticGamma * arithmeticMagnitude * (1 + 64 * Epsilon));
            // The safe term union is already complete. Optional positive-weight refinement must never
            // invalidate it or manufacture additional height authority when its own work is unavailable.
            if (canTighten && Tighten(full, filter, original, center, chord, ref candidates, budget, ref work,
                interval, cancelled, out var tight)) interval = tight;
            return SurfaceErrorStatus.Complete;
        }
        static bool Tighten(in NativeSurfaceLandformView full, in NativeSurfaceLandformFilterView filter, bool original,
            double3 center, double chord, ref FixedList4096Bytes<int> candidates, int budget, ref int work,
            SurfaceLandformHeightInterval union, Func<bool> cancelled, out SurfaceLandformHeightInterval result)
        {
            result = union;
            // Fixed banks use stack storage, not a managed/native allocation. A large patch keeps its
            // complete union. Reserve only a bounded optional slice, so later fine/parent queries can proceed.
            int optionalEnd = (int)math.min((long)budget, (long)work + 2048);
            var hash = new FixedList4096Bytes<int>();
            for (int i = 0; i < 128; i++)
            { if (!SpendOptional(optionalEnd, ref work, cancelled)) return false; hash.Add(0); }
            var records = new FixedList4096Bytes<WeightedControl>(); double scale = 1, floor = union.MinimumHeight;
            int anchorIndex = -1;
            for (int i = 0; i < candidates.Length; i++)
            {
                int encoded = candidates[i];
                int slot = (int)(unchecked((uint)encoded * 2654435761u) & 127);
                bool duplicate = false;
                while (true)
                {
                    if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                    int found = hash[slot];
                    if (found == 0) break;
                    if (records[found - 1].Encoded == encoded) { duplicate = true; break; }
                    slot = (slot + 1) & 127;
                }
                if (duplicate) continue;
                if (records.Length == 96 || !SpendOptional(optionalEnd, ref work, cancelled)) return false;
                SurfaceLandformControl control = original || encoded < 0 ?
                    full.Controls[original ? encoded : -(encoded + 1)] : filter.DerivedControls[encoded];
                double distance = math.length(center - control.Direction), factor = full.Radius / control.SupportMetres;
                double qLowDistance = math.max(0, distance - chord - UnitPadding) * factor;
                double qHighDistance = (distance + chord + UnitPadding) * factor;
                double qLow = qLowDistance * qLowDistance * (1 - 64 * Epsilon);
                double qHigh = qHighDistance * qHighDistance * (1 + 64 * Epsilon);
                if (!(qLow >= 0) || !math.isfinite(qHigh)) return false;
                // Exactly one possibly singular control can be bounded through its finite lower weight.
                // Several anchors keep the complete term union; the numerical PU sampler is untouched.
                if (qLow == 0)
                {
                    if (anchorIndex >= 0 || !(qHigh > 0) || qHigh >= 1) return false;
                    anchorIndex = records.Length;
                }
                double x = math.dot(control.Gradient, (center - control.Direction) * full.Radius);
                double reach = math.length(control.Gradient) * full.Radius * (chord + UnitPadding);
                if (!math.isfinite(x) || !math.isfinite(reach)) return false;
                double low = control.Height, high = control.Height;
                if (control.VariationLimit > 0)
                { low += Saturate(x - reach, control.VariationLimit); high += Saturate(x + reach, control.VariationLimit); }
                var term = Padded(low, high); floor = math.min(floor, term.MinimumHeight);
                if (qLow > 0) scale = math.min(scale, qLow);
                records.Add(new WeightedControl(encoded, term.MinimumHeight, term.MaximumHeight, qLow, qHigh));
                hash[slot] = records.Length;
            }
            if (anchorIndex >= 0)
            {
                bool accepted = TightenAroundAnchor(ref records, anchorIndex, scale, optionalEnd, ref work, union, cancelled, out result);
                if (accepted && TrySlope(full, filter, original, ref records, anchorIndex, scale, 0, union.Width,
                    optionalEnd, ref work, cancelled, out double slope))
                    result = WithSlope(result, slope, full.Radius);
                return accepted;
            }
            double denominatorLow = 0, denominatorHigh = 0, numeratorLow = 0, numeratorHigh = 0;
            for (int i = 0; i < records.Length; i++)
            {
                if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                var c = records[i];
                // qMin^2 is common to every normalized PU weight and cancels. Any common positive
                // scale is therefore valid; min(qLow) keeps all ratios <=1 without overflowing at anchors.
                double weightLow = Weight(c.QHigh, scale) * (1 - 512 * Epsilon);
                double weightHigh = Weight(c.QLow, scale) * (1 + 512 * Epsilon);
                // Shift by a certified common floor before interval multiplication. Negative signed
                // heights then cannot reverse numerator inequalities or introduce a false quotient bound.
                if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                numeratorLow += weightLow * math.max(0, c.Minimum - floor);
                numeratorHigh += weightHigh * math.max(0, c.Maximum - floor);
                denominatorLow += weightLow; denominatorHigh += weightHigh;
            }
            if (!(denominatorLow > 0) || !(denominatorHigh > 0)) return false;
            double guard = 4096 * Epsilon;
            double minimum = floor + numeratorLow * (1 - guard) / (denominatorHigh * (1 + guard));
            double maximum = floor + numeratorHigh * (1 + guard) / (denominatorLow * (1 - guard));
            if (!math.isfinite(minimum) || !math.isfinite(maximum)) return false;
            bool refined = Intersect(union, minimum, maximum, out result);
            if (refined && TrySlope(full, filter, original, ref records, -1, scale, denominatorLow, union.Width,
                optionalEnd, ref work, cancelled, out double maximumSlope))
                result = WithSlope(result, maximumSlope, full.Radius);
            return refined;
        }
        static bool TrySlope(in NativeSurfaceLandformView full, in NativeSurfaceLandformFilterView filter, bool original,
            ref FixedList4096Bytes<WeightedControl> records, int anchorIndex, double scale, double denominatorLow,
            double termWidth, int optionalEnd, ref int work, Func<bool> cancelled, out double maximumSlope)
        {
            maximumSlope = double.PositiveInfinity;
            double a = 0, aDerivative = 0, anchorQDerivative = 0;
            if (anchorIndex >= 0)
            {
                if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                var anchor = records[anchorIndex]; var control = Control(full, filter, original, anchor.Encoded);
                double q = anchor.QHigh, tail = 1 - q, tail2 = tail * tail, tail4 = tail2 * tail2;
                // Divide every raw weight by the anchor weight. A(q)=q^2/(1-q)^4 has a finite
                // derivative at q=0; this avoids any singular qLow or an epsilon-flat authority disc.
                a = q * q / tail4;
                aDerivative = (2 * q + 4 * q * q / tail) / tail4;
                anchorQDerivative = 2 * math.sqrt(q) / control.SupportMetres;
                if (!math.isfinite(a) || !math.isfinite(aDerivative) || !math.isfinite(anchorQDerivative)) return false;
            }
            else if (!(denominatorLow > 0)) return false;
            double termSlope = 0, weightSlope = 0;
            for (int i = 0; i < records.Length; i++)
            {
                if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                var c = records[i]; var control = Control(full, filter, original, c.Encoded);
                // Saturating Taylor correction has |derivative|<=1, hence each term's slope<=|g|.
                termSlope = math.max(termSlope, math.length(control.Gradient) * (1 + 128 * Epsilon));
                if (i == anchorIndex || c.QLow >= 1) continue;
                if (!(c.QLow > 0) || !SpendOptional(optionalEnd, ref work, cancelled)) return false;
                double q = c.QLow, tail = 1 - q, tail2 = tail * tail, tail3 = tail2 * tail, tail4 = tail2 * tail2;
                double qDerivative = 2 * math.sqrt(math.min(1, c.QHigh)) / control.SupportMetres;
                double contribution;
                if (anchorIndex >= 0)
                {
                    double q2 = q * q;
                    double b = tail4 / q2;
                    double bDerivative = (4 * tail3 + 2 * tail4 / q) / q2;
                    contribution = aDerivative * anchorQDerivative * b + a * bDerivative * qDerivative;
                }
                else
                {
                    double ratio = scale / q;
                    // Raw dw/dq remains finite at the compact-support q=1 edge. Bounding log(w)
                    // there would introduce an artificial infinity despite the C3 weight cutoff.
                    contribution = ratio * ratio * (4 * tail3 + 2 * tail4 / q) * qDerivative;
                }
                if (!math.isfinite(contribution) || contribution < 0) return false;
                weightSlope += contribution * (1 + 1024 * Epsilon);
            }
            if (!math.isfinite(weightSlope) || !SpendOptional(optionalEnd, ref work, cancelled)) return false;
            // Positive PU: df=sum(phi_i*dterm_i)+sum((term_i-f)*dw_i)/sum(w_i).
            // Every |term_i-f| is enclosed by the complete term union, not a finite sampled maximum.
            // Relative-to-anchor weights have denominator>=1, including the exact anchor limit.
            double divisor = anchorIndex >= 0 ? 1 : denominatorLow * (1 - 4096 * Epsilon);
            double slope = (termSlope + termWidth * weightSlope / divisor) * (1 + 4096 * Epsilon);
            if (!math.isfinite(slope) || slope < 0) return false;
            maximumSlope = slope; return true;
        }
        static SurfaceLandformControl Control(in NativeSurfaceLandformView full, in NativeSurfaceLandformFilterView filter,
            bool original, int encoded) => original || encoded < 0 ?
            full.Controls[original ? encoded : -(encoded + 1)] : filter.DerivedControls[encoded];
        static bool TightenAroundAnchor(ref FixedList4096Bytes<WeightedControl> records, int anchorIndex,
            double scale, int optionalEnd, ref int work, SurfaceLandformHeightInterval union, Func<bool> cancelled,
            out SurfaceLandformHeightInterval result)
        {
            result = union; var anchor = records[anchorIndex];
            // The common scale cancels from normalized weights. Choosing it below every positive qLow
            // and anchor.qHigh keeps these finite weight estimates <=1, including an exact q=0 center.
            scale = math.min(scale, anchor.QHigh);
            if (!(scale > 0) || !SpendOptional(optionalEnd, ref work, cancelled)) return false;
            double anchorWeightLow = Weight(anchor.QHigh, scale) * (1 - 512 * Epsilon);
            if (!(anchorWeightLow > 0) || !math.isfinite(anchorWeightLow)) return false;
            double otherWeightHigh = 0, otherMinimum = double.PositiveInfinity, otherMaximum = double.NegativeInfinity;
            for (int i = 0; i < records.Length; i++)
            {
                if (!SpendOptional(optionalEnd, ref work, cancelled)) return false;
                if (i == anchorIndex) continue;
                var c = records[i];
                if (!(c.QLow > 0) || !SpendOptional(optionalEnd, ref work, cancelled)) return false;
                otherWeightHigh += Weight(c.QLow, scale) * (1 + 512 * Epsilon);
                otherMinimum = math.min(otherMinimum, c.Minimum); otherMaximum = math.max(otherMaximum, c.Maximum);
            }
            if (!math.isfinite(otherWeightHigh)) return false;
            double minimum = anchor.Minimum, maximum = anchor.Maximum;
            if (records.Length > 1)
            {
                if (!math.isfinite(otherMinimum) || !math.isfinite(otherMaximum) ||
                    !SpendOptional(optionalEnd, ref work, cancelled)) return false;
                // phi=B/(A+B) is increasing in B and decreasing in A. BUpper and ALower thus give a
                // certified upper fraction for all non-anchor terms, even while A tends to infinity.
                // The guard encloses accumulation/division rounding; phi<=1 is an exact invariant.
                double guard = 4096 * Epsilon;
                double b = otherWeightHigh * (1 + guard);
                double fractionHigh = math.min(1, b / ((anchorWeightLow + b) * (1 - guard)));
                if (!math.isfinite(fractionHigh)) return false;
                // Signed terms are safe: select the adverse endpoint only when moving towards the
                // other-control union can lower/raise the anchor endpoint. No nonnegative-height assumption.
                minimum += math.min(0, fractionHigh * (otherMinimum - anchor.Minimum));
                maximum += math.max(0, fractionHigh * (otherMaximum - anchor.Maximum));
            }
            return Intersect(union, minimum, maximum, out result);
        }
        static bool Intersect(SurfaceLandformHeightInterval union, double minimum, double maximum,
            out SurfaceLandformHeightInterval result)
        {
            result = union;
            if (!math.isfinite(minimum) || !math.isfinite(maximum)) return false;
            var padded = Padded(minimum, maximum);
            minimum = math.max(union.MinimumHeight, padded.MinimumHeight); maximum = math.min(union.MaximumHeight, padded.MaximumHeight);
            if (minimum > maximum) return false;
            result = new SurfaceLandformHeightInterval(minimum, maximum, double.PositiveInfinity, union.ArithmeticErrorMetres); return true;
        }
        static bool SpendOptional(int budget, ref int work, Func<bool> cancelled)
        {
            if ((work & 63) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException();
            if (work >= budget) return false; work++; return true;
        }
        static double Weight(double q, double scale)
        {
            if (q >= 1) return 0;
            double tail = 1 - q, ratio = scale / q;
            return (tail * tail) * (tail * tail) * (ratio * ratio);
        }
        static double Saturate(double x, double variation)
        {
            double scaled = x / variation;
            // Avoid an overflow in a conservative metadata calculation; the actual sampler is untouched.
            if (!math.isfinite(scaled * scaled)) return x < 0 ? -variation : variation;
            return x / math.sqrt(1 + scaled * scaled);
        }
        static SurfaceLandformHeightInterval Global(in NativeSurfaceLandformView full) => Padded(full.MinimumHeight, full.MaximumHeight);
        static double Product(double a, double b) => a == 0 || b == 0 ? 0 : a * b;
        static SurfaceLandformHeightInterval WithSlope(SurfaceLandformHeightInterval interval, double slope, double radius)
        {
            // Support<=2R, robust unit normalization and the two q constructions perturb each
            // control's offset by <=64eps*R. The derivative proof uses absolute weight sensitivities,
            // so this also encloses independent q rounding, rather than assuming one common shift.
            // All-zero terms have exact zero output; do not introduce an arbitrary amplitude floor.
            double arithmetic = interval.ArithmeticErrorMetres == 0 ? 0 :
                (interval.ArithmeticErrorMetres + slope * radius * UnitPadding) * (1 + 64 * Epsilon);
            return new SurfaceLandformHeightInterval(interval.MinimumHeight, interval.MaximumHeight, slope, arithmetic);
        }
        static double BlendArithmetic(SurfaceLandformHeightInterval a, SurfaceLandformHeightInterval b, double t)
        {
            if (a.ArithmeticErrorMetres == 0 && b.ArithmeticErrorMetres == 0) return 0;
            double magnitude = math.max(math.max(math.abs(a.MinimumHeight), math.abs(a.MaximumHeight)),
                math.max(math.abs(b.MinimumHeight), math.abs(b.MaximumHeight)));
            // A dyadic blend adds a subtraction, multiply and add after the two certified PU calls.
            return (Product(1 - t, a.ArithmeticErrorMetres) + Product(t, b.ArithmeticErrorMetres) +
                (16 * Epsilon / (1 - 16 * Epsilon)) * magnitude) * (1 + 64 * Epsilon);
        }
        static SurfaceLandformHeightInterval Padded(double minimum, double maximum, double maximumSlope = double.PositiveInfinity,
            double arithmeticErrorMetres = double.PositiveInfinity)
        {
            // Leaf sums contain at most 96 terms. This also bounds the two normalized PU evaluations
            // and the dyadic lerp; it is an arithmetic guard, not an assumed physical slope.
            double pad = 4096 * Epsilon * math.max(1, math.max(math.abs(minimum), math.abs(maximum)));
            return new SurfaceLandformHeightInterval(minimum - pad, maximum + pad, maximumSlope, arithmeticErrorMetres);
        }
    }
}
