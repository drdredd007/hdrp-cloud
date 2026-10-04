// Ports js/plates.js and js/ocean-land.js from World Orogen commit
// cc2662b4edd52231c4f65d8765f3ef12cd82d9b7. GPL-3.0-only.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class WorldOrogenPlateSet
    {
        public readonly int[] RegionPlate,SeedOrder;
        // Plate records use the original coarse seed IDs, not a reordered dense remap.
        public readonly double4[] Motion;
        public readonly double[] Density;
        public readonly bool[] Ocean;
        public WorldOrogenPlateSet(int[] assignments,int[] seeds,int recordCount)
        {RegionPlate=assignments;SeedOrder=seeds;Motion=new double4[recordCount];Density=new double[recordCount];Ocean=new bool[recordCount];}
    }
    internal sealed class WorldOrogenOrderedSet
    {
        readonly HashSet<int> seen=new HashSet<int>();public readonly List<int> Values=new List<int>();
        public bool Add(int value){if(!seen.Add(value))return false;Values.Add(value);return true;}
        public bool Contains(int value)=>seen.Contains(value);
    }
    /// <summary>Sequential weighted-frontier topology and continent packing; this is an explicit CPU exception.</summary>
    public static class WorldOrogenPlates
    {
        static double3 D(float3 value)=>new double3(value.x,value.y,value.z);
        static void Cancel(Func<bool> cancelled,int step){if((step&1023)==0&&cancelled!=null&&cancelled())throw new OperationCanceledException();}
        public static WorldOrogenPlateSet Generate(WorldOrogenGraph graph,int numberOfPlates,double seed,Func<bool> cancelled=null)
        {
            int n=graph.RegionCount;if(numberOfPlates<1||numberOfPlates>n)throw new ArgumentOutOfRangeException(nameof(numberOfPlates));
            var random=new WorldOrogenRandom(seed+.5);var pickRandom=new WorldOrogenRandom(seed);var seeds=new List<int>();var isSeed=new bool[n];var minimum=new float[n];
            int first=pickRandom.NextInt(n);seeds.Add(first);isSeed[first]=true;var f=D(graph.Directions[first]);
            for(int r=0;r<n;r++)minimum[r]=(float)(1-math.dot(D(graph.Directions[r]),f));minimum[first]=0;
            while(seeds.Count<numberOfPlates)
            {
                int a=-1,b=-1,c=-1;double da=-1,db=-1,dc=-1;
                for(int r=0;r<n;r++){if(isSeed[r])continue;double d=minimum[r];if(d>dc){if(d>da){c=b;dc=db;b=a;db=da;a=r;da=d;}else if(d>db){c=b;dc=db;b=r;db=d;}else{c=r;dc=d;}}}
                int count=(a>=0?1:0)+(b>=0?1:0)+(c>=0?1:0);if(count==0)break;int choice=pickRandom.NextInt(count),s=choice==0?a:choice==1?b:c;
                seeds.Add(s);isSeed[s]=true;var direction=D(graph.Directions[s]);for(int r=0;r<n;r++){double d=1-math.dot(D(graph.Directions[r]),direction);if(d<minimum[r])minimum[r]=(float)d;}
            }
            double low=math.clamp((80-numberOfPlates)/60.0,0,1);var rates=new double[n];var strengths=new double[n];var growth=new double3[n];
            foreach(int s in seeds)
            {
                rates[s]=.7-.4*low+random.Next()*random.Next()*(2.3+2.4*low);var p=D(graph.Directions[s]);double pLength=math.length(p);if(pLength==0)pLength=1;p/=pLength;
                var vector=new double3(random.Next()-.5,random.Next()-.5,random.Next()-.5);vector-=math.dot(vector,p)*p;double tangentLength=math.length(vector);if(tangentLength==0)tangentLength=1;growth[s]=vector/tangentLength;
                strengths[s]=Math.Min(.85,random.Next()*(.15+.25*low+(.25+.25*low)/rates[s]));
            }
            var assigned=new int[n];for(int r=0;r<n;r++)assigned[r]=-1;var frontiers=new List<int>[n];var area=new int[n];
            foreach(int s in seeds){assigned[s]=s;frontiers[s]=new List<int>{s};area[s]=1;}
            int remaining=n-seeds.Count,visits=0;double expected=Math.Max(1,(n-seeds.Count)/(double)numberOfPlates),governor=2+2*low,compact=.3-.22*low;
            while(remaining>0)
            {
                bool progress=false;foreach(int s in seeds)
                {
                    Cancel(cancelled,visits++);var frontier=frontiers[s];if(frontier.Count==0)continue;double strength=strengths[s];int steps=Math.Max(1,(int)Math.Ceiling(rates[s]*(.5+random.Next())));
                    if(area[s]>expected*governor)steps=Math.Max(1,(int)Math.Ceiling(steps*.5));double threshold=Math.Sqrt(area[s]/(double)n/Math.PI)*2*1.8;var position=D(graph.Directions[s]);
                    for(int step=0;step<steps&&frontier.Count>0;step++)
                    {
                        int best=0,samples=Math.Min(frontier.Count,3+(int)Math.Floor(strength*5));double score=double.NegativeInfinity;
                        for(int sample=0;sample<samples;sample++){int at=pickRandom.NextInt(frontier.Count);var d=D(graph.Directions[frontier[at]])-position;double square=math.dot(d,d),length=Math.Sqrt(square);if(length==0)length=1;double candidate=math.dot(d,growth[s])/length*strength+random.Next()*(1-strength*.5)-Math.Max(0,square*.5-threshold)*(compact*4);if(candidate>score){score=candidate;best=at;}}
                        int current=frontier[best];frontier[best]=frontier[frontier.Count-1];frontier.RemoveAt(frontier.Count-1);
                        for(int j=graph.Offsets[current];j<graph.Offsets[current+1];j++){int neighbor=graph.Neighbors[j];if(assigned[neighbor]!=-1)continue;assigned[neighbor]=s;frontier.Add(neighbor);area[s]++;remaining--;progress=true;}
                    }
                }if(!progress)break;
            }
            bool orphans=true;while(orphans){orphans=false;for(int r=0;r<n;r++)if(assigned[r]<0)for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int neighbor=graph.Neighbors[j];if(assigned[neighbor]<0)continue;assigned[r]=assigned[neighbor];orphans=true;break;}}
            SmoothAndReconnect(graph,assigned,seeds.ToArray(),(int)Math.Floor(3-2*low+.5),cancelled);
            var result=new WorldOrogenPlateSet(assigned,seeds.ToArray(),n);foreach(int s in seeds)
            {double theta=random.Next()*2*Math.PI,cos=2*random.Next()-1,sin=Math.Sqrt(1-cos*cos),omega=(.5+random.Next()*1.5)*(random.Next()<.5?-1:1);result.Motion[s]=new double4(sin*Math.Cos(theta),sin*Math.Sin(theta),cos,omega);}
            return result;
        }
        public static void SmoothAndReconnect(WorldOrogenGraph graph,int[] plates,int[] seeds,int passes,Func<bool> cancelled=null)
        {
            int n=graph.RegionCount;var protectedSeeds=new bool[n];foreach(int s in seeds)if(s<n&&plates[s]==s)protectedSeeds[s]=true;
            var identities=new int[graph.MaximumDegree];var counts=new byte[graph.MaximumDegree];
            for(int pass=0;pass<passes;pass++)for(int r=0;r<n;r++)
            {
                Cancel(cancelled,r);int begin=graph.Offsets[r],end=graph.Offsets[r+1],distinct=0;
                for(int j=begin;j<end;j++){int identity=plates[graph.Neighbors[j]],at=0;for(;at<distinct;at++)if(identities[at]==identity)break;if(at<distinct)counts[at]++;else{identities[distinct]=identity;counts[distinct]=1;distinct++;}}
                int best=plates[r],count=0;for(int j=0;j<distinct;j++)if(counts[j]>count){count=counts[j];best=identities[j];}
                if(count>(end-begin)*(pass==0?.4:.5)&&!protectedSeeds[r])plates[r]=best;
            }
            var visited=new bool[n];var bestComponents=new Dictionary<int,List<int>>();
            for(int r=0;r<n;r++)
            {
                Cancel(cancelled,r);if(visited[r])continue;int plate=plates[r];var bfs=new List<int>{r};visited[r]=true;
                for(int q=0;q<bfs.Count;q++){int current=bfs[q];for(int j=graph.Offsets[current];j<graph.Offsets[current+1];j++){int neighbor=graph.Neighbors[j];if(!visited[neighbor]&&plates[neighbor]==plate){visited[neighbor]=true;bfs.Add(neighbor);}}}
                if(!bestComponents.TryGetValue(plate,out var previous)||bfs.Count>previous.Count)bestComponents[plate]=bfs;
            }
            var main=new bool[n];foreach(var component in bestComponents.Values)foreach(int r in component)main[r]=true;var queue=new List<int>();
            for(int r=0;r<n;r++)if(!main[r])for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int neighbor=graph.Neighbors[j];if(!main[neighbor])continue;plates[r]=plates[neighbor];main[r]=true;queue.Add(r);break;}
            for(int q=0;q<queue.Count;q++){Cancel(cancelled,q);int r=queue[q];for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int neighbor=graph.Neighbors[j];if(main[neighbor])continue;plates[neighbor]=plates[r];main[neighbor]=true;queue.Add(neighbor);}}
        }
        readonly struct Score
        {public readonly int Id,Order;public readonly double Value;public Score(int id,double value,int order){Id=id;Value=value;Order=order;}}
        static int CompareScore(Score a,Score b){int c=b.Value.CompareTo(a.Value);return c!=0?c:a.Order.CompareTo(b.Order);}
        public static void AssignOceanLand(WorldOrogenGraph graph,WorldOrogenPlateSet plates,double seed,int continents,double variety,double coverage,Func<bool> cancelled=null)
        {
            var random=new WorldOrogenRandom(seed+42);int n=graph.RegionCount,count=plates.SeedOrder.Length;var area=new int[plates.Motion.Length];var center=new double3[area.Length];var perimeter=new int[area.Length];var adjacent=new WorldOrogenOrderedSet[area.Length];
            foreach(int s in plates.SeedOrder)adjacent[s]=new WorldOrogenOrderedSet();
            for(int r=0;r<n;r++){int p=plates.RegionPlate[r];area[p]++;center[p]+=D(graph.Directions[r]);}
            foreach(int s in plates.SeedOrder)center[s]/=Math.Max(1,area[s]);
            for(int r=0;r<n;r++){Cancel(cancelled,r);int p=plates.RegionPlate[r];bool boundary=false;for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int next=plates.RegionPlate[graph.Neighbors[j]];if(p!=next){adjacent[p].Add(next);boundary=true;}}if(boundary)perimeter[p]++;}
            var compact=new double[area.Length];double maximum=0;foreach(int s in plates.SeedOrder){compact[s]=Math.Sqrt(Math.Max(1,area[s]))/Math.Max(1,perimeter[s]);maximum=Math.Max(maximum,compact[s]);}if(maximum>0)foreach(int s in plates.SeedOrder)compact[s]/=maximum;
            double target=coverage*n;var seeds=new List<int>();var chosen=new HashSet<int>();int first=plates.SeedOrder[random.NextInt(count)];seeds.Add(first);chosen.Add(first);
            for(int s=1;s<Math.Min(continents,count);s++)
            {
                var candidates=new List<Score>();foreach(int p in plates.SeedOrder)
                {if(chosen.Contains(p))continue;double distance=double.PositiveInfinity;foreach(int existing in seeds){var d=center[p]-center[existing];distance=Math.Min(distance,math.dot(d,d));}double areaFactor=1+(Math.Sqrt(n/(double)count)/Math.Sqrt(Math.Max(1,area[p]))-1)*(1-variety*.5);candidates.Add(new Score(p,distance*areaFactor*(.3+.7*compact[p]),candidates.Count));}
                if(candidates.Count==0)break;candidates.Sort(CompareScore);int pick=candidates[random.NextInt(Math.Min(candidates.Count,3))].Id;seeds.Add(pick);chosen.Add(pick);
            }
            double landArea=0;foreach(int s in seeds)landArea+=area[s];while(seeds.Count>1&&landArea>target){int maximumIndex=0;for(int i=1;i<seeds.Count;i++)if(area[seeds[i]]>area[seeds[maximumIndex]])maximumIndex=i;landArea-=area[seeds[maximumIndex]];chosen.Remove(seeds[maximumIndex]);seeds.RemoveAt(maximumIndex);}
            var assignments=new int[area.Length];for(int i=0;i<assignments.Length;i++)assignments[i]=-1;for(int c=0;c<seeds.Count;c++)assignments[seeds[c]]=c;
            double growTarget=target*.9;var targets=new double[seeds.Count];var continentAreas=new double[seeds.Count];for(int c=0;c<seeds.Count;c++)continentAreas[c]=area[seeds[c]];
            if(variety>0&&seeds.Count>1){double total=0;for(int c=0;c<seeds.Count;c++){targets[c]=Math.Exp((random.Next()-.5)*variety*2.5);total+=targets[c];}for(int c=0;c<seeds.Count;c++)targets[c]=growTarget*targets[c]/total;}
            else for(int c=0;c<seeds.Count;c++)targets[c]=growTarget/Math.Max(1,seeds.Count);
            bool progress=true;while(progress&&landArea<growTarget)
            {
                progress=false;for(int c=0;c<seeds.Count&&landArea<growTarget;c++)
                {
                    if(continentAreas[c]>=targets[c])continue;var candidates=new List<Score>();foreach(int p in plates.SeedOrder)
                    {if(assignments[p]>=0)continue;bool self=false,other=false;int same=0;foreach(int neighbor in adjacent[p].Values){int a=assignments[neighbor];if(a==c){self=true;same++;}else if(a>=0){other=true;break;}}if(self&&!other)candidates.Add(new Score(p,same+compact[p]*3+random.Next()*.5,candidates.Count));}
                    if(candidates.Count==0)continue;candidates.Sort(CompareScore);int pick=candidates[random.NextInt(Math.Min(candidates.Count,3))].Id;assignments[pick]=c;continentAreas[c]+=area[pick];landArea+=area[pick];progress=true;
                }
            }
            var visited=new HashSet<int>();var components=new List<List<int>>();foreach(int p in plates.SeedOrder)
            {if(assignments[p]>=0||visited.Contains(p))continue;var component=new List<int>{p};visited.Add(p);for(int q=0;q<component.Count;q++)foreach(int neighbor in adjacent[component[q]].Values)if(assignments[neighbor]<0&&visited.Add(neighbor))component.Add(neighbor);components.Add(component);}
            int main=0;for(int i=1;i<components.Count;i++){double a=0,b=0;foreach(int p in components[i])a+=area[p];foreach(int p in components[main])b+=area[p];if(a>b)main=i;}
            for(int i=0;i<components.Count;i++)
            {if(i==main)continue;var bordering=new WorldOrogenOrderedSet();foreach(int p in components[i]){foreach(int neighbor in adjacent[p].Values)if(assignments[neighbor]>=0)bordering.Add(assignments[neighbor]);if(bordering.Values.Count>1)break;}if(bordering.Values.Count!=1)continue;double a=0;foreach(int p in components[i])a+=area[p];if(landArea+a>target*1.1)continue;foreach(int p in components[i])assignments[p]=bordering.Values[0];landArea+=a;}
            foreach(int s in plates.SeedOrder)plates.Ocean[s]=assignments[s]<0;
        }
    }
}
