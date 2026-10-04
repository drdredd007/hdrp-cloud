// Original elevation.js stages 1–2, pinned cc2662b4; GPL-3.0-only.
// The random-frontier BFS and ordered boundary catalog are explicit CPU topology exceptions.
// They consume GPU stress/flags; they never evaluate or upload a final elevation value.
using System;
using System.Collections.Generic;
namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class WorldOrogenTectonicFields
    {
        public float[] Stress,Subduct;
        public uint[] Boundary,BothOcean,HasOcean,Mountain,Coast,Ocean;
        public double MaximumStress,Scale;
        internal WorldOrogenOrderedSet MountainSeeds,CoastSeeds,OceanSeeds,StressMountainSeeds;
        public void PrepareSeedMetadata(WorldOrogenPlateSet plates)
        {
            int n=Stress.Length;MountainSeeds=new WorldOrogenOrderedSet();CoastSeeds=new WorldOrogenOrderedSet();OceanSeeds=new WorldOrogenOrderedSet();StressMountainSeeds=new WorldOrogenOrderedSet();
            var representative=new int[plates.Motion.Length];Array.Fill(representative,-1);var sortedStress=new List<float>();double maximum=0;
            for(int r=0;r<n;r++)
            {if(Mountain[r]!=0)MountainSeeds.Add(r);if(Coast[r]!=0&&Mountain[r]==0)CoastSeeds.Add(r);if(Ocean[r]!=0)OceanSeeds.Add(r);if(Mountain[r]==0&&Coast[r]==0&&Ocean[r]==0&&representative[plates.RegionPlate[r]]<0)representative[plates.RegionPlate[r]]=r;maximum=Math.Max(maximum,Stress[r]);if(Stress[r]>.01)sortedStress.Add(Stress[r]);}
            foreach(int p in plates.SeedOrder)if(representative[p]>=0){int r=representative[p];if(plates.Ocean[p]){OceanSeeds.Add(r);Ocean[r]=1;}else{CoastSeeds.Add(r);Coast[r]=1;}}
            foreach(int r in MountainSeeds.Values)if(Subduct[r]<.55)StressMountainSeeds.Add(r);
            sortedStress.Sort();if(sortedStress.Count>0)maximum=sortedStress[Math.Min(sortedStress.Count-1,(int)Math.Floor(sortedStress.Count*.97))];MaximumStress=maximum<.01?1:maximum;Scale=Math.Sqrt(n/10000.0);
        }
    }
    public sealed class WorldOrogenSpatialFields
    {
        public readonly Dictionary<string,Array> Fields=new Dictionary<string,Array>();
        // Identical shared double scalar order used by WorldOrogenElevation.compute.
        public readonly double[] Scalars=new double[16];
        public uint[] OceanFlags=> (uint[])Fields["r_isOcean"];
        static int Round(double x)=>(int)Math.Floor(x+.5);
        static float[] Infinite(int n){var a=new float[n];Array.Fill(a,float.PositiveInfinity);return a;}
        public static WorldOrogenSpatialFields Build(WorldOrogenGraph graph,WorldOrogenPlateSet plates,WorldOrogenPlateSet super,WorldOrogenTectonicFields t,double seed,double noiseMagnitude,Func<bool> cancelled=null)
        {
            int n=graph.RegionCount;var result=new WorldOrogenSpatialFields();var ocean=new uint[n];for(int r=0;r<n;r++)ocean[r]=plates.Ocean[plates.RegionPlate[r]]?1u:0u;result.Fields.Add("r_isOcean",ocean);
            var stop=new bool[n];foreach(int r in t.StressMountainSeeds.Values)stop[r]=true;foreach(int r in t.CoastSeeds.Values)stop[r]=true;foreach(int r in t.OceanSeeds.Values)stop[r]=true;
            bool[] Stops(WorldOrogenOrderedSet set){var a=new bool[n];foreach(int r in set.Values)a[r]=true;return a;}
            result.Fields.Add("dist_mountain",RandomDistance(graph,t.StressMountainSeeds.Values,Stops(t.OceanSeeds),seed+1,cancelled));
            result.Fields.Add("dist_ocean",RandomDistance(graph,t.OceanSeeds.Values,Stops(t.CoastSeeds),seed+2,cancelled));
            result.Fields.Add("dist_coastline",RandomDistance(graph,t.CoastSeeds.Values,stop,seed+3,cancelled));
            var coast=new WorldOrogenOrderedSet();var landCoast=new WorldOrogenOrderedSet();var oceanBarrier=new bool[n];
            for(int r=0;r<n;r++)
            {
                if(ocean[r]!=0){oceanBarrier[r]=true;continue;}
                for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++)if(ocean[graph.Neighbors[i]]!=0){coast.Add(graph.Neighbors[i]);landCoast.Add(r);break;}
            }
            result.Fields.Add("dist_coast",RandomDistance(graph,coast.Values,new bool[n],seed+4,cancelled));
            result.Fields.Add("dist_coast_land",RandomDistance(graph,landCoast.Values,oceanBarrier,seed+5,cancelled));
            int maxCD=Math.Max(8,Round(8*t.Scale));var bdry=new List<int>();var distance=new float[n];Array.Fill(distance,maxCD+1);var stress=new float[n];var subduct=new float[n];var convergence=new uint[n];
            for(int r=0;r<n;r++)for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++)if(ocean[graph.Neighbors[i]]!=ocean[r]){bdry.Add(r);distance[r]=0;stress[r]=(float)Math.Min(1,t.Stress[r]/t.MaximumStress);subduct[r]=t.Subduct[r];convergence[r]=t.Boundary[r]==1?1u:0u;break;}
            for(int head=0;head<bdry.Count;head++)
            {
                if((head&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=bdry[head];float next=distance[r]+1;if(next>maxCD)continue;
                for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(next<distance[nb]){distance[nb]=next;stress[nb]=stress[r];subduct[nb]=subduct[r];convergence[nb]=convergence[r];bdry.Add(nb);}else if(next==distance[nb]&&stress[r]>stress[nb]){stress[nb]=stress[r];subduct[nb]=subduct[r];convergence[nb]=convergence[r];}}
            }
            result.Fields.Add("dBdry",distance);result.Fields.Add("coastStressMax",stress);result.Fields.Add("coastSubductMax",subduct);result.Fields.Add("coastConvergent",convergence);
            int riftWidth=Math.Max(2,Round(3.2*t.Scale)),ridgeWidth=Math.Max(2,Round(4*t.Scale)),fractureWidth=Math.Max(2,Round(3*t.Scale));
            var partition=super?.RegionPlate??plates.RegionPlate;
            result.Fields.Add("riftDist",Band(graph,r=>t.Boundary[r]==2&&t.HasOcean[r]==0,(r,nb)=>partition[nb]==partition[r]&&ocean[nb]==0,riftWidth,cancelled));
            result.Fields.Add("ridgeDist",Band(graph,r=>t.Boundary[r]==2&&t.BothOcean[r]!=0,(r,nb)=>ocean[nb]!=0,ridgeWidth,cancelled));
            result.Fields.Add("fractureDist",Band(graph,r=>t.Boundary[r]==3&&t.BothOcean[r]!=0,(r,nb)=>ocean[nb]!=0,fractureWidth,cancelled));
            int baStart=Math.Max(1,Round(2*t.Scale)),baPeak=Math.Max(2,Round(3*t.Scale)),baEnd=Math.Max(3,Round(5*t.Scale));
            var baDist=Infinite(n);var baStress=new float[n];var ba=new List<int>();
            for(int r=0;r<n;r++)if(t.Boundary[r]==1&&t.HasOcean[r]!=0&&t.Subduct[r]<.5){ba.Add(r);baDist[r]=0;baStress[r]=(float)Math.Min(1,t.Stress[r]/t.MaximumStress);}
            for(int head=0;head<ba.Count;head++){if((head&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=ba[head];float next=baDist[r]+1;if(next>baEnd)continue;for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(next<baDist[nb]&&plates.RegionPlate[nb]==plates.RegionPlate[r]){baDist[nb]=next;baStress[nb]=baStress[r];ba.Add(nb);}}}
            result.Fields.Add("backArcDist",baDist);result.Fields.Add("backArcStress",baStress);
            double[] values={t.MaximumStress,t.Scale,noiseMagnitude,Math.Max(4,Round(16*t.Scale)),Math.Max(6,Round(20*t.Scale)),Math.Max(2,Round(3*t.Scale)),Math.Max(2,Round(5*t.Scale)),Math.Max(1,Round(2*t.Scale)),Math.Max(4,Round(10*t.Scale)),maxCD,riftWidth,ridgeWidth,fractureWidth,baStart,baPeak,baEnd};Array.Copy(values,result.Scalars,16);return result;
        }
        internal static float[] RandomDistance(WorldOrogenGraph graph,List<int> seeds,bool[] stops,double seed,Func<bool> cancelled)
        {
            var random=new WorldOrogenRandom(seed);var distance=Infinite(graph.RegionCount);var queue=new List<int>(seeds);foreach(int r in seeds)distance[r]=0;
            for(int head=0;head<queue.Count;head++){if((head&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int at=head+random.NextInt(queue.Count-head),r=queue[at];queue[at]=queue[head];for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(float.IsPositiveInfinity(distance[nb])&&!stops[nb]){distance[nb]=distance[r]+1;queue.Add(nb);}}}return distance;
        }
        static float[] Band(WorldOrogenGraph graph,Func<int,bool> seed,Func<int,int,bool> admit,int width,Func<bool> cancelled)
        {
            var distance=Infinite(graph.RegionCount);var queue=new List<int>();for(int r=0;r<graph.RegionCount;r++)if(seed(r)){distance[r]=0;queue.Add(r);}
            for(int head=0;head<queue.Count;head++){if((head&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=queue[head];float next=distance[r]+1;if(next>width)continue;for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(next<distance[nb]&&admit(r,nb)){distance[nb]=next;queue.Add(nb);}}}return distance;
        }
    }
}
