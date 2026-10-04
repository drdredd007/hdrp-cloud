using System;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Derived GPU support. Its caller retains the native filter lease until this copy is released.</summary>
    internal sealed class PlanetSurfaceLandformFilterGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Control
        { public uint4 DirectionXY,DirectionZHeight,GradientXY,GradientZSupport,Variation; }
        [StructLayout(LayoutKind.Sequential)] struct Node
        { public uint4 Split;public int4 Children,Range; }
        [StructLayout(LayoutKind.Sequential)] struct Level
        { public int4 Layout;public int2 References;public uint2 Edge; }
        readonly NativeSurfaceLandformFilterView view;
        GraphicsBuffer controls,levels,index,references;
        int uploadedControls,uploadedLevels,uploadedIndex,uploadedReferences;
        bool disposed;
        internal SurfaceContentHash SourceDigest=>view.SourceDigest;
        internal bool IsReady=>!disposed&&uploadedControls==view.DerivedControls.Length&&uploadedLevels==view.Levels.Length&&
            uploadedIndex==view.Index.Length&&uploadedReferences==view.References.Length;
        internal static long EstimateBytes(in NativeSurfaceLandformFilterView view)=>checked(
            (long)math.max(1,view.DerivedControls.Length)*80+(long)math.max(1,view.Levels.Length)*32+
            (long)math.max(1,view.Index.Length)*48+(long)math.max(1,view.References.Length)*4);
        internal PlanetSurfaceLandformFilterGpu(in NativeSurfaceLandformFilterView view)
        {
            this.view=view;
            try
            {
                controls=Buffer<Control>(view.DerivedControls.Length);levels=Buffer<Level>(view.Levels.Length);
                index=Buffer<Node>(view.Index.Length);references=Buffer<int>(view.References.Length);
            }
            catch {Dispose();throw;}
        }
        /// <summary>The pool provides one global record allowance per frame across all active copies.</summary>
        internal int Upload(int maximumRecords)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetSurfaceLandformFilterGpu));
            if(maximumRecords<0)throw new ArgumentOutOfRangeException(nameof(maximumRecords));
            int remaining=maximumRecords;
            int count=math.min(remaining,view.DerivedControls.Length-uploadedControls);
            if(count>0)
            {
                var values=new Control[count];
                for(int i=0;i<count;i++)
                {
                    var c=view.DerivedControls[uploadedControls+i];values[i]=new Control {
                        DirectionXY=PlanetSurfaceGpuData.Pair(c.Direction.x,c.Direction.y),
                        DirectionZHeight=PlanetSurfaceGpuData.Pair(c.Direction.z,c.Height),
                        GradientXY=PlanetSurfaceGpuData.Pair(c.Gradient.x,c.Gradient.y),
                        GradientZSupport=PlanetSurfaceGpuData.Pair(c.Gradient.z,c.SupportMetres),
                        Variation=PlanetSurfaceGpuData.Pair(c.VariationLimit,0)};
                }
                controls.SetData(values,0,uploadedControls,count);uploadedControls+=count;remaining-=count;
            }
            count=math.min(remaining,view.Levels.Length-uploadedLevels);
            if(count>0)
            {
                var values=new Level[count];
                for(int i=0;i<count;i++)
                {
                    var h=view.Levels[uploadedLevels+i];var edge=PlanetSurfaceGpuData.Pair(h.MaximumCellEdgeMetres,0);
                    values[i]=new Level {Layout=new int4(h.Level,h.Root,h.FirstIndex,h.IndexCount),
                        References=new int2(h.FirstReference,h.ReferenceCount),Edge=edge.xy};
                }
                levels.SetData(values,0,uploadedLevels,count);uploadedLevels+=count;remaining-=count;
            }
            count=math.min(remaining,view.Index.Length-uploadedIndex);
            if(count>0)
            {
                var values=new Node[count];
                for(int i=0;i<count;i++)
                {
                    var n=view.Index[uploadedIndex+i];values[i]=new Node {Split=PlanetSurfaceGpuData.Pair(n.Split,0),
                        Children=new int4(n.Axis,n.Left,n.Right,0),Range=new int4(n.First,n.Count,0,0)};
                }
                index.SetData(values,0,uploadedIndex,count);uploadedIndex+=count;remaining-=count;
            }
            count=math.min(remaining,view.References.Length-uploadedReferences);
            if(count>0)
            {
                var values=new int[count];for(int i=0;i<count;i++)values[i]=view.References[uploadedReferences+i];
                references.SetData(values,0,uploadedReferences,count);uploadedReferences+=count;remaining-=count;
            }
            return maximumRecords-remaining;
        }
        internal void Bind(CommandBuffer command,ComputeShader shader,int kernel)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetSurfaceLandformFilterGpu));
            command.SetComputeBufferParam(shader,kernel,"_SurfaceLandformFilterControls",controls);
            command.SetComputeBufferParam(shader,kernel,"_SurfaceLandformFilterLevels",levels);
            command.SetComputeBufferParam(shader,kernel,"_SurfaceLandformFilterIndex",index);
            command.SetComputeBufferParam(shader,kernel,"_SurfaceLandformFilterReferences",references);
            command.SetComputeIntParams(shader,"_SurfaceLandformFilterCounts",IsReady&&view.SourceDigest.IsValid?1:0,
                view.MaximumLevel,view.DerivedControls.Length,view.Levels.Length);
            command.SetComputeIntParams(shader,"_SurfaceLandformFilterLayout",view.Index.Length,view.References.Length,view.PolicyVersion,0);
        }
        static GraphicsBuffer Buffer<T>(int count) where T:struct
        {
            var value=new GraphicsBuffer(GraphicsBuffer.Target.Structured,math.max(1,count),Marshal.SizeOf<T>());
            try {if(count==0)value.SetData(new T[1]);return value;}catch {value.Dispose();throw;}
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;controls?.Dispose();levels?.Dispose();index?.Dispose();references?.Dispose();
        }
    }
}
