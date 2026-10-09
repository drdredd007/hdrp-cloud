using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct PlanetGeologicalNormalRegion
    {
        public Vector3 Anchor,Right,Forward;
        public Vector4 Domain;
        public int Slice;
    }

    /// <summary>Absolute normals of the composed height field, stored in each region's chart basis.</summary>
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Geological Normals",fileName="GeologicalNormals")]
    public sealed class PlanetGeologicalNormalSettings : ScriptableObject
    {
        public bool Enabled=true;
        public PlanetSurfaceDataAsset Source;
        public string SourceContentDigest;
        public double Radius,DerivativeStepMetres=1;
        public Texture2DArray NormalMaps;
        public PlanetGeologicalNormalRegion[] Regions;
        [Range(0,1)] public float Strength=1;
        [Tooltip("Unresolved normal variance broadens roughness. Zero disables this bounded approximation.")]
        [Range(0,1)] public float VarianceScale=.5f;
        [NonSerialized] internal int Version;
        void OnValidate(){Version++;}
        public bool IsValid=>Source&&NormalMaps&&!NormalMaps.isDataSRGB&&NormalMaps.format==TextureFormat.RGBAHalf&&
            Regions!=null&&Regions.Length>0&&Regions.Length<=64&&math.isfinite(Radius)&&Radius>0&&
            math.isfinite(Strength)&&Strength>=0&&Strength<=1&&math.isfinite(VarianceScale)&&VarianceScale>=0&&VarianceScale<=1;
        public bool Matches(PlanetSurfaceDescriptor surface)
        {
            if(!Enabled||!IsValid||!surface.IsBound||!Source.TryCreateSnapshot(out var snapshot,out _))return false;
            return snapshot.ContentDigest.ToString()==SourceContentDigest&&snapshot.Recipe.Radius==Radius&&
                PlanetSurfaceDescriptor.FromSnapshot(snapshot).Equals(surface);
        }
    }

    public static class PlanetGeologicalNormalBinding
    {
        [StructLayout(LayoutKind.Sequential)] struct Region
        {public Vector4 Anchor,Right,Forward,Domain;}
        sealed class Entry
        {
            internal GraphicsBuffer Buffer;
            internal PlanetGeologicalNormalRegion[] Regions;
            internal string Digest;
            internal int Version;
        }
        static readonly Dictionary<PlanetGeologicalNormalSettings,Entry> cache=new Dictionary<PlanetGeologicalNormalSettings,Entry>();
        public static void Bind(MaterialPropertyBlock properties,PlanetGeologicalNormalSettings settings,
            PlanetSurfaceDescriptor surface,double3 planetAnchor)
        {
            properties.SetInteger("_PlanetGeologicalNormalCount",0);
            if(!settings||!settings.Matches(surface)||settings.Strength<=0)return;
            if(!cache.TryGetValue(settings,out var entry)||entry.Regions!=settings.Regions||entry.Digest!=settings.SourceContentDigest||entry.Version!=settings.Version)
            {
                var packed=new Region[settings.Regions.Length];
                for(int i=0;i<packed.Length;i++)
                {
                    var r=settings.Regions[i];
                    if(r.Slice<0||r.Slice>=settings.NormalMaps.depth||r.Domain.z<=0||r.Domain.w<=0)return;
                    packed[i]=new Region{Anchor=new Vector4(r.Anchor.x,r.Anchor.y,r.Anchor.z,(float)settings.Radius),
                        Right=new Vector4(r.Right.x,r.Right.y,r.Right.z,r.Slice),Forward=new Vector4(r.Forward.x,r.Forward.y,r.Forward.z,0),Domain=r.Domain};
                }
                var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,packed.Length,Marshal.SizeOf<Region>());
                try{buffer.SetData(packed);}catch{buffer.Dispose();throw;}
                entry?.Buffer.Dispose();
                entry=new Entry{Buffer=buffer,Regions=settings.Regions,Digest=settings.SourceContentDigest,Version=settings.Version};cache[settings]=entry;
            }
            properties.SetBuffer("_PlanetGeologicalNormalRegions",entry.Buffer);
            properties.SetTexture("_PlanetGeologicalNormalMaps",settings.NormalMaps);
            properties.SetVector("_PlanetGeologicalNormalAnchor",(Vector3)(float3)(planetAnchor/settings.Radius));
            properties.SetVector("_PlanetGeologicalNormalControls",new Vector4((float)(1/settings.Radius),settings.Strength,settings.VarianceScale,0));
            int w=settings.NormalMaps.width,h=settings.NormalMaps.height;
            properties.SetVector("_PlanetGeologicalNormalTexels",new Vector4((w-1f)/w,(h-1f)/h,.5f/w,.5f/h));
            properties.SetInteger("_PlanetGeologicalNormalCount",settings.Regions.Length);
        }
        static void Cleanup(){foreach(var entry in cache.Values)entry.Buffer.Dispose();cache.Clear();}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset()=>Cleanup();
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod] static void InstallCleanup()=>UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Cleanup;
#endif
    }
}
