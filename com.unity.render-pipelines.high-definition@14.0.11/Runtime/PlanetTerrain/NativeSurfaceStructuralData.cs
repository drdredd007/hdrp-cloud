using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Borrowed blittable view of explicitly captured whole-sphere structure. No seed reconstruction or managed lookups.</summary>
    public readonly struct NativeSurfaceStructuralView
    {
        public readonly SurfaceRecipe SourceRecipe;
        public readonly SurfaceContentHash SourceBaseDigest, ContentDigest;
        public readonly int RawMacroResolution;
        public readonly int MorphologyVersion;
        public readonly NativeSurfaceDrainageView DrainageField;
        public readonly NativeSurfaceLandformView LandformField;
        public readonly NativeSurfaceLandformFilterView LandformFilter;
        public readonly double ShelfWidthMetres, BeltWidthMetres, FeatureScaleMetres, CoastThresholdMetres, MountainFraction;
        public readonly double MinimumHeight, MaximumHeight;
        public readonly NativeArray<SurfaceGeologicalProvince>.ReadOnly Provinces;
        public readonly NativeArray<SurfaceGeologicalBoundary>.ReadOnly Boundaries;
        public readonly NativeArray<float>.ReadOnly RawMacro;
        public readonly NativeArray<int2>.ReadOnly Bins;
        public readonly NativeArray<int>.ReadOnly References;
        public bool Enabled => ContentDigest.IsValid;
        internal NativeSurfaceStructuralView(SurfaceStructuralField field, NativeArray<SurfaceGeologicalProvince> provinces,
            NativeArray<SurfaceGeologicalBoundary> boundaries, NativeArray<float> raw, NativeArray<int2> bins, NativeArray<int> references,NativeSurfaceDrainageView drainageField,NativeSurfaceLandformView landformField,NativeSurfaceLandformFilterView landformFilter)
        {
            SourceRecipe = field == null ? default : field.SourceRecipe;
            SourceBaseDigest = field == null ? default : field.SourceBaseDigest; ContentDigest = field == null ? default : field.ContentDigest;
            RawMacroResolution = field == null ? 0 : field.RawMacroResolution;
            MorphologyVersion=field?.MorphologyVersion??0;DrainageField=drainageField;
            LandformField=landformField;LandformFilter=landformFilter;
            ShelfWidthMetres = field == null ? 0 : field.ShelfWidthMetres; BeltWidthMetres = field == null ? 0 : field.BeltWidthMetres;
            FeatureScaleMetres = field == null ? 0 : field.RegionalFeatureScaleMetres; CoastThresholdMetres = field == null ? 0 : field.CoastThresholdMetres;
            MountainFraction = field == null ? 0 : field.MountainFraction; MinimumHeight = field == null ? 0 : field.MinimumHeight; MaximumHeight = field == null ? 0 : field.MaximumHeight;
            Provinces = provinces.AsReadOnly(); Boundaries = boundaries.AsReadOnly(); RawMacro = raw.AsReadOnly(); Bins = bins.AsReadOnly(); References = references.AsReadOnly();
        }
        NativeSurfaceStructuralView(in NativeSurfaceStructuralView source,in NativeSurfaceLandformFilterView filter)
        {this=source;LandformFilter=filter;}
        /// <summary>Borrowed renderer-only attachment. Both canonical and derived leases must outlive readers; no arrays are copied or built.</summary>
        public NativeSurfaceStructuralView WithLandformFilter(in NativeSurfaceLandformFilterView filter)=>new NativeSurfaceStructuralView(this,filter);
    }

    /// <summary>Native immutable structural data. The parent snapshot lease owns its job lifetime.</summary>
    public sealed class NativeSurfaceStructuralData : IDisposable
    {
        NativeArray<SurfaceGeologicalProvince> provinces;
        NativeArray<SurfaceGeologicalBoundary> boundaries;
        NativeArray<float> raw;
        NativeArray<int2> bins;
        NativeArray<int> references;
        readonly SurfaceStructuralField field;
        NativeSurfaceDrainageData drainage;
        NativeSurfaceLandformData landform;
        NativeSurfaceLandformFilterData emptyLandformFilter;
        bool disposed;
        public NativeSurfaceStructuralView View => !disposed ? new NativeSurfaceStructuralView(field, provinces, boundaries, raw, bins, references,drainage.View,landform.View,emptyLandformFilter.View) : throw new ObjectDisposedException(nameof(NativeSurfaceStructuralData));
        public NativeSurfaceStructuralData(SurfaceStructuralField field, Allocator allocator)
        {
            if (allocator == Allocator.Invalid || allocator == Allocator.None) throw new ArgumentException("Structural copies require an owning allocator.");
            this.field = field;
            try
            {
                drainage=new NativeSurfaceDrainageData(field?.DrainageField,allocator);
                landform=new NativeSurfaceLandformData(field?.LandformField,allocator);
                // Even disabled optional views cross the job safety validator. Their empty arrays
                // must have an owner; no derived hierarchy or quadrature is built here.
                emptyLandformFilter=new NativeSurfaceLandformFilterData(null,allocator);
                provinces = new NativeArray<SurfaceGeologicalProvince>(field?.Provinces.Count ?? 0, allocator);
                boundaries = new NativeArray<SurfaceGeologicalBoundary>(field?.Boundaries.Count ?? 0, allocator);
                raw = new NativeArray<float>(field == null ? 0 : 6 * (field.RawMacroResolution + 1) * (field.RawMacroResolution + 1), allocator);
                bins = new NativeArray<int2>(field == null ? 0 : 6 * SurfaceStructuralField.SpatialResolution * SurfaceStructuralField.SpatialResolution, allocator);
                references = new NativeArray<int>(field?.SpatialReferenceCount ?? 0, allocator);
                if (field == null) return;
                for (int i = 0; i < provinces.Length; i++) provinces[i] = field.Provinces[i];
                for (int i = 0; i < boundaries.Length; i++) boundaries[i] = field.Boundaries[i];
                for (int i = 0; i < raw.Length; i++) raw[i] = field.RawMacroAt(i);
                for (int i = 0; i < bins.Length; i++) bins[i] = field.SpatialBinAt(i);
                for (int i = 0; i < references.Length; i++) references[i] = field.SpatialReferenceAt(i);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            if (provinces.IsCreated) provinces.Dispose(); if (boundaries.IsCreated) boundaries.Dispose(); if (raw.IsCreated) raw.Dispose();
            if (bins.IsCreated) bins.Dispose(); if (references.IsCreated) references.Dispose(); disposed = true;
            drainage?.Dispose();
            landform?.Dispose();
            emptyLandformFilter?.Dispose();
        }
        public JobHandle Dispose(JobHandle readers)
        {
            if (disposed) return readers;
            var result = readers;
            if (provinces.IsCreated) result = JobHandle.CombineDependencies(result, provinces.Dispose(readers));
            if (boundaries.IsCreated) result = JobHandle.CombineDependencies(result, boundaries.Dispose(readers));
            if (raw.IsCreated) result = JobHandle.CombineDependencies(result, raw.Dispose(readers));
            if (bins.IsCreated) result = JobHandle.CombineDependencies(result, bins.Dispose(readers));
            if (references.IsCreated) result = JobHandle.CombineDependencies(result, references.Dispose(readers));
            if(drainage!=null)result=JobHandle.CombineDependencies(result,drainage.Dispose(readers));
            if(landform!=null)result=JobHandle.CombineDependencies(result,landform.Dispose(readers));
            if(emptyLandformFilter!=null)result=JobHandle.CombineDependencies(result,emptyLandformFilter.Dispose(readers));
            disposed = true; return result;
        }
    }
}
