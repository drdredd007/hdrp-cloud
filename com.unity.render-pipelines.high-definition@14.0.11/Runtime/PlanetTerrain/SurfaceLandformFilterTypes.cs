using System;
using Unity.Collections;
using Unity.Jobs;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Derived render approximation policy. Admission limits never alter an admitted surface.</summary>
    public readonly struct SurfaceLandformFilterSettings
    {
        public const int AlgorithmVersion = 1;
        public readonly int QuadratureResolution;
        public readonly long MaximumSupportSamples, MaximumResidentBytes, MaximumWorkingBytes;
        public SurfaceLandformFilterSettings(int quadratureResolution, long maximumSupportSamples, long maximumResidentBytes, long maximumWorkingBytes)
        { QuadratureResolution=quadratureResolution;MaximumSupportSamples=maximumSupportSamples;MaximumResidentBytes=maximumResidentBytes;MaximumWorkingBytes=maximumWorkingBytes; }
        public static SurfaceLandformFilterSettings Default => new SurfaceLandformFilterSettings(4,4L*1024*1024,96L*1024*1024,256L*1024*1024);
        public bool IsValid => QuadratureResolution>=2&&QuadratureResolution<=16&&MaximumSupportSamples>0&&MaximumSupportSamples<=64L*1024*1024&&
            MaximumResidentBytes>0&&MaximumResidentBytes<=256L*1024*1024&&MaximumWorkingBytes>0&&MaximumWorkingBytes<=1024L*1024*1024;
        public SurfaceContentHash PolicyDigest { get { int quadrature=QuadratureResolution;return SurfaceHashing.Compute(w=>{w.Write(AlgorithmVersion);w.Write(quadrature);w.Write(4.0);w.Write(1);}); } }
    }
    public readonly struct SurfaceLandformFilterEstimate
    {
        public readonly int MaximumLevel,DerivedControlCapacity;
        public readonly long SupportSamples,SourceNativeBytes,DerivedManagedReserveBytes,DerivedNativeReserveBytes,BuildScratchBytes,PeakWorkingBytes;
        internal SurfaceLandformFilterEstimate(int level,int controls,long samples,long sourceNative,long managed,long native,long scratch)
        {MaximumLevel=level;DerivedControlCapacity=controls;SupportSamples=samples;SourceNativeBytes=sourceNative;DerivedManagedReserveBytes=managed;DerivedNativeReserveBytes=native;BuildScratchBytes=scratch;PeakWorkingBytes=checked(sourceNative+managed+native+scratch);}
    }

    /// <summary>32-byte shader header. Node children and leaf references are absolute offsets into shared banks.</summary>
    public readonly struct SurfaceLandformFilterLevel
    {
        public readonly int Level,Root,FirstIndex,IndexCount,FirstReference,ReferenceCount;
        public readonly double MaximumCellEdgeMetres;
        public SurfaceLandformFilterLevel(int level,int root,int firstIndex,int indexCount,int firstReference,int referenceCount,double edge)
        {Level=level;Root=root;FirstIndex=firstIndex;IndexCount=indexCount;FirstReference=firstReference;ReferenceCount=referenceCount;MaximumCellEdgeMetres=edge;}
    }

    /// <summary>Borrowed renderer-only low-pass support. Original controls are negative -(id+1); derived controls are nonnegative.</summary>
    public readonly struct NativeSurfaceLandformFilterView
    {
        public readonly SurfaceContentHash SourceDigest,PolicyDigest;
        public readonly int PolicyVersion,MaximumLevel;
        public readonly double Radius,MinimumHeight,MaximumHeight;
        public readonly NativeArray<SurfaceLandformControl>.ReadOnly DerivedControls;
        public readonly NativeArray<SurfaceLandformFilterLevel>.ReadOnly Levels;
        public readonly NativeArray<SurfaceDrainageIndexNode>.ReadOnly Index;
        public readonly NativeArray<int>.ReadOnly References;
        internal NativeSurfaceLandformFilterView(SurfaceLandformFilterHierarchy source,NativeArray<SurfaceLandformControl> controls,
            NativeArray<SurfaceLandformFilterLevel> levels,NativeArray<SurfaceDrainageIndexNode> index,NativeArray<int> references)
        {SourceDigest=source?.SourceDigest??default;PolicyDigest=source?.PolicyDigest??default;PolicyVersion=source==null?0:SurfaceLandformFilterSettings.AlgorithmVersion;
            MaximumLevel=source?.MaximumLevel??0;Radius=source?.Radius??0;MinimumHeight=source?.MinimumHeight??0;MaximumHeight=source?.MaximumHeight??0;
            DerivedControls=controls.AsReadOnly();Levels=levels.AsReadOnly();Index=index.AsReadOnly();References=references.AsReadOnly();}
        public bool Matches(in NativeSurfaceLandformView full) => PolicyVersion==SurfaceLandformFilterSettings.AlgorithmVersion&&SourceDigest==full.ContentDigest&&
            SourceDigest.IsValid&&PolicyDigest.IsValid&&Radius==full.Radius&&DerivedControls.IsCreated&&Levels.IsCreated&&Index.IsCreated&&References.IsCreated&&Levels.Length==MaximumLevel;
    }

    /// <summary>Owns only derived banks. Caller separately retains the canonical lease used by negative control references.</summary>
    public sealed class NativeSurfaceLandformFilterData : IDisposable
    {
        readonly SurfaceLandformFilterHierarchy hierarchy;bool disposed;
        NativeArray<SurfaceLandformControl> controls;
        NativeArray<SurfaceLandformFilterLevel> levels;
        NativeArray<SurfaceDrainageIndexNode> index;
        NativeArray<int> references;
        public NativeSurfaceLandformFilterView View=>!disposed?new NativeSurfaceLandformFilterView(hierarchy,controls,levels,index,references):throw new ObjectDisposedException(nameof(NativeSurfaceLandformFilterData));
        public long EstimatedNativeBytes=>hierarchy?.EstimatedNativeBytes??0;
        public NativeSurfaceLandformFilterData(SurfaceLandformFilterHierarchy hierarchy,Allocator allocator)
        {
            if(allocator==Allocator.Invalid||allocator==Allocator.None)throw new ArgumentException("Derived support requires an owning allocator.");this.hierarchy=hierarchy;
            try
            {controls=new NativeArray<SurfaceLandformControl>(hierarchy?.DerivedControls.Count??0,allocator);levels=new NativeArray<SurfaceLandformFilterLevel>(hierarchy?.Levels.Count??0,allocator);
                index=new NativeArray<SurfaceDrainageIndexNode>(hierarchy?.IndexCount??0,allocator);references=new NativeArray<int>(hierarchy?.ReferenceCount??0,allocator);
                if(hierarchy==null)return;
                for(int i=0;i<controls.Length;i++)controls[i]=hierarchy.DerivedControls[i];for(int i=0;i<levels.Length;i++)levels[i]=hierarchy.Levels[i];
                for(int i=0;i<index.Length;i++)index[i]=hierarchy.IndexAt(i);for(int i=0;i<references.Length;i++)references[i]=hierarchy.ReferenceAt(i);}
            catch{Dispose();throw;}
        }
        public void Dispose(){if(disposed)return;if(controls.IsCreated)controls.Dispose();if(levels.IsCreated)levels.Dispose();if(index.IsCreated)index.Dispose();if(references.IsCreated)references.Dispose();disposed=true;}
        public JobHandle Dispose(JobHandle readers)
        {if(disposed)return readers;var result=readers;if(controls.IsCreated)result=JobHandle.CombineDependencies(result,controls.Dispose(readers));if(levels.IsCreated)result=JobHandle.CombineDependencies(result,levels.Dispose(readers));
            if(index.IsCreated)result=JobHandle.CombineDependencies(result,index.Dispose(readers));if(references.IsCreated)result=JobHandle.CombineDependencies(result,references.Dispose(readers));disposed=true;return result;}
    }
}
