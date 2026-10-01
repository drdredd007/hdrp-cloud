using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Universe-oriented radiance background. Air, clouds and fog are composed afterwards.</summary>
    public sealed class PlanetStarSkyPass : CustomPass
    {
        public Camera Observer;
        public Cubemap Cubemap;
        [Min(0)] public float Intensity=1;
        // Maps cubemap-local directions into the coordinate camera's observer axes.
        public Quaternion Rotation=Quaternion.identity;
        public bool Enabled=true,ShowSun=true;
        public bool UseSceneLights;
        public Quaternion CelestialLightRotation=Quaternion.identity;
        public Vector3 SunDirection=new Vector3(.4f,.7f,-.58f);
        public Color SunColor=Color.white;
        [Min(0)] public float SunLux=120000;
        [Range(.001f,10)] public float SunAngularDiameter=.53f;
        Material material;
        MaterialPropertyBlock properties;
        public static float SunRadiance(float lux,float diameterDegrees)
        {
            if(!math.isfinite(lux) || lux<=0 || !math.isfinite(diameterDegrees) || diameterDegrees<=0)return 0;
            double radius=math.clamp(diameterDegrees,.001f,10)*System.Math.PI/360;
            return (float)System.Math.Min(float.MaxValue,lux/(System.Math.PI*System.Math.Sin(radius)*System.Math.Sin(radius)));
        }
        protected override void Setup(ScriptableRenderContext context,CommandBuffer cmd)
        {
            material=CoreUtils.CreateEngineMaterial(Resources.Load<Shader>("PlanetStarSky"));
            properties=new MaterialPropertyBlock();
        }
        protected override void Execute(CustomPassContext ctx)
        {
            if(!Enabled || ctx.hdCamera.camera!=Observer || !material || !math.isfinite(Intensity) || Intensity<0)return;
            var q=(quaternion)Rotation;
            if(!math.all(math.isfinite(q.value)) || math.lengthsq(q.value)<1e-12f)return;
            q=math.normalize(q);
            properties.Clear();properties.SetTexture("_UniverseStars",Cubemap?Cubemap:CoreUtils.blackCubeTexture);
            properties.SetFloat("_UniverseStarIntensity",Intensity);
            properties.SetVector("_UniverseSkyRotation",new Vector4(-q.value.x,-q.value.y,-q.value.z,q.value.w));
            var lightRotation=(quaternion)CelestialLightRotation;
            bool validLights=math.all(math.isfinite(lightRotation.value))&&math.lengthsq(lightRotation.value)>1e-12f;
            lightRotation=validLights?math.normalize(lightRotation):quaternion.identity;
            var pipeline=RenderPipelineManager.currentPipeline as HDRenderPipeline;
            bool rawLights=ShowSun&&UseSceneLights&&validLights&&pipeline!=null&&pipeline.BindPlanetCelestialLights(ctx.cmd,Observer);
            properties.SetInt("_UniverseUseSceneLights",rawLights?1:0);
            properties.SetVector("_UniverseLightRotation",new Vector4(lightRotation.value.x,lightRotation.value.y,lightRotation.value.z,lightRotation.value.w));
            var direction=math.normalizesafe((float3)SunDirection,new float3(0,1,0));
            properties.SetVector("_UniverseSunDirection",new Vector4(direction.x,direction.y,direction.z,0));
            float radiance=ShowSun?SunRadiance(SunLux,SunAngularDiameter):0;
            var color=SunColor.linear;
            if(!math.all(math.isfinite(new float3(color.r,color.g,color.b))))color=Color.white;
            properties.SetVector("_UniverseSunRadiance",new Vector4(math.max(0,color.r)*radiance,math.max(0,color.g)*radiance,math.max(0,color.b)*radiance,0));
            float diameter=math.isfinite(SunAngularDiameter)&&SunAngularDiameter>0?Mathf.Clamp(SunAngularDiameter,.001f,10):.53f;
            properties.SetFloat("_UniverseSunCosRadius",Mathf.Cos(diameter*Mathf.Deg2Rad*.5f));
            CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);
            ctx.cmd.SetViewport(new Rect(0,0,ctx.hdCamera.actualWidth,ctx.hdCamera.actualHeight));
            CoreUtils.DrawFullScreen(ctx.cmd,material,properties);
        }
        protected override void Cleanup(){CoreUtils.Destroy(material);material=null;properties=null;}
    }
}
