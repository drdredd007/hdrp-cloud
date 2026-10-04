// Derived from World Orogen js/temperature.js (geographic metadata only).
// cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Integer connectivity and occupancy context. No temperatures or final zones are CPU-generated.</summary>
    internal sealed class WorldOrogenTemperatureContext
    {
        internal int[] Labels,Sizes,WarmDistance;
        internal float4[] Stats;
        internal float2[] Widths;
        internal double4[] Inputs;
        internal int BfsPasses;
        const double Degree=Math.PI/180;
        internal static WorldOrogenTemperatureContext Build(WorldOrogenGraph graph,float4[] geo,float3[] east,uint[] land,
            int[] coastLand,float4[] oceanSummer,double avgEdgeKm,Func<bool> cancelled,Action<long> reserveSparse=null)
        {
            int n=graph.RegionCount;var labels=new int[n];Array.Fill(labels,-1);var queue=new int[n];var sizeList=new List<int>();
            for(int r=0;r<n;r++)
            {
                Check(cancelled,r);if(land[r]==0||labels[r]>=0)continue;int id=sizeList.Count,head=0,tail=1;labels[r]=id;queue[0]=r;
                while(head<tail){Check(cancelled,head);int current=queue[head++];for(int j=graph.Offsets[current];j<graph.Offsets[current+1];j++){int nb=graph.Neighbors[j];if(land[nb]!=0&&labels[nb]<0){labels[nb]=id;queue[tail++]=nb;}}}sizeList.Add(tail);
            }
            int components=sizeList.Count;var stats=new float4[components];var widths=new float2[components];
            var globalRows=new SparseRows(72,reserveSparse);var subcontinentalRows=new SparseRows(720,reserveSparse);var northSouthRows=new SparseRows(360,reserveSparse);var shaveRows=new SparseRows(720,reserveSparse);
            for(int r=0;r<n;r++)
            {
                Check(cancelled,r);if(land[r]==0)continue;int id=labels[r];double latitude=(double)geo[r].x/Degree,absolute=Math.Abs(latitude),lon=((double)geo[r].y+Math.PI)/(2*Math.PI);int hemi=latitude>=0?0:1;
                if(absolute>=35&&absolute<=70){var st=stats[id];if(hemi==0)st.x++;else st.y++;stats[id]=st;globalRows.Add(Row(id,hemi,0,1),Math.Min(71,(int)Math.Floor(lon*72)));}
                if(absolute>=35){var st=stats[id];if(hemi==0)st.z++;else st.w++;stats[id]=st;int row=Math.Min(44,(int)Math.Floor(absolute/2));subcontinentalRows.Add(Row(id,hemi,row,45),Math.Min(719,(int)Math.Floor(lon*720)));}
                int nsrow=Math.Min(89,(int)Math.Floor(absolute));northSouthRows.Add(Row(id,hemi,nsrow,90),Math.Min(359,(int)Math.Floor(lon*360)));
                if(absolute>=30&&absolute<=65){int row=Math.Min(35,(int)Math.Floor(absolute/5));shaveRows.Add((long)id*36+row,Math.Min(719,(int)Math.Floor(lon*720)));}
            }
            double cellArea=avgEdgeKm*avgEdgeKm,cos52=Math.Cos(52.5*Degree);
            for(int id=0;id<components;id++)
            {
                widths[id]=new float2((float)(globalRows.Span(Row(id,0,0,1))*(2*Math.PI/72)*cos52*6371),(float)(globalRows.Span(Row(id,1,0,1))*(2*Math.PI/72)*cos52*6371));
                var st=stats[id];stats[id]=new float4((float)((double)st.x*cellArea),(float)((double)st.y*cellArea),(float)((double)st.z*cellArea),(float)((double)st.w*cellArea));
            }
            var warmSeeds=new List<int>();var eastSeeds=new List<int>();
            for(int r=0;r<n;r++)
            {
                Check(cancelled,r);if(land[r]==0)continue;double ad=Math.Abs((double)geo[r].x)/Degree;bool warm=false,coast=false;double3 direction=0;
                for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int nb=graph.Neighbors[j];if(land[nb]!=0)continue;coast=true;direction+=(double3)graph.Directions[nb]-(double3)graph.Directions[r];if(oceanSummer[nb].w>.3)warm=true;}
                if(ad>10&&ad<=23.5&&warm)warmSeeds.Add(r);
                double length=Math.Sqrt(math.dot(direction,direction));if(coast&&length>1e-10&&math.dot(direction,(double3)east[r])/length>.2)eastSeeds.Add(r);
            }
            int[] warmDist=Bfs(graph,land,warmSeeds,cancelled),eastDist=Bfs(graph,land,eastSeeds,cancelled);
            var inputs=new double4[n];
            for(int r=0;r<n;r++)
            {
                Check(cancelled,r);if(land[r]==0)continue;int id=labels[r];double ld=(double)geo[r].x/Degree,ad=Math.Abs(ld),ln=((double)geo[r].y+Math.PI)/(2*Math.PI);int hemi=ld>=0?0:1;
                int scrow=Math.Min(44,(int)Math.Floor(ad/2));double span=subcontinentalRows.Span(Row(id,hemi,scrow,45))*(2*Math.PI/720)*Math.Cos(geo[r].x)*6371;
                double nsDistance=9999;
                if(ad>=35&&ad<=70)
                {
                    int row=Math.Min(89,(int)Math.Floor(ad)),lon=Math.Min(359,(int)Math.Floor(ln*360)),equatorBands=0;
                    for(int y=row-1;y>=0;y--){bool ocean=false;for(int d=-3;d<=2;d++)if(!northSouthRows.Contains(Row(id,hemi,y,90),Wrap(lon+d,360))){ocean=true;break;}if(ocean)break;equatorBands++;}
                    nsDistance=equatorBands*Degree*6371;
                    if(ad<60){int poleBands=0;for(int y=row+1;y<90;y++){bool ocean=false;for(int d=-3;d<=2;d++)if(!northSouthRows.Contains(Row(id,hemi,y,90),Wrap(lon+d,360))){ocean=true;break;}if(ocean)break;poleBands++;}nsDistance=Math.Min(nsDistance,poleBands*Degree*6371);}
                }
                double westDistance=9999;
                if(ad>=30&&ad<=65){int row=Math.Min(35,(int)Math.Floor(ad/5)),lon=Math.Min(719,(int)Math.Floor(ln*720)),binsFromWest=0;long key=(long)id*36+row;for(int step=1;step<720;step++){if(!shaveRows.Contains(key,Wrap(lon-step,720)))break;binsFromWest=step;}westDistance=binsFromWest*(2*Math.PI/720)*Math.Cos(geo[r].x)*6371;}
                inputs[r]=new double4(span,nsDistance,westDistance,eastDist[r]>=0?eastDist[r]*avgEdgeKm:-1);
            }
            return new WorldOrogenTemperatureContext{Labels=labels,Sizes=sizeList.ToArray(),Stats=stats,Widths=widths,Inputs=inputs,WarmDistance=warmDist,BfsPasses=3};
        }
        static long Row(int component,int hemisphere,int latitude,int rows)=>((long)component*2+hemisphere)*rows+latitude;
        static int Wrap(int x,int n)=>((x%n)+n)%n;
        static void Check(Func<bool> cancelled,int work){if((work&1023)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();}
        static int[] Bfs(WorldOrogenGraph graph,uint[] land,IReadOnlyList<int> seeds,Func<bool> cancelled)
        {int n=graph.RegionCount;var dist=new int[n];Array.Fill(dist,-1);var queue=new int[n];int head=0,tail=0;foreach(int r in seeds){dist[r]=0;queue[tail++]=r;}while(head<tail){Check(cancelled,head);int r=queue[head++],d=dist[r]+1;for(int j=graph.Offsets[r];j<graph.Offsets[r+1];j++){int nb=graph.Neighbors[j];if(land[nb]!=0&&dist[nb]<0){dist[nb]=d;queue[tail++]=nb;}}}return dist;}

        sealed class SparseRows
        {
            readonly int bins;readonly Action<long> reserve;readonly Dictionary<long,HashSet<int>> rows=new Dictionary<long,HashSet<int>>();readonly Dictionary<long,int> spans=new Dictionary<long,int>();
            internal SparseRows(int bins,Action<long> reserve){this.bins=bins;this.reserve=reserve;}
            internal void Add(long row,int longitude){if(!rows.TryGetValue(row,out var values)){reserve?.Invoke(512);values=new HashSet<int>();rows.Add(row,values);}if(!values.Contains(longitude)){reserve?.Invoke(64);values.Add(longitude);}}
            internal bool Contains(long row,int longitude)=>rows.TryGetValue(row,out var values)&&values.Contains(longitude);
            internal int Span(long row)
            {if(spans.TryGetValue(row,out int span))return span;if(!rows.TryGetValue(row,out var values)||values.Count<2){reserve?.Invoke(128);spans[row]=0;return 0;}int gap=0,maximum=0;for(int i=0;i<bins*2;i++){if(!values.Contains(i%bins)){gap++;maximum=Math.Max(maximum,gap);}else gap=0;}span=bins-Math.Min(maximum,bins);reserve?.Invoke(128);spans[row]=span;return span;}
        }

        internal sealed class Patches
        {internal int[] Labels,Offsets,Members;internal float[] Zones;}
        internal static Patches BuildPatches(int[] offsets,int[] neighbors,uint[] land,float[] zones,Func<bool> cancelled)
        {
            int n=land.Length;var labels=new int[n];Array.Fill(labels,-1);var sizes=new List<int>();var levels=new List<float>();var queue=new int[n];
            for(int r=0;r<n;r++)
            {Check(cancelled,r);if(land[r]==0||labels[r]>=0)continue;int id=sizes.Count,head=0,tail=1;float z=(float)(Math.Floor((double)zones[r]*4+.5)/4);levels.Add(z);labels[r]=id;queue[0]=r;while(head<tail){Check(cancelled,head);int current=queue[head++];for(int j=offsets[current];j<offsets[current+1];j++){int nb=neighbors[j];if(land[nb]==0||labels[nb]>=0)continue;float nz=(float)(Math.Floor((double)zones[nb]*4+.5)/4);if(nz==z){labels[nb]=id;queue[tail++]=nb;}}}sizes.Add(tail);}
            var po=new int[sizes.Count+1];for(int i=0;i<sizes.Count;i++)po[i+1]=po[i]+sizes[i];var pm=new int[po[po.Length-1]];var fill=new int[sizes.Count];for(int r=0;r<n;r++)if(labels[r]>=0){int id=labels[r];pm[po[id]+fill[id]++]=r;}
            return new Patches{Labels=labels,Offsets=po,Members=pm,Zones=levels.ToArray()};
        }
    }
}
