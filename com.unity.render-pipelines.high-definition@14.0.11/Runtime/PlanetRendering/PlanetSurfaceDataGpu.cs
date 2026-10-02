using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Immutable GPU copy of the same leased CPU snapshot. Shared by far/local backends.</summary>
    internal sealed class PlanetSurfaceGpuData : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Tile {public int4 Address,Layout;}
        [StructLayout(LayoutKind.Sequential)] struct Region
        {
            public uint4 AnchorXY,AnchorZ,RightXY,RightZ,ForwardXY,ForwardZ;
            public uint4 Minimum,Maximum,Physical;
            public int4 Layout,Flags;
        }
        [StructLayout(LayoutKind.Sequential)] struct Stamp {public uint4 CenterXY,CenterZ,Shape0,Shape1;}
        static readonly Dictionary<PlanetSurfaceDescriptor,PlanetSurfaceGpuData> cache=new Dictionary<PlanetSurfaceDescriptor,PlanetSurfaceGpuData>();
        readonly PlanetSurfaceDataLease lease;
        readonly NativeSurfaceView view;
        GraphicsBuffer tiles,heights,regions,regionHeights,regionMasks,stamps,materialWeights,erosionData,regionMaterialWeights,regionErosionData;
        GraphicsBuffer neutralFilterRanges,neutralFilterMips,neutralFilterSamples;
        PlanetSurfaceRegionFilterLease filter;
        int references;
        internal PlanetSurfaceDescriptor Key {get;}
        internal bool IsDisposed {get;private set;}
        internal long EstimatedBytes=>Bytes(view)+PlanetSurfaceRegionFiltering.EstimateGpuBytes(view)+32;
        internal static long EstimateBytes(PlanetSurfaceDescriptor descriptor)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(cache.TryGetValue(descriptor,out var existing))return existing.EstimatedBytes;
            if(!PlanetSurfaceDataRegistry.TryAcquire(descriptor,out var held))throw new InvalidOperationException("Signed surface snapshot is not registered.");
            try{return Bytes(held.View)+PlanetSurfaceRegionFiltering.EstimateGpuBytes(held.View)+32;}finally{held.Dispose();}
        }
        static long Bytes(in NativeSurfaceView source)=>checked(
            (long)math.max(1,source.Tiles.Length)*Marshal.SizeOf<Tile>()+(long)math.max(1,source.Heights.Length)*4+
            (long)math.max(1,source.Regions.Length)*Marshal.SizeOf<Region>()+(long)math.max(1,source.RegionHeights.Length)*4+
            (long)math.max(1,source.RegionMasks.Length)*4+(long)math.max(1,source.Stamps.Length)*Marshal.SizeOf<Stamp>()+
            (long)math.max(1,source.MaterialWeights.Length)*16+(long)math.max(1,source.ErosionData.Length)*16+
            (long)math.max(1,source.RegionMaterialWeights.Length)*16+(long)math.max(1,source.RegionErosionData.Length)*16);
        internal static PlanetSurfaceGpuData Acquire(in PlanetDefinition definition)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if(cache.TryGetValue(definition.Surface,out var existing)){existing.ValidateDefinition(definition);existing.references++;return existing;}
            if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var held))throw new InvalidOperationException("Signed surface snapshot is not registered.");
            try
            {
                if(!PlanetSurfaceData.Compatible(definition,held.View))throw new InvalidOperationException("GPU surface binding does not match the published definition.");
                var result=new PlanetSurfaceGpuData(definition.Surface,held);result.references=1;cache.Add(definition.Surface,result);return result;
            }
            catch {held.Dispose();throw;}
        }
        internal static void Release(PlanetSurfaceGpuData data)
        {
            if(data==null)return;PlanetSurfaceDataRegistry.CheckMainThread();
            if(--data.references!=0)return;
            if(cache.TryGetValue(data.Key,out var current) && ReferenceEquals(current,data))cache.Remove(data.Key);
            data.Dispose();
        }
        internal void ValidateDefinition(in PlanetDefinition definition)
        {
            if(IsDisposed)throw new ObjectDisposedException(nameof(PlanetSurfaceGpuData));
            if(!PlanetSurfaceData.Compatible(definition,view))throw new InvalidOperationException("GPU surface binding does not match the published definition.");
        }
        PlanetSurfaceGpuData(PlanetSurfaceDescriptor key,PlanetSurfaceDataLease lease)
        {
            Key=key;this.lease=lease;view=lease.View;
            try
            {
                var tileData=new Tile[view.Tiles.Length];
                for(int i=0;i<tileData.Length;i++){var t=view.Tiles[i];tileData[i]=new Tile {Address=new int4(t.Key.Face,t.Key.Level,t.Key.X,t.Key.Y),Layout=new int4(t.Resolution,t.HeightOffset,t.AttributeOffset,(int)t.Channels)};}
                tiles=Buffer(tileData);heights=Buffer(Copy(view.Heights));
                materialWeights=Buffer(Copy(view.MaterialWeights));erosionData=Buffer(Copy(view.ErosionData));regionMaterialWeights=Buffer(Copy(view.RegionMaterialWeights));
                regionErosionData=Buffer(Copy(view.RegionErosionData));
                var regionData=new Region[view.Regions.Length];
                for(int i=0;i<regionData.Length;i++)
                {
                    var r=view.Regions[i];var p=r.Projection;
                    regionData[i]=new Region {AnchorXY=XY(p.AnchorDirection),AnchorZ=Z(p.AnchorDirection),RightXY=XY(p.Right),RightZ=Z(p.Right),
                        ForwardXY=XY(p.Forward),ForwardZ=Z(p.Forward),Minimum=Pair(p.MinimumMetres.x,p.MinimumMetres.y),Maximum=Pair(p.MaximumMetres.x,p.MaximumMetres.y),
                        Physical=Pair(p.Radius,r.BlendMetres),Layout=new int4(r.Resolution.x,r.Resolution.y,r.HeightOffset,r.MaskOffset),Flags=new int4((int)r.Mode,(int)r.DetailPolicy,(int)r.Channels,r.AttributeOffset)};
                }
                regions=Buffer(regionData);regionHeights=Buffer(Copy(view.RegionHeights));regionMasks=Buffer(Copy(view.RegionMasks));
                var stampData=new Stamp[view.Stamps.Length];
                for(int i=0;i<stampData.Length;i++)
                {
                    var s=view.Stamps[i];stampData[i]=new Stamp {CenterXY=XY(s.CenterDirection),CenterZ=Z(s.CenterDirection),
                        Shape0=Pair(s.RadiusMetres,s.DepthMetres),Shape1=Pair(s.RimWidthMetres,s.RimHeightMetres)};
                }
                stamps=Buffer(stampData);
                neutralFilterRanges=Buffer(new int2[1]);neutralFilterMips=Buffer(new int4[1]);neutralFilterSamples=Buffer(new float2[1]);
            }
            catch {Dispose();throw;}
        }
        static float[] Copy(Unity.Collections.NativeArray<float>.ReadOnly data)
        {var result=new float[data.Length];for(int i=0;i<result.Length;i++)result[i]=data[i];return result;}
        static float4[] Copy(Unity.Collections.NativeArray<float4>.ReadOnly data)
        {var result=new float4[data.Length];for(int i=0;i<result.Length;i++)result[i]=data[i];return result;}
        static GraphicsBuffer Buffer<T>(T[] data) where T:struct
        {
            var result=new GraphicsBuffer(GraphicsBuffer.Target.Structured,math.max(1,data.Length),Marshal.SizeOf<T>());
            try {result.SetData(data.Length==0?new T[1]:data);return result;}catch{result.Dispose();throw;}
        }
        static uint2 Bits(double value){ulong bits=unchecked((ulong)BitConverter.DoubleToInt64Bits(value));return new uint2((uint)bits,(uint)(bits>>32));}
        internal static uint4 Pair(double a,double b){var x=Bits(a);var y=Bits(b);return new uint4(x.x,x.y,y.x,y.y);}
        static uint4 XY(double3 value)=>Pair(value.x,value.y);
        static uint4 Z(double3 value){var z=Bits(value.z);return new uint4(z.x,z.y,0,0);}
        static void Set(CommandBuffer cmd,ComputeShader shader,string name,uint4 value)
            => cmd.SetComputeIntParams(shader,name,unchecked((int)value.x),unchecked((int)value.y),unchecked((int)value.z),unchecked((int)value.w));
        internal void Bind(CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            if(IsDisposed)throw new ObjectDisposedException(nameof(PlanetSurfaceGpuData));
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceTiles",tiles);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceHeights",heights);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegions",regions);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionHeights",regionHeights);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionMasks",regionMasks);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceStamps",stamps);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceMaterialWeights",materialWeights);cmd.SetComputeBufferParam(shader,kernel,"_SurfaceErosionData",erosionData);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionMaterialWeights",regionMaterialWeights);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionErosionData",regionErosionData);
            cmd.SetComputeIntParam(shader,"_SurfaceTileCount",view.Tiles.Length);cmd.SetComputeIntParam(shader,"_SurfaceCanonicalLevel",view.CanonicalTileLevel);
            cmd.SetComputeIntParam(shader,"_SurfaceRegionCount",view.Regions.Length);cmd.SetComputeIntParam(shader,"_SurfaceStampCount",view.Stamps.Length);
            Set(cmd,shader,"_SurfaceRadiusAndNormal",Pair(view.Recipe.Radius,PlanetSurfaceData.NormalSampleMetresFor(view.Recipe.Radius)));
            Set(cmd,shader,"_SurfaceDetailShape",Pair(view.Detail.WavelengthMetres,view.Detail.AmplitudeMetres));
            cmd.SetComputeIntParam(shader,"_SurfaceDetailSeed",view.Detail.Seed);
            bool ready=filter!=null&&filter.IsReady;var support=ready?filter.Entry:null;
            cmd.SetComputeIntParam(shader,"_SurfaceRegionFilterReady",ready?1:0);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionFilterRanges",ready?support.Ranges:neutralFilterRanges);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionFilterMips",ready?support.Mips:neutralFilterMips);
            cmd.SetComputeBufferParam(shader,kernel,"_SurfaceRegionFilterSamples",ready?support.Samples:neutralFilterSamples);
        }
        internal bool PrepareFiltering(out SurfaceRegionFilterStatus status)
        {
            if(filter!=null&&filter.IsDisposed){filter.Dispose();filter=null;}
            if(filter==null&&!PlanetSurfaceRegionFiltering.TryAcquire(Key,out filter,out status))return false;
            bool ready=filter.IsReady;status=filter.Status;return ready;
        }
        internal int FilterGeneration=>filter?.Generation??0;
        internal static void BindFrame(CommandBuffer cmd,ComputeShader shader,in PlanetDefinition definition,in PlanetSurfaceFrame frame,double size)
        {
            Set(cmd,shader,"_SurfaceFrameRightXY",XY(frame.Right));Set(cmd,shader,"_SurfaceFrameRightZ",Z(frame.Right));
            Set(cmd,shader,"_SurfaceFrameUpXY",XY(frame.Up));Set(cmd,shader,"_SurfaceFrameUpZ",Z(frame.Up));
            Set(cmd,shader,"_SurfaceFrameForwardXY",XY(frame.Forward));Set(cmd,shader,"_SurfaceFrameForwardZ",Z(frame.Forward));
            double rho=math.length(frame.Position);var radial=frame.Position/rho;
            Set(cmd,shader,"_SurfaceFrameRadialXY",XY(radial));Set(cmd,shader,"_SurfaceFrameRadialZ",Z(radial));
            Set(cmd,shader,"_SurfaceFrameShape",Pair(rho,rho-definition.Radius));Set(cmd,shader,"_SurfaceCellSize",Pair(size/PlanetGpuPatchBackend.Resolution,0));
        }
        public void Dispose()
        {
            if(IsDisposed)return;IsDisposed=true;tiles?.Dispose();heights?.Dispose();regions?.Dispose();regionHeights?.Dispose();regionMasks?.Dispose();stamps?.Dispose();
            materialWeights?.Dispose();erosionData?.Dispose();regionMaterialWeights?.Dispose();regionErosionData?.Dispose();lease.Dispose();
            neutralFilterRanges?.Dispose();neutralFilterMips?.Dispose();neutralFilterSamples?.Dispose();filter?.Dispose();filter=null;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset(){foreach(var value in cache.Values)value.Dispose();cache.Clear();}
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallEditorCleanup(){UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Reset;}
#endif
    }
}
