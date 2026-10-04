using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Version-four portable reference: compact catchment hillslopes around an explicitly connected stream graph.</summary>
    public static class SurfaceDrainageMath
    {
        /// <summary>Exact nearest coast within the declared saturated strip; deeper points need only the captured land sign.</summary>
        public static SurfaceSampleStatus TrySampleCoastForMacro(in NativeSurfaceDrainageView view,double3 direction,
            out double signedDistance,out int checkedSegments)
        {
            signedDistance=0;checkedSegments=0;
            if(!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(!view.Enabled||!view.CoastIndex.IsCreated||!view.CoastReferences.IsCreated)return SurfaceSampleStatus.NotReady;
            if(view.Landmasses.Length>SurfaceDrainageField.MaximumLandmasses)return SurfaceSampleStatus.IncompatibleData;
            if(!TryLeaf(view.CoastIndex,d,out int first,out int count)||first<0||(long)first+count>view.CoastReferences.Length)return SurfaceSampleStatus.IncompatibleData;
            double chord=2*SurfaceStructuralMath.Sin(view.CoastInfluenceMetres/(2*view.Radius));
            var distanceSquared=new FixedList512Bytes<double>();
            for(int i=0;i<view.Landmasses.Length;i++)distanceSquared.Add(chord*chord);
            for(int i=0;i<count;i++)
            {
                int candidate=view.CoastReferences[first+i];if(candidate<0||candidate>=view.CoastSegments.Length)return SurfaceSampleStatus.IncompatibleData;
                var segment=view.CoastSegments[candidate];if(math.any(d<segment.Minimum)||math.any(d>segment.Maximum))continue;
                if(segment.Landmass<0||segment.Landmass>=distanceSquared.Length||segment.First<0||segment.Last<0||segment.First>=view.CoastDirections.Length||segment.Last>=view.CoastDirections.Length)return SurfaceSampleStatus.IncompatibleData;
                checkedSegments++;distanceSquared[segment.Landmass]=math.min(distanceSquared[segment.Landmass],ClosestSquared(d,view.CoastDirections[segment.First],view.CoastDirections[segment.Last]));
            }
            double best=-view.CoastInfluenceMetres;
            for(int m=0;m<view.Landmasses.Length;m++)
            {
                double distance=distanceSquared[m]>=chord*chord?view.CoastInfluenceMetres:
                    2*SurfaceStructuralMath.Asin(math.min(1,math.sqrt(distanceSquared[m])*.5))*view.Radius;
                best=math.max(best,Contains(view,view.Landmasses[m],d)?distance:-distance);
            }
            signedDistance=best;return math.isfinite(best)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
        public static bool Contains(in NativeSurfaceDrainageView view,SurfaceCoastLandmass mass,double3 d)
        {
            double facing=math.dot(d,mass.Center);if(facing<=0)return false;
            var p=new double2(math.dot(d,mass.Right),math.dot(d,mass.Forward))*(view.Radius/facing);
            if(math.lengthsq(p)<1e-20)return true;
            var origin=view.CoastVertices[mass.FirstVertex];
            int low=1,high=mass.VertexCount;
            while(low<high)
            {
                int middle=low+((high-low)>>1);var vertex=view.CoastVertices[mass.FirstVertex+middle];
                if(AngleLessOrEqual(vertex,p,origin))low=middle+1;else high=middle;
            }
            int previous=low-1,next=low==mass.VertexCount?0:low;
            var a=view.CoastVertices[mass.FirstVertex+previous];var b=view.CoastVertices[mass.FirstVertex+next];
            return Cross(b-a,p-a)>=0;
        }
        static bool AngleLessOrEqual(double2 a,double2 b,double2 origin)
        {
            var first=new double2(math.dot(a,origin),Cross(origin,a));var second=new double2(math.dot(b,origin),Cross(origin,b));
            int ha=first.y>0||first.y==0&&first.x>=0?0:1,hb=second.y>0||second.y==0&&second.x>=0?0:1;
            return ha!=hb?ha<hb:Cross(first,second)>=0;
        }
        static double Cross(double2 a,double2 b)=>a.x*b.y-a.y*b.x;
        static double ClosestSquared(double3 d,double3 first,double3 last)
        {
            var normal=math.normalizesafe(math.cross(first,last));double dot=math.dot(d,normal);
            var projected=math.normalizesafe(d-normal*dot,first);if(math.dot(projected,first+last)<0)projected=-projected;
            bool onArc=math.dot(math.cross(first,projected),normal)>=-1e-13&&math.dot(math.cross(projected,last),normal)>=-1e-13;
            var closest=onArc?projected:(math.dot(d,first)>=math.dot(d,last)?first:last);
            return math.lengthsq(d-closest);
        }
        static bool TryLeaf(NativeArray<SurfaceDrainageIndexNode>.ReadOnly index,double3 d,out int first,out int count)
        {
            first=count=0;int cursor=0;
            for(int step=0;step<=SurfaceDrainageField.MaximumIndexDepth;step++)
            {
                if(cursor<0||cursor>=index.Length)return false;var node=index[cursor];
                if(node.Axis<0){first=node.First;count=node.Count;return count>=0&&count<=SurfaceDrainageField.MaximumLeafCandidates;}
                if(node.Axis>2)return false;cursor=d[node.Axis]<node.Split?node.Left:node.Right;
            }
            return false;
        }
        public static SurfaceSampleStatus TrySampleCoast(in NativeSurfaceDrainageView view,double3 direction,out double signedDistance)
        {
            signedDistance=0;
            if(!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(!view.Enabled||!view.Landmasses.IsCreated||!view.CoastVertices.IsCreated||!view.CoastDirections.IsCreated)return SurfaceSampleStatus.NotReady;
            double best=-view.Radius*math.PI;
            for(int m=0;m<view.Landmasses.Length;m++)
            {
                var mass=view.Landmasses[m];
                if(mass.FirstVertex<0||mass.VertexCount<8||(long)mass.FirstVertex+mass.VertexCount>view.CoastVertices.Length)return SurfaceSampleStatus.IncompatibleData;
                double facing=math.dot(d,mass.Center);bool inside=false;
                var p=facing>0?new double2(math.dot(d,mass.Right),math.dot(d,mass.Forward))*(view.Radius/facing):new double2(0);
                double distanceSquared=4;
                for(int j=0;j<mass.VertexCount;j++)
                {
                    int a=mass.FirstVertex+j,b=mass.FirstVertex+(j+1)%mass.VertexCount;
                    var pa=view.CoastVertices[a];var pb=view.CoastVertices[b];
                    // Hemisphere-local winding is independent of longitude seams and pole orientation.
                    if(facing>0&&(pa.y>p.y)!=(pb.y>p.y))
                    {double crossing=(pb.x-pa.x)*(p.y-pa.y)/(pb.y-pa.y)+pa.x;if(p.x<crossing)inside=!inside;}
                    var first=view.CoastDirections[a];var last=view.CoastDirections[b];
                    distanceSquared=math.min(distanceSquared,ClosestSquared(d,first,last));
                }
                double distance=2*SurfaceStructuralMath.Asin(math.min(1,math.sqrt(distanceSquared)*.5))*view.Radius;
                best=math.max(best,inside?distance:-distance);
            }
            signedDistance=best;return math.isfinite(best)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }

        public static SurfaceSampleStatus TrySampleIncision(in NativeSurfaceDrainageView view,double3 direction,double macroHeight,
            SurfaceSamplingFootprint footprint,out double incision,out int checkedSegments,bool allSegments=false)
        {
            incision=0;checkedSegments=0;
            if(!footprint.IsValid||!math.isfinite(macroHeight)||!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(!view.Enabled||!view.Nodes.IsCreated||!view.Segments.IsCreated||!view.Index.IsCreated||!view.References.IsCreated)return SurfaceSampleStatus.NotReady;
            int first=0,count=view.Segments.Length;
            if(!allSegments)
            {
                int cursor=0;bool found=false;
                for(int step=0;step<=SurfaceDrainageField.MaximumIndexDepth;step++)
                {
                    if(cursor<0||cursor>=view.Index.Length)return SurfaceSampleStatus.IncompatibleData;
                    var node=view.Index[cursor];
                    if(node.Axis<0)
                    {first=node.First;count=node.Count;found=true;break;}
                    if(node.Axis>2)return SurfaceSampleStatus.IncompatibleData;
                    cursor=d[node.Axis]<node.Split?node.Left:node.Right;
                }
                if(!found||count<0||count>SurfaceDrainageField.MaximumLeafCandidates||first<0||(long)first+count>view.References.Length)return SurfaceSampleStatus.IncompatibleData;
            }
            for(int i=0;i<count;i++)
            {
                int segmentIndex=allSegments?i:view.References[first+i];
                if(segmentIndex<0||segmentIndex>=view.Segments.Length)return SurfaceSampleStatus.IncompatibleData;
                var segment=view.Segments[segmentIndex];
                if(math.any(d<segment.Minimum)||math.any(d>segment.Maximum))continue;
                checkedSegments++;
                if(segment.Child<0||segment.Child>=view.Nodes.Length||segment.Parent<0||segment.Parent>=segment.Child)return SurfaceSampleStatus.IncompatibleData;
                var child=view.Nodes[segment.Child];var parent=view.Nodes[segment.Parent];
                var axis=child.Direction-parent.Direction;double lengthSquared=math.lengthsq(axis);
                if(lengthSquared<=0)return SurfaceSampleStatus.IncompatibleData;
                double t=math.clamp(math.dot(d-parent.Direction,axis)/lengthSquared,0,1);
                var nearest=math.normalizesafe(parent.Direction+axis*t,parent.Direction);
                double distance=math.length(d-nearest)*view.Radius;
                double width=math.lerp(parent.HillslopeWidth,child.HillslopeWidth,t);
                if(distance>=2*width)continue;
                // Bed and divide elevations are captured stream-power/hillslope results. Broad slopes reach
                // their divide at width, then blend compactly to the unchanged macro outside two widths.
                double bed=math.lerp(parent.BedHeight,child.BedHeight,t),divide=math.lerp(parent.DivideHeight,child.DivideHeight,t);
                double target=bed+math.max(0,divide-bed)*Smooth(distance/width);
                double lateral=1-Smooth(distance/width-1);
                double weight=footprint.DetailWeight(width*4);
                double carved=math.min(0,target-macroHeight)*lateral*weight;
                incision=math.min(incision,carved);
            }
            return math.isfinite(incision)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
        public static double Smooth(double x){x=math.clamp(x,0,1);return x*x*(3-2*x);}
    }
}
