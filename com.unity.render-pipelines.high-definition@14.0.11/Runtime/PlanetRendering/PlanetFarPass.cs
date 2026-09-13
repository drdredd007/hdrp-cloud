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
        public Quaternion PlanetRotation=Quaternion.identity;
        public Vector3 LightDirection;
        public Color LightColor=Color.white;
        public float LightLux=50000;
        public bool Enabled=true;
        public int PatchCount=>patches.Count;
        public double Altitude=>math.length(CameraPosition-Definition.Center)-Definition.Radius;
        readonly List<Mesh> patches=new List<Mesh>();
        readonly List<double3> pivots=new List<double3>();
        Material surface,composite;
        MaterialPropertyBlock properties;
        RenderTexture farBuffer;
        PlanetDefinition generated;
        bool hasGenerated;

        protected override void Setup(ScriptableRenderContext context,CommandBuffer cmd)
        {
            if(!PlanetShader || !CompositeShader)throw new InvalidOperationException("Planet shaders must be serialized in the sample scene.");
            surface=CoreUtils.CreateEngineMaterial(PlanetShader);composite=CoreUtils.CreateEngineMaterial(CompositeShader);
            surface.SetInt("_FarZTest",(int)(SystemInfo.usesReversedZBuffer?CompareFunction.GreaterEqual:CompareFunction.LessEqual));
            properties=new MaterialPropertyBlock();
        }
        void Generate()
        {
            ReleaseMeshes();
            const int level=2,resolution=32,count=(resolution+1)*(resolution+1);
            var indices=new int[resolution*resolution*6];
            int n=0;
            for(int y=0;y<resolution;y++)for(int x=0;x<resolution;x++)
            {
                int a=y*(resolution+1)+x,b=a+1,c=a+resolution+1,d=c+1;
                indices[n++]=a;indices[n++]=b;indices[n++]=c;indices[n++]=b;indices[n++]=d;indices[n++]=c;
            }
            using(var positions=new NativeArray<float3>(count,Allocator.TempJob))
            using(var normals=new NativeArray<float3>(count,Allocator.TempJob))
            using(var colors=new NativeArray<float4>(count,Allocator.TempJob))
            {
                var vertices=new Vector3[count];var normalArray=new Vector3[count];var colorArray=new Color[count];
                for(int face=0;face<6;face++)for(int y=0;y<4;y++)for(int x=0;x<4;x++)
                {
                    var key=new PlanetPatchKey(face,level,x,y);
                    double3 pivot=PlanetField.Direction(key,.5,.5)*Definition.Radius;
                    new PlanetPatchJob {Definition=Definition,Key=key,Resolution=resolution,Pivot=pivot,
                        Positions=positions,Normals=normals,Colors=colors}.Schedule(count,64).Complete();
                    for(int i=0;i<count;i++){vertices[i]=positions[i];normalArray[i]=normals[i];colorArray[i]=new Color(colors[i].x,colors[i].y,colors[i].z,1);}
                    var mesh=new Mesh {name=$"Planet {Definition.Id} {face}/{level}/{x}/{y}"};
                    mesh.vertices=vertices;mesh.normals=normalArray;mesh.colors=colorArray;mesh.triangles=indices;mesh.RecalculateBounds();mesh.UploadMeshData(true);
                    patches.Add(mesh);pivots.Add(pivot);
                }
            }
            generated=Definition;hasGenerated=true;
        }
        protected override void Execute(CustomPassContext ctx)
        {
            if(!Enabled || ctx.hdCamera.camera!=Observer || !Definition.IsValid || Altitude<10000 || Observer.orthographic)return;
            if(!hasGenerated || generated.Id!=Definition.Id || generated.Seed!=Definition.Seed || generated.Radius!=Definition.Radius ||
                generated.Relief!=Definition.Relief || generated.GeneratorVersion!=Definition.GeneratorVersion)Generate();
            int width=ctx.hdCamera.actualWidth,height=ctx.hdCamera.actualHeight;
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
            ctx.cmd.SetRenderTarget(farBuffer);ctx.cmd.SetViewport(new Rect(0,0,width,height));
            ctx.cmd.ClearRenderTarget(true,true,Color.clear,SystemInfo.usesReversedZBuffer?0:1);
            for(int i=0;i<patches.Count;i++)
            {
                var rotation=(double4)((quaternion)PlanetRotation).value;
                var relative=PlanetField.RelativeScaled(Definition.Center,CameraPosition,PlanetField.Rotate(rotation,pivots[i]));
                properties.SetVector("_PatchOffset",new Vector4((float)relative.x,(float)relative.y,(float)relative.z,0));
                ctx.cmd.DrawMesh(patches[i],Matrix4x4.identity,surface,0,0,properties);
            }
            composite.SetTexture("_PlanetFarBuffer",farBuffer);
            CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);
            ctx.cmd.SetViewport(new Rect(0,0,width,height));
            CoreUtils.DrawFullScreen(ctx.cmd,composite);
        }
        void ReleaseBuffer(){if(farBuffer){farBuffer.Release();CoreUtils.Destroy(farBuffer);farBuffer=null;}}
        void ReleaseMeshes(){foreach(var mesh in patches)CoreUtils.Destroy(mesh);patches.Clear();pivots.Clear();hasGenerated=false;}
        protected override void Cleanup(){ReleaseMeshes();ReleaseBuffer();CoreUtils.Destroy(surface);CoreUtils.Destroy(composite);}
    }
}
