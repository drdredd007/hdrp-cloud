using System;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetOrogenDetailMorphology { IndependentBands = 1, FlowAlignedRidges = 2, FlowAlignedPeaks = 3, FlowFilteredPeaks = 4, FlowBroadPeaks = 5 }

    [Serializable]
    public sealed class PlanetOrogenDetailSettings
    {
        public PlanetOrogenDetailMorphology Morphology = PlanetOrogenDetailMorphology.FlowBroadPeaks;
        [Range(0,1)] public double Strength = .85;
        [Tooltip("Characteristic scale of the finest carrier. Flow profiles use a dominant carrier four times larger, or eight times for Flow Broad Peaks. This is not a minimum feature width. Larger values need less near-surface LOD refinement.")]
        public double MinimumWavelengthMetres = 256;
        public int ConditioningResolution = 128;
        [Tooltip("Absolute amplitude cap before strength and coast / relief conditioning.")]
        public double MaximumAmplitudeMetres = 1000;
        [Range(0,1)] public double ReliefFraction = .25;
        public double CoastFadeMetres = 300;
        public WorldOrogenDetailRecipe Capture(int seed) => new WorldOrogenDetailRecipe(true, seed, Strength, MinimumWavelengthMetres,
            ConditioningResolution, MaximumAmplitudeMetres, ReliefFraction, CoastFadeMetres, (int)Morphology);
    }
}
