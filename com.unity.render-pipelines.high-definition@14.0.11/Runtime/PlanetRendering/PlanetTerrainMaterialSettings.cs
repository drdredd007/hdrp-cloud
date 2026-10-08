using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetTerrainNormalEncoding { UnityNormal, MetricSlopeMoments }
    [Serializable]
    public sealed class PlanetTerrainMaterialLayer
    {
        public Texture2D Albedo,Normal,Mask;
        [Tooltip("UnityNormal accepts existing RG/AG maps. MetricSlopeMoments requires linear RGBAHalf/Float: mean dh/dx, dh/dy (m/m), mean squared gradient, 1; mipmaps average moments without renormalizing.")]
        public PlanetTerrainNormalEncoding NormalEncoding;
        [Tooltip("Bounded isotropic slope-variance approximation for GGX roughness; zero disables filtering. Not an exact GGX convolution.")]
        [Range(0,1)] public float NormalVarianceScale=.5f;
        public Color Tint=Color.white;
        [Tooltip("Planet-local metres per texture repeat; placement and floating origins do not change this phase.")]
        public double MetresPerRepeat=2;
        [Range(0,4)] public float NormalScale=1;
        [Range(0,1)] public float Metallic;
        [Range(0,1)] public float Smoothness=.4f;
        [Range(0,1)] public float AmbientOcclusion=1;
        [Tooltip("Mask B is a shading blend height, never geometry or collision displacement.")]
        public float HeightAmplitudeMetres=.04f,HeightOffsetMetres;
        public bool IsValid=>(NormalEncoding==PlanetTerrainNormalEncoding.UnityNormal ||
            (NormalEncoding==PlanetTerrainNormalEncoding.MetricSlopeMoments && Normal && !Normal.isDataSRGB &&
             (Normal.format==TextureFormat.RGBAHalf || Normal.format==TextureFormat.RGBAFloat)))&&
            math.isfinite(NormalVarianceScale)&&NormalVarianceScale>=0&&NormalVarianceScale<=1&&
            math.isfinite(MetresPerRepeat)&&MetresPerRepeat>=.001&&MetresPerRepeat<=1e12&&
            math.isfinite(NormalScale)&&NormalScale>=0&&NormalScale<=4&&math.isfinite(Metallic)&&Metallic>=0&&Metallic<=1&&
            math.isfinite(Smoothness)&&Smoothness>=0&&Smoothness<=1&&math.isfinite(AmbientOcclusion)&&AmbientOcclusion>=0&&AmbientOcclusion<=1&&
            math.isfinite(HeightAmplitudeMetres)&&HeightAmplitudeMetres>=0&&math.isfinite(HeightOffsetMetres)&&
            math.isfinite(Tint.r)&&math.isfinite(Tint.g)&&math.isfinite(Tint.b)&&Tint.r>=0&&Tint.g>=0&&Tint.b>=0;
    }

    [CreateAssetMenu(menuName="Rendering/HDRP/Planet Terrain Materials",fileName="PlanetTerrainMaterials")]
    public sealed class PlanetTerrainMaterialSettings : ScriptableObject
    {
        // Canonical mask order is grass, sand, rock, snow; this is not HDRP's layered alpha/RGB order.
        public PlanetTerrainMaterialLayer Grass=new PlanetTerrainMaterialLayer {Tint=new Color(.24f,.34f,.15f)};
        public PlanetTerrainMaterialLayer Sand=new PlanetTerrainMaterialLayer {Tint=new Color(.65f,.55f,.36f)};
        public PlanetTerrainMaterialLayer Rock=new PlanetTerrainMaterialLayer {Tint=new Color(.34f,.32f,.30f)};
        public PlanetTerrainMaterialLayer Snow=new PlanetTerrainMaterialLayer {Tint=new Color(.85f,.89f,.92f)};
        public float HeightBlendTransitionMetres=.1f;
        [Range(1,16)] public float TriplanarBlendSharpness=4;
        [Range(0,1)] public float FarNormalStrength=.2f;
        [Tooltip("Continuous metric UV jitter suppresses regular repetition without adding texture samples. Zero preserves the original mapping.")]
        [Range(0,1)] public float AntiTilingStrength;
        [Range(0,.5f)] public float MacroVariationStrength;
        [Tooltip("Planet-local scale of texture warp and macro colour variation in metres.")]
        public double VariationWavelengthMetres=128;
        public uint VariationSeed=7243;
        [Tooltip("Reject a render bank lacking canonical material masks. Disabled explicitly uses one rock layer when masks are absent.")]
        public bool RequireMaterialWeights;
        public Shader NativeShader;
        public bool IsValid=>Grass!=null&&Grass.IsValid&&Sand!=null&&Sand.IsValid&&Rock!=null&&Rock.IsValid&&Snow!=null&&Snow.IsValid&&
            math.isfinite(HeightBlendTransitionMetres)&&HeightBlendTransitionMetres>0&&math.isfinite(TriplanarBlendSharpness)&&
            TriplanarBlendSharpness>=1&&TriplanarBlendSharpness<=16&&math.isfinite(FarNormalStrength)&&FarNormalStrength>=0&&FarNormalStrength<=1&&
            math.isfinite(AntiTilingStrength)&&AntiTilingStrength>=0&&AntiTilingStrength<=1&&math.isfinite(MacroVariationStrength)&&MacroVariationStrength>=0&&MacroVariationStrength<=.5f&&
            math.isfinite(VariationWavelengthMetres)&&VariationWavelengthMetres>=1&&VariationWavelengthMetres<=1e12;
        public PlanetTerrainMaterialLayer Layer(int index)
        {
            switch(index){case 0:return Grass;case 1:return Sand;case 2:return Rock;case 3:return Snow;default:throw new ArgumentOutOfRangeException(nameof(index));}
        }
    }

    /// <summary>CPU double phase reduction shared by native and scaled far materials.</summary>
    public static class PlanetTerrainMaterialBinding
    {
        public const string ShaderName="SpaceRunner/Planet Terrain Lit";
        static Texture2D flatNormal,neutralMask;
        public static double3 TexturePhase(double3 planetAnchor,double metresPerRepeat)
        {
            if(!math.all(math.isfinite(planetAnchor))||!math.isfinite(metresPerRepeat)||metresPerRepeat<=0)
                throw new ArgumentException("Texture phase requires a finite planet-local anchor and positive metric repeat.");
            var cycles=planetAnchor/metresPerRepeat;return cycles-math.floor(cycles);
        }
        public static double3 VariationCell(double3 planetAnchor,double metresPerCell)
        {
            TexturePhase(planetAnchor,metresPerCell);var cell=math.floor(planetAnchor/metresPerCell);
            // Exactly represented integer cells in a float material constant; the
            // shader hashes the local cell offset before applying the same modulus.
            return cell-math.floor(cell/65536)*65536;
        }
        static Texture2D Solid(string name,Color color)
        {
            var value=new Texture2D(1,1,TextureFormat.RGBA32,false,true){name=name,hideFlags=HideFlags.HideAndDontSave,
                wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear};
            value.SetPixel(0,0,color);value.Apply(false,true);return value;
        }
        public static void Bind(MaterialPropertyBlock properties,PlanetTerrainMaterialSettings settings,double3 planetAnchor,Quaternion planetToRender)
        {
            if(properties==null)throw new ArgumentNullException(nameof(properties));
            if(!settings||!settings.IsValid)throw new ArgumentException("A valid four-layer material palette is required.");
            if(!flatNormal)flatNormal=Solid("Planet neutral normal",new Color(.5f,.5f,1,1));
            if(!neutralMask)neutralMask=Solid("Planet neutral PBR mask",new Color(0,1,.5f,1));
            for(int i=0;i<4;i++)
            {
                var layer=settings.Layer(i);var phase=TexturePhase(planetAnchor,layer.MetresPerRepeat);
                properties.SetTexture("_PlanetLayerAlbedo"+i,layer.Albedo?layer.Albedo:Texture2D.whiteTexture);
                properties.SetTexture("_PlanetLayerNormal"+i,layer.Normal?layer.Normal:flatNormal);
                properties.SetTexture("_PlanetLayerMask"+i,layer.Mask?layer.Mask:neutralMask);
                // The shader consumes linear RGB; SetColor would convert this value a second time.
                var tint=layer.Tint.linear;
                properties.SetVector("_PlanetLayerTint"+i,new Vector4(tint.r,tint.g,tint.b,tint.a));
                properties.SetVector("_PlanetTexturePhase"+i,new Vector4((float)phase.x,(float)phase.y,(float)phase.z,(float)(1/layer.MetresPerRepeat)));
                properties.SetVector("_PlanetLayerControl"+i,new Vector4(layer.NormalScale,layer.HeightAmplitudeMetres,layer.HeightOffsetMetres,layer.Mask?1:0));
                properties.SetVector("_PlanetLayerPbr"+i,new Vector4(layer.Metallic,layer.AmbientOcclusion,layer.Smoothness,
                    layer.Normal?(layer.NormalEncoding==PlanetTerrainNormalEncoding.MetricSlopeMoments?2:1):0));
            }
            properties.SetVector("_PlanetNormalVarianceControls",new Vector4(settings.Grass.NormalVarianceScale,settings.Sand.NormalVarianceScale,
                settings.Rock.NormalVarianceScale,settings.Snow.NormalVarianceScale));
            properties.SetVector("_PlanetMaterialControls",new Vector4(settings.HeightBlendTransitionMetres,settings.TriplanarBlendSharpness,settings.FarNormalStrength,1));
            var variation=TexturePhase(planetAnchor,settings.VariationWavelengthMetres);var cell=VariationCell(planetAnchor,settings.VariationWavelengthMetres);
            properties.SetVector("_PlanetVariationPhase",new Vector4((float)variation.x,(float)variation.y,(float)variation.z,(float)(1/settings.VariationWavelengthMetres)));
            properties.SetVector("_PlanetVariationCell",new Vector4((float)cell.x,(float)cell.y,(float)cell.z,settings.VariationSeed&65535));
            properties.SetVector("_PlanetVariationControls",new Vector4(settings.AntiTilingStrength,settings.MacroVariationStrength,settings.VariationSeed>>16,0));
            properties.SetMatrix("_PlanetLocalToRender",Matrix4x4.Rotate(planetToRender));
            properties.SetMatrix("_PlanetRenderToLocal",Matrix4x4.Rotate(Quaternion.Inverse(planetToRender)));
        }
        static void Cleanup()
        {CoreUtils.Destroy(flatNormal);CoreUtils.Destroy(neutralMask);flatNormal=null;neutralMask=null;}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset()=>Cleanup();
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod] static void InstallCleanup()=>UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Cleanup;
#endif
    }
}
