using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    // Version four deliberately keeps coast identity separate from tectonic province identity.
    public readonly struct SurfaceCoastLandmass
    {
        public readonly double3 Center, Right, Forward;
        public readonly int FirstVertex, VertexCount;
        public SurfaceCoastLandmass(double3 center, double3 right, double3 forward, int first, int count)
        { Center = center; Right = right; Forward = forward; FirstVertex = first; VertexCount = count; }
    }
    public readonly struct SurfaceDrainageNode
    {
        public readonly double3 Direction;
        public readonly double BedHeight, DrainageArea, HillslopeWidth, DivideHeight;
        public readonly int Parent, Outlet, StrahlerOrder;
        public SurfaceDrainageNode(double3 direction, double bed, double area, double width, double divide, int parent, int outlet, int order)
        { Direction = direction; BedHeight = bed; DrainageArea = area; HillslopeWidth = width; DivideHeight = divide; Parent = parent; Outlet = outlet; StrahlerOrder = order; }
    }
    public readonly struct SurfaceDrainageSegment
    {
        public readonly double3 Minimum, Maximum;
        public readonly int Child, Parent;
        public SurfaceDrainageSegment(double3 minimum, double3 maximum, int child, int parent)
        { Minimum = minimum; Maximum = maximum; Child = child; Parent = parent; }
    }
    public readonly struct SurfaceCoastSegment
    {
        public readonly double3 Minimum, Maximum;
        public readonly int First, Last, Landmass;
        public SurfaceCoastSegment(double3 minimum,double3 maximum,int first,int last,int landmass)
        {Minimum=minimum;Maximum=maximum;First=first;Last=last;Landmass=landmass;}
    }
    /// <summary>Spatial partition of closed unit-direction cells. Overlapping supports occur in both leaves.</summary>
    public readonly struct SurfaceDrainageIndexNode
    {
        public readonly double Split;
        public readonly int Axis, Left, Right, First, Count;
        public SurfaceDrainageIndexNode(double split, int axis, int left, int right, int first, int count)
        { Split = split; Axis = axis; Left = left; Right = right; First = first; Count = count; }
        public bool IsLeaf => Axis < 0;
    }

    /// <summary>
    /// Captured coasts and rooted stream-power drainage, not seed-only runtime reconstruction.
    /// Geometry records are authority; the deterministic spatial partition is derived and rebuilt on decode.
    /// </summary>
    public sealed class SurfaceDrainageField
    {
        public const int CurrentVersion = 1;
        public const int MaximumLandmasses = 16, MaximumCoastVertices = 2048, MaximumNodes = 131072;
        public const int MaximumLeafCandidates = 96, MaximumIndexDepth = 30;
        public const int MaximumIndexNodes = 262144, MaximumReferences = 4194304;
        public const long MaximumResidentBytes = 96L * 1024 * 1024;
        readonly SurfaceCoastLandmass[] landmasses;
        readonly double2[] coastVertices;
        readonly double3[] coastDirections;
        readonly SurfaceDrainageNode[] nodes;
        readonly SurfaceDrainageSegment[] segments;
        readonly SurfaceDrainageIndexNode[] index;
        readonly int[] references;
        readonly SurfaceCoastSegment[] coastSegments;
        readonly SurfaceDrainageIndexNode[] coastIndex;
        readonly int[] coastReferences;
        public IReadOnlyList<SurfaceCoastLandmass> Landmasses { get; }
        public IReadOnlyList<double2> CoastVertices { get; }
        public IReadOnlyList<SurfaceDrainageNode> Nodes { get; }
        public IReadOnlyList<SurfaceDrainageSegment> Segments { get; }
        public int IndexNodeCount => index.Length;
        public int SpatialReferenceCount => references.Length;
        public int CoastIndexNodeCount => coastIndex.Length;
        public int CoastSpatialReferenceCount => coastReferences.Length;
        public IReadOnlyList<SurfaceCoastSegment> CoastSegments { get; }
        public double CoastInfluenceMetres { get; }
        public SurfaceRecipe SourceRecipe { get; }
        public SurfaceContentHash ContentDigest { get; }
        public double MaximumIncision { get; }
        public long EstimatedResidentBytes { get; }
        public int MaximumActualDepth { get; }

        public SurfaceDrainageField(SurfaceRecipe recipe, SurfaceCoastLandmass[] landmasses, double2[] coastVertices,
            SurfaceDrainageNode[] nodes, Func<bool> cancelled = null,double coastInfluenceMetres=0)
        {
            if (!recipe.IsValid || recipe.AlgorithmVersion != 4 || landmasses == null || coastVertices == null || nodes == null ||
                landmasses.Length < 1 || landmasses.Length > MaximumLandmasses || coastVertices.Length > MaximumCoastVertices || nodes.Length > MaximumNodes)
                throw new ArgumentException("Invalid version-four captured morphology counts before allocation.");
            SourceRecipe = recipe;
            CoastInfluenceMetres=coastInfluenceMetres>0?coastInfluenceMetres:recipe.Radius*.16;
            if(!Positive(CoastInfluenceMetres)||CoastInfluenceMetres>recipe.Radius*.8)throw new ArgumentException("Invalid saturated coast influence.");
            this.landmasses = (SurfaceCoastLandmass[])landmasses.Clone(); this.coastVertices = (double2[])coastVertices.Clone();
            coastDirections = new double3[coastVertices.Length];
            this.nodes = (SurfaceDrainageNode[])nodes.Clone();
            foreach (var mass in this.landmasses)
            {
                if (!Unit(mass.Center) || !Unit(mass.Right) || !Unit(mass.Forward) || math.abs(math.dot(mass.Center,mass.Right)) > 1e-10 ||
                    math.abs(math.dot(mass.Center,mass.Forward)) > 1e-10 || math.abs(math.dot(mass.Right,mass.Forward)) > 1e-10 ||
                    mass.VertexCount < 8 || mass.FirstVertex < 0 || (long)mass.FirstVertex + mass.VertexCount > this.coastVertices.Length)
                    throw new ArgumentException("Coast loops need bounded orthonormal hemisphere frames.");
                double area = 0, winding = 0;
                for (int j = 0; j < mass.VertexCount; j++)
                {
                    var a = this.coastVertices[mass.FirstVertex+j]; var b = this.coastVertices[mass.FirstVertex+(j+1)%mass.VertexCount];
                    if (!math.all(math.isfinite(a)) || math.length(a) > recipe.Radius * 3 || math.length(a) < recipe.Radius * .005)
                        throw new ArgumentException("Coast coordinates must be finite metric data within the local hemisphere.");
                    double cross=a.x*b.y-a.y*b.x;
                    if(cross<=0)throw new ArgumentException("Coast controls must advance monotonically around their star-shaped centre.");
                    area += cross;
                    winding += Math.Atan2(cross, math.dot(a,b));
                    coastDirections[mass.FirstVertex+j] = math.normalize(mass.Center + mass.Right*(a.x/recipe.Radius) + mass.Forward*(a.y/recipe.Radius));
                }
                if (area <= 0 || Math.Abs(winding-2*Math.PI)>1e-10)
                    throw new ArgumentException("Captured coast vertices must make exactly one positive star-shaped circuit.");
            }
            double maximumIncision = 0;
            var list = new List<SurfaceDrainageSegment>(nodes.Length);
            var childArea=new double[nodes.Length];double rootArea=0;
            for (int i = 0; i < this.nodes.Length; i++)
            {
                SurfaceBaker.CheckCancelled(cancelled,i); var node = this.nodes[i];
                if (!Unit(node.Direction) || !math.isfinite(node.BedHeight) || node.BedHeight < recipe.MinimumHeight || node.BedHeight > recipe.MaximumHeight ||
                    !Positive(node.DrainageArea) || !Positive(node.HillslopeWidth) || node.HillslopeWidth > recipe.Radius * .1 ||
                    !math.isfinite(node.DivideHeight) || node.DivideHeight < node.BedHeight || node.DivideHeight > recipe.MaximumHeight ||
                    node.Parent < -1 || node.Parent >= i || node.Outlet < 0 || node.Outlet > i || node.StrahlerOrder < 1 || node.StrahlerOrder > 32)
                    throw new ArgumentException("Drainage authority requires parent-first acyclic finite node records.");
                if (node.Parent < 0)
                { if (node.Outlet != i) throw new ArgumentException("An outlet must identify itself.");rootArea+=node.DrainageArea;continue; }
                var parent = this.nodes[node.Parent];
                if (parent.Outlet != node.Outlet || node.BedHeight <= parent.BedHeight || node.DrainageArea > parent.DrainageArea*(1+1e-10))
                    throw new ArgumentException("Captured drainage must carry strictly downhill beds and nondecreasing downstream area.");
                childArea[node.Parent]+=node.DrainageArea;
                double3 center = math.normalizesafe(node.Direction+parent.Direction);
                double chord = math.length(node.Direction-parent.Direction);
                if (chord < 1e-12 || chord > .25) throw new ArgumentException("Drainage segments need a finite short spherical chord.");
                // A cap centred on the arc midpoint encloses the full arc plus all lateral hillslope support.
                double support = math.min(Math.PI, 2*Math.Asin(math.min(1,chord*.5))*.5 +
                    2*math.max(node.HillslopeWidth,parent.HillslopeWidth)/recipe.Radius);
                double margin = 2*Math.Sin(support*.5) + 1e-12;
                list.Add(new SurfaceDrainageSegment(center-margin,center+margin,i,node.Parent));
                maximumIncision = math.max(maximumIncision,recipe.MaximumHeight-parent.BedHeight);
            }
            if(rootArea>4*Math.PI*recipe.Radius*recipe.Radius*(1+1e-10))throw new ArgumentException("Captured outlets cannot contain more reference-sphere area than the planet.");
            for(int i=0;i<childArea.Length;i++)if(childArea[i]>this.nodes[i].DrainageArea*(1+1e-10))
                throw new ArgumentException("Captured children must partition, rather than duplicate, their parent catchment area.");
            MaximumIncision = maximumIncision; segments = list.ToArray();
            BuildIndex(segments,cancelled,out index,out references,out int depth);
            var coastList=new List<SurfaceCoastSegment>();
            for(int m=0;m<this.landmasses.Length;m++)
            {
                var mass=this.landmasses[m];
                for(int j=0;j<mass.VertexCount;j++)
                {
                    int a=mass.FirstVertex+j,b=mass.FirstVertex+(j+1)%mass.VertexCount;
                    var first=coastDirections[a];var last=coastDirections[b];var center=math.normalize(first+last);
                    double support=math.min(Math.PI,Math.Asin(math.min(1,math.length(first-last)*.5))+CoastInfluenceMetres/recipe.Radius);
                    double margin=2*Math.Sin(support*.5)+1e-12;
                    coastList.Add(new SurfaceCoastSegment(center-margin,center+margin,a,b,m));
                }
            }
            coastSegments=coastList.ToArray();var coastBoxes=new SurfaceDrainageSegment[coastSegments.Length];
            for(int i=0;i<coastBoxes.Length;i++)coastBoxes[i]=new SurfaceDrainageSegment(coastSegments[i].Minimum,coastSegments[i].Maximum,0,0);
            BuildIndex(coastBoxes,cancelled,out coastIndex,out coastReferences,out int coastDepth);
            MaximumActualDepth=math.max(depth,coastDepth);
            EstimatedResidentBytes = checked(512L + this.landmasses.LongLength*88 + this.coastVertices.LongLength*40 +
                this.nodes.LongLength*80 + segments.LongLength*64 + index.LongLength*32 + references.LongLength*4 +
                coastSegments.LongLength*72+coastIndex.LongLength*32+coastReferences.LongLength*4);
            if (EstimatedResidentBytes > MaximumResidentBytes) throw new ArgumentException("Captured morphology exceeds its explicit resident byte budget.");
            Landmasses = Array.AsReadOnly(this.landmasses); CoastVertices = Array.AsReadOnly(this.coastVertices);
            Nodes = Array.AsReadOnly(this.nodes); Segments = Array.AsReadOnly(segments);
            CoastSegments=Array.AsReadOnly(coastSegments);
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(CurrentVersion); SurfaceHashing.WriteRecipe(writer,recipe);writer.Write(CoastInfluenceMetres);
                writer.Write(this.landmasses.Length); foreach(var mass in this.landmasses)
                {SurfaceHashing.WriteVector(writer,mass.Center);SurfaceHashing.WriteVector(writer,mass.Right);SurfaceHashing.WriteVector(writer,mass.Forward);writer.Write(mass.FirstVertex);writer.Write(mass.VertexCount);}
                writer.Write(this.coastVertices.Length); foreach(var p in this.coastVertices){writer.Write(p.x);writer.Write(p.y);}
                writer.Write(this.nodes.Length); foreach(var node in this.nodes)
                {SurfaceHashing.WriteVector(writer,node.Direction);writer.Write(node.BedHeight);writer.Write(node.DrainageArea);writer.Write(node.HillslopeWidth);writer.Write(node.DivideHeight);writer.Write(node.Parent);writer.Write(node.Outlet);writer.Write(node.StrahlerOrder);}
            });
        }
        public SurfaceDrainageIndexNode IndexNodeAt(int i) => index[i];
        public double3 CoastDirectionAt(int i) => coastDirections[i];
        public int SpatialReferenceAt(int i) => references[i];
        public SurfaceDrainageIndexNode CoastIndexNodeAt(int i)=>coastIndex[i];
        public int CoastSpatialReferenceAt(int i)=>coastReferences[i];
        public bool TryMountainRegion(out double3 direction)
        {
            direction=default;double best=0;
            foreach(var node in nodes)
            {
                if(node.Parent<0||node.StrahlerOrder<2||node.DivideHeight<=SourceRecipe.SeaLevel+(SourceRecipe.MaximumHeight-SourceRecipe.SeaLevel)*.2)continue;
                double score=(node.DivideHeight-node.BedHeight)*Math.Log(1+node.StrahlerOrder);
                if(score>best){best=score;direction=node.Direction;}
            }
            return best>0;
        }
        void BuildIndex(SurfaceDrainageSegment[] boxes,Func<bool> cancelled,out SurfaceDrainageIndexNode[] result,out int[] refs,out int depth)
        {
            var tree = new List<SurfaceDrainageIndexNode>(); var output = new List<int>();
            var initial = new int[boxes.Length]; for(int i=0;i<initial.Length;i++)initial[i]=i;
            int deepest = 0;
            BuildCell(boxes,new double3(-1),new double3(1),initial,0,tree,output,cancelled,ref deepest);
            result=tree.ToArray();refs=output.ToArray();depth=deepest;
        }
        int BuildCell(SurfaceDrainageSegment[] boxes,double3 lo,double3 hi,int[] candidates,int depth,List<SurfaceDrainageIndexNode> tree,List<int> refs,
            Func<bool> cancelled,ref int deepest)
        {
            SurfaceBaker.CheckCancelled(cancelled);deepest=math.max(deepest,depth);
            if(tree.Count>=MaximumIndexNodes)throw new ArgumentException("Morphology spatial nodes exceed the declared admission budget.");
            int own=tree.Count;tree.Add(default);
            if(candidates.Length<=MaximumLeafCandidates)
            {
                if((long)refs.Count+candidates.Length>MaximumReferences)throw new ArgumentException("Morphology spatial references exceed the declared admission budget.");
                int first=refs.Count;refs.AddRange(candidates);tree[own]=new SurfaceDrainageIndexNode(0,-1,-1,-1,first,candidates.Length);return own;
            }
            if(depth>=MaximumIndexDepth)throw new ArgumentException("Overlapping morphology cannot satisfy the explicit per-query work budget.");
            var size=hi-lo;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;double split=(lo[axis]+hi[axis])*.5;
            var left=new List<int>();var right=new List<int>();
            foreach(int i in candidates)
            { SurfaceBaker.CheckCancelled(cancelled,i);var b=boxes[i];if(b.Minimum[axis]<=split)left.Add(i);if(b.Maximum[axis]>=split)right.Add(i); }
            var lhi=hi; lhi[axis]=split; var rlo=lo;rlo[axis]=split;
            int l=BuildCell(boxes,lo,lhi,left.ToArray(),depth+1,tree,refs,cancelled,ref deepest);
            int r=BuildCell(boxes,rlo,hi,right.ToArray(),depth+1,tree,refs,cancelled,ref deepest);
            tree[own]=new SurfaceDrainageIndexNode(split,axis,l,r,0,0);return own;
        }
        static bool Unit(double3 v)=>math.all(math.isfinite(v))&&math.abs(math.lengthsq(v)-1)<1e-10;
        static bool Positive(double v)=>math.isfinite(v)&&v>0;
    }

    public readonly struct NativeSurfaceDrainageView
    {
        public readonly double Radius,CoastInfluenceMetres;
        public readonly SurfaceContentHash ContentDigest;
        public readonly NativeArray<SurfaceCoastLandmass>.ReadOnly Landmasses;
        public readonly NativeArray<double2>.ReadOnly CoastVertices;
        public readonly NativeArray<double3>.ReadOnly CoastDirections;
        public readonly NativeArray<SurfaceDrainageNode>.ReadOnly Nodes;
        public readonly NativeArray<SurfaceDrainageSegment>.ReadOnly Segments;
        public readonly NativeArray<SurfaceDrainageIndexNode>.ReadOnly Index;
        public readonly NativeArray<int>.ReadOnly References;
        public readonly NativeArray<SurfaceCoastSegment>.ReadOnly CoastSegments;
        public readonly NativeArray<SurfaceDrainageIndexNode>.ReadOnly CoastIndex;
        public readonly NativeArray<int>.ReadOnly CoastReferences;
        public bool Enabled=>ContentDigest.IsValid;
        internal NativeSurfaceDrainageView(SurfaceDrainageField field,NativeArray<SurfaceCoastLandmass> landmasses,
            NativeArray<double2> coasts,NativeArray<double3> coastDirections,NativeArray<SurfaceDrainageNode> nodes,NativeArray<SurfaceDrainageSegment> segments,
            NativeArray<SurfaceDrainageIndexNode> index,NativeArray<int> refs,NativeArray<SurfaceCoastSegment> coastSegments,
            NativeArray<SurfaceDrainageIndexNode> coastIndex,NativeArray<int> coastRefs)
        {Radius=field?.SourceRecipe.Radius??0;CoastInfluenceMetres=field?.CoastInfluenceMetres??0;ContentDigest=field?.ContentDigest??default;Landmasses=landmasses.AsReadOnly();CoastVertices=coasts.AsReadOnly();CoastDirections=coastDirections.AsReadOnly();Nodes=nodes.AsReadOnly();Segments=segments.AsReadOnly();Index=index.AsReadOnly();References=refs.AsReadOnly();CoastSegments=coastSegments.AsReadOnly();CoastIndex=coastIndex.AsReadOnly();CoastReferences=coastRefs.AsReadOnly();}
    }
    public sealed class NativeSurfaceDrainageData:IDisposable
    {
        NativeArray<SurfaceCoastLandmass> landmasses;NativeArray<double2> coasts;NativeArray<double3> coastDirections;NativeArray<SurfaceDrainageNode> nodes;
        NativeArray<SurfaceDrainageSegment> segments;NativeArray<SurfaceDrainageIndexNode> index;NativeArray<int> refs;
        NativeArray<SurfaceCoastSegment> coastSegments;NativeArray<SurfaceDrainageIndexNode> coastIndex;NativeArray<int> coastRefs;
        readonly SurfaceDrainageField field;bool disposed;
        public NativeSurfaceDrainageView View=>!disposed?new NativeSurfaceDrainageView(field,landmasses,coasts,coastDirections,nodes,segments,index,refs,coastSegments,coastIndex,coastRefs):throw new ObjectDisposedException(nameof(NativeSurfaceDrainageData));
        public NativeSurfaceDrainageData(SurfaceDrainageField field,Allocator allocator)
        {
            this.field=field;
            try
            {
                landmasses=new NativeArray<SurfaceCoastLandmass>(field?.Landmasses.Count??0,allocator);coasts=new NativeArray<double2>(field?.CoastVertices.Count??0,allocator);
                coastDirections=new NativeArray<double3>(field?.CoastVertices.Count??0,allocator);
                nodes=new NativeArray<SurfaceDrainageNode>(field?.Nodes.Count??0,allocator);segments=new NativeArray<SurfaceDrainageSegment>(field?.Segments.Count??0,allocator);
                index=new NativeArray<SurfaceDrainageIndexNode>(field?.IndexNodeCount??0,allocator);refs=new NativeArray<int>(field?.SpatialReferenceCount??0,allocator);
                coastSegments=new NativeArray<SurfaceCoastSegment>(field?.CoastSegments.Count??0,allocator);coastIndex=new NativeArray<SurfaceDrainageIndexNode>(field?.CoastIndexNodeCount??0,allocator);coastRefs=new NativeArray<int>(field?.CoastSpatialReferenceCount??0,allocator);
                if(field==null)return;
                for(int i=0;i<landmasses.Length;i++)landmasses[i]=field.Landmasses[i];for(int i=0;i<coasts.Length;i++){coasts[i]=field.CoastVertices[i];coastDirections[i]=field.CoastDirectionAt(i);}
                for(int i=0;i<nodes.Length;i++)nodes[i]=field.Nodes[i];for(int i=0;i<segments.Length;i++)segments[i]=field.Segments[i];
                for(int i=0;i<index.Length;i++)index[i]=field.IndexNodeAt(i);for(int i=0;i<refs.Length;i++)refs[i]=field.SpatialReferenceAt(i);
                for(int i=0;i<coastSegments.Length;i++)coastSegments[i]=field.CoastSegments[i];for(int i=0;i<coastIndex.Length;i++)coastIndex[i]=field.CoastIndexNodeAt(i);for(int i=0;i<coastRefs.Length;i++)coastRefs[i]=field.CoastSpatialReferenceAt(i);
            }
            catch{Dispose();throw;}
        }
        public void Dispose(){if(disposed)return;if(landmasses.IsCreated)landmasses.Dispose();if(coasts.IsCreated)coasts.Dispose();if(coastDirections.IsCreated)coastDirections.Dispose();if(nodes.IsCreated)nodes.Dispose();if(segments.IsCreated)segments.Dispose();if(index.IsCreated)index.Dispose();if(refs.IsCreated)refs.Dispose();if(coastSegments.IsCreated)coastSegments.Dispose();if(coastIndex.IsCreated)coastIndex.Dispose();if(coastRefs.IsCreated)coastRefs.Dispose();disposed=true;}
        public JobHandle Dispose(JobHandle readers)
        {if(disposed)return readers;var h=readers;if(landmasses.IsCreated)h=JobHandle.CombineDependencies(h,landmasses.Dispose(readers));if(coasts.IsCreated)h=JobHandle.CombineDependencies(h,coasts.Dispose(readers));if(coastDirections.IsCreated)h=JobHandle.CombineDependencies(h,coastDirections.Dispose(readers));if(nodes.IsCreated)h=JobHandle.CombineDependencies(h,nodes.Dispose(readers));if(segments.IsCreated)h=JobHandle.CombineDependencies(h,segments.Dispose(readers));if(index.IsCreated)h=JobHandle.CombineDependencies(h,index.Dispose(readers));if(refs.IsCreated)h=JobHandle.CombineDependencies(h,refs.Dispose(readers));if(coastSegments.IsCreated)h=JobHandle.CombineDependencies(h,coastSegments.Dispose(readers));if(coastIndex.IsCreated)h=JobHandle.CombineDependencies(h,coastIndex.Dispose(readers));if(coastRefs.IsCreated)h=JobHandle.CombineDependencies(h,coastRefs.Dispose(readers));disposed=true;return h;}
    }
}
