#ifndef SPACERUNNER_TERRAIN_LIT_DATA_INCLUDED
#define SPACERUNNER_TERRAIN_LIT_DATA_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/BuiltinUtilities.hlsl"
#define SPACERUNNER_PREPARED_PLANET_NORMALS
#include "PlanetTerrainMaterialShared.hlsl"
#include "WorldOrogen/WorldOrogenBaseMap.hlsl"
#include "PlanetGlobalColor.hlsl"
#include "PlanetPreparedColour.hlsl"
void GetSurfaceAndBuiltinData(FragInputs input,float3 V,inout PositionInputs posInput,out SurfaceData surfaceData,out BuiltinData builtinData)
{
    ZERO_INITIALIZE(SurfaceData,surfaceData);
    float3 geomNormal=normalize(input.tangentToWorld[2]);
    float3 normalPlanet=normalize(mul((float3x3)_PlanetRenderToLocal,geomNormal));
    float3 offset=float3(input.texCoord0.xy,input.texCoord1.x);
    float geologicalVariance;float3 geological=PlanetGeologicalNormal(offset,normalPlanet,geologicalVariance);
    bool orogenColour=_OrogenBaseColourEnabled>0 && _PlanetMaterialMaskEnabled<.5;
    // Orogen replaces layered PBR and normals. Its optional regional luminance
    // still needs the original layered albedo; every other layered read can be
    // skipped, including normal textures in that luminance-only case.
    bool layeredLuminance=_PlanetRegionalColorCount>0&&_PlanetRegionalColorControls.z!=0;
    PlanetTerrainMaterialSample material=(PlanetTerrainMaterialSample)0;
    if(!orogenColour||layeredLuminance)
        material=PlanetTerrainEvaluateFromGeological(offset,geological,geologicalVariance,input.color,orogenColour?0:1);
    surfaceData.materialFeatures=MATERIALFEATUREFLAGS_LIT_STANDARD;
    surfaceData.baseColor=material.albedo;surfaceData.metallic=material.metallic;
    if(orogenColour)
    {surfaceData.baseColor=OrogenBaseColour(normalize(_OrogenBaseAnchor+offset*_OrogenInverseRadius));surfaceData.metallic=0;}
    if(_PlanetMaterialMaskEnabled<.5 && _PlanetGlobalColorBlend>0)surfaceData.baseColor=PlanetApplyGlobalColorLocal(surfaceData.baseColor,
        _PlanetGlobalColorAnchor+offset*_PlanetGlobalColorInverseRadius);
    float3 regionalTone=1;
    if(_PlanetRegionalColorCount>0&&_PlanetRegionalColorControls.y>0&&_PlanetGlobalColorBlend>0)
        regionalTone=PlanetGlobalColorRelativeTint(_PlanetGlobalColorAnchor+offset*_PlanetGlobalColorInverseRadius);
    if(_PlanetMaterialMaskEnabled<.5)surfaceData.baseColor=PlanetRegionalColorApply(surfaceData.baseColor,offset,regionalTone,dot(material.albedo,float3(.2126,.7152,.0722)));
    if(_PlanetMaterialMaskEnabled<.5)surfaceData.baseColor=PlanetPreparedColour(offset,surfaceData.baseColor);
    if(orogenColour)
    {
        surfaceData.normalWS=normalize(mul((float3x3)_PlanetLocalToRender,geological));
        surfaceData.ambientOcclusion=1;surfaceData.perceptualSmoothness=PlanetGeologicalSmoothness(.4,geologicalVariance);
    }
    else
    {
        surfaceData.perceptualSmoothness=material.smoothness;surfaceData.ambientOcclusion=material.ao;
        surfaceData.normalWS=normalize(mul((float3x3)_PlanetLocalToRender,material.normal));
    }
    surfaceData.geomNormalWS=geomNormal;surfaceData.tangentWS=normalize(input.tangentToWorld[0]);
    surfaceData.specularOcclusion=1;surfaceData.thickness=1;surfaceData.ior=1;
    surfaceData.transmittanceColor=1;surfaceData.atDistance=1000000;
    // UV channels hold metric coordinates/erosion rather than baked lightmaps. Native light probes remain enabled.
    InitBuiltinData(posInput,1,surfaceData.normalWS,-geomNormal,0,0,builtinData);
    builtinData.emissiveColor=0;builtinData.depthOffset=0;
    if(PlanetPreparedColourIsUnlit()){builtinData.emissiveColor=surfaceData.baseColor/max(GetCurrentExposureMultiplier(),1e-20);surfaceData.baseColor=0;surfaceData.metallic=1;surfaceData.ambientOcclusion=1;}
    PostInitBuiltinData(V,posInput,surfaceData,builtinData);
}
#endif
