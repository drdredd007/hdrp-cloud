// Port of js/super-plates.js at cc2662b4edd52231c4f65d8765f3ef12cd82d9b7.
// GPL-3.0-only. At most 120 plate vertices: ordered BFS and Dijkstra are explicit CPU topology work.
using System;
using System.Collections.Generic;
using Unity.Mathematics;
namespace UnityEngine.Rendering.HighDefinition
{
    public static class WorldOrogenSuperPlates
    {
        public static WorldOrogenPlateSet Build(WorldOrogenGraph coarse,WorldOrogenPlateSet small,int[] highPlate,Func<bool> cancelled=null)
        {
            int records=small.Motion.Length;var areas=new int[records];var neighbors=new WorldOrogenOrderedSet[records];foreach(int p in small.SeedOrder)neighbors[p]=new WorldOrogenOrderedSet();
            for(int r=0;r<coarse.RegionCount;r++)
            {int p=small.RegionPlate[r];areas[p]++;for(int i=coarse.Offsets[r];i<coarse.Offsets[r+1];i++){int nb=small.RegionPlate[coarse.Neighbors[i]];if(nb!=p)neighbors[p].Add(nb);}}
            var components=new List<List<int>>();var visited=new bool[records];
            foreach(int p in small.SeedOrder)
            {
                if(visited[p])continue;var component=new List<int>{p};visited[p]=true;
                for(int head=0;head<component.Count;head++)foreach(int nb in neighbors[component[head]].Values)
                if(!visited[nb]&&small.Ocean[nb]==small.Ocean[p]){visited[nb]=true;component.Add(nb);}
                components.Add(component);
            }
            int target=Math.Max(2,Math.Min(20,(int)Math.Floor(small.SeedOrder.Length/4.0+.5))),next=0;
            var assignment=new int[records];Array.Fill(assignment,-1);var distance=new double[records];var workVisited=new bool[records];var weights=new double[records];foreach(int p in small.SeedOrder)weights[p]=Math.Sqrt(Math.Max(1,areas[p]));
            foreach(var component in components)
            {
                if(cancelled?.Invoke()==true)throw new OperationCanceledException();
                int k=Math.Max(1,(int)Math.Floor(target*component.Count/(double)small.SeedOrder.Length+.5));
                if(k<=1){foreach(int p in component)assignment[p]=next;next++;continue;}
                var members=new HashSet<int>(component);var seeds=new List<int>{component[0]};
                Distances(component,members,neighbors,weights,seeds,distance,workVisited,null,0);
                for(int i=1;i<k;i++){int farthest=component[0];double best=-1;foreach(int p in component)if(distance[p]>best){best=distance[p];farthest=p;}seeds.Add(farthest);Distances(component,members,neighbors,weights,seeds,distance,workVisited,null,0);}
                Distances(component,members,neighbors,weights,seeds,distance,workVisited,assignment,next);next+=seeds.Count;
            }
            var mapped=new int[highPlate.Length];for(int r=0;r<mapped.Length;r++)mapped[r]=assignment[highPlate[r]];
            var ids=new int[next];for(int i=0;i<next;i++)ids[i]=i;
            var result=new WorldOrogenPlateSet(mapped,ids,next);var angular=new double3[next];var omega=new double[next];var total=new double[next];var largest=new int[next];Array.Fill(largest,-1);
            var oceanArea=new double[next];var densitySum=new double[next];var densityArea=new double[next];
            foreach(int p in small.SeedOrder)
            {
                int sp=assignment[p];double area=areas[p];double4 pv=small.Motion[p];
                angular[sp]+=area*pv.w*pv.xyz;omega[sp]+=area*Math.Abs(pv.w);total[sp]+=area;
                if(largest[sp]<0||area>areas[largest[sp]])largest[sp]=p;
                if(small.Ocean[p])oceanArea[sp]+=area;densitySum[sp]+=area*small.Density[p];densityArea[sp]+=area;
            }
            for(int sp=0;sp<next;sp++)
            {
                double n=math.length(angular[sp]);
                result.Motion[sp]=n<1e-8||total[sp]<1?(largest[sp]>=0?small.Motion[largest[sp]]:new double4(0,1,0,0)):new double4(angular[sp]/n,omega[sp]/total[sp]);
                result.Ocean[sp]=oceanArea[sp]>total[sp]*.5;result.Density[sp]=densityArea[sp]>0?densitySum[sp]/densityArea[sp]:2.7;
            }
            return result;
        }
        static void Distances(List<int> component,HashSet<int> members,WorldOrogenOrderedSet[] neighbors,double[] weights,List<int> seeds,double[] distance,bool[] visited,int[] assignments,int offset)
        {
            foreach(int p in component){distance[p]=double.PositiveInfinity;visited[p]=false;if(assignments!=null)assignments[p]=-1;}
            for(int i=0;i<seeds.Count;i++){distance[seeds[i]]=0;if(assignments!=null)assignments[seeds[i]]=offset+i;}
            for(int iteration=0;iteration<component.Count;iteration++)
            {
                int current=-1;double minimum=double.PositiveInfinity;foreach(int p in component)if(!visited[p]&&distance[p]<minimum){minimum=distance[p];current=p;}
                if(current<0)break;visited[current]=true;
                foreach(int nb in neighbors[current].Values)if(members.Contains(nb))
                {double next=distance[current]+weights[nb];if(next<distance[nb]){distance[nb]=next;if(assignments!=null)assignments[nb]=assignments[current];}}
            }
        }
    }
}
