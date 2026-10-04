using System;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Bounded authoring capture from an already published height-only cube map. GPU resampling/conditioning;
    /// CPU priority-flood topology is metadata only. Does not run World Orogen again or alter the source snapshot.</summary>
    public sealed class OrogenDetailCaptureSession:IDisposable
    {
        public const long DefaultMaximumWorkingBytes=256L*1024*1024;
        // Producer provenance only: stored conditioning arrays remain authoritative,
        // so this does not reinterpret existing children or change their wire format.
        public const int CapturePolicyRevision=2;
        readonly SurfaceSnapshot source;readonly WorldOrogenDetailRecipe recipe;readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        readonly int resolution,count;readonly long budget;
        ComputeShader shader;ComputeBuffer sourceHeights,heights,geometry,environment;
        AsyncGPUReadbackRequest heightRead,geometryRead,environmentRead;
        bool heightRequested,geometryRequested,environmentRequested,disposed,taken,released;
        Task<WorldOrogenDetailRouting.Result> routing;
        Task<WorldOrogenDetailField> construction;
        WorldOrogenDetailRouting.Result routed;
        WorldOrogenDetailField result;
        int phase;
        public string Stage {get;private set;}="Admitted";
        public string Error {get;private set;}
        public double Progress01 {get;private set;}
        public bool IsCompleted {get;private set;}
        public long EstimatedWorkingBytes {get;}
        public int UniqueRoutingNodes=>routed?.UniqueNodes??0;
        public int UphillSpillEdges=>routed?.UphillSpillEdges??0;
        public static long EstimateWorkingBytes(SurfaceSnapshot source,WorldOrogenDetailRecipe recipe)
        {
            Validate(source,recipe);long n=6L*(recipe.ConditioningResolution+1)*(recipe.ConditioningResolution+1),s=6L*(source.Resolution+1)*(source.Resolution+1);
            // Source immutable retained + upload/copy; output GPU/readbacks/copies/digest; routing scratch and bounded range construction.
            return checked(s*12+6*256+n*192+WorldOrogenDetailRouting.EstimateScratchBytes(recipe.ConditioningResolution)+2*1024*1024);
        }
        OrogenDetailCaptureSession(SurfaceSnapshot source,WorldOrogenDetailRecipe recipe,long maximumWorkingBytes)
        {
            Validate(source,recipe);this.source=source;this.recipe=recipe;resolution=recipe.ConditioningResolution;count=checked(6*(resolution+1)*(resolution+1));budget=maximumWorkingBytes;
            EstimatedWorkingBytes=EstimateWorkingBytes(source,recipe);
            if(maximumWorkingBytes<=0||EstimatedWorkingBytes>maximumWorkingBytes)throw new ArgumentException("Orogen detail capture exceeds the declared working-memory budget: "+EstimatedWorkingBytes+" > "+maximumWorkingBytes);
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsAsyncGPUReadback)throw new NotSupportedException("Orogen detail capture requires compute and asynchronous GPU readback.");
            long device=(long)SystemInfo.graphicsMemorySize*1024*1024,gpu=checked(4L*6*(source.Resolution+1)*(source.Resolution+1)+count*(4L+16*2));
            if(device>0&&gpu>device*3/4)throw new ArgumentException("Orogen detail GPU reservation exceeds reported device memory.");
        }
        static void Validate(SurfaceSnapshot source,WorldOrogenDetailRecipe recipe)
        {
            if(source==null||!recipe.IsValid||!recipe.Enabled||source.CanonicalTileLevel!=0||source.Tiles.Count!=6||source.StructuralField!=null||source.OrogenDetail!=null||source.Detail.AmplitudeMetres!=0||source.Regions.Count!=0||source.Stamps.Count!=0)
                throw new ArgumentException("Capture requires an enabled policy and a retained height-only six-face base map.");
            for(int i=0;i<6;i++){var t=source.Tiles[i];if(t.Key.Face!=i||t.Key.Level!=0||t.Key.X!=0||t.Key.Y!=0||t.Resolution!=source.Resolution||t.Channels!=SurfaceChannels.None)throw new ArgumentException("Capture requires a complete canonical height-only cube base.");}
        }
        public static OrogenDetailCaptureSession Begin(SurfaceSnapshot sourceSnapshot,WorldOrogenDetailRecipe detailRecipe,long maximumWorkingBytes=DefaultMaximumWorkingBytes)
            =>new OrogenDetailCaptureSession(sourceSnapshot,detailRecipe,maximumWorkingBytes);
        /// <summary>Unity main thread. Each call advances a bounded dispatch or polls an asynchronous boundary.</summary>
        public bool Step(Func<bool> cancelled=null)
        {
            if(disposed)throw new ObjectDisposedException(nameof(OrogenDetailCaptureSession));if(IsCompleted||Error!=null)return IsCompleted;
            try
            {
                if(cancelled?.Invoke()==true)cancellation.Cancel();if(cancellation.IsCancellationRequested)throw new OperationCanceledException();
                switch(phase)
                {
                    case 0:
                        Stage="GPU final-map capture";shader=Resources.Load<ComputeShader>("OrogenDetailCapture");if(shader==null)throw new InvalidOperationException("OrogenDetailCapture compute resource is missing.");
                        int size=(source.Resolution+1)*(source.Resolution+1);var values=new float[6*size];for(int face=0;face<6;face++)for(int i=0;i<size;i++)values[face*size+i]=source.Tiles[face].HeightAt(i);
                        sourceHeights=WorldOrogenGpuGraph.Upload(values,4);heights=new ComputeBuffer(count,4);geometry=new ComputeBuffer(count,16);environment=new ComputeBuffer(count,16);
                        int capture=shader.FindKernel("CaptureFinalHeights");Bind(capture);WorldOrogenGpuState.DispatchBounded(shader,capture,count,true);
                        heightRequested=true;heightRead=AsyncGPUReadback.Request(heights,_=>OnReadbackComplete());phase=1;Progress01=.15;break;
                    case 1:
                        if(!heightRead.done)return false;if(heightRead.hasError)throw new InvalidOperationException("Final-map capture readback failed.");
                        var captured=heightRead.GetData<float>().ToArray();Stage="Final spill-tree routing";
                        routing=Task.Run(()=>WorldOrogenDetailRouting.Build(resolution,source.Recipe.Radius,source.Recipe.SeaLevel,captured,budget,()=>cancellation.IsCancellationRequested));
                        phase=2;Progress01=.3;break;
                    case 2:
                        if(!routing.IsCompleted)return false;routed=routing.GetAwaiter().GetResult();Stage="GPU local conditioning";
                        int condition=shader.FindKernel("ConditionFinalMap");Bind(condition);
                        WorldOrogenGpuState.DispatchBounded(shader,condition,count,true);
                        geometryRequested=true;geometryRead=AsyncGPUReadback.Request(geometry,_=>OnReadbackComplete());
                        environmentRequested=true;environmentRead=AsyncGPUReadback.Request(environment,_=>OnReadbackComplete());phase=3;Progress01=.65;break;
                    case 3:
                        if(!geometryRead.done||!environmentRead.done)return false;if(geometryRead.hasError||environmentRead.hasError)throw new InvalidOperationException("Conditioning readback failed.");
                        var g=geometryRead.GetData<float4>().ToArray();var e=environmentRead.GetData<float4>().ToArray();var f=routed.Flow;
                        Stage="Immutable detail field";construction=Task.Run(()=>{if(cancellation.IsCancellationRequested)throw new OperationCanceledException();var field=new WorldOrogenDetailField(recipe,source.Recipe.Radius,source.Recipe.SeaLevel,source.Revision.BaseDigest,resolution,g,f,e);if(cancellation.IsCancellationRequested)throw new OperationCanceledException();return field;});
                        phase=4;Progress01=.85;break;
                    case 4:
                        if(!construction.IsCompleted)return false;result=construction.GetAwaiter().GetResult();IsCompleted=true;Stage="Detail capture complete";Progress01=1;Release();break;
                }
            }
            catch(Exception ex){Error=ex is OperationCanceledException?"Cancelled":ex.ToString();Stage=ex is OperationCanceledException?"Cancelled":"Failed";cancellation.Cancel();ReleaseWhenSafe();}
            return IsCompleted;
        }
        void Bind(int kernel)
        {
            shader.SetInt("_OrogenResolution",resolution);shader.SetInt("_OrogenSourceResolution",source.Resolution);shader.SetInt("_OrogenSampleCount",count);
            shader.SetBuffer(kernel,"_OrogenSourceHeights",sourceHeights);shader.SetBuffer(kernel,"_OrogenCapturedHeights",heights);
            shader.SetBuffer(kernel,"_OrogenGeometry",geometry);shader.SetBuffer(kernel,"_OrogenEnvironment",environment);
            Bits("_OrogenRadiusBits",source.Recipe.Radius);Bits("_OrogenSeaBits",source.Recipe.SeaLevel);
            Bits("_OrogenMaximumAmplitudeBits",recipe.MaximumAmplitudeMetres);Bits("_OrogenReliefFractionBits",recipe.ReliefFraction);Bits("_OrogenCoastFadeBits",recipe.CoastFadeMetres);
        }
        void Bits(string name,double value){long b=BitConverter.DoubleToInt64Bits(value);shader.SetInts(name,unchecked((int)b),unchecked((int)(b>>32)));}
        public WorldOrogenDetailField TakeResult(){if(!IsCompleted||taken||result==null)throw new InvalidOperationException("A complete immutable capture is required.");taken=true;var value=result;result=null;return value;}
        bool ReadsDone=>(!heightRequested||heightRead.done)&&(!geometryRequested||geometryRead.done)&&(!environmentRequested||environmentRead.done);
        void OnReadbackComplete(){if(disposed||Error!=null)ReleaseWhenSafe();}
        void ReleaseWhenSafe(){if(ReadsDone)Release();}
        void Release(){if(released)return;released=true;sourceHeights?.Dispose();heights?.Dispose();geometry?.Dispose();environment?.Dispose();sourceHeights=heights=geometry=environment=null;}
        public void Dispose(){if(disposed)return;disposed=true;cancellation.Cancel();ReleaseWhenSafe();}
    }
}
