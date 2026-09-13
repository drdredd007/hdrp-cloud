using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    [CustomEditor(typeof(PlanetSiteAsset))]
    public sealed class PlanetSiteAssetEditor : UnityEditor.Editor
    {
        PreviewRenderUtility preview;
        Mesh cube;
        readonly List<Mesh> ground=new List<Mesh>();
        Material[] materials;
        PlanetSiteLayout layout;
        Vector2 orbit=new Vector2(-25,35);
        float zoom=1;
        string signature,error;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();DrawPropertiesExcluding(serializedObject,"m_Script");
            if(serializedObject.ApplyModifiedProperties())signature=null;
            EditorGUILayout.HelpBox("Metre dimensions. Address and heading use the generator's surface frame. Drag the preview to orbit; scroll to zoom.",MessageType.Info);
            if(GUILayout.Button("Refresh preview"))signature=null;
            if(!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Error);
        }
        public override bool HasPreviewGUI()=>true;
        public override GUIContent GetPreviewTitle()=>new GUIContent("Planet site");
        public override void OnPreviewSettings(){if(GUILayout.Button("Reset",EditorStyles.miniButton)){orbit=new Vector2(-25,35);zoom=1;}}
        public override void OnInteractivePreviewGUI(Rect rect,GUIStyle background)=>OnPreviewGUI(rect,background);
        public override void OnPreviewGUI(Rect rect,GUIStyle background)
        {
            var site=(PlanetSiteAsset)target;
            var e=Event.current;
            if(e.type==EventType.MouseDrag && rect.Contains(e.mousePosition)){orbit+=e.delta*.4f;orbit.y=math.clamp(orbit.y,5,85);e.Use();Repaint();}
            if(e.type==EventType.ScrollWheel && rect.Contains(e.mousePosition)){zoom=math.clamp(zoom*math.pow(1.1f,e.delta.y),.25f,4);e.Use();Repaint();}
            if(e.type!=EventType.Repaint)return;
            try
            {
                if(!site.Generator){GUI.Label(rect,"Assign a Planet Generator.");return;}
                if(!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)){GUI.Label(rect,"HDRP is required for this preview.");return;}
                string current=EditorJsonUtility.ToJson(site)+EditorJsonUtility.ToJson(site.Generator);
                if(preview==null || signature!=current){Rebuild(site);signature=current;}
                preview.BeginPreview(rect,background);
                Texture image=null;
                try
                {
                var pivot=new Vector3(0,20,45)*site.Settings.Scale;
                var rotation=Quaternion.Euler(orbit.y,orbit.x,0);
                preview.camera.transform.SetPositionAndRotation(pivot+rotation*new Vector3(0,0,-360*site.Settings.Scale*zoom),rotation);
                foreach(var mesh in ground)preview.DrawMesh(mesh,Matrix4x4.identity,materials[3],0);
                foreach(var part in layout.Parts)preview.DrawMesh(cube,Matrix4x4.TRS(part.Center,Quaternion.identity,part.Size),materials[part.Material],0);
                preview.Render(true);
                }
                finally {image=preview.EndPreview();}
                GUI.DrawTexture(rect,image,ScaleMode.StretchToFill,false);error=null;
            }
            catch(Exception exception){error=exception.Message;Release();GUI.Label(rect,error);}
        }
        void Rebuild(PlanetSiteAsset site)
        {
            Release();layout=site.Build();
            var shader=Shader.Find("HDRP/Lit");if(!shader)throw new InvalidOperationException("HDRP/Lit shader unavailable.");
            materials=new Material[4];var colors=new[]{new Color(.35f,.4f,.46f),new Color(.12f,.15f,.18f),new Color(.65f,.68f,.7f),new Color(.25f,.29f,.2f)};
            for(int i=0;i<4;i++){materials[i]=new Material(shader){hideFlags=HideFlags.HideAndDontSave};materials[i].SetColor("_BaseColor",colors[i]);}
            cube=PlanetSiteLayout.CreateUnitBoxMesh();cube.hideFlags=HideFlags.HideAndDontSave;
            for(int i=0;i<4;i++)using(var patch=PlanetLocalPatch.Build(site.Generator.Definition,site.Address,new int2(i%2-1,i/2-1),256,16))
            {
                var vertices=new Vector3[patch.Positions.Length];var normals=new Vector3[vertices.Length];var triangles=new int[patch.Triangles.Length*3];
                for(int v=0;v<vertices.Length;v++){vertices[v]=patch.Positions[v];normals[v]=patch.Normals[v];}
                for(int t=0;t<patch.Triangles.Length;t++){var p=patch.Triangles[t];triangles[t*3]=p.x;triangles[t*3+1]=p.y;triangles[t*3+2]=p.z;}
                var mesh=new Mesh {name="Site preview ground",hideFlags=HideFlags.HideAndDontSave};mesh.vertices=vertices;mesh.normals=normals;mesh.triangles=triangles;mesh.RecalculateBounds();ground.Add(mesh);
            }
            preview=new PreviewRenderUtility();preview.camera.fieldOfView=45;preview.camera.nearClipPlane=.1f;preview.camera.farClipPlane=5000;
            var hd=preview.camera.GetComponent<HDAdditionalCameraData>();if(!hd)hd=preview.camera.gameObject.AddComponent<HDAdditionalCameraData>();hd.volumeLayerMask=0;hd.customRenderingSettings=true;
            hd.clearColorMode=HDAdditionalCameraData.ClearColorMode.Color;hd.backgroundColorHDR=new Color(.035f,.04f,.05f);
            foreach(var flag in new[]{FrameSettingsField.ExposureControl,FrameSettingsField.Postprocess,FrameSettingsField.AtmosphericScattering})
            {hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)flag]=true;hd.renderingPathCustomFrameSettings.SetEnabled(flag,false);}
            preview.lights[0].type=LightType.Directional;preview.lights[0].transform.rotation=Quaternion.Euler(45,-30,0);
            var light=preview.lights[0].GetComponent<HDAdditionalLightData>();if(!light)light=preview.lights[0].gameObject.AddComponent<HDAdditionalLightData>();
            light.SetIntensity(4,LightUnit.Lux);preview.lights[1].intensity=0;
        }
        void OnDisable()=>Release();
        void Release()
        {
            preview?.Cleanup();preview=null;
            if(cube)DestroyImmediate(cube);cube=null;
            foreach(var mesh in ground)if(mesh)DestroyImmediate(mesh);ground.Clear();
            if(materials!=null)foreach(var material in materials)if(material)DestroyImmediate(material);materials=null;
            layout=null;signature=null;
        }
    }
}
