using System;
using System.IO;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetTerrainAuthoring
    {
        const long MaximumPublishedFileBytes=100L*1024*1024;
        const long SerializationHeadroomBytes=1024L*1024;
        public static SurfaceSnapshot ComposeBake(PlanetTerrainRecipeAsset recipe,SurfaceSnapshot baked)
        {
            if(!recipe || baked==null)throw new ArgumentNullException();
            if(!new SurfaceDetailRecipe(recipe.DetailWavelengthMetres,recipe.DetailAmplitudeMetres,recipe.Seed^137).IsValid)
                throw new ArgumentException("Metric detail requires a positive wavelength and a non-negative amplitude.");
            var withDetail=new SurfaceSnapshot(baked.Recipe,baked.Revision,baked.CanonicalTileLevel,baked.Resolution,
                baked.Tiles,new SurfaceDetailRecipe(recipe.DetailWavelengthMetres,recipe.DetailAmplitudeMetres,recipe.Seed^137),baked.Regions,baked.Stamps,baked.AutomaticMaterialProfile,structuralField:baked.StructuralField);
            SurfaceSnapshot previous=null;
            if(recipe.Published && !recipe.Published.TryCreateSnapshot(out previous,out var reason))
                throw new InvalidOperationException("Existing authored surface is unreadable: "+reason);
            var composed=PlanetTerrainExchangeService.PreserveAuthoredLayers(withDetail,previous);
            if(!composed.HasAutomaticMaterials)return composed;
            long retained=checked(PlanetTerrainBakeCache.EstimateSnapshotBytes(baked)+PlanetTerrainBakeCache.EstimateSnapshotBytes(composed)+(previous==null?0:PlanetTerrainBakeCache.EstimateSnapshotBytes(previous)));
            var allowance=PlanetTerrainGeneration.RepairAllowance(recipe.Bake.MaximumWorkingBytes,retained,composed.Tiles,composed.Regions,composed.AutomaticMaterialProfile);
            return SurfaceMaterialRepair.Rebuild(composed,allowance);
        }
        public static void PublishBake(PlanetTerrainRecipeAsset recipe,PlanetGeneratorAsset generator,SurfaceBakeResult result,string newAssetPath=null)
        {
            var snapshot=ComposeBake(recipe,result.Snapshot);var levels=new List<SurfaceSnapshot>();
            long retained=checked(PlanetTerrainBakeCache.EstimateBakeBytes(result)+PlanetTerrainBakeCache.EstimateSnapshotBytes(snapshot));
            foreach(var level in result.LodPyramid)
            {
                if(level.Resolution==snapshot.Resolution)continue;
                var allowance=snapshot.HasAutomaticMaterials?PlanetTerrainGeneration.RepairAllowance(recipe.Bake.MaximumWorkingBytes,retained,level.Tiles,snapshot.Regions,snapshot.AutomaticMaterialProfile):null;
                var coarse=new SurfaceSnapshot(snapshot.Recipe,snapshot.Revision,snapshot.CanonicalTileLevel,level.Resolution,
                    level.Tiles,snapshot.Detail,snapshot.Regions,snapshot.Stamps,snapshot.AutomaticMaterialProfile,structuralField:snapshot.StructuralField?.ForBakedResolution(level.Resolution));
                var ready=coarse.HasAutomaticMaterials?SurfaceMaterialRepair.Rebuild(coarse,allowance):coarse;
                levels.Add(ready);retained=checked(retained+PlanetTerrainBakeCache.EstimateSnapshotBytes(ready));
            }
            Publish(recipe,generator,snapshot,newAssetPath,levels,result.Hydrology);
        }
        public static void PublishGeneration(PlanetTerrainRecipeAsset recipe,PlanetGeneratorAsset generator,PlanetTerrainGenerationResult result,string newAssetPath=null)
        {
            if(result==null)throw new ArgumentNullException(nameof(result));
            Publish(recipe,generator,result.Snapshot,newAssetPath,result.MaterialLodPyramid,result.GlobalBake.Hydrology);
        }
        public static void Publish(PlanetTerrainRecipeAsset recipe,PlanetGeneratorAsset generator,SurfaceSnapshot snapshot,string newAssetPath=null,IReadOnlyList<SurfaceSnapshot> coarseLevels=null,SurfaceHydrologyField hydrology=null)
        {
            if(!recipe || snapshot==null)throw new ArgumentNullException();
            if(!snapshot.MaterialsReady)throw new InvalidOperationException("Prepare automatic materials on the captured final surface before publishing. The old dataset is retained.");
            if(!AssetDatabase.Contains(recipe))throw new ArgumentException("Save Terrain Recipe as an asset before publishing a generation.");
            var descriptor=PlanetSurfaceDescriptor.FromSnapshot(snapshot);
            var definition=new PlanetDefinition{Id=1,Seed=snapshot.Recipe.Seed,GeneratorVersion=3,Radius=snapshot.Recipe.Radius,
                Relief=Unity.Mathematics.math.max(Unity.Mathematics.math.abs(snapshot.MinimumHeight),Unity.Mathematics.math.abs(snapshot.MaximumHeight)),Surface=descriptor};
            if(!definition.IsValid)throw new ArgumentException("The published surface exceeds the planet adapter's supported height/radius range.");
            if(hydrology==null && recipe.Hydrology)
            {
                if(!recipe.Hydrology.TryCreateField(out var retained,out var reason))throw new InvalidOperationException("Existing coarse hydrology is unreadable: "+reason);
                if(retained.SourceBaseDigest==snapshot.Revision.BaseDigest)hydrology=retained;
            }
            if(hydrology!=null && (hydrology.SourceBaseDigest!=snapshot.Revision.BaseDigest || hydrology.Radius!=snapshot.Recipe.Radius))
                throw new ArgumentException("Coarse hydrology must belong to the published base and radius.");
            if(coarseLevels==null && recipe.LodPyramid!=null)
            {
                var retainedLevels=new List<SurfaceSnapshot>();
                long retained=PlanetTerrainBakeCache.EstimateSnapshotBytes(snapshot);
                foreach(var existing in recipe.LodPyramid)
                {
                    if(!existing)continue;
                    if(!existing.TryCreateSnapshot(out var level,out var reason))throw new InvalidOperationException("Existing LOD is unreadable: "+reason);
                    if(level.Revision.BaseDigest!=snapshot.Revision.BaseDigest || level.Recipe.Radius!=snapshot.Recipe.Radius || level.Resolution==snapshot.Resolution)continue;
                    retained=checked(retained+PlanetTerrainBakeCache.EstimateSnapshotBytes(level));
                    var allowance=snapshot.HasAutomaticMaterials?PlanetTerrainGeneration.RepairAllowance(recipe.Bake.MaximumWorkingBytes,retained,level.Tiles,snapshot.Regions,snapshot.AutomaticMaterialProfile):null;
                    var coarse=new SurfaceSnapshot(snapshot.Recipe,snapshot.Revision,level.CanonicalTileLevel,level.Resolution,
                        level.Tiles,snapshot.Detail,snapshot.Regions,snapshot.Stamps,snapshot.AutomaticMaterialProfile,structuralField:snapshot.StructuralField?.ForBakedResolution(level.Resolution));
                    var ready=coarse.HasAutomaticMaterials?SurfaceMaterialRepair.Rebuild(coarse,allowance):coarse;
                    retainedLevels.Add(ready);retained=checked(retained+PlanetTerrainBakeCache.EstimateSnapshotBytes(ready));
                }
                coarseLevels=retainedLevels;
            }
            var preparedResident=new PlanetTerrainBakeCache.ResidentEstimate();preparedResident.AddSnapshot(snapshot);
            if(coarseLevels!=null)foreach(var level in coarseLevels)
            {
                if(level==null || !level.MaterialsReady)throw new InvalidOperationException("Every material LOD must be complete before publication.");
                preparedResident.AddSnapshot(level);
            }
            if(preparedResident.Bytes>recipe.Bake.MaximumWorkingBytes)throw new InvalidOperationException("Prepared terrain and all material LOD data exceed MaximumWorkingBytes; the old generation is retained.");
            if(string.IsNullOrWhiteSpace(newAssetPath) && recipe.Published)
            {
                string previousPath=AssetDatabase.GetAssetPath(recipe.Published);
                if(!string.IsNullOrEmpty(previousPath))newAssetPath=Path.Combine(Path.GetDirectoryName(previousPath),recipe.name+"_Surface.asset").Replace('\\','/');
            }
            if(string.IsNullOrWhiteSpace(newAssetPath)||!newAssetPath.StartsWith("Assets/",StringComparison.Ordinal)||Path.GetExtension(newAssetPath)!=".asset")
                throw new ArgumentException("Choose an asset path below Assets for the published terrain.");
            string absolute=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath),newAssetPath));
            if(!absolute.StartsWith(Path.GetFullPath(Application.dataPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Published terrain must stay below Assets.");
            var data=ScriptableObject.CreateInstance<PlanetSurfaceDataAsset>();var prepared=new List<PlanetSurfaceDataAsset>{data};
            PlanetHydrologyDataAsset preparedHydrology=null;
            var previousData=recipe.Published;var previousLevels=recipe.LodPyramid;var previousHydrology=recipe.Hydrology;
            var previousGeneratorData=generator?generator.SurfaceData:null;
            string createdPath=null,createdFolder=null;var createdPaths=new List<string>();bool bound=false,assignmentStarted=false;
            try
            {
                // Build a new generation completely; the old published asset and its readers retain their data.
                data.SetSnapshot(snapshot);
                if(coarseLevels!=null)foreach(var level in coarseLevels)
                {
                    var coarse=ScriptableObject.CreateInstance<PlanetSurfaceDataAsset>();prepared.Add(coarse);
                    coarse.name="LOD "+level.Resolution;coarse.SetSnapshot(level);
                }
                if(hydrology!=null)
                {
                    preparedHydrology=ScriptableObject.CreateInstance<PlanetHydrologyDataAsset>();
                    preparedHydrology.name="Coarse hydrology";preparedHydrology.SetField(hydrology);
                }
                long mainPayload=checked(data.EncodedPayloadBytes+(preparedHydrology?preparedHydrology.EncodedPayloadBytes:0));
                long combinedPayload=mainPayload;
                foreach(var item in prepared)
                {
                    if(item!=data)combinedPayload=checked(combinedPayload+item.EncodedPayloadBytes);
                    if(item.EncodedPayloadBytes>MaximumPublishedFileBytes-SerializationHeadroomBytes)
                        throw new InvalidOperationException("One terrain LOD exceeds the supported per-file payload size; splitting levels cannot reduce that LOD. The old generation is retained.");
                }
                if(mainPayload>MaximumPublishedFileBytes-SerializationHeadroomBytes)
                    throw new InvalidOperationException("The finest terrain and coarse hydrology exceed the supported per-file payload size. The old generation is retained.");
                bool separateLevels=combinedPayload>MaximumPublishedFileBytes-SerializationHeadroomBytes;
                createdPath=AssetDatabase.GenerateUniqueAssetPath(newAssetPath);createdPaths.Add(createdPath);AssetDatabase.CreateAsset(data,createdPath);
                if(separateLevels)
                {
                    string parent=Path.GetDirectoryName(createdPath).Replace('\\','/');
                    createdFolder=AssetDatabase.GenerateUniqueAssetPath(parent+"/"+Path.GetFileNameWithoutExtension(createdPath)+"_LOD");
                    if(string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent,Path.GetFileName(createdFolder))))throw new IOException("Could not create the new terrain LOD folder.");
                }
                for(int i=1;i<prepared.Count;i++)
                {
                    if(!separateLevels){AssetDatabase.AddObjectToAsset(prepared[i],data);continue;}
                    string path=createdFolder+"/"+prepared[i].name.Replace(' ','_')+".asset";
                    createdPaths.Add(path);AssetDatabase.CreateAsset(prepared[i],path);
                }
                if(preparedHydrology)AssetDatabase.AddObjectToAsset(preparedHydrology,data);
                foreach(var item in prepared){EditorUtility.SetDirty(item);SaveVerified(item);}
                foreach(string path in createdPaths)
                    if(!File.Exists(path)||new FileInfo(path).Length>MaximumPublishedFileBytes)
                        throw new IOException("Saved terrain file is missing or exceeds its per-file limit: "+path);
                assignmentStarted=true;
                Undo.RecordObject(recipe,"Publish planet terrain");recipe.Published=data;
                recipe.LodPyramid=prepared.ToArray();
                recipe.Hydrology=preparedHydrology;
                EditorUtility.SetDirty(recipe);EditorUtility.SetDirty(data);
                if(generator)
                {
                    Undo.RecordObject(generator,"Bind published planet terrain");generator.SurfaceData=data;
                    EditorUtility.SetDirty(generator);
                }
                SaveVerified(recipe);
                if(generator&&AssetDatabase.Contains(generator))SaveVerified(generator);
                bound=true;
                SceneView.RepaintAll();
            }
            catch(Exception publicationFailure)
            {
                Exception restorationFailure=null;bool safeToDelete=!assignmentStarted;
                if(assignmentStarted&&!bound)
                {
                    recipe.Published=previousData;recipe.LodPyramid=previousLevels;recipe.Hydrology=previousHydrology;
                    if(generator)generator.SurfaceData=previousGeneratorData;
                    try
                    {
                        EditorUtility.SetDirty(recipe);SaveVerified(recipe);
                        if(generator){EditorUtility.SetDirty(generator);if(AssetDatabase.Contains(generator))SaveVerified(generator);}
                        safeToDelete=true;
                    }
                    catch(Exception exception){restorationFailure=exception;}
                }
                // Keep a complete candidate if disk failure prevented verification of reference rollback.
                if(!bound&&safeToDelete)
                {
                    foreach(string path in createdPaths)AssetDatabase.DeleteAsset(path);
                    if(createdFolder!=null&&AssetDatabase.IsValidFolder(createdFolder))AssetDatabase.DeleteAsset(createdFolder);
                }
                foreach(var item in prepared)if(item&&!AssetDatabase.Contains(item))UnityEngine.Object.DestroyImmediate(item);
                if(preparedHydrology && !AssetDatabase.Contains(preparedHydrology))UnityEngine.Object.DestroyImmediate(preparedHydrology);
                if(restorationFailure!=null)throw new AggregateException("Publication failed and reference rollback could not be saved; the complete candidate was retained at "+createdPath,publicationFailure,restorationFailure);
                throw;
            }
        }
        static void SaveVerified(UnityEngine.Object asset)
        {
            AssetDatabase.SaveAssetIfDirty(asset);
            if(EditorUtility.IsDirty(asset))throw new IOException("The asset could not be saved: "+AssetDatabase.GetAssetPath(asset));
        }
    }
}
