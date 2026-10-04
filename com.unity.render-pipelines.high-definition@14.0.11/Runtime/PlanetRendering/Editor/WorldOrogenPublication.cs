using System;
using System.IO;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class WorldOrogenPublication
    {
        public const long MaximumAssetBytes=100L*1024*1024;
        public static PlanetSurfaceDataAsset Publish(PlanetGeneratorAsset generator,WorldOrogenSettings captured,WorldOrogenBaseMap map)
        {
            if(!generator||captured==null||map==null||map.Surface==null||!map.Colour)throw new ArgumentException("Complete GPU result, captured settings and target generator are required.");
            if(captured.ConfigurationDigest()!=map.ConfigurationDigest)throw new ArgumentException("Map parameters changed before publication.");
            string target=AssetDatabase.GetAssetPath(generator).Replace('\\','/');
            if(!target.StartsWith("Assets/",StringComparison.Ordinal))throw new InvalidOperationException("Save the Planet Generator in Assets before generating a persistent map.");
            if(generator.CaptureOrogen().ConfigurationDigest()!=captured.ConfigurationDigest()||generator.Radius!=map.Surface.Recipe.Radius)
                throw new InvalidOperationException("Authoring parameters changed during generation. The candidate was not bound; rebuild with the current parameters.");
            if(generator.MapView!=map.View||(generator.BaseMapResolution!=0&&generator.BaseMapResolution!=map.Resolution))
                throw new InvalidOperationException("Map view or resolution changed during generation. Rebuild with the current parameters.");
            string folder=Path.GetDirectoryName(target).Replace('\\','/'),stem=Path.GetFileNameWithoutExtension(target);
            string surfacePath=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+stem+"_WorldOrogen_Base.asset");
            string colourPath=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+stem+"_WorldOrogen_Map.asset");
            var previous=generator.SurfaceData;var previousSettings=generator.PublishedOrogen;
            var data=ScriptableObject.CreateInstance<PlanetSurfaceDataAsset>();bool colourCreated=false,surfaceCreated=false,bound=false;
            try
            {
                data.name=stem+" World Orogen base";data.SetSnapshot(map.Surface);
                if(data.EncodedPayloadBytes>=MaximumAssetBytes-1024*1024)throw new InvalidOperationException("Encoded height dataset exceeds the saved-file admission; lower Base map resolution.");
                AssetDatabase.CreateAsset(map.Colour,colourPath);colourCreated=true;
                EditorUtility.SetDirty(map.Colour);SaveVerified(map.Colour,colourPath);
                data.SetBaseColour(map.Colour);data.SetWorldOrogenSource(captured);
                AssetDatabase.CreateAsset(data,surfacePath);surfaceCreated=true;EditorUtility.SetDirty(data);SaveVerified(data,surfacePath);
                // Decode a fresh disk-loaded object before moving the generator's reference.
                Resources.UnloadAsset(data);data=AssetDatabase.LoadAssetAtPath<PlanetSurfaceDataAsset>(surfacePath);
                SurfaceSnapshot verified=null;string error=null;
                if(!data||!data.TryCreateSnapshot(out verified,out error)||verified.ContentDigest!=map.Surface.ContentDigest||!data.BaseColour)
                    throw new InvalidDataException("Saved World Orogen dataset did not round-trip: "+error);
                Undo.RecordObject(generator,"Publish World Orogen base map");generator.SurfaceData=data;generator.RecordPublishedOrogen(captured);bound=true;
                EditorUtility.SetDirty(generator);SaveVerified(generator,target);
                map.TakeColour();return data;
            }
            catch(Exception publicationError)
            {
                if(bound)
                {
                    try{generator.SurfaceData=previous;generator.RecordPublishedOrogen(previousSettings);EditorUtility.SetDirty(generator);SaveVerified(generator,target);}
                    catch(Exception restorationError)
                    {map.TakeColour();throw new AggregateException("Could not save the restored generator reference. Complete candidate retained at "+surfacePath,publicationError,restorationError);}
                }
                if(surfaceCreated)AssetDatabase.DeleteAsset(surfacePath);else if(data)UnityEngine.Object.DestroyImmediate(data);
                if(colourCreated){map.TakeColour();AssetDatabase.DeleteAsset(colourPath);}
                throw;
            }
        }
        static void SaveVerified(UnityEngine.Object asset,string path)
        {
            AssetDatabase.SaveAssetIfDirty(asset);
            string absolute=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath),path));
            if(EditorUtility.IsDirty(asset)||!File.Exists(absolute)||new FileInfo(absolute).Length<=0)throw new IOException("Could not save generated asset: "+path);
            if(new FileInfo(absolute).Length>=MaximumAssetBytes)throw new IOException("Generated asset exceeds the saved-file size admission: "+path);
        }
    }
}
