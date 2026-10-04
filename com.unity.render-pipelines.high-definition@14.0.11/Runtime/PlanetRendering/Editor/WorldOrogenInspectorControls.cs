using System;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed class WorldOrogenInspectorControls
    {
        string codeDraft,codeError;bool host,legacy;
        internal void Draw(SerializedObject serialized,PlanetGeneratorAsset asset)
        {
            var p=serialized.FindProperty("Orogen");
            if(p==null)return;
            EditorGUILayout.LabelField("World Orogen",EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serialized.FindProperty("Seed"));
            p.FindPropertyRelative("Seed").intValue=serialized.FindProperty("Seed").intValue;
            EditorGUILayout.LabelField("Shape",EditorStyles.boldLabel);
            var detail=p.FindPropertyRelative("Detail");
            EditorGUI.BeginChangeCheck();int slider=EditorGUILayout.IntSlider("Detail",WorldOrogenSettings.SliderFromDetail(detail.intValue),0,1000);
            if(EditorGUI.EndChangeCheck())detail.intValue=WorldOrogenSettings.DetailFromSlider(slider);
            EditorGUILayout.LabelField("Regions",detail.intValue.ToString("N0"));
            Slider(p,"Irregularity",0,1,.05);IntSlider(p,"Plates",4,120);IntSlider(p,"Continents",1,10);
            Slider(p,"Roughness",0,.5,.01);Slider(p,"ContinentSizeVariety",0,1,.05);Slider(p,"LandCoverage",0,1,.01);
            EditorGUILayout.Space();EditorGUILayout.LabelField("Terrain sculpting",EditorStyles.boldLabel);
            foreach(var name in new[]{"TerrainWarp","Smoothing","GlacialErosion","HydraulicErosion","ThermalErosion","RidgeSharpening"})Slider(p,name,0,1,.05);
            EditorGUILayout.Space();EditorGUILayout.LabelField("Climate",EditorStyles.boldLabel);
            Slider(p,"TemperatureOffset",-15,15,1);Slider(p,"PrecipitationOffset",-1,1,.1);
            EditorGUILayout.PropertyField(p.FindPropertyRelative("AutoClimate"),new GUIContent("Automatic climate"));
            EditorGUILayout.HelpBox("Like the original, automatic climate runs up to 300,000 regions. At higher Detail use Compute Climate explicitly.",MessageType.None);
            EditorGUILayout.PropertyField(p.FindPropertyRelative("ToggledPlateIndices"),new GUIContent("Edited ocean / land plates"),true);
            EditorGUILayout.Space();var mapView=serialized.FindProperty("MapView");
            mapView.intValue=EditorGUILayout.Popup("Map",mapView.intValue,new[]{"Terrain","Satellite","Climate","Full Heightmap"});
            var resolution=serialized.FindProperty("BaseMapResolution");int[] sizes={0,64,128,256,512,1024};
            int selected=Array.IndexOf(sizes,resolution.intValue);if(selected<0)selected=0;
            resolution.intValue=sizes[EditorGUILayout.Popup("Base map resolution",selected,new[]{"Automatic","64","128","256","512","1024"})];
            DrawCode(serialized,asset);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("Terrain detail", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serialized.FindProperty("TerrainDetail"), new GUIContent("Retained-map detail settings"), true);
            EditorGUILayout.HelpBox("Bake detail after the base map. Geometry follows captured relief and reference flow; rebuilding the base map requires rebaking detail. Changes here apply when baked.", MessageType.None);
            EditorGUILayout.Space();EditorGUILayout.LabelField("Planet placement",EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serialized.FindProperty("Radius"));EditorGUILayout.PropertyField(serialized.FindProperty("Orientation"));
            using(new EditorGUI.DisabledScope(true))EditorGUILayout.PropertyField(serialized.FindProperty("SurfaceData"),new GUIContent("Published base map"));
            var captured=asset.PublishedOrogen;
            if(captured!=null&&captured.ConfigurationDigest()!=asset.CaptureOrogen().ConfigurationDigest())
                EditorGUILayout.HelpBox("Settings differ from the published map. Generate or Reapply to update it.",MessageType.Info);
            host=EditorGUILayout.Foldout(host,"Render / atmosphere",true);
            if(host)foreach(var name in new[]{"MaximumLevel","PatchBudget","PixelError","PatchesPerFrame","Atmosphere","OceanSettings"})EditorGUILayout.PropertyField(serialized.FindProperty(name),true);
            legacy=EditorGUILayout.Foldout(legacy,"Retained terrain / material settings",true);
            if(legacy)
            {
                EditorGUILayout.HelpBox("These retained settings belong to earlier datasets and later terrain detail. World Orogen generates its base height and map independently.",MessageType.None);
                foreach(var name in new[]{"TerrainStyle","Relief","NativeMaterialSettings","NativeSurfaceSettings","ScatterSettings"})EditorGUILayout.PropertyField(serialized.FindProperty(name),true);
            }
        }
        void DrawCode(SerializedObject serialized,PlanetGeneratorAsset asset)
        {
            var current=asset.CaptureOrogen();
            if(codeDraft==null&&current.Validate(out _))codeDraft=WorldOrogenPlanetCode.Encode(current);
            EditorGUILayout.LabelField("Planet code",EditorStyles.boldLabel);
            codeDraft=EditorGUILayout.TextField(codeDraft??"");
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button("Copy current code")&&current.Validate(out _))
                {codeDraft=WorldOrogenPlanetCode.Encode(current);EditorGUIUtility.systemCopyBuffer=codeDraft;codeError=null;}
                if(GUILayout.Button("Apply code"))
                {
                    if(WorldOrogenPlanetCode.TryDecode(codeDraft,out var decoded,out codeError))
                    {serialized.ApplyModifiedProperties();Undo.RecordObject(asset,"Apply World Orogen planet code");asset.Orogen=decoded;asset.Seed=decoded.Seed;EditorUtility.SetDirty(asset);serialized.Update();GUI.changed=true;}
                }
            }
            if(!string.IsNullOrEmpty(codeError))EditorGUILayout.HelpBox(codeError,MessageType.Error);
        }
        static void IntSlider(SerializedProperty root,string name,int minimum,int maximum)
        {var field=root.FindPropertyRelative(name);field.intValue=EditorGUILayout.IntSlider(ObjectNames.NicifyVariableName(name),field.intValue,minimum,maximum);}
        static void Slider(SerializedProperty root,string name,double minimum,double maximum,double step)
        {
            var field=root.FindPropertyRelative(name);EditorGUI.BeginChangeCheck();float value=EditorGUILayout.Slider(ObjectNames.NicifyVariableName(name),(float)field.doubleValue,(float)minimum,(float)maximum);
            if(EditorGUI.EndChangeCheck())field.doubleValue=Math.Max(minimum,Math.Min(maximum,Math.Round(value/step)*step));
        }
    }
}
