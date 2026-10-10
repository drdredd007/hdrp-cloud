#ifndef PLANET_REGIONAL_COLOR_INCLUDED
#define PLANET_REGIONAL_COLOR_INCLUDED
struct PlanetRegionalColorRegion {float4 anchor,right,forward,domain;};
StructuredBuffer<PlanetRegionalColorRegion> _PlanetRegionalColorRegions;
TEXTURE2D_ARRAY(_PlanetRegionalColorMaps);SAMPLER(sampler_PlanetRegionalColorMaps);
TEXTURE2D(_PlanetRegionalColorPalette);SAMPLER(sampler_PlanetRegionalColorPalette);
int _PlanetRegionalColorCount;
float3 _PlanetRegionalColorAnchor;
float4 _PlanetRegionalColorControls,_PlanetRegionalColorMetric,_PlanetRegionalColorTexels;
float3 _PlanetRegionalColorPaletteCoordinates;
float3 PlanetRegionalPalette(float t)
{
    float width,height;_PlanetRegionalColorPalette.GetDimensions(width,height);
    return SAMPLE_TEXTURE2D_LOD(_PlanetRegionalColorPalette,sampler_PlanetRegionalColorPalette,float2((saturate(t)*(width-1)+.5)/width,.5),0).rgb;
}
float3 PlanetRegionalColorApply(float3 albedo,float3 offset,float3 globalTone,float detailLuminance)
{
    if(_PlanetRegionalColorCount==0)return albedo;
    float3 d=_PlanetRegionalColorAnchor+offset*_PlanetRegionalColorMetric.x;
    float3 dx=ddx(d),dy=ddy(d);
    int selected=-1;float2 uv=0,du=0,dv=0;float edge=0;
    [loop]for(int i=0;i<_PlanetRegionalColorCount;i++) {
        PlanetRegionalColorRegion r=_PlanetRegionalColorRegions[i];
        float den=dot(d,r.anchor.xyz);if(den<=0)continue;
        float2 num=float2(dot(d,r.right.xyz),dot(d,r.forward.xyz));
        float2 m=num*(r.anchor.w/den),q=(m-r.domain.xy)/r.domain.zw;
        if(any(q<0)||any(q>1))continue;
        selected=i;uv=q;
        du=(float2(dot(dx,r.right.xyz),dot(dx,r.forward.xyz))*den-num*dot(dx,r.anchor.xyz))*(r.anchor.w/(den*den))/r.domain.zw;
        dv=(float2(dot(dy,r.right.xyz),dot(dy,r.forward.xyz))*den-num*dot(dy,r.anchor.xyz))*(r.anchor.w/(den*den))/r.domain.zw;
        float2 boundary=min(q,1-q)*r.domain.zw;
        edge=smoothstep(0,max(_PlanetRegionalColorMetric.y,1),min(boundary.x,boundary.y));
    }
    if(selected<0)return albedo;
    PlanetRegionalColorRegion r=_PlanetRegionalColorRegions[selected];
    float4 data=SAMPLE_TEXTURE2D_ARRAY_GRAD(_PlanetRegionalColorMaps,sampler_PlanetRegionalColorMaps,
        uv*_PlanetRegionalColorTexels.xy+_PlanetRegionalColorTexels.zw,r.right.w,du*_PlanetRegionalColorTexels.xy,dv*_PlanetRegionalColorTexels.xy);
    float3 color=data.rgb;
    if(_PlanetRegionalColorControls.w<.5)color=PlanetRegionalPalette(data.r);
    else if(_PlanetRegionalColorControls.w>1.5) {
        float3 weights=max(data.rgb,0);weights/=max(dot(weights,1),1e-6);
        color=PlanetRegionalPalette(_PlanetRegionalColorPaletteCoordinates.x)*weights.x+
              PlanetRegionalPalette(_PlanetRegionalColorPaletteCoordinates.y)*weights.y+
              PlanetRegionalPalette(_PlanetRegionalColorPaletteCoordinates.z)*weights.z;
    }
    color*=lerp(1,globalTone,_PlanetRegionalColorControls.y);
    color*=lerp(1,clamp(detailLuminance/.21404114,.25,4),_PlanetRegionalColorControls.z);
    return lerp(albedo,color,_PlanetRegionalColorControls.x*edge);
}
#endif
