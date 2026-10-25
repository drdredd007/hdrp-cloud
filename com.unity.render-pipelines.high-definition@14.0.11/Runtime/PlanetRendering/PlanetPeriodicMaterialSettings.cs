using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetPeriodicMaterialView { Materials, ResourceRoles, MaterialLayers }

    [CreateAssetMenu(menuName="Rendering/HDRP/Periodic Planet Materials")]
    public sealed class PlanetPeriodicMaterialSettings : ScriptableObject
    {
        public PlanetTerrainMaterialSettings Palette;
        public Texture2D LandformRoles, PlainsRoles;
        [HideInInspector] public string GeometryDigest;
        [Tooltip("Grass, sand, rock, snow weights of the local ground. Resource RGBA means ground, loose ground, scree, exposed rock, not four direct material IDs.")]
        public Vector4 BackgroundLayers=new Vector4(1,0,0,0);
        public PlanetPeriodicMaterialView View;
        public bool IsValid=>Palette&&Palette.IsValid&&LandformRoles&&PlainsRoles&&
            !LandformRoles.isDataSRGB&&!PlainsRoles.isDataSRGB&&
            math.all(math.isfinite((float4)BackgroundLayers))&&math.all((float4)BackgroundLayers>=0)&&math.csum((float4)BackgroundLayers)>0;
        public static void Bind(MaterialPropertyBlock block,PlanetPeriodicMaterialSettings settings)
        {
            bool ready=settings&&settings.IsValid;
            block.SetFloat("_PeriodicMaterialEnabled",ready?1:0);
            block.SetFloat("_PeriodicMaterialView",ready?(int)settings.View:0);
            if(!ready)return;
            block.SetTexture("_PeriodicLandformRoles",settings.LandformRoles);
            block.SetTexture("_PeriodicPlainsRoles",settings.PlainsRoles);
            var b=settings.BackgroundLayers;
            block.SetVector("_PeriodicBackgroundLayers",b/(b.x+b.y+b.z+b.w));
        }
#if UNITY_EDITOR
        void OnValidate(){UnityEditor.SceneView.RepaintAll();UnityEditor.EditorApplication.QueuePlayerLoopUpdate();}
#endif
    }
}
