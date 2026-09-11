namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // Fixed-size buffer: always fully allocated, only the first regionsCount entries returned by
        // UpdateVolumetricCloudsRegionBuffer are meaningful (see _VolumetricCloudsRegionCount in the shader).
        ComputeBuffer m_VolumetricCloudsRegionBuffer = null;
        VolumetricCloudsRegionData[] m_VolumetricCloudsRegionData;

        void InitializeVolumetricCloudsRegions()
        {
            m_VolumetricCloudsRegionData = new VolumetricCloudsRegionData[VolumetricCloudsRegionManager.maxRegionCount];
            // 8 floats per region (positionWS, radius, blendDistance, coverage, rainIntensity, cloudType, maxCloudHeight).
            m_VolumetricCloudsRegionBuffer = new ComputeBuffer(VolumetricCloudsRegionManager.maxRegionCount, 8 * sizeof(float));
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
