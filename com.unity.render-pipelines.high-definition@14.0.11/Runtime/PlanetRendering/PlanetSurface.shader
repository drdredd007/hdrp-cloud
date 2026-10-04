Shader "SpaceRunner/Planet Far Surface"
{
    Properties { _LayerToMeters("Layer distance scale", Float) = 1000 }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            // Written for Unity conventions; Unity reverses depth comparison on reversed-Z platforms.
            Cull Back ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex PlanetVert
            #pragma fragment PlanetFrag
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PhysicallyBasedSky/PhysicallyBasedSkyCommon.hlsl"
            #include "PlanetMediaBody.hlsl"
            #include "PlanetCelestialLights.hlsl"
            #include "PlanetTerrainMaterialShared.hlsl"
            #include "PlanetTerrainAmbientLighting.hlsl"
            #include "WorldOrogen/WorldOrogenBaseMap.hlsl"
            // Vertex layout shared with PlanetPatchGenerator.compute (PlanetVertex, 40 bytes).
            ByteAddressBuffer _PlanetTriangleIndices;
            float4x4 _FarViewProjection, _PlanetRotation;
            float3 _PatchOffset, _PlanetLightDirection;
            float4 _PlanetLightColor;
            float _PlanetLightLux, _LayerToMeters;
            TEXTURE2D(_PeriodicNormalSlopes);SAMPLER(sampler_PeriodicNormalSlopes);
            float _PeriodicNormalsEnabled,_PeriodicNormalCycles;
            float4x4 _PeriodicWorldToLocal,_PeriodicLocalToWorld;
            #include "PlanetPeriodicSurface.hlsl"
            float _PeriodicTerrainShadowEnabled;
            float3 _PeriodicShadowSunWorld;
            float3 PeriodicPixelNormal(float3 radialWorld)
            {
                float3 d=normalize(mul((float3x3)_PeriodicWorldToLocal,radialWorld));
                float3 p=d*_PeriodicNormalCycles,w=d*d;w*=w;w*=w;w*=w;w/=w.x+w.y+w.z;
                float2 sx=SAMPLE_TEXTURE2D(_PeriodicNormalSlopes,sampler_PeriodicNormalSlopes,p.zy).rg;
                float2 sy=SAMPLE_TEXTURE2D(_PeriodicNormalSlopes,sampler_PeriodicNormalSlopes,p.xz).rg;
                float2 sz=SAMPLE_TEXTURE2D(_PeriodicNormalSlopes,sampler_PeriodicNormalSlopes,p.xy).rg;
                float3 gradient=w.x*float3(0,sx.y,sx.x)+w.y*float3(sy.x,0,sy.y)+w.z*float3(sz.x,sz.y,0);
                gradient-=d*dot(gradient,d);
                return normalize(mul((float3x3)_PeriodicLocalToWorld,normalize(d-gradient)));
            }
            // Planet centre relative to the camera in metres, world axes.
            float3 _PlanetCenterRelative;
            // 1 when HDRP's resolved PhysicallyBasedSky describes this planet (tables and constants are bound).
            float _PlanetAtmosphere;
            // 1 to light with HDRP directional lights when the camera has any; otherwise the explicit light below.
            float _PlanetUseSceneLights;
            float4 _PlanetLightRotation;
            float _PlanetOwnAir;
            float4 _PlanetOwnAirExtinction,_PlanetOwnAerosol,_PlanetOwnDimensions;
            float3 PlanetOwnSunTransmission(float3 p,float3 L)
            {
                if(_PlanetOwnAir<=0)return 1;
                PlanetMediaBody b=(PlanetMediaBody)0;
                b.CenterRadius.w=_PlanetOwnDimensions.x;b.Limits.x=_PlanetOwnDimensions.y;
                b.AirExtinction=_PlanetOwnAirExtinction;b.AirScattering.w=_PlanetOwnAerosol.y;
                b.AerosolExtinction.x=_PlanetOwnAerosol.x;
                return MediaAirTransmissionToSun(b,p,L);
            }
            float3 _DetailOrigin;
            float4x4 _DetailRotation;
            // Coordinates remain planet-local across camera moves, patch rebases and planet rotation.
            float DetailHash(float3 p)
            {
                p=p-floor(p/256.0)*256.0;
                p=frac(p*0.1031);p+=dot(p,p.yzx+33.33);
                return frac((p.x+p.y)*p.z);
            }
            float DetailNoise(float3 p)
            {
                float3 i=floor(p),f=frac(p);f=f*f*(3.0-2.0*f);
                return lerp(lerp(lerp(DetailHash(i),DetailHash(i+float3(1,0,0)),f.x),
                    lerp(DetailHash(i+float3(0,1,0)),DetailHash(i+float3(1,1,0)),f.x),f.y),
                    lerp(lerp(DetailHash(i+float3(0,0,1)),DetailHash(i+float3(1,0,1)),f.x),
                    lerp(DetailHash(i+float3(0,1,1)),DetailHash(i+float3(1,1,1)),f.x),f.y),f.z);
            }
            float FilteredDetail(float3 p)
            {
                float footprint=max(length(ddx(p)),length(ddy(p)));
                return lerp(DetailNoise(p),0.5,smoothstep(0.3,1.0,footprint));
            }
            // Diagnostics (PlanetFarPass.DebugView): 1 tints skirt vertices (index >= _PlanetMainVertexCount).
            float _PlanetDebugView;
            int _PlanetMainVertexCount;
            // Far layout only: edges (bit 0 v=0, 1 u=1, 2 v=1, 3 u=0) adjacent to a one-level-coarser patch.
            struct PlanetVaryings {float4 position:SV_POSITION;float3 relative:TEXCOORD0;float3 normal:TEXCOORD1;float4 color:COLOR;float skirt:TEXCOORD2;float3 detail:TEXCOORD3;float4 masks:TEXCOORD4;};
            #include "PlanetPatchMorph.hlsl"
            #include "PlanetPatchMaterialMorph.hlsl"
            // Cheaper distant PBR: the same canonical albedo/metal/roughness with one direct GGX lobe.
            // Native near meshes retain HDRP's complete LightLoop, shadow maps and indirect lighting.
            float3 PlanetFarSpecular(float3 normal,float3 view,float3 light,float3 albedo,float metallic,float smoothness)
            {
                float3 halfVector=view+light;float halfLength=dot(halfVector,halfVector);if(halfLength<1e-8)return 0;
                halfVector*=rsqrt(halfLength);float nL=saturate(dot(normal,light)),nV=max(.001,saturate(dot(normal,view)));
                float nH=saturate(dot(normal,halfVector)),vH=saturate(dot(view,halfVector));
                float roughness=max(.04,(1-smoothness)*(1-smoothness)),a2=roughness*roughness;
                float denominator=nH*nH*(a2-1)+1;float distribution=a2/max(PI*denominator*denominator,1e-7);
                float k=(roughness+1)*(roughness+1)*.125;
                float visibility=(nL/max(nL*(1-k)+k,.001))*(nV/max(nV*(1-k)+k,.001));
                float3 f0=lerp(.04,albedo,metallic),fresnel=f0+(1-f0)*pow(1-vH,5);
                return distribution*visibility*fresnel/max(4*nV*nL,.001);
            }
            // Indexed procedural draw: SV_VertexID is the patch-local index from the shared index buffer.
            PlanetVaryings PlanetVert(uint vertexID:SV_VertexID)
            {
                PlanetVertex input=PlanetStitchedVertex(vertexID);
                PlanetVaryings o;
                o.relative=mul((float3x3)_PlanetRotation,input.position)+_PatchOffset;
                o.detail=mul((float3x3)_DetailRotation,input.position*_LayerToMeters)+_DetailOrigin;
                o.position=mul(_FarViewProjection,float4(o.relative,1));
                o.normal=mul((float3x3)_PlanetRotation,input.normal);o.color=input.color;o.skirt=vertexID>=(uint)_PlanetMainVertexCount?1:0;
                o.masks=PlanetMaterialMasks(vertexID);return o;
            }
            struct PlanetFragment {float4 color:SV_Target0;float4 solar:SV_Target1;};
            PlanetFragment PlanetFrag(PlanetVaryings input,uint primitiveID:SV_PrimitiveID)
            {
                // Read the whole indexed primitive: interpolated alpha could admit fragments
                // of a triangle whose missing vertex was collapsed to a harmless finite position.
                if(_PlanetNativeSurface!=0)
                {
                    uint3 ids=_PlanetTriangleIndices.Load3(primitiveID*12u);
                    float ready=min(PlanetStitchedVertex(ids.x).color.a,min(PlanetStitchedVertex(ids.y).color.a,PlanetStitchedVertex(ids.z).color.a));
                    clip(ready-1.0);
                }
                float3 normal=normalize(input.normal);
                float3 radialUp=normalize(input.relative*_LayerToMeters-_PlanetCenterRelative);
                if(_PeriodicNormalsEnabled>0)normal=PeriodicPixelNormal(radialUp);
                float slope=1.0-saturate(dot(normal,radialUp));
                float broad=FilteredDetail(input.detail/16.0);
                float fine=FilteredDetail(input.detail);
                float3 albedo=input.color.rgb*(0.78+0.30*broad+0.14*fine);
                float metallic=0,smoothness=.4;
                // Exposed slopes are slightly lighter rock. Material detail never displaces collision geometry.
                albedo=lerp(albedo,albedo*1.18,smoothstep(0.04,0.35,slope));
                if(_PeriodicSurfaceEnabled>0)PeriodicSnowAndRock(radialUp,input.detail,normal,albedo,smoothness);
                if(_PlanetTerrainPalette>0)
                {
                    float3 normalPlanet=normalize(mul((float3x3)_PlanetRenderToLocal,normal));
                    PlanetTerrainMaterialSample material=PlanetTerrainEvaluate(input.detail,normalPlanet,input.masks,_PlanetMaterialControls.z);
                    albedo=material.albedo*material.ao;
                    metallic=material.metallic;smoothness=material.smoothness;
                    normal=normalize(mul((float3x3)_PlanetLocalToRender,material.normal));
                }
                if(_OrogenBaseColourEnabled>0)
                {albedo=OrogenBaseColour(mul((float3x3)_OrogenRenderToLocal,radialUp));normal=normalize(input.normal);metallic=0;smoothness=.4;}
                if(_PlanetGlobalColorBlend>0)
                {
                    float3 globalColor=PlanetGlobalColor(radialUp);
                    // Macro color remains at the surface; retain local material contrast and detail there.
                    float3 localColor=albedo*globalColor/max(_PlanetGlobalColorReference.rgb,.01);
                    float3 colored=lerp(localColor,globalColor,_PlanetGlobalColorDistanceBlend);
                    albedo=lerp(albedo,saturate(colored),_PlanetGlobalColorBlend);
                }
                float3 brdf=albedo*INV_PI;
                if(_PlanetTerrainPalette>0||_PeriodicSurfaceEnabled>0)brdf*=1-metallic;
                float3 viewDirection=normalize(-input.relative);
                float3 radiance=0,solarRadiance=0;
                uint directionalCount=_PlanetCelestialLightDataReady!=0?_PlanetCelestialLightCount:_DirectionalLightCount;
                if(_PlanetUseSceneLights>0 && directionalCount>0)
                {
                    // Planet-centred position: the terrain point in the sky's own frame.
                    float3 position=input.relative*_LayerToMeters-_PlanetCenterRelative;
                    float radial=length(position);
                    float3 up=position/max(radial,1);
                    for(uint i=0;i<directionalCount;i++)
                    {
                        DirectionalLightData light=_DirectionalLightDatas[i];
                        float3 L=-light.forward;
                        L+=2*cross(_PlanetLightRotation.xyz,cross(_PlanetLightRotation.xyz,L)+_PlanetLightRotation.w*L);
                        float3 irradiance=light.color*light.diffuseDimmer;
                        float3 specularIrradiance=light.color*light.specularDimmer;
                        if(_PlanetCelestialLightDataReady!=0)
                        {
                            PlanetCelestialLightData celestial=_PlanetCelestialLightDatas[i];
                            irradiance=celestial.Color.rgb*celestial.Dimmers.x;
                            specularIrradiance=celestial.Color.rgb*celestial.Dimmers.y;
                        }
                        if(_PlanetAtmosphere>0 && asint(light.distanceFromCamera)>=0)
                        {
                            // Same models as the sky's analytic ground: sun transmittance to the point and
                            // precomputed sky irradiance for a horizontal surface.
                            float r=max(radial,_PlanetaryRadius+1);
                            // The nearest native body's camera probe replaces this old
                            // horizontal, per-light approximation of the same sky diffuse.
                            if(_PlanetNativeIndirect<=0)radiance+=brdf*SampleGroundIrradianceTexture(dot(up,L))*irradiance;
                            if(_PlanetOwnAir<=0)irradiance*=EvaluateSunColorAttenuation(dot(up,L),r);
                        }
                        float3 transmission=PlanetOwnSunTransmission(position,L);irradiance*=transmission;
                        float3 direct=brdf*irradiance*saturate(dot(normal,L))*
                            PlanetNativeDirectDiffuseFactor(normal,viewDirection,L,smoothness);
                        if(_PlanetTerrainPalette>0||_PeriodicSurfaceEnabled>0)direct+=PlanetFarSpecular(normal,viewDirection,L,albedo,metallic,smoothness)*
                            specularIrradiance*transmission*saturate(dot(normal,L));
                        if(_PeriodicTerrainShadowEnabled>0&&dot(L,_PeriodicShadowSunWorld)>.99999)solarRadiance+=direct;
                        else radiance+=direct;
                    }
                }
                else
                {
                    float3 L=normalize(_PlanetLightDirection);
                    float sun=saturate(dot(normal,L));
                    float3 p=input.relative*_LayerToMeters-_PlanetCenterRelative;
                    float3 transmission=PlanetOwnSunTransmission(p,L);
                    float3 direct=brdf*PlanetNativeDirectDiffuseFactor(normal,viewDirection,L,smoothness);
                    if(_PlanetTerrainPalette>0||_PeriodicSurfaceEnabled>0)
                        direct+=PlanetFarSpecular(normal,viewDirection,L,albedo,metallic,smoothness);
                    direct*=_PlanetLightLux*sun*_PlanetLightColor.rgb*transmission;
                    radiance=albedo*(_PlanetLightLux/PI)*.001;
                    if(_PeriodicTerrainShadowEnabled>0)solarRadiance=direct;else radiance+=direct;
                }
                // One surface/environment term, independent of directional-light count.
                radiance+=PlanetNativeEnvironmentLighting(albedo,metallic,smoothness,normal,viewDirection,input.relative*_LayerToMeters);
                if(_PlanetDebugView>0 && input.skirt>0){radiance=float3(1,0,1)*_PlanetLightLux;solarRadiance=0;}
                // Store environment/other lights independently. Never subtract rounded HDR sunlight to form a shadow.
                PlanetFragment output;output.color=float4(radiance*GetCurrentExposureMultiplier(),length(input.relative)*_LayerToMeters);
                output.solar=float4(solarRadiance*GetCurrentExposureMultiplier(),0);return output;
            }
            ENDHLSL
        }
    }
}
