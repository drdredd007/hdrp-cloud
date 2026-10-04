using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Authoring metadata on the deduplicated final cube map. Priority flood establishes an acyclic spill tree;
    /// reference-sphere area is accumulated along that tree. Uphill spill edges have zero downhill tangent.
    /// Captured heights are never changed, and neither flow nor filled height claims a physical discharge simulation.</summary>
    public static class WorldOrogenDetailRouting
    {
        public sealed class Result
        {
            public float4[] Flow {get;internal set;}
            public int UniqueNodes {get;internal set;}
            public int OceanRoots {get;internal set;}
            public int UphillSpillEdges {get;internal set;}
            public double ReferenceArea {get;internal set;}
            public double RootArea {get;internal set;}
        }
        public static long EstimateScratchBytes(int resolution)
        {
            ValidateResolution(resolution);long duplicated=6L*(resolution+1)*(resolution+1),unique=6L*resolution*resolution+2;
            // Maps, four-neighbour adjacency, heights/area/fill, double directions, heap, rank/tree and output.
            return checked(duplicated*(4*2+16)+unique*96+4096);
        }
        public static Result Build(int resolution,double radius,double seaLevel,float[] capturedHeights,long maximumWorkingBytes,
            Func<bool> cancelled=null)
        {
            ValidateResolution(resolution);int n=checked(6*(resolution+1)*(resolution+1));
            if(capturedHeights==null||capturedHeights.Length!=n||!math.isfinite(radius)||radius<=0||!math.isfinite(seaLevel)||
                EstimateScratchBytes(resolution)>maximumWorkingBytes)throw new ArgumentException("Invalid or over-budget final-map routing.");
            Check(cancelled,0);
            for(int i=0;i<n;i++)if(!math.isfinite(capturedHeights[i]))throw new ArgumentException("Nonfinite captured height.");
            var map=new int[n];var ownIds=new int[n];for(int i=0;i<n;i++)ownIds[i]=-1;int count=0;
            for(int face=0;face<6;face++)for(int y=0;y<=resolution;y++)for(int x=0;x<=resolution;x++)
            {
                int id=WorldOrogenDetailField.NodeIndex(resolution,face,x,y),owner=WorldOrogenDetailGrid.CanonicalNode(resolution,face,x,y);
                if(owner==id)ownIds[id]=count++;
            }
            if(count!=6*resolution*resolution+2)throw new InvalidOperationException("Cube ownership did not form a closed sphere.");
            var height=new float[count];var directions=new double3[count];
            var adjacency=new int[checked(count*4)];for(int i=0;i<adjacency.Length;i++)adjacency[i]=-1;
            for(int face=0;face<6;face++)for(int y=0;y<=resolution;y++)for(int x=0;x<=resolution;x++)
            {
                int id=WorldOrogenDetailField.NodeIndex(resolution,face,x,y);int owner=WorldOrogenDetailGrid.CanonicalNode(resolution,face,x,y),u=ownIds[owner];map[id]=u;
                if(capturedHeights[id]!=capturedHeights[owner])throw new ArgumentException("Routing input has inconsistent cube seam heights.");
                if(owner==id){height[u]=capturedHeights[id];CubeSurface.TrySampleDirection(new SurfaceTileKey(face,0,0,0),resolution,x,y,out directions[u]);}
            }
            var area=new double[count];
            for(int face=0;face<6;face++)for(int y=0;y<resolution;y++)for(int x=0;x<resolution;x++)
            {
                int a=map[WorldOrogenDetailField.NodeIndex(resolution,face,x,y)],b=map[WorldOrogenDetailField.NodeIndex(resolution,face,x+1,y)],
                    c=map[WorldOrogenDetailField.NodeIndex(resolution,face,x,y+1)],d=map[WorldOrogenDetailField.NodeIndex(resolution,face,x+1,y+1)];
                Edge(adjacency,a,b);Edge(adjacency,a,c);Edge(adjacency,b,d);Edge(adjacency,c,d);
                double q=(TriangleArea(directions[a],directions[b],directions[d])+TriangleArea(directions[a],directions[d],directions[c]))*.25*radius*radius;
                area[a]+=q;area[b]+=q;area[c]+=q;area[d]+=q;Check(cancelled,face*resolution*resolution+y*resolution+x);
            }
            double total=0;for(int i=0;i<count;i++)total+=area[i];
            var filled=new double[count];var parent=new int[count];var visited=new bool[count];var order=new int[count];var heap=new int[count];int used=0,ordered=0,roots=0;
            for(int i=0;i<count;i++){parent[i]=-1;if(height[i]<=seaLevel){visited[i]=true;filled[i]=height[i];Push(heap,ref used,i,filled);roots++;}}
            if(roots==0)
            {
                int lowest=0;for(int i=1;i<count;i++)if(height[i]<height[lowest])lowest=i;
                visited[lowest]=true;filled[lowest]=height[lowest];Push(heap,ref used,lowest,filled);
            }
            while(used>0)
            {
                int u=Pop(heap,ref used,filled);order[ordered++]=u;Check(cancelled,ordered);
                for(int j=0;j<4;j++){int v=adjacency[u*4+j];if(v<0||visited[v])continue;visited[v]=true;parent[v]=u;filled[v]=math.max(height[v],filled[u]);Push(heap,ref used,v,filled);}
            }
            if(ordered!=count)throw new InvalidOperationException("Final-map routing graph is disconnected.");
            for(int k=count-1;k>=0;k--){int u=order[k];if(parent[u]>=0)area[parent[u]]+=area[u];}
            var compact=new float4[count];int spills=0;double rootArea=0;
            // Normalised log reference area has no rain-rate or volume unit. Scale is one conditioning-cell area.
            double cellArea=4*Math.PI*radius*radius/count,normalizer=Math.Log(1+4*Math.PI*radius*radius/cellArea);
            for(int i=0;i<count;i++)
            {
                double3 tangent=default;int p=parent[i];
                if(p<0)rootArea+=area[i];
                else if(height[p]<height[i]){var delta=directions[p]-directions[i]*math.dot(directions[p],directions[i]);double length=math.length(delta);if(length>0)tangent=delta/length;}
                else if(height[p]>height[i])spills++;
                compact[i]=new float4((float3)tangent,(float)math.clamp(Math.Log(1+area[i]/cellArea)/normalizer,0,1));
            }
            var output=new float4[n];for(int i=0;i<n;i++){output[i]=compact[map[i]];Check(cancelled,i);}
            return new Result{Flow=output,UniqueNodes=count,OceanRoots=roots,UphillSpillEdges=spills,ReferenceArea=total,RootArea=rootArea};
        }
        static void ValidateResolution(int resolution){if(resolution<2||resolution>512||(resolution&(resolution-1))!=0)throw new ArgumentException("Invalid conditioning resolution.");}
        static void Check(Func<bool> cancelled,int i){if((i&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();}
        static double TriangleArea(double3 a,double3 b,double3 c)=>2*Math.Atan2(math.abs(math.dot(a,math.cross(b,c))),1+math.dot(a,b)+math.dot(b,c)+math.dot(c,a));
        static void Edge(int[] edges,int a,int b){Add(edges,a,b);Add(edges,b,a);}
        static void Add(int[] edges,int a,int b){for(int j=0;j<4;j++){int i=a*4+j;if(edges[i]==b)return;if(edges[i]<0){edges[i]=b;return;}}throw new InvalidOperationException("Final cube node exceeds four neighbours.");}
        static bool Less(int a,int b,double[] height)=>height[a]<height[b]||(height[a]==height[b]&&a<b);
        static void Push(int[] heap,ref int count,int id,double[] height){int i=count++;while(i>0){int p=(i-1)>>1;if(!Less(id,heap[p],height))break;heap[i]=heap[p];i=p;}heap[i]=id;}
        static int Pop(int[] heap,ref int count,double[] height)
        {
            int result=heap[0],value=heap[--count],i=0;
            while(i*2+1<count){int c=i*2+1;if(c+1<count&&Less(heap[c+1],heap[c],height))c++;if(!Less(heap[c],value,height))break;heap[i]=heap[c];i=c;}if(count>0)heap[i]=value;return result;
        }
    }
}
