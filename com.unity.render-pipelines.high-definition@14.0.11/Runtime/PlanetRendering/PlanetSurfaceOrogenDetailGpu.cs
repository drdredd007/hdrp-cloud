using System;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Immutable optional conditioning copy, owned by the parent surface GPU lease.</summary>
    internal sealed class PlanetSurfaceOrogenDetailGpu:IDisposable
    {
        readonly NativeWorldOrogenDetailView view;
        GraphicsBuffer geometry,flow,environment;bool disposed;
        internal static long EstimateBytes(in NativeWorldOrogenDetailView view)
        {
            if(view.Recipe.Enabled&&!view.IsValid)throw new ArgumentException("Enabled Orogen detail must have a complete valid captured view.");
            return checked((long)math.max(1,view.Enabled?view.Geometry.Length:0)*16*3);
        }
        internal PlanetSurfaceOrogenDetailGpu(in NativeWorldOrogenDetailView view)
        {
            EstimateBytes(view);this.view=view;
            try{geometry=Buffer(view.Enabled?Copy(view.Geometry):Array.Empty<float4>());flow=Buffer(view.Enabled?Copy(view.Flow):Array.Empty<float4>());environment=Buffer(view.Enabled?Copy(view.Environment):Array.Empty<float4>());}
            catch{Dispose();throw;}
        }
        internal void Bind(CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetSurfaceOrogenDetailGpu));
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceOrogenGeometry",geometry);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceOrogenFlow",flow);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceOrogenEnvironment",environment);
            cmd.SetComputeIntParam(shader,"_SurfaceOrogenEnabled",view.Enabled?1:0);cmd.SetComputeIntParam(shader,"_SurfaceOrogenResolution",view.Enabled?view.Resolution:0);cmd.SetComputeIntParam(shader,"_SurfaceOrogenSeed",view.Recipe.Seed);
            cmd.SetComputeIntParam(shader,"_SurfaceOrogenMorphology",view.Recipe.Morphology);
            Set(cmd,shader,"_SurfaceOrogenShape",PlanetSurfaceGpuData.Pair(view.Radius,view.SeaLevel));
            Set(cmd,shader,"_SurfaceOrogenPolicy",PlanetSurfaceGpuData.Pair(view.Recipe.MinimumWavelengthMetres,view.Recipe.Strength));
            Set(cmd,shader,"_SurfaceOrogenLimits",PlanetSurfaceGpuData.Pair(view.Recipe.CoastFadeMetres,view.MaximumAmplitude));
        }
        static void Set(CommandBuffer cmd,ComputeShader shader,string name,uint4 v)=>cmd.SetComputeIntParams(shader,name,unchecked((int)v.x),unchecked((int)v.y),unchecked((int)v.z),unchecked((int)v.w));
        static float4[] Copy(Unity.Collections.NativeArray<float4>.ReadOnly values){var r=new float4[values.Length];for(int i=0;i<r.Length;i++)r[i]=values[i];return r;}
        static GraphicsBuffer Buffer(float4[] values){var b=new GraphicsBuffer(GraphicsBuffer.Target.Structured,math.max(1,values.Length),16);try{b.SetData(values.Length==0?new float4[1]:values);return b;}catch{b.Dispose();throw;}}
        public void Dispose(){if(disposed)return;disposed=true;geometry?.Dispose();flow?.Dispose();environment?.Dispose();geometry=flow=environment=null;}
    }
}
