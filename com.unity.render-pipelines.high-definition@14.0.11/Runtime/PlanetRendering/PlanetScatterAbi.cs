using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>StableId is a fingerprint; the host retains the complete SurfaceScatterKey for persistence.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PlanetScatterCandidateGpu
    {
        public const int Stride=128;
        public uint4 StableId, Meta;
        public double4 PlanetLocalPosition;
        public float4 Rotation, ScaleRadius, MaterialWeights, ErosionData;
        public bool IsAccepted=>Meta.w==1;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PlanetScatterPoseGpu
    {
        public const int Stride=272;
        public Matrix4x4 ObjectToWorld, WorldToObject, PreviousObjectToWorld, PreviousWorldToObject;
        public uint4 Control;
    }
}
