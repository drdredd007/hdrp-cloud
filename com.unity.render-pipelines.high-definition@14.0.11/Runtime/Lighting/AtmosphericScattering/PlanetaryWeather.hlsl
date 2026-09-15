#ifndef PLANETARY_WEATHER_INCLUDED
#define PLANETARY_WEATHER_INCLUDED
// Placement comes from the per-camera global constant buffer; ordinary cameras have radius zero.
bool PlanetWeatherActive() { return _PlanetWeatherCenterRadius.w > 0; }
float3 PlanetWeatherPosition(float3 cameraRelativePosition)
{
    return cameraRelativePosition - GetCurrentViewPosition() - _PlanetWeatherCenterRadius.xyz;
}
float PlanetWeatherHeight(float3 cameraRelativePosition)
{
    return length(PlanetWeatherPosition(cameraRelativePosition))-_PlanetWeatherCenterRadius.w;
}
float PlanetWeatherRegionDistance(float3 localPosition,float2 latitudeLongitude)
{
    float2 angle=radians(latitudeLongitude);
    float3 direction=float3(cos(angle.x)*cos(angle.y),sin(angle.x),cos(angle.x)*sin(angle.y));
    float3 radial=normalize(localPosition);
    return atan2(length(cross(radial,direction)),dot(radial,direction))*_PlanetWeatherCenterRadius.w;
}
TEXTURE2D(_PlanetWeatherFarDistance);
TEXTURE2D(_PlanetWeatherNearDistance);
int _PlanetWeatherDepthReady;
int _PlanetWeatherHasNear;
float PlanetWeatherDistance(uint2 pixel)
{
    if(!PlanetWeatherActive() || !_PlanetWeatherDepthReady)return 0;
    float d=LOAD_TEXTURE2D(_PlanetWeatherFarDistance,pixel).a;
    if(_PlanetWeatherHasNear)
    {
        float n=LOAD_TEXTURE2D(_PlanetWeatherNearDistance,pixel).a;
        if(n>0)d=n;
    }
    return d;
}
// Bounded radial integration. Clip to the shell first so orbital rays do not skip the fog.
float PlanetWeatherFogOpticalDepth(float3 position, float3 direction, float distance, float extinction, float baseHeight, float scaleHeight)
{
    float3 origin=PlanetWeatherPosition(position);
    float outer=_PlanetWeatherCenterRadius.w+max(0,baseHeight)+scaleHeight*12;
    float b=dot(origin,direction), c=dot(origin,origin)-outer*outer;
    float discriminant=b*b-c;
    if(discriminant<0)return 0;
    float root=sqrt(discriminant);
    float entry=max(0,-b-root), end=min(distance,-b+root);
    if(end<=entry)return 0;
    float stepLength=(end-entry)/32, opticalDepth=0;
    [loop] for(int i=0;i<32;i++)
    {
        float h=length(origin+direction*(entry+(i+.5)*stepLength))-_PlanetWeatherCenterRadius.w;
        opticalDepth+=exp(-max(0,h-baseHeight)/max(.001,scaleHeight))*extinction*stepLength;
    }
    return opticalDepth;
}
#endif
