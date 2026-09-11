using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    [CustomEditor(typeof(VolumetricCloudsRegion))]
    [CanEditMultipleObjects]
    class VolumetricCloudsRegionEditor : Editor
    {
        SerializedProperty m_CloudType;
        SerializedProperty m_Coverage;
        SerializedProperty m_RainIntensity;
        SerializedProperty m_DensityOverride;
        SerializedProperty m_Radius;
        SerializedProperty m_BlendDistance;

        void OnEnable()
        {
            m_CloudType = serializedObject.FindProperty("cloudType");
            m_Coverage = serializedObject.FindProperty("coverage");
            m_RainIntensity = serializedObject.FindProperty("rainIntensity");
            m_DensityOverride = serializedObject.FindProperty("densityOverride");
            m_Radius = serializedObject.FindProperty("radius");
            m_BlendDistance = serializedObject.FindProperty("blendDistance");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_CloudType);
            EditorGUILayout.PropertyField(m_Coverage);
            EditorGUILayout.PropertyField(m_RainIntensity);
            EditorGUILayout.PropertyField(m_DensityOverride);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(m_Radius);
            if (m_Radius.floatValue < 0.0f)
                m_Radius.floatValue = 0.0f;
            EditorGUILayout.PropertyField(m_BlendDistance);
            if (m_BlendDistance.floatValue < 0.0f)
                m_BlendDistance.floatValue = 0.0f;

            serializedObject.ApplyModifiedProperties();
        }

        static readonly Color k_RegionColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);
        static readonly Color k_BlendColor = new Color(0.2f, 0.6f, 1.0f, 0.35f);

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        static void DrawGizmosSelected(VolumetricCloudsRegion region, GizmoType gizmoType)
        {
            Vector3 center = region.transform.position;

            Handles.color = k_RegionColor;
            Handles.DrawWireDisc(center, Vector3.up, Mathf.Max(region.radius, 0.0f));

            if (region.blendDistance > 0.0f)
            {
                Handles.color = k_BlendColor;
                Handles.DrawWireDisc(center, Vector3.up, Mathf.Max(region.radius, 0.0f) + region.blendDistance);
            }
        }

        void OnSceneGUI()
        {
            var region = target as VolumetricCloudsRegion;
            Vector3 center = region.transform.position;

            // Drag handle for the core radius, drawn flat on the world XZ plane to match how the region
            // is evaluated (footprint projected on XZ, independent of height).
            EditorGUI.BeginChangeCheck();
            Handles.color = k_RegionColor;
            float newRadius = Handles.RadiusHandle(Quaternion.identity, center, region.radius, true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(region, "Change Volumetric Clouds Region Radius");
                region.radius = Mathf.Max(newRadius, 0.0f);
            }

            // Drag handle for the outer (radius + blend) circle.
            EditorGUI.BeginChangeCheck();
            Handles.color = k_BlendColor;
            float newBlendRadius = Handles.RadiusHandle(Quaternion.identity, center, region.radius + region.blendDistance, true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(region, "Change Volumetric Clouds Region Blend Distance");
                region.blendDistance = Mathf.Max(newBlendRadius - region.radius, 0.0f);
            }
        }
    }
}
