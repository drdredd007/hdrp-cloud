using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    // Metre-space surface data for a bounded local region; independent of the orbital LOD.
    public sealed class PlanetLocalPatch : IDisposable
    {
        public NativeArray<float3> Positions,Normals;
        public NativeArray<float4> Colors;
        public NativeArray<int3> Triangles;
        internal NativeArray<SurfaceSampleStatus> SampleStatuses;
        public SurfaceSampleStatus SampleStatus
        {
            get {if(SampleStatuses.IsCreated)foreach(var value in SampleStatuses)if(value!=SurfaceSampleStatus.Ready)return value;return SurfaceSampleStatus.Ready;}
        }
        PlanetLocalPatch() { }
        public static PlanetLocalPatch Build(PlanetDefinition definition,PlanetSurfaceAddress address,int2 key,double size,int resolution)
        {
            using(var pending=Schedule(definition,address,key,size,resolution)) return pending.Complete();
        }
        public static PlanetLocalPatchRequest Schedule(PlanetDefinition definition,PlanetSurfaceAddress address,int2 key,double size,int resolution)
        {
            if(!PlanetSurfaceCoordinates.TryResolve(definition,address,out var frame))
                throw new ArgumentException("A ready surface and valid address are required.");
            return ScheduleInFrame(definition,frame,key,size,resolution);
        }
        /// <summary>Rebuild changed geometry in an already-published physical frame. Neither the
        /// original origin nor its tangent axes are recomputed from the new height or normal.</summary>
        public static PlanetLocalPatch BuildInFrame(PlanetDefinition definition,PlanetSurfaceFrame frame,int2 key,double size,int resolution)
        {
            using(var pending=ScheduleInFrame(definition,frame,key,size,resolution))return pending.Complete();
        }
        public static PlanetLocalPatchRequest ScheduleInFrame(PlanetDefinition definition,PlanetSurfaceFrame frame,int2 key,double size,int resolution)
        {
            if(!definition.IsValid || !frame.IsValid || math.lengthsq(frame.Position)<1 || !math.isfinite(size) || size<=0 ||
                resolution<2 || resolution>128 || (resolution&(resolution-1))!=0 ||
                math.any(math.abs((double2)key*size)+size>8192))
                throw new ArgumentException("Use a valid fixed surface frame, power-of-two resolution 2–128, and a local patch within 8192 metres.");
            var result=new PlanetLocalPatch();
            PlanetSurfaceDataLease lease=null;
            try
            {
                if(definition.GeneratorVersion==3 && !PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out lease))
                    throw new InvalidOperationException("Signed surface snapshot is not ready.");
                int count=(resolution+1)*(resolution+1);
                result.Positions=new NativeArray<float3>(count,Allocator.Persistent);
                result.Normals=new NativeArray<float3>(count,Allocator.Persistent);
                result.Colors=new NativeArray<float4>(count,Allocator.Persistent);
                result.Triangles=new NativeArray<int3>(resolution*resolution*2,Allocator.Persistent);
                if(lease!=null)result.SampleStatuses=new NativeArray<SurfaceSampleStatus>(count,Allocator.Persistent);
                int index=0;
                for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)
                {
                    int a=z*(resolution+1)+x,b=a+1,c=a+resolution+1,d=c+1;
                    result.Triangles[index++]=new int3(a,c,b);result.Triangles[index++]=new int3(b,c,d);
                }
                JobHandle job;
                if(lease==null)job=new PlanetLocalPatchJob {Definition=definition,Frame=frame,Key=key,Size=size,Resolution=resolution,
                    Positions=result.Positions,Normals=result.Normals,Colors=result.Colors}.Schedule(count,64);
                else job=new PlanetSurfaceLocalPatchJob {Definition=definition,Frame=frame,Key=key,Size=size,Resolution=resolution,
                    Surface=lease.View,SampleStatuses=result.SampleStatuses,
                    Positions=result.Positions,Normals=result.Normals,Colors=result.Colors}.Schedule(count,64);
                lease?.AddDependency(job);return new PlanetLocalPatchRequest(result,job,lease);
            }
            catch {lease?.Dispose();result.Dispose();throw;}
        }
        public void Dispose()
        {
            if(Positions.IsCreated)Positions.Dispose();if(Normals.IsCreated)Normals.Dispose();
            if(Colors.IsCreated)Colors.Dispose();if(Triangles.IsCreated)Triangles.Dispose();
            if(SampleStatuses.IsCreated)SampleStatuses.Dispose();
        }
    }
    /// <summary>Owns pending native data. TryComplete transfers ownership only after the job finishes.
    /// Explicit Complete and cancellation/Dispose may wait; normal polling never waits for the job.</summary>
    public sealed class PlanetLocalPatchRequest : IDisposable
    {
        PlanetLocalPatch result;
        JobHandle job;
        PlanetSurfaceDataLease lease;
        internal PlanetLocalPatchRequest(PlanetLocalPatch result,JobHandle job,PlanetSurfaceDataLease lease=null) { this.result=result;this.job=job;this.lease=lease; }
        public bool IsCompleted => result!=null && job.IsCompleted;
        public bool TryComplete(out PlanetLocalPatch patch)
        {
            patch=null;
            if(result==null) throw new ObjectDisposedException(nameof(PlanetLocalPatchRequest));
            if(!job.IsCompleted) return false;
            patch=Complete();return true;
        }
        public PlanetLocalPatch Complete()
        {
            if(result==null) throw new ObjectDisposedException(nameof(PlanetLocalPatchRequest));
            job.Complete();lease?.Dispose();lease=null;var patch=result;result=null;return patch;
        }
        public void Dispose()
        {
            if(result==null)return;
            job.Complete();lease?.Dispose();lease=null;result.Dispose();result=null;
        }
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    public struct PlanetLocalPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;
        public PlanetSurfaceFrame Frame;
        public int2 Key;
        public double Size;
        public int Resolution;
        public NativeArray<float3> Positions,Normals;
        public NativeArray<float4> Colors;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            // Compute shared grid coordinates before converting to metres: adjacent patches sample identical inputs.
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));
            Positions[index]=(float3)Frame.ToLocal(PlanetField.Surface(Definition,direction));
            var normal=(double3)PlanetField.Normal(Definition,direction);
            Normals[index]=(float3)new double3(math.dot(normal,Frame.Right),math.dot(normal,Frame.Up),math.dot(normal,Frame.Forward));
            Colors[index]=PlanetField.Color(Definition,direction);
        }
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    public struct PlanetSurfaceLocalPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;
        [ReadOnly] public NativeSurfaceView Surface;
        public NativeArray<SurfaceSampleStatus> SampleStatuses;
        public PlanetSurfaceFrame Frame;
        public int2 Key;
        public double Size;
        public int Resolution;
        public NativeArray<float3> Positions,Normals;
        public NativeArray<float4> Colors;
        public void Execute(int index)
        {
            int x=index%(Resolution+1),z=index/(Resolution+1);
            var local=new double3(((double)Key.x*Resolution+x)*(Size/Resolution),0,((double)Key.y*Resolution+z)*(Size/Resolution));
            var direction=math.normalize(Frame.ToPlanet(local));
            var status=PlanetSurfaceData.TrySurface(Definition,Surface,direction,out var point);
            float3 normal=default;
            if(status==SurfaceSampleStatus.Ready)status=PlanetSurfaceData.TryNormal(Definition,Surface,direction,out normal);
            SampleStatuses[index]=status;
            if(status!=SurfaceSampleStatus.Ready){Positions[index]=new float3(float.NaN);Normals[index]=default;Colors[index]=default;return;}
            Positions[index]=(float3)Frame.ToLocal(point);
            Normals[index]=new float3((float)math.dot(normal,Frame.Right),(float)math.dot(normal,Frame.Up),(float)math.dot(normal,Frame.Forward));
            Colors[index]=PlanetSurfaceData.Color(Definition,direction);
        }
    }
}
