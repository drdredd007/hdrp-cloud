using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>
    /// Persisted whole-sphere structural controls and an explicit pre-erosion reference grid.
    /// The authoritative full field is eroded-grid + exact-macro - reference-grid + connected ridge/drainage band.
    /// The spatial index is derived and conservatively contains every possible influential edge.
    /// </summary>
    public sealed class SurfaceStructuralField
    {
        public const int CurrentVersion = 1;
        public const int SpatialResolution = 32, MaximumReferences = 262144, MaximumEdgesPerBin = 64;
        readonly SurfaceGeologicalProvince[] provinces;
        readonly SurfaceGeologicalBoundary[] boundaries;
        readonly float[] rawMacro;
        readonly int2[] bins;
        readonly int[] references;
        public SurfaceRecipe SourceRecipe { get; }
        public SurfaceDrainageField DrainageField { get; }
        public SurfaceLandformField LandformField { get; }
        public int MorphologyVersion => LandformField!=null?3:DrainageField == null ? CurrentVersion : 2;
        public SurfaceContentHash SourceBaseDigest { get; }
        public SurfaceContentHash ContentDigest { get; }
        public int RawMacroResolution { get; }
        public double ShelfWidthMetres { get; }
        public double BeltWidthMetres { get; }
        public double RegionalFeatureScaleMetres { get; }
        public double CoastThresholdMetres { get; }
        public double MountainFraction { get; }
        public double MinimumErosionResidual { get; }
        public double MaximumErosionResidual { get; }
        public double MinimumHeight { get; }
        public double MaximumHeight { get; }
        public IReadOnlyList<SurfaceGeologicalProvince> Provinces { get; }
        public IReadOnlyList<SurfaceGeologicalBoundary> Boundaries { get; }
        public int SpatialReferenceCount => references.Length;
        public long EstimatedResidentBytes => checked(512L + provinces.Length * 64L + boundaries.Length * 128L + rawMacro.LongLength * 4 + bins.LongLength * 8 + references.LongLength * 4 + (DrainageField?.EstimatedResidentBytes??0)+(LandformField?.EstimatedResidentBytes??0));

        public SurfaceStructuralField(SurfaceRecipe sourceRecipe, SurfaceContentHash baseDigest, int rawResolution, float[] rawMacro,
            SurfaceGeologicalProvince[] provinces, SurfaceGeologicalBoundary[] boundaries, double shelf, double belt, double feature,
            double coast, double mountainFraction, double minimumResidual, double maximumResidual, Func<bool> cancelled = null,SurfaceDrainageField drainageField=null,SurfaceLandformField landformField=null)
        {
            if (!sourceRecipe.IsValid || !SurfaceRecipe.HasStructuralAuthority(sourceRecipe.AlgorithmVersion) ||
                (sourceRecipe.AlgorithmVersion==4)!=(drainageField!=null) || (sourceRecipe.AlgorithmVersion==5)!=(landformField!=null) || !baseDigest.IsValid || !CubeSurface.ValidResolution(rawResolution) || rawResolution > 512 ||
                rawMacro == null || rawMacro.Length != 6L * (rawResolution + 1) * (rawResolution + 1) ||
                provinces == null || provinces.Length < 8 || provinces.Length > 64 || boundaries == null || boundaries.Length != 3 * provinces.Length - 6 ||
                !Positive(shelf) || shelf > sourceRecipe.Radius * .2 || !Positive(belt) || belt > sourceRecipe.Radius * .2 ||
                !Positive(feature) || feature > sourceRecipe.Radius * .025 || sourceRecipe.Radius * Math.PI / feature > int.MaxValue - 4 || !math.isfinite(coast) || !math.isfinite(mountainFraction) || mountainFraction < 0 || mountainFraction > 1 ||
                !math.isfinite(minimumResidual) || !math.isfinite(maximumResidual) || minimumResidual > maximumResidual)
                throw new ArgumentException("Invalid bounded structural field or pre-erosion reference grid.");
            SourceRecipe = sourceRecipe; SourceBaseDigest = baseDigest; RawMacroResolution = rawResolution;
            if(drainageField!=null && (SurfaceHashing.Recipe(drainageField.SourceRecipe)!=SurfaceHashing.Recipe(sourceRecipe) || drainageField.CoastInfluenceMetres<4*shelf))
                throw new ArgumentException("Captured coast/drainage data must match the exact source recipe and saturated shelf reach.");
            DrainageField=drainageField;
            if(landformField!=null&&SurfaceHashing.Recipe(landformField.SourceRecipe)!=SurfaceHashing.Recipe(sourceRecipe))
                throw new ArgumentException("Captured reconstruction must match the exact source recipe.");
            LandformField=landformField;
            ShelfWidthMetres = shelf; BeltWidthMetres = belt; RegionalFeatureScaleMetres = feature; CoastThresholdMetres = coast; MountainFraction = mountainFraction;
            MinimumErosionResidual = minimumResidual; MaximumErosionResidual = maximumResidual;
            this.rawMacro = (float[])rawMacro.Clone(); this.provinces = (SurfaceGeologicalProvince[])provinces.Clone(); this.boundaries = (SurfaceGeologicalBoundary[])boundaries.Clone();
            // The authoritative reference is explicitly float encoded. Admit only endpoint roundoff, not an
            // arbitrary out-of-bounds field; eroded-minus-reference extrema include that same stored quantization.
            double rawMinimum = math.min(sourceRecipe.MinimumHeight, (double)(float)sourceRecipe.MinimumHeight);
            double rawMaximum = math.max(sourceRecipe.MaximumHeight, (double)(float)sourceRecipe.MaximumHeight);
            foreach (float value in this.rawMacro) if (!math.isfinite(value) || value < rawMinimum || value > rawMaximum)
                throw new ArgumentException("Reference macro values must belong to the declared source bounds.");
            foreach (var p in this.provinces) if (!Unit(p.Center) || !math.all(math.isfinite(p.AngularMotion)) || math.lengthsq(p.AngularMotion) > 1.0000000001 || !math.isfinite(p.Buoyancy) || p.Buoyancy < 0 || p.Buoyancy > 1)
                throw new ArgumentException("Invalid geological province controls.");
            foreach (var e in this.boundaries) if (e.ProvinceA < 0 || e.ProvinceB <= e.ProvinceA || e.ProvinceB >= this.provinces.Length || !Unit(e.Start) || !Unit(e.End) || !Unit(e.Normal) ||
                !math.isfinite(e.Convergence) || math.abs(e.Convergence) > 1 || math.abs(math.dot(e.Start, e.Normal)) > 1e-10 || math.abs(math.dot(e.End, e.Normal)) > 1e-10)
                throw new ArgumentException("Invalid connected structural boundary.");
            double sea = math.clamp(sourceRecipe.SeaLevel, sourceRecipe.MinimumHeight, sourceRecipe.MaximumHeight);
            double amplitude = (sourceRecipe.MaximumHeight - sea) * .1 * math.min(1, mountainFraction / .3);
            MinimumHeight = landformField!=null?landformField.MinimumHeight+minimumResidual:drainageField==null?sourceRecipe.MinimumHeight + minimumResidual - amplitude * 1.15:
                math.min(sourceRecipe.MinimumHeight+minimumResidual,sourceRecipe.MinimumHeight);
            MaximumHeight = landformField!=null?landformField.MaximumHeight+maximumResidual:drainageField==null?sourceRecipe.MaximumHeight + maximumResidual + amplitude * .75:sourceRecipe.MaximumHeight+maximumResidual;
            if (!math.isfinite(MinimumHeight) || !math.isfinite(MaximumHeight) || MinimumHeight <= -sourceRecipe.Radius)
                throw new ArgumentException("Structural full-field bounds exceed the supported thin shell.");
            Provinces = Array.AsReadOnly(this.provinces); Boundaries = Array.AsReadOnly(this.boundaries);
            BuildIndex(cancelled, out bins, out references);
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(MorphologyVersion); SurfaceHashing.WriteRecipe(writer, SourceRecipe); SurfaceHashing.WriteHash(writer, SourceBaseDigest);
                writer.Write(RawMacroResolution); writer.Write(shelf); writer.Write(belt); writer.Write(feature); writer.Write(coast); writer.Write(mountainFraction);
                writer.Write(minimumResidual); writer.Write(maximumResidual);
                foreach (var p in this.provinces) { SurfaceHashing.WriteVector(writer, p.Center); SurfaceHashing.WriteVector(writer, p.AngularMotion); writer.Write(p.Continental); writer.Write(p.Buoyancy); }
                foreach (var e in this.boundaries) { writer.Write(e.ProvinceA); writer.Write(e.ProvinceB); writer.Write(e.StartVertex); writer.Write(e.EndVertex); SurfaceHashing.WriteVector(writer, e.Start); SurfaceHashing.WriteVector(writer, e.End); writer.Write(e.Convergence); }
                foreach (float value in this.rawMacro) writer.Write(value);
                if(DrainageField!=null)SurfaceHashing.WriteHash(writer,DrainageField.ContentDigest);
                if(LandformField!=null)SurfaceHashing.WriteHash(writer,LandformField.ContentDigest);
            });
        }
        public float RawMacroAt(int index) => rawMacro[index];
        public int2 SpatialBinAt(int index) => bins[index];
        public int SpatialReferenceAt(int index) => references[index];
        public float[] CopyRawMacro() => (float[])rawMacro.Clone();
        public int2[] CopySpatialBins() => (int2[])bins.Clone();
        public int[] CopySpatialReferences() => (int[])references.Clone();
        public static SurfaceStructuralField Capture(SurfaceRecipe sourceRecipe, SurfaceGeomorphology geometry,
            IReadOnlyList<SurfaceTileData> erodedTiles, Func<bool> cancelled = null,SurfaceDrainageField drainageField=null,SurfaceLandformField landformField=null)
        {
            if (geometry == null || !geometry.HasGlobalGrid || geometry.Radius != sourceRecipe.Radius || geometry.Seed != sourceRecipe.Seed || erodedTiles == null || erodedTiles.Count != 6)
                throw new ArgumentException("A structural authority requires the completed matching pre-erosion grid and all six eroded faces.");
            var raw = geometry.CopyRawMacroGrid(); int resolution = geometry.RawMacroResolution, count = (resolution + 1) * (resolution + 1);
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity; var seen = new bool[6];
            foreach (var tile in erodedTiles)
            {
                if (tile.Key.Level != 0 || tile.Key.X != 0 || tile.Key.Y != 0 || tile.Resolution != resolution || seen[tile.Key.Face]) throw new ArgumentException("Structural capture requires six unique matching canonical faces.");
                seen[tile.Key.Face] = true;
                for (int i = 0; i < count; i++)
                {
                    SurfaceBaker.CheckCancelled(cancelled, i);
                    double delta = (double)tile.HeightAt(i) - raw[tile.Key.Face * count + i];
                    minimum = math.min(minimum, delta); maximum = math.max(maximum, delta);
                }
            }
            var provinces = new SurfaceGeologicalProvince[geometry.Provinces.Count]; var edges = new SurfaceGeologicalBoundary[geometry.Boundaries.Count];
            for (int i = 0; i < provinces.Length; i++) provinces[i] = geometry.Provinces[i];
            for (int i = 0; i < edges.Length; i++) edges[i] = geometry.Boundaries[i];
            return new SurfaceStructuralField(sourceRecipe, geometry.SourceBaseDigest, resolution, raw, provinces, edges,
                geometry.ShelfWidthMetres, geometry.BeltWidthMetres, geometry.RegionalFeatureScaleMetres, geometry.CoastThresholdMetres, geometry.MountainUpliftFraction, minimum, maximum, cancelled,drainageField,landformField);
        }
        public SurfaceStructuralField ForBakedResolution(int resolution)
        {
            if (resolution == RawMacroResolution) return this;
            if (!CubeSurface.ValidResolution(resolution) || resolution > RawMacroResolution) throw new ArgumentException("A render LOD needs a strided matching pre-erosion grid.");
            var samples = new float[6 * (resolution + 1) * (resolution + 1)]; int stride = RawMacroResolution / resolution;
            for (int face = 0; face < 6; face++) for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
                samples[face * (resolution + 1) * (resolution + 1) + y * (resolution + 1) + x] = rawMacro[face * (RawMacroResolution + 1) * (RawMacroResolution + 1) + y * stride * (RawMacroResolution + 1) + x * stride];
            return new SurfaceStructuralField(SourceRecipe, SourceBaseDigest, resolution, samples, provinces, boundaries,
                ShelfWidthMetres, BeltWidthMetres, RegionalFeatureScaleMetres, CoastThresholdMetres, MountainFraction, MinimumErosionResidual, MaximumErosionResidual,drainageField:DrainageField,landformField:LandformField);
        }
        public long EstimatedResidentBytesAtResolution(int resolution)
        {
            if (!CubeSurface.ValidResolution(resolution) || resolution > RawMacroResolution) throw new ArgumentException("Structural render LOD resolution must stride the captured reference grid.");
            return checked(512L + provinces.Length * 64L + boundaries.Length * 128L + 6L * (resolution + 1) * (resolution + 1) * 4 + bins.LongLength * 8 + references.LongLength * 4 + (DrainageField?.EstimatedResidentBytes??0)+(LandformField?.EstimatedResidentBytes??0));
        }
        void BuildIndex(Func<bool> cancelled, out int2[] headers, out int[] indices)
        {
            headers = new int2[6 * SpatialResolution * SpatialResolution]; var lists = new List<int>(8192);
            var centers = new double3[boundaries.Length]; var edgeCos = new double[boundaries.Length]; var edgeSin = new double[boundaries.Length];
            double influence = math.max(BeltWidthMetres * 5.4, ShelfWidthMetres * 4 + math.abs(CoastThresholdMetres)) / SourceRecipe.Radius;
            for (int i = 0; i < boundaries.Length; i++)
            {
                var e = boundaries[i]; centers[i] = math.normalize(e.Start + e.End);
                double half = math.asin(math.min(1, math.length(e.Start - e.End) * .5));
                double cap = math.min(Math.PI, half + influence); edgeCos[i] = math.cos(cap); edgeSin[i] = math.sin(cap);
            }
            for (int face = 0; face < 6; face++) for (int y = 0; y < SpatialResolution; y++) for (int x = 0; x < SpatialResolution; x++)
            {
                SurfaceBaker.CheckCancelled(cancelled, 0);
                CubeSurface.TryDirection(new SurfaceTileKey(face, 0, 0, 0), new double2((x + .5) / SpatialResolution, (y + .5) / SpatialResolution), out var center);
                double binCos = 1;
                for (int dy = 0; dy <= 1; dy++) for (int dx = 0; dx <= 1; dx++)
                {
                    CubeSurface.TryDirection(new SurfaceTileKey(face, 0, 0, 0), new double2((double)(x + dx) / SpatialResolution, (double)(y + dy) / SpatialResolution), out var corner);
                    binCos = math.min(binCos, math.dot(center, corner));
                }
                double binSin = math.sqrt(math.max(0, 1 - binCos * binCos)); int start = lists.Count;
                for (int edge = 0; edge < boundaries.Length; edge++)
                {
                    // If cap+bin angle exceeds pi, the complete sphere must be included.
                    double limit = edgeCos[edge] * binCos - edgeSin[edge] * binSin;
                    if ((edgeCos[edge] < 0 && edgeSin[edge] <= binSin) || math.dot(center, centers[edge]) >= limit - 1e-12) lists.Add(edge);
                }
                int count = lists.Count - start;
                if (count > MaximumEdgesPerBin || lists.Count > MaximumReferences) throw new ArgumentException("Structural spatial index exceeds its explicit per-bin/reference budget before native/GPU allocation.");
                headers[face * SpatialResolution * SpatialResolution + y * SpatialResolution + x] = new int2(start, count);
            }
            indices = lists.ToArray();
        }
        static bool Unit(double3 value) => math.all(math.isfinite(value)) && math.abs(math.lengthsq(value) - 1) < 1e-10;
        static bool Positive(double value) => math.isfinite(value) && value > 0;
    }
}
