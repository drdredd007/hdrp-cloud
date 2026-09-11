namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>
    /// A single small, distant glowing point (a window, a running light, a city light) rendered as a cheap
    /// camera-facing billboard instead of a real Light. Meant for points too small on screen to be worth a
    /// real light source. Its apparent size grows with distance and intensity like a real light would; once
    /// it would shrink below a screen-space floor, the render size is clamped and alpha fades out instead,
    /// avoiding the flicker/aliasing a genuinely sub-pixel quad would cause.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Rendering/Light Imposter")]
    public class LightImposter : MonoBehaviour
    {
        /// <summary>Color of the imposter. Alpha is ignored; use Intensity to control brightness and apparent size.</summary>
        [Tooltip("Color of the imposter. Alpha is ignored; use Intensity to control brightness and apparent size.")]
        public Color color = Color.white;

        /// <summary>
        /// Relative brightness of the imposter. Also drives its apparent size: brighter imposters read as
        /// bigger/closer light sources, matching how a real light's apparent size grows with output.
        /// </summary>
        [Tooltip("Relative brightness of the imposter. Also drives its apparent size: brighter imposters read as bigger/closer light sources, matching how a real light's apparent size grows with output.")]
        [Min(0.0f)]
        public float intensity = 1.0f;

        void OnEnable()
        {
            LightImposterManager.manager.RegisterImposter(this);
        }

        void OnDisable()
        {
            LightImposterManager.manager.DeRegisterImposter(this);
        }
    }
}
