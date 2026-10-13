using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Camera-owned spherical water input. Subtract high precision coordinates before publishing it.</summary>
    public sealed class PlanetaryWaterRendering
    {
        public static event Action<Camera, CommandBuffer, RenderTexture> MetricDepthReady;
        public static event Action<Camera, CommandBuffer, ComputeShader, int> BindOpaqueDepth;
        internal static void BindPlanetOpaqueDepth(Camera camera,CommandBuffer cmd,ComputeShader shader,int kernel)
        {
            cmd.SetComputeTextureParam(shader,kernel,"_WaterPlanetOpaqueDepth",Texture2D.blackTexture);
            BindOpaqueDepth?.Invoke(camera,cmd,shader,kernel);
        }
        internal static void PublishMetricDepth(Camera camera, CommandBuffer cmd, RenderTexture source)
            => MetricDepthReady?.Invoke(camera, cmd, source);
        public Camera Observer;
        public double3 CameraFromCenter;
        public double Radius, SeaLevel;
        public Quaternion Rotation = Quaternion.identity;
        public Action<MaterialPropertyBlock> BindShoreline;
        public bool Shoreline;
        public int RenderedFrame { get; internal set; } = -1;
        public int SurfaceIndex { get; internal set; } = -1;
        public void ResetHistory(){RenderedFrame=-1;SurfaceIndex=-1;}
        public double Altitude => math.length(CameraFromCenter) - Radius;
        public bool Matches(Camera camera) => Observer && Observer == camera && Radius > 0 && math.isfinite(Radius) && math.all(math.isfinite(CameraFromCenter));
        public float RenderScale
        {
            get
            {
                double closest = Math.Max(Math.Abs(Altitude), .001);
                return (float)Math.Max(.001, Math.Min(1, Observer ? Observer.nearClipPlane * 2 / closest : 1));
            }
        }
        internal void Fill(ref ShaderVariablesWaterRendering cb)
        {
            // Normalize in double precision: float quaternion error at planetary radii is visible in metres.
            var rotation = math.normalize((double4)((quaternion)Rotation).value);
            var q = new double4(-rotation.xyz, rotation.w);
            var local = CameraFromCenter + 2 * math.cross(q.xyz, math.cross(q.xyz, CameraFromCenter) + q.w * CameraFromCenter);
            var high = (float3)local;
            cb._PlanetWaterOriginHigh = new Vector4(high.x, high.y, high.z, (float)Radius);
            var low = (float3)(local - (double3)high);
            cb._PlanetWaterOriginLow = new Vector4(low.x, low.y, low.z, (float)(Radius - (double)(float)Radius));
            cb._PlanetWaterRotation = new Vector4((float)rotation.x, (float)rotation.y, (float)rotation.z, (float)rotation.w);
            cb._PlanetWaterOptions = new Vector4(1, RenderScale, (float)SeaLevel, Shoreline ? 1 : 0);
            cb._WaterMaxTessellationFactor = 1;
        }
    }
}
