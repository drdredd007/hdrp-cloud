using System;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Immutable FP64 copy of captured morphology. The parent surface lease owns its lifetime.</summary>
    internal sealed class PlanetSurfaceDrainageGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Landmass
        { public uint4 CenterXY,CenterZ,RightXY,RightZ,ForwardXY,ForwardZ;public int4 Layout; }
        [StructLayout(LayoutKind.Sequential)] struct Direction { public uint4 XY,Z; }
        [StructLayout(LayoutKind.Sequential)] struct Node
        { public uint4 DirectionXY,DirectionZ,BedArea,WidthDivide;public int4 Topology; }
        [StructLayout(LayoutKind.Sequential)] struct Segment
        { public uint4 MinimumXY,MinimumZ,MaximumXY,MaximumZ;public int4 Topology; }
        [StructLayout(LayoutKind.Sequential)] struct IndexNode
        { public uint4 Split;public int4 Children;public int4 Range; }
        readonly NativeSurfaceDrainageView view;
        GraphicsBuffer landmasses,coastVertices,coastDirections,nodes,segments,index,references,coastSegments,coastIndex,coastReferences;
        internal static long EstimateBytes(in NativeSurfaceDrainageView view)=>checked(
            (long)math.max(1,view.Landmasses.Length)*Marshal.SizeOf<Landmass>()+
            (long)math.max(1,view.CoastVertices.Length)*16+(long)math.max(1,view.CoastDirections.Length)*Marshal.SizeOf<Direction>()+
            (long)math.max(1,view.Nodes.Length)*Marshal.SizeOf<Node>()+(long)math.max(1,view.Segments.Length)*Marshal.SizeOf<Segment>()+
            (long)math.max(1,view.Index.Length)*Marshal.SizeOf<IndexNode>()+(long)math.max(1,view.References.Length)*4+
            (long)math.max(1,view.CoastSegments.Length)*Marshal.SizeOf<Segment>()+
            (long)math.max(1,view.CoastIndex.Length)*Marshal.SizeOf<IndexNode>()+(long)math.max(1,view.CoastReferences.Length)*4);
        internal PlanetSurfaceDrainageGpu(in NativeSurfaceDrainageView view)
        {
            this.view=view;
            try
            {
                var masses=new Landmass[view.Landmasses.Length];
                for(int i=0;i<masses.Length;i++)
                {var m=view.Landmasses[i];masses[i]=new Landmass {CenterXY=XY(m.Center),CenterZ=Z(m.Center),RightXY=XY(m.Right),RightZ=Z(m.Right),ForwardXY=XY(m.Forward),ForwardZ=Z(m.Forward),Layout=new int4(m.FirstVertex,m.VertexCount,0,0)};}
                var vertices=new uint4[view.CoastVertices.Length];var directions=new Direction[view.CoastDirections.Length];
                for(int i=0;i<vertices.Length;i++)vertices[i]=PlanetSurfaceGpuData.Pair(view.CoastVertices[i].x,view.CoastVertices[i].y);
                for(int i=0;i<directions.Length;i++)directions[i]=new Direction {XY=XY(view.CoastDirections[i]),Z=Z(view.CoastDirections[i])};
                var network=new Node[view.Nodes.Length];
                for(int i=0;i<network.Length;i++)
                {var n=view.Nodes[i];network[i]=new Node {DirectionXY=XY(n.Direction),DirectionZ=Z(n.Direction),BedArea=PlanetSurfaceGpuData.Pair(n.BedHeight,n.DrainageArea),WidthDivide=PlanetSurfaceGpuData.Pair(n.HillslopeWidth,n.DivideHeight),Topology=new int4(n.Parent,n.Outlet,n.StrahlerOrder,0)};}
                var support=new Segment[view.Segments.Length];
                for(int i=0;i<support.Length;i++)
                {var s=view.Segments[i];support[i]=new Segment {MinimumXY=XY(s.Minimum),MinimumZ=Z(s.Minimum),MaximumXY=XY(s.Maximum),MaximumZ=Z(s.Maximum),Topology=new int4(s.Child,s.Parent,0,0)};}
                var partition=new IndexNode[view.Index.Length];
                for(int i=0;i<partition.Length;i++)
                {var n=view.Index[i];partition[i]=new IndexNode {Split=PlanetSurfaceGpuData.Pair(n.Split,0),Children=new int4(n.Axis,n.Left,n.Right,0),Range=new int4(n.First,n.Count,0,0)};}
                var refs=new int[view.References.Length];for(int i=0;i<refs.Length;i++)refs[i]=view.References[i];
                var coastalSupport=new Segment[view.CoastSegments.Length];
                for(int i=0;i<coastalSupport.Length;i++)
                {var s=view.CoastSegments[i];coastalSupport[i]=new Segment {MinimumXY=XY(s.Minimum),MinimumZ=Z(s.Minimum),MaximumXY=XY(s.Maximum),MaximumZ=Z(s.Maximum),Topology=new int4(s.First,s.Last,s.Landmass,0)};}
                var coastalPartition=new IndexNode[view.CoastIndex.Length];
                for(int i=0;i<coastalPartition.Length;i++)
                {var n=view.CoastIndex[i];coastalPartition[i]=new IndexNode {Split=PlanetSurfaceGpuData.Pair(n.Split,0),Children=new int4(n.Axis,n.Left,n.Right,0),Range=new int4(n.First,n.Count,0,0)};}
                var coastalRefs=new int[view.CoastReferences.Length];for(int i=0;i<coastalRefs.Length;i++)coastalRefs[i]=view.CoastReferences[i];
                landmasses=Buffer(masses);coastVertices=Buffer(vertices);coastDirections=Buffer(directions);
                nodes=Buffer(network);segments=Buffer(support);index=Buffer(partition);references=Buffer(refs);
                coastSegments=Buffer(coastalSupport);coastIndex=Buffer(coastalPartition);coastReferences=Buffer(coastalRefs);
            }
            catch {Dispose();throw;}
        }
        internal void Bind(CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageLandmasses",landmasses);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageCoastVertices",coastVertices);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageCoastDirections",coastDirections);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageNodes",nodes);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageSegments",segments);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageIndex",index);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageReferences",references);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageCoastSegments",coastSegments);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageCoastIndex",coastIndex);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceDrainageCoastReferences",coastReferences);
            cmd.SetComputeIntParams(shader,"_SurfaceDrainageCounts",view.Enabled?view.Landmasses.Length:0,view.CoastVertices.Length,view.Nodes.Length,view.Segments.Length);
            cmd.SetComputeIntParams(shader,"_SurfaceDrainageIndexCounts",view.Index.Length,view.References.Length,view.CoastIndex.Length,view.CoastReferences.Length);
            var shape=PlanetSurfaceGpuData.Pair(view.Radius,view.CoastInfluenceMetres);
            cmd.SetComputeIntParams(shader,"_SurfaceDrainageShape",unchecked((int)shape.x),unchecked((int)shape.y),unchecked((int)shape.z),unchecked((int)shape.w));
        }
        static uint4 XY(double3 v)=>PlanetSurfaceGpuData.Pair(v.x,v.y);
        static uint4 Z(double3 v)=>PlanetSurfaceGpuData.Pair(v.z,0);
        static GraphicsBuffer Buffer<T>(T[] data) where T:struct
        {
            var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,math.max(1,data.Length),Marshal.SizeOf<T>());
            try {buffer.SetData(data.Length==0?new T[1]:data);return buffer;}catch{buffer.Dispose();throw;}
        }
        public void Dispose()
        {landmasses?.Dispose();coastVertices?.Dispose();coastDirections?.Dispose();nodes?.Dispose();segments?.Dispose();index?.Dispose();references?.Dispose();coastSegments?.Dispose();coastIndex?.Dispose();coastReferences?.Dispose();}
    }
}
