using System;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed class PlanetSurfaceMaterialGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Profile { public int4 Flags; public uint4 Temperature, LapseNormal, Rock, Snow; }
        readonly NativeSurfaceView view;
        GraphicsBuffer profile, regionalProfiles, tileProvenance;
        internal static long EstimateBytes(in NativeSurfaceView view) => checked(80L + (long)math.max(1, view.RegionalMaterialProfiles.Length) * 80 + (long)math.max(1, view.TileMaterialProvenance.Length) * 4);
        internal PlanetSurfaceMaterialGpu(in NativeSurfaceView view)
        {
            this.view = view;
            try
            {
                var regions = new Profile[view.RegionalMaterialProfiles.Length]; for (int i = 0; i < regions.Length; i++) regions[i] = Pack(view.RegionalMaterialProfiles[i]);
                var provenance = new int[view.TileMaterialProvenance.Length]; for (int i = 0; i < provenance.Length; i++) provenance[i] = view.TileMaterialProvenance[i];
                profile = Buffer(new[] { Pack(view.AutomaticMaterialProfile) }); regionalProfiles = Buffer(regions); tileProvenance = Buffer(provenance);
            }
            catch { Dispose(); throw; }
        }
        static Profile Pack(SurfaceAutomaticMaterialProfile p) => new Profile { Flags = new int4(p.Version, 0, 0, 0),
            Temperature = PlanetSurfaceGpuData.Pair(p.EquatorTemperature, p.PoleTemperature), LapseNormal = PlanetSurfaceGpuData.Pair(p.LapseRatePerKilometre, p.NormalSampleMetres),
            Rock = PlanetSurfaceGpuData.Pair(p.RockSlopeStart, p.RockSlopeEnd), Snow = PlanetSurfaceGpuData.Pair(p.SnowTemperatureStart, p.SnowTemperatureEnd) };
        internal void Bind(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, "_SurfaceAutomaticProfile", profile); cmd.SetComputeBufferParam(shader, kernel, "_SurfaceRegionalProfiles", regionalProfiles);
            cmd.SetComputeBufferParam(shader, kernel, "_SurfaceTileMaterialProvenance", tileProvenance);
            cmd.SetComputeIntParam(shader, "_SurfaceDynamicMaterials", view.StructuralField.Enabled && view.AutomaticMaterialProfile.IsValid ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_SurfaceDynamicMaterialStyle", (int)view.Recipe.Style);
            var sea = PlanetSurfaceGpuData.Pair(view.Recipe.SeaLevel, 0); cmd.SetComputeIntParams(shader, "_SurfaceDynamicSeaLevel", unchecked((int)sea.x), unchecked((int)sea.y), 0, 0);
        }
        static GraphicsBuffer Buffer<T>(T[] values) where T : struct
        {
            var result = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, values.Length), Marshal.SizeOf<T>());
            try { result.SetData(values.Length == 0 ? new T[1] : values); return result; } catch { result.Dispose(); throw; }
        }
        public void Dispose() { profile?.Dispose(); regionalProfiles?.Dispose(); tileProvenance?.Dispose(); }
    }
}
