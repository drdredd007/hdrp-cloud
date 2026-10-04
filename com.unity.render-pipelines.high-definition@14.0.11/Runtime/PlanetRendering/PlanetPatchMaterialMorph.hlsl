#ifndef SPACERUNNER_PLANET_PATCH_MATERIAL_MORPH_INCLUDED
#define SPACERUNNER_PLANET_PATCH_MATERIAL_MORPH_INCLUDED
// Attribute bands use the identical two-row parent blend and T-junction midpoint as geometry.
struct PlanetSurfaceVertexAttributes {float4 materialWeights;float4 erosionData;uint channels;};
StructuredBuffer<PlanetSurfaceVertexAttributes> _PlanetAttributes,_PlanetParentAttributes;
float _PlanetTerrainPalette;
int _PlanetNativeSurface;
float4 PlanetFetchMaterialMasks(uint id)
{
    PlanetSurfaceVertexAttributes fine=_PlanetAttributes[(uint)_PlanetBaseVertex+id];
    if((fine.channels&1u)==0)return float4(0,0,1,0);
    float weight=PlanetParentBandWeight(id);
    if(weight<=0)return fine.materialWeights;
    PlanetSurfaceVertexAttributes parent=_PlanetParentAttributes[(uint)_PlanetBaseVertex+id];
    if((parent.channels&1u)==0)return float4(0,0,1,0);
    return lerp(fine.materialWeights,parent.materialWeights,weight);
}
float4 PlanetMaterialMasks(uint id)
{
    if(_PlanetNativeSurface==0||_PlanetTerrainPalette<=0)return float4(0,0,1,0);
    uint step=PlanetStitchStep(id);
    if(step==0)return PlanetFetchMaterialMasks(id);
    return (PlanetFetchMaterialMasks(id-step)+PlanetFetchMaterialMasks(id+step))*0.5;
}
#endif
