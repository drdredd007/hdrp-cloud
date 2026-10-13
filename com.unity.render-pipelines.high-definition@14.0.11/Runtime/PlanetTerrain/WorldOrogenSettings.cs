// World Orogen parameter contract, adapted from raguilar011095/planet_heightmap_generation.
// Upstream commit cc2662b4edd52231c4f65d8765f3ef12cd82d9b7. GPL-3.0-only;
// see Runtime/PlanetRendering/WorldOrogen/LICENSE.txt and THIRD_PARTY_NOTICES.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
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
            var errors = GetValidationErrors();
            error = errors.Count == 0 ? null : string.Join("\n", errors);
            return errors.Count == 0;
        }
        public List<string> GetValidationErrors()
        {
            var errors = new List<string>();
            void Range(string label, double value, double minimum, double maximum) => PlanetParameterValidation.Range(errors, label, value, minimum, maximum);
            Range("Seed", Seed, 0, 16777215);
            Range("Detail", Detail, MinimumDetail, MaximumDetail);
            Range("Plate Count", Plates, 4, 120);
            Range("Requested Continents", Continents, 1, 10);
            Range("Irregularity", Irregularity, 0, 1);
            Range("Continent Size Variety", ContinentSizeVariety, 0, 1);
            Range("Target Land Coverage", LandCoverage, 0, 1);
            Range("Terrain Warp", TerrainWarp, 0, 1);
            Range("Smoothing", Smoothing, 0, 1);
            Range("Glacial Erosion", GlacialErosion, 0, 1);
            Range("Hydraulic Erosion", HydraulicErosion, 0, 1);
            Range("Thermal Erosion", ThermalErosion, 0, 1);
            Range("Ridge Sharpening", RidgeSharpening, 0, 1);
            Range("Code Soil Creep", CodeSoilCreep, 0, 1);
            Range("Roughness", Roughness, 0, .5);
            Range("Temperature Offset (°C)", TemperatureOffset, -15, 15);
            Range("Precipitation Offset", PrecipitationOffset, -1, 1);
            if (ToggledPlateIndices != null && Plates >= 4 && Plates <= 120)
                for (int i = 0; i < ToggledPlateIndices.Length; i++)
                    Range("Toggle Land / Ocean Plates[" + i + "]", ToggledPlateIndices[i], 0, Plates - 1);
            return errors;
        }

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

    // Shared authoring diagnostics. Keep values culture-independent and never modify inputs.
    public static class PlanetParameterValidation
    {
        public static string Number(double value) => value.ToString("G9", CultureInfo.InvariantCulture);
        public static void Range(List<string> errors, string label, double value, double minimum, double maximum)
        {
            if (!math.isfinite(value) || value < minimum || value > maximum)
                errors.Add(label + ": " + Number(value) + ". Allowed range: " + Number(minimum) + "–" + Number(maximum) + ".");
        }
        public static void NonNegative(List<string> errors, string label, double value)
        {
            if (!math.isfinite(value) || value < 0)
                errors.Add(label + ": " + Number(value) + ". Must be finite and at least 0.");
        }
        public static void Positive(List<string> errors, string label, double value, double minimum = 0)
        {
            if (!math.isfinite(value) || value <= minimum)
                errors.Add(label + ": " + Number(value) + ". Must be finite and greater than " + Number(minimum) + ".");
        }
    }
}
