using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    // Rendering ownership is per camera. The ordinary HDRP path remains available to every
    // camera without an owner; ambient sky/probe updates are deliberately not disabled.
    public static class PlanetMediaCameraRegistry
    {
        sealed class Entry
        {
            public Camera Camera;
            public readonly HashSet<object> Owners = new HashSet<object>();
            public readonly HashSet<object> NativeWeatherOwners = new HashSet<object>();
            public readonly Dictionary<object, Transport> Transports = new Dictionary<object, Transport>();
            public readonly Dictionary<object, WeatherLighting> Lights = new Dictionary<object, WeatherLighting>();
        }
        struct Transport { public Vector4 Distance, Grid; public long Revision; }
        public struct WeatherLighting
        {
            public bool UseSceneLights, HasAtmosphere;
            public Quaternion Rotation;
            public Vector3 Direction;
            public Color Color;
            public float Lux;
            internal long Revision;
        }
        static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        static long revision;

        public static void Register(Camera camera, object owner, bool nativeWeather=false)
        {
            if (!camera || owner == null) return;
            int id = camera.GetInstanceID();
            if (!entries.TryGetValue(id, out var entry) || entry.Camera != camera)
                entries[id] = entry = new Entry { Camera = camera };
            entry.Owners.Add(owner);
            if(nativeWeather)entry.NativeWeatherOwners.Add(owner);
            else entry.NativeWeatherOwners.Remove(owner);
        }
        public static void Unregister(Camera camera, object owner)
        {
            if (ReferenceEquals(camera, null) || owner == null) return;
            int id = camera.GetInstanceID();
            if (!entries.TryGetValue(id, out var entry) || entry.Camera != camera) return;
            entry.Owners.Remove(owner);
            entry.NativeWeatherOwners.Remove(owner);
            entry.Transports.Remove(owner);
            entry.Lights.Remove(owner);
            if (entry.Owners.Count == 0) entries.Remove(id);
        }
        public static bool IsActive(Camera camera)
        {
            if (!camera) return false;
            return entries.TryGetValue(camera.GetInstanceID(), out var entry) &&
                entry.Camera == camera && entry.Owners.Count != 0;
        }
        public static bool UsesNativeWeather(Camera camera)
            =>camera && entries.TryGetValue(camera.GetInstanceID(),out var entry) &&
                entry.Camera==camera && entry.NativeWeatherOwners.Count!=0;
        public static void SetNativeWeatherLighting(Camera camera, object owner, bool useSceneLights,
            bool hasAtmosphere, Quaternion rotation, Vector3 direction, Color color, float lux)
        {
            if (!camera || owner == null || !entries.TryGetValue(camera.GetInstanceID(), out var entry) ||
                entry.Camera != camera || !entry.NativeWeatherOwners.Contains(owner)) return;
            float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            if (!(length > 1e-6f) || float.IsInfinity(length)) rotation = Quaternion.identity;
            else rotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
            if (!Finite(direction.x) || !Finite(direction.y) || !Finite(direction.z) || direction.sqrMagnitude < 1e-8f)
                direction = Vector3.up;
            if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b)) color = Color.black;
            if (!Finite(lux)) lux = 0;
            entry.Lights[owner] = new WeatherLighting
            {
                UseSceneLights = useSceneLights, HasAtmosphere = hasAtmosphere, Rotation = rotation,
                Direction = direction.normalized, Color = color.linear, Lux = Mathf.Max(0, lux), Revision = ++revision
            };
        }
        public static bool TryGetNativeWeatherLighting(Camera camera, out WeatherLighting lighting)
        {
            lighting = default;
            if (!camera || !entries.TryGetValue(camera.GetInstanceID(), out var entry) || entry.Camera != camera) return false;
            long newest = -1;
            foreach (var pair in entry.Lights)
                if (entry.NativeWeatherOwners.Contains(pair.Key) && pair.Value.Revision > newest)
                { newest = pair.Value.Revision; lighting = pair.Value; }
            return newest >= 0;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static void SetTransparentTransport(Camera camera, object owner, Vector4 distance, Vector4 grid)
        {
            if (!camera || owner == null) return;
            if(!entries.TryGetValue(camera.GetInstanceID(),out var registered) || registered.Camera!=camera || !registered.Owners.Contains(owner))
                Register(camera, owner);
            var entry = entries[camera.GetInstanceID()];
            if (distance.w <= 0) entry.Transports.Remove(owner);
            else entry.Transports[owner] = new Transport { Distance = distance, Grid = grid, Revision = ++revision };
        }
        internal static bool TryGetTransparentTransport(Camera camera, out Vector4 distance, out Vector4 grid)
        {
            distance = grid = Vector4.zero;
            if (!camera || !entries.TryGetValue(camera.GetInstanceID(), out var entry) || entry.Camera != camera) return false;
            long newest = -1;
            foreach (var value in entry.Transports.Values)
                if (value.Revision > newest) { newest = value.Revision; distance = value.Distance; grid = value.Grid; }
            return newest >= 0;
        }
    }
}
