using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetRegionalColorMode { PaletteCoordinate, ReferenceRGB, LandformWeights }

    /// <summary>Source-bound regional colour, independent of mesh density and physical displacement.</summary>
    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Regional Colour")]
    public sealed class PlanetRegionalColorSettings : ScriptableObject
    {
        public bool Enabled=true;
        public PlanetSurfaceDataAsset Source;
        public string SourceContentDigest;
        public double Radius;
        public Texture2DArray Maps;
        public Texture2D Palette;
        public PlanetGeologicalNormalRegion[] Regions;
        public PlanetRegionalColorMode Mode;
        [Tooltip("Palette positions for rock tone, sediment tone and peak tone. These are colour regimes, not Scatter species masks.")]
        public Vector3 PaletteCoordinates=new Vector3(.17f,.54f,.9f);
        [Range(0,1)] public float Strength=1;
        [Tooltip("Zero compares the unmodified palette with the reference. One retains the shared planet tint.")]
        [Range(0,1)] public float GlobalToneStrength;
        [Tooltip("Optional neutral luminance detail from the existing PBR albedo; zero compares raw colour.")]
        [Range(0,1)] public float DetailAlbedoStrength;
        [Min(0)] public float EdgeBlendMetres=600;
        [NonSerialized] internal int Version;
        void OnValidate(){Version++;}
        public bool IsValid=>Source&&Maps&&!Maps.isDataSRGB&&Maps.format==TextureFormat.RGBAHalf&&
            Regions!=null&&Regions.Length>0&&Regions.Length<=64&&math.isfinite(Radius)&&Radius>0&&
            (Mode==PlanetRegionalColorMode.ReferenceRGB||(Mode==PlanetRegionalColorMode.PaletteCoordinate||Mode==PlanetRegionalColorMode.LandformWeights)&&Palette&&!Palette.isDataSRGB&&Palette.width>1&&Palette.height==1)&&
            math.all(math.isfinite((float3)PaletteCoordinates))&&math.all((float3)PaletteCoordinates>=0)&&math.all((float3)PaletteCoordinates<=1)&&
            math.isfinite(Strength)&&Strength>=0&&Strength<=1&&math.isfinite(GlobalToneStrength)&&GlobalToneStrength>=0&&GlobalToneStrength<=1&&
            math.isfinite(DetailAlbedoStrength)&&DetailAlbedoStrength>=0&&DetailAlbedoStrength<=1&&math.isfinite(EdgeBlendMetres)&&EdgeBlendMetres>=0;
        public bool Matches(PlanetSurfaceDescriptor surface)
        {
            if(!Enabled||!IsValid||!surface.IsBound||!Source.TryCreateSnapshot(out var snapshot,out _))return false;
            return snapshot.ContentDigest.ToString()==SourceContentDigest&&snapshot.Recipe.Radius==Radius&&PlanetSurfaceDescriptor.FromSnapshot(snapshot).Equals(surface);
        }
    }

    public static class PlanetRegionalColorBinding
    {
        [StructLayout(LayoutKind.Sequential)] struct Region {public Vector4 Anchor,Right,Forward,Domain;}
        sealed class Entry {internal GraphicsBuffer Buffer;internal PlanetGeologicalNormalRegion[] Regions;internal string Digest;internal int Version;}
        static readonly Dictionary<PlanetRegionalColorSettings,Entry> cache=new Dictionary<PlanetRegionalColorSettings,Entry>();
        public static void Bind(MaterialPropertyBlock p,PlanetRegionalColorSettings s,PlanetSurfaceDescriptor surface,double3 anchor)
        {
            p.SetInteger("_PlanetRegionalColorCount",0);
            if(!s||!s.Matches(surface)||s.Strength<=0)return;
            if(!cache.TryGetValue(s,out var e)||e.Regions!=s.Regions||e.Digest!=s.SourceContentDigest||e.Version!=s.Version)
            {
                var packed=new Region[s.Regions.Length];
                for(int i=0;i<packed.Length;i++) {
                    var r=s.Regions[i];
                    if(r.Slice<0||r.Slice>=s.Maps.depth||r.Domain.z<=0||r.Domain.w<=0)return;
                    packed[i]=new Region{Anchor=new Vector4(r.Anchor.x,r.Anchor.y,r.Anchor.z,(float)s.Radius),
                        Right=new Vector4(r.Right.x,r.Right.y,r.Right.z,r.Slice),Forward=new Vector4(r.Forward.x,r.Forward.y,r.Forward.z,0),Domain=r.Domain};
                }
                var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,packed.Length,Marshal.SizeOf<Region>());
                try{buffer.SetData(packed);}catch{buffer.Dispose();throw;}e?.Buffer.Dispose();
                e=new Entry{Buffer=buffer,Regions=s.Regions,Digest=s.SourceContentDigest,Version=s.Version};cache[s]=e;
            }
            p.SetBuffer("_PlanetRegionalColorRegions",e.Buffer);p.SetTexture("_PlanetRegionalColorMaps",s.Maps);
            p.SetTexture("_PlanetRegionalColorPalette",s.Palette?s.Palette:Texture2D.whiteTexture);
            p.SetVector("_PlanetRegionalColorAnchor",(Vector3)(float3)(anchor/s.Radius));
            p.SetVector("_PlanetRegionalColorControls",new Vector4(s.Strength,s.GlobalToneStrength,s.DetailAlbedoStrength,(float)s.Mode));
            p.SetVector("_PlanetRegionalColorPaletteCoordinates",s.PaletteCoordinates);
            p.SetVector("_PlanetRegionalColorMetric",new Vector4((float)(1/s.Radius),s.EdgeBlendMetres,0,0));
            int w=s.Maps.width,h=s.Maps.height;
            p.SetVector("_PlanetRegionalColorTexels",new Vector4((w-1f)/w,(h-1f)/h,.5f/w,.5f/h));
            p.SetInteger("_PlanetRegionalColorCount",s.Regions.Length);
        }
        static void Cleanup(){foreach(var e in cache.Values)e.Buffer.Dispose();cache.Clear();}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset()=>Cleanup();
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod] static void InstallCleanup()=>UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Cleanup;
#endif
    }
}
