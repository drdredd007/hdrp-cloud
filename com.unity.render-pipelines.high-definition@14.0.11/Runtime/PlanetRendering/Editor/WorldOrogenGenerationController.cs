using System;
using System.Collections.Generic;
using System.Diagnostics;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>One window's unpublished GPU workspace. Saved assets never depend on this cache.</summary>
    public sealed class WorldOrogenGenerationController : IDisposable
    {
        public enum Operation { Build, Reapply, Climate, Recolour }
        IEnumerator<object> execution;
        WorldOrogenCoreResult core;
        WorldOrogenPostResult post;
        WorldOrogenClimateResult climate;
        PlanetGeneratorAsset cacheOwner,target;
        WorldOrogenSettings cacheSettings,captured;
        double capturedRadius;
        int capturedResolution;
        WorldOrogenMapView capturedView;
        bool cancelled;
        public bool IsRunning=>execution!=null;
        public string Stage {get;private set;}="Ready";
        public string Error {get;private set;}
        public double Progress01 {get;private set;}
        public event Action Changed;
        public bool CanReapply(PlanetGeneratorAsset asset)=>Matches(asset,false,false)&&post.TerrainReady;
        public bool CanClimate(PlanetGeneratorAsset asset)=>Matches(asset,true,false)&&post.TerrainReady;
        public bool CanRecolour(PlanetGeneratorAsset asset)=>Matches(asset,true,true)&&post.TerrainReady;
        bool Matches(PlanetGeneratorAsset asset,bool includePost,bool includeClimate)
        {
            if(!asset||asset!=cacheOwner||core==null||cacheSettings==null)return false;
            return Key(asset.CaptureOrogen(),includePost,includeClimate)==Key(cacheSettings,includePost,includeClimate);
        }
        static SurfaceContentHash Key(WorldOrogenSettings value,bool includePost,bool includeClimate)
        {
            var v=value.Clone();v.CodeSoilCreep=.75;v.AutoClimate=true;
            if(!includeClimate){v.TemperatureOffset=0;v.PrecipitationOffset=0;}
            if(!includePost){v.TerrainWarp=0;v.Smoothing=0;v.GlacialErosion=0;v.HydraulicErosion=0;v.ThermalErosion=0;v.RidgeSharpening=0;}
            return v.ConfigurationDigest();
        }
        public void Start(PlanetGeneratorAsset asset,Operation operation)
        {
            if(IsRunning)throw new InvalidOperationException("Wait for generation or cancel it first.");
            if(!asset)throw new ArgumentNullException(nameof(asset));
            var input=asset.CaptureOrogen();if(!input.Validate(out var error))throw new ArgumentException(error);
            if(!AssetDatabase.GetAssetPath(asset).StartsWith("Assets/",StringComparison.Ordinal))throw new InvalidOperationException("Save the generator asset in Assets first.");
            if(operation==Operation.Reapply&&!CanReapply(asset)||operation==Operation.Climate&&!CanClimate(asset)||operation==Operation.Recolour&&!CanRecolour(asset))
                throw new InvalidOperationException("Build this configuration first. The editable GPU cache is empty or has different structural parameters.");
            target=asset;captured=input;capturedRadius=asset.Radius;capturedResolution=asset.BaseMapResolution;capturedView=asset.MapView;
            if(!(capturedRadius>20000))throw new ArgumentException("World Orogen requires radius above 20 km.");
            cancelled=false;Error=null;Progress01=0;Stage="Starting "+operation;execution=Run(operation);Changed?.Invoke();
        }
        public void Cancel(){cancelled=true;}
        /// <summary>Bound command submission and main-thread work between editor updates.</summary>
        public void Tick()
        {
            if(!IsRunning)return;
            var watch=Stopwatch.StartNew();
            try
            {
                int steps=0;do
                {
                    Check();if(!execution.MoveNext()){execution.Dispose();execution=null;Stage="Saved base map";Progress01=1;SceneView.RepaintAll();break;}
                }while(++steps<16&&watch.ElapsedMilliseconds<4);
            }
            catch(Exception e)
            {
                execution?.Dispose();execution=null;DropCache();
                Stage=e is OperationCanceledException?"Cancelled":"Failed";Error=e is OperationCanceledException?null:e.Message;
                UnityEngine.Debug.Log(e is OperationCanceledException?"World Orogen generation cancelled; saved planet retained.":"World Orogen generation failed: "+e);
            }
            Changed?.Invoke();
        }
        void Check(){if(cancelled)throw new OperationCanceledException();if(!target)throw new InvalidOperationException("The generator asset was removed during generation.");}
        IEnumerator<object> Run(Operation operation)
        {
            if(operation==Operation.Build)
            {
                DropCache();
                using(var session=WorldOrogenCoreSession.Begin(captured))
                {
                    while(!session.IsCompleted)
                    {Check();session.Step(()=>cancelled);if(!string.IsNullOrEmpty(session.Error))throw new InvalidOperationException(session.Error);Stage=session.Stage;Progress01=session.Progress01*.5;yield return null;}
                    core=session.TakeResult();cacheOwner=target;
                }
            }
            if(operation==Operation.Build||operation==Operation.Reapply)
            {
                if(operation==Operation.Reapply)WorldOrogenPostProcessor.CopyElevationFrom(core.State,post.PrePostElevation);
                climate=null;
                using(var session=WorldOrogenPostSession.Begin(core.State,captured))
                {
                    while(!session.IsCompleted)
                    {Check();session.Step(()=>cancelled);if(!string.IsNullOrEmpty(session.Error))throw new InvalidOperationException(session.Error);Stage=session.Stage;Progress01=.5+session.Progress01*.3;yield return null;}
                    post=session.TakeResult();
                }
            }
            bool needsClimate=operation==Operation.Climate||captured.GenerateClimateAutomatically||capturedView==WorldOrogenMapView.Satellite||capturedView==WorldOrogenMapView.Climate;
            if(needsClimate&&(climate==null||operation!=Operation.Recolour))
            {
                using(var session=WorldOrogenClimateSession.Begin(core.State,captured,core.RegionPlateOcean))
                {
                    while(!session.IsCompleted)
                    {Check();session.Step(()=>cancelled);if(!string.IsNullOrEmpty(session.Error))throw new InvalidOperationException(session.Error);Stage=session.Stage;Progress01=.8+session.Progress01*.15;yield return null;}
                    climate=session.TakeResult();
                }
            }
            cacheSettings=captured.Clone();Stage="Base map reprojection";Progress01=.95;yield return null;Check();
            WorldOrogenBaseMap candidate;
            using(var session=WorldOrogenBaseMap.Begin(core.State,captured,capturedRadius,capturedResolution,capturedView,climate?.Koppen))
            {
                while(!session.IsCompleted)
                {Check();session.Step(()=>cancelled);if(!string.IsNullOrEmpty(session.Error))throw new InvalidOperationException(session.Error);Stage=session.Stage;Progress01=.95+session.Progress01*.04;yield return null;}
                candidate=session.TakeResult();
            }
            using(var map=candidate)
            {
                Stage="Verifying and saving base map";Progress01=.99;yield return null;Check();
                if(target.BaseMapResolution!=capturedResolution||target.MapView!=capturedView)
                    throw new InvalidOperationException("Map view or resolution changed during generation. The candidate was not saved.");
                WorldOrogenPublication.Publish(target,captured,map);
            }
        }
        void DropCache(){core?.Dispose();core=null;climate=null;post=default;cacheOwner=null;cacheSettings=null;}
        public void Dispose(){cancelled=true;execution?.Dispose();execution=null;DropCache();}
    }
}
