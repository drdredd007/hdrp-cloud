namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetTerrainShadowRange { PlanetHorizon, LimitedDistance }
    [CreateAssetMenu(menuName="Rendering/HDRP/Periodic Planet Snow and Rock")]
    public sealed class PlanetPeriodicSurfaceSettings : ScriptableObject
    {
        public bool Enabled=true;
        public Texture2D SnowCoverage;
        public Texture2DArray DetailColor,DetailSlopes;
        public PlanetGlobalColorSettings GlobalColor;
        public bool EnableTerrainShadows=true;
        [Range(0,1)] public float TerrainShadowStrength=1;
        [Tooltip("1 = full, 2 = half, 4 = quarter screen resolution.")] public int TerrainShadowDownsample=2;
        public PlanetTerrainShadowRange TerrainShadowRange=PlanetTerrainShadowRange.PlanetHorizon;
        [Range(32,256)] public int TerrainShadowSteps=128;
        [Min(100)] public float TerrainShadowDistance=8000;
        [Range(.1f,10)] public float TerrainShadowBias=1.5f;
        [Range(0,1)] public float TerrainShadowSunRadiusDegrees=.27f;
        public Color SnowColor=new Color(.9f,.94f,.97f),RockColor=new Color(.31f,.33f,.36f);
        [Range(0,60)] public float SnowFullSlopeDegrees=18;
        [Range(1,80)] public float SnowBareSlopeDegrees=42;
        [Tooltip("Repeat periods must divide the renderer's 4096 metre detail origin period.")]
        public float SnowTileMetres=2,RockTileMetres=4;
        [Range(0,2)] public float SnowNormalStrength=.35f,RockNormalStrength=.65f;
        [Range(0,1)] public float SnowSmoothness=.35f,RockSmoothness=.2f;
        [HideInInspector] public Vector4 SnowMeanColor=Vector4.one,RockMeanColor=Vector4.one;
        static bool ValidPeriod(float value)=>value>0&&value<=4096&&Mathf.Abs(4096/value-Mathf.Round(4096/value))<.0001f;
        public bool IsValid=>Enabled&&SnowCoverage&&DetailColor&&DetailSlopes&&DetailColor.depth==2&&DetailSlopes.depth==2&&
            SnowBareSlopeDegrees>SnowFullSlopeDegrees&&ValidPeriod(SnowTileMetres)&&ValidPeriod(RockTileMetres);
        public static void Bind(MaterialPropertyBlock properties,PlanetPeriodicSurfaceSettings settings,bool periodic,float altitudeMetres=0)
        {
            bool enabled=periodic&&settings&&settings.IsValid;
            properties.SetFloat("_PeriodicSurfaceEnabled",enabled?1:0);
            PlanetGlobalColorSettings.Bind(properties,enabled?settings.GlobalColor:null,altitudeMetres);
            if(!enabled)return;
            properties.SetTexture("_PeriodicSnowCoverage",settings.SnowCoverage);
            properties.SetTexture("_PeriodicDetailColor",settings.DetailColor);
            properties.SetTexture("_PeriodicDetailSlopes",settings.DetailSlopes);
            properties.SetColor("_PeriodicSnowColor",settings.SnowColor.linear);
            properties.SetColor("_PeriodicRockColor",settings.RockColor.linear);
            properties.SetVector("_PeriodicSnowMean",settings.SnowMeanColor);
            properties.SetVector("_PeriodicRockMean",settings.RockMeanColor);
            properties.SetVector("_PeriodicDetailControls",new Vector4(settings.SnowTileMetres,settings.RockTileMetres,settings.SnowNormalStrength,settings.RockNormalStrength));
            properties.SetVector("_PeriodicSmoothness",new Vector4(settings.SnowSmoothness,settings.RockSmoothness,0,0));
        }
    }
}
