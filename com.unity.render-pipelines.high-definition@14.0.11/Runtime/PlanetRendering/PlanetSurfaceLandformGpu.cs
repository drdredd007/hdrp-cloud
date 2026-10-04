using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>FP64 GPU-owned copy, shared by captured child content rather than parent terrain revisions.</summary>
    internal sealed class PlanetSurfaceLandformGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Control
        { public uint4 DirectionXY,DirectionZHeight,GradientXY,GradientZSupport,Variation; }
        [StructLayout(LayoutKind.Sequential)] struct IndexNode
        { public uint4 Split;public int4 Children,Range; }
        internal const long MaximumModuleBytes=256L*1024*1024;
        const int ManagedArrayHeaderReserve=32;
        static readonly Dictionary<SurfaceContentHash,PlanetSurfaceLandformGpu> cache=new Dictionary<SurfaceContentHash,PlanetSurfaceLandformGpu>();
        static long residentBytes;
        readonly SurfaceContentHash digest;
        readonly bool enabled;
        readonly double radius;
        readonly int controlCount,indexCount,referenceCount;
        readonly long bytes;
        int holders;
        bool pooled,disposed,accounted;
        GraphicsBuffer controls,index,references;
        internal static long ResidentBytes=>residentBytes;
        internal static int CacheCount=>cache.Count;
        internal SurfaceContentHash ContentDigest=>digest;
        internal int HolderCount=>holders;
        internal long AllocatedBytes=>bytes;
        static int Length<T>(Unity.Collections.NativeArray<T>.ReadOnly values) where T:struct=>values.IsCreated?values.Length:0;
        internal static long EstimateBytes(in NativeSurfaceLandformView view)=>checked(
            (long)math.max(1,Length(view.Controls))*Marshal.SizeOf<Control>()+
            (long)math.max(1,Length(view.Index))*Marshal.SizeOf<IndexNode>()+(long)math.max(1,Length(view.References))*4);
        /// <summary>Three packing arrays coexist until upload; each includes an initialized max-one dummy record.</summary>
        internal static long EstimatePackingBytes(in NativeSurfaceLandformView view)=>checked(EstimateBytes(view)+3L*ManagedArrayHeaderReserve);
        internal static bool CanAllocate(in NativeSurfaceLandformView view,out long peakBytes)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();Validate(view);
            return CanAllocateCounts(Length(view.Controls),Length(view.Index),Length(view.References),out peakBytes);
        }
        /// <summary>Allocation-free preflight used by the constructor before packing arrays or GPU buffers exist.</summary>
        internal static bool CanAllocateCounts(int controls,int nodes,int refs,out long peakBytes)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(controls<0||controls>SurfaceLandformField.MaximumControls||nodes<0||nodes>SurfaceLandformField.MaximumIndexNodes||refs<0||refs>SurfaceLandformField.MaximumReferences)
                throw new ArgumentException("Full landform GPU counts exceed their captured source bounds.");
            long gpu=checked((long)math.max(1,controls)*Marshal.SizeOf<Control>()+(long)math.max(1,nodes)*Marshal.SizeOf<IndexNode>()+(long)math.max(1,refs)*4);
            peakBytes=checked(residentBytes+gpu*2+3L*ManagedArrayHeaderReserve);
            return peakBytes<=MaximumModuleBytes;
        }
        static void Validate(in NativeSurfaceLandformView view)
        {
            int controls=Length(view.Controls),nodes=Length(view.Index),refs=Length(view.References);
            if(!view.Enabled)
            {
                if(controls!=0||nodes!=0||refs!=0)throw new ArgumentException("Disabled landform GPU metadata must not hide nonempty source banks.");
                return;
            }
            if(!math.isfinite(view.Radius)||!(view.Radius>0)||controls<6||controls>SurfaceLandformField.MaximumControls||
                nodes<1||nodes>SurfaceLandformField.MaximumIndexNodes||refs<1||refs>SurfaceLandformField.MaximumReferences)
                throw new ArgumentException("Captured Full landform GPU copy requires valid finite source metadata and bounded created arrays.");
        }
        internal static PlanetSurfaceLandformGpu Acquire(in NativeSurfaceLandformView view)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();Validate(view);
            if(!view.Enabled)return new PlanetSurfaceLandformGpu(view);
            if(cache.TryGetValue(view.ContentDigest,out var shared))
            {
                if(shared.radius!=view.Radius||shared.controlCount!=Length(view.Controls)||shared.indexCount!=Length(view.Index)||shared.referenceCount!=Length(view.References))
                    throw new ArgumentException("Captured child digest cannot identify incompatible Full GPU metadata.");
                shared.holders++;return shared;
            }
            var created=new PlanetSurfaceLandformGpu(view);
            try {cache.Add(view.ContentDigest,created);created.pooled=true;created.holders=1;return created;}
            catch {created.Destroy();throw;}
        }
        internal static void Release(PlanetSurfaceLandformGpu instance)
        {
            if(instance==null)return;PlanetSurfaceDataRegistry.CheckMainThread();
            if(instance.disposed)return;
            if(instance.pooled)
            {
                if(--instance.holders>0)return;
                cache.Remove(instance.digest);instance.pooled=false;
            }
            instance.Destroy();
        }
        internal PlanetSurfaceLandformGpu(in NativeSurfaceLandformView view)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(!CanAllocate(view,out long peak))throw new InvalidOperationException($"Full landform GPU upload requires {peak} bytes including resident copies and packing arrays; module cap is {MaximumModuleBytes}.");
            digest=view.ContentDigest;enabled=view.Enabled;radius=view.Radius;
            controlCount=Length(view.Controls);indexCount=Length(view.Index);referenceCount=Length(view.References);bytes=EstimateBytes(view);
            try
            {
                var values=new Control[math.max(1,controlCount)];
                for(int i=0;i<controlCount;i++)
                {
                    var c=view.Controls[i];values[i]=new Control {
                        DirectionXY=PlanetSurfaceGpuData.Pair(c.Direction.x,c.Direction.y),
                        DirectionZHeight=PlanetSurfaceGpuData.Pair(c.Direction.z,c.Height),
                        GradientXY=PlanetSurfaceGpuData.Pair(c.Gradient.x,c.Gradient.y),
                        GradientZSupport=PlanetSurfaceGpuData.Pair(c.Gradient.z,c.SupportMetres),
                        Variation=PlanetSurfaceGpuData.Pair(c.VariationLimit,0)};
                }
                var nodes=new IndexNode[math.max(1,indexCount)];
                for(int i=0;i<indexCount;i++)
                {
                    var n=view.Index[i];nodes[i]=new IndexNode {Split=PlanetSurfaceGpuData.Pair(n.Split,0),
                        Children=new int4(n.Axis,n.Left,n.Right,0),Range=new int4(n.First,n.Count,0,0)};
                }
                var refs=new int[math.max(1,referenceCount)];for(int i=0;i<referenceCount;i++)refs[i]=view.References[i];
                controls=Buffer(values);index=Buffer(nodes);references=Buffer(refs);
                residentBytes+=bytes;accounted=true;
            }
            catch {Destroy();throw;}
        }
        internal void Bind(CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetSurfaceLandformGpu));
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceLandformControls",controls);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceLandformIndex",index);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceLandformReferences",references);
            cmd.SetComputeIntParams(shader,"_SurfaceLandformCounts",enabled?1:0,controlCount,indexCount,referenceCount);
            var shape=PlanetSurfaceGpuData.Pair(radius,0);
            cmd.SetComputeIntParams(shader,"_SurfaceLandformShape",unchecked((int)shape.x),unchecked((int)shape.y),unchecked((int)shape.z),unchecked((int)shape.w));
        }
        static GraphicsBuffer Buffer<T>(T[] values) where T:struct
        {
            var result=new GraphicsBuffer(GraphicsBuffer.Target.Structured,values.Length,Marshal.SizeOf<T>());
            try {result.SetData(values);return result;}catch {result.Dispose();throw;}
        }
        void Destroy()
        {
            if(disposed)return;disposed=true;controls?.Dispose();index?.Dispose();references?.Dispose();
            controls=index=references=null;if(accounted){residentBytes-=bytes;accounted=false;}
        }
        public void Dispose()=>Release(this);
    }
}
