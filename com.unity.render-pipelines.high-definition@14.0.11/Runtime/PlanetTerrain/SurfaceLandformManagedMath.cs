using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Allocation-free managed authoring diagnostics over the same immutable captured records.</summary>
    public static class SurfaceLandformManagedMath
    {
        public static SurfaceSampleStatus TrySample(this SurfaceLandformField field,double3 input,out double height)
        {
            height=0;if(!CubeSurface.TryNormalize(input,out var d))return SurfaceSampleStatus.InvalidInput;if(field==null)return SurfaceSampleStatus.NotReady;
            int node=0;
            for(int depth=0;depth<=SurfaceLandformField.MaximumIndexDepth;depth++)
            {
                if(node<0||node>=field.IndexNodeCount)return SurfaceSampleStatus.IncompatibleData;var index=field.IndexNodeAt(node);
                if(!index.IsLeaf){node=d[index.Axis]<=index.Split?index.Left:index.Right;continue;}
                double minimumQ=1;
                for(int i=0;i<index.Count;i++)
                {
                    var c=field.Controls[field.SpatialReferenceAt(index.First+i)];var offset=(d-c.Direction)*(field.SourceRecipe.Radius/c.SupportMetres);double q=math.dot(offset,offset);
                    if(q==0){height=c.Height;return SurfaceSampleStatus.Ready;}minimumQ=math.min(minimumQ,q);
                }
                if(!(minimumQ<1))return SurfaceSampleStatus.NotReady;double weighted=0,total=0;
                for(int i=0;i<index.Count;i++)
                {
                    var c=field.Controls[field.SpatialReferenceAt(index.First+i)];var offset=(d-c.Direction)*field.SourceRecipe.Radius;
                    double q=math.dot(offset/c.SupportMetres,offset/c.SupportMetres);if(q>=1)continue;
                    double tail=1-q,ratio=minimumQ/q,weight=(tail*tail)*(tail*tail)*(ratio*ratio),term=c.Height;
                    if(c.VariationLimit>0){double x=math.dot(c.Gradient,offset),scaled=x/c.VariationLimit;term+=x/math.sqrt(1+scaled*scaled);}
                    total+=weight;weighted+=weight*term;
                }
                if(!(total>0)||!math.isfinite(weighted))return SurfaceSampleStatus.IncompatibleData;height=weighted/total;
                return math.isfinite(height)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
            }
            return SurfaceSampleStatus.IncompatibleData;
        }
    }
}
