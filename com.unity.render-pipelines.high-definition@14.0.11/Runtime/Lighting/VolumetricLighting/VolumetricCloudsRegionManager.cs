using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    // Keeps track of the VolumetricCloudsRegion components currently active in the scene, so the render
    // pipeline can upload them to the GPU each frame. Mirrors the LocalVolumetricFogManager pattern.
    class VolumetricCloudsRegionManager
    {
        static VolumetricCloudsRegionManager m_Manager;
        public static VolumetricCloudsRegionManager manager
        {
            get
            {
                if (m_Manager == null)
                    m_Manager = new VolumetricCloudsRegionManager();
                return m_Manager;
            }
        }

        // Upper bound on the number of regions uploaded to the GPU per frame.
        internal const int maxRegionCount = 32;

        readonly List<VolumetricCloudsRegion> m_Regions = new List<VolumetricCloudsRegion>();

        public void RegisterRegion(VolumetricCloudsRegion region)
        {
            if (!m_Regions.Contains(region))
                m_Regions.Add(region);
        }

        public void DeRegisterRegion(VolumetricCloudsRegion region)
        {
            m_Regions.Remove(region);
        }

        public List<VolumetricCloudsRegion> regions => m_Regions;
    }
}
