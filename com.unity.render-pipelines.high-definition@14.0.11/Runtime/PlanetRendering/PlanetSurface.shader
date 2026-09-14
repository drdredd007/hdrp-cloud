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
            // Diagnostics (PlanetFarPass.DebugView): 1 tints skirt vertices (index >= _PlanetMainVertexCount).
            float _PlanetDebugView;
            int _PlanetMainVertexCount;
            // Far layout only: edges (bit 0 v=0, 1 u=1, 2 v=1, 3 u=0) adjacent to a one-level-coarser patch.
            int _PlanetStitchMask;
            struct PlanetVaryings {float4 position:SV_POSITION;float3 relative:TEXCOORD0;float3 normal:TEXCOORD1;float4 color:COLOR;float skirt:TEXCOORD2;};
            PlanetVertex PlanetFetch(uint id){return _PlanetVertices[(uint)_PlanetBaseVertex+id];}
            // T-junction removal: an odd vertex on a stitched edge (and its skirt vertex) moves to the midpoint of its
            // even neighbours, which coincide with the coarser patch's edge vertices, so both sides share one edge line.
            PlanetVertex PlanetStitchedVertex(uint id)
            {
                PlanetVertex v=PlanetFetch(id);
                uint mask=(uint)_PlanetStitchMask;
                if(mask==0)return v;
                const uint resolution=32,row=33,main=row*row;
                uint step=0;
                if(id<main)
                {
                    uint x=id%row,y=id/row;
                    if(y==0 && (x&1) && (mask&1))step=1;
                    else if(x==resolution && (y&1) && (mask&2))step=row;
                    else if(y==resolution && (x&1) && (mask&4))step=1;
                    else if(x==0 && (y&1) && (mask&8))step=row;
                }
                else if(id<main+4*row)
                {
                    uint local=id-main,edge=local/row,j=local%row;
                    if((j&1) && ((mask>>edge)&1))step=1;
                }
                if(step==0)return v;
                PlanetVertex a=PlanetFetch(id-step),b=PlanetFetch(id+step);
                v.position=(a.position+b.position)*0.5;
                v.normal=normalize(a.normal+b.normal);
                v.color=(a.color+b.color)*0.5;
                return v;
            }
            // Indexed procedural draw: SV_VertexID is the patch-local index from the shared index buffer.
            PlanetVaryings PlanetVert(uint vertexID:SV_VertexID)
            {
                PlanetVertex input=PlanetStitchedVertex(vertexID);
                PlanetVaryings o;
                o.relative=mul((float3x3)_PlanetRotation,input.position)+_PatchOffset;
                o.position=mul(_FarViewProjection,float4(o.relative,1));
                o.normal=mul((float3x3)_PlanetRotation,input.normal);o.color=input.color;o.skirt=vertexID>=(uint)_PlanetMainVertexCount?1:0;return o;
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
                if(_PlanetDebugView>0 && input.skirt>0)radiance=float3(1,0,1)*_PlanetLightLux;
                return float4(radiance*GetCurrentExposureMultiplier(),length(input.relative)*_LayerToMeters);
            }
            ENDHLSL
        }
    }
}
