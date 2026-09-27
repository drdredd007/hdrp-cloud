using System;
using UnityEngine.Rendering;
namespace UnityEngine.Rendering.HighDefinition
{
 // One pair per camera, independent of planet count. Alpha stores metric ray distance.
 public sealed class PlanetLayerDepth:IDisposable
 {
  RenderTexture a,b;public RenderTexture Current=>a;
  public void Begin(CustomPassContext ctx)
  {
   int w=ctx.hdCamera.actualWidth,h=ctx.hdCamera.actualHeight;
   if(!a||a.width!=w||a.height!=h){Dispose();a=Make(w,h);b=Make(w,h);}
   ctx.cmd.SetRenderTarget(a);ctx.cmd.ClearRenderTarget(false,true,Color.clear);
  }
  static RenderTexture Make(int w,int h){var t=new RenderTexture(w,h,0,RenderTextureFormat.ARGBFloat){name="All planet distances",filterMode=FilterMode.Point};t.Create();return t;}
  public void Merge(CustomPassContext ctx,Material material)
  {
   material.SetTexture("_PlanetAccumulatedDepth",a);ctx.cmd.SetRenderTarget(b);
   CoreUtils.DrawFullScreen(ctx.cmd,material,shaderPassId:1);var old=a;a=b;b=old;
  }
  public void Publish(CommandBuffer cmd)
  {cmd.SetGlobalTexture("_PlanetWeatherFarDistance",a);cmd.SetGlobalInt("_PlanetWeatherHasNear",0);cmd.SetGlobalInt("_PlanetWeatherDepthReady",a?1:0);}
  public void Dispose(){if(a)CoreUtils.Destroy(a);if(b)CoreUtils.Destroy(b);a=b=null;}
 }
 public sealed class PlanetDepthBeginPass:CustomPass
 {
  public PlanetLayerDepth Depth;
  protected override void Execute(CustomPassContext ctx)=>Depth.Begin(ctx);
  protected override void Cleanup()=>Depth?.Dispose();
 }
}
