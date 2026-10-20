using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>A captured geometry producer independent of the HDRP assembly.
    /// Requests own their private output until TakeMesh transfers ownership to
    /// the renderer. Dispose cancels without waiting; the source retains any
    /// unfinished GPU resources. A source is shared across observer cameras.</summary>
    public interface IPlanetNativeMeshRequest:IDisposable
    {
        bool IsCompleted{get;}
        bool TryTakeMesh(out Mesh mesh,out string failure);
    }
    public interface IPlanetNativeMeshPriority
    {
        void SetPriority(double squaredDistanceToReceiver);
    }
    public interface IPlanetNativeMeshSource
    {
        string GeometryIdentity{get;}
        bool Supports(in PlanetDefinition definition,in PlanetNativeSurfaceSettings settings,bool requireMasks,out string failure);
        IPlanetNativeMeshRequest Request(in PlanetDefinition definition,in PlanetSurfaceFrame frame,int2 key,double size);
        // One global source budget, not another budget per camera invocation.
        void Pump(int frame);
    }
    /// <summary>Explicit opt-in for immutable ready-data producers whose completed
    /// vertex buffers remain valid across residency views of the exact same field.
    /// It never transfers requests or changes the exact chart frame/settings.
    /// A ready producer may certify unchanged geometry across physical descriptor
    /// revisions; Supports still admits only explicitly published/staged inputs.</summary>
    public interface IPlanetReadyGeometrySource
    {
        bool CanReuseCompletedGeometry(IPlanetNativeMeshSource previous);
    }
}
