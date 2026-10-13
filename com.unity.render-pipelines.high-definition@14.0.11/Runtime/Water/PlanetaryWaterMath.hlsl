#ifndef HDRP_PLANETARY_WATER_MATH_INCLUDED
#define HDRP_PLANETARY_WATER_MATH_INCLUDED
bool PlanetWaterEnabled(){return _PlanetWaterOptions.x > .5;}
float3 PlanetWaterRotate(float3 p){return p+2*cross(_PlanetWaterRotation.xyz,cross(_PlanetWaterRotation.xyz,p)+_PlanetWaterRotation.w*p);}
float3 PlanetWaterInverseRotate(float3 p){float3 q=-_PlanetWaterRotation.xyz;return p+2*cross(q,cross(q,p)+_PlanetWaterRotation.w*p);}
#if defined(SHADER_API_D3D11)
double3 PlanetWaterOrigin(){return (double3)_PlanetWaterOriginHigh.xyz+(double3)_PlanetWaterOriginLow.xyz;}
double PlanetWaterRadius(){return (double)_PlanetWaterOriginHigh.w+(double)_PlanetWaterOriginLow.w;}
double PlanetWaterDot(double3 a,double3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
double PlanetWaterSqrt(double x){if(x<=0)return 0;double y=(double)sqrt((float)x);y=(y+x/y)*.5;y=(y+x/y)*.5;y=(y+x/y)*.5;return y;}
double3 PlanetWaterUnit(double3 p){return p/PlanetWaterSqrt(PlanetWaterDot(p,p));}
static double3 planetWaterPixelPoint;
static bool planetWaterPixelPointValid=false;
static float3 planetWaterTangentU,planetWaterTangentV;
double3 PlanetWaterLocalPoint(float3 positionAWS)
{
    if(planetWaterPixelPointValid)return planetWaterPixelPoint;
    float3 relative=GetCameraRelativePositionWS(positionAWS)/max(_PlanetWaterOptions.y,1e-8);
    return PlanetWaterOrigin()+(double3)PlanetWaterInverseRotate(relative);
}
float2 PlanetWaterWrappedProjection(float3 positionAWS,int plane,double scale,float2 offset)
{
    double3 p=PlanetWaterLocalPoint(positionAWS);
    double2 projected=plane==0?p.xy:plane==1?p.yz:p.xz;
    double2 uv=(projected-(double2)offset)*scale;
    // Wrapped before float conversion so floating origins cannot reset or quantize wave phase.
    double2 whole=(double2)floor((float2)uv);
    uv-=whole;
    uv-=(double2)floor((float2)uv);
    return (float2)uv;
}
float2 PlanetWaterProjectionUV(float3 positionAWS,int band,int plane)
{
    return PlanetWaterWrappedProjection(positionAWS,plane,1.0L/(double)_PatchSize[band],OrientationToDirection(_PatchCurrentOrientation[band])*_PatchCurrentSpeed[band]*_SimulationTime);
}
float2 PlanetWaterUV(float3 positionAWS,int band){return PlanetWaterProjectionUV(positionAWS,band,band);}
float3 PlanetWaterProjectionWeights(float3 positionAWS)
{
    float3 n=(float3)PlanetWaterUnit(PlanetWaterLocalPoint(positionAWS));
    // Every FFT band uses all three charts. A chart loses weight before its
    // projection becomes edge-on; assigning one chart to a band stretches it at its poles.
    float3 w=n*n;w*=w;w*=w;
    return w.zxy/(w.x+w.y+w.z);
}
bool PlanetWaterIntersect(float3 worldRay,double radius,out double distance)
{
    double3 ray=PlanetWaterUnit((double3)PlanetWaterInverseRotate(worldRay));
    double3 origin=PlanetWaterOrigin();double length=PlanetWaterSqrt(PlanetWaterDot(origin,origin));
    double b=PlanetWaterDot(origin,ray),c=(length-radius)*(length+radius),disc=b*b-c;
    distance=0;if(disc<0)return false;double root=PlanetWaterSqrt(disc);
    double near=-b-root,far=-b+root;distance=near>0?near:far;
    if(distance<=0)return false;
    planetWaterPixelPoint=origin+ray*distance;planetWaterPixelPointValid=true;return true;
}
float3 PlanetWaterBandGradient(float2 gradient,int band)
{
    float3 g=band==0?float3(gradient,0):band==1?float3(0,gradient):float3(gradient.x,0,gradient.y);
    float3 n=(float3)PlanetWaterUnit(planetWaterPixelPoint);
    // WaterSurfaceGradients subtracts this directly from the world-space normal.
    return PlanetWaterRotate(g-n*dot(g,n));
}
#endif
#endif
