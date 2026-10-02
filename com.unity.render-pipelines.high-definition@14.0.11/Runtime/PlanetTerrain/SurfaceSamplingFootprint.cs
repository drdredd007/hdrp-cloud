using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Explicit render sample support in metres. Full retains the canonical physical field.</summary>
    public readonly struct SurfaceSamplingFootprint
    {
        public readonly double Metres;
        public SurfaceSamplingFootprint(double metres) { Metres = metres; }
        public static SurfaceSamplingFootprint Full => default;
        public bool IsValid => math.isfinite(Metres) && Metres >= 0;

        /// <summary>Band attenuation only; baked heights, authored regions and craters are unaffected.</summary>
        public double DetailWeight(double wavelengthMetres)
        {
            if (!IsValid || !math.isfinite(wavelengthMetres) || wavelengthMetres <= 0) return 0;
            if (Metres == 0) return 1;
            double t = math.clamp((wavelengthMetres / Metres - 2) * .5, 0, 1);
            return t * t * (3 - 2 * t);
        }
    }
}
