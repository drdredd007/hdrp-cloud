using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    public struct PlanetMediaTextureSource
    {
        public int Mode; // 0 uniform Simple, 1 procedural planetary, 2 authored, 3 Advanced
        public Texture Map, Cumulus, Stratus, Nimbus, Rain, Lut;
        public Vector3 Multipliers;
    }
    // An atlas of packed RGBA8 texels in a structured buffer, avoiding texture-array slice caps.
    // Only currently visible resources reside here. Identical weather shares one bake, independent
    // of planet rotation, wind or render origin. The budget reduces detail, never body count.
    public sealed class PlanetMediaTextureCache : IDisposable
    {
        struct Key : IEquatable<Key>
        {
            public Vector4 Coverage, Climate, Tiling;
            public float Seed, Radius, Bottom, Top;
            public PlanetMediaTextureSource Source;
            public int Resolution;
            public bool Lut;
            public uint MapVersion, CumulusVersion, StratusVersion, NimbusVersion, RainVersion, LutVersion;
            static bool Same(Texture a, Texture b) => a == b;
            public bool Equals(Key other) => Coverage.Equals(other.Coverage) && Climate.Equals(other.Climate) && Tiling.Equals(other.Tiling) &&
                Seed == other.Seed && Radius == other.Radius && Bottom == other.Bottom && Top == other.Top &&
                Source.Mode == other.Source.Mode && Source.Multipliers.Equals(other.Source.Multipliers) &&
                Same(Source.Map, other.Source.Map) && Same(Source.Cumulus, other.Source.Cumulus) && Same(Source.Stratus, other.Source.Stratus) &&
                Same(Source.Nimbus, other.Source.Nimbus) && Same(Source.Rain, other.Source.Rain) && Same(Source.Lut, other.Source.Lut) &&
                MapVersion == other.MapVersion && CumulusVersion == other.CumulusVersion && StratusVersion == other.StratusVersion &&
                NimbusVersion == other.NimbusVersion && RainVersion == other.RainVersion && LutVersion == other.LutVersion &&
                Resolution == other.Resolution && Lut == other.Lut;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => HashCode.Combine(Coverage, Climate, Tiling, Seed, Radius, Resolution, Lut, Source.Mode);
        }
        sealed class Entry { public Key Key; public int Body, Offset; }
        readonly List<Entry> previous = new List<Entry>();
        readonly List<Entry> requests = new List<Entry>();
        readonly Dictionary<Key, Entry> unique = new Dictionary<Key, Entry>();
        GraphicsBuffer atlas;
        int capacity, texels;
        bool changed;
        public GraphicsBuffer Atlas => atlas;
        public int BakeCount { get; private set; }
        public long AllocatedBytes => (long)capacity * 4;
        static uint Version(Texture texture) => texture ? texture.updateCount : 0;
        static Key MakeKey(PlanetMediaGpuBody body, PlanetMediaTextureSource source, int resolution, bool lut)
        {
            if (lut) return new Key { Source = new PlanetMediaTextureSource { Lut = source.Lut }, Resolution = resolution,
                Lut = true, LutVersion = Version(source.Lut) };
            if (source.Mode == 0) return new Key { Resolution = 1 };
            return new Key
            {
                Coverage = body.Coverage, Climate = body.Climate, Tiling = body.CloudMapTiling, Seed = body.SeedWind.x,
                Radius = body.CenterRadius.w, Bottom = body.RegionMetadata.w > body.RegionMetadata.z ? body.RegionMetadata.z : body.Limits.y,
                Top = body.RegionMetadata.w > body.RegionMetadata.z ? body.RegionMetadata.w : body.Limits.z, Source = source, Resolution = resolution, Lut = false,
                MapVersion = Version(source.Map), CumulusVersion = Version(source.Cumulus), StratusVersion = Version(source.Stratus),
                NimbusVersion = Version(source.Nimbus), RainVersion = Version(source.Rain), LutVersion = Version(source.Lut)
            };
        }
        Entry Add(Key key, int body)
        {
            if (unique.TryGetValue(key, out var existing)) return existing;
            var entry = new Entry { Key = key, Body = body, Offset = texels };
            texels += key.Resolution * key.Resolution * (key.Lut ? 1 : 6);
            requests.Add(entry); unique.Add(key, entry); return entry;
        }
        public void Prepare(PlanetMediaGpuBody[] bodies, PlanetMediaTextureSource[] sources, int count,
            int maximumResolution, int budgetMiB, int screenHeight, float fieldOfView)
        {
            requests.Clear(); unique.Clear(); texels = 0;
            int budgetTexels = Mathf.Clamp(budgetMiB, 1, 512) * 1024 * 1024 / 4;
            for (int i = 0; i < count; ++i)
            {
                var body = bodies[i]; body.TextureMetadata = Vector4.zero;
                if (body.CloudLighting.w <= 0) { bodies[i] = body; continue; }
                float distance = new Vector3(body.CenterRadius.x, body.CenterRadius.y, body.CenterRadius.z).magnitude;
                float radius = body.CenterRadius.w + body.Limits.z;
                float pixels = distance <= radius ? screenHeight : radius / Mathf.Max(1, distance) * screenHeight /
                    (2 * Mathf.Tan(fieldOfView * Mathf.Deg2Rad * .5f));
                int resolution = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(pixels)), 8, Mathf.ClosestPowerOfTwo(Mathf.Clamp(maximumResolution, 8, 512)));
                if (sources[i].Mode == 0) resolution = 1;
                // A shared key consumes no further budget; otherwise decrease the map LOD until it fits.
                var key = MakeKey(body, sources[i], resolution, false);
                while (!unique.ContainsKey(key) && texels + resolution * resolution * 6 > budgetTexels && resolution > 8)
                { resolution /= 2; key = MakeKey(body, sources[i], resolution, false); }
                if (unique.ContainsKey(key) || texels + resolution * resolution * 6 <= budgetTexels)
                {
                    var entry = Add(key, i); body.TextureMetadata.x = entry.Offset; body.TextureMetadata.y = resolution;
                }
                // Type LUTs are tiny and shared as well. If no slot fits, native Simple curves remain available.
                if (sources[i].Lut)
                {
                    var lutKey = MakeKey(body, sources[i], 64, true);
                    if (unique.ContainsKey(lutKey) || texels + 4096 <= budgetTexels)
                    { var entry = Add(lutKey, i); body.TextureMetadata.z = entry.Offset; body.TextureMetadata.w = 64; }
                }
                bodies[i] = body;
            }
            changed = requests.Count != previous.Count;
            if (!changed)
                for (int i = 0; i < requests.Count; ++i)
                    if (!requests[i].Key.Equals(previous[i].Key) || requests[i].Offset != previous[i].Offset) { changed = true; break; }
            int required = Mathf.Min(budgetTexels, Mathf.NextPowerOfTwo(Mathf.Max(1, texels)));
            if (required > capacity || capacity > budgetTexels)
            { atlas?.Dispose(); capacity = required; atlas = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4); changed = true; }
        }
        public void Bake(CommandBuffer cmd, ComputeShader shader, int kernel, GraphicsBuffer bodies)
        {
            if (!changed) return;
            cmd.SetComputeBufferParam(shader, kernel, "_PlanetMediaBodies", bodies);
            cmd.SetComputeBufferParam(shader, kernel, "_PlanetMediaCoverageRW", atlas);
            foreach (var entry in requests)
            {
                var source = entry.Key.Source;
                cmd.SetComputeIntParam(shader, "_PlanetMediaBakeBody", entry.Body);
                cmd.SetComputeIntParam(shader, "_PlanetMediaBakeOffset", entry.Offset);
                cmd.SetComputeIntParam(shader, "_PlanetMediaBakeResolution", entry.Key.Resolution);
                cmd.SetComputeIntParam(shader, "_PlanetMediaBakeMode", entry.Key.Lut ? 4 : source.Mode);
                cmd.SetComputeVectorParam(shader, "_PlanetMediaMapMultipliers", source.Multipliers);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaAuthoredMap", source.Map ? source.Map : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaCumulusMap", source.Cumulus ? source.Cumulus : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaStratusMap", source.Stratus ? source.Stratus : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaNimbusMap", source.Nimbus ? source.Nimbus : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaRainMap", source.Rain ? source.Rain : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(shader, kernel, "_PlanetMediaAuthoredLut", source.Lut ? source.Lut : Texture2D.blackTexture);
                int groups = (entry.Key.Resolution + 7) / 8;
                cmd.DispatchCompute(shader, kernel, groups, groups, entry.Key.Lut ? 1 : 6); ++BakeCount;
            }
            previous.Clear(); previous.AddRange(requests); changed = false;
        }
        public void Dispose()
        { atlas?.Dispose(); atlas = null; capacity = 0; previous.Clear(); requests.Clear(); unique.Clear(); }
    }
}
