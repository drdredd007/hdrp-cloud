using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct SurfaceOrogenDetailBound
    {
        public readonly double MaximumAmplitude, FullToFilteredLoss, MaximumSlope, CellResidual, ArithmeticReserve;
        public readonly bool Complete;
        public SurfaceOrogenDetailBound(double amplitude, double loss, double slope, double residual, double reserve, bool complete)
        { MaximumAmplitude = amplitude; FullToFilteredLoss = loss; MaximumSlope = slope; CellResidual = residual; ArithmeticReserve = reserve; Complete = complete; }
    }

    /// <summary>Worker-safe local bounds on the intrinsic component, before authored region operators.
    /// One work unit reads a conditioning node (three float4 records) or one range/header record.
    /// No sampling, hierarchy construction, registry lookup or allocation occurs here.</summary>
    public static class SurfaceOrogenDetailBounds
    {
        // Value noise: |corner difference| <= 2 and max quintic derivative = 15/8 per axis.
        const double NoiseSlope = 3.75 * 1.7320508075688772935;
        const double Epsilon = 2.22044604925031308085e-16;

        public static bool TryBound(in NativeWorldOrogenDetailView view, SurfaceTileKey patch, int meshResolution,
            SurfaceSamplingFootprint footprint, double canonicalBaseSlope, int maximumWork,
            out SurfaceOrogenDetailBound bound, out int work)
        {
            bound = default; work = 0;
            if (!patch.IsValid || !CubeSurface.ValidResolution(meshResolution) || !footprint.IsValid ||
                double.IsNaN(canonicalBaseSlope) || canonicalBaseSlope < 0 || maximumWork < 0) return false;
            if (!view.Recipe.Enabled) { bound = new SurfaceOrogenDetailBound(0, 0, 0, 0, 0, true); return true; }
            if (!view.IsValid) return false;
            if (view.Recipe.Strength == 0 || view.MaximumAmplitude == 0)
            { bound = new SurfaceOrogenDetailBound(0, 0, 0, 0, 0, true); return true; }
            double count = 1L << patch.Level;
            int n = view.Resolution;
            int2 first = (int2)math.floor(new double2(patch.X, patch.Y) * n / count);
            int2 last = (int2)math.ceil(new double2(patch.X + 1, patch.Y + 1) * n / count) - 1;
            first = math.clamp(first, 0, n - 1); last = math.clamp(last, first, n - 1);
            double a = LocalAmplitude(view, patch, maximumWork, ref work);
            double ampSlope = 0, flowSlope = 0, mountainSlope = 0, incisionSlope = 0;
            double magnitude = math.abs(view.SeaLevel), dryMaximum = 0, scannedAmplitude = 0;
            bool complete = true;
            // For a unit sphere dominant-face chart: |grad(a)|, |grad(b)| <= sqrt(6)/R.
            // u=(a+1)*N/2. Sum of the two cell partial bounds is therefore conservative.
            double metric = n * 2.4494897427831780982 / (2 * view.Radius);
            for (int y = first.y; y <= last.y; y++) for (int x = first.x; x <= last.x; x++)
            {
                if (maximumWork - work < 4) { complete = false; goto finished; }
                work += 4;
                int i0 = WorldOrogenDetailField.NodeIndex(n, patch.Face, x, y), i1 = i0 + 1, i2 = i0 + n + 1, i3 = i2 + 1;
                double4 g0 = view.Geometry[i0], g1 = view.Geometry[i1], g2 = view.Geometry[i2], g3 = view.Geometry[i3];
                double4 f0 = view.Flow[i0], f1 = view.Flow[i1], f2 = view.Flow[i2], f3 = view.Flow[i3];
                double4 e0 = view.Environment[i0], e1 = view.Environment[i1], e2 = view.Environment[i2], e3 = view.Environment[i3];
                double4 gs = PartialBound(g0, g1, g2, g3) * metric;
                double4 fs = PartialBound(f0, f1, f2, f3) * metric;
                double4 es = PartialBound(e0, e1, e2, e3) * metric;
                double4 hi = math.max(math.max(g0, g1), math.max(g2, g3));
                double4 lo = math.min(math.min(g0, g1), math.min(g2, g3));
                double admitted = math.min(hi.w, .25 * math.max(0, hi.z - lo.y));
                scannedAmplitude = math.max(scannedAmplitude, admitted);
                magnitude = math.max(magnitude, math.cmax(math.abs(hi.xyz)) + math.cmax(math.abs(lo.xyz)));
                double dry = math.max(math.max(e0.x, e1.x), math.max(e2.x, e3.x));
                dryMaximum = math.max(dryMaximum, dry);
                double da = math.max(gs.w, .25 * (gs.y + gs.z));
                // a*coast derivative <= da + a*baseSlope/CoastFade; plus derivative of a itself and dry.
                double lamp = view.Recipe.Strength * (2 * da + admitted * es.x +
                    (admitted == 0 ? 0 : admitted * canonicalBaseSlope / view.Recipe.CoastFadeMetres));
                ampSlope = math.max(ampSlope, lamp);
                // Projecting raw flow onto the tangent plane adds at most 2*|flow|/R.
                flowSlope = math.max(flowSlope, math.length(fs.xyz) + 2.002 / view.Radius);
                mountainSlope = math.max(mountainSlope, .65 * es.y + .15 * es.w);
                incisionSlope = math.max(incisionSlope, .55 * fs.w + .4125 * es.z);
            }
        finished:
            if (complete) a = math.min(a, scannedAmplitude * view.Recipe.Strength * dryMaximum);
            if (a == 0) { bound = new SurfaceOrogenDetailBound(0, 0, 0, 0, 0, complete); return true; }
            if (view.Radius / view.Recipe.MinimumWavelengthMetres > SurfaceOrogenDetailMath.MaximumLatticeCoordinate) return false;
            double reserve = ArithmeticFloor(view, a, magnitude);
            double loss = 0;
            var parent = new SurfaceSamplingFootprint(footprint.Metres * 2);
            if (!parent.IsValid) { complete = false; loss = a; }
            else for (int band = 0; band < SurfaceOrogenDetailMath.BandCount; band++)
                loss += a * SurfaceOrogenDetailMath.BandWeight(view.Recipe, band) * (1 - parent.DetailWeight(SurfaceOrogenDetailMath.BandWavelength(view.Recipe, band)));
            double slope = double.PositiveInfinity;
            if (complete && math.isfinite(canonicalBaseSlope))
            {
                double lambda = view.Recipe.MinimumWavelengthMetres;
                double warpSlope = .55 * flowSlope + .2002 / view.Radius + .55055 * NoiseSlope / (8 * lambda);
                double shapeSlope = 0;
                for (int band = 0; band < SurfaceOrogenDetailMath.BandCount; band++)
                {
                    double bandWeight = SurfaceOrogenDetailMath.BandWeight(view.Recipe, band);
                    if (bandWeight == 0) continue;
                    if (view.Recipe.Morphology >= 2 && view.Recipe.Morphology <= 5)
                    {
                        // Five primary taps extend by one projected-flow unit in morphology 2,
                        // and half a unit in morphology 3;
                        // fine residuals use the original three taps at +/- .75 units.
                        double coordinate = 1 / SurfaceOrogenDetailMath.BandWavelength(view.Recipe, band) +
                            warpSlope + (band == 2 ? (view.Recipe.Morphology == 2 ? 1 : .5) : .75) * flowSlope;
                        // Smooth ridge (b^2-n^2)/(b^2+n^2), b=.25, has |R'|<=5.196154.
                        // Morphology 3's squared soft-absolute ridge has |R'|<4.102.
                        // Morphologies 4/5 average this profile with positive unit-sum tap weights.
                        // Morphology 5 has a broader carrier, included through BandWavelength.
                        // Both shared valleys have half the ridge derivative. Fine residuals are plain noise.
                        shapeSlope += bandWeight * (band == 2 ?
                            (view.Recipe.Morphology == 2 ? 5.2 : 4.102) * NoiseSlope * coordinate + 2 * mountainSlope + 2 * incisionSlope :
                            NoiseSlope * coordinate);
                        continue;
                    }
                    double coordinateSlope = 1 / SurfaceOrogenDetailMath.BandWavelength(view.Recipe, band) + warpSlope +
                        1.618 * (flowSlope + 1.001 / view.Radius);
                    shapeSlope += bandWeight *
                        (4 * NoiseSlope * coordinateSlope + 2 * mountainSlope + 2 * incisionSlope);
                }
                slope = ampSlope + a * shapeSlope;
                if (!math.isfinite(slope)) slope = double.PositiveInfinity;
            }
            // Two-cell diagonal covers fine triangles and the convex parent-edge stitch vertices.
            double diameter = 4 * 1.4142135623730950488 * view.Radius / (count * meshResolution);
            double residual = math.min(2 * a, slope * diameter) + 2 * reserve;
            bound = new SurfaceOrogenDetailBound(a + reserve, loss == 0 ? 0 : loss + 2 * reserve, slope, residual, reserve, complete);
            return true;
        }

        static double4 PartialBound(double4 a, double4 b, double4 c, double4 d) =>
            math.max(math.abs(b - a), math.abs(d - c)) + math.max(math.abs(c - a), math.abs(d - b));

        static double LocalAmplitude(in NativeWorldOrogenDetailView view, SurfaceTileKey patch, int maximumWork, ref int work)
        {
            int level = 0, resolution = view.Resolution;
            while (resolution > 1 && resolution > (1L << patch.Level)) { resolution >>= 1; level++; }
            if (work >= maximumWork || level >= view.RangeLevels.Length) return view.MaximumAmplitude;
            work++;
            var header = view.RangeLevels[level];
            if (header.Resolution != resolution || header.Offset < 0) return view.MaximumAmplitude;
            double count = 1L << patch.Level;
            int2 first = (int2)math.floor(new double2(patch.X, patch.Y) * resolution / count);
            int2 last = (int2)math.ceil(new double2(patch.X + 1, patch.Y + 1) * resolution / count) - 1;
            first = math.clamp(first, 0, resolution - 1); last = math.clamp(last, first, resolution - 1);
            double maximum = 0;
            for (int y = first.y; y <= last.y; y++) for (int x = first.x; x <= last.x; x++)
            {
                if (work >= maximumWork) return view.MaximumAmplitude;
                work++;
                int index = header.Offset + patch.Face * resolution * resolution + y * resolution + x;
                if (index < 0 || index >= view.Ranges.Length) return view.MaximumAmplitude;
                maximum = math.max(maximum, view.Ranges[index].y);
            }
            return maximum * view.Recipe.Strength;
        }

        static double ArithmeticFloor(in NativeWorldOrogenDetailView view, double amplitude, double sourceMagnitude) =>
            256 * Epsilon * amplitude * (1 + NoiseSlope * view.Radius / view.Recipe.MinimumWavelengthMetres +
                (sourceMagnitude + math.abs(view.SeaLevel)) / view.Recipe.CoastFadeMetres);
    }
}
