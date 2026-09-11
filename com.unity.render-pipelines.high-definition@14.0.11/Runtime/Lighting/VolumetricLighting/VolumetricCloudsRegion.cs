using System.Runtime.InteropServices;

namespace UnityEngine.Rendering.HighDefinition
{
    // GPU-side representation of a single region. Kept in sync by hand with the StructuredBuffer declaration
    // in VolumetricCloudsUtilities.hlsl (VolumetricCloudsRegionData) - 12 floats, 48 bytes.
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
        public float densityOverride;
        public float bottomAltitude;
        public float topAltitude;
        public float storminess;
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

        /// <summary>
        /// Coverage of the low, flat Alto Stratus layer inside the region. 0 removes it. Combined with Cumulus
        /// Coverage above zero, the region reads as a blended Cumulus + Alto Stratus layer instead of either
        /// shape alone, matching how the global Advanced control mode combines its Cumulus/Alto Stratus maps.
        /// </summary>
        [Tooltip("Coverage of the low, flat Alto Stratus layer inside the region. 0 removes it. Combined with Cumulus Coverage above zero, the region blends into a Cumulus + Alto Stratus layer, matching the global Advanced control mode.")]
        [Range(0.0f, 1.0f)]
        public float altoStratusCoverage = 0.0f;

        /// <summary>
        /// Coverage of fluffy, medium-height Cumulus clouds inside the region. 0 removes them.
        /// </summary>
        [Tooltip("Coverage of fluffy, medium-height Cumulus clouds inside the region. 0 removes them.")]
        [Range(0.0f, 1.0f)]
        public float cumulusCoverage = 0.0f;

        /// <summary>
        /// Coverage of tall, anvil-shaped Cumulonimbus storm clouds inside the region. 0 removes them; above
        /// zero it takes precedence over Cumulus/Alto Stratus, matching the global Advanced control mode.
        /// </summary>
        [Tooltip("Coverage of tall, anvil-shaped Cumulonimbus storm clouds inside the region. 0 removes them. Above zero it takes precedence over Cumulus/Alto Stratus, matching the global Advanced control mode.")]
        [Range(0.0f, 1.0f)]
        public float cumulonimbusCoverage = 1.0f;

        /// <summary>Rain intensity applied inside the region.</summary>
        [Tooltip("Rain intensity applied inside the region.")]
        [Range(0.0f, 1.0f)]
        public float rainIntensity = 1.0f;

        /// <summary>
        /// Strength at which the region forces a solid, unbroken cloud mass, overriding the shape/erosion
        /// noise that normally breaks up the clouds. 0 only overrides the coverage/rain/type map channels
        /// (subtle, and in Simple control mode barely visible since its LUT ignores cloud type); 1 makes the
        /// region read clearly in every control mode.
        /// </summary>
        [Tooltip("Strength at which the region forces a solid, unbroken cloud mass inside its radius, overriding the shape/erosion noise so it reads clearly in every control mode (including Simple).")]
        [Range(0.0f, 1.0f)]
        public float densityOverride = 1.0f;

        /// <summary>
        /// Darkens the region's cloud base and reduces its ambient light response, giving it the heavy,
        /// light-blocking underside of a real storm cell instead of a brighter, uniformly-lit cloud.
        /// </summary>
        [Tooltip("Darkens the region's cloud base and reduces its ambient light response, giving it the heavy, light-blocking underside of a real storm cell instead of a brighter, uniformly-lit cloud.")]
        [Range(0.0f, 1.0f)]
        public float storminess = 0.0f;

        /// <summary>
        /// Overrides the cloud altitude range inside the region instead of using the Volumetric Clouds
        /// Bottom Altitude/Altitude Range from the Volume. Lets a storm cell tower above (or sit lower than)
        /// the surrounding cloud layer.
        /// </summary>
        [Tooltip("Overrides the cloud altitude range inside the region instead of using the Volumetric Clouds Bottom Altitude/Altitude Range from the Volume. Lets a storm cell tower above (or sit lower than) the surrounding cloud layer.")]
        public bool altitudeOverride = false;

        /// <summary>World-space altitude of the region's cloud base, in meters. Only used when Altitude Override is enabled.</summary>
        [Tooltip("World-space altitude of the region's cloud base, in meters. Only used when Altitude Override is enabled.")]
        public float regionBottomAltitude = 1000.0f;

        /// <summary>World-space altitude of the region's cloud top, in meters. Only used when Altitude Override is enabled.</summary>
        [Tooltip("World-space altitude of the region's cloud top, in meters. Only used when Altitude Override is enabled. Must be greater than Bottom Altitude.")]
        public float regionTopAltitude = 6000.0f;

        [Tooltip("Adds a lit rain column to volumetric fog below this cloud region. Requires Volumetric Fog and sufficient Fog Depth Extent.")]
        public bool rainFog = true;

        [Tooltip("World-space altitude of the bottom of the rain column, in meters. The top follows the region's cloud base (Region Bottom Altitude when Altitude Override is enabled, otherwise Volumetric Clouds Bottom Altitude).")]
        public float rainFogBottomAltitude = 0.0f;

        [Tooltip("Mean free path in meters at full rain and coverage. Smaller values produce denser rain fog.")]
        [Min(1.0f)] public float rainFogMeanFreePath = 1200.0f;

        [Tooltip("Scattering albedo of the rain mist. Lighting is supplied by HDRP volumetric fog.")]
        public Color rainFogAlbedo = new Color(0.65f, 0.7f, 0.75f, 1.0f);

        [Tooltip("Vertical fade distance at the bottom and cloud base, clamped to half the column height.")]
        [Min(1.0f)] public float rainFogVerticalFade = 200.0f;

        [Tooltip("Relative density at the bottom of the column. Density increases towards the cloud base.")]
        [Range(0.0f, 1.0f)] public float rainFogBottomDensity = 0.25f;

        /// <summary>
        /// Combines independent per-type coverage into the single coverage/type/height triplet the shader's
        /// cloud type LUT axis expects. Mirrors the precedence rules baked into CloudMapGenerator.compute
        /// (Cumulonimbus overrides Cumulus/Alto Stratus, which in turn blend together when both are present)
        /// so a region reads exactly like the equivalent combination would on the global Advanced-mode map.
        /// </summary>
        internal static void EvaluateCloudTypeBlend(float altoStratusCoverage, float cumulusCoverage, float cumulonimbusCoverage,
            out float coverage, out float cloudType, out float maxCloudHeight)
        {
            altoStratusCoverage = Mathf.Clamp01(altoStratusCoverage);
            cumulusCoverage = Mathf.Clamp01(cumulusCoverage);
            cumulonimbusCoverage = Mathf.Clamp01(cumulonimbusCoverage);

            // Matches the sub-ranges of the 0..1 cloud type LUT axis evaluated by CloudMapGenerator.compute.
            const float k_AltoStratusRangeMin = 0.0f / 256.0f;
            const float k_AltoStratusRangeMax = 32.0f / 256.0f;
            const float k_CumulusAltoStratusRangeMin = 32.0f / 256.0f;
            const float k_CumulusAltoStratusRangeMax = 64.0f / 256.0f;
            const float k_CumulusRangeMin = 64.0f / 256.0f;
            const float k_CumulusRangeMax = 128.0f / 256.0f;
            const float k_CumulonimbusRangeFirstMin = 128.0f / 256.0f;
            const float k_CumulonimbusRangeSecondMin = 130.0f / 256.0f;
            const float k_CumulonimbusRangeThirdMin = 136.0f / 256.0f;
            const float k_CumulonimbusRangeMax = 1.0f;

            if (cumulonimbusCoverage > 0.0f)
            {
                // Cumulonimbus takes precedence over every other type, same as the global Advanced mode map.
                cloudType = cumulonimbusCoverage * (k_CumulonimbusRangeMax - k_CumulonimbusRangeFirstMin) + k_CumulonimbusRangeFirstMin;
                if (cloudType < k_CumulonimbusRangeSecondMin)
                {
                    coverage = 0.0f;
                    maxCloudHeight = 0.0f;
                }
                else if (cloudType < k_CumulonimbusRangeThirdMin)
                {
                    float t = (cloudType - k_CumulonimbusRangeSecondMin) / (k_CumulonimbusRangeThirdMin - k_CumulonimbusRangeSecondMin);
                    coverage = Mathf.Lerp(0.0f, cumulonimbusCoverage, t);
                    maxCloudHeight = Mathf.Lerp(0.0f, 1.0f, t);
                }
                else
                {
                    coverage = Mathf.Lerp(0.0f, 0.75f, cumulonimbusCoverage);
                    maxCloudHeight = 1.0f;
                }
            }
            else if (cumulusCoverage > 0.0f)
            {
                if (altoStratusCoverage > 0.0f)
                {
                    cloudType = 0.5f * (k_CumulusAltoStratusRangeMax - k_CumulusAltoStratusRangeMin) + k_CumulusAltoStratusRangeMin;
                    coverage = Mathf.Max(cumulusCoverage, altoStratusCoverage);
                    maxCloudHeight = 1.0f;
                }
                else
                {
                    cloudType = 0.5f * (k_CumulusRangeMax - k_CumulusRangeMin) + k_CumulusRangeMin;
                    coverage = cumulusCoverage;
                    maxCloudHeight = 0.5f;
                }
            }
            else if (altoStratusCoverage > 0.0f)
            {
                cloudType = 0.5f * (k_AltoStratusRangeMax - k_AltoStratusRangeMin) + k_AltoStratusRangeMin;
                coverage = altoStratusCoverage;
                maxCloudHeight = 1.0f;
            }
            else
            {
                coverage = 0.0f;
                cloudType = 0.0f;
                maxCloudHeight = 0.0f;
            }
        }

        internal VolumetricCloudsRegionData GetRegionData()
        {
            EvaluateCloudTypeBlend(altoStratusCoverage, cumulusCoverage, cumulonimbusCoverage,
                out float coverage, out float cloudType, out float maxHeight);

            // A disabled/degenerate override is encoded as bottomAltitude == topAltitude == 0 so the shader
            // can detect it (topAltitude > bottomAltitude) without needing a separate flag in the buffer.
            bool overrideActive = altitudeOverride && regionTopAltitude > regionBottomAltitude;

            Vector3 position = transform.position;
            return new VolumetricCloudsRegionData
            {
                positionWS = new Vector2(position.x, position.z),
                radius = Mathf.Max(radius, 0.0f),
                blendDistance = Mathf.Max(blendDistance, 0.0f),
                coverage = coverage,
                rainIntensity = rainIntensity,
                cloudType = cloudType,
                maxCloudHeight = maxHeight,
                densityOverride = densityOverride,
                bottomAltitude = overrideActive ? regionBottomAltitude : 0.0f,
                topAltitude = overrideActive ? regionTopAltitude : 0.0f,
                storminess = storminess,
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
