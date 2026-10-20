using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Serialized bridge to the renderer-neutral, immutable surface data format.</summary>
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Surface Data",fileName="PlanetSurface")]
    [PreferBinarySerialization]
    public sealed class PlanetSurfaceDataAsset : ScriptableObject
    {
        [SerializeField,HideInInspector] byte[] payload;
        [SerializeField,HideInInspector] Texture2DArray baseColour;
        [SerializeField,HideInInspector] string baseColourDigest;
        [SerializeField,HideInInspector] WorldOrogenSettings worldOrogenSettings;
        [SerializeField,HideInInspector] string worldOrogenCommit;
        // Explicit only for a newly published prepared native parent. Ordinary
        // authored surfaces retain their existing synchronous API and storage.
        [Serializable] sealed class PreparedHeader
        {
            public string model,payloadDigest;public PlanetSurfaceDescriptor descriptor;
            public int seed;public double radius,minimum,maximum;
            public PreparedHeader Clone()=>(PreparedHeader)MemberwiseClone();
            public bool IsValid=>model=="prepared-background-parent/v1"&&descriptor.IsBound&&
                SurfaceContentHash.TryParse(payloadDigest,out _)&&double.IsFinite(radius)&&radius>0&&double.IsFinite(minimum)&&double.IsFinite(maximum)&&minimum<=maximum&&minimum>-radius;
        }
        [SerializeField,HideInInspector] PreparedHeader preparedHeader;
        [NonSerialized] PreparedLoad preparedLoad;
        [NonSerialized] int payloadGeneration,preparedGeneration=-1;
        [NonSerialized] string preparedFailure;
        [NonSerialized] int preparedRequests;
        public bool PreparedBackgroundEnabled=>preparedHeader!=null;
        public bool PreparedBackgroundPending=>PreparedBackgroundEnabled&&preparedFailure==null&&(preparedLoad==null||!preparedLoad.Work.IsCompleted);
        public string PreparedBackgroundFailure=>preparedFailure;
        public int PreparedBackgroundRequestsStarted=>preparedRequests;
        public Task PreparedBackgroundCompletion=>preparedLoad?.Work??Task.CompletedTask;
        public int PreparedBackgroundDecodeThreadId=>preparedLoad?.DecodeThreadId??0;
        public static int PreparedBackgroundWorkerCount=>Volatile.Read(ref preparedWorkers);
        public static int PreparedBackgroundUnclaimedNativeCopies=>Volatile.Read(ref preparedNativeCopies);
        public Texture2DArray BaseColour=>baseColour;
        // Unity reconstructs null inline serializable classes on asset reload.
        // Only SetWorldOrogenSource publishes the provenance marker; a default
        // reconstructed settings object is not a captured source.
        public WorldOrogenSettings WorldOrogenSource=>string.IsNullOrEmpty(worldOrogenCommit)?null:worldOrogenSettings?.Clone();
        public string WorldOrogenSourceCommit=>worldOrogenCommit;
        public void SetWorldOrogenSource(WorldOrogenSettings value)
        {
            if(value==null||!value.Validate(out _))throw new ArgumentException("Complete captured World Orogen parameters are required.");
            if(GetSnapshot().Recipe.Seed!=value.Seed)throw new ArgumentException("Source seed differs from the published surface.");
            worldOrogenSettings=value.Clone();worldOrogenCommit=WorldOrogenSettings.SourceCommit;
        }
        public void SetBaseColour(Texture2DArray value)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();var snapshot=GetSnapshot();
            if(value&&(value.depth!=6||value.width!=snapshot.Resolution+1||value.height!=snapshot.Resolution+1||snapshot.CanonicalTileLevel!=0))
                throw new ArgumentException("A generated base colour must match the six canonical face vertex grids.");
            PlanetSurfaceDataRegistry.Unregister(this);baseColour=value;baseColourDigest=snapshot.Revision.BaseDigest.ToString();
        }
        [NonSerialized] SurfaceSnapshot cached;
        [NonSerialized] SurfaceRegionFilterSettings runtimeRegionFilterSettings;
        internal SurfaceRegionFilterSettings RuntimeRegionFilterSettings=>runtimeRegionFilterSettings.IsValid?runtimeRegionFilterSettings:SurfaceRegionFilterSettings.Default;
        /// <summary>Persisted payload size for authoring storage admission; transient runtime instances have no encoded payload.</summary>
        public long EncodedPayloadBytes=>payload?.LongLength??0;
        public SurfaceRecipe Recipe => GetSnapshot().Recipe;
        public PlanetSurfaceDescriptor Descriptor
        {
            get {
                if(Application.isPlaying&&PreparedBackgroundEnabled){TryPrepareBackgroundSnapshot(out _,out _);return preparedHeader.descriptor;}
                return PlanetSurfaceDataRegistry.Register(this,GetSnapshot());
            }
        }
        public bool TryCreateSnapshot(out SurfaceSnapshot snapshot,out string error)
        {
            snapshot=null;error=null;
            try {snapshot=GetSnapshot();return true;}
            catch(Exception exception) when(exception is ArgumentException || exception is InvalidOperationException || exception is IOException || exception is InvalidDataException)
            {error=exception.Message;return false;}
        }
        /// <summary>Publish only for a verified ready bare parent. Stores a small
        /// definition header, so runtime bootstrap never decodes the map merely
        /// to discover its radius or identity. This does not start a worker.</summary>
        public void ConfigurePreparedBackground()
        {
            PlanetSurfaceDataRegistry.CheckMainThread();var snapshot=GetSnapshot();RequirePreparedParent(snapshot);
            if(payload==null||payload.Length==0)throw new InvalidOperationException("A persisted prepared parent payload is required.");
            ReleasePrepared();preparedHeader=new PreparedHeader{model="prepared-background-parent/v1",payloadDigest=SurfaceContentHash.Compute(payload).ToString(),descriptor=PlanetSurfaceDescriptor.FromSnapshot(snapshot),seed=snapshot.Recipe.Seed,radius=snapshot.Recipe.Radius,minimum=snapshot.MinimumHeight,maximum=snapshot.MaximumHeight};
            preparedFailure=null;Interlocked.Increment(ref payloadGeneration);
        }
        public bool TryGetPreparedDefinitionHeader(out PlanetSurfaceDescriptor descriptor,out int seed,out double radius,out double relief)
        {
            descriptor=default;seed=0;radius=relief=0;var header=preparedHeader;if(header==null||!header.IsValid)return false;
            descriptor=header.descriptor;seed=header.seed;radius=header.radius;relief=Math.Max(Math.Abs(header.minimum),Math.Abs(header.maximum));return true;
        }
        /// <summary>Explicit nonblocking prepared-parent provider. Captures an
        /// immutable managed payload reference; decode and native copies happen
        /// on a shared worker. Pending never waits or publishes substitute data.</summary>
        public bool TryPrepareBackgroundSnapshot(out SurfaceSnapshot snapshot,out string error)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();snapshot=null;error=null;
            if(preparedHeader==null||!preparedHeader.IsValid||payload==null||payload.Length==0){preparedFailure=error="The prepared background parent header or payload is invalid.";return false;}
            if(preparedLoad!=null&&(preparedGeneration!=payloadGeneration||preparedLoad.Epoch!=preparedEpoch)){ReleasePrepared();preparedFailure=null;}
            if(preparedFailure!=null){error=preparedFailure;return false;}
            if(preparedLoad==null){
                preparedGeneration=payloadGeneration;var header=preparedHeader.Clone();
                preparedLoad=AcquirePrepared(payload,header);preparedRequests++;error="Prepared parent decoding is pending.";return false;
            }
            if(!preparedLoad.Work.IsCompleted){error="Prepared parent decoding is pending.";return false;}
            NativeSurfaceSnapshot native=null;
            try {
                var ready=preparedLoad.Work.GetAwaiter().GetResult();ValidatePreparedHeader(ready.Snapshot,preparedHeader);native=ready.TakeNative();
                PlanetSurfaceDataRegistry.RegisterPrepared(this,ready.Snapshot,ref native);
                cached=snapshot=ready.Snapshot;return true;
            }catch(Exception exception){preparedFailure=error=exception.GetBaseException().Message;return false;}
            finally {if(native!=null){var unused=native;_=Task.Run(()=>unused.Dispose());}}
        }
        /// <summary>Releases this asset's interest without canceling another
        /// asset alias. Worker results remain owned until completion.</summary>
        public void CancelPreparedBackground()
        {PlanetSurfaceDataRegistry.CheckMainThread();ReleasePrepared();preparedFailure=null;cached=null;Interlocked.Increment(ref payloadGeneration);}
        public void SetSnapshot(SurfaceSnapshot snapshot)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
            if(!snapshot.MaterialsReady)throw new InvalidOperationException("Prepare the captured automatic material rules on final geometry before saving this surface. The existing asset payload remains unchanged.");
            var candidatePayload=PlanetSurfaceAssetPayload.Encode(snapshot);
            PlanetSurfaceDataRegistry.Unregister(this);
            ReleasePrepared();preparedHeader=null;preparedFailure=null;Interlocked.Increment(ref payloadGeneration);
            payload=candidatePayload;cached=snapshot;baseColour=null;baseColourDigest=null;worldOrogenSettings=null;worldOrogenCommit=null;
        }
        /// <summary>Bind a prepared immutable runtime instance without serializing its complete bake
        /// again on the publication thread. Only a fresh transient asset may use this path; archives
        /// capture the snapshot itself, and persistent authoring still uses SetSnapshot.</summary>
        public void InitializeRuntimeSnapshot(SurfaceSnapshot snapshot)
            =>InitializeRuntimeSnapshot(snapshot,default);
        public void InitializeRuntimeSnapshot(SurfaceSnapshot snapshot,SurfaceRegionFilterSettings regionFilterSettings)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(snapshot==null||!snapshot.MaterialsReady)throw new ArgumentException("A complete prepared instance snapshot is required.");
            if(cached!=null||payload!=null&&payload.Length!=0)throw new InvalidOperationException("Runtime initialization requires a fresh transient asset.");
#if UNITY_EDITOR
            if(UnityEditor.EditorUtility.IsPersistent(this))throw new InvalidOperationException("An authored dataset cannot become a runtime instance.");
#endif
            if(!regionFilterSettings.IsValid&&(regionFilterSettings.MaximumBytes!=0||regionFilterSettings.MaximumSupportSamples!=0))throw new ArgumentException("A bounded derived region filter policy is required.");
            runtimeRegionFilterSettings=regionFilterSettings;cached=snapshot;
        }
        SurfaceSnapshot GetSnapshot()
        {
            if(Application.isPlaying&&PreparedBackgroundEnabled){if(TryPrepareBackgroundSnapshot(out var prepared,out var pending))return prepared;throw new InvalidOperationException(pending);}
            if(cached!=null)return cached;
            if(payload==null || payload.Length==0)throw new InvalidOperationException("Planet surface asset contains no published snapshot.");
            var result=PlanetSurfaceAssetPayload.Decode(payload);
            if(baseColour&&baseColourDigest!=result.Revision.BaseDigest.ToString())throw new InvalidDataException("Base colour provenance differs from the published height map.");
            cached=result;return result;
        }
        // OnValidate can execute on Unity's loading thread. Resolution and native allocation happen on the main thread.
        void OnValidate(){cached=null;Interlocked.Increment(ref payloadGeneration);}
        void OnDisable(){PlanetSurfaceDataRegistry.Unregister(this);ReleasePrepared();}
        static void RequirePreparedParent(SurfaceSnapshot snapshot)
        {
            if(snapshot.CanonicalTileLevel!=0||snapshot.Tiles.Count!=6||snapshot.Regions.Count!=0||snapshot.Stamps.Count!=0||snapshot.Detail.AmplitudeMetres!=0||snapshot.StructuralField!=null||snapshot.OrogenDetail!=null||snapshot.HasAutomaticMaterials)
                throw new InvalidDataException("Only a complete bare prepared six-face parent uses background publication.");
        }
        static void ValidatePreparedHeader(SurfaceSnapshot snapshot,PreparedHeader header)
        {
            if(!PlanetSurfaceDescriptor.FromSnapshot(snapshot).Equals(header.descriptor)||snapshot.Recipe.Seed!=header.seed||snapshot.Recipe.Radius!=header.radius||snapshot.MinimumHeight!=header.minimum||snapshot.MaximumHeight!=header.maximum)throw new InvalidDataException("Prepared parent definition header differs from its decoded immutable payload.");
        }
        sealed class PreparedResult
        {
            internal readonly SurfaceSnapshot Snapshot;NativeSurfaceSnapshot native;
            internal PreparedResult(SurfaceSnapshot snapshot,NativeSurfaceSnapshot native){Snapshot=snapshot;this.native=native;Interlocked.Increment(ref preparedNativeCopies);}
            internal NativeSurfaceSnapshot TakeNative(){var value=Interlocked.Exchange(ref native,null);if(value!=null)Interlocked.Decrement(ref preparedNativeCopies);return value;}
            internal void DisposeUnclaimed(){TakeNative()?.Dispose();}
        }
        sealed class PreparedLoad
        {
            internal readonly string Key;internal readonly int Epoch;internal readonly Task<PreparedResult> Work;
            internal int DecodeThreadId;
            internal int References;readonly CancellationTokenSource cancellation=new CancellationTokenSource();int retiring;
            internal PreparedLoad(byte[] bytes,PreparedHeader header,int epoch)
            {
                Key=header.payloadDigest;Epoch=epoch;
                Work=Task.Run(()=>{
                    NativeSurfaceSnapshot native=null;var token=cancellation.Token;DecodeThreadId=Thread.CurrentThread.ManagedThreadId;Interlocked.Increment(ref preparedWorkers);
                    try {
                        token.ThrowIfCancellationRequested();if(SurfaceContentHash.Compute(bytes).ToString()!=header.payloadDigest)throw new InvalidDataException("Prepared parent payload SHA changed.");
                        var snapshot=PlanetSurfaceAssetPayload.Decode(bytes);token.ThrowIfCancellationRequested();RequirePreparedParent(snapshot);
                        ValidatePreparedHeader(snapshot,header);
                        native=snapshot.CreateNative(Allocator.Persistent);token.ThrowIfCancellationRequested();var result=new PreparedResult(snapshot,native);native=null;return result;
                    }finally {native?.Dispose();Interlocked.Decrement(ref preparedWorkers);}
                });
            }
            internal void Retire()
            {
                if(Interlocked.Exchange(ref retiring,1)!=0)return;cancellation.Cancel();
                _=Work.ContinueWith(done=>{try{if(done.Status==TaskStatus.RanToCompletion)done.Result.DisposeUnclaimed();else if(done.IsFaulted)_=done.Exception;}finally{cancellation.Dispose();}},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
            }
        }
        static readonly Dictionary<string,PreparedLoad> preparedLoads=new Dictionary<string,PreparedLoad>(StringComparer.Ordinal);
        static int preparedEpoch,preparedWorkers,preparedNativeCopies;
        static PreparedLoad AcquirePrepared(byte[] bytes,PreparedHeader header)
        {
            if(!preparedLoads.TryGetValue(header.payloadDigest,out var load)){load=new PreparedLoad(bytes,header,preparedEpoch);preparedLoads.Add(load.Key,load);}
            load.References++;return load;
        }
        void ReleasePrepared()
        {
            var load=preparedLoad;preparedLoad=null;preparedGeneration=-1;if(load==null)return;
            if(--load.References!=0)return;if(preparedLoads.TryGetValue(load.Key,out var current)&&ReferenceEquals(current,load))preparedLoads.Remove(load.Key);load.Retire();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPreparedBackground()
        {foreach(var load in preparedLoads.Values)load.Retire();preparedLoads.Clear();preparedEpoch++;}
    }
}
