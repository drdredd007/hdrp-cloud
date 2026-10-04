using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Captured intrinsic detail policy. This does not rerun World Orogen or change its macro map.
    /// MinimumWavelengthMetres describes a characteristic carrier scale, not a strict Fourier cutoff or minimum feature width.</summary>
    public readonly struct WorldOrogenDetailRecipe
    {
        public const int CurrentVersion = 1;
        public readonly int Version, Seed, ConditioningResolution, Morphology;
        public readonly bool Enabled;
        public readonly double Strength, MinimumWavelengthMetres, MaximumAmplitudeMetres, ReliefFraction, CoastFadeMetres;
        public WorldOrogenDetailRecipe(bool enabled, int seed, double strength = .5, double minimumWavelengthMetres = 64,
            int conditioningResolution = 128, double maximumAmplitudeMetres = 120, double reliefFraction = .15, double coastFadeMetres = 150, int morphology = 1)
        {
            Version=CurrentVersion;Enabled=enabled;Seed=seed;Strength=strength;MinimumWavelengthMetres=minimumWavelengthMetres;
            ConditioningResolution=conditioningResolution;MaximumAmplitudeMetres=maximumAmplitudeMetres;ReliefFraction=reliefFraction;CoastFadeMetres=coastFadeMetres;
            Morphology=morphology;
        }
        public static WorldOrogenDetailRecipe Default => new WorldOrogenDetailRecipe(true, 7243);
        public static WorldOrogenDetailRecipe Disabled => new WorldOrogenDetailRecipe(false, 0, 0);
        public bool IsValid => Version==CurrentVersion && (Morphology==1 || Morphology==2 || Morphology==3 || Morphology==4 || Morphology==5) && math.isfinite(Strength) && Strength>=0 && Strength<=1 &&
            math.isfinite(MinimumWavelengthMetres) && MinimumWavelengthMetres>=64 && MinimumWavelengthMetres<=100000 &&
            ConditioningResolution>=2 && ConditioningResolution<=512 && (ConditioningResolution&(ConditioningResolution-1))==0 &&
            math.isfinite(MaximumAmplitudeMetres) && MaximumAmplitudeMetres>=0 && MaximumAmplitudeMetres<=1000 &&
            math.isfinite(ReliefFraction) && ReliefFraction>=0 && ReliefFraction<=1 && math.isfinite(CoastFadeMetres) && CoastFadeMetres>=1 && CoastFadeMetres<=100000;
        public SurfaceContentHash ContentDigest
        {
            get {var r=this;return SurfaceHashing.Compute(w=>{w.Write(r.Version);w.Write(r.Enabled);w.Write(r.Seed);w.Write(r.Strength);w.Write(r.MinimumWavelengthMetres);
                w.Write(r.ConditioningResolution);w.Write(r.MaximumAmplitudeMetres);w.Write(r.ReliefFraction);w.Write(r.CoastFadeMetres);
                if(r.Morphology>=2){w.Write("WorldOrogenDetail.Morphology");w.Write(r.Morphology);}});}
        }
    }

    /// <summary>One six-face amplitude range level. Cells at level zero use the captured node grid.</summary>
    public readonly struct WorldOrogenDetailRangeLevel
    {
        public readonly int Level, Resolution, Offset;
        public WorldOrogenDetailRangeLevel(int level,int resolution,int offset){Level=level;Resolution=resolution;Offset=offset;}
    }
}
