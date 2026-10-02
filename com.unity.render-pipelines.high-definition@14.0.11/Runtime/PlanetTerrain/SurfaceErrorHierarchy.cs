using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public enum SurfaceErrorStatus { Complete, SupportBudgetExceeded, MeasurementBudgetExceeded, MissingData, Pending }

    /// <summary>Measured signed-height residual, separately from spherical geometry and procedural bandwidth.</summary>
    public readonly struct SurfaceLodError
    {
        public readonly double MeasuredBakedMetres, UnmeasuredBakedMetres, CurvatureMetres;
        public readonly double ProceduralAttenuationMetres, ProceduralInterpolationMetres;
        public readonly double RegionalFilteringMetres;
        public readonly int SampleCount;
        public readonly SurfaceErrorStatus Status;
        public bool IsComplete => Status == SurfaceErrorStatus.Complete;
        public double TotalMetres => math.max(MeasuredBakedMetres, UnmeasuredBakedMetres) + CurvatureMetres +
            ProceduralAttenuationMetres + ProceduralInterpolationMetres + RegionalFilteringMetres;
        internal SurfaceLodError(double measured, double unmeasured, double curvature, double attenuation,
            double interpolation, int samples, SurfaceErrorStatus status, double regionalFiltering = 0)
        { MeasuredBakedMetres = measured; UnmeasuredBakedMetres = unmeasured; CurvatureMetres = curvature;
            ProceduralAttenuationMetres = attenuation; ProceduralInterpolationMetres = interpolation; RegionalFilteringMetres = regionalFiltering; SampleCount = samples; Status = status; }
        public SurfaceLodError WithRegionalFiltering(double bound) => new SurfaceLodError(MeasuredBakedMetres, UnmeasuredBakedMetres,
            CurvatureMetres, ProceduralAttenuationMetres, ProceduralInterpolationMetres, SampleCount, Status, bound);
    }

    public readonly struct SurfaceErrorBuildSettings
    {
        public readonly int MaximumSupportSamples;
        public SurfaceErrorBuildSettings(int maximumSupportSamples) { MaximumSupportSamples = maximumSupportSamples; }
        public static SurfaceErrorBuildSettings Default => new SurfaceErrorBuildSettings(524288);
        public bool IsValid => MaximumSupportSamples >= 1 && MaximumSupportSamples <= 4194304;
    }

    /// <summary>
    /// Immutable content-addressed support hierarchy. Probes include canonical and regional vertices and cell centres,
    /// region feather lines and crater rings. These are measured errors on the published field, not a continuous
    /// mathematical supremum. A caller must retain the matching native view while measuring a patch.
    /// </summary>
    public sealed class SurfaceErrorHierarchy
    {
        readonly Probe[] probes;
        readonly double bakedRange, outerRadius, suppressionSlope;
        readonly SurfaceDetailRecipe detail;
        readonly bool missingData;
        public SurfaceContentHash ContentDigest { get; }
        public bool SupportComplete { get; }
        public int SupportSampleCount => probes.Length;
        public long EstimatedBytes => (long)probes.Length * 32;
        readonly struct Probe
        {
            public readonly int Face;
            public readonly double2 Uv;
            public readonly double Height;
            public Probe(int face, double2 uv, double height) { Face = face; Uv = uv; Height = height; }
        }
        SurfaceErrorHierarchy(SurfaceSnapshot snapshot, Probe[] probes, bool complete, bool missing)
        {
            this.probes = probes; SupportComplete = complete; missingData = missing;
            ContentDigest = snapshot.ContentDigest; detail = snapshot.Detail;
            bakedRange = snapshot.MaximumHeight - snapshot.MinimumHeight;
            outerRadius = snapshot.Recipe.Radius + math.max(math.abs(snapshot.MinimumHeight), math.abs(snapshot.MaximumHeight));
            foreach(var region in snapshot.Regions)
            {
                if(region.DetailPolicy!=SurfaceDetailPolicy.Suppress)continue;
                var extent=region.Projection.MaximumMetres-region.Projection.MinimumMetres;
                double dx=0,dy=0;int row=region.Resolution.x+1;
                for(int y=0;y<=region.Resolution.y;y++)for(int x=0;x<=region.Resolution.x;x++)
                {
                    int i=y*row+x;
                    if(x<region.Resolution.x)dx=math.max(dx,math.abs(region.MaskAt(i+1)-region.MaskAt(i))*region.Resolution.x/extent.x);
                    if(y<region.Resolution.y)dy=math.max(dy,math.abs(region.MaskAt(i+row)-region.MaskAt(i))*region.Resolution.y/extent.y);
                }
                double feather=region.BlendMetres>0?1.5/region.BlendMetres:0;
                var reach=math.max(math.abs(region.Projection.MinimumMetres),math.abs(region.Projection.MaximumMetres))/snapshot.Recipe.Radius;
                suppressionSlope+=(math.sqrt(dx*dx+dy*dy)+feather)*(1+math.lengthsq(reach));
            }
        }

        public static SurfaceErrorHierarchy Build(SurfaceSnapshot snapshot, in NativeSurfaceView view,
            SurfaceErrorBuildSettings settings)
        {
            if (snapshot == null || snapshot.ContentDigest != view.ContentDigest || !settings.IsValid)
                throw new ArgumentException("Error support requires the same immutable snapshot and a bounded sample budget.");
            long expected=0;
            foreach(var tile in snapshot.Tiles)expected+=tile.SampleCount+(long)tile.Resolution*tile.Resolution;
            foreach(var region in snapshot.Regions)
                expected+=region.SampleCount+(long)region.Resolution.x*region.Resolution.y+
                    (region.BlendMetres>0?4L*(region.Resolution.x+region.Resolution.y+2):0);
            expected+=(long)snapshot.Stamps.Count*513;
            var list = new List<Probe>((int)Math.Min((long)settings.MaximumSupportSamples,expected));
            bool complete = true, missing = false;
            var native = view; // Local copy allows the preparation callback to borrow this view.
            var noDetail = new SurfaceSamplingFootprint(view.Detail.WavelengthMetres);
            Action<double3> add = direction =>
            {
                if (list.Count >= settings.MaximumSupportSamples) { complete = false; return; }
                if (!CubeSurface.TryLocate(direction, 0, out var key, out var uv) ||
                    SurfaceSampler.TrySampleHeight(native, direction, noDetail, out var height) != SurfaceSampleStatus.Ready)
                { missing = true; return; }
                list.Add(new Probe(key.Face, uv, height));
            };
            foreach (var tile in snapshot.Tiles)
            {
                int n = tile.Resolution;
                for (int y = 0; y <= n && complete; y++) for (int x = 0; x <= n && complete; x++)
                { CubeSurface.TrySampleDirection(tile.Key, n, x, y, out var direction); add(direction); }
                for (int y = 0; y < n && complete; y++) for (int x = 0; x < n && complete; x++)
                { CubeSurface.TryDirection(tile.Key, new double2(x + .5, y + .5) / n, out var direction); add(direction); }
            }
            foreach (var region in snapshot.Regions)
            {
                var min = region.Projection.MinimumMetres; var extent = region.Projection.MaximumMetres - min;
                for (int y = 0; y <= region.Resolution.y && complete; y++) for (int x = 0; x <= region.Resolution.x && complete; x++)
                { region.Projection.TryDirection(min + extent * (new double2(x, y) / (double2)region.Resolution), out var direction); add(direction); }
                for (int y = 0; y < region.Resolution.y && complete; y++) for (int x = 0; x < region.Resolution.x && complete; x++)
                { region.Projection.TryDirection(min + extent * (new double2(x + .5, y + .5) / (double2)region.Resolution), out var direction); add(direction); }
                // A narrow authored feather need not cross a source-grid centre. Sample its half/full width explicitly.
                if (region.BlendMetres > 0)
                    for (int edge = 0; edge < 4 && complete; edge++) for (int band = 1; band <= 2 && complete; band++)
                    {
                        int axis = edge & 1, along = 1 - axis, count = region.Resolution[along];
                        double inset = math.min(region.BlendMetres * band * .5, extent[axis] * .5);
                        for (int i = 0; i <= count && complete; i++)
                        {
                            var metres = min; metres[along] += extent[along] * i / count;
                            metres[axis] += edge < 2 ? inset : extent[axis] - inset;
                            region.Projection.TryDirection(metres, out var direction); add(direction);
                        }
                    }
            }
            foreach (var stamp in snapshot.Stamps)
            {
                var unit = stamp.CenterDirection;
                var tangent = math.normalize(math.cross(math.abs(unit.y) < .9 ? new double3(0, 1, 0) : new double3(1, 0, 0), unit));
                var bitangent = math.cross(unit, tangent); add(unit);
                for (int ring = 1; ring <= 16 && complete; ring++) for (int angle = 0; angle < 32 && complete; angle++)
                {
                    double arc = ring <= 8 ? stamp.RadiusMetres * ring / 8 : stamp.RadiusMetres + stamp.RimWidthMetres * (ring - 8) / 8;
                    double a = angle * (2 * Math.PI / 32), distance = arc / snapshot.Recipe.Radius;
                    add(unit * math.cos(distance) + (tangent * math.cos(a) + bitangent * math.sin(a)) * math.sin(distance));
                }
            }
            list.Sort((a, b) => { int n = a.Face.CompareTo(b.Face); if (n != 0) return n;
                n = a.Uv.x.CompareTo(b.Uv.x); return n != 0 ? n : a.Uv.y.CompareTo(b.Uv.y); });
            return new SurfaceErrorHierarchy(snapshot, list.ToArray(), complete, missing);
        }

        /// <summary>Finite metadata-only fallback while data/error preparation is pending; never implies measured accuracy.</summary>
        public static SurfaceLodError Conservative(in NativeSurfaceView view, SurfaceTileKey key, int meshResolution,
            SurfaceErrorStatus status = SurfaceErrorStatus.Pending)
        {
            if(!key.IsValid||!CubeSurface.ValidResolution(meshResolution)||!view.Recipe.IsValid)
                throw new ArgumentException("Invalid conservative error request.");
            double outer=view.Recipe.Radius+math.max(math.abs(view.MinimumHeight),math.abs(view.MaximumHeight));
            double angle=2*Math.Sqrt(2)/((1L<<key.Level)*meshResolution);
            double curvature=outer*(2*math.pow(math.sin(angle*.5),2));
            // Snapshot min/max already include the whole procedural/stamp range. Keep that amplitude in
            // UnmeasuredBakedMetres here; splitting components requires the prepared support metadata.
            return new SurfaceLodError(0,view.MaximumHeight-view.MinimumHeight,curvature,0,0,0,status);
        }

        /// <summary>32-cell far topology uses the b-c diagonal. Work counts all actual sampler calls and probe comparisons.</summary>
        public SurfaceLodError Measure(in NativeSurfaceView view, SurfaceTileKey key, int meshResolution,
            SurfaceSamplingFootprint footprint, int maximumWork, out int work)
        {
            if (view.ContentDigest != ContentDigest || !key.IsValid || !CubeSurface.ValidResolution(meshResolution) ||
                !footprint.IsValid || maximumWork < 0) throw new ArgumentException("Invalid or incompatible error measurement.");
            var evaluator = new Evaluator(view, key, meshResolution, maximumWork);
            double count = 1L << key.Level;
            var min = new double2(key.X, key.Y) / count; var max = min + 1 / count;
            int first = LowerBound(key.Face, min.x);
            for (int i = first; i < probes.Length; i++)
            {
                var p = probes[i]; if (p.Face != key.Face || p.Uv.x > max.x) break;
                if (p.Uv.y < min.y || p.Uv.y > max.y) continue;
                if (!evaluator.Compare((p.Uv - min) * count, p.Height)) break;
            }
            // Fine render patches can fall between all source-grid vertices. Cell centres measure the actual bilinear
            // field/triangle mismatch there too, rather than reporting an empty probe set as zero terrain error.
            for (int y = 0; y < meshResolution && !evaluator.Exhausted; y++)
                for (int x = 0; x < meshResolution && !evaluator.Exhausted; x++)
                    evaluator.Compare(new double2(x + .5, y + .5) / meshResolution);
            work = evaluator.Work;
            var status = evaluator.Missing || missingData ? SurfaceErrorStatus.MissingData :
                evaluator.Exhausted ? SurfaceErrorStatus.MeasurementBudgetExceeded :
                !SupportComplete ? SurfaceErrorStatus.SupportBudgetExceeded : SurfaceErrorStatus.Complete;
            double angle = 2 * Math.Sqrt(2) / (count * meshResolution);
            // Stable sagitta even at high LOD levels (1-cos(angle) loses small-angle precision).
            double curvature = outerRadius * (2 * math.pow(math.sin(angle * .5), 2));
            double w = footprint.DetailWeight(detail.WavelengthMetres);
            double attenuation = detail.AmplitudeMetres * (1 - w);
            // Quintic value-noise derivative <= 1.875, corner range <= 2A, three axes and triangle diameter.
            double interpolation = detail.AmplitudeMetres == 0 ? 0 : w * math.min(2 * detail.AmplitudeMetres,
                detail.AmplitudeMetres * (2 * outerRadius / (count * meshResolution)) *
                (12 / detail.WavelengthMetres + 2 * suppressionSlope));
            return new SurfaceLodError(evaluator.Maximum, status == SurfaceErrorStatus.Complete ? 0 : bakedRange,
                curvature, attenuation, interpolation, evaluator.Comparisons, status);
        }

        int LowerBound(int face, double x)
        {
            int low = 0, high = probes.Length;
            while (low < high) { int mid = low + (high - low) / 2; var p = probes[mid];
                if (p.Face < face || (p.Face == face && p.Uv.x < x)) low = mid + 1; else high = mid; }
            return low;
        }
        sealed class Evaluator
        {
            readonly NativeSurfaceView view;
            readonly SurfaceTileKey key;
            readonly int n, budget;
            readonly SurfaceSamplingFootprint noDetail;
            readonly Dictionary<int, double> vertices = new Dictionary<int, double>();
            public int Work, Comparisons;
            public double Maximum;
            public bool Exhausted, Missing;
            public Evaluator(NativeSurfaceView view, SurfaceTileKey key, int n, int budget)
            { this.view = view; this.key = key; this.n = n; this.budget = budget; noDetail = new SurfaceSamplingFootprint(view.Detail.WavelengthMetres); }
            bool Spend() { if (Work >= budget) { Exhausted = true; return false; } Work++; return true; }
            bool Height(double2 uv, out double height)
            {
                height = 0; if (!Spend()) return false;
                if (!CubeSurface.TryDirection(key, uv, out var dir) ||
                    SurfaceSampler.TrySampleHeight(view, dir, noDetail, out height) != SurfaceSampleStatus.Ready)
                { Missing = true; return false; } return true;
            }
            bool Vertex(int x, int y, out double height)
            {
                int index = y * (n + 1) + x;
                if (vertices.TryGetValue(index, out height)) return true;
                if (!Height(new double2(x, y) / n, out height)) return false; vertices.Add(index, height); return true;
            }
            public bool Compare(double2 uv) { return Height(uv, out var height) && Compare(uv, height); }
            public bool Compare(double2 uv, double height)
            {
                if (!Spend()) return false;
                var grid = math.clamp(uv, 0, 1) * n;
                int x = (int)math.min(n - 1, math.floor(grid.x)), y = (int)math.min(n - 1, math.floor(grid.y));
                var f = grid - new double2(x, y);
                double aHeight,bHeight,cHeight,aWeight,bWeight,cWeight;
                double2 aUv,bUv,cUv;
                if (f.x + f.y <= 1)
                { if (!Vertex(x, y, out aHeight) || !Vertex(x + 1, y, out bHeight) || !Vertex(x, y + 1, out cHeight)) return false;
                    aWeight=1-f.x-f.y;bWeight=f.x;cWeight=f.y;
                    aUv=new double2(x,y)/n;bUv=new double2(x+1,y)/n;cUv=new double2(x,y+1)/n; }
                else
                { if (!Vertex(x+1,y+1,out aHeight)||!Vertex(x+1,y,out bHeight)||!Vertex(x,y+1,out cHeight))return false;
                    aWeight=f.x+f.y-1;bWeight=1-f.y;cWeight=1-f.x;
                    aUv=new double2(x+1,y+1)/n;bUv=new double2(x+1,y)/n;cUv=new double2(x,y+1)/n; }
                // A normalized cube mesh is not affine in face UV. Intersect the radial ray with the actual
                // triangle plane. Subtract its zero-height sphere intersection algebraically, without losing
                // metre residuals through subtraction of two planetary-radius positions.
                double r=view.Recipe.Radius;
                double la=aWeight*CubeLength(aUv),lb=bWeight*CubeLength(bUv),lc=cWeight*CubeLength(cUv);
                double qa=r/(r+aHeight),qb=r/(r+bHeight),qc=r/(r+cHeight);
                double inverse=la*qa+lb*qb+lc*qc;
                double difference=la*aHeight*qa+lb*bHeight*qb+lc*cHeight*qc;
                double coarse=CubeLength(uv)*(difference/((la+lb+lc)*inverse));
                if(!math.isfinite(coarse)){Missing=true;return false;}
                Maximum = math.max(Maximum, math.abs(height - coarse)); Comparisons++; return true;
            }
            double CubeLength(double2 uv)
            { var ab=2*(new double2(key.X,key.Y)+uv)/(1L<<key.Level)-1;return math.sqrt(1+math.lengthsq(ab)); }
        }
    }
}
