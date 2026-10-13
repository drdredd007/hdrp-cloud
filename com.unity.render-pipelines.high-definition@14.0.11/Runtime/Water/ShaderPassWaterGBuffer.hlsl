#if SHADERPASS != SHADERPASS_GBUFFER
#error SHADERPASS_is_not_correctly_define
#endif
// Given that the geometry is either procedural or a mesh given by the user, we only need this as an input
struct VaryingsToPS
{
    VaryingsMeshToPS vmesh;
};

struct PackedVaryingsToPS
{
    PackedVaryingsMeshToPS vmesh;

    // SGVs must be packed after all non-SGVs have been packed.
    // If there are several SGVs, they are packed in the order of HLSL declaration.

    UNITY_VERTEX_OUTPUT_STEREO

#if defined(PLATFORM_SUPPORTS_PRIMITIVE_ID_IN_PIXEL_SHADER) && SHADER_STAGE_FRAGMENT
#if (defined(VARYINGS_NEED_PRIMITIVEID) || (SHADERPASS == SHADERPASS_FULL_SCREEN_DEBUG))
    uint primitiveID : SV_PrimitiveID;
#endif
#endif

#if defined(VARYINGS_NEED_CULLFACE) && SHADER_STAGE_FRAGMENT
    FRONT_FACE_TYPE cullFace : FRONT_FACE_SEMANTIC;
#endif
};

PackedVaryingsToPS PackVaryingsToPS(VaryingsToPS input)
{
    PackedVaryingsToPS output;
    output.vmesh = PackVaryingsMeshToPS(input.vmesh);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    return output;
}

// We only need the tessellation version
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Water/WaterVertexTessellation.hlsl"
#if defined(SHADER_API_D3D11)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceCanonicalHeight.hlsl"
#endif
TEXTURE2D(_PlanetWaterOpaqueEndpoints);
TEXTURE2D(_PlanetWaterOwnCoarseEndpoints);

FragInputs UnpackVaryingsToFragInputs(PackedVaryingsToPS packedInput)
{
    FragInputs input = UnpackVaryingsMeshToFragInputs(packedInput.vmesh);

#if defined(PLATFORM_SUPPORTS_PRIMITIVE_ID_IN_PIXEL_SHADER) && SHADER_STAGE_FRAGMENT
#if (defined(VARYINGS_NEED_PRIMITIVEID) || (SHADERPASS == SHADERPASS_FULL_SCREEN_DEBUG))
    input.primitiveID = packedInput.primitiveID;
#endif
#endif

#if defined(VARYINGS_NEED_CULLFACE) && SHADER_STAGE_FRAGMENT
    input.isFrontFace = IS_FRONT_VFACE(packedInput.cullFace, true, false);
#endif

    return input;
}

void Frag(PackedVaryingsToPS packedInput,
    out float4 outGBuffer0 : SV_Target0,
    out float4 outGBuffer1 : SV_Target1,
    out float4 outGBuffer2 : SV_Target2,
    out float4 outGBuffer3 : SV_Target3,
    out float outPlanetaryDistance : SV_Target4,
    out float outputDepth : SV_Depth)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(packedInput);
    FragInputs input = UnpackVaryingsToFragInputs(packedInput);
    outputDepth=input.positionSS.z;outPlanetaryDistance=0;
#if defined(SHADER_API_D3D11)
    if(PlanetWaterEnabled())
    {
        // The carrier's winding is unrelated to which side of the spherical water the observer sees.
        input.isFrontFace = !(_CameraInUnderwaterRegion && _WaterCameraHeightBuffer[0] < 0);
        float3 nearPoint=ComputeWorldSpacePosition(input.positionSS.xy*_ScreenSize.zw,UNITY_NEAR_CLIP_VALUE,UNITY_MATRIX_I_VP);
        float3 ray=normalize(nearPoint-GetCurrentViewPosition());
        double distance;
        if(!PlanetWaterIntersect(ray,PlanetWaterRadius(),distance))discard;
        WaterDisplacementData displacement;
        [unroll]for(int iteration=0;iteration<3;iteration++)
        {
            EvaluateWaterDisplacement(GetAbsolutePositionWS(ray*(float)distance),1,displacement);
            if(!PlanetWaterIntersect(ray,PlanetWaterRadius()+(double)displacement.displacement.y,distance))discard;
        }
        float3 normalLocal=(float3)PlanetWaterUnit(planetWaterPixelPoint);
        float3 axis=abs(normalLocal.x)<.9?float3(1,0,0):float3(0,0,1);
        planetWaterTangentU=normalize(axis-normalLocal*dot(axis,normalLocal));
        planetWaterTangentV=normalize(cross(normalLocal,planetWaterTangentU));
        float3 normal=PlanetWaterRotate(normalLocal);
        input.tangentToWorld=float3x3(PlanetWaterRotate(planetWaterTangentU),PlanetWaterRotate(planetWaterTangentV),normal);
        if(_PlanetWaterOptions.w>.5)
        {
            double3 unit;int face;double a,b,height;
            if(!SurfaceCanonicalHeight(planetWaterPixelPoint,unit,face,a,b,height)||height>(double)_PlanetWaterOptions.z+(double)displacement.displacement.y)discard;
            if(!input.isFrontFace)
            {
                // A submerged observer cannot see the opposite ocean surface through the solid planet.
                double3 seaPoint=planetWaterPixelPoint,groundPoint=seaPoint;
                double groundDistance=distance;bool groundOccludes=false;
                [unroll]for(int iteration=0;iteration<3;iteration++)
                {
                    double3 groundUnit;int groundFace;double groundA,groundB,groundHeight;
                    if(!SurfaceCanonicalHeight(groundPoint,groundUnit,groundFace,groundA,groundB,groundHeight))break;
                    if(!PlanetWaterIntersect(ray,PlanetWaterRadius()+groundHeight-(double)_PlanetWaterOptions.z,groundDistance))break;
                    groundPoint=planetWaterPixelPoint;groundOccludes=groundDistance<distance-.001;
                }
                planetWaterPixelPoint=seaPoint;
                if(groundOccludes)discard;
            }
        }
        float previous=LOAD_TEXTURE2D(_PlanetWaterOpaqueEndpoints,(int2)input.positionSS.xy).a;
        if(previous>0&&previous<(float)distance-.001)
        {
            float ownCoarse=LOAD_TEXTURE2D(_PlanetWaterOwnCoarseEndpoints,(int2)input.positionSS.xy).a;
            bool ownChord=_PlanetWaterOptions.w>.5&&input.isFrontFace&&previous==ownCoarse;
            if(!ownChord)discard;
        }
        float sceneDepth=LOAD_TEXTURE2D_X(_CameraDepthTexture,(uint2)input.positionSS.xy).x;
        if(sceneDepth!=UNITY_RAW_FAR_CLIP_VALUE)
        {
            float3 scenePoint=ComputeWorldSpacePosition(input.positionSS.xy*_ScreenSize.zw,sceneDepth,UNITY_MATRIX_I_VP);
            if(length(scenePoint-GetCurrentViewPosition())<(float)distance-.001)discard;
        }
        outPlanetaryDistance=(float)distance;
        // SSR and the pre-refraction depth pyramid consume this native depth before
        // the late planet composition pass. A compressed carrier appears camera-bound.
        input.positionRWS=GetCurrentViewPosition()+ray*(float)distance;
        float4 clipPosition=TransformWorldToHClip(input.positionRWS);
        outputDepth=max(1e-7,saturate(clipPosition.z/clipPosition.w));
        input.positionSS.z=outputDepth;input.positionSS.w=rcp(clipPosition.w);
        float3 absolutePoint=GetAbsolutePositionWS(ray*(float)distance);
        input.texCoord0=float4(absolutePoint.x,displacement.displacement.y,absolutePoint.z,0);
        input.texCoord1=float4(displacement.lowFrequencyHeight,0,displacement.sssMask,length(displacement.displacement.xz));
    }
#endif

    // Compute the tile index
    uint2 tileIndex = uint2(input.positionSS.xy) / GetTileSize();

    // input.positionSS is SV_Position
    PositionInputs posInput = GetPositionInput(input.positionSS.xy, _ScreenSize.zw, input.positionSS.z, input.positionSS.w, input.positionRWS.xyz, tileIndex);

#ifdef VARYINGS_NEED_POSITION_WS
    float3 V = GetWorldSpaceNormalizeViewDir(input.positionRWS);
#else
    // Unused
    float3 V = float3(1.0, 1.0, 1.0); // Avoid the division by 0
#endif

    // Get the surface and built in data
    SurfaceData surfaceData;
    BuiltinData builtinData;
    GetSurfaceAndBuiltinData(input, V, posInput, surfaceData, builtinData);

    // Light layers need to be set manually here as there is no mesh renderer
    builtinData.renderingLayers = DEFAULT_LIGHT_LAYERS;

    // The indirect diffuse term will be injected in the lighting much later
    builtinData.bakeDiffuseLighting = 0.0;

    // Compute the BSDF Data
    BSDFData bsdfData = ConvertSurfaceDataToBSDFData(input.positionSS.xy, surfaceData);

    // If the camera is in the underwater region of this surface and the the camera is under the surface
    if (_CameraInUnderwaterRegion && _WaterCameraHeightBuffer[0] < 0.0)
    {
        // For now we simply flip the normals and kill the caustics
        bsdfData.normalWS = -bsdfData.normalWS;
        bsdfData.lowFrequencyNormalWS = -bsdfData.lowFrequencyNormalWS;
        // TODO: This invalidation should happen earlier based on the CPU under water test
        bsdfData.caustics = 0;
    }

    // Encode the water into the gbuffer
    EncodeIntoGBuffer(bsdfData, builtinData, posInput.positionSS, outGBuffer0, outGBuffer1, outGBuffer2, outGBuffer3);
}
