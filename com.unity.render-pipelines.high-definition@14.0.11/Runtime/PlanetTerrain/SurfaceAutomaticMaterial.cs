using System;
using System.IO;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public enum SurfaceMaterialProvenance { LegacyBaked = 0, Automatic = 1, Authored = 2 }

    /// <summary>Captured material rules. Moisture/erosion are inherited context, not a new hydraulic solve.</summary>
    public readonly struct SurfaceAutomaticMaterialProfile
    {
        public const int CurrentVersion = 1;
        public readonly int Version;
        public readonly double EquatorTemperature, PoleTemperature, LapseRatePerKilometre;
        public readonly double RockSlopeStart, RockSlopeEnd, SnowTemperatureStart, SnowTemperatureEnd, NormalSampleMetres;
        public bool IsValid => Version == CurrentVersion && math.isfinite(EquatorTemperature) && math.isfinite(PoleTemperature) &&
            math.isfinite(LapseRatePerKilometre) && LapseRatePerKilometre >= 0 && math.isfinite(RockSlopeStart) && RockSlopeStart >= 0 &&
            math.isfinite(RockSlopeEnd) && RockSlopeEnd > RockSlopeStart && math.isfinite(SnowTemperatureStart) &&
            math.isfinite(SnowTemperatureEnd) && SnowTemperatureEnd > SnowTemperatureStart && math.isfinite(NormalSampleMetres) && NormalSampleMetres > 0;
        public SurfaceAutomaticMaterialProfile(double equatorTemperature, double poleTemperature, double lapseRatePerKilometre,
            double rockSlopeStart = .15, double rockSlopeEnd = .8, double snowTemperatureStart = -8, double snowTemperatureEnd = 3,
            double normalSampleMetres = 1, int version = CurrentVersion)
        {
            Version = version; EquatorTemperature = equatorTemperature; PoleTemperature = poleTemperature; LapseRatePerKilometre = lapseRatePerKilometre;
            RockSlopeStart = rockSlopeStart; RockSlopeEnd = rockSlopeEnd; SnowTemperatureStart = snowTemperatureStart; SnowTemperatureEnd = snowTemperatureEnd;
            NormalSampleMetres = normalSampleMetres;
            if (!IsValid) throw new ArgumentException("Automatic material rules must have a supported version and finite valid thresholds.");
        }
        public static SurfaceAutomaticMaterialProfile FromBakeSettings(SurfaceBakeSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new SurfaceAutomaticMaterialProfile(settings.EquatorTemperature, settings.PoleTemperature, settings.LapseRatePerKilometre);
        }
        public SurfaceContentHash ContentDigest
        {
            get { if(!IsValid)return default;var captured=this;return SurfaceHashing.Compute(writer=>Write(writer,captured)); }
        }
        public float4 Evaluate(SurfaceRecipe recipe, double3 unitDirection, double height, double slope, double wetness)
        {
            if (!IsValid || !recipe.IsValid || !math.all(math.isfinite(unitDirection)) || !math.isfinite(height) || !math.isfinite(slope) || slope < 0 || !math.isfinite(wetness))
                throw new ArgumentException("Automatic material evaluation requires a valid profile and finite final surface samples.");
            double latitude = math.abs(unitDirection.y), altitude = math.max(0, height - recipe.SeaLevel);
            double temperature = math.lerp(EquatorTemperature, PoleTemperature, math.pow(latitude, .75)) - altitude / 1000 * LapseRatePerKilometre;
            double rock = Smooth(RockSlopeStart, RockSlopeEnd, slope), snow = recipe.Style == SurfaceStyle.Rocky ? 0 : 1 - Smooth(SnowTemperatureStart, SnowTemperatureEnd, temperature);
            wetness = recipe.Style == SurfaceStyle.Rocky ? 0 : math.clamp(wetness, 0, 1);
            double sand = recipe.Style == SurfaceStyle.Rocky ? (1 - rock) * .35 : (1 - wetness) * (1 - snow) * (1 - rock);
            double grass = recipe.Style == SurfaceStyle.Rocky ? 0 : wetness * (1 - snow) * (1 - rock);
            if (recipe.Style == SurfaceStyle.Rocky) rock = 1 - sand;
            double4 weights = new double4(grass, sand, rock, snow); double total = math.csum(weights);
            return total <= 0 ? new float4(0, 0, 1, 0) : (float4)(weights / total);
        }
        static double Smooth(double minimum, double maximum, double value) { double t = math.clamp((value - minimum) / (maximum - minimum), 0, 1); return t * t * (3 - 2 * t); }
        internal static void Write(BinaryWriter writer, SurfaceAutomaticMaterialProfile profile)
        {
            writer.Write(profile.Version); writer.Write(profile.EquatorTemperature); writer.Write(profile.PoleTemperature); writer.Write(profile.LapseRatePerKilometre);
            writer.Write(profile.RockSlopeStart); writer.Write(profile.RockSlopeEnd); writer.Write(profile.SnowTemperatureStart); writer.Write(profile.SnowTemperatureEnd); writer.Write(profile.NormalSampleMetres);
        }
        internal static SurfaceAutomaticMaterialProfile ReadVersioned(BinaryReader reader)
        {
            int version = reader.ReadInt32();
            return new SurfaceAutomaticMaterialProfile(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), version);
        }
    }
}
