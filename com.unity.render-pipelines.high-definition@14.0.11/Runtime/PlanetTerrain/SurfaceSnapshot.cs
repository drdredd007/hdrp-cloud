using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Immutable resident data, independent of cameras, graphics, physical owners and asset objects.</summary>
    public sealed class SurfaceSnapshot
    {
        readonly SurfaceTileData[] tiles;
        readonly SurfaceRegionData[] regions;
        readonly SurfaceCraterStamp[] stamps;
        public SurfaceRecipe Recipe { get; }
        public SurfaceRevision Revision { get; }
        public int CanonicalTileLevel { get; }
        public int Resolution { get; }
        public SurfaceDetailRecipe Detail { get; }
        public double MinimumHeight { get; }
        public double MaximumHeight { get; }
        public SurfaceContentHash ContentDigest { get; }
        public SurfaceContentHash GeometryDigest { get; }
        public SurfaceContentHash MaterialRulesDigest { get; }
        public SurfaceAutomaticMaterialProfile AutomaticMaterialProfile { get; }
        public SurfaceResolvedMaterials ResolvedMaterials { get; }
        public SurfaceStructuralField StructuralField { get; }
        public WorldOrogenDetailField OrogenDetail { get; }
        public bool HasAutomaticMaterials => AutomaticMaterialProfile.IsValid;
        public bool MaterialsReady => !HasAutomaticMaterials || StructuralField != null || (ResolvedMaterials != null && ResolvedMaterials.StampLayers.Count == Stamps.Count);
        public IReadOnlyList<SurfaceTileData> Tiles { get; }
        public IReadOnlyList<SurfaceRegionData> Regions { get; }
        public IReadOnlyList<SurfaceCraterStamp> Stamps { get; }

        public SurfaceSnapshot(SurfaceRecipe recipe, SurfaceRevision revision, int canonicalTileLevel, int resolution,
            IEnumerable<SurfaceTileData> tiles, SurfaceDetailRecipe detail = default,
            IEnumerable<SurfaceRegionData> regions = null, IEnumerable<SurfaceCraterStamp> stamps = null,
            SurfaceAutomaticMaterialProfile automaticMaterialProfile = default, SurfaceResolvedMaterials resolvedMaterials = null,
            SurfaceStructuralField structuralField = null, WorldOrogenDetailField orogenDetail = null)
        {
            if (!recipe.IsValid || !revision.IsValid || revision.RecipeDigest != SurfaceHashing.Recipe(recipe) ||
                canonicalTileLevel < 0 || canonicalTileLevel > SurfaceTileKey.MaximumLevel || !CubeSurface.ValidResolution(resolution))
                throw new ArgumentException("A snapshot requires a matching recipe digest, valid revision and canonical vertex grid.");
            if (detail.WavelengthMetres == 0 && detail.AmplitudeMetres == 0) detail = SurfaceDetailRecipe.Disabled;
            if (!detail.IsValid) throw new ArgumentException("Invalid metric detail recipe.", nameof(detail));
            Recipe = recipe; Revision = revision; CanonicalTileLevel = canonicalTileLevel; Resolution = resolution; Detail = detail;
            if (automaticMaterialProfile.Version != 0 && !automaticMaterialProfile.IsValid) throw new ArgumentException("Invalid automatic material rules.");
            AutomaticMaterialProfile = automaticMaterialProfile;
            if (SurfaceRecipe.HasStructuralAuthority(recipe.AlgorithmVersion) != (structuralField != null))
                throw new ArgumentException("Algorithm three requires explicit persisted structural authority; algorithms one and two retain their baked semantics.");
            if (structuralField != null && (canonicalTileLevel != 0 || structuralField.RawMacroResolution != resolution ||
                structuralField.SourceRecipe.AlgorithmVersion != recipe.AlgorithmVersion ||
                structuralField.SourceBaseDigest != revision.BaseDigest || structuralField.SourceRecipe.Radius != recipe.Radius || structuralField.SourceRecipe.Seed != recipe.Seed))
                throw new ArgumentException("Structural authority must match the canonical grid, radius, seed and exact base provenance.");
            StructuralField = structuralField;
            if (orogenDetail != null && (structuralField != null || recipe.AlgorithmVersion != SurfaceRecipe.CurrentAlgorithmVersion ||
                canonicalTileLevel != 0 || orogenDetail.SourceBaseDigest != revision.BaseDigest ||
                orogenDetail.SourceRadius != recipe.Radius || orogenDetail.SeaLevel != recipe.SeaLevel))
                throw new ArgumentException("WorldOrogen detail must match its retained base provenance, radius and sea level.");
            OrogenDetail = orogenDetail;
            var tileList = tiles == null ? new List<SurfaceTileData>() : new List<SurfaceTileData>(tiles);
            foreach (var tile in tileList)
                if (tile == null || tile.Key.Level != canonicalTileLevel || tile.Resolution != resolution ||
                    tile.MinimumHeight < recipe.MinimumHeight || tile.MaximumHeight > recipe.MaximumHeight)
                    throw new ArgumentException("Resident tiles must match the canonical grid and declared base height bounds.", nameof(tiles));
            tileList.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 1; i < tileList.Count; i++)
                if (tileList[i - 1].Key.Equals(tileList[i].Key)) throw new ArgumentException("Duplicate resident tile.", nameof(tiles));
            this.tiles = tileList.ToArray();
            ValidateSharedEdges(this.tiles, canonicalTileLevel, resolution);
            var regionList = regions == null ? new List<SurfaceRegionData>() : new List<SurfaceRegionData>(regions);
            foreach (var region in regionList)
                if (region == null || region.Projection.Radius != recipe.Radius ||
                    ((region.Mode == SurfaceRegionMode.Delta || region.Kind == SurfaceRegionKind.GeneratedRefinement) && region.BaseDigest != revision.BaseDigest))
                    throw new ArgumentException("A region requires the same radius; delta requires the exact exported base digest.", nameof(regions));
            regionList.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            for (int i = 1; i < regionList.Count; i++)
                if (regionList[i - 1].Priority == regionList[i].Priority)
                    throw new ArgumentException("Overlapping layer order must use unique explicit priorities.", nameof(regions));
            this.regions = regionList.ToArray();
            var stampList = stamps == null ? new List<SurfaceCraterStamp>() : new List<SurfaceCraterStamp>(stamps);
            foreach (var stamp in stampList)
                if (!stamp.IsValid || stamp.RadiusMetres + stamp.RimWidthMetres > Math.PI * recipe.Radius)
                    throw new ArgumentException("Invalid crater stamp.", nameof(stamps));
            stampList.Sort();
            for (int i = stampList.Count - 1; i > 0; i--)
                if (stampList[i - 1].CompareTo(stampList[i]) == 0)
                {
                    if (!stampList[i - 1].Equals(stampList[i])) throw new ArgumentException("A stamp ID cannot identify conflicting commands.", nameof(stamps));
                    stampList.RemoveAt(i); // A repeated identical command is idempotent.
                }
            this.stamps = stampList.ToArray();
            double min = recipe.MinimumHeight, max = recipe.MaximumHeight;
            if (orogenDetail != null) { min -= orogenDetail.MaximumAmplitude; max += orogenDetail.MaximumAmplitude; }
            if (structuralField != null) { min = math.min(min, structuralField.MinimumHeight); max = math.max(max, structuralField.MaximumHeight); }
            foreach (var region in this.regions)
                if (region.Mode == SurfaceRegionMode.Replace)
                { min = math.min(min, region.MinimumHeight); max = math.max(max, region.MaximumHeight); }
                else { min += math.min(0, region.MinimumHeight); max += math.max(0, region.MaximumHeight); }
            min -= detail.AmplitudeMetres; max += detail.AmplitudeMetres;
            foreach (var stamp in this.stamps) { min -= stamp.DepthMetres; max += stamp.RimHeightMetres; }
            if (!math.isfinite(min) || !math.isfinite(max) || min <= -recipe.Radius)
                throw new ArgumentException("Conservative final bounds must remain finite and outside the planet centre.");
            MinimumHeight = min; MaximumHeight = max;
            Tiles = Array.AsReadOnly(this.tiles); Regions = Array.AsReadOnly(this.regions); Stamps = Array.AsReadOnly(this.stamps);
            GeometryDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); writer.Write(Recipe.Radius); writer.Write(CanonicalTileLevel); writer.Write(Resolution); writer.Write(this.tiles.Length);
                foreach (var tile in this.tiles) { SurfaceHashing.WriteKey(writer,tile.Key); foreach(var height in tile.CopyHeights()) writer.Write(height); }
                writer.Write(Detail.WavelengthMetres); writer.Write(Detail.AmplitudeMetres); writer.Write(Detail.Seed); writer.Write(this.regions.Length);
                foreach(var region in this.regions)
                {
                    SurfaceHashing.WriteProjection(writer,region.Projection); writer.Write(region.Resolution.x); writer.Write(region.Resolution.y);
                    writer.Write(region.Priority); writer.Write((int)region.Mode); writer.Write((int)region.DetailPolicy); writer.Write(region.BlendMetres);
                    foreach(var height in region.CopyHeights()) writer.Write(height); foreach(var mask in region.CopyBlendMask()) writer.Write(mask);
                }
                writer.Write(this.stamps.Length);
                foreach(var stamp in this.stamps) { writer.Write(stamp.IdHigh); writer.Write(stamp.IdLow); SurfaceHashing.WriteVector(writer,stamp.CenterDirection); writer.Write(stamp.RadiusMetres); writer.Write(stamp.DepthMetres); writer.Write(stamp.RimWidthMetres); writer.Write(stamp.RimHeightMetres); }
                if (StructuralField != null) { writer.Write(6); SurfaceHashing.WriteHash(writer, StructuralField.ContentDigest); }
                if (OrogenDetail != null) { writer.Write(9); SurfaceHashing.WriteHash(writer, OrogenDetail.ContentDigest); }
            });
            MaterialRulesDigest = HasAutomaticMaterials ? SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); SurfaceAutomaticMaterialProfile.Write(writer,AutomaticMaterialProfile); SurfaceHashing.WriteRecipe(writer,Recipe);
                writer.Write(this.tiles.Length); foreach(var tile in this.tiles) SurfaceHashing.WriteHash(writer,tile.ContentHash);
                writer.Write(this.regions.Length); foreach(var region in this.regions) SurfaceHashing.WriteHash(writer,region.ContentHash);
                if (StructuralField != null) SurfaceHashing.WriteHash(writer, StructuralField.ContentDigest);
                if (OrogenDetail != null) SurfaceHashing.WriteHash(writer, OrogenDetail.ContentDigest);
            }) : default;
            if (resolvedMaterials != null)
            {
                if (!HasAutomaticMaterials || resolvedMaterials.GeometryDigest != GeometryDigest || resolvedMaterials.RulesDigest != MaterialRulesDigest || resolvedMaterials.Tiles.Count != this.tiles.Length)
                    throw new ArgumentException("Resolved material weights belong to different geometry or material sources.");
                for(int i=0;i<this.tiles.Length;i++) if(!resolvedMaterials.Tiles[i].Key.Equals(this.tiles[i].Key) || resolvedMaterials.Tiles[i].Resolution != Resolution)
                    throw new ArgumentException("Resolved material tiles must match all resident height tiles.");
                if(resolvedMaterials.Layers.Count != this.regions.Length)throw new ArgumentException("Resolved material cache must cover every source region and its normal halo.");
                for(int i=0;i<this.regions.Length;i++)
                {
                    var source=this.regions[i];var layer=resolvedMaterials.Layers[i];
                    SurfaceResolvedMaterials.RequiredLayerLayout(AutomaticMaterialProfile,source,out var p,out var size,out var blend);
                    var actual=layer.Projection;
                    if(layer.SourcePriority!=source.Priority || !math.all(layer.Resolution==size) || layer.BlendMetres!=blend || actual.Radius!=p.Radius ||
                        !math.all(actual.AnchorDirection==p.AnchorDirection)||!math.all(actual.Right==p.Right)||!math.all(actual.Forward==p.Forward)||
                        !math.all(actual.MinimumMetres==p.MinimumMetres)||!math.all(actual.MaximumMetres==p.MaximumMetres))
                        throw new ArgumentException("Resolved material cache manifest differs from the source grid, order or required normal halo.");
                }
                if(resolvedMaterials.StampLayers.Count>0)
                {
                    if(resolvedMaterials.StampLayers.Count!=this.stamps.Length)throw new ArgumentException("Resolved stamp materials must cover every instance command.");
                    for(int i=0;i<this.stamps.Length;i++)if(!resolvedMaterials.StampLayers[i].Matches(Recipe,AutomaticMaterialProfile,Regions,this.stamps[i]))
                        throw new ArgumentException("Resolved stamp cache manifest differs from its command, sampling policy or normal halo.");
                }
                if (this.regions.Length > 0 && this.regions[this.regions.Length-1].Priority > int.MaxValue-resolvedMaterials.Layers.Count-resolvedMaterials.StampLayers.Count)
                    throw new ArgumentException("Reserve priorities below Int32.MaxValue for resolved material overlays.");
            }
            if (StructuralField != null && resolvedMaterials != null) throw new ArgumentException("Algorithm three computes captured automatic rules on final Full geometry; baked resolved material overlays cannot replace those rules.");
            ResolvedMaterials = resolvedMaterials;
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); SurfaceHashing.WriteRecipe(writer, Recipe); SurfaceHashing.WriteHash(writer, Revision.BaseDigest);
                writer.Write(CanonicalTileLevel); writer.Write(Resolution); writer.Write(this.tiles.Length);
                foreach (var tile in this.tiles) SurfaceHashing.WriteHash(writer, tile.ContentHash);
                writer.Write(Detail.WavelengthMetres); writer.Write(Detail.AmplitudeMetres); writer.Write(Detail.Seed);
                writer.Write(this.regions.Length); foreach (var region in this.regions) SurfaceHashing.WriteHash(writer, region.ContentHash);
                writer.Write(this.stamps.Length);
                foreach (var stamp in this.stamps)
                {
                    writer.Write(stamp.IdHigh); writer.Write(stamp.IdLow); SurfaceHashing.WriteVector(writer, stamp.CenterDirection);
                    writer.Write(stamp.RadiusMetres); writer.Write(stamp.DepthMetres); writer.Write(stamp.RimWidthMetres); writer.Write(stamp.RimHeightMetres);
                }
                if (HasAutomaticMaterials)
                {
                    writer.Write(4); SurfaceAutomaticMaterialProfile.Write(writer,AutomaticMaterialProfile); writer.Write(ResolvedMaterials != null);
                    if (ResolvedMaterials != null) SurfaceHashing.WriteHash(writer,ResolvedMaterials.ContentDigest);
                }
                if (StructuralField != null) { writer.Write(6); SurfaceHashing.WriteHash(writer, StructuralField.ContentDigest); }
                if (OrogenDetail != null) { writer.Write(9); SurfaceHashing.WriteHash(writer, OrogenDetail.ContentDigest); }
            });
        }

        public NativeSurfaceSnapshot CreateNative(Allocator allocator) => new NativeSurfaceSnapshot(this, allocator);

        // Every loaded duplicated boundary sample must agree exactly. Missing neighbours remain legal residency gaps.
        static void ValidateSharedEdges(SurfaceTileData[] tiles, int level, int resolution)
        {
            var shared = new Dictionary<GridPoint, float>(); long count = (1L << level) * resolution;
            foreach (var tile in tiles)
                for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
                {
                    if (x != 0 && y != 0 && x != resolution && y != resolution) continue;
                    long a = 2 * ((long)tile.Key.X * resolution + x) - count;
                    long b = 2 * ((long)tile.Key.Y * resolution + y) - count;
                    var point = GridPoint.For(tile.Key.Face, count, a, b); float value = tile.HeightAt(y * (resolution + 1) + x);
                    if (shared.TryGetValue(point, out var previous) && previous != value)
                        throw new ArgumentException("Loaded neighbouring cube tiles disagree at a shared vertex.");
                    shared[point] = value;
                }
        }
        readonly struct GridPoint : IEquatable<GridPoint>
        {
            readonly long x, y, z;
            GridPoint(long x, long y, long z) { this.x = x; this.y = y; this.z = z; }
            public static GridPoint For(int face, long count, long a, long b)
            {
                switch (face)
                {
                    case 0: return new GridPoint(count, b, -a);
                    case 1: return new GridPoint(-count, b, a);
                    case 2: return new GridPoint(a, count, -b);
                    case 3: return new GridPoint(a, -count, b);
                    case 4: return new GridPoint(a, b, count);
                    default: return new GridPoint(-a, b, -count);
                }
            }
            public bool Equals(GridPoint other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object other) => other is GridPoint point && Equals(point);
            public override int GetHashCode() { unchecked { return (x.GetHashCode() * 397 ^ y.GetHashCode()) * 397 ^ z.GetHashCode(); } }
        }
    }

    /// <summary>Borrowed readonly job view. Its owner must outlive every reader/job.</summary>
    public readonly struct NativeSurfaceView
    {
        public readonly SurfaceRecipe Recipe;
        public readonly SurfaceRevision Revision;
        public readonly SurfaceContentHash ContentDigest;
        public readonly int CanonicalTileLevel, Resolution;
        public readonly double MinimumHeight, MaximumHeight;
        public readonly SurfaceDetailRecipe Detail;
        public readonly NativeSurfaceStructuralView StructuralField;
        public readonly NativeWorldOrogenDetailView OrogenDetail;
        public readonly SurfaceAutomaticMaterialProfile AutomaticMaterialProfile;
        public readonly NativeArray<int>.ReadOnly TileMaterialProvenance;
        public readonly NativeArray<SurfaceAutomaticMaterialProfile>.ReadOnly RegionalMaterialProfiles;
        public readonly NativeArray<SurfaceTileHeader>.ReadOnly Tiles;
        public readonly NativeArray<float>.ReadOnly Heights;
        public readonly NativeArray<SurfaceRegionHeader>.ReadOnly Regions;
        public readonly NativeArray<float>.ReadOnly RegionHeights, RegionMasks;
        public readonly NativeArray<float4>.ReadOnly MaterialWeights, ErosionData, RegionMaterialWeights, RegionErosionData;
        public readonly NativeArray<SurfaceCraterStamp>.ReadOnly Stamps;
        internal NativeSurfaceView(SurfaceSnapshot snapshot, NativeArray<SurfaceTileHeader> tiles, NativeArray<float> heights,
            NativeArray<SurfaceRegionHeader> regions, NativeArray<float> regionHeights, NativeArray<float> regionMasks, NativeArray<SurfaceCraterStamp> stamps,
            NativeArray<float4> materialWeights, NativeArray<float4> erosionData, NativeArray<float4> regionMaterialWeights, NativeArray<float4> regionErosionData, NativeSurfaceStructuralView structuralField, NativeArray<int> tileProvenance, NativeArray<SurfaceAutomaticMaterialProfile> regionalProfiles,
            NativeWorldOrogenDetailView orogenDetail = default)
        {
            Recipe = snapshot.Recipe; Revision = snapshot.Revision; ContentDigest = snapshot.ContentDigest;
            CanonicalTileLevel = snapshot.CanonicalTileLevel; Resolution = snapshot.Resolution;
            MinimumHeight = snapshot.MinimumHeight; MaximumHeight = snapshot.MaximumHeight; Detail = snapshot.Detail; StructuralField = structuralField; AutomaticMaterialProfile = snapshot.AutomaticMaterialProfile;
            OrogenDetail = orogenDetail;
            TileMaterialProvenance = tileProvenance.AsReadOnly(); RegionalMaterialProfiles = regionalProfiles.AsReadOnly();
            Tiles = tiles.AsReadOnly(); Heights = heights.AsReadOnly(); Regions = regions.AsReadOnly();
            RegionHeights = regionHeights.AsReadOnly(); RegionMasks = regionMasks.AsReadOnly(); Stamps = stamps.AsReadOnly();
            MaterialWeights = materialWeights.AsReadOnly(); ErosionData = erosionData.AsReadOnly(); RegionMaterialWeights = regionMaterialWeights.AsReadOnly();
            RegionErosionData = regionErosionData.AsReadOnly();
        }
        NativeSurfaceView(in NativeSurfaceView source,in NativeSurfaceLandformFilterView filter)
        {this=source;StructuralField=source.StructuralField.WithLandformFilter(filter);}
        /// <summary>Attach an explicitly retained renderer cache without changing canonical arrays, identity or physics ownership.</summary>
        public NativeSurfaceView WithLandformFilter(in NativeSurfaceLandformFilterView filter)=>new NativeSurfaceView(this,filter);
    }

    /// <summary>Owns native copies. Dispose only after readers finish, or schedule release with their dependency.</summary>
    public sealed class NativeSurfaceSnapshot : IDisposable
    {
        NativeArray<SurfaceTileHeader> tiles;
        NativeArray<float> heights;
        NativeArray<SurfaceRegionHeader> regions;
        NativeArray<float> regionHeights, regionMasks;
        NativeArray<float4> materialWeights, erosionData, regionMaterialWeights, regionErosionData;
        NativeArray<SurfaceCraterStamp> stamps;
        readonly SurfaceSnapshot snapshot;
        NativeSurfaceStructuralData structuralField;
        NativeWorldOrogenDetailData orogenDetail;
        NativeArray<int> tileProvenance;
        NativeArray<SurfaceAutomaticMaterialProfile> regionalProfiles;
        bool disposed;
        public bool IsDisposed => disposed;
        public NativeSurfaceView View
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(NativeSurfaceSnapshot));
                return new NativeSurfaceView(snapshot, tiles, heights, regions, regionHeights, regionMasks, stamps, materialWeights, erosionData, regionMaterialWeights, regionErosionData, structuralField.View, tileProvenance, regionalProfiles, orogenDetail.View);
            }
        }
        internal NativeSurfaceSnapshot(SurfaceSnapshot snapshot, Allocator allocator)
        {
            this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (allocator == Allocator.None || allocator == Allocator.Invalid) throw new ArgumentException("Native copies require an owning allocator.", nameof(allocator));
            try
            {
                structuralField = new NativeSurfaceStructuralData(snapshot.StructuralField, allocator);
                orogenDetail = new NativeWorldOrogenDetailData(snapshot.OrogenDetail, allocator);
                bool structuralMaterials = snapshot.StructuralField != null;
                var resolved = structuralMaterials ? null : snapshot.ResolvedMaterials;
                tileProvenance = new NativeArray<int>(structuralMaterials ? snapshot.Tiles.Count : 0, allocator);
                regionalProfiles = new NativeArray<SurfaceAutomaticMaterialProfile>(structuralMaterials ? snapshot.Regions.Count : 0, allocator);
                int heightCount = 0, regionCount = 0;
                foreach (var tile in snapshot.Tiles) heightCount = checked(heightCount + tile.SampleCount);
                foreach (var region in snapshot.Regions) regionCount = checked(regionCount + region.SampleCount);
                if(resolved != null) foreach(var layer in resolved.Layers) regionCount = checked(regionCount+layer.SampleCount);
                if(resolved != null) foreach(var layer in resolved.StampLayers) regionCount = checked(regionCount+layer.SampleCount);
                tiles = new NativeArray<SurfaceTileHeader>(snapshot.Tiles.Count, allocator);
                heights = new NativeArray<float>(heightCount, allocator);
                materialWeights = new NativeArray<float4>(heightCount, allocator); erosionData = new NativeArray<float4>(heightCount, allocator);
                int derivedCount=(resolved?.Layers.Count??0)+(resolved?.StampLayers.Count??0);
                regions = new NativeArray<SurfaceRegionHeader>(snapshot.Regions.Count+derivedCount, allocator);
                regionHeights = new NativeArray<float>(regionCount, allocator); regionMasks = new NativeArray<float>(regionCount, allocator);
                regionMaterialWeights = new NativeArray<float4>(regionCount, allocator);
                regionErosionData = new NativeArray<float4>(regionCount, allocator);
                stamps = new NativeArray<SurfaceCraterStamp>(snapshot.Stamps.Count, allocator);
                int offset = 0;
                for (int i = 0; i < snapshot.Tiles.Count; i++)
                {
                    var tile = snapshot.Tiles[i]; if (structuralMaterials) tileProvenance[i] = (int)tile.MaterialProvenance; tiles[i] = new SurfaceTileHeader(tile.Key, tile.Resolution, offset, tile.MinimumHeight, tile.MaximumHeight, tile.ContentHash,
                        (structuralMaterials && tile.MaterialProvenance != SurfaceMaterialProvenance.Authored ? tile.Channels & ~SurfaceChannels.MaterialWeights : resolved == null ? tile.Channels : tile.Channels | SurfaceChannels.MaterialWeights), offset, tile.MeasuredLodErrorMetres);
                    for (int j = 0; j < tile.SampleCount; j++)
                    {
                        heights[offset + j] = tile.HeightAt(j);
                        if(resolved != null) materialWeights[offset+j]=resolved.Tiles[i].WeightAt(j);
                        else if (tile.HasMaterialWeights) materialWeights[offset + j] = tile.MaterialWeightsAt(j);
                        if (tile.HasErosionData) erosionData[offset + j] = tile.ErosionDataAt(j);
                    }
                    offset += tile.SampleCount;
                }
                offset = 0;
                for (int i = 0; i < snapshot.Regions.Count; i++)
                {
                    var region = snapshot.Regions[i]; if (structuralMaterials) regionalProfiles[i] = region.AutomaticMaterialProfile;
                    regions[i] = new SurfaceRegionHeader(region.Projection, region.Resolution, offset, offset, region.Priority, region.BlendMetres,
                        region.MinimumHeight, region.MaximumHeight, region.Mode, region.DetailPolicy, region.BaseDigest, region.ContentHash,
                        (region.HasMaterialWeights && resolved == null && (!structuralMaterials || region.MaterialProvenance == SurfaceMaterialProvenance.Authored) ? SurfaceChannels.MaterialWeights : SurfaceChannels.None) |
                        (region.HasErosionData ? SurfaceChannels.ErosionData : SurfaceChannels.None), offset);
                    for (int j = 0; j < region.SampleCount; j++)
                    {
                        regionHeights[offset + j] = region.HeightAt(j); regionMasks[offset + j] = region.MaskAt(j);
                        if (region.HasMaterialWeights) regionMaterialWeights[offset + j] = region.MaterialWeightsAt(j);
                        if (region.HasErosionData) regionErosionData[offset + j] = region.ErosionDataAt(j);
                    }
                    offset += region.SampleCount;
                }
                if(resolved != null) for(int i=0;i<resolved.Layers.Count;i++)
                {
                    var layer=resolved.Layers[i];
                    regions[snapshot.Regions.Count+i]=new SurfaceRegionHeader(layer.Projection,layer.Resolution,offset,offset,
                        int.MaxValue-derivedCount+1+i,layer.BlendMetres,0,0,SurfaceRegionMode.Delta,SurfaceDetailPolicy.Preserve,
                        snapshot.Revision.BaseDigest,resolved.ContentDigest,SurfaceChannels.MaterialWeights,offset);
                    for(int j=0;j<layer.SampleCount;j++) { regionHeights[offset+j]=0; regionMasks[offset+j]=1; regionMaterialWeights[offset+j]=layer.WeightAt(j); }
                    offset+=layer.SampleCount;
                }
                if(resolved != null) for(int i=0;i<resolved.StampLayers.Count;i++)
                {
                    var layer=resolved.StampLayers[i];
                    regions[snapshot.Regions.Count+resolved.Layers.Count+i]=new SurfaceRegionHeader(layer.Projection,layer.Resolution,offset,offset,
                        int.MaxValue-resolved.StampLayers.Count+1+i,layer.BlendMetres,0,0,SurfaceRegionMode.Delta,SurfaceDetailPolicy.Preserve,
                        snapshot.Revision.BaseDigest,resolved.ContentDigest,SurfaceChannels.MaterialWeights,offset);
                    for(int j=0;j<layer.SampleCount;j++){regionHeights[offset+j]=0;regionMasks[offset+j]=1;regionMaterialWeights[offset+j]=layer.WeightAt(j);}
                    offset+=layer.SampleCount;
                }
                for (int i = 0; i < snapshot.Stamps.Count; i++) stamps[i] = snapshot.Stamps[i];
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            structuralField?.Dispose();
            orogenDetail?.Dispose();
            if (tileProvenance.IsCreated) tileProvenance.Dispose(); if (regionalProfiles.IsCreated) regionalProfiles.Dispose();
            if (tiles.IsCreated) tiles.Dispose(); if (heights.IsCreated) heights.Dispose(); if (regions.IsCreated) regions.Dispose();
            if (regionHeights.IsCreated) regionHeights.Dispose(); if (regionMasks.IsCreated) regionMasks.Dispose(); if (stamps.IsCreated) stamps.Dispose();
            if (materialWeights.IsCreated) materialWeights.Dispose(); if (erosionData.IsCreated) erosionData.Dispose(); if (regionMaterialWeights.IsCreated) regionMaterialWeights.Dispose();
            if (regionErosionData.IsCreated) regionErosionData.Dispose();
            disposed = true;
        }
        public JobHandle Dispose(JobHandle readers)
        {
            if (disposed) return readers;
            var result = structuralField == null ? readers : structuralField.Dispose(readers);
            if (orogenDetail != null) result = JobHandle.CombineDependencies(result, orogenDetail.Dispose(readers));
            if (tileProvenance.IsCreated) result = JobHandle.CombineDependencies(result, tileProvenance.Dispose(readers));
            if (regionalProfiles.IsCreated) result = JobHandle.CombineDependencies(result, regionalProfiles.Dispose(readers));
            if (tiles.IsCreated) result = JobHandle.CombineDependencies(result, tiles.Dispose(readers));
            if (heights.IsCreated) result = JobHandle.CombineDependencies(result, heights.Dispose(readers));
            if (regions.IsCreated) result = JobHandle.CombineDependencies(result, regions.Dispose(readers));
            if (regionHeights.IsCreated) result = JobHandle.CombineDependencies(result, regionHeights.Dispose(readers));
            if (regionMasks.IsCreated) result = JobHandle.CombineDependencies(result, regionMasks.Dispose(readers));
            if (stamps.IsCreated) result = JobHandle.CombineDependencies(result, stamps.Dispose(readers));
            if (materialWeights.IsCreated) result = JobHandle.CombineDependencies(result, materialWeights.Dispose(readers));
            if (erosionData.IsCreated) result = JobHandle.CombineDependencies(result, erosionData.Dispose(readers));
            if (regionMaterialWeights.IsCreated) result = JobHandle.CombineDependencies(result, regionMaterialWeights.Dispose(readers));
            if (regionErosionData.IsCreated) result = JobHandle.CombineDependencies(result, regionErosionData.Dispose(readers));
            disposed = true; return result;
        }
    }
}
