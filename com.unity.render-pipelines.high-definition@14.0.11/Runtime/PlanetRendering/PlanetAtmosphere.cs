using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // Per-planet parameters of the stock HDRP PhysicallyBasedSky (Custom model). Values use the same
    // meaning as the volume component: zenith opacity per channel and layer depth in metres.
    // Blittable, so an application can bake it into an ECS snapshot next to PlanetDefinition.
    [Serializable]
    public struct PlanetAtmosphereSettings : IEquatable<PlanetAtmosphereSettings>
    {
        public bool Enabled;
        [Tooltip("Zenith opacity of air per colour channel at sea level.")]
        [ColorUsage(false)] public Color AirOpacity;
        [ColorUsage(false)] public Color AirTint;
        [Min(1)] public float AirMaximumAltitude;
        [Range(0,1)] public float AerosolOpacity;
        [ColorUsage(false)] public Color AerosolTint;
        [Min(1)] public float AerosolMaximumAltitude;
        [Range(-1,1)] public float AerosolAnisotropy;
        [Tooltip("Tint of the sky's analytic sea-level sphere and of its ground irradiance precomputation.")]
        [ColorUsage(false)] public Color GroundTint;

        // Earth defaults of PhysicallyBasedSky (EarthAdvanced) expressed as Custom parameters.
        public static PlanetAtmosphereSettings EarthLike => new PlanetAtmosphereSettings
        {
            Enabled=true,
            AirOpacity=new Color(1-math.exp(-5.8e-6f*8000),1-math.exp(-13.5e-6f*8000),1-math.exp(-33.1e-6f*8000)),
            AirTint=new Color(.9f,.9f,1),AirMaximumAltitude=8000/0.144765f,
            AerosolOpacity=1-math.exp(-10e-6f*1200),AerosolTint=new Color(.9f,.9f,.9f),AerosolMaximumAltitude=1200/0.144765f,
            AerosolAnisotropy=0,GroundTint=new Color(.4f,.25f,.15f)
        };

        public bool IsValid => Enabled && Finite(AirOpacity) && Finite(AirTint) && Finite(AerosolTint) && Finite(GroundTint) &&
            math.isfinite(AirMaximumAltitude) && AirMaximumAltitude>=1 && math.isfinite(AerosolMaximumAltitude) && AerosolMaximumAltitude>=1 &&
            math.isfinite(AerosolOpacity) && math.isfinite(AerosolAnisotropy);
        // The outer atmosphere radius used by HDRP tables.
        public float Depth => math.max(AirMaximumAltitude,AerosolMaximumAltitude);

        static bool Finite(Color c)=>math.isfinite(c.r) && math.isfinite(c.g) && math.isfinite(c.b);
        public bool Equals(PlanetAtmosphereSettings o)=>Enabled==o.Enabled && AirOpacity==o.AirOpacity && AirTint==o.AirTint &&
            AirMaximumAltitude==o.AirMaximumAltitude && AerosolOpacity==o.AerosolOpacity && AerosolTint==o.AerosolTint &&
            AerosolMaximumAltitude==o.AerosolMaximumAltitude && AerosolAnisotropy==o.AerosolAnisotropy && GroundTint==o.GroundTint;
        public override bool Equals(object other)=>other is PlanetAtmosphereSettings settings && Equals(settings);
        public override int GetHashCode()=>HashCode.Combine(Enabled,AirOpacity,AirMaximumAltitude,AerosolOpacity,AerosolMaximumAltitude,AerosolAnisotropy,GroundTint);
    }

    // Couples one procedural planet to the stock PhysicallyBasedSky. The application owns the volume
    // (and therefore its priority and lifetime); this class only fills parameters and checks that a
    // camera's resolved sky really describes the planet before planet layers sample its tables.
    public static class PlanetAtmosphere
    {
        // Float placement tolerance: HDRP stores the planet centre as a float3 in Unity world space.
        const float RelativeTolerance=2e-6f, AbsoluteTolerance=16;

        // centerWorld: planet centre in Unity world space (the application's double position minus its render origin).
        public static void Configure(VisualEnvironment environment,PhysicallyBasedSky sky,in PlanetAtmosphereSettings settings,
            in PlanetDefinition definition,Quaternion planetRotation,Vector3 centerWorld)
        {
            if(environment==null || sky==null)throw new ArgumentNullException(environment==null?nameof(environment):nameof(sky));
            environment.skyType.Override((int)SkyType.PhysicallyBased);
            sky.type.Override(PhysicallyBasedSkyModel.Custom);
            sky.sphericalMode.Override(true);
            sky.planetaryRadius.Override((float)definition.Radius);
            sky.planetCenterPosition.Override(centerWorld);
            sky.planetRotation.Override(planetRotation.eulerAngles);
            sky.airDensityR.Override(math.saturate(settings.AirOpacity.r));
            sky.airDensityG.Override(math.saturate(settings.AirOpacity.g));
            sky.airDensityB.Override(math.saturate(settings.AirOpacity.b));
            sky.airTint.Override(settings.AirTint);
            sky.airMaximumAltitude.Override(settings.AirMaximumAltitude);
            sky.aerosolDensity.Override(math.saturate(settings.AerosolOpacity));
            sky.aerosolTint.Override(settings.AerosolTint);
            sky.aerosolMaximumAltitude.Override(settings.AerosolMaximumAltitude);
            sky.aerosolAnisotropy.Override(math.clamp(settings.AerosolAnisotropy,-1,1));
            sky.groundTint.Override(settings.GroundTint);
        }

        // True when the camera's resolved volume stack renders a PhysicallyBasedSky for this planet:
        // sky type, Custom spherical model, radius and centre (cameraPosition is the observer position
        // in the planet's double frame, whose Unity-space counterpart is the camera transform).
        public static bool Matches(HDCamera camera,in PlanetDefinition definition,double3 cameraPosition)
            =>camera!=null && Matches(camera.volumeStack,camera.camera.transform.position,definition,cameraPosition);
        // cameraWorld: the camera transform position (Unity world space) that corresponds to cameraPosition.
        public static bool Matches(VolumeStack stack,Vector3 cameraWorld,in PlanetDefinition definition,double3 cameraPosition)
        {
            if(stack==null || !definition.IsValid)return false;
            var environment=stack.GetComponent<VisualEnvironment>();
            var sky=stack.GetComponent<PhysicallyBasedSky>();
            if(environment==null || sky==null || environment.skyType.value!=(int)SkyType.PhysicallyBased)return false;
            if(sky.type.value!=PhysicallyBasedSkyModel.Custom || !sky.sphericalMode.value)return false;
            double radius=definition.Radius;
            if(math.abs(sky.planetaryRadius.value-radius)>AbsoluteTolerance+RelativeTolerance*radius)return false;
            var expected=(double3)(float3)cameraWorld+(definition.Center-cameraPosition);
            double distance=math.length(expected);
            return math.length((double3)(float3)sky.planetCenterPosition.value-expected)<=AbsoluteTolerance+RelativeTolerance*math.max(distance,radius);
        }
    }
}
