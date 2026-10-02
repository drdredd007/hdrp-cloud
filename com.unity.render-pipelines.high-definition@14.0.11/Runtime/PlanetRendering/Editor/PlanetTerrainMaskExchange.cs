using System;
using System.IO;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetTerrainMaskExchange
    {
        public static string[] Export(string directory,PlanetTerrainExchangeManifest manifest,float4[] samples,bool materials)
        {
            if(samples==null||samples.Length!=checked(manifest.Width*manifest.Height))throw new InvalidDataException("Mask grid differs from height grid.");
            string[] names=materials?new[]{"grass","sand","rock","snow"}:new[]{"flow","wetness","wear","deposition"};
            var result=new string[4];var map=new float[samples.Length];var linear=LinearManifest(manifest);
            for(int channel=0;channel<4;channel++)
            {
                for(int i=0;i<map.Length;i++)
                {
                    float value=samples[i][channel];
                    if(!math.isfinite(value)||value<0||value>1)throw new InvalidDataException("Mask samples must be finite linear [0,1].");
                    map[i]=value;
                }
                result[channel]=names[channel]+".exr";
                File.WriteAllBytes(Path.Combine(directory,names[channel]+".r32"),PlanetTerrainExchange.EncodeRaw(map,linear));
                var texture=new Texture2D(manifest.Width,manifest.Height,TextureFormat.RGBAFloat,false,true);
                try
                {
                    var colors=new Color[map.Length];for(int i=0;i<map.Length;i++)colors[i]=new Color(map[i],map[i],map[i],1);
                    texture.SetPixels(colors);texture.Apply(false,false);
                    File.WriteAllBytes(Path.Combine(directory,result[channel]),texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat|Texture2D.EXRFlags.CompressZIP));
                }
                finally{UnityEngine.Object.DestroyImmediate(texture);}
            }
            return result;
        }
        public static float4[] ImportMaterials(string directory,PlanetTerrainExchangeManifest manifest)
            =>ImportMaps(directory,manifest,true);
        public static float4[] ImportErosion(string directory,PlanetTerrainExchangeManifest manifest)
            =>ImportMaps(directory,manifest,false);
        static float4[] ImportMaps(string directory,PlanetTerrainExchangeManifest manifest,bool materials)
        {
            manifest.Validate();
            var files=materials?manifest.MaterialWeightFiles:manifest.ErosionFiles;
            if(files==null||files.Length!=4)throw new InvalidDataException("The manifest has no requested attribute maps.");
            var result=new float4[checked(manifest.Width*manifest.Height)];var linear=LinearManifest(manifest);
            for(int channel=0;channel<4;channel++)
            {
                var values=PlanetTerrainExchange.ReadHeight(Path.Combine(directory,files[channel]),linear);
                for(int i=0;i<values.Length;i++)
                {
                    float value=values[i];if(!math.isfinite(value)||value<0||value>1)throw new InvalidDataException("Attribute maps must be finite linear [0,1].");
                    var vector=result[i];vector[channel]=value;result[i]=vector;
                }
            }
            if(materials)for(int i=0;i<result.Length;i++)
            {
                float sum=math.csum(result[i]);if(sum<=1e-6f)throw new InvalidDataException("Material weights have no active layer at a vertex.");
                result[i]/=sum;
            }
            return result;
        }
        static PlanetTerrainExchangeManifest LinearManifest(PlanetTerrainExchangeManifest source)
        {
            var result=JsonUtility.FromJson<PlanetTerrainExchangeManifest>(JsonUtility.ToJson(source));result.HeightOffset=0;result.HeightScale=1;return result;
        }
    }
}
