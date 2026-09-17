using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // Cloud coverage for a planet comes from a cube map baked from PlanetaryWeather instead of the flat
        // cloud map, which carries no structure at planetary scale. See PlanetaryWeather.proceduralCoverage.
        internal static readonly int _PlanetCloudMapId = Shader.PropertyToID("_PlanetCloudMap");
        internal static readonly int _PlanetCloudMapActiveId = Shader.PropertyToID("_PlanetCloudMapActive");
        static readonly int _PlanetCloudMapRW = Shader.PropertyToID("_PlanetCloudMapRW");
        static readonly int _PlanetCloudMapResolution = Shader.PropertyToID("_PlanetCloudMapResolution");
        static readonly int _PlanetCloudMapSeed = Shader.PropertyToID("_PlanetCloudMapSeed");
        static readonly int _PlanetCloudMapFrequency = Shader.PropertyToID("_PlanetCloudMapFrequency");
        static readonly int _PlanetCloudCoverage = Shader.PropertyToID("_PlanetCloudCoverage");
        static readonly int _PlanetCloudContrast = Shader.PropertyToID("_PlanetCloudContrast");
        static readonly int _PlanetCloudOpenSky = Shader.PropertyToID("_PlanetCloudOpenSky");
        static readonly int _PlanetCloudBands = Shader.PropertyToID("_PlanetCloudBands");
        static readonly int _PlanetStormCount = Shader.PropertyToID("_PlanetStormCount");
        static readonly int _PlanetStormRadius = Shader.PropertyToID("_PlanetStormRadius");
        static readonly int _PlanetStormStrength = Shader.PropertyToID("_PlanetStormStrength");

        // One texel spans radius/resolution metres at the equator of a cube face; 512 keeps that near ten
        // kilometres on a prototype planet, below which the existing shape noise supplies the detail.
        const int k_PlanetCloudMapResolution = 512;

        RTHandle m_PlanetCloudMap;
        // A compute shader writes six slices of an array; the sampled copy is a cube so that the hardware
        // filters across face borders. Binding a cube directly as the array output is rejected by the device.
        RTHandle m_PlanetCloudMapFaces;
        bool m_PlanetCloudMapActive;
        int m_PlanetCloudMapHash;
        int m_EvaluatePlanetCloudMapKernel;

        void InitializeVolumetricCloudsPlanetMap()
        {
            m_EvaluatePlanetCloudMapKernel = m_Asset.renderPipelineResources.shaders.volumetricCloudMapGeneratorCS.FindKernel("EvaluatePlanetCloudMap");
            m_PlanetCloudMapHash = 0;
        }

        void ReleaseVolumetricCloudsPlanetMap()
        {
            RTHandles.Release(m_PlanetCloudMap);
            RTHandles.Release(m_PlanetCloudMapFaces);
            m_PlanetCloudMap = null;
            m_PlanetCloudMapFaces = null;
            m_PlanetCloudMapActive = false;
        }

        static void BindPlanetCloudMap(CommandBuffer cmd, in VolumetricCloudCommonData commonData, int kernel)
        {
            cmd.SetComputeTextureParam(commonData.volumetricCloudsCS, kernel, _PlanetCloudMapId, commonData.planetCloudMap);
            cmd.SetComputeIntParam(commonData.volumetricCloudsCS, _PlanetCloudMapActiveId, commonData.planetCloudMapActive);
        }

        struct PlanetCloudMapGenerationParameters
        {
            public ComputeShader generationCS;
            public int generationKernel;
            public float seed;
            public float frequency;
            public float coverage;
            public float contrast;
            public float openSky;
            public float bands;
            public int stormCount;
            public float stormRadius;
            public float stormStrength;
        }

        static PlanetCloudMapGenerationParameters PreparePlanetCloudMapGenerationParameters(ComputeShader generationCS, int kernel, PlanetaryWeather weather)
        {
            PlanetCloudMapGenerationParameters parameters = new PlanetCloudMapGenerationParameters();
            parameters.generationCS = generationCS;
            parameters.generationKernel = kernel;
            // Hashing the seed keeps neighbouring generator seeds from producing visibly related planets.
            parameters.seed = (Mathf.Abs(weather.seed.value) % 8192) * 7.13f;
            // A feature of systemScale metres along the surface subtends systemScale/radius radians, so the
            // noise frequency that produces it on the unit direction sphere is radius/systemScale.
            parameters.frequency = Mathf.Clamp(weather.radius.value / Mathf.Max(weather.systemScale.value, 1.0f), 0.35f, 256.0f);
            parameters.coverage = weather.coverage.value;
            parameters.contrast = weather.coverageContrast.value;
            parameters.openSky = weather.openSky.value;
            parameters.bands = weather.climateBands.value;
            parameters.stormCount = weather.stormCount.value;
            // Metres along the surface become the angle the bake works in.
            parameters.stormRadius = Mathf.Clamp(weather.stormScale.value / Mathf.Max(weather.radius.value, 1.0f), 0.002f, 1.2f);
            parameters.stormStrength = weather.stormStrength.value;
            return parameters;
        }

        static void EvaluatePlanetCloudMap(CommandBuffer cmd, PlanetCloudMapGenerationParameters parameters, RTHandle faces, RTHandle output)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.VolumetricCloudMapGeneration)))
            {
                cmd.SetComputeIntParam(parameters.generationCS, _PlanetCloudMapResolution, k_PlanetCloudMapResolution);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudMapSeed, parameters.seed);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudMapFrequency, parameters.frequency);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudCoverage, parameters.coverage);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudContrast, parameters.contrast);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudOpenSky, parameters.openSky);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetCloudBands, parameters.bands);
                cmd.SetComputeIntParam(parameters.generationCS, _PlanetStormCount, parameters.stormCount);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetStormRadius, parameters.stormRadius);
                cmd.SetComputeFloatParam(parameters.generationCS, _PlanetStormStrength, parameters.stormStrength);
                cmd.SetComputeTextureParam(parameters.generationCS, parameters.generationKernel, _PlanetCloudMapRW, faces);
                int groups = (k_PlanetCloudMapResolution + 7) / 8;
                cmd.DispatchCompute(parameters.generationCS, parameters.generationKernel, groups, groups, 6);
                for (int face = 0; face < 6; ++face)
                    cmd.CopyTexture(faces, face, 0, output, face, 0);
            }
        }

        class VolumetricCloudsPlanetMapData
        {
            public PlanetCloudMapGenerationParameters parameters;
            public RTHandle faces;
            public RTHandle map;
        }

        // Runs while the camera's cloud passes are being declared, so FillVolumetricCloudsCommonData sees the
        // state of this camera. The sky and shadow paths are disabled on a planet and keep the previous state.
        void PreRenderVolumetricCloudsPlanetMap(RenderGraph renderGraph, HDCamera hdCamera)
        {
            PlanetaryWeather weather = hdCamera.volumeStack.GetComponent<PlanetaryWeather>();
            m_PlanetCloudMapActive = weather != null && weather.active && weather.ProceduralCoverageActive;
            if (!m_PlanetCloudMapActive)
                return;

            if (m_PlanetCloudMap == null)
            {
                m_PlanetCloudMap = RTHandles.Alloc(k_PlanetCloudMapResolution, k_PlanetCloudMapResolution,
                    colorFormat: GraphicsFormat.R8G8B8A8_UNorm, dimension: TextureDimension.Cube,
                    filterMode: FilterMode.Bilinear, useMipMap: false,
                    useDynamicScale: false, name: "Planetary Cloud Coverage Map");
                m_PlanetCloudMapFaces = RTHandles.Alloc(k_PlanetCloudMapResolution, k_PlanetCloudMapResolution, slices: 6,
                    colorFormat: GraphicsFormat.R8G8B8A8_UNorm, dimension: TextureDimension.Tex2DArray,
                    filterMode: FilterMode.Bilinear, enableRandomWrite: true, useMipMap: false,
                    useDynamicScale: false, name: "Planetary Cloud Coverage Faces");
                m_PlanetCloudMapHash = 0;
            }

            int hash = weather.CoverageHash();
            if (hash == m_PlanetCloudMapHash)
                return;
            m_PlanetCloudMapHash = hash;

            using (var builder = renderGraph.AddRenderPass<VolumetricCloudsPlanetMapData>("Planetary cloud coverage map", out var passData, ProfilingSampler.Get(HDProfileId.VolumetricCloudMapGeneration)))
            {
                builder.EnableAsyncCompute(false);
                // The bake writes an imported texture, so nothing in the graph keeps this pass alive.
                builder.AllowPassCulling(false);
                passData.map = m_PlanetCloudMap;
                passData.faces = m_PlanetCloudMapFaces;
                passData.parameters = PreparePlanetCloudMapGenerationParameters(m_Asset.renderPipelineResources.shaders.volumetricCloudMapGeneratorCS, m_EvaluatePlanetCloudMapKernel, weather);
                builder.SetRenderFunc((VolumetricCloudsPlanetMapData data, RenderGraphContext ctx) => EvaluatePlanetCloudMap(ctx.cmd, data.parameters, data.faces, data.map));
            }
        }
    }
}
