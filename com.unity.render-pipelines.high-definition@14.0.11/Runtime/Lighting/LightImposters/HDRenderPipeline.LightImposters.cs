using System.Collections.Generic;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // Below this on-screen size (pixels), the imposter's quad is clamped to the floor and its alpha
        // fades towards 0 instead of shrinking further - avoids the flicker/aliasing a sub-pixel quad causes.
        const float k_LightImposterMinPixelSize = 2.0f;
        // Hard cap on the on-screen size so a very bright/close imposter doesn't turn into a giant blob.
        const float k_LightImposterMaxPixelSize = 48.0f;
        // rawPixelSize = k_LightImposterSizeScale * sqrt(intensity) / distance. Matches the fact that a
        // point source's received flux falls off with distance^2, so its apparent radius (~sqrt(flux))
        // falls off linearly with distance. The constant is a starting guess (an intensity-1 imposter reads
        // as a small, clearly visible dot around 1-3km and fades out towards 10km+) - tune once visible.
        const float k_LightImposterSizeScale = 20000.0f;
        // Distance past which imposters are fully culled; they fade out over the last 20% of this range.
        const float k_LightImposterMaxDistance = 200000.0f;
        // Maximum instances per DrawMeshInstanced call (Unity's hard limit for this API).
        const int k_LightImposterMaxBatchSize = 1023;

        Mesh m_LightImposterQuadMesh;
        Material m_LightImposterMaterial;
        static readonly int s_ImposterColorId = Shader.PropertyToID("_ImposterColor");

        void InitializeLightImposters()
        {
            m_LightImposterQuadMesh = CreateLightImposterQuadMesh();

            // Loaded by name rather than through the pipeline's serialized resource assets so this first,
            // simple version needs no manual asset wiring in the editor. Editor-only guarantee: revisit with
            // a proper HDRenderPipelineRuntimeShaders entry (and an "Always Included Shaders" note) before
            // relying on this in player builds.
            var shader = Shader.Find("Hidden/HDRP/LightImposter");
            if (shader == null)
            {
                Debug.LogWarning("Light Imposters: could not find shader \"Hidden/HDRP/LightImposter\", the effect will be disabled.");
                return;
            }

            m_LightImposterMaterial = CoreUtils.CreateEngineMaterial(shader);
            m_LightImposterMaterial.enableInstancing = true;
        }

        void ReleaseLightImposters()
        {
            CoreUtils.Destroy(m_LightImposterMaterial);
            CoreUtils.Destroy(m_LightImposterQuadMesh);
        }

        static Mesh CreateLightImposterQuadMesh()
        {
            var mesh = new Mesh { name = "Light Imposter Quad" };
            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0.0f),
                new Vector3(0.5f, -0.5f, 0.0f),
                new Vector3(0.5f, 0.5f, 0.0f),
                new Vector3(-0.5f, 0.5f, 0.0f),
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0.0f, 0.0f),
                new Vector2(1.0f, 0.0f),
                new Vector2(1.0f, 1.0f),
                new Vector2(0.0f, 1.0f),
            };
            mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            // The mesh is billboarded and sized entirely on the CPU per instance (see RenderLightImposters),
            // so its local-space bounds say nothing about the actual world-space footprint - never cull it.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e8f);
            return mesh;
        }

        class LightImposterPassData
        {
            public Material material;
            public Mesh mesh;
            public List<Matrix4x4> matrices;
            public List<Vector4> colors;
        }

        // Batches of camera-facing billboards for LightImposter components too small on screen to be worth a
        // real Light - see LightImposter.cs. Fully CPU-driven (size/fade/culling computed here, not GPU
        // instanced culling) for a first, simple version; revisit with indirect draws if the count grows large.
        void RenderLightImposters(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle colorBuffer, TextureHandle depthBuffer)
        {
            if (m_LightImposterMaterial == null)
                return;

            var imposters = LightImposterManager.manager.imposters;
            if (imposters.Count == 0)
                return;

            var camera = hdCamera.camera;
            Vector3 camPos = camera.transform.position;
            // World-space size of one pixel at one meter of distance, along the vertical FOV.
            float pixelToWorldAtUnitDistance = 2.0f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(hdCamera.actualHeight, 1);

            var matrices = new List<Matrix4x4>(Mathf.Min(imposters.Count, k_LightImposterMaxBatchSize));
            var colors = new List<Vector4>(Mathf.Min(imposters.Count, k_LightImposterMaxBatchSize));

            foreach (var imposter in imposters)
            {
                if (imposter == null || !imposter.isActiveAndEnabled)
                    continue;

                Vector3 toImposter = imposter.transform.position - camPos;
                float distance = toImposter.magnitude;
                if (distance <= 0.01f || distance > k_LightImposterMaxDistance)
                    continue;

                float rawPixelSize = k_LightImposterSizeScale * Mathf.Sqrt(Mathf.Max(imposter.intensity, 0.0f)) / distance;
                if (rawPixelSize <= 0.01f)
                    continue;

                // Fade in as the imposter first becomes visible / fade out again as it shrinks below the
                // floor, instead of popping in/out at a hard cutoff.
                float alpha = Mathf.Clamp01(rawPixelSize / k_LightImposterMinPixelSize);

                // Separate, optional long-range cutoff so the effect does not draw forever into the distance.
                float t = Mathf.InverseLerp(k_LightImposterMaxDistance * 0.8f, k_LightImposterMaxDistance, distance);
                alpha *= 1.0f - t * t * (3.0f - 2.0f * t);
                if (alpha <= 0.001f)
                    continue;

                float pixelSize = Mathf.Clamp(rawPixelSize, k_LightImposterMinPixelSize, k_LightImposterMaxPixelSize);
                float worldSize = pixelSize * distance * pixelToWorldAtUnitDistance;

                Quaternion rotation = Quaternion.LookRotation(-toImposter, camera.transform.up);
                matrices.Add(Matrix4x4.TRS(imposter.transform.position, rotation, Vector3.one * worldSize));

                Color linearColor = imposter.color.linear;
                colors.Add(new Vector4(linearColor.r, linearColor.g, linearColor.b, alpha));

                if (matrices.Count == k_LightImposterMaxBatchSize)
                {
                    FlushLightImposterBatch(renderGraph, colorBuffer, depthBuffer, matrices, colors);
                    matrices.Clear();
                    colors.Clear();
                }
            }

            if (matrices.Count > 0)
                FlushLightImposterBatch(renderGraph, colorBuffer, depthBuffer, matrices, colors);
        }

        void FlushLightImposterBatch(RenderGraph renderGraph, TextureHandle colorBuffer, TextureHandle depthBuffer,
            List<Matrix4x4> matrices, List<Vector4> colors)
        {
            using (var builder = renderGraph.AddRenderPass<LightImposterPassData>("Light Imposters", out var passData))
            {
                builder.UseColorBuffer(colorBuffer, 0);
                builder.UseDepthBuffer(depthBuffer, DepthAccess.Read);

                passData.material = m_LightImposterMaterial;
                passData.mesh = m_LightImposterQuadMesh;
                // SetRenderFunc executes later, once the graph runs - snapshot the batch instead of handing
                // over the caller's lists, which get cleared/reused for the next batch right after this call.
                passData.matrices = new List<Matrix4x4>(matrices);
                passData.colors = new List<Vector4>(colors);

                builder.SetRenderFunc(
                    (LightImposterPassData data, RenderGraphContext ctx) =>
                    {
                        var mpb = new MaterialPropertyBlock();
                        mpb.SetVectorArray(s_ImposterColorId, data.colors);
                        ctx.cmd.DrawMeshInstanced(data.mesh, 0, data.material, 0, data.matrices.ToArray(), data.matrices.Count, mpb);
                    });
            }
        }
    }
}
