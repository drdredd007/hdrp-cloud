#ifndef SPACERUNNER_SCATTER_INSTANCING_DATA
#define SPACERUNNER_SCATTER_INSTANCING_DATA
// The matrices are absolute floating-world transforms. HDRP performs its own
// camera-relative translation exactly once through ShaderVariables.hlsl.
struct PlanetScatterPoseGpu
{
    float4x4 objectToWorld,worldToObject,previousObjectToWorld,previousWorldToObject;
    uint4 control;
};
#if defined(PROCEDURAL_INSTANCING_ON)
#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
#include "UnityIndirect.cginc"
StructuredBuffer<PlanetScatterPoseGpu> _PlanetScatterPoses;
StructuredBuffer<uint> _PlanetScatterVisible;
static float4x4 _PlanetScatterObjectToWorld,_PlanetScatterWorldToObject;
static float4x4 _PlanetScatterPreviousObjectToWorld,_PlanetScatterPreviousWorldToObject;
#endif
#endif
