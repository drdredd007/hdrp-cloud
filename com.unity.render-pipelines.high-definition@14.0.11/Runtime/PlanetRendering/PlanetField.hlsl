#ifndef SPACERUNNER_PLANET_FIELD_INCLUDED
#define SPACERUNNER_PLANET_FIELD_INCLUDED

// GPU port of PlanetField (ProceduralPlanet.cs), generator version 1. The CPU field already samples
// noise in float, so both sides agree to float rounding. Keep this file and PlanetField in sync;
// PlanetGpuPatchTests compares them on a real device.

// Seed terms are evaluated on the CPU as PlanetField does: float3(Seed%101,Seed%79,Seed%67)*.137f and Seed*.01f.
float3 _PlanetSeedShift;
float _PlanetSeedDryness;
float _PlanetRadius, _PlanetRelief;

struct PlanetVertex
{
    float3 position;
    float3 normal;
    float4 color;
};

// Ashima 3D simplex noise, term for term as Unity.Mathematics noise.snoise(float3) 1.3.2.
float  PlanetMod289(float x)  { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 PlanetMod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 PlanetMod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 PlanetPermute(float4 x) { return PlanetMod289((34.0 * x + 1.0) * x); }
float4 PlanetTaylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float PlanetSimplex(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    const float4 D = float4(0.0, 0.5, 1.0, 2.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - D.yyy;
    i = PlanetMod289(i);
    float4 p = PlanetPermute(PlanetPermute(PlanetPermute(
        i.z + float4(0.0, i1.z, i2.z, 1.0))
        + i.y + float4(0.0, i1.y, i2.y, 1.0))
        + i.x + float4(0.0, i1.x, i2.x, 1.0));
    float n_ = 0.142857142857;
    float3 ns = n_ * D.wyz - D.xzx;
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);
    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = PlanetTaylorInvSqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

// PlanetField.Height: signed height in metres for a planet-local unit direction.
float PlanetHeight(float3 direction)
{
    float3 shift = _PlanetSeedShift;
    float continents = PlanetSimplex(direction * 2.7 + shift);
    float detail = 0.24 * PlanetSimplex(direction * 11.0 + shift) + 0.07 * PlanetSimplex(direction * 39.0 - shift);
    return clamp((continents + detail - 0.08) * _PlanetRelief, -_PlanetRelief, _PlanetRelief);
}

// PlanetField.Surface(d) - d0 * R, without forming the radius-scale position (float cancellation).
float3 PlanetSurfaceFrom(float3 direction, float3 origin)
{
    return _PlanetRadius * (direction - origin) + direction * max(0.0, PlanetHeight(direction));
}

// PlanetField.Normal: central differences with the same 1e-4 angular step.
float3 PlanetNormal(float3 direction)
{
    float3 hint = abs(direction.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 tangent = normalize(cross(hint, direction));
    float3 bitangent = cross(direction, tangent);
    const float stepSize = 0.0001;
    float3 a = PlanetSurfaceFrom(normalize(direction + tangent * stepSize), direction) - PlanetSurfaceFrom(normalize(direction - tangent * stepSize), direction);
    float3 b = PlanetSurfaceFrom(normalize(direction + bitangent * stepSize), direction) - PlanetSurfaceFrom(normalize(direction - bitangent * stepSize), direction);
    return normalize(cross(a, b));
}

// PlanetField.Color.
float4 PlanetColor(float3 direction)
{
    float height = PlanetHeight(direction);
    float polar = saturate((abs(direction.y) - 0.9) * 15.0);
    float3 color;
    if (height <= 0.0)
        color = lerp(float3(0.009, 0.032, 0.075), float3(0.02, 0.12, 0.16), saturate(1.0 + (height / max(1.0, _PlanetRelief)) * 3.0));
    else
    {
        float dryness = saturate(PlanetSimplex(direction * 8.0 + _PlanetSeedDryness) * 0.8 + 0.45);
        color = lerp(float3(0.025, 0.095, 0.035), float3(0.28, 0.19, 0.085), dryness);
        color = lerp(color, float3(0.24, 0.22, 0.19), saturate((height / max(1.0, _PlanetRelief)) * 2.0));
    }
    return float4(lerp(color, float3(0.72, 0.79, 0.82), polar), 1.0);
}

// PlanetField.Direction for a cube-face quadtree address.
float3 PlanetCubeDirection(int face, int level, int keyX, int keyY, float u, float v)
{
    float count = (float)(1 << level);
    float a = 2.0 * (keyX + u) / count - 1.0;
    float b = 2.0 * (keyY + v) / count - 1.0;
    float3 cube;
    if (face == 0) cube = float3(1, b, -a);
    else if (face == 1) cube = float3(-1, b, a);
    else if (face == 2) cube = float3(a, 1, -b);
    else if (face == 3) cube = float3(a, -1, b);
    else if (face == 4) cube = float3(a, b, 1);
    else cube = float3(-a, b, -1);
    return normalize(cube);
}

#endif
