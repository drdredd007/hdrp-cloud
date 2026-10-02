using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public static class CubeSurface
    {
        /// <summary>Robust normalisation without overflowing at huge finite inputs.</summary>
        public static bool TryNormalize(double3 input, out double3 direction)
        {
            direction = default;
            if (!math.all(math.isfinite(input))) return false;
            double scale = math.cmax(math.abs(input)); if (!(scale > 0)) return false;
            var scaled = input / scale; direction = scaled / math.sqrt(math.lengthsq(scaled)); return true;
        }

        public static bool TryDirection(SurfaceTileKey key, double2 uv, out double3 direction)
        {
            direction = default;
            if (!key.IsValid || !math.all(math.isfinite(uv)) || math.any(uv < 0) || math.any(uv > 1)) return false;
            double count = 1L << key.Level;
            return TryNormalize(Cube(key.Face, 2 * (key.X + uv.x) / count - 1, 2 * (key.Y + uv.y) / count - 1), out direction);
        }

        /// <summary>Integer arithmetic establishes the shared grid coordinate before conversion to double.</summary>
        public static bool TrySampleDirection(SurfaceTileKey key, int resolution, int x, int y, out double3 direction)
        {
            direction = default;
            if (!key.IsValid || !ValidResolution(resolution) || x < 0 || y < 0 || x > resolution || y > resolution) return false;
            long count = (1L << key.Level) * resolution;
            long a = 2 * ((long)key.X * resolution + x) - count;
            long b = 2 * ((long)key.Y * resolution + y) - count;
            return TryNormalize(Cube(key.Face, (double)a / count, (double)b / count), out direction);
        }

        public static bool TryLocate(double3 input, int level, out SurfaceTileKey key, out double2 uv)
        {
            key = default; uv = default;
            if (level < 0 || level > SurfaceTileKey.MaximumLevel || !TryNormalize(input, out var direction)) return false;
            var abs = math.abs(direction); int face; double a, b;
            if (abs.x >= abs.y && abs.x >= abs.z)
            {
                var cube = direction / abs.x;
                if (direction.x > 0) { face = 0; a = -cube.z; b = cube.y; }
                else { face = 1; a = cube.z; b = cube.y; }
            }
            else if (abs.y >= abs.z)
            {
                var cube = direction / abs.y;
                if (direction.y > 0) { face = 2; a = cube.x; b = -cube.z; }
                else { face = 3; a = cube.x; b = cube.z; }
            }
            else
            {
                var cube = direction / abs.z;
                if (direction.z > 0) { face = 4; a = cube.x; b = cube.y; }
                else { face = 5; a = -cube.x; b = cube.y; }
            }
            double count = 1L << level;
            var grid = math.clamp((new double2(a, b) + 1) * .5, 0, 1) * count;
            int x = (int)math.min(count - 1, math.floor(grid.x)), y = (int)math.min(count - 1, math.floor(grid.y));
            key = new SurfaceTileKey(face, level, x, y); uv = math.clamp(grid - new double2(x, y), 0, 1); return true;
        }

        public static bool ValidResolution(int resolution) => resolution >= 1 && resolution <= 4096 && (resolution & (resolution - 1)) == 0;
        static double3 Cube(int face, double a, double b)
        {
            switch (face)
            {
                case 0: return new double3(1, b, -a);
                case 1: return new double3(-1, b, a);
                case 2: return new double3(a, 1, -b);
                case 3: return new double3(a, -1, b);
                case 4: return new double3(a, b, 1);
                default: return new double3(-a, b, -1);
            }
        }
    }
}
