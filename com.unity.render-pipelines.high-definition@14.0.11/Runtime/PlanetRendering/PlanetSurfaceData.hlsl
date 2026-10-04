#ifndef SPACERUNNER_SURFACE_DATA_INCLUDED
#define SPACERUNNER_SURFACE_DATA_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfacePrecision.hlsl"
// Immutable height data shared with NativeSurfaceView. Double addresses retain metre detail
// on large planets; the SM5 kernel capability is checked before any native dispatch.
struct SurfaceGpuTile {int4 address;int4 layout;};
struct SurfaceGpuRegion
{
    uint4 anchorXY,anchorZ,rightXY,rightZ,forwardXY,forwardZ;
    uint4 minimum,maximum,physical;
    int4 layout,flags;
};
struct SurfaceGpuStamp {uint4 centerXY,centerZ,shape0,shape1;};
StructuredBuffer<SurfaceGpuTile> _SurfaceTiles;
StructuredBuffer<float> _SurfaceHeights,_SurfaceRegionHeights,_SurfaceRegionMasks;
StructuredBuffer<SurfaceGpuRegion> _SurfaceRegions;
StructuredBuffer<SurfaceGpuStamp> _SurfaceStamps;
StructuredBuffer<float4> _SurfaceMaterialWeights,_SurfaceErosionData,_SurfaceRegionMaterialWeights,_SurfaceRegionErosionData;
StructuredBuffer<int2> _SurfaceRegionFilterRanges;
StructuredBuffer<int4> _SurfaceRegionFilterMips;
StructuredBuffer<float2> _SurfaceRegionFilterSamples;
int _SurfaceRegionFilterReady;
struct PlanetSurfaceVertexAttributes {float4 materialWeights;float4 erosionData;uint channels;};
int _SurfaceTileCount,_SurfaceCanonicalLevel,_SurfaceRegionCount,_SurfaceStampCount,_SurfaceDetailSeed;
uint4 _SurfaceRadiusAndNormal,_SurfaceDetailShape;
uint4 _SurfaceFrameRightXY,_SurfaceFrameRightZ,_SurfaceFrameUpXY,_SurfaceFrameUpZ;
uint4 _SurfaceFrameForwardXY,_SurfaceFrameForwardZ,_SurfaceFrameRadialXY,_SurfaceFrameRadialZ;
uint4 _SurfaceFrameShape,_SurfaceCellSize;
double SurfaceD(uint2 bits){return asdouble(bits.x,bits.y);}
double3 SurfaceD3(uint4 xy,uint4 z){return double3(SurfaceD(xy.xy),SurfaceD(xy.zw),SurfaceD(z.xy));}
double SurfaceDot(double3 a,double3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
double3 SurfaceCross(double3 a,double3 b){return double3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
double SurfaceAbs(double x){return x<0?-x:x;}
double SurfaceMin(double a,double b){return a<b?a:b;}
double SurfaceMax(double a,double b){return a>b?a:b;}
double SurfaceClamp(double x,double lo,double hi){return SurfaceMin(hi,SurfaceMax(lo,x));}
bool SurfaceFinite(double x){return x==x&&SurfaceAbs(x)<=asdouble(0xffffffffu,0x7fefffffu);}
double SurfaceSqrt(double x)
{
    if(x<=0)return 0;
    double y=(double)sqrt((float)x);
    y=(y+x/y)*0.5;y=(y+x/y)*0.5;y=(y+x/y)*0.5;return y;
}
double3 SurfaceUnit(double3 x)
{
    double scale=SurfaceMax(SurfaceAbs(x.x),SurfaceMax(SurfaceAbs(x.y),SurfaceAbs(x.z)));
    double3 s=x/scale;return s/SurfaceSqrt(SurfaceDot(s,s));
}
double SurfaceLerp(double a,double b,double t){return a+(b-a)*t;}
double3 SurfaceCubeDirection(int face,int level,int x,int y,double u,double v)
{
    double count=(double)(1u<<level),a=2*((double)x+u)/count-1,b=2*((double)y+v)/count-1;
    double3 p;
    if(face==0)p=double3(1,b,-a);else if(face==1)p=double3(-1,b,a);
    else if(face==2)p=double3(a,1,-b);else if(face==3)p=double3(a,-1,b);
    else if(face==4)p=double3(a,b,1);else p=double3(-a,b,-1);
    return SurfaceUnit(p);
}
double SurfaceBilinear(StructuredBuffer<float> data,int offset,int2 resolution,double2 uv)
{
    double gx=SurfaceClamp(uv.x,0,1)*(double)resolution.x,gy=SurfaceClamp(uv.y,0,1)*(double)resolution.y;
    int x=(int)SurfaceMin((double)(resolution.x-1),gx),y=(int)SurfaceMin((double)(resolution.y-1),gy);
    double tx=gx-(double)x,ty=gy-(double)y;int row=resolution.x+1,index=offset+y*row+x;
    return SurfaceLerp(SurfaceLerp((double)data[index],(double)data[index+1],tx),SurfaceLerp((double)data[index+row],(double)data[index+row+1],tx),ty);
}
// The CPU value-noise hash uses ulong. SM5 uses exact two-word unsigned arithmetic.
uint2 SurfaceShift64(uint2 x,uint n){return uint2((x.x>>n)|(x.y<<(32u-n)),x.y>>n);}
uint2 SurfaceMul64(uint2 a,uint2 b)
{
    // Portable SM5 multiplication: the shader compiler does not expose GLSL's
    // umulExtended. Four 16-bit products retain the exact low/high uint words.
    uint a0=a.x&65535u,a1=a.x>>16u,b0=b.x&65535u,b1=b.x>>16u;
    uint p0=a0*b0,p1=a0*b1,p2=a1*b0,p3=a1*b1;
    uint middle=(p0>>16u)+(p1&65535u)+(p2&65535u);
    uint lo=(p0&65535u)|(middle<<16u);
    uint hi=p3+(p1>>16u)+(p2>>16u)+(middle>>16u);
    return uint2(lo,hi+a.x*b.y+a.y*b.x);
}
uint2 SurfaceMix64(uint2 x)
{
    x^=SurfaceShift64(x,30u);x=SurfaceMul64(x,uint2(0x1ce4e5b9u,0xbf58476du));
    x^=SurfaceShift64(x,27u);x=SurfaceMul64(x,uint2(0x133111ebu,0x94d049bbu));return x^SurfaceShift64(x,31u);
}
uint2 SurfaceAdd64(uint2 x,uint step){uint lo=x.x+step;return uint2(lo,x.y+(lo<x.x?1u:0u));}
bool SurfaceCell(double p,out uint2 cell,out double fraction)
{
    cell=0;fraction=0;
    if(!SurfaceFinite(p)||SurfaceAbs(p)>asdouble(0xfffffffcu,0x432fffffu))return false;
    double magnitude=SurfaceAbs(p),high=(double)(uint)(magnitude/4294967296.0);
    double remainder=magnitude-high*4294967296.0;uint low=(uint)remainder;
    fraction=remainder-(double)low;cell=uint2(low,(uint)high);
    if(p<0)
    {
        if(fraction>0)cell=SurfaceAdd64(cell,1u);
        uint lo=0u-cell.x;cell=uint2(lo,0u-cell.y-(cell.x!=0u?1u:0u));
        if(fraction>0)fraction=1-fraction;
    }
    return true;
}
double SurfaceValue(uint2 x,uint2 y,uint2 z)
{
    uint2 h=SurfaceMix64(x^uint2(0x7f4a7c15u,0x9e3779b9u));h=SurfaceMix64(h^y);h=SurfaceMix64(h^z);h=SurfaceMix64(h^uint2((uint)_SurfaceDetailSeed,0u));
    h=SurfaceShift64(h,11u);return (((double)h.y*4294967296.0+(double)h.x)/9007199254740992.0)*2-1;
}
bool SurfaceDetail(double3 unit,out double value)
{
    value=0;double amplitude=SurfaceD(_SurfaceDetailShape.zw);if(amplitude==0)return true;
    double wavelength=SurfaceD(_SurfaceDetailShape.xy),radius=SurfaceD(_SurfaceRadiusAndNormal.xy);
    double3 p=unit*(radius/wavelength);uint2 x,y,z;double tx,ty,tz;
    if(!SurfaceCell(p.x,x,tx)||!SurfaceCell(p.y,y,ty)||!SurfaceCell(p.z,z,tz))return false;
    tx=tx*tx*tx*(tx*(tx*6-15)+10);ty=ty*ty*ty*(ty*(ty*6-15)+10);tz=tz*tz*tz*(tz*(tz*6-15)+10);
    uint2 xx=SurfaceAdd64(x,1u),yy=SurfaceAdd64(y,1u),zz=SurfaceAdd64(z,1u);
    double a=SurfaceLerp(SurfaceValue(x,y,z),SurfaceValue(xx,y,z),tx),b=SurfaceLerp(SurfaceValue(x,yy,z),SurfaceValue(xx,yy,z),tx);
    double c=SurfaceLerp(SurfaceValue(x,y,zz),SurfaceValue(xx,y,zz),tx),d=SurfaceLerp(SurfaceValue(x,yy,zz),SurfaceValue(xx,yy,zz),tx);
    value=SurfaceLerp(SurfaceLerp(a,b,ty),SurfaceLerp(c,d,ty),tz)*amplitude;return true;
}
double SurfaceAsinLow(double x)
{
    double sum=x,term=x,square=x*x;
    [unroll]for(int n=1;n<=32;n++){double odd=(double)(2*n-1);term*=square*odd*odd/((double)(2*n)*(double)(2*n+1));sum+=term;}
    return sum;
}
double SurfaceAsin(double x)
{
    x=SurfaceClamp(x,0,1);
    return x<=0.5?SurfaceAsinLow(x):asdouble(0x54442d18u,0x3ff921fbu)-2*SurfaceAsinLow(SurfaceSqrt((1-x)*0.5));
}
#if !defined(PLANET_SURFACE_NO_STRUCTURE)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceStructural.hlsl"
#else
// Automatic material/feather rules share this polynomial; no structural buffers
// are declared in a plain or Orogen family.
double StructureSmooth(double x) { x=SurfaceClamp(x,0,1); return x*x*(3-2*x); }
#endif
#if !defined(PLANET_SURFACE_NO_OROGEN)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceOrogenDetail.hlsl"
#endif
double SurfaceDetailBandWeight(double footprint)
{
    double support=footprint>0?footprint:1.0L;
    if(footprint<=0)return 1;
    double t=SurfaceClamp((SurfaceD(_SurfaceDetailShape.xy)/support-2)*0.5,0,1);
    return t*t*(3-2*t);
}
double2 SurfaceRegionFullPair(SurfaceGpuRegion region,double2 metres,double2 minimum,double2 maximum)
{
    double2 result=double2(0,0);
    if(metres.x>=minimum.x&&metres.y>=minimum.y&&metres.x<=maximum.x&&metres.y<=maximum.y)
    {
        double2 uv=(metres-minimum)/(maximum-minimum);
        double value=SurfaceBilinear(_SurfaceRegionHeights,region.layout.z,region.layout.xy,uv);
        double alpha=SurfaceClamp(SurfaceBilinear(_SurfaceRegionMasks,region.layout.w,region.layout.xy,uv),0,1),blend=SurfaceD(region.physical.zw);
        if(blend>0)
        {
            double border=SurfaceMin(SurfaceMin(metres.x-minimum.x,maximum.x-metres.x),SurfaceMin(metres.y-minimum.y,maximum.y-metres.y));
            double t=SurfaceClamp(border/blend,0,1);alpha*=t*t*(3-2*t);
        }
        result=double2(value*alpha,alpha);
    }
    return result;
}
double2 SurfaceRegionMipPair(int4 mip,double2 metres,double2 minimum,double2 maximum)
{
    double2 result=double2(0,0);
    double2 grid=(metres-minimum)/(maximum-minimum)*(double2)mip.xy+2;
    if(grid.x>=0&&grid.y>=0&&grid.x<=(double)(mip.x+4)&&grid.y<=(double)(mip.y+4))
    {
        int x=(int)SurfaceMin((double)(mip.x+3),grid.x),y=(int)SurfaceMin((double)(mip.y+3),grid.y),row=mip.x+5,index=mip.z+y*row+x;
        double tx=grid.x-(double)x,ty=grid.y-(double)y;
        float2 a=_SurfaceRegionFilterSamples[index],b=_SurfaceRegionFilterSamples[index+1],c=_SurfaceRegionFilterSamples[index+row],d=_SurfaceRegionFilterSamples[index+row+1];
        result=double2(SurfaceLerp(SurfaceLerp((double)a.x,(double)b.x,tx),SurfaceLerp((double)c.x,(double)d.x,tx),ty),
            SurfaceLerp(SurfaceLerp((double)a.y,(double)b.y,tx),SurfaceLerp((double)c.y,(double)d.y,tx),ty));
    }
    return result;
}
double2 SurfaceRegionFilteredPair(int regionIndex,SurfaceGpuRegion region,double2 metres,double2 minimum,double2 maximum,double footprint)
{
    double2 result=double2(0,0);
    int2 range=_SurfaceRegionFilterRanges[regionIndex];
    if(range.y==0)result=SurfaceRegionFullPair(region,metres,minimum,maximum);
    else
    {
        double2 spacing=(maximum-minimum)/(double2)region.layout.xy,relative=metres/SurfaceD(region.physical.xy);
        double projectionScale=1+relative.x*relative.x+relative.y*relative.y;
        double ratio=SurfaceMax(1,footprint*projectionScale/SurfaceMin(spacing.x,spacing.y));
        double lod=SurfaceRegionalFilterLod(ratio,range.y);
        int lower=(int)lod,upper=min(range.y,lower+1);
        double2 a=double2(0,0),b=double2(0,0);
        if(lower==0)a=SurfaceRegionFullPair(region,metres,minimum,maximum);
        else a=SurfaceRegionMipPair(_SurfaceRegionFilterMips[range.x+lower-1],metres,minimum,maximum);
        if(upper==0)b=SurfaceRegionFullPair(region,metres,minimum,maximum);
        else b=SurfaceRegionMipPair(_SurfaceRegionFilterMips[range.x+upper-1],metres,minimum,maximum);
        double t=lod-(double)lower;result=double2(SurfaceLerp(a.x,b.x,t),SurfaceLerp(a.y,b.y,t));
    }
    return result;
}
bool SurfaceNativeHeightFiltered(double3 input,double footprint,out double height)
{
    height=0;double3 unit=SurfaceUnit(input),m=double3(SurfaceAbs(unit.x),SurfaceAbs(unit.y),SurfaceAbs(unit.z));int face;double a,b;
    if(m.x>=m.y&&m.x>=m.z){if(unit.x>0){face=0;a=-unit.z/m.x;b=unit.y/m.x;}else{face=1;a=unit.z/m.x;b=unit.y/m.x;}}
    else if(m.y>=m.z){if(unit.y>0){face=2;a=unit.x/m.y;b=-unit.z/m.y;}else{face=3;a=unit.x/m.y;b=unit.z/m.y;}}
    else{if(unit.z>0){face=4;a=unit.x/m.z;b=unit.y/m.z;}else{face=5;a=-unit.x/m.z;b=unit.y/m.z;}}
    double count=(double)(1u<<_SurfaceCanonicalLevel),gx=SurfaceClamp((a+1)*0.5,0,1)*count,gy=SurfaceClamp((b+1)*0.5,0,1)*count;
    int x=(int)SurfaceMin(count-1,gx),y=(int)SurfaceMin(count-1,gy);int low=0,high=_SurfaceTileCount-1,found=-1;
    [loop]while(low<=high)
    {
        int mid=low+((high-low)>>1);int4 key=_SurfaceTiles[mid].address;
        int comparison=key.x!=face?(key.x<face?-1:1):key.y!=_SurfaceCanonicalLevel?(key.y<_SurfaceCanonicalLevel?-1:1):key.w!=y?(key.w<y?-1:1):key.z!=x?(key.z<x?-1:1):0;
        if(comparison==0){found=mid;break;}if(comparison<0)low=mid+1;else high=mid-1;
    }
    if(found<0)return false;
    SurfaceGpuTile tile=_SurfaceTiles[found];height=SurfaceBilinear(_SurfaceHeights,tile.layout.y,tile.layout.xx,double2(gx-(double)x,gy-(double)y));
#if !defined(PLANET_SURFACE_NO_STRUCTURE)
    if(_SurfaceStructureCounts.x!=0)height+=SurfaceStructureBand(unit,face,double2((a+1)*0.5,(b+1)*0.5),footprint);
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY) && !defined(PLANET_SURFACE_STRUCTURE_LANDFORM_ONLY)
    if(_SurfaceStructureCounts.x==2)
    {double incision;if(!SurfaceDrainageIncision(unit,height,footprint,incision))return false;height+=incision;}
#endif
#endif
    double intrinsicDetail=0;
#if !defined(PLANET_SURFACE_NO_OROGEN)
    if(!SurfaceOrogenDetail(unit,height,footprint,intrinsicDetail))return false;
#endif
    height+=intrinsicDetail;
    double detailWeight=SurfaceDetailBandWeight(footprint);
    [loop]for(int heightRegionIndex=0;heightRegionIndex<_SurfaceRegionCount;heightRegionIndex++)
    {
        SurfaceGpuRegion region=_SurfaceRegions[heightRegionIndex];double3 anchor=SurfaceD3(region.anchorXY,region.anchorZ);
        double cosine=SurfaceDot(unit,anchor);if(cosine<=0)continue;
        double2 metres=double2(SurfaceDot(unit,SurfaceD3(region.rightXY,region.rightZ)),SurfaceDot(unit,SurfaceD3(region.forwardXY,region.forwardZ)))*(SurfaceD(region.physical.xy)/cosine);
        double2 minimum=double2(SurfaceD(region.minimum.xy),SurfaceD(region.minimum.zw)),maximum=double2(SurfaceD(region.maximum.xy),SurfaceD(region.maximum.zw));
        if(footprint>0&&_SurfaceRegionFilterReady!=0)
        {
            double2 pair=SurfaceRegionFilteredPair(heightRegionIndex,region,metres,minimum,maximum,footprint);
            if(region.flags.x==0){height=height*(1-pair.y)+pair.x;intrinsicDetail*=1-pair.y;}
            else{height+=pair.x;if(region.flags.y==1){height-=intrinsicDetail*pair.y;intrinsicDetail*=1-pair.y;}}
            if(region.flags.y==1)detailWeight*=1-pair.y;
            continue;
        }
        if(metres.x<minimum.x||metres.y<minimum.y||metres.x>maximum.x||metres.y>maximum.y)continue;
        double2 uv=(metres-minimum)/(maximum-minimum);
        double value=SurfaceBilinear(_SurfaceRegionHeights,region.layout.z,region.layout.xy,uv),weight=SurfaceClamp(SurfaceBilinear(_SurfaceRegionMasks,region.layout.w,region.layout.xy,uv),0,1);
        double blend=SurfaceD(region.physical.zw);
        if(blend>0)
        {
            double border=SurfaceMin(SurfaceMin(metres.x-minimum.x,maximum.x-metres.x),SurfaceMin(metres.y-minimum.y,maximum.y-metres.y));
            double t=SurfaceClamp(border/blend,0,1);weight*=t*t*(3-2*t);
        }
        if(region.flags.x==0){height=SurfaceLerp(height,value,weight);intrinsicDetail*=1-weight;}
        else{height+=value*weight;if(region.flags.y==1){height-=intrinsicDetail*weight;intrinsicDetail*=1-weight;}}
        if(region.flags.y==1)detailWeight*=1-weight;
    }
    if(detailWeight>0){double detail;if(!SurfaceDetail(unit,detail))return false;height+=detail*detailWeight;}
    double radius=SurfaceD(_SurfaceRadiusAndNormal.xy);
    [loop]for(int j=0;j<_SurfaceStampCount;j++)
    {
        SurfaceGpuStamp stamp=_SurfaceStamps[j];double3 delta=unit-SurfaceD3(stamp.centerXY,stamp.centerZ);
        double distance=2*SurfaceAsin(SurfaceSqrt(SurfaceDot(delta,delta))*0.5)*radius;
        double bowlRadius=SurfaceD(stamp.shape0.xy),depth=SurfaceD(stamp.shape0.zw),rimWidth=SurfaceD(stamp.shape1.xy),rimHeight=SurfaceD(stamp.shape1.zw);
        if(distance<bowlRadius){double t=distance/bowlRadius,bowl=1-t*t;height-=depth*bowl*bowl;}
        else if(rimWidth>0&&distance<bowlRadius+rimWidth){double t=(distance-bowlRadius)/rimWidth;height+=rimHeight*16*t*t*(1-t)*(1-t);}
    }
    return SurfaceFinite(height)&&height>-radius;
}
bool SurfaceNativeHeight(double3 input,out double height)
{return SurfaceNativeHeightFiltered(input,0,height);}

float4 SurfaceBilinear4(StructuredBuffer<float4> data,int offset,int2 resolution,double2 uv)
{
    double2 grid=double2(SurfaceClamp(uv.x,0,1),SurfaceClamp(uv.y,0,1))*(double2)resolution;
    int x=(int)SurfaceMin((double)(resolution.x-1),grid.x),y=(int)SurfaceMin((double)(resolution.y-1),grid.y);
    float2 f=(float2)(grid-double2(x,y));int row=resolution.x+1,index=offset+y*row+x;
    return lerp(lerp(data[index],data[index+1],f.x),lerp(data[index+row],data[index+row+1],f.x),f.y);
}
struct SurfaceAutomaticProfile { int4 flags; uint4 temperature,lapseNormal,rock,snow; };
StructuredBuffer<SurfaceAutomaticProfile> _SurfaceAutomaticProfile,_SurfaceRegionalProfiles;
StructuredBuffer<int> _SurfaceTileMaterialProvenance;
int _SurfaceDynamicMaterials,_SurfaceDynamicMaterialStyle;
uint4 _SurfaceDynamicSeaLevel;
bool SurfaceNativeNormalAtMetres(double3 input,double footprint,double metres,out double3 normal);
double SurfaceProfileSmooth(double start,double end,double x) { return StructureSmooth((x-start)/(end-start)); }
float4 SurfaceEvaluateProfile(SurfaceAutomaticProfile profile,double3 unit,double height,double slope,double wetness)
{
    double sea=SurfaceD(_SurfaceDynamicSeaLevel.xy),latitude=SurfaceAbs(unit.y),altitude=SurfaceMax(0,height-sea);
    double temperature=SurfaceLerp(SurfaceD(profile.temperature.xy),SurfaceD(profile.temperature.zw),SurfaceSqrt(latitude)*SurfaceSqrt(SurfaceSqrt(latitude)))-altitude/1000*SurfaceD(profile.lapseNormal.xy);
    double rock=SurfaceProfileSmooth(SurfaceD(profile.rock.xy),SurfaceD(profile.rock.zw),slope);
    double snow=_SurfaceDynamicMaterialStyle==2?0:1-SurfaceProfileSmooth(SurfaceD(profile.snow.xy),SurfaceD(profile.snow.zw),temperature);
    wetness=_SurfaceDynamicMaterialStyle==2?0:SurfaceClamp(wetness,0,1);
    double sand=_SurfaceDynamicMaterialStyle==2?(1-rock)*0.35L:(1-wetness)*(1-snow)*(1-rock);
    double grass=_SurfaceDynamicMaterialStyle==2?0:wetness*(1-snow)*(1-rock);
    if(_SurfaceDynamicMaterialStyle==2)rock=1-sand;
    double total=grass+sand+rock+snow;return total<=0?float4(0,0,1,0):(float4)(double4(grass,sand,rock,snow)/total);
}
bool SurfaceProfileRegionWeight(int i,double3 unit,out double2 uv,out double weight)
{
    uv=0;weight=0;SurfaceGpuRegion region=_SurfaceRegions[i];
    double cosine=SurfaceDot(unit,SurfaceD3(region.anchorXY,region.anchorZ));if(cosine<=0)return false;
    double2 metres=double2(SurfaceDot(unit,SurfaceD3(region.rightXY,region.rightZ)),SurfaceDot(unit,SurfaceD3(region.forwardXY,region.forwardZ)))*(SurfaceD(region.physical.xy)/cosine);
    double2 minimum=double2(SurfaceD(region.minimum.xy),SurfaceD(region.minimum.zw)),maximum=double2(SurfaceD(region.maximum.xy),SurfaceD(region.maximum.zw));
    if(metres.x<minimum.x||metres.y<minimum.y||metres.x>maximum.x||metres.y>maximum.y)return false;
    uv=(metres-minimum)/(maximum-minimum);weight=SurfaceClamp(SurfaceBilinear(_SurfaceRegionMasks,region.layout.w,region.layout.xy,uv),0,1);
    double blend=SurfaceD(region.physical.zw);
    if(blend>0)weight*=StructureSmooth(SurfaceMin(SurfaceMin(metres.x-minimum.x,maximum.x-metres.x),SurfaceMin(metres.y-minimum.y,maximum.y-metres.y))/blend);
    return weight>0;
}
PlanetSurfaceVertexAttributes SurfaceNativeAttributesForGeometry(double3 input,double footprint,bool geometryReady,double geometryHeight,double3 geometryNormal)
{
    PlanetSurfaceVertexAttributes result=(PlanetSurfaceVertexAttributes)0;
    double3 unit=SurfaceUnit(input),m=double3(SurfaceAbs(unit.x),SurfaceAbs(unit.y),SurfaceAbs(unit.z));int face;double a,b;
    if(m.x>=m.y&&m.x>=m.z){if(unit.x>0){face=0;a=-unit.z/m.x;b=unit.y/m.x;}else{face=1;a=unit.z/m.x;b=unit.y/m.x;}}
    else if(m.y>=m.z){if(unit.y>0){face=2;a=unit.x/m.y;b=-unit.z/m.y;}else{face=3;a=unit.x/m.y;b=unit.z/m.y;}}
    else{if(unit.z>0){face=4;a=unit.x/m.z;b=unit.y/m.z;}else{face=5;a=-unit.x/m.z;b=unit.y/m.z;}}
    double count=(double)(1u<<_SurfaceCanonicalLevel),gx=SurfaceClamp((a+1)*0.5,0,1)*count,gy=SurfaceClamp((b+1)*0.5,0,1)*count;
    int x=(int)SurfaceMin(count-1,gx),y=(int)SurfaceMin(count-1,gy),low=0,high=_SurfaceTileCount-1,found=-1;
    [loop]while(low<=high)
    {
        int mid=low+((high-low)>>1);int4 key=_SurfaceTiles[mid].address;
        int comparison=key.x!=face?(key.x<face?-1:1):key.y!=_SurfaceCanonicalLevel?(key.y<_SurfaceCanonicalLevel?-1:1):key.w!=y?(key.w<y?-1:1):key.z!=x?(key.z<x?-1:1):0;
        if(comparison==0){found=mid;break;}if(comparison<0)low=mid+1;else high=mid-1;
    }
    if(found<0)return result;
    SurfaceGpuTile tile=_SurfaceTiles[found];result.channels=(uint)tile.layout.w;
    double2 uv=double2(gx-(double)x,gy-(double)y);
    if((result.channels&1u)!=0)result.materialWeights=SurfaceBilinear4(_SurfaceMaterialWeights,tile.layout.z,tile.layout.xx,uv);
    if((result.channels&2u)!=0)result.erosionData=SurfaceBilinear4(_SurfaceErosionData,tile.layout.z,tile.layout.xx,uv);
    [loop]for(int erosionRegionIndex=0;erosionRegionIndex<_SurfaceRegionCount;erosionRegionIndex++)
    {
        SurfaceGpuRegion region=_SurfaceRegions[erosionRegionIndex];if((region.flags.z&3)==0)continue;
        double cosine=SurfaceDot(unit,SurfaceD3(region.anchorXY,region.anchorZ));if(cosine<=0)continue;
        double2 metres=double2(SurfaceDot(unit,SurfaceD3(region.rightXY,region.rightZ)),SurfaceDot(unit,SurfaceD3(region.forwardXY,region.forwardZ)))*(SurfaceD(region.physical.xy)/cosine);
        double2 minimum=double2(SurfaceD(region.minimum.xy),SurfaceD(region.minimum.zw)),maximum=double2(SurfaceD(region.maximum.xy),SurfaceD(region.maximum.zw));
        if(metres.x<minimum.x||metres.y<minimum.y||metres.x>maximum.x||metres.y>maximum.y)continue;
        double2 regionUv=(metres-minimum)/(maximum-minimum);
        double weight=SurfaceClamp(SurfaceBilinear(_SurfaceRegionMasks,region.layout.w,region.layout.xy,regionUv),0,1),blend=SurfaceD(region.physical.zw);
        if(blend>0)
        {
            double border=SurfaceMin(SurfaceMin(metres.x-minimum.x,maximum.x-metres.x),SurfaceMin(metres.y-minimum.y,maximum.y-metres.y));
            double t=SurfaceClamp(border/blend,0,1);weight*=t*t*(3-2*t);
        }
        if(_SurfaceDynamicMaterials==0&&(region.flags.z&1)!=0)
        {
            float4 imported=SurfaceBilinear4(_SurfaceRegionMaterialWeights,region.flags.w,region.layout.xy,regionUv);
            if((result.channels&1u)!=0)result.materialWeights=lerp(result.materialWeights,imported,(float)weight);
            else if(weight>=1){result.materialWeights=imported;result.channels|=1u;}
        }
        if((region.flags.z&2)!=0)
        {
            float4 imported=SurfaceBilinear4(_SurfaceRegionErosionData,region.flags.w,region.layout.xy,regionUv);
            if((result.channels&2u)!=0)result.erosionData=lerp(result.erosionData,imported,(float)weight);
            else if(weight>=1){result.erosionData=imported;result.channels|=2u;}
        }
    }
    if(_SurfaceDynamicMaterials!=0)
    {
        SurfaceAutomaticProfile profile=_SurfaceAutomaticProfile[0];double height=geometryHeight;double3 normal=geometryNormal;
        double profileStep=SurfaceMax(SurfaceD(profile.lapseNormal.zw),footprint*0.5);
        double geometryStep=SurfaceMax(SurfaceD(_SurfaceRadiusAndNormal.zw),footprint*0.5);
        if(!geometryReady&&!SurfaceNativeHeightFiltered(unit,footprint,height))return (PlanetSurfaceVertexAttributes)0;
        if((!geometryReady||profileStep!=geometryStep)&&!SurfaceNativeNormalAtMetres(unit,footprint,SurfaceD(profile.lapseNormal.zw),normal))return (PlanetSurfaceVertexAttributes)0;
        double cosine=SurfaceClamp(SurfaceDot(normal,unit),1e-12L,1),slope=SurfaceSqrt(SurfaceMax(0,1-cosine*cosine))/cosine;
        double wetness=(result.channels&2u)!=0?(double)result.erosionData.y:0;
        float4 weights=SurfaceEvaluateProfile(profile,unit,height,slope,wetness);
        [loop]for(int profileRegionIndex=0;profileRegionIndex<_SurfaceRegionCount;profileRegionIndex++)
        {
            SurfaceAutomaticProfile local=_SurfaceRegionalProfiles[profileRegionIndex];if(local.flags.x==0)continue;
            double2 localUv=0;double weight=0;if(!SurfaceProfileRegionWeight(profileRegionIndex,unit,localUv,weight))continue;
            double localSlope=slope;
            if(SurfaceD(local.lapseNormal.zw)!=SurfaceD(profile.lapseNormal.zw))
            {
                if(!SurfaceNativeNormalAtMetres(unit,footprint,SurfaceD(local.lapseNormal.zw),normal))return (PlanetSurfaceVertexAttributes)0;
                cosine=SurfaceClamp(SurfaceDot(normal,unit),1e-12L,1);localSlope=SurfaceSqrt(SurfaceMax(0,1-cosine*cosine))/cosine;
            }
            weights=lerp(weights,SurfaceEvaluateProfile(local,unit,height,localSlope,wetness),(float)weight);
        }
        if((result.channels&1u)!=0&&_SurfaceTileMaterialProvenance[found]==2)weights=result.materialWeights;
        [loop]for(int authoredRegionIndex=0;authoredRegionIndex<_SurfaceRegionCount;authoredRegionIndex++)
        {
            SurfaceGpuRegion region=_SurfaceRegions[authoredRegionIndex];if((region.flags.z&1)==0)continue;
            double2 localUv=0;double weight=0;if(!SurfaceProfileRegionWeight(authoredRegionIndex,unit,localUv,weight))continue;
            weights=lerp(weights,SurfaceBilinear4(_SurfaceRegionMaterialWeights,region.flags.w,region.layout.xy,localUv),(float)weight);
        }
        result.materialWeights=weights;result.channels|=1u;
    }
    if((result.channels&1u)!=0)result.materialWeights/=dot(result.materialWeights,float4(1,1,1,1));
    return result;
}
PlanetSurfaceVertexAttributes SurfaceNativeAttributes(double3 input)
{return SurfaceNativeAttributesForGeometry(input,0,false,0,double3(0,0,0));}
PlanetSurfaceVertexAttributes SurfaceNativeRenderAttributes(double3 input,double footprint,double height,double3 normal)
{return SurfaceNativeAttributesForGeometry(input,footprint,true,height,normal);}
bool SurfaceNativeNormalAtMetres(double3 input,double footprint,double metres,out double3 normal)
{
    normal=0;double3 unit=SurfaceUnit(input);double radius=SurfaceD(_SurfaceRadiusAndNormal.xy);
    metres=SurfaceMax(metres,footprint*0.5);
    if(metres>radius*0.25)return false;
    double3 hint=SurfaceAbs(unit.y)<0.9?double3(0,1,0):double3(1,0,0),tangent=SurfaceUnit(SurfaceCross(hint,unit)),bitangent=SurfaceCross(unit,tangent);
    double angle=metres/radius,square=angle*angle;
    double sine=angle*(1-square/6+square*square/120-square*square*square/5040+square*square*square*square/362880);
    double cosine=1-square/2+square*square/24-square*square*square/720+square*square*square*square/40320-square*square*square*square*square/3628800;
    double3 a=unit*cosine+tangent*sine,b=unit*cosine-tangent*sine,c=unit*cosine+bitangent*sine,d=unit*cosine-bitangent*sine;
    double ha=0,hb=0,hc=0,hd=0;
    // Retain the a/b/c/d evaluation order without cloning the complete structural
    // field four times in FXC's intermediate representation.
    [loop]for(int normalSampleIndex=0;normalSampleIndex<4;normalSampleIndex++)
    {
        double3 sampleDirection=normalSampleIndex==0?a:normalSampleIndex==1?b:normalSampleIndex==2?c:d;
        double sampleHeight=0;if(!SurfaceNativeHeightFiltered(sampleDirection,footprint,sampleHeight))return false;
        if(normalSampleIndex==0)ha=sampleHeight;else if(normalSampleIndex==1)hb=sampleHeight;
        else if(normalSampleIndex==2)hc=sampleHeight;else hd=sampleHeight;
    }
    double3 along=(a-b)*radius+a*ha-b*hb,across=(c-d)*radius+c*hc-d*hd;
    normal=SurfaceUnit(SurfaceCross(along,across));return true;
}
bool SurfaceNativeNormalFiltered(double3 input,double footprint,out float3 normal)
{
    double3 precise=double3(0,0,0);bool ready=SurfaceNativeNormalAtMetres(input,footprint,SurfaceD(_SurfaceRadiusAndNormal.zw),precise);normal=(float3)precise;return ready;
}
bool SurfaceNativeNormal(double3 input,out float3 normal)
{return SurfaceNativeNormalFiltered(input,0,normal);}
#endif
