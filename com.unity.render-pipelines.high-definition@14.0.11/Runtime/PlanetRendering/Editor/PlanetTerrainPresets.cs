using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetTerrainClimatePreset { Desert, Temperate, Polar }
    public enum PlanetTerrainScatterPreset { Sparse, Balanced, Dense }

    public readonly struct PlanetTerrainClimatePresetData
    {
        public readonly double EquatorTemperature,PoleTemperature,LapseRate,MoistureRadiusFraction,RainMetresPerSecond,EvaporationPerSecond;
        internal PlanetTerrainClimatePresetData(double equator,double pole,double lapse,double moisture,double rain,double evaporation)
        {EquatorTemperature=equator;PoleTemperature=pole;LapseRate=lapse;MoistureRadiusFraction=moisture;RainMetresPerSecond=rain;EvaporationPerSecond=evaporation;}
        public bool IsValid=>math.isfinite(EquatorTemperature)&&math.isfinite(PoleTemperature)&&math.isfinite(LapseRate)&&LapseRate>=0&&
            math.isfinite(MoistureRadiusFraction)&&MoistureRadiusFraction>0&&MoistureRadiusFraction<=1&&
            math.isfinite(RainMetresPerSecond)&&RainMetresPerSecond>=0&&math.isfinite(EvaporationPerSecond)&&EvaporationPerSecond>=0;
        public SurfaceBakeSettings Capture(SurfaceRecipe recipe,SurfaceBakeSettings existing)
        {
            if(existing==null)throw new ArgumentNullException(nameof(existing));
            if(!IsValid)throw new ArgumentException("Unsupported climate preset data.");
            if(recipe.Style!=SurfaceStyle.EarthLike)throw new ArgumentException("Atmospheric Desert/Temperate/Polar presets require EarthLike. Rocky retains its explicit dry/no-rain policy; change Style deliberately before selecting an atmospheric preset.");
            var result=existing.Clone();result.EquatorTemperature=EquatorTemperature;result.PoleTemperature=PoleTemperature;
            result.LapseRatePerKilometre=LapseRate;result.MoistureDistanceMetres=recipe.Radius*MoistureRadiusFraction;
            result.RainMetresPerSecond=RainMetresPerSecond;result.EvaporationPerSecond=EvaporationPerSecond;
            if(!result.Validate(recipe,out var reason))throw new ArgumentException(reason);
            return result;
        }
    }
    public readonly struct PlanetTerrainScatterPresetData
    {
        public readonly double CapacityFraction;
        public readonly PlanetTerrainScatterPreviewBudget Budget;
        internal PlanetTerrainScatterPresetData(double fraction,int cells,int candidates,long bytes,int batches)
        {CapacityFraction=fraction;Budget=new PlanetTerrainScatterPreviewBudget(cells,candidates,bytes,batches);}
    }
    public sealed class PlanetTerrainScatterPresetPlan
    {
        public double ReferenceRadius {get;}
        public SurfaceContentHash SourceProfileDigest {get;}
        public PlanetTerrainScatterPresetData Data {get;}
        public IReadOnlyList<SurfaceScatterSpecies> Species {get;}
        internal PlanetTerrainScatterPresetPlan(double radius,SurfaceScatterProfile source,PlanetTerrainScatterPresetData data)
        {
            ReferenceRadius=radius;SourceProfileDigest=source.ContentDigest;Data=data;
            var result=new List<SurfaceScatterSpecies>();
            foreach(var item in source.Species)
            {
                double width=2.0/(1L<<item.FixedLevel),area=radius*radius*width*width;
                double density=item.CandidatesPerCell/area*data.CapacityFraction;
                // Cube-face centre is the maximum metric Jacobian. This bounds every cell on every face.
                double capacity=density*area;
                if(!math.isfinite(area)||area<=0||!math.isfinite(density)||density<=0||!math.isfinite(capacity)||capacity>item.CandidatesPerCell||item.NormalSampleMetres>radius*.25)
                    throw new ArgumentException($"Species {item.SpeciesId}: this radius cannot safely support its fixed-grid proposal/normal contract. Adjust grid or normal support explicitly; no settings were changed.");
                var proposed=new SurfaceScatterSpecies(item.SpeciesId,item.FixedLevel,item.CandidatesPerCell,density,item.MinimumHeight,item.MaximumHeight,
                    item.MaximumSlopeDegrees,item.MinimumWetness,item.MaximumWetness,item.MaterialAffinity,item.ScaleRange,item.NormalSampleMetres,
                    item.Seed,item.RequiredChannels,item.ExcludeSurfaceStamps,item.MinimumReferenceChordSpacingMetres);
                if(SurfaceScatterSpacing.Validate(proposed,radius,out _)!=SurfaceSampleStatus.Ready)
                    throw new ArgumentException($"Species {item.SpeciesId}: minimum reference-chord spacing exceeds the bounded neighbour budget at this radius. Reduce spacing or proposals before applying a preset; no settings were changed.");
                result.Add(proposed);
            }
            Species=result.AsReadOnly();
        }
    }

    /// <summary>Explicit editor actions only. No bake, asset publication, key rewrite or default-asset mutation.</summary>
    public static class PlanetTerrainPresets
    {
        public static PlanetTerrainClimatePresetData Climate(PlanetTerrainClimatePreset preset)
        {
            switch(preset)
            {
                case PlanetTerrainClimatePreset.Desert:return new PlanetTerrainClimatePresetData(45,10,5,.025,0,.003);
                case PlanetTerrainClimatePreset.Temperate:return new PlanetTerrainClimatePresetData(24,-18,6.5,.25,.002,.0002);
                case PlanetTerrainClimatePreset.Polar:return new PlanetTerrainClimatePresetData(-15,-45,4.5,.12,.00015,.00004);
                default:throw new ArgumentOutOfRangeException(nameof(preset));
            }
        }
        public static PlanetTerrainScatterPresetData Scatter(PlanetTerrainScatterPreset preset)
        {
            switch(preset)
            {
                case PlanetTerrainScatterPreset.Sparse:return new PlanetTerrainScatterPresetData(.125,64,32768,64L*1024*1024,2);
                case PlanetTerrainScatterPreset.Balanced:return new PlanetTerrainScatterPresetData(.25,128,65536,128L*1024*1024,4);
                case PlanetTerrainScatterPreset.Dense:return new PlanetTerrainScatterPresetData(.5,256,131072,256L*1024*1024,8);
                default:throw new ArgumentOutOfRangeException(nameof(preset));
            }
        }
        public static PlanetTerrainScatterPresetPlan CaptureScatter(double radius,IEnumerable<SurfaceScatterSpecies> species,PlanetTerrainScatterPreset preset,
            PlanetTerrainScatterPreviewBudget existingBudget=default)
        {
            if(!math.isfinite(radius)||radius<=0)throw new ArgumentException("Scatter preset requires a finite positive reference planet radius.");
            var desired=Scatter(preset);
            if(existingBudget.IsValid)
                desired=new PlanetTerrainScatterPresetData(desired.CapacityFraction,Math.Max(desired.Budget.MaximumCells,existingBudget.MaximumCells),
                    Math.Max(desired.Budget.MaximumCandidates,existingBudget.MaximumCandidates),Math.Max(desired.Budget.MaximumBytes,existingBudget.MaximumBytes),desired.Budget.NewCellsPerFrame);
            else if(existingBudget.MaximumCells!=0||existingBudget.MaximumCandidates!=0||existingBudget.MaximumBytes!=0||existingBudget.NewCellsPerFrame!=0)
                throw new ArgumentException("Existing scatter residency limits must be complete and positive.");
            return new PlanetTerrainScatterPresetPlan(radius,new SurfaceScatterProfile(species),desired);
        }
        public static void ApplyClimate(PlanetTerrainRecipeAsset recipe,PlanetTerrainClimatePreset preset)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Apply climate presets outside Play; runtime snapshots are immutable.");
            if(!recipe)throw new ArgumentException("Assign a Terrain Recipe before applying climate settings.");
            var result=Climate(preset).Capture(recipe.Recipe,recipe.Bake);
            Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Planet climate "+preset);
            Undo.RecordObject(recipe,"Planet climate "+preset);recipe.Bake=result;EditorUtility.SetDirty(recipe);
        }
        public static PlanetTerrainScatterPresetPlan ApplyScatter(PlanetGeneratorAsset generator,PlanetTerrainScatterPreset preset)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Apply scatter presets outside Play; runtime collider/renderer revisions are immutable.");
            if(!generator||!generator.ScatterSettings||!generator.ScatterSettings.IsValid)
                throw new ArgumentException("Assign valid Scatter Settings and all species meshes/materials before applying a density/quality preset.");
            var settings=generator.ScatterSettings;double radius=generator.Radius;
            if(generator.SurfaceData)
            {
                if(!generator.SurfaceData.TryCreateSnapshot(out var snapshot,out var reason))throw new InvalidOperationException(reason);
                if(radius!=snapshot.Recipe.Radius)throw new InvalidOperationException("Generator Radius differs from its bound signed Surface Data radius. Match the authored radius to the current dataset, or explicitly rebuild/rebind terrain, before applying scatter settings; no density was changed.");
            }
            var captured=new List<SurfaceScatterSpecies>();foreach(var item in settings.Species)captured.Add(item.Placement);
            var existing=new PlanetTerrainScatterPreviewBudget(settings.MaximumResidentCells,settings.MaximumResidentCandidates,settings.MaximumResidentBytes,settings.MaximumNewCellsPerFrame);
            var plan=CaptureScatter(radius,captured,preset,existing);var targets=new UnityEngine.Object[settings.Species.Length+1];targets[0]=settings;
            for(int i=0;i<settings.Species.Length;i++)targets[i+1]=settings.Species[i];
            // All source species and the full proposal-capacity plan passed before the first undo/dirty mutation.
            Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Planet scatter "+preset);Undo.RecordObjects(targets,"Planet scatter "+preset);
            foreach(var target in settings.Species)
                foreach(var item in plan.Species)if(target.SpeciesId==item.SpeciesId){target.DensityPerSquareMetre=item.DensityPerSquareMetre;EditorUtility.SetDirty(target);break;}
            settings.MaximumResidentCells=plan.Data.Budget.MaximumCells;settings.MaximumResidentCandidates=plan.Data.Budget.MaximumCandidates;
            settings.MaximumResidentBytes=plan.Data.Budget.MaximumBytes;settings.MaximumNewCellsPerFrame=plan.Data.Budget.NewCellsPerFrame;
            EditorUtility.SetDirty(settings);return plan;
        }
    }
}
