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
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/AtmosphericScattering/AtmosphericScattering.hlsl"
            TEXTURE2D(_PlanetFarBuffer);
            TEXTURE2D(_PlanetNearBuffer);
            float _PlanetHasNear;
            float _PlanetDebugView;
            // 1 when HDRP's resolved PhysicallyBasedSky describes this planet (see PlanetAtmosphere.Matches).
            float _PlanetAtmosphere;
            // Points this close to the sea-level sphere are shaded as "ground" by the sky tables,
            // which avoids the numerically unstable segment subtraction right at the horizon.
            #define PLANET_SEA_LEVEL_BAND 50.0

            // Aerial perspective from the camera to a planet surface point at 'distance' metres,
            // using the same evaluation as HDRP's sky pass so the limb and terminator agree with it.
            float3 ApplyAtmosphere(float3 color,float2 positionCS,float distance)
            {
                PositionInputs p=GetPositionInput(positionCS,_ScreenSize.zw,0.5,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
                float3 V=GetWorldSpaceNormalizeViewDir(p.positionWS); // towards the camera
                float3 camera=_WorldSpaceCameraPos.xyz;
                float3 O=camera-_PlanetCenterPosition.xyz;
                float r=length(O);
                float2 sea=IntersectSphere(_PlanetaryRadius,dot(O,-V)/max(r,1),r);
                float height=length(O-V*distance)-_PlanetaryRadius;
                bool ground=sea.x>=0 && height<PLANET_SEA_LEVEL_BAND;
                // Skirts and patch chords can dip below sea level, where the sky tables are not valid.
                if(ground)distance=min(distance,sea.x);
                float3 skyColor,skyOpacity;
                EvaluatePbrAtmosphere(camera,V,ground?-distance:distance,false,skyColor,skyOpacity);
                return color*(1-skyOpacity)+skyColor*_IntensityMultiplier*GetCurrentExposureMultiplier();
            }

            float4 Composite(Varyings input):SV_Target
            {
                float4 far=LOAD_TEXTURE2D(_PlanetFarBuffer,uint2(input.positionCS.xy));
                if(_PlanetHasNear>0)
                {
                    float4 local=LOAD_TEXTURE2D(_PlanetNearBuffer,uint2(input.positionCS.xy));
                    // Local terrain replaces its coarse approximation wherever it covers the pixel.
                    if(local.a>0){far=local;if(_PlanetDebugView>0)far.rgb*=float3(1,.25,.25);}
                }
                if(far.a<=0)return _PlanetDebugView>0?float4(0,1,0,1):0;
                float depth=LoadCameraDepth(input.positionCS.xy);
                if(depth!=UNITY_RAW_FAR_CLIP_VALUE)
                {
                    PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,depth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
                    float nearDistance=length(p.positionWS-GetCameraRelativePositionWS(_WorldSpaceCameraPos));
                    if(nearDistance<far.a)return 0;
                }
                float3 color=far.rgb;
                if(_PlanetAtmosphere>0 && _PlanetDebugView<=0)color=ApplyAtmosphere(color,input.positionCS.xy,far.a);
                return float4(color,1);
            }
            ENDHLSL
        }
    }
}
