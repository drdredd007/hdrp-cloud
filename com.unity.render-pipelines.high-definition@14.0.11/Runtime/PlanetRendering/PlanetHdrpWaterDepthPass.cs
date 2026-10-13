using System;
namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Publish HDRP's physical water endpoints before transparent/cloud composition.</summary>
    public sealed class PlanetHdrpWaterDepthPass : CustomPass
    {
        [NonSerialized] public PlanetLayerDepth Depth;
        [NonSerialized] public Camera Observer;
        [NonSerialized] Material material;
        [NonSerialized] MaterialPropertyBlock properties;
        protected override void Setup(ScriptableRenderContext context, CommandBuffer cmd)
        {
            material=CoreUtils.CreateEngineMaterial(Resources.Load<Shader>("PlanetHdrpWaterDepth"));
            properties=new MaterialPropertyBlock();
        }
        protected override void Execute(CustomPassContext ctx)
        {
            if(ctx.hdCamera.camera!=Observer||Depth?.Current==null)return;
            foreach(var owner in PlanetFarPass.LiveOwners)
            {
                var water=owner.HdrpWaterRenderer;
                if(owner.Observer!=Observer||!owner.Enabled||water==null||!water.Active||!water.MetricDepth)continue;
                properties.SetTexture("_HdrpWaterMetricDepth",water.MetricDepth);
                Depth.Merge(ctx,material,properties);
                // Publish the final physical endpoint before native transparent objects.
                CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer,ctx.cameraDepthBuffer);
                CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:2);break;
            }
            Depth.Publish(ctx.cmd);
            PlanetMediaPass.CompositeAfterWater(ctx);
        }
        protected override void Cleanup(){CoreUtils.Destroy(material);material=null;properties=null;}
    }
}
