using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // 4×4 metric patches around a surface frame for the near layer. Vertices come from an
    // IPlanetPatchBackend with the PlanetLocalPatch layout; colliders keep using PlanetLocalPatch on the CPU.
    public sealed class PlanetNearSurfaceCache : IDisposable
    {
        public const int PatchCount=16;
        public const double HalfSize=1024,PatchSize=512;
        readonly IPlanetPatchBackend backend;
        readonly List<int> active=new List<int>();
        List<int> pending;
        PlanetSurfaceFrame pendingFrame;
        PlanetDefinition generated;
        bool initialized;
        public PlanetNearSurfaceCache(IPlanetPatchBackend backend)
        {
            if(backend==null)throw new ArgumentNullException(nameof(backend));
            if(backend.Layout!=PlanetPatchLayout.Local)throw new ArgumentException("The near layer needs a local-layout backend.",nameof(backend));
            this.backend=backend;
        }
        public IPlanetPatchBackend Backend=>backend;
        public PlanetSurfaceFrame Frame {get;private set;}
        public IReadOnlyList<int> Slots=>active;
        public bool IsRefining=>pending!=null;
        public int ResidentCount=>active.Count+(pending?.Count??0);
        public static int2 PatchKey(int index)=>new int2(index%4-2,index/4-2);
        public bool Covers(double3 camera,double margin=0)
        {
            var local=Frame.ToLocal(camera);
            return active.Count==PatchCount && math.abs(local.x)<=HalfSize-margin && math.abs(local.z)<=HalfSize-margin;
        }
        public bool Covers(PlanetDefinition definition,double3 camera,double margin=0)
            => initialized && definition.IsValid && generated.Seed==definition.Seed && generated.Radius==definition.Radius && generated.Relief==definition.Relief &&
                generated.GeneratorVersion==definition.GeneratorVersion && Covers(camera,margin);
        public void Update(CommandBuffer cmd,PlanetDefinition definition,double3 camera,int budget=PatchCount)
        {
            if(cmd==null || !definition.IsValid || !math.all(math.isfinite(camera)) || math.lengthsq(camera)<1)return;
            bool discarded=backend.Reserve(2*PatchCount);
            if(discarded || !initialized || generated.Seed!=definition.Seed || generated.Radius!=definition.Radius || generated.Relief!=definition.Relief || generated.GeneratorVersion!=definition.GeneratorVersion)
            {Reset();generated=definition;initialized=true;}
            var local=Frame.ToLocal(camera);
            if(pending==null && (active.Count==0 || math.abs(local.x)>256 || math.abs(local.z)>256))
            {
                var direction=math.normalize(camera);
                var address=new PlanetSurfaceAddress {Latitude=math.degrees(math.asin(math.clamp(direction.y,-1,1))),Longitude=math.degrees(math.atan2(direction.z,direction.x))};
                if(!PlanetSurfaceCoordinates.TryResolve(definition,address,out pendingFrame))return;
                pending=new List<int>();
            }
            if(pending==null)return;
            for(int i=0;i<math.clamp(budget,1,PatchCount) && pending.Count<PatchCount;i++)
            {
                int slot=backend.Acquire();pending.Add(slot);
                backend.GenerateLocal(cmd,slot,definition,pendingFrame,PatchKey(pending.Count-1),PatchSize);
            }
            if(pending.Count!=PatchCount)return;
            foreach(var slot in active)backend.Release(slot);
            active.Clear();active.AddRange(pending);pending=null;Frame=pendingFrame;
        }
        void Reset()
        {
            backend.ReleaseAll();active.Clear();pending=null;initialized=false;Frame=default;
        }
        // Releases GPU storage; the cache can be updated again afterwards.
        public void Dispose(){Reset();backend.Dispose();}
    }
}
