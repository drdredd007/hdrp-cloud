using System;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Bounded GPU copy of persisted structure. Pooled by its parent immutable surface descriptor.</summary>
    internal sealed class PlanetSurfaceStructuralGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Province { public uint4 CenterXY, CenterZ, Buoyancy; public int4 Flags; }
        [StructLayout(LayoutKind.Sequential)] struct Edge { public uint4 StartXY, StartZ, EndXY, EndZ, NormalXY, NormalZ, Convergence; public int4 Flags; }
        readonly NativeSurfaceStructuralView view;
        GraphicsBuffer provinces, edges, raw, bins, references;
        PlanetSurfaceDrainageGpu drainage;
        PlanetSurfaceLandformGpu landform;
        PlanetSurfaceLandformFilterGpu neutralLandformFilter;
        internal static long EstimateBytes(in NativeSurfaceStructuralView view) => checked((long)math.max(1, view.Provinces.Length) * 64 +
            (long)math.max(1, view.Boundaries.Length) * 128 + (long)math.max(1, view.RawMacro.Length) * 4 +
            (long)math.max(1, view.Bins.Length) * 8 + (long)math.max(1, view.References.Length) * 4+PlanetSurfaceDrainageGpu.EstimateBytes(view.DrainageField)+
            PlanetSurfaceLandformGpu.EstimateBytes(view.LandformField)+PlanetSurfaceLandformFilterGpu.EstimateBytes(default));
        internal PlanetSurfaceStructuralGpu(in NativeSurfaceStructuralView view)
        {
            this.view = view;
            try
            {
                drainage=new PlanetSurfaceDrainageGpu(view.DrainageField);
                landform=PlanetSurfaceLandformGpu.Acquire(view.LandformField);
                neutralLandformFilter=new PlanetSurfaceLandformFilterGpu(default);
                var p = new Province[view.Provinces.Length];
                for (int i = 0; i < p.Length; i++) { var value = view.Provinces[i]; p[i] = new Province { CenterXY = XY(value.Center), CenterZ = Z(value.Center), Buoyancy = PlanetSurfaceGpuData.Pair(value.Buoyancy, 0), Flags = new int4(value.Continental ? 1 : 0, 0, 0, 0) }; }
                var e = new Edge[view.Boundaries.Length];
                for (int i = 0; i < e.Length; i++) { var value = view.Boundaries[i]; e[i] = new Edge { StartXY = XY(value.Start), StartZ = Z(value.Start), EndXY = XY(value.End), EndZ = Z(value.End),
                    NormalXY = XY(value.Normal), NormalZ = Z(value.Normal), Convergence = PlanetSurfaceGpuData.Pair(value.Convergence, 0), Flags = new int4(value.ContinentalA ? 1 : 0, value.ContinentalB ? 1 : 0, 0, 0) }; }
                var r = new float[view.RawMacro.Length]; for (int i = 0; i < r.Length; i++) r[i] = view.RawMacro[i];
                var b = new int2[view.Bins.Length]; for (int i = 0; i < b.Length; i++) b[i] = view.Bins[i];
                var indices = new int[view.References.Length]; for (int i = 0; i < indices.Length; i++) indices[i] = view.References[i];
                provinces = Buffer(p); edges = Buffer(e); raw = Buffer(r); bins = Buffer(b); references = Buffer(indices);
            }
            catch { Dispose(); throw; }
        }
        internal void Bind(CommandBuffer cmd, ComputeShader shader, int kernel, PlanetSurfaceGpuFamily family=PlanetSurfaceGpuFamily.Complete)
        {
            if(family==PlanetSurfaceGpuFamily.Complete||family==PlanetSurfaceGpuFamily.Drainage)drainage.Bind(cmd,shader,kernel);
            if(family==PlanetSurfaceGpuFamily.Complete||family==PlanetSurfaceGpuFamily.Landform)
            {landform.Bind(cmd,shader,kernel);neutralLandformFilter.Bind(cmd,shader,kernel);}
            if(family!=PlanetSurfaceGpuFamily.Landform)
            {
                cmd.SetComputeBufferParam(shader, kernel, "_SurfaceStructureProvinces", provinces); cmd.SetComputeBufferParam(shader, kernel, "_SurfaceStructureEdges", edges);
                cmd.SetComputeBufferParam(shader, kernel, "_SurfaceStructureBins", bins); cmd.SetComputeBufferParam(shader, kernel, "_SurfaceStructureReferences", references);
            }
            cmd.SetComputeBufferParam(shader, kernel, "_SurfaceStructureRawMacro", raw);
            cmd.SetComputeIntParams(shader, "_SurfaceStructureCounts", view.Enabled ? view.MorphologyVersion : 0, view.RawMacroResolution, view.Provinces.Length, view.SourceRecipe.Seed);
            Set(cmd, shader, "_SurfaceStructureBounds", PlanetSurfaceGpuData.Pair(view.SourceRecipe.MinimumHeight, view.SourceRecipe.MaximumHeight));
            Set(cmd, shader, "_SurfaceStructureSeaShelf", PlanetSurfaceGpuData.Pair(view.SourceRecipe.SeaLevel, view.ShelfWidthMetres));
            Set(cmd, shader, "_SurfaceStructureBeltFeature", PlanetSurfaceGpuData.Pair(view.BeltWidthMetres, view.FeatureScaleMetres));
            Set(cmd, shader, "_SurfaceStructureCoastMountain", PlanetSurfaceGpuData.Pair(view.CoastThresholdMetres, view.MountainFraction));
        }
        static uint4 XY(double3 v) => PlanetSurfaceGpuData.Pair(v.x, v.y);
        static uint4 Z(double3 v) => PlanetSurfaceGpuData.Pair(v.z, 0);
        static void Set(CommandBuffer cmd, ComputeShader shader, string name, uint4 value) => cmd.SetComputeIntParams(shader, name, unchecked((int)value.x), unchecked((int)value.y), unchecked((int)value.z), unchecked((int)value.w));
        static GraphicsBuffer Buffer<T>(T[] values) where T : struct
        {
            var result = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, values.Length), Marshal.SizeOf<T>());
            try { result.SetData(values.Length == 0 ? new T[1] : values); return result; } catch { result.Dispose(); throw; }
        }
        public void Dispose() { provinces?.Dispose(); edges?.Dispose(); raw?.Dispose(); bins?.Dispose(); references?.Dispose();drainage?.Dispose();PlanetSurfaceLandformGpu.Release(landform);landform=null;neutralLandformFilter?.Dispose(); }
    }
}
