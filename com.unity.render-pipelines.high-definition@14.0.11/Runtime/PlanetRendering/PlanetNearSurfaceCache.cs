using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class PlanetNearSurfaceCache : IDisposable
    {
        public const int PatchCount=16;
        public const double HalfSize=1024;
        readonly List<Mesh> active=new List<Mesh>();
        List<Mesh> pending;
        PlanetSurfaceFrame pendingFrame;
        PlanetSurfaceAddress pendingAddress;
        PlanetDefinition generated;
        bool initialized;
        public PlanetSurfaceFrame Frame {get;private set;}
        public IReadOnlyList<Mesh> Meshes=>active;
        public bool IsRefining=>pending!=null;
        public int ResidentCount=>active.Count+(pending?.Count??0);
        public bool Covers(double3 camera,double margin=0)
        {
            var local=Frame.ToLocal(camera);
            return active.Count==PatchCount && math.abs(local.x)<=HalfSize-margin && math.abs(local.z)<=HalfSize-margin;
        }
        public void Update(PlanetDefinition definition,double3 camera,int budget=4)
        {
            if(!definition.IsValid || !math.all(math.isfinite(camera)) || math.lengthsq(camera)<1)return;
            if(!initialized || generated.Seed!=definition.Seed || generated.Radius!=definition.Radius || generated.Relief!=definition.Relief || generated.GeneratorVersion!=definition.GeneratorVersion)
            {Dispose();generated=definition;initialized=true;}
            var local=Frame.ToLocal(camera);
            if(pending==null && (active.Count==0 || math.abs(local.x)>256 || math.abs(local.z)>256))
            {
                var direction=math.normalize(camera);
                pendingAddress=new PlanetSurfaceAddress {Latitude=math.degrees(math.asin(math.clamp(direction.y,-1,1))),Longitude=math.degrees(math.atan2(direction.z,direction.x))};
                if(!PlanetSurfaceCoordinates.TryResolve(definition,pendingAddress,out pendingFrame))return;
                pending=new List<Mesh>();
            }
            if(pending==null)return;
            for(int i=0;i<math.clamp(budget,1,16) && pending.Count<PatchCount;i++)
            {
                int index=pending.Count;
                using(var data=PlanetLocalPatch.Build(definition,pendingAddress,new int2(index%4-2,index/4-2),512,32))
                {
                    var vertices=new Vector3[data.Positions.Length];var normals=new Vector3[vertices.Length];var colors=new Color[vertices.Length];
                    for(int v=0;v<vertices.Length;v++){vertices[v]=data.Positions[v];normals[v]=data.Normals[v];var c=data.Colors[v];colors[v]=new Color(c.x,c.y,c.z,c.w);}
                    var indices=new int[data.Triangles.Length*3];
                    for(int t=0;t<data.Triangles.Length;t++){var triangle=data.Triangles[t];indices[t*3]=triangle.x;indices[t*3+1]=triangle.y;indices[t*3+2]=triangle.z;}
                    var mesh=new Mesh {name="Planet local surface "+index};
                    mesh.vertices=vertices;mesh.normals=normals;mesh.colors=colors;mesh.triangles=indices;mesh.RecalculateBounds();mesh.UploadMeshData(true);pending.Add(mesh);
                }
            }
            if(pending.Count!=PatchCount)return;
            foreach(var mesh in active)CoreUtils.Destroy(mesh);active.Clear();active.AddRange(pending);pending=null;Frame=pendingFrame;
        }
        public void Dispose()
        {
            foreach(var mesh in active)CoreUtils.Destroy(mesh);active.Clear();
            if(pending!=null)foreach(var mesh in pending)CoreUtils.Destroy(mesh);
            pending=null;initialized=false;Frame=default;
        }
    }
}
