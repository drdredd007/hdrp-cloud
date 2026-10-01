Shader "Hidden/SpaceRunner/Planet Star Sky"
{
 SubShader
 {
  Tags { "RenderPipeline"="HDRenderPipeline" }
  Pass
  {
   ZWrite Off ZTest Always Cull Off
   HLSLPROGRAM
   #pragma target 4.5
   #pragma vertex Vert
   #pragma fragment StarSky
   #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
   #include "../PlanetCelestialLights.hlsl"
   TEXTURECUBE(_UniverseStars);SAMPLER(sampler_UniverseStars);
   float _UniverseStarIntensity;
   float4 _UniverseSkyRotation;
   float4 _UniverseLightRotation;
   int _UniverseUseSceneLights;
   float3 _UniverseSunDirection,_UniverseSunRadiance;
   float _UniverseSunCosRadius;
   float3 RotateUniverseDirection(float3 v)
   {
    float3 t=2*cross(_UniverseSkyRotation.xyz,v);
    return v+_UniverseSkyRotation.w*t+cross(_UniverseSkyRotation.xyz,t);
   }
   float3 SceneSunRadiance(float3 direction)
   {
    float3 radiance=0;
    [loop] for(uint index=0;index<(uint)_PlanetCelestialLightCount;index++)
    {
     PlanetCelestialLightData light=_PlanetCelestialLightDatas[index];
     if(light.Color.w<=0 || light.Dimmers.w<=0)continue;
     float3 sun=light.Direction.xyz;
     sun+=2*cross(_UniverseLightRotation.xyz,cross(_UniverseLightRotation.xyz,sun)+_UniverseLightRotation.w*sun);
     float radius=clamp(light.Dimmers.w,.001,10)*PI/360;
     float cosine=dot(direction,normalize(sun));
     float edge=max(fwidth(cosine),1e-8);
     radiance+=smoothstep(cos(radius)-edge,cos(radius)+edge,cosine)*max(light.Color.rgb,0)*
      max(0,light.Direction.w)/(PI*Sq(sin(radius)));
    }
    return radiance;
   }
   float4 StarSky(Varyings input):SV_Target
   {
    // The background does not replace ordinary opaque geometry. Planet layers are drawn next.
    if(LoadCameraDepth(input.positionCS.xy)!=UNITY_RAW_FAR_CLIP_VALUE)discard;
    PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,0.5,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
    float3 direction=-GetWorldSpaceNormalizeViewDir(p.positionWS);
    float3 cube=RotateUniverseDirection(direction);
    // A star texture has a finite pixel footprint. Its authored mip chain preserves
    // average radiance when the camera rotates, rather than aliasing finest texels.
    float3 radiance=max(SAMPLE_TEXTURECUBE(_UniverseStars,sampler_UniverseStars,cube).rgb,0)*_UniverseStarIntensity;
    // Integrate the finite solar disc instead of depending on one planet's visual PBS sky.
    if(_UniverseUseSceneLights!=0)radiance+=SceneSunRadiance(direction);
    else
    {
     float cosine=dot(direction,normalize(_UniverseSunDirection));
     float edge=max(fwidth(cosine),1e-8);
     radiance+=smoothstep(_UniverseSunCosRadius-edge,_UniverseSunCosRadius+edge,cosine)*_UniverseSunRadiance;
    }
    return float4(min(radiance*GetCurrentExposureMultiplier(),65504),1);
   }
   ENDHLSL
  }
 }
}
