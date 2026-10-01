#ifndef PLANET_MEDIA_COMMON_INCLUDED
#define PLANET_MEDIA_COMMON_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
#include "PlanetMediaBody.hlsl"
#include "PlanetCelestialLights.hlsl"
StructuredBuffer<PlanetMediaBody> _PlanetMediaBodies;
StructuredBuffer<float4> _PlanetMediaCurves;
StructuredBuffer<float4> _PlanetMediaAmbient;
StructuredBuffer<uint> _PlanetMediaCoverage;
struct PlanetMediaRegion { float4 Anchor,Cloud,Shape,Rain,RainOptical; };
StructuredBuffer<PlanetMediaRegion> _PlanetMediaRegions;
int _PlanetMediaBodyCount, _PlanetMediaRaySteps, _PlanetMediaLightSteps;
int _PlanetMediaUseSceneLights;
float4 _PlanetMediaFallbackDirection, _PlanetMediaFallbackColor;
float4 _PlanetMediaLightRotation;
TEXTURE3D(_PlanetMediaShapeNoise);
TEXTURE3D(_PlanetMediaWorleyNoise);
TEXTURE3D(_PlanetMediaPerlinNoise);
uint MediaLightCount() { return _PlanetMediaUseSceneLights!=0 && _PlanetCelestialLightCount>0 ? _PlanetCelestialLightCount : 1; }
bool MediaGetLight(uint index,out float3 direction,out float3 color)
{
    direction=float3(0,1,0);color=0;
    bool accepted=true;
    if(_PlanetMediaUseSceneLights!=0 && _PlanetCelestialLightCount>0)
    {
        PlanetCelestialLightData light=_PlanetCelestialLightDatas[index];
        direction=MediaRotate(_PlanetMediaLightRotation,light.Direction.xyz);color=light.Color.rgb*light.Direction.w;
        accepted=light.Color.w>0;
    }
    else { direction=_PlanetMediaFallbackDirection.xyz;color=_PlanetMediaFallbackColor.rgb*_PlanetMediaFallbackDirection.w; }
    return accepted;
}
float MediaVolumetricDimmer(uint index)
{
    if(_PlanetMediaUseSceneLights!=0 && _PlanetCelestialLightCount>0)
        return _PlanetCelestialLightDatas[index].Dimmers.z;
    return 1;
}

float3 MediaCoverageHash(float3 p)
{
    p=float3(dot(p,float3(127.1,311.7,74.7)),dot(p,float3(269.5,183.3,246.1)),dot(p,float3(113.5,271.9,124.6)));
    return frac(sin(p)*43758.5453123)*2-1;
}
float4 MediaUnpack(uint packed)
{ return float4(packed&255,(packed>>8)&255,(packed>>16)&255,(packed>>24)&255)/255.0; }
float3 MediaCubeDirection(uint face,float2 uv)
{
    float u=uv.x*2-1,v=uv.y*2-1;
    if(face==0)return normalize(float3(1,-v,-u));
    if(face==1)return normalize(float3(-1,-v,u));
    if(face==2)return normalize(float3(u,1,v));
    if(face==3)return normalize(float3(u,-1,-v));
    if(face==4)return normalize(float3(u,-v,1));
    return normalize(float3(-u,-v,-1));
}
void MediaCubeFace(float3 direction,out uint face,out float2 uv)
{
    float3 a=abs(direction);
    if(a.x>=a.y && a.x>=a.z)
    { face=direction.x>0?0:1;uv=direction.x>0?float2(-direction.z,-direction.y)/a.x:float2(direction.z,-direction.y)/a.x; }
    else if(a.y>=a.z)
    { face=direction.y>0?2:3;uv=direction.y>0?float2(direction.x,direction.z)/a.y:float2(direction.x,-direction.z)/a.y; }
    else
    { face=direction.z>0?4:5;uv=direction.z>0?float2(direction.x,-direction.y)/a.z:float2(-direction.x,-direction.y)/a.z; }
    uv=uv*.5+.5;
}
float4 MediaCoverageTap(PlanetMediaBody b,uint face,int2 pixel)
{
    uint resolution=(uint)b.TextureMetadata.y;
    // Cross-face taps retain the cube's seam behaviour without texture-array slice caps.
    if(any(pixel<0) || any(pixel>=int2(resolution,resolution)))
    {
        float3 direction=MediaCubeDirection(face,(pixel+.5)/resolution);float2 uv;
        MediaCubeFace(direction,face,uv);pixel=min(int2(uv*resolution),int2(resolution-1,resolution-1));
    }
    uint index=(uint)b.TextureMetadata.x+(face*resolution+pixel.y)*resolution+pixel.x;
    return MediaUnpack(_PlanetMediaCoverage[index]);
}
float4 MediaCachedCoverage(PlanetMediaBody b,float3 direction)
{
    if(b.TextureMetadata.y<=0)return float4(b.Coverage.x,0,.25,1);
    uint face;float2 uv;MediaCubeFace(direction,face,uv);
    float2 p=uv*b.TextureMetadata.y-.5;int2 pixel=(int2)floor(p);float2 f=frac(p);
    return lerp(lerp(MediaCoverageTap(b,face,pixel),MediaCoverageTap(b,face,pixel+int2(1,0)),f.x),
        lerp(MediaCoverageTap(b,face,pixel+int2(0,1)),MediaCoverageTap(b,face,pixel+int2(1,1)),f.x),f.y);
}
float MediaCoverageNoise(float3 p,float seed)
{
    float3 cell=floor(p),offset=p-cell,weight=offset*offset*(3-2*offset);
    float result=0;
    [unroll] for(int corner=0;corner<8;corner++)
    {
        float3 c=float3(corner&1,(corner>>1)&1,(corner>>2)&1);
        float3 blend=lerp(1-weight,weight,c);
        result+=blend.x*blend.y*blend.z*dot(MediaCoverageHash(cell+c+seed),offset-c);
    }
    return result;
}
float MediaCoverageFbm(float3 p,float seed,int octaves)
{
    float sum=0,amplitude=.5,normalization=0;
    [loop] for(int octave=0;octave<octaves;octave++)
    {sum+=amplitude*MediaCoverageNoise(p,seed);normalization+=amplitude;p*=2.03;amplitude*=.5;}
    return sum/max(normalization,1e-5);
}
float MediaStormBias(PlanetMediaBody b,float3 direction,float warp)
{
    float result=0;
    [loop] for(int index=0;index<(int)b.Climate.y;index++)
    {
        float2 noise=frac(sin(float2(index*12.9898+b.SeedWind.x,index*78.233+b.SeedWind.x*1.37))*43758.5453);
        float hemisphere=(index&1)?1:-1;
        float latitude=radians(lerp(22,58,noise.y))*hemisphere,longitude=noise.x*TWO_PI;
        float3 center=float3(cos(latitude)*cos(longitude),sin(latitude),cos(latitude)*sin(longitude));
        float angle=acos(clamp(dot(direction,center),-1,1));
        float radial=angle/max(b.Climate.z,1e-4)*(1+warp*.28);
        if(radial>=1)continue;
        float3 east=normalize(cross(abs(center.y)>.99?float3(1,0,0):float3(0,1,0),center));
        float3 north=cross(center,east);
        float bearing=atan2(dot(direction,north),dot(direction,east))+warp*.7;
        float arms=cos(2*(bearing*hemisphere+1.6*log(max(radial,.04))));
        float profile=1-smoothstep(.30,1,radial),eye=smoothstep(.02,.10,radial);
        result+=profile*(eye*arms-(1-eye))*(.6+.4*(warp*.5+.5));
    }
    return clamp(result,-1,1)*b.Climate.w;
}
float MediaProceduralCoverage(PlanetMediaBody b,float3 direction)
{
    float3 p=direction*b.Coverage.z;
    float seed=b.SeedWind.x;
    float3 warp=float3(MediaCoverageFbm(p+5.2,seed,3),MediaCoverageFbm(p+13.7,seed,3),MediaCoverageFbm(p+27.3,seed,3));
    float field=MediaCoverageFbm(p+warp*.6,seed,6)*.5+.5;
    float province=MediaCoverageFbm(direction*max(b.Coverage.z*.35,.35)+41.3,seed,3)*.5+.5;
    float latitude=abs(degrees(asin(clamp(direction.y,-1,1))));
    float bands=1+.60*exp(-Sq(latitude/12))-.55*exp(-Sq((latitude-28)/13))+.35*exp(-Sq((latitude-55)/16));
    float coverage=saturate(b.Coverage.x*lerp(1,bands,b.Climate.x));
    float storm=MediaStormBias(b,direction,MediaCoverageFbm(p*1.7+71.9,seed,3));
    float threshold=1-coverage-(province-.5)*b.Coverage.w*.8-storm*.55;
    float width=lerp(.40,.05,b.Coverage.y);
    return smoothstep(threshold-width,threshold+width,field);
}
float3 MediaCurve(PlanetMediaBody b,float height,float cloudType)
{
    float3 result=0;
    if(b.TextureMetadata.w>0)
    {
        uint resolution=(uint)b.TextureMetadata.w,offset=(uint)b.TextureMetadata.z;
        float2 p=saturate(float2(cloudType,height))*(resolution-1);uint2 low=(uint2)floor(p),high=min(low+1,resolution-1);float2 f=frac(p);
        result=lerp(lerp(MediaUnpack(_PlanetMediaCoverage[offset+low.y*resolution+low.x]).rgb,
            MediaUnpack(_PlanetMediaCoverage[offset+low.y*resolution+high.x]).rgb,f.x),
            lerp(MediaUnpack(_PlanetMediaCoverage[offset+high.y*resolution+low.x]).rgb,
            MediaUnpack(_PlanetMediaCoverage[offset+high.y*resolution+high.x]).rgb,f.x),f.y);
    }
    else
    {
        float t=saturate(height)*31;
        uint index=(uint)floor(t),curveOffset=(uint)b.CloudMisc.y;
        result=lerp(_PlanetMediaCurves[curveOffset+index].rgb,_PlanetMediaCurves[curveOffset+min(index+1,31)].rgb,frac(t));
    }
    return result;
}
struct MediaCloud
{
    float extinction,density,height,ambientOcclusion,storminess;
};
float MediaRegionWeight(PlanetMediaBody b,PlanetMediaRegion region,float3 planetLocalDirection)
{
    float2 coordinate=radians(region.Anchor.xy);
    float3 anchor=float3(cos(coordinate.x)*cos(coordinate.y),sin(coordinate.x),cos(coordinate.x)*sin(coordinate.y));
    float sine=length(cross(anchor,planetLocalDirection)),cosine=clamp(dot(anchor,planetLocalDirection),-1,1);
    float distance=atan2(sine,cosine)*b.CenterRadius.w;
    return 1-smoothstep(region.Anchor.z,region.Anchor.z+max(.01,region.Anchor.w),distance);
}
// Native HDRP Perlin-Worley shape, erosion, density remap and curve conventions.
// Transport and resource ownership are independent; no camera-global cloud state is sampled.
MediaCloud MediaCloudProperties(PlanetMediaBody b,float3 worldPlanetPosition,float footprint,bool cheap)
{
    MediaCloud result=(MediaCloud)0;
    float radialHeight=length(worldPlanetPosition)-b.CenterRadius.w;
    if(b.CloudLighting.w<=0 || radialHeight<b.Limits.y || radialHeight>b.Limits.z)return result;
    float3 p=MediaRotate(b.InverseRotation,worldPlanetPosition);
    float3 up=p/max(length(p),.001);
    float bottom=b.RegionMetadata.z,top=b.RegionMetadata.w;
    if(top<=bottom){bottom=b.Limits.y;top=b.Limits.z;}
    float3 wind=b.SeedWind.yzw;
    float3 mapP=p+wind*b.CloudWind.x;
    float4 map=MediaCachedCoverage(b,normalize(mapP));float densityOverride=0;
    [loop] for(uint regionIndex=0;regionIndex<(uint)b.RegionMetadata.y;regionIndex++)
    {
        PlanetMediaRegion region=_PlanetMediaRegions[(uint)b.RegionMetadata.x+regionIndex];
        float weight=MediaRegionWeight(b,region,up);
        map=lerp(map,region.Cloud,weight);bottom=lerp(bottom,region.Shape.y,weight);top=lerp(top,region.Shape.z,weight);
        densityOverride=max(densityOverride,region.Shape.x*weight);result.storminess=max(result.storminess,region.Shape.w*weight);
    }
    if(radialHeight<bottom || radialHeight>top)return result;
    result.height=saturate((radialHeight-bottom)/max(1,top-bottom));float coverage=map.x;
    if(coverage<=.001 || map.w<result.height)return result;
    float3 curves=MediaCurve(b,result.height,map.z);
    float shape=lerp(.1,1,b.CloudShape.y)*curves.y,erosion=b.CloudShape.w*curves.y;
    float period=100000/max(b.CloudShape.z,1e-3);
    float shear=dot(p,float3(.8085,.3582,-.4677))/3+dot(p,float3(-.3164,.8746,.3684))/7;
    p+=up*(shear-period*round(shear/period));
    float3 displacement=wind*b.CloudWind.y;
    float3 shapeP=p+displacement-up*dot(displacement,up)+up*b.CloudWind.w;
    float3 coordinates=shapeP.xzy/100000*b.CloudShape.z-b.CloudOffset.xzy;
    float windLength=length(wind.xz);
    coordinates+=result.height*float3(windLength>0?wind.xz/windLength:float2(0,0),0)*b.CloudOffset.w;
    float shapeMip=max(0,log2(max(1,footprint*b.CloudShape.z/100000*128)));
    float low=SAMPLE_TEXTURE3D_LOD(_PlanetMediaShapeNoise,s_trilinear_repeat_sampler,coordinates,shapeMip).r;
    low=lerp(1,low,shape);
    float threshold=1-curves.x*coverage*(1-shape);
    float density=saturate((low-threshold)/max(1e-4,1-threshold))*coverage*coverage;
    result.ambientOcclusion=lerp(1,curves.z,saturate(1-max(erosion,shape)*.5));
    if(!cheap && erosion>0)
    {
        float3 fine=(p+wind*b.CloudWind.z+up*b.CloudMisc.x)/100000*b.CloudErosion.x;
        float mip=max(0,log2(max(1,footprint*b.CloudErosion.x/100000*32)));
        float noise=b.CloudErosion.w>0?
            SAMPLE_TEXTURE3D_LOD(_PlanetMediaPerlinNoise,s_linear_repeat_sampler,fine,mip).r:
            SAMPLE_TEXTURE3D_LOD(_PlanetMediaWorleyNoise,s_linear_repeat_sampler,fine,mip).r;
        float high=(1-noise)*erosion*.75*coverage*b.CloudErosion.z;
        density=max(0,(density-high)/max(1e-4,1-high));
        result.ambientOcclusion=saturate(result.ambientOcclusion-sqrt(max(0,high*b.CloudErosion.y)));
    }
    result.density=max(0,density-(cheap?erosion*.1:0))*b.CloudShape.x;
    result.density=max(result.density,densityOverride*coverage*curves.x*b.CloudShape.x);
    result.ambientOcclusion*=1-result.storminess*(1-result.height)*.85;
    result.extinction=result.density*lerp(.04,.12,map.y);
    return result;
}
float MediaFogExtinction(PlanetMediaBody b,float height)
{
    if(b.FogDensity.x<=0 || height>b.Limits.w)return 0;
    return b.FogDensity.x*exp(-max(0,height-b.FogDensity.y)/max(.001,b.FogDensity.z));
}
float3 MediaAmbient(float3 direction)
{ return max(0,SampleSH9(_PlanetMediaAmbient,direction)); }
// The native probe is retained for daytime color, but it may have been computed in
// another celestial light frame. Its ambient response cannot illuminate a body's
// full night shadow. This bounded twilight gate is an approximation to skylight.
float MediaAmbientVisibility(PlanetMediaBody b,float3 p)
{
    float radius=max(length(p),b.CenterRadius.w+.001);
    float horizon=-sqrt(saturate(1-Sq(b.CenterRadius.w/radius)));
    float visibility=0;
    [loop] for(uint ambientLightIndex=0;ambientLightIndex<MediaLightCount();ambientLightIndex++)
    {
        float3 L,lightColor;
        if(!MediaGetLight(ambientLightIndex,L,lightColor) || max(lightColor.r,max(lightColor.g,lightColor.b))<=0)continue;
        visibility=max(visibility,smoothstep(horizon-.01,horizon+.03,dot(p,L)/radius));
    }
    return visibility;
}
float3 MediaCloudLighting(PlanetMediaBody b,MediaCloud cloud,float3 p,float3 direction,float footprint)
{
    float3 up=p/max(length(p),.001),ambient=MediaAmbient(up);
    float3 bottom=lerp(MediaAmbient(-up),b.CloudBottomLighting.rgb,b.CloudBottomLighting.w);
    float3 source=lerp(bottom,ambient,cloud.height)*cloud.ambientOcclusion*b.CloudLighting.z*MediaAmbientVisibility(b,p);
    [loop] for(uint lightIndex=0;lightIndex<MediaLightCount();lightIndex++)
    {
        float3 L,lightColor;
        if(!MediaGetLight(lightIndex,L,lightColor))continue;
        float3 sun=lightColor*MediaAirTransmissionToSun(b,p,L)*b.CloudScatteringTint.w*MediaVolumetricDimmer(lightIndex);
        if(max(sun.r,max(sun.g,sun.b))<=0)continue;
        float2 exit;MediaSphereRange(-p,b.CenterRadius.w+b.Limits.z,L,exit);
        float distance=min(max(0,exit.y),_PlanetMediaLightSteps*1000.0),stepLength=distance/max(1,_PlanetMediaLightSteps);
        float cloudOD=0;
        [loop] for(int j=0;j<_PlanetMediaLightSteps;j++)
        { MediaCloud sampleCloud=MediaCloudProperties(b,p+L*(j+.25)*stepLength,max(footprint,stepLength),true);cloudOD+=sampleCloud.extinction*stepLength; }
        float cosine=dot(direction,L),phase=MediaPhase(cosine,.7)+MediaPhase(cosine,-.7);
        float powder=lerp(1,1-exp(-cloud.density*4),b.CloudLighting.y);
        source+=sun*phase*exp(-cloudOD*b.CloudScatteringTint.rgb)*powder;
        float ms=b.CloudLighting.x;
        source+=sun*(MediaPhase(cosine,.7*ms)+MediaPhase(cosine,-.7*ms))*
            exp(-cloudOD*b.CloudScatteringTint.rgb*ms)*ms*powder;
    }
    return source;
}
void MediaAtPoint(PlanetMediaBody b,float3 p,float3 direction,float footprint,bool lighting,
    out float3 extinction,out float3 source)
{
    float height=length(p)-b.CenterRadius.w;
    extinction=MediaAirExtinction(b,height);
    float fog=MediaFogExtinction(b,height);
    extinction+=fog;source=0;
    float rain=0;float3 rainScattering=0;
    float3 localDirection=MediaRotate(b.InverseRotation,p)/max(length(p),.001);
    [loop] for(uint regionIndex=0;regionIndex<(uint)b.RegionMetadata.y;regionIndex++)
    {
        PlanetMediaRegion region=_PlanetMediaRegions[(uint)b.RegionMetadata.x+regionIndex];
        if(region.RainOptical.w<=0 || height<region.Rain.x || height>region.Rain.y)continue;
        float vertical=saturate((height-region.Rain.x)/region.Rain.z)*saturate((region.Rain.y-height)/region.Rain.z);
        float density=lerp(region.Rain.w,1,saturate((height-region.Rain.x)/max(1,region.Rain.y-region.Rain.x)));
        float rainSigma=region.RainOptical.w*MediaRegionWeight(b,region,localDirection)*vertical*density;
        rain+=rainSigma;rainScattering+=rainSigma*region.RainOptical.rgb;
    }
    extinction+=rain;
    MediaCloud cloud=MediaCloudProperties(b,p,footprint,false);
    extinction+=cloud.extinction;
    if(!lighting)return;
    // Fog's constant/tint color is a scattering albedo, not an unlit light source.
    // Thus the same fog retains extinction and becomes dark on an unlit night side.
    if(fog>0 || rain>0)
    {
        float3 ambient=MediaAmbient(p/max(length(p),.001))*MediaAmbientVisibility(b,p);
        source+=(fog*b.FogAlbedo.rgb*b.FogColor.rgb+rainScattering)*ambient*b.FogAlbedo.w;
    }
    [loop] for(uint lightIndex=0;lightIndex<MediaLightCount();lightIndex++)
    {
        float3 L,lightColor;
        if(!MediaGetLight(lightIndex,L,lightColor))continue;
        float3 sun=lightColor*MediaAirTransmissionToSun(b,p,L);
        float cosine=dot(direction,L);
        source+=sun*MediaAirSource(b,height,cosine);
        float3 volumetricSun=sun*MediaVolumetricDimmer(lightIndex);
        source+=volumetricSun*(fog*b.FogAlbedo.rgb*b.FogColor.rgb+rainScattering)*MediaPhase(cosine,b.FogDensity.w);
    }
    if(cloud.extinction>0)source+=cloud.extinction*MediaCloudLighting(b,cloud,p,direction,footprint);
}
void MediaConsiderBoundary(float3 center,float radius,float3 direction,float cursor,float endpoint,inout float next)
{
    float2 range;
    if(!MediaSphereRange(center,radius,direction,range))return;
    if(range.x>cursor && range.x<next)next=range.x;
    if(range.y>cursor && range.y<next)next=range.y;
}
struct MediaPrefixRecorder
{
    uint2 pixel;
    uint slice;
    float distance,growth;
    bool enabled;
};
void MediaRecordPrefix(inout MediaPrefixRecorder recorder,float start,float end,float3 extinction,float3 source,
    float3 accumulatedLight,float3 accumulatedTransmission)
{
#ifdef PLANET_MEDIA_PREFIX_WRITER
    if(!recorder.enabled)return;
    [loop] while(recorder.slice<(uint)_PlanetMediaTransparentParameters.z && recorder.distance<=end)
    {
        float length=max(0,recorder.distance-start);
        float3 tau=extinction*length,partialT=exp(-max(0,tau)),absorbed=1-partialT;
        absorbed=lerp(absorbed,tau*(1-tau*.5),step(tau,.001));
        float3 radiance=accumulatedLight+accumulatedTransmission*source*absorbed/max(extinction,1e-20);
        uint3 coordinate=uint3(recorder.pixel,recorder.slice);
        _PlanetMediaPrefixScatteringRW[coordinate]=float4(clamp(radiance*GetCurrentExposureMultiplier(),0,65504),1);
        _PlanetMediaPrefixTransmissionRW[coordinate]=float4(accumulatedTransmission*partialT,1);
        ++recorder.slice;recorder.distance*=recorder.growth;
    }
#endif
}
// Process every sphere boundary on this ray. Empty space is skipped. In overlapping
// intervals all extinction and source terms are summed before the same analytical step.
// No array with a fixed number of celestial bodies is used.
void PlanetMediaIntegrateRay(float3 direction,float endpoint,float pixelSpread,bool lighting,int raySteps,inout MediaPrefixRecorder recorder,
    out float3 inScattering,out float3 transmittance)
{
    inScattering=0;transmittance=1;
    float cursor=0;
    uint events=(uint)_PlanetMediaBodyCount*14+1;
    [loop] for(int eventBodyIndex=0;eventBodyIndex<_PlanetMediaBodyCount;eventBodyIndex++)events+=(uint)_PlanetMediaBodies[eventBodyIndex].RegionMetadata.y*4;
    [loop] for(uint eventIndex=0;eventIndex<events && cursor<endpoint;eventIndex++)
    {
        float next=endpoint;
        [loop] for(int boundaryBodyIndex=0;boundaryBodyIndex<_PlanetMediaBodyCount;boundaryBodyIndex++)
        {
            PlanetMediaBody b=_PlanetMediaBodies[boundaryBodyIndex];
            float r=b.CenterRadius.w;
            MediaConsiderBoundary(b.CenterRadius.xyz,r+MediaOuterHeight(b),direction,cursor,endpoint,next);
            if(b.Limits.x>0)MediaConsiderBoundary(b.CenterRadius.xyz,r+b.Limits.x,direction,cursor,endpoint,next);
            if(b.CloudLighting.w>0)
            {
                MediaConsiderBoundary(b.CenterRadius.xyz,r+b.Limits.y,direction,cursor,endpoint,next);
                MediaConsiderBoundary(b.CenterRadius.xyz,r+b.Limits.z,direction,cursor,endpoint,next);
                MediaConsiderBoundary(b.CenterRadius.xyz,r+b.RegionMetadata.z,direction,cursor,endpoint,next);
                MediaConsiderBoundary(b.CenterRadius.xyz,r+b.RegionMetadata.w,direction,cursor,endpoint,next);
            }
            if(b.Limits.w>0)MediaConsiderBoundary(b.CenterRadius.xyz,r+b.Limits.w,direction,cursor,endpoint,next);
            [loop] for(uint regionIndex=0;regionIndex<(uint)b.RegionMetadata.y;regionIndex++)
            {
                PlanetMediaRegion region=_PlanetMediaRegions[(uint)b.RegionMetadata.x+regionIndex];
                if(region.RainOptical.w<=0)continue;
                MediaConsiderBoundary(b.CenterRadius.xyz,r+region.Rain.x,direction,cursor,endpoint,next);
                MediaConsiderBoundary(b.CenterRadius.xyz,r+region.Rain.y,direction,cursor,endpoint,next);
            }
        }
        float midpoint=(cursor+next)*.5;
        bool active=false;
        [loop] for(int activeBodyIndex=0;activeBodyIndex<_PlanetMediaBodyCount;activeBodyIndex++)
        {
            PlanetMediaBody b=_PlanetMediaBodies[activeBodyIndex];
            if(length(direction*midpoint-b.CenterRadius.xyz)<=b.CenterRadius.w+MediaOuterHeight(b)){active=true;break;}
        }
        if(active)
        {
            int count=max(4,raySteps);
            if(next-cursor<=128)count=4;
            float stepLength=(next-cursor)/count;
            [loop] for(int sampleIndex=0;sampleIndex<count;sampleIndex++)
            {
                float distance=cursor+(sampleIndex+.5)*stepLength;
                float3 position=direction*distance,totalExtinction=0,totalSource=0;
                [loop] for(int sampleBodyIndex=0;sampleBodyIndex<_PlanetMediaBodyCount;sampleBodyIndex++)
                {
                    PlanetMediaBody b=_PlanetMediaBodies[sampleBodyIndex];
                    float3 p=position-b.CenterRadius.xyz;
                    if(length(p)>b.CenterRadius.w+MediaOuterHeight(b))continue;
                    float3 extinction,source;
                    MediaAtPoint(b,p,direction,max(stepLength,distance*pixelSpread),lighting,extinction,source);
                    totalExtinction+=extinction;totalSource+=source;
                }
                float3 tau=totalExtinction*stepLength,stepT=exp(-max(0,tau));
                MediaRecordPrefix(recorder,distance-stepLength*.5,distance+stepLength*.5,totalExtinction,totalSource,inScattering,transmittance);
                float3 absorbed=1-stepT;
                // Keep centimetre-scale signals without losing them to 1-exp(-tiny).
                absorbed=lerp(absorbed,tau*(1-tau*.5),step(tau,.001));
                inScattering+=transmittance*totalSource*absorbed/max(totalExtinction,1e-20);
                transmittance*=stepT;
                if(max(transmittance.r,max(transmittance.g,transmittance.b))<1e-5)break;
            }
        }
        else MediaRecordPrefix(recorder,cursor,next,0,0,inScattering,transmittance);
        cursor=next;
        if(max(transmittance.r,max(transmittance.g,transmittance.b))<1e-5)break;
    }
    // Complete the remaining slices after empty space or early optical saturation.
    MediaRecordPrefix(recorder,endpoint,1e30,0,0,inScattering,transmittance);
}
void PlanetMediaEvaluateRay(float3 direction,float endpoint,float pixelSpread,bool lighting,
    out float3 inScattering,out float3 transmittance)
{
    MediaPrefixRecorder recorder=(MediaPrefixRecorder)0;
    PlanetMediaIntegrateRay(direction,endpoint,pixelSpread,lighting,_PlanetMediaRaySteps,recorder,inScattering,transmittance);
}
#endif
