#ifndef SPACERUNNER_OROGEN_DETAIL_INCLUDED
#define SPACERUNNER_OROGEN_DETAIL_INCLUDED
// Version-one intrinsic synthesis. Full conditioning uses the same float nodes and double interpolation as Burst.
// Included after SurfaceD/Unit/Cell/Add64. This file does not mutate or reconstruct the original macro heightmap.
StructuredBuffer<float4> _SurfaceOrogenGeometry,_SurfaceOrogenFlow,_SurfaceOrogenEnvironment;
int _SurfaceOrogenEnabled,_SurfaceOrogenResolution,_SurfaceOrogenSeed,_SurfaceOrogenMorphology;
uint4 _SurfaceOrogenShape,_SurfaceOrogenPolicy,_SurfaceOrogenLimits;
uint OrogenMix(uint x){x^=x>>16u;x*=0x7feb352du;x^=x>>15u;x*=0x846ca68bu;return x^(x>>16u);}
uint OrogenFold(uint h,uint2 value){return OrogenMix(OrogenMix(h^value.x)^value.y);}
double OrogenCorner(uint2 x,uint2 y,uint2 z,uint stream)
{
    uint h=OrogenMix((uint)_SurfaceOrogenSeed^(stream*0x9e3779b9u));h=OrogenFold(h,x);h=OrogenFold(h,y);h=OrogenFold(h,z);
    return (double)h*(2.0L/4294967295.0L)-1;
}
bool OrogenNoise(double3 p,uint stream,out double value)
{
    value=0;uint2 x,y,z;double tx,ty,tz;
    if(!SurfaceCell(p.x,x,tx)||!SurfaceCell(p.y,y,ty)||!SurfaceCell(p.z,z,tz))return false;
    tx=tx*tx*tx*(tx*(tx*6-15)+10);ty=ty*ty*ty*(ty*(ty*6-15)+10);tz=tz*tz*tz*(tz*(tz*6-15)+10);
    uint2 xx=SurfaceAdd64(x,1u),yy=SurfaceAdd64(y,1u),zz=SurfaceAdd64(z,1u);
    double a=SurfaceLerp(OrogenCorner(x,y,z,stream),OrogenCorner(xx,y,z,stream),tx),b=SurfaceLerp(OrogenCorner(x,yy,z,stream),OrogenCorner(xx,yy,z,stream),tx);
    double c=SurfaceLerp(OrogenCorner(x,y,zz,stream),OrogenCorner(xx,y,zz,stream),tx),d=SurfaceLerp(OrogenCorner(x,yy,zz,stream),OrogenCorner(xx,yy,zz,stream),tx);
    value=SurfaceLerp(SurfaceLerp(a,b,ty),SurfaceLerp(c,d,ty),tz);return true;
}
double4 OrogenBilinear(StructuredBuffer<float4> values,int face,int2 cell,double2 t)
{
    int side=_SurfaceOrogenResolution+1,a=face*side*side+cell.y*side+cell.x,b=a+1,c=a+side,d=c+1;
    double4 low=(double4)values[a]+((double4)values[b]-(double4)values[a])*t.x;
    double4 high=(double4)values[c]+((double4)values[d]-(double4)values[c])*t.x;
    return low+(high-low)*t.y;
}
void OrogenCondition(double3 unit,out double4 geometry,out double4 flow,out double4 environment)
{
    double3 q=abs(unit);int face;double a,b;
    if(q.x>=q.y&&q.x>=q.z){double3 c=unit/q.x;if(unit.x>0){face=0;a=-c.z;b=c.y;}else{face=1;a=c.z;b=c.y;}}
    else if(q.y>=q.z){double3 c=unit/q.y;if(unit.y>0){face=2;a=c.x;b=-c.z;}else{face=3;a=c.x;b=c.z;}}
    else{double3 c=unit/q.z;if(unit.z>0){face=4;a=c.x;b=c.y;}else{face=5;a=-c.x;b=c.y;}}
    double2 grid=double2(SurfaceClamp((a+1)*.5,0,1),SurfaceClamp((b+1)*.5,0,1))*_SurfaceOrogenResolution;
    // Grid is nonnegative and bounded: conversion truncates toward the required floor without FP32 floor.
    int2 cell=min(_SurfaceOrogenResolution-1,(int2)grid);double2 t=grid-cell;
    geometry=OrogenBilinear(_SurfaceOrogenGeometry,face,cell,t);flow=OrogenBilinear(_SurfaceOrogenFlow,face,cell,t);environment=OrogenBilinear(_SurfaceOrogenEnvironment,face,cell,t);
}
double OrogenBandWeight(double wavelength,double footprint)
{
    double denominator=footprint>0?footprint:1.0L;if(footprint<=0)return 1;
    double t=SurfaceClamp((wavelength/denominator-2)*.5,0,1);return t*t*(3-2*t);
}
double OrogenPeakedRidge(double n)
{
    double epsilon=.025L,s=(SurfaceSqrt(n*n+epsilon*epsilon)-epsilon)/(SurfaceSqrt(1+epsilon*epsilon)-epsilon);
    return 2*(1-s)*(1-s)-1;
}
double OrogenWavelength(double wavelength,int band)
{
    return wavelength*(_SurfaceOrogenMorphology==5&&band==2?8u:(1u<<band));
}
bool SurfaceOrogenDetail(double3 unit,double baseHeight,double footprint,out double value)
{
    value=0;if(_SurfaceOrogenEnabled==0)return true;
    double radius=SurfaceD(_SurfaceOrogenShape.xy),sea=SurfaceD(_SurfaceOrogenShape.zw),wavelength=SurfaceD(_SurfaceOrogenPolicy.xy),strength=SurfaceD(_SurfaceOrogenPolicy.zw);
    if(!SurfaceFinite(baseHeight)||!SurfaceFinite(footprint)||footprint<0||_SurfaceOrogenResolution<2)return false;
    if(strength==0||SurfaceD(_SurfaceOrogenLimits.zw)==0||baseHeight<=sea)return true;
    unit=SurfaceUnit(unit);double4 geometry,flow,environment;OrogenCondition(unit,geometry,flow,environment);
    double admitted=SurfaceMin(geometry.w,.25*SurfaceMax(0,geometry.z-geometry.y));
    double coast=SurfaceClamp((baseHeight-sea)/SurfaceMax(SurfaceD(_SurfaceOrogenLimits.xy),4*admitted),0,1);
    double amplitude=admitted*strength*environment.x*coast;if(amplitude==0)return true;
    bool contributing=false;[loop]for(int wb=0;wb<4;wb++)contributing=contributing||((_SurfaceOrogenMorphology==1||wb<3)&&OrogenBandWeight(OrogenWavelength(wavelength,wb),footprint)!=0);
    if(!contributing)return true;
    double scale=radius/wavelength;double3 scaled=unit*scale;
    if(!SurfaceFinite(scale)||SurfaceAbs(scaled.x)>4503599627370480.0L||SurfaceAbs(scaled.y)>4503599627370480.0L||SurfaceAbs(scaled.z)>4503599627370480.0L)return false;
    double3 downstream=flow.xyz-unit*SurfaceDot(unit,flow.xyz),across=SurfaceCross(unit,downstream),macro=unit*(scale/8);
    double warp0,warp1;if(!OrogenNoise(macro+double3(17,-31,11),0u,warp0)||!OrogenNoise(macro+double3(-19,7,43),1u,warp1))return false;
    double3 warp=downstream*(.35L*warp0)+across*(.2L*warp1);
    double mountain=SurfaceClamp(.2L+.65L*environment.y+.15L*environment.w,0,1),incision=.55L*flow.w*(.25+.75*environment.z),sum=0;
    if(_SurfaceOrogenMorphology==2||_SurfaceOrogenMorphology==3||_SurfaceOrogenMorphology==4||_SurfaceOrogenMorphology==5)
    {
        [loop]for(int flowBand=0;flowBand<3;flowBand++)
        {
            double weight=OrogenBandWeight(OrogenWavelength(wavelength,flowBand),footprint);if(weight==0)continue;
            double3 p=unit*(scale/(_SurfaceOrogenMorphology==5&&flowBand==2?8u:(1u<<flowBand)))+warp;double n0,n1,n2,n3=0,n4=0,n;
            if(flowBand==2&&(_SurfaceOrogenMorphology==3||_SurfaceOrogenMorphology==4||_SurfaceOrogenMorphology==5))
            {
                if(!OrogenNoise(p-downstream*.5,2u,n0)||!OrogenNoise(p-downstream*.25,2u,n1)||!OrogenNoise(p,2u,n2)||
                    !OrogenNoise(p+downstream*.25,2u,n3)||!OrogenNoise(p+downstream*.5,2u,n4))return false;
                n=(n0+4*n1+6*n2+4*n3+n4)/16;
            }
            else if(flowBand==2)
            {
                if(!OrogenNoise(p-downstream,2u,n0)||!OrogenNoise(p-downstream*.5,2u,n1)||!OrogenNoise(p,2u,n2)||
                    !OrogenNoise(p+downstream*.5,2u,n3)||!OrogenNoise(p+downstream,2u,n4))return false;
                n=(n0+4*n1+6*n2+4*n3+n4)/16;
            }
            else
            {
                if(!OrogenNoise(p,2u,n0)||!OrogenNoise(p+downstream*.75,2u,n1)||!OrogenNoise(p-downstream*.75,2u,n2))return false;
                n=.5*n0+.25*n1+.25*n2;
            }
            double shape=n;
            if(flowBand==2)
            {
                double ridge;
                if(_SurfaceOrogenMorphology==4||_SurfaceOrogenMorphology==5)
                    ridge=(OrogenPeakedRidge(n0)+4*OrogenPeakedRidge(n1)+6*OrogenPeakedRidge(n2)+4*OrogenPeakedRidge(n3)+OrogenPeakedRidge(n4))/16;
                else if(_SurfaceOrogenMorphology==3)
                {
                    double epsilon=.025L,s=(SurfaceSqrt(n*n+epsilon*epsilon)-epsilon)/(SurfaceSqrt(1+epsilon*epsilon)-epsilon);
                    ridge=2*(1-s)*(1-s)-1;
                }
                else ridge=(.0625L-n*n)/(.0625L+n*n);
                double valley=(ridge-1)*.5;
                shape=SurfaceLerp(SurfaceLerp(n,ridge,mountain),valley,incision);
            }
            double bandWeight=(_SurfaceOrogenMorphology==3||_SurfaceOrogenMorphology==4||_SurfaceOrogenMorphology==5)?(flowBand==0?.18L:flowBand==1?.22L:.60L):(flowBand==0?.08L:flowBand==1?.12L:.80L);
            sum+=bandWeight*weight*shape;
        }
        value=amplitude*sum;return SurfaceFinite(value);
    }
    [loop]for(int band=0;band<4;band++)
    {
        double weight=OrogenBandWeight(wavelength*(1u<<band),footprint);if(weight==0)continue;
        double3 p=unit*(scale/(1u<<band))+warp;double n0,n1,n2,channelNoise;uint stream=(uint)(2+band*4);
        if(!OrogenNoise(p,stream,n0)||!OrogenNoise(p+downstream*.75,stream,n1)||!OrogenNoise(p-downstream*.75,stream,n2)||!OrogenNoise(p+across*1.618L,(uint)(3+band*4),channelNoise))return false;
        double n=.5*n0+.25*n1+.25*n2,r=1-SurfaceAbs(n),ridge=2*r*r-1,channel=1-SurfaceAbs(channelNoise),valley=-channel*channel*channel*channel;
        double shape=SurfaceLerp(SurfaceLerp(n,ridge,mountain),valley,incision),bandWeight=band==0?.12L:band==1?.18L:band==2?.27L:.43L;
        sum+=bandWeight*weight*shape;
    }
    value=amplitude*sum;return SurfaceFinite(value);
}
#endif
