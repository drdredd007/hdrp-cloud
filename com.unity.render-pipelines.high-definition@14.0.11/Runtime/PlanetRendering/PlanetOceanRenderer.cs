using System;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>One observer's analytic opaque sea. It never changes terrain, colliders, scatter or physical revisions.</summary>
    public sealed class PlanetOceanRenderer:IDisposable
    {
        Material material;
        readonly MaterialPropertyBlock properties=new MaterialPropertyBlock();
        RenderTexture buffer;
        PlanetSurfaceDescriptor descriptor;
        SurfaceRecipe recipe;
        bool resolved;
        public RenderTexture Buffer=>buffer;
        public bool RenderedThisFrame {get;private set;}
        public double SeaLevel=>resolved?recipe.SeaLevel:double.NaN;
        public string Status {get;private set;}="Static sea is not submitted";
        public bool WantsRender(PlanetFarPass owner)
        {
            RenderedThisFrame=false;
            // The source's base map already contains ocean colours and bathymetry.
            if(owner.BaseMapColour){Status="World Orogen base map owns the ocean appearance";return false;}
            var settings=owner.OceanSettings.Resolved;
            if(owner.Definition.GeneratorVersion!=3||!settings.Enabled){Status="Static sea is disabled or legacy terrain owns its appearance";return false;}
            if(!settings.IsValid){Status="Invalid static sea material settings";return false;}
            if(!resolved||!descriptor.Equals(owner.Definition.Surface))
            {
                resolved=false;descriptor=owner.Definition.Surface;
                if(!PlanetSurfaceDataRegistry.TryAcquire(descriptor,out var lease)){Status="Static sea waits for the registered signed recipe";return false;}
                using(lease)recipe=lease.View.Recipe;
                resolved=true;
            }
            if(recipe.Radius!=owner.Definition.Radius||!PlanetOceanMath.TryRadius(recipe,out double radius))
            {Status="This signed recipe has no EarthLike sea";return false;}
            if(math.length(owner.CameraPosition-owner.Definition.Center)<=radius)
            {Status="Underwater rendering is outside the static opaque sea scope";return false;}
            return true;
        }
        public void Render(CustomPassContext ctx,PlanetFarPass owner,PlanetLayerDepth depth)
        {
            if(ctx.hdCamera.camera!=owner.Observer||!WantsRender(owner))return;
            if(depth==null)throw new InvalidOperationException("Static sea requires the observer's composed metric depth.");
            if(!material)
            {
                var shader=Resources.Load<Shader>("PlanetOcean");
                if(!shader||!shader.isSupported){Status="Static sea shader is unavailable";return;}
                material=CoreUtils.CreateEngineMaterial(shader);
            }
            int width=ctx.hdCamera.actualWidth,height=ctx.hdCamera.actualHeight;
            if(!buffer||buffer.width!=width||buffer.height!=height)
            {
                ReleaseBuffer();buffer=new RenderTexture(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)
                {name="Static planet sea color + metric endpoint",filterMode=FilterMode.Point};buffer.Create();
            }
            double radius=recipe.Radius+recipe.SeaLevel;
            var origin=owner.CameraPosition-owner.Definition.Center;double altitude=math.length(origin)-radius;
            properties.Clear();SetDoubleVector("_OceanOrigin",origin);
            properties.SetVector("_OceanDimensionsHigh",new Vector4((float)radius,(float)altitude,0,0));
            properties.SetVector("_OceanDimensionsLow",new Vector4((float)(radius-(double)(float)radius),(float)(altitude-(double)(float)altitude),0,0));
            var settings=owner.OceanSettings.Resolved;
            // SetColor converts sRGB to linear itself; these shader values are already linear.
            properties.SetVector("_OceanAlbedo",LinearVector(settings.Albedo));properties.SetFloat("_OceanSmoothness",settings.Smoothness);
            var lightDirection=math.normalizesafe((float3)owner.LightDirection,new float3(0,1,0));
            properties.SetVector("_OceanLightDirection",new Vector4(lightDirection.x,lightDirection.y,lightDirection.z,0));properties.SetVector("_OceanLightColor",LinearVector(owner.LightColor));
            properties.SetFloat("_OceanLightLux",math.isfinite(owner.LightLux)?math.max(0,owner.LightLux):0);properties.SetInt("_OceanUseSceneLights",owner.UseSceneLights?1:0);
            var q=(quaternion)owner.CelestialLightRotation;
            q=math.all(math.isfinite(q.value))&&math.lengthsq(q.value)>1e-12f?math.normalize(q):quaternion.identity;
            properties.SetVector("_OceanLightRotation",new Vector4(q.value.x,q.value.y,q.value.z,q.value.w));
            properties.SetFloat("_OceanOwnAir",owner.Atmosphere.IsValid?1:0);
            if(owner.Atmosphere.IsValid)
            {
                var air=owner.Atmosphere;float h=PlanetMediaMath.ScaleHeight(air.AirMaximumAltitude),ah=PlanetMediaMath.ScaleHeight(air.AerosolMaximumAltitude);
                properties.SetVector("_OceanOwnAirExtinction",new Vector4(PlanetMediaMath.Extinction(air.AirOpacity.r,h),PlanetMediaMath.Extinction(air.AirOpacity.g,h),PlanetMediaMath.Extinction(air.AirOpacity.b,h),h));
                properties.SetVector("_OceanOwnAerosol",new Vector4(PlanetMediaMath.Extinction(air.AerosolOpacity,ah),ah,0,0));
                properties.SetVector("_OceanOwnDimensions",new Vector4((float)recipe.Radius,air.Depth,0,0));
            }
            ctx.cmd.SetRenderTarget(buffer);ctx.cmd.SetViewport(new Rect(0,0,width,height));
            CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:0);
            material.SetTexture("_OceanBuffer",buffer);material.SetTexture("_PlanetAccumulatedDepth",depth.Current);
            material.SetFloat("_OceanLayerWeight",owner.LayerWeight);
            CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);ctx.cmd.SetViewport(new Rect(0,0,width,height));
            CoreUtils.DrawFullScreen(ctx.cmd,material,shaderPassId:2);
            depth.Merge(ctx,material);RenderedThisFrame=true;Status="Opaque static sea submitted at recipe SeaLevel";
        }
        void SetDoubleVector(string name,double3 value)
        {var high=(float3)value;var low=(float3)(value-(double3)high);properties.SetVector(name+"High",new Vector4(high.x,high.y,high.z,0));properties.SetVector(name+"Low",new Vector4(low.x,low.y,low.z,0));}
        static Vector4 LinearVector(Color color)
        {var value=color.linear;return new Vector4(value.r,value.g,value.b,value.a);}
        void ReleaseBuffer(){if(buffer){buffer.Release();CoreUtils.Destroy(buffer);buffer=null;}}
        public void Dispose(){ReleaseBuffer();CoreUtils.Destroy(material);material=null;resolved=false;RenderedThisFrame=false;}
    }
}
