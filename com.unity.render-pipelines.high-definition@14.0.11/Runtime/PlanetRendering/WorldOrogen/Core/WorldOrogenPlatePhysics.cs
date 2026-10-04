// Original js/plate-physics.js, cc2662b4edd52231c4f65d8765f3ef12cd82d9b7.
// GPL-3.0-only. Small plate decisions/reductions retain the original iteration order;
// the per-region mantle flow and signed field are computed by MantleFlow on the GPU.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WorldOrogenMantleCell { public double3 Position; public double RadialSign, RotationSign, Strength; }
    public sealed class WorldOrogenPlatePhysics
    {
        readonly WorldOrogenGraph graph; readonly WorldOrogenPlateSet plates; readonly double blend;
        readonly int[] areas; readonly double3[] centroids;
        readonly List<Boundary> boundaries=new List<Boundary>();
        public WorldOrogenMantleCell[] Cells {get;}
        struct Boundary {public int A,B; public List<double3> Points;}
        static double Length(double3 x)=>Math.Sqrt((x.x*x.x+x.y*x.y)+x.z*x.z);
        static double Dot(double3 a,double3 b)=>(a.x*b.x+a.y*b.y)+a.z*b.z;
        static double3 Unit(double3 x){double n=Length(x);return n>1e-12?x/n:new double3(0,0,1);}
        static double3 Velocity(double4 pv,double3 point)=>pv.w*math.cross(pv.xyz,point);
        static double3 Bias(double4 pv,double3 center,double3 direction,double blend)
        {
            double3 current=Velocity(pv,center),target=Unit(current+direction*(blend*Length(current)+.01));
            double3 cross=math.cross(center,target);if(Length(cross)<1e-10)return pv.xyz;
            double3 candidate=Unit(cross),pole=Unit(pv.xyz+(candidate-pv.xyz)*blend);
            return Dot(pole,pv.xyz)<0?-pole:pole;
        }
        public WorldOrogenPlatePhysics(WorldOrogenGraph source,WorldOrogenPlateSet plateSet,double seed,double blendMultiplier=1,Func<bool> cancelled=null)
        {
            graph=source;plates=plateSet;blend=blendMultiplier;areas=new int[plates.Motion.Length];centroids=new double3[areas.Length];
            for(int r=0;r<graph.RegionCount;r++){if((r&4095)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int p=plates.RegionPlate[r];areas[p]++;centroids[p]+=(double3)graph.Directions[r];}
            foreach(int p in plates.SeedOrder)centroids[p]=Unit(centroids[p]/Math.Max(1,areas[p]));
            var orderedPairs=new Dictionary<long,int>();
            for(int r=0;r<graph.RegionCount;r++)for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++)
            {
                int nb=graph.Neighbors[i],a=plates.RegionPlate[r],b=plates.RegionPlate[nb];if(a>=b)continue;long key=((long)a<<32)|(uint)b;
                if(!orderedPairs.TryGetValue(key,out int index)){index=boundaries.Count;orderedPairs.Add(key,index);boundaries.Add(new Boundary{A=a,B=b,Points=new List<double3>()});}
                boundaries[index].Points.Add(((double3)graph.Directions[r]+(double3)graph.Directions[nb])*.5);
            }
            double mean=0,variance=0;int landCount=0;
            foreach(int p in plates.SeedOrder)if(!plates.Ocean[p]){mean+=areas[p];landCount++;}
            if(landCount>0)mean/=landCount;
            foreach(int p in plates.SeedOrder)if(!plates.Ocean[p]){double d=areas[p]-mean;variance+=d*d;}
            double std=landCount>0?Math.Sqrt(variance/landCount):1;if(std==0)std=1;
            double average=graph.RegionCount/(double)plates.SeedOrder.Length;
            foreach(int p in plates.SeedOrder)
            {double drag=plates.Ocean[p]?1:.35+.65*Math.Min(1,Math.Max(0,(mean-areas[p])/std)/2);double size=math.clamp(1/Math.Pow(areas[p]/average,.5),.4,2.5);double4 pv=plates.Motion[p];pv.w*=drag*size;plates.Motion[p]=pv;}
            var convergent=new List<double3>();
            foreach(var b in boundaries)
            {
                int count=0;double3 n=Unit(centroids[b.B]-centroids[b.A]);
                foreach(var pt in b.Points)if(-Dot(Velocity(plates.Motion[b.A],pt)-Velocity(plates.Motion[b.B],pt),n)>.05)count++;
                if(count>b.Points.Count*.4)convergent.AddRange(b.Points);
            }
            var rng=new WorldOrogenRandom(seed+9999);var placed=new List<double3>();var cells=new List<WorldOrogenMantleCell>();
            int down=Math.Min(3,convergent.Count),up=5-down;
            if(convergent.Count>0)
            {
                placed.Add(Unit(convergent[(int)Math.Floor(rng.Next()*convergent.Count)]));
                for(int i=1;i<down;i++)
                {double best=-1;double3 value=default;bool found=false;foreach(var pt in convergent){double3 n=Unit(pt);double distance=double.PositiveInfinity;foreach(var c in placed)distance=Math.Min(distance,1-Dot(n,c));if(distance>=.6&&distance>best){best=distance;value=n;found=true;}}if(found)placed.Add(value);}
                for(int i=0;i<placed.Count;i++)cells.Add(new WorldOrogenMantleCell{Position=placed[i],RadialSign=-1,RotationSign=rng.Next()<.5?1:-1,Strength=i==0?2:.7});
            }
            var candidates=new List<double3>();int stride=Math.Max(1,graph.RegionCount/400);
            for(int r=0;r<graph.RegionCount;r+=stride)candidates.Add(Unit((double3)graph.Directions[r]));
            for(int i=0;i<up;i++)
            {
                double best=-1;double3 value=default;bool found=false;
                foreach(var pt in candidates){double distance=double.PositiveInfinity;foreach(var c in placed)distance=Math.Min(distance,1-Dot(pt,c));if(distance>=.6&&distance>best){best=distance;value=pt;found=true;}}
                if(!found)foreach(var pt in candidates){double distance=double.PositiveInfinity;foreach(var c in placed)distance=Math.Min(distance,1-Dot(pt,c));if(distance>best){best=distance;value=pt;found=true;}}
                if(found){placed.Add(value);cells.Add(new WorldOrogenMantleCell{Position=value,RadialSign=1,RotationSign=rng.Next()<.5?1:-1,Strength=i==0?2:.7});}
            }
            if(cells.Count==0)for(int i=0;i<5;i++)
            {double theta=rng.Next()*2*Math.PI,c=2*rng.Next()-1,s=Math.Sqrt(1-c*c);cells.Add(new WorldOrogenMantleCell{Position=new double3(s*Math.Cos(theta),s*Math.Sin(theta),c),RadialSign=i%2==0?1:-1,RotationSign=rng.Next()<.5?1:-1,Strength=i<2?2:.7});}
            Cells=cells.ToArray();
        }
        public void Complete(double3[] gpuFlow,Func<bool> cancelled=null)
        {
            if(gpuFlow.Length!=graph.RegionCount)throw new ArgumentException("Mantle result length mismatch.");
            var flows=new double3[areas.Length];
            for(int r=0;r<gpuFlow.Length;r++){if((r&4095)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();flows[plates.RegionPlate[r]]+=gpuFlow[r];}
            foreach(int p in plates.SeedOrder)
            {
                if(Length(flows[p])<1e-10)continue;double3 fd=Unit(flows[p]),t=fd-centroids[p]*Dot(fd,centroids[p]);if(Length(t)<1e-10)continue;
                double4 pv=plates.Motion[p];pv.xyz=Bias(pv,centroids[p],Unit(t),Math.Min(.9,.45*blend));plates.Motion[p]=pv;
            }
            foreach(int p in plates.SeedOrder)
            {
                if(Length(flows[p])<1e-10)continue;double4 pv=plates.Motion[p];double3 velocity=Velocity(pv,centroids[p]);if(Length(velocity)<1e-10)continue;
                double3 tangent=flows[p]-centroids[p]*Dot(flows[p],centroids[p]);if(Length(tangent)<1e-10)continue;
                pv.w*=1+.35*Math.Max(0,Dot(Unit(velocity),Unit(tangent)));plates.Motion[p]=pv;
            }
            var convergent=new List<double3>[areas.Length];var divergent=new List<double3>[areas.Length];foreach(int p in plates.SeedOrder){convergent[p]=new List<double3>();divergent[p]=new List<double3>();}
            foreach(var b in boundaries)
            {
                int cc=0,dc=0;double3 c=0,d=0,n=Unit(centroids[b.B]-centroids[b.A]);
                foreach(var pt in b.Points){double value=-Dot(Velocity(plates.Motion[b.A],pt)-Velocity(plates.Motion[b.B],pt),n);if(value>.05){cc++;c+=pt;}else if(value<-.05){dc++;d+=pt;}}
                if(cc>b.Points.Count*.3){c/=cc;if(plates.Ocean[b.A])convergent[b.A].Add(c);if(plates.Ocean[b.B])divergent[b.B].Add(c);}
                if(dc>b.Points.Count*.3){d/=dc;divergent[b.A].Add(d);divergent[b.B].Add(d);}
            }
            foreach(int p in plates.SeedOrder)if(plates.Ocean[p]&&convergent[p].Count>0)ApplyBoundary(p,convergent[p],true,Math.Min(.9,.65*blend));
            foreach(int p in plates.SeedOrder)if(divergent[p].Count>0)ApplyBoundary(p,divergent[p],false,Math.Min(.9,.4*blend));
            foreach(int p in plates.SeedOrder){double4 pv=plates.Motion[p];pv.xyz=Unit(pv.xyz);plates.Motion[p]=pv;}
        }
        void ApplyBoundary(int p,List<double3> points,bool pull,double b)
        {double3 sum=0;foreach(var v in points)sum+=v;double3 center=Unit(sum/points.Count),direction=Unit(pull?center-centroids[p]:centroids[p]-center);double3 tangent=direction-centroids[p]*Dot(direction,centroids[p]);if(Length(tangent)<1e-10)return;double4 pv=plates.Motion[p];pv.xyz=Bias(pv,centroids[p],Unit(tangent),b);plates.Motion[p]=pv;}
    }
}
