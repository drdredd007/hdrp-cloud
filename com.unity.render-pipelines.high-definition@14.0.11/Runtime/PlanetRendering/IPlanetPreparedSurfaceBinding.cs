using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Optional read-only prepared terrain data. The offset passed to
    /// surface shaders is in planet axes relative to planetAnchor, independent
    /// of the Native bank's local axes. No generation is performed by this hook.</summary>
    public interface IPlanetPreparedSurfaceBinding
    {
        void Bind(MaterialPropertyBlock properties,double3 planetAnchor,Quaternion planetRotation);
    }

    /// <summary>Optional material-local specialization of immutable prepared
    /// data. Property blocks cannot select shader variants. Implementations
    /// configure only the renderer-owned material, never global keywords.</summary>
    public interface IPlanetPreparedSurfaceMaterialBinding
    {
        void ConfigureMaterial(Material material);
    }

    public static class PlanetPreparedSurfaceBinding
    {
        public const string ReferenceKeyword="_PREPARED_BACKEND_REFERENCE";
        public const string WholeFloatKeyword="_PREPARED_BACKEND_WHOLE_FLOAT";
        public const string NativeKeyword="_PREPARED_BACKEND_NATIVE";
        public const string MixedKeyword="_PREPARED_BACKEND_MIXED";
        static readonly string[] Backends={ReferenceKeyword,WholeFloatKeyword,NativeKeyword,MixedKeyword};
        static readonly string[] TextureCounts={"_PREPARED_TEXTURES_1","_PREPARED_TEXTURES_2","_PREPARED_TEXTURES_4","_PREPARED_TEXTURES_8"};
        static void ResetMaterial(Material material)
        {
            material.DisableKeyword("_PREPARED_COLOUR");
            foreach(var keyword in Backends)material.DisableKeyword(keyword);
            foreach(var keyword in TextureCounts)material.DisableKeyword(keyword);
        }
        public static void ConfigureMaterial(Material material,IPlanetPreparedSurfaceBinding source)
        {
            if(!material)return;
            ResetMaterial(material);
            if(source==null)return;
            if(source is IPlanetPreparedSurfaceMaterialBinding specialized)specialized.ConfigureMaterial(material);
            else {material.EnableKeyword(MixedKeyword);material.EnableKeyword(TextureCounts[3]);}
        }
        public static void ConfigureMaterial(Material material,bool hierarchy,bool wholeFloat,int textureResources)
        {
            if(!material)return;
            if(textureResources<0||textureResources>8)throw new System.ArgumentOutOfRangeException(nameof(textureResources));
            int kinds=(hierarchy?1:0)+(wholeFloat?1:0)+(textureResources>0?1:0);
            if(kinds==0)throw new System.ArgumentException("At least one captured prepared source backend is required.");
            ResetMaterial(material);
            material.EnableKeyword(kinds>1?MixedKeyword:hierarchy?ReferenceKeyword:wholeFloat?WholeFloatKeyword:NativeKeyword);
            if(textureResources>0)material.EnableKeyword(TextureCounts[textureResources<=1?0:textureResources<=2?1:textureResources<=4?2:3]);
        }
        public static void Bind(MaterialPropertyBlock properties,IPlanetPreparedSurfaceBinding source,
            double3 planetAnchor,Quaternion planetRotation)
        {
            properties.SetFloat("_PlanetMaterialMaskEnabled",0);properties.SetInt("_PlanetMaterialSourceMask",0);
            properties.SetFloat("_PreparedPlanetEnabled",0);properties.SetFloat("_PreparedColourEnabled",0);properties.SetFloat("_PreparedColourUnlit",0);
            source?.Bind(properties,planetAnchor,planetRotation);
        }
    }
}
