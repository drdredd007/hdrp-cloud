using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    [CustomEditor(typeof(VolumetricCloudsRegion))]
    [CanEditMultipleObjects]
    class VolumetricCloudsRegionEditor : Editor
    {
        SerializedProperty m_AltoStratusCoverage;
        SerializedProperty m_CumulusCoverage;
        SerializedProperty m_CumulonimbusCoverage;
        SerializedProperty m_RainIntensity;
        SerializedProperty m_DensityOverride;
        SerializedProperty m_Storminess;
        SerializedProperty m_Radius;
        SerializedProperty m_BlendDistance;
        SerializedProperty m_AltitudeOverride;
        SerializedProperty m_RegionBottomAltitude;
        SerializedProperty m_RegionTopAltitude;
        SerializedProperty m_RainFog;
        SerializedProperty m_RainFogBottomAltitude;
        SerializedProperty m_RainFogMeanFreePath;
        SerializedProperty m_RainFogAlbedo;
        SerializedProperty m_RainFogVerticalFade;
        SerializedProperty m_RainFogBottomDensity;

        void OnEnable()
        {
            m_AltoStratusCoverage = serializedObject.FindProperty("altoStratusCoverage");
            m_CumulusCoverage = serializedObject.FindProperty("cumulusCoverage");
            m_CumulonimbusCoverage = serializedObject.FindProperty("cumulonimbusCoverage");
            m_RainIntensity = serializedObject.FindProperty("rainIntensity");
            m_DensityOverride = serializedObject.FindProperty("densityOverride");
            m_Storminess = serializedObject.FindProperty("storminess");
            m_Radius = serializedObject.FindProperty("radius");
            m_BlendDistance = serializedObject.FindProperty("blendDistance");
            m_AltitudeOverride = serializedObject.FindProperty("altitudeOverride");
            m_RegionBottomAltitude = serializedObject.FindProperty("regionBottomAltitude");
            m_RegionTopAltitude = serializedObject.FindProperty("regionTopAltitude");
            m_RainFog = serializedObject.FindProperty("rainFog");
            m_RainFogBottomAltitude = serializedObject.FindProperty("rainFogBottomAltitude");
            m_RainFogMeanFreePath = serializedObject.FindProperty("rainFogMeanFreePath");
            m_RainFogAlbedo = serializedObject.FindProperty("rainFogAlbedo");
            m_RainFogVerticalFade = serializedObject.FindProperty("rainFogVerticalFade");
            m_RainFogBottomDensity = serializedObject.FindProperty("rainFogBottomDensity");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Cloud Types", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_AltoStratusCoverage);
            EditorGUILayout.PropertyField(m_CumulusCoverage);
            EditorGUILayout.PropertyField(m_CumulonimbusCoverage);
            if (!m_CumulonimbusCoverage.hasMultipleDifferentValues && !m_CumulusCoverage.hasMultipleDifferentValues
                && !m_AltoStratusCoverage.hasMultipleDifferentValues && m_CumulonimbusCoverage.floatValue <= 0.0f
                && m_CumulusCoverage.floatValue <= 0.0f && m_AltoStratusCoverage.floatValue <= 0.0f)
                EditorGUILayout.HelpBox("All three coverages are 0: the region clears the clouds instead of adding any.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(m_RainIntensity);
            EditorGUILayout.PropertyField(m_DensityOverride);
            EditorGUILayout.PropertyField(m_Storminess);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(m_Radius);
            if (m_Radius.floatValue < 0.0f)
                m_Radius.floatValue = 0.0f;
            EditorGUILayout.PropertyField(m_BlendDistance);
            if (m_BlendDistance.floatValue < 0.0f)
                m_BlendDistance.floatValue = 0.0f;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Altitude Override", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_AltitudeOverride);
            using (new EditorGUI.DisabledScope(!m_AltitudeOverride.boolValue && !m_AltitudeOverride.hasMultipleDifferentValues))
            {
                EditorGUILayout.PropertyField(m_RegionBottomAltitude);
                EditorGUILayout.PropertyField(m_RegionTopAltitude);
            }
            if ((m_AltitudeOverride.boolValue || m_AltitudeOverride.hasMultipleDifferentValues)
                && !m_RegionTopAltitude.hasMultipleDifferentValues && !m_RegionBottomAltitude.hasMultipleDifferentValues
                && m_RegionTopAltitude.floatValue <= m_RegionBottomAltitude.floatValue)
                EditorGUILayout.HelpBox("Top Altitude must be greater than Bottom Altitude for the override to take effect; the region falls back to the Volume's altitude range otherwise.", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rain Fog", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_RainFog);
            using (new EditorGUI.DisabledScope(!m_RainFog.boolValue && !m_RainFog.hasMultipleDifferentValues))
            {
                EditorGUILayout.PropertyField(m_RainFogBottomAltitude);
                EditorGUILayout.PropertyField(m_RainFogMeanFreePath);
                EditorGUILayout.PropertyField(m_RainFogAlbedo);
                EditorGUILayout.PropertyField(m_RainFogVerticalFade);
                EditorGUILayout.PropertyField(m_RainFogBottomDensity);
            }
            if (m_RainFog.boolValue || m_RainFog.hasMultipleDifferentValues)
                EditorGUILayout.HelpBox("The column follows the cloud base and region radius. Density is multiplied by Rain Intensity and Coverage. Enable Volumetric Fog and set Fog > Depth Extent and Camera > Far Clip Plane to reach the background regions. Increasing the distance reduces depth precision; adjust volumetric quality as needed.", MessageType.Info);

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
