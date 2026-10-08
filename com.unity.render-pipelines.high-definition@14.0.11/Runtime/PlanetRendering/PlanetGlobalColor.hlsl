#ifndef PLANET_GLOBAL_COLOR_INCLUDED
#define PLANET_GLOBAL_COLOR_INCLUDED
TEXTURE2D(_PlanetGlobalColorMap);SAMPLER(sampler_PlanetGlobalColorMap);
float _PlanetGlobalColorBlend,_PlanetGlobalColorDistanceBlend;
float4 _PlanetGlobalColorReference,_PlanetGlobalColorControls;
float4x4 _PlanetGlobalColorWorldToLocal;
float3 _PlanetGlobalColorAnchor;
float _PlanetGlobalColorInverseRadius;
float4 PlanetGlobalColorSample(float3 directionLocal)
{
    float3 d=normalize(directionLocal);
    float2 uv=float2(atan2(d.z,d.x)*(0.5/PI)+0.5,asin(clamp(d.y,-1,1))/PI+0.5);
    float2 dx=ddx(uv),dy=ddy(uv);dx.x-=round(dx.x);dy.x-=round(dy.x);
    return SAMPLE_TEXTURE2D_GRAD(_PlanetGlobalColorMap,sampler_PlanetGlobalColorMap,uv,dx,dy);
}
float3 PlanetApplyGlobalColorLocal(float3 albedo,float3 directionLocal)
{
    if(_PlanetGlobalColorBlend<=0)return albedo;
    float4 map=PlanetGlobalColorSample(directionLocal);
    float3 tint=albedo*map.rgb/max(_PlanetGlobalColorReference.rgb,.01);
    float3 local=lerp(albedo,tint,_PlanetGlobalColorControls.x);
    float3 colored=lerp(local,map.rgb,_PlanetGlobalColorDistanceBlend);
    float weight=_PlanetGlobalColorBlend*lerp(1,saturate(map.a),_PlanetGlobalColorControls.y);
    return lerp(albedo,saturate(colored),weight);
}
float3 PlanetApplyGlobalColorWorld(float3 albedo,float3 radialWorld)
{
    return PlanetApplyGlobalColorLocal(albedo,mul((float3x3)_PlanetGlobalColorWorldToLocal,radialWorld));
}
#endif
