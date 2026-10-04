// World Orogen climate source-order orchestration, cc2662b4; GPL-3.0-only.
using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Main compute-thread incremental climate. State is borrowed; CPU geographic
    /// context/percentile workers contain no Unity API calls. A result becomes ready only
    /// after every source-order GPU stage has been issued. No independent simulation clock.</summary>
    public sealed class WorldOrogenClimateSession:IDisposable
    {
        readonly WorldOrogenClimateProcessor.Runner runner;IEnumerator<object> execution;bool disposed,taken;
        public string Stage {get;private set;}="Climate initialization";
        public double Progress01 {get;private set;}
        public string Error {get;private set;}
        public bool IsCompleted {get;private set;}
        public WorldOrogenWorkingEstimate Estimate {get;}
        public long AdditionalManagedReservedBytes=>runner.AdditionalManagedReserved;
        public WorldOrogenPostProgress Progress {get;private set;}
        WorldOrogenClimateSession(WorldOrogenGpuState state,WorldOrogenSettings settings,uint[] regionPlateOcean,long budget)
        {
            Estimate=WorldOrogenPostBudget.EstimateClimate(state);
            runner=WorldOrogenClimateProcessor.CreateRunner(state,settings,regionPlateOcean,Report,null,false,budget);execution=runner.Execute();
        }
        public static WorldOrogenClimateSession Begin(WorldOrogenGpuState state,WorldOrogenSettings settings,uint[] regionPlateOcean,
            long maximumWorkingBytes=WorldOrogenPostBudget.DefaultMaximumWorkingBytes)
            =>new WorldOrogenClimateSession(state,settings,regionPlateOcean,maximumWorkingBytes);
        void Report(WorldOrogenPostProgress value)
        {
            Progress=value;Stage=value.Stage;
            double phase=value.Stage.Contains("Koppen")?.98:value.Stage.Contains("temperature")||value.Stage.Contains("Temperature")?.75:
                value.Stage.Contains("Precipitation")||value.Stage.Contains("precipitation")||value.Stage.Contains("Moisture")?.45:
                value.Stage.Contains("ocean")?.30:value.Stage.Contains("pressure")?.20:value.Stage.Contains("ITCZ")?.10:Progress01;
            Progress01=Math.Max(Progress01,phase);
        }
        public bool Step(Func<bool> cancelled=null)
        {
            if(disposed)throw new ObjectDisposedException(nameof(WorldOrogenClimateSession));if(IsCompleted||Error!=null)return IsCompleted;
            try{if(cancelled?.Invoke()==true){runner.Cancel();throw new OperationCanceledException();}if(!execution.MoveNext()){IsCompleted=true;Progress01=1;Stage="Climate ready";execution.Dispose();execution=null;}}
            catch(Exception e){runner.Cancel();Error=e is OperationCanceledException?"Cancelled":e.ToString();Stage=e is OperationCanceledException?"Cancelled":"Failed";execution?.Dispose();execution=null;}
            return IsCompleted;
        }
        public WorldOrogenClimateResult TakeResult()
        {if(!IsCompleted||taken)throw new InvalidOperationException("Climate stages must complete before taking a result.");taken=true;return runner.Result;}
        public void Dispose(){if(disposed)return;disposed=true;runner.Cancel();execution?.Dispose();execution=null;}
    }
}
