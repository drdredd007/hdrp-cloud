namespace UnityEngine.Rendering.HighDefinition
{
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Global Color")]
    public sealed class PlanetGlobalColorSettings : ScriptableObject
    {
        public bool Enabled=true;
        public Texture2D ColorMap;
        [Range(0,1)] public float SurfaceTintStrength=1;
        public bool UseMapAlphaForStrength;
        [HideInInspector] public bool ReferenceIsLinear;
        public int Seed=42377;
        [Tooltip("Equirectangular width: a power of two from 256 to 4096; height is half. Regenerate after editing generation parameters.")]
        [Range(256,4096)] public int MapWidth=2048;
        [Range(.5f,12)] public float RegionFrequency=2.6f;
        [Range(0,1)] public float DomainWarp=.38f;
        [Range(.05f,.95f)] public float SnowCoverage=.52f;
        [Range(.01f,.3f)] public float EdgeSoftness=.055f;
        [Range(0,.4f)] public float PolarSnow=.12f;
        public Color SlateColor=new Color(.37f,.46f,.52f);
        public Color IceColor=new Color(.58f,.69f,.75f);
        public Color SnowColor=new Color(.9f,.94f,.96f);
        [HideInInspector] public Color LocalReferenceColor=Color.white;
        [Min(0)] public float StartAltitudeMetres=8000;
        [Min(1)] public float FullAltitudeMetres=80000;
        [Range(0,1)] public float Strength=1;
        static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        public bool IsValid=>Enabled&&ColorMap&&Finite(StartAltitudeMetres)&&Finite(FullAltitudeMetres)&&
            FullAltitudeMetres>StartAltitudeMetres&&StartAltitudeMetres>=0&&Finite(Strength)&&Strength>=0&&Strength<=1&&
            Finite(SurfaceTintStrength)&&SurfaceTintStrength>=0&&SurfaceTintStrength<=1&&
            Finite(LocalReferenceColor.r)&&Finite(LocalReferenceColor.g)&&Finite(LocalReferenceColor.b);
        public float Weight=>IsValid?Strength:0;
        public float DistanceBlend(float altitude)
        {
            if(!IsValid)return 0;
            float t=Mathf.InverseLerp(StartAltitudeMetres,FullAltitudeMetres,altitude);
            return t*t*(3-2*t);
        }
        public static void Bind(MaterialPropertyBlock properties,PlanetGlobalColorSettings settings,float altitude)
            => Bind(properties,settings,altitude,Quaternion.identity);
        public static void Bind(MaterialPropertyBlock properties,PlanetGlobalColorSettings settings,float altitude,Quaternion planetToRender)
        {
            properties.SetMatrix("_PlanetGlobalColorWorldToLocal",Matrix4x4.Rotate(Quaternion.Inverse(planetToRender)));
            float blend=settings?settings.Weight:0;
            properties.SetFloat("_PlanetGlobalColorBlend",blend);
            if(blend>0)
            {
                properties.SetTexture("_PlanetGlobalColorMap",settings.ColorMap);
                properties.SetFloat("_PlanetGlobalColorDistanceBlend",settings.DistanceBlend(altitude));
                if(settings.ReferenceIsLinear)properties.SetVector("_PlanetGlobalColorReference",(Vector4)settings.LocalReferenceColor);
                else properties.SetColor("_PlanetGlobalColorReference",settings.LocalReferenceColor);
                properties.SetVector("_PlanetGlobalColorControls",new Vector4(settings.SurfaceTintStrength,settings.UseMapAlphaForStrength?1:0,0,0));
            }
        }
    }
}
