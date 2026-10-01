using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // A private resolved copy: neither a scene's volume stack nor an authored profile is edited.
    // Placement is supplied separately by PlanetMediaBody, so scene weather cannot move a planet.
    public sealed class PlanetMediaWeatherSnapshot : IDisposable
    {
        public readonly VolumetricClouds Clouds = Make<VolumetricClouds>();
        public readonly Fog Fog = Make<Fog>();
        public readonly PlanetaryWeather Weather = Make<PlanetaryWeather>();
        public readonly VisualEnvironment Environment = Make<VisualEnvironment>();
        readonly VolumetricClouds defaultClouds = Make<VolumetricClouds>();
        readonly Fog defaultFog = Make<Fog>();
        readonly PlanetaryWeather defaultWeather = Make<PlanetaryWeather>();
        readonly VisualEnvironment defaultEnvironment = Make<VisualEnvironment>();

        static T Make<T>() where T : VolumeComponent => ScriptableObject.CreateInstance<T>();
        public PlanetMediaWeatherSnapshot()
        {
            Clouds.hideFlags = Fog.hideFlags = Weather.hideFlags = Environment.hideFlags = HideFlags.HideAndDontSave;
            defaultClouds.hideFlags = defaultFog.hideFlags = defaultWeather.hideFlags = defaultEnvironment.hideFlags = HideFlags.HideAndDontSave;
        }
        static void Resolve<T>(T destination, T defaults, VolumeStack stack, VolumeProfile profile) where T : VolumeComponent
        {
            var source = stack?.GetComponent<T>() ?? defaults;
            destination.active = source.active;
            for (int i = 0; i < destination.parameters.Count; ++i)
                destination.parameters[i].SetValue(source.parameters[i]);
            if (profile && profile.TryGet<T>(out var authored) && authored.active)
            {
                destination.active = true;
                authored.Override(destination, 1);
            }
        }
        public void Resolve(VolumeStack stack, VolumeProfile profile)
        {
            Resolve(Clouds, defaultClouds, stack, profile); Resolve(Fog, defaultFog, stack, profile);
            Resolve(Weather, defaultWeather, stack, profile); Resolve(Environment, defaultEnvironment, stack, profile);
        }

        static float WindValue(WindParameter parameter, float global)
        {
            var value = parameter.value;
            switch (value.mode)
            {
                case WindParameter.WindOverrideMode.Custom: return value.customValue;
                case WindParameter.WindOverrideMode.Additive: return global + value.additiveValue;
                case WindParameter.WindOverrideMode.Multiply: return global * value.multiplyValue;
                default: return global;
            }
        }

        static Vector4 V(Color color, float w) => new Vector4(color.r, color.g, color.b, w);
        static Vector4 V(Vector3 value, float w) => new Vector4(value.x, value.y, value.z, w);
        static Color NonNegative(Color color) => new Color(Mathf.Max(0, color.r), Mathf.Max(0, color.g), Mathf.Max(0, color.b));
        public PlanetMediaGpuBody Pack(in PlanetMediaBody body, int curveOffset, float timeSeconds)
        {
            var center = (float3)(body.Definition.Center - body.CameraPosition);
            var rotation = Quaternion.Inverse(body.Rotation);
            var result = new PlanetMediaGpuBody
            {
                CenterRadius = new Vector4(center.x, center.y, center.z, (float)body.Definition.Radius),
                InverseRotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
            };
            var air = body.Atmosphere;
            if (air.IsValid)
            {
                float h = PlanetMediaMath.ScaleHeight(air.AirMaximumAltitude);
                float ah = PlanetMediaMath.ScaleHeight(air.AerosolMaximumAltitude);
                var extinction = new Vector3(PlanetMediaMath.Extinction(air.AirOpacity.r, h),
                    PlanetMediaMath.Extinction(air.AirOpacity.g, h), PlanetMediaMath.Extinction(air.AirOpacity.b, h));
                result.AirExtinction = V(extinction, h);
                result.AirScattering = new Vector4(extinction.x * air.AirTint.r, extinction.y * air.AirTint.g, extinction.z * air.AirTint.b, ah);
                float ae = PlanetMediaMath.Extinction(air.AerosolOpacity, ah);
                result.AerosolExtinction = new Vector4(ae, air.AerosolAnisotropy, 0, 0);
                result.AerosolScattering = V(NonNegative(air.AerosolTint) * ae, 0);
                result.Limits.x = Mathf.Max(air.AirMaximumAltitude, air.AerosolMaximumAltitude);
            }
            if (body.Fog && Fog.active && Fog.enabled.value)
            {
                float h = PlanetMediaMath.ScaleHeight(Mathf.Max(0.01f, Fog.maximumHeight.value - Fog.baseHeight.value));
                result.FogDensity = new Vector4(1 / Mathf.Max(1, Fog.meanFreePath.value), Fog.baseHeight.value, h, Fog.anisotropy.value);
                result.FogAlbedo = V(NonNegative(Fog.albedo.value), Fog.globalLightProbeDimmer.value);
                result.FogColor = V(NonNegative(Fog.colorMode.value == FogColorMode.ConstantColor ? Fog.color.value : Fog.tint.value),
                    Fog.colorMode.value == FogColorMode.ConstantColor ? 0 : 1);
                result.Limits.w = Mathf.Max(0, Fog.baseHeight.value + 12 * h);
            }
            if (body.Clouds && Clouds.active && Clouds.enable.value)
            {
                float bottom = Mathf.Max(body.MinimumCloudAltitude, Clouds.bottomAltitude.value);
                result.Limits.y = bottom;
                result.Limits.z = bottom + Mathf.Max(1, Clouds.altitudeRange.value);
                result.CloudShape = new Vector4(Clouds.densityMultiplier.value * Clouds.densityMultiplier.value * 2,
                    Clouds.shapeFactor.value, Clouds.shapeScale.value, Clouds.erosionFactor.value);
                result.CloudErosion = new Vector4(Clouds.erosionScale.value, Clouds.erosionOcclusion.value,
                    Clouds.erosionNoiseType.value == VolumetricClouds.CloudErosionNoise.Perlin32 ? 0.75f : 1,
                    Clouds.erosionNoiseType.value == VolumetricClouds.CloudErosionNoise.Perlin32 ? 1 : 0);
                var scattering = Color.white - Clouds.scatteringTint.value * 0.75f;
                result.CloudScatteringTint = V(scattering, Clouds.sunLightDimmer.value);
                result.CloudLighting = new Vector4(1 - Clouds.multiScattering.value * 0.95f, Clouds.powderEffectIntensity.value,
                    Clouds.ambientLightProbeDimmer.value, 1);
                result.CloudBottomLighting = V(Clouds.customBottomColor.value.linear * Clouds.customBottomIntensity.value,
                    Clouds.bottomLightingBlend.value);
                result.CloudOffset = V(Clouds.shapeOffset.value, Clouds.altitudeDistortion.value * 0.25f);
                result.Coverage = new Vector4(Weather.coverage.value, Weather.coverageContrast.value,
                    Mathf.Clamp((float)body.Definition.Radius / Mathf.Max(1, Weather.systemScale.value), 0.35f, 256), Weather.openSky.value);
                result.Climate = new Vector4(Weather.climateBands.value, Weather.stormCount.value,
                    Mathf.Clamp(Weather.stormScale.value / Mathf.Max(1, (float)body.Definition.Radius), 0.002f, 1.2f), Weather.stormStrength.value);
                int seed = body.Seed;
                if (body.WeatherProfile && body.WeatherProfile.TryGet<PlanetaryWeather>(out var authored) && authored.seed.overrideState)
                    seed = Weather.seed.value;
                // Native wind speed is km/h; modulo bounds retain precision during long sessions.
                float angle = WindValue(Clouds.orientation, Environment.windOrientation.value) * Mathf.Deg2Rad;
                float travel = (timeSeconds % 100000) * WindValue(Clouds.globalWindSpeed, Environment.windSpeed.value) / 3.6f;
                result.SeedWind = new Vector4((float)(((long)seed & 8191) * 7.13), -Mathf.Cos(angle) * travel, 0, -Mathf.Sin(angle) * travel);
                result.CloudWind = new Vector4(Clouds.cloudMapSpeedMultiplier.value, Clouds.shapeSpeedMultiplier.value,
                    Clouds.erosionSpeedMultiplier.value, Clouds.verticalShapeWindSpeed.value * (timeSeconds % 100000));
                result.CloudMisc = new Vector4(Clouds.verticalErosionWindSpeed.value * (timeSeconds % 100000), curveOffset,
                    -1, Weather.proceduralCoverage.value ? 1 : 0);
                result.CloudMapTiling = new Vector4(Clouds.cloudTiling.value.x, Clouds.cloudTiling.value.y,
                    Clouds.cloudOffset.value.x, Clouds.cloudOffset.value.y);
            }
            return result;
        }
        public void SampleCurves(Vector4[] target, int offset)
        {
            for (int i = 0; i < PlanetMediaMath.CurveSamples; ++i)
            {
                float t = i / (PlanetMediaMath.CurveSamples - 1f);
                float density = Clouds.densityCurve.value == null || Clouds.densityCurve.value.length == 0 ? 1 :
                    (i == 0 || i == PlanetMediaMath.CurveSamples - 1 ? 0 : Mathf.Clamp01(Clouds.densityCurve.value.Evaluate(t)));
                float erosion = Clouds.erosionCurve.value == null ? 1 : Mathf.Clamp01(Clouds.erosionCurve.value.Evaluate(t));
                float ambient = Clouds.ambientOcclusionCurve.value == null ? 1 : 1 - Mathf.Clamp01(Clouds.ambientOcclusionCurve.value.Evaluate(t));
                target[offset + i] = new Vector4(density, erosion, ambient, 1);
            }
        }
        public PlanetMediaTextureSource TextureSource(HDRenderPipeline pipeline)
        {
            bool procedural = Weather.proceduralCoverage.value;
            bool advanced = Clouds.cloudControl.value == VolumetricClouds.CloudControl.Advanced;
            bool manual = Clouds.cloudControl.value == VolumetricClouds.CloudControl.Manual;
            return new PlanetMediaTextureSource
            {
                Mode = procedural ? 1 : (advanced ? 3 : manual ? 2 : 0),
                Map = Clouds.cloudMap.value, Cumulus = Clouds.cumulusMap.value, Stratus = Clouds.altoStratusMap.value,
                Nimbus = Clouds.cumulonimbusMap.value, Rain = Clouds.rainMap.value,
                Lut = advanced ? pipeline.PlanetMediaCloudTypeLut : manual ? Clouds.cloudLut.value : null,
                Multipliers = new Vector3(Clouds.cumulusMapMultiplier.value, Clouds.altoStratusMapMultiplier.value, Clouds.cumulonimbusMapMultiplier.value)
            };
        }
        public void Dispose()
        {
            CoreUtils.Destroy(Clouds); CoreUtils.Destroy(Fog); CoreUtils.Destroy(Weather); CoreUtils.Destroy(Environment);
            CoreUtils.Destroy(defaultClouds); CoreUtils.Destroy(defaultFog); CoreUtils.Destroy(defaultWeather); CoreUtils.Destroy(defaultEnvironment);
        }
    }
}
