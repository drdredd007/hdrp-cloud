using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Authoring settings; Bake captures a clone before invoking any client callback.</summary>
    [Serializable]
    public sealed class SurfaceBakeSettings
    {
        public int FaceResolution = 32, HydraulicIterations = 48, ThermalIterations = 16;
        /// <summary>Version-four routing graph only. Zero selects a bounded resolution from the captured shelf/belt widths; it does not increase stored height-map resolution.</summary>
        public int DrainageTopologyResolution;
        public SurfaceLandformBuildSettings Landform = new SurfaceLandformBuildSettings();
        public double ContinentScaleMetres, MountainScaleMetres;
        public double LandFraction = .4, MountainFraction = .3;
        public int ProvinceCount = 24;
        public double ShelfWidthMetres, MountainBeltWidthMetres, RegionalFeatureScaleMetres = 4000;
        public double TimeStepSeconds = 60, RainMetresPerSecond = .002, EvaporationPerSecond = .0002;
        public double FlowDamping = .05, Capacity = 2, ErosionPerSecond = .002, DepositionPerSecond = .004;
        public double MaximumErosionDepthFraction = .2, AngleOfReposeDegrees = 32, ThermalRate = .2;
        public double EquatorTemperature = 30, PoleTemperature = -25, LapseRatePerKilometre = 6.5;
        public double MoistureDistanceMetres;
        public long MaximumWorkingBytes = 512L * 1024 * 1024;
        public bool EnableHydraulicOnRocky;
        public static SurfaceBakeSettings Preview => new SurfaceBakeSettings();
        public static SurfaceBakeSettings Final => new SurfaceBakeSettings { FaceResolution = 128, HydraulicIterations = 160, ThermalIterations = 48 };
        public SurfaceBakeSettings Clone()
        {var clone=(SurfaceBakeSettings)MemberwiseClone();clone.Landform=Landform?.Clone();return clone;}
        public bool Validate(SurfaceRecipe recipe, out string error)=>Validate(recipe,true,out error);
        /// <summary>
        /// Validates erosion/climate arithmetic over an already admitted source field. The regional
        /// caller must separately admit its actual local graph and native source copies; this path
        /// does not reserve or reconstruct a new global continent/drainage graph.
        /// </summary>
        public bool ValidateErosion(SurfaceRecipe recipe,out string error)=>Validate(recipe,false,out error);
        bool Validate(SurfaceRecipe recipe,bool freshGlobal,out string error)
        {
            error = null;
            if (!recipe.IsValid || (recipe.AlgorithmVersion != SurfaceRecipe.CurrentAlgorithmVersion && recipe.AlgorithmVersion != SurfaceRecipe.StructuralAlgorithmVersion &&
                !SurfaceRecipe.HasStructuralAuthority(recipe.AlgorithmVersion))) error = "Unsupported or invalid generator recipe.";
            else if (!CubeSurface.ValidResolution(FaceResolution) || FaceResolution < 2 || FaceResolution > 512) error = "FaceResolution must be a power of two in [2,512].";
            else if (HydraulicIterations < 0 || HydraulicIterations > 4096 || ThermalIterations < 0 || ThermalIterations > 4096) error = "Iteration counts must lie in [0,4096].";
            else if (!Positive(TimeStepSeconds) || TimeStepSeconds > 86400 || !Unit(FlowDamping) || !Unit(MaximumErosionDepthFraction) || !Unit(ThermalRate) ||
                !Unit(LandFraction) || !Unit(MountainFraction) || !NonNegative(ContinentScaleMetres) || !NonNegative(MountainScaleMetres) ||
                !NonNegative(RainMetresPerSecond) || !NonNegative(EvaporationPerSecond) || !NonNegative(Capacity) || !NonNegative(ErosionPerSecond) ||
                !NonNegative(DepositionPerSecond) || !NonNegative(MoistureDistanceMetres) || !NonNegative(LapseRatePerKilometre) ||
                !math.isfinite(EquatorTemperature) || !math.isfinite(PoleTemperature) || !Positive(AngleOfReposeDegrees) || AngleOfReposeDegrees >= 89)
                error = "Generator parameters must be finite and within their physical bounds.";
            else if (recipe.Radius < 1 || recipe.Radius > 1e12 || math.max(math.abs(recipe.MinimumHeight), math.abs(recipe.MaximumHeight)) > recipe.Radius * .5 ||
                RainMetresPerSecond > 1000 || EvaporationPerSecond > 1000 || ErosionPerSecond > 1000 || DepositionPerSecond > 1000 || Capacity > 1e6)
                error = "Offline thin-shell bake requires radius [1,1e12] metres, relief at most half the radius, and bounded process rates.";
            else if(freshGlobal&&recipe.AlgorithmVersion==SurfaceRecipe.DrainageAuthorityAlgorithmVersion&&
                (!CubeSurface.ValidResolution(ResolvedDrainageTopologyResolution(recipe))||ResolvedDrainageTopologyResolution(recipe)>512||
                 (DrainageTopologyResolution!=0&&(DrainageTopologyResolution<16||DrainageTopologyResolution<RequiredDrainageTopologyResolution(recipe)))))
                error="Captured drainage topology requires a power-of-two resolution in [16,512] with at least two cells across the narrower shelf/belt; requested controls exceed its bounded admission.";
            else if (freshGlobal&&MaximumWorkingBytes < EstimatedWorkingBytes(recipe)) error = "Requested connected graph exceeds MaximumWorkingBytes.";
            else if (!freshGlobal&&MaximumWorkingBytes<=0) error="Erosion requires a positive working-memory budget; the caller must admit its local graph and source copies.";
            else if (!math.isfinite(recipe.Radius * recipe.Radius * (recipe.MaximumHeight - recipe.MinimumHeight + 1))) error = "Recipe exceeds bake volume arithmetic.";
            else if (recipe.AlgorithmVersion >= SurfaceRecipe.StructuralAlgorithmVersion &&
                (ProvinceCount < 8 || ProvinceCount > 64 || !NonNegative(ShelfWidthMetres) || !NonNegative(MountainBeltWidthMetres) || !Positive(RegionalFeatureScaleMetres)))
                error = "Structural bake requires 8..64 provinces and finite positive metric feature controls.";
            else if (freshGlobal&&recipe.AlgorithmVersion == SurfaceRecipe.StructuralAuthorityAlgorithmVersion &&
                recipe.Radius * Math.PI / math.min(recipe.Radius * .025, RegionalFeatureScaleMetres) > int.MaxValue - 4)
                error = "Structural kilometre-scale branch coordinates exceed the portable integer domain.";
            else if(freshGlobal&&recipe.AlgorithmVersion==SurfaceRecipe.DrainageAuthorityAlgorithmVersion&&(LandFraction<.02||LandFraction>.9))
                error="Captured hemisphere-coast generation requires LandFraction in [.02,.9]; existing algorithms retain their original range.";
            else if(freshGlobal&&recipe.AlgorithmVersion==SurfaceRecipe.LandformAuthorityAlgorithmVersion)
            {
                if(Landform==null||!Landform.Validate(recipe,out error))error=error??"Landform generation requires an explicit captured spacing policy.";
                else if(LandFraction<.02||LandFraction>.9)error="Captured landform coastline requires LandFraction in [.02,.9].";
            }
            return error == null;
        }
        static bool Positive(double value) => math.isfinite(value) && value > 0;
        static bool NonNegative(double value) => math.isfinite(value) && value >= 0;
        static bool Unit(double value) => math.isfinite(value) && value >= 0 && value <= 1;
        // Conservative live arrays + dictionary/hash-set construction + immutable output/pyramid overhead.
        public static long EstimateWorkingBytes(int resolution) => checked((6L * resolution * resolution + 2) * 768 + 8L * 1024 * 1024);
        public int ResolvedDrainageTopologyResolution(SurfaceRecipe recipe)=>DrainageTopologyResolution==0?RequiredDrainageTopologyResolution(recipe):DrainageTopologyResolution;
        int RequiredDrainageTopologyResolution(SurfaceRecipe recipe)
        {
            double shelf=math.min(recipe.Radius*.2,ShelfWidthMetres>0?ShelfWidthMetres:recipe.Radius*.04);
            double range=MountainScaleMetres>0?MountainScaleMetres:recipe.Radius*.22;
            double belt=math.min(recipe.Radius*.2,MountainBeltWidthMetres>0?MountainBeltWidthMetres:range*.15);
            double required=4*recipe.Radius/math.min(shelf,belt);
            if(!math.isfinite(required)||required>512)return 1024;
            int resolution=16;while(resolution<required)resolution<<=1;return resolution;
        }
        public long EstimatedWorkingBytes(SurfaceRecipe recipe)
        {
            long graph = EstimateWorkingBytes(FaceResolution), raw = 6L * (FaceResolution + 1) * (FaceResolution + 1) * 4;
            // Five admits the exact adaptive graph separately before its solve/control allocation.
            // This is the retained global erosion/reference reservation, not a claim that every
            // requested adaptive planet fits. Build rejects actual-count overflow without substitution.
            if(recipe.AlgorithmVersion==SurfaceRecipe.LandformAuthorityAlgorithmVersion)
                return checked(graph+raw*6+32L*1024*1024);
            if (recipe.AlgorithmVersion == SurfaceRecipe.StructuralAlgorithmVersion) return checked(graph + 256L * 1024 + raw);
            if (!SurfaceRecipe.HasStructuralAuthority(recipe.AlgorithmVersion)) return graph;
            // Raw retained by geomorphology, immutable field and SHA serialization; temporary managed/native
            // controls, index list growth and copied indices are also live during the structural capture.
            long index = 6L * SurfaceStructuralField.SpatialResolution * SurfaceStructuralField.SpatialResolution * 8 + SurfaceStructuralField.MaximumReferences * 4L;
            long controls = 64L * 64 + (3 * 64 - 6) * 128L + (2 * 64 - 4) * 24L + 512;
            long structural=checked(graph + raw * 6 + index * 8 + controls * 8 + 8L * 1024 * 1024);
            // Captured drainage, constructor clones/hash, derived KD builders/native controls, and
            // flood/area/adjacency scratch must coexist before publication. Admission uses the declared
            // maximum instead of quietly assuming a particular seed generates few catchments.
            if(recipe.AlgorithmVersion!=SurfaceRecipe.DrainageAuthorityAlgorithmVersion)return structural;
            int routing=ResolvedDrainageTopologyResolution(recipe);
            return checked(structural+SurfaceDrainageField.MaximumResidentBytes*4+(6L*routing*routing+2)*160+
                (routing==FaceResolution?0:EstimateWorkingBytes(routing)));
        }
        public SurfaceContentHash ConfigurationDigest(SurfaceRecipe recipe) => SurfaceHashing.Compute(writer =>
        {
            writer.Write(1); SurfaceHashing.WriteRecipe(writer, recipe);
            writer.Write(FaceResolution); writer.Write(HydraulicIterations); writer.Write(ThermalIterations);
            writer.Write(ContinentScaleMetres); writer.Write(MountainScaleMetres); writer.Write(LandFraction); writer.Write(MountainFraction);
            writer.Write(TimeStepSeconds); writer.Write(RainMetresPerSecond); writer.Write(EvaporationPerSecond); writer.Write(FlowDamping);
            writer.Write(Capacity); writer.Write(ErosionPerSecond); writer.Write(DepositionPerSecond); writer.Write(MaximumErosionDepthFraction);
            writer.Write(AngleOfReposeDegrees); writer.Write(ThermalRate); writer.Write(EquatorTemperature); writer.Write(PoleTemperature);
            writer.Write(LapseRatePerKilometre); writer.Write(MoistureDistanceMetres); writer.Write(EnableHydraulicOnRocky);
            if (recipe.AlgorithmVersion >= SurfaceRecipe.StructuralAlgorithmVersion)
            { writer.Write(ProvinceCount); writer.Write(ShelfWidthMetres); writer.Write(MountainBeltWidthMetres); writer.Write(RegionalFeatureScaleMetres); }
            if (recipe.AlgorithmVersion == SurfaceRecipe.StructuralAuthorityAlgorithmVersion)
            { writer.Write(SurfaceStructuralField.CurrentVersion); writer.Write(SurfaceStructuralField.SpatialResolution); writer.Write(1); } // Portable macro/reference policy.
            if(recipe.AlgorithmVersion==SurfaceRecipe.DrainageAuthorityAlgorithmVersion)
            {writer.Write(2);writer.Write(SurfaceDrainageField.CurrentVersion);writer.Write(2);writer.Write(ResolvedDrainageTopologyResolution(recipe));} // Separate, admitted routing graph; captured catchment/coast and stream-power policy.
            if(recipe.AlgorithmVersion==SurfaceRecipe.LandformAuthorityAlgorithmVersion)
            {writer.Write(3);writer.Write(SurfaceLandformField.CurrentVersion);if(Landform==null)throw new ArgumentException("Missing captured landform policy.");Landform.Write(writer);}
            // Memory budget affects admissibility, not generated content.
        });
    }

    public readonly struct SurfaceBakeProgress
    {
        public readonly string Stage;
        public readonly int Completed, Total;
        public double Fraction => Total <= 0 ? 1 : math.clamp((double)Completed / Total, 0, 1);
        public SurfaceBakeProgress(string stage, int completed, int total) { Stage = stage; Completed = completed; Total = total; }
    }
    public readonly struct SurfaceBakeDiagnostics
    {
        public readonly double ElapsedSeconds, ErodedVolume, DepositedVolume, InitialGroundVolume, FinalGroundVolume;
        public readonly double SuspendedSedimentVolume, MassResidual, MaxLodError, SurfaceArea, MaximumSlope, PublishedGroundVolume, QuantizationVolume;
        public readonly int NodeCount, EdgeCount, ActualHydraulicIterations;
        public readonly int DrainageTopologyResolution, DrainageTopologyNodeCount, CapturedDrainageNodeCount;
        public readonly long EstimatedWorkingBytes;
        internal SurfaceBakeDiagnostics(double elapsed, int nodes, int edges, long bytes, double eroded, double deposited,
            double initial, double final, double suspended, double lod, double area, double slope, int hydraulic, double published,
            int drainageTopologyResolution=0,int drainageTopologyNodeCount=0,int capturedDrainageNodeCount=0)
        {
            ElapsedSeconds = elapsed; NodeCount = nodes; EdgeCount = edges; EstimatedWorkingBytes = bytes;
            ErodedVolume = eroded; DepositedVolume = deposited; InitialGroundVolume = initial; FinalGroundVolume = final;
            SuspendedSedimentVolume = suspended; MassResidual = final + suspended - initial;
            MaxLodError = lod; SurfaceArea = area; MaximumSlope = slope; ActualHydraulicIterations = hydraulic;
            PublishedGroundVolume = published; QuantizationVolume = published - final;
            DrainageTopologyResolution=drainageTopologyResolution;DrainageTopologyNodeCount=drainageTopologyNodeCount;CapturedDrainageNodeCount=capturedDrainageNodeCount;
        }
    }
    public sealed class SurfaceBakeResult
    {
        public SurfaceSnapshot Snapshot { get; }
        public SurfaceBakeDiagnostics Diagnostics { get; }
        public SurfaceHydrologyField Hydrology { get; }
        public SurfaceGeomorphology Geomorphology { get; }
        /// <summary>Finest first; every level has six complete faces and the same canonical material/erosion semantics.</summary>
        public IReadOnlyList<SurfaceLodLevel> LodPyramid { get; }
        internal SurfaceBakeResult(SurfaceSnapshot snapshot, SurfaceBakeDiagnostics diagnostics, List<SurfaceLodLevel> pyramid, SurfaceHydrologyField hydrology, SurfaceGeomorphology geomorphology = null)
        { Snapshot = snapshot; Diagnostics = diagnostics; LodPyramid = pyramid.AsReadOnly(); Hydrology = hydrology; Geomorphology = geomorphology; }
    }
    public sealed class SurfaceLodLevel
    {
        public int Resolution { get; }
        public IReadOnlyList<SurfaceTileData> Tiles { get; }
        /// <summary>Maximum measured deviation at finest stored vertices from this level's triangle mesh; not a continuous analytic bound.</summary>
        public double MeasuredErrorMetres { get; }
        internal SurfaceLodLevel(int resolution, SurfaceTileData[] tiles, double error)
        { Resolution = resolution; Tiles = Array.AsReadOnly(tiles); MeasuredErrorMetres = error; }
    }
}
