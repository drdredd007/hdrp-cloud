
// Planet patch vertices for rendering. Output layouts match PlanetPatchJob/PlanetPatchMesh (far)
// and PlanetLocalPatchJob (local) so GPU geometry and CPU colliders/picking describe one surface.
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetField.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceData.hlsl"

#define PLANET_FAR_SCALE 0.001 // PlanetField.FarScale

RWStructuredBuffer<PlanetVertex> _PlanetVertices;
RWStructuredBuffer<PlanetVertex> _PlanetParentVertices;
RWStructuredBuffer<PlanetSurfaceVertexAttributes> _PlanetAttributes,_PlanetParentAttributes;
uint _PlanetBaseVertex, _PlanetVertexCount;
int _PlanetResolution;

// Far patch: cube-face address; skirt depth is evaluated in double on the CPU.
int _PatchFace, _PatchLevel, _PatchX, _PatchY;
float _PatchSkirtDepth;
uint4 _SurfaceRenderFootprints;

// Local patch: surface frame whose Up is the radial direction through its origin.
float3 _FrameRight, _FrameUp, _FrameForward;
float _FrameRadius;       // |frame origin| in metres
float _FrameHeight;       // |frame origin| - planet radius, evaluated in double
int _LocalKeyX, _LocalKeyZ;
float _LocalCellSize;

PlanetVertex FarVertex(uint x, uint y, out float3 direction)
{
    float3 pivot = PlanetCubeDirection(_PatchFace, _PatchLevel, _PatchX, _PatchY, 0.5, 0.5);
    direction = PlanetCubeDirection(_PatchFace, _PatchLevel, _PatchX, _PatchY, x / (float)_PlanetResolution, y / (float)_PlanetResolution);
    PlanetVertex v;
    v.position = PlanetSurfaceFrom(direction, pivot) * PLANET_FAR_SCALE;
    v.normal = PlanetNormal(direction);
    v.color = PlanetColor(direction);
    return v;
}

[numthreads(64, 1, 1)]
void FarPatch(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= _PlanetVertexCount) return;
    uint resolution = (uint)_PlanetResolution, row = resolution + 1, count = row * row;
    float3 direction;
    PlanetVertex v;
    if (index < count)
        v = FarVertex(index % row, index / row, direction);
    else
    {
        // Radial skirt below the matching edge vertex (PlanetPatchMesh edge order).
        uint local = index - count, edge = local / row, j = local % row;
        uint top = edge == 0 ? j : edge == 1 ? j * row + resolution : edge == 2 ? resolution * row + resolution - j : (resolution - j) * row;
        v = FarVertex(top % row, top / row, direction);
        v.position -= direction * (_PatchSkirtDepth * PLANET_FAR_SCALE);
    }
    _PlanetVertices[_PlanetBaseVertex + index] = v;
}

[numthreads(64, 1, 1)]
void LocalPatch(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= _PlanetVertexCount) return;
    uint row = (uint)_PlanetResolution + 1;
    float lx = (_LocalKeyX * _PlanetResolution + (int)(index % row)) * _LocalCellSize;
    float lz = (_LocalKeyZ * _PlanetResolution + (int)(index / row)) * _LocalCellSize;
    float3 tangent = _FrameRight * lx + _FrameForward * lz;
    float3 direction = normalize(_FrameUp * _FrameRadius + tangent);
    float height = max(0.0, PlanetHeight(direction));

    // Frame-local surface point without radius-scale cancellation:
    // |q| = sqrt(rho^2 + t^2) = rho + e, e = t^2 / (rho + |q|); delta = (R + h) - |q| = h - h0 - e.
    float t2 = lx * lx + lz * lz;
    float e = t2 / (_FrameRadius + sqrt(_FrameRadius * _FrameRadius + t2));
    float radial = _FrameRadius + e;
    float delta = height - _FrameHeight - e;
    PlanetVertex v;
    v.position = float3(lx * (1.0 + delta / radial), (_FrameRadius / radial) * delta, lz * (1.0 + delta / radial));
    float3 normal = PlanetNormal(direction);
    v.normal = float3(dot(normal, _FrameRight), dot(normal, _FrameUp), dot(normal, _FrameForward));
    v.color = PlanetColor(direction);
    _PlanetVertices[_PlanetBaseVertex + index] = v;
}

PlanetVertex NativeFarVertexFiltered(uint x,uint y,double footprint,out double3 direction,out double height,out double3 normal)
{
    double3 pivot=SurfaceCubeDirection(_PatchFace,_PatchLevel,_PatchX,_PatchY,0.5,0.5);
    direction=SurfaceCubeDirection(_PatchFace,_PatchLevel,_PatchX,_PatchY,(double)x/(double)_PlanetResolution,(double)y/(double)_PlanetResolution);
    PlanetVertex v=(PlanetVertex)0;height=0;normal=double3(0,0,0);
    if(!SurfaceNativeHeightFiltered(direction,footprint,height)||!SurfaceNativeNormalAtMetres(direction,footprint,SurfaceD(_SurfaceRadiusAndNormal.zw),normal))return v;
    double radius=SurfaceD(_SurfaceRadiusAndNormal.xy);
    v.position=(float3)((radius*(direction-pivot)+direction*height)*0.001L);
    v.normal=(float3)normal;v.color=float4(0.24,0.22,0.19,1);return v;
}
PlanetVertex NativeFarVertexFiltered(uint x,uint y,double footprint,out double3 direction)
{double height;double3 normal;return NativeFarVertexFiltered(x,y,footprint,direction,height,normal);}
PlanetVertex NativeFarVertex(uint x,uint y,out double3 direction)
{return NativeFarVertexFiltered(x,y,0,direction);}
[numthreads(32,1,1)]
void NativeFarPatch(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;if(index>=_PlanetVertexCount)return;
    uint resolution=(uint)_PlanetResolution,row=resolution+1,count=row*row;double3 direction;PlanetVertex v;
    if(index<count)v=NativeFarVertex(index%row,index/row,direction);
    else
    {
        uint local=index-count,edge=local/row,j=local%row;
        uint top=edge==0?j:edge==1?j*row+resolution:edge==2?resolution*row+resolution-j:(resolution-j)*row;
        v=NativeFarVertex(top%row,top/row,direction);if(v.color.a>0)v.position-=(float3)direction*(_PatchSkirtDepth*PLANET_FAR_SCALE);
    }
    _PlanetVertices[_PlanetBaseVertex+index]=v;
    if(v.color.a>0)_PlanetAttributes[_PlanetBaseVertex+index]=SurfaceNativeAttributes(direction);
    else _PlanetAttributes[_PlanetBaseVertex+index]=(PlanetSurfaceVertexAttributes)0;
}
[numthreads(16,1,1)]
void NativeFilteredFarPatch(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;if(index>=_PlanetVertexCount)return;
    uint resolution=(uint)_PlanetResolution,row=resolution+1,count=row*row,top=index;
    if(index>=count)
    {
        uint local=index-count,edge=local/row,j=local%row;
        top=edge==0?j:edge==1?j*row+resolution:edge==2?resolution*row+resolution-j:(resolution-j)*row;
    }
    double3 direction=0,parentDirection=0,fineNormal=0,parentNormal=0;double fineHeight=0,parentHeight=0;
    double fineSupport=SurfaceD(_SurfaceRenderFootprints.xy),parentSupport=SurfaceD(_SurfaceRenderFootprints.zw);
    PlanetVertex fine=(PlanetVertex)0,parent=(PlanetVertex)0;
    // Fine geometry precedes parent geometry, as before. A single rolled body
    // keeps the structural field call graph independent of the number of bands.
    [loop]for(int geometryBandIndex=0;geometryBandIndex<2;geometryBandIndex++)
    {
        double support=geometryBandIndex==0?fineSupport:parentSupport;
        double3 bandDirection,bandNormal;double bandHeight;
        PlanetVertex band=NativeFarVertexFiltered(top%row,top/row,support,bandDirection,bandHeight,bandNormal);
        if(geometryBandIndex==0){fine=band;direction=bandDirection;fineHeight=bandHeight;fineNormal=bandNormal;}
        else{parent=band;parentDirection=bandDirection;parentHeight=bandHeight;parentNormal=bandNormal;}
    }
    if(index>=count)
    {
        if(fine.color.a>0)fine.position-=(float3)direction*(_PatchSkirtDepth*PLANET_FAR_SCALE);
        if(parent.color.a>0)parent.position-=(float3)direction*(_PatchSkirtDepth*PLANET_FAR_SCALE);
    }
    _PlanetVertices[_PlanetBaseVertex+index]=fine;_PlanetParentVertices[_PlanetBaseVertex+index]=parent;
    // Keep fine attributes before parent attributes and after both geometry
    // evaluations. Invalid bands still write the exact zero attribute payload.
    [loop]for(int attributeBandIndex=0;attributeBandIndex<2;attributeBandIndex++)
    {
        bool fineBand=attributeBandIndex==0;
        PlanetSurfaceVertexAttributes attributes=(PlanetSurfaceVertexAttributes)0;
        if((fineBand?fine.color.a:parent.color.a)>0)
            attributes=SurfaceNativeRenderAttributes(fineBand?direction:parentDirection,fineBand?fineSupport:parentSupport,
                fineBand?fineHeight:parentHeight,fineBand?fineNormal:parentNormal);
        if(fineBand)_PlanetAttributes[_PlanetBaseVertex+index]=attributes;
        else _PlanetParentAttributes[_PlanetBaseVertex+index]=attributes;
    }
}
[numthreads(32,1,1)]
void NativeLocalPatch(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;if(index>=_PlanetVertexCount)return;uint row=(uint)_PlanetResolution+1;
    double cell=SurfaceD(_SurfaceCellSize.xy);
    double lx=(double)(_LocalKeyX*_PlanetResolution+(int)(index%row))*cell,lz=(double)(_LocalKeyZ*_PlanetResolution+(int)(index/row))*cell;
    double3 right=SurfaceD3(_SurfaceFrameRightXY,_SurfaceFrameRightZ),up=SurfaceD3(_SurfaceFrameUpXY,_SurfaceFrameUpZ),forward=SurfaceD3(_SurfaceFrameForwardXY,_SurfaceFrameForwardZ);
    double3 radial=SurfaceD3(_SurfaceFrameRadialXY,_SurfaceFrameRadialZ),tangent=right*lx+forward*lz;
    double rho=SurfaceD(_SurfaceFrameShape.xy),h0=SurfaceD(_SurfaceFrameShape.zw);
    double numerator=2*rho*SurfaceDot(radial,tangent)+SurfaceDot(tangent,tangent);
    double e=numerator/(rho+SurfaceSqrt(rho*rho+numerator));
    double3 direction=SurfaceUnit(radial*rho+tangent);PlanetVertex v=(PlanetVertex)0;double height;float3 normal;
    if(SurfaceNativeHeight(direction,height)&&SurfaceNativeNormal(direction,normal))
    {
        double3 relative=tangent+direction*(height-h0-e);
        v.position=(float3)double3(SurfaceDot(relative,right),SurfaceDot(relative,up),SurfaceDot(relative,forward));
        v.normal=(float3)double3(SurfaceDot((double3)normal,right),SurfaceDot((double3)normal,up),SurfaceDot((double3)normal,forward));
        v.color=float4(0.24,0.22,0.19,1);
    }
    _PlanetVertices[_PlanetBaseVertex+index]=v;
    if(v.color.a>0)_PlanetAttributes[_PlanetBaseVertex+index]=SurfaceNativeAttributes(direction);
    else _PlanetAttributes[_PlanetBaseVertex+index]=(PlanetSurfaceVertexAttributes)0;
}
