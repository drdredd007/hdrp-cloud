using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Intrinsic metric detail conditioned by the captured final World Orogen map.
    /// This is geometry synthesis along reference flow, not a new hydraulic simulation.</summary>
    public static class SurfaceOrogenDetailMath
    {
        public const int BandCount = 4;
        // Leave room for warp and neighbouring integer corners before the double lattice loses unit resolution.
        public const double MaximumLatticeCoordinate = 4503599627370480d;
        public static double BandWeight(int band) => band == 0 ? .12 : band == 1 ? .18 : band == 2 ? .27 : band == 3 ? .43 : 0;
        public static double BandWeight(WorldOrogenDetailRecipe recipe, int band) => recipe.Morphology == 3 || recipe.Morphology == 4 || recipe.Morphology == 5 ?
            (band == 0 ? .18 : band == 1 ? .22 : band == 2 ? .60 : 0) : recipe.Morphology == 2 ?
            (band == 0 ? .08 : band == 1 ? .12 : band == 2 ? .80 : 0) : BandWeight(band);
        public static double BandWavelength(WorldOrogenDetailRecipe recipe, int band) =>
            recipe.MinimumWavelengthMetres * (recipe.Morphology == 5 && band == 2 ? 8 : 1 << band);
        /// <summary>C1 ridge profile without an absolute-value cusp. For |n|<=1, |R|<=1 and |R'|<=5.2.</summary>
        public static double SmoothRidge(double n) => (.0625 - n * n) / (.0625 + n * n);
        /// <summary>Smooth squared ridge for the third captured policy. For |n|<=1, |R|<=1
        /// and |R'|<=4/(sqrt(1+.025^2)-.025)&lt;4.102. No hard absolute-value crest cusp.</summary>
        public static double PeakedRidge(double n)
        {
            const double epsilon = .025;
            double s = (math.sqrt(n * n + epsilon * epsilon) - epsilon) / (math.sqrt(1 + epsilon * epsilon) - epsilon);
            return 2 * (1 - s) * (1 - s) - 1;
        }

        public static SurfaceSampleStatus TrySample(in NativeWorldOrogenDetailView view, double3 direction,
            double baseHeight, SurfaceSamplingFootprint footprint, out double displacement)
        {
            displacement = 0;
            if (!math.isfinite(baseHeight) || !footprint.IsValid || !CubeSurface.TryNormalize(direction, out var unit))
                return SurfaceSampleStatus.InvalidInput;
            if (!view.Recipe.Enabled) return SurfaceSampleStatus.Ready;
            if (view.Version != WorldOrogenDetailField.CurrentVersion) return SurfaceSampleStatus.IncompatibleData;
            if (!view.IsValid) return SurfaceSampleStatus.NotReady;
            if (view.Recipe.Strength == 0 || view.MaximumAmplitude == 0 || baseHeight <= view.SeaLevel)
                return SurfaceSampleStatus.Ready;
            var status = TryCondition(view, unit, out var geometry, out var flow, out var environment);
            if (status != SurfaceSampleStatus.Ready) return status;
            double admitted = math.min(geometry.w, .25 * math.max(0, geometry.z - geometry.y));
            double coast = math.saturate((baseHeight - view.SeaLevel) / math.max(view.Recipe.CoastFadeMetres, 4 * admitted));
            double amplitude = admitted * view.Recipe.Strength * environment.x * coast;
            if (amplitude == 0) return SurfaceSampleStatus.Ready;
            bool contributing = false;
            for (int band = 0; band < BandCount; band++)
                contributing |= BandWeight(view.Recipe, band) != 0 && footprint.DetailWeight(BandWavelength(view.Recipe, band)) != 0;
            if (!contributing) return SurfaceSampleStatus.Ready;
            double scale = view.Radius / view.Recipe.MinimumWavelengthMetres;
            if (!math.isfinite(scale) || math.any(math.abs(unit * scale) > MaximumLatticeCoordinate))
                return SurfaceSampleStatus.InvalidInput;

            // A bilinearly interpolated vector need not remain tangent. Project without normalising:
            // a zero/pit flow stays zero, and the operation has a bounded derivative.
            double3 downstream = flow.xyz - unit * math.dot(unit, flow.xyz);
            double3 across = math.cross(unit, downstream);
            double3 macro = unit * (scale / 8);
            double3 warp = downstream * (.35 * Noise(macro + new double3(17, -31, 11), view.Recipe.Seed, 0)) +
                across * (.2 * Noise(macro + new double3(-19, 7, 43), view.Recipe.Seed, 1));
            double mountain = math.saturate(.2 + .65 * environment.y + .15 * environment.w);
            double incision = .55 * flow.w * (.25 + .75 * environment.z);
            double sum = 0;
            if (view.Recipe.Morphology == 2 || view.Recipe.Morphology == 3 || view.Recipe.Morphology == 4 || view.Recipe.Morphology == 5)
            {
                // One mesoscale carrier supplies both crest and valley phase. Averaging the same
                // field along the captured vector elongates forms; it does not route new rivers.
                for (int band = 0; band < 3; band++)
                {
                    double weight = footprint.DetailWeight(BandWavelength(view.Recipe, band));
                    if (weight == 0) continue;
                    double3 p = unit * (scale / (view.Recipe.Morphology == 5 && band == 2 ? 8 : 1 << band)) + warp;
                    double n, filteredRidge = 0;
                    if (band == 2 && (view.Recipe.Morphology == 4 || view.Recipe.Morphology == 5))
                    {
                        // Transform before averaging: opposite signed carriers must not manufacture
                        // a perfect crest merely by cancelling to zero. The same 13 noise calls,
                        // positive tap weights and captured reference-flow coordinates are retained.
                        double a = Noise(p - downstream * .5, view.Recipe.Seed, 2),
                            b = Noise(p - downstream * .25, view.Recipe.Seed, 2),
                            c = Noise(p, view.Recipe.Seed, 2),
                            d = Noise(p + downstream * .25, view.Recipe.Seed, 2),
                            e = Noise(p + downstream * .5, view.Recipe.Seed, 2);
                        n = (a + 4 * b + 6 * c + 4 * d + e) / 16;
                        filteredRidge = (PeakedRidge(a) + 4 * PeakedRidge(b) + 6 * PeakedRidge(c) +
                            4 * PeakedRidge(d) + PeakedRidge(e)) / 16;
                    }
                    else if (band == 2 && view.Recipe.Morphology == 3)
                        n = (Noise(p - downstream * .5, view.Recipe.Seed, 2) +
                            4 * Noise(p - downstream * .25, view.Recipe.Seed, 2) +
                            6 * Noise(p, view.Recipe.Seed, 2) +
                            4 * Noise(p + downstream * .25, view.Recipe.Seed, 2) +
                            Noise(p + downstream * .5, view.Recipe.Seed, 2)) / 16;
                    else if (band == 2)
                        n = (Noise(p - downstream, view.Recipe.Seed, 2) +
                            4 * Noise(p - downstream * .5, view.Recipe.Seed, 2) +
                            6 * Noise(p, view.Recipe.Seed, 2) +
                            4 * Noise(p + downstream * .5, view.Recipe.Seed, 2) +
                            Noise(p + downstream, view.Recipe.Seed, 2)) / 16;
                    else
                        n = .5 * Noise(p, view.Recipe.Seed, 2) +
                            .25 * Noise(p + downstream * .75, view.Recipe.Seed, 2) +
                            .25 * Noise(p - downstream * .75, view.Recipe.Seed, 2);
                    double shape = n;
                    if (band == 2)
                    {
                        double ridge = view.Recipe.Morphology == 4 || view.Recipe.Morphology == 5 ? filteredRidge : view.Recipe.Morphology == 3 ? PeakedRidge(n) : SmoothRidge(n), valley = (ridge - 1) * .5;
                        shape = math.lerp(math.lerp(n, ridge, mountain), valley, incision);
                    }
                    sum += BandWeight(view.Recipe, band) * weight * shape;
                }
                displacement = amplitude * sum;
                return math.isfinite(displacement) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.InvalidInput;
            }
            for (int band = 0; band < BandCount; band++)
            {
                double weight = footprint.DetailWeight(BandWavelength(view.Recipe, band));
                if (weight == 0) continue;
                double3 p = unit * (scale / (1 << band)) + warp;
                double n = .5 * Noise(p, view.Recipe.Seed, 2 + band * 4) +
                    .25 * Noise(p + downstream * .75, view.Recipe.Seed, 2 + band * 4) +
                    .25 * Noise(p - downstream * .75, view.Recipe.Seed, 2 + band * 4);
                double r = 1 - math.abs(n); double ridge = 2 * r * r - 1;
                double channel = 1 - math.abs(Noise(p + across * 1.618, view.Recipe.Seed, 3 + band * 4));
                double valley = -channel * channel * channel * channel;
                double shape = math.lerp(math.lerp(n, ridge, mountain), valley, incision);
                sum += BandWeight(band) * weight * shape;
            }
            displacement = amplitude * sum;
            return math.isfinite(displacement) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.InvalidInput;
        }

        /// <summary>Double interpolation of captured float nodes. Dominant-face ownership matches CubeSurface.</summary>
        public static SurfaceSampleStatus TryCondition(in NativeWorldOrogenDetailView view, double3 direction,
            out double4 geometry, out double4 flow, out double4 environment)
        {
            geometry = flow = environment = default;
            if (!CubeSurface.TryLocate(direction, 0, out var key, out var uv)) return SurfaceSampleStatus.InvalidInput;
            if (!view.IsValid) return SurfaceSampleStatus.NotReady;
            double2 grid = uv * view.Resolution;
            int2 cell = (int2)math.min(view.Resolution - 1, math.floor(grid));
            double2 t = grid - cell;
            int a = WorldOrogenDetailField.NodeIndex(view.Resolution, key.Face, cell.x, cell.y);
            int b = a + 1, c = a + view.Resolution + 1, d = c + 1;
            geometry = Bilinear(view.Geometry[a], view.Geometry[b], view.Geometry[c], view.Geometry[d], t);
            flow = Bilinear(view.Flow[a], view.Flow[b], view.Flow[c], view.Flow[d], t);
            environment = Bilinear(view.Environment[a], view.Environment[b], view.Environment[c], view.Environment[d], t);
            return SurfaceSampleStatus.Ready;
        }

        static double4 Bilinear(float4 a, float4 b, float4 c, float4 d, double2 t) =>
            math.lerp(math.lerp((double4)a, (double4)b, t.x), math.lerp((double4)c, (double4)d, t.x), t.y);

        // Integer-only 32-bit hash is mirrored by compute; both halves of every signed 64-bit lattice coordinate participate.
        public static uint Mix(uint x)
        { unchecked { x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; return x ^ (x >> 16); } }
        static uint Fold(uint h, long value)
        { unchecked { return Mix(Mix(h ^ (uint)value) ^ (uint)((ulong)value >> 32)); } }
        static double Corner(long x, long y, long z, int seed, int stream)
        {
            unchecked
            {
                uint h = Mix((uint)seed ^ ((uint)stream * 0x9e3779b9U));
                h = Fold(h, x); h = Fold(h, y); h = Fold(h, z);
                return h * (2d / uint.MaxValue) - 1;
            }
        }
        static double Noise(double3 p, int seed, int stream)
        {
            double3 floor = math.floor(p), t = p - floor;
            t = t * t * t * (t * (t * 6 - 15) + 10);
            long x = (long)floor.x, y = (long)floor.y, z = (long)floor.z;
            double a = math.lerp(Corner(x, y, z, seed, stream), Corner(x + 1, y, z, seed, stream), t.x);
            double b = math.lerp(Corner(x, y + 1, z, seed, stream), Corner(x + 1, y + 1, z, seed, stream), t.x);
            double c = math.lerp(Corner(x, y, z + 1, seed, stream), Corner(x + 1, y, z + 1, seed, stream), t.x);
            double d = math.lerp(Corner(x, y + 1, z + 1, seed, stream), Corner(x + 1, y + 1, z + 1, seed, stream), t.x);
            return math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z);
        }
    }
}
