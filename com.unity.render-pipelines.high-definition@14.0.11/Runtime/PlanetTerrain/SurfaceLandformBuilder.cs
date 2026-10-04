using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>
    /// Whole-cover offline graph solve. Tectonic uplift sets stream-power/hillslope rates rather than
    /// a retained plateau. Every adaptive land cell receives a downstream bed and shared ridge controls.
    /// This is an admitted procedural geomorphology model, not a geological-time simulation.
    /// </summary>
    public static class SurfaceLandformBuilder
    {
        // Captured controls remain codec authority. A generator revision invalidates bake caches
        // without reinterpreting any previously captured algorithm-five field.
        public const int GeneratorRevision=6;
        // Fresh profile admission is additional to, never an expansion of, the captured field caps.
        public const int MaximumAddedProfileControls=196608;
        public const int MaximumNearbyProfileArcQueries=2048;
        public static SurfaceLandformField Build(SurfaceRecipe recipe,SurfaceBakeSettings geologicalSettings,SurfaceLandformBuildSettings settings,
            out SurfaceLandformBuildDiagnostics diagnostics,Func<bool> cancelled=null,long retainedWorkingBytes=0)
            =>BuildCore(recipe,geologicalSettings,settings,true,out diagnostics,cancelled,retainedWorkingBytes);
        /// <summary>Explicit numerical diagnostic of the historical fresh Gen4 builder. This does not select a bake/cache policy.</summary>
        public static SurfaceLandformField BuildGeneratorFour(SurfaceRecipe recipe,SurfaceBakeSettings geologicalSettings,SurfaceLandformBuildSettings settings,
            out SurfaceLandformBuildDiagnostics diagnostics,Func<bool> cancelled=null,long retainedWorkingBytes=0)
            =>BuildCore(recipe,geologicalSettings,settings,false,out diagnostics,cancelled,retainedWorkingBytes);
        static SurfaceLandformField BuildCore(SurfaceRecipe recipe,SurfaceBakeSettings geologicalSettings,SurfaceLandformBuildSettings settings,bool crestProfiles,
            out SurfaceLandformBuildDiagnostics diagnostics,Func<bool> cancelled,long retainedWorkingBytes)
        {
            diagnostics=default;if(geologicalSettings==null||settings==null)throw new ArgumentNullException(nameof(settings));
            var policy=settings.Clone();if(!policy.Validate(recipe,out string error))throw new ArgumentException(error);
            SurfaceBaker.CheckCancelled(cancelled);
            // Bootstrap captures only the released coast/province controls, never its selected/trenched
            // drainage geometry. Exact old-four helpers and numeric operations remain unchanged.
            var bootstrapRecipe=new SurfaceRecipe(recipe.Seed,recipe.Style,recipe.Radius,recipe.MinimumHeight,recipe.MaximumHeight,recipe.SeaLevel,4,recipe.SchemaVersion);
            var bootstrapSettings=geologicalSettings.Clone();bootstrapSettings.FaceResolution=16;bootstrapSettings.HydraulicIterations=0;bootstrapSettings.ThermalIterations=0;
            var geometry=SurfaceGeomorphology.Build(bootstrapRecipe,bootstrapSettings,cancelled);
            var calibration=new SurfaceBakeGraph(32,recipe.Radius,cancelled);
            SurfaceDrainageBuilder.BuildCoasts(bootstrapRecipe,bootstrapSettings,calibration,out var landmasses,out var coastVertices,cancelled);
            var coast=new SurfaceDrainageField(bootstrapRecipe,landmasses,coastVertices,Array.Empty<SurfaceDrainageNode>(),cancelled,geometry.ShelfWidthMetres*4);
            var provinces=new SurfaceGeologicalProvince[geometry.Provinces.Count];var boundaries=new SurfaceGeologicalBoundary[geometry.Boundaries.Count];
            for(int i=0;i<provinces.Length;i++)provinces[i]=geometry.Provinces[i];for(int i=0;i<boundaries.Length;i++)boundaries[i]=geometry.Boundaries[i];
            var placeholder=new float[24];for(int i=0;i<placeholder.Length;i++)placeholder[i]=(float)recipe.SeaLevel;
            var bootstrap=new SurfaceStructuralField(bootstrapRecipe,geometry.ContentDigest,1,placeholder,provinces,boundaries,
                geometry.ShelfWidthMetres,geometry.BeltWidthMetres,geometry.RegionalFeatureScaleMetres,0,geometry.MountainUpliftFraction,0,0,cancelled,coast);
            using(var native=new NativeSurfaceStructuralData(bootstrap,Allocator.Persistent))
            {
                var leaves=PrepareCells(recipe,geometry,native.View,policy,cancelled);Balance(leaves,policy.MaximumCells,cancelled);
                var keys=new List<SurfaceTileKey>(leaves.Keys);keys.Sort();int count=keys.Count;
                // Each cell has at most eight edge neighbors after balancing. Count the exact shared
                // edges before allocating solve/control banks. Dictionaries only cover admitted leaves.
                var edges=Edges(keys,leaves,cancelled);int totalControls=checked(count+edges.Count);
                if(totalControls>SurfaceLandformField.MaximumControls)throw new ArgumentException("Adaptive landform controls exceed declared cap.");
                long authority=SurfaceLandformField.EstimateAuthorityBytes(totalControls,count,edges.Count,count,edges.Count*2);
                long indexReserve=SurfaceLandformField.MaximumIndexNodes*32L+SurfaceLandformField.MaximumReferences*4L;
                long working=checked(retainedWorkingBytes+authority*3+indexReserve*2+count*257L+edges.Count*80L+leaves.Count*128L+32L*1024*1024);
                if(authority+indexReserve>SurfaceLandformField.MaximumResidentBytes||working>policy.MaximumWorkingBytes)
                    throw new ArgumentException($"Adaptive landform preflight rejects cells={count}, controls={totalControls}, working={working}; no coarser hidden substitute is published.");
                return Solve(recipe,geometry,native.View,keys,edges,working,policy.MaximumWorkingBytes,crestProfiles,out diagnostics,cancelled);
            }
        }
        static Dictionary<SurfaceTileKey,int> PrepareCells(SurfaceRecipe recipe,SurfaceGeomorphology geometry,in NativeSurfaceStructuralView bootstrap,
            SurfaceLandformBuildSettings policy,Func<bool> cancelled)
        {
            var leaves=new Dictionary<SurfaceTileKey,int>();var stack=new Stack<SurfaceTileKey>();
            for(int face=5;face>=0;face--)stack.Push(new SurfaceTileKey(face,0,0,0));int visits=0;
            while(stack.Count>0)
            {
                SurfaceBaker.CheckCancelled(cancelled,visits++);var key=stack.Pop();CubeSurface.TryDirection(key,new double2(.5),out var center);
                double radius=CellRadius(recipe.Radius,key,center),uplift=0;
                // Distance is 1-Lipschitz: this envelope upper bound admits any belt crossing the
                // complete cell, including between sampled corners. No finite sample claims completeness.
                foreach(var boundary in geometry.Boundaries)
                {
                    if(!boundary.MountainBelt)continue;double distance=Distance(boundary,center,recipe.Radius);
                    double q=math.max(0,distance-radius)/geometry.BeltWidthMetres;
                    double weight=q>=3?0:Math.Exp(-q*q)*(1-Smooth(q-2));
                    uplift=math.max(uplift,weight*(boundary.Convergence-.08)/.92);
                }
                if(SurfaceDrainageMath.TrySampleCoastForMacro(bootstrap.DrainageField,center,out double coast,out _)!=SurfaceSampleStatus.Ready)
                    throw new InvalidOperationException("Captured landform coastline is not ready.");
                double target=policy.PlainSpacingMetres;
                if(coast+radius>=0)
                {
                    if(uplift>=policy.HighlandUpliftThreshold)target=policy.HighlandSpacingMetres;
                    else if(uplift>=policy.FoothillUpliftThreshold||math.abs(coast)<=geometry.ShelfWidthMetres+radius)target=policy.FoothillSpacingMetres;
                }
                double width=2*recipe.Radius/(1<<key.Level);
                if(width>target)
                {
                    if(key.Level>=12)throw new ArgumentException("Landform metric policy requires unsupported adaptive level.");
                    if((long)leaves.Count+stack.Count+4>policy.MaximumCells)throw new ArgumentException("Landform adaptive preflight exceeds declared covering-cell cap.");
                    for(int child=3;child>=0;child--)stack.Push(new SurfaceTileKey(key.Face,key.Level+1,key.X*2+(child&1),key.Y*2+(child>>1)));
                }
                else leaves.Add(key,0);
            }
            return leaves;
        }
        static void Balance(Dictionary<SurfaceTileKey,int> leaves,int maximum,Func<bool> cancelled)
        {
            bool changed=true;int iteration=0;
            while(changed)
            {
                SurfaceBaker.CheckCancelled(cancelled,iteration++);changed=false;var keys=new List<SurfaceTileKey>(leaves.Keys);keys.Sort();var split=new SortedSet<SurfaceTileKey>();
                foreach(var key in keys)for(int edge=0;edge<4;edge++)
                {
                    var neighbor=Neighbor(leaves,key,edge,.5);if(key.Level-neighbor.Level>1)split.Add(neighbor);
                }
                foreach(var key in split)
                {
                    if(leaves.Count+3>maximum)throw new ArgumentException("Balanced landform cover exceeds declared cell cap.");
                    leaves.Remove(key);for(int child=0;child<4;child++)leaves.Add(new SurfaceTileKey(key.Face,key.Level+1,key.X*2+(child&1),key.Y*2+(child>>1)),0);changed=true;
                }
            }
        }
        static List<int2> Edges(List<SurfaceTileKey> keys,Dictionary<SurfaceTileKey,int> leaves,Func<bool> cancelled)
        {
            for(int i=0;i<keys.Count;i++)leaves[keys[i]]=i;
            var set=new HashSet<ulong>();
            for(int i=0;i<keys.Count;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var key=keys[i];
                for(int side=0;side<4;side++)for(int sample=0;sample<2;sample++)
                {
                    int other=leaves[Neighbor(leaves,key,side,sample==0?.25:.75)];if(other==i)throw new InvalidOperationException("Closed cell cannot neighbor itself.");
                    uint first=(uint)math.min(i,other),last=(uint)math.max(i,other);set.Add(((ulong)first<<32)|last);
                }
            }
            var ordered=new List<ulong>(set);ordered.Sort();var output=new List<int2>(ordered.Count);foreach(ulong edge in ordered)output.Add(new int2((int)(edge>>32),(int)(edge&0xffffffffu)));return output;
        }
        static SurfaceTileKey Neighbor(Dictionary<SurfaceTileKey,int> leaves,SurfaceTileKey key,int edge,double t)
        {
            // Continue the exact cube-face coordinates beyond its edge; dominant-axis lookup maps
            // the resulting direction onto the adjacent face using the same canonical face convention.
            const double outside=1e-7;double count=1<<key.Level;
            double u=edge==0?-outside:edge==1?1+outside:t,v=edge==2?-outside:edge==3?1+outside:t;
            double a=2*(key.X+u)/count-1,b=2*(key.Y+v)/count-1;var d=math.normalize(Cube(key.Face,a,b));
            CubeSurface.TryLocate(d,12,out var found,out _);
            for(int level=found.Level;level>=0;level--){var candidate=new SurfaceTileKey(found.Face,level,found.X>>(found.Level-level),found.Y>>(found.Level-level));if(leaves.ContainsKey(candidate))return candidate;}
            throw new InvalidOperationException("Adaptive sphere has a missing neighboring canonical cell.");
        }
        static SurfaceLandformField Solve(SurfaceRecipe recipe,SurfaceGeomorphology geometry,in NativeSurfaceStructuralView bootstrap,
            List<SurfaceTileKey> keys,List<int2> edges,long working,long maximumWorking,bool crestProfiles,out SurfaceLandformBuildDiagnostics diagnostics,Func<bool> cancelled)
        {
            int n=keys.Count;var directions=new double3[n];var cellRadius=new double[n];var area=new double[n];var macro=new double[n];var uplift=new double[n];
            var neighbors=new int[n*8];var degree=new int[n];var parent=new int[n];var order=new int[n];var outlet=new int[n];var height=new double[n];var marked=new bool[n];var ocean=new bool[n];
            var heap=new FloodHeap(n);double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight);int lowest=0;
            for(int i=0;i<n;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);CubeSurface.TryDirection(keys[i],new double2(.5),out directions[i]);cellRadius[i]=CellRadius(recipe.Radius,keys[i],directions[i]);
                CubeSurface.TryDirection(keys[i],new double2(0,0),out var corner0);CubeSurface.TryDirection(keys[i],new double2(1,0),out var corner1);
                CubeSurface.TryDirection(keys[i],new double2(0,1),out var corner2);CubeSurface.TryDirection(keys[i],new double2(1,1),out var corner3);
                area[i]=(SolidAngle(corner0,corner1,corner2)+SolidAngle(corner1,corner3,corner2))*recipe.Radius*recipe.Radius;
                if(SurfaceStructuralMath.TrySampleBaseFour(bootstrap,directions[i],out macro[i],out _,out _)!=SurfaceSampleStatus.Ready)throw new InvalidOperationException("Bootstrap uplift is not ready.");
                ocean[i]=CapturedOcean(bootstrap.DrainageField,directions[i]);
                var sample=geometry.Sample(directions[i]);uplift[i]=math.clamp(sample.Uplift*math.min(1,geometry.MountainUpliftFraction/.3),0,1);parent[i]=-2;
                if(macro[i]<macro[lowest])lowest=i;
            }
            foreach(var edge in edges)
            {
                if(degree[edge.x]>=8||degree[edge.y]>=8)throw new ArgumentException("Balanced cell graph exceeded its explicit eight-neighbor cap.");
                neighbors[edge.x*8+degree[edge.x]++]=edge.y;neighbors[edge.y*8+degree[edge.y]++]=edge.x;
            }
            for(int i=0;i<n;i++)if(macro[i]<=sea){parent[i]=-1;marked[i]=true;heap.Push(i,macro[i]);}
            if(heap.Count==0){parent[lowest]=-1;marked[lowest]=true;heap.Push(lowest,macro[lowest]);}
            int visited=0;
            while(heap.Count>0)
            {
                SurfaceBaker.CheckCancelled(cancelled,visited);heap.Pop(out int node,out double flooded);order[visited++]=node;
                for(int j=0;j<degree[node];j++){int next=neighbors[node*8+j];if(marked[next])continue;marked[next]=true;parent[next]=node;heap.Push(next,math.max(flooded,macro[next]));}
            }
            if(visited!=n)throw new InvalidOperationException("Captured whole-cover drainage graph is disconnected.");
            var drainageArea=(double[])area.Clone();var rank=new int[n];var maxRank=new int[n];var equalRank=new int[n];
            for(int j=n-1;j>=0;j--)
            {
                int i=order[j],p=parent[i];rank[i]=math.max(1,maxRank[i]+(equalRank[i]>1?1:0));if(p<0)continue;drainageArea[p]+=drainageArea[i];
                if(rank[i]>maxRank[p]){maxRank[p]=rank[i];equalRank[p]=1;}else if(rank[i]==maxRank[p])equalRank[p]++;
            }
            double maximumRise=0;var rise=new double[n];
            for(int j=0;j<n;j++)
            {
                SurfaceBaker.CheckCancelled(cancelled,j);int i=order[j],p=parent[i];outlet[i]=p<0?i:outlet[p];
                if(p<0){height[i]=macro[i];continue;}
                double distance=Arc(directions[i],directions[p])*recipe.Radius;
                // A stream-power equilibrium approximation on EVERY land cell: headwater catchments
                // have steeper gradients, large downstream drainage has smaller gradient. There is no
                // min(macro,bed) plateau cap and no isolated channel-radius subtraction afterwards.
                double reference=4000.0*4000.0;
                double gradient=.0015+.32*uplift[i]*Math.Pow(reference/math.max(reference,drainageArea[i]),.45);
                rise[i]=distance*gradient;height[i]=(macro[p]<=sea?sea:height[p])+rise[i];maximumRise=math.max(maximumRise,rise[i]);
            }
            // Scale the connected land component uniformly if necessary: this preserves ridge/valley
            // differences and avoids saturation plateaus at the source maximum. Oceans remain captured.
            double rawMaximum=sea;for(int i=0;i<n;i++)if(macro[i]>sea)rawMaximum=math.max(rawMaximum,height[i]);
            double scale=rawMaximum>sea?(recipe.MaximumHeight-sea)*.78/(rawMaximum-sea):1;scale=math.min(1,scale);
            for(int i=0;i<n;i++)if(macro[i]>sea)height[i]=sea+(height[i]-sea)*scale;
            var gradientField=new double3[n];
            for(int i=0;i<n;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var normal=directions[i];var referenceAxis=math.abs(normal.y)<.9?new double3(0,1,0):new double3(1,0,0);
                var right=math.normalize(math.cross(referenceAxis,normal));var forward=math.cross(normal,right);double xx=0,xy=0,yy=0,xh=0,yh=0;
                for(int j=0;j<degree[i];j++)
                {
                    int other=neighbors[i*8+j];var delta=(directions[other]-normal)*recipe.Radius;double x=math.dot(delta,right),y=math.dot(delta,forward),h=height[other]-height[i];
                    double w=1/math.max(1,x*x+y*y);xx+=w*x*x;xy+=w*x*y;yy+=w*y*y;xh+=w*x*h;yh+=w*y*h;
                }
                double det=xx*yy-xy*xy;if(det>1e-12)gradientField[i]=right*((xh*yy-yh*xy)/det)+forward*((yh*xx-xh*xy)/det);
                double length=math.length(gradientField[i]);if(length>1)gradientField[i]/=length;
                // A shared Y control has one tangent derivative. Each incident upstream strip
                // includes both the correct sign and its actual captured bed/chord secant bound.
                var constraints=new FixedList512Bytes<double4>();
                if(parent[i]>=0)constraints.Add(new double4(-TangentToward(normal,directions[parent[i]]),Secant(height[i],height[parent[i]],normal,directions[parent[i]],recipe.Radius)));
                for(int j=0;j<degree[i];j++)
                {int other=neighbors[i*8+j];if(parent[other]==i)constraints.Add(new double4(TangentToward(normal,directions[other]),Secant(height[other],height[i],directions[other],normal,recipe.Radius)));}
                gradientField[i]=ProjectTangentStrips(normal,gradientField[i],constraints);
            }
            var controls=new SurfaceLandformControl[n+edges.Count];var channels=new SurfaceLandformChannel[n];var map=new int[n];
            // Channels are parent-first even when cube key ordering is different. Controls stay in
            // deterministic cell-key order; neither topology nor hash depends on renderer residency.
            for(int j=0;j<n;j++)map[order[j]]=j;
            int oceanChannels=0;
            for(int i=0;i<n;i++)
            {
                // Membership is captured coastline topology, not a claimed signed-distance margin.
                // Ocean terms retain their bathymetry while their Hermite offsets cannot invent land.
                bool boundedOcean=ocean[i]&&height[i]<=sea;if(boundedOcean)oceanChannels++;
                double variation=boundedOcean?BoundedVariation(height[i],recipe.MinimumHeight,sea):math.min(height[i]-recipe.MinimumHeight,recipe.MaximumHeight-height[i]);
                // Start with the exact historical radius. Fresh selected crest incident cells
                // are tightened after selection; other channel cells keep their bridging tail.
                double support=cellRadius[i]*1.6;
                controls[i]=new SurfaceLandformControl(directions[i],height[i],variation>0?gradientField[i]:new double3(0),support,math.max(0,variation));
                int id=map[i];channels[id]=new SurfaceLandformChannel(i,parent[i]<0?-1:map[parent[i]],map[outlet[i]],rank[i],drainageArea[i],rise[i]*scale);
            }
            var divides=new SurfaceLandformDivide[edges.Count];var cellReferences=new List<int>[n];for(int i=0;i<n;i++)cellReferences[i]=new List<int>(8);
            double maximumProminence=0;int oceanMidpoints=0,macroIslandMidpoints=0;
            for(int i=0;i<edges.Count;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var edge=edges[i];int a=edge.x,b=edge.y;var direction=math.normalize(directions[a]+directions[b]);
                bool connected=parent[a]==b||parent[b]==a;
                double span=Arc(directions[a],directions[b])*recipe.Radius;
                double strength=math.max(uplift[a],uplift[b]);
                double prominence=connected?0:math.min((recipe.MaximumHeight-sea)*.2,span*.16*strength*scale);
                double h=connected?(height[a]+height[b])*.5:math.max(height[a],height[b])+prominence;
                bool midpointOcean=CapturedOcean(bootstrap.DrainageField,direction),oceanPair=ocean[a]&&ocean[b];
                if(oceanPair)
                {
                    if(SurfaceStructuralMath.TrySampleBaseFour(bootstrap,direction,out double midpointMacro,out _,out _)!=SurfaceSampleStatus.Ready)
                        throw new InvalidOperationException("Captured ocean midpoint macro is not ready.");
                    if(midpointOcean)
                    {
                        // Terrestrial prominence is inappropriate between genuinely ocean cells.
                        // Capture the negative basin/shelf/rift value, rather than a flat sea plane.
                        h=midpointMacro;prominence=0;oceanMidpoints++;
                    }
                    else
                    {
                        // A captured continent may cross an edge whose two coarse centers are ocean.
                        // Keep that island authority explicit instead of erasing it with a sea clamp.
                        h=math.max(h,midpointMacro);macroIslandMidpoints++;
                    }
                }
                h=math.clamp(h,recipe.MinimumHeight,recipe.MaximumHeight);
                var g=connected?(gradientField[a]+gradientField[b])*.5:new double3(0);g-=direction*math.dot(direction,g);
                if(connected)
                {
                    int downstream=parent[a]==b?b:a,upstream=downstream==a?b:a;
                    var constraints=new FixedList512Bytes<double4>();
                    constraints.Add(new double4(TangentToward(direction,directions[upstream]),Secant(height[upstream],h,directions[upstream],direction,recipe.Radius)));
                    constraints.Add(new double4(-TangentToward(direction,directions[downstream]),Secant(h,height[downstream],direction,directions[downstream],recipe.Radius)));
                    g=ProjectTangentStrips(direction,g,constraints);
                }
                // Mixed shore controls remain open: this point classification does not certify
                // the whole support or impose an artificial coastline on the smooth PU blend.
                double variation=midpointOcean&&h<=sea?BoundedVariation(h,recipe.MinimumHeight,sea):math.min(h-recipe.MinimumHeight,recipe.MaximumHeight-h);
                controls[n+i]=new SurfaceLandformControl(direction,h,variation>0?g:new double3(0),math.max(cellRadius[a],cellRadius[b])*1.25,math.max(0,variation));
                divides[i]=new SurfaceLandformDivide(n+i,map[a],map[b],connected?0:outlet[a]==outlet[b]?1:2);
                cellReferences[a].Add(i);cellReferences[b].Add(i);maximumProminence=math.max(maximumProminence,prominence);
            }
            var cells=new SurfaceLandformCell[n];var refs=new int[edges.Count*2];int cursor=0,highest=0;
            for(int i=0;i<n;i++)
            {
                cells[i]=new SurfaceLandformCell(keys[i],map[i],cursor,cellReferences[i].Count);foreach(int d in cellReferences[i])refs[cursor++]=d;highest=math.max(highest,keys[i].Level);
            }
            if(crestProfiles)CaptureCrestProfiles(recipe,bootstrap,controls,channels,divides,cells,cellRadius,maximumWorking,ref working,out controls,out divides,cancelled);
            var field=new SurfaceLandformField(recipe,controls,channels,divides,cells,refs,cancelled);
            diagnostics=new SurfaceLandformBuildDiagnostics(field,highest,working,maximumRise*scale,maximumProminence,oceanChannels,oceanMidpoints,macroIslandMidpoints);return field;
        }
        sealed class CrestNode
        {
            public readonly int3 Key;
            public double3 Direction;
            public double Height;
            public readonly List<int> Edges=new List<int>(4);
            public CrestNode(int3 key){Key=key;Direction=math.normalize((double3)key);}
        }
        struct CrestEdge
        {
            public int Divide,First,Last;
            public double Length,Prominence;
            public bool Selected;
        }
        // Every auxiliary is an ordinary captured Hermite control of a REAL neighboring channel
        // pair (Flags3). The generation-only chain is not claimed to be a new persisted graph ABI.
        // No runtime seed solve, PU formula change or new codec/native layout is involved.
        static void CaptureCrestProfiles(SurfaceRecipe recipe,in NativeSurfaceStructuralView bootstrap,
            SurfaceLandformControl[] sourceControls,SurfaceLandformChannel[] channels,SurfaceLandformDivide[] sourceDivides,
            SurfaceLandformCell[] cells,double[] cellRadii,long maximumWorking,ref long working,out SurfaceLandformControl[] controls,
            out SurfaceLandformDivide[] divides,Func<bool> cancelled)
        {
            controls=sourceControls;divides=sourceDivides;double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight);
            int eligible=0;
            for(int id=0;id<sourceDivides.Length;id++)
            {
                SurfaceBaker.CheckCancelled(cancelled,id);var d=sourceDivides[id];
                if(d.Flags==0)continue;var a=sourceControls[channels[d.FirstChannel].Control];var b=sourceControls[channels[d.SecondChannel].Control];
                if(math.min(a.Height,b.Height)>sea+200&&sourceControls[d.Control].Height-math.max(a.Height,b.Height)>=50)eligible++;
            }
            if(eligible==0)return;
            // Reserve worst-case graph/scratch BEFORE its dictionaries/lists or profile banks.
            // Includes the global selected-arc search bank (96B arc, two 64B BVH nodes,
            // index IDs and sort/traversal allowance), not a hidden whole-graph scan per query.
            long scratch=checked(eligible*1264L+channels.LongLength*65L+4128);
            if(working+scratch>maximumWorking)throw new ArgumentException("Crest graph scratch exceeds declared working budget; no profile subset is published.");
            var keyByChannel=new SurfaceTileKey[channels.Length];for(int i=0;i<cells.Length;i++)keyByChannel[cells[i].Channel]=cells[i].Key;
            var neighbors=new int[channels.Length*8];var degree=new int[channels.Length];var arcVisits=new int[channels.Length];int stationVisit=0;
            foreach(var d in sourceDivides)
            {
                if(degree[d.FirstChannel]>=8||degree[d.SecondChannel]>=8)throw new ArgumentException("Profile neighborhood exceeds the captured eight-neighbor cell graph.");
                neighbors[d.FirstChannel*8+degree[d.FirstChannel]++]=d.SecondChannel;neighbors[d.SecondChannel*8+degree[d.SecondChannel]++]=d.FirstChannel;
            }
            var nodes=new List<CrestNode>(checked(eligible*2));var nodeIds=new Dictionary<int3,int>();var edges=new List<CrestEdge>(eligible);
            for(int i=0;i<sourceDivides.Length;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var d=sourceDivides[i];if(d.Flags==0)continue;
                var a=sourceControls[channels[d.FirstChannel].Control];var b=sourceControls[channels[d.SecondChannel].Control];double prominence=sourceControls[d.Control].Height-math.max(a.Height,b.Height);
                if(math.min(a.Height,b.Height)<=sea+200||prominence<50)continue;
                SharedCorners(keyByChannel[d.FirstChannel],keyByChannel[d.SecondChannel],out var first,out var last);
                int f=Node(first),l=Node(last),own=edges.Count;
                edges.Add(new CrestEdge{Divide=i,First=f,Last=l,Length=Arc(nodes[f].Direction,nodes[l].Direction)*recipe.Radius,Prominence=prominence});
                if(nodes[f].Edges.Count>=8||nodes[l].Edges.Count>=8)throw new ArgumentException("Shared crest junction exceeds bounded valence eight.");
                nodes[f].Edges.Add(own);nodes[l].Edges.Add(own);
            }
            // Strahler/area ancestry remains the existing connected channel tree. Its complementary
            // shared-edge graph produces actual branched divides, rather than independent peaks.
            var visited=new bool[edges.Count];var queue=new Queue<int>();var component=new List<int>();
            for(int start=0;start<edges.Count;start++)
            {
                if(visited[start])continue;SurfaceBaker.CheckCancelled(cancelled,start);queue.Enqueue(start);visited[start]=true;component.Clear();double length=0;bool strong=false;int componentVisits=0;
                while(queue.Count>0)
                {
                    SurfaceBaker.CheckCancelled(cancelled,componentVisits++);
                    int id=queue.Dequeue();var e=edges[id];component.Add(id);length+=e.Length;strong|=e.Prominence>=100;
                    for(int endpoint=0;endpoint<2;endpoint++)foreach(int neighbor in nodes[endpoint==0?e.First:e.Last].Edges)if(!visited[neighbor]){visited[neighbor]=true;queue.Enqueue(neighbor);}
                }
                if(!strong||length<8000)continue;
                foreach(int id in component){var e=edges[id];e.Selected=true;edges[id]=e;}
            }
            int edgeCount=0,nodeCount=0;
            foreach(var e in edges)if(e.Selected)edgeCount++;
            foreach(var node in nodes){int count=0;foreach(int id in node.Edges)if(edges[id].Selected)count++;if(count>=2&&!CapturedOcean(bootstrap.DrainageField,node.Direction))nodeCount++;}
            if(edgeCount==0)return;
            // The midpoint pair alone leaves a low-weight gap between its compact tail and
            // an arc-separated common corner. Two ordered intermediate shoulder pairs cover
            // those half-edges without expanding any corner support across a channel.
            int added=checked(edgeCount*5+nodeCount),totalControls=checked(sourceControls.Length+added),totalDivides=checked(sourceDivides.Length+added);
            long extraAuthority=checked(added*(72L+16));long nextWorking=checked(working+scratch+extraAuthority*3);
            long authority=SurfaceLandformField.EstimateAuthorityBytes(totalControls,channels.Length,totalDivides,cells.Length,cells.Length*8);
            long indexReserve=SurfaceLandformField.MaximumIndexNodes*32L+SurfaceLandformField.MaximumReferences*4L;
            if(added>MaximumAddedProfileControls||totalControls>SurfaceLandformField.MaximumControls||totalDivides>SurfaceLandformField.MaximumControls||
                authority+indexReserve>SurfaceLandformField.MaximumResidentBytes||nextWorking>maximumWorking)
                throw new ArgumentException($"Crest profile preflight rejects added={added}, controls={totalControls}, divides={totalDivides}, working={nextWorking}; no reduced profile quality is substituted.");
            // Common junction datum never exceeds the highest existing neighboring ridge control.
            // Its shallow saddle is a linked profile, not an amplitude/noise layer.
            foreach(var node in nodes)
            {
                double sum=0,maxBed=double.NegativeInfinity,minProm=double.PositiveInfinity;int count=0;
                foreach(int id in node.Edges)if(edges[id].Selected)
                {
                    var e=edges[id];var d=sourceDivides[e.Divide];sum+=sourceControls[d.Control].Height;count++;minProm=math.min(minProm,e.Prominence);
                    maxBed=math.max(maxBed,math.max(sourceControls[channels[d.FirstChannel].Control].Height,sourceControls[channels[d.SecondChannel].Control].Height));
                }
                if(count>0)node.Height=math.max(maxBed,sum/count-minProm*.15);
            }
            var output=new SurfaceLandformControl[totalControls];Array.Copy(sourceControls,output,sourceControls.Length);
            var records=new SurfaceLandformDivide[totalDivides];Array.Copy(sourceDivides,records,sourceDivides.Length);
            var crestIndex=new CrestSupportIndex(nodes,edges,edgeCount,cancelled);
            // One admitted bit per original channel control. Controls are in cell-key order;
            // channels are parent-first, so address the captured Control rather than channel ID.
            var selectedPrimary=new bool[channels.Length];
            foreach(var e in edges)if(e.Selected)
            {
                SurfaceBaker.CheckCancelled(cancelled,e.Divide);var d=sourceDivides[e.Divide];
                selectedPrimary[channels[d.FirstChannel].Control]=true;selectedPrimary[channels[d.SecondChannel].Control]=true;
            }
            for(int id=0;id<channels.Length;id++)
            {
                SurfaceBaker.CheckCancelled(cancelled,id);
                if(selectedPrimary[id])output[id]=WithSupport(sourceControls[id],cellRadii[id]*1.1);
            }
            int selectedCursor=0;
            for(int id=0;id<sourceDivides.Length;id++)
            {
                SurfaceBaker.CheckCancelled(cancelled,id);
                while(selectedCursor<edges.Count&&!edges[selectedCursor].Selected)selectedCursor++;
                if(selectedCursor<edges.Count&&edges[selectedCursor].Divide==id){selectedCursor++;continue;}
                var d=sourceDivides[id];var original=sourceControls[d.Control];
                // Tighten low midpoint tails away from the selected crest spine, but retain
                // the exact historical midpoint radius if the tightened circle cannot reach
                // either linked quarter station. Closed primary coverage alone does not keep
                // the changing PU weights monotone along a long fine/coarse channel bridge.
                double distance=crestIndex.NearestChord(original.Direction,original.SupportMetres/(recipe.Radius*.8),cancelled);
                double support=math.min(original.SupportMetres,distance*recipe.Radius*.8);
                if(d.Flags==0)
                {
                    var first=sourceControls[channels[d.FirstChannel].Control].Direction;
                    var last=sourceControls[channels[d.SecondChannel].Control].Direction;
                    support=QuarterBridgeSupport(original,first,last,recipe.Radius,support);
                }
                else
                {
                    // Both neighboring cells, their one-ring neighbors, and every deduplicated
                    // parent/child arc incident to that local bank. Same bounded neighborhood
                    // as shoulder separation; no whole-channel BVH or per-ray exclusion.
                    double channelDistance=NearbyProfileArcDistance(original.Direction,d.FirstChannel,d.SecondChannel,channels,
                        sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled);
                    support=math.min(support,.8*channelDistance);
                }
                if(!(support>0))throw new ArgumentException("A background midpoint coincides with a selected crest arc; no partial support bank is published.");
                output[d.Control]=WithSupport(original,support);
            }
            int controlCursor=sourceControls.Length,divideCursor=sourceDivides.Length;
            foreach(var e in edges)if(e.Selected)
            {
                SurfaceBaker.CheckCancelled(cancelled,e.Divide);var d=sourceDivides[e.Divide];var original=sourceControls[d.Control];var a=sourceControls[channels[d.FirstChannel].Control];var b=sourceControls[channels[d.SecondChannel].Control];
                var center=math.normalize(nodes[e.First].Direction+nodes[e.Last].Direction);var along=TangentToward(center,nodes[e.Last].Direction);
                double spacing=math.length(a.Direction-b.Direction)*recipe.Radius;
                // A shared physical cube edge defines the crest tangent. Fine/coarse channel
                // centers need not be directly across from one another: their difference must
                // not rotate the transverse shoulder down the neighboring crest branch.
                var across=math.normalize(math.cross(center,along));if(math.dot(across,b.Direction-a.Direction)<0)across=-across;
                var longitudinal=along*math.clamp((nodes[e.Last].Height-nodes[e.First].Height)/math.max(1,e.Length),-.5,.5);
                var strips=new FixedList512Bytes<double4>();
                CrestStrip(center,original.Height,nodes[e.First].Direction,nodes[e.First].Height,ref strips);
                CrestStrip(center,original.Height,nodes[e.Last].Direction,nodes[e.Last].Height,ref strips);
                longitudinal=ProjectTangentStrips(center,longitudinal,strips);
                double ceiling=math.max(original.Height,math.max(nodes[e.First].Height,nodes[e.Last].Height));
                // Two co-located records otherwise double the old far tail. Their metric radius
                // uses the fourth-root density correction (unscaled PU tail is proportional S^4).
                double support=original.SupportMetres/Math.Sqrt(Math.Sqrt(2));
                Pair(center,original.Height,longitudinal,across,spacing,e.Prominence,support,ceiling,out var left,out var right);
                support=math.min(support,.8*NearbyProfileArcDistance(left.Direction,d.FirstChannel,d.SecondChannel,channels,sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled));
                support=math.min(support,.8*NearbyProfileArcDistance(right.Direction,d.FirstChannel,d.SecondChannel,channels,sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled));
                if(!(support>0))throw new ArgumentException("A central crest shoulder intersects a connected channel arc; no partial support bank is published.");
                output[d.Control]=WithSupport(left,support);output[controlCursor]=WithSupport(right,support);records[divideCursor++]=new SurfaceLandformDivide(controlCursor++,d.FirstChannel,d.SecondChannel,3);
            }
            foreach(var e in edges)if(e.Selected)
            {
                SurfaceBaker.CheckCancelled(cancelled,e.Divide);var d=sourceDivides[e.Divide];var original=sourceControls[d.Control];
                var a=sourceControls[channels[d.FirstChannel].Control];var b=sourceControls[channels[d.SecondChannel].Control];
                var middle=math.normalize(nodes[e.First].Direction+nodes[e.Last].Direction);double spacing=math.length(a.Direction-b.Direction)*recipe.Radius;
                for(int half=0;half<2;half++)
                {
                    var node=nodes[half==0?e.First:e.Last];var center=math.normalize(node.Direction+middle);
                    var toward=TangentToward(center,middle);var across=math.normalize(math.cross(center,toward));if(math.dot(across,b.Direction-a.Direction)<0)across=-across;
                    double target=(node.Height+original.Height)*.5,ceiling=math.max(node.Height,original.Height);
                    // A half-edge station uses the ordered node-to-midpoint secant. Its tangent
                    // is constrained by BOTH endpoints, rather than a fork's unrelated branch.
                    double chord=math.length(node.Direction-middle)*recipe.Radius;
                    var longitudinal=toward*math.clamp((original.Height-node.Height)/math.max(1,chord),-.5,.5);
                    var strips=new FixedList512Bytes<double4>();CrestStrip(center,target,node.Direction,node.Height,ref strips);CrestStrip(center,target,middle,original.Height,ref strips);
                    longitudinal=ProjectTangentStrips(center,longitudinal,strips);
                    double support=original.SupportMetres/Math.Sqrt(Math.Sqrt(2));
                    Pair(center,target,longitudinal,across,spacing,e.Prominence,support,ceiling,out var left,out var right);
                    // Evaluate the actual displaced shoulder directions. A center-distance
                    // estimate alone would not account for the transverse shoulder offset.
                    support=math.min(support,.8*NearbyProfileArcDistance(left.Direction,d.FirstChannel,d.SecondChannel,channels,sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled));
                    support=math.min(support,.8*NearbyProfileArcDistance(right.Direction,d.FirstChannel,d.SecondChannel,channels,sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled));
                    if(!(support>0))throw new ArgumentException("An intermediate crest shoulder intersects a connected channel arc; no partial profile bank is published.");
                    output[controlCursor]=WithSupport(left,support);
                    records[divideCursor++]=new SurfaceLandformDivide(controlCursor++,d.FirstChannel,d.SecondChannel,3);
                    output[controlCursor]=WithSupport(right,support);
                    records[divideCursor++]=new SurfaceLandformDivide(controlCursor++,d.FirstChannel,d.SecondChannel,3);
                }
            }
            foreach(var node in nodes)
            {
                int count=0,anchor=-1;foreach(int id in node.Edges)if(edges[id].Selected){count++;if(anchor<0)anchor=id;}
                if(count<2||CapturedOcean(bootstrap.DrainageField,node.Direction))continue;
                SurfaceBaker.CheckCancelled(cancelled,controlCursor);var e=edges[anchor];var d=sourceDivides[e.Divide];
                double support=sourceControls[d.Control].SupportMetres;
                // One common station and datum serves every incident crest branch. At a fork the
                // tangent is fitted from ALL incident edge centers, never independently per branch.
                var reference=math.abs(node.Direction.y)<.9?new double3(0,1,0):new double3(1,0,0);var r=math.normalize(math.cross(reference,node.Direction));var f=math.cross(node.Direction,r);
                double xx=0,xy=0,yy=0,xh=0,yh=0,ceiling=node.Height;var strips=new FixedList512Bytes<double4>();
                foreach(int id in node.Edges)if(edges[id].Selected)
                {
                    var other=edges[id];var center=math.normalize(nodes[other.First].Direction+nodes[other.Last].Direction);var delta=(center-node.Direction)*recipe.Radius;
                    double x=math.dot(delta,r),y=math.dot(delta,f),dh=sourceControls[sourceDivides[other.Divide].Control].Height-node.Height,w=1/math.max(1,x*x+y*y);
                    xx+=w*x*x;xy+=w*x*y;yy+=w*y*y;xh+=w*x*dh;yh+=w*y*dh;support=math.max(support,sourceControls[sourceDivides[other.Divide].Control].SupportMetres);ceiling=math.max(ceiling,sourceControls[sourceDivides[other.Divide].Control].Height);
                    CrestStrip(node.Direction,node.Height,center,sourceControls[sourceDivides[other.Divide].Control].Height,ref strips);
                }
                double det=xx*yy-xy*xy;var g=det>1e-12?r*((xh*yy-yh*xy)/det)+f*((yh*xx-xh*xy)/det):new double3(0);
                if(math.length(g)>.5)g=math.normalize(g)*.5;
                g=ProjectTangentStrips(node.Direction,g,strips);
                // One fork datum has no anchor-edge transverse slope: such a slope was pointing
                // downhill along a DIFFERENT incident crest and producing an artificial trough.
                // Its compact support is additionally separated from actual nearby channel arcs.
                support=math.min(support,NearbyProfileArcDistance(node,edges,sourceDivides,channels,sourceControls,neighbors,degree,arcVisits,++stationVisit,recipe.Radius,cancelled)*.8);
                if(!(support>0))throw new ArgumentException("A shared crest station intersects a connected channel arc; no partial profile bank is published.");
                double variation=BoundedVariation(node.Height,recipe.MinimumHeight,math.min(recipe.MaximumHeight,ceiling));
                output[controlCursor]=new SurfaceLandformControl(node.Direction,node.Height,variation>0?g:new double3(0),support,variation);
                records[divideCursor++]=new SurfaceLandformDivide(controlCursor++,d.FirstChannel,d.SecondChannel,3);
            }
            if(controlCursor!=totalControls||divideCursor!=totalDivides)throw new InvalidOperationException("Crest preflight count differs from captured profile bank.");
            controls=output;divides=records;working=nextWorking;

            int Node(int3 key){if(nodeIds.TryGetValue(key,out int id))return id;id=nodes.Count;nodeIds.Add(key,id);nodes.Add(new CrestNode(key));return id;}
            void CrestStrip(double3 direction,double height,double3 other,double otherHeight,ref FixedList512Bytes<double4> strips)
            {
                var toward=TangentToward(direction,other);double dh=otherHeight-height,chord=math.length(direction-other)*recipe.Radius;
                strips.Add(new double4(dh<0?-toward:toward,math.abs(dh)/chord));
            }
            void Pair(double3 center,double target,double3 along,double3 across,double spacing,double prominence,double support,double ceiling,out SurfaceLandformControl left,out SurfaceLandformControl right)
            {
                double width=math.clamp(spacing*.04,25,200),slope=math.clamp(prominence/math.max(1,spacing*.5)*1.5,.1,.7);
                // Even individual Hermite terms remain below existing adjacent crest heights.
                // Sharper curvature/connectedness is not supplied by a hidden amplitude increase.
                double h=math.max(recipe.MinimumHeight,target-slope*width),variation=BoundedVariation(h,recipe.MinimumHeight,math.min(recipe.MaximumHeight,ceiling));
                var ld=math.normalize(center-across*(width/recipe.Radius));var rd=math.normalize(center+across*(width/recipe.Radius));
                var lg=along+across*slope;lg-=ld*math.dot(ld,lg);var rg=along-across*slope;rg-=rd*math.dot(rd,rg);
                left=new SurfaceLandformControl(ld,h,variation>0?lg:new double3(0),support,variation);right=new SurfaceLandformControl(rd,h,variation>0?rg:new double3(0),support,variation);
            }
        }
        static SurfaceLandformControl WithSupport(SurfaceLandformControl control,double support)
            =>new SurfaceLandformControl(control.Direction,control.Height,control.Gradient,support,control.VariationLimit);

        static double QuarterBridgeSupport(in SurfaceLandformControl midpoint,double3 first,double3 last,double radius,double tightenedSupport)
        {
            double quarterDistance=math.max(math.length(math.normalize(first+midpoint.Direction)-midpoint.Direction),
                math.length(math.normalize(last+midpoint.Direction)-midpoint.Direction))*radius;
            // The radius is the ORIGINAL divide radius (max cell radius * 1.25), not a value
            // inferred from a primary cell's independently selected 1.1/1.6 support margin.
            return tightenedSupport<quarterDistance?midpoint.SupportMetres:tightenedSupport;
        }

        // Generation-only nearest arc bank. Captured radial supports are the only output;
        // neither the authority codec nor runtime/GPU query layout gains this scratch index.
        sealed class CrestSupportIndex
        {
            struct ArcRecord{public double3 First,Last,Minimum,Maximum;}
            struct SearchNode{public double3 Minimum,Maximum;public int Left,Right,First,Count;}
            readonly ArcRecord[] arcs;readonly SearchNode[] tree;readonly int[] ids;
            readonly IComparer<int>[] comparers;int cursor;
            sealed class ArcComparer:IComparer<int>
            {
                readonly ArcRecord[] arcs;readonly int axis;
                public ArcComparer(ArcRecord[] arcs,int axis){this.arcs=arcs;this.axis=axis;}
                public int Compare(int a,int b)
                {int order=(arcs[a].Minimum[axis]+arcs[a].Maximum[axis]).CompareTo(arcs[b].Minimum[axis]+arcs[b].Maximum[axis]);return order!=0?order:a.CompareTo(b);}
            }
            public CrestSupportIndex(List<CrestNode> vertices,List<CrestEdge> edges,int count,Func<bool> cancelled)
            {
                arcs=new ArcRecord[count];ids=new int[count];tree=new SearchNode[checked(count*2)];int own=0;
                foreach(var edge in edges)if(edge.Selected)
                {
                    SurfaceBaker.CheckCancelled(cancelled,own);var a=vertices[edge.First].Direction;var b=vertices[edge.Last].Direction;
                    double halfChordSquared=math.lengthsq(a-b)*.25;
                    // Every short-arc point is a normalized convex chord point. Normalizing it
                    // moves it by at most the chord sagitta, giving a conservative component box.
                    double sagitta=halfChordSquared/(1+Math.Sqrt(math.max(0,1-halfChordSquared)))+1.4210854715202004e-14;
                    arcs[own]=new ArcRecord{First=a,Last=b,Minimum=math.min(a,b)-sagitta,Maximum=math.max(a,b)+sagitta};ids[own]=own;own++;
                }
                if(own!=count)throw new InvalidOperationException("Selected crest index count differs from admitted count.");
                comparers=new IComparer<int>[]{new ArcComparer(arcs,0),new ArcComparer(arcs,1),new ArcComparer(arcs,2)};
                Build(0,count,cancelled);
            }
            int Build(int first,int count,Func<bool> cancelled)
            {
                SurfaceBaker.CheckCancelled(cancelled,cursor);int own=cursor++;var lo=new double3(double.PositiveInfinity);var hi=new double3(double.NegativeInfinity);
                for(int i=first;i<first+count;i++){SurfaceBaker.CheckCancelled(cancelled,i-first);lo=math.min(lo,arcs[ids[i]].Minimum);hi=math.max(hi,arcs[ids[i]].Maximum);}
                if(count<=8){tree[own]=new SearchNode{Minimum=lo,Maximum=hi,Left=-1,Right=-1,First=first,Count=count};return own;}
                var size=hi-lo;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;Array.Sort(ids,first,count,comparers[axis]);int half=count/2;
                int left=Build(first,half,cancelled),right=Build(first+half,count-half,cancelled);
                tree[own]=new SearchNode{Minimum=lo,Maximum=hi,Left=left,Right=right};return own;
            }
            public double NearestChord(double3 direction,double cutoff,Func<bool> cancelled)
            {
                double closest=cutoff;int tests=0,visits=0;Search(0,direction,ref closest,ref tests,ref visits,cancelled);return closest;
            }
            void Search(int id,double3 direction,ref double closest,ref int tests,ref int visits,Func<bool> cancelled)
            {
                var node=tree[id];if(BoxDistance(direction,node)>=closest)return;
                SurfaceBaker.CheckCancelled(cancelled,visits++);
                if(visits>4096)throw new ArgumentException("Crest support search exceeds its explicit 4096-node query cap.");
                if(node.Left<0)
                {
                    for(int i=node.First;i<node.First+node.Count;i++)
                    {
                        if(++tests>MaximumNearbyProfileArcQueries)throw new ArgumentException("Crest support search exceeds its explicit arc-query cap.");
                        var arc=arcs[ids[i]];closest=math.min(closest,ChordDistanceToArc(direction,arc.First,arc.Last));
                    }
                    return;
                }
                int first=node.Left,last=node.Right;if(BoxDistance(direction,tree[first])>BoxDistance(direction,tree[last])){first=node.Right;last=node.Left;}
                Search(first,direction,ref closest,ref tests,ref visits,cancelled);Search(last,direction,ref closest,ref tests,ref visits,cancelled);
            }
            static double BoxDistance(double3 point,SearchNode node)
                =>math.length(math.max(math.max(node.Minimum-point,point-node.Maximum),new double3(0)));
        }
        static double NearbyProfileArcDistance(CrestNode node,List<CrestEdge> edges,SurfaceLandformDivide[] divides,
            SurfaceLandformChannel[] channels,SurfaceLandformControl[] controls,int[] neighbors,int[] degree,int[] visited,
            int visit,double radius,Func<bool> cancelled)
        {
            // Every channel of this shared vertex, its one-ring cell neighbors, and their
            // incident parent/child arcs. Fixed stack storage avoids a 4KB captured closure
            // allocation for each station. Work is explicit and deduplicated by child ID.
            var local=new FixedList4096Bytes<int>();foreach(int id in node.Edges)if(edges[id].Selected)
            {var d=divides[edges[id].Divide];AddProfileCell(ref local,d.FirstChannel);AddProfileCell(ref local,d.SecondChannel);}
            return NearbyProfileArcDistance(node.Direction,ref local,channels,controls,neighbors,degree,visited,visit,radius,cancelled);
        }
        static double NearbyProfileArcDistance(double3 direction,int first,int second,SurfaceLandformChannel[] channels,
            SurfaceLandformControl[] controls,int[] neighbors,int[] degree,int[] visited,int visit,double radius,Func<bool> cancelled)
        {
            var local=new FixedList4096Bytes<int>();AddProfileCell(ref local,first);AddProfileCell(ref local,second);
            return NearbyProfileArcDistance(direction,ref local,channels,controls,neighbors,degree,visited,visit,radius,cancelled);
        }
        static double NearbyProfileArcDistance(double3 direction,ref FixedList4096Bytes<int> local,SurfaceLandformChannel[] channels,
            SurfaceLandformControl[] controls,int[] neighbors,int[] degree,int[] visited,int visit,double radius,Func<bool> cancelled)
        {
            int incident=local.Length;for(int i=0;i<incident;i++)for(int j=0;j<degree[local[i]];j++)AddProfileCell(ref local,neighbors[local[i]*8+j]);
            int queries=0;double closest=double.PositiveInfinity;
            for(int i=0;i<local.Length;i++)
            {
                int channel=local[i];ObserveProfileArc(direction,channel,channels,controls,visited,visit,radius,cancelled,ref queries,ref closest);
                for(int j=0;j<degree[channel];j++)
                {int other=neighbors[channel*8+j];if(channels[other].Parent==channel)ObserveProfileArc(direction,other,channels,controls,visited,visit,radius,cancelled,ref queries,ref closest);}
            }
            return closest;
        }
        static void AddProfileCell(ref FixedList4096Bytes<int> local,int channel)
        {
            for(int i=0;i<local.Length;i++)if(local[i]==channel)return;
            if(local.Length>=144)throw new ArgumentException("Crest local support neighborhood exceeds its explicit 144-cell cap.");local.Add(channel);
        }
        static void ObserveProfileArc(double3 direction,int child,SurfaceLandformChannel[] channels,SurfaceLandformControl[] controls,
            int[] visited,int visit,double radius,Func<bool> cancelled,ref int queries,ref double closest)
        {
            int parent=channels[child].Parent;if(parent<0||visited[child]==visit)return;visited[child]=visit;
            SurfaceBaker.CheckCancelled(cancelled,queries++);
            if(queries>MaximumNearbyProfileArcQueries)throw new ArgumentException("Crest local connected-arc queries exceed their declared cap.");
            var first=controls[channels[parent].Control].Direction;var last=controls[channels[child].Control].Direction;
            closest=math.min(closest,ChordDistanceToArc(direction,first,last)*radius);
        }
        static double ChordDistanceToArc(double3 direction,double3 first,double3 last)
        {
            var normal=math.normalize(math.cross(first,last));var projected=math.normalizesafe(direction-normal*math.dot(direction,normal));
            double distance=math.min(math.length(direction-first),math.length(direction-last));
            if(math.lengthsq(projected)>0&&math.dot(math.cross(first,projected),normal)>=-1.4210854715202004e-14&&math.dot(math.cross(projected,last),normal)>=-1.4210854715202004e-14)
                distance=math.min(distance,math.length(direction-projected));
            return distance;
        }
        // Canonical integer cube lattice makes adaptive shared vertices identical across faces,
        // including T junctions and poles. Only a positive-length actual shared edge is admitted.
        static void SharedCorners(SurfaceTileKey first,SurfaceTileKey last,out int3 begin,out int3 end)
        {
            begin=end=default;int found=0;
            for(int a=0;a<4;a++)for(int b=0;b<4;b++)
            {
                Edge(first,a,out var a0,out var a1);Edge(last,b,out var b0,out var b1);int axis=Axis(a0,a1);
                if(axis!=Axis(b0,b1))continue;bool same=true;for(int k=0;k<3;k++)if(k!=axis&&a0[k]!=b0[k])same=false;if(!same)continue;
                int lo=math.max(math.min(a0[axis],a1[axis]),math.min(b0[axis],b1[axis]));int hi=math.min(math.max(a0[axis],a1[axis]),math.max(b0[axis],b1[axis]));
                if(lo>=hi)continue;begin=a0;end=a0;begin[axis]=lo;end[axis]=hi;found++;
            }
            if(found!=1)throw new ArgumentException("Crest channels do not have one actual shared adaptive cube edge.");
            int Axis(int3 a,int3 b){for(int k=0;k<3;k++)if(a[k]!=b[k])return k;throw new ArgumentException("Degenerate crest edge.");}
            void Edge(SurfaceTileKey key,int side,out int3 a,out int3 b)
            {a=Corner(key,side==0?0:side==1?1:0,side==2?0:side==3?1:0);b=Corner(key,side==0?0:side==1?1:1,side==2?0:side==3?1:1);}
            int3 Corner(SurfaceTileKey key,int x,int y)
            {
                const int grid=1<<12;int step=1<<(12-key.Level),u=2*(key.X+x)*step-grid,v=2*(key.Y+y)*step-grid;
                switch(key.Face){case 0:return new int3(grid,v,-u);case 1:return new int3(-grid,v,u);case 2:return new int3(u,grid,-v);case 3:return new int3(u,-grid,v);case 4:return new int3(u,v,grid);default:return new int3(-u,v,-grid);}
            }
        }
        static bool CapturedOcean(in NativeSurfaceDrainageView coast,double3 direction)
        {
            for(int i=0;i<coast.Landmasses.Length;i++)if(SurfaceDrainageMath.Contains(coast,coast.Landmasses[i],direction))return false;
            return true;
        }
        static double BoundedVariation(double height,double lower,double upper)
        {
            double variation=math.max(0,math.min(height-lower,upper-height));
            // Preserve a strict captured interval even when h +/- V rounds outward by one ULP.
            while(variation>0&&(height-variation<lower||height+variation>upper))
                variation=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(variation)-1);
            return variation;
        }
        static double3 TangentToward(double3 normal,double3 other)=>math.normalize(other-normal*math.dot(normal,other));
        static double Secant(double upstream,double downstream,double3 first,double3 last,double radius)
        {
            double chord=math.length(first-last)*radius,bound=(upstream-downstream)/chord;
            if(!(chord>0)||!math.isfinite(bound)||bound<0)throw new ArgumentException("Captured flow secant must be finite, positive-distance and downstream ordered.");
            return bound;
        }
        static double3 ProjectTangentStrips(double3 normal,double3 gradient,FixedList512Bytes<double4> strips)
        {
            if(FeasibleStrips(gradient,strips))return gradient;
            // Zero is feasible for every 0 <= tangent dot g <= secant strip. In a 2D tangent
            // plane the exact nearest point is either the input, a boundary projection or an
            // intersection of two boundaries. At most eight strips use bounded stack storage.
            var best=new double3(0);double bestDistance=math.lengthsq(gradient);
            for(int i=0;i<strips.Length;i++)for(int side=0;side<2;side++)
            {
                var a=strips[i].xyz;double boundA=side==0?0:strips[i].w;
                var candidate=gradient+a*((boundA-math.dot(a,gradient))/math.lengthsq(a));
                candidate-=normal*math.dot(normal,candidate);
                ConsiderStripCandidate(candidate,gradient,strips,ref best,ref bestDistance);
                for(int j=0;j<i;j++)for(int otherSide=0;otherSide<2;otherSide++)
                {
                    var b=strips[j].xyz;double determinant=math.dot(normal,math.cross(a,b));if(determinant==0)continue;
                    double boundB=otherSide==0?0:strips[j].w;
                    // Cross-product determinant avoids cancellation in 1-dot(a,b)^2 for nearly
                    // parallel strips. Nonfinite far intersections cannot beat the feasible zero.
                    candidate=(math.cross(b,normal)*boundA+math.cross(normal,a)*boundB)/determinant;
                    ConsiderStripCandidate(candidate,gradient,strips,ref best,ref bestDistance);
                }
            }
            return best;
        }
        static void ConsiderStripCandidate(double3 candidate,double3 original,FixedList512Bytes<double4> strips,ref double3 best,ref double bestDistance)
        {
            if(!math.all(math.isfinite(candidate)))return;double distance=math.lengthsq(candidate-original);
            if(distance<bestDistance&&FeasibleStrips(candidate,strips)){best=candidate;bestDistance=distance;}
        }
        static bool FeasibleStrips(double3 gradient,FixedList512Bytes<double4> strips)
        {
            const double roundingAllowance=1.4210854715202004e-14;
            for(int i=0;i<strips.Length;i++){double slope=math.dot(gradient,strips[i].xyz);if(slope< -roundingAllowance||slope>strips[i].w+roundingAllowance)return false;}
            return true;
        }
        static double3 ProjectTangentCone(double3 normal,double3 gradient,FixedList512Bytes<double3> constraints)
        {
            if(Feasible(gradient,constraints))return gradient;
            // In the two-dimensional tangent plane the nearest point of a homogeneous convex
            // cone is either the original vector, an admitted boundary projection, or its vertex.
            // The explicit <=8 constraints require no iterative solve or per-control allocation.
            var best=new double3(0);double bestDistance=math.lengthsq(gradient);
            for(int i=0;i<constraints.Length;i++)
            {
                var boundary=constraints[i];var candidate=gradient-boundary*math.dot(gradient,boundary);
                candidate-=normal*math.dot(normal,candidate);
                if(!Feasible(candidate,constraints))continue;double distance=math.lengthsq(candidate-gradient);
                if(distance<bestDistance){best=candidate;bestDistance=distance;}
            }
            return best;
        }
        static bool Feasible(double3 gradient,FixedList512Bytes<double3> constraints)
        {
            const double roundingAllowance=1.4210854715202004e-14;
            for(int i=0;i<constraints.Length;i++)if(math.dot(gradient,constraints[i])< -roundingAllowance)return false;
            return true;
        }
        static double CellRadius(double radius,SurfaceTileKey key,double3 center)
        {double distance=0;for(int c=0;c<4;c++){CubeSurface.TryDirection(key,new double2(c&1,c>>1),out var corner);distance=math.max(distance,Arc(center,corner)*radius);}return distance;}
        static double SolidAngle(double3 a,double3 b,double3 c)=>2*Math.Atan2(math.abs(math.dot(a,math.cross(b,c))),1+math.dot(a,b)+math.dot(b,c)+math.dot(c,a));
        static double Arc(double3 a,double3 b)=>2*Math.Asin(math.min(1,math.length(a-b)*.5));
        static double Smooth(double x){x=math.clamp(x,0,1);return x*x*(3-2*x);}
        static double Distance(SurfaceGeologicalBoundary edge,double3 d,double radius)
        {
            double side=math.dot(d,edge.Normal);var projected=math.normalizesafe(d-edge.Normal*side);double length=Arc(edge.Start,edge.End);
            return (Arc(projected,edge.Start)+Arc(projected,edge.End)<=length+1e-9?Math.Asin(math.min(1,math.abs(side))):math.min(Arc(d,edge.Start),Arc(d,edge.End)))*radius;
        }
        static double3 Cube(int face,double a,double b)
        {switch(face){case 0:return new double3(1,b,-a);case 1:return new double3(-1,b,a);case 2:return new double3(a,1,-b);case 3:return new double3(a,-1,b);case 4:return new double3(a,b,1);default:return new double3(-a,b,-1);}}
        sealed class FloodHeap
        {
            readonly int[] ids;readonly double[] values;int count;public int Count=>count;
            public FloodHeap(int maximum){ids=new int[maximum];values=new double[maximum];}
            static bool Earlier(double first,int a,double second,int b)=>first<second||(first==second&&a<b);
            public void Push(int id,double value)
            {if(count==ids.Length)throw new ArgumentException("Flood heap exceeds admitted graph.");int i=count++;while(i>0){int p=(i-1)/2;if(!Earlier(value,id,values[p],ids[p]))break;ids[i]=ids[p];values[i]=values[p];i=p;}ids[i]=id;values[i]=value;}
            public void Pop(out int id,out double value)
            {id=ids[0];value=values[0];int last=--count;if(last==0)return;int item=ids[last];double itemValue=values[last];int i=0;while(i*2+1<count){int c=i*2+1;if(c+1<count&&Earlier(values[c+1],ids[c+1],values[c],ids[c]))c++;if(!Earlier(values[c],ids[c],itemValue,item))break;ids[i]=ids[c];values[i]=values[c];i=c;}ids[i]=item;values[i]=itemValue;}
        }
    }
}
