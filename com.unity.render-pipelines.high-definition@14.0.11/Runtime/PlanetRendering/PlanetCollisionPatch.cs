using System;
using SpaceRunner.PlanetTerrain;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed partial class PlanetLocalPatch
    {
        /// <summary>Canonical collision mesh only: positions, triangles and signed sample statuses.
        /// Normal/color arrays are unallocated; the collider derives its contact normals from triangles.</summary>
        public static PlanetLocalPatch BuildCollision(PlanetDefinition definition,PlanetSurfaceAddress address,int2 key,double size,int resolution)
        {using(var pending=ScheduleCollision(definition,address,key,size,resolution))return pending.Complete();}
        public static PlanetLocalPatchRequest ScheduleCollision(PlanetDefinition definition,PlanetSurfaceAddress address,int2 key,double size,int resolution)
        {
            if(!PlanetSurfaceCoordinates.TryResolve(definition,address,out var frame))
                throw new ArgumentException("A ready surface and valid address are required.");
            return ScheduleCollisionInFrame(definition,frame,key,size,resolution);
        }
        public static PlanetLocalPatch BuildCollisionInFrame(PlanetDefinition definition,PlanetSurfaceFrame frame,int2 key,double size,int resolution)
        {using(var pending=ScheduleCollisionInFrame(definition,frame,key,size,resolution))return pending.Complete();}
        public static PlanetLocalPatchRequest ScheduleCollisionInFrame(PlanetDefinition definition,PlanetSurfaceFrame frame,int2 key,double size,int resolution)
        {
            if(!definition.IsValid||!frame.IsValid||math.lengthsq(frame.Position)<1||!math.isfinite(size)||size<=0||
                resolution<2||resolution>128||(resolution&(resolution-1))!=0||math.any(math.abs((double2)key*size)+size>8192))
                throw new ArgumentException("Use a valid fixed surface frame, power-of-two resolution 2–128, and a local patch within 8192 metres.");
            var result=new PlanetLocalPatch();PlanetSurfaceDataLease lease=null;
            try
            {
                if(definition.GeneratorVersion==3&&!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out lease))
                    throw new InvalidOperationException("Signed surface snapshot is not ready.");
                int count=(resolution+1)*(resolution+1);
                result.Positions=new NativeArray<float3>(count,Allocator.Persistent);
                result.Triangles=new NativeArray<int3>(resolution*resolution*2,Allocator.Persistent);
                if(lease!=null)result.SampleStatuses=new NativeArray<SurfaceSampleStatus>(count,Allocator.Persistent);
                int index=0;
                for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)
                {
                    int a=z*(resolution+1)+x,b=a+1,c=a+resolution+1,d=c+1;
                    result.Triangles[index++]=new int3(a,c,b);result.Triangles[index++]=new int3(b,c,d);
                }
                JobHandle job;
                if(lease==null)job=new PlanetLegacyCollisionPatchJob{Definition=definition,Frame=frame,Key=key,Size=size,Resolution=resolution,
                    Positions=result.Positions}.Schedule(count,64);
                else job=new PlanetSurfaceCollisionPatchJob{Definition=definition,Surface=lease.View,Frame=frame,Key=key,Size=size,Resolution=resolution,
                    Positions=result.Positions,SampleStatuses=result.SampleStatuses}.Schedule(count,64);
                lease?.AddDependency(job);return new PlanetLocalPatchRequest(result,job,lease);
            }
            catch{lease?.Dispose();result.Dispose();throw;}
        }
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    public struct PlanetLegacyCollisionPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;
        public PlanetSurfaceFrame Frame;
        public int2 Key;
        public double Size;
        public int Resolution;
        [WriteOnly] public NativeArray<float3> Positions;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));
            Positions[index]=(float3)Frame.ToLocal(PlanetField.Surface(Definition,direction));
        }
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    public struct PlanetSurfaceCollisionPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;
        [ReadOnly] public NativeSurfaceView Surface;
        public PlanetSurfaceFrame Frame;
        public int2 Key;
        public double Size;
        public int Resolution;
        [WriteOnly] public NativeArray<float3> Positions;
        [WriteOnly] public NativeArray<SurfaceSampleStatus> SampleStatuses;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));
            var status=PlanetSurfaceData.TrySurface(Definition,Surface,direction,out var point);
            SampleStatuses[index]=status;
            Positions[index]=status==SurfaceSampleStatus.Ready?(float3)Frame.ToLocal(point):new float3(float.NaN);
        }
    }
}
