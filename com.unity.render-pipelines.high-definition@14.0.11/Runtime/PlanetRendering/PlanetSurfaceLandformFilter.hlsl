#ifndef SPACERUNNER_LANDFORM_FILTER_INCLUDED
#define SPACERUNNER_LANDFORM_FILTER_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceLandform.hlsl"
// Shared derived banks. Negative references address captured Full controls.
struct SurfaceLandformFilterLevel {int4 layout;int2 references;uint2 edge;};
StructuredBuffer<SurfaceLandformControl> _SurfaceLandformFilterControls;
StructuredBuffer<SurfaceLandformFilterLevel> _SurfaceLandformFilterLevels;
StructuredBuffer<SurfaceLandformIndexNode> _SurfaceLandformFilterIndex;
StructuredBuffer<int> _SurfaceLandformFilterReferences;
int4 _SurfaceLandformFilterCounts,_SurfaceLandformFilterLayout;

bool SurfaceLandformFilterControlValid(int encoded)
{
    if(encoded>=0)return encoded<_SurfaceLandformFilterCounts.z;
    int original=-(encoded+1);
    return original>=0&&original<_SurfaceLandformCounts.y;
}
SurfaceLandformControl SurfaceLandformFilterControl(int encoded)
{
    // Callers validate the reference before either load. Use one initialized
    // return record; FXC also analyzes the generated return temporary.
    SurfaceLandformControl value=(SurfaceLandformControl)0;
    if(encoded>=0)value=_SurfaceLandformFilterControls[encoded];
    else value=_SurfaceLandformControls[-(encoded+1)];
    return value;
}
bool SurfaceLandformFilteredLevel(double3 direction,int level,out double height)
{
    // One initialized result and one return avoid FXC's return-temporary
    // analysis across the bounded traversal's early exits.
    height=0;bool success=false;
    [unroll]do
    {
        if(level==_SurfaceLandformFilterCounts.y){success=SurfaceLandformHeight(direction,height);break;}
        if(level<0||level>=_SurfaceLandformFilterCounts.w)break;
        // TrySampleLevel normalizes again on CPU, including normalized transfer directions.
        direction=SurfaceUnit(direction);
        SurfaceLandformFilterLevel header=_SurfaceLandformFilterLevels[level];
        if(header.layout.x!=level||header.layout.z<0||header.layout.w<=0||header.layout.z>_SurfaceLandformFilterLayout.x-header.layout.w||
            header.references.x<0||header.references.y<0||header.references.x>_SurfaceLandformFilterLayout.y-header.references.y)break;
        int node=header.layout.y;SurfaceLandformIndexNode leaf=(SurfaceLandformIndexNode)0;bool found=false,valid=true;
        [loop]for(int depth=0;depth<=30;depth++)
        {
            if(node<header.layout.z||node>=header.layout.z+header.layout.w){valid=false;break;}
            leaf=_SurfaceLandformFilterIndex[node];
            if(leaf.children.x<0){found=true;break;}
            if(leaf.children.x>2){valid=false;break;}
            double coordinate=leaf.children.x==0?direction.x:(leaf.children.x==1?direction.y:direction.z);
            node=coordinate<=SurfaceD(leaf.split.xy)?leaf.children.y:leaf.children.z;
        }
        if(!valid||!found||leaf.range.x<header.references.x||leaf.range.y<0||leaf.range.y>96||
            leaf.range.x-header.references.x>header.references.y-leaf.range.y)break;
        double radius=SurfaceD(_SurfaceLandformShape.xy),minimumQ=1;
        [loop]for(int i=0;i<leaf.range.y;i++)
        {
            int encoded=_SurfaceLandformFilterReferences[leaf.range.x+i];
            if(!SurfaceLandformFilterControlValid(encoded)){valid=false;break;}
            SurfaceLandformControl c=SurfaceLandformFilterControl(encoded);
            double support=SurfaceD(c.gradientZSupport.zw);
            double3 offset=(direction-SurfaceD3(c.directionXY,c.directionZHeight))*(radius/support);
            double q=SurfaceDot(offset,offset);
            if(q==0){height=SurfaceD(c.directionZHeight.zw);success=true;break;}
            minimumQ=SurfaceMin(minimumQ,q);
        }
        if(success||!valid||!(minimumQ<1))break;
        double total=0,weighted=0;
        [loop]for(int j=0;j<leaf.range.y;j++)
        {
            int encoded=_SurfaceLandformFilterReferences[leaf.range.x+j];
            if(!SurfaceLandformFilterControlValid(encoded)){valid=false;break;}
            SurfaceLandformControl c=SurfaceLandformFilterControl(encoded);
            double3 offset=(direction-SurfaceD3(c.directionXY,c.directionZHeight))*radius;
            double support=SurfaceD(c.gradientZSupport.zw);double3 relative=offset/support;
            double q=SurfaceDot(relative,relative);if(q>=1)continue;
            double tail=1-q,ratio=minimumQ/q,weight=(tail*tail)*(tail*tail)*(ratio*ratio);
            double term=SurfaceD(c.directionZHeight.zw),variation=SurfaceD(c.variation.xy);
            if(variation>0)
            {
                double x=SurfaceDot(SurfaceD3(c.gradientXY,c.gradientZSupport),offset),scaled=x/variation,square=scaled*scaled;
                if(SurfaceFinite(square))term+=SurfaceAbs(scaled)>1e18L?(x/SurfaceAbs(scaled))/SurfaceSqrt(1+1/square):x/SurfaceSqrt(1+square);
            }
            total+=weight;weighted+=weight*term;
        }
        if(!valid||!(total>0)||!SurfaceFinite(weighted))break;
        height=weighted/total;success=SurfaceFinite(height);
    }while(false);
    return success;
}
bool SurfaceLandformFilteredHeight(double3 input,double footprint,out double height)
{
    height=0;
    if(!SurfaceFinite(footprint)||footprint<0)return false;
    if(footprint==0)return SurfaceLandformHeight(input,height);
    if(_SurfaceLandformFilterCounts.x!=1||_SurfaceLandformFilterLayout.z!=1||_SurfaceLandformCounts.x!=1||
        _SurfaceLandformFilterCounts.y<0||_SurfaceLandformFilterCounts.y>30||
        _SurfaceLandformFilterCounts.w!=_SurfaceLandformFilterCounts.y||
        !SurfaceFinite(input.x)||!SurfaceFinite(input.y)||!SurfaceFinite(input.z)||SurfaceDot(input,input)<=0)return false;
    double radius=SurfaceD(_SurfaceLandformShape.xy);if(!(radius>0))return false;
    int maximumLevel=_SurfaceLandformFilterCounts.y;
    if(maximumLevel==0)return SurfaceLandformHeight(input,height);
    double threshold=radius/(2*(double)(1u<<maximumLevel));
    if(footprint<=threshold)return SurfaceLandformHeight(input,height);
    int upper=maximumLevel;
    [loop]while(upper>0&&footprint>2*threshold){upper--;threshold*=2;}
    double3 direction=SurfaceUnit(input);
    // One dynamic evaluator call in FXC's intermediate representation, rather
    // than separate clones of the complete KD/PU traversal for each band.
    double a=0,b=0;
    int samples=upper>0?2:1;
    [loop]for(int sample=0;sample<samples;sample++)
    {
        double value=0;if(!SurfaceLandformFilteredLevel(direction,upper-sample,value))return false;
        if(sample==0)a=value;else b=value;
    }
    if(upper==0){height=a;return true;}
    double blend=SurfaceClamp(footprint/threshold-1,0,1);blend=blend*blend*(3-2*blend);
    height=SurfaceLerp(a,b,blend);return SurfaceFinite(height);
}
#endif
