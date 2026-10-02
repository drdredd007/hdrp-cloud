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
        public double ContinentScaleMetres, MountainScaleMetres;
        public double LandFraction = .4, MountainFraction = .3;
        public double TimeStepSeconds = 60, RainMetresPerSecond = .002, EvaporationPerSecond = .0002;
        public double FlowDamping = .05, Capacity = 2, ErosionPerSecond = .002, DepositionPerSecond = .004;
        public double MaximumErosionDepthFraction = .2, AngleOfReposeDegrees = 32, ThermalRate = .2;
        public double EquatorTemperature = 30, PoleTemperature = -25, LapseRatePerKilometre = 6.5;
        public double MoistureDistanceMetres;
        public long MaximumWorkingBytes = 512L * 1024 * 1024;
        public bool EnableHydraulicOnRocky;
        public static SurfaceBakeSettings Preview => new SurfaceBakeSettings();
        public static SurfaceBakeSettings Final => new SurfaceBakeSettings { FaceResolution = 128, HydraulicIterations = 160, ThermalIterations = 48 };
        public SurfaceBakeSettings Clone() => (SurfaceBakeSettings)MemberwiseClone();
        public bool Validate(SurfaceRecipe recipe, out string error)
        {
            error = null;
            if (!recipe.IsValid || recipe.AlgorithmVersion != SurfaceRecipe.CurrentAlgorithmVersion) error = "Unsupported or invalid generator recipe.";
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
            else if (MaximumWorkingBytes < EstimateWorkingBytes(FaceResolution)) error = "Requested connected graph exceeds MaximumWorkingBytes.";
            else if (!math.isfinite(recipe.Radius * recipe.Radius * (recipe.MaximumHeight - recipe.MinimumHeight + 1))) error = "Recipe exceeds bake volume arithmetic.";
            return error == null;
        }
        static bool Positive(double value) => math.isfinite(value) && value > 0;
        static bool NonNegative(double value) => math.isfinite(value) && value >= 0;
        static bool Unit(double value) => math.isfinite(value) && value >= 0 && value <= 1;
        // Conservative live arrays + dictionary/hash-set construction + immutable output/pyramid overhead.
        public static long EstimateWorkingBytes(int resolution) => checked((6L * resolution * resolution + 2) * 768 + 8L * 1024 * 1024);
        public SurfaceContentHash ConfigurationDigest(SurfaceRecipe recipe) => SurfaceHashing.Compute(writer =>
        {
            writer.Write(1); SurfaceHashing.WriteRecipe(writer, recipe);
            writer.Write(FaceResolution); writer.Write(HydraulicIterations); writer.Write(ThermalIterations);
            writer.Write(ContinentScaleMetres); writer.Write(MountainScaleMetres); writer.Write(LandFraction); writer.Write(MountainFraction);
            writer.Write(TimeStepSeconds); writer.Write(RainMetresPerSecond); writer.Write(EvaporationPerSecond); writer.Write(FlowDamping);
            writer.Write(Capacity); writer.Write(ErosionPerSecond); writer.Write(DepositionPerSecond); writer.Write(MaximumErosionDepthFraction);
            writer.Write(AngleOfReposeDegrees); writer.Write(ThermalRate); writer.Write(EquatorTemperature); writer.Write(PoleTemperature);
            writer.Write(LapseRatePerKilometre); writer.Write(MoistureDistanceMetres); writer.Write(EnableHydraulicOnRocky);
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
        public readonly long EstimatedWorkingBytes;
        internal SurfaceBakeDiagnostics(double elapsed, int nodes, int edges, long bytes, double eroded, double deposited,
            double initial, double final, double suspended, double lod, double area, double slope, int hydraulic, double published)
        {
            ElapsedSeconds = elapsed; NodeCount = nodes; EdgeCount = edges; EstimatedWorkingBytes = bytes;
            ErodedVolume = eroded; DepositedVolume = deposited; InitialGroundVolume = initial; FinalGroundVolume = final;
            SuspendedSedimentVolume = suspended; MassResidual = final + suspended - initial;
            MaxLodError = lod; SurfaceArea = area; MaximumSlope = slope; ActualHydraulicIterations = hydraulic;
            PublishedGroundVolume = published; QuantizationVolume = published - final;
        }
    }
    public sealed class SurfaceBakeResult
    {
        public SurfaceSnapshot Snapshot { get; }
        public SurfaceBakeDiagnostics Diagnostics { get; }
        public SurfaceHydrologyField Hydrology { get; }
        /// <summary>Finest first; every level has six complete faces and the same canonical material/erosion semantics.</summary>
        public IReadOnlyList<SurfaceLodLevel> LodPyramid { get; }
        internal SurfaceBakeResult(SurfaceSnapshot snapshot, SurfaceBakeDiagnostics diagnostics, List<SurfaceLodLevel> pyramid, SurfaceHydrologyField hydrology)
        { Snapshot = snapshot; Diagnostics = diagnostics; LodPyramid = pyramid.AsReadOnly(); Hydrology = hydrology; }
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
