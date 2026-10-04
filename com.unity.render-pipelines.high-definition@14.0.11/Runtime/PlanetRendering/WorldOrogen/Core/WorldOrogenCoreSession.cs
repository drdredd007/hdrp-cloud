// Original World Orogen worker/core orchestration, pinned cc2662b4, GPL-3.0-only.
// Explicit CPU exceptions: Delaunay/CSR, ordered plate fronts, plate-sized reductions,
// seed catalogs/random-frontier BFS, stable ranks and connectivity. Region field values
// (projection, mantle, collision, stress, all elevation features) are actual compute.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class WorldOrogenCoreSession : IDisposable
    {
        public const long DefaultMaximumWorkingBytes=4L*1024*1024*1024;
        readonly WorldOrogenSettings settings;readonly long budget;readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        readonly Task<Bootstrap> bootstrap;IEnumerator execution;WorldOrogenCoreResult result;WorldOrogenGpuState state,coarseState;
        ComputeShader core,terrain;bool disposed,taken;
        public string Stage {get;private set;}="Topology";
        public double Progress01 {get;private set;}
        public string Error {get;private set;}
        public bool IsCompleted {get;private set;}
        public long EstimatedWorkingBytes {get;private set;}
        public long PeakGpuReservationBytes {get;private set;}
        long sourceManagedBytes;
        sealed class Bootstrap {public WorldOrogenGraph Graph,Coarse;public WorldOrogenPlateSet Plates;}
        sealed class Record
        {public ComputeBuffer Stress,Direction,Subduct,Flags;}
        WorldOrogenCoreSession(WorldOrogenSettings source,long maximumWorkingBytes)
        {
            if(source==null)throw new ArgumentNullException(nameof(source));if(!source.Validate(out var error))throw new ArgumentException(error);
            settings=source.Clone();budget=maximumWorkingBytes;
            EstimatedWorkingBytes=checked((long)(settings.Detail+1)*1450+WorldOrogenSettings.CoarseDetail*1000L+16*1024*1024);
            if(budget<=0||EstimatedWorkingBytes>budget)throw new ArgumentException("World Orogen preflight exceeds the declared working-memory limit.");
            // Exact initial closed-sphere graph/basic-bank sizes. Topology-dependent
            // frontiers and catalogs receive their own admission once their counts exist.
            RequireHardware(checked(136L*(settings.Detail+1+WorldOrogenSettings.CoarseDetail+1)+4096),EstimatedWorkingBytes);
            bootstrap=Task.Run(()=>
            {
                Func<bool> stop=()=>cancellation.IsCancellationRequested;
                var graph=WorldOrogenTopology.Build(settings.Detail,settings.Irregularity,new WorldOrogenRandom(settings.Seed),stop);
                var coarse=WorldOrogenTopology.Build(WorldOrogenSettings.CoarseDetail,.75,new WorldOrogenRandom(settings.Seed+137),stop);
                var plates=WorldOrogenPlates.Generate(coarse,settings.Plates,settings.Seed,stop);
                WorldOrogenPlates.AssignOceanLand(coarse,plates,settings.Seed,settings.Continents,settings.ContinentSizeVariety,settings.LandCoverage,stop);
                foreach(int index in settings.ToggledPlateIndices){int p=plates.SeedOrder[index];plates.Ocean[p]=!plates.Ocean[p];}
                foreach(int p in plates.SeedOrder){var rng=new WorldOrogenRandom(p+777);double sea=3+rng.Next()*.5,land=2.4+rng.Next()*.5;plates.Density[p]=plates.Ocean[p]?sea:land;}
                return new Bootstrap{Graph=graph,Coarse=coarse,Plates=plates};
            });
        }
        public static WorldOrogenCoreSession Begin(WorldOrogenSettings settings,long maximumWorkingBytes=DefaultMaximumWorkingBytes)=>new WorldOrogenCoreSession(settings,maximumWorkingBytes);
        /// <summary>Call on the main thread. One dispatch/chunk or asynchronous boundary per step.</summary>
        public bool Step(Func<bool> cancelled=null)
        {
            if(disposed)throw new ObjectDisposedException(nameof(WorldOrogenCoreSession));if(IsCompleted||Error!=null)return IsCompleted;
            try
            {
                if(cancelled?.Invoke()==true)cancellation.Cancel();if(cancellation.IsCancellationRequested)throw new OperationCanceledException();
                if(execution==null){if(!bootstrap.IsCompleted)return false;if(bootstrap.IsFaulted)throw bootstrap.Exception.InnerException;var data=bootstrap.GetAwaiter().GetResult();execution=Run(data);}
                if(!execution.MoveNext()){IsCompleted=true;Progress01=1;Stage="Core complete";}
            }
            catch(Exception e){Error=e is OperationCanceledException?"Cancelled":e.ToString();Stage=e is OperationCanceledException?"Cancelled":"Failed";Release();}
            return IsCompleted;
        }
        public WorldOrogenCoreResult TakeResult()
        {if(!IsCompleted||result==null||taken)throw new InvalidOperationException("All thirteen core stages must complete before publication.");taken=true;var value=result;result=null;state=null;return value;}
        void SetStage(int index,string name){Stage=name;Progress01=Math.Min(.99,index/18.0);}
        bool Stop()=>cancellation.IsCancellationRequested;
        IEnumerator Worker<T>(Func<T> build,Action<T> accept)
        {var task=Task.Run(build);while(!task.IsCompleted)yield return null;if(task.IsFaulted)throw task.Exception.InnerException;if(task.IsCanceled)throw new OperationCanceledException();accept(task.GetAwaiter().GetResult());}
        IEnumerator Read<T>(ComputeBuffer buffer,Action<T[]> accept) where T:struct
        {var request=AsyncGPUReadback.Request(buffer);while(!request.done)yield return null;if(request.hasError)throw new InvalidOperationException("World Orogen GPU readback failed.");accept(request.GetData<T>().ToArray());}
        static IEnumerable<object> Enumerate(IEnumerator iterator){while(iterator.MoveNext())yield return iterator.Current;}
        static void RequireHardware(long gpuBytes,long workingBytes)
        {
            long device=SystemInfo.graphicsMemorySize>0?(long)SystemInfo.graphicsMemorySize*1024*1024:0;
            if(device>0&&gpuBytes>device*3/4)throw new ArgumentException("World Orogen GPU reservation exceeds 75% of reported device memory.");
            long host=SystemInfo.systemMemorySize>0?(long)SystemInfo.systemMemorySize*1024*1024:0;
            if(host>0&&workingBytes>host/2)throw new ArgumentException("World Orogen working reservation exceeds half of reported host memory.");
        }
        void RequireAllocation(long gpuBytes,long managedBytes)
        {
            long working=checked(gpuBytes+managedBytes);
            if(working>budget)throw new ArgumentException("World Orogen allocation exceeds the declared working-memory limit: "+working+" > "+budget+" bytes.");
            RequireHardware(gpuBytes,working);PeakGpuReservationBytes=Math.Max(PeakGpuReservationBytes,gpuBytes);
        }
        // Graphs are counted separately. Reserve live Float32 readbacks/tectonic arrays,
        // spatial fields, ordered seed sets, list capacity and worker/rank scratch.
        long ManagedReserve=>checked(sourceManagedBytes+256L*(settings.Detail+1)+16*1024*1024);
        void RemoveFields(params string[] names){foreach(string name in names)state.RemoveField(name);}
        void RemoveRecord(string prefix){RemoveFields(prefix+"Stress",prefix+"Direction",prefix+"Subduct",prefix+"Flags");}
        void RemovePlate(string prefix){RemoveFields(prefix+"Plate",prefix+"Motion",prefix+"Density",prefix+"Ocean");}
        ComputeBuffer Buffer(string name,int stride=4,int count=-1)
        {
            int n=count<0?state.RegionCount:count;long extra=(long)Math.Max(1,n)*stride;
            // Rebinding a live field reserves no second copy; Field still validates shape.
            if(state.TryGetField(name,out _))return state.Field(name,stride,n);
            RequireAllocation(checked(state.EstimatedBytes+(coarseState?.EstimatedBytes??0)+extra),ManagedReserve);return state.Field(name,stride,n);
        }
        ComputeBuffer Upload(string name,Array values,int stride=4){var b=Buffer(name,stride,values.Length);if(values.Length>0)b.SetData(values);return b;}
        static void Double(ComputeShader shader,string name,double value){long bits=BitConverter.DoubleToInt64Bits(value);shader.SetInts(name+"Bits",unchecked((int)bits),unchecked((int)(bits>>32)));}
        Record NewRecord(string label)
        {return new Record{Stress=Buffer(label+"Stress"),Direction=Buffer(label+"Direction",12),Subduct=Buffer(label+"Subduct"),Flags=Buffer(label+"Flags",16)};}
        void BindRecord(int kernel,Record record)
        {core.SetBuffer(kernel,"_WorldStress",record.Stress);core.SetBuffer(kernel,"_WorldStressDirection",record.Direction);core.SetBuffer(kernel,"_WorldSubduct",record.Subduct);core.SetBuffer(kernel,"_WorldCollisionFlags",record.Flags);}
        void BindPlate(int kernel,WorldOrogenPlateSet plates,string prefix)
        {
            core.SetBuffer(kernel,"_WorldPlate",Upload(prefix+"Plate",plates.RegionPlate));core.SetBuffer(kernel,"_PlateMotion",Upload(prefix+"Motion",plates.Motion,32));core.SetBuffer(kernel,"_PlateDensity",Upload(prefix+"Density",plates.Density,8));
            var ocean=new uint[plates.Ocean.Length];for(int i=0;i<ocean.Length;i++)ocean[i]=plates.Ocean[i]?1u:0u;core.SetBuffer(kernel,"_PlateOcean",Upload(prefix+"Ocean",ocean));
        }
        void DispatchCore(int kernel,int count=-1){state.Dispatch(core,kernel,count);}
        Record Collision(WorldOrogenPlateSet plates,string prefix)
        {int k=core.FindKernel("FindCollisions");var r=NewRecord(prefix);BindRecord(k,r);BindPlate(k,plates,prefix);Double(core,"_CollisionDt",.01/Math.Max(1,Math.Sqrt(state.RegionCount/10000.0)));DispatchCore(k);return r;}
        void BindSources(int kernel,Record small,Record super)
        {core.SetBuffer(kernel,"_SmallStress",small.Stress);core.SetBuffer(kernel,"_SmallSubduct",small.Subduct);core.SetBuffer(kernel,"_SmallDirection",small.Direction);core.SetBuffer(kernel,"_SuperStress",super.Stress);core.SetBuffer(kernel,"_SuperSubduct",super.Subduct);core.SetBuffer(kernel,"_SuperDirection",super.Direction);core.SetBuffer(kernel,"_SourceCollisionFlags",super.Flags);}
        Record Copy(Record source,string prefix)
        {var result=NewRecord(prefix);int k=core.FindKernel("CopyTectonic");BindSources(k,source,source);BindRecord(k,result);DispatchCore(k);return result;}
        IEnumerator Propagate(Record record,WorldOrogenPlateSet plates,string prefix)
        {
            int capacity=checked(state.RegionCount*state.Graph.Source.MaximumDegree);var front=Buffer(prefix+"Frontier",4,capacity);var next=Buffer(prefix+"NextFrontier",4,capacity);var counters=Buffer(prefix+"Counters",4,3);counters.SetData(new uint[3]);
            int initialize=core.FindKernel("InitializeStressFrontier");BindRecord(initialize,record);core.SetBuffer(initialize,"_StressFrontier",front);core.SetBuffer(initialize,"_StressCounters",counters);core.SetInt("_StressFrontierCapacity",capacity);
            for(int start=0;start<state.RegionCount;start+=1024){core.SetInt("_ChunkStart",start);core.SetInt("_ChunkCount",Math.Min(1024,state.RegionCount-start));DispatchCore(initialize,1);yield return null;}
            uint[] counts=null;foreach(var item in Enumerate(Read<uint>(counters,v=>counts=v)))yield return item;
            if(counts[2]!=0||counts[0]>(uint)capacity)throw new ArgumentException("Ordered stress frontier exceeds its declared capacity.");
            double scale=Math.Sqrt(state.RegionCount/10000.0);Double(core,"_StressDecay",Math.Pow(.7,1/scale));Double(core,"_SubductDecay",Math.Pow(.7*.45,1/scale));int passes=Math.Max(1,(int)Math.Floor(15*scale+.5));
            int propagate=core.FindKernel("PropagateStressChunk");BindRecord(propagate,record);BindPlate(propagate,plates,prefix);core.SetBuffer(propagate,"_StressCounters",counters);
            for(int pass=0;pass<passes&&counts[0]>0;pass++)
            {
                counters.SetData(new[]{counts[0],0u,0u});core.SetBuffer(propagate,"_StressFrontier",front);core.SetBuffer(propagate,"_StressNextFrontier",next);
                for(int start=0;start<(int)counts[0];start+=256){core.SetInt("_ChunkStart",start);core.SetInt("_ChunkCount",Math.Min(256,(int)counts[0]-start));DispatchCore(propagate,1);yield return null;}
                foreach(var item in Enumerate(Read<uint>(counters,v=>counts=v)))yield return item;
                if(counts[2]!=0||counts[1]>(uint)capacity)throw new ArgumentException("Ordered stress propagation exceeded its explicit frontier capacity; no entries were silently dropped.");
                counts[0]=counts[1];var swap=front;front=next;next=swap;
            }
            // The last counters request completed all frontier reads/writes. Direction
            // smoothing uses only the record/graph, so both queue owners can retire now.
            RemoveFields(prefix+"Frontier",prefix+"NextFrontier",prefix+"Counters");
            int smooth=core.FindKernel("SmoothStressDirectionChunk");BindRecord(smooth,record);BindPlate(smooth,plates,prefix);
            for(int pass=0;pass<2;pass++)for(int start=0;start<state.RegionCount;start+=512){core.SetInt("_ChunkStart",start);core.SetInt("_ChunkCount",Math.Min(512,state.RegionCount-start));DispatchCore(smooth,1);yield return null;}
        }
        IEnumerator Physics(WorldOrogenGraph graph,WorldOrogenPlateSet plates,WorldOrogenGpuState gpu,double seed,double multiplier,Action<float[]> accept)
        {
            WorldOrogenPlatePhysics plan=null;foreach(var item in Enumerate(Worker(()=>new WorldOrogenPlatePhysics(graph,plates,seed,multiplier,Stop),v=>plan=v)))yield return item;
            var cells=Buffer("Physics"+seed+"Cells",48,plan.Cells.Length);cells.SetData(plan.Cells);var flow=Buffer("Physics"+seed+"Flow",24,graph.RegionCount);var field=Buffer("Physics"+seed+"Field",4,graph.RegionCount);
            int k=core.FindKernel("MantleFlow");core.SetBuffer(k,"_WorldMantleCells",cells);core.SetBuffer(k,"_WorldMantleFlow",flow);core.SetBuffer(k,"_WorldMantleField",field);core.SetInt("_WorldMantleCellCount",plan.Cells.Length);gpu.Dispatch(core,k);yield return null;
            double3[] flows=null;float[] fields=null;foreach(var item in Enumerate(Read<double3>(flow,v=>flows=v)))yield return item;foreach(var item in Enumerate(Read<float>(field,v=>fields=v)))yield return item;
            foreach(var item in Enumerate(Worker(()=>{plan.Complete(flows,Stop);return true;},_=>{})))yield return item;accept(fields);
            // Both real requests have completed; CPU reductions consumed their copies.
            RemoveFields("Physics"+seed+"Cells","Physics"+seed+"Flow","Physics"+seed+"Field");
        }
        IEnumerator Run(Bootstrap data)
        {
            core=UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("WorldOrogenCore"));terrain=UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("WorldOrogenElevation"));
            if(core==null||terrain==null)throw new InvalidOperationException("World Orogen core compute resources are unavailable.");
            if(!SystemInfo.supportsComputeShaders)throw new NotSupportedException("World Orogen generation requires compute shaders.");
            sourceManagedBytes=checked(data.Graph.EstimatedBytes+data.Coarse.EstimatedBytes);
            RequireAllocation(checked(WorldOrogenGpuState.EstimateInitialBytes(data.Graph)+WorldOrogenGpuState.EstimateInitialBytes(data.Coarse)),ManagedReserve);
            state=new WorldOrogenGpuState(data.Graph,settings.Seed);coarseState=new WorldOrogenGpuState(data.Coarse,settings.Seed);
            SetStage(1,"Project coarse plates");int projection=core.FindKernel("ProjectCoarsePlates");
            core.SetBuffer(projection,"_CoarsePositions",coarseState.Graph.Positions);core.SetBuffer(projection,"_CoarseOffsets",coarseState.Graph.Offsets);core.SetBuffer(projection,"_CoarseNeighbors",coarseState.Graph.Neighbors);core.SetBuffer(projection,"_CoarsePlate",Upload("CoarsePlate",data.Plates.RegionPlate));core.SetInt("_CoarseRegionCount",data.Coarse.RegionCount);
            var cursor=Buffer("ProjectionCursor",4,1);cursor.SetData(new uint[1]);var projected=Buffer("_WorldPlate");core.SetBuffer(projection,"_WorldProjectionCursor",cursor);core.SetBuffer(projection,"_WorldPlate",projected);
            state.NoisePermutation.SetData(WorldOrogenNoise.CreatePermutation(settings.Seed+999));Double(core,"_PerturbAmplitude",Math.PI/Math.Sqrt(data.Coarse.RegionCount)*(1.5+math.clamp((80-settings.Plates)/60.0,0,1)));
            for(int start=0;start<state.RegionCount;start+=256){core.SetInt("_ProjectionStart",start);core.SetInt("_ProjectionCount",Math.Min(256,state.RegionCount-start));DispatchCore(projection,1);yield return null;}
            int[] high=null;foreach(var item in Enumerate(Read<int>(projected,v=>high=v)))yield return item;
            RemoveFields("CoarsePlate","ProjectionCursor");
            foreach(var item in Enumerate(Worker(()=>{WorldOrogenPlates.SmoothAndReconnect(data.Graph,high,data.Plates.SeedOrder,3,Stop);return true;},_=>{})))yield return item;projected.SetData(high);state.NoisePermutation.SetData(WorldOrogenNoise.CreatePermutation(settings.Seed));
            var highPlates=new WorldOrogenPlateSet(high,data.Plates.SeedOrder,data.Plates.Motion.Length);Array.Copy(data.Plates.Ocean,highPlates.Ocean,data.Plates.Ocean.Length);Array.Copy(data.Plates.Density,highPlates.Density,data.Plates.Density.Length);
            SetStage(2,"Plate physics and mantle");float[] coarseMantle=null;foreach(var item in Enumerate(Physics(data.Coarse,data.Plates,coarseState,settings.Seed,1,v=>coarseMantle=v)))yield return item;Array.Copy(data.Plates.Motion,highPlates.Motion,data.Plates.Motion.Length);coarseState.Dispose();coarseState=null;
            WorldOrogenPlateSet super=null;if(settings.Plates>=8)
            {foreach(var item in Enumerate(Worker(()=>WorldOrogenSuperPlates.Build(data.Coarse,data.Plates,high,Stop),v=>super=v)))yield return item;foreach(var item in Enumerate(Physics(data.Graph,super,state,settings.Seed+7777,1.6,_=>{})))yield return item;}
            var means=new double[data.Plates.Motion.Length];var counts=new int[means.Length];for(int r=0;r<data.Coarse.RegionCount;r++){int p=data.Plates.RegionPlate[r];means[p]+=coarseMantle[r];counts[p]++;}foreach(int p in data.Plates.SeedOrder)means[p]/=Math.Max(1,counts[p]);
            int expand=core.FindKernel("ExpandMantle");core.SetBuffer(expand,"_WorldPlate",projected);core.SetBuffer(expand,"_PlateMantleMean",Upload("PlateMantleMean",means,8));var mantle=Buffer("r_mantleField");core.SetBuffer(expand,"_WorldMantleField",mantle);DispatchCore(expand);yield return null;
            float[] rawMantle=null;foreach(var item in Enumerate(Read<float>(mantle,v=>rawMantle=v)))yield return item;double maxMantle=0;foreach(float v in rawMantle)maxMantle=Math.Max(maxMantle,Math.Abs(v));
            SetStage(3,"1. Tectonic state");Record small=Collision(highPlates,"Small");yield return null;Record large=super!=null?Collision(super,"Super"):null;yield return null;
            var final=new Record{Stress=Buffer("r_stress"),Direction=Buffer("_WorldStressDirection",12),Subduct=Buffer("r_subductFactor"),Flags=Buffer("FinalFlags",16)};
            if(super==null){int k=core.FindKernel("CopyTectonic");BindSources(k,small,small);BindRecord(k,final);DispatchCore(k);yield return null;foreach(var item in Enumerate(Propagate(final,highPlates,"SmallProp")))yield return item;}
            else
            {int k=core.FindKernel("BlendTectonicInitial");BindSources(k,small,large);BindRecord(k,final);DispatchCore(k);yield return null;var smallWork=Copy(small,"SmallWork");yield return null;var superWork=Copy(large,"SuperWork");yield return null;foreach(var item in Enumerate(Propagate(smallWork,highPlates,"SmallProp")))yield return item;foreach(var item in Enumerate(Propagate(superWork,super,"SuperProp")))yield return item;k=core.FindKernel("BlendTectonicPropagated");BindSources(k,smallWork,superWork);BindRecord(k,final);DispatchCore(k);yield return null;}
            int normalize=core.FindKernel("NormalizeMantleAndStress");BindRecord(normalize,final);core.SetBuffer(normalize,"_WorldMantleField",mantle);var norm=Buffer("r_mantleNorm");core.SetBuffer(normalize,"_WorldMantleNormalized",norm);Double(core,"_WorldMantleMaximum",maxMantle);DispatchCore(normalize);yield return null;
            var tect=new WorldOrogenTectonicFields();uint4[] flags=null;foreach(var item in Enumerate(Read<float>(final.Stress,v=>tect.Stress=v)))yield return item;foreach(var item in Enumerate(Read<float>(final.Subduct,v=>tect.Subduct=v)))yield return item;foreach(var item in Enumerate(Read<uint4>(final.Flags,v=>flags=v)))yield return item;
            // Final flags fence the preceding blend/normalization/smoothing work.
            // Preserve the final record as public diagnostics; retire only intermediates.
            foreach(string prefix in new[]{"Small","Super","SmallWork","SuperWork"})RemoveRecord(prefix);
            foreach(string prefix in new[]{"Small","Super","SmallProp","SuperProp"})RemovePlate(prefix);
            RemoveFields("PlateMantleMean","r_mantleField");
            tect.Boundary=new uint[state.RegionCount];tect.BothOcean=new uint[state.RegionCount];tect.HasOcean=new uint[state.RegionCount];tect.Mountain=new uint[state.RegionCount];tect.Coast=new uint[state.RegionCount];tect.Ocean=new uint[state.RegionCount];for(int r=0;r<state.RegionCount;r++){tect.Boundary[r]=flags[r].x;tect.BothOcean[r]=flags[r].y;tect.HasOcean[r]=flags[r].z;tect.Mountain[r]=(flags[r].w&1)!=0?1u:0u;tect.Coast[r]=(flags[r].w&2)!=0?1u:0u;tect.Ocean[r]=(flags[r].w&4)!=0?1u:0u;}
            tect.PrepareSeedMetadata(highPlates);SetStage(4,"2. Spatial fields");WorldOrogenSpatialFields spatial=null;foreach(var item in Enumerate(Worker(()=>WorldOrogenSpatialFields.Build(data.Graph,highPlates,super,tect,settings.Seed,settings.Roughness,Stop),v=>spatial=v)))yield return item;
            state.OceanFlags.SetData(spatial.OceanFlags);foreach(var pair in spatial.Fields)if(pair.Key!="r_isOcean")Upload(pair.Key,pair.Value);Upload("r_boundaryType",tect.Boundary);Upload("r_hasOcean",tect.HasOcean);Upload("r_bothOcean",tect.BothOcean);Upload("_WorldScalars",spatial.Scalars,8);
            var terrainFields=new Dictionary<string,ComputeBuffer>();foreach(var pair in spatial.Fields)terrainFields[pair.Key]=pair.Key=="r_isOcean"?state.OceanFlags:Buffer(pair.Key);terrainFields["r_stress"]=final.Stress;terrainFields["r_subductFactor"]=final.Subduct;terrainFields["r_boundaryType"]=Buffer("r_boundaryType");terrainFields["r_hasOcean"]=Buffer("r_hasOcean");terrainFields["r_bothOcean"]=Buffer("r_bothOcean");terrainFields["r_mantleNorm"]=norm;
            foreach(string name in new[]{"r_basinFactor","r_tectonicActivity","r_t_foldBelt","r_t_craton","r_t_basin","r_noiseAmp","r_t_plateau","dl_orogenicPower","dl_phasor"})terrainFields[name]=Buffer(name);
            var permutations=new Dictionary<string,ComputeBuffer>();foreach(var pair in new[]{("_BasinPermutation",661),("_FoldPermutation",557),("_RiftPermutation",419),("_CoastPermutation",77),("_IslandPermutation",133),("_CoastWarpPermutation",211),("_AddPermutation",500),("_SubtractPermutation",501),("_PhasorWarpPermutation",1717),("_ArcMacroPermutation",911),("_ArcPermutation",307),("_VolcanoPermutation",713),("_HotspotPermutation",501),("_HotspotWarpPermutation",502),("_LipWarpPermutation",7771)})permutations[pair.Item1]=Upload(pair.Item1,WorldOrogenNoise.CreatePermutation(settings.Seed+pair.Item2));
            var baseHeights=new double[highPlates.Motion.Length];var baseRng=new WorldOrogenRandom(settings.Seed+777);foreach(int p in highPlates.SeedOrder)if(!highPlates.Ocean[p]){double a=baseRng.Next(),b=baseRng.Next();baseHeights[p]=-.15+Math.Sqrt(-2*Math.Log(a==0?1e-10:a))*Math.Cos(2*Math.PI*b)*.025;}Upload("_PlateBaseHeight",baseHeights,8);
            Action<int> bind=k=>
            {state.Bind(terrain,k);terrain.SetBuffer(k,"_WorldPlate",projected);terrain.SetBuffer(k,"_WorldScalars",Buffer("_WorldScalars",8,16));terrain.SetBuffer(k,"_PlateBaseHeight",Buffer("_PlateBaseHeight",8,baseHeights.Length));foreach(var p in terrainFields)terrain.SetBuffer(k,p.Key,p.Value);foreach(var p in permutations)terrain.SetBuffer(k,p.Key,p.Value);terrain.SetBuffer(k,"_BasinOutput",terrainFields["r_basinFactor"]);terrain.SetBuffer(k,"_ActivityOutput",terrainFields["r_tectonicActivity"]);terrain.SetBuffer(k,"_FoldOutput",terrainFields["r_t_foldBelt"]);terrain.SetBuffer(k,"_CratonOutput",terrainFields["r_t_craton"]);terrain.SetBuffer(k,"_BasinWeightOutput",terrainFields["r_t_basin"]);terrain.SetBuffer(k,"_NoiseAmpOutput",terrainFields["r_noiseAmp"]);terrain.SetBuffer(k,"_PlateauOutput",terrainFields["r_t_plateau"]);};
            Action<string> dispatch=name=>{int k=terrain.FindKernel(name);bind(k);state.Dispatch(terrain,k,regionOffsetSupported:true);};
            SetStage(5,"3. Terrain classification");dispatch("BasinPersonality");yield return null;dispatch("ClassifyTerrain");yield return null;
            SetStage(6,"4. Skeleton");dispatch("BuildSkeleton");yield return null;
            SetStage(7,"5. Phasor ridges");var dirA=final.Direction;var dirB=Buffer("PhasorDirectionA",12);var dirC=Buffer("PhasorDirectionB",12);int smoothing=Math.Max(2,(int)Math.Floor(220/(Math.PI*6371/Math.Sqrt(state.RegionCount))+.5));
            for(int pass=0;pass<smoothing;pass++){int k=terrain.FindKernel("SmoothPhasorDirection");bind(k);terrain.SetBuffer(k,"_WorldStressDirection",dirA);terrain.SetBuffer(k,"_WorldSmoothedDirection",dirB);state.Dispatch(terrain,k,regionOffsetSupported:true);yield return null;dirA=dirB;var swap=dirB;dirB=dirC;dirC=swap;}
            float3[] smoothed=null;foreach(var item in Enumerate(Read<float3>(dirA,v=>smoothed=v)))yield return item;WorldOrogenPhasor phasor=null;foreach(var item in Enumerate(Worker(()=>new WorldOrogenPhasor(data.Graph,tect.Stress,smoothed,tect.Subduct,spatial.OceanFlags,tect.MaximumStress,settings.Seed,Stop),v=>phasor=v)))yield return item;
            RemoveFields("PhasorDirectionA","PhasorDirectionB");
            if(phasor.Kernels.Length>0){int k=terrain.FindKernel("PhasorRidges");bind(k);terrain.SetBuffer(k,"_WorldPhasorKernels",Upload("PhasorKernels",phasor.Kernels,64));terrain.SetBuffer(k,"_WorldPhasorRanges",Upload("PhasorRanges",phasor.Ranges,8));terrain.SetBuffer(k,"_WorldPhasorReferences",Upload("PhasorReferences",phasor.References));state.Dispatch(terrain,k,regionOffsetSupported:true);yield return null;}
            SetStage(8,"6. Discrete edifices");var arcScores=Buffer("ArcScores",8);var volcanoScores=Buffer("VolcanoScores",24);int metadata=terrain.FindKernel("EdificeCandidateMetadata");bind(metadata);terrain.SetBuffer(metadata,"_WorldArcScores",arcScores);terrain.SetBuffer(metadata,"_WorldVolcanoScores",volcanoScores);state.Dispatch(terrain,metadata,regionOffsetSupported:true);yield return null;
            double[] arcValues=null;double3[] volcanoValues=null;foreach(var item in Enumerate(Read<double>(arcScores,v=>arcValues=v)))yield return item;foreach(var item in Enumerate(Read<double3>(volcanoScores,v=>volcanoValues=v)))yield return item;
            RemoveFields("ArcScores","VolcanoScores","PhasorKernels","PhasorRanges","PhasorReferences");
            float[] arcDist=null,arcStress=null;foreach(var item in Enumerate(Worker(()=>{WorldOrogenEdifices.IslandArcTopology(data.Graph,highPlates,tect,spatial.OceanFlags,arcValues,out arcDist,out arcStress,Stop);return true;},_=>{})))yield return item;
            int arcs=terrain.FindKernel("IslandArcs");bind(arcs);terrain.SetBuffer(arcs,"_WorldArcDistance",Upload("ArcDistance",arcDist));terrain.SetBuffer(arcs,"_WorldArcStress",Upload("ArcStress",arcStress));state.Dispatch(terrain,arcs,regionOffsetSupported:true);yield return null;
            WorldOrogenVolcano[] volcanos=null;foreach(var item in Enumerate(Worker(()=>WorldOrogenEdifices.VolcanoCatalog(data.Graph,tect,volcanoValues,Stop),v=>volcanos=v)))yield return item;
            WorldOrogenEdifices.BuildGrid(Array.ConvertAll(volcanos,v=>v.Position),36,72,out var volcanoRanges,out var volcanoReferences);int volcano=terrain.FindKernel("VolcanicArcs");bind(volcano);terrain.SetBuffer(volcano,"_WorldVolcanos",Upload("Volcanos",volcanos,48));terrain.SetBuffer(volcano,"_WorldVolcanoRanges",Upload("VolcanoRanges",volcanoRanges,8));terrain.SetBuffer(volcano,"_WorldVolcanoReferences",Upload("VolcanoReferences",volcanoReferences));state.Dispatch(terrain,volcano,regionOffsetSupported:true);yield return null;
            float[] normalizedMantle=null;foreach(var item in Enumerate(Read<float>(norm,v=>normalizedMantle=v)))yield return item;WorldOrogenEdifices edifices=null;foreach(var item in Enumerate(Worker(()=>new WorldOrogenEdifices(data.Graph,highPlates,maxMantle>1e-6?normalizedMantle:null,spatial.OceanFlags,settings.Seed,Stop),v=>edifices=v)))yield return item;
            int domes=terrain.FindKernel("HotspotDomes");bind(domes);terrain.SetBuffer(domes,"_WorldDomes",Upload("Domes",edifices.Domes,192));terrain.SetBuffer(domes,"_WorldDomeRanges",Upload("DomeRanges",edifices.DomeRanges,8));terrain.SetBuffer(domes,"_WorldDomeReferences",Upload("DomeReferences",edifices.DomeReferences));state.Dispatch(terrain,domes,regionOffsetSupported:true);yield return null;
            if(edifices.Lips.Length>0){int k=terrain.FindKernel("LargeIgneousProvinces");bind(k);terrain.SetInt("_WorldLipCount",edifices.Lips.Length);terrain.SetBuffer(k,"_WorldLips",Upload("Lips",edifices.Lips,96));state.Dispatch(terrain,k,regionOffsetSupported:true);yield return null;}
            SetStage(9,"7. Tectonic-band noise");dispatch("TectonicBandNoise");yield return null;SetStage(10,"8. Detail texture");dispatch("DetailTexture");yield return null;SetStage(11,"9. Coastal detail");dispatch("CoastalDetail");yield return null;
            SetStage(12,"10. Uniform land noise");int uniform=terrain.FindKernel("UniformLandNoiseChunk");bind(uniform);for(int start=0;start<state.RegionCount;start+=256){terrain.SetInt("_TerrainChunkStart",start);terrain.SetInt("_TerrainChunkCount",Math.Min(256,state.RegionCount-start));state.Dispatch(terrain,uniform,1);yield return null;}
            SetStage(13,"11. Dynamic topography");if(maxMantle>1e-6){dispatch("DynamicTopography");yield return null;}
            SetStage(14,"12. Final shaping");dispatch("PeakCompression");yield return null;dispatch("IsostaticAdjustment");yield return null;float[] elevation=null;foreach(var item in Enumerate(Read<float>(state.Elevation,v=>elevation=v)))yield return item;
            // This completed height request also fences the last noise/edifice consumers.
            foreach(var p in permutations)state.RemoveField(p.Key);
            permutations.Clear();
            RemoveFields("Volcanos","VolcanoRanges","VolcanoReferences","Domes","DomeRanges","DomeReferences","Lips");
            uint[] ranks=null;double[] hypsometry=null;foreach(var item in Enumerate(Worker(()=>WorldOrogenTopologyFinal.LandRanks(elevation,out hypsometry,Stop),v=>ranks=v)))yield return item;
            int remap=terrain.FindKernel("HypsometricRemap");bind(remap);terrain.SetBuffer(remap,"_WorldLandRank",Upload("LandRank",ranks));terrain.SetBuffer(remap,"_WorldHypsometry",Upload("Hypsometry",hypsometry,8));state.Dispatch(terrain,remap,regionOffsetSupported:true);yield return null;
            SetStage(15,"13. Topology fixup");foreach(var item in Enumerate(Read<float>(state.Elevation,v=>elevation=v)))yield return item;uint[] reachable=null;foreach(var item in Enumerate(Worker(()=>WorldOrogenTopologyFinal.SeaReachable(data.Graph,elevation,spatial.OceanFlags,Stop),v=>reachable=v)))yield return item;
            RemoveFields("LandRank","Hypsometry");
            int fill=terrain.FindKernel("FillInteriorSeas");bind(fill);terrain.SetBuffer(fill,"_WorldSeaReachable",Upload("SeaReachable",reachable));state.Dispatch(terrain,fill,regionOffsetSupported:true);yield return null;
            dispatch("FinalizePostMetadata");yield return null;
            // A four-byte asynchronous result-bank request is a final completion fence;
            // no synchronous GetData or full-array copy is introduced for retirement.
            var completed=AsyncGPUReadback.Request(state.Dampen,4,0);while(!completed.done)yield return null;if(completed.hasError)throw new InvalidOperationException("World Orogen final completion request failed.");
            RemoveFields("SeaReachable","_PlateBaseHeight");result=new WorldOrogenCoreResult(state,highPlates);
        }
        void Release()
        {coarseState?.Dispose();coarseState=null;if(!taken){state?.Dispose();state=null;}if(core!=null)UnityEngine.Object.DestroyImmediate(core);if(terrain!=null)UnityEngine.Object.DestroyImmediate(terrain);core=terrain=null;}
        public void Dispose(){if(disposed)return;disposed=true;cancellation.Cancel();(execution as IDisposable)?.Dispose();Release();}
    }
}
