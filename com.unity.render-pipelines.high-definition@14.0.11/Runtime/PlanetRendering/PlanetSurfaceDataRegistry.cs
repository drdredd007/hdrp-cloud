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
            internal NativeSurfaceSnapshot Native;
            internal SurfaceErrorHierarchy Errors;
            internal readonly Dictionary<ErrorKey,SurfaceLodError> ErrorCache=new Dictionary<ErrorKey,SurfaceLodError>();
            internal long ErrorWork;
            internal int ErrorVersion;
            internal Task<PreparedErrors> ErrorTask;
            internal PlanetSurfaceDataLease WorkerLease;
            internal readonly Queue<ErrorKey> ErrorRequests=new Queue<ErrorKey>();
            internal readonly HashSet<ErrorKey> RequestedErrors=new HashSet<ErrorKey>();
            internal readonly Dictionary<ErrorKey,long> LastUsed=new Dictionary<ErrorKey,long>();
            internal long UseClock;
            internal Exception PreparationFailure;
            internal int Owners,Leases;
            internal bool Retired;
            internal void Retire(){Retired=true;ErrorRequests.Clear();if(ErrorTask!=null)retired.Add(this);TryDispose();}
            internal void TryDispose(){if(Retired && Leases==0){Native?.Dispose();Native=null;Snapshot=null;Errors=null;ErrorCache.Clear();}}
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
                        // Exhaustion is a retryable preparation failure, not a permanent cached terrain value.
                        if(pair.Value.Status==SurfaceErrorStatus.MeasurementBudgetExceeded)continue;
                        if(entry.ErrorCache.Count>=MaximumCachedPatches)EvictLeastUsed(entry);
                        entry.ErrorCache[pair.Key]=pair.Value;entry.LastUsed[pair.Key]=++entry.UseClock;
                    }
                    entry.ErrorVersion++;
                }
            }
            catch(Exception error)
            {
                entry.PreparationFailure=error;entry.RequestedErrors.Clear();entry.ErrorRequests.Clear();entry.ErrorVersion++;
                Debug.LogException(error);
            }
            finally
            {
                entry.ErrorTask=null;activeErrorWorkers--;var lease=entry.WorkerLease;entry.WorkerLease=null;lease?.Dispose();
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
            var keys=new List<ErrorKey>();
            // One worker batch has a global bound; a per-key bound alone could produce unbounded cold work.
            while(entry.ErrorRequests.Count>0&&keys.Count<MaximumBatchWork/MaximumPatchWork)keys.Add(entry.ErrorRequests.Dequeue());
            entry.Leases++;entry.WorkerLease=new PlanetSurfaceDataLease(entry);
            var view=entry.Native.View;var snapshot=entry.Snapshot;var existing=entry.Errors;
            activeErrorWorkers++;entry.ErrorTask=Task.Run(()=>
            {
                var result=new PreparedErrors{Hierarchy=existing??SurfaceErrorHierarchy.Build(snapshot,view,SurfaceErrorBuildSettings.Default)};
                foreach(var key in keys)
                {
                    var error=result.Hierarchy.Measure(view,key.Patch,key.Resolution,new SurfaceSamplingFootprint(key.Footprint),MaximumPatchWork,out int work);
                    result.Work+=work;result.Results.Add(new KeyValuePair<ErrorKey,SurfaceLodError>(key,error));
                }
                return result;
            });
        }
        internal static PlanetSurfaceDescriptor Register(PlanetSurfaceDataAsset asset,SurfaceSnapshot snapshot)
        {
            CheckMainThread();var key=PlanetSurfaceDescriptor.FromSnapshot(snapshot);int id=asset.GetInstanceID();
            if(owners.TryGetValue(id,out var old))
            {if(old.Equals(key))return key;Unregister(asset);}
            if(!entries.TryGetValue(key,out var entry)){entry=new Entry {Snapshot=snapshot};entries.Add(key,entry);}
            entry.Owners++;owners.Add(id,key);return key;
        }
        internal static void Unregister(PlanetSurfaceDataAsset asset)
        {
            CheckMainThread();if(ReferenceEquals(asset,null))return;
            int id=asset.GetInstanceID();if(!owners.TryGetValue(id,out var key))return;owners.Remove(id);
            if(entries.TryGetValue(key,out var entry) && --entry.Owners==0){entries.Remove(key);entry.Retire();}
        }
        public static bool TryAcquire(PlanetSurfaceDescriptor descriptor,out PlanetSurfaceDataLease lease)
        {
            CheckMainThread();lease=null;
            if(!descriptor.IsBound || !entries.TryGetValue(descriptor,out var entry) || entry.Retired)return false;
            if(entry.Native==null)entry.Native=entry.Snapshot.CreateNative(Allocator.Persistent);
            entry.Leases++;lease=new PlanetSurfaceDataLease(entry);return true;
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
    /// <summary>One native lease for a whole LOD selection; candidates perform no asset lookup or registry lease allocation.</summary>
    public sealed class PlanetSurfaceLodContext : IDisposable
    {
        PlanetSurfaceDataRegistry.Entry entry;
        PlanetSurfaceDataLease lease;
        readonly PlanetSurfaceDescriptor descriptor;
        internal PlanetSurfaceLodContext(PlanetSurfaceDataRegistry.Entry entry,PlanetSurfaceDataLease lease){this.entry=entry;this.lease=lease;descriptor=PlanetSurfaceDescriptor.FromSnapshot(entry.Snapshot);}
        public SurfaceLodError Error(SurfaceTileKey key,int resolution,SurfaceSamplingFootprint footprint)
        {
            if(entry==null)throw new ObjectDisposedException(nameof(PlanetSurfaceLodContext));
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(!key.IsValid||!CubeSurface.ValidResolution(resolution)||!footprint.IsValid)throw new ArgumentException("Invalid LOD error request.");
            var measured=PlanetSurfaceDataRegistry.GetError(entry,lease.View,new PlanetSurfaceDataRegistry.ErrorKey(key,resolution,footprint.Metres));
            if(footprint.Metres>0&&PlanetSurfaceRegionFiltering.TryBorrow(descriptor,out var filter))
                return measured.WithRegionalFiltering(filter.Native.View.ConservativeDeviation(lease.View,key,footprint));
            return measured;
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
}
