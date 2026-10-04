using System;
using System.IO;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Offline boundary context for regional refinement; runtime rendering does not load this field.</summary>
    [PreferBinarySerialization]
    public sealed class PlanetHydrologyDataAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] byte[] payload;
        [NonSerialized] SurfaceHydrologyField cached;
        /// <summary>Persisted payload size for authoring storage admission.</summary>
        public long EncodedPayloadBytes=>payload?.LongLength??0;

        public void SetField(SurfaceHydrologyField field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    SurfaceHydrologyCodec.Write(writer, field);
                payload = stream.ToArray();
            }
            cached = field;
        }

        public bool TryCreateField(out SurfaceHydrologyField field, out string failure)
        {
            field = cached; failure = null;
            if (field != null) return true;
            try
            {
                if (payload == null || payload.Length == 0) throw new InvalidDataException("The coarse hydrology payload is missing.");
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream))
                {
                    field = SurfaceHydrologyCodec.Read(reader);
                    if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected data follows the coarse hydrology field.");
                }
                cached = field; return true;
            }
            catch (Exception exception)
            {
                field = null; failure = exception.Message; return false;
            }
        }

        void OnValidate() => cached = null;
    }
}
