// Port of js/planet-code.js from World Orogen, upstream cc2662b4edd52231c4f65d8765f3ef12cd82d9b7.
// GPL-3.0-only; see Runtime/PlanetRendering/WorldOrogen/LICENSE.txt.
using System;
using System.Numerics;
using System.Text;

namespace SpaceRunner.PlanetTerrain
{
    public static class WorldOrogenPlanetCode
    {
        static readonly double[] Minimum = { 5000, 0, 4, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, -15, -1, 0 };
        static readonly double[] Step = { 1000, .05, 1, 1, .01, .05, .05, .05, .05, .05, .05, .05, .05, 1, .1, .01 };
        static readonly int[] Count = { 2556, 21, 117, 10, 51, 21, 21, 21, 21, 21, 21, 21, 21, 31, 21, 101 };
        static readonly int[] CurrentFields = { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

        public static string Encode(WorldOrogenSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!settings.Validate(out var error)) throw new ArgumentException(error);
            BigInteger packed = settings.Seed;
            for (int position = CurrentFields.Length - 1; position >= 0; position--)
            {
                int field = CurrentFields[position];
                int index = (int)Math.Floor((Get(settings, field) - Minimum[field]) / Step[field] + .5);
                if (index < 0 || index >= Count[field]) throw new ArgumentException("Planet-code field exceeds its upstream radix.");
                packed = packed * Count[field] + index;
            }
            string code = Base36(packed).PadLeft(22, '0');
            var toggles = settings.ToggledPlateIndices;
            if (toggles != null && toggles.Length != 0)
            {
                var text = new StringBuilder(code).Append('-');
                foreach (int index in toggles) text.Append(Base36(index).PadLeft(2, '0'));
                code = text.ToString();
            }
            return code;
        }

        public static bool TryDecode(string code, out WorldOrogenSettings settings, out string error)
        {
            settings = null; error = "Invalid World Orogen planet code.";
            if (code == null || code.Length > 4096) return false;
            code = code.Trim().ToLowerInvariant(); int dash = code.IndexOf('-');
            string basis = dash < 0 ? code : code.Substring(0, dash), suffix = dash < 0 ? "" : code.Substring(dash + 1);
            int[] fields = Format(basis.Length);
            if (fields == null || suffix.Length % 2 != 0 || !TryBase36(basis, out var packed) ||
                suffix.Length != 0 && !TryBase36(suffix, out _)) return false;
            var candidate = new WorldOrogenSettings(); ApplyHistoricalDefaults(candidate, basis.Length);
            for (int position = 0; position < fields.Length; position++)
            {
                int field = fields[position];
                int radix = field == 0 && basis.Length <= 17 ? 2559 : Count[field];
                int index = (int)(packed % radix); packed /= radix;
                if (index >= Count[field]) return false;
                int decimals = Step[field] == .01 ? 2 : Step[field] < 1 ? (Step[field] == .05 ? 2 : 1) : 0;
                double value = Math.Round(Minimum[field] + index * Step[field], decimals, MidpointRounding.AwayFromZero);
                Set(candidate, field, value);
            }
            if (packed < 0 || packed >= 16777216) return false;
            candidate.Seed = (int)packed;
            candidate.ToggledPlateIndices = new int[suffix.Length / 2];
            for (int i = 0; i < candidate.ToggledPlateIndices.Length; i++)
            {
                if (!TryBase36(suffix.Substring(i * 2, 2), out var index) || index >= candidate.Plates) return false;
                candidate.ToggledPlateIndices[i] = (int)index;
            }
            if (!candidate.Validate(out error)) return false;
            settings = candidate; error = null; return true;
        }
        static int[] Format(int length)
        {
            switch (length)
            {
                case 13: return new[] { 7, 5, 4, 3, 2, 1, 0 };
                case 14: return new[] { 8, 7, 5, 4, 3, 2, 1, 0 };
                case 16: return new[] { 10, 9, 8, 7, 5, 4, 3, 2, 1, 0 };
                case 17: return new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
                case 18: return new[] { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
                case 21: return new[] { 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
                case 22: return CurrentFields;
                default: return null;
            }
        }
        static void ApplyHistoricalDefaults(WorldOrogenSettings settings, int length)
        {
            if (length == 22) return;
            settings.LandCoverage = .3;
            if (length == 21) return;
            settings.ContinentSizeVariety = 0; settings.TemperatureOffset = 0; settings.PrecipitationOffset = 0;
            if (length == 18) return;
            settings.TerrainWarp = .5;
            if (length == 17) return;
            settings.GlacialErosion = 0;
            if (length == 16) return;
            settings.RidgeSharpening = .35; settings.CodeSoilCreep = .05;
            if (length == 14) return;
            settings.ThermalErosion = .1;
        }
        static bool TryBase36(string text, out BigInteger value)
        {
            value = BigInteger.Zero; if (text.Length == 0) return false;
            foreach (char c in text)
            {
                int digit = Digits.IndexOf(c); if (digit < 0) return false;
                value = value * 36 + digit;
            }
            return true;
        }
        static string Base36(BigInteger value)
        {
            if (value == 0) return "0";
            var result = new StringBuilder();
            while (value > 0) { var digit = (int)(value % 36); result.Insert(0, Digits[digit]); value /= 36; }
            return result.ToString();
        }
        static double Get(WorldOrogenSettings s, int field)
        {
            switch (field)
            {
                case 0: return s.Detail; case 1: return s.Irregularity; case 2: return s.Plates; case 3: return s.Continents;
                case 4: return s.Roughness; case 5: return s.Smoothing; case 6: return s.GlacialErosion;
                case 7: return s.HydraulicErosion; case 8: return s.ThermalErosion; case 9: return s.RidgeSharpening;
                case 10: return s.CodeSoilCreep; case 11: return s.TerrainWarp; case 12: return s.ContinentSizeVariety;
                case 13: return s.TemperatureOffset; case 14: return s.PrecipitationOffset; case 15: return s.LandCoverage;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        static void Set(WorldOrogenSettings s, int field, double value)
        {
            switch (field)
            {
                case 0: s.Detail = (int)value; break; case 1: s.Irregularity = value; break;
                case 2: s.Plates = (int)value; break; case 3: s.Continents = (int)value; break;
                case 4: s.Roughness = value; break; case 5: s.Smoothing = value; break;
                case 6: s.GlacialErosion = value; break; case 7: s.HydraulicErosion = value; break;
                case 8: s.ThermalErosion = value; break; case 9: s.RidgeSharpening = value; break;
                case 10: s.CodeSoilCreep = value; break; case 11: s.TerrainWarp = value; break;
                case 12: s.ContinentSizeVariety = value; break; case 13: s.TemperatureOffset = value; break;
                case 14: s.PrecipitationOffset = value; break; case 15: s.LandCoverage = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
    }
}
