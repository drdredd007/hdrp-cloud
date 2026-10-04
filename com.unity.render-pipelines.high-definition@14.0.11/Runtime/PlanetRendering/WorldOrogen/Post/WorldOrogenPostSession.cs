// World Orogen source-order post orchestration, cc2662b4; GPL-3.0-only.
using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Call Step on the Unity compute/API thread. One command/chunk or asynchronous
    /// wait per step. State is borrowed: cancellation never publishes or disposes its owner.
    /// CPU queue/sort workers contain no Unity calls and retain their own managed references.</summary>
    public sealed class WorldOrogenPostSession:IDisposable
    {
        readonly WorldOrogenPostProcessor.Runner runner;IEnumerator<object> execution;bool disposed,taken;
        public string Stage {get;private set;}="Post initialization";
        public double Progress01 {get;private set;}
        public string Error {get;private set;}
        public bool IsCompleted {get;private set;}
        public WorldOrogenWorkingEstimate Estimate {get;}
        public WorldOrogenPostProgress Progress {get;private set;}
        WorldOrogenPostSession(WorldOrogenGpuState state,WorldOrogenSettings settings,long budget)
        {
            Estimate=WorldOrogenPostBudget.EstimateTerrain(state);
            runner=WorldOrogenPostProcessor.CreateRunner(state,settings,null,Report,false,budget);execution=runner.Execute();
        }
        public static WorldOrogenPostSession Begin(WorldOrogenGpuState state,WorldOrogenSettings settings,long maximumWorkingBytes=WorldOrogenPostBudget.DefaultMaximumWorkingBytes)
            =>new WorldOrogenPostSession(state,settings,maximumWorkingBytes);
        void Report(WorldOrogenPostProgress value)
        {
            Progress=value;Stage=value.Stage;
            double phase=value.Stage.Contains("erosion")?.35:value.Stage.Contains("Ridge")?.80:value.Stage.Contains("Soil")?.90:value.Stage.Contains("Physical")?.98:value.Stage.Contains("detail")?.25:value.Stage.Contains("smoothing")?.15:value.Stage.Contains("warp")?.05:Progress01;
            Progress01=Math.Max(Progress01,phase);
        }
        public bool Step(Func<bool> cancelled=null)
        {
            if(disposed)throw new ObjectDisposedException(nameof(WorldOrogenPostSession));if(IsCompleted||Error!=null)return IsCompleted;
            try{if(cancelled?.Invoke()==true){runner.Cancel();throw new OperationCanceledException();}if(!execution.MoveNext()){IsCompleted=true;Progress01=1;Stage="Terrain post ready";execution.Dispose();execution=null;}}
            catch(Exception e){runner.Cancel();Error=e is OperationCanceledException?"Cancelled":e.ToString();Stage=e is OperationCanceledException?"Cancelled":"Failed";execution?.Dispose();execution=null;}
            return IsCompleted;
        }
        public WorldOrogenPostResult TakeResult()
        {if(!IsCompleted||taken)throw new InvalidOperationException("Terrain post stages must complete before taking a result.");taken=true;return runner.Result;}
        public void Dispose(){if(disposed)return;disposed=true;runner.Cancel();execution?.Dispose();execution=null;}
    }
}
