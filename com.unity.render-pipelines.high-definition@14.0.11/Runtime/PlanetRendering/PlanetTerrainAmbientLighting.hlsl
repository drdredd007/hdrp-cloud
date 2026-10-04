#ifndef SPACERUNNER_TERRAIN_AMBIENT_LIGHTING_INCLUDED
#define SPACERUNNER_TERRAIN_AMBIENT_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/BSDF.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/PreIntegratedFGD/PreIntegratedFGD.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/AtmosphericScattering/AtmosphericScattering.hlsl"

// This camera probe belongs to the nearest native terrain's environment. The body
// binding excludes other planets and remains stable while its native bank prepares.
float _PlanetNativeIndirect;
// Stock Lit's normalized Disney diffuse is inexpensive and removes the direct
// Lambert/Disney brightness step for the same nearest native body's material.
// Other celestial bodies retain their existing distant-light approximation.
float PlanetNativeDirectDiffuseFactor(float3 normal,float3 view,float3 light,float smoothness)
{
    if(_PlanetNativeIndirect<=0)return 1;
    return DisneyDiffuseNoPI(max(saturate(dot(normal,view)),1e-4),saturate(dot(normal,light)),
        dot(light,view),1-smoothness);
}
float3 PlanetNativeEnvironmentLighting(float3 albedo,float metallic,float smoothness,float3 normal,float3 view,float3 positionWS)
{
    if(_PlanetNativeIndirect<=0)return 0;
    // The camera SH is already convolved with the cosine kernel, with Lambert's
    // 1/pi convention. Stock HDRP Lit multiplies it by Disney's diffuse FGD and
    // the diffuse colour; dividing by pi here again would darken the handoff.
    float3 specularFGD;float diffuseFGD,reflectivity;
    float nV=max(saturate(dot(normal,view)),1e-4),perceptualRoughness=1-smoothness;
    float3 fresnel0=lerp(.04,albedo,metallic);
    GetPreIntegratedFGDGGXAndDisneyDiffuse(nV,perceptualRoughness,
        fresnel0,specularFGD,diffuseFGD,reflectivity);
    float3 radiance=SampleSH9(_AmbientProbeData,normal)*diffuseFGD*albedo*(1-metallic)*
        GetIndirectDiffuseMultiplier(0xFFu);
    if(_EnableSkyReflection!=0 && _EnvLightSkyEnabled!=0 && reflectivity>0)
    {
        // Stock Lit's isotropic sky reflection (Lit.hlsl EvaluateBSDF_Env).
        // The camera cubemap is already GGX-filtered and is not pre-exposed.
        float3 reflection=reflect(-view,normal);
        float3 dominant=GetSpecularDominantDir(normal,reflection,perceptualRoughness,nV);
        float roughness=perceptualRoughness*perceptualRoughness;
        float3 direction=lerp(dominant,reflection,saturate(smoothstep(0,1,roughness*roughness)));
        float3 sky=ClampToFloat16Max(SampleSkyTexture(direction,PerceptualRoughnessToMipmapLevel(perceptualRoughness),0).rgb);
        if(_FogEnabled)
        {
            // Material.hlsl EvaluateFogForSkyReflections: transport between this
            // metric camera-relative surface point and sky, independent of view media.
            float3 volumeAlbedo=_HeightFogBaseScattering.xyz/_HeightFogBaseExtinction;
            float opticalDepth=OpticalDepthHeightFog(_HeightFogBaseExtinction,_HeightFogBaseHeight,
                _HeightFogExponents,direction.y,positionWS.y,_MaxFogDistance);
            float transmission=TransmittanceFromOpticalDepth(opticalDepth);
            sky=sky*transmission+GetFogColor(-direction,_MaxFogDistance)*volumeAlbedo*(1-transmission);
        }
        float3 compensation=1+fresnel0*(1/reflectivity-1);
        radiance+=sky*specularFGD*compensation*GetIndirectSpecularMultiplier(0xFFu);
    }
    return radiance;
}
#endif
