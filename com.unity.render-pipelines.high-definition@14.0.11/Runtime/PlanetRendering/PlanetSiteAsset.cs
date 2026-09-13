using System;
namespace UnityEngine.Rendering.HighDefinition
{
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Site",fileName="Planet Site")]
    public sealed class PlanetSiteAsset : ScriptableObject
    {
        public PlanetGeneratorAsset Generator;
        public PlanetSurfaceAddress Address=new PlanetSurfaceAddress {Latitude=20,Longitude=35};
        public PlanetSiteSettings Settings=PlanetSiteSettings.Default;
        public PlanetSiteLayout Build()
        {
            if(!Generator)throw new InvalidOperationException("Assign a Planet Generator asset.");
            return PlanetSiteLayout.Build(Generator.Definition,Address,Settings);
        }
    }
}
