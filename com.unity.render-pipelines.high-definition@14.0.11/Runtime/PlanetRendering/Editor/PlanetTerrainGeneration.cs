using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class PlanetTerrainGenerationResult
    {
        public SurfaceBakeResult GlobalBake {get;}
        public SurfaceSnapshot Snapshot {get;}
        public IReadOnlyList<SurfaceRegionRefinementDiagnostics> RegionalDiagnostics {get;}
        public bool UsedCache {get;}
        public IReadOnlyList<SurfaceSnapshot> MaterialLodPyramid {get;}
        /// <summary>Conservative retained result payload; shared source references may be counted twice.</summary>
        public long EstimatedResidentBytes {get;}
        internal PlanetTerrainGenerationResult(SurfaceBakeResult global,SurfaceSnapshot snapshot,List<SurfaceRegionRefinementDiagnostics> regions,bool cache,List<SurfaceSnapshot> levels)
        {
            GlobalBake=global;Snapshot=snapshot;RegionalDiagnostics=regions.AsReadOnly();UsedCache=cache;MaterialLodPyramid=levels.AsReadOnly();
            long bytes=checked(PlanetTerrainBakeCache.EstimateBakeBytes(global)+PlanetTerrainBakeCache.EstimateSnapshotBytes(snapshot));
            foreach(var level in levels)bytes=checked(bytes+PlanetTerrainBakeCache.EstimateSnapshotBytes(level));EstimatedResidentBytes=bytes;
        }
    }

    /// <summary>Pure captured-input editor workflow. No Unity objects are accessed from the worker task.</summary>
    public static class PlanetTerrainGeneration
    {
        public static PlanetTerrainGenerationResult Bake(SurfaceRecipe recipe,SurfaceBakeSettings settings,SurfaceDetailRecipe detail,
            SurfaceSnapshot previous,IReadOnlyList<SurfaceRegionRefinementSettings> regions,
            Action<SurfaceBakeProgress> progress=null,Func<bool> cancelled=null)
        {
            string reason=null;
            if(settings==null||!settings.Validate(recipe,out reason))throw new ArgumentException(settings==null?"Missing bake settings.":reason);
            if(!detail.IsValid)throw new ArgumentException("Metric detail settings are invalid.");
            var captured=settings.Clone();var capturedRegions=new List<SurfaceRegionRefinementSettings>();
            var priorities=new HashSet<int>();
            if(regions!=null)foreach(var region in regions)
            {
                if(region==null||!priorities.Add(region.Priority))throw new ArgumentException("Every generated region requires a distinct priority.");
                capturedRegions.Add(region.Clone());
            }
            CheckCancelled(cancelled);var key=captured.ConfigurationDigest(recipe);
            bool cached=PlanetTerrainBakeCache.TryGet(key,out var global);
            if(!cached){global=SurfaceBaker.Bake(recipe,captured,progress,cancelled);CheckCancelled(cancelled);PlanetTerrainBakeCache.Store(key,global);}
            var baseSurface=new SurfaceSnapshot(global.Snapshot.Recipe,global.Snapshot.Revision,global.Snapshot.CanonicalTileLevel,
                global.Snapshot.Resolution,global.Snapshot.Tiles,detail,automaticMaterialProfile:global.Snapshot.AutomaticMaterialProfile);
            var source=PlanetTerrainExchangeService.PreserveAuthoredLayers(baseSurface,previous);
            long retainedGlobal=checked(PlanetTerrainBakeCache.EstimateBakeBytes(global)+(previous==null?0:PlanetTerrainBakeCache.EstimateSnapshotBytes(previous)));
            var published=source;var diagnostics=new List<SurfaceRegionRefinementDiagnostics>();
            foreach(var region in capturedRegions)
            {
                CheckCancelled(cancelled);
                if(!region.Validate(source,global.Hydrology,out var regionError))throw new ArgumentException(regionError);
                long retained=checked(retainedGlobal+PlanetTerrainBakeCache.EstimateSnapshotBytes(published));
                long remaining=captured.MaximumWorkingBytes-retained;
                if(region.EstimatedWorkingBytes(source)>remaining)throw new InvalidOperationException("Regional solve plus retained global/authored data exceeds MaximumWorkingBytes before allocation.");
                var boundedRegion=region.Clone();boundedRegion.Erosion.MaximumWorkingBytes=Math.Min(boundedRegion.Erosion.MaximumWorkingBytes,remaining);
                // Every solve reads the same base+authored source. Generated neighbours never become input.
                var result=SurfaceBaker.RefineRegion(source,global.Hydrology,boundedRegion,progress,cancelled);
                published=PlanetTerrainExchangeService.PublishLayer(published,result.Region,repairAutomaticMaterials:false);
                diagnostics.Add(result.Diagnostics);
            }
            var fineBudget=RepairAllowance(captured.MaximumWorkingBytes,checked(retainedGlobal+PlanetTerrainBakeCache.EstimateSnapshotBytes(published)),published.Tiles,published.Regions,published.AutomaticMaterialProfile);
            published=SurfaceMaterialRepair.Rebuild(published,fineBudget,progress,cancelled);
            var levels=new List<SurfaceSnapshot>();
            long retainedLevels=checked(retainedGlobal+PlanetTerrainBakeCache.EstimateSnapshotBytes(published));
            foreach(var level in global.LodPyramid)
            {
                if(level.Resolution==published.Resolution)continue;
                var budget=RepairAllowance(captured.MaximumWorkingBytes,retainedLevels,level.Tiles,published.Regions,published.AutomaticMaterialProfile);
                var coarse=new SurfaceSnapshot(published.Recipe,published.Revision,published.CanonicalTileLevel,level.Resolution,level.Tiles,published.Detail,published.Regions,published.Stamps,published.AutomaticMaterialProfile);
                var ready=SurfaceMaterialRepair.Rebuild(coarse,budget,progress,cancelled);levels.Add(ready);
                retainedLevels=checked(retainedLevels+PlanetTerrainBakeCache.EstimateSnapshotBytes(ready));
            }
            CheckCancelled(cancelled);var completed=new PlanetTerrainGenerationResult(global,published,diagnostics,cached,levels);
            if(completed.EstimatedResidentBytes>captured.MaximumWorkingBytes)throw new InvalidOperationException("Completed terrain and all resolved material LOD arrays exceed MaximumWorkingBytes; reduce region count/resolution before publishing.");
            return completed;
        }
        internal static SurfaceMaterialRepairSettings RepairAllowance(long maximumBytes,long retainedBytes,IReadOnlyList<SurfaceTileData> tiles,
            IReadOnlyList<SurfaceRegionData> regions,SurfaceAutomaticMaterialProfile profile)
        {
            long remaining=maximumBytes-retainedBytes;var cost=SurfaceMaterialRepair.EstimateCost(tiles,regions,profile);
            if(remaining<=0||cost.EstimatedWorkingBytes>remaining)throw new InvalidOperationException("Automatic material preparation plus retained global/fine/coarse data exceeds MaximumWorkingBytes before allocation.");
            return new SurfaceMaterialRepairSettings{MaximumWorkingBytes=remaining};
        }
        static void CheckCancelled(Func<bool> cancelled){if(cancelled!=null&&cancelled())throw new OperationCanceledException("Terrain generation cancelled.");}
    }
}
