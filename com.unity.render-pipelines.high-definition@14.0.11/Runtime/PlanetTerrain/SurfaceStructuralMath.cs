using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Algorithm-three CPU/Burst reference. The same bounded polynomial/domain operations are mirrored in SM5 FP64.</summary>
    public static class SurfaceStructuralMath
    {
        public static SurfaceSampleStatus TrySampleBand(in NativeSurfaceStructuralView view, double3 direction,
            SurfaceSamplingFootprint footprint, out double height, out int evaluatedEdges)
        {
            height = 0; evaluatedEdges = 0;
            if (!footprint.IsValid || !CubeSurface.TryNormalize(direction, out var d)) return SurfaceSampleStatus.InvalidInput;
            if (!view.Enabled || !view.Provinces.IsCreated || !view.Boundaries.IsCreated || !view.RawMacro.IsCreated || !view.Bins.IsCreated || !view.References.IsCreated)
                return SurfaceSampleStatus.NotReady;
            if (!CubeSurface.TryLocate(d, 0, out var key, out var uv) || view.Provinces.Length < 8 || view.Bins.Length != 6 * SurfaceStructuralField.SpatialResolution * SurfaceStructuralField.SpatialResolution)
                return SurfaceSampleStatus.IncompatibleData;
            int2 cell = (int2)math.min(SurfaceStructuralField.SpatialResolution - 1, math.floor(uv * SurfaceStructuralField.SpatialResolution));
            int2 range = view.Bins[key.Face * SurfaceStructuralField.SpatialResolution * SurfaceStructuralField.SpatialResolution + cell.y * SurfaceStructuralField.SpatialResolution + cell.x];
            if (range.x < 0 || range.y < 0 || range.y > SurfaceStructuralField.MaximumEdgesPerBin || (long)range.x + range.y > view.References.Length)
                return SurfaceSampleStatus.IncompatibleData;
            double macro,ridges;
            if(view.MorphologyVersion==3)
            {
                // Derived support is an explicit borrowed attachment. Full authority never requires it.
                var status=SurfaceLandformFilterMath.TrySample(view.LandformField,view.LandformFilter,d,footprint,out macro);if(status!=SurfaceSampleStatus.Ready)return status;ridges=0;
            }
            else if(view.MorphologyVersion==2)
            {var status=TrySampleBaseFour(view,d,out macro,out _,out evaluatedEdges);if(status!=SurfaceSampleStatus.Ready)return status;ridges=0;}
            else macro = Evaluate(view, d, range, false, out ridges, out evaluatedEdges);
            int row = view.RawMacroResolution + 1; double2 grid = uv * view.RawMacroResolution;
            int x = (int)math.min(view.RawMacroResolution - 1, math.floor(grid.x)), y = (int)math.min(view.RawMacroResolution - 1, math.floor(grid.y));
            int i = key.Face * row * row + y * row + x;
            if (i < 0 || (long)i + row + 1 >= view.RawMacro.Length) return SurfaceSampleStatus.IncompatibleData;
            double2 f = grid - new double2(x, y);
            double raw = math.lerp(math.lerp((double)view.RawMacro[i], view.RawMacro[i + 1], f.x), math.lerp((double)view.RawMacro[i + row], view.RawMacro[i + row + 1], f.x), f.y);
            // Five's macro is already low-pass reconstructed; applying the old structural-band
            // attenuation here would remove the whole range a second time at large footprints.
            height = view.MorphologyVersion==3?macro-raw:(macro - raw) * footprint.DetailWeight(math.min(view.ShelfWidthMetres, view.BeltWidthMetres)) + ridges * footprint.DetailWeight(view.FeatureScaleMetres * .2);
            return math.isfinite(height) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }
        public static SurfaceSampleStatus TrySampleMacro(in NativeSurfaceStructuralView view, double3 direction, out double macro, out double ridges, bool allEdges = false)
        {
            macro = ridges = 0;
            if (!CubeSurface.TryNormalize(direction, out var d)) return SurfaceSampleStatus.InvalidInput;
            if (!view.Enabled || !view.Provinces.IsCreated || !view.Bins.IsCreated) return SurfaceSampleStatus.NotReady;
            if(view.MorphologyVersion==3)return SurfaceLandformMath.TrySample(view.LandformField,d,out macro);
            if(view.MorphologyVersion==2)
            {
                var status=TrySampleBaseFour(view,d,out macro,out _,out _,allEdges);if(status!=SurfaceSampleStatus.Ready)return status;
                return SurfaceDrainageMath.TrySampleIncision(view.DrainageField,d,macro,SurfaceSamplingFootprint.Full,out ridges,out _,allEdges);
            }
            CubeSurface.TryLocate(d, 0, out var key, out var uv);
            int2 cell = (int2)math.min(SurfaceStructuralField.SpatialResolution - 1, math.floor(uv * SurfaceStructuralField.SpatialResolution));
            int2 range = view.Bins[key.Face * SurfaceStructuralField.SpatialResolution * SurfaceStructuralField.SpatialResolution + cell.y * SurfaceStructuralField.SpatialResolution + cell.x];
            macro = Evaluate(view, d, range, allEdges, out ridges, out _);
            return math.isfinite(macro) && math.isfinite(ridges) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }
        public static SurfaceSampleStatus TrySampleBaseFour(in NativeSurfaceStructuralView view,double3 direction,
            out double height,out int evaluatedCoasts,out int evaluatedEdges,bool allEdges=false)
        {
            height=0;evaluatedCoasts=evaluatedEdges=0;
            if(!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(!view.Enabled||view.MorphologyVersion!=2||!view.DrainageField.Enabled)return SurfaceSampleStatus.NotReady;
            var status=SurfaceDrainageMath.TrySampleCoastForMacro(view.DrainageField,d,out double coast,out evaluatedCoasts);
            if(status!=SurfaceSampleStatus.Ready)return status;
            CubeSurface.TryLocate(d,0,out var key,out var uv);int2 cell=(int2)math.min(SurfaceStructuralField.SpatialResolution-1,math.floor(uv*SurfaceStructuralField.SpatialResolution));
            var range=view.Bins[key.Face*SurfaceStructuralField.SpatialResolution*SurfaceStructuralField.SpatialResolution+cell.y*SurfaceStructuralField.SpatialResolution+cell.x];
            if(range.x<0||range.y<0||(long)range.x+range.y>view.References.Length)return SurfaceSampleStatus.IncompatibleData;
            int count=allEdges?view.Boundaries.Length:range.y;double uplift=0,rift=0;
            for(int j=0;j<count;j++)
            {
                var edge=view.Boundaries[allEdges?j:view.References[range.x+j]];evaluatedEdges++;
                double distance=Distance(edge,d,view.SourceRecipe.Radius,out _,out _);
                if(edge.Convergence>.08)uplift=math.max(uplift,Envelope(distance/view.BeltWidthMetres)*(edge.Convergence-.08)/.92);
                if(edge.Convergence<-.08)rift=math.max(rift,Envelope(distance/view.BeltWidthMetres)*-edge.Convergence);
            }
            var recipe=view.SourceRecipe;double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight);
            double land=recipe.MaximumHeight-sea,ocean=sea-recipe.MinimumHeight,mountain=math.min(1,view.MountainFraction/.3);
            if(coast>=0)
            {
                double basin=0,weight=0;
                for(int i=0;i<view.Provinces.Length;i++)
                {
                    var province=view.Provinces[i];double w=ExpMinus(math.max(0,12*(1-math.dot(d,province.Center))));
                    basin+=w*math.clamp((province.Buoyancy-.12)/.15,0,1);weight+=w;
                }
                height=sea+land*(.02+.10*basin/weight)*Smooth(coast/(view.ShelfWidthMetres*2))+
                    land*.78*mountain*uplift*Smooth(coast/(view.ShelfWidthMetres*.4));
            }
            else height=sea-ocean*(.06*Smooth(-coast/view.ShelfWidthMetres)+.84*Smooth(-coast/(view.ShelfWidthMetres*4)))+
                ocean*.18*rift*Smooth(-coast/(view.ShelfWidthMetres*4));
            height=math.clamp(height,recipe.MinimumHeight,recipe.MaximumHeight);
            return math.isfinite(height)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
        static double Evaluate(in NativeSurfaceStructuralView view, double3 d, int2 range, bool allEdges, out double ridges, out int evaluatedEdges)
        {
            var recipe = view.SourceRecipe; int plate = 0; double best = -2;
            for (int i = 0; i < view.Provinces.Length; i++) { double dot = math.dot(d, view.Provinces[i].Center); if (dot > best) { best = dot; plate = i; } }
            double coast = view.ShelfWidthMetres * 4 + math.abs(view.CoastThresholdMetres), uplift = 0, rift = 0, basin = 0, basinWeight = 0;
            for (int i = 0; i < view.Provinces.Length; i++)
            {
                var province = view.Provinces[i]; double w = ExpMinus(math.max(0, (best - math.dot(d, province.Center)) * recipe.Radius / view.ShelfWidthMetres));
                basin += w * province.Buoyancy; basinWeight += w;
            }
            double sum = 0, weightSum = 0, scale = view.FeatureScaleMetres;
            double sea = math.clamp(recipe.SeaLevel, recipe.MinimumHeight, recipe.MaximumHeight), landRelief = recipe.MaximumHeight - sea, oceanRelief = sea - recipe.MinimumHeight;
            double mountain = math.min(1, view.MountainFraction / .3), amplitude = landRelief * .1 * mountain;
            int count = allEdges ? view.Boundaries.Length : range.y; evaluatedEdges = count;
            for (int j = 0; j < count; j++)
            {
                int edgeIndex = allEdges ? j : view.References[range.x + j]; var edge = view.Boundaries[edgeIndex];
                double distance = Distance(edge, d, recipe.Radius, out double along, out double across);
                if (edge.ContinentalA != edge.ContinentalB) coast = math.min(coast, distance);
                if (edge.OceanRift) rift = math.max(rift, Envelope(distance / view.BeltWidthMetres) * -edge.Convergence);
                if (!edge.MountainBelt) continue;
                double strength = (edge.Convergence - .08) / .92;
                uplift = math.max(uplift, Envelope(distance / view.BeltWidthMetres) * strength * (edge.ContinentalA && edge.ContinentalB ? 1 : .75));
                double weight = Envelope(distance / (view.BeltWidthMetres * 1.8)) * strength;
                if (weight == 0) continue;
                // Stable tributaries branch from the same primary crest across every residency boundary.
                int slot = (int)math.floor((along - math.abs(across) * .35) / scale);
                double crest = 0, tributary = 0;
                for (int branch = slot - 1; branch <= slot + 1; branch++)
                {
                    double root = BranchRoot(recipe.Seed, edgeIndex, branch, math.abs(across), scale), next = BranchRoot(recipe.Seed, edgeIndex, branch + 1, math.abs(across), scale);
                    double width = scale * (.12 + .08 * Random(recipe.Seed, edgeIndex, branch, 23));
                    double q = (along - root) / width;
                    crest = math.max(crest, ExpMinus(q * q) * (.65 + .35 * Random(recipe.Seed, edgeIndex, branch, 25)));
                    q = (along - (root + next) * .5) / (scale * .1); tributary = math.max(tributary, ExpMinus(q * q));
                }
                double downstream = .45 + .55 * Smooth(math.abs(across) / (scale * 3));
                double z = (math.abs(across) - scale * 3) / (scale * .18), trunk = ExpMinus(z * z);
                double foothill = 1 - Smooth((math.abs(across) - scale * 3) / scale);
                sum += weight * amplitude * (.75 * crest * foothill - .55 * tributary * downstream * foothill - .6 * trunk); weightSum += weight;
            }
            ridges = sum / math.max(1, weightSum);
            coast = coast * (view.Provinces[plate].Continental ? 1 : -1) - view.CoastThresholdMetres;
            basin /= basinWeight; double height;
            if (coast >= 0) height = sea + landRelief * basin * Smooth(coast / view.ShelfWidthMetres) + landRelief * .72 * mountain * uplift * Smooth(coast / (view.ShelfWidthMetres * .25));
            else
            {
                double shelf = Smooth(-coast / view.ShelfWidthMetres), deep = Smooth(-coast / (view.ShelfWidthMetres * 4));
                height = sea - oceanRelief * (.06 * shelf + (.65 + basin) * deep) + oceanRelief * .22 * rift * deep;
            }
            return math.clamp(height, recipe.MinimumHeight, recipe.MaximumHeight);
        }
        static double Distance(SurfaceGeologicalBoundary edge, double3 direction, double radius, out double along, out double across)
        {
            double signed = math.dot(direction, edge.Normal); var projected = direction - edge.Normal * signed;
            if (math.lengthsq(projected) < 1e-24) projected = edge.Start; else projected = math.normalize(projected);
            if (math.dot(projected, edge.Start + edge.End) < 0) projected = -projected;
            bool inside = math.dot(math.cross(edge.Start, projected), edge.Normal) >= -1e-13 && math.dot(math.cross(projected, edge.End), edge.Normal) >= -1e-13;
            var closest = inside ? projected : (math.dot(direction, edge.Start) >= math.dot(direction, edge.End) ? edge.Start : edge.End);
            along = Arc(edge.Start, closest) * radius;
            // Influential samples lie on the nearer hemisphere; the opposite hemisphere is outside all declared envelopes.
            across = (signed < 0 ? -1 : 1) * Asin(math.min(1, math.abs(signed))) * radius;
            return Arc(direction, closest) * radius;
        }
        public static double Arc(double3 a, double3 b) => 2 * Asin(math.min(1, math.sqrt(math.lengthsq(a - b)) * .5));
        public static double Asin(double x) => x <= .5 ? AsinLow(x) : Math.PI * .5 - 2 * AsinLow(math.sqrt(math.max(0, (1 - x) * .5)));
        static double AsinLow(double x)
        {
            double sum = x, term = x, square = x * x;
            for (int n = 1; n <= 32; n++) { double odd = 2 * n - 1; term *= square * odd * odd / ((2 * n) * (double)(2 * n + 1)); sum += term; }
            return sum;
        }
        public static double ExpMinus(double x)
        {
            if (x >= 64) return 0;
            double y = math.max(0, x) / 64, value = 1.0 / 479001600;
            for (int n = 11; n >= 0; n--) value = value * -y + InvFactorial(n);
            for (int n = 0; n < 6; n++) value *= value;
            return value;
        }
        static double InvFactorial(int n)
        {
            switch (n) { case 0: case 1: return 1; case 2: return .5; case 3: return 1.0 / 6; case 4: return 1.0 / 24; case 5: return 1.0 / 120; case 6: return 1.0 / 720; case 7: return 1.0 / 5040; case 8: return 1.0 / 40320; case 9: return 1.0 / 362880; case 10: return 1.0 / 3628800; default: return 1.0 / 39916800; }
        }
        public static double Sin(double x)
        {
            x -= math.floor((x + Math.PI) / (2 * Math.PI)) * (2 * Math.PI);
            if (x > Math.PI * .5) x = Math.PI - x; else if (x < -Math.PI * .5) x = -Math.PI - x;
            double square = x * x, sum = x, term = x;
            for (int n = 1; n <= 8; n++) { term *= -square / ((2 * n) * (double)(2 * n + 1)); sum += term; } return sum;
        }
        static double Envelope(double distance) => distance >= 3 ? 0 : ExpMinus(distance * distance) * (1 - Smooth(distance - 2));
        static double Smooth(double x) { x = math.clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        static double BranchRoot(int seed, int edge, int slot, double across, double scale)
        {
            double phase = Random(seed, edge, slot, 19) * 2 * Math.PI;
            return (slot + .2 + .6 * Random(seed, edge, slot, 17)) * scale + .35 * across + scale * .12 * (Sin(across / (scale * 2) + phase) - Sin(phase));
        }
        static double Random(int seed, int edge, int slot, uint stream)
        {
            unchecked { uint x = (uint)(seed ^ (edge * 7907)) ^ ((uint)(slot ^ (slot < 0 ? -1 : 0)) * 0x9e3779b9u) ^ (stream * 0x85ebca6bu);
                x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return (x + .5) / 4294967296.0; }
        }
    }
}
