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
        [NonSerialized] SurfaceSnapshot cached;
        public SurfaceRecipe Recipe => GetSnapshot().Recipe;
        public PlanetSurfaceDescriptor Descriptor => PlanetSurfaceDataRegistry.Register(this,GetSnapshot());
        public bool TryCreateSnapshot(out SurfaceSnapshot snapshot,out string error)
        {
            snapshot=null;error=null;
            try {snapshot=GetSnapshot();return true;}
            catch(Exception exception) when(exception is ArgumentException || exception is InvalidOperationException || exception is IOException)
            {error=exception.Message;return false;}
        }
        public void SetSnapshot(SurfaceSnapshot snapshot)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
            if(!snapshot.MaterialsReady)throw new InvalidOperationException("Prepare the captured automatic material rules on final geometry before saving this surface. The existing asset payload remains unchanged.");
            using(var stream=new MemoryStream())
            {
                using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)) SurfaceSnapshotCodec.Write(writer,snapshot);
                payload=stream.ToArray();
            }
            PlanetSurfaceDataRegistry.Unregister(this);cached=snapshot;
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
            using(var stream=new MemoryStream(payload,false))
            using(var reader=new BinaryReader(stream))
            {
                var result=SurfaceSnapshotCodec.Read(reader);
                if(!result.MaterialsReady)throw new InvalidDataException("Published surface contains an incomplete automatic material cache.");
                if(stream.Position!=stream.Length)throw new InvalidDataException("Unexpected data after the surface snapshot.");
                cached=result;return result;
            }
        }
        // OnValidate can execute on Unity's loading thread. Resolution and native allocation happen on the main thread.
        void OnValidate(){cached=null;}
        void OnDisable(){PlanetSurfaceDataRegistry.Unregister(this);}
    }
}
