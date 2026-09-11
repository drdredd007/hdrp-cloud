using System.Runtime.InteropServices;

namespace UnityEngine.Rendering.HighDefinition
{
    // GPU-side representation of a single region. Kept in sync by hand with the StructuredBuffer declaration
    // in VolumetricCloudsUtilities.hlsl (VolumetricCloudsRegionData) - 8 floats, 32 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct VolumetricCloudsRegionData
    {
        public Vector2 positionWS;
        public float radius;
        public float blendDistance;
        public float coverage;
        public float rainIntensity;
        public float cloudType;
        public float maxCloudHeight;
    }

    /// <summary>
    /// Cloud shape applied inside a <see cref="VolumetricCloudsRegion"/>. Matches the shapes available in the
    /// Volumetric Clouds Advanced control mode (Cumulus, Alto Stratus, Cumulonimbus).
    /// </summary>
    public enum VolumetricCloudsRegionType
    {
        /// <summary>Flat, low density stratus layer.</summary>
        AltoStratus,
        /// <summary>Fluffy, medium height cumulus clouds.</summary>
        Cumulus,
        /// <summary>Cumulus clouds blended with a stratus layer.</summary>
        CumulusAltoStratus,
        /// <summary>Tall, anvil-shaped storm clouds.</summary>
        Cumulonimbus,
    }

    /// <summary>
    /// Marks a circular area of the world where the volumetric clouds coverage, type and rain are overridden,
    /// regardless of the procedural noise or the authored/generated cloud map. This lets you manually place
    /// storm clouds or a rain area in the scene instead of relying only on the global, procedurally driven look.
    /// The region stays fixed at the world position it was placed at: it does not drift with the wind/cloud
    /// map animation like the rest of the cloud pattern does.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Rendering/Volumetric Clouds Region")]
    public class VolumetricCloudsRegion : MonoBehaviour
    {
        /// <summary>Radius of the region, in meters, projected on the world XZ plane.</summary>
        [Tooltip("Radius of the region, in meters, projected on the world XZ plane.")]
        public float radius = 500.0f;

        /// <summary>Distance, in meters, over which the region blends out into the surrounding cloud coverage.</summary>
        [Tooltip("Distance, in meters, over which the region blends out into the surrounding cloud coverage.")]
        public float blendDistance = 250.0f;

        /// <summary>Cloud shape applied inside the region.</summary>
        [Tooltip("Cloud shape applied inside the region.")]
        public VolumetricCloudsRegionType cloudType = VolumetricCloudsRegionType.Cumulonimbus;

        /// <summary>Cloud coverage applied inside the region. 0 clears the clouds, 1 is fully covered.</summary>
        [Tooltip("Cloud coverage applied inside the region. 0 clears the clouds, 1 is fully covered.")]
        [Range(0.0f, 1.0f)]
        public float coverage = 1.0f;

        /// <summary>Rain intensity applied inside the region.</summary>
        [Tooltip("Rain intensity applied inside the region.")]
        [Range(0.0f, 1.0f)]
        public float rainIntensity = 1.0f;

        internal VolumetricCloudsRegionData GetRegionData()
        {
            float typeValue, maxHeight;
            switch (cloudType)
            {
                case VolumetricCloudsRegionType.AltoStratus:
                    // Matches the Alto Stratus only range evaluated by CloudMapGenerator.compute.
                    typeValue = 16.0f / 256.0f;
                    maxHeight = 1.0f;
                    break;
                case VolumetricCloudsRegionType.CumulusAltoStratus:
                    typeValue = 48.0f / 256.0f;
                    maxHeight = 1.0f;
                    break;
                case VolumetricCloudsRegionType.Cumulonimbus:
                    // Highest sub-range: full anvil shape at maximal cloud height.
                    typeValue = 1.0f;
                    maxHeight = 1.0f;
                    break;
                default: // Cumulus
                    typeValue = 96.0f / 256.0f;
                    maxHeight = 0.5f;
                    break;
            }

            Vector3 position = transform.position;
            return new VolumetricCloudsRegionData
            {
                positionWS = new Vector2(position.x, position.z),
                radius = Mathf.Max(radius, 0.0f),
                blendDistance = Mathf.Max(blendDistance, 0.0f),
                coverage = coverage,
                rainIntensity = rainIntensity,
                cloudType = typeValue,
                maxCloudHeight = maxHeight,
            };
        }

        void OnEnable()
        {
            VolumetricCloudsRegionManager.manager.RegisterRegion(this);
        }

        void OnDisable()
        {
            VolumetricCloudsRegionManager.manager.DeRegisterRegion(this);
        }
    }
}
