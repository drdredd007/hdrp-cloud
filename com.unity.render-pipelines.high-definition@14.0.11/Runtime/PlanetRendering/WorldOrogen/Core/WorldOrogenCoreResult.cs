// World Orogen port of raguilar011095/planet_heightmap_generation, commit
// cc2662b4edd52231c4f65d8765f3ef12cd82d9b7. GPL-3.0-only; see ../LICENSE.txt.
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Completed core generation. Post stages mutate State; climate uses the captured plate mask.</summary>
    public sealed class WorldOrogenCoreResult : IDisposable
    {
        public WorldOrogenGpuState State { get; }
        public WorldOrogenPlateSet Plates { get; }
        // Upstream plateIsOcean.has(r_plate[r]); independent of elevation after erosion.
        public uint[] RegionPlateOcean { get; }
        internal WorldOrogenCoreResult(WorldOrogenGpuState state, WorldOrogenPlateSet plates)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Plates = plates ?? throw new ArgumentNullException(nameof(plates));
            if (plates.RegionPlate.Length != state.RegionCount) throw new ArgumentException("Mismatched plate graph.");
            RegionPlateOcean = new uint[state.RegionCount];
            for (int r = 0; r < RegionPlateOcean.Length; ++r)
                RegionPlateOcean[r] = plates.Ocean[plates.RegionPlate[r]] ? 1u : 0u;
        }
        public void Dispose() => State.Dispose();
    }
}
