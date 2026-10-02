using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public sealed class PlanetTerrainExchangeManifest
    {
        public int SchemaVersion=1;
        public string PlanetInstanceId,BaseDigest,RecipeDigest,LayerId;
        public string SourceContentDigest,ExportedHeightFile="source-height.r32",ExportedHeightSha256;
        public string Projection="TangentGnomonic",SampleLayout="VertexGrid",RowDirection="PositiveForward";
        public string HeightEncoding="NormalizedFloat32",HeightFile="height.r32",HeightSha256;
        public int Width,Height,GuardSamples;
        public double Radius,Latitude,Longitude,Heading,MinimumX,MinimumZ,MaximumX,MaximumZ;
        public double HeightOffset,HeightScale;
        public string IncludedDetailBands="None";
        public bool SuppressRuntimeDetail;
        public string[] MaterialWeightFiles,ErosionFiles;
        public string MaterialChannelOrder="Grass,Sand,Rock,Snow";
        public string ErosionChannelOrder="Flow,Wetness,Wear,Deposition";

        public void Validate()
        {
            if(SchemaVersion!=1||Projection!="TangentGnomonic"||SampleLayout!="VertexGrid"||RowDirection!="PositiveForward")
                throw new InvalidDataException("Unsupported terrain exchange projection, version or sample convention.");
            if(HeightEncoding!="NormalizedFloat32")throw new InvalidDataException("Expected normalized float32 height data and explicit metre scale/offset.");
            if(Width<2||Height<2||Width>8193||Height>8193||GuardSamples<0||GuardSamples*2>=Math.Min(Width,Height)-1)
                throw new InvalidDataException("Invalid height resolution or guard samples.");
            if(!math.isfinite(Radius)||Radius<=0||!math.isfinite(HeightOffset)||!math.isfinite(HeightScale)||HeightScale<=0||
                !math.isfinite(Latitude)||math.abs(Latitude)>90||!math.isfinite(Longitude)||!math.isfinite(Heading)||
                !math.isfinite(MinimumX)||!math.isfinite(MinimumZ)||!math.isfinite(MaximumX)||!math.isfinite(MaximumZ)||
                MaximumX<=MinimumX||MaximumZ<=MinimumZ)
                throw new InvalidDataException("Invalid metric terrain exchange definition.");
            if(string.IsNullOrWhiteSpace(PlanetInstanceId)||string.IsNullOrWhiteSpace(LayerId)||!IsDigest(BaseDigest)||!IsDigest(RecipeDigest))
                throw new InvalidDataException("Exchange must identify the planet, layer, base and recipe.");
            if(string.IsNullOrWhiteSpace(HeightFile)||Path.GetFileName(HeightFile)!=HeightFile)
                throw new InvalidDataException("HeightFile must be a file name within the exchange package.");
            if(string.IsNullOrWhiteSpace(ExportedHeightFile)||Path.GetFileName(ExportedHeightFile)!=ExportedHeightFile||ExportedHeightFile==HeightFile)
                throw new InvalidDataException("The original export must have a separate file name within the exchange package.");
            ValidateMapFiles(MaterialWeightFiles);ValidateMapFiles(ErosionFiles);
            if(MaterialChannelOrder!="Grass,Sand,Rock,Snow"||ErosionChannelOrder!="Flow,Wetness,Wear,Deposition")
                throw new InvalidDataException("Unsupported material or erosion channel order.");
        }
        static void ValidateMapFiles(string[] files)
        {
            if(files==null||files.Length==0)return;
            if(files.Length!=4)throw new InvalidDataException("An attribute set requires four ordered maps.");
            foreach(var file in files)if(string.IsNullOrWhiteSpace(file)||Path.GetFileName(file)!=file)
                throw new InvalidDataException("Attribute maps must be file names within the exchange package.");
        }
        public void ValidateAgainst(string planetInstanceId,string baseDigest,double radius,bool additive)
        {
            Validate();
            if(PlanetInstanceId!=planetInstanceId||Radius!=radius)throw new InvalidDataException("The exchange belongs to a different planet or metric projection.");
            if(additive&&!string.Equals(BaseDigest,baseDigest,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A delta can only be imported against its exact exported base digest. Export again or rebase the authored layer explicitly.");
        }
        static bool IsDigest(string value)
        {
            if(value==null||value.Length!=64)return false;
            foreach(char c in value)if(!Uri.IsHexDigit(c))return false;
            return true;
        }
        public double3 Direction(int x,int y)
        {
            if(x<0||y<0||x>=Width||y>=Height)throw new ArgumentOutOfRangeException();
            var up=PlanetSurfaceCoordinates.Direction(Latitude,Longitude);
            double longitude=math.radians(Longitude%360);
            var east=new double3(-math.sin(longitude),0,math.cos(longitude));
            var north=math.normalize(math.cross(east,up));
            var right=math.normalize(math.cross(up,north));
            double angle=math.radians(Heading%360),s=math.sin(angle),c=math.cos(angle);
            var rotatedRight=right*c-north*s;var forward=north*c+right*s;
            double px=math.lerp(MinimumX,MaximumX,(double)x/(Width-1));
            double pz=math.lerp(MinimumZ,MaximumZ,(double)y/(Height-1));
            return math.normalize(up*Radius+rotatedRight*px+forward*pz);
        }
    }

    /// <summary>Data exchange only: values remain linear, with an explicit inverse mapping to metres.</summary>
    public static class PlanetTerrainExchange
    {
        public static byte[] EncodeRaw(float[] heightsMetres,PlanetTerrainExchangeManifest manifest)
        {
            manifest.Validate();ValidateSamples(heightsMetres,manifest);
            using(var stream=new MemoryStream(checked(heightsMetres.Length*4)))
            using(var writer=new BinaryWriter(stream))
            {
                foreach(float height in heightsMetres)writer.Write((float)(((double)height-manifest.HeightOffset)/manifest.HeightScale));
                return stream.ToArray();
            }
        }
        public static float[] DecodeRaw(byte[] data,PlanetTerrainExchangeManifest manifest)
        {
            manifest.Validate();
            int count=checked(manifest.Width*manifest.Height);
            if(data==null||data.Length!=checked(count*4))throw new InvalidDataException("Height data byte count differs from the manifest.");
            var result=new float[count];
            using(var reader=new BinaryReader(new MemoryStream(data,false)))
                for(int i=0;i<count;i++)result[i]=DecodeHeight(reader.ReadSingle(),manifest);
            return result;
        }
        public static void Export(string directory,PlanetTerrainExchangeManifest manifest,float[] heightsMetres)
        {
            var raw=EncodeRaw(heightsMetres,manifest);
            Directory.CreateDirectory(directory);
            string rawName=Path.ChangeExtension(manifest.HeightFile,".r32");
            manifest.HeightFile=rawName;manifest.HeightSha256=Digest(raw);
            File.WriteAllBytes(Path.Combine(directory,rawName),raw);
            manifest.ExportedHeightSha256=manifest.HeightSha256;
            File.WriteAllBytes(Path.Combine(directory,manifest.ExportedHeightFile),raw);
            // Both formats carry the same normalized samples. RAW is useful for an exact diagnostic round-trip.
            var texture=new Texture2D(manifest.Width,manifest.Height,TextureFormat.RGBAFloat,false,true);
            try
            {
                var values=new Color[heightsMetres.Length];
                for(int i=0;i<values.Length;i++)
                {
                    float h=(float)(((double)heightsMetres[i]-manifest.HeightOffset)/manifest.HeightScale);
                    values[i]=new Color(h,h,h,1);
                }
                texture.SetPixels(values);texture.Apply(false,false);
                File.WriteAllBytes(Path.Combine(directory,Path.ChangeExtension(rawName,".exr")),
                    texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat|Texture2D.EXRFlags.CompressZIP));
            }
            finally{Object.DestroyImmediate(texture);}
            File.WriteAllText(Path.Combine(directory,"manifest.json"),JsonUtility.ToJson(manifest,true),new UTF8Encoding(false));
        }
        public static PlanetTerrainExchangeManifest ReadManifest(string path)
        {
            var manifest=JsonUtility.FromJson<PlanetTerrainExchangeManifest>(File.ReadAllText(path));
            if(manifest==null)throw new InvalidDataException("Missing terrain exchange manifest.");
            manifest.Validate();return manifest;
        }
        public static float[] ReadHeight(string path,PlanetTerrainExchangeManifest manifest)
        {
            manifest.Validate();
            if(string.Equals(Path.GetExtension(path),".r32",StringComparison.OrdinalIgnoreCase))return DecodeRaw(File.ReadAllBytes(path),manifest);
            if(!string.Equals(Path.GetExtension(path),".exr",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Import a float32 EXR or float32 R32 heightfield.");
            // Import into a temporary owned Editor asset. LoadImage only decodes PNG/JPEG and must not handle terrain EXR.
            string folder="Assets/__PlanetTerrainExchange_"+Guid.NewGuid().ToString("N");
            string assetPath=folder+"/height.exr";
            string absoluteFolder=Path.Combine(Application.dataPath,Path.GetFileName(folder));
            try
            {
                Directory.CreateDirectory(absoluteFolder);File.Copy(path,Path.Combine(absoluteFolder,"height.exr"));
                AssetDatabase.ImportAsset(assetPath,ImportAssetOptions.ForceSynchronousImport);
                var importer=(TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.sRGBTexture=false;importer.mipmapEnabled=false;importer.isReadable=true;
                importer.npotScale=TextureImporterNPOTScale.None;importer.textureCompression=TextureImporterCompression.Uncompressed;
                var platform=importer.GetDefaultPlatformTextureSettings();platform.format=TextureImporterFormat.RGBAFloat;
                platform.maxTextureSize=16384;importer.SetPlatformTextureSettings(platform);importer.SaveAndReimport();
                var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if(!texture||texture.width!=manifest.Width||texture.height!=manifest.Height)
                    throw new InvalidDataException("Imported EXR resolution differs from the manifest.");
                var pixels=texture.GetPixels();var values=new float[pixels.Length];
                for(int i=0;i<values.Length;i++)values[i]=DecodeHeight(pixels[i].r,manifest);
                return values;
            }
            finally{AssetDatabase.DeleteAsset(folder);if(Directory.Exists(absoluteFolder))Directory.Delete(absoluteFolder,true);}
        }
        public static string Digest(byte[] bytes)
        {
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
        static float DecodeHeight(float normalized,PlanetTerrainExchangeManifest manifest)
        {
            double metres=manifest.HeightOffset+(double)normalized*manifest.HeightScale;
            if(!math.isfinite(normalized)||!math.isfinite(metres)||math.abs(metres)>float.MaxValue)
                throw new InvalidDataException("Heightfield contains a non-finite or overflowing sample.");
            return (float)metres;
        }
        static void ValidateSamples(float[] heights,PlanetTerrainExchangeManifest manifest)
        {
            if(heights==null||heights.Length!=checked(manifest.Width*manifest.Height))throw new InvalidDataException("Height count differs from the manifest.");
            foreach(float h in heights)
                if(!math.isfinite(h)||math.abs(((double)h-manifest.HeightOffset)/manifest.HeightScale)>float.MaxValue)
                    throw new InvalidDataException("Invalid height sample.");
        }
    }
}
