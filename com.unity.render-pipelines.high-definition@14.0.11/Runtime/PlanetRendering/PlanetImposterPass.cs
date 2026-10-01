using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace UnityEngine.Rendering.HighDefinition
{
 [StructLayout(LayoutKind.Sequential)] public struct PlanetImposter
 {
  public Vector4 Screen; // centre UV, PSF radius pixels, nearest sphere distance metres
  public Vector4 Radiance; // pre-exposure radiance integrated over projected disc, alpha unused
 }
 public sealed class PlanetImposterPass:CustomPass
 {
  public PlanetLayerDepth Depth;
  // PlanetMediaPass owns the final draw when enabled, so a tiny body's flux is
  // attenuated only before its own distance and never becomes an opaque sky pixel.
  public bool DeferredTransport;
  public readonly List<PlanetImposter> Bodies=new List<PlanetImposter>();
  GraphicsBuffer buffer;int capacity;Material material;
  protected override void Setup(ScriptableRenderContext context,CommandBuffer cmd)
  {material=CoreUtils.CreateEngineMaterial(Resources.Load<Shader>("PlanetImposter"));}
  protected override void Execute(CustomPassContext ctx)
  {
   Depth.Publish(ctx.cmd);
   if(DeferredTransport)return;
   Draw(ctx,false);
  }
  public void DrawWithMedia(CustomPassContext ctx)=>Draw(ctx,true);
  void Draw(CustomPassContext ctx,bool media)
  {
   if(Bodies.Count==0)return;
   if(!material)material=CoreUtils.CreateEngineMaterial(Resources.Load<Shader>("PlanetImposter"));
   if(buffer==null||capacity<Bodies.Count){buffer?.Dispose();capacity=Mathf.NextPowerOfTwo(Bodies.Count);buffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,capacity,32);}
   buffer.SetData(Bodies);material.SetBuffer("_PlanetImposters",buffer);material.SetTexture("_PlanetAccumulatedDepth",Depth.Current);
   material.SetFloat("_PlanetMediaTransport",media?1:0);
   CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);ctx.cmd.SetViewport(new Rect(0,0,ctx.hdCamera.actualWidth,ctx.hdCamera.actualHeight));
   ctx.cmd.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Triangles,6,Bodies.Count);
  }
  public void ReleaseResources(){buffer?.Dispose();buffer=null;CoreUtils.Destroy(material);material=null;capacity=0;}
  protected override void Cleanup()=>ReleaseResources();
 }
}
