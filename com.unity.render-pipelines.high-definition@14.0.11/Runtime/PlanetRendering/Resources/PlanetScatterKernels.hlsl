#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceData.hlsl"

struct PlanetScatterCandidateGpu
{
    uint4 stableId,meta;
    double4 planetLocalPosition;
    float4 rotation,scaleRadius,materialWeights,erosionData;
};
struct PlanetScatterPoseGpu
{
    float4x4 objectToWorld,worldToObject,previousObjectToWorld,previousWorldToObject;
    uint4 control;
};
struct ScatterExclusionKey {uint4 address,identity;};
struct ScatterExclusionCircle {uint4 centerXY,centerZ,radius;};
StructuredBuffer<int4> _ScatterCells;
StructuredBuffer<ScatterExclusionKey> _ScatterExcludedKeys;
StructuredBuffer<ScatterExclusionCircle> _ScatterExcludedCircles;
RWStructuredBuffer<PlanetScatterCandidateGpu> _ScatterCandidates;
RWStructuredBuffer<PlanetScatterPoseGpu> _ScatterPoses;
RWStructuredBuffer<uint> _ScatterLodState,_ScatterStatusCounters;
StructuredBuffer<PlanetScatterCandidateGpu> _ScatterOldCandidates;
StructuredBuffer<PlanetScatterPoseGpu> _ScatterOldPoses;
StructuredBuffer<uint> _ScatterOldLodState;
StructuredBuffer<int> _ScatterHistoryCells;
AppendStructuredBuffer<uint> _ScatterVisibleNear,_ScatterVisibleFar,_ScatterShadowNear,_ScatterShadowFar;
uint4 _ScatterPlanet,_ScatterDensityHeightMinimum,_ScatterHeightMaximumSlopeCosine,_ScatterWetness;
uint4 _ScatterSpacing;
uint4 _ScatterCameraXY,_ScatterCameraZ;
uint _ScatterSpecies,_ScatterSeed,_ScatterSlots,_ScatterCount,_ScatterRequiredChannels;
uint _ScatterStart,_ScatterEnd;
int _ScatterExcludeStamps,_ScatterExcludedKeyCount,_ScatterExcludedCircleCount,_ScatterMaterialsReady;
float4 _ScatterAffinity,_ScatterScaleRadius,_ScatterPlanetRotation,_ScatterCameraWorld;
float4 _ScatterDistances;
float4 _ScatterFrustum[6];
uint _ScatterHistoryValid,_ScatterFrame,_ScatterGeneration;

uint ScatterMix(uint v){v^=v>>16u;v*=0x7feb352du;v^=v>>15u;v*=0x846ca68bu;v^=v>>16u;return v;}
uint ScatterFold(int4 cell,uint slot)
{
    uint h=0x6d2b79f5u;
    h=ScatterMix(h^_ScatterPlanet.x);h=ScatterMix(h^_ScatterPlanet.y);h=ScatterMix(h^_ScatterPlanet.z);h=ScatterMix(h^_ScatterPlanet.w);
    h=ScatterMix(h^_ScatterSpecies);h=ScatterMix(h^(uint)cell.x);h=ScatterMix(h^(uint)cell.y);
    h=ScatterMix(h^(uint)cell.z);h=ScatterMix(h^(uint)cell.w);return ScatterMix(h^slot);
}
uint ScatterWord(uint hash,uint seed,uint stream){return ScatterMix(hash^ScatterMix(seed+0x9e3779b9u)^(stream*0x85ebca6bu));}
double ScatterOpen(uint word){return ((double)word+0.5)/4294967296.0;}
float4 ScatterQuaternion(float3 forward,float3 up)
{
    forward=normalize(forward);float3 right=normalize(cross(up,forward));up=cross(forward,right);
    float trace=right.x+up.y+forward.z;float4 q;
    if(trace>0){float s=sqrt(trace+1)*2;q=float4((up.z-forward.y)/s,(forward.x-right.z)/s,(right.y-up.x)/s,s*0.25);}
    else if(right.x>up.y&&right.x>forward.z){float s=sqrt(1+right.x-up.y-forward.z)*2;q=float4(s*0.25,(up.x+right.y)/s,(forward.x+right.z)/s,(up.z-forward.y)/s);}
    else if(up.y>forward.z){float s=sqrt(1+up.y-right.x-forward.z)*2;q=float4((up.x+right.y)/s,s*0.25,(forward.y+up.z)/s,(forward.x-right.z)/s);}
    else{float s=sqrt(1+forward.z-right.x-up.y)*2;q=float4((forward.x+right.z)/s,(forward.y+up.z)/s,s*0.25,(right.y-up.x)/s);}
    return normalize(q);
}
bool ScatterExcluded(int4 cell,uint slot,double3 direction,double radius)
{
    // Full species/cell/slot identity is tested. The uint4 rendering fingerprint is never authoritative.
    for(int i=0;i<_ScatterExcludedKeyCount;i++)
    {
        ScatterExclusionKey key=_ScatterExcludedKeys[i];
        if(all(key.address==(uint4)cell)&&key.identity.x==_ScatterSpecies&&key.identity.y==slot)return true;
    }
    for(int j=0;j<_ScatterExcludedCircleCount;j++)
    {
        ScatterExclusionCircle c=_ScatterExcludedCircles[j];double3 delta=direction-SurfaceD3(c.centerXY,c.centerZ);
        double distance=2*SurfaceAsin(SurfaceSqrt(SurfaceDot(delta,delta))*0.5)*radius;
        if(distance<=SurfaceD(c.radius.xy))return true;
    }
    if(_ScatterExcludeStamps!=0)for(int k=0;k<_SurfaceStampCount;k++)
    {
        SurfaceGpuStamp s=_SurfaceStamps[k];double3 delta=direction-SurfaceD3(s.centerXY,s.centerZ);
        double distance=2*SurfaceAsin(SurfaceSqrt(SurfaceDot(delta,delta))*0.5)*radius;
        if(distance<=SurfaceD(s.shape0.xy)+SurfaceD(s.shape1.xy))return true;
    }
    return false;
}
double3 ScatterProposalDirection(int4 cell,uint hash,out double2 ab)
{
    double count=(double)(1u<<(uint)cell.y);
    double2 low=2*double2(cell.z,cell.w)/count-1,high=2*(double2(cell.z,cell.w)+1)/count-1;
    double2 uv=double2(ScatterOpen(ScatterWord(hash,_ScatterSeed,0)),ScatterOpen(ScatterWord(hash,_ScatterSeed,1)));
    ab=2*(double2(cell.z,cell.w)+uv)/count-1;
    const double epsilon=8.8817841970012523233890533447265625e-16;
    ab.x=SurfaceClamp(ab.x,low.x+epsilon,high.x-epsilon);ab.y=SurfaceClamp(ab.y,low.y+epsilon,high.y-epsilon);
    double3 cube=double3(0,0,0);
    if(cell.x==0)cube=double3(1,ab.y,-ab.x);else if(cell.x==1)cube=double3(-1,ab.y,ab.x);
    else if(cell.x==2)cube=double3(ab.x,1,-ab.y);else if(cell.x==3)cube=double3(ab.x,-1,ab.y);
    else if(cell.x==4)cube=double3(ab.x,ab.y,1);else cube=double3(-ab.x,ab.y,-1);
    return SurfaceUnit(cube);
}
double ScatterMinimumAbs(double a,double b){return a<=0&&b>=0?0:SurfaceMin(SurfaceAbs(a),SurfaceAbs(b));}
int4 ScatterSpacingRectangle(int face,double3 lo,double3 hi,int grid)
{
    double d0=0,d1=0,other=0;double2 n0=double2(0,0),n1=double2(0,0);
    if(face==0||face==1)
    {d0=face==0?lo.x:-hi.x;d1=face==0?hi.x:-lo.x;other=SurfaceMax(ScatterMinimumAbs(lo.y,hi.y),ScatterMinimumAbs(lo.z,hi.z));}
    else if(face==2||face==3)
    {d0=face==2?lo.y:-hi.y;d1=face==2?hi.y:-lo.y;other=SurfaceMax(ScatterMinimumAbs(lo.z,hi.z),ScatterMinimumAbs(lo.x,hi.x));}
    else
    {d0=face==4?lo.z:-hi.z;d1=face==4?hi.z:-lo.z;other=SurfaceMax(ScatterMinimumAbs(lo.x,hi.x),ScatterMinimumAbs(lo.y,hi.y));}
    if(face==0){n0=double2(-hi.z,lo.y);n1=double2(-lo.z,hi.y);}
    else if(face==1){n0=double2(lo.z,lo.y);n1=double2(hi.z,hi.y);}
    else if(face==2){n0=double2(lo.x,-hi.z);n1=double2(hi.x,-lo.z);}
    else if(face==3){n0=double2(lo.x,lo.z);n1=double2(hi.x,hi.z);}
    else if(face==4){n0=double2(lo.x,lo.y);n1=double2(hi.x,hi.y);}
    else{n0=double2(-hi.x,lo.y);n1=double2(-lo.x,hi.y);}
    int4 result=int4(0,0,-1,-1);
    if(d1>0&&d1>=other)
    {
        d0=SurfaceMax(d0,other);double2 a0=double2(-1,-1),a1=double2(1,1);
        if(d0>0)
        {
            double2 p00=n0/d0,p01=n0/d1,p10=n1/d0,p11=n1/d1;
            a0=double2(SurfaceMax(-1,SurfaceMin(SurfaceMin(p00.x,p01.x),SurfaceMin(p10.x,p11.x))),SurfaceMax(-1,SurfaceMin(SurfaceMin(p00.y,p01.y),SurfaceMin(p10.y,p11.y))));
            a1=double2(SurfaceMin(1,SurfaceMax(SurfaceMax(p00.x,p01.x),SurfaceMax(p10.x,p11.x))),SurfaceMin(1,SurfaceMax(SurfaceMax(p00.y,p01.y),SurfaceMax(p10.y,p11.y))));
        }
        if(a0.x<=a1.x&&a0.y<=a1.y)
        {
            int2 lower=int2((int)SurfaceMin((double)(grid-1),SurfaceMax(0,(a0.x+1)*0.5)*(double)grid),(int)SurfaceMin((double)(grid-1),SurfaceMax(0,(a0.y+1)*0.5)*(double)grid));
            int2 upper=int2((int)SurfaceMin((double)(grid-1),SurfaceMin(1,(a1.x+1)*0.5)*(double)grid),(int)SurfaceMin((double)(grid-1),SurfaceMin(1,(a1.y+1)*0.5)*(double)grid));
            result=int4(lower,upper);
        }
    }
    return result;
}
bool ScatterProposalLowerPriority(int4 other,uint otherSlot,uint otherHash,int4 cell,uint slot,uint hash)
{
    uint a=ScatterWord(otherHash,_ScatterSeed,5),b=ScatterWord(hash,_ScatterSeed,5);
    if(a!=b)return a<b;
    if(other.x!=cell.x)return other.x<cell.x;if(other.y!=cell.y)return other.y<cell.y;
    if(other.w!=cell.w)return other.w<cell.w;if(other.z!=cell.z)return other.z<cell.z;return otherSlot<slot;
}
bool ScatterSpacingRetained(int4 cell,uint slot,uint hash,double3 direction,double radius,out bool supported)
{
    supported=true;double spacing=SurfaceD(_ScatterSpacing.xy);if(spacing==0)return true;
    double padding=SurfaceD(_ScatterSpacing.zw),chord=spacing/radius+2*padding;
    double3 center=SurfaceUnit(direction),lo=double3(SurfaceMax(-1,center.x-chord),SurfaceMax(-1,center.y-chord),SurfaceMax(-1,center.z-chord));
    double3 hi=double3(SurfaceMin(1,center.x+chord),SurfaceMin(1,center.y+chord),SurfaceMin(1,center.z+chord));
    int grid=(int)(1u<<(uint)cell.y);int4 rectangles[6];uint cells=0;
    [unroll]for(int face=0;face<6;face++)
    {
        int4 r=ScatterSpacingRectangle(face,lo,hi,grid);rectangles[face]=r;
        if(r.z>=r.x&&r.w>=r.y)cells+=(uint)(r.z-r.x+1)*(uint)(r.w-r.y+1);
    }
    if(cells>64u||cells*_ScatterSlots>8192u){supported=false;return false;}
    double threshold=spacing/radius+padding,thresholdSquared=threshold*threshold;
    [loop]for(int faceIndex=0;faceIndex<6;faceIndex++)
    {
        int4 rectangle=rectangles[faceIndex];
        [loop]for(int y=rectangle.y;y<=rectangle.w;y++)[loop]for(int x=rectangle.x;x<=rectangle.z;x++)[loop]for(uint otherSlot=0;otherSlot<_ScatterSlots;otherSlot++)
        {
            int4 other=int4(faceIndex,cell.y,x,y);if(all(other==cell)&&otherSlot==slot)continue;
            uint otherHash=ScatterFold(other,otherSlot);if(!ScatterProposalLowerPriority(other,otherSlot,otherHash,cell,slot,hash))continue;
            double2 ab;double3 proposed=ScatterProposalDirection(other,otherHash,ab);
            double width=2/(double)grid,shape=1+ab.x*ab.x+ab.y*ab.y;
            double probability=SurfaceD(_ScatterDensityHeightMinimum.xy)*width*width*(radius*radius/(shape*SurfaceSqrt(shape)))/(double)_ScatterSlots;
            if(!(ScatterOpen(ScatterWord(otherHash,_ScatterSeed,2))<probability))continue;
            double3 delta=direction-proposed;double squared=(delta.x*delta.x+delta.y*delta.y)+delta.z*delta.z;
            if(squared<=thresholdSquared)return false;
        }
    }
    return true;
}
// Canonical FP64 sampling has a larger register footprint than culling. One 32-lane
// group keeps the combined register allocation below D3D11's recommended budget.
[numthreads(32,1,1)]
void GenerateCandidates(uint3 dispatchId:SV_DispatchThreadID)
{
    uint index=dispatchId.x+_ScatterStart;if(index>=_ScatterEnd)return;
    uint cellIndex=index/_ScatterSlots,slot=index-cellIndex*_ScatterSlots;
    int4 cell=_ScatterCells[cellIndex];uint hash=ScatterFold(cell,slot);
    PlanetScatterCandidateGpu result=(PlanetScatterCandidateGpu)0;
    result.stableId=uint4(ScatterWord(hash,0,101),ScatterWord(hash,0,102),ScatterWord(hash,0,103),ScatterWord(hash,0,104));
    result.meta=uint4(_ScatterSpecies,cellIndex,slot,0);_ScatterLodState[index]=0;_ScatterPoses[index]=(PlanetScatterPoseGpu)0;
    if(_ScatterMaterialsReady==0){_ScatterCandidates[index]=result;InterlockedAdd(_ScatterStatusCounters[0],1);return;}
    double count=(double)(1u<<(uint)cell.y),width=2/count;
    double2 low=2*double2(cell.z,cell.w)/count-1,high=low+width;
    double2 uv=double2(ScatterOpen(ScatterWord(hash,_ScatterSeed,0)),ScatterOpen(ScatterWord(hash,_ScatterSeed,1)));
    double2 ab=2*(double2(cell.z,cell.w)+uv)/count-1;
    const double epsilon=8.8817841970012523233890533447265625e-16;
    ab.x=SurfaceClamp(ab.x,low.x+epsilon,high.x-epsilon);ab.y=SurfaceClamp(ab.y,low.y+epsilon,high.y-epsilon);
    double3 cube;
    if(cell.x==0)cube=double3(1,ab.y,-ab.x);else if(cell.x==1)cube=double3(-1,ab.y,ab.x);
    else if(cell.x==2)cube=double3(ab.x,1,-ab.y);else if(cell.x==3)cube=double3(ab.x,-1,ab.y);
    else if(cell.x==4)cube=double3(ab.x,ab.y,1);else cube=double3(-ab.x,ab.y,-1);
    double3 direction=SurfaceUnit(cube);double height;double3 geometryNormal;
    if(!SurfaceNativeHeight(direction,height)||!SurfaceNativeNormalAtMetres(direction,0,SurfaceD(_SurfaceRadiusAndNormal.zw),geometryNormal))
    {_ScatterCandidates[index]=result;InterlockedAdd(_ScatterStatusCounters[0],1);return;}
    // Placement already queried the canonical height and normal. Reuse that geometry for
    // automatic masks; profiles with a different normal step still issue their own query.
    PlanetSurfaceVertexAttributes attrs=SurfaceNativeAttributesForGeometry(direction,0,true,height,geometryNormal);
    if((attrs.channels&_ScatterRequiredChannels)!=_ScatterRequiredChannels)
    {_ScatterCandidates[index]=result;InterlockedAdd(_ScatterStatusCounters[0],1);return;}
    double radius=SurfaceD(_SurfaceRadiusAndNormal.xy);double3 position=direction*(radius+height);
    if(radius+height<=0||!SurfaceFinite(position.x)||!SurfaceFinite(position.y)||!SurfaceFinite(position.z))
    {_ScatterCandidates[index]=result;InterlockedAdd(_ScatterStatusCounters[0],1);return;}
    double3 normal=SurfaceUnit(geometryNormal);
    double3 reference=SurfaceAbs(normal.y)<0.9?double3(0,1,0):double3(1,0,0);
    double3 forward=SurfaceUnit(reference-normal*SurfaceDot(reference,normal)),right=SurfaceCross(normal,forward);
    double yaw=ScatterOpen(ScatterWord(hash,_ScatterSeed,3))*6.2831853071795864769;
    float scale=(float)SurfaceLerp((double)_ScatterScaleRadius.x,(double)_ScatterScaleRadius.y,ScatterOpen(ScatterWord(hash,_ScatterSeed,4)));
    result.planetLocalPosition=double4(position,height);
    result.rotation=ScatterQuaternion((float3)(forward*(double)cos((float)yaw)+right*(double)sin((float)yaw)),(float3)normal);
    result.scaleRadius=float4(scale,scale,scale,scale*_ScatterScaleRadius.z);
    result.materialWeights=attrs.materialWeights;result.erosionData=attrs.erosionData;
    uint decision=1;
    if(ScatterExcluded(cell,slot,direction,radius))decision=7;
    else if(height<SurfaceD(_ScatterDensityHeightMinimum.zw)||height>SurfaceD(_ScatterHeightMaximumSlopeCosine.xy))decision=3;
    else if(SurfaceDot(normal,direction)<SurfaceD(_ScatterHeightMaximumSlopeCosine.zw))decision=4;
    else if((attrs.channels&2u)!=0&&(attrs.erosionData.y<SurfaceD(_ScatterWetness.xy)||attrs.erosionData.y>SurfaceD(_ScatterWetness.zw)))decision=6;
    else
    {
        double affinity=(double)dot(attrs.materialWeights,_ScatterAffinity);
        if(!(affinity>0))decision=5;
        else
        {
            double shape=1+ab.x*ab.x+ab.y*ab.y;
            double jacobian=radius*radius/(shape*SurfaceSqrt(shape));
            double probability=SurfaceD(_ScatterDensityHeightMinimum.xy)*width*width*jacobian/(double)_ScatterSlots*affinity;
            if(!(ScatterOpen(ScatterWord(hash,_ScatterSeed,2))<probability))decision=2;
        }
    }
    if(decision==1&&SurfaceD(_ScatterSpacing.xy)>0)
    {
        bool supported=true;bool retained=ScatterSpacingRetained(cell,slot,hash,direction,radius,supported);
        if(!supported){_ScatterCandidates[index]=result;InterlockedAdd(_ScatterStatusCounters[0],1);return;}
        if(!retained)decision=8;
    }
    result.meta.w=decision;_ScatterCandidates[index]=result;
    if(decision==1)InterlockedAdd(_ScatterStatusCounters[1],1);
}
[numthreads(64,1,1)]
void CopyHistory(uint3 dispatchId:SV_DispatchThreadID)
{
    uint index=dispatchId.x;if(index>=_ScatterCount)return;
    uint cellIndex=index/_ScatterSlots,slot=index-cellIndex*_ScatterSlots;
    int oldCell=_ScatterHistoryCells[cellIndex];if(oldCell<0||_ScatterCandidates[index].meta.w!=1)return;
    uint oldIndex=(uint)oldCell*_ScatterSlots+slot;
    if(_ScatterOldCandidates[oldIndex].meta.w!=1)return;
    _ScatterPoses[index]=_ScatterOldPoses[oldIndex];_ScatterLodState[index]=_ScatterOldLodState[oldIndex];
}
float4 ScatterMultiply(float4 a,float4 b){return float4(a.w*b.xyz+b.w*a.xyz+cross(a.xyz,b.xyz),a.w*b.w-dot(a.xyz,b.xyz));}
double3 ScatterRotate(double3 v,float4 q){double3 t=2*SurfaceCross((double3)q.xyz,v);return v+(double)q.w*t+SurfaceCross((double3)q.xyz,t);}
void ScatterMatrices(float3 position,float4 q,float scale,out float4x4 objectMatrix,out float4x4 inverseMatrix)
{
    float x=q.x,y=q.y,z=q.z,w=q.w;
    float3 right=float3(1-2*(y*y+z*z),2*(x*y+z*w),2*(x*z-y*w));
    float3 up=float3(2*(x*y-z*w),1-2*(x*x+z*z),2*(y*z+x*w));
    float3 forward=float3(2*(x*z+y*w),2*(y*z-x*w),1-2*(x*x+y*y));
    objectMatrix=float4x4(float4(right.x*scale,up.x*scale,forward.x*scale,position.x),
        float4(right.y*scale,up.y*scale,forward.y*scale,position.y),float4(right.z*scale,up.z*scale,forward.z*scale,position.z),float4(0,0,0,1));
    right/=scale;up/=scale;forward/=scale;
    inverseMatrix=float4x4(float4(right,-dot(right,position)),float4(up,-dot(up,position)),float4(forward,-dot(forward,position)),float4(0,0,0,1));
}
[numthreads(64,1,1)]
void CullAndPose(uint3 dispatchId:SV_DispatchThreadID)
{
    uint index=dispatchId.x;if(index>=_ScatterCount)return;
    PlanetScatterCandidateGpu candidate=_ScatterCandidates[index];if(candidate.meta.w!=1)return;
    double3 localCamera=SurfaceD3(_ScatterCameraXY,_ScatterCameraZ);
    // Subtract large planet-local coordinates in double, before any rotation or float conversion.
    double3 delta=candidate.planetLocalPosition.xyz-localCamera;
    float3 relative=(float3)ScatterRotate(delta,_ScatterPlanetRotation),world=relative+_ScatterCameraWorld.xyz;
    PlanetScatterPoseGpu old=_ScatterPoses[index],pose=(PlanetScatterPoseGpu)0;
    ScatterMatrices(world,normalize(ScatterMultiply(_ScatterPlanetRotation,candidate.rotation)),candidate.scaleRadius.x,pose.objectToWorld,pose.worldToObject);
    bool valid=_ScatterHistoryValid!=0&&old.control.x==_ScatterGeneration&&old.control.y+1u==_ScatterFrame;
    pose.previousObjectToWorld=valid?old.objectToWorld:pose.objectToWorld;
    pose.previousWorldToObject=valid?old.worldToObject:pose.worldToObject;
    pose.control=uint4(_ScatterGeneration,_ScatterFrame,valid?1u:0u,0);_ScatterPoses[index]=pose;
    float distance=length(relative),radius=candidate.scaleRadius.w;
    uint lod=_ScatterLodState[index];
    if(distance>_ScatterDistances.z+_ScatterDistances.w)lod=1;
    else if(distance<_ScatterDistances.z-_ScatterDistances.w)lod=0;
    _ScatterLodState[index]=lod;
    bool visible=distance<=_ScatterDistances.x+radius;
    for(uint p=0;p<6u;p++)visible=visible&&(dot(_ScatterFrustum[p].xyz,world)+_ScatterFrustum[p].w>=-radius);
    if(visible){if(lod==0)_ScatterVisibleNear.Append(index);else _ScatterVisibleFar.Append(index);}
    // Native ShadowsOnly submissions retain off-camera casters. Native HDRP light
    // culling handles the conservative union; camera frustum rejection is not reused.
    if(distance<=_ScatterDistances.y+radius){if(lod==0)_ScatterShadowNear.Append(index);else _ScatterShadowFar.Append(index);}
}
