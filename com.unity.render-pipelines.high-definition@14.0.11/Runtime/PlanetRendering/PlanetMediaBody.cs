using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // Spatial inputs share the observer's double frame. Only their difference becomes float.
    [Serializable]
    public struct PlanetMediaBody
    {
        public PlanetDefinition Definition;
        public double3 CameraPosition;
        public Quaternion Rotation;
        public PlanetAtmosphereSettings Atmosphere;
        public VolumeProfile WeatherProfile;
        public bool Clouds, Fog;
        public float MinimumCloudAltitude;
        public int Seed;
    }

    // Keep these float4 records in exactly the same order as PlanetMediaBody.hlsl.
    [StructLayout(LayoutKind.Sequential)]
    public struct PlanetMediaGpuBody
    {
        public Vector4 CenterRadius, InverseRotation, Limits;
        public Vector4 AirExtinction, AirScattering, AerosolExtinction, AerosolScattering;
        public Vector4 FogDensity, FogAlbedo, FogColor;
        public Vector4 CloudShape, CloudErosion, CloudScatteringTint, CloudLighting, CloudBottomLighting;
        public Vector4 CloudOffset, Coverage, Climate, SeedWind, CloudWind, CloudMisc, CloudMapTiling;
        public Vector4 TextureMetadata, RegionMetadata;
    }

    public static class PlanetMediaMath
    {
        public const int CurveSamples = 32;
        public static float ScaleHeight(float depth) => Mathf.Max(0.001f, depth * 0.144765f);
        public static float Extinction(float zenithOpacity, float scaleHeight) =>
            -Mathf.Log(1 - Mathf.Clamp(zenithOpacity, 0, 0.999999f)) / Mathf.Max(0.001f, scaleHeight);
        static bool Finite(Vector4 value) => math.all(math.isfinite((float4)value));
        public static bool IsFinite(in PlanetMediaGpuBody b) => Finite(b.CenterRadius) && Finite(b.InverseRotation) && Finite(b.Limits) &&
            Finite(b.AirExtinction) && Finite(b.AirScattering) && Finite(b.AerosolExtinction) && Finite(b.AerosolScattering) &&
            Finite(b.FogDensity) && Finite(b.FogAlbedo) && Finite(b.FogColor) && Finite(b.CloudShape) && Finite(b.CloudErosion) &&
            Finite(b.CloudScatteringTint) && Finite(b.CloudLighting) && Finite(b.CloudBottomLighting) && Finite(b.CloudOffset) &&
            Finite(b.Coverage) && Finite(b.Climate) && Finite(b.SeedWind) && Finite(b.CloudWind) && Finite(b.CloudMisc) &&
            Finite(b.CloudMapTiling) && Finite(b.TextureMetadata) && Finite(b.RegionMetadata);
        public static bool IntersectsFrustum(Plane[] planes, Vector3 centerFromCamera, float outerRadius)
        {
            // Far clipping describes local opaque geometry. Celestial media can be far beyond it.
            for (int i = 0; i < 5; ++i)
                if (planes[i].GetDistanceToPoint(centerFromCamera) < -outerRadius) return false;
            return true;
        }

        // Projection onto the ray avoids subtracting two planet-sized squares. This also
        // handles inside cameras, tangent rays and endpoints before atmospheric entry.
        public static bool TrySphereSegment(double3 centerFromCamera, double radius, double3 direction,
            double endpoint, out double entry, out double exit)
        {
            entry = exit = 0;
            if (!math.all(math.isfinite(centerFromCamera)) || !math.all(math.isfinite(direction)) ||
                !math.isfinite(radius) || radius <= 0 || !math.isfinite(endpoint) || endpoint <= 0) return false;
            double norm = math.length(direction);
            if (!math.isfinite(norm) || norm <= 0) return false;
            direction /= norm;
            double along = math.dot(centerFromCamera, direction);
            double3 perpendicular = centerFromCamera - direction * along;
            double closest = math.length(perpendicular);
            if (!math.isfinite(closest) || closest > radius) return false;
            double half = math.sqrt(math.max(0, (radius - closest) * (radius + closest)));
            entry = math.max(0, along - half);
            exit = math.min(endpoint, along + half);
            return math.isfinite(entry) && math.isfinite(exit) && exit > entry;
        }
    }
}
