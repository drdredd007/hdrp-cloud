// World Orogen GPU transport for upstream cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Shared native compute ABI. Positions preserve upstream xyz; publication converts axes explicitly.</summary>
    public sealed class WorldOrogenGpuGraph:IDisposable
    {
        public WorldOrogenGraph Source {get;}
        public ComputeBuffer Positions {get;private set;}
        public ComputeBuffer Offsets {get;private set;}
        public ComputeBuffer Neighbors {get;private set;}
        public ComputeBuffer NeighborDistances {get;private set;}
        public ComputeBuffer Triangles {get;private set;}
        public ComputeBuffer Halfedges {get;private set;}
        public int RegionCount=>Source.RegionCount;
        public long EstimatedBytes=>checked(Bytes(Positions)+Bytes(Offsets)+Bytes(Neighbors)+Bytes(NeighborDistances)+Bytes(Triangles)+Bytes(Halfedges));
        internal static long Bytes(ComputeBuffer buffer)=>buffer==null?0:checked((long)buffer.count*buffer.stride);
        internal static long EstimateBytes(WorldOrogenGraph graph)=>checked((long)graph.RegionCount*12+(long)(graph.Offsets.Length+graph.Neighbors.Length+graph.Triangles.Length+graph.Halfedges.Length)*4+(long)graph.NeighborDistances.Length*4);
        public WorldOrogenGpuGraph(WorldOrogenGraph graph)
        {
            Source=graph??throw new ArgumentNullException(nameof(graph));
            try{Positions=Upload(graph.Directions,12);Offsets=Upload(graph.Offsets,4);Neighbors=Upload(graph.Neighbors,4);NeighborDistances=Upload(graph.NeighborDistances,4);Triangles=Upload(graph.Triangles,4);Halfedges=Upload(graph.Halfedges,4);}
            catch{Dispose();throw;}
        }
        internal static ComputeBuffer Upload(Array values,int stride){var buffer=new ComputeBuffer(Math.Max(1,values.Length),stride);try{if(values.Length>0)buffer.SetData(values);return buffer;}catch{buffer.Dispose();throw;}}
        public void Bind(ComputeShader shader,int kernel)
        {shader.SetInt("_WorldRegionCount",RegionCount);shader.SetInt("_WorldTriangleCount",Source.TriangleCount);shader.SetBuffer(kernel,"_WorldPositions",Positions);shader.SetBuffer(kernel,"_WorldNeighborOffsets",Offsets);shader.SetBuffer(kernel,"_WorldNeighbors",Neighbors);shader.SetBuffer(kernel,"_WorldNeighborDistances",NeighborDistances);shader.SetBuffer(kernel,"_WorldTriangles",Triangles);shader.SetBuffer(kernel,"_WorldHalfedges",Halfedges);}
        public void Dispose(){Positions?.Dispose();Offsets?.Dispose();Neighbors?.Dispose();NeighborDistances?.Dispose();Triangles?.Dispose();Halfedges?.Dispose();Positions=Offsets=Neighbors=NeighborDistances=Triangles=Halfedges=null;}
    }
    /// <summary>Elevation stays in upstream normalized units until final publication, not per-stage metres.</summary>
    public sealed class WorldOrogenGpuState:IDisposable
    {
        readonly Dictionary<string,ComputeBuffer> fields=new Dictionary<string,ComputeBuffer>();
        bool disposed;
        public WorldOrogenGpuGraph Graph {get;}
        public ComputeBuffer Elevation {get;private set;}
        public ComputeBuffer ScratchElevation {get;private set;}
        public ComputeBuffer OceanFlags {get;private set;}
        public ComputeBuffer Hotspot {get;private set;}
        public ComputeBuffer Dampen {get;private set;}
        public ComputeBuffer Orogenic {get;private set;}
        public ComputeBuffer NoisePermutation {get;private set;}
        public int RegionCount=>Graph.RegionCount;
        public const int Threads=64;
        public long ReleasedFieldBytes {get;private set;}
        public int AdditionalFieldCount=>fields.Count;
        public long EstimatedBytes {get{long bytes=Graph.EstimatedBytes+WorldOrogenGpuGraph.Bytes(Elevation)+WorldOrogenGpuGraph.Bytes(ScratchElevation)+WorldOrogenGpuGraph.Bytes(OceanFlags)+WorldOrogenGpuGraph.Bytes(Hotspot)+WorldOrogenGpuGraph.Bytes(Dampen)+WorldOrogenGpuGraph.Bytes(Orogenic)+WorldOrogenGpuGraph.Bytes(NoisePermutation);foreach(var pair in fields)bytes=checked(bytes+WorldOrogenGpuGraph.Bytes(pair.Value));return bytes;}}
        internal static long EstimateInitialBytes(WorldOrogenGraph graph)=>checked(WorldOrogenGpuGraph.EstimateBytes(graph)+(long)graph.RegionCount*6*4+512*4);
        public WorldOrogenGpuState(WorldOrogenGraph source,double seed)
        {
            Graph=new WorldOrogenGpuGraph(source);try
            {var zero=new float[source.RegionCount];Elevation=WorldOrogenGpuGraph.Upload(zero,4);ScratchElevation=WorldOrogenGpuGraph.Upload(zero,4);OceanFlags=WorldOrogenGpuGraph.Upload(new uint[source.RegionCount],4);Hotspot=WorldOrogenGpuGraph.Upload(zero,4);Dampen=WorldOrogenGpuGraph.Upload(zero,4);Orogenic=WorldOrogenGpuGraph.Upload(zero,4);NoisePermutation=WorldOrogenGpuGraph.Upload(new WorldOrogenNoise(seed).Permutation,4);}catch{Dispose();throw;}
        }
        public ComputeBuffer Field(string shaderName,int stride=4,int count=-1)
        {
            if(disposed)throw new ObjectDisposedException(nameof(WorldOrogenGpuState));
            if(fields.TryGetValue(shaderName,out var existing)){if(existing.stride!=stride||(count>=0&&existing.count!=count))throw new ArgumentException("Conflicting GPU field shape.");return existing;}
            var result=new ComputeBuffer(Math.Max(1,count<0?RegionCount:count),stride);fields.Add(shaderName,result);return result;
        }
        public bool TryGetField(string shaderName,out ComputeBuffer buffer)=>fields.TryGetValue(shaderName,out buffer);
        /// <summary>The caller must have passed the final dispatch/readback that consumes this field.
        /// Only the named owner is retired; fixed graph/result banks remain owned by State.</summary>
        public bool RemoveField(string shaderName)
        {
            if(!fields.TryGetValue(shaderName,out var buffer))return false;
            long bytes=WorldOrogenGpuGraph.Bytes(buffer);fields.Remove(shaderName);buffer.Dispose();ReleasedFieldBytes=checked(ReleasedFieldBytes+bytes);return true;
        }
        public void SwapElevation(){var previous=Elevation;Elevation=ScratchElevation;ScratchElevation=previous;}
        public void Bind(ComputeShader shader,int kernel)
        {if(disposed)throw new ObjectDisposedException(nameof(WorldOrogenGpuState));Graph.Bind(shader,kernel);shader.SetBuffer(kernel,"_WorldElevation",Elevation);shader.SetBuffer(kernel,"_WorldScratchElevation",ScratchElevation);shader.SetBuffer(kernel,"_WorldOceanFlags",OceanFlags);shader.SetBuffer(kernel,"_WorldHotspot",Hotspot);shader.SetBuffer(kernel,"_WorldDampen",Dampen);shader.SetBuffer(kernel,"_WorldOrogenic",Orogenic);shader.SetBuffer(kernel,"_WorldNoisePermutation",NoisePermutation);}
        public const int MaximumDispatchGroups=65535;
        public void Dispatch(ComputeShader shader,int kernel,int elements=-1,bool regionOffsetSupported=false)
        {if(!shader.IsSupported(kernel))throw new NotSupportedException("World Orogen compute kernel is unavailable on this device or failed compilation: "+shader.name+" #"+kernel);Bind(shader,kernel);DispatchBounded(shader,kernel,elements<0?RegionCount:elements,regionOffsetSupported);}
        /// <summary>Buffers are already bound. An opted-in per-region kernel must add
        /// _WorldDispatchOffset to SV_DispatchThreadID.x and retain its source bounds check.
        /// Sequential/scalar kernels never opt in and are never silently repeated.</summary>
        public static void DispatchBounded(ComputeShader shader,int kernel,int elements,bool regionOffsetSupported=false)
        {
            if(shader==null)throw new ArgumentNullException(nameof(shader));
            if(elements<0)throw new ArgumentOutOfRangeException(nameof(elements));
            if(!shader.IsSupported(kernel))throw new NotSupportedException("World Orogen compute kernel is unavailable on this device or failed compilation: "+shader.name+" #"+kernel);
            shader.GetKernelThreadGroupSizes(kernel,out uint x,out uint y,out uint z);
            if(x==0||x>1024||y!=1||z!=1)throw new InvalidOperationException("Expected one-dimensional generation kernel.");
            int width=(int)x,capacity=checked(width*MaximumDispatchGroups);
            if(elements>capacity&&!regionOffsetSupported)throw new NotSupportedException("This kernel does not declare a region-offset ABI for dispatches above the group limit.");
            try
            {
                for(int offset=0;offset<elements;)
                {
                    int count=Math.Min(capacity,elements-offset);
                    if(regionOffsetSupported)shader.SetInt("_WorldDispatchOffset",offset);
                    shader.Dispatch(kernel,(count+width-1)/width,1,1);offset=checked(offset+count);
                }
            }
            finally{if(regionOffsetSupported)shader.SetInt("_WorldDispatchOffset",0);}
        }
        public void Dispose(){if(disposed)return;disposed=true;foreach(var pair in fields)pair.Value.Dispose();fields.Clear();Elevation?.Dispose();ScratchElevation?.Dispose();OceanFlags?.Dispose();Hotspot?.Dispose();Dampen?.Dispose();Orogenic?.Dispose();NoisePermutation?.Dispose();Elevation=ScratchElevation=OceanFlags=Hotspot=Dampen=Orogenic=NoisePermutation=null;Graph.Dispose();}
    }
}
