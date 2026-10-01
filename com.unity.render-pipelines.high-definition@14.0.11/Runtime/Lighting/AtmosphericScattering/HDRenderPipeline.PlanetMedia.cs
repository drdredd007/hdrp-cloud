namespace UnityEngine.Rendering.HighDefinition
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PlanetCelestialLightData
    {
        public Vector4 Color;     // Linear unattenuated radiance; original interactsWithSky flag.
        public Vector4 Direction; // Towards the sun; native light dimmer.
        public Vector4 Dimmers;   // Diffuse, specular, volumetric dimmers; angular diameter in degrees.
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PlanetMediaGpuRegion
    {
        public Vector4 Anchor, Cloud, Shape, Rain, RainOptical;
    }
    public partial class HDRenderPipeline
    {
        GraphicsBuffer planetCelestialLights;
        int planetCelestialLightCapacity;
        public bool BindPlanetCelestialLights(CommandBuffer cmd, Camera camera)
        {
            bool active = PlanetMediaCameraRegistry.IsActive(camera);
            cmd.SetGlobalInt("_PlanetCelestialLightDataReady", active ? 1 : 0);
            cmd.SetGlobalInt("_PlanetCelestialLightCount", active ? m_GpuLightsBuilder.directionalLightCount : 0);
            int count = Mathf.Max(1, active ? m_GpuLightsBuilder.directionalLightCount : 0);
            if (planetCelestialLightCapacity < count)
            {
                planetCelestialLights?.Dispose(); planetCelestialLightCapacity = Mathf.NextPowerOfTwo(count);
                planetCelestialLights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, planetCelestialLightCapacity, 48);
            }
            if (active && m_GpuLightsBuilder.directionalLightCount > 0)
                cmd.SetBufferData(planetCelestialLights, m_GpuLightsBuilder.celestialDirectionalLights, 0, 0, m_GpuLightsBuilder.directionalLightCount);
            cmd.SetGlobalBuffer("_PlanetCelestialLightDatas", planetCelestialLights);
            return active;
        }
        void ReleasePlanetCelestialLights()
        { planetCelestialLights?.Dispose(); planetCelestialLights = null; planetCelestialLightCapacity = 0; }
        // Public accessors let the independent planet module share HDRP's noise resources
        // and the camera's lighting sky without depending on its private cloud renderer.
        public Texture3D PlanetMediaShapeNoise => m_Asset.renderPipelineResources.textures.worleyNoise128RGBA;
        public Texture3D PlanetMediaWorleyErosion => m_Asset.renderPipelineResources.textures.worleyNoise32RGB;
        public Texture3D PlanetMediaPerlinErosion => m_Asset.renderPipelineResources.textures.perlinNoise32RGB;
        public Texture2D PlanetMediaCloudTypeLut => m_Asset.renderPipelineResources.textures.cloudLutRainAO;
        public void BindPlanetMediaLights(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, HDShaderIDs._DirectionalLightDatas, m_LightLoopLightData.directionalLightData);
            if (planetCelestialLights != null) cmd.SetComputeBufferParam(shader, kernel, "_PlanetCelestialLightDatas", planetCelestialLights);
            cmd.SetComputeIntParam(shader, "_PlanetCelestialLightCount", m_GpuLightsBuilder.directionalLightCount);
            cmd.SetComputeIntParam(shader, "_PlanetCelestialLightDataReady", planetCelestialLights != null ? 1 : 0);
        }
        public void FillPlanetMediaAmbientProbe(HDCamera camera, Vector4[] packed)
        {
            if (packed == null || packed.Length < 7) throw new System.ArgumentException("Seven packed SH vectors are required.", nameof(packed));
            var probe = m_SkyManager.GetAmbientProbe(camera);
            SphericalHarmonicMath.PackCoefficients(packed, probe);
        }
        // The independent path shares native authoring, but does not inherit its 32-region upload cap.
        public static void CollectPlanetMediaRegions(int planetId, float minimumCloudAltitude, float defaultBottom,
            float defaultTop, bool cloudsEnabled, bool fogEnabled, System.Collections.Generic.List<PlanetMediaGpuRegion> result)
        {
            if (cloudsEnabled)
            foreach (var region in VolumetricCloudsRegionManager.manager.regions)
            {
                if (!region || !region.isActiveAndEnabled || !region.planetary || region.planetId != planetId ||
                    float.IsNaN(region.planetCoordinates.x) || float.IsInfinity(region.planetCoordinates.x) ||
                    float.IsNaN(region.planetCoordinates.y) || float.IsInfinity(region.planetCoordinates.y) ||
                    Mathf.Abs(region.planetCoordinates.x) > 90 || !(region.radius > 0) || float.IsInfinity(region.radius)) continue;
                var source = region.GetRegionData();
                bool altitude = source.topAltitude > source.bottomAltitude &&
                    !float.IsInfinity(source.topAltitude) && !float.IsInfinity(source.bottomAltitude);
                float bottom = altitude ? Mathf.Max(minimumCloudAltitude, source.bottomAltitude) : defaultBottom;
                float top = altitude ? Mathf.Max(bottom + 1, source.topAltitude) : defaultTop;
                var albedo = region.rainFogAlbedo.linear;
                float rainBottom = region.rainFogBottomAltitude;
                float rainTop = bottom;
                float rainExtinction = region.rainFog && fogEnabled && rainTop > rainBottom ?
                    Mathf.Clamp01(region.rainIntensity) * Mathf.Clamp01(source.coverage) / Mathf.Max(1, region.rainFogMeanFreePath) : 0;
                var packed = new PlanetMediaGpuRegion
                {
                    Anchor = new Vector4(source.positionWS.x, source.positionWS.y, source.radius, Mathf.Max(0, source.blendDistance)),
                    Cloud = new Vector4(source.coverage, source.rainIntensity, source.cloudType, source.maxCloudHeight),
                    Shape = new Vector4(source.densityOverride, bottom, top, source.storminess),
                    Rain = new Vector4(rainBottom, rainTop, Mathf.Min(Mathf.Max(1, region.rainFogVerticalFade), Mathf.Max(1, (rainTop - rainBottom) * .5f)), region.rainFogBottomDensity),
                    RainOptical = new Vector4(albedo.r, albedo.g, albedo.b, rainExtinction)
                };
                if (!PlanetMediaFinite(packed.Anchor) || !PlanetMediaFinite(packed.Cloud) || !PlanetMediaFinite(packed.Shape) ||
                    !PlanetMediaFinite(packed.Rain) || !PlanetMediaFinite(packed.RainOptical)) continue;
                result.Add(packed);
            }
        }
        static bool PlanetMediaFinite(Vector4 value)
        {
            for (int i = 0; i < 4; ++i) if (float.IsNaN(value[i]) || float.IsInfinity(value[i])) return false;
            return true;
        }
    }
}
