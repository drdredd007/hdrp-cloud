using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>
    /// Immutable algorithm-five channel/divide reconstruction authority. The adaptive partition and
    /// Hermite controls are captured; the bounded KD partition is derived, never a seed reconstruction.
    /// Compact overlapping controls cover every point, including interfluves outside channel beds.
    /// </summary>
    public sealed class SurfaceLandformField
    {
        public const int CurrentVersion=1, MaximumCells=262144, MaximumControls=1048576;
        public const int MaximumIndexNodes=1048576, MaximumReferences=4194304, MaximumLeafCandidates=96, MaximumIndexDepth=30;
        public const long MaximumResidentBytes=144L*1024*1024;
        readonly SurfaceLandformControl[] controls;
        readonly SurfaceLandformChannel[] channels;
        readonly SurfaceLandformDivide[] divides;
        readonly SurfaceLandformCell[] cells;
        readonly int[] cellDivides, references;
        readonly SurfaceDrainageIndexNode[] index;
        public SurfaceRecipe SourceRecipe {get;}
        public SurfaceContentHash ContentDigest {get;}
        public IReadOnlyList<SurfaceLandformControl> Controls {get;}
        public IReadOnlyList<SurfaceLandformChannel> Channels {get;}
        public IReadOnlyList<SurfaceLandformDivide> Divides {get;}
        public IReadOnlyList<SurfaceLandformCell> Cells {get;}
        public double MinimumHeight {get;}
        public double MaximumHeight {get;}
        public int IndexNodeCount=>index.Length;
        public int SpatialReferenceCount=>references.Length;
        public int CellDivideReferenceCount=>cellDivides.Length;
        public int MaximumActualDepth {get;}
        public long EstimatedResidentBytes {get;}

        public SurfaceLandformField(SurfaceRecipe recipe,SurfaceLandformControl[] controls,SurfaceLandformChannel[] channels,
            SurfaceLandformDivide[] divides,SurfaceLandformCell[] cells,int[] cellDivides,Func<bool> cancelled=null)
        {
            if(!recipe.IsValid||recipe.AlgorithmVersion!=5||controls==null||channels==null||divides==null||cells==null||cellDivides==null||
                controls.Length<6||controls.Length>MaximumControls||cells.Length<6||cells.Length>MaximumCells||channels.Length!=cells.Length||
                divides.Length>MaximumControls||cellDivides.Length>4L*MaximumCells*2)
                throw new ArgumentException("Invalid captured landform counts before allocation.");
            // Reserve authority copies and the maximum derived index before cloning any caller arrays.
            long authority=EstimateAuthorityBytes(controls.Length,channels.Length,divides.Length,cells.Length,cellDivides.Length);
            if(authority+MaximumIndexNodes*32L+MaximumReferences*4L>MaximumResidentBytes)
                throw new ArgumentException("Landform authority and reserved derived index exceed the explicit resident budget.");
            SourceRecipe=recipe;
            ValidateCover(cells,cancelled);
            var usedControls=new bool[controls.Length];var childArea=new double[channels.Length];double rootArea=0;
            double lo=double.PositiveInfinity,hi=double.NegativeInfinity;
            for(int i=0;i<controls.Length;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var c=controls[i];
                if(!Unit(c.Direction)||!math.all(math.isfinite(c.Gradient))||math.abs(math.dot(c.Direction,c.Gradient))>1e-9||
                    !math.isfinite(c.Height)||!Positive(c.SupportMetres)||c.SupportMetres>recipe.Radius*2||
                    !math.isfinite(c.VariationLimit)||c.VariationLimit<0||math.length(c.Gradient)>2||(c.VariationLimit==0&&math.lengthsq(c.Gradient)>0)||
                    c.Height-c.VariationLimit<recipe.MinimumHeight||c.Height+c.VariationLimit>recipe.MaximumHeight)
                    throw new ArgumentException("Landform controls require finite captured tangent slopes and safe height intervals.");
                lo=math.min(lo,c.Height-c.VariationLimit);hi=math.max(hi,c.Height+c.VariationLimit);
            }
            for(int i=0;i<channels.Length;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var c=channels[i];
                if(c.Control<0||c.Control>=controls.Length||usedControls[c.Control]||c.Parent< -1||c.Parent>=i||c.Outlet<0||c.Outlet>i||
                    c.Strahler<1||c.Strahler>32||!Positive(c.Area)||!math.isfinite(c.RiseScale)||c.RiseScale<0)
                    throw new ArgumentException("Landform channels require unique controls and parent-first drainage.");
                usedControls[c.Control]=true;
                if(c.Parent<0){if(c.Outlet!=i)throw new ArgumentException("Landform outlet must identify itself.");rootArea+=c.Area;}
                else
                {
                    var p=channels[c.Parent];if(p.Outlet!=c.Outlet||c.Area>p.Area*(1+1e-10)||controls[c.Control].Height<controls[p.Control].Height)
                        throw new ArgumentException("Landform flow must descend with conserved area and shared outlet.");
                    childArea[c.Parent]+=c.Area;
                }
            }
            if(rootArea>4*Math.PI*recipe.Radius*recipe.Radius*(1+1e-9))throw new ArgumentException("Captured catchments exceed sphere area.");
            for(int i=0;i<channels.Length;i++)if(childArea[i]>channels[i].Area*(1+1e-9))throw new ArgumentException("Child catchments duplicate parent area.");
            for(int i=0;i<divides.Length;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var d=divides[i];
                if(d.Control<0||d.Control>=controls.Length||usedControls[d.Control]||d.FirstChannel<0||d.SecondChannel<0||
                    d.FirstChannel>=channels.Length||d.SecondChannel>=channels.Length||d.FirstChannel==d.SecondChannel||d.Flags<0||d.Flags>3)
                    throw new ArgumentException("Shared divides require unique captured controls and two neighboring channels.");
                usedControls[d.Control]=true;
            }
            foreach(bool used in usedControls)if(!used)throw new ArgumentException("Unreferenced landform control is not authority.");
            var seenChannels=new bool[channels.Length];
            for(int i=0;i<cells.Length;i++)
            {
                var c=cells[i];if(c.Channel<0||c.Channel>=channels.Length||seenChannels[c.Channel]||c.FirstDivide<0||c.DivideCount<0||c.DivideCount>8||
                    (long)c.FirstDivide+c.DivideCount>cellDivides.Length)throw new ArgumentException("Covering cells require one unique channel and bounded shared divides.");
                seenChannels[c.Channel]=true;
                if(!CubeSurface.TryDirection(c.Key,new double2(.5),out var center)||math.length(center-controls[channels[c.Channel].Control].Direction)>1e-10)
                    throw new ArgumentException("Channel center does not belong to its captured covering cell.");
                // A center support alone strictly covers its entire closed cell. Other controls refine,
                // but their omission is never used to fill a missing canonical page.
                for(int corner=0;corner<4;corner++)
                {
                    CubeSurface.TryDirection(c.Key,new double2(corner&1,corner>>1),out var direction);
                    if(math.length(direction-center)*recipe.Radius>=controls[channels[c.Channel].Control].SupportMetres*(1-1e-10))
                        throw new ArgumentException("Captured control support does not cover its complete cell.");
                }
                for(int j=0;j<c.DivideCount;j++)
                {
                    int d=cellDivides[c.FirstDivide+j];if(d<0||d>=divides.Length||(divides[d].FirstChannel!=c.Channel&&divides[d].SecondChannel!=c.Channel))
                        throw new ArgumentException("A cell divide must refer to its shared channel pair.");
                }
            }
            this.controls=(SurfaceLandformControl[])controls.Clone();this.channels=(SurfaceLandformChannel[])channels.Clone();
            this.divides=(SurfaceLandformDivide[])divides.Clone();this.cells=(SurfaceLandformCell[])cells.Clone();this.cellDivides=(int[])cellDivides.Clone();
            BuildIndex(this.controls,recipe.Radius,cancelled,out index,out references,out int depth);
            MaximumActualDepth=depth;MinimumHeight=lo;MaximumHeight=hi;
            EstimatedResidentBytes=checked(authority+index.LongLength*32+references.LongLength*4);
            if(EstimatedResidentBytes>MaximumResidentBytes)throw new ArgumentException("Landform control index exceeds the explicit resident budget.");
            Controls=Array.AsReadOnly(this.controls);Channels=Array.AsReadOnly(this.channels);Divides=Array.AsReadOnly(this.divides);Cells=Array.AsReadOnly(this.cells);
            ContentDigest=SurfaceHashing.Compute(w=>
            {
                w.Write(CurrentVersion);SurfaceHashing.WriteRecipe(w,recipe);w.Write(controls.Length);
                foreach(var c in this.controls){Write(w,c.Direction);Write(w,c.Gradient);w.Write(c.Height);w.Write(c.SupportMetres);w.Write(c.VariationLimit);}
                w.Write(channels.Length);foreach(var c in this.channels){w.Write(c.Control);w.Write(c.Parent);w.Write(c.Outlet);w.Write(c.Strahler);w.Write(c.Area);w.Write(c.RiseScale);}
                w.Write(divides.Length);foreach(var d in this.divides){w.Write(d.Control);w.Write(d.FirstChannel);w.Write(d.SecondChannel);w.Write(d.Flags);}
                w.Write(cells.Length);foreach(var c in this.cells){w.Write(c.Key.Face);w.Write(c.Key.Level);w.Write(c.Key.X);w.Write(c.Key.Y);w.Write(c.Channel);w.Write(c.FirstDivide);w.Write(c.DivideCount);}
                w.Write(cellDivides.Length);foreach(int d in this.cellDivides)w.Write(d);
            });
        }
        public static long EstimateAuthorityBytes(int controls,int channels,int divides,int cells,int cellReferences)=>checked(512L+controls*72L+channels*32L+divides*16L+cells*28L+cellReferences*4L);
        internal SurfaceDrainageIndexNode IndexNodeAt(int i)=>index[i];
        internal int SpatialReferenceAt(int i)=>references[i];
        public int CellDivideAt(int i)=>cellDivides[i];
        static void Write(System.IO.BinaryWriter w,double3 v){w.Write(v.x);w.Write(v.y);w.Write(v.z);}
        static bool Unit(double3 v)=>math.all(math.isfinite(v))&&math.abs(math.lengthsq(v)-1)<1e-10;
        static bool Positive(double x)=>math.isfinite(x)&&x>0;
        static void ValidateCover(SurfaceLandformCell[] cells,Func<bool> cancelled)
        {
            var keys=new HashSet<SurfaceTileKey>();int maximumLevel=0;
            for(int i=0;i<cells.Length;i++){SurfaceBaker.CheckCancelled(cancelled,i);var k=cells[i].Key;if(!k.IsValid||k.Level>12||!keys.Add(k))throw new ArgumentException("Invalid or duplicate landform covering cell.");maximumLevel=math.max(maximumLevel,k.Level);}
            long area=0,total=6L<<(2*maximumLevel);
            foreach(var k in keys)
            {
                for(int level=k.Level-1;level>=0;level--)if(keys.Contains(new SurfaceTileKey(k.Face,level,k.X>>(k.Level-level),k.Y>>(k.Level-level))))throw new ArgumentException("Landform cells overlap.");
                area+=1L<<(2*(maximumLevel-k.Level));
            }
            if(area!=total)throw new ArgumentException("Landform authority requires complete closed-sphere coverage.");
        }
        static void BuildIndex(SurfaceLandformControl[] controls,double radius,Func<bool> cancelled,out SurfaceDrainageIndexNode[] index,out int[] refs,out int depth)
        {
            var boxes=new SurfaceDrainageSegment[controls.Length];var ids=new int[controls.Length];
            for(int i=0;i<controls.Length;i++){SurfaceBaker.CheckCancelled(cancelled,i);double margin=controls[i].SupportMetres/radius+1e-12;boxes[i]=new SurfaceDrainageSegment(controls[i].Direction-margin,controls[i].Direction+margin,0,0);ids[i]=i;}
            var tree=new List<SurfaceDrainageIndexNode>();var references=new List<int>();depth=0;
            BuildCell(boxes,controls,radius,new double3(-1),new double3(1),ids,0,tree,references,cancelled,ref depth);
            index=tree.ToArray();refs=references.ToArray();
        }
        static int BuildCell(SurfaceDrainageSegment[] boxes,SurfaceLandformControl[] controls,double radius,double3 lo,double3 hi,int[] candidates,int depth,List<SurfaceDrainageIndexNode> tree,List<int> refs,Func<bool> cancelled,ref int deepest)
        {
            SurfaceBaker.CheckCancelled(cancelled);deepest=math.max(deepest,depth);
            if(tree.Count>=MaximumIndexNodes)throw new ArgumentException("Landform index exceeds bounded node count.");
            int own=tree.Count;tree.Add(default);
            if(candidates.Length<=MaximumLeafCandidates)
            {
                if((long)refs.Count+candidates.Length>MaximumReferences)throw new ArgumentException("Landform index exceeds bounded references.");
                int first=refs.Count;refs.AddRange(candidates);tree[own]=new SurfaceDrainageIndexNode(0,-1,-1,-1,first,candidates.Length);return own;
            }
            if(depth>=MaximumIndexDepth)throw new ArgumentException(DescribeLeafRefusal(controls,radius,lo,hi,candidates,cancelled));
            var size=hi-lo;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;double split=(lo[axis]+hi[axis])*.5;
            var left=new List<int>();var right=new List<int>();
            var lhi=hi;lhi[axis]=split;var rlo=lo;rlo[axis]=split;
            foreach(int i in candidates)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);
                // A Cartesian support box can overlap a leaf even when its compact chord ball
                // is disjoint from the entire closed leaf. Exclude only that certified case.
                // The same unit-coordinate halo used for boxes covers arithmetic at split planes;
                // surviving IDs keep their original ascending order and the query cap is unchanged.
                if(boxes[i].Minimum[axis]<=split&&SupportReachesCell(controls[i],radius,lo,lhi))left.Add(i);
                if(boxes[i].Maximum[axis]>=split&&SupportReachesCell(controls[i],radius,rlo,hi))right.Add(i);
            }
            int l=BuildCell(boxes,controls,radius,lo,lhi,left.ToArray(),depth+1,tree,refs,cancelled,ref deepest);
            int r=BuildCell(boxes,controls,radius,rlo,hi,right.ToArray(),depth+1,tree,refs,cancelled,ref deepest);
            tree[own]=new SurfaceDrainageIndexNode(split,axis,l,r,0,0);return own;
        }
        static bool SupportReachesCell(SurfaceLandformControl control,double radius,double3 lo,double3 hi)
        {
            double3 offset=math.max(math.max(lo-control.Direction,control.Direction-hi),0);
            double reach=control.SupportMetres/radius+1e-12;
            return math.lengthsq(offset)<=reach*reach;
        }
        static string DescribeLeafRefusal(SurfaceLandformControl[] controls,double radius,double3 lo,double3 hi,
            int[] candidates,Func<bool> cancelled)
        {
            // Admission failure concerns an AABB leaf, not necessarily an actual spherical query.
            // Eight bounded feasible directions provide causal evidence without changing the index,
            // sampling, accepted candidate limit, or the refusal. They do not certify the whole leaf.
            double3 closest=math.clamp(new double3(0),lo,hi);
            double minimumNormSquared=math.lengthsq(closest),maximumNormSquared=0;
            double minimumSupport=double.PositiveInfinity,maximumSupport=0;
            foreach(int id in candidates)
            {
                SurfaceBaker.CheckCancelled(cancelled,id);
                minimumSupport=math.min(minimumSupport,controls[id].SupportMetres);
                maximumSupport=math.max(maximumSupport,controls[id].SupportMetres);
            }
            int directions=0,maximumActive=-1;double3 worst=default;
            for(int corner=0;corner<8;corner++)
            {
                double3 far=new double3((corner&1)==0?lo.x:hi.x,(corner&2)==0?lo.y:hi.y,(corner&4)==0?lo.z:hi.z);
                double farNormSquared=math.lengthsq(far);maximumNormSquared=math.max(maximumNormSquared,farNormSquared);
                if(minimumNormSquared>1||farNormSquared<1)continue;
                double3 delta=far-closest;double lengthSquared=math.lengthsq(delta),dot=math.dot(closest,delta);
                if(lengthSquared<=0)continue;
                double root=math.sqrt(math.max(0,dot*dot+lengthSquared*(1-minimumNormSquared)));
                double denominator=dot+root;
                if(denominator<=0)continue;
                double t=(1-minimumNormSquared)/denominator;
                double3 direction=closest+math.clamp(t,0,1)*delta;int active=0;
                foreach(int id in candidates)
                {
                    SurfaceBaker.CheckCancelled(cancelled,id);
                    if(math.length(direction-controls[id].Direction)*radius<controls[id].SupportMetres)active++;
                }
                directions++;
                if(active>maximumActive){maximumActive=active;worst=direction;}
            }
            return FormattableString.Invariant($"Landform supports cannot satisfy per-query candidate budget. depth={MaximumIndexDepth}, AabbCandidates={candidates.Length}, limit={MaximumLeafCandidates}, leafMetricSize={((hi-lo)*radius)}, normSquaredRange={minimumNormSquared:R}..{maximumNormSquared:R}, feasibleDirectionSamples={directions}, maximumActiveAtSamples={maximumActive}, worstDirection={worst}, supportRangeMetres={minimumSupport:R}..{maximumSupport:R}. Sampled active counts do not bound every direction in the leaf.");
        }
        /// <summary>Diagnostic dry crest address, at least 200m above the clamped source sea datum.
        /// This metadata query does not certify an offset camera/region footprint; the caller must
        /// check its actual published Full heights, including erosion/refinement/stamps.</summary>
        public bool TryMountainRegion(out double3 direction)
        {
            direction=default;double best=0,dry=math.clamp(SourceRecipe.SeaLevel,SourceRecipe.MinimumHeight,SourceRecipe.MaximumHeight)+200;
            foreach(var divide in divides)
            {
                var c=controls[divide.Control];var first=controls[channels[divide.FirstChannel].Control];var second=controls[channels[divide.SecondChannel].Control];
                bool connected=channels[divide.FirstChannel].Parent==divide.SecondChannel||channels[divide.SecondChannel].Parent==divide.FirstChannel;
                if(divide.Flags==0||connected||c.Height<=dry||first.Height<=dry||second.Height<=dry)continue;
                double prominence=c.Height-math.max(first.Height,second.Height);
                if(prominence>best){best=prominence;direction=c.Direction;}
            }
            return best>=100;
        }
    }
}
