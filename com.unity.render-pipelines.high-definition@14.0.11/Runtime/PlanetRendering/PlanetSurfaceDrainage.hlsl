#ifndef SPACERUNNER_DRAINAGE_FIELD_INCLUDED
#define SPACERUNNER_DRAINAGE_FIELD_INCLUDED
// Captured morphology-four authority. FP64 packed words mirror the leased CPU DTOs.
struct SurfaceDrainageLandmass { uint4 centerXY,centerZ,rightXY,rightZ,forwardXY,forwardZ;int4 layout; };
struct SurfaceDrainageDirection { uint4 xy,z; };
struct SurfaceDrainageNode { uint4 directionXY,directionZ,bedArea,widthDivide;int4 topology; };
struct SurfaceDrainageSegment { uint4 minimumXY,minimumZ,maximumXY,maximumZ;int4 topology; };
struct SurfaceDrainageIndexNode { uint4 split;int4 children,range; };
StructuredBuffer<SurfaceDrainageLandmass> _SurfaceDrainageLandmasses;
StructuredBuffer<uint4> _SurfaceDrainageCoastVertices;
StructuredBuffer<SurfaceDrainageDirection> _SurfaceDrainageCoastDirections;
StructuredBuffer<SurfaceDrainageNode> _SurfaceDrainageNodes;
StructuredBuffer<SurfaceDrainageSegment> _SurfaceDrainageSegments,_SurfaceDrainageCoastSegments;
StructuredBuffer<SurfaceDrainageIndexNode> _SurfaceDrainageIndex,_SurfaceDrainageCoastIndex;
StructuredBuffer<int> _SurfaceDrainageReferences,_SurfaceDrainageCoastReferences;
int4 _SurfaceDrainageCounts,_SurfaceDrainageIndexCounts;
uint4 _SurfaceDrainageShape;
double3 DrainageUnitOr(double3 value,double3 fallback)
{
    // Unity Mathematics normalizesafe(double3) uses FLT_MIN_NORMAL as its
    // threshold too. Keep decoded, almost coincident coast vertices identical.
    return SurfaceDot(value,value)>1.1754943508222875e-38L?SurfaceUnit(value):fallback;
}
double3 DrainageCrossUncontracted(double3 a,double3 b)
{
    // CPU cross evaluates the two products separately. A fused multiply/subtract
    // can leave a cancellation residual above normalizesafe's threshold for
    // valid nearly coincident coast vertices. Keep this authority operation exact.
    precise double x=a.y*b.z-a.z*b.y;
    precise double y=a.z*b.x-a.x*b.z;
    precise double z=a.x*b.y-a.y*b.x;
    return double3(x,y,z);
}
bool DrainageLeaf(bool coastal,double3 d,out int first,out int count)
{
    first=count=0;int cursor=0,size=coastal?_SurfaceDrainageIndexCounts.z:_SurfaceDrainageIndexCounts.x;
    [loop]for(int step=0;step<=30;step++)
    {
        if(cursor<0||cursor>=size)return false;
        SurfaceDrainageIndexNode node;
        if(coastal)node=_SurfaceDrainageCoastIndex[cursor];else node=_SurfaceDrainageIndex[cursor];
        if(node.children.x<0){first=node.range.x;count=node.range.y;return count>=0&&count<=96;}
        if(node.children.x>2)return false;
        // DX11 cannot dynamically address a double vector in a rolled loop.
        // Explicit components preserve FP64 comparison and the bounded traversal.
        double coordinate=node.children.x==0?d.x:(node.children.x==1?d.y:d.z);
        cursor=coordinate<SurfaceD(node.split.xy)?node.children.y:node.children.z;
    }
    return false;
}
double DrainageCross2(double2 a,double2 b){return a.x*b.y-a.y*b.x;}
double2 DrainageCoastVertex(int index)
{uint4 p=_SurfaceDrainageCoastVertices[index];return double2(SurfaceD(p.xy),SurfaceD(p.zw));}
bool DrainageAngleLessOrEqual(double2 a,double2 b,double2 origin)
{
    double2 first=double2(a.x*origin.x+a.y*origin.y,DrainageCross2(origin,a));
    double2 second=double2(b.x*origin.x+b.y*origin.y,DrainageCross2(origin,b));
    int ha=first.y>0||(first.y==0&&first.x>=0)?0:1,hb=second.y>0||(second.y==0&&second.x>=0)?0:1;
    return ha!=hb?ha<hb:DrainageCross2(first,second)>=0;
}
bool DrainageContains(SurfaceDrainageLandmass mass,double3 d)
{
    double facing=SurfaceDot(d,SurfaceD3(mass.centerXY,mass.centerZ));if(facing<=0)return false;
    double2 p=double2(SurfaceDot(d,SurfaceD3(mass.rightXY,mass.rightZ)),SurfaceDot(d,SurfaceD3(mass.forwardXY,mass.forwardZ)))*(SurfaceD(_SurfaceDrainageShape.xy)/facing);
    if(p.x*p.x+p.y*p.y<1e-20L)return true;
    double2 origin=DrainageCoastVertex(mass.layout.x);int low=1,high=mass.layout.y;
    [loop]while(low<high)
    {
        int middle=low+((high-low)>>1);double2 vertex=DrainageCoastVertex(mass.layout.x+middle);
        if(DrainageAngleLessOrEqual(vertex,p,origin))low=middle+1;else high=middle;
    }
    int previous=low-1,next=low==mass.layout.y?0:low;
    double2 a=DrainageCoastVertex(mass.layout.x+previous),b=DrainageCoastVertex(mass.layout.x+next);
    return DrainageCross2(b-a,p-a)>=0;
}
double DrainageClosestSquared(double3 d,double3 first,double3 last)
{
    double3 normal=DrainageUnitOr(DrainageCrossUncontracted(first,last),double3(0,0,0));double perpendicular=SurfaceDot(d,normal);
    double3 projected=DrainageUnitOr(d-normal*perpendicular,first);if(SurfaceDot(projected,first+last)<0)projected=-projected;
    bool onArc=SurfaceDot(SurfaceCross(first,projected),normal)>=-1e-13L&&SurfaceDot(SurfaceCross(projected,last),normal)>=-1e-13L;
    double3 closest=onArc?projected:(SurfaceDot(d,first)>=SurfaceDot(d,last)?first:last),delta=d-closest;
    return SurfaceDot(delta,delta);
}
bool SurfaceDrainageCoast(double3 input,out double signedDistance)
{
    signedDistance=0;if(_SurfaceDrainageCounts.x<=0||_SurfaceDrainageCounts.x>16)return false;
    double3 d=SurfaceUnit(input);int first,count;
    if(!DrainageLeaf(true,d,first,count)||first<0||first+count>_SurfaceDrainageIndexCounts.w)return false;
    double radius=SurfaceD(_SurfaceDrainageShape.xy),influence=SurfaceD(_SurfaceDrainageShape.zw);
    double chord=2*StructureSin(influence/(2*radius)),limit=chord*chord;
    double distances[16];
    [unroll]for(int massSlot=0;massSlot<16;massSlot++)distances[massSlot]=limit;
    [loop]for(int i=0;i<count;i++)
    {
        SurfaceDrainageSegment segment=_SurfaceDrainageCoastSegments[_SurfaceDrainageCoastReferences[first+i]];
        if(any(d<SurfaceD3(segment.minimumXY,segment.minimumZ))||any(d>SurfaceD3(segment.maximumXY,segment.maximumZ)))continue;
        int m=segment.topology.z;if(m<0||m>=_SurfaceDrainageCounts.x)return false;
        SurfaceDrainageDirection a=_SurfaceDrainageCoastDirections[segment.topology.x],b=_SurfaceDrainageCoastDirections[segment.topology.y];
        distances[m]=SurfaceMin(distances[m],DrainageClosestSquared(d,SurfaceD3(a.xy,a.z),SurfaceD3(b.xy,b.z)));
    }
    double best=-influence;
    [loop]for(int m=0;m<_SurfaceDrainageCounts.x;m++)
    {
        double distance=distances[m]>=limit?influence:2*StructureAsin(SurfaceMin(1,SurfaceSqrt(distances[m])*0.5L))*radius;
        best=SurfaceMax(best,DrainageContains(_SurfaceDrainageLandmasses[m],d)?distance:-distance);
    }
    signedDistance=best;return SurfaceFinite(best);
}
bool SurfaceDrainageIncision(double3 input,double currentHeight,double footprint,out double incision)
{
    incision=0;double3 d=SurfaceUnit(input);int first,count;
    if(!DrainageLeaf(false,d,first,count)||first<0||first+count>_SurfaceDrainageIndexCounts.y)return false;
    double radius=SurfaceD(_SurfaceDrainageShape.xy);
    [loop]for(int i=0;i<count;i++)
    {
        SurfaceDrainageSegment segment=_SurfaceDrainageSegments[_SurfaceDrainageReferences[first+i]];
        if(any(d<SurfaceD3(segment.minimumXY,segment.minimumZ))||any(d>SurfaceD3(segment.maximumXY,segment.maximumZ)))continue;
        if(segment.topology.x<0||segment.topology.x>=_SurfaceDrainageCounts.z||segment.topology.y<0||segment.topology.y>=segment.topology.x)return false;
        SurfaceDrainageNode child=_SurfaceDrainageNodes[segment.topology.x],parent=_SurfaceDrainageNodes[segment.topology.y];
        double3 parentDirection=SurfaceD3(parent.directionXY,parent.directionZ),axis=SurfaceD3(child.directionXY,child.directionZ)-parentDirection;
        double lengthSquared=SurfaceDot(axis,axis);if(lengthSquared<=0)return false;
        double t=SurfaceClamp(SurfaceDot(d-parentDirection,axis)/lengthSquared,0,1);
        double3 nearest=DrainageUnitOr(parentDirection+axis*t,parentDirection),delta=d-nearest;
        double distance=SurfaceSqrt(SurfaceDot(delta,delta))*radius;
        double width=SurfaceLerp(SurfaceD(parent.widthDivide.xy),SurfaceD(child.widthDivide.xy),t);if(distance>=2*width)continue;
        double bed=SurfaceLerp(SurfaceD(parent.bedArea.xy),SurfaceD(child.bedArea.xy),t),divide=SurfaceLerp(SurfaceD(parent.widthDivide.zw),SurfaceD(child.widthDivide.zw),t);
        double target=bed+SurfaceMax(0,divide-bed)*StructureSmooth(distance/width);
        double lateral=1-StructureSmooth(distance/width-1),weight=StructureBandWeight(width*4,footprint);
        incision=SurfaceMin(incision,SurfaceMin(0,target-currentHeight)*lateral*weight);
    }
    return SurfaceFinite(incision);
}
#endif
