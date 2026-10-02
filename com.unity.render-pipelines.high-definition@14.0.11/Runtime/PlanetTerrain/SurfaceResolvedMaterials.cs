using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Resolved final weights; source authored masks remain in the original immutable tiles/regions.</summary>
    public sealed class SurfaceMaterialTileData
    {
        readonly float4[] weights;
        public SurfaceTileKey Key { get; }
        public int Resolution { get; }
        public int SampleCount => weights.Length;
        public SurfaceMaterialTileData(SurfaceTileKey key, int resolution, float4[] weights)
        {
            if (!key.IsValid || !CubeSurface.ValidResolution(resolution)) throw new ArgumentException("Invalid resolved material tile.");
            Key = key; Resolution = resolution; this.weights = SurfaceDataValidation.Attributes(weights, checked((resolution + 1) * (resolution + 1)), true) ?? throw new ArgumentNullException(nameof(weights));
        }
        public float4 WeightAt(int index) => weights[index];
        public float4[] CopyWeights() => (float4[])weights.Clone();
    }
    public sealed class SurfaceMaterialLayerData
    {
        readonly float4[] weights;
        public SurfaceRegionProjection Projection { get; }
        public int2 Resolution { get; }
        public double BlendMetres { get; }
        public int SourcePriority { get; }
        public int SampleCount => weights.Length;
        public SurfaceMaterialLayerData(SurfaceRegionProjection projection, int2 resolution, double blendMetres, int sourcePriority, float4[] weights)
        {
            if (!projection.IsValid || math.any(resolution < 1) || math.any(resolution > 4096) || !math.isfinite(blendMetres) || blendMetres < 0) throw new ArgumentException("Invalid resolved material layer.");
            Projection = projection; Resolution = resolution; BlendMetres = blendMetres; SourcePriority = sourcePriority;
            this.weights = SurfaceDataValidation.Attributes(weights, checked((resolution.x + 1) * (resolution.y + 1)), true) ?? throw new ArgumentNullException(nameof(weights));
        }
        public float4 WeightAt(int index) => weights[index];
        public float4[] CopyWeights() => (float4[])weights.Clone();
    }
    public sealed class SurfaceResolvedMaterials
    {
        public SurfaceContentHash GeometryDigest { get; }
        public SurfaceContentHash RulesDigest { get; }
        public SurfaceContentHash ContentDigest { get; }
        public IReadOnlyList<SurfaceMaterialTileData> Tiles { get; }
        public IReadOnlyList<SurfaceMaterialLayerData> Layers { get; }
        public IReadOnlyList<SurfaceStampMaterialData> StampLayers { get; }
        public SurfaceResolvedMaterials(SurfaceContentHash geometryDigest, SurfaceContentHash rulesDigest, IEnumerable<SurfaceMaterialTileData> tiles, IEnumerable<SurfaceMaterialLayerData> layers = null,
            IEnumerable<SurfaceStampMaterialData> stampLayers = null)
        {
            if (!geometryDigest.IsValid || !rulesDigest.IsValid) throw new ArgumentException("Resolved weights require final geometry and material rule digests.");
            GeometryDigest = geometryDigest; RulesDigest = rulesDigest;
            var tileList = new List<SurfaceMaterialTileData>(tiles ?? throw new ArgumentNullException(nameof(tiles)));
            foreach(var tile in tileList)if(tile==null)throw new ArgumentException("Null resolved tile.");
            tileList.Sort((a,b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < tileList.Count; i++) if (tileList[i] == null || (i > 0 && tileList[i-1].Key.Equals(tileList[i].Key))) throw new ArgumentException("Duplicate resolved tile.");
            var layerList = layers == null ? new List<SurfaceMaterialLayerData>() : new List<SurfaceMaterialLayerData>(layers);
            foreach(var layer in layerList)if(layer==null)throw new ArgumentException("Null resolved material layer.");
            layerList.Sort((a,b) => a.SourcePriority.CompareTo(b.SourcePriority));
            for (int i = 0; i < layerList.Count; i++) if (layerList[i] == null || (i > 0 && layerList[i-1].SourcePriority == layerList[i].SourcePriority)) throw new ArgumentException("Duplicate resolved material layer.");
            var stampList = stampLayers == null ? new List<SurfaceStampMaterialData>() : new List<SurfaceStampMaterialData>(stampLayers);
            foreach(var layer in stampList)if(layer==null)throw new ArgumentException("Null stamp material layer.");
            stampList.Sort((a,b)=>a.CompareId(b));
            for(int i=1;i<stampList.Count;i++)if(stampList[i-1].CompareId(stampList[i])==0)throw new ArgumentException("Duplicate stamp material layer.");
            Tiles = tileList.AsReadOnly(); Layers = layerList.AsReadOnly(); StampLayers = stampList.AsReadOnly();
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); SurfaceHashing.WriteHash(writer, GeometryDigest); SurfaceHashing.WriteHash(writer, RulesDigest); writer.Write(Tiles.Count);
                foreach(var tile in Tiles) { SurfaceHashing.WriteKey(writer,tile.Key); writer.Write(tile.Resolution); SurfaceHashing.WriteAttributes(writer,tile.CopyWeights()); }
                writer.Write(Layers.Count);
                foreach(var layer in Layers) { SurfaceHashing.WriteProjection(writer,layer.Projection); writer.Write(layer.Resolution.x); writer.Write(layer.Resolution.y); writer.Write(layer.BlendMetres); writer.Write(layer.SourcePriority); SurfaceHashing.WriteAttributes(writer,layer.CopyWeights()); }
                // An absent extension keeps the canonical V4 digest unchanged.
                if(StampLayers.Count>0){writer.Write(5);writer.Write(StampLayers.Count);foreach(var layer in StampLayers)layer.Write(writer);}
            });
        }
        internal static void RequiredLayerLayout(SurfaceAutomaticMaterialProfile profile,SurfaceRegionData region,
            out SurfaceRegionProjection projection,out int2 resolution,out double blend)
        {
            var p=region.Projection;double2 pitch=(p.MaximumMetres-p.MinimumMetres)/(double2)region.Resolution;
            double support=profile.NormalSampleMetres;
            if(region.AutomaticMaterialProfile.IsValid)support=math.max(support,region.AutomaticMaterialProfile.NormalSampleMetres);
            double2 required=math.ceil(math.max(pitch*2,support*2)/pitch);
            if(!math.all(math.isfinite(required))||math.any(required>2048))throw new ArgumentException("Material normal/context halo exceeds its bounded grid.");
            int2 halo=(int2)required;resolution=region.Resolution+2*halo;
            if(math.any(resolution>4096))throw new ArgumentException("Material repair plus its normal/context halo exceeds 4096 cells; split this region.");
            double2 context=(double2)halo*pitch;
            projection=new SurfaceRegionProjection(p.AnchorDirection,p.Right,p.Forward,p.Radius,p.MinimumMetres-context,p.MaximumMetres+context);blend=math.cmin(context);
        }
    }
}
