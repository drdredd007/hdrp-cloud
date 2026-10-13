using System;
using Unity.Mathematics;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>One observer's HDRP WaterSurface; authored templates and transforms stay unchanged.</summary>
    public sealed class PlanetHdrpWaterRenderer : IDisposable
    {
        readonly PlanetFarPass owner;
        readonly PlanetaryWaterRendering state = new PlanetaryWaterRendering();
        GameObject host, template;
        WaterSurface surface;
        string templateJson;
        PlanetSurfaceGpuData shoreline;
        RenderTexture metricDepth;
        bool disposed;
        MaterialPropertyBlock shorelineProperties;
        Material opaqueDepthMaterial;
        MaterialPropertyBlock opaqueDepthProperties;
        PlanetLayerDepth opaqueDepth;
        public bool Active => surface && surface.enabled && state.Matches(owner.Observer);
        public WaterSurface Surface => surface;
        public RenderTexture MetricDepth => state.RenderedFrame >= 0 ? metricDepth : null;
        public string Status { get; private set; } = "Assign an HDRP WaterSurface template";
        public PlanetHdrpWaterRenderer(PlanetFarPass owner)
        {
            this.owner = owner;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            PlanetaryWaterRendering.MetricDepthReady += CaptureDepth;
            PlanetaryWaterRendering.BindOpaqueDepth += BindOpaqueDepth;
        }
        void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (disposed || camera != owner.Observer) return;
            // A disabled camera frame setting/volume must not republish the previous render's endpoint.
            state.ResetHistory();
            opaqueDepth=null;
            if (!owner.Enabled || !owner.HdrpWaterTemplate || !owner.Definition.IsValid || owner.Definition.GeneratorVersion != 3)
            { ReleaseSurface(); return; }
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            { Status = "Spherical HDRP water currently requires Direct3D 11"; ReleaseSurface(); return; }
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset pipeline) || !pipeline.currentPlatformRenderPipelineSettings.supportWater)
            { Status = "Enable Water in the active HDRP asset"; ReleaseSurface(); return; }
            var source = owner.HdrpWaterTemplate.GetComponentInChildren<WaterSurface>(true);
            if (!source) { Status = "The selected template has no WaterSurface"; ReleaseSurface(); return; }
            if (!PlanetSurfaceDataRegistry.TryAcquire(owner.Definition.Surface, out var lease))
            { Status = "Waiting for the planet surface"; ReleaseSurface(); return; }
            double radius, seaLevel;
            using (lease)
            {
                var recipe = lease.View.Recipe;
                if (!PlanetOceanMath.TryRadius(recipe, out radius)) { Status = "This world has no ocean"; ReleaseSurface(); return; }
                seaLevel = recipe.SeaLevel;
            }
            try
            {
                if (template != owner.HdrpWaterTemplate || !surface)
                {
                    ReleaseSurface(); template = owner.HdrpWaterTemplate;
                    host = new GameObject("Planet HDRP water observer") { hideFlags = HideFlags.HideAndDontSave };
                    if (camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded) SceneManager.MoveGameObjectToScene(host, camera.gameObject.scene);
                    surface = host.AddComponent<WaterSurface>();
                }
                string nextJson = JsonUtility.ToJson(source);
                if (nextJson != templateJson)
                { JsonUtility.FromJsonOverwrite(nextJson, surface); templateJson = nextJson; }
                if (shoreline == null || !shoreline.Key.Equals(owner.Definition.Surface))
                {
                    var next = PlanetSurfaceGpuData.Acquire(owner.Definition);
                    var previous = shoreline; shoreline = next; PlanetSurfaceGpuData.Release(previous);
                }
                state.Observer = camera; state.CameraFromCenter = owner.CameraPosition - owner.Definition.Center;
                state.Radius = radius; state.SeaLevel = seaLevel; state.Rotation = owner.PlanetRotation;
                // The shared canonical sampler is only enabled for global Foundation roots.
                if(shorelineProperties==null)shorelineProperties=new MaterialPropertyBlock();
                state.Shoreline = shoreline.BindCanonicalShoreline(shorelineProperties);
                state.BindShoreline = BindShoreline;
                surface.geometryType = WaterGeometryType.Planetary; surface.planetaryRendering = state; surface.enabled = true;
                Status = state.RenderedFrame < 0 ? "Preparing HDRP water simulation" : "HDRP spherical water active";
            }
            catch (Exception error) { Status = error.Message; ReleaseSurface(); }
        }
        void BindShoreline(MaterialPropertyBlock properties)
        {
            shoreline?.BindCanonicalShoreline(properties);
            properties.SetTexture("_PlanetWaterOpaqueEndpoints",owner.LayerDepth?.Current ? (Texture)owner.LayerDepth.Current : Texture2D.blackTexture);
            properties.SetTexture("_PlanetWaterOwnCoarseEndpoints",owner.OceanCoarseTerrainDistance ? (Texture)owner.OceanCoarseTerrainDistance : Texture2D.blackTexture);
            properties.SetTexture("_WaterPlanetOpaqueDepth",opaqueDepth?.Current ? (Texture)opaqueDepth.Current : Texture2D.blackTexture);
        }
        void BindOpaqueDepth(Camera camera,CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            if(!disposed&&camera==owner.Observer&&Active&&opaqueDepth?.Current)
                cmd.SetComputeTextureParam(shader,kernel,"_WaterPlanetOpaqueDepth",opaqueDepth.Current);
        }
        internal void PrepareOpaqueDepth(CustomPassContext ctx,PlanetLayerDepth depth)
        {
            if(!Active||depth?.Current==null)return;
            opaqueDepth=depth;
            if(!state.Shoreline||!owner.OceanCoarseTerrainDistance)return;
            if(!opaqueDepthMaterial)opaqueDepthMaterial=CoreUtils.CreateEngineMaterial(Resources.Load<Shader>("PlanetOcean"));
            if(opaqueDepthProperties==null)opaqueDepthProperties=new MaterialPropertyBlock();
            var properties=opaqueDepthProperties;properties.Clear();shoreline.BindCanonicalShoreline(properties);
            var origin=owner.CameraPosition-owner.Definition.Center;float3 high=(float3)origin,low=(float3)(origin-(double3)high);
            properties.SetVector("_OceanOriginHigh",new Vector4(high.x,high.y,high.z,0));
            properties.SetVector("_OceanOriginLow",new Vector4(low.x,low.y,low.z,0));
            double radius=state.Radius,altitude=state.Altitude,sea=state.SeaLevel;
            properties.SetVector("_OceanDimensionsHigh",new Vector4((float)radius,(float)altitude,(float)sea,0));
            properties.SetVector("_OceanDimensionsLow",new Vector4((float)(radius-(double)(float)radius),(float)(altitude-(double)(float)altitude),(float)(sea-(double)(float)sea),0));
            var q=Quaternion.Inverse(owner.PlanetRotation);
            properties.SetVector("_OceanWorldToLocal",new Vector4(q.x,q.y,q.z,q.w));
            properties.SetInt("_OceanShorelineEnabled",1);
            properties.SetTexture("_OceanBuffer",Texture2D.blackTexture);
            properties.SetTexture("_OceanCoarseTerrainDistance",owner.OceanCoarseTerrainDistance);
            // The legacy merge already refines a root's chord when no opaque sea
            // is submitted. Reuse it for both dry terrain and submerged seabed.
            depth.Merge(ctx,opaqueDepthMaterial,properties);
        }
        void CaptureDepth(Camera camera, CommandBuffer cmd, RenderTexture source)
        {
            if (disposed || !Active || state.RenderedFrame < 0 || camera != owner.Observer) return;
            int width = source.width, height = source.height;
            if (!metricDepth || metricDepth.width != width || metricDepth.height != height)
            {
                CoreUtils.Destroy(metricDepth);
                metricDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { name = "HDRP planetary water metric depth", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                metricDepth.Create();
            }
            cmd.CopyTexture(source, 0, 0, metricDepth, 0, 0);
            Status = "HDRP spherical water active";
        }
        void ReleaseSurface()
        {
            if (host) CoreUtils.Destroy(host);
            host = null; surface = null; template = null; templateJson = null;
            PlanetSurfaceGpuData.Release(shoreline); shoreline = null;
            CoreUtils.Destroy(metricDepth); metricDepth = null;
            state.ResetHistory();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            PlanetaryWaterRendering.MetricDepthReady -= CaptureDepth;
            PlanetaryWaterRendering.BindOpaqueDepth -= BindOpaqueDepth;
            CoreUtils.Destroy(opaqueDepthMaterial);opaqueDepthMaterial=null;opaqueDepth=null;
            ReleaseSurface();
        }
    }
}
