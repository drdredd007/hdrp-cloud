#ifndef SPACERUNNER_CANONICAL_HEIGHT_INCLUDED
#define SPACERUNNER_CANONICAL_HEIGHT_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfacePrecision.hlsl"
// Canonical cube height addressing is shared by mesh generation and shoreline classification.
struct SurfaceGpuTile {int4 address;int4 layout;};
StructuredBuffer<SurfaceGpuTile> _SurfaceTiles;
StructuredBuffer<float> _SurfaceHeights;
int _SurfaceTileCount,_SurfaceCanonicalLevel;
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
bool SurfaceCanonicalHeight(double3 input,out double3 unit,out int face,out double a,out double b,out double height)
{
    height=0;unit=SurfaceUnit(input);double3 m=double3(SurfaceAbs(unit.x),SurfaceAbs(unit.y),SurfaceAbs(unit.z));
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
    return true;
}
#endif
