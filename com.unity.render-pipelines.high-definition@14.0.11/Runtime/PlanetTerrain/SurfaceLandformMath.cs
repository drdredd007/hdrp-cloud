using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Pure bounded C1 reconstruction, shared by Full physics and the renderer adapter.</summary>
    public static class SurfaceLandformMath
    {
        public static SurfaceSampleStatus TrySample(in NativeSurfaceLandformView field,double3 input,out double height)
        {
            height=0;
            if(!CubeSurface.TryNormalize(input,out var direction))return SurfaceSampleStatus.InvalidInput;
            if(!field.Enabled)return SurfaceSampleStatus.NotReady;
            if(field.Index.Length==0||field.Controls.Length==0||!(field.Radius>0))return SurfaceSampleStatus.IncompatibleData;
            int node=0;
            for(int depth=0;depth<=SurfaceLandformField.MaximumIndexDepth;depth++)
            {
                if(node<0||node>=field.Index.Length)return SurfaceSampleStatus.IncompatibleData;
                var index=field.Index[node];
                if(index.IsLeaf)return SampleLeaf(field,direction,index,out height);
                if(index.Axis<0||index.Axis>2)return SurfaceSampleStatus.IncompatibleData;
                node=direction[index.Axis]<=index.Split?index.Left:index.Right;
            }
            return SurfaceSampleStatus.IncompatibleData;
        }
        static SurfaceSampleStatus SampleLeaf(in NativeSurfaceLandformView field,double3 direction,SurfaceDrainageIndexNode index,out double height)
        {
            height=0;
            if(index.First<0||index.Count<0||index.Count>SurfaceLandformField.MaximumLeafCandidates||
                (long)index.First+index.Count>field.References.Length)return SurfaceSampleStatus.IncompatibleData;
            double minimumQ=1;
            for(int i=0;i<index.Count;i++)
            {
                int id=field.References[index.First+i];if(id<0||id>=field.Controls.Length)return SurfaceSampleStatus.IncompatibleData;
                var c=field.Controls[id];var offset=(direction-c.Direction)*(field.Radius/c.SupportMetres);
                double q=math.dot(offset,offset);
                // Only exact coincidence takes this branch. Otherwise relative weights use qMin/q,
                // which cannot overflow even arbitrarily close to a captured control. Its limiting
                // derivative is the captured tangent gradient; no epsilon-radius flat disc is added.
                if(q==0){height=c.Height;return SurfaceSampleStatus.Ready;}
                minimumQ=math.min(minimumQ,q);
            }
            if(!(minimumQ<1))return SurfaceSampleStatus.NotReady;
            double weighted=0,total=0;
            for(int i=0;i<index.Count;i++)
            {
                var c=field.Controls[field.References[index.First+i]];
                var offset=(direction-c.Direction)*field.Radius;
                double q=math.dot(offset/c.SupportMetres,offset/c.SupportMetres);if(q>=1)continue;
                double tail=1-q,ratio=minimumQ/q,weight=(tail*tail)*(tail*tail)*(ratio*ratio);
                double term=c.Height;
                if(c.VariationLimit>0)
                {
                    double x=math.dot(c.Gradient,offset),scaled=x/c.VariationLimit;
                    term+=x/math.sqrt(1+scaled*scaled);
                }
                total+=weight;weighted+=weight*term;
            }
            if(!(total>0)||!math.isfinite(weighted))return SurfaceSampleStatus.IncompatibleData;
            height=weighted/total;return math.isfinite(height)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
    }
}
