using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;
using Unity.Jobs;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Main-thread asset resolution. Jobs receive only an immutable NativeSurfaceView and a held lease.</summary>
    public static class PlanetSurfaceDataRegistry
    {
        internal sealed class Entry
        {
            internal SurfaceSnapshot Snapshot;
            internal Texture2DArray BaseColour;
            internal NativeSurfaceSnapshot Native;
            internal SurfaceErrorHierarchy Errors;
            internal readonly Dictionary<ErrorKey,SurfaceLodError> ErrorCache=new Dictionary<ErrorKey,SurfaceLodError>();
            internal long ErrorWork;
            internal int ErrorVersion;
            internal Task<PreparedErrors> ErrorTask;
            internal CancellationTokenSource ErrorCancellation;
            internal PlanetSurfaceDataLease WorkerLease;
            internal PlanetSurfaceLandformFilterLease LandformFilter;
            internal int LandformFilterGeneration;
            internal readonly Queue<ErrorKey> ErrorRequests=new Queue<ErrorKey>();
            internal readonly HashSet<ErrorKey> RequestedErrors=new HashSet<ErrorKey>();
            internal readonly Dictionary<ErrorKey,long> LastUsed=new Dictionary<ErrorKey,long>();
            internal long UseClock;
            internal Exception PreparationFailure;
            internal int Owners,Leases;
            internal bool Retired;
            internal void Retire(){Retired=true;ErrorRequests.Clear();ErrorCancellation?.Cancel();if(ErrorTask!=null)retired.Add(this);TryDispose();}
            internal void TryDispose(){if(Retired && Leases==0){Native?.Dispose();Native=null;LandformFilter?.Dispose();LandformFilter=null;Snapshot=null;Errors=null;ErrorCache.Clear();}}
        }
        internal sealed class PreparedErrors
        {
            internal SurfaceErrorHierarchy Hierarchy;
            internal readonly List<KeyValuePair<ErrorKey,SurfaceLodError>> Results=new List<KeyValuePair<ErrorKey,SurfaceLodError>>();
            internal long Work;
        }
        internal readonly struct ErrorKey : IEquatable<ErrorKey>
        {
            internal readonly SurfaceTileKey Patch;
            internal readonly int Resolution;
            internal readonly double Footprint;
            internal ErrorKey(SurfaceTileKey patch,int resolution,double footprint){Patch=patch;Resolution=resolution;Footprint=footprint;}
            public bool Equals(ErrorKey other)=>Patch.Equals(other.Patch)&&Resolution==other.Resolution&&Footprint.Equals(other.Footprint);
            public override bool Equals(object other)=>other is ErrorKey key&&Equals(key);
            public override int GetHashCode(){unchecked{return (Patch.GetHashCode()*397^Resolution)*397^Footprint.GetHashCode();}}
        }
        static readonly Dictionary<PlanetSurfaceDescriptor,Entry> entries=new Dictionary<PlanetSurfaceDescriptor,Entry>();
        static readonly Dictionary<int,PlanetSurfaceDescriptor> owners=new Dictionary<int,PlanetSurfaceDescriptor>();
        static readonly HashSet<Entry> retired=new HashSet<Entry>();
        const int MaximumCachedPatches=4096,MaximumPendingPatches=4096,MaximumPatchWork=1048576,MaximumBatchWork=4194304;
        const int MaximumConcurrentErrorWorkers=2;
        static int activeErrorWorkers;
        static bool pumpInstalled;
        static int mainThread;
        internal static void CheckMainThread()
        {
            int current=Thread.CurrentThread.ManagedThreadId;
            if(mainThread==0)mainThread=current;
            if(current!=mainThread)throw new InvalidOperationException("Planet surface resources must be resolved and released on the Unity main thread.");
        }
        static void EnsurePump()
        {
            if(pumpInstalled)return;pumpInstalled=true;
            RenderPipelineManager.beginFrameRendering+=OnBeginFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update+=PumpLodWork;
#endif
        }
        static void OnBeginFrame(ScriptableRenderContext context,Camera[] cameras)=>PumpLodWork();
        /// <summary>Accept immutable worker results and retire native readers only on the Unity main thread.</summary>
        public static void PumpLodWork()
        {
            CheckMainThread();
            foreach(var entry in entries.Values){CompleteWork(entry,false);ScheduleWork(entry);}
            if(retired.Count==0)return;
            var completed=new List<Entry>();
            foreach(var entry in retired){CompleteWork(entry,false);if(entry.ErrorTask==null)completed.Add(entry);}
            foreach(var entry in completed)retired.Remove(entry);
        }
        static void CompleteWork(Entry entry,bool wait)
        {
            if(entry.ErrorTask==null||(!wait&&!entry.ErrorTask.IsCompleted))return;
            try
            {
                var result=entry.ErrorTask.GetAwaiter().GetResult();
                if(!entry.Retired)
                {
                    entry.Errors=result.Hierarchy;entry.ErrorWork+=result.Work;
                    foreach(var pair in result.Results)
                    {
                        entry.RequestedErrors.Remove(pair.Key);
                        // A terminal finite conservative result may refine even when its measurement budget ran out.
                        // Retrying the identical fixed budget forever cannot produce a different answer.
                        if(pair.Value.Status==SurfaceErrorStatus.MeasurementBudgetExceeded&&!pair.Value.HasConservativeBound)continue;
                        if(entry.ErrorCache.Count>=MaximumCachedPatches)EvictLeastUsed(entry);
                        entry.ErrorCache[pair.Key]=pair.Value;entry.LastUsed[pair.Key]=++entry.UseClock;
                    }
                    entry.ErrorVersion++;
                }
            }
            catch(OperationCanceledException) when(entry.Retired) { }
            catch(Exception error)
            {
                entry.PreparationFailure=error;entry.RequestedErrors.Clear();entry.ErrorRequests.Clear();entry.ErrorVersion++;
                Debug.LogException(error);
            }
            finally
            {
                entry.ErrorTask=null;entry.ErrorCancellation?.Dispose();entry.ErrorCancellation=null;activeErrorWorkers--;var lease=entry.WorkerLease;entry.WorkerLease=null;lease?.Dispose();
            }
        }
        static void EvictLeastUsed(Entry entry)
        {
            ErrorKey oldest=default;long age=long.MaxValue;
            foreach(var pair in entry.LastUsed)if(pair.Value<age){oldest=pair.Key;age=pair.Value;}
            entry.ErrorCache.Remove(oldest);entry.LastUsed.Remove(oldest);
        }
        static void ScheduleWork(Entry entry)
        {
            if(entry.Retired||entry.PreparationFailure!=null||entry.ErrorTask!=null||entry.ErrorRequests.Count==0||activeErrorWorkers>=MaximumConcurrentErrorWorkers)return;
            if(!PrepareLandformFiltering(entry))return;
            var keys=new List<ErrorKey>();
            // One worker batch has a global bound; a per-key bound alone could produce unbounded cold work.
            while(entry.ErrorRequests.Count>0&&keys.Count<MaximumBatchWork/MaximumPatchWork)keys.Add(entry.ErrorRequests.Dequeue());
            entry.Leases++;entry.WorkerLease=new PlanetSurfaceDataLease(entry);
            var view=entry.Native.View;var snapshot=entry.Snapshot;var existing=entry.Errors;
            if(entry.LandformFilter!=null)view=view.WithLandformFilter(entry.LandformFilter.NativeView);
            entry.ErrorCancellation=new CancellationTokenSource();var cancellation=entry.ErrorCancellation.Token;
            activeErrorWorkers++;entry.ErrorTask=Task.Run(()=>
            {
                var result=new PreparedErrors{Hierarchy=existing??SurfaceErrorHierarchy.Build(snapshot,view,SurfaceErrorBuildSettings.Default,()=>cancellation.IsCancellationRequested)};
                foreach(var key in keys)
                {
                    var error=result.Hierarchy.Measure(view,key.Patch,key.Resolution,new SurfaceSamplingFootprint(key.Footprint),MaximumPatchWork,out int work,()=>cancellation.IsCancellationRequested);
                    result.Work+=work;result.Results.Add(new KeyValuePair<ErrorKey,SurfaceLodError>(key,error));
                }
                return result;
            });
        }
        static bool PrepareLandformFiltering(Entry entry)
        {
            bool ready=PrepareLandformFiltering(entry,out var status);
            if(!ready&&status!=PlanetSurfaceLandformFilterStatus.Pending)
            {entry.PreparationFailure=new InvalidOperationException($"Landform LOD filtering failed: {status}: {entry.LandformFilter?.Failure}");entry.ErrorVersion++;}
            return ready;
        }
        static bool PrepareLandformFiltering(Entry entry,out PlanetSurfaceLandformFilterStatus status)
        {
            var field=entry.Snapshot.StructuralField?.LandformField;
            if(field==null){status=PlanetSurfaceLandformFilterStatus.Ready;return true;}
            if(entry.LandformFilter!=null&&entry.LandformFilter.IsDisposed){entry.LandformFilter.Dispose();entry.LandformFilter=null;entry.LandformFilterGeneration=-1;}
            if(entry.LandformFilter==null&&!PlanetSurfaceLandformFiltering.TryAcquire(field,out entry.LandformFilter,out var admission))
            {
                // A valid policy can be temporarily queued behind another child's working
                // reservation. Its unchanged requests retry after that worker retires.
                if(admission==PlanetSurfaceLandformFilterStatus.BudgetExceeded&&
                    SurfaceLandformFilterHierarchy.TryEstimate(field,SurfaceLandformFilterSettings.Default,out _,out _))
                {status=PlanetSurfaceLandformFilterStatus.Pending;return false;}
                status=admission;return false;
            }
            bool ready=entry.LandformFilter.Poll();
            if(entry.LandformFilterGeneration!=entry.LandformFilter.Generation)
            {entry.LandformFilterGeneration=entry.LandformFilter.Generation;entry.ErrorVersion++;}
            status=entry.LandformFilter.Status;
            return ready;
        }
        internal static bool TryGetLandformField(PlanetSurfaceDescriptor descriptor,out SurfaceLandformField field)
        {
            CheckMainThread();field=null;
            if(!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            field=entry.Snapshot.StructuralField?.LandformField;return field!=null;
        }
        // Explicit cleanup only: cache shutdown order cannot dispose derived native
        // banks while a managed LOD worker is still reading an attached view.
        internal static void CompleteReadersBeforeFilterReset()
        {
            CheckMainThread();foreach(var entry in entries.Values)CompleteWork(entry,true);
            foreach(var entry in retired)CompleteWork(entry,true);
            foreach(var entry in entries.Values)
            {
                if(entry.LandformFilter==null)continue;
                entry.LandformFilter.Dispose();entry.LandformFilter=null;entry.LandformFilterGeneration=-1;entry.ErrorVersion++;
            }
        }
        internal static PlanetSurfaceDescriptor Register(PlanetSurfaceDataAsset asset,SurfaceSnapshot snapshot)
        {
            CheckMainThread();var key=PlanetSurfaceDescriptor.FromSnapshot(snapshot);int id=asset.GetInstanceID();
            if(owners.TryGetValue(id,out var old))
            {if(old.Equals(key))return key;Unregister(asset);}
            if(!entries.TryGetValue(key,out var entry)){entry=new Entry {Snapshot=snapshot,BaseColour=asset.BaseColour};entries.Add(key,entry);}
            entry.Owners++;owners.Add(id,key);return key;
        }
        internal static void Unregister(PlanetSurfaceDataAsset asset)
        {
            CheckMainThread();if(ReferenceEquals(asset,null))return;
            int id=asset.GetInstanceID();if(!owners.TryGetValue(id,out var key))return;owners.Remove(id);
            if(entries.TryGetValue(key,out var entry) && --entry.Owners==0){entries.Remove(key);entry.Retire();}
        }
        public static bool TryGetBaseColour(PlanetSurfaceDescriptor descriptor,out Texture2DArray colour)
        {CheckMainThread();colour=null;if(!descriptor.IsBound||!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;colour=entry.BaseColour;return colour;}
        public static bool TryAcquire(PlanetSurfaceDescriptor descriptor,out PlanetSurfaceDataLease lease)
        {
            CheckMainThread();lease=null;
            if(!descriptor.IsBound || !entries.TryGetValue(descriptor,out var entry) || entry.Retired)return false;
            if(entry.Native==null)entry.Native=entry.Snapshot.CreateNative(Allocator.Persistent);
            entry.Leases++;lease=new PlanetSurfaceDataLease(entry);return true;
        }
        /// <summary>Nonblocking render admission. Starts only derived child preparation; no canonical native or patch arrays are allocated while pending.</summary>
        public static bool TryPrepareForRendering(PlanetSurfaceDescriptor descriptor,out PlanetSurfaceLandformFilterStatus status)
        {
            CheckMainThread();status=PlanetSurfaceLandformFilterStatus.InvalidData;
            if(!descriptor.IsBound||!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            return PrepareLandformFiltering(entry,out status);
        }
        /// <summary>Holds both the canonical originals and the matching ready derived attachment for finite render sampling. Physics TryAcquire stays Full.</summary>
        public static bool TryAcquireForRendering(PlanetSurfaceDescriptor descriptor,out PlanetSurfaceRenderLease lease,out PlanetSurfaceLandformFilterStatus status)
        {
            CheckMainThread();lease=null;status=PlanetSurfaceLandformFilterStatus.InvalidData;
            if(!descriptor.IsBound||!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            if(!PrepareLandformFiltering(entry,out status))return false;
            PlanetSurfaceLandformFilterLease filter=null;PlanetSurfaceDataLease canonical=null;
            try
            {
                var field=entry.Snapshot.StructuralField?.LandformField;
                if(field!=null&&!PlanetSurfaceLandformFiltering.TryBorrow(field,out filter,out status))return false;
                if(!TryAcquire(descriptor,out canonical)){status=PlanetSurfaceLandformFilterStatus.InvalidData;return false;}
                var view=canonical.View;
                if(filter!=null)
                {
                    if(!filter.NativeView.Matches(view.StructuralField.LandformField))
                    {status=PlanetSurfaceLandformFilterStatus.InvalidData;return false;}
                    view=view.WithLandformFilter(filter.NativeView);
                }
                lease=new PlanetSurfaceRenderLease(canonical,filter,view);canonical=null;filter=null;
                status=PlanetSurfaceLandformFilterStatus.Ready;return true;
            }
            finally {canonical?.Dispose();filter?.Dispose();}
        }
        public static bool IsRegistered(PlanetSurfaceDescriptor descriptor)
        {CheckMainThread();return descriptor.IsBound && entries.ContainsKey(descriptor);}
        /// <summary>Resolve once per selection. The entry owns support and immutable measurements for its full content digest.</summary>
        public static bool TryAcquireLodContext(PlanetSurfaceDescriptor descriptor,out PlanetSurfaceLodContext context)
        {
            CheckMainThread();EnsurePump();PumpLodWork();context=null;
            if(!descriptor.IsBound||!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            if(entry.Native==null)entry.Native=entry.Snapshot.CreateNative(Allocator.Persistent);
            entry.Leases++;context=new PlanetSurfaceLodContext(entry,new PlanetSurfaceDataLease(entry));return true;
        }
        public static int LodPreparationVersion(PlanetSurfaceDescriptor descriptor)
        {CheckMainThread();PumpLodWork();return entries.TryGetValue(descriptor,out var entry)?entry.ErrorVersion:-1;}
        public static int RetiredLodWorkerCount { get { CheckMainThread();return retired.Count; } }
        /// <summary>Blocking offline/editor prewarming only. Render callbacks use PumpLodWork and never call this.</summary>
        public static void CompleteLodPreparation(PlanetSurfaceDescriptor descriptor)
        {
            CheckMainThread();
            if(!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return;
            while(entry.PreparationFailure==null&&(entry.ErrorTask!=null||entry.ErrorRequests.Count>0))
            {
                if(entry.ErrorRequests.Count>0&&entry.Snapshot.StructuralField?.LandformField!=null)
                {
                    PrepareLandformFiltering(entry);
                    if(entry.PreparationFailure!=null)break;
                    if(entry.LandformFilter==null)throw new InvalidOperationException("Offline LOD preparation requires free landform cache admission; release unrelated retained children before prewarming.");
                    PlanetSurfaceLandformFiltering.CompletePreparation(entry.LandformFilter);
                }
                PumpLodWork();ScheduleWork(entry);
                if(entry.ErrorTask!=null){CompleteWork(entry,true);continue;}
                // Other datasets may own both worker slots. Complete one of those readers on this explicit
                // offline path, then pump its lease/result before retrying the requested dataset.
                Entry running=null;
                foreach(var other in entries.Values)if(other.ErrorTask!=null){running=other;break;}
                if(running==null)foreach(var other in retired)if(other.ErrorTask!=null){running=other;break;}
                if(running==null&&entry.ErrorRequests.Count>0)throw new InvalidOperationException("Queued terrain preparation has no worker capable of progress.");
                if(running!=null)CompleteWork(running,true);
            }
        }
        internal static SurfaceLodError GetError(Entry entry,NativeSurfaceView view,ErrorKey key)
        {
            if(entry.PreparationFailure!=null)return SurfaceErrorHierarchy.Conservative(view,key.Patch,key.Resolution,SurfaceErrorStatus.MissingData);
            if(entry.ErrorCache.TryGetValue(key,out var cached)){entry.LastUsed[key]=++entry.UseClock;return cached;}
            if(!entry.Retired&&entry.RequestedErrors.Count<MaximumPendingPatches&&entry.RequestedErrors.Add(key))entry.ErrorRequests.Enqueue(key);
            return SurfaceErrorHierarchy.Conservative(view,key.Patch,key.Resolution,
                entry.RequestedErrors.Contains(key)?SurfaceErrorStatus.Pending:SurfaceErrorStatus.MeasurementBudgetExceeded);
        }
        internal static void KickLodWork(Entry entry){CheckMainThread();ScheduleWork(entry);}
        public static bool TryGetLodCacheStatistics(PlanetSurfaceDescriptor descriptor,out int supportSamples,out int cachedPatches,out long work)
        {
            CheckMainThread();supportSamples=0;cachedPatches=0;work=0;
            if(!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            supportSamples=entry.Errors?.SupportSampleCount??0;cachedPatches=entry.ErrorCache.Count;work=entry.ErrorWork;return true;
        }
        public static bool TryGetLodPreparationStatistics(PlanetSurfaceDescriptor descriptor,out PlanetLodPreparationStatistics statistics)
        {
            CheckMainThread();statistics=default;
            if(!entries.TryGetValue(descriptor,out var entry)||entry.Retired)return false;
            var hierarchy=entry.Errors;
            statistics=new PlanetLodPreparationStatistics(hierarchy?.UsesPagedSupport??false,hierarchy?.PageMetadataBudgetExceeded??false,
                hierarchy?.RepresentedSupportSamples??0,hierarchy?.PreparationNodes??0,hierarchy?.EstimatedBytes??0,
                entry.ErrorCache.Count,entry.RequestedErrors.Count,entry.ErrorWork);
            return true;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            CheckMainThread();foreach(var entry in entries.Values)entry.Retire();entries.Clear();owners.Clear();
            foreach(var entry in retired)CompleteWork(entry,true);retired.Clear();
            if(pumpInstalled)
            {
                RenderPipelineManager.beginFrameRendering-=OnBeginFrame;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update-=PumpLodWork;
#endif
                pumpInstalled=false;
            }
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallEditorCleanup(){CheckMainThread();UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Reset;}
#endif
    }
    /// <summary>Value-only metadata; source support residency is distinct from materialized full-height probes.</summary>
    public readonly struct PlanetLodPreparationStatistics
    {
        public readonly bool UsesPagedSupport,MetadataBudgetExceeded;
        public readonly long RepresentedSupportSamples,PreparationSourceNodes,MetadataBytes,MeasurementWork;
        public readonly int CachedPatches,PendingPatches;
        internal PlanetLodPreparationStatistics(bool paged,bool exceeded,long support,long nodes,long bytes,int cached,int pending,long work)
        { UsesPagedSupport=paged;MetadataBudgetExceeded=exceeded;RepresentedSupportSamples=support;PreparationSourceNodes=nodes;
            MetadataBytes=bytes;CachedPatches=cached;PendingPatches=pending;MeasurementWork=work; }
    }
    /// <summary>One native lease for a whole LOD selection; candidates perform no asset lookup or registry lease allocation.</summary>
    public sealed class PlanetSurfaceLodContext : IDisposable
    {
        PlanetSurfaceDataRegistry.Entry entry;
        PlanetSurfaceDataLease lease;
        internal PlanetSurfaceLodContext(PlanetSurfaceDataRegistry.Entry entry,PlanetSurfaceDataLease lease){this.entry=entry;this.lease=lease;}
        public SurfaceLodError Error(SurfaceTileKey key,int resolution,SurfaceSamplingFootprint footprint)
        {
            if(entry==null)throw new ObjectDisposedException(nameof(PlanetSurfaceLodContext));
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(!key.IsValid||!CubeSurface.ValidResolution(resolution)||!footprint.IsValid)throw new ArgumentException("Invalid LOD error request.");
            // All components, including raw-source regional filtering bounds, are prepared by
            // the retained worker and cached together. Selection only reads that completed value.
            return PlanetSurfaceDataRegistry.GetError(entry,lease.View,new PlanetSurfaceDataRegistry.ErrorKey(key,resolution,footprint.Metres));
        }
        public bool TryGetRenderedHeightRange(SurfaceTileKey key,int resolution,SurfaceSamplingFootprint footprint,out double minimum,out double maximum)
        {
            var error=Error(key,resolution,footprint);
            minimum=error.MinimumRenderedHeightMetres;maximum=error.MaximumRenderedHeightMetres;
            return error.HasRenderedHeightRange&&error.HasConservativeBound;
        }
        public void Dispose(){if(entry==null)return;PlanetSurfaceDataRegistry.KickLodWork(entry);entry=null;lease.Dispose();lease=null;}
    }
    public sealed class PlanetSurfaceDataLease : IDisposable
    {
        PlanetSurfaceDataRegistry.Entry entry;
        JobHandle dependency;
        internal PlanetSurfaceDataLease(PlanetSurfaceDataRegistry.Entry entry){this.entry=entry;}
        public NativeSurfaceView View => entry!=null?entry.Native.View:throw new ObjectDisposedException(nameof(PlanetSurfaceDataLease));
        public void AddDependency(JobHandle job){if(entry==null)throw new ObjectDisposedException(nameof(PlanetSurfaceDataLease));dependency=JobHandle.CombineDependencies(dependency,job);}
        public void Dispose()
        {
            if(entry==null)return;PlanetSurfaceDataRegistry.CheckMainThread();dependency.Complete();
            var released=entry;entry=null;released.Leases--;released.TryDispose();
        }
    }
    /// <summary>Render-only closure. Jobs must register their reader handle; explicit filter reset waits those readers and invalidates further View access.</summary>
    public sealed class PlanetSurfaceRenderLease : IDisposable
    {
        PlanetSurfaceDataLease canonical;
        PlanetSurfaceLandformFilterLease filter;
        readonly NativeSurfaceView view;
        readonly int generation;
        internal PlanetSurfaceRenderLease(PlanetSurfaceDataLease canonical,PlanetSurfaceLandformFilterLease filter,NativeSurfaceView view)
        {this.canonical=canonical;this.filter=filter;this.view=view;generation=filter?.Generation??0;}
        public NativeSurfaceView View
        {
            get
            {
                PlanetSurfaceDataRegistry.CheckMainThread();
                if(canonical==null)throw new ObjectDisposedException(nameof(PlanetSurfaceRenderLease));
                if(filter!=null&&(!filter.IsReady||filter.Generation!=generation))
                    throw new InvalidOperationException("The derived render attachment was reset or released; acquire a fresh render lease.");
                return view;
            }
        }
        public void AddDependency(JobHandle reader)
        {
            // Validate before recording a handle so stale attached views cannot start new readers.
            _=View;canonical.AddDependency(reader);filter?.AddDependency(reader);
        }
        public void Dispose()
        {
            if(canonical==null)return;PlanetSurfaceDataRegistry.CheckMainThread();
            canonical.Dispose();canonical=null;filter?.Dispose();filter=null;
        }
    }
}
