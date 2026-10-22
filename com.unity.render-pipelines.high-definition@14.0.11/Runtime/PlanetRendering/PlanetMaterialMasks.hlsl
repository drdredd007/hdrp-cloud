#ifndef SPACERUNNER_MATERIAL_MASKS_INCLUDED
#define SPACERUNNER_MATERIAL_MASKS_INCLUDED
// Shared baked material roles. Background and placements use the same planet-space materials.
float _PlanetMaterialMaskEnabled;
Texture2D<float4> _PlanetMaterialWeights;
Texture2D<float2> _PlanetMaterialClimate;
SamplerState sampler_PlanetMaterialClimate;
int _PlanetMaterialSourceMask,_PlanetMaterialClimateWidth,_PlanetMaterialClimateHeight,_PlanetMaterialMaskSide;
float2 PMMClimate(float3 direction) {
 float2 uv=float2(atan2(direction.z,direction.x)/(2*PI)+.5,asin(clamp(direction.y,-1,1))/PI+.5);
 float2 q=uv*float2(_PlanetMaterialClimateWidth-1,_PlanetMaterialClimateHeight-1);int2 a=(int2)q,b=min(a+1,int2(_PlanetMaterialClimateWidth-1,_PlanetMaterialClimateHeight-1));float2 f=q-a;
 return lerp(lerp(_PlanetMaterialClimate.Load(int3(a,0)),_PlanetMaterialClimate.Load(int3(b.x,a.y,0)),f.x),lerp(_PlanetMaterialClimate.Load(int3(a.x,b.y,0)),_PlanetMaterialClimate.Load(int3(b,0)),f.x),f.y);
}
float4 PMMBackground(float3 direction) {
 float2 climate=PMMClimate(direction);
 float snow=1-smoothstep(-5,2,climate.x),dry=1-smoothstep(180,650,climate.y);
 return float4((1-snow)*(1-dry),(1-snow)*dry,0,snow);
}
#if defined(SPACERUNNER_PREPARED_GRAPHICS_ACTIVE)
float4 PMMSampleLevel(double2 source,double width,int level) {
 int n=max(1,_PlanetMaterialMaskSide>>level);if(n==1)return _PlanetMaterialWeights.Load(int3(0,0,level));
 double2 q=double2(PPClamp(source.x/width+.5L,0,1),PPClamp(source.y/width+.5L,0,1))*(n-1);int2 a=min(n-2,(int2)q);float2 f=(float2)(q-a);
 return lerp(lerp(_PlanetMaterialWeights.Load(int3(a,level)),_PlanetMaterialWeights.Load(int3(a+int2(1,0),level)),f.x),lerp(_PlanetMaterialWeights.Load(int3(a+int2(0,1),level)),_PlanetMaterialWeights.Load(int3(a+1,level)),f.x),f.y);
}
#endif
float4 PlanetMaterialMaskApply(float3 offset,float4 original) {
#if defined(SPACERUNNER_PREPARED_GRAPHICS_ACTIVE)
 if(_PlanetMaterialMaskEnabled<.5||_PreparedPlanetEnabled<.5)return original;
 double3 anchor=(double3)_PreparedPlanetAnchorHigh.xyz+(double3)_PreparedPlanetAnchorLow.xyz;
 float3 local=mul((float3x3)_PreparedPlanetLocalToPlanet,offset);
 double3 d=PPNormalize(anchor+(double3)local);float4 bg=PMMBackground((float3)d);
 double radius=_PreparedPlanetParameters[0].x;double footprint=max(length(ddx(local)),length(ddy(local)));
 int cell=PPCell(d),begin=_PreparedPlanetOffsets[cell],end=_PreparedPlanetOffsets[cell+1];double sum=0,product=1;float4 mixed=0;
 [loop]for(int i=begin;i<end;i++){
 PreparedPlanetPlacement p=_PreparedPlanetPlacements[_PreparedPlanetContributors[i]];if((_PlanetMaterialSourceMask&(1<<(int)p.metadata.x))==0)continue;double denominator=PPDot(d,p.centreRadius.xyz);if(denominator<=0)continue;
 double2 pos=double2(PPDot(d,p.rightScale.xyz),PPDot(d,p.forwardAmplitude.xyz))*(radius/denominator);
 if(dot(pos,pos)>=p.centreRadius.w*p.centreRadius.w)continue;
 double2 source=p.cropReference.xy+pos*p.rightScale.w,relative=source-p.cropReference.xy,ignored;
 PreparedPlanetSourceGpu s=_PreparedPlanetSources[p.metadata.x];
 #if defined(PP_NATIVE_SHADING)
 float2 unusedSlope;float w=PSPlacementSupport(p.metadata.x,(float2)relative,(float)p.cropReference.z,(float)p.featherHeading.x,unusedSlope);
 if(w<=0)continue;
 w*=s.levels.w==0?1:PSTextureBilinear(s.levels.z,0,3,s.data.x,source,unusedSlope);
 #else
 double w=PPPlacementSupport(p.metadata.x,relative,p.cropReference.z,p.featherHeading.x,ignored)*PPSourceBlend(p.metadata.x,source,ignored);
 #endif
 if(w<=0)continue;
 float lod=clamp(log2(max(1,(float)(footprint*p.rightScale.w/(denominator*denominator)*_PlanetMaterialMaskSide/s.data.x))),0,floor(log2((float)_PlanetMaterialMaskSide)));
 int l=(int)lod;float4 m=lerp(PMMSampleLevel(source,s.data.x,l),PMMSampleLevel(source,s.data.x,min(l+1,(int)floor(log2((float)_PlanetMaterialMaskSide)))),frac(lod));m=max(m,0);m/=max(dot(m,1),1e-8);
 // RGBA roles: local ground, loose ground, scree, exposed rock.
 float4 debris=lerp(bg,float4(0,.45,.55,0),.65);
 float4 resource=m.x*bg+m.y*lerp(bg,float4(0,1,0,0),.35)+m.z*debris+m.w*float4(0,0,1,0);
 mixed+=(float)w*resource;product*=1-w;sum+=w;
 }
 return (bg*(float)product+mixed)/(float)(product+sum);
#else
 return original;
#endif
}
#endif
