using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Colour-independent terrain rules and a broad signed-curvature context.</summary>
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Surface Paint")]
    public sealed class PlanetSurfacePaintSettings : ScriptableObject
    {
        public bool Enabled=true;
        public SurfaceRockyPaintRules Rules=SurfaceRockyPaintRules.Default;
        public Texture2D CurvatureMap,Palette;
        [Range(0,1)] public float ColorStrength=.65f;
        [Range(0,.3f)] public float GrainStrength=.08f;
        [Min(.25f)] public double GrainWavelengthMetres=4;
        public string SourceContentDigest;
        public double CurvatureRadiusMetres;
        public bool IsValid=>Rules.IsValid&&CurvatureMap&&!CurvatureMap.isDataSRGB&&Palette&&!Palette.isDataSRGB&&Palette.width==256&&Palette.height==2&&
            math.isfinite(ColorStrength)&&ColorStrength>=0&&ColorStrength<=1&&math.isfinite(GrainStrength)&&GrainStrength>=0&&GrainStrength<=.3f&&
            math.isfinite(GrainWavelengthMetres)&&GrainWavelengthMetres>=.25&&math.isfinite(CurvatureRadiusMetres)&&CurvatureRadiusMetres>0;
        public static void Bind(MaterialPropertyBlock properties,PlanetSurfacePaintSettings settings,double3 anchor,double radius)
        {
            bool enabled=settings&&settings.Enabled&&settings.IsValid;
            properties.SetFloat("_PlanetPaintEnabled",enabled?1:0);if(!enabled)return;
            var rules=settings.Rules;
            properties.SetTexture("_PlanetPaintCurvature",settings.CurvatureMap);properties.SetTexture("_PlanetPaintPalette",settings.Palette);
            properties.SetVector("_PlanetPaintFeatureGrid",new Vector4(settings.CurvatureMap.width-1,settings.CurvatureMap.height-1,settings.CurvatureMap.width,settings.CurvatureMap.height));
            properties.SetVector("_PlanetPaintAnchor",new Vector4((float)(anchor.x/radius),(float)(anchor.y/radius),(float)(anchor.z/radius),(float)(1/radius)));
            properties.SetVector("_PlanetPaintSlope",new Vector4(rules.SlopeStartDegrees,rules.SlopeEndDegrees,rules.SedimentCoverage,rules.SlopeExposure));
            properties.SetVector("_PlanetPaintTerrain",new Vector4(rules.RidgeExposure,rules.HollowAccumulation,rules.CurvatureReferencePerMetre,rules.BoundaryVariation));
            properties.SetVector("_PlanetPaintColor",new Vector4(settings.ColorStrength,settings.GrainStrength,0,0));
            uint seed=unchecked((uint)rules.Seed);
            BindNoise(properties,anchor,rules.BoundaryWavelengthMetres,false);
            BindNoise(properties,anchor,settings.GrainWavelengthMetres,true);
            properties.SetVector("_PlanetPaintSeed",new Vector4(seed&65535,seed>>16,0,0));
        }
        static readonly int phaseId=Shader.PropertyToID("_PlanetPaintPhase"),cellId=Shader.PropertyToID("_PlanetPaintCell"),
            grainPhaseId=Shader.PropertyToID("_PlanetPaintGrainPhase"),grainCellId=Shader.PropertyToID("_PlanetPaintGrainCell");
        static void BindNoise(MaterialPropertyBlock properties,double3 anchor,double scale,bool grain)
        {
            var phase=PlanetTerrainMaterialBinding.TexturePhase(anchor,scale);var cell=PlanetTerrainMaterialBinding.VariationCell(anchor,scale);
            properties.SetVector(grain?grainPhaseId:phaseId,new Vector4((float)phase.x,(float)phase.y,(float)phase.z,(float)(1/scale)));
            properties.SetVector(grain?grainCellId:cellId,new Vector4((float)cell.x,(float)cell.y,(float)cell.z,0));
        }
    }
}
