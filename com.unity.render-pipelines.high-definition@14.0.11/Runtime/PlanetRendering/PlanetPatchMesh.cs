using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class PlanetPatchMesh : IDisposable
    {
        public Mesh Mesh {get;private set;}
        public double3 Pivot {get;private set;}
        public static PlanetPatchMesh Build(PlanetDefinition definition,PlanetPatchKey key)
        {
            const int resolution=32,row=33,count=row*row,edgeCount=4*row;
            var result=new PlanetPatchMesh {Pivot=PlanetField.Direction(key,.5,.5)*definition.Radius};
            using(var positions=new NativeArray<float3>(count,Allocator.TempJob))
            using(var normals=new NativeArray<float3>(count,Allocator.TempJob))
            using(var colors=new NativeArray<float4>(count,Allocator.TempJob))
            {
                new PlanetPatchJob {Definition=definition,Key=key,Resolution=resolution,Pivot=result.Pivot,Positions=positions,Normals=normals,Colors=colors}.Schedule(count,64).Complete();
                var vertices=new Vector3[count+edgeCount];var normalArray=new Vector3[vertices.Length];var colorArray=new Color[vertices.Length];
                for(int i=0;i<count;i++){vertices[i]=positions[i];normalArray[i]=normals[i];colorArray[i]=new Color(colors[i].x,colors[i].y,colors[i].z,1);}
                var triangles=new int[resolution*resolution*6+4*resolution*6];int n=0;
                for(int y=0;y<resolution;y++)for(int x=0;x<resolution;x++)
                {
                    int a=y*row+x,b=a+1,c=a+row,d=c+1;
                    triangles[n++]=a;triangles[n++]=b;triangles[n++]=c;triangles[n++]=b;triangles[n++]=d;triangles[n++]=c;
                }
                double span=math.PI/(2*(1<<key.Level));
                double skirtDepth=math.max(10,definition.Relief*.1+definition.Radius*(1-math.cos(span/resolution))*4);
                // Radial skirts cover visual T-junctions between different LODs. They are not collision geometry.
                for(int edge=0;edge<4;edge++)for(int j=0;j<row;j++)
                {
                    int top=edge==0?j:edge==1?j*row+resolution:edge==2?resolution*row+resolution-j:(resolution-j)*row;
                    int bottom=count+edge*row+j;
                    double3 radial=math.normalize((double3)positions[top]/PlanetField.FarScale+result.Pivot);
                    vertices[bottom]=(Vector3)(positions[top]-(float3)(radial*skirtDepth*PlanetField.FarScale));normalArray[bottom]=normalArray[top];colorArray[bottom]=colorArray[top];
                    if(j==resolution)continue;
                    int next=edge==0?top+1:edge==1?top+row:edge==2?top-1:top-row;
                    triangles[n++]=top;triangles[n++]=bottom;triangles[n++]=next;
                    triangles[n++]=next;triangles[n++]=bottom;triangles[n++]=bottom+1;
                }
                result.Mesh=new Mesh {name=$"Planet {definition.Id} {key.Face}/{key.Level}/{key.X}/{key.Y}"};
                result.Mesh.vertices=vertices;result.Mesh.normals=normalArray;result.Mesh.colors=colorArray;result.Mesh.triangles=triangles;
                result.Mesh.RecalculateBounds();result.Mesh.UploadMeshData(true);
            }
            return result;
        }
        public void Dispose(){CoreUtils.Destroy(Mesh);Mesh=null;}
    }
}
