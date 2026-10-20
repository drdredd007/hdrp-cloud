#ifndef SPACERUNNER_PREPARED_PLANET_SAMPLING_INCLUDED
#define SPACERUNNER_PREPARED_PLANET_SAMPLING_INCLUDED
// Graphics materials select an immutable ready-data backend. Compute callers
// without a graphics keyword retain the complete reference/mixed contract.
#if defined(_PREPARED_BACKEND_REFERENCE)
#define PP_REFERENCE_BACKEND 1
#elif defined(_PREPARED_BACKEND_WHOLE_FLOAT)
#define PP_WHOLE_FLOAT_BACKEND 1
#elif defined(_PREPARED_BACKEND_NATIVE)
#define PP_NATIVE_BACKEND 1
#elif defined(_PREPARED_BACKEND_MIXED)
#define PP_REFERENCE_BACKEND 1
#define PP_WHOLE_FLOAT_BACKEND 1
#if defined(_PREPARED_TEXTURES_1) || defined(_PREPARED_TEXTURES_2) || defined(_PREPARED_TEXTURES_4) || defined(_PREPARED_TEXTURES_8)
#define PP_NATIVE_BACKEND 1
#endif
#else
#define PP_REFERENCE_BACKEND 1
#define PP_WHOLE_FLOAT_BACKEND 1
#define PP_NATIVE_BACKEND 1
#endif
#if defined(_PREPARED_TEXTURES_1)
#define PP_TEXTURE_RESOURCE_SLOTS 1
#elif defined(_PREPARED_TEXTURES_2)
#define PP_TEXTURE_RESOURCE_SLOTS 2
#elif defined(_PREPARED_TEXTURES_4)
#define PP_TEXTURE_RESOURCE_SLOTS 4
#else
#define PP_TEXTURE_RESOURCE_SLOTS 8
#endif
// Ready-data addressing, interpolation, analytic interpolation derivatives and
// radius composition only. No geological generator or procedural source code.
static const double PPPi=3.1415926535897932384626433832795L;
double PPAbs(double x){return x<0?-x:x;}
double PPMin(double x,double y){return x<y?x:y;}
double PPMax(double x,double y){return x>y?x:y;}
double PPClamp(double x,double a,double b){return PPMin(b,PPMax(a,x));}
precise double PPDot(double3 x,double3 y){return (x.x*y.x+x.y*y.y)+x.z*y.z;}
double3 PPCross(double3 a,double3 b){return double3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
uint2 PPMultiply32(uint a,uint b)
{
    uint a0=a&65535u,a1=a>>16,b0=b&65535u,b1=b>>16,p0=a0*b0,p1=a0*b1,p2=a1*b0,p3=a1*b1;
    uint middle=(p0>>16)+(p1&65535u)+(p2&65535u);return uint2((p0&65535u)|(middle<<16),p3+(p1>>16)+(p2>>16)+(middle>>16));
}
uint4 PPSquare54(uint2 n)
{
    uint2 low=PPMultiply32(n.x,n.x),cross=PPMultiply32(n.x,n.y),high=PPMultiply32(n.y,n.y);uint y=low.y,z=high.x,w=high.y;
    [unroll]for(int i=0;i<2;i++){uint next=y+cross.x,carry=next<y?1u:0u;y=next;next=z+cross.y;uint upper=next<z?1u:0u;z=next;next=z+carry;upper+=next<z?1u:0u;z=next;w+=upper;}
    return uint4(low.x,y,z,w);
}
int PPCompare128(uint4 a,uint4 b)
{
    if(a.w!=b.w)return a.w<b.w?-1:1;if(a.z!=b.z)return a.z<b.z?-1:1;if(a.y!=b.y)return a.y<b.y?-1:1;return a.x==b.x?0:(a.x<b.x?-1:1);
}
double PPRoundSqrt(double x,double candidate)
{
    precise double square=candidate*candidate;if(square==x)return candidate;bool down=square>x;uint lo,hi;asuint(candidate,lo,hi);
    uint2 significand=uint2(lo,(hi&0xfffffu)|0x100000u);if(candidate>=2)significand=uint2(significand.x<<1,(significand.y<<1)|(significand.x>>31));
    uint2 midpoint=uint2(significand.x<<1,(significand.y<<1)|(significand.x>>31));
    if(down){uint previous=midpoint.x;midpoint.x--;if(previous==0)midpoint.y--;}else{midpoint.x++;if(midpoint.x==0)midpoint.y++;}
    uint xl,xh;asuint(x,xl,xh);uint shift=(xh>>20)==1023u?22u:23u,mantissaHigh=(xh&0xfffffu)|0x100000u;
    uint4 scaled=uint4(0,xl<<shift,(mantissaHigh<<shift)|(xl>>(32u-shift)),mantissaHigh>>(32u-shift));int comparison=PPCompare128(scaled,PPSquare54(midpoint));
    bool change=down?(comparison<0||(comparison==0&&(lo&1u)!=0)):(comparison>0||(comparison==0&&(lo&1u)!=0));
    if(change){if(down){if(lo==0)hi--;lo--;}else{lo++;if(lo==0)hi++;}}return asdouble(lo,hi);
}
// Mantissa-scaled Newton avoids overflow/underflow for valid directions. The
// original source arrays remain exact; this is addressing math, not synthesis.
precise double PPSqrt(double x)
{
    if(x==0)return 0;uint lo,hi;asuint(x,lo,hi);int adjustment=0;
    if((hi&0x7ff00000u)==0){x*=18014398509481984.0L;adjustment=-54;asuint(x,lo,hi);}
    int exponent=(int)((hi>>20)&2047u)-1023+adjustment,halfExponent=exponent>>1;
    double mantissa=asdouble(lo,(hi&0xfffffu)|0x3ff00000u);if(exponent-2*halfExponent!=0)mantissa*=2;
    precise double value=(double)sqrt((float)mantissa);[unroll]for(int i=0;i<4;i++)value=.5L*(value+mantissa/value);
    return PPRoundSqrt(mantissa,value)*asdouble(0u,(uint)(halfExponent+1023)<<20);
}
precise double3 PPNormalize(double3 d){double scale=PPMax(PPAbs(d.x),PPMax(PPAbs(d.y),PPAbs(d.z)));d/=scale;return d/PPSqrt(PPDot(d,d));}
precise double PPAtanUnit(double x)
{
    precise double t=x/(1+PPSqrt(1+x*x));t=t/(1+PPSqrt(1+t*t));
    precise double z=-t*t,p=1.0L/37.0L;[unroll]for(int k=17;k>=0;k--)p=1.0L/(double)(2*k+1)+z*p;return 4*t*p;
}
precise double PPAtan(double x){double a=PPAbs(x),r=a>1?PPPi*.5L-PPAtanUnit(1/a):PPAtanUnit(a);return x<0?-r:r;}
precise double PPAtan2(double y,double x)
{
    uint yl,yh,xl,xh;asuint(y,yl,yh);asuint(x,xl,xh);bool negative=(yh&0x80000000u)!=0;
    if(y==0)return (xh&0x80000000u)!=0?(negative?-PPPi:PPPi):y;
    if(x>0)return PPAtan(y/x);if(x<0)return PPAtan(y/x)+(negative?-PPPi:PPPi);return negative?-PPPi*.5L:PPPi*.5L;
}
double PPFloor(double x){int i=(int)x;return x<i?i-1:i;}
struct PreparedPlanetPlacement {double4 centreRadius,rightScale,forwardAmplitude,cropReference;uint4 metadata;double2 featherHeading;};
struct PreparedPlanetSourceGpu {int4 grids,levels;double4 data;};
struct PreparedPlanetLevelGpu {double4 data;int4 grid,table;};
struct PreparedPlanetFineGpu {uint2 height;float3 normal;};
struct PreparedPlanetQuery {double3 direction;double worldFootprint;};
struct PreparedPlanetSurface {double height;double2 gradient;float3 normal;uint ready;double reserved;};
StructuredBuffer<PreparedPlanetPlacement> _PreparedPlanetPlacements;
StructuredBuffer<int> _PreparedPlanetOffsets,_PreparedPlanetContributors;
StructuredBuffer<float> _PreparedPlanetFoundation;
#if defined(PP_REFERENCE_BACKEND)
StructuredBuffer<float> _PreparedPlanetLattices;
StructuredBuffer<PreparedPlanetLevelGpu> _PreparedPlanetLevels;
StructuredBuffer<int> _PreparedPlanetTileTable;
StructuredBuffer<PreparedPlanetFineGpu> _PreparedPlanetFine0,_PreparedPlanetFine1,_PreparedPlanetFine2,_PreparedPlanetFine3;
StructuredBuffer<PreparedPlanetFineGpu> _PreparedPlanetFine4,_PreparedPlanetFine5,_PreparedPlanetFine6,_PreparedPlanetFine7;
#endif
StructuredBuffer<PreparedPlanetSourceGpu> _PreparedPlanetSources;
StructuredBuffer<double4> _PreparedPlanetParameters;
#if defined(PP_WHOLE_FLOAT_BACKEND)
StructuredBuffer<float> _PreparedPlanetWhole0,_PreparedPlanetWhole1,_PreparedPlanetWhole2,_PreparedPlanetWhole3;
StructuredBuffer<float> _PreparedPlanetWhole4,_PreparedPlanetWhole5,_PreparedPlanetWhole6,_PreparedPlanetWhole7;
#endif
#if defined(PP_WHOLE_FLOAT_BACKEND) || defined(PP_NATIVE_BACKEND)
StructuredBuffer<int4> _PreparedPlanetWholeMips,_PreparedPlanetWholeConfiguration;
#endif
#if defined(PP_NATIVE_BACKEND)
Texture2D<float> _PreparedPlanetTextureHeight0,_PreparedPlanetTextureMean0,_PreparedPlanetTextureBlend0;
Texture2D<float4> _PreparedPlanetTextureNormal0;
Texture2D<float4> _PreparedPlanetTextureMaterial0;
#if PP_TEXTURE_RESOURCE_SLOTS > 1
Texture2D<float> _PreparedPlanetTextureHeight1,_PreparedPlanetTextureMean1,_PreparedPlanetTextureBlend1;
Texture2D<float4> _PreparedPlanetTextureNormal1;
Texture2D<float4> _PreparedPlanetTextureMaterial1;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 2
Texture2D<float> _PreparedPlanetTextureHeight2,_PreparedPlanetTextureMean2,_PreparedPlanetTextureBlend2;
Texture2D<float4> _PreparedPlanetTextureNormal2;
Texture2D<float4> _PreparedPlanetTextureMaterial2;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 3
Texture2D<float> _PreparedPlanetTextureHeight3,_PreparedPlanetTextureMean3,_PreparedPlanetTextureBlend3;
Texture2D<float4> _PreparedPlanetTextureNormal3;
Texture2D<float4> _PreparedPlanetTextureMaterial3;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 4
Texture2D<float> _PreparedPlanetTextureHeight4,_PreparedPlanetTextureMean4,_PreparedPlanetTextureBlend4;
Texture2D<float4> _PreparedPlanetTextureNormal4;
Texture2D<float4> _PreparedPlanetTextureMaterial4;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 5
Texture2D<float> _PreparedPlanetTextureHeight5,_PreparedPlanetTextureMean5,_PreparedPlanetTextureBlend5;
Texture2D<float4> _PreparedPlanetTextureNormal5;
Texture2D<float4> _PreparedPlanetTextureMaterial5;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 6
Texture2D<float> _PreparedPlanetTextureHeight6,_PreparedPlanetTextureMean6,_PreparedPlanetTextureBlend6;
Texture2D<float4> _PreparedPlanetTextureNormal6;
Texture2D<float4> _PreparedPlanetTextureMaterial6;
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 7
Texture2D<float> _PreparedPlanetTextureHeight7,_PreparedPlanetTextureMean7,_PreparedPlanetTextureBlend7;
Texture2D<float4> _PreparedPlanetTextureNormal7;
Texture2D<float4> _PreparedPlanetTextureMaterial7;
#endif
SamplerState sampler_PreparedPlanet_linear_clamp;
#endif
float _PreparedPlanetEnabled;
float4 _PreparedPlanetAnchorHigh,_PreparedPlanetAnchorLow;
float4x4 _PreparedPlanetLocalToPlanet,_PreparedPlanetPlanetToLocal;
#if defined(PP_REFERENCE_BACKEND)
PreparedPlanetFineGpu PPFineAt(uint index)
{
    uint size=(uint)_PreparedPlanetParameters[1].x,chunk=index/size,at=index-chunk*size;
    if(chunk==0)return _PreparedPlanetFine0[at];if(chunk==1)return _PreparedPlanetFine1[at];
    if(chunk==2)return _PreparedPlanetFine2[at];if(chunk==3)return _PreparedPlanetFine3[at];
    if(chunk==4)return _PreparedPlanetFine4[at];if(chunk==5)return _PreparedPlanetFine5[at];
    if(chunk==6)return _PreparedPlanetFine6[at];return _PreparedPlanetFine7[at];
}
#endif
// Complete whole resources remain resident. Segments only bound individual
// GPU allocations; no address causes IO, generation or a residency fallback.
#if defined(PP_WHOLE_FLOAT_BACKEND)
float PPWholeAt(uint index)
{
    uint size=(uint)_PreparedPlanetWholeConfiguration[0].x,chunk=index/size,at=index-chunk*size;
    if(chunk==0)return _PreparedPlanetWhole0[at];if(chunk==1)return _PreparedPlanetWhole1[at];
    if(chunk==2)return _PreparedPlanetWhole2[at];if(chunk==3)return _PreparedPlanetWhole3[at];
    if(chunk==4)return _PreparedPlanetWhole4[at];if(chunk==5)return _PreparedPlanetWhole5[at];
    if(chunk==6)return _PreparedPlanetWhole6[at];return _PreparedPlanetWhole7[at];
}
double4 PPWholeValue(int4 mip,int index)
{
    uint at=(uint)(mip.x+index*mip.z);double4 value=double4((double)PPWholeAt(at),0,0,0);
    if(mip.z>1)value.y=(double)PPWholeAt(at+1);if(mip.z>2)value.z=(double)PPWholeAt(at+2);if(mip.z>3)value.w=(double)PPWholeAt(at+3);return value;
}
double4 PPWholeBilinear(int selected,double width,double2 sourcePosition,out double2 gradient)
{
    int4 mip=_PreparedPlanetWholeMips[selected];int n=mip.y;if(n==1){gradient=0;return PPWholeValue(mip,0);}
    double2 uv=double2(PPClamp(sourcePosition.x/width+.5L,0,1),PPClamp(sourcePosition.y/width+.5L,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);double2 t=uv-xy;int at=xy.y*n+xy.x;
    double4 a=PPWholeValue(mip,at),b=PPWholeValue(mip,at+1),c=PPWholeValue(mip,at+n),d=PPWholeValue(mip,at+n+1);
    gradient=double2((b.x-a.x)*(1-t.y)+(d.x-c.x)*t.y,(c.x-a.x)*(1-t.x)+(d.x-b.x)*t.x)*((n-1)/width);
    return (a*(1-t.x)+b*t.x)*(1-t.y)+(c*(1-t.x)+d*t.x)*t.y;
}
double4 PPWholeFiltered(int first,int count,double width,double2 sourcePosition,double footprint,out double2 gradient,out double footprintDerivative)
{
    int selected=first;[loop]for(int i=1;i<count;i++){if(width/(double)_PreparedPlanetWholeMips[first+i].y>footprint)break;selected=first+i;}
    double4 a=PPWholeBilinear(selected,width,sourcePosition,gradient);double lower=width/(double)_PreparedPlanetWholeMips[selected].y;footprintDerivative=0;
    if(selected+1>=first+count||footprint<=lower)return a;
    double span=width/(double)_PreparedPlanetWholeMips[selected+1].y-lower,t=(footprint-lower)/span;double2 upperGradient;
    double4 b=PPWholeBilinear(selected+1,width,sourcePosition,upperGradient);gradient=gradient*(1-t)+upperGradient*t;footprintDerivative=(b.x-a.x)/span;return a*(1-t)+b*t;
}
// Native whole resources are immutable Unity texture assets. Logical texels
// retain source resolution; storage padding is addressed independently per mip.
#endif
#if defined(PP_NATIVE_BACKEND)
float PPTextureLoad(int resource,int kind,int3 texel)
{
    if(resource==0)return kind==0?_PreparedPlanetTextureHeight0.Load(texel):_PreparedPlanetTextureBlend0.Load(texel);
#if PP_TEXTURE_RESOURCE_SLOTS > 1
    if(resource==1)return kind==0?_PreparedPlanetTextureHeight1.Load(texel):_PreparedPlanetTextureBlend1.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 2
    if(resource==2)return kind==0?_PreparedPlanetTextureHeight2.Load(texel):_PreparedPlanetTextureBlend2.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 3
    if(resource==3)return kind==0?_PreparedPlanetTextureHeight3.Load(texel):_PreparedPlanetTextureBlend3.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 4
    if(resource==4)return kind==0?_PreparedPlanetTextureHeight4.Load(texel):_PreparedPlanetTextureBlend4.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 5
    if(resource==5)return kind==0?_PreparedPlanetTextureHeight5.Load(texel):_PreparedPlanetTextureBlend5.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 6
    if(resource==6)return kind==0?_PreparedPlanetTextureHeight6.Load(texel):_PreparedPlanetTextureBlend6.Load(texel);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 7
    if(resource==7)return kind==0?_PreparedPlanetTextureHeight7.Load(texel):_PreparedPlanetTextureBlend7.Load(texel);
#endif
    return 0;
}
double PPTextureBilinear(int selected,int level,int kind,double width,double2 sourcePosition,out double2 gradient)
{
    int4 mip=_PreparedPlanetWholeMips[selected];int n=mip.y;if(n==1){gradient=0;return (double)PPTextureLoad(mip.x,kind,int3(0,0,level));}
    double2 uv=double2(PPClamp(sourcePosition.x/width+.5L,0,1),PPClamp(sourcePosition.y/width+.5L,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);double2 t=uv-xy;
    double a=(double)PPTextureLoad(mip.x,kind,int3(xy,level)),b=(double)PPTextureLoad(mip.x,kind,int3(xy+int2(1,0),level)),c=(double)PPTextureLoad(mip.x,kind,int3(xy+int2(0,1),level)),d=(double)PPTextureLoad(mip.x,kind,int3(xy+1,level));
    gradient=double2((b-a)*(1-t.y)+(d-c)*t.y,(c-a)*(1-t.x)+(d-b)*t.x)*((n-1)/width);
    return (a*(1-t.x)+b*t.x)*(1-t.y)+(c*(1-t.x)+d*t.x)*t.y;
}
double PPTextureHeightFiltered(int first,int count,double width,double2 sourcePosition,double footprint,out double2 gradient,out double footprintDerivative)
{
    int selected=first;[loop]for(int i=1;i<count;i++){if(width/(double)_PreparedPlanetWholeMips[first+i].y>footprint)break;selected=first+i;}
    double a=PPTextureBilinear(selected,selected-first,0,width,sourcePosition,gradient),lower=width/(double)_PreparedPlanetWholeMips[selected].y;footprintDerivative=0;
    if(selected+1>=first+count||footprint<=lower)return a;
    double span=width/(double)_PreparedPlanetWholeMips[selected+1].y-lower,t=(footprint-lower)/span;double2 upperGradient;
    double b=PPTextureBilinear(selected+1,selected+1-first,0,width,sourcePosition,upperGradient);gradient=gradient*(1-t)+upperGradient*t;footprintDerivative=(b-a)/span;return a*(1-t)+b*t;
}
double3 PPTextureNormalAt(int resource,float2 uv,float2 meanUv,int level)
{
    float2 encoded=0;float mean=0;
    if(resource==0){encoded=_PreparedPlanetTextureNormal0.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean0.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#if PP_TEXTURE_RESOURCE_SLOTS > 1
    if(resource==1){encoded=_PreparedPlanetTextureNormal1.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean1.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 2
    if(resource==2){encoded=_PreparedPlanetTextureNormal2.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean2.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 3
    if(resource==3){encoded=_PreparedPlanetTextureNormal3.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean3.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 4
    if(resource==4){encoded=_PreparedPlanetTextureNormal4.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean4.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 5
    if(resource==5){encoded=_PreparedPlanetTextureNormal5.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean5.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 6
    if(resource==6){encoded=_PreparedPlanetTextureNormal6.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean6.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 7
    if(resource==7){encoded=_PreparedPlanetTextureNormal7.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level).rg;mean=_PreparedPlanetTextureMean7.SampleLevel(sampler_PreparedPlanet_linear_clamp,meanUv,level);}
#endif
    float2 xz=encoded*2-1;
    // BC5 quantization may move XZ onto/outside the unit disk. A bounded
    // positive Y avoids infinite source slopes without changing geometry.
    float3 unit=normalize(float3(xz.x,sqrt(max(1e-12f,1-dot(xz,xz))),xz.y));
    return (double3)unit*(double)mean;
}

double3 PPTextureNormalMeanTexel(int resource,int3 texel,int normalProfile)
{
    float4 encoded=0;float mean=0;
    if(resource==0){encoded=_PreparedPlanetTextureNormal0.Load(texel);mean=_PreparedPlanetTextureMean0.Load(texel);}
#if PP_TEXTURE_RESOURCE_SLOTS > 1
    if(resource==1){encoded=_PreparedPlanetTextureNormal1.Load(texel);mean=_PreparedPlanetTextureMean1.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 2
    if(resource==2){encoded=_PreparedPlanetTextureNormal2.Load(texel);mean=_PreparedPlanetTextureMean2.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 3
    if(resource==3){encoded=_PreparedPlanetTextureNormal3.Load(texel);mean=_PreparedPlanetTextureMean3.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 4
    if(resource==4){encoded=_PreparedPlanetTextureNormal4.Load(texel);mean=_PreparedPlanetTextureMean4.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 5
    if(resource==5){encoded=_PreparedPlanetTextureNormal5.Load(texel);mean=_PreparedPlanetTextureMean5.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 6
    if(resource==6){encoded=_PreparedPlanetTextureNormal6.Load(texel);mean=_PreparedPlanetTextureMean6.Load(texel);}
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 7
    if(resource==7){encoded=_PreparedPlanetTextureNormal7.Load(texel);mean=_PreparedPlanetTextureMean7.Load(texel);}
#endif
    float3 normal;
    if((normalProfile&7)==2){normal=encoded.rgb*2-1;normal.y=max(1e-6f,normal.y);}
    else{float2 oct=encoded.rg*2-1;float y=1-abs(oct.x)-abs(oct.y);normal=float3(oct.x,max(1e-6f,y),oct.y);}
    float3 unit=normalize(normal);
    // Match the qualified per-texel decode before interpolation. The mean
    // length remains in this vector, rather than being interpolated separately.
    float3 saved=unit*mean;return (double3)saved;
}

double3 PPTextureNormalMeanBilinear(int selected,int level,double width,double2 sourcePosition,int normalProfile)
{
    int4 mip=_PreparedPlanetWholeMips[selected];int n=mip.y;
    if(n==1)return PPTextureNormalMeanTexel(mip.x,int3(0,0,level),normalProfile);
    double2 uv=double2(PPClamp(sourcePosition.x/width+.5L,0,1),PPClamp(sourcePosition.y/width+.5L,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);double2 t=uv-xy;
    // Both textures use the same logical integer node. Their storage widths
    // can differ: RGHalf4097 and the retained R16mean4100 require no shared UV.
    double3 a=PPTextureNormalMeanTexel(mip.x,int3(xy,level),normalProfile),b=PPTextureNormalMeanTexel(mip.x,int3(xy+int2(1,0),level),normalProfile);
    double3 c=PPTextureNormalMeanTexel(mip.x,int3(xy+int2(0,1),level),normalProfile),d=PPTextureNormalMeanTexel(mip.x,int3(xy+1,level),normalProfile);
    return (a*(1-t.x)+b*t.x)*(1-t.y)+(c*(1-t.x)+d*t.x)*t.y;
}
double4 PPTextureMaterialAt(int resource,float2 uv,int level)
{
    if(resource==0)return _PreparedPlanetTextureMaterial0.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#if PP_TEXTURE_RESOURCE_SLOTS > 1
    if(resource==1)return _PreparedPlanetTextureMaterial1.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 2
    if(resource==2)return _PreparedPlanetTextureMaterial2.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 3
    if(resource==3)return _PreparedPlanetTextureMaterial3.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 4
    if(resource==4)return _PreparedPlanetTextureMaterial4.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 5
    if(resource==5)return _PreparedPlanetTextureMaterial5.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 6
    if(resource==6)return _PreparedPlanetTextureMaterial6.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
#if PP_TEXTURE_RESOURCE_SLOTS > 7
    if(resource==7)return _PreparedPlanetTextureMaterial7.SampleLevel(sampler_PreparedPlanet_linear_clamp,uv,level);
#endif
    return 0;
}
double4 PPTextureLightingLevel(int selected,int level,int kind,double width,double2 sourcePosition,int normalProfile)
{
    // Native profile = encoding +8*interpolation, carried in SourceGpu.data.y.
    // v1 profile0 keeps the original encoded hardware filtering below; v2
    // profile10 (BC7) /12 (RGHalf) decode four mean vectors before interpolation.
    if(kind==1&&normalProfile>=8)return double4(PPTextureNormalMeanBilinear(selected,level,width,sourcePosition,normalProfile),0);
    int4 mip=_PreparedPlanetWholeMips[selected];double2 cell=double2(PPClamp(sourcePosition.x/width+.5L,0,1),PPClamp(sourcePosition.y/width+.5L,0,1))*(mip.y-1)+.5L;
    float2 uv=(float2)(cell/(double)mip.z);
    if(kind==1)return double4(PPTextureNormalAt(mip.x,uv,(float2)(cell/(double)mip.w),level),0);
    double4 value=PPTextureMaterialAt(mip.x,uv,level);
    return value*(double4)asfloat(_PreparedPlanetWholeConfiguration[2+mip.x*2])+(double4)asfloat(_PreparedPlanetWholeConfiguration[1+mip.x*2]);
}
double4 PPTextureLightingFiltered(int first,int count,int kind,double width,double2 sourcePosition,double footprint,int normalProfile)
{
    int selected=first;[loop]for(int i=1;i<count;i++){if(width/(double)_PreparedPlanetWholeMips[first+i].y>footprint)break;selected=first+i;}
    double4 a=PPTextureLightingLevel(selected,selected-first,kind,width,sourcePosition,normalProfile);double lower=width/(double)_PreparedPlanetWholeMips[selected].y;
    if(selected+1>=first+count||footprint<=lower)return a;
    double span=width/(double)_PreparedPlanetWholeMips[selected+1].y-lower,t=(footprint-lower)/span;
    return a*(1-t)+PPTextureLightingLevel(selected+1,selected+1-first,kind,width,sourcePosition,normalProfile)*t;
}
#endif
double PPSourceBlend(uint source,double2 sourcePosition,out double2 gradient)
{
    PreparedPlanetSourceGpu s=_PreparedPlanetSources[source];gradient=0;if(s.data.z==0||s.levels.w==0)return 1;
#if defined(PP_NATIVE_BACKEND)
    if(s.data.z==2)return PPTextureBilinear(s.levels.z,0,3,s.data.x,sourcePosition,gradient);
#endif
#if defined(PP_WHOLE_FLOAT_BACKEND)
    if(s.data.z==1)return PPWholeBilinear(s.levels.z,s.data.x,sourcePosition,gradient).x;
#endif
    return 0;
}
double4 PPSourceMaterial(uint source,double2 sourcePosition,double footprint)
{
    PreparedPlanetSourceGpu s=_PreparedPlanetSources[source];if(s.data.z==0||s.levels.y==0)return 0;double2 gradient;double derivative;
#if defined(PP_NATIVE_BACKEND)
    if(s.data.z==2)return PPTextureLightingFiltered(s.levels.x,s.levels.y,2,s.data.x,sourcePosition,footprint,0);
#endif
#if defined(PP_WHOLE_FLOAT_BACKEND)
    if(s.data.z==1)return PPWholeFiltered(s.levels.x,s.levels.y,s.data.x,sourcePosition,footprint,gradient,derivative);
#endif
    return 0;
}
double PPCubic(double a,double b,double c,double d,double t){return b+.5L*t*(c-a+t*(2*a-5*b+4*c-d+t*(3*(b-c)+d-a)));}
double PPCubicD(double a,double b,double c,double d,double t){return .5L*(c-a+t*(2*(2*a-5*b+4*c-d)+3*t*(3*(b-c)+d-a)));}
#if defined(PP_REFERENCE_BACKEND)
double PPLatticeHeight(int offset,int n,double width,double2 p)
{
    double2 uv=double2(PPClamp(p.x/width+.5L,0,1),PPClamp(p.y/width+.5L,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);double2 t=uv-xy;double4 rows=0;
    [unroll]for(int y=0;y<4;y++){
        int start=offset+clamp(xy.y+y-1,0,n-1)*n;
        rows[y]=PPCubic(_PreparedPlanetLattices[start+max(0,xy.x-1)],_PreparedPlanetLattices[start+xy.x],_PreparedPlanetLattices[start+xy.x+1],_PreparedPlanetLattices[start+min(n-1,xy.x+2)],t.x);
    }
    return PPCubic(rows.x,rows.y,rows.z,rows.w,t.y);
}
double3 PPLattice(int offset,int n,double width,double2 p,double derivativeStep,out double2 heightGradient)
{
    double2 uv=double2(PPClamp(p.x/width+.5L,0,1),PPClamp(p.y/width+.5L,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);double2 t=uv-xy;double4 rows=0,dx=0,trueDx=0;double h=derivativeStep*(n-1)/width;
    [unroll]for(int y=0;y<4;y++){
        int start=offset+clamp(xy.y+y-1,0,n-1)*n;
        double a=_PreparedPlanetLattices[start+max(0,xy.x-1)],b=_PreparedPlanetLattices[start+xy.x],c=_PreparedPlanetLattices[start+xy.x+1],d=_PreparedPlanetLattices[start+min(n-1,xy.x+2)];
        rows[y]=PPCubic(a,b,c,d,t.x);trueDx[y]=PPCubicD(a,b,c,d,t.x);dx[y]=trueDx[y]+.5L*(3*(b-c)+d-a)*h*h;
    }
    double value=PPCubic(rows.x,rows.y,rows.z,rows.w,t.y),gx=PPCubic(dx.x,dx.y,dx.z,dx.w,t.y)*(n-1)/width;
    double gz=(PPCubicD(rows.x,rows.y,rows.z,rows.w,t.y)+.5L*(3*(rows.y-rows.z)+rows.w-rows.x)*h*h)*(n-1)/width;
    heightGradient=double2(PPCubic(trueDx.x,trueDx.y,trueDx.z,trueDx.w,t.y),PPCubicD(rows.x,rows.y,rows.z,rows.w,t.y))*(n-1)/width;
    if(t.x<=h||t.x>=1-h)gx=(PPLatticeHeight(offset,n,width,p+double2(derivativeStep,0))-PPLatticeHeight(offset,n,width,p-double2(derivativeStep,0)))/(2*derivativeStep);
    if(t.y<=h||t.y>=1-h)gz=(PPLatticeHeight(offset,n,width,p+double2(0,derivativeStep))-PPLatticeHeight(offset,n,width,p-double2(0,derivativeStep)))/(2*derivativeStep);
    return double3(value,gx,gz);
}
void PPSourceLevel(int selected,double2 p,double baseHeight,double2 baseGradient,double2 baseNormalGradient,out double height,out double2 gradient,out double2 normalGradient,out uint ready,out float variance)
{
    height=baseHeight;gradient=baseGradient;normalGradient=baseNormalGradient;ready=1;variance=0;
    PreparedPlanetLevelGpu level=_PreparedPlanetLevels[selected];int2 tile=int2(PPFloor(p.x/level.data.y),PPFloor(p.y/level.data.y));int2 relative=tile-level.grid.yz;
    if(any(relative<0)||relative.x>=level.grid.w||relative.y>=level.table.x){ready=0;return;}
    int at=_PreparedPlanetTileTable[level.table.y+relative.y*level.grid.w+relative.x];if(at==-2)return;if(at<0){ready=0;return;}
    double2 cell=(p-(double2)tile*level.data.y)/level.data.x;int2 xy=min(level.grid.x-1,(int2)cell);double2 t=cell-xy;int side=level.grid.x+1,first=at+xy.y*side+xy.x;
    PreparedPlanetFineGpu n0=PPFineAt(first),n1=PPFineAt(first+1),n2=PPFineAt(first+side),n3=PPFineAt(first+side+1);
    double h0=asdouble(n0.height.x,n0.height.y),h1=asdouble(n1.height.x,n1.height.y),h2=asdouble(n2.height.x,n2.height.y),h3=asdouble(n3.height.x,n3.height.y);
    height+=(h0*(1-t.x)+h1*t.x)*(1-t.y)+(h2*(1-t.x)+h3*t.x)*t.y;
    gradient+=double2((h1-h0)*(1-t.y)+(h3-h2)*t.y,(h2-h0)*(1-t.x)+(h3-h1)*t.x)/level.data.x;
    double3 normal=(double3)n0.normal*((1-t.x)*(1-t.y))+(double3)n1.normal*(t.x*(1-t.y))+(double3)n2.normal*((1-t.x)*t.y)+(double3)n3.normal*(t.x*t.y);
    normalGradient=-normal.xz/normal.y;variance=(float)PPMax(0,1-PPSqrt(PPDot(normal,normal)));
}
#endif
void PPSource(uint source,double2 p,double requestedStep,out double height,out double2 gradient,out double2 normalGradient,out uint ready,out float variance,out double footprintDerivative)
{
    PreparedPlanetSourceGpu s=_PreparedPlanetSources[source];
#if defined(PP_NATIVE_BACKEND)
    if(s.data.z==2){
        height=PPTextureHeightFiltered(s.grids.x,s.grids.y,s.data.x,p,requestedStep,gradient,footprintDerivative);
        double3 savedNormal=PPTextureLightingFiltered(s.grids.z,s.grids.w,1,s.data.x,p,requestedStep,(int)s.data.y).xyz;
        double ny=PPMax(savedNormal.y,1e-20L);normalGradient=-savedNormal.xz/ny;ready=1;variance=(float)PPMax(0,1-PPSqrt(PPDot(savedNormal,savedNormal)));return;
    }
#endif
#if defined(PP_WHOLE_FLOAT_BACKEND)
    if(s.data.z==1){
        height=PPWholeFiltered(s.grids.x,s.grids.y,s.data.x,p,requestedStep,gradient,footprintDerivative).x;
        double2 unused;double derivative;double3 normal=PPWholeFiltered(s.grids.z,s.grids.w,s.data.x,p,requestedStep,unused,derivative).xyz;
        normalGradient=-normal.xz/normal.y;ready=1;variance=(float)PPMax(0,1-PPSqrt(PPDot(normal,normal)));return;
    }
#endif
#if defined(PP_REFERENCE_BACKEND)
    double2 ga,gb;double3 a=PPLattice(s.grids.x,s.grids.y,s.data.x,p,s.data.y,ga),b=PPLattice(s.grids.z,s.grids.w,s.data.x,p,s.data.y,gb);
    double baseHeight=a.x+b.x;double2 baseGradient=ga+gb,baseNormalGradient=a.yz+b.yz;int selected=s.levels.x;
    [loop]for(int i=1;i<s.levels.y;i++){if(_PreparedPlanetLevels[s.levels.x+i].data.x>requestedStep)break;selected=s.levels.x+i;}
    PPSourceLevel(selected,p,baseHeight,baseGradient,baseNormalGradient,height,gradient,normalGradient,ready,variance);footprintDerivative=0;
    if(selected+1>=s.levels.x+s.levels.y||requestedStep<=_PreparedPlanetLevels[selected].data.x)return;
    double span=_PreparedPlanetLevels[selected+1].data.x-_PreparedPlanetLevels[selected].data.x;
    double blend=(requestedStep-_PreparedPlanetLevels[selected].data.x)/span;if(blend<=0)return;
    double upperHeight;double2 upperGradient,upperNormalGradient;uint upperReady;float upperVariance;
    PPSourceLevel(selected+1,p,baseHeight,baseGradient,baseNormalGradient,upperHeight,upperGradient,upperNormalGradient,upperReady,upperVariance);
    footprintDerivative=(upperHeight-height)/span;height=height*(1-blend)+upperHeight*blend;
    gradient=gradient*(1-blend)+upperGradient*blend;normalGradient=normalGradient*(1-blend)+upperNormalGradient*blend;
    variance=(float)((double)variance*(1-blend)+(double)upperVariance*blend);ready&=upperReady;
#else
    // A material/backend mismatch must never turn absent data into ready terrain.
    height=0;gradient=0;normalGradient=0;ready=0;variance=0;footprintDerivative=0;
#endif
}
void PPFrame(double3 d,out double3 east,out double3 north){double h=PPSqrt(d.x*d.x+d.z*d.z);east=h>1e-15L?double3(-d.z/h,0,d.x/h):double3(0,0,1);north=PPCross(east,d);}
void PPFoundation(double3 d,out double height,out double2 gradient)
{
    double radius=_PreparedPlanetParameters[0].x;int nx=(int)_PreparedPlanetParameters[0].y,ny=nx/2,width=nx+1;double horizontal=PPSqrt(d.x*d.x+d.z*d.z);
    double u=PPAbs(d.y)>1-1e-15L?0:PPAtan2(d.z,d.x)/(2*PPPi)+.5L;u-=PPFloor(u);
    double px=u*nx,py=PPClamp((PPAtan2(d.y,horizontal)/PPPi+.5L)*ny,0,(double)ny);int x=min(nx-1,(int)px),y=min(ny-1,(int)py);double tx=px-x,ty=py-y;int at=y*width+x;
    double a=_PreparedPlanetFoundation[at],b=_PreparedPlanetFoundation[at+1],c=_PreparedPlanetFoundation[at+width],e=_PreparedPlanetFoundation[at+width+1];
    height=(a*(1-tx)+b*tx)*(1-ty)+(c*(1-tx)+e*tx)*ty;
    gradient=double2(horizontal>1e-15L?((b-a)*(1-ty)+(e-c)*ty)*nx/(2*PPPi*radius*horizontal):0,((c-a)*(1-tx)+(e-b)*tx)*ny/(PPPi*radius));
}
int PPCell(double3 d)
{
    int face;double2 uv;double3 a=double3(PPAbs(d.x),PPAbs(d.y),PPAbs(d.z));
    if(a.x>=a.y&&a.x>=a.z){face=d.x>=0?0:1;uv=d.yz/a.x;}else if(a.y>=a.z){face=d.y>=0?2:3;uv=d.xz/a.y;}else{face=d.z>=0?4:5;uv=d.xy/a.z;}
    int n=(int)_PreparedPlanetParameters[0].z;int2 xy=clamp(int2(PPFloor((uv.x+1)*(.5L*n)),PPFloor((uv.y+1)*(.5L*n))),0,n-1);return(face*n+xy.y)*n+xy.x;
}
float PPContourSample(uint first,uint index)
{
    double4 row=_PreparedPlanetParameters[first+(index>>3)];uint component=(index>>1)&3u;
    double packed=component==0?row.x:component==1?row.y:component==2?row.z:row.w;uint low,high;asuint(packed,low,high);return asfloat((index&1u)==0?low:high);
}
// Offline-ready radius lookup. No procedural noise or terrain generator.
double PPPlacementSupport(uint source,double2 relative,double radius,double feather,out double2 gradient)
{
    double distance=PPSqrt(relative.x*relative.x+relative.y*relative.y),edge=radius;double2 edgeGradient=0;
    double4 range=0;if(_PreparedPlanetParameters[1].w>0)range=_PreparedPlanetParameters[2+source];
    if(range.y==0){double t=PPClamp((radius-distance)/feather,0,1);gradient=distance>0?relative*(-30*t*t*(t-1)*(t-1)/(distance*feather)):0;return t*t*t*(t*(t*6-15)+10);}
    if(range.y>0){
        if(distance==0){gradient=0;return 1;}
        double at=(PPAtan2(relative.y,relative.x)/(2*PPPi)+.5L)*range.y;at-=PPFloor(at/(double)range.y)*range.y;
        uint i=(uint)at,n=(uint)range.y,start=(uint)range.x;double t=at-i,a=PPContourSample(start,i),b=PPContourSample(start,(i+1)%n);
        edge=radius*(a+(b-a)*t*t*(3-2*t));double da=(b-a)*6*t*(1-t)*n/(2*PPPi);
        edgeGradient=radius*da*double2(-relative.y,relative.x)/(distance*distance);feather=radius*range.z;
    }
    double t=PPClamp((edge-distance)/feather,0,1),derivative=30*t*t*(t-1)*(t-1)/feather;
    gradient=(edgeGradient-(distance>0?relative/distance:(double2)0))*derivative;
    return t*t*t*(t*(t*6-15)+10);
}
PreparedPlanetSurface PreparedPlanetSample(double3 direction,double worldFootprint)
{
    PreparedPlanetSurface result=(PreparedPlanetSurface)0;double3 d=PPNormalize(direction),east,north;PPFrame(d,east,north);
    double baseHeight;double2 baseGradient;PPFoundation(d,baseHeight,baseGradient);double sum=0,product=1,weighted=0;double2 ds=0,dp=0,dweighted=0,dnormal=0;uint ready=1;
    double radius=_PreparedPlanetParameters[0].x;int cell=PPCell(d),begin=_PreparedPlanetOffsets[cell],end=_PreparedPlanetOffsets[cell+1];
    // CSR is the complete conservative saved index. There is no contributor cap.
    [loop]for(int i=begin;i<end;i++){
        PreparedPlanetPlacement p=_PreparedPlanetPlacements[_PreparedPlanetContributors[i]];double denominator=PPDot(d,p.centreRadius.xyz);if(denominator<=0)continue;
        double rx=PPDot(d,p.rightScale.xyz),rz=PPDot(d,p.forwardAmplitude.xyz);double2 local=double2(rx,rz)*(radius/denominator);if(local.x*local.x+local.y*local.y>=p.centreRadius.w*p.centreRadius.w)continue;
        double2 source=p.cropReference.xy+local*p.rightScale.w,relative=source-p.cropReference.xy;double distance=PPSqrt(relative.x*relative.x+relative.y*relative.y);
        double2 supportSlope;double weight=PPPlacementSupport(p.metadata.x,relative,p.cropReference.z,p.featherHeading.x,supportSlope);if(weight==0)continue;
        double2 maskGradient;double mask=PPSourceBlend(p.metadata.x,source,maskGradient);supportSlope=supportSlope*mask+weight*maskGradient;weight*=mask;if(weight==0)continue;
        double inverse=1/(denominator*denominator),scale=p.rightScale.w;double2 dc=double2(PPDot(p.centreRadius.xyz,east),PPDot(p.centreRadius.xyz,north));
        double2 jx=scale*inverse*(double2(PPDot(p.rightScale.xyz,east),PPDot(p.rightScale.xyz,north))*denominator-rx*dc);
        double2 jz=scale*inverse*(double2(PPDot(p.forwardAmplitude.xyz,east),PPDot(p.forwardAmplitude.xyz,north))*denominator-rz*dc);
        double h,footprintDerivative;double2 g,ng;uint sourceReady;float variance;PPSource(p.metadata.x,source,worldFootprint*scale*inverse,h,g,ng,sourceReady,variance,footprintDerivative);ready&=sourceReady;
        double2 footprintSlope=-2*worldFootprint*scale/(radius*denominator*denominator*denominator)*dc;
        double value=(h-p.cropReference.w)*p.forwardAmplitude.w;double2 slope=(jx*g.x+jz*g.y+footprintDerivative*footprintSlope)*p.forwardAmplitude.w,dw=jx*supportSlope.x+jz*supportSlope.y;
        double2 normalSlope=(jx*ng.x+jz*ng.y+footprintDerivative*footprintSlope)*p.forwardAmplitude.w;
        dp=dp*(1-weight)-product*dw;product*=1-weight;sum+=weight;ds+=dw;weighted+=weight*value;dweighted+=weight*slope+value*dw;dnormal+=weight*normalSlope+value*dw;
    }
    double z=product+sum;result.height=baseHeight+weighted/z;result.gradient=baseGradient+(dweighted*z-weighted*(dp+ds))/(z*z);
    double2 normalSlope=baseGradient+(dnormal*z-weighted*(dp+ds))/(z*z);
    double radialScale=radius/(radius+result.height);result.normal=(float3)PPNormalize(d-radialScale*(east*normalSlope.x+north*normalSlope.y));result.ready=ready;return result;
}
#if !defined(SHADER_STAGE_COMPUTE)
bool PreparedPlanetFragmentNormal(float3 localOffset,float3 fallback,out float3 normal,out float variance)
{
    normal=fallback;variance=0;if(_PreparedPlanetEnabled<.5)return false;
    double3 anchor=(double3)_PreparedPlanetAnchorHigh.xyz+(double3)_PreparedPlanetAnchorLow.xyz;
    float3 offset=mul((float3x3)_PreparedPlanetLocalToPlanet,localOffset);
    float footprint=max(length(ddx(offset)),length(ddy(offset)));PreparedPlanetSurface value=PreparedPlanetSample(anchor+(double3)offset,(double)footprint);
    if(value.ready==0)return false;normal=normalize(mul((float3x3)_PreparedPlanetPlanetToLocal,value.normal));return true;
}
#endif
#endif
