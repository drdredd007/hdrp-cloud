using System;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    // Visible receivers are sampled on screen; occluders come from the entire canonical spherical field.
    // No camera-centred atlas, recenter thresholds or LOD mesh shadow casters.
    public sealed class PlanetPeriodicTerrainShadows : IDisposable
    {
        static readonly ProfilerMarker Marker=new ProfilerMarker("PlanetTerrain.ShadowUpdate");
        Texture2D heights; ComputeShader shader; uint4 identity;
        RenderTexture farSolar,nearSolar,farMask,nearMask;
        readonly RenderTargetIdentifier[] layerTargets=new RenderTargetIdentifier[2];
        Vector4 source,trace; Vector3 sun,worldSun,cameraFromCenter; Matrix4x4 inverseProjection;
        int steps,downsample; float strength,innerRadius; bool horizon;
        public bool Active {get;private set;}
        public RenderTexture Map=>farMask;
        public Texture2D HeightTexture=>heights;
        public int UpdateCount {get;private set;}
        public long RuntimeTextureBytes
        {
            get {long n=0;foreach(var t in new UnityEngine.Object[]{heights,farSolar,nearSolar,farMask,nearMask})if(t)n+=UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t);return n;}
        }
        public void Prepare(PlanetDefinition definition,Vector3 localSun,Vector3 worldSun,PlanetPeriodicSurfaceSettings settings,MaterialPropertyBlock properties)
        {
            using var timer=Marker.Auto();Active=false;properties.SetFloat("_PeriodicTerrainShadowEnabled",0);
            if(definition.GeneratorVersion!=4||!definition.PeriodicHeight.IsCreated||!settings||!settings.EnableTerrainShadows||settings.TerrainShadowStrength<=0||worldSun.sqrMagnitude<.5f||!SystemInfo.supportsComputeShaders)
            {Dispose();return;}
            ref var data=ref definition.PeriodicHeight.Value;
            if(!heights||!identity.Equals(data.Identity))
            {
                CoreUtils.Destroy(heights);
                heights=new Texture2D(data.Width,data.Height,TextureFormat.RFloat,false,true)
                    {name="Planet shadow canonical heights",wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear};
                var values=new float[data.Samples.Length];for(int i=0;i<values.Length;i++)values[i]=data.Samples[i];
                heights.SetPixelData(values,0);heights.Apply(false,true);identity=data.Identity;
            }
            if(!shader)shader=Resources.Load<ComputeShader>("PlanetPeriodicTerrainShadow");
            if(!shader)throw new InvalidOperationException("PlanetPeriodicTerrainShadow compute resource is missing.");
            source=new Vector4((float)definition.Radius,data.TileMetres,data.HeightScaleMetres,data.MaximumMetres);
            innerRadius=(float)(definition.Radius+data.MinimumMetres);
            horizon=settings.TerrainShadowRange==PlanetTerrainShadowRange.PlanetHorizon;
            trace=new Vector4(Mathf.Max(100,settings.TerrainShadowDistance),Mathf.Clamp(settings.TerrainShadowBias,.1f,10),
                Mathf.Tan(Mathf.Clamp(settings.TerrainShadowSunRadiusDegrees,0,1)*Mathf.Deg2Rad),Mathf.Max(1,data.MaximumSlope*2+.1f));
            sun=localSun.normalized;steps=Mathf.Clamp(settings.TerrainShadowSteps,32,256);
            this.worldSun=worldSun.normalized;
            downsample=settings.TerrainShadowDownsample<=1?1:settings.TerrainShadowDownsample<=2?2:4;
            strength=Mathf.Clamp01(settings.TerrainShadowStrength);Active=true;
            properties.SetFloat("_PeriodicTerrainShadowEnabled",1);properties.SetVector("_PeriodicShadowSunWorld",worldSun.normalized);
        }
        static void Release(ref RenderTexture t){if(t){t.Release();CoreUtils.Destroy(t);t=null;}}
        static void Ensure(ref RenderTexture t,int width,int height,RenderTextureFormat format,bool random,string name)
        {
            if(t&&t.width==width&&t.height==height&&t.format==format&&t.enableRandomWrite==random)return;
            Release(ref t);t=new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear)
                {name=name,enableRandomWrite=random,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};t.Create();
        }
        public void SetLayerTarget(CommandBuffer cmd,RenderTexture color,bool near)
        {
            if(!Active){cmd.SetRenderTarget(color);return;}
            if(near)Ensure(ref nearSolar,color.width,color.height,RenderTextureFormat.ARGBFloat,false,"Planet near direct sunlight");
            else Ensure(ref farSolar,color.width,color.height,RenderTextureFormat.ARGBFloat,false,"Planet far direct sunlight");
            layerTargets[0]=color;layerTargets[1]=near?nearSolar:farSolar;
            cmd.SetRenderTarget(layerTargets,color);
        }
        public void TraceLayer(CommandBuffer cmd,RenderTexture color,Matrix4x4 inverseViewProjection,Vector3 cameraFromCenter,Matrix4x4 worldToLocal,bool near)
        {
            if(!Active)return;
            cmd.SetRenderTarget(BuiltinRenderTextureType.None);
            if(!near){inverseProjection=inverseViewProjection;this.cameraFromCenter=cameraFromCenter;}
            int width=(color.width+downsample-1)/downsample,height=(color.height+downsample-1)/downsample;
            if(near)Ensure(ref nearMask,width,height,RenderTextureFormat.RFloat,true,"Planet near sun visibility");
            else Ensure(ref farMask,width,height,RenderTextureFormat.RFloat,true,"Planet far sun visibility");
            int kernel=shader.FindKernel("TraceScreen");
            cmd.SetComputeTextureParam(shader,kernel,"_ShadowHeights",heights);
            cmd.SetComputeTextureParam(shader,kernel,"_ShadowColor",color);
            cmd.SetComputeTextureParam(shader,kernel,"_ShadowMask",near?nearMask:farMask);
            cmd.SetComputeVectorParam(shader,"_ShadowSource",source);cmd.SetComputeVectorParam(shader,"_ShadowTrace",trace);
            cmd.SetComputeVectorParam(shader,"_ShadowSun",sun);cmd.SetComputeIntParam(shader,"_ShadowSteps",steps);
            cmd.SetComputeIntParam(shader,"_ShadowHorizon",horizon?1:0);cmd.SetComputeFloatParam(shader,"_ShadowInnerRadius",innerRadius);
            cmd.SetComputeVectorParam(shader,"_ShadowDimensions",new Vector4(color.width,color.height,width,height));
            cmd.SetComputeVectorParam(shader,"_ShadowCamera",cameraFromCenter);
            cmd.SetComputeMatrixParam(shader,"_ShadowInverseViewProjection",inverseViewProjection);cmd.SetComputeMatrixParam(shader,"_ShadowWorldToLocal",worldToLocal);
            cmd.BeginSample("PlanetTerrain.ShadowGenerate");cmd.DispatchCompute(shader,kernel,(width+7)/8,(height+7)/8,1);cmd.EndSample("PlanetTerrain.ShadowGenerate");UpdateCount++;
        }
        public void BindComposite(Material material,bool hasNear)
        {
            material.SetFloat("_PeriodicTerrainShadowEnabled",Active?1:0);
            if(!Active)return;
            material.SetFloat("_PeriodicTerrainShadowStrength",strength);
            material.SetTexture("_PeriodicFarSolar",farSolar);material.SetTexture("_PeriodicFarShadow",farMask);
            material.SetTexture("_PeriodicNearSolar",hasNear?nearSolar:farSolar);material.SetTexture("_PeriodicNearShadow",hasNear?nearMask:farMask);
            material.SetMatrix("_PeriodicShadowInverseProjection",inverseProjection);material.SetVector("_PeriodicShadowCamera",cameraFromCenter);
            material.SetVector("_PeriodicShadowSunWorld",worldSun);
            material.SetVector("_PeriodicShadowBody",new Vector4(innerRadius,source.x+source.w+trace.y,0,0));
        }
        public void Dispose(){Release(ref farSolar);Release(ref nearSolar);Release(ref farMask);Release(ref nearMask);CoreUtils.Destroy(heights);heights=null;Active=false;}
    }
}
