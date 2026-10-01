#ifndef PLANET_CELESTIAL_LIGHTS_INCLUDED
#define PLANET_CELESTIAL_LIGHTS_INCLUDED
struct PlanetCelestialLightData
{
    float4 Color,Direction,Dimmers;
};
StructuredBuffer<PlanetCelestialLightData> _PlanetCelestialLightDatas;
int _PlanetCelestialLightDataReady;
int _PlanetCelestialLightCount;
#endif
