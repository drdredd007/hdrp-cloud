#ifndef PLANET_PERIODIC_SURFACE_INCLUDED
#define PLANET_PERIODIC_SURFACE_INCLUDED
TEXTURE2D(_PeriodicSnowCoverage);SAMPLER(sampler_PeriodicSnowCoverage);
TEXTURE2D_ARRAY(_PeriodicDetailColor);SAMPLER(sampler_PeriodicDetailColor);
TEXTURE2D_ARRAY(_PeriodicDetailSlopes);SAMPLER(sampler_PeriodicDetailSlopes);
float _PeriodicSurfaceEnabled;
float4 _PeriodicSnowColor,_PeriodicRockColor,_PeriodicSnowMean,_PeriodicRockMean;
float4 _PeriodicDetailControls,_PeriodicSmoothness;
TEXTURE2D(_PlanetGlobalColorMap);SAMPLER(sampler_PlanetGlobalColorMap);
float _PlanetGlobalColorBlend;
float _PlanetGlobalColorDistanceBlend;
float4 _PlanetGlobalColorReference;
float3 PlanetGlobalColor(float3 radialWorld)
{
    float3 d=normalize(mul((float3x3)_PeriodicWorldToLocal,radialWorld));
    float2 uv=float2(atan2(d.z,d.x)*(0.5/PI)+0.5,asin(clamp(d.y,-1,1))/PI+0.5);
    // Longitude wraps; its derivative must wrap too, otherwise the meridian selects a coarse mip.
    float2 dx=ddx(uv),dy=ddy(uv);dx.x-=round(dx.x);dy.x-=round(dy.x);
    return SAMPLE_TEXTURE2D_GRAD(_PlanetGlobalColorMap,sampler_PlanetGlobalColorMap,uv,dx,dy).rgb;
}
float3 PeriodicSurfaceWeights(float3 d)
{float3 w=d*d;w*=w;w*=w;w*=w;return w/(w.x+w.y+w.z);}
float4 PeriodicDetailColor(float3 p,float3 w,float slice)
{
    return w.x*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.zy,slice)+
        w.y*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.xz,slice)+
        w.z*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.xy,slice);
}
float3 PeriodicDetailGradient(float3 p,float3 w,float slice)
{
    float2 x=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.zy,slice).rg;
    float2 y=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.xz,slice).rg;
    float2 z=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.xy,slice).rg;
    return w.x*float3(0,x.y,x.x)+w.y*float3(y.x,0,y.y)+w.z*float3(z.x,z.y,0);
}
void PeriodicSnowAndRock(float3 radialWorld,float3 detail,inout float3 normal,out float3 albedo,out float smoothness)
{
    float3 d=normalize(mul((float3x3)_PeriodicWorldToLocal,radialWorld));
    float3 w=PeriodicSurfaceWeights(d),p=d*_PeriodicNormalCycles;
    // Coverage is imported from the full-resolution source slope, never from the current mesh normal.
    float snow=saturate(w.x*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.zy).r+
        w.y*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.xz).r+
        w.z*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.xy).r);
    // Detail coordinates use the renderer's periodic planet-local origin, shared by far and near meshes.
    float3 ps=detail/_PeriodicDetailControls.x,pr=detail/_PeriodicDetailControls.y;
    float4 cs=PeriodicDetailColor(ps,w,0),cr=PeriodicDetailColor(pr,w,1);
    float3 snowAlbedo=saturate(_PeriodicSnowColor.rgb*cs.rgb/max(_PeriodicSnowMean.rgb,.001));
    float3 rockAlbedo=saturate(_PeriodicRockColor.rgb*cr.rgb/max(_PeriodicRockMean.rgb,.001));
    albedo=lerp(rockAlbedo,snowAlbedo,snow);
    smoothness=saturate(lerp(_PeriodicSmoothness.y*cr.a,_PeriodicSmoothness.x*cs.a,snow));
    float3 gradient=lerp(PeriodicDetailGradient(pr,w,1)*_PeriodicDetailControls.w,
        PeriodicDetailGradient(ps,w,0)*_PeriodicDetailControls.z,snow);
    float3 n=normalize(mul((float3x3)_PeriodicWorldToLocal,normal));
    gradient-=n*dot(gradient,n);
    normal=normalize(mul((float3x3)_PeriodicLocalToWorld,normalize(n-gradient)));
}
#endif
