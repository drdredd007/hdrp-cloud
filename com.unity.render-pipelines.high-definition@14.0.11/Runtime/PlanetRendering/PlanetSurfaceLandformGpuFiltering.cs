using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Shared derived GPU banks. CPU preparation and globally bounded uploads never wait in a render callback.</summary>
    internal static class PlanetSurfaceLandformGpuFiltering
    {
        internal const long MaximumResidentBytes=128L*1024*1024;
        internal const int MaximumUploadRecordsPerFrame=4096;
        // Four possible packing arrays, including a conservative managed header allowance.
        internal const long MaximumUploadScratchBytes=MaximumUploadRecordsPerFrame*80L+4*32L;
        readonly struct Key : IEquatable<Key>
        {
            internal readonly SurfaceContentHash Source,Policy;
            internal Key(SurfaceContentHash source,SurfaceContentHash policy){Source=source;Policy=policy;}
            public bool Equals(Key other)=>Source==other.Source&&Policy==other.Policy;
            public override bool Equals(object other)=>other is Key key&&Equals(key);
            public override int GetHashCode(){unchecked{return Source.GetHashCode()*397^Policy.GetHashCode();}}
        }
        internal sealed class Entry
        {
            internal readonly PlanetSurfaceLandformFilterLease Source;
            internal PlanetSurfaceLandformFilterGpu Gpu;
            internal PlanetSurfaceLandformFilterStatus Status=PlanetSurfaceLandformFilterStatus.Pending;
            internal string Failure;
            internal int References,Generation;
            internal long Bytes;
            internal bool Disposed;
            internal Entry(PlanetSurfaceLandformFilterLease source){Source=source;}
        }
        static readonly Dictionary<Key,Entry> entries=new Dictionary<Key,Entry>();
        static long residentBytes;
        static int remainingRecords=MaximumUploadRecordsPerFrame,lastRuntimeFrame=-1,generation;
        static bool allocatedThisCycle,installed;
        internal static long ResidentBytes=>residentBytes;
        internal static int ResidentEntries=>entries.Count;
        internal static bool TryAcquire(PlanetSurfaceDescriptor descriptor,out Lease lease,out PlanetSurfaceLandformFilterStatus status)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();lease=null;
            if(!PlanetSurfaceDataRegistry.TryGetLandformField(descriptor,out var source))
            {status=PlanetSurfaceLandformFilterStatus.InvalidData;return false;}
            var key=new Key(source.ContentDigest,SurfaceLandformFilterSettings.Default.PolicyDigest);
            if(entries.TryGetValue(key,out var existing))
            {existing.References++;lease=new Lease(existing);status=existing.Status;return true;}
            if(!PlanetSurfaceLandformFiltering.TryAcquire(source,out var held,out status))return false;
            var entry=new Entry(held){References=1};entries.Add(key,entry);lease=new Lease(entry);
            InstallPump();status=entry.Status;return true;
        }
        static void BeginCycle(){remainingRecords=MaximumUploadRecordsPerFrame;allocatedThisCycle=false;}
        static void OnFrame(ScriptableRenderContext context,Camera[] cameras)
        {
            if(Application.isPlaying&&lastRuntimeFrame!=Time.frameCount){lastRuntimeFrame=Time.frameCount;BeginCycle();}
            PollUploads();
        }
#if UNITY_EDITOR
        static void OnEditorUpdate(){if(!Application.isPlaying){BeginCycle();PollUploads();}}
#endif
        static void InstallPump()
        {
            if(installed)return;installed=true;RenderPipelineManager.beginFrameRendering+=OnFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update+=OnEditorUpdate;
#endif
        }
        internal static void PollUploads()
        {
            PlanetSurfaceDataRegistry.CheckMainThread();PlanetSurfaceLandformFiltering.Poll();
            foreach(var entry in entries.Values)
            {
                if(entry.Disposed||entry.Status!=PlanetSurfaceLandformFilterStatus.Pending)continue;
                if(!entry.Source.IsReady)
                {
                    if(entry.Source.Status!=PlanetSurfaceLandformFilterStatus.Pending)
                    {entry.Status=entry.Source.Status;entry.Failure=entry.Source.Failure;entry.Generation=++generation;}
                    continue;
                }
                try
                {
                    if(entry.Gpu==null)
                    {
                        if(allocatedThisCycle)continue;
                        var view=entry.Source.NativeView;
                        if(view.SourceDigest!=entry.Source.SourceDigest||view.PolicyDigest!=entry.Source.PolicyDigest)
                            throw new InvalidOperationException("Derived GPU banks do not match their held child-content lease.");
                        long bytes=PlanetSurfaceLandformFilterGpu.EstimateBytes(view);
                        long capacity=MaximumResidentBytes-MaximumUploadScratchBytes;
                        if(bytes>capacity)
                        {
                            entry.Status=PlanetSurfaceLandformFilterStatus.BudgetExceeded;
                            entry.Failure=$"Derived GPU allocation {bytes} bytes exceeds module capacity {capacity}.";
                            entry.Generation=++generation;continue;
                        }
                        if(bytes>capacity-residentBytes)
                        {
                            // Other held planets can release their banks later. Keep this request retryable.
                            entry.Failure=$"Derived GPU allocation {bytes} bytes is waiting for module headroom {capacity-residentBytes}.";
                            continue;
                        }
                        entry.Failure=null;
                        entry.Gpu=new PlanetSurfaceLandformFilterGpu(view);entry.Bytes=bytes;residentBytes+=bytes;allocatedThisCycle=true;
                    }
                    if(remainingRecords>0)remainingRecords-=entry.Gpu.Upload(remainingRecords);
                    if(entry.Gpu.IsReady){entry.Status=PlanetSurfaceLandformFilterStatus.Ready;entry.Generation=++generation;}
                }
                catch(Exception error)
                {entry.Gpu?.Dispose();entry.Gpu=null;residentBytes-=entry.Bytes;entry.Bytes=0;entry.Status=PlanetSurfaceLandformFilterStatus.Failed;entry.Failure=error.Message;entry.Generation=++generation;}
            }
        }
        /// <summary>Explicit offline prewarm only. Production rendering uses PollUploads.</summary>
        internal static PlanetSurfaceLandformFilterStatus CompletePreparation(Lease lease)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(lease==null||lease.IsDisposed)throw new ObjectDisposedException(nameof(Lease));
            PlanetSurfaceLandformFiltering.CompletePreparation(lease.Entry.Source);
            while(lease.Status==PlanetSurfaceLandformFilterStatus.Pending)
            {
                BeginCycle();PollUploads();
                // An offline caller cannot free banks held by unrelated live leases. Report pressure
                // to that caller without making the live entry terminal or spinning indefinitely.
                if(lease.Status==PlanetSurfaceLandformFilterStatus.Pending&&lease.Entry.Source.IsReady&&
                    lease.Entry.Gpu==null&&PlanetSurfaceLandformFilterGpu.EstimateBytes(lease.Entry.Source.NativeView)>
                    MaximumResidentBytes-MaximumUploadScratchBytes-residentBytes)
                    return PlanetSurfaceLandformFilterStatus.BudgetExceeded;
            }
            return lease.Status;
        }
        static void Release(Entry entry)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();if(entry.Disposed||--entry.References!=0)return;
            entries.Remove(new Key(entry.Source.SourceDigest,entry.Source.PolicyDigest));DisposeEntry(entry);
        }
        static void DisposeEntry(Entry entry)
        {if(entry.Disposed)return;entry.Disposed=true;entry.Gpu?.Dispose();entry.Gpu=null;residentBytes-=entry.Bytes;entry.Bytes=0;entry.Source.Dispose();}
        internal static void ReleaseAllBeforeCpuReset()=>Reset();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            PlanetSurfaceDataRegistry.CheckMainThread();foreach(var entry in entries.Values)DisposeEntry(entry);entries.Clear();residentBytes=0;generation=0;lastRuntimeFrame=-1;BeginCycle();
            if(!installed)return;installed=false;RenderPipelineManager.beginFrameRendering-=OnFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update-=OnEditorUpdate;
#endif
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallCleanup()=>UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Reset;
#endif
        internal sealed class Lease : IDisposable
        {
            internal Entry Entry{get;private set;}
            internal Lease(Entry entry){Entry=entry;}
            internal bool IsDisposed=>Entry==null||Entry.Disposed;
            internal bool IsReady=>!IsDisposed&&Entry.Status==PlanetSurfaceLandformFilterStatus.Ready;
            internal PlanetSurfaceLandformFilterStatus Status=>IsDisposed?PlanetSurfaceLandformFilterStatus.Cancelled:Entry.Status;
            internal int Generation=>Entry?.Generation??0;
            internal bool Poll(){PollUploads();return IsReady;}
            internal void Bind(CommandBuffer cmd,ComputeShader shader,int kernel,in NativeSurfaceLandformView full)
            {
                if(!IsReady||!Entry.Source.NativeView.Matches(full))throw new InvalidOperationException("Filtered rendering requires a ready matching derived GPU lease.");
                Entry.Gpu.Bind(cmd,shader,kernel);
            }
            public void Dispose(){if(Entry==null)return;var previous=Entry;Entry=null;Release(previous);}
        }
    }
}
