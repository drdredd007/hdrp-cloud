using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Owns immutable cloned float32 vertex samples; data are linear signed metres.</summary>
    public sealed class SurfaceTileData
    {
        readonly float[] heights;
        readonly float4[] materialWeights, erosionData;
        public SurfaceTileKey Key { get; }
        public int Resolution { get; }
        public int SampleCount => heights.Length;
        public double MinimumHeight { get; }
        public double MaximumHeight { get; }
        public SurfaceContentHash ContentHash { get; }
        public SurfaceChannels Channels { get; }
        public bool HasMaterialWeights => (Channels & SurfaceChannels.MaterialWeights) != 0;
        public bool HasErosionData => (Channels & SurfaceChannels.ErosionData) != 0;
        public double MeasuredLodErrorMetres { get; }
        public SurfaceMaterialProvenance MaterialProvenance { get; }
        public SurfaceTileData(SurfaceTileKey key, int resolution, float[] heights, float4[] materialWeights = null,
            float4[] erosionData = null, double measuredLodErrorMetres = 0, SurfaceMaterialProvenance materialProvenance = SurfaceMaterialProvenance.LegacyBaked)
        {
            if (!key.IsValid || !CubeSurface.ValidResolution(resolution)) throw new ArgumentException("Invalid cube tile or vertex-grid resolution.");
            if (heights == null || heights.Length != (resolution + 1) * (resolution + 1)) throw new ArgumentException("A tile requires (resolution+1)^2 vertex samples.", nameof(heights));
            Key = key; Resolution = resolution; this.heights = (float[])heights.Clone();
            SurfaceDataValidation.Range(this.heights, false, out var min, out var max);
            MinimumHeight = min; MaximumHeight = max;
            this.materialWeights = SurfaceDataValidation.Attributes(materialWeights, SampleCount, true);
            this.erosionData = SurfaceDataValidation.Attributes(erosionData, SampleCount, false);
            if (!math.isfinite(measuredLodErrorMetres) || measuredLodErrorMetres < 0) throw new ArgumentException("LOD sample error must be finite and nonnegative.");
            MeasuredLodErrorMetres = measuredLodErrorMetres;
            SurfaceDataValidation.Provenance(materialProvenance); MaterialProvenance = materialProvenance;
            Channels = (materialWeights == null ? SurfaceChannels.None : SurfaceChannels.MaterialWeights) |
                (erosionData == null ? SurfaceChannels.None : SurfaceChannels.ErosionData);
            ContentHash = SurfaceHashing.Compute(writer =>
            {
                bool legacy = Channels == SurfaceChannels.None && MeasuredLodErrorMetres == 0;
                writer.Write(materialProvenance == SurfaceMaterialProvenance.LegacyBaked ? (legacy ? 1 : 2) : 4); SurfaceHashing.WriteKey(writer, Key); writer.Write(Resolution);
                foreach (float value in this.heights) writer.Write(value);
                if (!legacy)
                {
                    writer.Write((int)Channels); writer.Write(MeasuredLodErrorMetres);
                    SurfaceHashing.WriteAttributes(writer, this.materialWeights); SurfaceHashing.WriteAttributes(writer, this.erosionData);
                }
                if (materialProvenance != SurfaceMaterialProvenance.LegacyBaked) writer.Write((int)materialProvenance);
            });
        }
        public float HeightAt(int index) => heights[index];
        public float[] CopyHeights() => (float[])heights.Clone();
        public float4 MaterialWeightsAt(int index) => materialWeights == null ? throw new InvalidOperationException("Material weights are absent.") : materialWeights[index];
        public float4 ErosionDataAt(int index) => erosionData == null ? throw new InvalidOperationException("Erosion data are absent.") : erosionData[index];
        public float4[] CopyMaterialWeights() => materialWeights == null ? null : (float4[])materialWeights.Clone();
        public float4[] CopyErosionData() => erosionData == null ? null : (float4[])erosionData.Clone();
    }

    /// <summary>Persistent authored layer; never mutates or replaces the generated source asset.</summary>
    public sealed class SurfaceRegionData
    {
        readonly float[] heights, mask;
        readonly float4[] materialWeights, erosionData;
        public SurfaceRegionProjection Projection { get; }
        public int2 Resolution { get; }
        public int SampleCount => heights.Length;
        public double BlendMetres { get; }
        public int Priority { get; }
        public SurfaceRegionMode Mode { get; }
        public SurfaceDetailPolicy DetailPolicy { get; }
        public SurfaceContentHash BaseDigest { get; }
        public SurfaceContentHash ContentHash { get; }
        public double MinimumHeight { get; }
        public double MaximumHeight { get; }
        public bool HasMaterialWeights => materialWeights != null;
        public bool HasErosionData => erosionData != null;
        public SurfaceRegionKind Kind { get; }
        public SurfaceContentHash SourceContentDigest { get; }
        public double ContextMetres { get; }
        public SurfaceMaterialProvenance MaterialProvenance { get; }
        public SurfaceAutomaticMaterialProfile AutomaticMaterialProfile { get; }
        public SurfaceRegionData(SurfaceRegionProjection projection, int2 resolution, float[] heights, float[] blendMask = null,
            double blendMetres = 0, SurfaceRegionMode mode = SurfaceRegionMode.Replace, SurfaceContentHash baseDigest = default,
            SurfaceDetailPolicy detailPolicy = SurfaceDetailPolicy.Suppress, int priority = 0, float4[] materialWeights = null,
            SurfaceRegionKind kind = SurfaceRegionKind.Authored, SurfaceContentHash sourceContentDigest = default, double contextMetres = 0, float4[] erosionData = null,
            SurfaceMaterialProvenance materialProvenance = SurfaceMaterialProvenance.LegacyBaked, SurfaceAutomaticMaterialProfile automaticMaterialProfile = default)
        {
            if (!projection.IsValid || math.any(resolution < 1) || math.any(resolution > 4096) || !math.isfinite(blendMetres) || blendMetres < 0 ||
                (mode != SurfaceRegionMode.Replace && mode != SurfaceRegionMode.Delta) || (mode == SurfaceRegionMode.Delta && !baseDigest.IsValid) ||
                (detailPolicy != SurfaceDetailPolicy.Preserve && detailPolicy != SurfaceDetailPolicy.Suppress))
                throw new ArgumentException("Invalid gnomonic region, mode or provenance.");
            if ((kind != SurfaceRegionKind.Authored && kind != SurfaceRegionKind.GeneratedRefinement) || !math.isfinite(contextMetres) || contextMetres < 0 ||
                (kind == SurfaceRegionKind.GeneratedRefinement && (!baseDigest.IsValid || !sourceContentDigest.IsValid || contextMetres <= 0)))
                throw new ArgumentException("Generated refinement requires exact source provenance and positive metric context.");
            int count = (resolution.x + 1) * (resolution.y + 1);
            if (heights == null || heights.Length != count || (blendMask != null && blendMask.Length != count))
                throw new ArgumentException("Region height and blend mask use the same vertex grid.");
            Projection = projection; Resolution = resolution; BlendMetres = blendMetres; Mode = mode;
            BaseDigest = baseDigest; DetailPolicy = detailPolicy; Priority = priority;
            Kind = kind; SourceContentDigest = sourceContentDigest; ContextMetres = contextMetres;
            SurfaceDataValidation.Provenance(materialProvenance); MaterialProvenance = materialProvenance;
            if (automaticMaterialProfile.Version != 0 && !automaticMaterialProfile.IsValid) throw new ArgumentException("Invalid automatic material profile.");
            AutomaticMaterialProfile = automaticMaterialProfile;
            this.heights = (float[])heights.Clone();
            mask = blendMask == null ? new float[count] : (float[])blendMask.Clone();
            if (blendMask == null) for (int i = 0; i < count; i++) mask[i] = 1;
            SurfaceDataValidation.Range(this.heights, false, out var min, out var max);
            SurfaceDataValidation.Range(mask, true, out _, out _);
            MinimumHeight = min; MaximumHeight = max;
            this.materialWeights = SurfaceDataValidation.Attributes(materialWeights, SampleCount, true);
            this.erosionData = SurfaceDataValidation.Attributes(erosionData, SampleCount, false);
            ContentHash = SurfaceHashing.Compute(writer =>
            {
                bool oldMetadata = Kind == SurfaceRegionKind.Authored && !SourceContentDigest.IsValid && ContextMetres == 0 && !HasErosionData;
                bool newMetadata = MaterialProvenance != SurfaceMaterialProvenance.LegacyBaked || AutomaticMaterialProfile.IsValid;
                writer.Write(newMetadata ? 4 : oldMetadata ? (this.materialWeights == null ? 1 : 2) : 3); SurfaceHashing.WriteProjection(writer, Projection); writer.Write(Resolution.x); writer.Write(Resolution.y);
                writer.Write(BlendMetres); writer.Write((int)Mode); SurfaceHashing.WriteHash(writer, BaseDigest);
                writer.Write((int)DetailPolicy); writer.Write(Priority);
                foreach (float value in this.heights) writer.Write(value);
                foreach (float value in mask) writer.Write(value);
                if (this.materialWeights != null) SurfaceHashing.WriteAttributes(writer, this.materialWeights);
                if (!oldMetadata)
                {
                    writer.Write(HasMaterialWeights); writer.Write((int)Kind); SurfaceHashing.WriteHash(writer, SourceContentDigest); writer.Write(ContextMetres);
                    writer.Write(HasErosionData); SurfaceHashing.WriteAttributes(writer, this.erosionData);
                }
                if (newMetadata)
                {
                    writer.Write((int)MaterialProvenance); writer.Write(AutomaticMaterialProfile.IsValid);
                    if (AutomaticMaterialProfile.IsValid) SurfaceAutomaticMaterialProfile.Write(writer, AutomaticMaterialProfile);
                }
            });
        }
        public float HeightAt(int index) => heights[index];
        public float MaskAt(int index) => mask[index];
        public float[] CopyHeights() => (float[])heights.Clone();
        public float[] CopyBlendMask() => (float[])mask.Clone();
        public float4 MaterialWeightsAt(int index) => materialWeights == null ? throw new InvalidOperationException("Material weights are absent.") : materialWeights[index];
        public float4[] CopyMaterialWeights() => materialWeights == null ? null : (float4[])materialWeights.Clone();
        public float4 ErosionDataAt(int index) => erosionData == null ? throw new InvalidOperationException("Erosion data are absent.") : erosionData[index];
        public float4[] CopyErosionData() => erosionData == null ? null : (float4[])erosionData.Clone();
    }

    internal static class SurfaceDataValidation
    {
        public static void Provenance(SurfaceMaterialProvenance provenance)
        { if ((uint)provenance > (uint)SurfaceMaterialProvenance.Authored) throw new ArgumentException("Unknown material provenance."); }
        public static float4[] Attributes(float4[] values, int expected, bool weights)
        {
            if (values == null) return null;
            if (values.Length != expected) throw new ArgumentException("Attribute channels must use the height vertex grid.");
            var clone = (float4[])values.Clone();
            for (int i = 0; i < clone.Length; i++)
            {
                var value = clone[i];
                if (!math.all(math.isfinite(value)) || math.any(value < 0) || math.any(value > 1) ||
                    (weights && math.abs(math.csum(value) - 1) > 1e-5f))
                    throw new ArgumentException("Attributes must be finite [0,1]; material weights sum to one.");
                clone[i] = math.select(value, 0, value == 0);
            }
            return clone;
        }
        public static void Range(float[] values, bool mask, out double minimum, out double maximum)
        {
            minimum = double.PositiveInfinity; maximum = double.NegativeInfinity;
            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];
                if (!math.isfinite(value) || (mask && (value < 0 || value > 1))) throw new ArgumentException("Samples must be finite; blend masks lie in [0,1].");
                if (value == 0) values[i] = 0; // Canonicalise signed zero for content identity.
                minimum = math.min(minimum, value); maximum = math.max(maximum, value);
            }
        }
    }

    /// <summary>Canonical little-endian binary identity. Hashing runs during authoring/preparation, never inside jobs.</summary>
    public static class SurfaceHashing
    {
        public static SurfaceContentHash Recipe(SurfaceRecipe recipe)
        {
            if (!recipe.IsValid) throw new ArgumentException("Invalid surface recipe.", nameof(recipe));
            return Compute(writer => WriteRecipe(writer, recipe));
        }
        internal static SurfaceContentHash Compute(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            { write(writer); writer.Flush(); return SurfaceContentHash.Compute(stream.ToArray()); }
        }
        internal static void WriteRecipe(BinaryWriter writer, SurfaceRecipe recipe)
        {
            writer.Write(recipe.SchemaVersion); writer.Write(recipe.AlgorithmVersion); writer.Write((int)recipe.Style); writer.Write(recipe.Seed);
            writer.Write(recipe.Radius); writer.Write(recipe.SeaLevel == 0 ? 0 : recipe.SeaLevel);
            writer.Write(recipe.MinimumHeight == 0 ? 0 : recipe.MinimumHeight); writer.Write(recipe.MaximumHeight == 0 ? 0 : recipe.MaximumHeight);
        }
        internal static void WriteKey(BinaryWriter writer, SurfaceTileKey key)
        { writer.Write(key.Face); writer.Write(key.Level); writer.Write(key.X); writer.Write(key.Y); }
        internal static void WriteAttributes(BinaryWriter writer, float4[] values)
        { if (values != null) foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); } }
        internal static void WriteHash(BinaryWriter writer, SurfaceContentHash hash)
        { writer.Write(hash.A); writer.Write(hash.B); writer.Write(hash.C); writer.Write(hash.D); }
        internal static void WriteVector(BinaryWriter writer, double3 value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        internal static void WriteProjection(BinaryWriter writer, SurfaceRegionProjection projection)
        {
            WriteVector(writer, projection.AnchorDirection); WriteVector(writer, projection.Right); WriteVector(writer, projection.Forward);
            writer.Write(projection.Radius); writer.Write(projection.MinimumMetres.x); writer.Write(projection.MinimumMetres.y);
            writer.Write(projection.MaximumMetres.x); writer.Write(projection.MaximumMetres.y);
        }
    }
}
