#ifndef PLANET_SURFACE_PAINT_INCLUDED
#define PLANET_SURFACE_PAINT_INCLUDED
TEXTURE2D(_PlanetPaintCurvature);TEXTURE2D(_PlanetPaintPalette);SAMPLER(sampler_LinearClamp);
float _PlanetPaintEnabled;
float4 _PlanetPaintFeatureGrid,_PlanetPaintAnchor,_PlanetPaintSlope,_PlanetPaintTerrain,_PlanetPaintColor;
float4 _PlanetPaintPhase,_PlanetPaintCell,_PlanetPaintGrainPhase,_PlanetPaintGrainCell,_PlanetPaintSeed;
uint PlanetPaintHash(uint3 cell,uint seed)
{
    cell&=65535u;uint h=cell.x*73856093u^cell.y*19349663u^cell.z*83492791u^seed;
    h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;return h^(h>>16);
}
float PlanetPaintNoise(float3 offset,float4 phase,float4 cell,uint seed,bool filtered)
{
    float3 p=offset*phase.w+phase.xyz;int3 local=(int3)floor(p);float3 t=frac(p);t=t*t*t*(t*(t*6-15)+10);float value=0;
    [unroll]for(uint z=0;z<2;z++)[unroll]for(uint y=0;y<2;y++)[unroll]for(uint x=0;x<2;x++) {
        uint h=PlanetPaintHash((uint3)((int3)cell.xyz+local+int3(x,y,z)),seed);
        float w=(x?t.x:1-t.x)*(y?t.y:1-t.y)*(z?t.z:1-t.z);value+=((h&65535u)/32767.5-1)*w;
    }
    if(filtered)value*=saturate(1-max(length(ddx(p)),length(ddy(p)))*2);
    return value;
}
float PlanetPaintCurvature(float3 direction)
{
    float2 uv=float2(atan2(direction.z,direction.x)*0.159154943091895+.5,asin(clamp(direction.y,-1,1))*0.31830988618379+.5);
    uv=(uv*_PlanetPaintFeatureGrid.xy+.5)/_PlanetPaintFeatureGrid.zw;
    return SAMPLE_TEXTURE2D_LOD(_PlanetPaintCurvature,sampler_LinearClamp,uv,0).r;
}
float4 PlanetPaintWeights(float3 offset,float3 normalPlanet,float3 direction,float noise)
{
    float slope=acos(clamp(dot(normalPlanet,direction),0,1))*57.29577951308232;
    float response=smoothstep(_PlanetPaintSlope.x,_PlanetPaintSlope.y,slope);
    float curvature=clamp(PlanetPaintCurvature(direction)/_PlanetPaintTerrain.z,-1,1);
    float sediment=saturate(_PlanetPaintSlope.z-_PlanetPaintSlope.w*response-_PlanetPaintTerrain.x*max(0,-curvature)+
        _PlanetPaintTerrain.y*max(0,curvature)+_PlanetPaintTerrain.w*noise);
    return float4(0,sediment,1-sediment,0);
}
float3 PlanetPaintModulation(float4 weights,float noise,float grain)
{
    float u=((noise*.5+.5)*255+.5)/256;
    float3 rock=SAMPLE_TEXTURE2D_LOD(_PlanetPaintPalette,sampler_LinearClamp,float2(u,.25),0).rgb;
    float3 sediment=SAMPLE_TEXTURE2D_LOD(_PlanetPaintPalette,sampler_LinearClamp,float2(u,.75),0).rgb;
    return lerp(1,rock*weights.z+sediment*weights.y,_PlanetPaintColor.x)*(1+grain*_PlanetPaintColor.y);
}
#endif
