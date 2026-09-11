using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    // Keeps track of the LightImposter components currently active in the scene, so the render pipeline can
    // gather and draw them each frame. Mirrors the VolumetricCloudsRegionManager pattern.
    class LightImposterManager
    {
        static LightImposterManager m_Manager;
        public static LightImposterManager manager
        {
            get
            {
                if (m_Manager == null)
                    m_Manager = new LightImposterManager();
                return m_Manager;
            }
        }

        readonly List<LightImposter> m_Imposters = new List<LightImposter>();

        public void RegisterImposter(LightImposter imposter)
        {
            if (!m_Imposters.Contains(imposter))
                m_Imposters.Add(imposter);
        }

        public void DeRegisterImposter(LightImposter imposter)
        {
            m_Imposters.Remove(imposter);
        }

        public List<LightImposter> imposters => m_Imposters;
    }
}
