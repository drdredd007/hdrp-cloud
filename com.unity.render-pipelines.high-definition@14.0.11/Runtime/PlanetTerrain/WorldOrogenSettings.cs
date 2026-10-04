// World Orogen parameter contract, adapted from raguilar011095/planet_heightmap_generation.
// Upstream commit cc2662b4edd52231c4f65d8765f3ef12cd82d9b7. GPL-3.0-only;
// see Runtime/PlanetRendering/WorldOrogen/LICENSE.txt and THIRD_PARTY_NOTICES.txt.
using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Captured authoring inputs. World pose, camera, radius and rendering budgets are independent.</summary>
    [Serializable]
    public sealed class WorldOrogenSettings
    {
        public const string SourceRepository = "https://github.com/raguilar011095/planet_heightmap_generation";
        public const string SourceCommit = "cc2662b4edd52231c4f65d8765f3ef12cd82d9b7";
        public const int PortVersion = 1;
        public const int MinimumDetail = 5000, MaximumDetail = 2560000, CoarseDetail = 20000;
        public const int AutomaticClimateThreshold = 300000;
        public int Seed = 7243;
        public int Detail = 204000;
        public double Irregularity = .75;
        public int Plates = 80, Continents = 4;
        public double Roughness = .40, ContinentSizeVariety = .35, LandCoverage = .30;
        public double TerrainWarp = .75, Smoothing = .10, GlacialErosion = .50;
        public double HydraulicErosion = .50, ThermalErosion = .10, RidgeSharpening = .50;
        public double TemperatureOffset, PrecipitationOffset;
        public bool AutoClimate = true;
        public int[] ToggledPlateIndices = Array.Empty<int>();
        // Retained only for upstream planet-code interoperability. The current worker
        // uses a fixed three-pass soil-creep stage, irrespective of this historical input.
        public double CodeSoilCreep = .75;

        public bool GenerateClimateAutomatically => AutoClimate && Detail <= AutomaticClimateThreshold;
        public WorldOrogenSettings Clone()
        {
            var result = (WorldOrogenSettings)MemberwiseClone();
            result.ToggledPlateIndices = ToggledPlateIndices == null ? Array.Empty<int>() : (int[])ToggledPlateIndices.Clone();
            return result;
        }
        public bool Validate(out string error)
        {
            error = null;
            if (Seed < 0 || Seed >= 16777216) error = "World Orogen seed must be in [0, 16777215].";
            else if (Detail < MinimumDetail || Detail > MaximumDetail) error = "Detail must be in [5000, 2560000].";
            else if (Plates < 4 || Plates > 120 || Continents < 1 || Continents > 10)
                error = "World Orogen requires 4–120 plates and 1–10 requested continents.";
            else if (!Unit(Irregularity) || !Unit(ContinentSizeVariety) || !Unit(LandCoverage) ||
                !Unit(TerrainWarp) || !Unit(Smoothing) || !Unit(GlacialErosion) || !Unit(HydraulicErosion) ||
                !Unit(ThermalErosion) || !Unit(RidgeSharpening) || !Unit(CodeSoilCreep) ||
                !math.isfinite(Roughness) || Roughness < 0 || Roughness > .5)
                error = "Terrain controls must remain within the upstream slider ranges.";
            else if (!math.isfinite(TemperatureOffset) || TemperatureOffset < -15 || TemperatureOffset > 15 ||
                !math.isfinite(PrecipitationOffset) || PrecipitationOffset < -1 || PrecipitationOffset > 1)
                error = "Climate offsets require temperature [-15,15] °C and precipitation [-1,1].";
            else if (ToggledPlateIndices != null)
                foreach (int index in ToggledPlateIndices)
                    if (index < 0 || index >= Plates) { error = "A reshaped plate index is outside the current plate count."; break; }
            return error == null;
        }
        static bool Unit(double value) => math.isfinite(value) && value >= 0 && value <= 1;

        public SurfaceContentHash ConfigurationDigest() => SurfaceHashing.Compute(writer =>
        {
            writer.Write(PortVersion); writer.Write(SourceCommit); writer.Write(Seed); writer.Write(Detail);
            writer.Write(Irregularity); writer.Write(Plates); writer.Write(Continents); writer.Write(Roughness);
            writer.Write(ContinentSizeVariety); writer.Write(LandCoverage); writer.Write(TerrainWarp);
            writer.Write(Smoothing); writer.Write(GlacialErosion); writer.Write(HydraulicErosion);
            writer.Write(ThermalErosion); writer.Write(RidgeSharpening); writer.Write(TemperatureOffset);
            writer.Write(PrecipitationOffset); writer.Write(AutoClimate);
            var toggles = ToggledPlateIndices ?? Array.Empty<int>(); writer.Write(toggles.Length);
            foreach (int index in toggles) writer.Write(index);
        });
        // Upstream detail-scale.js uses a fifth-power control, not a logarithmic slider.
        public static int DetailFromSlider(int position)
        {
            double t = math.clamp(position, 0, 1000) / 1000.0;
            return (int)math.floor((MinimumDetail + (MaximumDetail - MinimumDetail) * Math.Pow(t, 5)) / 1000 + .5) * 1000;
        }
        public static int SliderFromDetail(int detail) => (int)math.floor(1000 *
            Math.Pow(math.max(0, detail - MinimumDetail) / (double)(MaximumDetail - MinimumDetail), .2) + .5);
        public static double HeightMetres(double elevation)
        {
            if (!math.isfinite(elevation)) throw new ArgumentException("Elevation must be finite.");
            if (elevation <= 0) return elevation * 10000;
            double t = math.min(elevation, 1), t2 = t * t;
            return 6000 * t2 * t2 * (5 - 4 * t);
        }
    }
}
