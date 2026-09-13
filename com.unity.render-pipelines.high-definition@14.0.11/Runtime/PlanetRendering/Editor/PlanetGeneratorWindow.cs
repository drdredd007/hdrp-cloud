using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class PlanetGeneratorWindow : EditorWindow
    {
        [SerializeField] PlanetGeneratorAsset settings;
        [SerializeField] bool livePreview=true;
        [SerializeField] double altitude=2000000;
        [SerializeField] Vector2 orbit=new Vector2(35,15);
        UnityEditor.Editor inspector;
        Vector2 scroll;
        Scene previewScene;
        Camera previewCamera;
        PlanetFarPass pass;
        RenderTexture texture;
        bool dirty=true,manualRefresh;
        double changedAt;
        string failure;
        int previewWidth=640,previewHeight=480;

        [MenuItem("Window/Rendering/HDRP Planet Generator")]
        public static void Open()=>GetWindow<PlanetGeneratorWindow>("Planet Generator");
        public static void Open(PlanetGeneratorAsset asset){var window=GetWindow<PlanetGeneratorWindow>("Planet Generator");window.settings=asset;window.Invalidate();}
        [OnOpenAsset] static bool OpenAsset(int instanceId,int line)
        {if(EditorUtility.InstanceIDToObject(instanceId) is PlanetGeneratorAsset asset){Open(asset);return true;}return false;}
        void OnEnable()
        {
            PlanetGeneratorAssetInspector.SettingsChanged+=OnAssetChanged;minSize=new Vector2(760,440);EditorApplication.update+=Tick;Undo.undoRedoPerformed+=Invalidate;
            EditorApplication.playModeStateChanged+=OnPlayMode;AssemblyReloadEvents.beforeAssemblyReload+=ReleasePreview;
        }
        void OnDisable()
        {
            PlanetGeneratorAssetInspector.SettingsChanged-=OnAssetChanged;EditorApplication.update-=Tick;Undo.undoRedoPerformed-=Invalidate;EditorApplication.playModeStateChanged-=OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload-=ReleasePreview;ReleasePreview();if(inspector)DestroyImmediate(inspector);
        }
        void OnPlayMode(PlayModeStateChange state){ReleasePreview();Invalidate();}
        void OnAssetChanged(PlanetGeneratorAsset asset){if(asset==settings)Invalidate();}
        void OnSelectionChange(){if(Selection.activeObject is PlanetGeneratorAsset asset){settings=asset;Invalidate();}}
        void Invalidate(){dirty=true;failure=null;changedAt=EditorApplication.timeSinceStartup;Repaint();}
        void InvalidateView(){dirty=true;failure=null;changedAt=0;Repaint();}
        public static PlanetGeneratorAsset CreateAsset(string path)
        {
            var asset=CreateInstance<PlanetGeneratorAsset>();asset.PlanetId=Math.Max(1,Guid.NewGuid().GetHashCode()&int.MaxValue);
            AssetDatabase.CreateAsset(asset,AssetDatabase.GenerateUniqueAssetPath(path));AssetDatabase.SaveAssetIfDirty(asset);return asset;
        }
        void CreateNew()
        {
            string path=EditorUtility.SaveFilePanelInProject("Create planet","Planet","asset","Choose where to save the planet generator.");
            if(string.IsNullOrEmpty(path))return;
            settings=CreateAsset(path);Selection.activeObject=settings;Invalidate();
        }
        void OnGUI()
        {
            using(new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if(GUILayout.Button("New Planet",EditorStyles.toolbarButton,GUILayout.Width(90)))CreateNew();
                EditorGUI.BeginChangeCheck();settings=(PlanetGeneratorAsset)EditorGUILayout.ObjectField(settings,typeof(PlanetGeneratorAsset),false,GUILayout.MinWidth(170));
                if(EditorGUI.EndChangeCheck())Invalidate();
                if(GUILayout.Button("Save",EditorStyles.toolbarButton,GUILayout.Width(55)) && settings)AssetDatabase.SaveAssetIfDirty(settings);
                livePreview=GUILayout.Toggle(livePreview,"Live Preview",EditorStyles.toolbarButton,GUILayout.Width(95));
                if(GUILayout.Button("Refresh",EditorStyles.toolbarButton,GUILayout.Width(65))){manualRefresh=true;Invalidate();RenderPreview();}
            }
            if(!settings){EditorGUILayout.HelpBox("Create a planet or select a Planet Generator asset in the Project window.",MessageType.Info);return;}
            using(new EditorGUILayout.HorizontalScope())
            {
                using(new EditorGUILayout.VerticalScope(GUILayout.Width(290)))
                {
                    scroll=EditorGUILayout.BeginScrollView(scroll);
                    UnityEditor.Editor.CreateCachedEditor(settings,typeof(PlanetGeneratorAssetInspector),ref inspector);
                    EditorGUI.BeginChangeCheck();((PlanetGeneratorAssetInspector)inspector).DrawSettings();
                    if(EditorGUI.EndChangeCheck())Invalidate();
                    if(GUILayout.Button("New seed")){Undo.RecordObject(settings,"Change planet seed");settings.Seed=Guid.NewGuid().GetHashCode();EditorUtility.SetDirty(settings);Invalidate();}
                    EditorGUILayout.Space();EditorGUILayout.LabelField("Observer",EditorStyles.boldLabel);
                    EditorGUI.BeginChangeCheck();altitude=math.max(50000,EditorGUILayout.DoubleField("Altitude (km)",altitude/1000)*1000);
                    if(EditorGUI.EndChangeCheck())Invalidate();
                    if(GUILayout.Button("Whole planet")){altitude=math.max(50000,settings.Radius);orbit=new Vector2(35,15);Invalidate();}
                    EditorGUILayout.EndScrollView();
                }
                using(new EditorGUILayout.VerticalScope())
                {
                    var rect=GUILayoutUtility.GetRect(200,200,GUILayout.ExpandWidth(true),GUILayout.ExpandHeight(true));
                    int width=math.clamp((int)rect.width,64,1280),height=math.clamp((int)rect.height,64,960);
                    if(Event.current.type==EventType.Repaint && (previewWidth!=width || previewHeight!=height)){previewWidth=width;previewHeight=height;Invalidate();}
                    EditorGUI.DrawRect(rect,new Color(.035f,.04f,.05f));
                    if(texture)GUI.DrawTexture(rect,texture,ScaleMode.ScaleToFit,false);
                    HandleOrbit(rect);
                    GUILayout.Label("Drag to orbit · Wheel to zoom · Orbital preview, minimum altitude 50 km",EditorStyles.miniLabel);
                    if(pass!=null)GUILayout.Label($"{pass.PatchCount} patches"+(pass.IsRefining?" · Refining…":""),EditorStyles.miniLabel);
                    if(!settings.Definition.IsValid)EditorGUILayout.HelpBox("Enter a positive radius and relief below one tenth of the radius.",MessageType.Error);
                    if(EditorApplication.isPlayingOrWillChangePlaymode)EditorGUILayout.HelpBox("The generator preview is paused during Play Mode.",MessageType.Info);
                    if(!string.IsNullOrEmpty(failure))EditorGUILayout.HelpBox(failure,MessageType.Error);
                }
            }
        }
        void HandleOrbit(Rect rect)
        {
            var e=Event.current;int control=GUIUtility.GetControlID(FocusType.Passive);
            if(e.type==EventType.MouseDown && e.button==0 && rect.Contains(e.mousePosition)){GUIUtility.hotControl=control;e.Use();}
            if(e.type==EventType.MouseDrag && GUIUtility.hotControl==control)
            {orbit.x+=e.delta.x*.3f;orbit.y=math.clamp(orbit.y+e.delta.y*.3f,-85,85);InvalidateView();e.Use();}
            if(e.type==EventType.MouseUp && GUIUtility.hotControl==control){GUIUtility.hotControl=0;e.Use();}
            if(e.type==EventType.ScrollWheel && rect.Contains(e.mousePosition))
            {altitude=math.clamp(altitude*math.pow(1.1,e.delta.y),50000,math.max(50000,settings.Radius*10));InvalidateView();e.Use();}
        }
        void Tick()
        {
            if((livePreview || manualRefresh) && dirty && EditorApplication.timeSinceStartup-changedAt>.25)RenderPreview();
        }
        void EnsurePreview()
        {
            if(previewCamera)return;
            if(!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset))throw new InvalidOperationException("Select an HDRP render pipeline to use the planet preview.");
            previewScene=EditorSceneManager.NewPreviewScene();
            var root=EditorUtility.CreateGameObjectWithHideFlags("Planet generator preview",HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(root,previewScene);
            previewCamera=root.AddComponent<Camera>();previewCamera.enabled=false;previewCamera.cullingMask=0;previewCamera.fieldOfView=70;previewCamera.nearClipPlane=.05f;previewCamera.farClipPlane=10000;
            var hd=root.AddComponent<HDAdditionalCameraData>();hd.clearColorMode=HDAdditionalCameraData.ClearColorMode.Color;hd.backgroundColorHDR=new Color(.006f,.009f,.018f);hd.volumeLayerMask=0;
            hd.customRenderingSettings=true;
            foreach(var flag in new[]{FrameSettingsField.ExposureControl,FrameSettingsField.Postprocess,FrameSettingsField.AtmosphericScattering})
            {hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)flag]=true;hd.renderingPathCustomFrameSettings.SetEnabled(flag,false);}
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.CustomPass]=true;
            hd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.CustomPass,true);
            var volume=root.AddComponent<CustomPassVolume>();volume.targetCamera=previewCamera;volume.injectionPoint=CustomPassInjectionPoint.BeforeTransparent;
            pass=(PlanetFarPass)volume.AddPassOfType<PlanetFarPass>();pass.name="Planet generator preview";pass.Observer=previewCamera;
            pass.PlanetShader=Shader.Find("SpaceRunner/Planet Far Surface");pass.CompositeShader=Shader.Find("SpaceRunner/Planet Composite");
            pass.LightDirection=new Vector3(.4f,.7f,-.58f).normalized;pass.LightLux=math.PI;
        }
        void RenderPreview()
        {
            dirty=false;
            if(!settings || !settings.Definition.IsValid || EditorApplication.isPlayingOrWillChangePlaymode)return;
            try
            {
                EnsurePreview();
                if(!texture || texture.width!=previewWidth || texture.height!=previewHeight)
                {
                    if(texture){texture.Release();DestroyImmediate(texture);}
                    texture=new RenderTexture(previewWidth,previewHeight,24,RenderTextureFormat.ARGB32){hideFlags=HideFlags.HideAndDontSave};texture.Create();
                }
                var definition=settings.Definition;pass.Definition=definition;pass.LodSettings=settings.Lod;pass.PlanetRotation=Quaternion.Euler(settings.Orientation);
                var radial=Quaternion.Euler(orbit.y,orbit.x,0)*Vector3.back;
                pass.CameraPosition=(double3)(float3)radial*(definition.Radius+altitude);
                previewCamera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.LookRotation(-radial,Vector3.up));
                previewCamera.targetTexture=texture;previewCamera.Render();previewCamera.targetTexture=null;
                dirty=pass.IsRefining;manualRefresh=manualRefresh && dirty;failure=null;Repaint();
            }
            catch(Exception e){failure=e.Message;ReleasePreview();Repaint();}
        }
        void ReleasePreview()
        {
            if(previewCamera)previewCamera.targetTexture=null;
            if(previewScene.IsValid())EditorSceneManager.ClosePreviewScene(previewScene);
            previewCamera=null;pass=null;
            if(texture){texture.Release();DestroyImmediate(texture);texture=null;}
        }
    }
    [CustomEditor(typeof(PlanetGeneratorAsset))]
    public sealed class PlanetGeneratorAssetInspector : UnityEditor.Editor
    {
        internal static event Action<PlanetGeneratorAsset> SettingsChanged;
        public void DrawSettings(){serializedObject.Update();DrawPropertiesExcluding(serializedObject,"m_Script");if(serializedObject.ApplyModifiedProperties())SettingsChanged?.Invoke((PlanetGeneratorAsset)target);}
        public override void OnInspectorGUI(){DrawSettings();if(GUILayout.Button("Open Planet Generator"))PlanetGeneratorWindow.Open((PlanetGeneratorAsset)target);}
    }
}
