Shader "Hidden/SpaceRunner/Planet Static Ocean"
{
 SubShader
 {
  Tags { "RenderPipeline"="HDRenderPipeline" }
  Pass
  {
   ZWrite Off ZTest Always Cull Off
   HLSLPROGRAM
   #pragma target 5.0
   #pragma vertex Vert
   #pragma fragment Ocean
   #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
   #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PhysicallyBasedSky/PhysicallyBasedSkyCommon.hlsl"
   #include "../PlanetMediaBody.hlsl"
   #include "../PlanetCelestialLights.hlsl"
   float3 _OceanOriginHigh,_OceanOriginLow;
   float2 _OceanDimensionsHigh,_OceanDimensionsLow;
   float3 _OceanAlbedo,_OceanLightDirection,_OceanLightColor;
   float _OceanSmoothness,_OceanLightLux,_OceanOwnAir;
   int _OceanUseSceneLights;
   float4 _OceanLightRotation,_OceanOwnAirExtinction,_OceanOwnAerosol,_OceanOwnDimensions;
   double OceanDot(double3 a,double3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
   double OceanSqrt(double x)
   {
    if(x<=0)return 0;
    double y=(double)sqrt((float)x);y=.5L*(y+x/y);return .5L*(y+x/y);
   }
   float3 Transmission(float3 position,float3 L)
   {
    if(_OceanOwnAir<=0)return 1;
    PlanetMediaBody body=(PlanetMediaBody)0;body.CenterRadius.w=_OceanOwnDimensions.x;body.Limits.x=_OceanOwnDimensions.y;
    body.AirExtinction=_OceanOwnAirExtinction;body.AirScattering.w=_OceanOwnAerosol.y;body.AerosolExtinction.x=_OceanOwnAerosol.x;
    return MediaAirTransmissionToSun(body,position,L);
   }
   float3 Brdf(float3 N,float3 V,float3 L)
   {
    float3 h=V+L;float nV=max(.001,saturate(dot(N,V))),nL=saturate(dot(N,L));
    float roughness=max(.06,Sq(1-_OceanSmoothness)),a2=roughness*roughness;
    float3 specular=0;
    if(dot(h,h)>1e-8)
    {
     h=normalize(h);float nH=saturate(dot(N,h)),vH=saturate(dot(V,h));
     float d=nH*nH*(a2-1)+1,D=a2/max(PI*d*d,1e-7),k=Sq(roughness+1)*.125;
     float G=(nL/max(nL*(1-k)+k,.001))*(nV/max(nV*(1-k)+k,.001));
     float F=.02+.98*pow(1-vH,5);specular=D*G*F/max(4*nV*nL,.001);
    }
    return _OceanAlbedo*INV_PI+specular;
   }
   float4 Ocean(Varyings input):SV_Target
   {
    PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,.5,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
    double3 ray=(double3)(-GetWorldSpaceNormalizeViewDir(p.positionWS));ray/=OceanSqrt(OceanDot(ray,ray));
    double3 origin=(double3)_OceanOriginHigh+(double3)_OceanOriginLow;
    double radius=(double)_OceanDimensionsHigh.x+(double)_OceanDimensionsLow.x;
    double altitude=(double)_OceanDimensionsHigh.y+(double)_OceanDimensionsLow.y;
    double b=OceanDot(origin,ray),c=altitude*(2.0L*radius+altitude),disc=b*b-c;
    if(altitude<=0||b>=0||disc<0)return 0;
    double distance=c/(-b+OceanSqrt(disc));if(distance<=0)return 0;
    float3 position=(float3)(origin+ray*distance),N=normalize(position),V=(float3)(-ray),radiance=0;
    uint count=_PlanetCelestialLightDataReady!=0?_PlanetCelestialLightCount:_DirectionalLightCount;
    if(_OceanUseSceneLights!=0&&count>0)
    {
     [loop] for(uint i=0;i<count;i++)
     {
      float3 L,diffuse,specular;
      if(_PlanetCelestialLightDataReady!=0)
      {PlanetCelestialLightData raw=_PlanetCelestialLightDatas[i];L=raw.Direction.xyz;diffuse=raw.Color.rgb*raw.Dimmers.x;specular=raw.Color.rgb*raw.Dimmers.y;}
      else
      {DirectionalLightData light=_DirectionalLightDatas[i];L=-light.forward;diffuse=light.color*light.diffuseDimmer;specular=light.color*light.specularDimmer;}
      L+=2*cross(_OceanLightRotation.xyz,cross(_OceanLightRotation.xyz,L)+_OceanLightRotation.w*L);
      float3 transmission=Transmission(position,L);float3 brdf=Brdf(N,V,L);
      radiance+=(_OceanAlbedo*INV_PI*diffuse+(brdf-_OceanAlbedo*INV_PI)*specular)*transmission*saturate(dot(N,L));
     }
    }
    else
    {float3 L=normalize(_OceanLightDirection);radiance=Brdf(N,V,L)*_OceanLightColor*_OceanLightLux*Transmission(position,L)*saturate(dot(N,L));}
    return float4(radiance*GetCurrentExposureMultiplier(),(float)distance);
   }
   ENDHLSL
  }
  Pass
  {
   ZWrite Off ZTest Always Cull Off
   HLSLPROGRAM
   #pragma target 4.5
   #pragma vertex Vert
   #pragma fragment MergeDepth
   #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
   TEXTURE2D(_OceanBuffer);TEXTURE2D(_PlanetAccumulatedDepth);
   float4 MergeDepth(Varyings input):SV_Target
   {
    uint2 pixel=uint2(input.positionCS.xy);float distance=LOAD_TEXTURE2D(_OceanBuffer,pixel).a;
    float previous=LOAD_TEXTURE2D(_PlanetAccumulatedDepth,pixel).a;
    if(previous>0&&(distance<=0||previous<distance))distance=previous;
    return float4(0,0,0,distance);
   }
   ENDHLSL
  }
  Pass
  {
   ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha
   HLSLPROGRAM
   #pragma target 4.5
   #pragma vertex Vert
   #pragma fragment Composite
   #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
   TEXTURE2D(_OceanBuffer);TEXTURE2D(_PlanetAccumulatedDepth);float _OceanLayerWeight;
   float4 Composite(Varyings input):SV_Target
   {
    uint2 pixel=uint2(input.positionCS.xy);float4 ocean=LOAD_TEXTURE2D(_OceanBuffer,pixel);if(ocean.a<=0)return 0;
    float previous=LOAD_TEXTURE2D(_PlanetAccumulatedDepth,pixel).a;if(previous>0&&previous<ocean.a)return 0;
    // Native ownership suppresses coarse terrain, not sea: underwater native
    // ground must be covered, while foreground props and dry land still win.
    float depth=LoadCameraDepth(input.positionCS.xy);
    if(depth!=UNITY_RAW_FAR_CLIP_VALUE)
    {
     PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,depth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
     if(length(p.positionWS-GetCameraRelativePositionWS(_WorldSpaceCameraPos))<ocean.a)return 0;
    }
    return float4(ocean.rgb,_OceanLayerWeight);
   }
   ENDHLSL
  }
 }
}
