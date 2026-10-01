Shader "Hidden/SpaceRunner/Planet Media Composite"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
        TEXTURE2D(_PlanetMediaScattering);
        TEXTURE2D(_PlanetMediaTransmission);
        float4 Multiply(Varyings input):SV_Target
        { return float4(LOAD_TEXTURE2D(_PlanetMediaTransmission,uint2(input.positionCS.xy)).rgb,1); }
        float4 Add(Varyings input):SV_Target
        { return float4(LOAD_TEXTURE2D(_PlanetMediaScattering,uint2(input.positionCS.xy)).rgb,0); }
        ENDHLSL
        Pass
        {
            ZWrite Off ZTest Always Cull Off ColorMask RGB Blend Zero SrcColor
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Multiply
            ENDHLSL
        }
        Pass
        {
            ZWrite Off ZTest Always Cull Off ColorMask RGB Blend One One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Add
            ENDHLSL
        }
    }
}
