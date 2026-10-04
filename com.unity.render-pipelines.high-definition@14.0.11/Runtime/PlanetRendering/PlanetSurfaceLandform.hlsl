#ifndef SPACERUNNER_LANDFORM_FIELD_INCLUDED
#define SPACERUNNER_LANDFORM_FIELD_INCLUDED
// Captured C1 partition of unity. No procedural regeneration or channel-only subtraction.
struct SurfaceLandformControl {uint4 directionXY,directionZHeight,gradientXY,gradientZSupport,variation;};
struct SurfaceLandformIndexNode {uint4 split;int4 children,range;};
StructuredBuffer<SurfaceLandformControl> _SurfaceLandformControls;
StructuredBuffer<SurfaceLandformIndexNode> _SurfaceLandformIndex;
StructuredBuffer<int> _SurfaceLandformReferences;
int4 _SurfaceLandformCounts;
uint4 _SurfaceLandformShape;

bool SurfaceLandformHeight(double3 input,out double height)
{
    height=0;
    if(_SurfaceLandformCounts.x==0||_SurfaceLandformCounts.y<=0||_SurfaceLandformCounts.z<=0||
        !SurfaceFinite(input.x)||!SurfaceFinite(input.y)||!SurfaceFinite(input.z)||SurfaceDot(input,input)<=0)return false;
    double3 direction=SurfaceUnit(input);double radius=SurfaceD(_SurfaceLandformShape.xy);
    if(!(radius>0))return false;
    int node=0;SurfaceLandformIndexNode leaf=(SurfaceLandformIndexNode)0;bool found=false;
    [loop]for(int depth=0;depth<=30;depth++)
    {
        if(node<0||node>=_SurfaceLandformCounts.z)return false;
        leaf=_SurfaceLandformIndex[node];
        if(leaf.children.x<0){found=true;break;}
        if(leaf.children.x>2)return false;
        // DX11 dynamic indexing of a double3 is unreliable; select its scalar explicitly.
        double coordinate=leaf.children.x==0?direction.x:(leaf.children.x==1?direction.y:direction.z);
        node=coordinate<=SurfaceD(leaf.split.xy)?leaf.children.y:leaf.children.z;
    }
    if(!found||leaf.range.x<0||leaf.range.y<0||leaf.range.y>96||leaf.range.x>_SurfaceLandformCounts.w-leaf.range.y)return false;
    double minimumQ=1;
    [loop]for(int i=0;i<leaf.range.y;i++)
    {
        int id=_SurfaceLandformReferences[leaf.range.x+i];if(id<0||id>=_SurfaceLandformCounts.y)return false;
        SurfaceLandformControl c=_SurfaceLandformControls[id];
        double support=SurfaceD(c.gradientZSupport.zw);
        double3 offset=(direction-SurfaceD3(c.directionXY,c.directionZHeight))*(radius/support);
        double q=SurfaceDot(offset,offset);
        if(q==0){height=SurfaceD(c.directionZHeight.zw);return true;}
        minimumQ=SurfaceMin(minimumQ,q);
    }
    if(!(minimumQ<1))return false;
    double total=0,weighted=0;
    [loop]for(int j=0;j<leaf.range.y;j++)
    {
        SurfaceLandformControl c=_SurfaceLandformControls[_SurfaceLandformReferences[leaf.range.x+j]];
        double3 offset=(direction-SurfaceD3(c.directionXY,c.directionZHeight))*radius;
        double support=SurfaceD(c.gradientZSupport.zw);double3 relative=offset/support;
        double q=SurfaceDot(relative,relative);if(q>=1)continue;
        double tail=1-q,ratio=minimumQ/q,weight=(tail*tail)*(tail*tail)*(ratio*ratio);
        double term=SurfaceD(c.directionZHeight.zw),variation=SurfaceD(c.variation.xy);
        if(variation>0)
        {
            double x=SurfaceDot(SurfaceD3(c.gradientXY,c.gradientZSupport),offset),scaled=x/variation;
            double square=scaled*scaled;
            // Preserve CPU's finite result even when the saturating term's denominator
            // overflows; avoid a float seed for otherwise finite very large square roots.
            if(SurfaceFinite(square))
                term+=SurfaceAbs(scaled)>1e18L?(x/SurfaceAbs(scaled))/SurfaceSqrt(1+1/square):x/SurfaceSqrt(1+square);
        }
        total+=weight;weighted+=weight*term;
    }
    if(!(total>0)||!SurfaceFinite(weighted))return false;
    height=weighted/total;return SurfaceFinite(height);
}
#endif
