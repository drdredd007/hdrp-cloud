using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    // Generation and publication are independent of a camera render, so lifecycle can be tested without GPU work.
    public sealed class PlanetSurfaceCache : IDisposable
    {
        List<PlanetPatchKey> active=new List<PlanetPatchKey>();
        readonly Dictionary<PlanetPatchKey,PlanetPatchMesh> cache=new Dictionary<PlanetPatchKey,PlanetPatchMesh>();
        List<PlanetPatchKey> pending;
        int pendingIndex,lastHeight;
        double3 lastCamera;
        float lastFov;
        PlanetLodSettings lastSettings;
        PlanetDefinition generated;
        bool initialized;
        public IReadOnlyList<PlanetPatchKey> Active=>active;
        public bool IsRefining=>pending!=null;
        public int ResidentCount=>cache.Count;
        public PlanetPatchMesh Get(PlanetPatchKey key)=>cache[key];
        public void Update(PlanetDefinition definition,double3 camera,int height,float fov,PlanetLodSettings requested)
        {
            if(!definition.IsValid || !math.all(math.isfinite(camera)) || height<=0 || !math.isfinite(fov))return;
            if(!initialized || generated.Id!=definition.Id || generated.Seed!=definition.Seed || generated.Radius!=definition.Radius ||
                generated.Relief!=definition.Relief || generated.GeneratorVersion!=definition.GeneratorVersion)
            {
                Dispose();
                for(int face=0;face<6;face++)
                {var key=new PlanetPatchKey(face,0,0,0);active.Add(key);cache.Add(key,PlanetPatchMesh.Build(definition,key));}
                generated=definition;initialized=true;
            }
            var settings=requested.Clamped;
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
                if(!cache.ContainsKey(key)){cache.Add(key,PlanetPatchMesh.Build(definition,key));created++;}
            }
            if(pendingIndex<pending.Count)return;
            // One complete covering set replaces another; camera motion cannot cancel pending generation forever.
            active=pending;pending=null;
            var retained=new HashSet<PlanetPatchKey>(active);var obsolete=new List<PlanetPatchKey>();
            foreach(var pair in cache)if(!retained.Contains(pair.Key)){pair.Value.Dispose();obsolete.Add(pair.Key);}
            foreach(var key in obsolete)cache.Remove(key);
        }
        public void Dispose()
        {foreach(var tile in cache.Values)tile.Dispose();cache.Clear();active.Clear();pending=null;initialized=false;lastHeight=0;}
    }
}
