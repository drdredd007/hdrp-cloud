Shader "Hidden/SpaceRunner/Planet Imposter"
{
 SubShader { Tags { "RenderPipeline"="HDRenderPipeline" }
 Pass { ZWrite Off ZTest Always Cull Off Blend One One
 HLSLPROGRAM
 #pragma target 4.5
 #pragma vertex ImposterVert
 #pragma fragment ImposterFrag
 #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
 struct Body {float4 screen;float4 radiance;};StructuredBuffer<Body> _PlanetImposters;
 TEXTURE2D(_PlanetAccumulatedDepth);
 struct Output {float4 position:SV_POSITION;float2 delta:TEXCOORD0;nointerpolation float4 light:TEXCOORD1;nointerpolation float2 shape:TEXCOORD2;};
 Output ImposterVert(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
 {
  const float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,1),float2(-1,-1),float2(1,1),float2(1,-1)};
  Body b=_PlanetImposters[instance];Output o;float2 offset=corners[vertex]*b.screen.z*3;
  float2 uv=b.screen.xy+offset*_ScreenSize.zw;
  o.position=float4(uv*2-1,0,1);
  #if UNITY_UV_STARTS_AT_TOP
  o.position.y=-o.position.y;
  #endif
  o.delta=offset/b.screen.z;o.light=b.radiance;o.shape=b.screen.zw;return o;
 }
 float4 ImposterFrag(Output input):SV_Target
 {
  float previous=LOAD_TEXTURE2D(_PlanetAccumulatedDepth,uint2(input.position.xy)).a;
  if(previous>0 && previous<input.shape.y*(1-1e-5))return 0;
  float depth=LoadCameraDepth(input.position.xy);
  if(depth!=UNITY_RAW_FAR_CLIP_VALUE)
  {
   PositionInputs p=GetPositionInput(input.position.xy,_ScreenSize.zw,depth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
   if(length(p.positionWS-GetCameraRelativePositionWS(_WorldSpaceCameraPos))<input.shape.y)return 0;
  }
  float profile=exp(-0.5*dot(input.delta,input.delta))/(TWO_PI*input.shape.x*input.shape.x);
  return float4(input.light.rgb*profile*GetCurrentExposureMultiplier(),0);
 }
 ENDHLSL
 } }
}
