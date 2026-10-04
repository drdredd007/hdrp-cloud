// Derived from World Orogen js/terrain-post.js and js/generate.js.
// https://github.com/raguilar011095/planet_heightmap_generation
// Commit cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Rendering;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    public readonly struct WorldOrogenPostProgress
    {
        public readonly string Stage;
        public readonly int Iteration, Iterations, Dispatches, ElevationReadbacks;
        internal WorldOrogenPostProgress(string stage,int iteration,int iterations,int dispatches,int reads)
        {Stage=stage;Iteration=iteration;Iterations=iterations;Dispatches=dispatches;ElevationReadbacks=reads;}
    }

    public readonly struct WorldOrogenPostResult
    {
        public readonly int Dispatches,ElevationReadbacks,PriorityFloodPlans,StableElevationSorts;
        public readonly bool TerrainReady;
        /// <summary>Borrowed GPU outputs. The generating WorldOrogenGpuState owns their lifetime.</summary>
        public readonly ComputeBuffer NormalizedElevation,HeightMetres,PrePostElevation,PreErosionElevation,ErosionDelta;
        public bool ClimateReady=>false;
        internal WorldOrogenPostResult(WorldOrogenGpuState state,int dispatches,int reads,int floods,int sorts)
        {Dispatches=dispatches;ElevationReadbacks=reads;PriorityFloodPlans=floods;StableElevationSorts=sorts;TerrainReady=true;
         NormalizedElevation=state.Elevation;HeightMetres=state.Field("_WorldHeightMetres");PrePostElevation=state.Field("_WorldPrePostElevation");PreErosionElevation=state.Field("_WorldPreErosionElevation");ErosionDelta=state.Field("_WorldErosionDelta");}
    }

    /// <summary>
    /// Exact upstream stage order. Height mutation and neighbor passes execute on GPU.
    /// The upstream binary priority queue and stable elevation sort are CPU planning
    /// exceptions, with explicit synchronous GPU readbacks; this is not an all-GPU pipeline.
    /// Must be called on Unity's compute/API thread. Cancellation leaves an unpublished state.
    /// </summary>
    public static class WorldOrogenPostProcessor
    {
        public const long MaximumWorkingBytes=4L*1024*1024*1024;
        const int OrderedChunk=256;

        public static WorldOrogenPostResult Process(WorldOrogenGpuState state,WorldOrogenSettings settings,
            Action<WorldOrogenPostProgress> progress=null,Func<bool> cancelled=null,long maximumWorkingBytes=MaximumWorkingBytes)
        {
            var runner=CreateRunner(state,settings,cancelled,progress,true,maximumWorkingBytes);
            var execution=runner.Execute();try{while(execution.MoveNext()){}return runner.Result;}finally{execution.Dispose();}
        }
        /// <summary>GPU copy for reapplying post settings to a previously captured input.
        /// The caller supplies its retained PrePostElevation, not a CPU-computed replacement.</summary>
        public static void CopyElevationFrom(WorldOrogenGpuState state,ComputeBuffer source)
        {
            if(state==null||source==null||!source.IsValid()||source.count!=state.RegionCount||source.stride!=4)
                throw new ArgumentException("A live, matching scalar GPU elevation source is required.");
            if(source==state.Elevation)return;
            var shader=Resources.Load<ComputeShader>("WorldOrogenPost");if(shader==null)throw new InvalidOperationException("World Orogen post resource is unavailable.");
            int kernel=shader.FindKernel("CopyElevation");state.Bind(shader,kernel);
            shader.SetBuffer(kernel,"_WorldElevation",source);shader.SetBuffer(kernel,"_PostOriginal",state.Elevation);
            shader.GetKernelThreadGroupSizes(kernel,out uint x,out uint y,out uint z);if(x==0||y!=1||z!=1)throw new InvalidOperationException("Expected a one-dimensional elevation copy.");
            shader.Dispatch(kernel,(state.RegionCount+(int)x-1)/(int)x,1,1);
        }
        internal static Runner CreateRunner(WorldOrogenGpuState state,WorldOrogenSettings settings,Func<bool> cancelled,
            Action<WorldOrogenPostProgress> progress,bool synchronous,long maximumWorkingBytes)
        {
            if(state==null||settings==null)throw new ArgumentNullException(state==null?nameof(state):nameof(settings));
            if(!settings.Validate(out string error))throw new ArgumentException(error,nameof(settings));
            if(!SystemInfo.supportsComputeShaders||SystemInfo.graphicsShaderLevel<50)
                throw new NotSupportedException("World Orogen requires a compute-capable SM5 device.");
            WorldOrogenPostBudget.Require(WorldOrogenPostBudget.EstimateTerrain(state),maximumWorkingBytes);
            var shader=Resources.Load<ComputeShader>("WorldOrogenPost");
            if(shader==null)throw new InvalidOperationException("WorldOrogenPost compute resource is unavailable.");
            return new Runner(state,shader,settings,cancelled,progress,synchronous);
        }

        internal sealed class Runner
        {
            readonly WorldOrogenGpuState state;
            readonly ComputeShader shader;
            readonly bool synchronous;
            int stopped;
            internal WorldOrogenPostResult Result {get;private set;}
            internal void Cancel()=>Interlocked.Exchange(ref stopped,1);
            bool WorkerCancelled()=>Volatile.Read(ref stopped)!=0;
            readonly WorldOrogenSettings settings;
            readonly Func<bool> cancelled;
            readonly Action<WorldOrogenPostProgress> progress;
            readonly Dictionary<string,ComputeBuffer> fields=new Dictionary<string,ComputeBuffer>();
            readonly Dictionary<string,int> kernels=new Dictionary<string,int>();
            readonly double[] parameters=new double[12];
            readonly int[] offsets,neighbors;
            readonly float[] elevation;
            readonly uint[] ocean;
            readonly int[] order,rank;
            readonly uint[] gpuOrder;
            readonly ComputeBuffer warpNoise,detailNoise,detailNoise2;
            int dispatches,readbacks,landCount,floodPlans,sorts;
            ComputeBuffer selectedNoise;

            internal Runner(WorldOrogenGpuState state,ComputeShader shader,WorldOrogenSettings settings,
                Func<bool> cancelled,Action<WorldOrogenPostProgress> progress,bool synchronous)
            {
                this.synchronous=synchronous;
                this.state=state;this.shader=shader;this.settings=settings.Clone();this.cancelled=cancelled;this.progress=progress;
                offsets=state.Graph.Source.Offsets;neighbors=state.Graph.Source.Neighbors;
                elevation=new float[state.RegionCount];ocean=new uint[state.RegionCount];order=new int[state.RegionCount];rank=new int[state.RegionCount];gpuOrder=new uint[state.RegionCount];
                string[] scalar={"_PostSmoothLocked","_PostOriginal","_PostGlacialIndex","_PostIceFlow","_PostFlow","_PostDelta","_PostCellDistance","_PostDrain","_PostIceTarget","_PostIceUpstream","_PostOrder","_PostFloodSurface","_PostFloodDrain","_WorldHeightMetres","_WorldPrePostElevation","_WorldPreErosionElevation","_WorldErosionDelta"};
                foreach(string name in scalar)fields.Add(name,state.Field(name));
                fields.Add("_PostParameters",state.Field("_PostParameters",8,12));
                // Separate read/write names keep ordered DX11 kernels below eight UAVs.
                fields.Add("_PostOceanWrite",state.OceanFlags);
                fields.Add("_PostSmoothLockedWrite",fields["_PostSmoothLocked"]);
                fields.Add("_PostGlacialIndexWrite",fields["_PostGlacialIndex"]);
                fields.Add("_PostCellDistanceWrite",fields["_PostCellDistance"]);
                fields.Add("_PostDrainWrite",fields["_PostDrain"]);
                fields.Add("_PostIceTargetWrite",fields["_PostIceTarget"]);
                warpNoise=state.Field("_PostWarpPermutation",4,512);warpNoise.SetData(WorldOrogenNoise.CreatePermutation(settings.Seed+9999));
                detailNoise=state.Field("_PostDetailPermutation",4,512);detailNoise.SetData(WorldOrogenNoise.CreatePermutation(settings.Seed+31337));
                detailNoise2=state.Field("_PostDetailPermutation2",4,512);detailNoise2.SetData(WorldOrogenNoise.CreatePermutation(settings.Seed+13579));
                selectedNoise=state.NoisePermutation;
            }
            void Check(){if(WorkerCancelled()||cancelled?.Invoke()==true)throw new OperationCanceledException("World Orogen post processing cancelled before publication.");}
            void Report(string stage,int iteration=0,int count=1){Check();progress?.Invoke(new WorldOrogenPostProgress(stage,iteration,count,dispatches,readbacks));}
            int Kernel(string name){if(!kernels.TryGetValue(name,out int k)){k=shader.FindKernel(name);kernels.Add(name,k);}return k;}
            void Dispatch(string name,bool swap=false,ComputeBuffer capture=null)
            {
                Check();int k=Kernel(name);state.Bind(shader,k);
                foreach(var field in fields)shader.SetBuffer(k,field.Key,field.Value);
                shader.SetBuffer(k,"_WorldNoisePermutation",selectedNoise);
                if(capture!=null)shader.SetBuffer(k,"_PostOriginal",capture);
                fields["_PostParameters"].SetData(parameters);
                shader.GetKernelThreadGroupSizes(k,out uint x,out uint y,out uint z);
                if(y!=1||z!=1||x==0)throw new InvalidOperationException("World Orogen post requires one-dimensional kernels.");
                shader.Dispatch(k,(state.RegionCount+(int)x-1)/(int)x,1,1);dispatches++;
                if(swap)state.SwapElevation();
            }
            IEnumerable<object> Ordered(int mode,int count)
            {
                int kernel=Kernel("OrderedErosion");fields["_PostParameters"].SetData(parameters);
                for(int start=0;start<count;start+=OrderedChunk)
                {
                    Check();state.Bind(shader,kernel);foreach(var field in fields)shader.SetBuffer(kernel,field.Key,field.Value);
                    shader.SetInt("_PostMode",mode);shader.SetInt("_PostOffset",start);shader.SetInt("_PostCount",Math.Min(OrderedChunk,count-start));
                    shader.Dispatch(kernel,1,1,1);dispatches++;yield return null;
                }
            }
            void UploadOrder(int count,bool reverse=false)
            {
                for(int i=0;i<count;i++)gpuOrder[i]=(uint)order[reverse?count-1-i:i];
                if(count>0)fields["_PostOrder"].SetData(gpuOrder,0,0,count);
            }
            IEnumerable<object> ReadElevation()
            {
                Check();
                if(synchronous)state.Elevation.GetData(elevation);
                else
                {
                    var request=AsyncGPUReadback.Request(state.Elevation);
                    while(!request.done){Check();yield return null;}
                    if(request.hasError)throw new InvalidOperationException("World Orogen elevation readback failed.");
                    request.GetData<float>().CopyTo(elevation);
                }
                readbacks++;Check();yield return null;
            }
            IEnumerable<object> ReadOcean()
            {
                Check();
                if(synchronous)state.OceanFlags.GetData(ocean);
                else
                {
                    var request=AsyncGPUReadback.Request(state.OceanFlags);
                    while(!request.done){Check();yield return null;}
                    if(request.hasError)throw new InvalidOperationException("World Orogen ocean-mask readback failed.");
                    request.GetData<uint>().CopyTo(ocean);
                }
                readbacks++;Check();yield return null;
            }
            IEnumerable<object> SortLand()
            {
                foreach(var item in ReadElevation())yield return item;sorts++;
                Action sort=()=>{for(int i=0;i<landCount;i++)rank[order[i]]=i;
                    Array.Sort(order,0,landCount,Comparer<int>.Create((a,b)=>{int c=elevation[b].CompareTo(elevation[a]);return c!=0?c:rank[a].CompareTo(rank[b]);}));};
                if(synchronous)sort();else{var task=Task.Run(sort);while(!task.IsCompleted){Check();yield return null;}task.GetAwaiter().GetResult();}
                Check();UploadOrder(landCount);yield return null;
            }
            internal IEnumerator<object> Execute()
            {
                Report("Capture pre-post elevation");Dispatch("CopyElevation",capture:fields["_WorldPrePostElevation"]);yield return null;
                if(settings.TerrainWarp>0){Report("Domain warp");selectedNoise=warpNoise;parameters[0]=settings.TerrainWarp;Dispatch("Warp",true);yield return null;}
                Dispatch("FreezeOcean");yield return null;foreach(var item in ReadOcean())yield return item;
                for(int i=0;i<ocean.Length;i++)if(ocean[i]==0)order[landCount++]=i;
                Dispatch("CopyElevation",capture:fields["_WorldPreErosionElevation"]);yield return null;
                if(settings.Smoothing>0)
                {Dispatch("CaptureSmoothLocks");yield return null;int iterations=Round(1+settings.Smoothing*4);parameters[0]=.2+settings.Smoothing*.5;for(int i=0;i<iterations;i++){Report("Bilateral smoothing",i,iterations);Dispatch("Smooth",true);yield return null;}}
                Report("Unipolar detail noise");selectedNoise=detailNoise;parameters[1]=.10;parameters[2]=1;parameters[3]=1;parameters[4]=1;shader.SetInt("_PostMode",0);Dispatch("DetailNoise");yield return null;
                Report("Bipolar detail noise");selectedNoise=detailNoise2;parameters[1]=.05;parameters[2]=2;parameters[3]=2;parameters[4]=.4;shader.SetInt("_PostMode",1);Dispatch("DetailNoise");yield return null;
                foreach(var item in Erode())yield return item;
                if(settings.RidgeSharpening>0)
                {Report("Capture ridge reference");Dispatch("CopyElevation");yield return null;int iterations=Round(1+settings.RidgeSharpening*3);parameters[0]=settings.RidgeSharpening*.08;for(int i=0;i<iterations;i++){Report("Ridge sharpening",i,iterations);Dispatch("Sharpen",true);yield return null;}}
                for(int i=0;i<3;i++){Report("Soil creep",i,3);Dispatch("SoilCreep",true);yield return null;}
                Report("Physical height conversion");Dispatch("HeightMetres");yield return null;Dispatch("ErosionDelta");yield return null;
                Report("Terrain post ready");Result=new WorldOrogenPostResult(state,dispatches,readbacks,floodPlans,sorts);
            }
            IEnumerable<object> Flood(double strength)
            {
                Report("CPU priority-flood queue planning");foreach(var item in ReadElevation())yield return item;
                WorldOrogenFloodPlan plan;
                if(synchronous)plan=WorldOrogenFloodPlan.Create(offsets,neighbors,elevation,ocean,cancelled);
                else{var task=Task.Run(()=>WorldOrogenFloodPlan.Create(offsets,neighbors,elevation,ocean,WorkerCancelled));while(!task.IsCompleted){Check();yield return null;}plan=task.GetAwaiter().GetResult();}
                floodPlans++;Check();fields["_PostFloodSurface"].SetData(plan.Surface);fields["_PostFloodDrain"].SetData(plan.DrainTo);
                for(int i=0;i<gpuOrder.Length;i++)gpuOrder[i]=(uint)i;fields["_PostOrder"].SetData(gpuOrder);parameters[0]=strength;
                Report("GPU ordered flood canyon carving");foreach(var item in Ordered(6,gpuOrder.Length))yield return item;
                for(int i=0;i<plan.AscendingLand.Length;i++)gpuOrder[i]=(uint)plan.AscendingLand[i];
                if(plan.AscendingLand.Length>0)fields["_PostOrder"].SetData(gpuOrder,0,0,plan.AscendingLand.Length);
                foreach(var item in Ordered(7,plan.AscendingLand.Length))yield return item;
                UploadOrder(landCount);yield return null;
            }
            IEnumerable<object> Erode()
            {
                int hi=Round(settings.HydraulicErosion*20),ti=Round(settings.ThermalErosion*10),gi=Round(settings.GlacialErosion*10),total=Math.Max(hi,Math.Max(ti,gi));
                if(total==0||landCount==0)yield break;
                parameters[5]=settings.HydraulicErosion*.0006;parameters[6]=.5;parameters[7]=1;
                parameters[8]=1.2-settings.ThermalErosion*.4;parameters[9]=settings.ThermalErosion*.15;
                parameters[10]=settings.GlacialErosion;parameters[11]=gi>0?1.0/gi:0;
                if(hi>0)foreach(var item in Flood(.5))yield return item;
                Dispatch("GlacialIndex");yield return null;
                bool midDone=false;int mid=Round(total*.75);
                for(int iteration=0;iteration<total;iteration++)
                {
                    Report("Composite erosion",iteration,total);
                    if(!midDone&&iteration>=mid){midDone=true;foreach(var item in Flood(.85))yield return item;}
                    bool glacial=iteration<gi&&settings.GlacialErosion>0,hydraulic=iteration<hi;
                    if(glacial||hydraulic)foreach(var item in SortLand())yield return item;
                    Dispatch("InitializeFlow");yield return null;
                    if(glacial)
                    {shader.SetInt("_PostMode",0);Dispatch("BuildDrainage");yield return null;foreach(var item in Ordered(0,landCount))yield return item;foreach(var item in Ordered(1,landCount))yield return item;foreach(var item in Ordered(2,landCount))yield return item;Dispatch("Fjords");yield return null;Dispatch("ClampLand");yield return null;}
                    if(hydraulic)
                    {if(glacial)foreach(var item in SortLand())yield return item;shader.SetInt("_PostMode",1);Dispatch("BuildDrainage");yield return null;foreach(var item in Ordered(3,landCount))yield return item;UploadOrder(landCount,true);foreach(var item in Ordered(4,landCount))yield return item;UploadOrder(landCount);yield return null;}
                    if(iteration<ti){foreach(var item in Ordered(5,landCount))yield return item;Dispatch("ApplyDelta");yield return null;}
                }
                if(gi>0&&settings.GlacialErosion>0){Dispatch("GlacialSmooth",true);yield return null;}
            }
        }
        internal static int Round(double x)=>(int)Math.Floor(x+.5);
    }

    /// <summary>Upstream queue metadata only: does not carve, fill or upload final height.</summary>
    internal sealed class WorldOrogenFloodPlan
    {
        internal float[] Surface;
        internal int[] DrainTo,AscendingLand;
        internal static WorldOrogenFloodPlan Create(int[] offsets,int[] neighbors,float[] elevation,uint[] ocean,Func<bool> cancelled)
        {
            int n=elevation.Length;
            var labels=new int[n];var stack=new int[n];var sizes=new List<int>();Array.Fill(labels,-1);
            for(int r=0;r<n;r++)
            {
                if((r&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();
                if(ocean[r]==0||labels[r]>=0)continue;int label=sizes.Count,count=0,top=0;stack[top++]=r;labels[r]=label;
                while(top>0){int current=stack[--top];count++;for(int j=offsets[current];j<offsets[current+1];j++){int nb=neighbors[j];if(ocean[nb]!=0&&labels[nb]<0){labels[nb]=label;stack[top++]=nb;}}}
                sizes.Add(count);
            }
            int main=0;for(int i=1;i<sizes.Count;i++)if(sizes[i]>sizes[main])main=i;
            var surface=(float[])elevation.Clone();var drain=new int[n];Array.Fill(drain,-1);var visited=new byte[n];var keys=new float[n];
            for(int r=0;r<n;r++)keys[r]=(float)((double)elevation[r]+CellNoise(r));
            var heap=new Heap(keys,n);
            for(int r=0;r<n;r++)
            {
                if(ocean[r]!=0){visited[r]=1;continue;}
                for(int j=offsets[r];j<offsets[r+1];j++){int nb=neighbors[j];if(ocean[nb]!=0&&labels[nb]==main){visited[r]=1;drain[r]=nb;heap.Push(r);break;}}
            }
            int work=0;
            while(heap.Count>0)
            {
                if((work++&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();
                int r=heap.Pop();double current=surface[r];
                for(int j=offsets[r];j<offsets[r+1];j++){int nb=neighbors[j];if(visited[nb]!=0)continue;visited[nb]=1;drain[nb]=r;if((double)elevation[nb]<current+1e-7){surface[nb]=(float)(current+1e-7);keys[nb]=(float)((double)surface[nb]+CellNoise(nb));}heap.Push(nb);}
            }
            var land=new List<int>();for(int r=0;r<n;r++)if(ocean[r]==0)land.Add(r);
            land.Sort((a,b)=>{int c=surface[a].CompareTo(surface[b]);return c!=0?c:a.CompareTo(b);});
            return new WorldOrogenFloodPlan{Surface=surface,DrainTo=drain,AscendingLand=land.ToArray()};
        }
        static uint JsUint(double number)=>unchecked((uint)(long)(Math.Truncate(number)%4294967296.0));
        internal static double CellNoise(int r)
        {uint h=JsUint((double)r*2654435761.0);h=JsUint((double)((h>>16)^h)*0x45d9f3b);h=(h>>16)^h;return h/4294967295.0*.01;}
        sealed class Heap
        {
            readonly float[] keys;readonly int[] values;internal int Count;
            internal Heap(float[] keys,int capacity){this.keys=keys;values=new int[capacity];}
            internal void Push(int value){int i=Count++;values[i]=value;while(i>0){int p=(i-1)>>1;if(keys[values[i]]>=keys[values[p]])break;int t=values[i];values[i]=values[p];values[p]=t;i=p;}}
            internal int Pop(){int result=values[0],last=values[--Count];if(Count>0){values[0]=last;int i=0;for(;;){int smallest=i,l=2*i+1,r=2*i+2;if(l<Count&&keys[values[l]]<keys[values[smallest]])smallest=l;if(r<Count&&keys[values[r]]<keys[values[smallest]])smallest=r;if(smallest==i)break;int t=values[i];values[i]=values[smallest];values[smallest]=t;i=smallest;}}return result;}
        }
    }
}
