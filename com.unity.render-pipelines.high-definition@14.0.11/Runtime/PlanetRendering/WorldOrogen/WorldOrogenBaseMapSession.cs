using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed partial class WorldOrogenBaseMap
    {
        public static Session Begin(WorldOrogenGpuState state,WorldOrogenSettings settings,double radius,int resolution=0,
            WorldOrogenMapView view=WorldOrogenMapView.Terrain,ComputeBuffer koppen=null)=>new Session(state,settings,radius,resolution,view,koppen);

        /// <summary>GPU projection with asynchronous readback; CPU index and immutable capture
        /// run on workers. Call Step and Dispose from the Unity API thread.</summary>
        public sealed class Session:IDisposable
        {
            readonly WorldOrogenGpuState state;
            readonly WorldOrogenSettings settings;
            readonly double radius;
            readonly int resolution;
            readonly WorldOrogenMapView view;
            readonly ComputeBuffer koppen;
            readonly List<ComputeBuffer> temporary=new List<ComputeBuffer>();
            readonly Task<SpatialIndex> index;
            Task<Captured> capture;
            AsyncGPUReadbackRequest readback,failureReadback;
            ComputeShader shader;
            WorldOrogenBaseMap result;
            int phase,face,stopped;
            bool disposed,taken;
            public string Stage {get;private set;}="Base map spatial index";
            public string Error {get;private set;}
            public double Progress01 {get;private set;}
            public bool IsCompleted {get;private set;}
            public long EstimatedWorkingBytes {get;}
            sealed class Captured {internal SurfaceSnapshot Surface;internal Color32[][] Colour;}

            internal Session(WorldOrogenGpuState state,WorldOrogenSettings settings,double radius,int resolution,WorldOrogenMapView view,ComputeBuffer koppen)
            {
                this.state=state??throw new ArgumentNullException(nameof(state));this.settings=settings?.Clone()??throw new ArgumentNullException(nameof(settings));
                if(!settings.Validate(out var error))throw new ArgumentException(error);
                if(!(radius>20000)||!math.isfinite(radius))throw new ArgumentException("A planet radius above 20 km is required.");
                this.radius=radius;this.resolution=resolution==0?RecommendedResolution(state.RegionCount):resolution;this.view=view;this.koppen=koppen;
                if(!CubeSurface.ValidResolution(this.resolution)||this.resolution>2048)throw new ArgumentOutOfRangeException(nameof(resolution));
                if((view==WorldOrogenMapView.Satellite||view==WorldOrogenMapView.Climate)&&koppen==null)throw new InvalidOperationException("Calculate climate before choosing this map.");
                EstimatedWorkingBytes=WorkingBytes(state,this.resolution);
                if(EstimatedWorkingBytes>4L*1024*1024*1024)throw new InvalidOperationException("Base map exceeds the explicit 4 GiB working budget. Use a smaller map resolution.");
                index=Task.Run(()=>new SpatialIndex(state.Graph.Source.Directions,WorkerCancelled));
            }
            bool WorkerCancelled()=>Volatile.Read(ref stopped)!=0;
            ComputeBuffer Keep(ComputeBuffer value){temporary.Add(value);return value;}
            void CheckWorker(){if(WorkerCancelled())throw new OperationCanceledException();}
            public bool Step(Func<bool> cancelled=null)
            {
                if(disposed)throw new ObjectDisposedException(nameof(Session));if(IsCompleted||Error!=null)return IsCompleted;
                try
                {
                    if(cancelled?.Invoke()==true)Interlocked.Exchange(ref stopped,1);CheckWorker();
                    if(phase==0)
                    {
                        if(!index.IsCompleted)return false;
                        if(index.IsFaulted)throw index.Exception.InnerException;
                        var tree=index.GetAwaiter().GetResult();var graph=state.Graph.Source;
                        shader=UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("WorldOrogenBaseMap"));
                        if(!shader)throw new InvalidOperationException("WorldOrogenBaseMap compute resource is unavailable.");
                        int kernel=shader.FindKernel("BuildBaseMap");if(!shader.IsSupported(kernel))throw new NotSupportedException("World Orogen base map kernel is unsupported.");
                        var nodes=Keep(WorldOrogenGpuGraph.Upload(tree.Nodes,16));
                        var triangles=Keep(WorldOrogenGpuGraph.Upload(graph.NeighborTriangles,4));
                        var centres=Keep(WorldOrogenGpuGraph.Upload(graph.TriangleCenters(),12));
                        var neutral=Keep(WorldOrogenGpuGraph.Upload(new uint[state.RegionCount],4));
                        var output=Keep(new ComputeBuffer(checked(6*(resolution+1)*(resolution+1)),16));var failures=Keep(new ComputeBuffer(1,4));failures.SetData(new uint[]{0});
                        state.Bind(shader,kernel);shader.SetBuffer(kernel,"_MapNodes",nodes);shader.SetBuffer(kernel,"_MapNeighborTriangles",triangles);shader.SetBuffer(kernel,"_MapCenters",centres);
                        shader.SetBuffer(kernel,"_MapKoppen",koppen??neutral);shader.SetBuffer(kernel,"_MapOutput",output);shader.SetBuffer(kernel,"_MapFailures",failures);
                        shader.SetInt("_MapRoot",tree.Root);shader.SetInt("_MapResolution",resolution);shader.SetInt("_MapView",(int)view);
                        shader.Dispatch(kernel,(resolution+8)/8,(resolution+8)/8,6);
                        readback=AsyncGPUReadback.Request(output);failureReadback=AsyncGPUReadback.Request(failures);phase=1;Stage="Base map GPU projection";Progress01=.3;
                    }
                    else if(phase==1)
                    {
                        if(!readback.done||!failureReadback.done)return false;
                        if(readback.hasError||failureReadback.hasError)throw new InvalidOperationException("Base map GPU readback failed.");
                        uint missing=failureReadback.GetData<uint>()[0];if(missing!=0)throw new InvalidOperationException($"Source cell fan projection left {missing} samples uncovered. No partial map was published.");
                        var values=readback.GetData<float4>().ToArray();ReleaseTemporary();
                        capture=Task.Run(()=>
                        {
                            CheckWorker();var surface=CaptureSurface(values,resolution,settings,radius,view,WorkerCancelled);
                            int faceSamples=(resolution+1)*(resolution+1);var colours=new Color32[6][];
                            for(int f=0;f<6;f++){CheckWorker();colours[f]=new Color32[faceSamples];for(int i=0;i<faceSamples;i++){var c=values[f*faceSamples+i];colours[f][i]=new Color(c.x,c.y,c.z,1);}}
                            return new Captured{Surface=surface,Colour=colours};
                        });phase=2;Stage="Base map immutable capture";Progress01=.6;
                    }
                    else if(phase==2)
                    {
                        if(!capture.IsCompleted)return false;if(capture.IsFaulted)throw capture.Exception.InnerException;
                        var value=capture.GetAwaiter().GetResult();int side=resolution+1;
                        result=new WorldOrogenBaseMap{Surface=value.Surface,Colour=new Texture2DArray(side,side,6,TextureFormat.RGBA32,false,true){name="World Orogen "+view,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp},ConfigurationDigest=settings.ConfigurationDigest(),Resolution=resolution,View=view};
                        phase=3;Stage="Base map texture upload";
                    }
                    else
                    {
                        result.Colour.SetPixels32(capture.Result.Colour[face],face);face++;Progress01=.7+.3*face/6;
                        if(face==6){result.Colour.Apply(false,false);IsCompleted=true;Stage="Base map ready";}
                    }
                }
                catch(Exception e){Interlocked.Exchange(ref stopped,1);Error=e is OperationCanceledException?"Cancelled":e.ToString();Stage=e is OperationCanceledException?"Cancelled":"Failed";ReleaseTemporary();result?.Dispose();result=null;}
                return IsCompleted;
            }
            public WorldOrogenBaseMap TakeResult()
            {if(!IsCompleted||taken)throw new InvalidOperationException("Complete the base map before taking its result.");taken=true;var value=result;result=null;return value;}
            void ReleaseTemporary()
            {foreach(var buffer in temporary)buffer.Dispose();temporary.Clear();if(shader){UnityEngine.Object.DestroyImmediate(shader);shader=null;}}
            public void Dispose(){if(disposed)return;disposed=true;Interlocked.Exchange(ref stopped,1);ReleaseTemporary();if(!taken)result?.Dispose();result=null;}
        }
    }
}
