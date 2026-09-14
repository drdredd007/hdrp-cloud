using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PlanetVertex
    {
        public float3 Position,Normal;
        public float4 Color;
        public const int Stride=40;
    }

    public enum PlanetPatchLayout
    {
        // PlanetPatchMesh topology: 32×32 cube-face cells in 1:1000 far units plus radial skirts.
        Far,
        // PlanetLocalPatch topology: 32×32 metric cells in a surface frame.
        Local
    }

    // Slot storage for patch vertices. Caches decide which patches exist; a backend only stores and
    // fills slots, so cache lifecycle is testable without a graphics device.
    public interface IPlanetPatchBackend : IDisposable
    {
        PlanetPatchLayout Layout {get;}
        // Ensures capacity for at least 'slots'. Returns true when previously generated contents were discarded.
        bool Reserve(int slots);
        int Acquire();
        void Release(int slot);
        void ReleaseAll();
        void GenerateFar(CommandBuffer cmd,int slot,in PlanetDefinition definition,PlanetPatchKey key);
        void GenerateLocal(CommandBuffer cmd,int slot,in PlanetDefinition definition,in PlanetSurfaceFrame frame,int2 key,double size);
    }

    // GPU backend: one structured vertex buffer split into fixed slots plus one shared index buffer.
    // Generation is enqueued on the caller's command buffer and never read back.
    public sealed class PlanetGpuPatchBackend : IPlanetPatchBackend
    {
        public const string ResourceName="PlanetPatchGenerator";
        public const int Resolution=32,Row=Resolution+1;
        public static int VertexCount(PlanetPatchLayout layout)=>layout==PlanetPatchLayout.Far?Row*Row+4*Row:Row*Row;
        public static int IndexCount(PlanetPatchLayout layout)=>layout==PlanetPatchLayout.Far?Resolution*Resolution*6+4*Resolution*6:Resolution*Resolution*6;

        ComputeShader generator;
        int farKernel=-1,localKernel=-1;
        GraphicsBuffer vertices,indices;
        int capacity;
        readonly Stack<int> free=new Stack<int>();
        readonly HashSet<int> used=new HashSet<int>();

        public PlanetPatchLayout Layout {get;}
        public int SlotVertexCount=>VertexCount(Layout);
        public int PatchIndexCount=>IndexCount(Layout);
        public GraphicsBuffer Vertices=>vertices;
        public GraphicsBuffer Indices=>indices;
        public int Capacity=>capacity;
        // Optional serialized reference; otherwise the shader is loaded from this module's Resources.
        public ComputeShader Generator
        {
            get=>generator;
            set{if(value!=generator){generator=value;farKernel=localKernel=-1;}}
        }

        public PlanetGpuPatchBackend(PlanetPatchLayout layout,ComputeShader generator=null){Layout=layout;this.generator=generator;}

        public bool Reserve(int slots)
        {
            if(slots<=capacity && vertices!=null)return false;
            int next=math.max(slots,math.max(8,capacity*2));
            vertices?.Dispose();
            vertices=new GraphicsBuffer(GraphicsBuffer.Target.Structured,next*SlotVertexCount,PlanetVertex.Stride){name=$"Planet {Layout} patch vertices"};
            if(indices==null)indices=BuildIndices(Layout);
            capacity=next;free.Clear();used.Clear();
            for(int i=capacity-1;i>=0;i--)free.Push(i);
            return true;
        }
        public int Acquire()
        {
            if(free.Count==0)throw new InvalidOperationException("Planet patch backend is full; reserve capacity first.");
            int slot=free.Pop();used.Add(slot);return slot;
        }
        public void Release(int slot){if(used.Remove(slot))free.Push(slot);}
        public void ReleaseAll(){free.Clear();used.Clear();for(int i=capacity-1;i>=0;i--)free.Push(i);}

        public void GenerateFar(CommandBuffer cmd,int slot,in PlanetDefinition definition,PlanetPatchKey key)
        {
            var shader=Prepare(cmd,definition,slot,PlanetPatchLayout.Far,out int kernel);
            double span=math.PI/(2*(1<<key.Level));
            double skirt=math.max(10,definition.Relief*.1+definition.Radius*(1-math.cos(span/Resolution))*4);
            cmd.SetComputeIntParam(shader,"_PatchFace",key.Face);cmd.SetComputeIntParam(shader,"_PatchLevel",key.Level);
            cmd.SetComputeIntParam(shader,"_PatchX",key.X);cmd.SetComputeIntParam(shader,"_PatchY",key.Y);
            cmd.SetComputeFloatParam(shader,"_PatchSkirtDepth",(float)skirt);
            cmd.DispatchCompute(shader,kernel,(SlotVertexCount+63)/64,1,1);
        }
        public void GenerateLocal(CommandBuffer cmd,int slot,in PlanetDefinition definition,in PlanetSurfaceFrame frame,int2 key,double size)
        {
            var shader=Prepare(cmd,definition,slot,PlanetPatchLayout.Local,out int kernel);
            double radius=math.length(frame.Position);
            cmd.SetComputeVectorParam(shader,"_FrameRight",(Vector3)(float3)frame.Right);
            cmd.SetComputeVectorParam(shader,"_FrameUp",(Vector3)(float3)frame.Up);
            cmd.SetComputeVectorParam(shader,"_FrameForward",(Vector3)(float3)frame.Forward);
            cmd.SetComputeFloatParam(shader,"_FrameRadius",(float)radius);
            cmd.SetComputeFloatParam(shader,"_FrameHeight",(float)(radius-definition.Radius));
            cmd.SetComputeIntParam(shader,"_LocalKeyX",key.x);cmd.SetComputeIntParam(shader,"_LocalKeyZ",key.y);
            cmd.SetComputeFloatParam(shader,"_LocalCellSize",(float)(size/Resolution));
            cmd.DispatchCompute(shader,kernel,(SlotVertexCount+63)/64,1,1);
        }

        ComputeShader Prepare(CommandBuffer cmd,in PlanetDefinition definition,int slot,PlanetPatchLayout layout,out int kernel)
        {
            if(layout!=Layout)throw new InvalidOperationException($"This backend stores {Layout} patches.");
            if(vertices==null || !used.Contains(slot))throw new ArgumentOutOfRangeException(nameof(slot));
            if(!generator)generator=Resources.Load<ComputeShader>(ResourceName);
            if(!generator)throw new InvalidOperationException("Planet patch generator compute shader is unavailable.");
            if(farKernel<0){farKernel=generator.FindKernel("FarPatch");localKernel=generator.FindKernel("LocalPatch");}
            kernel=layout==PlanetPatchLayout.Far?farKernel:localKernel;
            cmd.SetComputeVectorParam(generator,"_PlanetSeedShift",(Vector3)(new float3(definition.Seed%101,definition.Seed%79,definition.Seed%67)*.137f));
            cmd.SetComputeFloatParam(generator,"_PlanetSeedDryness",definition.Seed*.01f);
            cmd.SetComputeFloatParam(generator,"_PlanetRadius",(float)definition.Radius);
            cmd.SetComputeFloatParam(generator,"_PlanetRelief",(float)definition.Relief);
            cmd.SetComputeIntParam(generator,"_PlanetResolution",Resolution);
            cmd.SetComputeIntParam(generator,"_PlanetBaseVertex",slot*SlotVertexCount);
            cmd.SetComputeIntParam(generator,"_PlanetVertexCount",SlotVertexCount);
            cmd.SetComputeBufferParam(generator,kernel,"_PlanetVertices",vertices);
            return generator;
        }

        public static int[] BuildIndexArray(PlanetPatchLayout layout)
        {
            var triangles=new int[IndexCount(layout)];int n=0;
            for(int y=0;y<Resolution;y++)for(int x=0;x<Resolution;x++)
            {
                int a=y*Row+x,b=a+1,c=a+Row,d=c+1;
                // Far: PlanetPatchMesh winding. Local: PlanetLocalPatch triangles (a,c,b),(b,c,d).
                if(layout==PlanetPatchLayout.Far){triangles[n++]=a;triangles[n++]=b;triangles[n++]=c;triangles[n++]=b;triangles[n++]=d;triangles[n++]=c;}
                else{triangles[n++]=a;triangles[n++]=c;triangles[n++]=b;triangles[n++]=b;triangles[n++]=c;triangles[n++]=d;}
            }
            if(layout==PlanetPatchLayout.Far)
                for(int edge=0;edge<4;edge++)for(int j=0;j<Resolution;j++)
                {
                    int top=edge==0?j:edge==1?j*Row+Resolution:edge==2?Resolution*Row+Resolution-j:(Resolution-j)*Row;
                    int bottom=Row*Row+edge*Row+j;
                    int next=edge==0?top+1:edge==1?top+Row:edge==2?top-1:top-Row;
                    triangles[n++]=top;triangles[n++]=bottom;triangles[n++]=next;
                    triangles[n++]=next;triangles[n++]=bottom;triangles[n++]=bottom+1;
                }
            return triangles;
        }
        static GraphicsBuffer BuildIndices(PlanetPatchLayout layout)
        {
            var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Index,IndexCount(layout),sizeof(int)){name=$"Planet {layout} patch indices"};
            buffer.SetData(BuildIndexArray(layout));return buffer;
        }
        public void Dispose()
        {
            vertices?.Dispose();indices?.Dispose();vertices=null;indices=null;capacity=0;free.Clear();used.Clear();
        }
    }
}
