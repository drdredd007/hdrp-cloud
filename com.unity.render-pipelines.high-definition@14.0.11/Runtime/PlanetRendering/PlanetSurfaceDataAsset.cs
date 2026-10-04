using System;
using System.IO;
using SpaceRunner.PlanetTerrain;

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
        public Texture2DArray BaseColour=>baseColour;
        public WorldOrogenSettings WorldOrogenSource=>worldOrogenSettings?.Clone();
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
        /// <summary>Persisted payload size for authoring storage admission; transient runtime instances have no encoded payload.</summary>
        public long EncodedPayloadBytes=>payload?.LongLength??0;
        public SurfaceRecipe Recipe => GetSnapshot().Recipe;
        public PlanetSurfaceDescriptor Descriptor => PlanetSurfaceDataRegistry.Register(this,GetSnapshot());
        public bool TryCreateSnapshot(out SurfaceSnapshot snapshot,out string error)
        {
            snapshot=null;error=null;
            try {snapshot=GetSnapshot();return true;}
            catch(Exception exception) when(exception is ArgumentException || exception is InvalidOperationException || exception is IOException || exception is InvalidDataException)
            {error=exception.Message;return false;}
        }
        public void SetSnapshot(SurfaceSnapshot snapshot)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
            if(!snapshot.MaterialsReady)throw new InvalidOperationException("Prepare the captured automatic material rules on final geometry before saving this surface. The existing asset payload remains unchanged.");
            var candidatePayload=PlanetSurfaceAssetPayload.Encode(snapshot);
            PlanetSurfaceDataRegistry.Unregister(this);
            payload=candidatePayload;cached=snapshot;baseColour=null;baseColourDigest=null;worldOrogenSettings=null;worldOrogenCommit=null;
        }
        /// <summary>Bind a prepared immutable runtime instance without serializing its complete bake
        /// again on the publication thread. Only a fresh transient asset may use this path; archives
        /// capture the snapshot itself, and persistent authoring still uses SetSnapshot.</summary>
        public void InitializeRuntimeSnapshot(SurfaceSnapshot snapshot)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(snapshot==null||!snapshot.MaterialsReady)throw new ArgumentException("A complete prepared instance snapshot is required.");
            if(cached!=null||payload!=null&&payload.Length!=0)throw new InvalidOperationException("Runtime initialization requires a fresh transient asset.");
#if UNITY_EDITOR
            if(UnityEditor.EditorUtility.IsPersistent(this))throw new InvalidOperationException("An authored dataset cannot become a runtime instance.");
#endif
            cached=snapshot;
        }
        SurfaceSnapshot GetSnapshot()
        {
            if(cached!=null)return cached;
            if(payload==null || payload.Length==0)throw new InvalidOperationException("Planet surface asset contains no published snapshot.");
            var result=PlanetSurfaceAssetPayload.Decode(payload);
            if(baseColour&&baseColourDigest!=result.Revision.BaseDigest.ToString())throw new InvalidDataException("Base colour provenance differs from the published height map.");
            cached=result;return result;
        }
        // OnValidate can execute on Unity's loading thread. Resolution and native allocation happen on the main thread.
        void OnValidate(){cached=null;}
        void OnDisable(){PlanetSurfaceDataRegistry.Unregister(this);}
    }
}
