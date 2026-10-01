#ifndef PLANET_WEATHER_LIGHTING_INCLUDED
#define PLANET_WEATHER_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/AtmosphericScattering/PlanetaryWeather.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetCelestialLights.hlsl"

bool PlanetWeatherOwnsLighting() { return PlanetWeatherActive() && _PlanetWeatherLighting.x > 0; }
float3 PlanetWeatherRotateLight(float3 direction)
{
    float4 q=_PlanetWeatherLightRotation;
    return direction+2*cross(q.xyz,cross(q.xyz,direction)+q.w*direction);
}
float PlanetWeatherSunVisibility(float3 positionWS,float3 direction)
{
    float3 p=PlanetWeatherPosition(positionWS);
    float r=max(length(p),_PlanetWeatherCenterRadius.w);
    float horizon=-sqrt(saturate(1-Sq(_PlanetWeatherCenterRadius.w/r)));
    return smoothstep(horizon-.02,horizon+.02,dot(p/max(length(p),1),direction));
}
// The native probe remains a single camera sky. Its daytime multiple scattering
// is retained; this twilight gate prevents an unrotated probe lighting the body's night.
float PlanetWeatherAmbientVisibility(float3 positionWS)
{
    if(!PlanetWeatherOwnsLighting())return 1;
    if(_PlanetWeatherFallbackColor.w<.5)
        return _PlanetWeatherFallbackSun.w>0?PlanetWeatherSunVisibility(positionWS,_PlanetWeatherFallbackSun.xyz):0;
    float visibility=0;
    if(_PlanetCelestialLightDataReady!=0)
    [loop] for(int i=0;i<_PlanetCelestialLightCount;i++)
    {
        PlanetCelestialLightData light=_PlanetCelestialLightDatas[i];
        if(light.Color.w>0 && light.Direction.w>0 && any(light.Color.rgb>0))
            visibility=max(visibility,PlanetWeatherSunVisibility(positionWS,PlanetWeatherRotateLight(light.Direction.xyz)));
    }
    return visibility;
}
// Same native PBS extinction as the original weather path, evaluated once at the
// selected body's sample instead of reusing an attenuated camera-global Sun colour.
float3 PlanetWeatherSunTransmission(float3 positionWS,float3 direction)
{
    float3 p=PlanetWeatherPosition(positionWS);
    float r=max(length(p),_PlanetWeatherCenterRadius.w+1);
    float cosTheta=dot(p,direction)/r;
    float horizon=-sqrt(saturate(1-Sq(_PlanetWeatherCenterRadius.w/r)));
    if(cosTheta<horizon)return 0;
    if(_PlanetWeatherLighting.y<.5)return 1;
    float3 opacity=1-TransmittanceFromOpticalDepth(ComputeAtmosphericOpticalDepth(r,cosTheta,true));
    return 1-Desaturate(opacity,_AlphaSaturation)*_AlphaMultiplier;
}
#endif
