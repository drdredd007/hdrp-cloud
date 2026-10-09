#ifndef SPACERUNNER_GEOLOGICAL_NORMAL_INCLUDED
#define SPACERUNNER_GEOLOGICAL_NORMAL_INCLUDED
struct PlanetGeologicalNormalRegion {float4 anchor,right,forward,domain;};
StructuredBuffer<PlanetGeologicalNormalRegion> _PlanetGeologicalNormalRegions;
TEXTURE2D_ARRAY(_PlanetGeologicalNormalMaps);
SAMPLER(sampler_PlanetGeologicalNormalMaps);
int _PlanetGeologicalNormalCount;
float3 _PlanetGeologicalNormalAnchor;
float4 _PlanetGeologicalNormalControls,_PlanetGeologicalNormalTexels;
float3 PlanetGeologicalNormal(float3 offset,float3 meshNormal,out float variance)
{
    variance=0;
    if(_PlanetGeologicalNormalCount==0)return meshNormal;
    float3 direction=_PlanetGeologicalNormalAnchor+offset*_PlanetGeologicalNormalControls.x;
    // Derivatives outside divergent region selection retain a consistent pixel footprint.
    float3 dx=ddx(direction),dy=ddy(direction);
    int selected=-1;float2 uv=0,uvdx=0,uvdy=0;float edge=0;
    [loop]for(int i=0;i<_PlanetGeologicalNormalCount;i++)
    {
        PlanetGeologicalNormalRegion r=_PlanetGeologicalNormalRegions[i];
        float denominator=dot(direction,r.anchor.xyz);
        if(denominator<=0)continue;
        float2 numerator=float2(dot(direction,r.right.xyz),dot(direction,r.forward.xyz));
        float2 metres=numerator*(r.anchor.w/denominator);
        float2 coordinate=(metres-r.domain.xy)/r.domain.zw;
        if(any(coordinate<0)||any(coordinate>1))continue;
        selected=i;uv=coordinate;
        uvdx=(float2(dot(dx,r.right.xyz),dot(dx,r.forward.xyz))*denominator-numerator*dot(dx,r.anchor.xyz))*(r.anchor.w/(denominator*denominator))/r.domain.zw;
        uvdy=(float2(dot(dy,r.right.xyz),dot(dy,r.forward.xyz))*denominator-numerator*dot(dy,r.anchor.xyz))*(r.anchor.w/(denominator*denominator))/r.domain.zw;
        // The bake already includes the true blend-mask derivative. This last texel
        // only joins its finite support to the outside mesh without a hard boundary.
        edge=saturate(min(min(coordinate.x,coordinate.y),min(1-coordinate.x,1-coordinate.y))/max(_PlanetGeologicalNormalTexels.z,_PlanetGeologicalNormalTexels.w));
    }
    if(selected<0)return meshNormal;
    PlanetGeologicalNormalRegion region=_PlanetGeologicalNormalRegions[selected];
    float3 mean=SAMPLE_TEXTURE2D_ARRAY_GRAD(_PlanetGeologicalNormalMaps,sampler_PlanetGeologicalNormalMaps,
        uv*_PlanetGeologicalNormalTexels.xy+_PlanetGeologicalNormalTexels.zw,region.right.w,
        uvdx*_PlanetGeologicalNormalTexels.xy,uvdy*_PlanetGeologicalNormalTexels.xy).xyz;
    float lengthSquared=dot(mean,mean);if(lengthSquared<1e-8)return meshNormal;
    float3 normal=normalize(region.right.xyz*mean.x+region.anchor.xyz*mean.y+region.forward.xyz*mean.z);
    float strength=_PlanetGeologicalNormalControls.y*edge;
    variance=max(1-lengthSquared,0)*_PlanetGeologicalNormalControls.z*strength*strength;
    // Absolute replacement: do not add the same geological slope to its mesh normal twice.
    return normalize(lerp(meshNormal,normal,strength));
}
float PlanetGeologicalSmoothness(float smoothness,float variance)
{
    float roughness=1-saturate(smoothness),alpha=roughness*roughness;
    return 1-sqrt(sqrt(saturate(alpha*alpha+variance)));
}
#endif
