using System;
using SpaceRunner.PlanetTerrain;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Render-only immutable geometry and material samples. Physics continues to use PlanetLocalPatch.</summary>
    public sealed class PlanetRenderPatch : IDisposable
    {
        public NativeArray<float3> Positions,Normals,PlanetOffsets;
        public NativeArray<SurfaceAttributes> Attributes;
        public NativeArray<SurfaceSampleStatus> GeometryStatuses,AttributeStatuses;
        public PlanetSurfaceFrame Frame {get;internal set;}
        public int2 Key {get;internal set;}
        public int Resolution {get;internal set;}
        public double Size {get;internal set;}
        public SurfaceChannels RequiredAttributes {get;internal set;}
        public SurfaceSampleStatus GeometryStatus=>Status(GeometryStatuses);
        public SurfaceSampleStatus AttributeStatus=>RequiredAttributes==SurfaceChannels.None?SurfaceSampleStatus.Ready:Status(AttributeStatuses);
        static SurfaceSampleStatus Status(NativeArray<SurfaceSampleStatus> values)
        {if(!values.IsCreated)return SurfaceSampleStatus.NotReady;foreach(var value in values)if(value!=SurfaceSampleStatus.Ready)return value;return SurfaceSampleStatus.Ready;}
        public void Dispose()
        {
            if(Positions.IsCreated)Positions.Dispose();if(Normals.IsCreated)Normals.Dispose();if(PlanetOffsets.IsCreated)PlanetOffsets.Dispose();
            if(Attributes.IsCreated)Attributes.Dispose();if(GeometryStatuses.IsCreated)GeometryStatuses.Dispose();if(AttributeStatuses.IsCreated)AttributeStatuses.Dispose();
        }
        public static PlanetRenderPatchRequest Schedule(PlanetDefinition definition,in PlanetSurfaceFrame frame,int2 key,double size,int resolution,
            SurfaceChannels requiredAttributes=SurfaceChannels.None)
        {
            if(!definition.IsValid||!math.all(math.isfinite(frame.Position))||math.lengthsq(frame.Position)<=0||!math.isfinite(size)||size<=0||
                resolution<2||resolution>128||(resolution&(resolution-1))!=0||math.any(math.abs((double2)key*size)+size>8192))
                throw new ArgumentException("Use a bounded metric render patch and power-of-two resolution 2–128.");
            var patch=new PlanetRenderPatch {Frame=frame,Key=key,Size=size,Resolution=resolution,RequiredAttributes=requiredAttributes};
            PlanetSurfaceRenderLease lease=null;
            try
            {
                // Readiness is established before any output allocation. A finite algorithm-five
                // render sample must never borrow the intentionally unfiltered physical view.
                if(definition.GeneratorVersion==3)
                {
                    if(!PlanetSurfaceDataRegistry.TryAcquireForRendering(definition.Surface,out lease,out var status))
                        throw new InvalidOperationException("Signed render preparation is not ready: "+status);
                    if(!PlanetSurfaceData.Compatible(definition,lease.View))throw new InvalidOperationException("Signed render snapshot does not match the definition.");
                }
                int count=(resolution+1)*(resolution+1);
                patch.Positions=new NativeArray<float3>(count,Allocator.Persistent);patch.Normals=new NativeArray<float3>(count,Allocator.Persistent);
                patch.PlanetOffsets=new NativeArray<float3>(count,Allocator.Persistent);patch.Attributes=new NativeArray<SurfaceAttributes>(count,Allocator.Persistent);
                patch.GeometryStatuses=new NativeArray<SurfaceSampleStatus>(count,Allocator.Persistent);patch.AttributeStatuses=new NativeArray<SurfaceSampleStatus>(count,Allocator.Persistent);
                JobHandle job;
                if(definition.GeneratorVersion==3)
                {
                    job=new PlanetSignedRenderPatchJob {Definition=definition,Surface=lease.View,Frame=frame,Key=key,Size=size,Resolution=resolution,
                        RequiredAttributes=requiredAttributes,Positions=patch.Positions,Normals=patch.Normals,PlanetOffsets=patch.PlanetOffsets,
                        Attributes=patch.Attributes,GeometryStatuses=patch.GeometryStatuses,AttributeStatuses=patch.AttributeStatuses}.Schedule(count,64);
                }
                else
                    job=new PlanetLegacyRenderPatchJob {Definition=definition,Frame=frame,Key=key,Size=size,Resolution=resolution,
                        Positions=patch.Positions,Normals=patch.Normals,PlanetOffsets=patch.PlanetOffsets,Attributes=patch.Attributes,
                        GeometryStatuses=patch.GeometryStatuses,AttributeStatuses=patch.AttributeStatuses}.Schedule(count,64);
                lease?.AddDependency(job);return new PlanetRenderPatchRequest(patch,job,lease);
            }
            catch {lease?.Dispose();patch.Dispose();throw;}
        }
    }
    public sealed class PlanetRenderPatchRequest : IDisposable
    {
        PlanetRenderPatch patch;JobHandle job;PlanetSurfaceRenderLease lease;
        internal PlanetRenderPatchRequest(PlanetRenderPatch patch,JobHandle job,PlanetSurfaceRenderLease lease){this.patch=patch;this.job=job;this.lease=lease;}
        public bool IsCompleted=>patch!=null&&job.IsCompleted;
        public bool TryComplete(out PlanetRenderPatch result)
        {result=null;if(patch==null)throw new ObjectDisposedException(nameof(PlanetRenderPatchRequest));if(!job.IsCompleted)return false;result=Complete();return true;}
        public PlanetRenderPatch Complete()
        {if(patch==null)throw new ObjectDisposedException(nameof(PlanetRenderPatchRequest));job.Complete();lease?.Dispose();lease=null;var result=patch;patch=null;return result;}
        public void Dispose(){if(patch==null)return;job.Complete();lease?.Dispose();lease=null;patch.Dispose();patch=null;}
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    internal struct PlanetSignedRenderPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;[ReadOnly]public NativeSurfaceView Surface;
        public PlanetSurfaceFrame Frame;public int2 Key;public double Size;public int Resolution;public SurfaceChannels RequiredAttributes;
        public NativeArray<float3> Positions,Normals,PlanetOffsets;public NativeArray<SurfaceAttributes> Attributes;
        public NativeArray<SurfaceSampleStatus> GeometryStatuses,AttributeStatuses;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));var footprint=new SurfaceSamplingFootprint(Size/Resolution);
            var status=SurfaceSampler.TrySampleHeight(Surface,direction,footprint,out double height);double3 normal=default;
            double normalStep=PlanetSurfaceData.NormalSampleMetresFor(Definition.Radius);
            if(status==SurfaceSampleStatus.Ready)status=SurfaceSampler.TrySampleNormal(Surface,direction,normalStep,footprint,out normal);
            GeometryStatuses[index]=status;SurfaceAttributes attributes=default;
            AttributeStatuses[index]=status==SurfaceSampleStatus.Ready?
                SurfaceSampler.TrySampleRenderAttributes(Surface,direction,footprint,height,normal,normalStep,out attributes,RequiredAttributes):status;
            Attributes[index]=attributes;
            CubeSurface.TryNormalize(direction,out var unit);var point=unit*(Definition.Radius+height);
            if(status!=SurfaceSampleStatus.Ready)return;
            var offset=point-Frame.Position;PlanetOffsets[index]=(float3)offset;
            Positions[index]=(float3)new double3(math.dot(offset,Frame.Right),math.dot(offset,Frame.Up),math.dot(offset,Frame.Forward));
            Normals[index]=(float3)new double3(math.dot(normal,Frame.Right),math.dot(normal,Frame.Up),math.dot(normal,Frame.Forward));
        }
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    internal struct PlanetLegacyRenderPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;public PlanetSurfaceFrame Frame;public int2 Key;public double Size;public int Resolution;
        public NativeArray<float3> Positions,Normals,PlanetOffsets;public NativeArray<SurfaceAttributes> Attributes;
        public NativeArray<SurfaceSampleStatus> GeometryStatuses,AttributeStatuses;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));var point=PlanetField.Surface(Definition,direction);var normal=(double3)PlanetField.Normal(Definition,direction);
            var offset=point-Frame.Position;PlanetOffsets[index]=(float3)offset;Positions[index]=(float3)Frame.ToLocal(point);
            Normals[index]=(float3)new double3(math.dot(normal,Frame.Right),math.dot(normal,Frame.Up),math.dot(normal,Frame.Forward));
            // An explicit single-rock fallback for legacy data without canonical biome masks.
            Attributes[index]=new SurfaceAttributes(SurfaceChannels.MaterialWeights,new float4(0,0,1,0),default);
            GeometryStatuses[index]=SurfaceSampleStatus.Ready;AttributeStatuses[index]=SurfaceSampleStatus.Ready;
        }
    }
}
