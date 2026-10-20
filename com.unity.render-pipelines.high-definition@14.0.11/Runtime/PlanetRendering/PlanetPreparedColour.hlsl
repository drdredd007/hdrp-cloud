#ifndef SPACERUNNER_PREPARED_COLOUR_INCLUDED
#define SPACERUNNER_PREPARED_COLOUR_INCLUDED
#if defined(_PREPARED_COLOUR) && defined(SPACERUNNER_PREPARED_GRAPHICS_ACTIVE)
Texture2D<float4> _PreparedColourTexture;
float4 _PreparedColourMips[15];
int _PreparedColourMipCount,_PreparedColourSourceMask;
float _PreparedColourEnabled,_PreparedColourUnlit;
float3 PPColourBilinear(double2 position,double width,int level)
{
    int n=(int)_PreparedColourMips[level].x;if(n==1)return _PreparedColourTexture.Load(int3(0,0,level)).rgb;
    double2 q=double2(PPClamp(position.x/width+.5L,0,1),PPClamp(position.y/width+.5L,0,1))*(double)(n-1);int2 xy=min(n-2,(int2)q);float2 f=(float2)(q-(double2)xy);
    int2 a=xy,b=xy+int2(1,0),c=xy+int2(0,1),e=xy+1;
    return lerp(lerp(_PreparedColourTexture.Load(int3(a,level)).rgb,_PreparedColourTexture.Load(int3(b,level)).rgb,f.x),lerp(_PreparedColourTexture.Load(int3(c,level)).rgb,_PreparedColourTexture.Load(int3(e,level)).rgb,f.x),f.y);
}
float3 PPColourFiltered(double2 position,double width,double footprint)
{
    int level=0;[loop]while(level+1<_PreparedColourMipCount&&width/(double)_PreparedColourMips[level+1].x<=footprint)level++;
    float3 a=PPColourBilinear(position,width,level);double lower=width/(double)_PreparedColourMips[level].x;
    if(level+1>=_PreparedColourMipCount||footprint<=lower)return a;double upper=width/(double)_PreparedColourMips[level+1].x;
    return lerp(a,PPColourBilinear(position,width,level+1),(float)PPClamp((footprint-lower)/(upper-lower),0,1));
}
float3 PlanetPreparedColour(float3 localOffset,float3 baseAlbedo)
{
    if(_PreparedColourEnabled<.5||_PreparedPlanetEnabled<.5)return baseAlbedo;
    double3 anchor=(double3)_PreparedPlanetAnchorHigh.xyz+(double3)_PreparedPlanetAnchorLow.xyz;float3 offset=mul((float3x3)_PreparedPlanetLocalToPlanet,localOffset);
    double3 d=PPNormalize(anchor+(double3)offset);double radius=_PreparedPlanetParameters[0].x;double footprint=(double)max(length(ddx(offset)),length(ddy(offset)));
    int cell=PPCell(d),begin=_PreparedPlanetOffsets[cell],end=_PreparedPlanetOffsets[cell+1];double sum=0,product=1;double3 weighted=0;
    // Same complete ordered CSR, gnomonic source transform, radius support and
    // ready source blend texture as height composition. No height/normal query.
    [loop]for(int i=begin;i<end;i++){
        PreparedPlanetPlacement p=_PreparedPlanetPlacements[_PreparedPlanetContributors[i]];double denominator=PPDot(d,p.centreRadius.xyz);if(denominator<=0)continue;
        double rx=PPDot(d,p.rightScale.xyz),rz=PPDot(d,p.forwardAmplitude.xyz);double2 local=double2(rx,rz)*(radius/denominator);if(local.x*local.x+local.y*local.y>=p.centreRadius.w*p.centreRadius.w)continue;
        double2 source=p.cropReference.xy+local*p.rightScale.w,relative=source-p.cropReference.xy;double distance=PPSqrt(relative.x*relative.x+relative.y*relative.y);
        double2 ignoredSupportSlope;double weight=PPPlacementSupport(p.metadata.x,relative,p.cropReference.z,p.featherHeading.x,ignoredSupportSlope);if(weight==0)continue;
        double2 unused;weight*=PPSourceBlend(p.metadata.x,source,unused);if(weight==0)continue;
        float3 rgb=baseAlbedo;if((_PreparedColourSourceMask&(1<<(int)p.metadata.x))!=0){PreparedPlanetSourceGpu s=_PreparedPlanetSources[p.metadata.x];rgb=PPColourFiltered(source,s.data.x,footprint*p.rightScale.w/(denominator*denominator));}
        product*=1-weight;sum+=weight;weighted+=weight*(double3)rgb;
    }
    // Convex extension of the existing P+S spatial support: full single source
    // is exact RGB, outside is exact base, overlap is independent of altitude.
    return (float3)((product*(double3)baseAlbedo+weighted)/(product+sum));
}
bool PlanetPreparedColourIsUnlit(){return _PreparedColourEnabled>.5&&_PreparedColourUnlit>.5;}
#else
float3 PlanetPreparedColour(float3 offset,float3 baseAlbedo){return baseAlbedo;}
bool PlanetPreparedColourIsUnlit(){return false;}
#endif
#endif
