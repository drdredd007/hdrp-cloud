using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    public sealed class RuntimeVolumeWindow : EditorWindow
    {
        [SerializeField] Camera selectedCamera;
        [SerializeField] Volume selectedVolume;
        Editor profileEditor;
        VolumeProfile editedProfile;
        readonly Dictionary<Object, HideFlags> originalFlags = new Dictionary<Object, HideFlags>();
        Vector2 scroll;
        double nextRepaint;

        [MenuItem("Tools/Coordinates/Live Volume Inspector")]
        public static void Open() => GetWindow<RuntimeVolumeWindow>("Live Volumes");
        void OnEnable()
        {
            minSize = new Vector2(420, 350);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            ClearEditor();
        }
        void PlayModeChanged(PlayModeStateChange state)
        {
            if(state == PlayModeStateChange.ExitingPlayMode) ClearEditor();
        }
        void ClearEditor()
        {
            if(profileEditor) DestroyImmediate(profileEditor);
            profileEditor = null; editedProfile = null;
            foreach(var pair in originalFlags) if(pair.Key) pair.Key.hideFlags = pair.Value;
            originalFlags.Clear();
        }
        void MakeEditable(Object target)
        {
            if(!target || EditorUtility.IsPersistent(target)) return;
            if(!originalFlags.ContainsKey(target)) originalFlags.Add(target, target.hideFlags);
            target.hideFlags &= ~HideFlags.NotEditable;
        }
        void Tick()
        {
            if(EditorApplication.timeSinceStartup < nextRepaint) return;
            nextRepaint = EditorApplication.timeSinceStartup + .15;
            if(!selectedCamera) selectedCamera = Camera.main ? Camera.main : Camera.allCameras.FirstOrDefault(c => c.cameraType == CameraType.Game);
            Repaint();
        }
        public static VolumeProfile UsedProfile(Volume volume) => volume && volume.HasInstantiatedProfile() ? volume.profile : volume ? volume.sharedProfile : null;
        static bool AffectsCamera(Volume volume, Transform anchor)
        {
            if(volume.isGlobal) return true;
            if(!anchor) return false;
            foreach(var collider in volume.GetComponents<Collider>())
                if(collider.enabled && (collider.ClosestPoint(anchor.position)-anchor.position).sqrMagnitude <= volume.blendDistance*volume.blendDistance)
                    return true;
            return false;
        }
        void OnGUI()
        {
            selectedCamera = (Camera)EditorGUILayout.ObjectField("Camera", selectedCamera, typeof(Camera), true);
            if(!selectedCamera) { EditorGUILayout.HelpBox("Select a camera to see its Volume profiles.",MessageType.Info); return; }
            var hd = HDCamera.GetOrCreate(selectedCamera);
            var volumes = VolumeManager.instance.GetVolumes(hd.volumeLayerMask)
                .Where(v => v && v.isActiveAndEnabled && v.weight > 0 && UsedProfile(v) && AffectsCamera(v, hd.volumeAnchor)).Reverse().ToArray();
            if(volumes.Length == 0) { ClearEditor(); EditorGUILayout.HelpBox("No active Volume profiles in this camera's Volume mask.", MessageType.Info); return; }
            int index = System.Array.IndexOf(volumes, selectedVolume);
            if(index < 0) index = 0;
            var labels = volumes.Select(v => $"{v.name} / {UsedProfile(v).name} (priority {v.priority:g})").ToArray();
            index = EditorGUILayout.Popup("Volume / Profile", index, labels);
            selectedVolume = volumes[index];
            var profile = UsedProfile(selectedVolume);
            if(profile != editedProfile)
            {
                ClearEditor(); editedProfile = profile;
                MakeEditable(profile);
                foreach(var component in profile.components) MakeEditable(component);
                profileEditor = Editor.CreateEditor(profile);
            }
            // Add Override can create new components during the session.
            foreach(var component in profile.components) MakeEditable(component);
            using(new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.ObjectField("Profile", profile, typeof(VolumeProfile), false);
                if(GUILayout.Button("Select", GUILayout.Width(55))) Selection.activeObject = profile;
                if(GUILayout.Button("Save copy…", GUILayout.Width(95))) SaveCopy(profile);
            }
            EditorGUILayout.LabelField($"Weight {selectedVolume.weight:g} · {(selectedVolume.isGlobal ? "Global" : "Local (distance blend)")}", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(EditorUtility.IsPersistent(profile)
                ? "Editing the actual profile asset. Changes are saved normally, including in Play Mode. Higher-priority profiles can override its values."
                : "Editing the actual runtime profile. Changes remain for this Play session; Save copy keeps a reusable asset.", MessageType.None);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            profileEditor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();
        }
        void SaveCopy(VolumeProfile source)
        {
            string path = EditorUtility.SaveFilePanelInProject("Save VolumeProfile copy", "WeatherProfile", "asset", "Save the full profile with its current overrides.");
            if(string.IsNullOrEmpty(path)) return;
            var result = CreateInstance<VolumeProfile>();
            foreach(var component in source.components)
            {
                if(!component) continue;
                var copy = Instantiate(component); copy.hideFlags = HideFlags.None; copy.name = component.GetType().Name;
                result.components.Add(copy);
            }
            AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(path));
            foreach(var component in result.components) AssetDatabase.AddObjectToAsset(component,result);
            EditorUtility.SetDirty(result); AssetDatabase.SaveAssets(); EditorGUIUtility.PingObject(result);
        }
    }
}
