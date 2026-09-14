using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // Chooses and publishes complete covering sets of orbital patches. Vertex generation goes to an
    // IPlanetPatchBackend (GPU in rendering), so lifecycle can be tested without a graphics device.
    public sealed class PlanetSurfaceCache : IDisposable
    {
        List<PlanetPatchKey> active=new List<PlanetPatchKey>();
        readonly Dictionary<PlanetPatchKey,int> slots=new Dictionary<PlanetPatchKey,int>();
        readonly IPlanetPatchBackend backend;
        List<PlanetPatchKey> pending;
        int pendingIndex,lastHeight;
        double3 lastCamera;
        float lastFov;
        PlanetLodSettings lastSettings;
        PlanetDefinition generated;
        bool initialized;
        public PlanetSurfaceCache(IPlanetPatchBackend backend)
        {
            if(backend==null)throw new ArgumentNullException(nameof(backend));
            if(backend.Layout!=PlanetPatchLayout.Far)throw new ArgumentException("Orbital patches need a far-layout backend.",nameof(backend));
            this.backend=backend;
        }
        public IPlanetPatchBackend Backend=>backend;
        public IReadOnlyList<PlanetPatchKey> Active=>active;
        public bool IsRefining=>pending!=null;
        public int ResidentCount=>slots.Count;
        public int Slot(PlanetPatchKey key)=>slots[key];
        public static double3 Pivot(in PlanetDefinition definition,PlanetPatchKey key)=>PlanetField.Direction(key,.5,.5)*definition.Radius;
        // Worst case residency: a complete old set plus a complete replacement.
        public static int RequiredSlots(PlanetLodSettings settings)=>2*settings.Clamped.PatchBudget+6;

        public void Update(CommandBuffer cmd,PlanetDefinition definition,double3 camera,int height,float fov,PlanetLodSettings requested)
        {
            if(cmd==null || !definition.IsValid || !math.all(math.isfinite(camera)) || height<=0 || !math.isfinite(fov))return;
            var settings=requested.Clamped;
            bool discarded=backend.Reserve(RequiredSlots(settings));
            if(discarded || !initialized || generated.Id!=definition.Id || generated.Seed!=definition.Seed || generated.Radius!=definition.Radius ||
                generated.Relief!=definition.Relief || generated.GeneratorVersion!=definition.GeneratorVersion)
            {
                Reset();
                for(int face=0;face<6;face++){var key=new PlanetPatchKey(face,0,0,0);active.Add(key);Generate(cmd,definition,key);}
                generated=definition;initialized=true;
            }
            double altitude=math.length(camera)-definition.Radius;
            if(pending==null && (lastHeight!=height || math.distance(lastCamera,camera)>math.max(10,altitude*.02) ||
                math.abs(lastFov-fov)>.1f || !lastSettings.Equals(settings)))
            {
                pending=PlanetLodSelector.Select(definition,camera,height,fov,settings,active);pendingIndex=0;
                lastCamera=camera;lastHeight=height;lastFov=fov;lastSettings=settings;
            }
            if(pending==null)return;
            int created=0;
            while(pendingIndex<pending.Count && created<settings.PatchesPerFrame)
            {
                var key=pending[pendingIndex++];
                if(!slots.ContainsKey(key)){Generate(cmd,definition,key);created++;}
            }
            if(pendingIndex<pending.Count)return;
            // One complete covering set replaces another; camera motion cannot cancel pending generation forever.
            active=pending;pending=null;
            var retained=new HashSet<PlanetPatchKey>(active);var obsolete=new List<PlanetPatchKey>();
            foreach(var pair in slots)if(!retained.Contains(pair.Key))obsolete.Add(pair.Key);
            foreach(var key in obsolete){backend.Release(slots[key]);slots.Remove(key);}
        }
        void Generate(CommandBuffer cmd,in PlanetDefinition definition,PlanetPatchKey key)
        {
            int slot=backend.Acquire();slots.Add(key,slot);
            backend.GenerateFar(cmd,slot,definition,key);
        }
        void Reset(){backend.ReleaseAll();slots.Clear();active.Clear();pending=null;initialized=false;lastHeight=0;}
        // Releases GPU storage; the cache can be updated again afterwards.
        public void Dispose(){Reset();backend.Dispose();}
    }
}
