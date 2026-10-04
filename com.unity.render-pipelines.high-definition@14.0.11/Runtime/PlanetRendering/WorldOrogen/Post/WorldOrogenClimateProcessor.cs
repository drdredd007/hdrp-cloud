// Derived from World Orogen wind.js/ocean.js/precipitation.js/temperature.js.
// Upstream cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Rendering;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>GPU outputs owned by the generating State. Names and seasonal conventions match upstream.</summary>
    public sealed class WorldOrogenClimateResult
    {
        public bool ClimateReady {get;internal set;}
        public ComputeBuffer WindSummer {get;internal set;}
        public ComputeBuffer WindWinter {get;internal set;}
        public ComputeBuffer PressureSummer {get;internal set;}
        public ComputeBuffer PressureWinter {get;internal set;}
        public ComputeBuffer ItczLatitude {get;internal set;}
        public ComputeBuffer ItczSpline {get;internal set;}
        public ComputeBuffer OceanSummer {get;internal set;}
        public ComputeBuffer OceanWinter {get;internal set;}
        public ComputeBuffer Precipitation {get;internal set;}
        public ComputeBuffer Temperature {get;internal set;}
        public ComputeBuffer Koppen {get;internal set;}
        public ComputeBuffer Geography {get;internal set;}
        public ComputeBuffer Continentality {get;internal set;}
        public ComputeBuffer TemperatureContinentality {get;internal set;}
        public ComputeBuffer Westness {get;internal set;}
        public ComputeBuffer RainShadowSummer {get;internal set;}
        public ComputeBuffer RainShadowWinter {get;internal set;}
        public int Dispatches {get;internal set;}
        public int Readbacks {get;internal set;}
        public int CpuBfsPasses {get;internal set;}
        public int CpuPercentileSelections {get;internal set;}
    }

    public static class WorldOrogenClimateProcessor
    {
        public const long MaximumWorkingBytes=WorldOrogenPostBudget.DefaultMaximumWorkingBytes;
        /// <summary>On-demand source-order climate; GPU scalar fields are not CPU-generated.
        /// Process is an explicitly blocking offline wrapper. Interactive callers use ClimateSession.</summary>
        public static WorldOrogenClimateResult Process(WorldOrogenGpuState state,WorldOrogenSettings settings,uint[] regionPlateOcean,
            Action<WorldOrogenPostProgress> progress=null,Func<bool> cancelled=null,long maximumWorkingBytes=MaximumWorkingBytes)
        {
            var runner=CreateRunner(state,settings,regionPlateOcean,progress,cancelled,true,maximumWorkingBytes);
            var execution=runner.Execute();try{while(execution.MoveNext()){}return runner.Result;}finally{execution.Dispose();}
        }
        internal static Runner CreateRunner(WorldOrogenGpuState state,WorldOrogenSettings settings,uint[] regionPlateOcean,
            Action<WorldOrogenPostProgress> progress,Func<bool> cancelled,bool synchronous,long budget)
        {
            if(state==null||settings==null||regionPlateOcean==null)throw new ArgumentNullException();
            if(!settings.Validate(out string error))throw new ArgumentException(error);
            if(regionPlateOcean.Length!=state.RegionCount)throw new ArgumentException("Climate requires the captured, pre-warp per-region oceanic plate mask.");
            foreach(uint flag in regionPlateOcean)if(flag>1)throw new ArgumentException("Plate ocean flags must be binary.");
            if(!SystemInfo.supportsComputeShaders||SystemInfo.graphicsShaderLevel<50)throw new NotSupportedException("World Orogen climate requires a compute-capable SM5 device.");
            var estimate=WorldOrogenPostBudget.EstimateClimate(state);WorldOrogenPostBudget.Require(estimate,budget);
            var shader=Resources.Load<ComputeShader>("WorldOrogenClimate");if(shader==null)throw new InvalidOperationException("World Orogen climate compute resource is unavailable.");
            return new Runner(state,shader,settings.Clone(),(uint[])regionPlateOcean.Clone(),progress,cancelled,synchronous,checked(budget-estimate.WorkingBytes));
        }
        internal sealed class Runner
        {
            readonly bool synchronous;readonly long additionalManagedAllowance;long additionalManagedReserved;int stopped;
            internal WorldOrogenClimateResult Result {get;private set;}
            internal long AdditionalManagedReserved=>Interlocked.Read(ref additionalManagedReserved);
            internal void Cancel()=>Interlocked.Exchange(ref stopped,1);
            bool WorkerCancelled()=>Volatile.Read(ref stopped)!=0;
            void ReserveSparse(long bytes)
            {
                long next=checked(Interlocked.Read(ref additionalManagedReserved)+bytes);
                if(next>additionalManagedAllowance)throw new InvalidOperationException("World Orogen climate sparse geographic context exceeds the explicit working-memory admission before allocation.");
                Interlocked.Exchange(ref additionalManagedReserved,next);
            }
            readonly WorldOrogenGpuState state;readonly ComputeShader shader;readonly WorldOrogenSettings settings;
            readonly Action<WorldOrogenPostProgress> progress;readonly Func<bool> cancelled;
            readonly Dictionary<string,ComputeBuffer> fields=new Dictionary<string,ComputeBuffer>();
            readonly Dictionary<string,int> kernels=new Dictionary<string,int>();
            readonly double[] parameters=new double[8];
            readonly uint[] plateOcean,land;readonly float4[] geo,packed;readonly float3[] east,north;
            readonly float[] scalar;readonly int[] offsets,neighbors;
            readonly double avgEdgeKm;
            int dispatches,readbacks,bfsPasses,percentiles;
            ComputeBuffer read,write,scalarRead,scalarWrite;
            readonly ComputeBuffer scratch4A,scratch4B,scratchScalar,scratchScalarB;
            int[] coastLand,coastPlate,oceanLabels;

            internal Runner(WorldOrogenGpuState state,ComputeShader shader,WorldOrogenSettings settings,uint[] plateOcean,
                Action<WorldOrogenPostProgress> progress,Func<bool> cancelled,bool synchronous,long additionalManagedAllowance)
            {
                this.synchronous=synchronous;this.additionalManagedAllowance=additionalManagedAllowance;
                this.state=state;this.shader=shader;this.settings=settings;this.plateOcean=plateOcean;this.progress=progress;this.cancelled=cancelled;
                int n=state.RegionCount;offsets=state.Graph.Source.Offsets;neighbors=state.Graph.Source.Neighbors;avgEdgeKm=Math.PI*6371/Math.Sqrt(n);
                geo=new float4[n];packed=new float4[n];east=new float3[n];north=new float3[n];land=new uint[n];scalar=new float[n];
                Make("_ClimateGeo",16);Make("_ClimateEast",12);Make("_ClimateNorth",12);Make("_ClimateLand");
                Make("_ClimateCont");Make("_ClimatePlateCont");Make("_ClimateWestness");Make("_ClimatePlateOcean");
                Make("_ClimateCoastLand");Make("_ClimateCoastPlate");Make("_ClimateCoastOcean");Make("_ClimateCoastOceanWest");Make("_ClimateCoastOceanEast");
                Make("_ClimateBinOffsets",4,2593);Make("_ClimateBinIndices");Make("_ClimateItcz",8,144);Make("_ClimateSpline",32,144);Make("_ClimateParameters",8,8);
                Make("_ClimatePressureSummer",16);Make("_ClimatePressureWinter",16);Make("_ClimateGrad",8);Make("_ClimateElevGrad",8);Make("_ClimateWindSummer",16);Make("_ClimateWindWinter",16);Make("_ClimateOceanSummer",16);Make("_ClimateOceanWinter",16);
                Make("_ClimatePrecip",8);Make("_ClimateTemperature",8);Make("_ClimateKoppen");Make("_ClimateBlendWind",8);Make("_ClimateWind3D",12);
                Make("_ClimateMoisture");Make("_ClimateConvergence");Make("_ClimateHeightKm");Make("_ClimateHeuristicWest");Make("_ClimateSeasonPrecipSummer");Make("_ClimateSeasonPrecipWinter");
                Make("_ClimateSeasonTemperatureSummer");Make("_ClimateSeasonTemperatureWinter");
                Make("_ClimateRainShadowSummer",16);Make("_ClimateRainShadowWinter",16);Make("_ClimateTempCont");
                Make("_ClimateZoneInputs",32);Make("_ClimateLandComponents");Make("_ClimateLandComponentStats",16,n);Make("_ClimateLandComponentWidth",8,n);Make("_ClimateLandComponentSizes");Make("_ClimateWarmCoastDistance");
                Make("_ClimatePatchLabels");Make("_ClimatePatchOffsets",4,n+1);Make("_ClimatePatchMembers");Make("_ClimatePatchZones");
                scratch4A=state.Field("_ClimateScratch4A",16);scratch4B=state.Field("_ClimateScratch4B",16);scratchScalar=state.Field("_ClimateScratchScalar");scratchScalarB=state.Field("_ClimateScratchScalarB");
                read=scratch4A;write=scratch4B;scalarRead=fields["_ClimateCont"];scalarWrite=scratchScalar;
                string[] aliases={"_ClimateGeo","_ClimateEast","_ClimateNorth","_ClimateLand","_ClimateCont","_ClimatePlateCont","_ClimateGrad","_ClimateBlendWind","_ClimateWind3D","_ClimateHeightKm","_ClimateHeuristicWest","_ClimatePrecip","_ClimateTemperature","_ClimateTempCont"};
                foreach(string name in aliases)fields.Add(name+"Write",fields[name]);
                fields["_ClimatePlateOcean"].SetData(plateOcean);
            }
            void Make(string name,int stride=4,int count=-1){fields.Add(name,state.Field(name,stride,count));}
            void Check(){if(WorkerCancelled()||cancelled?.Invoke()==true)throw new OperationCanceledException("World Orogen climate cancelled before publication.");}
            void Report(string stage,int iteration=0,int total=1){Check();progress?.Invoke(new WorldOrogenPostProgress(stage,iteration,total,dispatches,readbacks));}
            int Kernel(string name){if(!kernels.TryGetValue(name,out int k)){k=shader.FindKernel(name);kernels.Add(name,k);}return k;}
            void Bind(int k)
            {
                state.Bind(shader,k);shader.SetInt("_ClimateSeason",season);shader.SetInt("_ClimateMode",mode);shader.SetInt("_ClimateComponentMask",componentMask);shader.SetInt("_ClimatePatchCount",patchCount);shader.SetInt("_ClimateOffset",offset);shader.SetInt("_ClimateCount",count);foreach(var f in fields)shader.SetBuffer(k,f.Key,f.Value);
                fields["_ClimateParameters"].SetData(parameters);
                shader.SetBuffer(k,"_ClimateRead",read);shader.SetBuffer(k,"_ClimateWrite",write);
                shader.SetBuffer(k,"_ClimateScalarRead",scalarRead);shader.SetBuffer(k,"_ClimateScalarWrite",scalarWrite);
                shader.SetBuffer(k,"_ClimateSeasonWind",fields[season==0?"_ClimateWindSummer":"_ClimateWindWinter"]);
                shader.SetBuffer(k,"_ClimateSeasonOcean",fields[season==0?"_ClimateOceanSummer":"_ClimateOceanWinter"]);
            }
            int season,mode,componentMask=15,patchCount,offset,count;
            void Season(int value){season=value;}
            void Dispatch(string name,int elements=-1)
            {Check();int k=Kernel(name);Bind(k);shader.GetKernelThreadGroupSizes(k,out uint x,out uint y,out uint z);if(x==0||y!=1||z!=1)throw new InvalidOperationException("Climate kernels must be one-dimensional.");int n=elements<0?state.RegionCount:elements;if(n>0){shader.Dispatch(k,(n+(int)x-1)/(int)x,1,1);dispatches++;}}
            IEnumerable<object> PackedPass(string kernel,ComputeBuffer source,ComputeBuffer target)
            {
                // DX11 cannot bind one resource as both SRV and UAV in a dispatch.
                ComputeBuffer actualTarget=source==target?(source==scratch4A?scratch4B:scratch4A):target;
                read=source;write=actualTarget;Dispatch(kernel);yield return null;
                if(actualTarget!=target){read=actualTarget;write=target;Dispatch("CopyField");yield return null;}
            }
            IEnumerable<object> SmoothPacked(ComputeBuffer value,int passes,int mode=0,int mask=15)
            {
                this.mode=mode;componentMask=mask;ComputeBuffer a=value,b=value==scratch4A?scratch4B:scratch4A;
                for(int i=0;i<passes;i++){foreach(var item in PackedPass("SmoothField",a,b))yield return item;var t=a;a=b;b=t;}
                if(a!=value)foreach(var item in PackedPass("CopyField",a,value))yield return item;
            }
            IEnumerable<object> SmoothScalar(ComputeBuffer value,int passes,int mode=0)
            {
                this.mode=mode;ComputeBuffer a=value,b=value==scratchScalar?scratchScalarB:scratchScalar;
                for(int i=0;i<passes;i++){scalarRead=a;scalarWrite=b;Dispatch("SmoothScalar");yield return null;var t=a;a=b;b=t;}
                if(a!=value){scalarRead=a;scalarWrite=value;Dispatch("CopyScalar");yield return null;}
            }
            IEnumerable<object> Read<T>(ComputeBuffer source,T[] output) where T:struct
            {
                Check();if(synchronous){source.GetData(output);readbacks++;Check();yield break;}
                var request=AsyncGPUReadback.Request(source);yield return null;
                while(!request.done){Check();yield return null;}Check();
                if(request.hasError)throw new InvalidOperationException("World Orogen climate GPU readback failed.");
                var values=request.GetData<T>();if(values.Length!=output.Length)throw new InvalidOperationException("World Orogen climate readback shape mismatch.");
                values.CopyTo(output);readbacks++;
            }
            double Percentile(float[] values,int count)
            {percentiles++;if(count==0)return 1;Array.Sort(values,0,count);double result=values[(int)Math.Floor(count*.95)];return result!=0?result:1;}
            IEnumerable<object> PackedPercentile(ComputeBuffer value,int channel,bool oceanOnly=false,bool squared=false)
            {
                foreach(var item in Read(value,packed))yield return item;
                Func<double> select=()=>{int filled=0;for(int r=0;r<packed.Length;r++){CheckWorkerOccasional(r);if(oceanOnly&&land[r]!=0)continue;float x=squared?(float)((double)packed[r].x*packed[r].x+(double)packed[r].y*packed[r].y):packed[r][channel];if(oceanOnly&&x<=0)continue;scalar[filled++]=x;}return Percentile(scalar,filled);};
                if(synchronous)parameters[0]=select();
                else{var task=Task.Run(select);while(!task.IsCompleted){Check();yield return null;}parameters[0]=task.GetAwaiter().GetResult();}Check();
            }
            IEnumerable<object> Cpu(Action action)
            {if(synchronous){action();Check();yield break;}var task=Task.Run(action);while(!task.IsCompleted){Check();yield return null;}task.GetAwaiter().GetResult();Check();}
            void CheckWorkerOccasional(int work){if((work&1023)==0&&WorkerCancelled())throw new OperationCanceledException();}
            internal IEnumerator<object> Execute()
            {
                foreach(var item in WindOcean())yield return item;foreach(var item in Precipitation())yield return item;foreach(var item in Temperature())yield return item;Report("Koppen classification");Dispatch("Koppen");yield return null;
                Result=new WorldOrogenClimateResult{ClimateReady=true,WindSummer=fields["_ClimateWindSummer"],WindWinter=fields["_ClimateWindWinter"],PressureSummer=fields["_ClimatePressureSummer"],PressureWinter=fields["_ClimatePressureWinter"],ItczLatitude=fields["_ClimateItcz"],ItczSpline=fields["_ClimateSpline"],OceanSummer=fields["_ClimateOceanSummer"],OceanWinter=fields["_ClimateOceanWinter"],Precipitation=fields["_ClimatePrecip"],Temperature=fields["_ClimateTemperature"],Koppen=fields["_ClimateKoppen"],Geography=fields["_ClimateGeo"],Continentality=fields["_ClimateCont"],TemperatureContinentality=fields["_ClimateTempCont"],Westness=fields["_ClimateWestness"],RainShadowSummer=fields["_ClimateRainShadowSummer"],RainShadowWinter=fields["_ClimateRainShadowWinter"],Dispatches=dispatches,Readbacks=readbacks,CpuBfsPasses=bfsPasses,CpuPercentileSelections=percentiles};
            }
            IEnumerable<object> WindOcean()
            {
                Report("Climate geography");Dispatch("Geography");yield return null;foreach(var item in Read(fields["_ClimateGeo"],geo))yield return item;foreach(var item in Read(fields["_ClimateEast"],east))yield return item;foreach(var item in Read(fields["_ClimateNorth"],north))yield return item;foreach(var item in Read(fields["_ClimateLand"],land))yield return item;
                Report("CPU geographic bins and BFS metadata");GeographicMetadata metadata=null;foreach(var item in Cpu(()=>metadata=BuildGeographicMetadata()))yield return item;
                fields["_ClimateBinOffsets"].SetData(metadata.BinOffsets);fields["_ClimateBinIndices"].SetData(metadata.BinIndices);fields["_ClimateCoastLand"].SetData(coastLand);fields["_ClimateCoastPlate"].SetData(coastPlate);fields["_ClimateWestness"].SetData(scalar);yield return null;Dispatch("Continentality");yield return null;
                int contSmooth=Math.Max(1,Round(100/avgEdgeKm));foreach(var item in SmoothScalar(fields["_ClimateCont"],contSmooth))yield return item;foreach(var item in SmoothScalar(fields["_ClimatePlateCont"],contSmooth))yield return item;
                foreach(var item in SmoothScalar(fields["_ClimateWestness"],Math.Max(1,Round(150/avgEdgeKm))))yield return item;
                for(int s=0;s<2;s++)
                {
                    Season(s);Report("ITCZ thermal search",s,2);Dispatch("ItczSearch",72);yield return null;Dispatch("ItczSpline",1);yield return null;
                    Report("Seasonal pressure and wind",s,2);var pressureField=fields[s==0?"_ClimatePressureSummer":"_ClimatePressureWinter"];write=pressureField;Dispatch("Pressure");yield return null;foreach(var item in SmoothPacked(pressureField,Math.Max(1,Round(75/avgEdgeKm))))yield return item;
                    read=pressureField;Dispatch("Gradient");yield return null;write=fields[s==0?"_ClimateWindSummer":"_ClimateWindWinter"];Dispatch("Wind");yield return null;
                    foreach(var item in PackedPercentile(write,2))yield return item;foreach(var item in PackedPass("NormalizeWind",write,write))yield return item;
                    // Upstream publishes p-1013 only after the physical pressure-to-wind calculation.
                    foreach(var item in PackedPass("PressureDeviation",write,pressureField))yield return item;
                }
                Report("CPU ocean coast BFS metadata");OceanMetadata oceanMetadata=null;foreach(var item in Cpu(()=>oceanMetadata=BuildOceanMetadata()))yield return item;fields["_ClimateCoastOcean"].SetData(oceanMetadata.All);fields["_ClimateCoastOceanWest"].SetData(oceanMetadata.West);fields["_ClimateCoastOceanEast"].SetData(oceanMetadata.East);yield return null;
                for(int s=0;s<2;s++)
                {
                    Season(s);Report("Seasonal ocean currents",s,2);var output=fields[s==0?"_ClimateOceanSummer":"_ClimateOceanWinter"];
                    write=output;Dispatch("OceanCurrent");yield return null;foreach(var item in SmoothPacked(output,Math.Max(2,Round(125/avgEdgeKm)),1,3))yield return item;
                    foreach(var item in PackedPass("OceanWarmth",output,output))yield return item;foreach(var item in SmoothPacked(output,Math.Max(3,Round(900/avgEdgeKm)),1,8))yield return item;
                    foreach(var item in PackedPercentile(output,2,true,true))yield return item;foreach(var item in PackedPass("OceanSpeed",output,output))yield return item;
                }
            }
            IEnumerable<object> Precipitation()
            {
                Report("Precipitation elevation gradients");write=scratch4A;Dispatch("PrecipTerrain");yield return null;foreach(var item in SmoothPacked(scratch4A,Math.Max(2,Round(200/avgEdgeKm)),0,1))yield return item;
                foreach(var item in PackedPass("PrecipElevationBlend",scratch4A,scratch4B))yield return item;read=scratch4B;
                int gradient=Kernel("Gradient");Bind(gradient);shader.SetBuffer(gradient,"_ClimateGradWrite",fields["_ClimateElevGrad"]);shader.GetKernelThreadGroupSizes(gradient,out uint gx,out uint gy,out uint gz);if(gx==0||gy!=1||gz!=1)throw new InvalidOperationException("Climate gradient must be one-dimensional.");shader.Dispatch(gradient,(state.RegionCount+(int)gx-1)/(int)gx,1,1);dispatches++;yield return null;
                Dispatch("HeuristicWest");yield return null;foreach(var item in SmoothScalar(fields["_ClimateHeuristicWest"],Math.Max(2,Round(300/avgEdgeKm)),2))yield return item;
                parameters[4]=Math.Max(8,Math.Min(20,Round(WorldOrogenClimateDefaults.PRECIP_ADVECT_REACH_KM/avgEdgeKm)));parameters[5]=settings.PrecipitationOffset;parameters[6]=settings.LandCoverage;
                for(int s=0;s<2;s++)
                {
                    Season(s);Report("Moisture and convergence",s,2);Dispatch("PrecipWind");yield return null;scalarWrite=fields["_ClimateConvergence"];Dispatch("PrecipConvergence");yield return null;foreach(var item in SmoothScalar(fields["_ClimateConvergence"],Math.Max(3,Round(400/avgEdgeKm))))yield return item;
                    scalarWrite=fields["_ClimateMoisture"];Dispatch("MoistureInitialize");yield return null;var a=fields["_ClimateMoisture"];var b=scratchScalar;
                    for(int i=0;i<(int)parameters[4];i++){scalarRead=a;scalarWrite=b;Dispatch("MoistureAdvect");yield return null;var t=a;a=b;b=t;}
                    if(a!=fields["_ClimateMoisture"]){scalarRead=a;scalarWrite=fields["_ClimateMoisture"];Dispatch("CopyScalar");yield return null;}
                    Report("Precipitation mechanisms and rain shadow",s,2);var output=fields[s==0?"_ClimateRainShadowSummer":"_ClimateRainShadowWinter"];
                    write=output;Dispatch("PrecipMechanisms");yield return null;
                    int shadowHops=Math.Max(8,Round(WorldOrogenClimateDefaults.PRECIP_RS_SHADOW_PROP_KM/avgEdgeKm)),windwardHops=Math.Max(6,Round(1500/avgEdgeKm));
                    mode=0;parameters[7]=Math.Pow(.15,1.0/shadowHops);a=output;b=scratch4A;
                    for(int i=0;i<shadowHops;i++){foreach(var item in PackedPass("ShadowPropagate",a,b))yield return item;var t=a;a=b;b=t;}if(a!=output)foreach(var item in PackedPass("CopyField",a,output))yield return item;
                    mode=1;parameters[7]=Math.Pow(.25,1.0/windwardHops);a=output;b=scratch4A;
                    for(int i=0;i<windwardHops;i++){foreach(var item in PackedPass("ShadowPropagate",a,b))yield return item;var t=a;a=b;b=t;}if(a!=output)foreach(var item in PackedPass("CopyField",a,output))yield return item;
                    foreach(var item in PackedPass("ShadowMerge",output,output))yield return item;foreach(var item in SmoothPacked(output,Math.Max(2,Round(150/avgEdgeKm)),0,8))yield return item;
                    foreach(var item in PackedPass("ShadowApply",output,output))yield return item;foreach(var item in SmoothPacked(output,Math.Max(1,Round(100/avgEdgeKm)),0,1))yield return item;
                    write=scratch4A;Dispatch("HeuristicPrecipitation");yield return null;foreach(var item in SmoothPacked(scratch4A,Math.Max(1,Round(100/avgEdgeKm)),0,1))yield return item;
                    scalarWrite=scratchScalar;read=scratch4A;Dispatch("ScalarFromField");yield return null;scalarRead=scratchScalar;
                    foreach(var item in PackedPass("PrecipBlend",output,scratch4B))yield return item;foreach(var item in PackedPercentile(scratch4B,0))yield return item;read=scratch4B;scalarWrite=fields[s==0?"_ClimateSeasonPrecipSummer":"_ClimateSeasonPrecipWinter"];Dispatch("PrecipNormalize");yield return null;
                }
                Report("Seasonal precipitation contrast");Dispatch("PrecipContrast");yield return null;
            }
            IEnumerable<object> Temperature()
            {
                Report("Temperature geographic component metadata");foreach(var item in Read(fields["_ClimateOceanSummer"],packed))yield return item;
                WorldOrogenTemperatureContext context=null;foreach(var item in Cpu(()=>context=WorldOrogenTemperatureContext.Build(state.Graph.Source,geo,east,land,coastLand,packed,avgEdgeKm,WorkerCancelled,ReserveSparse)))yield return item;
                bfsPasses+=context.BfsPasses;fields["_ClimateZoneInputs"].SetData(context.Inputs);fields["_ClimateLandComponents"].SetData(context.Labels);
                if(context.Stats.Length>0)fields["_ClimateLandComponentStats"].SetData(context.Stats);if(context.Widths.Length>0)fields["_ClimateLandComponentWidth"].SetData(context.Widths);if(context.Sizes.Length>0)fields["_ClimateLandComponentSizes"].SetData(context.Sizes);fields["_ClimateWarmCoastDistance"].SetData(context.WarmDistance);
                Dispatch("TemperatureZones");yield return null;foreach(var item in Read(fields["_ClimateTempCont"],scalar))yield return item;
                WorldOrogenTemperatureContext.Patches patches=null;foreach(var item in Cpu(()=>patches=WorldOrogenTemperatureContext.BuildPatches(offsets,neighbors,land,scalar,WorkerCancelled)))yield return item;
                fields["_ClimatePatchLabels"].SetData(patches.Labels);fields["_ClimatePatchOffsets"].SetData(patches.Offsets);if(patches.Members.Length>0)fields["_ClimatePatchMembers"].SetData(patches.Members);if(patches.Zones.Length>0)fields["_ClimatePatchZones"].SetData(patches.Zones);
                patchCount=patches.Zones.Length;parameters[7]=Math.Max(5,Round(500000/(avgEdgeKm*avgEdgeKm)));
                Report("GPU ordered temperature zone cleanup");for(int start=0;start<patchCount;start+=16){offset=start;count=Math.Min(16,patchCount-start);Dispatch("TemperaturePatchCleanup",1);yield return null;}
                int bufferPasses=Math.Max(3,Round(100/avgEdgeKm));int k=Kernel("TemperatureZoneBuffer");
                for(int pass=0;pass<bufferPasses;pass++)for(int start=0;start<state.RegionCount;start+=256){Check();offset=start;count=Math.Min(256,state.RegionCount-start);Bind(k);shader.Dispatch(k,1,1,1);dispatches++;yield return null;}
                Dispatch("TemperatureZoneOceanZero");yield return null;foreach(var item in SmoothScalar(fields["_ClimateTempCont"],Math.Max(2,Round(200/avgEdgeKm))))yield return item;Dispatch("TemperatureZoneFinalize");yield return null;
                parameters[7]=settings.TemperatureOffset;
                for(int s=0;s<2;s++)
                {
                    Season(s);Report("Seasonal temperature",s,2);scalarWrite=scratchScalar;Dispatch("CoastalWarmthInitialize");yield return null;
                    ComputeBuffer a=scratchScalar,b=fields["_ClimateConvergence"];
                    int diffusion=Math.Max(4,Round(WorldOrogenClimateDefaults.TEMP_OCEAN_WARMTH_DIFFUSE_KM/avgEdgeKm));
                    for(int i=0;i<diffusion;i++){scalarRead=a;scalarWrite=b;Dispatch("CoastalWarmthDiffuse");yield return null;var t=a;a=b;b=t;}
                    var temperatureOutput=fields[s==0?"_ClimateSeasonTemperatureSummer":"_ClimateSeasonTemperatureWinter"];scalarRead=a;scalarWrite=temperatureOutput;Dispatch("TemperatureCurve");yield return null;
                    foreach(var item in SmoothScalar(temperatureOutput,1))yield return item;scalarRead=temperatureOutput;Dispatch("TemperatureNormalize");yield return null;
                }
            }
            sealed class GeographicMetadata{internal uint[] BinOffsets,BinIndices;}
            GeographicMetadata BuildGeographicMetadata()
            {
                int n=state.RegionCount;var bins=new uint[n];var counts=new uint[2592];
                for(int r=0;r<n;r++){CheckOccasional(r);int bi=Math.Max(0,Math.Min(35,(int)Math.Floor(((double)geo[r].x+Math.PI/2)/Math.PI*36))),li=Math.Max(0,Math.Min(71,(int)Math.Floor(((double)geo[r].y+Math.PI)/(2*Math.PI)*72)));uint bin=(uint)(bi*72+li);bins[r]=bin;counts[bin]++;}
                var bo=new uint[2593];for(int i=0;i<2592;i++)bo[i+1]=bo[i]+counts[i];var indices=new uint[n];Array.Clear(counts,0,counts.Length);for(uint r=0;r<n;r++){uint bin=bins[r];indices[bo[bin]+counts[bin]++]=r;}
                oceanLabels=Components(land,false,out int[] oceanSizes);int main=-1,size=0;for(int i=0;i<oceanSizes.Length;i++)if(oceanSizes[i]>size){main=i;size=oceanSizes[i];}
                var coasts=new List<int>();var west=new List<int>();var eastSeeds=new List<int>();var plateSeeds=new List<int>();
                for(int r=0;r<n;r++)
                {
                    CheckOccasional(r);bool mainCoast=false;double ox=0,oz=0;
                    if(land[r]!=0){for(int j=offsets[r];j<offsets[r+1];j++){int nb=neighbors[j];if(land[nb]==0&&oceanLabels[nb]==main){mainCoast=true;ox+=(double)state.Graph.Source.Directions[nb].x-state.Graph.Source.Directions[r].x;oz+=(double)state.Graph.Source.Directions[nb].z-state.Graph.Source.Directions[r].z;}}if(mainCoast){coasts.Add(r);double dot=ox*Math.Cos(geo[r].y)-oz*Math.Sin(geo[r].y);if(dot<0)west.Add(r);else eastSeeds.Add(r);}}
                    if(plateOcean[r]==0){for(int j=offsets[r];j<offsets[r+1];j++)if(plateOcean[neighbors[j]]!=0){plateSeeds.Add(r);break;}}
                }
                coastLand=Bfs(land,true,coasts);coastPlate=Bfs(plateOcean,false,plateSeeds);
                var dw=Bfs(land,true,west);var de=Bfs(land,true,eastSeeds);for(int r=0;r<n;r++){float v=0;if(land[r]!=0){if(dw[r]<0&&de[r]>=0)v=-1;else if(de[r]<0&&dw[r]>=0)v=1;else if(dw[r]>=0&&de[r]>=0)v=(float)((double)(de[r]-dw[r])/(de[r]+dw[r]+1e-6));}scalar[r]=v;}return new GeographicMetadata{BinOffsets=bo,BinIndices=indices};
            }
            sealed class OceanMetadata{internal int[] All,West,East;}
            OceanMetadata BuildOceanMetadata()
            {
                var all=new List<int>();var west=new List<int>();var eastSeeds=new List<int>();
                for(int r=0;r<state.RegionCount;r++)
                {CheckOccasional(r);if(land[r]!=0)continue;double3 direction=0;bool adjacent=false;for(int j=offsets[r];j<offsets[r+1];j++){int nb=neighbors[j];if(land[nb]!=0){adjacent=true;direction+=(double3)state.Graph.Source.Directions[nb]-(double3)state.Graph.Source.Directions[r];}}if(!adjacent)continue;all.Add(r);double d=math.dot(direction,(double3)east[r]);if(d<=0)west.Add(r);else eastSeeds.Add(r);}
                var result=new OceanMetadata{All=Bfs(land,false,all),West=Bfs(land,false,west),East=Bfs(land,false,eastSeeds)};
                parameters[1]=Math.Max(5,Round(Math.Sqrt(state.RegionCount)*.035));parameters[2]=Circumpolar(60)?1:0;parameters[3]=Circumpolar(-60)?1:0;return result;
            }
            bool Circumpolar(double target)
            {var bins=new bool[72];double min=(target-5)*Math.PI/180,max=(target+5)*Math.PI/180;for(int r=0;r<land.Length;r++){if(land[r]!=0||geo[r].x<min||geo[r].x>max)continue;int bin=(int)Math.Floor(((double)geo[r].y+Math.PI)/(2*Math.PI)*72);bins[((bin%72)+72)%72]=true;}foreach(bool b in bins)if(!b)return false;return true;}
            int[] Components(uint[] mask,bool positive,out int[] sizes)
            {
                bfsPasses++;int n=mask.Length;var labels=new int[n];Array.Fill(labels,-1);var queue=new int[n];var counts=new List<int>();
                for(int r=0;r<n;r++){CheckOccasional(r);if((mask[r]!=0)!=positive||labels[r]>=0)continue;int label=counts.Count,head=0,tail=1,count=0;queue[0]=r;labels[r]=label;while(head<tail){CheckWorkerOccasional(head);int current=queue[head++];count++;for(int j=offsets[current];j<offsets[current+1];j++){int nb=neighbors[j];if((mask[nb]!=0)==positive&&labels[nb]<0){labels[nb]=label;queue[tail++]=nb;}}}counts.Add(count);}sizes=counts.ToArray();return labels;
            }
            int[] Bfs(uint[] mask,bool positive,IReadOnlyList<int> seeds)
            {bfsPasses++;int n=mask.Length;var dist=new int[n];Array.Fill(dist,-1);var q=new int[n];int tail=0,head=0;foreach(int r in seeds){dist[r]=0;q[tail++]=r;}while(head<tail){CheckOccasional(head);int r=q[head++],d=dist[r]+1;for(int j=offsets[r];j<offsets[r+1];j++){int nb=neighbors[j];if((mask[nb]!=0)==positive&&dist[nb]<0){dist[nb]=d;q[tail++]=nb;}}}return dist;}
            void CheckOccasional(int r)=>CheckWorkerOccasional(r);
            static int Round(double x)=>(int)Math.Floor(x+.5);
        }
    }
}
