// World Orogen js/sphere-mesh.js port, upstream cc2662b4edd52231c4f65d8765f3ef12cd82d9b7.
// GPL-3.0-only. Original sphere construction credited upstream to Red Blob Games.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Upstream z-pole sphere, closed Delaunay halfedges and winding-preserving CSR adjacency.</summary>
    public sealed class WorldOrogenGraph
    {
        public readonly float3[] Directions;
        public readonly int[] Triangles,Halfedges,Offsets,Neighbors,NeighborTriangles;
        public readonly float[] NeighborDistances;
        public int RegionCount=>Directions.Length;
        public int TriangleCount=>Triangles.Length/3;
        public int SideCount=>Triangles.Length;
        public int MaximumDegree {get;}
        public long EstimatedBytes=>checked((long)RegionCount*12+(long)(Triangles.Length+Halfedges.Length+Offsets.Length+Neighbors.Length+NeighborTriangles.Length)*4+(long)NeighborDistances.Length*4);
        public WorldOrogenGraph(float3[] directions,int[] triangles,int[] halfedges)
        {
            if(directions==null||triangles==null||halfedges==null||triangles.Length!=halfedges.Length||triangles.Length%3!=0)throw new ArgumentException("Invalid closed sphere arrays.");
            Directions=directions;Triangles=triangles;Halfedges=halfedges;
            var first=new int[RegionCount];for(int i=0;i<first.Length;i++)first[i]=-1;
            for(int s=0;s<SideCount;s++)
            {
                if(triangles[s]<0||triangles[s]>=RegionCount||halfedges[s]<0||halfedges[s]>=SideCount||halfedges[halfedges[s]]!=s)throw new ArgumentException("Unpaired or invalid closed halfedge.");
                if(first[triangles[s]]<0)first[triangles[s]]=s;
            }
            Offsets=new int[RegionCount+1];int maxDegree=0;
            for(int r=0;r<RegionCount;r++)
            {
                if(first[r]<0)throw new ArgumentException("A sphere region has no incident triangle.");
                int s=first[r],count=0;do{if(++count>SideCount)throw new ArgumentException("Broken sphere circulation.");s=Next(Halfedges[s]);}while(s!=first[r]);
                Offsets[r+1]=checked(Offsets[r]+count);maxDegree=Math.Max(maxDegree,count);
            }
            MaximumDegree=maxDegree;Neighbors=new int[Offsets[RegionCount]];NeighborTriangles=new int[Neighbors.Length];NeighborDistances=new float[Neighbors.Length];
            for(int r=0;r<RegionCount;r++)
            {
                int s=first[r],at=Offsets[r];do
                {
                    int neighbor=Triangles[Next(s)];Neighbors[at]=neighbor;NeighborTriangles[at]=s/3;
                    var a=Directions[r];var b=Directions[neighbor];double dx=(double)a.x-b.x,dy=(double)a.y-b.y,dz=(double)a.z-b.z;
                    NeighborDistances[at]=(float)Math.Sqrt(dx*dx+dy*dy+dz*dz);at++;s=Next(Halfedges[s]);
                }while(s!=first[r]);
            }
            if(TriangleCount!=2*RegionCount-4||Offsets[RegionCount]!=SideCount)throw new ArgumentException("Sphere Euler invariant differs from upstream closed topology.");
        }
        public static int Next(int side)=>side%3==2?side-2:side+1;
        public float3[] TriangleCenters()
        {
            var result=new float3[TriangleCount];for(int t=0;t<result.Length;t++)
            {var a=Directions[Triangles[t*3]];var b=Directions[Triangles[t*3+1]];var c=Directions[Triangles[t*3+2]];result[t]=new float3((float)(((double)a.x+b.x+c.x)/3),(float)(((double)a.y+b.y+c.y)/3),(float)(((double)a.z+b.z+c.z)/3));}return result;
        }
        public static float3[] Fibonacci(int count,double jitter,WorldOrogenRandom random,Func<bool> cancelled=null)
        {
            if(count<3||count>2560000||!math.isfinite(jitter)||jitter<0||jitter>1||random==null)throw new ArgumentException("Invalid World Orogen sphere settings.");
            var result=new float3[count+1];double spacing=3.6/Math.Sqrt(count),dlong=Math.PI*(3-Math.Sqrt(5)),dz=2.0/count,lng=0,z=1-dz/2;
            for(int k=0;k<count;k++,z-=dz)
            {
                if((k&1023)==0&&cancelled!=null&&cancelled())throw new OperationCanceledException();
                double r=Math.Sqrt(1-z*z),lat=Math.Asin(z)*180/Math.PI,lon=lng*180/Math.PI;
                if(jitter>0){double jLat=random.Next()-random.Next(),jLon=random.Next()-random.Next(),nextZ=Math.Max(-1,z-dz*2*Math.PI*r/spacing);lat+=jitter*jLat*(lat-Math.Asin(nextZ)*180/Math.PI);lon+=jitter*jLon*(spacing/r*180/Math.PI);}
                double latitude=lat*Math.PI/180,longitude=lon*Math.PI/180;
                result[k]=new float3((float)(Math.Cos(latitude)*Math.Cos(longitude)),(float)(Math.Cos(latitude)*Math.Sin(longitude)),(float)Math.Sin(latitude));lng+=dlong;
            }
            result[count]=new float3(0,0,1);return result;
        }
        public static double[] Stereographic(float3[] directions)
        {
            int count=directions.Length-1;var result=new double[count*2];for(int i=0;i<count;i++)
            {double denominator=Math.Max(1e-12,1-(double)directions[i].z);result[i*2]=directions[i].x/denominator;result[i*2+1]=directions[i].y/denominator;}return result;
        }
        public static WorldOrogenGraph ClosePole(float3[] directions,int[] planarTriangles,int[] planarHalfedges)
        {
            int oldLength=planarTriangles.Length,unpaired=0,first=-1;var pointToSide=new int[directions.Length];
            for(int s=0;s<oldLength;s++)if(planarHalfedges[s]==-1){unpaired++;pointToSide[planarTriangles[s]]=s;first=s;}
            if(unpaired<3)throw new ArgumentException("A planar sphere projection needs a closed hull.");
            var triangles=new int[oldLength+3*unpaired];var halfedges=new int[triangles.Length];Array.Copy(planarTriangles,triangles,oldLength);Array.Copy(planarHalfedges,halfedges,oldLength);
            int side=first;for(int i=0;i<unpaired;i++)
            {int ns=oldLength+3*i;halfedges[side]=ns;halfedges[ns]=side;triangles[ns]=triangles[Next(side)];triangles[ns+1]=triangles[side];triangles[ns+2]=directions.Length-1;int k=oldLength+(3*i+4)%(3*unpaired);halfedges[ns+2]=k;halfedges[k]=ns+2;side=pointToSide[triangles[Next(side)]];}
            return new WorldOrogenGraph(directions,triangles,halfedges);
        }
    }
}
