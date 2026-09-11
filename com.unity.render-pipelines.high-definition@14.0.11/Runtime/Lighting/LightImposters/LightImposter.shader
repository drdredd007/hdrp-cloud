Shader "Hidden/HDRP/LightImposter"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Name "LightImposter"
            Tags { "LightMode" = "ForwardOnly" }

            ZWrite Off
            ZTest LEqual
            Cull Off
            // Additive: order-independent, and lets overlapping imposters accumulate brightness naturally.
            Blend One One
            BlendOp Add

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                // rgb = HDR color, a = fade-in/fade-out amount computed on the CPU (see HDRenderPipeline.LightImposters.cs).
                UNITY_DEFINE_INSTANCED_PROP(float4, _ImposterColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                // The per-instance matrix (built on the CPU) already bakes in the billboard rotation and the
                // distance/intensity-driven world-space size, so a plain object -> clip transform is enough.
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = UNITY_ACCESS_INSTANCED_PROP(Props, _ImposterColor);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Soft circle falling off to nothing at the quad edge. Combined with the CPU-side minimum
                // screen-size clamp, this is what reads as "a circle that fades to a small crisp dot" once
                // the quad itself is clamped to its smallest allowed on-screen size instead of shrinking further.
                float2 centered = input.uv - 0.5;
                float r = length(centered) * 2.0;
                float glow = saturate(1.0 - r);
                glow *= glow;
                return float4(input.color.rgb * (glow * input.color.a), 0.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
