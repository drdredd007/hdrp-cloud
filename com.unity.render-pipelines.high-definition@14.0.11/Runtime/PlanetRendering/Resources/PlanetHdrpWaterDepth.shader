Shader "Hidden/SpaceRunner/HDRP Water Metric Depth"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
        TEXTURE2D(_HdrpWaterMetricDepth); TEXTURE2D(_PlanetAccumulatedDepth);
        struct Varyings {float4 positionCS:SV_POSITION;};
        Varyings Vert(uint vertexID:SV_VertexID){Varyings o;o.positionCS=GetFullScreenTriangleVertexPosition(vertexID);return o;}
        float4 Frag(Varyings input):SV_Target
        {
            int2 pixel=(int2)input.positionCS.xy;
            float4 previous=LOAD_TEXTURE2D(_PlanetAccumulatedDepth,pixel);
            float water=LOAD_TEXTURE2D(_HdrpWaterMetricDepth,pixel).x;
            if(water>0&&(previous.a<=0||water<previous.a))previous.a=water;
            return previous;
        }
        float PhysicalDepth(Varyings input):SV_Depth
        {
            float distance=LOAD_TEXTURE2D(_HdrpWaterMetricDepth,(int2)input.positionCS.xy).x;
            if(distance<=0)discard;
            float3 nearPoint=ComputeWorldSpacePosition(input.positionCS.xy*_ScreenSize.zw,.5,UNITY_MATRIX_I_VP);
            float3 ray=normalize(nearPoint-GetCurrentViewPosition());
            float4 projected=TransformWorldToHClip(GetCurrentViewPosition()+ray*distance);
            return max(1e-7,saturate(projected.z/projected.w));
        }
        ENDHLSL
        Pass { ZWrite Off ZTest Always Cull Off HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
        ENDHLSL }
        Pass { ZWrite Off ZTest Always Cull Off HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
        ENDHLSL }
        Pass { ColorMask 0 ZWrite On ZTest Always Cull Off HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment PhysicalDepth
        ENDHLSL }
    }
}
