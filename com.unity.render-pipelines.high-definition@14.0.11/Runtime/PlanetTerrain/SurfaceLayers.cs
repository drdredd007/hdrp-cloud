using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Gnomonic region: a metre-space tangent plane, projected onto the planet-local sphere.</summary>
    public readonly struct SurfaceRegionProjection
    {
        public readonly double3 AnchorDirection, Right, Forward;
        public readonly double Radius;
        public readonly double2 MinimumMetres, MaximumMetres;
        public SurfaceRegionProjection(double3 anchorDirection, double3 right, double3 forward, double radius,
            double2 minimumMetres, double2 maximumMetres)
        {
            AnchorDirection = anchorDirection; Right = right; Forward = forward; Radius = radius;
            MinimumMetres = minimumMetres; MaximumMetres = maximumMetres;
        }
        public bool IsValid => math.all(math.isfinite(AnchorDirection)) && math.all(math.isfinite(Right)) &&
            math.all(math.isfinite(Forward)) && math.abs(math.lengthsq(AnchorDirection) - 1) < 1e-10 &&
            math.abs(math.lengthsq(Right) - 1) < 1e-10 && math.abs(math.lengthsq(Forward) - 1) < 1e-10 &&
            math.abs(math.dot(AnchorDirection, Right)) < 1e-10 && math.abs(math.dot(AnchorDirection, Forward)) < 1e-10 &&
            math.abs(math.dot(Right, Forward)) < 1e-10 && math.isfinite(Radius) && Radius > 0 &&
            math.all(math.isfinite(MinimumMetres)) && math.all(math.isfinite(MaximumMetres)) &&
            math.all(MaximumMetres > MinimumMetres) && math.all(math.isfinite(MaximumMetres - MinimumMetres));
        public bool TryProject(double3 direction, out double2 metres)
        {
            metres = default;
            if (!IsValid || !CubeSurface.TryNormalize(direction, out var unit)) return false;
            double cosine = math.dot(unit, AnchorDirection); if (!(cosine > 0)) return false;
            metres = new double2(math.dot(unit, Right), math.dot(unit, Forward)) * (Radius / cosine);
            return math.all(math.isfinite(metres));
        }
        public bool TryDirection(double2 metres, out double3 direction)
        {
            direction = default;
            return IsValid && math.all(math.isfinite(metres)) &&
                CubeSurface.TryNormalize(AnchorDirection + Right * (metres.x / Radius) + Forward * (metres.y / Radius), out direction);
        }
    }

    public readonly struct SurfaceRegionHeader
    {
        public readonly SurfaceRegionProjection Projection;
        public readonly int2 Resolution;
        public readonly int HeightOffset, MaskOffset, Priority, AttributeOffset;
        public readonly SurfaceChannels Channels;
        public readonly double BlendMetres, MinimumHeight, MaximumHeight;
        public readonly SurfaceRegionMode Mode;
        public readonly SurfaceDetailPolicy DetailPolicy;
        public readonly SurfaceContentHash BaseDigest, ContentHash;
        public SurfaceRegionHeader(SurfaceRegionProjection projection, int2 resolution, int heightOffset, int maskOffset,
            int priority, double blendMetres, double min, double max, SurfaceRegionMode mode, SurfaceDetailPolicy detailPolicy,
            SurfaceContentHash baseDigest, SurfaceContentHash contentHash, SurfaceChannels channels = SurfaceChannels.None, int attributeOffset = 0)
        {
            Projection = projection; Resolution = resolution; HeightOffset = heightOffset; MaskOffset = maskOffset;
            Priority = priority; BlendMetres = blendMetres; MinimumHeight = min; MaximumHeight = max; Mode = mode;
            DetailPolicy = detailPolicy; BaseDigest = baseDigest; ContentHash = contentHash;
            Channels = channels; AttributeOffset = attributeOffset;
        }
    }

    /// <summary>Continuous planet-local value noise. Double lattice coordinates retain metric detail at large radii.</summary>
    public readonly struct SurfaceDetailRecipe
    {
        public readonly double WavelengthMetres, AmplitudeMetres;
        public readonly int Seed;
        public SurfaceDetailRecipe(double wavelengthMetres, double amplitudeMetres, int seed)
        { WavelengthMetres = wavelengthMetres; AmplitudeMetres = amplitudeMetres; Seed = seed; }
        public static SurfaceDetailRecipe Disabled => new SurfaceDetailRecipe(1, 0, 0);
        public bool IsValid => math.isfinite(WavelengthMetres) && WavelengthMetres > 0 &&
            math.isfinite(AmplitudeMetres) && AmplitudeMetres >= 0;
        public SurfaceSampleStatus TryHeight(double3 direction, double radius, out double height)
        {
            height = 0;
            if (!IsValid || !math.isfinite(radius) || radius <= 0 || !CubeSurface.TryNormalize(direction, out var unit))
                return SurfaceSampleStatus.InvalidInput;
            if (AmplitudeMetres == 0) return SurfaceSampleStatus.Ready;
            var p = unit * (radius / WavelengthMetres);
            // Beyond 2^52, adjacent lattice integers cannot be represented by double. Reject instead of losing detail silently.
            const double limit = 4503599627370494;
            if (!math.all(math.isfinite(p)) || math.any(math.abs(p) > limit)) return SurfaceSampleStatus.InvalidInput;
            var floor = math.floor(p); var t = p - floor;
            t = t * t * t * (t * (t * 6 - 15) + 10);
            long x = (long)floor.x, y = (long)floor.y, z = (long)floor.z;
            double a = math.lerp(Value(x, y, z, Seed), Value(x + 1, y, z, Seed), t.x);
            double b = math.lerp(Value(x, y + 1, z, Seed), Value(x + 1, y + 1, z, Seed), t.x);
            double c = math.lerp(Value(x, y, z + 1, Seed), Value(x + 1, y, z + 1, Seed), t.x);
            double d = math.lerp(Value(x, y + 1, z + 1, Seed), Value(x + 1, y + 1, z + 1, Seed), t.x);
            height = math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z) * AmplitudeMetres;
            return SurfaceSampleStatus.Ready;
        }
        static double Value(long x, long y, long z, int seed)
        {
            unchecked
            {
                ulong h = Mix((ulong)x ^ 0x9e3779b97f4a7c15UL);
                h = Mix(h ^ (ulong)y); h = Mix(h ^ (ulong)z); h = Mix(h ^ (uint)seed);
                return ((h >> 11) * (1.0 / 9007199254740992.0)) * 2 - 1;
            }
        }
        static ulong Mix(ulong x)
        {
            unchecked { x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL; x ^= x >> 27; x *= 0x94d049bb133111ebUL; return x ^ (x >> 31); }
        }
    }

    /// <summary>Pure signed stamp data/math. Event thresholds, physical publication and instance ownership belong to the host.</summary>
    public readonly struct SurfaceCraterStamp : IEquatable<SurfaceCraterStamp>, IComparable<SurfaceCraterStamp>
    {
        public readonly ulong IdHigh, IdLow;
        public readonly double3 CenterDirection;
        public readonly double RadiusMetres, DepthMetres, RimWidthMetres, RimHeightMetres;
        public SurfaceCraterStamp(ulong idHigh, ulong idLow, double3 centerDirection, double radiusMetres, double depthMetres,
            double rimWidthMetres = 0, double rimHeightMetres = 0)
        {
            IdHigh = idHigh; IdLow = idLow;
            CenterDirection = math.all(math.isfinite(centerDirection)) && math.abs(math.lengthsq(centerDirection) - 1) < 1e-14
                ? centerDirection : CubeSurface.TryNormalize(centerDirection, out var unit) ? unit : default;
            RadiusMetres = radiusMetres; DepthMetres = depthMetres; RimWidthMetres = rimWidthMetres; RimHeightMetres = rimHeightMetres;
        }
        public bool IsValid => (IdHigh | IdLow) != 0 && math.all(math.isfinite(CenterDirection)) &&
            math.abs(math.lengthsq(CenterDirection) - 1) < 1e-10 && math.isfinite(RadiusMetres) && RadiusMetres > 0 &&
            math.isfinite(DepthMetres) && DepthMetres >= 0 && math.isfinite(RimWidthMetres) && RimWidthMetres >= 0 &&
            math.isfinite(RimHeightMetres) && RimHeightMetres >= 0 && (RimHeightMetres == 0 || RimWidthMetres > 0) &&
            math.isfinite(RadiusMetres + RimWidthMetres);
        public SurfaceSampleStatus TryHeight(double3 direction, double planetRadius, out double height)
        {
            height = 0;
            if (!IsValid || !math.isfinite(planetRadius) || planetRadius <= 0 || !CubeSurface.TryNormalize(direction, out var unit))
                return SurfaceSampleStatus.InvalidInput;
            // Chord-to-arc conversion avoids acos(dot) cancellation for metre craters on million-metre planets.
            double distance = 2 * math.asin(math.clamp(math.length(unit - CenterDirection) * .5, 0, 1)) * planetRadius;
            if (distance < RadiusMetres)
            { double t = distance / RadiusMetres; double bowl = 1 - t * t; height = -DepthMetres * bowl * bowl; }
            else if (RimWidthMetres > 0 && distance < RadiusMetres + RimWidthMetres)
            { double t = (distance - RadiusMetres) / RimWidthMetres; height = RimHeightMetres * 16 * t * t * (1 - t) * (1 - t); }
            return SurfaceSampleStatus.Ready;
        }
        public int CompareTo(SurfaceCraterStamp other)
        { int n = IdHigh.CompareTo(other.IdHigh); return n != 0 ? n : IdLow.CompareTo(other.IdLow); }
        public bool Equals(SurfaceCraterStamp other) => CompareTo(other) == 0 && math.all(CenterDirection == other.CenterDirection) &&
            RadiusMetres == other.RadiusMetres && DepthMetres == other.DepthMetres && RimWidthMetres == other.RimWidthMetres && RimHeightMetres == other.RimHeightMetres;
        public override bool Equals(object other) => other is SurfaceCraterStamp stamp && Equals(stamp);
        public override int GetHashCode() { unchecked { return (int)IdHigh * 397 ^ (int)IdLow; } }
    }
}
