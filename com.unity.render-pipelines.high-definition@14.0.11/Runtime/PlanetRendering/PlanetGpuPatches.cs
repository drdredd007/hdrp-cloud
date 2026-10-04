using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct PlanetSurfaceVertexAttributes
    {
        public float4 MaterialWeights,ErosionData;
        public uint Channels;
        public const int Stride=36;
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
        public const string OrogenResourceName="PlanetOrogenPatchGenerator";
        public const int Resolution=32,Row=Resolution+1;
        public static int VertexCount(PlanetPatchLayout layout)=>layout==PlanetPatchLayout.Far?Row*Row+4*Row:Row*Row;
        public static int IndexCount(PlanetPatchLayout layout)=>layout==PlanetPatchLayout.Far?Resolution*Resolution*6+4*Resolution*6:Resolution*Resolution*6;

        ComputeShader generator,requestedGenerator;
        ComputeShader builtinEntry;
        PlanetSurfaceGpuFamily selectedFamily=PlanetSurfaceGpuFamily.Complete;
        int farKernel=-1,localKernel=-1,nativeFarKernel=-1,nativeLocalKernel=-1,filteredFarKernel=-1;
        readonly Dictionary<int,int> groupSizes=new Dictionary<int,int>();
        PlanetSurfaceGpuData surfaceData;
        GraphicsBuffer vertices,indices,attributes,parentVertices,parentAttributes;
        int capacity;
        readonly Stack<int> free=new Stack<int>();
        readonly HashSet<int> used=new HashSet<int>();

        public PlanetPatchLayout Layout {get;}
        public int SlotVertexCount=>VertexCount(Layout);
        public int PatchIndexCount=>IndexCount(Layout);
        public GraphicsBuffer Vertices=>vertices;
        public GraphicsBuffer Indices=>indices;
        // Populated only by canonical version-3 generation; legacy backends allocate no attribute buffer.
        public GraphicsBuffer Attributes=>attributes;
        public GraphicsBuffer ParentVertices=>parentVertices;
        public GraphicsBuffer ParentAttributes=>parentAttributes;
        public ComputeShader ActiveGenerator=>generator;
        // Per-backend allocations only; immutable SurfaceGpuData is shared and accounted once per retained descriptor.
        public long EstimatedGeometryBytes=>checked(BufferBytes(vertices)+BufferBytes(indices)+BufferBytes(attributes)+BufferBytes(parentVertices)+BufferBytes(parentAttributes));
        static long BufferBytes(GraphicsBuffer buffer)=>buffer==null?0:checked((long)buffer.count*buffer.stride);
        // Physical/foundation callers retain Full. Rendering opts into explicit metric band support.
        public bool FilterRenderingDetail {get;set;}
        public SurfaceRegionFilterStatus FilteringStatus {get;private set;}
        public int FilteringGeneration=>surfaceData?.FilterGeneration??0;
        public bool PrepareRegionalFiltering(in PlanetDefinition definition)
        {
            if(definition.GeneratorVersion!=3){FilteringStatus=SurfaceRegionFilterStatus.Ready;return true;}
            if(surfaceData==null||surfaceData.IsDisposed||!surfaceData.Key.Equals(definition.Surface))
            {var next=PlanetSurfaceGpuData.AcquireForFiltering(definition);var previous=surfaceData;surfaceData=next;PlanetSurfaceGpuData.Release(previous);}
            surfaceData.ValidateDefinition(definition);
            bool ready=surfaceData.PrepareFiltering(out var status);FilteringStatus=status;return ready;
        }
        public static double RenderFootprint(in PlanetDefinition definition,PlanetPatchKey key)
            =>2*(definition.Radius+definition.Relief)/((1<<key.Level)*(double)Resolution);
        public int Capacity=>capacity;
        // Optional serialized reference; otherwise the shader is loaded from this module's Resources.
        public ComputeShader Generator
        {
            get=>requestedGenerator;
            set{if(value!=requestedGenerator){requestedGenerator=value;SelectGenerator(value);}}
        }

        public PlanetGpuPatchBackend(PlanetPatchLayout layout,ComputeShader generator=null){Layout=layout;this.generator=this.requestedGenerator=generator;}

        public bool Reserve(int slots)
        {
            if(slots<=capacity && vertices!=null)return false;
            int next=math.max(slots,math.max(8,capacity*2));
            vertices?.Dispose();
            parentVertices?.Dispose();parentVertices=null;
            parentAttributes?.Dispose();parentAttributes=null;
            attributes?.Dispose();attributes=null;
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
            double skirt=PlanetLodSelector.RenderedSkirtDepthMetres(definition,key,Resolution);
            cmd.SetComputeIntParam(shader,"_PatchFace",key.Face);cmd.SetComputeIntParam(shader,"_PatchLevel",key.Level);
            cmd.SetComputeIntParam(shader,"_PatchX",key.X);cmd.SetComputeIntParam(shader,"_PatchY",key.Y);
            cmd.SetComputeFloatParam(shader,"_PatchSkirtDepth",(float)skirt);
            if(definition.GeneratorVersion==3&&FilterRenderingDetail)
            {
                double footprint=RenderFootprint(definition,key);var bits=PlanetSurfaceGpuData.Pair(footprint,footprint*2);
                cmd.SetComputeIntParams(shader,"_SurfaceRenderFootprints",unchecked((int)bits.x),unchecked((int)bits.y),unchecked((int)bits.z),unchecked((int)bits.w));
            }
            Dispatch(cmd,shader,kernel);
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
            if(definition.GeneratorVersion==3)PlanetSurfaceGpuData.BindFrame(cmd,shader,definition,frame,size);
            Dispatch(cmd,shader,kernel);
        }

        void Dispatch(CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            // Shader import can replace a kernel while retaining its ComputeShader object
            // and integer kernel ID. Re-query only on actual generation, so a live Editor
            // backend never dispatches a newly imported kernel with a stale group size.
            shader.GetKernelThreadGroupSizes(kernel,out uint x,out uint y,out uint z);
            if(x==0||x>int.MaxValue||y!=1||z!=1)throw new NotSupportedException("Planet patch kernels must use one-dimensional work groups.");
            int size=(int)x;
            if(!groupSizes.TryGetValue(kernel,out int previous)||previous!=size)groupSizes[kernel]=size;
            cmd.DispatchCompute(shader,kernel,checked((SlotVertexCount+size-1)/size),1,1);
        }

        void SelectGenerator(ComputeShader value)
        {
            if(generator==value)return;
            generator=value;selectedFamily=PlanetSurfaceGpuFamily.Complete;
            farKernel=localKernel=nativeFarKernel=nativeLocalKernel=filteredFarKernel=-1;groupSizes.Clear();
        }
        static string FamilyResource(PlanetSurfaceGpuFamily family)
        {
            switch(family)
            {
                case PlanetSurfaceGpuFamily.Plain:return ResourceName;
                case PlanetSurfaceGpuFamily.Orogen:return OrogenResourceName;
                case PlanetSurfaceGpuFamily.LegacyStructure:return "PlanetStructurePatchGenerator";
                case PlanetSurfaceGpuFamily.Drainage:return "PlanetDrainagePatchGenerator";
                case PlanetSurfaceGpuFamily.Landform:return "PlanetLandformPatchGenerator";
                default:throw new NotSupportedException("No built-in patch resource exists for the requested surface family.");
            }
        }

        ComputeShader Prepare(CommandBuffer cmd,in PlanetDefinition definition,int slot,PlanetPatchLayout layout,out int kernel)
        {
            if(layout!=Layout)throw new InvalidOperationException($"This backend stores {Layout} patches.");
            if(vertices==null || !used.Contains(slot))throw new ArgumentOutOfRangeException(nameof(slot));
            if(definition.GeneratorVersion==3)
            {
                if(surfaceData==null || surfaceData.IsDisposed || !surfaceData.Key.Equals(definition.Surface))
                {var next=layout==PlanetPatchLayout.Far&&FilterRenderingDetail?PlanetSurfaceGpuData.AcquireForFiltering(definition):PlanetSurfaceGpuData.Acquire(definition);
                    var previous=surfaceData;surfaceData=next;PlanetSurfaceGpuData.Release(previous);}
                surfaceData.ValidateDefinition(definition);
                // A serialized reference to the built-in entry remains source-aware. An
                // independently supplied shader retains its complete/custom binding contract.
                if(requestedGenerator&&!builtinEntry)builtinEntry=Resources.Load<ComputeShader>(ResourceName);
                bool custom=requestedGenerator&&requestedGenerator!=builtinEntry;
                var family=custom?PlanetSurfaceGpuFamily.Complete:surfaceData.PatchFamily;
                if(custom)SelectGenerator(requestedGenerator);
                else if(!generator||selectedFamily!=family)
                {SelectGenerator(Resources.Load<ComputeShader>(FamilyResource(family)));selectedFamily=family;}
                if(!generator)throw new InvalidOperationException("The captured surface patch shader family is unavailable.");
                if(nativeFarKernel<0){nativeFarKernel=generator.FindKernel("NativeFarPatch");nativeLocalKernel=generator.FindKernel("NativeLocalPatch");}
                kernel=layout==PlanetPatchLayout.Far?nativeFarKernel:nativeLocalKernel;
                if(layout==PlanetPatchLayout.Far&&FilterRenderingDetail)
                {if(filteredFarKernel<0)filteredFarKernel=generator.FindKernel("NativeFilteredFarPatch");kernel=filteredFarKernel;}
                if(!generator.IsSupported(kernel))throw new NotSupportedException("The device cannot run the signed terrain kernel; legacy noise is not a valid replacement.");
                if(layout==PlanetPatchLayout.Far&&FilterRenderingDetail)
                {surfaceData.PrepareFiltering(out var status);FilteringStatus=status;}
                surfaceData.Bind(cmd,generator,kernel,family);
                if(attributes==null)attributes=new GraphicsBuffer(GraphicsBuffer.Target.Structured,capacity*SlotVertexCount,PlanetSurfaceVertexAttributes.Stride){name=$"Planet {Layout} canonical attributes"};
                cmd.SetComputeBufferParam(generator,kernel,"_PlanetAttributes",attributes);
                if(layout==PlanetPatchLayout.Far&&FilterRenderingDetail)
                {
                    if(parentVertices==null)parentVertices=new GraphicsBuffer(GraphicsBuffer.Target.Structured,capacity*SlotVertexCount,PlanetVertex.Stride){name="Planet parent-band patch vertices"};
                    if(parentAttributes==null)parentAttributes=new GraphicsBuffer(GraphicsBuffer.Target.Structured,capacity*SlotVertexCount,PlanetSurfaceVertexAttributes.Stride){name="Planet parent-band render attributes"};
                    cmd.SetComputeBufferParam(generator,kernel,"_PlanetParentVertices",parentVertices);
                    cmd.SetComputeBufferParam(generator,kernel,"_PlanetParentAttributes",parentAttributes);
                }
            }
            else
            {
                SelectGenerator(requestedGenerator?requestedGenerator:Resources.Load<ComputeShader>(ResourceName));
                if(!generator)throw new InvalidOperationException("Planet patch generator compute shader is unavailable.");
                if(farKernel<0){farKernel=generator.FindKernel("FarPatch");localKernel=generator.FindKernel("LocalPatch");}
                kernel=layout==PlanetPatchLayout.Far?farKernel:localKernel;
            }
            cmd.SetComputeVectorParam(generator,"_PlanetSeedShift",(Vector3)(new float3(definition.Seed%101,definition.Seed%79,definition.Seed%67)*.137f));
            cmd.SetComputeFloatParam(generator,"_PlanetSeedDryness",definition.Seed*.01f);
            cmd.SetComputeFloatParam(generator,"_PlanetRadius",(float)definition.Radius);
            cmd.SetComputeFloatParam(generator,"_PlanetRelief",(float)definition.Relief);
            cmd.SetComputeIntParam(generator,"_PlanetGeneratorVersion",definition.GeneratorVersion);
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
                // Both layouts are Unity front faces toward the outside (cross(b-a,c-a) points outward/up):
                // cube-face u×v is outward while local x×z is down, hence the different orders.
                if(layout==PlanetPatchLayout.Far){triangles[n++]=a;triangles[n++]=b;triangles[n++]=c;triangles[n++]=b;triangles[n++]=d;triangles[n++]=c;}
                else{triangles[n++]=a;triangles[n++]=c;triangles[n++]=b;triangles[n++]=b;triangles[n++]=c;triangles[n++]=d;}
            }
            if(layout==PlanetPatchLayout.Far)
                for(int edge=0;edge<4;edge++)for(int j=0;j<Resolution;j++)
                {
                    int top=edge==0?j:edge==1?j*Row+Resolution:edge==2?Resolution*Row+Resolution-j:(Resolution-j)*Row;
                    int bottom=Row*Row+edge*Row+j;
                    int next=edge==0?top+1:edge==1?top+Row:edge==2?top-1:top-Row;
                    // Skirts face away from their patch, toward the neighbour that may show a crack.
                    triangles[n++]=top;triangles[n++]=bottom;triangles[n++]=next;
                    triangles[n++]=next;triangles[n++]=bottom;triangles[n++]=bottom+1;
                }
            return triangles;
        }
        static GraphicsBuffer BuildIndices(PlanetPatchLayout layout)
        {
            var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Index|GraphicsBuffer.Target.Raw,IndexCount(layout),sizeof(int)){name=$"Planet {layout} patch indices"};
            buffer.SetData(BuildIndexArray(layout));return buffer;
        }
        public void Dispose()
        {
            PlanetSurfaceGpuData.Release(surfaceData);surfaceData=null;
            vertices?.Dispose();indices?.Dispose();attributes?.Dispose();parentVertices?.Dispose();parentAttributes?.Dispose();vertices=null;indices=null;attributes=null;parentVertices=null;parentAttributes=null;capacity=0;free.Clear();used.Clear();
        }
    }
}
