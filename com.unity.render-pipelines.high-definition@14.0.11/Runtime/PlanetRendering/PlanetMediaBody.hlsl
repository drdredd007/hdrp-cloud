#ifndef PLANET_MEDIA_BODY_INCLUDED
#define PLANET_MEDIA_BODY_INCLUDED
// Mirrors PlanetMediaGpuBody. Every member occupies exactly sixteen bytes.
struct PlanetMediaBody
{
    float4 CenterRadius, InverseRotation, Limits;
    float4 AirExtinction, AirScattering, AerosolExtinction, AerosolScattering;
    float4 FogDensity, FogAlbedo, FogColor;
    float4 CloudShape, CloudErosion, CloudScatteringTint, CloudLighting, CloudBottomLighting;
    float4 CloudOffset, Coverage, Climate, SeedWind, CloudWind, CloudMisc, CloudMapTiling;
    float4 TextureMetadata, RegionMetadata;
};
float3 MediaRotate(float4 q,float3 p) { return p+2*cross(q.xyz,cross(q.xyz,p)+q.w*p); }
float MediaOuterHeight(PlanetMediaBody b) { return max(max(b.Limits.x,b.Limits.z),b.Limits.w); }
bool MediaSphereRange(float3 center,float radius,float3 direction,out float2 range)
{
    float along=dot(center,direction);
    float closest=length(center-direction*along);
    float delta=radius-closest;
    if(delta<0 || radius<=0){range=0;return false;}
    float halfChord=sqrt(max(0,delta*(radius+closest)));
    range=float2(along-halfChord,along+halfChord);
    return range.y>0;
}
// The same Chapman approximation and density convention as HDRP PBS, evaluated
// from this body's coefficients instead of a camera-global sky constant buffer.
float MediaChapmanUpper(float z,float cosine)
{
    float n=.761643*((1+2*z)-cosine*cosine*z);
    float d=cosine*z+sqrt(max(1e-20,z*(1.47721+.273828*cosine*cosine*z)));
    return .5*cosine+n/max(d,1e-20);
}
float MediaChapman(float radius,float groundRadius,float height,float cosine)
{
    float z=radius/max(height,.001),Z=groundRadius/max(height,.001);
    float upper=MediaChapmanUpper(z,abs(cosine))*exp(min(0,Z-z));
    if(cosine>=0)return upper;
    float horizontal=max(z*sqrt(saturate(1-cosine*cosine)),1e-10);
    float ch=.626657*(rsqrt(horizontal)+2*sqrt(horizontal));
    return max(0,2*ch*exp(min(0,Z-horizontal))-upper);
}
float3 MediaAirTransmissionToSun(PlanetMediaBody b,float3 p,float3 lightDirection)
{
    float radius=max(length(p),b.CenterRadius.w+.001);
    float cosine=dot(p,lightDirection)/radius;
    float horizon=-sqrt(saturate(1-(b.CenterRadius.w/radius)*(b.CenterRadius.w/radius)));
    if(cosine<horizon)return 0;
    if(b.Limits.x<=0)return 1;
    float3 opticalDepth=b.AirExtinction.rgb*b.AirExtinction.w*
        MediaChapman(radius,b.CenterRadius.w,b.AirExtinction.w,cosine);
    opticalDepth+=b.AerosolExtinction.x*b.AirScattering.w*
        MediaChapman(radius,b.CenterRadius.w,b.AirScattering.w,cosine);
    return exp(-max(0,opticalDepth));
}
float MediaPhase(float cosine,float g)
{
    float g2=g*g;
    return (1-g2)/(4*PI*pow(max(1e-5,1+g2-2*g*cosine),1.5));
}
float3 MediaAirExtinction(PlanetMediaBody b,float height)
{
    if(b.Limits.x<=0 || height>b.Limits.x)return 0;
    return b.AirExtinction.rgb*exp(-max(0,height)/max(b.AirExtinction.w,.001))+
        b.AerosolExtinction.x*exp(-max(0,height)/max(b.AirScattering.w,.001));
}
float3 MediaAirSource(PlanetMediaBody b,float height,float phaseCosine)
{
    if(b.Limits.x<=0 || height>b.Limits.x)return 0;
    float rayleigh=3*(1+phaseCosine*phaseCosine)/(16*PI);
    // Cornette-Shanks, used by PBS for aerosols.
    float g=clamp(b.AerosolExtinction.y,-.99,.99),g2=g*g;
    float mie=3*(1-g2)*(1+phaseCosine*phaseCosine)/
        (8*PI*(2+g2)*pow(max(1e-5,1+g2-2*g*phaseCosine),1.5));
    return b.AirScattering.rgb*exp(-max(0,height)/max(b.AirExtinction.w,.001))*rayleigh+
        b.AerosolScattering.rgb*exp(-max(0,height)/max(b.AirScattering.w,.001))*mie;
}
#endif
