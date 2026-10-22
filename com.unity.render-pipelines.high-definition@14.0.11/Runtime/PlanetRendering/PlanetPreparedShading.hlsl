#ifndef SPACERUNNER_PREPARED_SHADING_INCLUDED
#define SPACERUNNER_PREPARED_SHADING_INCLUDED
// Appearance-only native-map evaluator. Planet/placement addressing stays FP64;
// interpolation and the local lighting frame use FP32. Canonical queries and
// mesh generation continue to use PreparedPlanetSample without this path.
#if defined(_PREPARED_BACKEND_NATIVE) && !defined(PP_REFERENCE_SHADING) && (!defined(SHADER_STAGE_COMPUTE) || defined(PP_SHADING_PROBE))
#define PP_NATIVE_SHADING 1
float PSTextureBilinear(int selected,int level,int kind,double width,double2 sourcePosition,out float2 gradient)
{
    int4 mip=_PreparedPlanetWholeMips[selected];int n=mip.y;if(n==1){gradient=0;return (double)PPTextureLoad(mip.x,kind,int3(0,0,level));}
    double2 uv=double2(PPClamp(sourcePosition.x/width+.5f,0,1),PPClamp(sourcePosition.y/width+.5f,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);float2 t=(float2)(uv-xy);
    float a=PPTextureLoad(mip.x,kind,int3(xy,level)),b=PPTextureLoad(mip.x,kind,int3(xy+int2(1,0),level)),c=PPTextureLoad(mip.x,kind,int3(xy+int2(0,1),level)),d=PPTextureLoad(mip.x,kind,int3(xy+1,level));
    gradient=float2((b-a)*(1-t.y)+(d-c)*t.y,(c-a)*(1-t.x)+(d-b)*t.x)*((n-1)/(float)width);
    return (a*(1-t.x)+b*t.x)*(1-t.y)+(c*(1-t.x)+d*t.x)*t.y;
}
float PSTextureHeightFiltered(int first,int count,double width,double2 sourcePosition,double footprint,out float2 gradient,out float footprintDerivative)
{
    int selected=first;[loop]for(int i=1;i<count;i++){if(width/(double)_PreparedPlanetWholeMips[first+i].y>footprint)break;selected=first+i;}
    float a=PSTextureBilinear(selected,selected-first,0,width,sourcePosition,gradient),lower=(float)width/(float)_PreparedPlanetWholeMips[selected].y;footprintDerivative=0;
    if(selected+1>=first+count||footprint<=lower)return a;
    float span=(float)width/(float)_PreparedPlanetWholeMips[selected+1].y-lower,t=(float)(footprint-lower)/span;float2 upperGradient;
    float b=PSTextureBilinear(selected+1,selected+1-first,0,width,sourcePosition,upperGradient);gradient=gradient*(1-t)+upperGradient*t;footprintDerivative=(b-a)/span;return a*(1-t)+b*t;
}
float3 PSTextureNormalMeanBilinear(int selected,int level,double width,double2 sourcePosition,int normalProfile)
{
    int4 mip=_PreparedPlanetWholeMips[selected];int n=mip.y;
    if(n==1)return PPTextureNormalMeanTexel(mip.x,int3(0,0,level),normalProfile);
    double2 uv=double2(PPClamp(sourcePosition.x/width+.5f,0,1),PPClamp(sourcePosition.y/width+.5f,0,1))*(n-1);int2 xy=min(n-2,(int2)uv);float2 t=(float2)(uv-xy);
    // Both textures use the same logical integer node. Their storage widths
    // can differ: RGHalf4097 and the retained R16mean4100 require no shared UV.
    float3 a=PPTextureNormalMeanTexel(mip.x,int3(xy,level),normalProfile),b=PPTextureNormalMeanTexel(mip.x,int3(xy+int2(1,0),level),normalProfile);
    float3 c=PPTextureNormalMeanTexel(mip.x,int3(xy+int2(0,1),level),normalProfile),d=PPTextureNormalMeanTexel(mip.x,int3(xy+1,level),normalProfile);
    return (a*(1-t.x)+b*t.x)*(1-t.y)+(c*(1-t.x)+d*t.x)*t.y;
}
float4 PSLightingLevel(int selected,int level,int kind,double width,double2 p,int profile) {
    if(kind==1&&profile>=8)return float4(PSTextureNormalMeanBilinear(selected,level,width,p,profile),0);
    return (float4)PPTextureLightingLevel(selected,level,kind,width,p,profile);
}
float4 PSTextureLightingFiltered(int first,int count,int kind,double width,double2 sourcePosition,double footprint,int normalProfile)
{
    int selected=first;[loop]for(int i=1;i<count;i++){if(width/(double)_PreparedPlanetWholeMips[first+i].y>footprint)break;selected=first+i;}
    float4 a=PSLightingLevel(selected,selected-first,kind,width,sourcePosition,normalProfile);double lower=width/(double)_PreparedPlanetWholeMips[selected].y;
    if(selected+1>=first+count||footprint<=lower)return a;
    double span=width/(double)_PreparedPlanetWholeMips[selected+1].y-lower,t=(footprint-lower)/span;
    return a*(1-(float)t)+PSLightingLevel(selected+1,selected+1-first,kind,width,sourcePosition,normalProfile)*(float)t;
}
float PSPlacementSupport(uint source,float2 relative,float radius,float feather,out float2 gradient)
{
    float distance=sqrt(relative.x*relative.x+relative.y*relative.y),edge=radius;float2 edgeGradient=0;
    float4 range=0;if(_PreparedPlanetParameters[1].w>0)range=_PreparedPlanetParameters[2+source];
    if(range.y==0){float t=clamp((radius-distance)/feather,0,1);gradient=distance>0?relative*(-30*t*t*(t-1)*(t-1)/(distance*feather)):0;return saturate(t*t*t*(t*(t*6-15)+10));}
    if(range.y>0){
        if(distance==0){gradient=0;return 1;}
        float at=(atan2(relative.y,relative.x)/(2*3.14159265358979323846f)+.5f)*range.y;at-=floor(at/(float)range.y)*range.y;
        uint i=(uint)at,n=(uint)range.y,start=(uint)range.x;float t=at-i,a=PPContourSample(start,i),b=PPContourSample(start,(i+1)%n);
        edge=radius*(a+(b-a)*t*t*(3-2*t));float da=(b-a)*6*t*(1-t)*n/(2*3.14159265358979323846f);
        edgeGradient=radius*da*float2(-relative.y,relative.x)/(distance*distance);feather=radius*range.z;
    }
    float t=clamp((edge-distance)/feather,0,1),derivative=30*t*t*(t-1)*(t-1)/feather;
    gradient=(edgeGradient-(distance>0?relative/distance:(float2)0))*derivative;
    return saturate(t*t*t*(t*(t*6-15)+10));
}

// atan's twice-reduced argument is at most tan(pi/16). Nine odd terms
// bound the omitted angular term by 4*t^19/19 < 1.1e-14 radians.
// These square roots only receive [1,2]; two double Newton steps refine a
// hardware float seed. Canonical correctly-rounded math remains untouched.
precise double PSSqrtUnit(double x) {precise double r=(double)sqrt((float)x);r=.5L*(r+x/r);return .5L*(r+x/r);}
precise double PSAtanUnit(double x) {
    precise double t=x/(1+PSSqrtUnit(1+x*x));t=t/(1+PSSqrtUnit(1+t*t));
    precise double z=-t*t,p=1.0L/17.0L;[unroll]for(int k=7;k>=0;k--)p=1.0L/(double)(2*k+1)+z*p;return 4*t*p;
}
precise double PSAtan(double x){double a=PPAbs(x),r=a>1?PPPi*.5L-PSAtanUnit(1/a):PSAtanUnit(a);return x<0?-r:r;}
precise double PSAtan2(double y,double x) {
    uint yl,yh,xl,xh;asuint(y,yl,yh);asuint(x,xl,xh);bool negative=(yh&0x80000000u)!=0;
    if(y==0)return (xh&0x80000000u)!=0?(negative?-PPPi:PPPi):y;
    if(x>0)return PSAtan(y/x);if(x<0)return PSAtan(y/x)+(negative?-PPPi:PPPi);return negative?-PPPi*.5L:PPPi*.5L;
}
void PSFoundation(double3 d,out double height,out double2 gradient) {
    double radius=_PreparedPlanetParameters[0].x;int nx=(int)_PreparedPlanetParameters[0].y,ny=nx/2,width=nx+1;double horizontal=PPSqrt(d.x*d.x+d.z*d.z);
    double u=PPAbs(d.y)>1-1e-15L?0:PSAtan2(d.z,d.x)/(2*PPPi)+.5L;u-=PPFloor(u);
    double px=u*nx,py=PPClamp((PSAtan2(d.y,horizontal)/PPPi+.5L)*ny,0,(double)ny);int x=min(nx-1,(int)px),y=min(ny-1,(int)py);double tx=px-x,ty=py-y;
    // Avoid choosing the opposite derivative at a foundation cell boundary.
    if(PPMin(PPMin(tx,1-tx),PPMin(ty,1-ty))<1e-7L){PPFoundation(d,height,gradient);return;}
    int at=y*width+x;double a=_PreparedPlanetFoundation[at],b=_PreparedPlanetFoundation[at+1],c=_PreparedPlanetFoundation[at+width],e=_PreparedPlanetFoundation[at+width+1];
    height=(a*(1-tx)+b*tx)*(1-ty)+(c*(1-tx)+e*tx)*ty;
    gradient=double2(horizontal>1e-15L?((b-a)*(1-ty)+(e-c)*ty)*nx/(2*PPPi*radius*horizontal):0,((c-a)*(1-tx)+(e-b)*tx)*ny/(PPPi*radius));
}
struct PreparedPlanetShadingResult {float3 normal;float radialHeight;};
PreparedPlanetShadingResult PreparedPlanetShadingSample(double3 direction,double worldFootprint)
{
    PreparedPlanetShadingResult result=(PreparedPlanetShadingResult)0;
    double3 d=PPNormalize(direction);float3 radial=(float3)d;
    float horizontal=length(radial.xz);float3 east=horizontal>1e-15f?float3(-radial.z/horizontal,0,radial.x/horizontal):float3(0,0,1);
    float3 north=cross(east,radial);double exactHeight;double2 exactGradient;PSFoundation(d,exactHeight,exactGradient);float baseHeight=(float)exactHeight;float2 baseGradient=(float2)exactGradient;
    float sum=0,product=1,weighted=0;float2 ds=0,dp=0,dnormal=0;
    double radius=_PreparedPlanetParameters[0].x;int cell=PPCell(d),begin=_PreparedPlanetOffsets[cell],end=_PreparedPlanetOffsets[cell+1];
    [loop]for(int i=begin;i<end;i++) {
        PreparedPlanetPlacement p=_PreparedPlanetPlacements[_PreparedPlanetContributors[i]];
        double denominator=PPDot(d,p.centreRadius.xyz);if(denominator<=0)continue;
        double rx=PPDot(d,p.rightScale.xyz),rz=PPDot(d,p.forwardAmplitude.xyz);
        double2 local=double2(rx,rz)*(radius/denominator);if(dot(local,local)>=p.centreRadius.w*p.centreRadius.w)continue;
        double2 source=p.cropReference.xy+local*p.rightScale.w,relative=source-p.cropReference.xy;
        float2 supportSlope;float weight=PSPlacementSupport(p.metadata.x,(float2)relative,(float)p.cropReference.z,(float)p.featherHeading.x,supportSlope);if(weight==0)continue;
        PreparedPlanetSourceGpu s=_PreparedPlanetSources[p.metadata.x];
        float2 maskGradient=0;float mask=s.levels.w==0?1:PSTextureBilinear(s.levels.z,0,3,s.data.x,source,maskGradient);
        supportSlope=supportSlope*mask+weight*maskGradient;weight*=mask;if(weight==0)continue;
        float den=(float)denominator,scale=(float)p.rightScale.w,inverse=1/(den*den);
        float2 dc=float2(dot((float3)p.centreRadius.xyz,east),dot((float3)p.centreRadius.xyz,north));
        float2 jx=scale*inverse*(float2(dot((float3)p.rightScale.xyz,east),dot((float3)p.rightScale.xyz,north))*den-(float)rx*dc);
        float2 jz=scale*inverse*(float2(dot((float3)p.forwardAmplitude.xyz,east),dot((float3)p.forwardAmplitude.xyz,north))*den-(float)rz*dc);
        double footprint=worldFootprint*p.rightScale.w/(denominator*denominator);
        float2 unused;float fd;float height=PSTextureHeightFiltered(s.grids.x,s.grids.y,s.data.x,source,footprint,unused,fd);
        float3 mean=PSTextureLightingFiltered(s.grids.z,s.grids.w,1,s.data.x,source,footprint,(int)s.data.y).xyz;
        float2 ng=-mean.xz/max(mean.y,1e-20f);
        float2 footprintSlope=(-2*(float)worldFootprint*scale/((float)radius*den*den*den))*dc;
        float value=(height-(float)p.cropReference.w)*(float)p.forwardAmplitude.w;
        float2 dw=jx*supportSlope.x+jz*supportSlope.y;
        float2 slope=(jx*ng.x+jz*ng.y+fd*footprintSlope)*(float)p.forwardAmplitude.w;
        dp=dp*(1-weight)-product*dw;product*=1-weight;sum+=weight;ds+=dw;weighted+=weight*value;dnormal+=weight*slope+value*dw;
    }
    float z=product+sum,height=baseHeight+weighted/z;
    float2 slope=baseGradient+(dnormal*z-weighted*(dp+ds))/(z*z);
    result.radialHeight=height;result.normal=normalize(radial-((float)radius/((float)radius+height))*(east*slope.x+north*slope.y));
    return result;
}
#endif
#endif
