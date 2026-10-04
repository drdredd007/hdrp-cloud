#ifndef SPACERUNNER_TERRAIN_LIT_DATA_INCLUDED
#define SPACERUNNER_TERRAIN_LIT_DATA_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/BuiltinUtilities.hlsl"
#include "PlanetTerrainMaterialShared.hlsl"
#include "WorldOrogen/WorldOrogenBaseMap.hlsl"
void GetSurfaceAndBuiltinData(FragInputs input,float3 V,inout PositionInputs posInput,out SurfaceData surfaceData,out BuiltinData builtinData)
{
    ZERO_INITIALIZE(SurfaceData,surfaceData);
    float3 geomNormal=normalize(input.tangentToWorld[2]);
    float3 normalPlanet=normalize(mul((float3x3)_PlanetRenderToLocal,geomNormal));
    float3 offset=float3(input.texCoord0.xy,input.texCoord1.x);
    PlanetTerrainMaterialSample material=PlanetTerrainEvaluate(offset,normalPlanet,input.color,1);
    surfaceData.materialFeatures=MATERIALFEATUREFLAGS_LIT_STANDARD;
    surfaceData.baseColor=material.albedo;surfaceData.metallic=material.metallic;
    if(_OrogenBaseColourEnabled>0)
    {surfaceData.baseColor=OrogenBaseColour(normalize(_OrogenBaseAnchor+offset*_OrogenInverseRadius));surfaceData.metallic=0;}
    surfaceData.perceptualSmoothness=material.smoothness;surfaceData.ambientOcclusion=material.ao;
    surfaceData.normalWS=normalize(mul((float3x3)_PlanetLocalToRender,material.normal));
    if(_OrogenBaseColourEnabled>0){surfaceData.normalWS=geomNormal;surfaceData.ambientOcclusion=1;surfaceData.perceptualSmoothness=.4;}
    surfaceData.geomNormalWS=geomNormal;surfaceData.tangentWS=normalize(input.tangentToWorld[0]);
    surfaceData.specularOcclusion=1;surfaceData.thickness=1;surfaceData.ior=1;
    surfaceData.transmittanceColor=1;surfaceData.atDistance=1000000;
    // UV channels hold metric coordinates/erosion rather than baked lightmaps. Native light probes remain enabled.
    InitBuiltinData(posInput,1,surfaceData.normalWS,-geomNormal,0,0,builtinData);
    builtinData.emissiveColor=0;builtinData.depthOffset=0;
    PostInitBuiltinData(V,posInput,surfaceData,builtinData);
}
#endif
