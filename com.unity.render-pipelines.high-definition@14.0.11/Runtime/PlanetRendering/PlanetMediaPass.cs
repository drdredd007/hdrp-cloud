using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    // One owner per rendering camera, independent of celestial body count. The pass runs
    // after raw opaque planet layers and integrates only the medium before each endpoint.
    public sealed class PlanetMediaPass : CustomPass
    {
        public Camera Observer;
        public PlanetLayerDepth LayerDepth;
        public readonly List<PlanetMediaBody> Bodies = new List<PlanetMediaBody>();
        public bool Enabled = true;
        // AIR remains independent for every body. The selected planet uses native HDRP
        // cloud density/lighting and fog instead of the bounded weather approximation.
        public bool NativeWeather;
        public bool NativeWeatherAtmosphere = true;
        [Range(1, 4)] public int ResolutionDivisor = 2;
        [Range(8, 256)] public int RaySteps = 64;
        [Range(1, 16)] public int LightSteps = 4;
        [Range(8, 512)] public int CoverageResolution = 256;
        [Range(1, 512)] public int CoverageCacheMiB = 64;
        public bool TransportTransparents = true;
        [Range(16, 256)] public int TransparentGridHeight = 96;
        [Range(8, 64)] public int TransparentDepthSlices = 32;
        [Range(8, 128)] public int TransparentRaySteps = 32;
        public bool UseSceneLights = true;
        public Quaternion CelestialLightRotation = Quaternion.identity;
        public Vector3 LightDirection = new Vector3(.4f, .7f, -.58f).normalized;
        public Color LightColor = Color.white;
        public float LightLux = 120000;
        public RenderTexture ScatteringBuffer => scattering;
        public RenderTexture TransmittanceBuffer => transmission;
        public int UploadedBodyCount { get; private set; }
        public long CoverageCacheBytes => textures.AllocatedBytes;
        public int CoverageBakeCount => textures.BakeCount;
        public RenderTexture TransparentScatteringBuffer => transparentScattering;
        public RenderTexture TransparentTransmittanceBuffer => transparentTransmission;
        PlanetImposterPass imposters;
        public PlanetImposterPass Imposters
        {
            get => imposters;
            set
            {
                if (imposters != null) imposters.DeferredTransport = false;
                imposters = value;
                if (imposters != null) imposters.DeferredTransport = true;
            }
        }
        Camera registeredCamera;
        ComputeShader integrator;
        int kernel, bakeKernel, transparentKernel, reconstructKernel;
        Material composite;
        GraphicsBuffer bodyBuffer, curveBuffer, ambientBuffer, regionBuffer;
        readonly List<PlanetMediaGpuRegion> regions = new List<PlanetMediaGpuRegion>();
        int regionCapacity;
        PlanetMediaGpuBody[] bodyData;
        PlanetMediaTextureSource[] textureSources;
        Vector4[] curveData;
        readonly Vector4[] ambientData = new Vector4[7];
        readonly Plane[] frustumPlanes = new Plane[6];
        PlanetMediaWeatherSnapshot weather;
        readonly PlanetMediaTextureCache textures = new PlanetMediaTextureCache();
        RenderTexture scattering, transmission;
        RenderTexture lowScattering, lowTransmission;
        RenderTexture transparentScattering, transparentTransmission;
        Vector4 transparentDistance, transparentGrid;
        int capacity;

        public void Activate()
        {
            if (registeredCamera != Observer) Deactivate();
            if (!Enabled || !Observer) { Deactivate(); return; }
            registeredCamera = Observer;
            PlanetMediaCameraRegistry.Register(registeredCamera, this, NativeWeather);
            if(NativeWeather)PlanetMediaCameraRegistry.SetNativeWeatherLighting(registeredCamera,this,UseSceneLights,
                NativeWeatherAtmosphere,CelestialLightRotation,LightDirection,LightColor,LightLux);
            transparentDistance = transparentGrid = Vector4.zero;
            if ((TransportTransparents || NativeWeather) && !Observer.orthographic)
            {
                int height = Mathf.Min(Mathf.Max(1, Observer.pixelHeight), Mathf.Clamp(TransparentGridHeight, 16, 256));
                int width = Mathf.Min(Mathf.Max(1, Observer.pixelWidth), Mathf.Max(1, Mathf.CeilToInt(height * Observer.aspect)));
                var projection = Observer.projectionMatrix;
                float tanX = (1 + Mathf.Abs(projection.m02)) / Mathf.Max(.001f, Mathf.Abs(projection.m00));
                float tanY = (1 + Mathf.Abs(projection.m12)) / Mathf.Max(.001f, Mathf.Abs(projection.m11));
                float near = Mathf.Max(.01f, Observer.nearClipPlane * .25f);
                float far = Mathf.Max(near * 2, Observer.farClipPlane * Mathf.Sqrt(1 + tanX * tanX + tanY * tanY));
                transparentDistance = new Vector4(near, 1 / Mathf.Log(far / near, 2), Mathf.Clamp(TransparentDepthSlices, 8, 64), NativeWeather?(TransportTransparents?2:3):1);
                transparentGrid = new Vector4(width, height, 1f / width, 1f / height);
                if (!math.all(math.isfinite((float4)transparentDistance)) || transparentDistance.y <= 0)
                    transparentDistance = transparentGrid = Vector4.zero;
            }
            PlanetMediaCameraRegistry.SetTransparentTransport(registeredCamera, this, transparentDistance, transparentGrid);
        }
        public void Deactivate()
        {
            PlanetMediaCameraRegistry.Unregister(registeredCamera, this);
            registeredCamera = null;
        }
        protected override void Setup(ScriptableRenderContext context, CommandBuffer cmd)
        {
            integrator = Resources.Load<ComputeShader>("PlanetMediaIntegrator");
            var shader = Resources.Load<Shader>("PlanetMediaComposite");
            if (!integrator || !shader) throw new InvalidOperationException("Planet media shaders are unavailable.");
            kernel = integrator.FindKernel("IntegrateMedia");
            bakeKernel = integrator.FindKernel("BakeMediaCoverage");
            transparentKernel = integrator.FindKernel("BakeTransparentPrefix");
            reconstructKernel = integrator.FindKernel("ReconstructMedia");
            composite = CoreUtils.CreateEngineMaterial(shader);
            weather = new PlanetMediaWeatherSnapshot();
            ambientBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 7, 16);
        }
        void EnsureCapacity(int count)
        {
            if (capacity >= Math.Max(count, 1)) return;
            bodyBuffer?.Dispose(); curveBuffer?.Dispose();
            capacity = Mathf.NextPowerOfTwo(Math.Max(count, 1));
            bodyData = new PlanetMediaGpuBody[capacity];
            textureSources = new PlanetMediaTextureSource[capacity];
            curveData = new Vector4[capacity * PlanetMediaMath.CurveSamples];
            bodyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, Marshal.SizeOf<PlanetMediaGpuBody>());
            curveBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, curveData.Length, 16);
        }
        void EnsureTargets(int width, int height, int divisor)
        {
            if (!scattering || scattering.width != width || scattering.height != height)
            {
                CoreUtils.Destroy(scattering); CoreUtils.Destroy(transmission);
                scattering = MakeTarget(width, height, "Planet medium in-scattering + opaque endpoint");
                transmission = MakeTarget(width, height, "Planet medium RGB transmittance");
            }
            int lowWidth = (width + divisor - 1) / divisor, lowHeight = (height + divisor - 1) / divisor;
            if (divisor == 1)
            {
                CoreUtils.Destroy(lowScattering); CoreUtils.Destroy(lowTransmission);
                lowScattering = lowTransmission = null;
            }
            else if (!lowScattering || lowScattering.width != lowWidth || lowScattering.height != lowHeight)
            {
                CoreUtils.Destroy(lowScattering); CoreUtils.Destroy(lowTransmission);
                lowScattering = MakeTarget(lowWidth, lowHeight, "Planet medium reduced in-scattering + endpoint");
                lowTransmission = MakeTarget(lowWidth, lowHeight, "Planet medium reduced RGB transmittance");
            }
        }
        static RenderTexture MakeTarget(int width, int height, string label)
        {
            var target = new RenderTexture(width, height, 0, GraphicsFormat.R32G32B32A32_SFloat)
            { name = label, enableRandomWrite = true, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            target.Create(); return target;
        }
        void EnsureTransparentTargets()
        {
            if (transparentDistance.w <= 0)
            {
                CoreUtils.Destroy(transparentScattering); CoreUtils.Destroy(transparentTransmission);
                transparentScattering = transparentTransmission = null; return;
            }
            int width = (int)transparentGrid.x, height = (int)transparentGrid.y, slices = (int)transparentDistance.z;
            if (transparentScattering && transparentScattering.width == width && transparentScattering.height == height && transparentScattering.volumeDepth == slices) return;
            CoreUtils.Destroy(transparentScattering); CoreUtils.Destroy(transparentTransmission);
            transparentScattering = MakeTransparentTarget(width, height, slices, "Planet transparent prefix in-scattering");
            transparentTransmission = MakeTransparentTarget(width, height, slices, "Planet transparent prefix transmittance");
        }
        static RenderTexture MakeTransparentTarget(int width, int height, int slices, string label)
        {
            var target = new RenderTexture(width, height, 0, GraphicsFormat.R16G16B16A16_SFloat)
            { dimension = TextureDimension.Tex3D, volumeDepth = slices, enableRandomWrite = true, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp, name = label, hideFlags = HideFlags.HideAndDontSave };
            target.Create(); return target;
        }
        void BindMediaCompute(CommandBuffer cmd, HDRenderPipeline pipeline, int targetKernel)
        {
            cmd.SetComputeBufferParam(integrator, targetKernel, "_PlanetMediaBodies", bodyBuffer);
            cmd.SetComputeBufferParam(integrator, targetKernel, "_PlanetMediaCurves", curveBuffer);
            cmd.SetComputeBufferParam(integrator, targetKernel, "_PlanetMediaAmbient", ambientBuffer);
            cmd.SetComputeBufferParam(integrator, targetKernel, "_PlanetMediaCoverage", textures.Atlas);
            cmd.SetComputeBufferParam(integrator, targetKernel, "_PlanetMediaRegions", regionBuffer);
            cmd.SetComputeTextureParam(integrator, targetKernel, "_PlanetMediaShapeNoise", pipeline.PlanetMediaShapeNoise);
            cmd.SetComputeTextureParam(integrator, targetKernel, "_PlanetMediaWorleyNoise", pipeline.PlanetMediaWorleyErosion);
            cmd.SetComputeTextureParam(integrator, targetKernel, "_PlanetMediaPerlinNoise", pipeline.PlanetMediaPerlinErosion);
            pipeline.BindPlanetMediaLights(cmd, integrator, targetKernel);
        }
        void Publish(CommandBuffer cmd, HDRenderPipeline pipeline)
        {
            pipeline.BindPlanetCelestialLights(cmd, Observer);
            cmd.SetGlobalBuffer("_PlanetMediaBodies", bodyBuffer);
            cmd.SetGlobalBuffer("_PlanetMediaCurves", curveBuffer);
            cmd.SetGlobalBuffer("_PlanetMediaAmbient", ambientBuffer);
            cmd.SetGlobalBuffer("_PlanetMediaCoverage", textures.Atlas);
            cmd.SetGlobalBuffer("_PlanetMediaRegions", regionBuffer);
            cmd.SetGlobalInt("_PlanetMediaBodyCount", UploadedBodyCount);
            cmd.SetGlobalInt("_PlanetMediaRaySteps", Mathf.Clamp(RaySteps, 8, 256));
            cmd.SetGlobalInt("_PlanetMediaLightSteps", Mathf.Clamp(LightSteps, 1, 16));
            cmd.SetGlobalInt("_PlanetMediaUseSceneLights", UseSceneLights ? 1 : 0);
            cmd.SetGlobalVector("_PlanetMediaLightRotation", new Vector4(CelestialLightRotation.x, CelestialLightRotation.y, CelestialLightRotation.z, CelestialLightRotation.w));
            cmd.SetGlobalVector("_PlanetMediaFallbackDirection", new Vector4(LightDirection.normalized.x, LightDirection.normalized.y,
                LightDirection.normalized.z, Mathf.Max(0, LightLux)));
            cmd.SetGlobalColor("_PlanetMediaFallbackColor", LightColor.linear);
            cmd.SetGlobalTexture("_PlanetMediaShapeNoise", pipeline.PlanetMediaShapeNoise);
            cmd.SetGlobalTexture("_PlanetMediaWorleyNoise", pipeline.PlanetMediaWorleyErosion);
            cmd.SetGlobalTexture("_PlanetMediaPerlinNoise", pipeline.PlanetMediaPerlinErosion);
        }
        protected override void Execute(CustomPassContext ctx)
        {
            if (!Enabled || ctx.hdCamera.camera != Observer || !Observer || Observer.orthographic) return;
            var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
            if (pipeline == null) return;
            // Publish the minimum distance across the complete celestial layer set. A
            // last-body far buffer cannot clip native weather against all foregrounds.
            LayerDepth?.Publish(ctx.cmd);
            EnsureCapacity(Bodies.Count);
            regions.Clear();
            var cameraView = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.Rotate(Quaternion.Inverse(Observer.transform.rotation));
            GeometryUtility.CalculateFrustumPlanes(Observer.projectionMatrix * cameraView, frustumPlanes);
            UploadedBodyCount = 0;
            foreach (var authoredBody in Bodies)
            {
                var body=authoredBody;
                if(NativeWeather){body.Clouds=false;body.Fog=false;}
                if (!body.Definition.IsValid || !math.all(math.isfinite(body.CameraPosition))) continue;
                var relative = body.Definition.Center - body.CameraPosition;
                // Above this range float ray products become unrepresentable. Such a body with an
                // ordinary planet radius is far below the projected pixel footprint as well.
                if (!math.all(math.isfinite(relative)) || math.cmax(math.abs(relative)) > 1e18 || body.Definition.Radius > 1e18) continue;
                weather.Resolve(ctx.hdCamera.volumeStack, body.WeatherProfile);
                int curveOffset = UploadedBodyCount * PlanetMediaMath.CurveSamples;
                var packed = weather.Pack(body, curveOffset, Time.realtimeSinceStartup);
                if (!PlanetMediaMath.IsFinite(packed)) continue;
                int regionStart = regions.Count;
                float defaultBottom = packed.Limits.y, defaultTop = packed.Limits.z;
                HDRenderPipeline.CollectPlanetMediaRegions(body.Definition.Id, body.MinimumCloudAltitude, defaultBottom, defaultTop,
                    packed.CloudLighting.w > 0, packed.FogDensity.x > 0, regions);
                for (int i = regionStart; i < regions.Count; ++i)
                {
                    packed.Limits.y = Mathf.Min(packed.Limits.y, regions[i].Shape.y);
                    packed.Limits.z = Mathf.Max(packed.Limits.z, regions[i].Shape.z);
                    if (regions[i].RainOptical.w > 0) packed.Limits.w = Mathf.Max(packed.Limits.w, regions[i].Rain.y);
                }
                packed.RegionMetadata = new Vector4(regionStart, regions.Count - regionStart, defaultBottom, defaultTop);
                if (math.cmax((float4)packed.Limits) <= 0) continue;
                float outerRadius = packed.CenterRadius.w + Mathf.Max(Mathf.Max(packed.Limits.x, packed.Limits.z), packed.Limits.w);
                if (!math.isfinite(outerRadius) || outerRadius > 1e18f)
                { regions.RemoveRange(regionStart, regions.Count - regionStart); continue; }
                float guard = 4 * new Vector3(packed.CenterRadius.x, packed.CenterRadius.y, packed.CenterRadius.z).magnitude /
                    Mathf.Max(1, ctx.hdCamera.actualHeight);
                if (!PlanetMediaMath.IntersectsFrustum(frustumPlanes, (Vector3)(float3)relative, outerRadius + guard))
                { regions.RemoveRange(regionStart, regions.Count - regionStart); continue; }
                bodyData[UploadedBodyCount] = packed;
                textureSources[UploadedBodyCount++] = weather.TextureSource(pipeline);
                weather.SampleCurves(curveData, curveOffset);
            }
            textures.Prepare(bodyData, textureSources, UploadedBodyCount, CoverageResolution, CoverageCacheMiB,
                ctx.hdCamera.actualHeight, Observer.fieldOfView);
            if (regionCapacity < Mathf.Max(1, regions.Count))
            {
                regionBuffer?.Dispose(); regionCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, regions.Count));
                regionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, regionCapacity, Marshal.SizeOf<PlanetMediaGpuRegion>());
            }
            if (regions.Count > 0) ctx.cmd.SetBufferData(regionBuffer, regions);
            if (UploadedBodyCount > 0)
            {
                ctx.cmd.SetBufferData(bodyBuffer, bodyData, 0, 0, UploadedBodyCount);
                ctx.cmd.SetBufferData(curveBuffer, curveData, 0, 0, UploadedBodyCount * PlanetMediaMath.CurveSamples);
            }
            textures.Bake(ctx.cmd, integrator, bakeKernel, bodyBuffer);
            pipeline.FillPlanetMediaAmbientProbe(ctx.hdCamera, ambientData);
            ctx.cmd.SetBufferData(ambientBuffer, ambientData);
            Publish(ctx.cmd, pipeline);
            int divisor = Mathf.Clamp(ResolutionDivisor, 1, 4);
            EnsureTargets(ctx.hdCamera.actualWidth, ctx.hdCamera.actualHeight, divisor);
            ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaBodyCount", UploadedBodyCount);
            ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaRaySteps", Mathf.Clamp(RaySteps, 8, 256));
            ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaLightSteps", Mathf.Clamp(LightSteps, 1, 16));
            ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaUseSceneLights", UseSceneLights ? 1 : 0);
            ctx.cmd.SetComputeVectorParam(integrator, "_PlanetMediaLightRotation", new Vector4(CelestialLightRotation.x, CelestialLightRotation.y, CelestialLightRotation.z, CelestialLightRotation.w));
            ctx.cmd.SetComputeVectorParam(integrator, "_PlanetMediaFallbackDirection", new Vector4(LightDirection.normalized.x,
                LightDirection.normalized.y, LightDirection.normalized.z, Mathf.Max(0, LightLux)));
            ctx.cmd.SetComputeVectorParam(integrator, "_PlanetMediaFallbackColor", LightColor.linear);
            BindMediaCompute(ctx.cmd, pipeline, kernel);
            ctx.cmd.SetComputeTextureParam(integrator, kernel, "_PlanetMediaOpaqueDepth", ctx.cameraDepthBuffer);
            ctx.cmd.SetComputeTextureParam(integrator, kernel, "_PlanetMediaLayerDepth", LayerDepth?.Current ? (Texture)LayerDepth.Current : Texture2D.blackTexture);
            ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaHasLayerDepth", LayerDepth?.Current ? 1 : 0);
            var integrationL = divisor == 1 ? scattering : lowScattering;
            var integrationT = divisor == 1 ? transmission : lowTransmission;
            var outputSize = new Vector4(integrationL.width, integrationL.height, 1f / integrationL.width, 1f / integrationL.height);
            ctx.cmd.SetComputeVectorParam(integrator, "_PlanetMediaOutputSize", outputSize);
            ctx.cmd.SetComputeTextureParam(integrator, kernel, "_PlanetMediaScatteringRW", integrationL);
            ctx.cmd.SetComputeTextureParam(integrator, kernel, "_PlanetMediaTransmissionRW", integrationT);
            ctx.cmd.DispatchCompute(integrator, kernel, (integrationL.width + 7) / 8, (integrationL.height + 7) / 8, 1);
            if (divisor != 1)
            {
                BindMediaCompute(ctx.cmd, pipeline, reconstructKernel);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaOpaqueDepth", ctx.cameraDepthBuffer);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaLayerDepth", LayerDepth?.Current ? (Texture)LayerDepth.Current : Texture2D.blackTexture);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaLowScattering", lowScattering);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaLowTransmission", lowTransmission);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaScatteringRW", scattering);
                ctx.cmd.SetComputeTextureParam(integrator, reconstructKernel, "_PlanetMediaTransmissionRW", transmission);
                ctx.cmd.DispatchCompute(integrator, reconstructKernel, (scattering.width + 7) / 8, (scattering.height + 7) / 8, 1);
            }
            EnsureTransparentTargets();
            if (transparentDistance.w > 0)
            {
                BindMediaCompute(ctx.cmd, pipeline, transparentKernel);
                ctx.cmd.SetComputeIntParam(integrator, "_PlanetMediaTransparentRaySteps", Mathf.Clamp(TransparentRaySteps, 8, 128));
                ctx.cmd.SetComputeTextureParam(integrator, transparentKernel, "_PlanetMediaPrefixScatteringRW", transparentScattering);
                ctx.cmd.SetComputeTextureParam(integrator, transparentKernel, "_PlanetMediaPrefixTransmissionRW", transparentTransmission);
                ctx.cmd.DispatchCompute(integrator, transparentKernel, ((int)transparentGrid.x + 7) / 8, ((int)transparentGrid.y + 7) / 8, 1);
                ctx.cmd.SetGlobalTexture("_PlanetMediaTransparentScattering", transparentScattering);
                ctx.cmd.SetGlobalTexture("_PlanetMediaTransparentTransmission", transparentTransmission);
            }
            composite.SetTexture("_PlanetMediaScattering", scattering);
            composite.SetTexture("_PlanetMediaTransmission", transmission);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer);
            ctx.cmd.SetViewport(new Rect(0, 0, scattering.width, scattering.height));
            CoreUtils.DrawFullScreen(ctx.cmd, composite, shaderPassId: 0);
            CoreUtils.DrawFullScreen(ctx.cmd, composite, shaderPassId: 1);
            imposters?.DrawWithMedia(ctx);
        }
        protected override void Cleanup()
        {
            Deactivate();
            bodyBuffer?.Dispose(); curveBuffer?.Dispose(); ambientBuffer?.Dispose(); regionBuffer?.Dispose();
            bodyBuffer = curveBuffer = ambientBuffer = null; capacity = 0;
            regionBuffer = null; regionCapacity = 0; regions.Clear();
            weather?.Dispose(); weather = null;
            textures.Dispose();
            CoreUtils.Destroy(scattering); CoreUtils.Destroy(transmission); scattering = transmission = null;
            CoreUtils.Destroy(lowScattering); CoreUtils.Destroy(lowTransmission); lowScattering = lowTransmission = null;
            CoreUtils.Destroy(transparentScattering); CoreUtils.Destroy(transparentTransmission); transparentScattering = transparentTransmission = null;
            CoreUtils.Destroy(composite); composite = null;
            imposters?.ReleaseResources();
            Imposters = null;
        }
    }
}
