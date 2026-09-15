using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition
{
    /// <summary>A private, editable copy of a camera's resolved volumes. Never edits source profiles.</summary>
    public sealed class RuntimeVolumeSession : IDisposable
    {
        public Camera Camera { get; private set; }
        public VolumeProfile Profile { get; private set; }
        public bool Preview = true;
        GameObject resetMarker;

        public void Capture(Camera camera, VolumeStack stack)
        {
            Dispose();
            Camera = camera;
            Profile = ScriptableObject.CreateInstance<VolumeProfile>();
            Profile.name = "Live camera volume overrides";
            Profile.hideFlags = HideFlags.HideAndDontSave;
            // Keep VolumeManager's normal per-frame reset active even in a scene with no volumes.
            // This empty, zero-weight volume contributes no settings to any camera.
            resetMarker = new GameObject("Live Volume preview lifecycle") { hideFlags = HideFlags.HideAndDontSave };
            resetMarker.AddComponent<Volume>().weight = 0;
            foreach (var type in VolumeManager.instance.baseComponentTypeArray.OrderBy(t => t.Name))
            {
                var source = stack.GetComponent(type);
                if (source == null) continue;
                var copy = UnityEngine.Object.Instantiate(source);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.name = type.Name;
                copy.active = true;
                copy.SetAllOverridesTo(false);
                Profile.components.Add(copy);
            }
        }

        public void Apply(Camera camera, VolumeStack stack)
        {
            if (!Profile || camera != Camera) return;
            foreach (var component in Profile.components)
            {
                if (!component) continue;
                var resolved = stack.GetComponent(component.GetType());
                if (resolved == null) continue;
                // Keep all untouched values live, especially the planet centre after rebasing.
                for (int i = 0; i < component.parameters.Count; ++i)
                    if (!component.parameters[i].overrideState)
                        component.parameters[i].SetValue(resolved.parameters[i]);
                if (Preview && component.active) component.Override(resolved, 1);
            }
        }

        public void Reset()
        {
            if (!Profile) return;
            foreach (var component in Profile.components) if (component) component.SetAllOverridesTo(false);
        }

        public void Import(VolumeProfile source)
        {
            if (!Profile || !source) return;
            Reset();
            foreach (var component in source.components)
            {
                var destination = Profile.components.FirstOrDefault(c => c && c.GetType() == component.GetType());
                if (destination == null) continue;
                EditorUtility.CopySerialized(component, destination);
                destination.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        public VolumeProfile Export()
        {
            var result = ScriptableObject.CreateInstance<VolumeProfile>();
            if (Profile)
                foreach (var component in Profile.components)
                    if (component && component.active && component.parameters.Any(p => p.overrideState))
                    {
                        var copy = UnityEngine.Object.Instantiate(component);
                        copy.hideFlags = HideFlags.None;
                        copy.name = component.GetType().Name;
                        result.components.Add(copy);
                    }
            return result;
        }

        public void Dispose()
        {
            if (Profile)
            {
                foreach (var component in Profile.components) if (component) UnityEngine.Object.DestroyImmediate(component);
                UnityEngine.Object.DestroyImmediate(Profile);
            }
            Profile = null;
            Camera = null;
            if (resetMarker) UnityEngine.Object.DestroyImmediate(resetMarker);
            resetMarker = null;
        }
    }
}
