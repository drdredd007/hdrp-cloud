using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>
    /// Explicit worker-built renderer cache: every source leaf contributes a bounded Full quadrature
    /// integral; dyadic parents aggregate all descendants. Coarse Hermite interpolation is an
    /// approximation, never a replacement physics surface or a persisted seed reconstruction.
    /// </summary>
    public sealed class SurfaceLandformFilterHierarchy
    {
        public const int MaximumIndexNodes=1048576,MaximumReferences=4194304;
        readonly SurfaceLandformControl[] controls;
        readonly SurfaceLandformFilterLevel[] levels;
        readonly SurfaceDrainageIndexNode[] index;
        readonly int[] references;
        public SurfaceContentHash SourceDigest{get;}
        public SurfaceContentHash PolicyDigest{get;}
        public int MaximumLevel{get;}
        public double Radius{get;}
        public double MinimumHeight{get;}
        public double MaximumHeight{get;}
        public long SupportSamples{get;}
        public long EstimatedResidentBytes{get;}
        public long EstimatedNativeBytes=>EstimatedResidentBytes-256;
        /// <summary>Incremental build peak: source Native clone, all derived copies and bounded scratch. Existing authority is retained/accounted by the caller.</summary>
        public long EstimatedWorkingBytes{get;}
        public IReadOnlyList<SurfaceLandformControl> DerivedControls{get;}
        public IReadOnlyList<SurfaceLandformFilterLevel> Levels{get;}
        public int IndexCount=>index.Length;
        public int ReferenceCount=>references.Length;
        public SurfaceDrainageIndexNode IndexAt(int i)=>index[i];
        public int ReferenceAt(int i)=>references[i];
        SurfaceLandformFilterHierarchy(SurfaceLandformField source,SurfaceLandformFilterSettings settings,int maximumLevel,
            SurfaceLandformControl[] controls,SurfaceLandformFilterLevel[] levels,SurfaceDrainageIndexNode[] index,int[] references,long samples,long working)
        {
            SourceDigest=source.ContentDigest;PolicyDigest=settings.PolicyDigest;MaximumLevel=maximumLevel;Radius=source.SourceRecipe.Radius;
            MinimumHeight=source.MinimumHeight;MaximumHeight=source.MaximumHeight;this.controls=controls;this.levels=levels;this.index=index;this.references=references;
            DerivedControls=Array.AsReadOnly(controls);Levels=Array.AsReadOnly(levels);SupportSamples=samples;EstimatedWorkingBytes=working;
            EstimatedResidentBytes=256L+controls.LongLength*72+levels.LongLength*32+index.LongLength*32+references.LongLength*4;
        }
        struct Integral { public double Sum,Area;public Integral(double sum,double area){Sum=sum;Area=area;}public double Height=>Sum/Area; }

        /// <summary>Metadata-only admission before any quadrature/native/index allocation. All-level index capacity is reserved once.</summary>
        public static bool TryEstimate(SurfaceLandformField source,SurfaceLandformFilterSettings settings,out SurfaceLandformFilterEstimate estimate,out string error)
        {
            estimate=default;error=null;if(source==null||!settings.IsValid){error="A captured field and explicit valid filter policy are required.";return false;}
            int leaves=source.Cells.Count,level=0,parents=(leaves-6)/3;foreach(var cell in source.Cells)level=math.max(level,cell.Key.Level);
            if(level==0)
            {estimate=new SurfaceLandformFilterEstimate(0,0,0,0,256,0,0);if(estimate.PeakWorkingBytes>settings.MaximumWorkingBytes||256>settings.MaximumResidentBytes)error="Derived root alias exceeds metadata admission.";return error==null;}
            long samples=checked((long)leaves*settings.QuadratureResolution*settings.QuadratureResolution);
            long nativeSource=NativeSurfaceLandformData.HeightOnlyBytes(source);
            long managed=checked(256+parents*72L+level*32L+MaximumIndexNodes*32L+MaximumReferences*4L),native=managed-256;
            long scratch=checked(managed+(leaves+parents)*256L+leaves*32L+16L*1024*1024);
            estimate=new SurfaceLandformFilterEstimate(level,parents,samples,nativeSource,managed,native,scratch);
            if(samples>settings.MaximumSupportSamples||managed>settings.MaximumResidentBytes||estimate.PeakWorkingBytes>settings.MaximumWorkingBytes)
                error=$"Derived landform preflight rejects samples={samples}, residentReserve={managed}, working={estimate.PeakWorkingBytes}; no lower-quality substitute is built.";
            return error==null;
        }

        public static SurfaceLandformFilterHierarchy Build(SurfaceLandformField source,SurfaceLandformFilterSettings settings,Func<bool>cancelled=null)
        {
            SurfaceBaker.CheckCancelled(cancelled);if(!TryEstimate(source,settings,out var estimate,out string error))throw new ArgumentException(error);
            if(estimate.MaximumLevel==0)return new SurfaceLandformFilterHierarchy(source,settings,0,Array.Empty<SurfaceLandformControl>(),Array.Empty<SurfaceLandformFilterLevel>(),Array.Empty<SurfaceDrainageIndexNode>(),Array.Empty<int>(),0,estimate.PeakWorkingBytes);
            int leaves=source.Cells.Count,maximumLevel=0;
            // A complete four-child forest has exactly (leaves-six)/three internal nodes.
            int maximumParents=estimate.DerivedControlCapacity;long samples=estimate.SupportSamples,working=estimate.PeakWorkingBytes;
            var leafByKey=new Dictionary<SurfaceTileKey,int>();var allKeys=new HashSet<SurfaceTileKey>();var channelKeys=new SurfaceTileKey[source.Channels.Count];
            for(int i=0;i<leaves;i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i);var cell=source.Cells[i];maximumLevel=math.max(maximumLevel,cell.Key.Level);leafByKey.Add(cell.Key,i);channelKeys[cell.Channel]=cell.Key;
                for(int level=cell.Key.Level;level>=0;level--)allKeys.Add(Ancestor(cell.Key,level));
            }
            if(allKeys.Count-leaves!=maximumParents)throw new ArgumentException("Derived source is not a complete dyadic forest.");
            var ordered=new List<SurfaceTileKey>(allKeys);ordered.Sort();var integrals=new Dictionary<SurfaceTileKey,Integral>();
            using(var native=NativeSurfaceLandformData.CreateHeightOnly(source,Allocator.Persistent))
            using(var cells=new NativeArray<SurfaceTileKey>(leaves,Allocator.Persistent))
            using(var results=new NativeArray<double2>(leaves,Allocator.Persistent))
            using(var statuses=new NativeArray<SurfaceSampleStatus>(leaves,Allocator.Persistent))
            {
                var writableCells=cells;for(int i=0;i<leaves;i++)writableCells[i]=source.Cells[i].Key;
                // Cancellation is observed between bounded jobs, never millions of synchronous
                // queries on a render thread. Build is an explicit caller-owned worker operation.
                for(int first=0;first<leaves;first+=1024)
                {
                    SurfaceBaker.CheckCancelled(cancelled);int count=math.min(1024,leaves-first);
                    new QuadratureJob{Full=native.View,Cells=cells,Results=results.GetSubArray(first,count),Statuses=statuses.GetSubArray(first,count),First=first,Resolution=settings.QuadratureResolution}.Schedule(count,32).Complete();
                }
                for(int i=0;i<leaves;i++)
                {SurfaceBaker.CheckCancelled(cancelled,i);if(statuses[i]!=SurfaceSampleStatus.Ready)throw new ArgumentException("Derived quadrature cannot borrow missing or invalid authority.");integrals.Add(source.Cells[i].Key,new Integral(results[i].x,results[i].y));}
            }
            for(int level=maximumLevel-1;level>=0;level--)foreach(var key in ordered)
            {
                if(key.Level!=level||leafByKey.ContainsKey(key))continue;SurfaceBaker.CheckCancelled(cancelled);double sum=0,area=0;
                for(int c=0;c<4;c++){var child=new SurfaceTileKey(key.Face,level+1,key.X*2+(c&1),key.Y*2+(c>>1));if(!integrals.TryGetValue(child,out var value))throw new ArgumentException("Incomplete dyadic mean support.");sum+=value.Sum;area+=value.Area;}
                integrals.Add(key,new Integral(sum,area));
            }
            var parentIds=new Dictionary<SurfaceTileKey,int>();var derived=new SurfaceLandformControl[maximumParents];
            foreach(var key in ordered)if(!leafByKey.ContainsKey(key))parentIds.Add(key,parentIds.Count);
            var tree=new List<SurfaceDrainageIndexNode>();var refs=new List<int>();var headers=new SurfaceLandformFilterLevel[maximumLevel];
            for(int level=0;level<maximumLevel;level++)
            {
                SurfaceBaker.CheckCancelled(cancelled);var cover=new HashSet<SurfaceTileKey>();foreach(var cell in source.Cells)cover.Add(Ancestor(cell.Key,math.min(level,cell.Key.Level)));
                var keys=new List<SurfaceTileKey>(cover);keys.Sort();var selected=new List<int>();
                foreach(var key in keys)
                {
                    if(leafByKey.TryGetValue(key,out int leaf))selected.Add(-(source.Channels[source.Cells[leaf].Channel].Control+1));
                    else
                    {
                        int own=parentIds[key];CubeSurface.TryDirection(key,new double2(.5),out var d);
                        // Guard accumulation roundoff at constant extreme means. Control intervals,
                        // not a final height clamp, keep every PU level inside the Full envelope.
                        double h=math.clamp(integrals[key].Height,source.MinimumHeight,source.MaximumHeight);
                        var g=Slope(source,key,d,h,cover,leafByKey,integrals);double variation=BoundedVariation(h,source.MinimumHeight,source.MaximumHeight);
                        derived[own]=new SurfaceLandformControl(d,h,variation>0?g:new double3(0),CellRadius(source.SourceRecipe.Radius,key,d)*1.6,variation);selected.Add(own);
                    }
                }
                for(int i=0;i<source.Divides.Count;i++)
                {
                    var divide=source.Divides[i];if(channelKeys[divide.FirstChannel].Level<=level&&channelKeys[divide.SecondChannel].Level<=level)selected.Add(-(divide.Control+1));
                }
                int firstNode=tree.Count,firstReference=refs.Count;
                var boxes=new SurfaceDrainageSegment[selected.Count];
                for(int i=0;i<boxes.Length;i++){var c=Control(source,derived,selected[i]);double margin=c.SupportMetres/source.SourceRecipe.Radius+1e-12;boxes[i]=new SurfaceDrainageSegment(c.Direction-margin,c.Direction+margin,0,0);}
                var localIds=new int[selected.Count];for(int i=0;i<localIds.Length;i++)localIds[i]=i;
                int root=BuildIndex(boxes,selected,new double3(-1),new double3(1),localIds,0,tree,refs,cancelled);
                headers[level]=new SurfaceLandformFilterLevel(level,root,firstNode,tree.Count-firstNode,firstReference,refs.Count-firstReference,2*source.SourceRecipe.Radius/(1<<level));
            }
            SurfaceBaker.CheckCancelled(cancelled);
            var result=new SurfaceLandformFilterHierarchy(source,settings,maximumLevel,derived,headers,tree.ToArray(),refs.ToArray(),samples,working);
            if(result.EstimatedResidentBytes>settings.MaximumResidentBytes)throw new ArgumentException("Derived support exceeded admitted residency.");return result;
        }
        static SurfaceTileKey Ancestor(SurfaceTileKey key,int level)=>new SurfaceTileKey(key.Face,level,key.X>>(key.Level-level),key.Y>>(key.Level-level));
        static double BoundedVariation(double height,double minimum,double maximum)
        {
            double variation=math.max(0,math.min(height-minimum,maximum-height));
            // Subtraction/addition may round a nominal boundary interval one ULP outside
            // its source envelope. Shrink the positive radius, never clamp the sampled height.
            while(variation>0&&(height-variation<minimum||height+variation>maximum))
                variation=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(variation)-1);
            return variation;
        }
        static SurfaceLandformControl Control(SurfaceLandformField source,SurfaceLandformControl[] derived,int encoded)=>encoded<0?source.Controls[-(encoded+1)]:derived[encoded];
        static double3 Slope(SurfaceLandformField source,SurfaceTileKey key,double3 d,double height,HashSet<SurfaceTileKey> cover,
            Dictionary<SurfaceTileKey,int> leaves,Dictionary<SurfaceTileKey,Integral> integrals)
        {
            var axis=math.abs(d.y)<.9?new double3(0,1,0):new double3(1,0,0);var right=math.normalize(math.cross(axis,d));var forward=math.cross(d,right);
            double xx=0,xy=0,yy=0,xh=0,yh=0;var seen=new FixedList512Bytes<SurfaceTileKey>();
            for(int side=0;side<4;side++)for(int sample=0;sample<2;sample++)
            {
                var other=Neighbor(cover,key,side,sample==0?.25:.75);bool duplicate=false;for(int j=0;j<seen.Length;j++)if(seen[j].Equals(other)){duplicate=true;break;}if(duplicate)continue;seen.Add(other);CubeSurface.TryDirection(other,new double2(.5),out var od);
                double oh=leaves.TryGetValue(other,out int leaf)?source.Controls[source.Channels[source.Cells[leaf].Channel].Control].Height:integrals[other].Height;
                var delta=(od-d)*source.SourceRecipe.Radius;double x=math.dot(delta,right),y=math.dot(delta,forward),h=oh-height,w=1/math.max(1,x*x+y*y);
                xx+=w*x*x;xy+=w*x*y;yy+=w*y*y;xh+=w*x*h;yh+=w*y*h;
            }
            double det=xx*yy-xy*xy;var g=det>1e-12?right*((xh*yy-yh*xy)/det)+forward*((yh*xx-xh*xy)/det):new double3(0);
            double length=math.length(g);return length>1?g/length:g;
        }
        static SurfaceTileKey Neighbor(HashSet<SurfaceTileKey> cover,SurfaceTileKey key,int edge,double t)
        {
            const double outside=1e-7;double n=1<<key.Level,u=edge==0?-outside:edge==1?1+outside:t,v=edge==2?-outside:edge==3?1+outside:t;
            double a=2*(key.X+u)/n-1,b=2*(key.Y+v)/n-1;double3 d;
            switch(key.Face){case 0:d=new double3(1,b,-a);break;case 1:d=new double3(-1,b,a);break;case 2:d=new double3(a,1,-b);break;case 3:d=new double3(a,-1,b);break;case 4:d=new double3(a,b,1);break;default:d=new double3(-a,b,-1);break;}
            CubeSurface.TryLocate(math.normalize(d),12,out var found,out _);for(int level=found.Level;level>=0;level--){var candidate=Ancestor(found,level);if(cover.Contains(candidate))return candidate;}
            throw new ArgumentException("Derived mixed cover has a missing neighbor.");
        }
        static double CellRadius(double radius,SurfaceTileKey key,double3 d)
        {double q=0;for(int c=0;c<4;c++){CubeSurface.TryDirection(key,new double2(c&1,c>>1),out var corner);q=math.max(q,2*Math.Asin(math.min(1,math.length(corner-d)*.5))*radius);}return q;}
        static int BuildIndex(SurfaceDrainageSegment[] boxes,List<int>encoded,double3 lo,double3 hi,int[] candidates,int depth,
            List<SurfaceDrainageIndexNode> tree,List<int> refs,Func<bool>cancelled)
        {
            SurfaceBaker.CheckCancelled(cancelled);if(tree.Count>=MaximumIndexNodes)throw new ArgumentException("Derived all-level index node cap exceeded.");int own=tree.Count;tree.Add(default);
            if(candidates.Length<=SurfaceLandformField.MaximumLeafCandidates)
            {if((long)refs.Count+candidates.Length>MaximumReferences)throw new ArgumentException("Derived all-level reference cap exceeded.");int first=refs.Count;foreach(int i in candidates)refs.Add(encoded[i]);tree[own]=new SurfaceDrainageIndexNode(0,-1,-1,-1,first,candidates.Length);return own;}
            if(depth>=SurfaceLandformField.MaximumIndexDepth)throw new ArgumentException("Derived controls exceed the bounded per-query overlap cap.");
            var size=hi-lo;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;double split=(lo[axis]+hi[axis])*.5;
            var left=new List<int>();var right=new List<int>();foreach(int i in candidates){if(boxes[i].Minimum[axis]<=split)left.Add(i);if(boxes[i].Maximum[axis]>=split)right.Add(i);}
            var lhi=hi;lhi[axis]=split;var rlo=lo;rlo[axis]=split;int l=BuildIndex(boxes,encoded,lo,lhi,left.ToArray(),depth+1,tree,refs,cancelled),r=BuildIndex(boxes,encoded,rlo,hi,right.ToArray(),depth+1,tree,refs,cancelled);
            tree[own]=new SurfaceDrainageIndexNode(split,axis,l,r,0,0);return own;
        }
        [BurstCompile]
        struct QuadratureJob:IJobParallelFor
        {
            [ReadOnly]public NativeSurfaceLandformView Full;
            [ReadOnly]public NativeArray<SurfaceTileKey> Cells;
            public NativeArray<double2> Results;
            public NativeArray<SurfaceSampleStatus> Statuses;
            public int First,Resolution;
            public void Execute(int i)
            {
                int id=First+i;var key=Cells[id];double sum=0,area=0;
                for(int y=0;y<Resolution;y++)for(int x=0;x<Resolution;x++)
                {
                    CubeSurface.TryDirection(key,new double2(x/(double)Resolution,y/(double)Resolution),out var a);
                    CubeSurface.TryDirection(key,new double2((x+1.0)/Resolution,y/(double)Resolution),out var b);
                    CubeSurface.TryDirection(key,new double2(x/(double)Resolution,(y+1.0)/Resolution),out var c);
                    CubeSurface.TryDirection(key,new double2((x+1.0)/Resolution,(y+1.0)/Resolution),out var e);
                    CubeSurface.TryDirection(key,new double2((x+.5)/Resolution,(y+.5)/Resolution),out var d);
                    var status=SurfaceLandformMath.TrySample(Full,d,out double h);if(status!=SurfaceSampleStatus.Ready){Statuses[i]=status;return;}
                    double weight=SolidAngle(a,b,c)+SolidAngle(b,e,c);sum+=weight*h;area+=weight;
                }
                Results[i]=new double2(sum,area);Statuses[i]=area>0&&math.isfinite(sum)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
            }
            static double SolidAngle(double3 a,double3 b,double3 c)=>2*math.atan2(math.abs(math.dot(a,math.cross(b,c))),1+math.dot(a,b)+math.dot(b,c)+math.dot(c,a));
        }
    }
}
