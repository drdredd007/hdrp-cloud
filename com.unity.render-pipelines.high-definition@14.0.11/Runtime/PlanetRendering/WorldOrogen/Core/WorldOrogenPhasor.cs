// Elevation stage 5 kernel catalog and original 36x72 grid, cc2662b4, GPL-3.0-only.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
namespace UnityEngine.Rendering.HighDefinition
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WorldOrogenPhasorKernel { public double3 Position,Direction; public double Phase,Stress; }
    public sealed class WorldOrogenPhasor
    {
        public readonly WorldOrogenPhasorKernel[] Kernels;
        public readonly int2[] Ranges;
        public readonly int[] References;
        public WorldOrogenPhasor(WorldOrogenGraph graph,float[] stress,float3[] smoothedDirection,float[] subduct,uint[] ocean,double maxStress,double seed,Func<bool> cancelled=null)
        {
            var candidates=new List<int>();for(int r=0;r<graph.RegionCount;r++)
            {if(ocean[r]!=0||stress[r]/maxStress<.02||subduct[r]>.75)continue;double3 d=(double3)smoothedDirection[r];if((d.x*d.x+d.y*d.y)+d.z*d.z<.25)continue;candidates.Add(r);}
            var rng=new WorldOrogenRandom(seed+1313);for(int i=candidates.Count-1;i>0;i--){int j=(int)Math.Floor(rng.Next()*(i+1)),v=candidates[i];candidates[i]=candidates[j];candidates[j]=v;}
            var kernels=new List<WorldOrogenPhasorKernel>();int count=Math.Min(4000,candidates.Count);
            for(int i=0;i<count;i++)
            {
                if((i&255)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=candidates[i];double3 p=(double3)graph.Directions[r],d=(double3)smoothedDirection[r];double radial=(d.x*p.x+d.y*p.y)+d.z*p.z;d-=radial*p;double n=Math.Sqrt((d.x*d.x+d.y*d.y)+d.z*d.z);if(n<1e-6)continue;d/=n;
                double jitter=(rng.Next()-.5)*2*.22;double3 cross=math.cross(p,d),rotated=d*Math.Cos(jitter)+cross*Math.Sin(jitter);
                kernels.Add(new WorldOrogenPhasorKernel{Position=p,Direction=rotated,Phase=rng.Next()*2*Math.PI,Stress=Math.Min(1,stress[r]/maxStress)});
            }
            Kernels=kernels.ToArray();Ranges=new int2[36*72];var bins=new List<int>[Ranges.Length];
            for(int i=0;i<Kernels.Length;i++)
            {var p=Kernels[i].Position;double lat=Math.Asin(math.clamp(p.y,-1,1)),lon=Math.Atan2(p.x,p.z);int row=math.clamp((int)Math.Floor((lat+Math.PI/2)/Math.PI*36),0,35),col=math.clamp((int)Math.Floor((lon+Math.PI)/(2*Math.PI)*72),0,71),bin=row*72+col;if(bins[bin]==null)bins[bin]=new List<int>();bins[bin].Add(i);}
            var references=new List<int>();for(int i=0;i<bins.Length;i++){Ranges[i]=new int2(references.Count,bins[i]?.Count??0);if(bins[i]!=null)references.AddRange(bins[i]);}References=references.ToArray();
        }
    }
}
