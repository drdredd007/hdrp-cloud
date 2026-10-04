using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    // Worker-only certificates for the fixed algorithm-1 positive P/A mip operator.
    // They read captured raw metadata, never a renderer cache or mutable GPU owner.
    internal static class SurfaceRegionFilterBounds
    {
        const double DoubleEpsilon = 2.22044604925031308085e-16;
        const double FloatUnitRoundoff = 5.9604644775390625e-8;
        const double FloatMinimumNormal = 1.1754943508222875e-38;
        internal readonly struct Domain
        {
            internal readonly double2 Low, High;
            internal readonly double Reach;
            internal readonly int Level;
            internal readonly bool Outside;
            internal Domain(double2 low, double2 high, double reach, int level, bool outside)
            { Low = low; High = high; Reach = reach; Level = level; Outside = outside; }
        }
        internal readonly struct Raw
        {
            internal readonly double Minimum, Maximum, HeightSlope, MaskMinimum, MaskMaximum, MaskSlope;
            internal Raw(double minimum, double maximum, double heightSlope, double maskMinimum, double maskMaximum, double maskSlope)
            { Minimum = minimum; Maximum = maximum; HeightSlope = heightSlope; MaskMinimum = maskMinimum; MaskMaximum = maskMaximum; MaskSlope = maskSlope; }
        }
        internal readonly struct Error
        {
            internal readonly double Height, Coverage, Gain;
            internal Error(double height, double coverage, double gain) { Height = height; Coverage = coverage; Gain = gain; }
        }

        internal static bool HasGeometry(SurfaceRegionHeader region) => region.Mode == SurfaceRegionMode.Replace ||
            region.MinimumHeight != 0 || region.MaximumHeight != 0 || region.DetailPolicy == SurfaceDetailPolicy.Suppress;

        internal static bool TryCanonicalDomain(SurfaceRegionHeader region, double3 center, double angle, out Domain domain)
        {
            domain = default;
            var p = region.Projection;
            if (!ProjectionInterval(p, center, angle, out var low, out var high, out bool horizon)) return false;
            bool outside = horizon || math.any(low < p.MinimumMetres) || math.any(high > p.MaximumMetres);
            low = math.max(low, p.MinimumMetres); high = math.min(high, p.MaximumMetres);
            if (math.any(high < low)) return false;
            domain = new Domain(low, high, 0, 0, outside); return true;
        }

        static bool ProjectionInterval(SurfaceRegionProjection p, double3 center, double angle,
            out double2 low, out double2 high, out bool horizon)
        {
            low = high = default; horizon = false;
            double reserve = 128 * DoubleEpsilon;
            SphereDotInterval(center,angle,p.AnchorDirection,out double dlo,out double dhi);
            if (dhi <= 0) return false;
            horizon = dlo <= 0;
            if (horizon)
            { var extent = p.MaximumMetres - p.MinimumMetres; low = p.MinimumMetres - 2 * extent; high = p.MaximumMetres + 2 * extent; }
            else
            {
                SphereDotInterval(center,angle,p.Right,out double rightLow,out double rightHigh);
                SphereDotInterval(center,angle,p.Forward,out double forwardLow,out double forwardHigh);
                AxisInterval(rightLow,rightHigh,dlo,dhi,p.Radius,out double lx,out double hx);
                AxisInterval(forwardLow,forwardHigh,dlo,dhi,p.Radius,out double ly,out double hy);
                double padding = p.Radius * reserve / dlo;
                low = new double2(lx, ly) - padding; high = new double2(hx, hy) + padding;
            }
            return true;
        }

        // The cap includes the stitched geometry halo. Ratio bounds cover the complete cap,
        // including cube seams; a gnomonic horizon crossing conservatively uses the whole page.
        internal static bool TryDomain(SurfaceRegionHeader region, double3 center, double angle,
            SurfaceSamplingFootprint footprint, out Domain domain)
        {
            domain = default;
            if (footprint.Metres == 0 || !HasGeometry(region)) return false;
            var p = region.Projection; var extent = p.MaximumMetres - p.MinimumMetres;
            var n = region.Resolution; int levels = 0;
            while (n.x > 1 || n.y > 1) { n = math.max(new int2(1), (n + 1) / 2); levels++; }
            if (levels == 0) return false; // The actual filter aliases Full for a 1x1 source.
            if (!ProjectionInterval(p, center, angle, out var low, out var high, out bool horizon)) return false;
            // Every derived level has two zero support nodes beyond its metric page.
            if (math.any(high < p.MinimumMetres - 2 * extent) || math.any(low > p.MaximumMetres + 2 * extent)) return false;
            double spacing = math.cmin(extent / (double2)region.Resolution);
            double scale = horizon ? double.PositiveInfinity : 1 + math.lengthsq(math.max(math.abs(low), math.abs(high)) / p.Radius);
            double ratio = footprint.Metres * 2 * scale / spacing * (1 + 128 * DoubleEpsilon);
            if (ratio <= 1) return false; // Both fine and parent are exact Full aliases.
            int maximumLevel = !math.isfinite(ratio) ? levels : math.min(levels, (int)Math.Floor(Math.Log(ratio, 2)) + 1);
            maximumLevel = math.max(1, maximumLevel);
            double2 reach = 0; n = region.Resolution;
            for (int level = 1; level <= maximumLevel; level++)
            { n = math.max(new int2(1), (n + 1) / 2); reach += 1.5 * extent / (double2)n; }
            // A level's bilinear corner is at most one step away, and its 3x3 tent
            // samples at most another half-step away. The recursion adds these reaches.
            double2 arithmeticReach = 128 * DoubleEpsilon * (maximumLevel + 1) *
                (math.max(math.abs(p.MinimumMetres), math.abs(p.MaximumMetres)) + extent + math.max(math.abs(low), math.abs(high)));
            reach += arithmeticReach;
            var expandedLow = low - reach; var expandedHigh = high + reach;
            bool outside = horizon || math.any(expandedLow < p.MinimumMetres) || math.any(expandedHigh > p.MaximumMetres);
            expandedLow = math.max(expandedLow, p.MinimumMetres); expandedHigh = math.min(expandedHigh, p.MaximumMetres);
            if (math.any(expandedHigh < expandedLow)) return false;
            domain = new Domain(expandedLow, expandedHigh, horizon ? double.PositiveInfinity : math.length(reach), maximumLevel, outside);
            return true;
        }
        // Extremise an axis dot product over the actual spherical cap, rather than its
        // Cartesian chord ball. In particular an antipodal cap <90 degrees stays negative.
        internal static void SphereDotInterval(double3 center,double angle,double3 axis,out double low,out double high)
        {
            double length=math.length(axis),dot=math.dot(center,axis),reserve=128*DoubleEpsilon*length;
            double cosine=Math.Cos(math.clamp(angle,0,Math.PI)),sine=Math.Sin(math.clamp(angle,0,Math.PI));
            double transverse=Math.Sqrt(math.max(0,length*length-dot*dot)+reserve*length);
            low=dot<=-length*cosine+reserve?-length:dot*cosine-transverse*sine;
            high=dot>=length*cosine-reserve?length:dot*cosine+transverse*sine;
            low-=reserve;high+=reserve;
        }
        static void AxisInterval(double numeratorLow,double numeratorHigh,double dlo,double dhi,double scale,out double low,out double high)
        {
            double a = numeratorLow / dlo, b = numeratorLow / dhi;
            double c = numeratorHigh / dlo, d = numeratorHigh / dhi;
            low = math.min(math.min(a, b), math.min(c, d)) * scale;
            high = math.max(math.max(a, b), math.max(c, d)) * scale;
        }

        internal static void Coverage(SurfaceRegionHeader region, in Domain domain, in Raw raw,
            out double amin, out double amax, out double alphaSlope)
        {
            amin = domain.Outside ? 0 : math.clamp(raw.MaskMinimum, 0, 1);
            amax = math.clamp(raw.MaskMaximum, 0, 1);
            alphaSlope = raw.MaskSlope;
            if (region.BlendMetres > 0)
            {
                var p = region.Projection;
                double border = math.cmin(math.min(domain.Low - p.MinimumMetres, p.MaximumMetres - domain.High));
                double t = math.clamp(border / region.BlendMetres, 0, 1);
                amin *= t * t * (3 - 2 * t);
                // The smooth feather is identically one in the interior plateau. A
                // distant page border cannot impose its maximum derivative on this cap.
                if(border<region.BlendMetres)alphaSlope += amax * 1.5 / region.BlendMetres;
            }
            else if (domain.Outside && amax > 0) alphaSlope = double.PositiveInfinity;
        }

        internal static Error Bound(SurfaceRegionHeader region, in Domain domain, in Raw raw, double priorMinimum, double priorMaximum)
        {
            double center = priorMinimum * .5 + priorMaximum * .5;
            double priorDistance = math.max(math.abs(priorMinimum - center), math.abs(priorMaximum - center));
            Coverage(region, domain, raw, out double amin, out double amax, out double alphaSlope);
            double hlo = raw.Minimum - center, hhi = raw.Maximum - center;
            ProductInterval(hlo, hhi, amin, amax, out double qlo, out double qhi);
            if (domain.Outside) { qlo = math.min(qlo, 0); qhi = math.max(qhi, 0); }
            double localMagnitude = math.max(math.abs(hlo), math.abs(hhi));
            double qSlope = Product(amax, raw.HeightSlope) + Product(localMagnitude, alphaSlope);
            double qDifference = math.min(qhi - qlo, Product(qSlope, domain.Reach));
            double alphaDifference = math.min(amax - amin, Product(alphaSlope, domain.Reach));
            // Positive kernels cannot amplify previous pack errors. Reserve each actual
            // float2 cast, double interpolation/sum arithmetic, and FTZ subnormal loss.
            double heightMagnitude = math.max(math.abs(raw.Minimum), math.abs(raw.Maximum));
            double perStage = FloatUnitRoundoff + 128 * DoubleEpsilon;
            double amplification = Math.Pow(1 + perStage, domain.Level + 1) - 1;
            double alphaReserve = amplification + (domain.Level + 1) * FloatMinimumNormal;
            double pairReserve = (heightMagnitude + math.abs(center)) * amplification +
                (domain.Level + 1) * FloatMinimumNormal * (1 + math.abs(center));
            double coverage = alphaDifference + alphaReserve;
            double shifted = qDifference + pairReserve;
            double height;
            if (region.Mode == SurfaceRegionMode.Replace) height = shifted + Product(coverage, priorDistance);
            else
            {
                ProductInterval(raw.Minimum, raw.Maximum, amin, amax, out double plo, out double phi);
                if (domain.Outside) { plo = math.min(plo, 0); phi = math.max(phi, 0); }
                double slope = Product(amax, raw.HeightSlope) + Product(heightMagnitude, alphaSlope);
                height = math.min(phi - plo, Product(slope, domain.Reach)) + heightMagnitude * amplification +
                    (domain.Level + 1) * FloatMinimumNormal;
            }
            return new Error(Outward(height), Outward(coverage), Outward(1 - amin + alphaReserve));
        }
        internal static Error Global(SurfaceRegionHeader region, double priorMinimum, double priorMaximum)
        {
            double height = math.max(math.abs(region.MinimumHeight), math.abs(region.MaximumHeight));
            double prior = math.max(math.abs(priorMinimum), math.abs(priorMaximum));
            double reserve = (height + prior + 1) * .00001; // More than every algorithm-1 (<=31) float pack reserve.
            return new Error(Outward(2 * height + (region.Mode == SurfaceRegionMode.Replace ? prior : 0) + reserve), 1.00001, 1.00001);
        }
        internal static Error Alias(SurfaceRegionHeader region, double priorMinimum, double priorMaximum, double amin)
        {
            // Level zero avoids float packing, but the filtered branch executes F*(1-A)+P
            // whereas canonical replacement executes lerp(F,H,A). Preserve that arithmetic reserve.
            double magnitude = math.max(math.abs(region.MinimumHeight), math.abs(region.MaximumHeight)) +
                math.max(math.abs(priorMinimum), math.abs(priorMaximum));
            return new Error(Outward(magnitude * (128 * DoubleEpsilon)), 128 * DoubleEpsilon, Outward(1 - amin + 128 * DoubleEpsilon));
        }
        static void ProductInterval(double lo, double hi, double alo, double ahi, out double minimum, out double maximum)
        { minimum = math.min(lo * alo, lo * ahi); maximum = math.max(hi * alo, hi * ahi); }
        internal static double Product(double a, double b) => a == 0 || b == 0 ? 0 : a * b;
        internal static double Outward(double value) => value == 0 ? 0 : value + math.abs(value) * (128 * DoubleEpsilon) + double.Epsilon;
    }
}
