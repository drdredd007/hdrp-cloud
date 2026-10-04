// Elevation.js stage 12 stable rank / stage 13 connectivity, pinned cc2662b4, GPL-3.0-only.
// Only ordering and connectivity are CPU work; the remap/fill values are actual compute kernels.
using System;
using System.Collections.Generic;
namespace UnityEngine.Rendering.HighDefinition
{
    public static class WorldOrogenTopologyFinal
    {
        public static uint[] LandRanks(float[] gpuElevation,out double[] hypsometry,Func<bool> cancelled=null)
        {
            var land=new List<int>();for(int r=0;r<gpuElevation.Length;r++){if((r&4095)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();if(gpuElevation[r]>0)land.Add(r);}
            land.Sort((a,b)=>{int c=gpuElevation[a].CompareTo(gpuElevation[b]);return c==0?a.CompareTo(b):c;});
            var rank=new uint[gpuElevation.Length];Array.Fill(rank,uint.MaxValue);for(int i=0;i<land.Count;i++)rank[land[i]]=(uint)i;
            hypsometry=new[]{(double)land.Count,land.Count>0?(double)gpuElevation[land[0]]:0,land.Count>0?(double)gpuElevation[land[land.Count-1]]-gpuElevation[land[0]]:0};return rank;
        }
        public static uint[] SeaReachable(WorldOrogenGraph graph,float[] gpuElevation,uint[] ocean,Func<bool> cancelled=null)
        {
            var visited=new uint[graph.RegionCount];var queue=new List<int>();for(int r=0;r<graph.RegionCount;r++)if(ocean[r]!=0){visited[r]=1;queue.Add(r);}
            for(int head=0;head<queue.Count;head++){if((head&4095)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=queue[head];for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(visited[nb]==0&&gpuElevation[nb]<=0){visited[nb]=1;queue.Add(nb);}}}return visited;
        }
    }
}
