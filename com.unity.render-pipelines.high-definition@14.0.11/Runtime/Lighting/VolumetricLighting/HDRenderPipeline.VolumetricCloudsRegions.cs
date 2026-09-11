namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // A per-camera snapshot owned by its render pass. Four float4s per region;
        // independent of the cloud buffer, which can be updated by other camera passes.
        Vector4[] PrepareRainFogRegions(HDCamera camera)
        {
            var clouds = camera.volumeStack.GetComponent<VolumetricClouds>();
            if (!m_ActiveVolumetricClouds || !clouds.active || !HasVolumetricClouds(camera, clouds))
                return System.Array.Empty<Vector4>();

            var regions = VolumetricCloudsRegionManager.manager.regions;
            var result = new System.Collections.Generic.List<Vector4>();
            int cloudRegionCount = 0;
            foreach (var region in regions)
            {
                if (region == null || !region.isActiveAndEnabled) continue;
                // Match the set uploaded to the cloud shader, including non-raining regions.
                if (cloudRegionCount++ >= VolumetricCloudsRegionManager.maxRegionCount) break;
                VolumetricCloudsRegion.EvaluateCloudTypeBlend(region.altoStratusCoverage, region.cumulusCoverage,
                    region.cumulonimbusCoverage, out float regionCoverage, out _, out _);
                float strength = Mathf.Clamp01(region.rainIntensity) * Mathf.Clamp01(regionCoverage);
                // The column top follows the region's own cloud base when its altitude override is active,
                // otherwise the ambient cloud layer's bottom altitude from the Volume.
                bool altitudeOverrideActive = region.altitudeOverride && region.regionTopAltitude > region.regionBottomAltitude;
                float top = altitudeOverrideActive ? region.regionBottomAltitude : clouds.bottomAltitude.value;
                if (!region.rainFog || strength <= 0 || region.radius <= 0 || top <= region.rainFogBottomAltitude)
                    continue;

                var p = region.transform.position;
                var albedo = region.rainFogAlbedo.linear;
                result.Add(new Vector4(p.x, p.z, Mathf.Max(0, region.radius), Mathf.Max(1, region.blendDistance)));
                result.Add(new Vector4(region.rainFogBottomAltitude, top,
                    Mathf.Max(1, region.rainFogVerticalFade), Mathf.Clamp01(region.rainFogBottomDensity)));
                result.Add(new Vector4(Mathf.Clamp01(albedo.r), Mathf.Clamp01(albedo.g), Mathf.Clamp01(albedo.b),
                    strength / Mathf.Max(1, region.rainFogMeanFreePath)));
                // Cloud-shell curvature uses absolute XZ for local clouds, camera-relative XZ for distant clouds.
                result.Add(new Vector4(Mathf.Lerp(1, 0.025f, clouds.earthCurvature.value) * k_EarthRadius,
                    clouds.localClouds.value ? 1 : 0, 0, 0));
            }
            return result.ToArray();
        }

        // Fixed-size buffer: always fully allocated, only the first regionsCount entries returned by
        // UpdateVolumetricCloudsRegionBuffer are meaningful (see _VolumetricCloudsRegionCount in the shader).
        ComputeBuffer m_VolumetricCloudsRegionBuffer = null;
        VolumetricCloudsRegionData[] m_VolumetricCloudsRegionData;

        void InitializeVolumetricCloudsRegions()
        {
            m_VolumetricCloudsRegionData = new VolumetricCloudsRegionData[VolumetricCloudsRegionManager.maxRegionCount];
            // 12 floats per region (positionWS, radius, blendDistance, coverage, rainIntensity, cloudType, maxCloudHeight,
            // densityOverride, bottomAltitude, topAltitude, storminess).
            m_VolumetricCloudsRegionBuffer = new ComputeBuffer(VolumetricCloudsRegionManager.maxRegionCount, 12 * sizeof(float));
        }

        void ReleaseVolumetricCloudsRegions()
        {
            CoreUtils.SafeRelease(m_VolumetricCloudsRegionBuffer);
        }

        // Gathers the currently active regions, uploads them to the GPU and returns how many are valid.
        int UpdateVolumetricCloudsRegionBuffer()
        {
            var regions = VolumetricCloudsRegionManager.manager.regions;
            int count = 0;
            for (int i = 0; i < regions.Count && count < VolumetricCloudsRegionManager.maxRegionCount; ++i)
            {
                var region = regions[i];
                if (region == null || !region.isActiveAndEnabled)
                    continue;

                m_VolumetricCloudsRegionData[count] = region.GetRegionData();
                count++;
            }

            if (count > 0)
                m_VolumetricCloudsRegionBuffer.SetData(m_VolumetricCloudsRegionData, 0, 0, count);

            return count;
        }
    }
}
