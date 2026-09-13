Shader "SpaceRunner/Planet Far Surface"
{
    Properties { _FarZTest("Depth test", Int) = 4 _LayerToMeters("Layer distance scale", Float) = 1000 }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            Cull Back ZWrite On ZTest [_FarZTest]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex PlanetVert
            #pragma fragment PlanetFrag
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            float4x4 _FarViewProjection, _PlanetRotation;
            float3 _PatchOffset, _PlanetLightDirection;
            float4 _PlanetLightColor;
            float _PlanetLightLux, _LayerToMeters;
            struct PlanetVertex {float3 position:POSITION;float3 normal:NORMAL;float4 color:COLOR;};
            struct PlanetVaryings {float4 position:SV_POSITION;float3 relative:TEXCOORD0;float3 normal:TEXCOORD1;float4 color:COLOR;};
            PlanetVaryings PlanetVert(PlanetVertex input)
            {
                PlanetVaryings o;
                o.relative=mul((float3x3)_PlanetRotation,input.position)+_PatchOffset;
                o.position=mul(_FarViewProjection,float4(o.relative,1));
                o.normal=mul((float3x3)_PlanetRotation,input.normal);o.color=input.color;return o;
            }
            float4 PlanetFrag(PlanetVaryings input):SV_Target
            {
                float sun=saturate(dot(normalize(input.normal),normalize(_PlanetLightDirection)));
                float3 radiance=input.color.rgb*(_PlanetLightLux/PI)*(sun*_PlanetLightColor.rgb+0.001);
                return float4(radiance*GetCurrentExposureMultiplier(),length(input.relative)*_LayerToMeters);
            }
            ENDHLSL
        }
    }
}
