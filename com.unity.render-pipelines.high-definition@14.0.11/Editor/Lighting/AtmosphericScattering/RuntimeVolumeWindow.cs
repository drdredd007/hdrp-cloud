using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    public sealed class RuntimeVolumeWindow : EditorWindow
    {
        [SerializeField] Camera selectedCamera;
        [SerializeField] VolumeProfile savedProfile;
        [SerializeField] bool showSources;
        Vector2 scroll, sourceScroll;
        RuntimeVolumeSession session;
        Editor profileEditor;
        double nextRepaint;

        [MenuItem("Tools/Coordinates/Live Volume Inspector")]
        public static void Open() => GetWindow<RuntimeVolumeWindow>("Live Volumes");

        void OnEnable()
        {
            minSize = new Vector2(420, 350);
            session = new RuntimeVolumeSession();
            HDCamera.volumeStackUpdated += OnVolumeStackUpdated;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += Tick;
        }
        void OnDisable()
        {
            HDCamera.volumeStackUpdated -= OnVolumeStackUpdated;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= Tick;
            Clear();
        }
        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) Clear();
            Repaint();
        }
        void Clear()
        {
            if (profileEditor) DestroyImmediate(profileEditor);
            profileEditor = null;
            session?.Dispose();
        }
        void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextRepaint) return;
            nextRepaint = EditorApplication.timeSinceStartup + .15;
            if (session?.Profile && !session.Camera) Clear();
            if (EditorApplication.isPlaying && !selectedCamera)
                selectedCamera = Camera.main ? Camera.main : Camera.allCameras.FirstOrDefault(c => c.cameraType == CameraType.Game);
            Repaint();
        }
        void OnVolumeStackUpdated(HDCamera camera)
        {
            if (!EditorApplication.isPlaying || camera.camera != selectedCamera) return;
            if (!session.Profile) session.Capture(selectedCamera, camera.volumeStack);
            session.Apply(camera.camera, camera.volumeStack);
        }
        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var next = (Camera)EditorGUILayout.ObjectField(selectedCamera, typeof(Camera), true);
                if (GUILayout.Button("Game camera", EditorStyles.toolbarButton, GUILayout.Width(90)))
                    next = Camera.main ? Camera.main : Camera.allCameras.FirstOrDefault(c => c.cameraType == CameraType.Game);
                if (next != selectedCamera) { Clear(); selectedCamera = next; }
            }
            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode and select a camera. Live edits are temporary; save a profile before leaving Play Mode to keep them.", MessageType.Info);
                return;
            }
            if (!selectedCamera || !session.Profile)
            {
                EditorGUILayout.HelpBox("Select a rendering HDRP camera. Waiting for its resolved Volume settings…", MessageType.Info);
                return;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                session.Preview = EditorGUILayout.ToggleLeft("Preview edits", session.Preview, GUILayout.Width(110));
                if (GUILayout.Button("Reset overrides"))
                {
                    Undo.RecordObjects(session.Profile.components.ToArray(), "Reset live volume overrides");
                    session.Reset();
                }
                if (GUILayout.Button("Save profile…")) Save();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                savedProfile = (VolumeProfile)EditorGUILayout.ObjectField("Saved profile", savedProfile, typeof(VolumeProfile), false);
                using (new EditorGUI.DisabledScope(!savedProfile))
                    if (GUILayout.Button("Load", GUILayout.Width(55)))
                    {
                        Undo.RecordObjects(session.Profile.components.ToArray(), "Load live volume overrides");
                        session.Import(savedProfile);
                        if (profileEditor) DestroyImmediate(profileEditor);
                    }
            }
            EditorGUILayout.HelpBox("Values follow this camera live. Tick a parameter's override checkbox to edit it. Preview affects only this camera; closing the window restores normal Volume blending. Save exports only your overrides.", MessageType.None);
            showSources = EditorGUILayout.Foldout(showSources, "Source volumes and priorities", true);
            if (showSources) DrawSources();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (!profileEditor) profileEditor = Editor.CreateEditor(session.Profile);
            profileEditor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();
        }
        void DrawSources()
        {
            var camera = HDCamera.GetOrCreate(selectedCamera);
            sourceScroll = EditorGUILayout.BeginScrollView(sourceScroll, GUILayout.MaxHeight(180));
            EditorGUILayout.LabelField("Candidates in the camera Volume mask; local influence also depends on distance.", EditorStyles.wordWrappedMiniLabel);
            foreach (var volume in VolumeManager.instance.GetVolumes(camera.volumeLayerMask).Reverse())
            {
                if (!volume) continue;
                if (!volume.HasInstantiatedProfile() && !volume.sharedProfile) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.ObjectField(volume, typeof(Volume), true);
                    var profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
                    EditorGUILayout.ObjectField(profile, typeof(VolumeProfile), false);
                    EditorGUILayout.LabelField($"Priority {volume.priority:g} · Weight {volume.weight:g} · {(volume.isGlobal ? "Global" : "Local")} · {(volume.isActiveAndEnabled ? "Enabled" : "Disabled")}", EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }
        void Save()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save live volume overrides", "LiveWeather", "asset", "Save the checked overrides as a reusable VolumeProfile.");
            if (string.IsNullOrEmpty(path)) return;
            // Never replace an existing shared profile implicitly.
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            var result = session.Export();
            AssetDatabase.CreateAsset(result, path);
            foreach (var component in result.components) AssetDatabase.AddObjectToAsset(component, result);
            EditorUtility.SetDirty(result);
            AssetDatabase.SaveAssets();
            savedProfile = result;
            EditorGUIUtility.PingObject(result);
        }
    }
}
