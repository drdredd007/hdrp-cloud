using System;
using Unity.Mathematics;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Generator",fileName="Planet")]
    public sealed class PlanetGeneratorAsset : ScriptableObject
    {
        [HideInInspector] public int PlanetId=1;
        [Header("Surface")]
        public int Seed=7243;
        public WorldOrogenSettings Orogen=new WorldOrogenSettings();
        public WorldOrogenMapView MapView=WorldOrogenMapView.Terrain;
        [Tooltip("0 selects a canonical base map resolution from the original graph detail. Generation detail remains independent.")]
        public int BaseMapResolution;
        public PlanetOrogenDetailSettings TerrainDetail = new PlanetOrogenDetailSettings();
        [SerializeField,HideInInspector] WorldOrogenSettings publishedOrogen;
        public WorldOrogenSettings PublishedOrogen=>publishedOrogen?.Clone();
        public WorldOrogenSettings CaptureOrogen(){var value=(Orogen??new WorldOrogenSettings()).Clone();value.Seed=Seed;return value;}
        public void RecordPublishedOrogen(WorldOrogenSettings value){publishedOrogen=value?.Clone();}
        public PlanetTerrainStyle TerrainStyle=PlanetTerrainStyle.EarthLike;
        public double Radius=6371000.0/3;
        public double Relief=6000;
        [Tooltip("Optional immutable signed terrain snapshot. Unassigned keeps the existing noise surface.")]
        public PlanetSurfaceDataAsset SurfaceData;
        [Tooltip("Independent periodic heightmap source. Overrides the procedural generator and signed bake when assigned.")]
        public PlanetPeriodicHeightAsset PeriodicHeightSource;
        [Tooltip("Optional native four-layer PBR palette. Unassigned retains the existing scaled procedural surface renderer.")]
        public PlanetTerrainMaterialSettings NativeMaterialSettings;
        [Tooltip("Planet-wide albedo and material tint independent of the height source.")]
        public PlanetGlobalColorSettings GlobalColor;
        [Tooltip("Camera-owned native terrain coverage, geometry spacing and bounded preparation. These settings do not change collision sampling.")]
        public PlanetNativeSurfaceSettings NativeSurfaceSettings=PlanetNativeSurfaceSettings.Default;
        [Tooltip("Opaque static sea for signed EarthLike recipes. SeaLevel belongs to the immutable surface recipe; this does not change seabed collision.")]
        public PlanetOceanSettings OceanSettings=PlanetOceanSettings.Default;
        [Tooltip("Optional deterministic GPU scatter. Collision interest is managed independently by the host.")]
        public PlanetScatterSettings ScatterSettings;
        public Vector3 Orientation=new Vector3(0,0,35);
        [Header("Level of detail")]
        [Range(0,PlanetLodSelector.MaximumSupportedLevel)] public int MaximumLevel=8;
        [Range(6,384)] public int PatchBudget=192;
        [Range(.5f,32)] public float PixelError=4;
        [Tooltip("Patches generated per rendered frame (GPU dispatches).")][Range(1,128)] public int PatchesPerFrame=32;
        [Header("Atmosphere")]
        public PlanetAtmosphereSettings Atmosphere=PlanetAtmosphereSettings.EarthLike;
        public PlanetDefinition Definition
        {
            get
            {
                if(PeriodicHeightSource)
                {
                    var blob=PeriodicHeightSource.PreviewBlob;
                    return new PlanetDefinition {Id=PlanetId,Seed=Seed,GeneratorVersion=4,Radius=Radius,
                        Relief=math.max(math.abs(blob.Value.MinimumMetres),math.abs(blob.Value.MaximumMetres)),PeriodicHeight=blob};
                }
                if(!SurfaceData)return new PlanetDefinition {Id=PlanetId,Seed=Seed,GeneratorVersion=(int)TerrainStyle,Radius=Radius,Relief=Relief};
                if(!SurfaceData.TryCreateSnapshot(out var snapshot,out _))return default;
                var recipe=snapshot.Recipe;
                return new PlanetDefinition {Id=PlanetId,Seed=recipe.Seed,GeneratorVersion=3,Radius=recipe.Radius,
                    Relief=math.max(math.abs(snapshot.MinimumHeight),math.abs(snapshot.MaximumHeight)),Surface=SurfaceData.Descriptor};
            }
        }
        public PlanetLodSettings Lod => new PlanetLodSettings {MaximumLevel=MaximumLevel,PatchBudget=PatchBudget,PixelError=PixelError,PatchesPerFrame=PatchesPerFrame};
    }
    public struct PlanetLodSettings : IEquatable<PlanetLodSettings>
    {
        public int MaximumLevel,PatchBudget,PatchesPerFrame;
        public bool Equals(PlanetLodSettings other)=>MaximumLevel==other.MaximumLevel && PatchBudget==other.PatchBudget && PatchesPerFrame==other.PatchesPerFrame && PixelError==other.PixelError;
        public float PixelError;
        public static PlanetLodSettings Default => new PlanetLodSettings {MaximumLevel=8,PatchBudget=192,PatchesPerFrame=32,PixelError=4};
        public PlanetLodSettings Clamped => new PlanetLodSettings {MaximumLevel=math.clamp(MaximumLevel,0,PlanetLodSelector.MaximumSupportedLevel),PatchBudget=math.clamp(PatchBudget,6,384),
            PatchesPerFrame=math.clamp(PatchesPerFrame,1,128),PixelError=math.isfinite(PixelError)?math.clamp(PixelError,.5f,32):4};
    }
}
