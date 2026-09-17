using System;
namespace UnityEngine.Rendering.HighDefinition
{
    // Opt-in placement shared by the planet adapter, stock clouds and stock fog.
    [Serializable, VolumeComponentMenu("Sky/Planetary Weather")]
    public sealed class PlanetaryWeather : VolumeComponent
    {
        public BoolParameter enabled = new BoolParameter(false);
        public IntParameter planetId = new IntParameter(1);
        public MinFloatParameter radius = new MinFloatParameter(2123666.7f, 1);
        public Vector3Parameter center = new Vector3Parameter(Vector3.zero);
        public Vector3Parameter rotation = new Vector3Parameter(Vector3.zero);
        [Tooltip("Minimum cloud base above sea level, in metres. The planet adapter includes terrain relief and clearance.")]
        public MinFloatParameter minimumCloudAltitude = new MinFloatParameter(0, 0);
        public float CloudBottom(float requested) => IsValid ? Mathf.Max(requested, minimumCloudAltitude.value) : requested;

        // Cloud control modes place a flat map over a dome: Simple is a single constant texel, and an authored
        // map repeats. Neither carries structure at planetary scale, so on a planet the coverage channels come
        // from a cube map baked from these parameters instead. Ordinary scenes are unaffected.
        [Tooltip("Bake planet-wide cloud coverage from the parameters below instead of the flat cloud map.")]
        public BoolParameter proceduralCoverage = new BoolParameter(true);
        [Tooltip("Seed of the planetary coverage field. The planet adapter drives it from the generator seed.")]
        public IntParameter seed = new IntParameter(0);
        [Tooltip("Average fraction of the planet under cloud.")]
        public ClampedFloatParameter coverage = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);
        [Tooltip("Sharpness of the edge between open sky and cloud mass.")]
        public ClampedFloatParameter coverageContrast = new ClampedFloatParameter(0.55f, 0.0f, 1.0f);
        [Tooltip("Size of the largest cloud systems along the surface, in metres.")]
        public MinFloatParameter systemScale = new MinFloatParameter(900000.0f, 1000.0f);
        [Tooltip("Strength of the large clear provinces between cloud systems.")]
        public ClampedFloatParameter openSky = new ClampedFloatParameter(0.6f, 0.0f, 1.0f);
        [Tooltip("Strength of the latitude bands: cloudy equator, dry subtropics, stormy middle latitudes. Latitude is measured against the planet rotation given above.")]
        public ClampedFloatParameter climateBands = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);
        [Tooltip("Number of cyclonic storm systems placed over the planet.")]
        public ClampedIntParameter stormCount = new ClampedIntParameter(10, 0, 32);
        [Tooltip("Radius of a storm system along the surface, in metres.")]
        public MinFloatParameter stormScale = new MinFloatParameter(700000.0f, 1000.0f);
        [Tooltip("How much a storm system thickens the cloud above the surrounding weather.")]
        public ClampedFloatParameter stormStrength = new ClampedFloatParameter(0.6f, 0.0f, 1.0f);
        public bool ProceduralCoverageActive => IsValid && proceduralCoverage.value;
        // Rebaking is driven by this hash, so every parameter the bake reads has to take part in it.
        public int CoverageHash()
        {
            unchecked
            {
                int hash = seed.value;
                hash = 23 * hash + radius.value.GetHashCode();
                hash = 23 * hash + coverage.value.GetHashCode();
                hash = 23 * hash + coverageContrast.value.GetHashCode();
                hash = 23 * hash + systemScale.value.GetHashCode();
                hash = 23 * hash + openSky.value.GetHashCode();
                hash = 23 * hash + climateBands.value.GetHashCode();
                hash = 23 * hash + stormCount.value;
                hash = 23 * hash + stormScale.value.GetHashCode();
                hash = 23 * hash + stormStrength.value.GetHashCode();
                return hash;
            }
        }
        public bool IsValid => enabled.value && Finite(radius.value) && radius.value>0 &&
            Finite(center.value.x) && Finite(center.value.y) && Finite(center.value.z) &&
            Finite(rotation.value.x) && Finite(rotation.value.y) && Finite(rotation.value.z);
        static bool Finite(float value)=>!float.IsNaN(value) && !float.IsInfinity(value);
        public static bool IsActive(HDCamera camera)
        {
            var value=camera.volumeStack.GetComponent<PlanetaryWeather>();
            return value!=null && value.active && value.IsValid;
        }
        internal static void Update(ref ShaderVariablesGlobal cb,HDCamera camera)
        {
            cb._PlanetWeatherCenterRadius=Vector4.zero;
            cb._PlanetWeatherWorldToLocal=Matrix4x4.identity;
            if(!IsActive(camera))return;
            var value=camera.volumeStack.GetComponent<PlanetaryWeather>();
            var center=value.center.value-camera.camera.transform.position;
            cb._PlanetWeatherCenterRadius=new Vector4(center.x,center.y,center.z,value.radius.value);
            cb._PlanetWeatherWorldToLocal=Matrix4x4.Rotate(Quaternion.Inverse(Quaternion.Euler(value.rotation.value)));
        }
    }
}
