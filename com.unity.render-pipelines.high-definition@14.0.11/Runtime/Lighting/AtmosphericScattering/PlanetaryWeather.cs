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
