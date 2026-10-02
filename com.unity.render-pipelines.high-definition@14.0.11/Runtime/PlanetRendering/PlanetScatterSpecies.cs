using System;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetScatterColliderShape { None, Box, Sphere, Capsule }

    /// <summary>One stable placement species and its two native opaque mesh LODs.</summary>
    [CreateAssetMenu(menuName="Rendering/Planet Scatter Species")]
    public sealed class PlanetScatterSpecies : ScriptableObject
    {
        public uint SpeciesId=1, Seed;
        [Range(0,30)] public int FixedLevel=16;
        [Range(1,4096)] public int CandidatesPerCell=64;
        public double DensityPerSquareMetre=.005;
        [Min(0), Tooltip("Same-species minimum chord on the reference sphere, in metres. Does not measure terrain geodesics or mesh clearance. Zero preserves independent proposals; large neighbourhoods are rejected before allocation.")]
        public double MinimumReferenceChordSpacingMetres;
        public double MinimumHeight=-1e12, MaximumHeight=1e12;
        [Range(0,90)] public float MaximumSlopeDegrees=45;
        [Range(0,1)] public float MinimumWetness, MaximumWetness=1;
        public Vector4 MaterialAffinity=Vector4.one;
        public Vector2 ScaleRange=new Vector2(.8f,1.2f);
        public double NormalSampleMetres=1;
        public SurfaceChannels RequiredChannels=SurfaceChannels.MaterialWeights|SurfaceChannels.ErosionData;
        public bool ExcludeSurfaceStamps=true;
        public Mesh NearMesh, FarMesh;
        [Tooltip("Supports HDRP/Lit or SpaceRunner/Planet Scatter Lit in Opaque mode, including alpha cutout. Transparent and custom shaders are rejected.")]
        public Material Material;
        static readonly int surfaceTypeId=Shader.PropertyToID("_SurfaceType");
        [NonSerialized] Shader validatedShader;
        [NonSerialized] bool supportedShader;
        [Min(1)] public float RenderDistance=800, ShadowDistance=1600;
        [Min(0)] public float LodDistance=150, LodHysteresis=15;
        public PlanetScatterColliderShape ColliderShape;
        public Vector3 ColliderCenter;
        public Vector3 ColliderSize=Vector3.one;
        [Min(0)] public float ColliderRadius=.5f, ColliderHeight=2;
        public bool CollisionEnabled=>ColliderShape!=PlanetScatterColliderShape.None;
        public SurfaceScatterSpecies Placement=>new SurfaceScatterSpecies(SpeciesId,FixedLevel,CandidatesPerCell,DensityPerSquareMetre,
            MinimumHeight,MaximumHeight,MaximumSlopeDegrees,MinimumWetness,MaximumWetness,
            (float4)MaterialAffinity,(float2)ScaleRange,NormalSampleMetres,Seed,RequiredChannels,ExcludeSurfaceStamps,MinimumReferenceChordSpacingMetres);
        public bool IsValid
        {
            get
            {
                try
                {
                    if(!Placement.IsValid||!NearMesh||!FarMesh||!ValidMaterial()||!Finite(RenderDistance)||!Finite(ShadowDistance)||
                        !Finite(LodDistance)||!Finite(LodHysteresis)||RenderDistance<=0||ShadowDistance<RenderDistance||
                        LodDistance<0||LodDistance>RenderDistance||LodHysteresis<0||LodHysteresis>LodDistance||
                        !math.all(math.isfinite((float3)ColliderCenter))||!math.all(math.isfinite((float3)ColliderSize))||
                        !Finite(ColliderRadius)||!Finite(ColliderHeight))return false;
                    return ColliderShape==PlanetScatterColliderShape.None||
                        ColliderShape==PlanetScatterColliderShape.Box&&math.all((float3)ColliderSize>0)||
                        ColliderShape==PlanetScatterColliderShape.Sphere&&ColliderRadius>0||
                        ColliderShape==PlanetScatterColliderShape.Capsule&&ColliderRadius>0&&ColliderHeight>=2*ColliderRadius;
                }
                catch(ArgumentException){return false;}
            }
        }
        bool ValidMaterial()
        {
            if(!Material||!Material.shader)return false;
            var shader=Material.shader;
            if(shader!=validatedShader)
            {
                // Shader names allocate managed strings. Cache this immutable identity
                // so fixed-tick readiness/commit validation remains allocation-free.
                validatedShader=shader;var name=shader.name;
                supportedShader=name=="HDRP/Lit"||name==PlanetScatterGpu.ShaderName;
            }
            return supportedShader&&Material.HasProperty(surfaceTypeId)&&Material.GetFloat(surfaceTypeId)==0;
        }
        static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        public float UnscaledBoundingRadius=>math.max(Radius(NearMesh),Radius(FarMesh));
        static float Radius(Mesh mesh)=>mesh?mesh.bounds.center.magnitude+mesh.bounds.extents.magnitude:0;
    }
}
