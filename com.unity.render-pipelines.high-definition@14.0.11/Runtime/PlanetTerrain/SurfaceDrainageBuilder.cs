using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>
    /// Offline graph construction. A priority flood connects every terrestrial catchment to an ocean outlet;
    /// stream-power gradients and recursively joined tributaries are captured as immutable records.
    /// This is a landform model, not a simulation of geological time or a hydrodynamic river renderer.
    /// </summary>
    internal static class SurfaceDrainageBuilder
    {
        internal static SurfaceDrainageField Build(SurfaceRecipe recipe,SurfaceBakeSettings settings,
            SurfaceGeomorphology geometry,SurfaceBakeGraph graph,out double[] macro,Func<bool> cancelled)
        {
            // Routing samples the declared geological controls independently of the erosion/storage grid.
            // A low-resolution preview must not silently omit a narrow analytic belt between its texels.
            int routingResolution=settings.ResolvedDrainageTopologyResolution(recipe);
            var routing=routingResolution==graph.Resolution?graph:new SurfaceBakeGraph(routingResolution,recipe.Radius,cancelled);
            BuildCoasts(recipe,settings,routing,out var masses,out var vertices,cancelled);
            double influence=geometry.ShelfWidthMetres*4;
            var coast=new SurfaceDrainageField(recipe,masses,vertices,Array.Empty<SurfaceDrainageNode>(),cancelled,influence);
            var plates=new SurfaceGeologicalProvince[geometry.Provinces.Count];var edges=new SurfaceGeologicalBoundary[geometry.Boundaries.Count];
            for(int i=0;i<plates.Length;i++)plates[i]=geometry.Provinces[i];for(int i=0;i<edges.Length;i++)edges[i]=geometry.Boundaries[i];
            var placeholder=new float[24];for(int i=0;i<placeholder.Length;i++)placeholder[i]=(float)recipe.SeaLevel;
            var controls=new SurfaceStructuralField(recipe,geometry.ContentDigest,1,placeholder,plates,edges,
                geometry.ShelfWidthMetres,geometry.BeltWidthMetres,geometry.RegionalFeatureScaleMetres,0,
                geometry.MountainUpliftFraction,0,0,cancelled,coast);
            macro=new double[graph.NodeCount];
            using(var native=new NativeSurfaceStructuralData(controls,Allocator.Persistent))
            {
                for(int i=0;i<macro.Length;i++)
                {
                    SurfaceBaker.CheckCancelled(cancelled,i);
                    if(SurfaceStructuralMath.TrySampleBaseFour(native.View,graph.Directions[i],out macro[i],out _,out _)!=SurfaceSampleStatus.Ready)
                        throw new InvalidOperationException("Captured coast/uplift authority is not ready.");
                }
                var routingMacro=routing==graph?macro:new double[routing.NodeCount];
                if(routing!=graph)for(int i=0;i<routingMacro.Length;i++)
                {
                    SurfaceBaker.CheckCancelled(cancelled,i);
                    if(SurfaceStructuralMath.TrySampleBaseFour(native.View,routing.Directions[i],out routingMacro[i],out _,out _)!=SurfaceSampleStatus.Ready)
                        throw new InvalidOperationException("Captured drainage routing requires its complete admitted geological sample graph.");
                }
                var nodes=BuildDrainage(recipe,geometry,routing,routingMacro,native.View,cancelled);
                return new SurfaceDrainageField(recipe,masses,vertices,nodes,cancelled,influence);
            }
        }

        internal static void BuildCoasts(SurfaceRecipe recipe,SurfaceBakeSettings settings,SurfaceBakeGraph graph,
            out SurfaceCoastLandmass[] masses,out double2[] vertices,Func<bool> cancelled)
        {
            int count=(int)math.clamp(math.round(settings.ContinentScaleMetres>0?
                2*Math.PI*recipe.Radius*recipe.Radius/(settings.ContinentScaleMetres*settings.ContinentScaleMetres):settings.ProvinceCount/5.0),3,8);
            const int controls=64;
            masses=new SurfaceCoastLandmass[count];vertices=new double2[count*controls];
            double3 axis=math.normalize(new double3(Random(recipe.Seed,0,1)*2-1,Random(recipe.Seed,0,2)*2-1,Random(recipe.Seed,0,3)*2-1));
            double rotation=Random(recipe.Seed,0,4)*2*Math.PI;
            double theta=Math.Acos(math.clamp(1-2*math.max(.02,settings.LandFraction)/count,-1,1));
            double radius=recipe.Radius*Math.Tan(math.min(.9,theta));
            for(int m=0;m<count;m++)
            {
                SurfaceBaker.CheckCancelled(cancelled,m);
                double y=1-2*(m+.5)/count,a=m*Math.PI*(3-Math.Sqrt(5));
                var centre=Rotate(new double3(Math.Sqrt(1-y*y)*Math.Cos(a),y,Math.Sqrt(1-y*y)*Math.Sin(a)),axis,rotation);
                var reference=math.abs(centre.y)<.9?new double3(0,1,0):new double3(1,0,0);
                var right=math.normalize(math.cross(reference,centre));var forward=math.cross(centre,right);
                masses[m]=new SurfaceCoastLandmass(centre,right,forward,m*controls,controls);
                double phase=Random(recipe.Seed,m,7)*2*Math.PI;
                double stretch=.8+.4*Random(recipe.Seed,m,8);
                for(int j=0;j<controls;j++)
                {
                    double angle=2*Math.PI*j/controls;
                    // Captured lobes at several geological scales form bays, necks and promontories.
                    // Their longitude/latitude do not depend on the Voronoi plate labels.
                    double shape=1+.22*Math.Sin(3*angle+phase)+.13*Math.Sin(7*angle-phase*.7)+.06*Math.Sin(13*angle+phase*1.3);
                    vertices[m*controls+j]=radius*shape*new double2(Math.Cos(angle)*stretch,Math.Sin(angle)/stretch);
                }
            }
            // Calibrate the actual polygon union by reference-sphere area, once offline. A point's
            // inclusion threshold is computed against a ray/edge intersection, not a nearest plate ID.
            var required=new double[graph.NodeCount];var order=new int[graph.NodeCount];
            for(int i=0;i<required.Length;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);required[i]=double.PositiveInfinity;order[i]=i;
                var d=graph.Directions[i];
                for(int m=0;m<count;m++)
                {
                    var mass=masses[m];double facing=math.dot(d,mass.Center);if(facing<=0)continue;
                    var p=new double2(math.dot(d,mass.Right),math.dot(d,mass.Forward))*(recipe.Radius/facing);
                    double length=math.length(p);if(length<1e-20){required[i]=0;continue;}
                    var ray=p/length;
                    for(int j=0;j<controls;j++)
                    {
                        var first=vertices[m*controls+j];var last=vertices[m*controls+(j+1)%controls];
                        double denominator=Cross(ray,last-first);if(math.abs(denominator)<1e-20)continue;
                        double t=Cross(first,last-first)/denominator,u=Cross(first,ray)/denominator;
                        if(t>0&&u>=0&&u<=1)required[i]=math.min(required[i],length/t);
                    }
                }
            }
            Array.Sort(order,(a,b)=>{int c=required[a].CompareTo(required[b]);return c!=0?c:a.CompareTo(b);});
            double area=0,total=4*Math.PI*recipe.Radius*recipe.Radius,target=total*settings.LandFraction;
            double scale=1;
            foreach(int i in order){area+=graph.Areas[i];scale=required[i];if(area>=target)break;}
            if(!math.isfinite(scale)||scale<=0)
                throw new ArgumentException("Requested land fraction cannot be represented by the captured hemisphere coast graph at this resolution.");
            double maximum=0;foreach(var vertex in vertices)maximum=math.max(maximum,math.length(vertex));
            if(scale>recipe.Radius*2.9/maximum)
                throw new ArgumentException("Requested land fraction exceeds the admitted metric hemisphere coast radius; no tiny-island or clamped-area substitution is published.");
            for(int i=0;i<vertices.Length;i++)vertices[i]*=scale;
        }

        static SurfaceDrainageNode[] BuildDrainage(SurfaceRecipe recipe,SurfaceGeomorphology geometry,
            SurfaceBakeGraph graph,double[] macro,in NativeSurfaceStructuralView controls,Func<bool> cancelled)
        {
            int n=graph.NodeCount;var parent=new int[n];var order=new int[n];var flooded=new double[n];
            var area=(double[])graph.Areas.Clone();var marked=new bool[n];var selected=new bool[n];var streamOrder=new int[n];
            CreateDiagonals(graph,recipe.Seed,out var diagonals,out var diagonalCount,cancelled);
            var heap=new FloodHeap();double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight);
            int lowest=0;for(int i=0;i<n;i++){parent[i]=-2;streamOrder[i]=1;if(macro[i]<macro[lowest])lowest=i;}
            for(int i=0;i<n;i++)if(macro[i]<=sea){parent[i]=-1;flooded[i]=macro[i];heap.Push(i,macro[i]);}
            if(heap.Count==0){parent[lowest]=-1;flooded[lowest]=macro[lowest];heap.Push(lowest,macro[lowest]);}
            int visits=0;var neighbours=new int[24];
            while(heap.Count>0)
            {
                SurfaceBaker.CheckCancelled(cancelled,visits);heap.Pop(out int i,out _);order[visits++]=i;
                int count=0;for(int j=0;j<graph.Degrees[i];j++)neighbours[count++]=graph.Neighbours[i][j];
                for(int j=0;j<diagonalCount[i];j++)neighbours[count++]=diagonals[i][j];
                for(int j=0;j<count;j++)
                {
                    int next=neighbours[j];if(parent[next]!=-2)continue;
                    parent[next]=i;flooded[next]=math.max(macro[next],flooded[i]);heap.Push(next,flooded[next]);
                }
            }
            if(visits!=n)throw new InvalidOperationException("Drainage flood did not cover the closed spherical graph.");
            var ceiling=(double[])macro.Clone();double epsilonSlope=1e-9;
            for(int j=n-1;j>=0;j--)
            {
                int i=order[j],p=parent[i];if(p<0)continue;
                area[p]+=area[i];ceiling[p]=math.min(ceiling[p],ceiling[i]-Arc(graph.Directions[p],graph.Directions[i])*recipe.Radius*epsilonSlope);
            }
            // Strahler ranks describe the whole drainage tree rather than decorative branch labels.
            var maximumOrder=new int[n];var equalMaximum=new int[n];
            for(int j=n-1;j>=0;j--)
            {
                int i=order[j];streamOrder[i]=math.max(1,maximumOrder[i]+(equalMaximum[i]>1?1:0));int p=parent[i];if(p<0)continue;
                if(streamOrder[i]>maximumOrder[p]){maximumOrder[p]=streamOrder[i];equalMaximum[p]=1;}else if(streamOrder[i]==maximumOrder[p])equalMaximum[p]++;
            }
            var bed=new double[n];var outlets=new int[n];double feature=geometry.RegionalFeatureScaleMetres;
            double minimumArea=math.max(feature*feature*4,4*Math.PI*recipe.Radius*recipe.Radius/n*4);
            for(int j=0;j<n;j++)
            {
                SurfaceBaker.CheckCancelled(cancelled,j);int i=order[j],p=parent[i];
                outlets[i]=p<0?i:outlets[p];
                // A coarse ocean graph vertex may be kilometres below the visible shoreline. Its
                // captured river outlet bed is just below sea level, rather than forcing that deep
                // seabed elevation through the entire terrestrial catchment. Incision never raises
                // the ocean terrain to this control bed.
                if(p<0)bed[i]=macro[i]<=sea?math.max(recipe.MinimumHeight,sea-.01):math.max(recipe.MinimumHeight,ceiling[i]);
                else
                {
                    double distance=Arc(graph.Directions[p],graph.Directions[i])*recipe.Radius;
                    double uplift=math.clamp((macro[i]-sea)/(math.max(1,recipe.MaximumHeight-sea)),0,1);
                    double gradient=.001+.22*uplift/Math.Sqrt(math.max(1,area[i]/(feature*feature)));
                    bed[i]=math.min(ceiling[i],bed[p]+distance*gradient);
                    if(bed[i]<=bed[p])bed[i]=bed[p]+distance*epsilonSlope;
                }
                if(macro[i]>sea+2&&area[i]>=minimumArea)selected[i]=true;
            }
            // Keep the entire parent chain to its true ocean outlet; no independently stamped channels.
            for(int j=n-1;j>=0;j--){int i=order[j];if(selected[i])marked[i]=true;if(marked[i]&&parent[i]>=0)marked[parent[i]]=true;}
            var exclusive=(double[])area.Clone();
            for(int i=0;i<n;i++)if(marked[i]&&parent[i]>=0)exclusive[parent[i]]-=area[i];
            var output=new List<SurfaceDrainageNode>();var ownArea=new List<double>();var map=new int[n];for(int i=0;i<n;i++)map[i]=-1;
            var branches=new List<int2>();
            for(int j=0;j<n;j++)
            {
                SurfaceBaker.CheckCancelled(cancelled,j);int i=order[j];if(!marked[i])continue;int p=parent[i];
                double width=math.clamp(Math.Sqrt(area[i])*.07,feature*.25,recipe.Radius*.015);
                double divide=math.max(bed[i],macro[i]);
                if(p<0)
                {int own=output.Count;Add(output,new SurfaceDrainageNode(graph.Directions[i],bed[i],area[i],width,divide,-1,own,streamOrder[i]));ownArea.Add(math.max(0,exclusive[i]));map[i]=own;continue;}
                int previous=map[p];if(previous<0)throw new InvalidOperationException("Captured drainage lost a parent.");
                var first=output[previous];double length=Arc(first.Direction,graph.Directions[i])*recipe.Radius;
                int splits=math.max(2,(int)Math.Ceiling(math.length(first.Direction-graph.Directions[i])/.18));
                // A captured midpoint is a real Y junction. Its two sides share the same bed/width/divide.
                for(int split=1;split<=splits;split++)
                {
                    double t=(double)split/splits;int own=output.Count;
                    Add(output,new SurfaceDrainageNode(math.normalize(math.lerp(first.Direction,graph.Directions[i],t)),
                        math.lerp(first.BedHeight,bed[i],t),area[i],
                        math.lerp(first.HillslopeWidth,width,t),math.lerp(first.DivideHeight,divide,t),previous,first.Outlet,streamOrder[i]));
                    ownArea.Add(split==splits?math.max(0,exclusive[i]):0);
                    previous=own;
                    if(split==1&&selected[i]&&length>feature&&divide-bed[i]>160)branches.Add(new int2(own,i));
                }
                map[i]=previous;
            }
            // Tributary hierarchy varies catchment orientation and scale. It inherits the downstream
            // outlet and rises from the same captured bed; branches cannot remain equally parallel.
            foreach(var branch in branches)
            {
                int junction=branch.x;
                SurfaceBaker.CheckCancelled(cancelled,junction);var node=output[junction];var downstream=output[node.Parent];
                var along=math.normalizesafe(node.Direction-downstream.Direction*math.dot(node.Direction,downstream.Direction));
                double side=Random(recipe.Seed,junction,31)<.5?-1:1;
                double parcel=ownArea[map[branch.y]]*.8;
                if(parcel<=0)continue;
                bool added=Branch(output,ownArea,recipe,controls,graph,outlets,outlets[branch.y],junction,along,
                    feature*(.9+.7*Random(recipe.Seed,junction,32)),side,2,parcel,cancelled);
                if(added)ownArea[map[branch.y]]-=parcel;
            }
            // Fine tributaries partition the coarse node's reference-sphere catchment parcel. They
            // do not invent extra drainage area; inclusive downstream area is recomputed on the
            // final explicit graph, including every Y connection and intermediate segment.
            var finalArea=ownArea.ToArray();var finalOrder=new int[output.Count];var maxOrder=new int[output.Count];var equalOrder=new int[output.Count];
            for(int i=output.Count-1;i>=0;i--)
            {
                finalOrder[i]=math.max(1,maxOrder[i]+(equalOrder[i]>1?1:0));int p=output[i].Parent;if(p<0)continue;
                finalArea[p]+=finalArea[i];
                if(finalOrder[i]>maxOrder[p]){maxOrder[p]=finalOrder[i];equalOrder[p]=1;}else if(finalOrder[i]==maxOrder[p])equalOrder[p]++;
            }
            for(int i=0;i<output.Count;i++)
            {var node=output[i];output[i]=new SurfaceDrainageNode(node.Direction,node.BedHeight,math.max(1e-12,finalArea[i]),node.HillslopeWidth,node.DivideHeight,node.Parent,node.Outlet,finalOrder[i]);}
            return output.ToArray();
        }

        static bool Branch(List<SurfaceDrainageNode> nodes,List<double> ownArea,SurfaceRecipe recipe,in NativeSurfaceStructuralView controls,
            SurfaceBakeGraph graph,int[] outlets,int outlet,int parent,double3 along,double length,double side,int depth,double area,Func<bool> cancelled)
        {
            SurfaceBaker.CheckCancelled(cancelled);var first=nodes[parent];
            along=math.normalizesafe(along-first.Direction*math.dot(along,first.Direction),new double3(1,0,0));
            var cross=math.cross(first.Direction,along);double angle=(.65+.4*Random(recipe.Seed,parent,41))*side;
            var direction=along*Math.Cos(angle)+cross*Math.Sin(angle);
            double angular=math.min(.12,length/recipe.Radius);
            var child=math.normalize(first.Direction*Math.Cos(angular)+direction*Math.Sin(angular));
            for(int sample=1;sample<=4;sample++)
            {
                var d=math.normalize(math.lerp(first.Direction,child,sample*.25));
                CubeSurface.TryLocate(d,0,out var cell,out var uv);var xy=(int2)math.clamp(math.round(uv*graph.Resolution),0,graph.Resolution);
                if(outlets[graph.FaceNode(cell.Face,xy.x,xy.y)]!=outlet)return false;
            }
            if(SurfaceStructuralMath.TrySampleBaseFour(controls,child,out double divide,out _,out _)!=SurfaceSampleStatus.Ready)
                throw new InvalidOperationException("A tributary requires captured global structure.");
            double relief=divide-first.BedHeight;
            if(relief<100||divide<recipe.SeaLevel+10)return false;
            double rise=math.min(relief*.55,length*(.014+.025*Random(recipe.Seed,parent,43)));
            if(rise<=0)return false;
            double width=math.min(first.HillslopeWidth,length*(.28+.12*Random(recipe.Seed,parent,45)));
            int own=nodes.Count;
            Add(nodes,new SurfaceDrainageNode(child,first.BedHeight+rise,area,math.max(recipe.Radius*1e-8,width),
                math.max(divide,first.BedHeight+rise),parent,first.Outlet,math.max(1,depth)));
            ownArea.Add(area);
            if(depth<=0)return true;
            if(Branch(nodes,ownArea,recipe,controls,graph,outlets,outlet,own,direction,length*(.50+.14*Random(recipe.Seed,own,48)),1,depth-1,area*.35,cancelled))ownArea[own]-=area*.35;
            if(Branch(nodes,ownArea,recipe,controls,graph,outlets,outlet,own,direction,length*(.52+.14*Random(recipe.Seed,own,49)),-1,depth-1,area*.35,cancelled))ownArea[own]-=area*.35;
            return true;
        }
        static void Add(List<SurfaceDrainageNode> nodes,SurfaceDrainageNode node)
        {if(nodes.Count>=SurfaceDrainageField.MaximumNodes)throw new ArgumentException("Captured catchments exceed the admitted node count.");nodes.Add(node);}
        static void CreateDiagonals(SurfaceBakeGraph graph,int seed,out int4[] neighbours,out byte[] counts,Func<bool> cancelled)
        {
            neighbours=new int4[graph.NodeCount];counts=new byte[graph.NodeCount];var local=neighbours;var degree=counts;
            void Add(int a,int b)
            {
                for(int i=0;i<degree[a];i++)if(local[a][i]==b)return;
                if(degree[a]>=4)throw new InvalidOperationException("Captured triangle adjacency exceeds four diagonals per vertex.");
                var value=local[a];value[degree[a]++]=b;local[a]=value;
            }
            for(int f=0;f<6;f++)for(int y=0;y<graph.Resolution;y++)for(int x=0;x<graph.Resolution;x++)
            {
                int cell=f*graph.Resolution*graph.Resolution+y*graph.Resolution+x;SurfaceBaker.CheckCancelled(cancelled,cell);
                int a,b;
                if(Random(seed,cell,71)<.5){a=graph.FaceNode(f,x,y);b=graph.FaceNode(f,x+1,y+1);}
                else{a=graph.FaceNode(f,x+1,y);b=graph.FaceNode(f,x,y+1);}
                Add(a,b);Add(b,a);
            }
        }
        static double Arc(double3 a,double3 b)=>2*Math.Asin(math.min(1,math.length(a-b)*.5));
        static double Cross(double2 a,double2 b)=>a.x*b.y-a.y*b.x;
        static double3 Rotate(double3 v,double3 axis,double angle)=>v*Math.Cos(angle)+math.cross(axis,v)*Math.Sin(angle)+axis*math.dot(axis,v)*(1-Math.Cos(angle));
        static double Random(int seed,int slot,int stream)
        {uint v=unchecked((uint)seed^((uint)slot*0x9e3779b9u)^((uint)stream*0x85ebca6bu));v^=v>>16;v*=0x7feb352du;v^=v>>15;v*=0x846ca68bu;v^=v>>16;return(v+.5)/4294967296.0;}
        sealed class FloodHeap
        {
            readonly List<int> nodes=new List<int>();readonly List<double> values=new List<double>();public int Count=>nodes.Count;
            static bool Less(double a,int ai,double b,int bi)=>a<b||a==b&&ai<bi;
            public void Push(int node,double value)
            {int i=Count;nodes.Add(node);values.Add(value);while(i>0){int p=(i-1)>>1;if(!Less(value,node,values[p],nodes[p]))break;nodes[i]=nodes[p];values[i]=values[p];i=p;}nodes[i]=node;values[i]=value;}
            public void Pop(out int node,out double value)
            {node=nodes[0];value=values[0];int last=Count-1,n=nodes[last];double v=values[last];nodes.RemoveAt(last);values.RemoveAt(last);if(Count==0)return;int i=0;while(i*2+1<Count){int child=i*2+1;if(child+1<Count&&Less(values[child+1],nodes[child+1],values[child],nodes[child]))child++;if(!Less(values[child],nodes[child],v,n))break;nodes[i]=nodes[child];values[i]=values[child];i=child;}nodes[i]=n;values[i]=v;}
        }
    }
}
