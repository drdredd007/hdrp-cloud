using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Generator",fileName="Planet")]
    public sealed class PlanetGeneratorAsset : ScriptableObject
    {
        [HideInInspector] public int PlanetId=1;
        [Header("Surface")]
        public int Seed=7243;
        public double Radius=6371000.0/3;
        public double Relief=6000;
        public Vector3 Orientation=new Vector3(0,0,35);
        [Header("Level of detail")]
        [Range(0,10)] public int MaximumLevel=8;
        [Range(6,384)] public int PatchBudget=192;
        [Range(.5f,32)] public float PixelError=4;
        [Tooltip("Patches generated per rendered frame (GPU dispatches).")][Range(1,128)] public int PatchesPerFrame=32;
        [Header("Atmosphere")]
        public PlanetAtmosphereSettings Atmosphere=PlanetAtmosphereSettings.EarthLike;
        public PlanetDefinition Definition => new PlanetDefinition {Id=PlanetId,Seed=Seed,GeneratorVersion=1,Radius=Radius,Relief=Relief};
        public PlanetLodSettings Lod => new PlanetLodSettings {MaximumLevel=MaximumLevel,PatchBudget=PatchBudget,PixelError=PixelError,PatchesPerFrame=PatchesPerFrame};
    }
    public struct PlanetLodSettings : IEquatable<PlanetLodSettings>
    {
        public int MaximumLevel,PatchBudget,PatchesPerFrame;
        public bool Equals(PlanetLodSettings other)=>MaximumLevel==other.MaximumLevel && PatchBudget==other.PatchBudget && PatchesPerFrame==other.PatchesPerFrame && PixelError==other.PixelError;
        public float PixelError;
        public static PlanetLodSettings Default => new PlanetLodSettings {MaximumLevel=8,PatchBudget=192,PatchesPerFrame=32,PixelError=4};
        public PlanetLodSettings Clamped => new PlanetLodSettings {MaximumLevel=math.clamp(MaximumLevel,0,10),PatchBudget=math.clamp(PatchBudget,6,384),
            PatchesPerFrame=math.clamp(PatchesPerFrame,1,128),PixelError=math.isfinite(PixelError)?math.clamp(PixelError,.5f,32):4};
    }
}
