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
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PhysicallyBasedSky/PhysicallyBasedSkyCommon.hlsl"
            // Vertex layout shared with PlanetPatchGenerator.compute (PlanetVertex, 40 bytes).
            struct PlanetVertex {float3 position;float3 normal;float4 color;};
            StructuredBuffer<PlanetVertex> _PlanetVertices;
            int _PlanetBaseVertex;
            float4x4 _FarViewProjection, _PlanetRotation;
            float3 _PatchOffset, _PlanetLightDirection;
            float4 _PlanetLightColor;
            float _PlanetLightLux, _LayerToMeters;
            // Planet centre relative to the camera in metres, world axes.
            float3 _PlanetCenterRelative;
            // 1 when HDRP's resolved PhysicallyBasedSky describes this planet (tables and constants are bound).
            float _PlanetAtmosphere;
            // 1 to light with HDRP directional lights when the camera has any; otherwise the explicit light below.
            float _PlanetUseSceneLights;
            struct PlanetVaryings {float4 position:SV_POSITION;float3 relative:TEXCOORD0;float3 normal:TEXCOORD1;float4 color:COLOR;};
            // Indexed procedural draw: SV_VertexID is the patch-local index from the shared index buffer.
            PlanetVaryings PlanetVert(uint vertexID:SV_VertexID)
            {
                PlanetVertex input=_PlanetVertices[(uint)_PlanetBaseVertex+vertexID];
                PlanetVaryings o;
                o.relative=mul((float3x3)_PlanetRotation,input.position)+_PatchOffset;
                o.position=mul(_FarViewProjection,float4(o.relative,1));
                o.normal=mul((float3x3)_PlanetRotation,input.normal);o.color=input.color;return o;
            }
            float4 PlanetFrag(PlanetVaryings input):SV_Target
            {
                float3 normal=normalize(input.normal);
                float3 brdf=input.color.rgb*INV_PI;
                float3 radiance=0;
                if(_PlanetUseSceneLights>0 && _DirectionalLightCount>0)
                {
                    // Planet-centred position: the terrain point in the sky's own frame.
                    float3 position=input.relative*_LayerToMeters-_PlanetCenterRelative;
                    float radial=length(position);
                    float3 up=position/max(radial,1);
                    for(uint i=0;i<_DirectionalLightCount;i++)
                    {
                        DirectionalLightData light=_DirectionalLightDatas[i];
                        float3 L=-light.forward;
                        float3 irradiance=light.color*light.diffuseDimmer;
                        if(_PlanetAtmosphere>0 && asint(light.distanceFromCamera)>=0)
                        {
                            // Same models as the sky's analytic ground: sun transmittance to the point and
                            // precomputed sky irradiance for a horizontal surface.
                            float r=max(radial,_PlanetaryRadius+1);
                            radiance+=brdf*SampleGroundIrradianceTexture(dot(up,L))*irradiance;
                            irradiance*=EvaluateSunColorAttenuation(dot(up,L),r);
                        }
                        radiance+=brdf*irradiance*saturate(dot(normal,L));
                    }
                }
                else
                {
                    float sun=saturate(dot(normal,normalize(_PlanetLightDirection)));
                    radiance=input.color.rgb*(_PlanetLightLux/PI)*(sun*_PlanetLightColor.rgb+0.001);
                }
                return float4(radiance*GetCurrentExposureMultiplier(),length(input.relative)*_LayerToMeters);
            }
            ENDHLSL
        }
    }
}
