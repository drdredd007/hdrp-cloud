using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class PlanetTerrainAuthoringWindow : EditorWindow
    {
        [SerializeField] PlanetTerrainRecipeAsset recipe;
        [SerializeField] PlanetGeneratorAsset generator;
        [SerializeField] string planetInstanceId="";
        [SerializeField] double latitude,longitude,heading,widthMetres=4096;
        [SerializeField] int cells=256,guardSamples=16,layerPriority=10;
        [SerializeField] SurfaceRegionMode importMode=SurfaceRegionMode.Replace;
        [SerializeField] bool replacePriority;
        [SerializeField] bool importMaterialMasks;
        [SerializeField] bool importErosionMasks;
        [SerializeField] bool migrateLegacyMaterialRules;
        [SerializeField] PlanetTerrainMapMode previewMode;
        [SerializeField] int previewSamples=64;
        [SerializeField] double previewObserverHeight=100;
        [SerializeField] int previewMaximumMiB=128,previewMaximumChecksMillions=16;
        [SerializeField] bool autoPreview;
        readonly PlanetTerrainPreviewPublication previewPublication=new PlanetTerrainPreviewPublication();
        Task<PlanetTerrainPreviewResult> pendingPreview;
        CancellationTokenSource previewCancellation;
        PlanetSurfaceDataAsset previewSource;
        PlanetTerrainRecipeAsset previewRecipe;
        PlanetGeneratorAsset previewGenerator;
        PlanetTerrainPreviewInput previewInput;
        Texture2D previewTexture;
        long previewToken;
        string previewMessage;
        double nextPreviewPoll,previewChangedAt;
        SurfaceContentHash observedPreviewSource,observedPreviewConfiguration;
        int observedPreviewAsset,observedPreviewRecipe,observedPreviewGenerator;
        public PlanetTerrainPreviewResult CurrentPreview=>previewPublication.Current;
        UnityEditor.Editor inspector;
        Vector2 scroll;
        Task<PlanetTerrainGenerationResult> pending;
        sealed class MaterialImportResult { public SurfaceSnapshot Snapshot; public List<SurfaceSnapshot> Levels; }
        Task<MaterialImportResult> pendingImport;
        CancellationTokenSource cancellation;
        readonly object progressLock=new object();
        SurfaceBakeProgress progress;
        string failure,information,recipeSignature,newAssetPath;
        SurfaceContentHash previousContent;

        [MenuItem("Window/Rendering/Planet Terrain Authoring")]
        public static void Open()=>GetWindow<PlanetTerrainAuthoringWindow>("Planet Terrain");
        public static void Open(PlanetGeneratorAsset target,string instanceId=null)
        {
            var window=GetWindow<PlanetTerrainAuthoringWindow>("Planet Terrain");window.generator=target;
            if(!string.IsNullOrEmpty(instanceId))window.planetInstanceId=instanceId;
            window.Show();
        }
        void OnEnable(){EditorApplication.update+=Tick;AssemblyReloadEvents.beforeAssemblyReload+=Cancel;}
        void OnDisable(){EditorApplication.update-=Tick;AssemblyReloadEvents.beforeAssemblyReload-=Cancel;Cancel();DetachPreviewTask();if(previewTexture)DestroyImmediate(previewTexture);if(inspector)DestroyImmediate(inspector);}
        void Cancel(){cancellation?.Cancel();CancelPreview();}
        void OnGUI()
        {
            scroll=EditorGUILayout.BeginScrollView(scroll);
            bool busy=pending!=null||pendingImport!=null;
            using(new EditorGUI.DisabledScope(busy||EditorApplication.isPlayingOrWillChangePlaymode))
            {
                generator=(PlanetGeneratorAsset)EditorGUILayout.ObjectField("Planet Generator",generator,typeof(PlanetGeneratorAsset),false);
                recipe=(PlanetTerrainRecipeAsset)EditorGUILayout.ObjectField("Terrain Recipe",recipe,typeof(PlanetTerrainRecipeAsset),false);
                if(GUILayout.Button("Create Terrain Recipe"))CreateRecipe();
                if(recipe)
                {
                    UnityEditor.Editor.CreateCachedEditor(recipe,null,ref inspector);inspector.OnInspectorGUI();
                    EditorGUILayout.LabelField("Climate preset (settings only)",EditorStyles.boldLabel);
                    using(new EditorGUILayout.HorizontalScope())
                    {
                        if(GUILayout.Button("Desert"))ApplyClimatePreset(PlanetTerrainClimatePreset.Desert);
                        if(GUILayout.Button("Temperate"))ApplyClimatePreset(PlanetTerrainClimatePreset.Temperate);
                        if(GUILayout.Button("Polar"))ApplyClimatePreset(PlanetTerrainClimatePreset.Polar);
                    }
                    using(new EditorGUILayout.HorizontalScope())
                    {
                        if(GUILayout.Button("Preview preset")){Undo.RecordObject(recipe,"Terrain preview preset");recipe.Bake=SurfaceBakeSettings.Preview;EditorUtility.SetDirty(recipe);}
                        if(GUILayout.Button("Final preset")){Undo.RecordObject(recipe,"Terrain final preset");recipe.Bake=SurfaceBakeSettings.Final;EditorUtility.SetDirty(recipe);}
                        if(GUILayout.Button("Bake and Publish"))StartBake();
                    }
                }
            }
            using(new EditorGUI.DisabledScope(busy||EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.LabelField("Scatter density / residency preset",EditorStyles.boldLabel);
                using(new EditorGUILayout.HorizontalScope())
                {
                    if(GUILayout.Button("Sparse"))ApplyScatterPreset(PlanetTerrainScatterPreset.Sparse);
                    if(GUILayout.Button("Balanced"))ApplyScatterPreset(PlanetTerrainScatterPreset.Balanced);
                    if(GUILayout.Button("Dense"))ApplyScatterPreset(PlanetTerrainScatterPreset.Dense);
                }
                EditorGUILayout.LabelField("Explicitly edits assigned Species and Settings assets. Density uses 12.5 / 25 / 50% of proposals at the current Generator radius; fixed grid and stable identities stay unchanged. Larger existing residency limits are retained; new-cell batches become 2 / 4 / 8. Reapply after radius changes. Bake/publication remain separate; residency estimates do not guarantee frame rate.",EditorStyles.wordWrappedLabel);
            }
            if(busy)
            {
                SurfaceBakeProgress current;lock(progressLock)current=progress;
                var rect=GUILayoutUtility.GetRect(20,22);EditorGUI.ProgressBar(rect,(float)current.Fraction,current.Stage??"Preparing");
                if(GUILayout.Button("Cancel bake"))Cancel();
            }
            DrawPreview();
            if(recipe && recipe.Published)
            {
                EditorGUILayout.LabelField($"Last bake: {recipe.LastBakeSeconds:F2}s · {recipe.LastNodeCount:N0} nodes · {recipe.LastWorkingBytes/(1024.0*1024):F1} MiB estimate");
                EditorGUILayout.Space();EditorGUILayout.LabelField("Regional authoring / Gaea",EditorStyles.boldLabel);
                using(new EditorGUI.DisabledScope(busy||EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    planetInstanceId=EditorGUILayout.TextField("Planet Instance ID",planetInstanceId);
                    cells=EditorGUILayout.IntField("Grid cells",cells);guardSamples=EditorGUILayout.IntField("Context guard samples",guardSamples);
                    layerPriority=EditorGUILayout.IntField("Layer priority",layerPriority);
                    if(GUILayout.Button("Add generated region at this address"))AddRegion();
                    if(GUILayout.Button("Export height + masks + manifest"))Export();
                    importMode=(SurfaceRegionMode)EditorGUILayout.EnumPopup("Import mode",importMode);
                    replacePriority=EditorGUILayout.Toggle("Replace occupied priority",replacePriority);
                    importMaterialMasks=EditorGUILayout.Toggle("Import four material maps",importMaterialMasks);
                    importErosionMasks=EditorGUILayout.Toggle("Import four erosion maps",importErosionMasks);
                    migrateLegacyMaterialRules=EditorGUILayout.Toggle("Migrate legacy masks from this recipe",migrateLegacyMaterialRules);
                    if(GUILayout.Button("Import authored height"))Import();
                }
            }
            if(!string.IsNullOrEmpty(failure))EditorGUILayout.HelpBox(failure,MessageType.Warning);
            if(!string.IsNullOrEmpty(information))EditorGUILayout.HelpBox(information,MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
        void ApplyClimatePreset(PlanetTerrainClimatePreset preset)
        {
            try{PlanetTerrainPresets.ApplyClimate(recipe,preset);failure=null;information="Climate settings applied with Undo. Existing published heights/materials remain unchanged until Bake and Publish.";}
            catch(Exception exception){failure=exception.Message;}
        }
        void ApplyScatterPreset(PlanetTerrainScatterPreset preset)
        {
            try{var plan=PlanetTerrainPresets.ApplyScatter(generator,preset);failure=null;information=$"Scatter {preset} settings applied with Undo at radius {plan.ReferenceRadius:N1} m. IDs, seed, meshes, colliders and fixed cells retained; refresh the numerical preview to check interest budgets.";}
            catch(Exception exception){failure=exception.Message;}
        }
        void DrawPreview()
        {
            EditorGUILayout.Space();EditorGUILayout.LabelField("Canonical terrain map / scatter estimates",EditorStyles.boldLabel);
            var displayedSource=recipe&&recipe.Published?recipe.Published:generator?generator.SurfaceData:null;
            EditorGUILayout.LabelField(displayedSource?$"Source: {displayedSource.name} ({(recipe&&recipe.Published?"Terrain Recipe published dataset":"Generator Surface Data")})":"Source: no published signed dataset",EditorStyles.wordWrappedLabel);
            using(new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                latitude=EditorGUILayout.DoubleField("Latitude (degrees)",latitude);longitude=EditorGUILayout.DoubleField("Longitude (degrees)",longitude);
                heading=EditorGUILayout.DoubleField("Heading (degrees)",heading);widthMetres=EditorGUILayout.DoubleField("Region width (metres)",widthMetres);
                previewMode=(PlanetTerrainMapMode)EditorGUILayout.EnumPopup("Map channel",previewMode);
                previewSamples=EditorGUILayout.IntPopup("Map samples",previewSamples,new[]{"64 × 64","128 × 128"},new[]{64,128});
                previewObserverHeight=EditorGUILayout.DoubleField("Interest height above ground (m)",previewObserverHeight);
                previewMaximumMiB=EditorGUILayout.IntField("Preview memory limit (MiB)",previewMaximumMiB);
                previewMaximumChecksMillions=EditorGUILayout.IntField("Composition limit (million)",previewMaximumChecksMillions);
                autoPreview=EditorGUILayout.Toggle("Refresh after settings/species edits",autoPreview);
                using(new EditorGUILayout.HorizontalScope())
                {
                    using(new EditorGUI.DisabledScope(pendingPreview!=null))if(GUILayout.Button("Build map + cost estimate"))RequestPreview();
                    using(new EditorGUI.DisabledScope(pendingPreview==null))if(GUILayout.Button("Cancel preview")){CancelPreview();previewMessage="Preview cancelled; the last complete map is retained.";}
                }
            }
            if(pendingPreview!=null)EditorGUILayout.LabelField("Sampling immutable surface on background worker…");
            if(!string.IsNullOrEmpty(previewMessage))EditorGUILayout.HelpBox(previewMessage,MessageType.Info);
            var result=CurrentPreview;if(result==null)return;
            if(previewTexture)
            {
                var rect=GUILayoutUtility.GetRect(160,320,GUILayout.ExpandWidth(true));EditorGUI.DrawPreviewTexture(rect,previewTexture,null,ScaleMode.ScaleToFit);
            }
            EditorGUILayout.LabelField($"Completed {result.Request.Mode} · {result.Request.Samples}² · {result.Request.WidthMetres:N0} m · height {result.MinimumHeight:F3}…{result.MaximumHeight:F3} m (signed)");
            EditorGUILayout.LabelField($"Address {result.Request.Latitude:F4}°, {result.Request.Longitude:F4}° / heading {result.Request.Heading:F2}° · area {result.RegionArea:N1} m² · worker memory {result.EstimatedWorkingBytes/(1024.0*1024):F1} MiB estimate");
            EditorGUILayout.LabelField(result.Request.Mode==PlanetTerrainMapMode.MaterialGRSS?"GRSS legend: green grass / ochre sand / grey rock / white snow.":
                result.Request.Mode==PlanetTerrainMapMode.Height?"Black → white: displayed signed minimum → maximum height.":"Black → white: channel value 0 → 1.",EditorStyles.wordWrappedLabel);
            foreach(var row in result.Scatter)
            {
                EditorGUILayout.Space();EditorGUILayout.LabelField($"Species {row.SpeciesId} · {row.Density:G5} objects/m²",EditorStyles.boldLabel);
                string densityLabel=row.MinimumReferenceChordSpacingMetres>0?"Upper population estimate before spacing":"Expected in displayed region";
                EditorGUILayout.LabelField(row.SamplingStatus==SurfaceSampleStatus.Ready?$"{densityLabel}: {row.ExpectedObjects:N2} · weighted habitat acceptance {row.FilteredFraction:P2}":
                    $"Canonical filter data {row.SamplingStatus}: density result unavailable.");
                if(row.MinimumReferenceChordSpacingMetres>0)EditorGUILayout.LabelField($"Minimum reference chord: {row.MinimumReferenceChordSpacingMetres:G5} m · final count is lower after deterministic same-species thinning.");
                EditorGUILayout.LabelField(row.BankCells>=0?$"Render cells {row.RenderCells} / shadow-bank cells {row.BankCells} · proposals {row.Proposals:N0} · buffers {row.EstimatedBufferBytes/(1024.0*1024):F2} MiB":
                    "Cell estimate exceeded the bounded 8192-cell preview; cost is unavailable.");
                if(!row.CapacityValid)EditorGUILayout.HelpBox("Candidate capacity is insufficient for the metric density in an interest cell; change fixed level, density or slots.",MessageType.Warning);
            }
            if(result.Scatter.Count!=0)
            {
                EditorGUILayout.LabelField($"Configured budget: {result.Budget.MaximumCells:N0} cells / {result.Budget.MaximumCandidates:N0} proposals / {result.Budget.MaximumBytes/(1024.0*1024):F1} MiB / {result.Budget.NewCellsPerFrame} new cells per frame");
                EditorGUILayout.LabelField(result.CompleteCellEstimate?$"Single-bank budget: {(result.FitsSingleBank?"fits":"exceeded")} · active + replacement: {(result.FitsActiveAndReplacement?"fits":"exceeded")} · minimum cell batches {result.MinimumPreparationFrames:N0}":"Residency cost incomplete; no fit claim is available.");
                EditorGUILayout.HelpBox("Numerical estimate, not GPU timing or readback. Cell/proposal/pose buffers only; shared SurfaceField, meshes, textures and other retained banks are excluded. Terrain stamp exclusions are included; manual gameplay exclusions, frustum/occlusion and retention are not. Density integrates reference-sphere area and habitat gates before minimum-spacing thinning; it is not a final accepted count.",MessageType.Info);
            }
        }
        PlanetTerrainPreviewInput CapturePreviewInput(out PlanetSurfaceDataAsset source)
        {
            source=recipe&&recipe.Published?recipe.Published:generator?generator.SurfaceData:null;
            if(!source)throw new InvalidOperationException("Assign a published Terrain Recipe or a Generator with signed Surface Data; map preview never bakes or modifies its source.");
            if(!source.TryCreateSnapshot(out var snapshot,out var reason))throw new InvalidOperationException(reason);
            var request=new PlanetTerrainPreviewRequest(latitude,longitude,heading,widthMetres,previewSamples,previewMode,previewObserverHeight,
                checked(previewMaximumMiB*1024L*1024),checked(previewMaximumChecksMillions*1000000L));
            var species=new List<PlanetTerrainScatterPreviewSpecies>();var settings=generator?generator.ScatterSettings:null;
            var budget=default(PlanetTerrainScatterPreviewBudget);
            if(settings&&settings.Enabled)
            {
                if(!settings.IsValid)throw new InvalidOperationException("Assigned scatter settings require valid unique species, meshes/materials and positive budgets.");
                foreach(var item in settings.Species)species.Add(new PlanetTerrainScatterPreviewSpecies(item.Placement,item.RenderDistance,item.ShadowDistance,item.UnscaledBoundingRadius));
                budget=new PlanetTerrainScatterPreviewBudget(settings.MaximumResidentCells,settings.MaximumResidentCandidates,settings.MaximumResidentBytes,settings.MaximumNewCellsPerFrame);
            }
            return new PlanetTerrainPreviewInput(snapshot,request,species,budget);
        }
        public void RequestPreview()
        {
            if(pendingPreview!=null||EditorApplication.isPlayingOrWillChangePlaymode)return;
            try
            {
                previewInput=CapturePreviewInput(out previewSource);previewRecipe=recipe;previewGenerator=generator;
                previewCancellation=new CancellationTokenSource();var token=previewCancellation.Token;var input=previewInput;
                previewToken=previewPublication.Begin();previewMessage=null;
                ObservePreview(input,previewSource);pendingPreview=Task.Run(()=>PlanetTerrainPreview.Build(input,()=>token.IsCancellationRequested));
            }
            catch(Exception exception){previewMessage=exception.Message;}
        }
        void CancelPreview(){previewPublication.Cancel();previewCancellation?.Cancel();}
        void DetachPreviewTask()
        {
            var task=pendingPreview;pendingPreview=null;
            if(task!=null)task.ContinueWith(faulted=>{var ignored=faulted.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            previewCancellation?.Dispose();previewCancellation=null;
        }
        void ObservePreview(PlanetTerrainPreviewInput input,PlanetSurfaceDataAsset source)
        {
            observedPreviewSource=input.Source.ContentDigest;observedPreviewConfiguration=input.ConfigurationDigest;
            observedPreviewAsset=source.GetInstanceID();observedPreviewRecipe=recipe?recipe.GetInstanceID():0;observedPreviewGenerator=generator?generator.GetInstanceID():0;
        }
        void TickPreview()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode){CancelPreview();return;}
            if(pendingPreview!=null&&pendingPreview.IsCompleted)
            {
                var task=pendingPreview;pendingPreview=null;
                try
                {
                    var result=task.GetAwaiter().GetResult();var current=CapturePreviewInput(out var source);
                    if(previewCancellation.IsCancellationRequested||source!=previewSource||recipe!=previewRecipe||generator!=previewGenerator||
                        result.SourceContentDigest!=current.Source.ContentDigest||result.ConfigurationDigest!=current.ConfigurationDigest)
                    {previewMessage="Source, address or species changed; the stale map was discarded and the last complete map retained.";}
                    else
                    {
                        var texture=new Texture2D(result.Request.Samples,result.Request.Samples,TextureFormat.RGBAFloat,false,true){name="Planet Terrain numerical preview",hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp};
                        try
                        {
                            var colors=new Color[result.Pixels.Count];for(int i=0;i<colors.Length;i++){var p=result.Pixels[i];colors[i]=new Color(p.x,p.y,p.z,p.w);}texture.SetPixels(colors);texture.Apply(false,true);
                            if(previewPublication.TryPublish(previewToken,result,current.Source.ContentDigest,current.ConfigurationDigest))
                            {if(previewTexture)DestroyImmediate(previewTexture);previewTexture=texture;texture=null;previewMessage="Complete map published in this window; source assets unchanged.";}
                        }
                        finally{if(texture)DestroyImmediate(texture);}
                    }
                }
                catch(OperationCanceledException){previewMessage="Preview cancelled; the last complete map is retained.";}
                catch(Exception exception){previewMessage=exception.Message;}
                finally{previewCancellation?.Dispose();previewCancellation=null;Repaint();}
            }
            if(!autoPreview||EditorApplication.timeSinceStartup<nextPreviewPoll)return;
            nextPreviewPoll=EditorApplication.timeSinceStartup+.25;
            try
            {
                var input=CapturePreviewInput(out var source);
                if(input.Source.ContentDigest!=observedPreviewSource||input.ConfigurationDigest!=observedPreviewConfiguration||
                    source.GetInstanceID()!=observedPreviewAsset||(recipe?recipe.GetInstanceID():0)!=observedPreviewRecipe||(generator?generator.GetInstanceID():0)!=observedPreviewGenerator)
                {ObservePreview(input,source);previewChangedAt=EditorApplication.timeSinceStartup;CancelPreview();}
                else if(pendingPreview==null&&previewChangedAt>0&&EditorApplication.timeSinceStartup-previewChangedAt>=.35)
                {previewChangedAt=0;RequestPreview();}
            }
            catch(Exception exception){CancelPreview();previewMessage=exception.Message;}
        }
        void AddRegion()
        {
            try
            {
                var regions=new List<PlanetTerrainRegionRecipe>(recipe.Regions??Array.Empty<PlanetTerrainRegionRecipe>());
                foreach(var region in regions)if(region!=null&&region.Priority==layerPriority)throw new InvalidOperationException("Generated region priority is already occupied.");
                if(cells<2||cells>512)throw new ArgumentException("In-engine regional refinement supports 2–512 cells; Gaea exchange has a separate grid budget.");
                var next=new PlanetTerrainRegionRecipe{Latitude=latitude,Longitude=longitude,Heading=heading,WidthMetres=widthMetres,Cells=cells,Priority=layerPriority};
                next.ContextMetres=Math.Max(next.ContextMetres,4*widthMetres/cells);
                next.BlendMetres=Math.Min(next.ContextMetres,Math.Min(256,widthMetres*.25));
                next.DetailWavelengthMetres=Math.Max(128,2*widthMetres/cells);
                next.Capture(recipe.Radius);Undo.RecordObject(recipe,"Add regional terrain refinement");
                regions.Add(next);recipe.Regions=regions.ToArray();EditorUtility.SetDirty(recipe);AssetDatabase.SaveAssetIfDirty(recipe);
                information="Regional parameters were saved in Terrain Recipe. Bake and Publish regenerates them after the global solve.";failure=null;
            }
            catch(Exception exception){failure=exception.Message;}
        }
        void CreateRecipe()
        {
            string path=EditorUtility.SaveFilePanelInProject("Terrain recipe","TerrainRecipe","asset","Save the generation recipe.");
            if(string.IsNullOrEmpty(path))return;
            recipe=CreateInstance<PlanetTerrainRecipeAsset>();
            if(generator){recipe.Seed=generator.Seed;recipe.Radius=generator.Radius;recipe.MinimumHeight=-generator.Relief;recipe.MaximumHeight=generator.Relief;recipe.Style=(SurfaceStyle)generator.TerrainStyle;recipe.Published=generator.SurfaceData;}
            AssetDatabase.CreateAsset(recipe,AssetDatabase.GenerateUniqueAssetPath(path));AssetDatabase.SaveAssetIfDirty(recipe);
        }
        void StartBake()
        {
            failure=null;information=null;
            try
            {
                var source=recipe.Recipe;var settings=recipe.Bake?.Clone();
                if(settings==null)throw new ArgumentException("Bake settings are missing.");
                if(!settings.Validate(source,out var reason))throw new ArgumentException(reason);
                SurfaceSnapshot previous=null;
                if(recipe.Published&&!recipe.Published.TryCreateSnapshot(out previous,out reason))throw new InvalidOperationException(reason);
                previousContent=previous?.ContentDigest??default;
                var detail=new SurfaceDetailRecipe(recipe.DetailWavelengthMetres,recipe.DetailAmplitudeMetres,recipe.Seed^137);
                var regions=new List<SurfaceRegionRefinementSettings>();
                if(recipe.Regions!=null)foreach(var region in recipe.Regions)
                    regions.Add(region?.Capture(source.Radius)??throw new ArgumentException("Regional recipe entry is missing."));
                newAssetPath=null;
                if(!recipe.Published)
                {
                    newAssetPath=EditorUtility.SaveFilePanelInProject("Published terrain","PlanetSurface","asset","Save the completed terrain dataset.");
                    if(string.IsNullOrEmpty(newAssetPath))return;
                }
                recipeSignature=JsonUtility.ToJson(recipe);cancellation=new CancellationTokenSource();var token=cancellation.Token;
                pending=Task.Run(()=>
                {
                    if(token.IsCancellationRequested)throw new OperationCanceledException();
                    var result=PlanetTerrainGeneration.Bake(source,settings,detail,previous,regions,value=>{lock(progressLock)progress=value;},()=>token.IsCancellationRequested);
                    if(token.IsCancellationRequested)throw new OperationCanceledException();
                    return result;
                });
            }
            catch(Exception exception){failure=exception.Message;}
        }
        void Tick()
        {
            TickPreview();
            if(pendingImport!=null){TickImport();return;}
            if(pending==null)return;
            Repaint();if(!pending.IsCompleted)return;
            var completed=pending;pending=null;
            try
            {
                var result=completed.GetAwaiter().GetResult();
                if(cancellation.IsCancellationRequested)throw new OperationCanceledException();
                if(!recipe||JsonUtility.ToJson(recipe)!=recipeSignature)throw new InvalidOperationException("The recipe changed during bake. The previous published surface was retained.");
                if(recipe.Published&&(!recipe.Published.TryCreateSnapshot(out var current,out var error)||current.ContentDigest!=previousContent))
                    throw new InvalidOperationException("The published source changed during bake; the completed stale generation was not bound.");
                PlanetTerrainAuthoring.PublishGeneration(recipe,generator,result,newAssetPath);
                var diagnostics=result.GlobalBake.Diagnostics;
                recipe.LastBakeSeconds=diagnostics.ElapsedSeconds;recipe.LastNodeCount=diagnostics.NodeCount;
                recipe.LastWorkingBytes=Math.Max(diagnostics.EstimatedWorkingBytes,result.EstimatedResidentBytes);recipe.LastMassResidual=diagnostics.MassResidual;recipe.LastLodError=diagnostics.MaxLodError;
                foreach(var region in result.RegionalDiagnostics){recipe.LastBakeSeconds+=region.ElapsedSeconds;recipe.LastWorkingBytes=Math.Max(recipe.LastWorkingBytes,region.EstimatedWorkingBytes);}
                EditorUtility.SetDirty(recipe);AssetDatabase.SaveAssetIfDirty(recipe);
                information=result.UsedCache?"Reused unchanged global bake; regional solves completed and authored layers were preserved.":"Published completed global and regional terrain generation.";failure=null;
            }
            catch(OperationCanceledException){failure="Bake cancelled; previous published surface retained.";}
            catch(Exception exception){failure=exception.Message;}
            finally{cancellation?.Dispose();cancellation=null;}
        }
        void TickImport()
        {
            Repaint();if(!pendingImport.IsCompleted)return;var completed=pendingImport;pendingImport=null;
            try
            {
                var result=completed.GetAwaiter().GetResult();
                if(cancellation.IsCancellationRequested)throw new OperationCanceledException();
                if(!recipe||JsonUtility.ToJson(recipe)!=recipeSignature)throw new InvalidOperationException("The recipe changed during mask repair. The previous published surface was retained.");
                if(!recipe.Published||!recipe.Published.TryCreateSnapshot(out var current,out var error)||current.ContentDigest!=previousContent)
                    throw new InvalidOperationException("The published source changed during mask repair; this stale import was not bound.");
                PlanetTerrainAuthoring.Publish(recipe,generator,result.Snapshot,coarseLevels:result.Levels);
                information="Published authored height and repaired automatic materials together; explicit masks were retained. Hydrology remains inherited context.";failure=null;
            }
            catch(OperationCanceledException){failure="Import cancelled; previous published surface retained.";}
            catch(Exception exception){failure=exception.Message;}
            finally{cancellation?.Dispose();cancellation=null;}
        }
        void Export()
        {
            try
            {
                string folder=EditorUtility.OpenFolderPanel("Export Gaea region","","");if(string.IsNullOrEmpty(folder))return;
                if(!recipe.Published.TryCreateSnapshot(out var snapshot,out var reason))throw new InvalidOperationException(reason);
                PlanetTerrainExchangeService.Export(snapshot,planetInstanceId,folder,new PlanetSurfaceAddress{Latitude=latitude,Longitude=longitude,Heading=heading},widthMetres,cells,guardSamples);
                failure=null;
            }
            catch(Exception exception){failure=exception.Message;}
        }
        void Import()
        {
            try
            {
                string manifest=EditorUtility.OpenFilePanel("Terrain exchange manifest","","json");if(string.IsNullOrEmpty(manifest))return;
                string height=EditorUtility.OpenFilePanel("Edited linear float32 height",Path.GetDirectoryName(manifest),"r32,exr");if(string.IsNullOrEmpty(height))return;
                string masks=null,erosionMasks=null;
                if(importMaterialMasks){masks=EditorUtility.OpenFolderPanel("Edited grass/sand/rock/snow maps",Path.GetDirectoryName(manifest),"");if(string.IsNullOrEmpty(masks))return;}
                if(importErosionMasks){erosionMasks=EditorUtility.OpenFolderPanel("Edited flow/wetness/wear/deposition maps",Path.GetDirectoryName(manifest),"");if(string.IsNullOrEmpty(erosionMasks))return;}
                if(!recipe.Published.TryCreateSnapshot(out var snapshot,out var reason))throw new InvalidOperationException(reason);
                // EXR texture import is main-thread-only. The common height-only RAW route reads files on the worker.
                bool rawOnly=string.Equals(Path.GetExtension(height),".r32",StringComparison.OrdinalIgnoreCase)&&string.IsNullOrWhiteSpace(masks)&&string.IsNullOrWhiteSpace(erosionMasks);
                var layer=rawOnly?null:PlanetTerrainExchangeService.Import(snapshot,planetInstanceId,manifest,height,importMode,layerPriority,masks,erosionMasks);
                if(!snapshot.HasAutomaticMaterials && string.IsNullOrWhiteSpace(masks) && !migrateLegacyMaterialRules)
                    throw new InvalidOperationException("This legacy dataset has no captured material profile. Verify its original Terrain Recipe and explicitly enable legacy mask migration; custom climate settings cannot be inferred.");
                bool migrate=!snapshot.HasAutomaticMaterials&&migrateLegacyMaterialRules,replace=replacePriority;
                var profile=migrate?SurfaceAutomaticMaterialProfile.FromBakeSettings(recipe.Bake):default;
                var capturedMode=importMode;int capturedPriority=layerPriority;string capturedInstance=planetInstanceId;
                var coarseSources=new List<SurfaceSnapshot>();
                if(recipe.LodPyramid!=null)foreach(var data in recipe.LodPyramid)
                {
                    if(!data)continue;if(!data.TryCreateSnapshot(out var coarse,out reason))throw new InvalidOperationException(reason);
                    if(coarse.Revision.BaseDigest==snapshot.Revision.BaseDigest&&coarse.Resolution!=snapshot.Resolution)coarseSources.Add(coarse);
                }
                long maximumBytes=recipe.Bake.MaximumWorkingBytes;
                long retainedSource=PlanetTerrainBakeCache.EstimateSnapshotBytes(snapshot);
                foreach(var coarse in coarseSources)retainedSource=checked(retainedSource+PlanetTerrainBakeCache.EstimateSnapshotBytes(coarse));
                previousContent=snapshot.ContentDigest;recipeSignature=JsonUtility.ToJson(recipe);cancellation=new CancellationTokenSource();var token=cancellation.Token;
                pendingImport=Task.Run(()=>
                {
                    Func<bool> cancelled=()=>token.IsCancellationRequested;Action<SurfaceBakeProgress> report=value=>{lock(progressLock)progress=value;};
                    if(cancelled())throw new OperationCanceledException();
                    var imported=layer??PlanetTerrainExchangeService.Import(snapshot,capturedInstance,manifest,height,capturedMode,capturedPriority);
                    var input=migrate?SurfaceMaterialRepair.MigrateFromRecipe(snapshot,profile):snapshot;
                    var result=PlanetTerrainExchangeService.PublishLayer(input,imported,replace,repairAutomaticMaterials:false);
                    if(result.HasAutomaticMaterials)
                    {
                        long retained=checked(retainedSource+PlanetTerrainBakeCache.EstimateSnapshotBytes(result)+(migrate?PlanetTerrainBakeCache.EstimateSnapshotBytes(input):0));
                        var allowance=PlanetTerrainGeneration.RepairAllowance(maximumBytes,retained,result.Tiles,result.Regions,result.AutomaticMaterialProfile);
                        result=SurfaceMaterialRepair.Rebuild(result,allowance,report,cancelled);
                    }
                    var levels=new List<SurfaceSnapshot>();
                    long retainedLevels=checked(retainedSource+PlanetTerrainBakeCache.EstimateSnapshotBytes(result));
                    foreach(var coarse in coarseSources)
                    {
                        var allowance=result.HasAutomaticMaterials?PlanetTerrainGeneration.RepairAllowance(maximumBytes,retainedLevels,coarse.Tiles,result.Regions,result.AutomaticMaterialProfile):null;
                        var candidate=new SurfaceSnapshot(result.Recipe,result.Revision,coarse.CanonicalTileLevel,coarse.Resolution,coarse.Tiles,result.Detail,result.Regions,result.Stamps,result.AutomaticMaterialProfile);
                        var ready=candidate.HasAutomaticMaterials?SurfaceMaterialRepair.Rebuild(candidate,allowance,report,cancelled):candidate;
                        levels.Add(ready);retainedLevels=checked(retainedLevels+PlanetTerrainBakeCache.EstimateSnapshotBytes(ready));
                    }
                    if(retainedLevels>maximumBytes)throw new InvalidOperationException("Imported terrain plus retained source and material LOD data exceeds MaximumWorkingBytes; the old generation is retained.");
                    if(cancelled())throw new OperationCanceledException();return new MaterialImportResult{Snapshot=result,Levels=levels};
                });
                failure=null;information=null;
            }
            catch(Exception exception){failure=exception.Message;}
        }
    }
}
