using System;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Terrain Recipe",fileName="TerrainRecipe")]
    public sealed class PlanetTerrainRecipeAsset : ScriptableObject
    {
        public int Seed=7243;
        public SurfaceStyle Style=SurfaceStyle.EarthLike;
        public double Radius=2000000, SeaLevel=0, MinimumHeight=-6000, MaximumHeight=6000;
        public SurfaceBakeSettings Bake=SurfaceBakeSettings.Preview;
        public double DetailWavelengthMetres=128, DetailAmplitudeMetres=2;
        public PlanetTerrainRegionRecipe[] Regions=Array.Empty<PlanetTerrainRegionRecipe>();
        public PlanetSurfaceDataAsset Published;
        [HideInInspector] public PlanetHydrologyDataAsset Hydrology;
        [HideInInspector] public PlanetSurfaceDataAsset[] LodPyramid;
        [HideInInspector] public double LastBakeSeconds,LastMassResidual,LastLodError;
        [HideInInspector] public long LastWorkingBytes;
        [HideInInspector] public int LastNodeCount;
        public SurfaceRecipe Recipe => new SurfaceRecipe(Seed,Style,Radius,MinimumHeight,MaximumHeight,SeaLevel,algorithmVersion:SurfaceRecipe.CurrentAlgorithmVersion);
    }

    [Serializable]
    public sealed class PlanetTerrainRegionRecipe
    {
        public double Latitude,Longitude,Heading,WidthMetres=8192;
        public int Cells=256,Priority=1;
        public double ContextMetres=1024,BlendMetres=256,DetailWavelengthMetres=128,DetailAmplitudeMetres=40;
        public SurfaceBakeSettings Erosion=SurfaceBakeSettings.Preview;
        public SurfaceRegionRefinementSettings Capture(double radius)
        {
            var address=new PlanetSurfaceAddress{Latitude=Latitude,Longitude=Longitude,Heading=Heading};
            if(!address.IsValid||!math.isfinite(WidthMetres)||WidthMetres<=0)throw new ArgumentException("Regional recipe requires finite metric dimensions and a valid planet address.");
            var up=PlanetSurfaceCoordinates.Direction(Latitude,Longitude);
            double longitude=math.radians(Longitude%360),angle=math.radians(Heading%360);
            var east=new double3(-math.sin(longitude),0,math.cos(longitude));
            var north=math.normalize(math.cross(east,up));var right=math.normalize(math.cross(up,north));
            double s=math.sin(angle),c=math.cos(angle);
            return new SurfaceRegionRefinementSettings {
                Projection=new SurfaceRegionProjection(up,right*c-north*s,north*c+right*s,radius,new double2(-WidthMetres*.5),new double2(WidthMetres*.5)),
                Resolution=new int2(Cells),Priority=Priority,ContextMetres=ContextMetres,BlendMetres=BlendMetres,
                DetailWavelengthMetres=DetailWavelengthMetres,DetailAmplitudeMetres=DetailAmplitudeMetres,Erosion=Erosion?.Clone()
            };
        }
    }
}
