using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEngine.Rendering.HighDefinition
{
    // Owned by one observer. Never installs an enormous float Transform into the scene.
    [Serializable]
    public sealed class PlanetFarPass : CustomPass
    {
        public Camera Observer;
        public Shader PlanetShader,CompositeShader;
        public PlanetDefinition Definition;
        public double3 CameraPosition;
        public double3? LocalSurfaceTarget;
        public Quaternion PlanetRotation=Quaternion.identity;
        public Vector3 LightDirection;
        public Color LightColor=Color.white;
        public float LightLux=50000;
        public bool Enabled=true;
        // Aerial perspective and sun transmittance from HDRP's PhysicallyBasedSky, applied only while the
        // camera's resolved sky describes this planet (the application configures it, see PlanetAtmosphere).
        public bool EnableAtmosphere=true;
        // Light the surface with HDRP directional lights when the camera has any, instead of LightDirection/LightLux.
        public bool UseSceneLights=true;
        public bool AtmosphereActive {get;private set;}
        public int PatchCount=>geometry.Active.Count;
        public double Altitude=>math.length(CameraPosition-Definition.Center)-Definition.Radius;
        readonly PlanetSurfaceCache geometry=new PlanetSurfaceCache();
        public PlanetLodSettings LodSettings=PlanetLodSettings.Default;
        public bool IsRefining=>geometry.IsRefining;
        public bool EnableLocalSurface;
        public int LocalSurfacePatchCount=>nearGeometry.Meshes.Count;
        public bool HasLocalSurfaceAt(double3 position)
        {
            var q=(double4)((quaternion)PlanetRotation).value;
            return EnableLocalSurface && nearGeometry.Covers(Definition,PlanetField.Rotate(new double4(-q.xyz,q.w),position-Definition.Center),128);
        }
        readonly PlanetNearSurfaceCache nearGeometry=new PlanetNearSurfaceCache();
        Material surface,composite;
        MaterialPropertyBlock properties;
        RenderTexture farBuffer;
        RenderTexture nearBuffer;



        protected override void Setup(ScriptableRenderContext context,CommandBuffer cmd)
        {
            if(!PlanetShader || !CompositeShader)throw new InvalidOperationException("Planet shaders must be serialized in the sample scene.");
            surface=CoreUtils.CreateEngineMaterial(PlanetShader);composite=CoreUtils.CreateEngineMaterial(CompositeShader);
            surface.SetInt("_FarZTest",(int)(SystemInfo.usesReversedZBuffer?CompareFunction.GreaterEqual:CompareFunction.LessEqual));
            properties=new MaterialPropertyBlock();
        }
        protected override void Execute(CustomPassContext ctx)
        {
            if(!EnableLocalSurface){nearGeometry.Dispose();ReleaseNearBuffer();}
            if(ctx.hdCamera.camera==Observer)AtmosphereActive=false;
            if(!Enabled || ctx.hdCamera.camera!=Observer || !Definition.IsValid || (Altitude<10000 && !EnableLocalSurface) || Observer.orthographic)return;
            AtmosphereActive=EnableAtmosphere && PlanetAtmosphere.Matches(ctx.hdCamera,Definition,CameraPosition);
            var centerRelative=(float3)(Definition.Center-CameraPosition);
            int width=ctx.hdCamera.actualWidth,height=ctx.hdCamera.actualHeight;
            var q=(double4)((quaternion)PlanetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),CameraPosition-Definition.Center);
            geometry.Update(Definition,localCamera,height,Observer.fieldOfView,LodSettings);
            var nearTarget=LocalSurfaceTarget ?? CameraPosition;
            if(EnableLocalSurface && math.length(nearTarget-Definition.Center)-Definition.Radius<20000)
                nearGeometry.Update(Definition,PlanetField.Rotate(new double4(-q.xyz,q.w),nearTarget-Definition.Center));
            else if(Altitude>30000){nearGeometry.Dispose();ReleaseNearBuffer();}
            if(!farBuffer || farBuffer.width!=width || farBuffer.height!=height)
            {
                ReleaseBuffer();farBuffer=new RenderTexture(width,height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)
                {name="Planet far color + ray distance in metres",filterMode=FilterMode.Point};farBuffer.Create();
            }
            // The scaled layer uses its own projection/depth. Its alpha stores unscaled ray distance.
            var projection=GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(Observer.fieldOfView,(float)width/height,.001f,30000),true);
            var view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.Rotate(Quaternion.Inverse(Observer.transform.rotation));
            surface.SetMatrix("_FarViewProjection",projection*view);surface.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(PlanetRotation));
            surface.SetVector("_PlanetLightDirection",LightDirection);surface.SetColor("_PlanetLightColor",LightColor);surface.SetFloat("_PlanetLightLux",LightLux);
            properties.SetVector("_PlanetCenterRelative",(Vector3)centerRelative);
            properties.SetFloat("_PlanetAtmosphere",AtmosphereActive?1:0);
            properties.SetFloat("_PlanetUseSceneLights",UseSceneLights?1:0);
            ctx.cmd.SetRenderTarget(farBuffer);ctx.cmd.SetViewport(new Rect(0,0,width,height));
            ctx.cmd.ClearRenderTarget(true,true,Color.clear,SystemInfo.usesReversedZBuffer?0:1);
            for(int i=0;i<geometry.Active.Count;i++)
            {
                var rotation=(double4)((quaternion)PlanetRotation).value;
                var relative=PlanetField.RelativeScaled(Definition.Center,CameraPosition,PlanetField.Rotate(rotation,geometry.Get(geometry.Active[i]).Pivot));
                properties.SetVector("_PatchOffset",new Vector4((float)relative.x,(float)relative.y,(float)relative.z,0));
                properties.SetFloat("_LayerToMeters",1000);
                properties.SetMatrix("_FarViewProjection",projection*view);
                properties.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(PlanetRotation));
                ctx.cmd.DrawMesh(geometry.Get(geometry.Active[i]).Mesh,Matrix4x4.identity,surface,0,0,properties);
            }
            bool hasNear=EnableLocalSurface && nearGeometry.Meshes.Count>0 && Altitude<20000;
            if(hasNear)
            {
                if(!nearBuffer || nearBuffer.width!=width || nearBuffer.height!=height)
                {
                    ReleaseNearBuffer();nearBuffer=new RenderTexture(width,height,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)
                    {name="Planet local surface color + metric ray distance",filterMode=FilterMode.Point};nearBuffer.Create();
                }
                ctx.cmd.SetRenderTarget(nearBuffer);ctx.cmd.SetViewport(new Rect(0,0,width,height));
                ctx.cmd.ClearRenderTarget(true,true,Color.clear,SystemInfo.usesReversedZBuffer?0:1);
                var frame=nearGeometry.Frame;
                var relative=(Definition.Center-CameraPosition)+PlanetField.Rotate(q,frame.Position);
                var localRotation=PlanetRotation*(Quaternion)new quaternion((float4)frame.Rotation);
                properties.SetVector("_PatchOffset",new Vector4((float)relative.x,(float)relative.y,(float)relative.z,0));
                properties.SetFloat("_LayerToMeters",1);
                properties.SetMatrix("_FarViewProjection",GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(Observer.fieldOfView,(float)width/height,.05f,10000),true)*view);
                properties.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(localRotation));
                foreach(var mesh in nearGeometry.Meshes)ctx.cmd.DrawMesh(mesh,Matrix4x4.identity,surface,0,0,properties);
            }
            composite.SetTexture("_PlanetFarBuffer",farBuffer);
            composite.SetFloat("_PlanetHasNear",hasNear?1:0);
            composite.SetFloat("_PlanetAtmosphere",AtmosphereActive?1:0);
            if(hasNear)composite.SetTexture("_PlanetNearBuffer",nearBuffer);
            CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);
            ctx.cmd.SetViewport(new Rect(0,0,width,height));
            CoreUtils.DrawFullScreen(ctx.cmd,composite);
        }
        void ReleaseBuffer(){if(farBuffer){farBuffer.Release();CoreUtils.Destroy(farBuffer);farBuffer=null;}}
        void ReleaseNearBuffer(){if(nearBuffer){nearBuffer.Release();CoreUtils.Destroy(nearBuffer);nearBuffer=null;}}

        protected override void Cleanup(){geometry.Dispose();nearGeometry.Dispose();ReleaseBuffer();ReleaseNearBuffer();CoreUtils.Destroy(surface);CoreUtils.Destroy(composite);}
    }
}
