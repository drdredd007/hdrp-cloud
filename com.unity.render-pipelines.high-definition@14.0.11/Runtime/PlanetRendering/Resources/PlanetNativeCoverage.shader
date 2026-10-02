Shader "Hidden/SpaceRunner/Planet Native Coverage"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            // Select the nearest published native triangle independently of opaque props.
            // Backface/clip rules match the native terrain receiver passes.
            Cull Back ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            struct Attributes {float3 positionOS:POSITION;};
            struct Varyings {float4 positionCS:SV_Position;float3 relative:TEXCOORD0;};
            Varyings Vert(Attributes input)
            {
                Varyings output;float3 positionRWS=TransformObjectToWorld(input.positionOS);
                output.positionCS=TransformWorldToHClip(positionRWS);
                output.relative=positionRWS-GetCameraRelativePositionWS(_WorldSpaceCameraPos);return output;
            }
            float Frag(Varyings input):SV_Target{return length(input.relative);}
            ENDHLSL
        }
    }
}
