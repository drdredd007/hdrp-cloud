using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Continuous finite-footprint evaluation of an explicit approximate area hierarchy; Full authority is unchanged.</summary>
    public static class SurfaceLandformFilterMath
    {
        public static SurfaceSampleStatus TrySample(in NativeSurfaceLandformView full,in NativeSurfaceLandformFilterView filter,
            double3 direction,SurfaceSamplingFootprint footprint,out double height)
        {
            height=0;if(!footprint.IsValid)return SurfaceSampleStatus.InvalidInput;
            // Preserve the exact existing Full arithmetic, including its one normalization.
            if(footprint.Metres==0)return SurfaceLandformMath.TrySample(full,direction,out height);
            if(!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(!filter.Matches(full))return SurfaceSampleStatus.NotReady;
            if(filter.MaximumLevel==0||footprint.Metres<=Threshold(filter,filter.MaximumLevel))return SurfaceLandformMath.TrySample(full,direction,out height);
            // Exact dyadic FP64 comparisons and polynomials, also portable to SM5 without float log2.
            for(int upper=filter.MaximumLevel;upper>=1;upper--)
            {
                if(footprint.Metres>Threshold(filter,upper-1))continue;
                double t=math.clamp(footprint.Metres/Threshold(filter,upper)-1,0,1);t=t*t*(3-2*t);
                var status=TrySampleLevel(full,filter,d,upper,out double a);if(status!=SurfaceSampleStatus.Ready)return status;
                status=TrySampleLevel(full,filter,d,upper-1,out double b);if(status!=SurfaceSampleStatus.Ready)return status;
                height=math.lerp(a,b,t);return math.isfinite(height)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
            }
            return TrySampleLevel(full,filter,d,0,out height);
        }
        static double Threshold(in NativeSurfaceLandformFilterView filter,int level)=>filter.Radius/(2*(1<<level));
        public static SurfaceSampleStatus TrySampleLevel(in NativeSurfaceLandformView full,in NativeSurfaceLandformFilterView filter,double3 direction,int level,out double height)
        {
            height=0;
            if(!filter.Matches(full))return SurfaceSampleStatus.NotReady;
            if(level==filter.MaximumLevel)return SurfaceLandformMath.TrySample(full,direction,out height);
            if(!CubeSurface.TryNormalize(direction,out var d))return SurfaceSampleStatus.InvalidInput;
            if(level<0||level>=filter.Levels.Length)return SurfaceSampleStatus.InvalidInput;
            var header=filter.Levels[level];int node=header.Root;
            if(header.Level!=level||header.FirstIndex<0||header.IndexCount<=0||(long)header.FirstIndex+header.IndexCount>filter.Index.Length||
                header.FirstReference<0||header.ReferenceCount<0||(long)header.FirstReference+header.ReferenceCount>filter.References.Length)return SurfaceSampleStatus.IncompatibleData;
            for(int depth=0;depth<=SurfaceLandformField.MaximumIndexDepth;depth++)
            {
                if(node<header.FirstIndex||node>=header.FirstIndex+header.IndexCount)return SurfaceSampleStatus.IncompatibleData;
                var index=filter.Index[node];if(index.IsLeaf)return SampleLeaf(full,filter,header,index,d,out height);
                if(index.Axis<0||index.Axis>2)return SurfaceSampleStatus.IncompatibleData;node=d[index.Axis]<=index.Split?index.Left:index.Right;
            }
            return SurfaceSampleStatus.IncompatibleData;
        }
        static SurfaceSampleStatus SampleLeaf(in NativeSurfaceLandformView full,in NativeSurfaceLandformFilterView filter,SurfaceLandformFilterLevel header,SurfaceDrainageIndexNode index,double3 d,out double height)
        {
            height=0;if(index.Count<0||index.Count>SurfaceLandformField.MaximumLeafCandidates||index.First<header.FirstReference||(long)index.First+index.Count>header.FirstReference+header.ReferenceCount)return SurfaceSampleStatus.IncompatibleData;
            double minimumQ=1;
            for(int i=0;i<index.Count;i++)
            {
                if(!TryControl(full,filter,filter.References[index.First+i],out var c))return SurfaceSampleStatus.IncompatibleData;
                var offset=(d-c.Direction)*(filter.Radius/c.SupportMetres);double q=math.dot(offset,offset);if(q==0){height=c.Height;return SurfaceSampleStatus.Ready;}minimumQ=math.min(minimumQ,q);
            }
            if(!(minimumQ<1))return SurfaceSampleStatus.NotReady;double sum=0,total=0;
            for(int i=0;i<index.Count;i++)
            {
                if(!TryControl(full,filter,filter.References[index.First+i],out var c))return SurfaceSampleStatus.IncompatibleData;
                var offset=(d-c.Direction)*filter.Radius;double q=math.dot(offset/c.SupportMetres,offset/c.SupportMetres);if(q>=1)continue;
                double tail=1-q,ratio=minimumQ/q,w=(tail*tail)*(tail*tail)*(ratio*ratio),term=c.Height;
                if(c.VariationLimit>0){double x=math.dot(c.Gradient,offset),scaled=x/c.VariationLimit;term+=x/math.sqrt(1+scaled*scaled);}sum+=w*term;total+=w;
            }
            if(!(total>0)||!math.isfinite(sum))return SurfaceSampleStatus.IncompatibleData;height=sum/total;return math.isfinite(height)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
        static bool TryControl(in NativeSurfaceLandformView full,in NativeSurfaceLandformFilterView filter,int encoded,out SurfaceLandformControl c)
        {
            c=default;if(encoded>=0){if(encoded>=filter.DerivedControls.Length)return false;c=filter.DerivedControls[encoded];return true;}
            int original=-(encoded+1);if(original<0||original>=full.Controls.Length)return false;c=full.Controls[original];return true;
        }
    }
}
