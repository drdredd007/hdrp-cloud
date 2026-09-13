Shader "SpaceRunner/Planet Composite"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Composite
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            TEXTURE2D(_PlanetFarBuffer);
            float4 Composite(Varyings input):SV_Target
            {
                float4 far=LOAD_TEXTURE2D(_PlanetFarBuffer,uint2(input.positionCS.xy));
                if(far.a<=0)return 0;
                float depth=LoadCameraDepth(input.positionCS.xy);
                if(depth!=UNITY_RAW_FAR_CLIP_VALUE)
                {
                    PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,depth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
                    float nearDistance=length(p.positionWS-GetCameraRelativePositionWS(_WorldSpaceCameraPos));
                    if(nearDistance<far.a)return 0;
                }
                return float4(far.rgb,1);
            }
            ENDHLSL
        }
    }
}
