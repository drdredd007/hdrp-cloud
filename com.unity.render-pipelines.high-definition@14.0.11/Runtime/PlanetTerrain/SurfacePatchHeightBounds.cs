using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    // Derived worker-only metadata. Source arrays stay in the immutable snapshot; no height authority is copied or changed.
    internal sealed class SurfacePatchHeightBounds
    {
        const int BlockSize = 16;
        const int FixedMetadataBytes = 256;
        readonly SurfaceSnapshot source;
        readonly Grid[] tiles, raw, regions;
        readonly double globalRegionalCoefficient,globalRegionalConstant,globalSuppression;
        readonly double3 canonicalRegionMinimum,canonicalRegionMaximum,renderRegionMinimum,renderRegionMaximum;
        readonly double globalBaseMinimum,globalBaseMaximum;
        readonly bool hasGeometryRegions;
        public readonly bool MetadataBudgetExceeded;
        public long EstimatedBytes { get; }
        public long PreparationNodes { get; }
        public double SuppressionSlope { get; }
        readonly struct Range
        {
            public readonly double Min, Max, Slope;
            public Range(double min, double max, double slope) { Min = min; Max = max; Slope = slope; }
            public double Width => Max - Min;
            public double Magnitude => math.max(math.abs(Min), math.abs(Max));
        }
        readonly struct Block
        {
            public readonly Range Height, Mask;
            public Block(Range height, Range mask) { Height = height; Mask = mask; }
        }
        sealed class Grid
        {
            readonly Block[] blocks;
            readonly int2 resolution, count;
            readonly Range whole;
            public long Bytes => 64L + (blocks == null ? 0 : 24L + blocks.LongLength * 48);
            public Grid(int2 resolution, Func<int,double> height, Func<int,double> mask, double2 step,
                Range whole, bool allocate, Func<bool> cancelled)
            {
                this.resolution = resolution; this.whole = whole; count = (resolution + BlockSize - 1) / BlockSize;
                if (!allocate) return;
                blocks = new Block[count.x * count.y]; int row = resolution.x + 1;
                for (int by = 0; by < count.y; by++) for (int bx = 0; bx < count.x; bx++)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity, mx = 0, my = 0;
                    double alo = 1, ahi = 0, ax = 0, ay = 0;
                    int x0 = bx * BlockSize, y0 = by * BlockSize;
                    int x1 = math.min(resolution.x, x0 + BlockSize), y1 = math.min(resolution.y, y0 + BlockSize);
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        int i = y * row + x; double value = height(i), alpha = mask == null ? 1 : mask(i);
                        lo = math.min(lo,value); hi = math.max(hi,value); alo = math.min(alo,alpha); ahi = math.max(ahi,alpha);
                        if (x < x1) { mx = math.max(mx,math.abs(height(i+1)-value)/step.x); if(mask!=null) ax = math.max(ax,math.abs(mask(i+1)-alpha)/step.x); }
                        if (y < y1) { my = math.max(my,math.abs(height(i+row)-value)/step.y); if(mask!=null) ay = math.max(ay,math.abs(mask(i+row)-alpha)/step.y); }
                    }
                    blocks[by*count.x+bx] = new Block(new Range(lo,hi,mx+my),new Range(alo,ahi,ax+ay));
                }
            }
            public Range Query(double2 low, double2 high, out Range mask)
            { int work=0;bool incomplete=false;return Query(low,high,out mask,int.MaxValue,ref work,ref incomplete,null); }
            public Range Query(double2 low,double2 high,out Range mask,int maximumWork,ref int work,ref bool incomplete,Func<bool> cancelled)
            {
                if (blocks == null || !Spend(maximumWork,ref work,cancelled))
                { incomplete=true;mask = new Range(0,1,double.PositiveInfinity); return whole; }
                // Every bilinear cell touched by the closed query rectangle, including both sides of an exact grid edge.
                var first = (int2)math.clamp(math.floor(low*resolution/BlockSize - 1e-12),0,count-1);
                var last = (int2)math.clamp(math.floor(high*resolution/BlockSize + 1e-12),0,count-1);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity, slope = 0, alo = 1, ahi = 0, aslope = 0;
                for(int y=first.y;y<=last.y;y++) for(int x=first.x;x<=last.x;x++)
                {
                    if(!Spend(maximumWork,ref work,cancelled))
                    {incomplete=true;mask=new Range(0,1,double.PositiveInfinity);return whole;}
                    var b=blocks[y*count.x+x];lo=math.min(lo,b.Height.Min);hi=math.max(hi,b.Height.Max);slope=math.max(slope,b.Height.Slope);
                    alo=math.min(alo,b.Mask.Min);ahi=math.max(ahi,b.Mask.Max);aslope=math.max(aslope,b.Mask.Slope); }
                mask=new Range(alo,ahi,aslope); return new Range(lo,hi,slope);
            }
        }
        public SurfacePatchHeightBounds(SurfaceSnapshot snapshot, SurfaceErrorBuildSettings settings, Func<bool> cancelled)
        {
            source=snapshot;bool structural=source.StructuralField!=null;
            double baseMinimum=double.PositiveInfinity,baseMaximum=double.NegativeInfinity;
            foreach(var tile in source.Tiles)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                baseMinimum=math.min(baseMinimum,tile.MinimumHeight);baseMaximum=math.max(baseMaximum,tile.MaximumHeight);
            }
            if(structural)
            {
                var recipe=source.StructuralField.SourceRecipe;
                baseMinimum-=math.max(recipe.MaximumHeight,(double)(float)recipe.MaximumHeight);
                baseMaximum-=math.min(recipe.MinimumHeight,(double)(float)recipe.MinimumHeight);
            }
            globalBaseMinimum=baseMinimum;globalBaseMaximum=baseMaximum;
            // Constant-size unions are prepared once. A zero-work query can prove that
            // ALL region operators are remote without scanning any header or raw block.
            double3 canonicalLow=new double3(double.PositiveInfinity),canonicalHigh=new double3(double.NegativeInfinity);
            double3 renderLow=canonicalLow,renderHigh=canonicalHigh;bool geometryRegions=false;
            // Precompute the affine whole-chain fallback once. An exhausted query never scans
            // thousands of remaining headers outside its work budget to reconstruct this bound.
            double prefix=0,coefficient=0,constant=0,suppressionFallback=0;
            foreach(var region in source.Regions)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                double h=math.max(math.abs(region.MinimumHeight),math.abs(region.MaximumHeight));
                if(region.Mode==SurfaceRegionMode.Replace||h!=0||region.DetailPolicy==SurfaceDetailPolicy.Suppress)
                {
                    geometryRegions=true;var p=region.Projection;var extent=p.MaximumMetres-p.MinimumMetres;
                    IncludeRegionCap(p,math.max(math.abs(p.MinimumMetres),math.abs(p.MaximumMetres)),ref canonicalLow,ref canonicalHigh);
                    IncludeRegionCap(p,math.max(math.abs(p.MinimumMetres-2*extent),math.abs(p.MaximumMetres+2*extent)),ref renderLow,ref renderHigh);
                }
                if(region.Mode==SurfaceRegionMode.Replace)
                {coefficient=1.00001*coefficient+1.00001;constant=1.00001*constant+2*h+prefix+(h+prefix+1)*.00001;}
                else constant+=2*h+(h+prefix+1)*.00001;
                if(region.Mode!=SurfaceRegionMode.Replace)coefficient+=.00001;
                if(region.DetailPolicy==SurfaceDetailPolicy.Suppress)suppressionFallback=1.00001*(suppressionFallback+1);
                prefix+=h;
            }
            hasGeometryRegions=geometryRegions;canonicalRegionMinimum=canonicalLow;canonicalRegionMaximum=canonicalHigh;
            renderRegionMinimum=renderLow;renderRegionMaximum=renderHigh;
            globalRegionalCoefficient=coefficient;globalRegionalConstant=constant;globalSuppression=suppressionFallback;
            long grids=source.Tiles.Count*(structural?2L:1L)+source.Regions.Count;
            long headers=FixedMetadataBytes+72L*grids;
            if(headers>settings.MaximumPageMetadataBytes)
            { MetadataBudgetExceeded=true;EstimatedBytes=FixedMetadataBytes;SuppressionSlope=double.PositiveInfinity;return; }
            tiles=new Grid[source.Tiles.Count];regions=new Grid[source.Regions.Count];raw=structural?new Grid[source.Tiles.Count]:null;
            long nodes=0, bytes=FixedMetadataBytes + 8L*grids;
            foreach(var tile in source.Tiles) { nodes+=tile.SampleCount;bytes+=GridBytes(new int2(tile.Resolution))*(structural?2:1); }
            foreach(var region in source.Regions) { nodes+=region.SampleCount;bytes+=GridBytes(region.Resolution); }
            MetadataBudgetExceeded=nodes>settings.MaximumPageSourceNodes||bytes>settings.MaximumPageMetadataBytes;
            PreparationNodes=MetadataBudgetExceeded?0:nodes; bool allocate=!MetadataBudgetExceeded;
            var field=source.StructuralField;
            for(int t=0;t<tiles.Length;t++)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                var tile=source.Tiles[t]; var step=new double2(2*source.Recipe.Radius/((1L<<tile.Key.Level)*tile.Resolution));
                if(field==null) tiles[t]=new Grid(new int2(tile.Resolution),i=>tile.HeightAt(i),null,step,
                    new Range(tile.MinimumHeight,tile.MaximumHeight,double.PositiveInfinity),allocate,cancelled);
                else
                {
                    int offset=tile.Key.Face*(tile.Resolution+1)*(tile.Resolution+1);
                    tiles[t]=new Grid(new int2(tile.Resolution),i=>(double)tile.HeightAt(i)-field.RawMacroAt(offset+i),null,step,
                        new Range(tile.MinimumHeight-math.max(field.SourceRecipe.MaximumHeight,(double)(float)field.SourceRecipe.MaximumHeight),
                            tile.MaximumHeight-math.min(field.SourceRecipe.MinimumHeight,(double)(float)field.SourceRecipe.MinimumHeight),double.PositiveInfinity),allocate,cancelled);
                    raw[t]=new Grid(new int2(tile.Resolution),i=>field.RawMacroAt(offset+i),null,step,
                        new Range(math.min(field.SourceRecipe.MinimumHeight,(double)(float)field.SourceRecipe.MinimumHeight),
                            math.max(field.SourceRecipe.MaximumHeight,(double)(float)field.SourceRecipe.MaximumHeight),double.PositiveInfinity),allocate,cancelled);
                }
            }
            for(int r=0;r<regions.Length;r++)
            {
                var region=source.Regions[r]; var step=(region.Projection.MaximumMetres-region.Projection.MinimumMetres)/region.Resolution;
                regions[r]=new Grid(region.Resolution,i=>region.HeightAt(i),i=>region.MaskAt(i),step,
                    new Range(region.MinimumHeight,region.MaximumHeight,double.PositiveInfinity),allocate,cancelled);
            }
            long actual=FixedMetadataBytes+8L*(tiles.Length+regions.Length+(raw==null?0:raw.Length));
            foreach(var grid in tiles)actual+=grid.Bytes;foreach(var grid in regions)actual+=grid.Bytes;if(raw!=null)foreach(var grid in raw)actual+=grid.Bytes;
            EstimatedBytes=actual;
            double suppression=0;
            for(int r=0;r<regions.Length;r++)
            {
                var region=source.Regions[r];if(region.DetailPolicy!=SurfaceDetailPolicy.Suppress)continue;
                regions[r].Query(new double2(0),new double2(1),out var mask);
                var reach=math.max(math.abs(region.Projection.MinimumMetres),math.abs(region.Projection.MaximumMetres))/source.Recipe.Radius;
                suppression+=(mask.Slope+(region.BlendMetres>0?1.5/region.BlendMetres:double.PositiveInfinity))*(1+math.lengthsq(reach));
            }
            SuppressionSlope=suppression;
        }
        static long GridBytes(int2 n)=>88L+48L*((n.x+BlockSize-1)/BlockSize)*((n.y+BlockSize-1)/BlockSize);

        // The local field range and its variation bound include authority between all measured supports.
        public bool TryBound(in NativeSurfaceView view,SurfaceTileKey key,int meshResolution,SurfaceSamplingFootprint footprint,
            int maximumWork,out int work,out bool incomplete,
            out double interpolation,out double attenuation,out double minimum,out double maximum,Func<bool> cancelled=null)
            => TryBound(view,key,meshResolution,footprint,maximumWork,out work,out incomplete,
                out interpolation,out attenuation,out minimum,out maximum,out _,cancelled);

        public bool TryBound(in NativeSurfaceView view,SurfaceTileKey key,int meshResolution,SurfaceSamplingFootprint footprint,
            int maximumWork,out int work,out bool incomplete,
            out double interpolation,out double attenuation,out double minimum,out double maximum,out double regionalFilteringMetres,Func<bool> cancelled=null)
        {
            interpolation=attenuation=minimum=maximum=regionalFilteringMetres=0;work=0;incomplete=false;
            bool localSupport=source.Regions.Count>0||source.Stamps.Count>0||source.OrogenDetail!=null;
            bool regional=source.Regions.Count>0&&footprint.Metres>0;
            if(tiles==null)
            {
                if(!HasCanonicalCover(key))return false;
                minimum=source.MinimumHeight;maximum=source.MaximumHeight;interpolation=maximum-minimum;
                // Oversized metadata is explicit incomplete authority support, not a silently truncated page list.
                attenuation=footprint.Metres>0?interpolation:0;incomplete=true;
                if(regional)
                {
                    double finiteLoss=attenuation;
                    if(source.StructuralField?.LandformField!=null)
                    {
                        CubeSurface.TryDirection(key,new double2(.5),out var capCenter);
                        double fallbackAngle=math.min(Math.PI,PatchAngle(key,capCenter)+4*Math.Sqrt(2)/((1L<<key.Level)*(double)meshResolution));
                        // A metadata failure is not permission to substitute Full for a missing finite
                        // landform attachment. The bounded native certificate retains that contract.
                        if(!LandformRange(view,capCenter,fallbackAngle,footprint,maximumWork,out _,out finiteLoss,out double arithmetic,
                            out work,out _,cancelled))return false;
                        interpolation+=arithmetic;attenuation+=finiteLoss;
                    }
                    regionalFilteringMetres=GlobalRegionalBound(view,minimum-finiteLoss,maximum+finiteLoss);
                }
                return true;
            }
            double count=1L<<key.Level; var pmin=new double2(key.X,key.Y)/count;var pmax=pmin+1/count;
            double area=0,lo=double.PositiveInfinity,hi=double.NegativeInfinity,slope=0;
            double rawLo=double.PositiveInfinity,rawHi=double.NegativeInfinity;
            for(int t=0;t<tiles.Length;t++)
            {
                var tile=source.Tiles[t];if(tile.Key.Face!=key.Face)continue;
                double tc=1L<<tile.Key.Level;var tmin=new double2(tile.Key.X,tile.Key.Y)/tc;
                var low=math.max(pmin,tmin);var high=math.min(pmax,tmin+1/tc);if(math.any(high<=low))continue;
                area+=(high.x-low.x)*(high.y-low.y);
                var b=localSupport?tiles[t].Query((low-tmin)*tc,(high-tmin)*tc,out _,maximumWork,ref work,ref incomplete,cancelled):
                    tiles[t].Query((low-tmin)*tc,(high-tmin)*tc,out _);
                lo=math.min(lo,b.Min);hi=math.max(hi,b.Max);
                // Inverse normalised cube projection has Jacobian <=3 over a face; sum slopes remains conservative.
                slope=math.max(slope,b.Slope*3);
                if(raw!=null){var q=localSupport?raw[t].Query((low-tmin)*tc,(high-tmin)*tc,out _,maximumWork,ref work,ref incomplete,cancelled):
                    raw[t].Query((low-tmin)*tc,(high-tmin)*tc,out _);rawLo=math.min(rawLo,q.Min);rawHi=math.max(rawHi,q.Max);}
            }
            if(area<(1/count)*(1/count)*(1-1e-10))return false; // Missing canonical tiles are never a finite refinement permission.
            CubeSurface.TryDirection(key,new double2(.5),out var center);
            double angle=PatchAngle(key,center), chord=2*math.sin(angle*.5);
            double cellMetres=4*Math.Sqrt(2)*source.Recipe.Radius/(count*meshResolution);
            double haloAngle=math.min(Math.PI,angle+cellMetres/source.Recipe.Radius);
            bool canonicalRegionsRelevant=RegionsMayIntersect(center,haloAngle,false);
            regional&=RegionsMayIntersect(center,haloAngle,true);
            if(localSupport)
            {
                CapBaseRange(center,haloAngle,maximumWork,ref work,ref incomplete,out var cap,out var capRaw,cancelled);
                lo=cap.Min;hi=cap.Max;slope=cap.Slope;
                if(raw!=null){rawLo=capRaw.Min;rawHi=capRaw.Max;}
            }
            double priorLo=lo,priorHi=hi;
            double jump=0;
            double intrinsicAmplitude=0,intrinsicSlope=0,intrinsicLoss=0,intrinsicReserve=0;
            if(source.OrogenDetail!=null)
            {
                if(!SurfaceOrogenDetailBounds.TryBound(view.OrogenDetail,key,meshResolution,footprint,slope,maximumWork-work,
                    out var intrinsic,out int intrinsicWork))return false;
                work+=intrinsicWork;incomplete|=!intrinsic.Complete;
                intrinsicAmplitude=intrinsic.MaximumAmplitude;intrinsicSlope=intrinsic.MaximumSlope;intrinsicLoss=intrinsic.FullToFilteredLoss;
                intrinsicReserve=intrinsic.ArithmeticReserve;
            }
            if(source.StructuralField!=null)
            {
                Range macro,ridges;double structuralSlope;
                var f=source.StructuralField;
                if(f.LandformField!=null)
                {
                    // Include a conservative seam/normal halo around the closed leaf and the
                    // parent's twice-fine footprint. The edge stitch itself stays inside the leaf.
                    if(!LandformRange(view,center,haloAngle,footprint,maximumWork-work,
                        out macro,out attenuation,out jump,out int landformWork,out bool landformIncomplete,cancelled))return false;
                    work+=landformWork;incomplete|=landformIncomplete;
                    ridges=new Range(0,0,0);structuralSlope=macro.Slope;
                }
                else
                {
                    StructuralRange(center,localSupport?haloAngle:angle,localSupport?2*math.sin(haloAngle*.5):chord,
                        out macro,out ridges,out structuralSlope,out jump);
                    attenuation=math.max(math.abs(macro.Min-rawHi),math.abs(macro.Max-rawLo))*(1-footprint.DetailWeight(math.min(f.ShelfWidthMetres,f.BeltWidthMetres)))+
                        ridges.Magnitude*(1-footprint.DetailWeight(f.RegionalFeatureScaleMetres*.2));
                }
                lo+=macro.Min+ridges.Min;hi+=macro.Max+ridges.Max;slope+=structuralSlope;
                if(regional)
                {
                    priorLo+=macro.Min+ridges.Min;priorHi+=macro.Max+ridges.Max;
                }
                if(f.MorphologyVersion==2)
                {
                    // Incision is a min/blend against the actual eroded base, not an additive negative
                    // amplitude. A captured bed therefore provides a tighter conservative lower floor.
                    DrainageRange(f.DrainageField,center,localSupport?2*math.sin(haloAngle*.5):chord,hi,footprint,
                        out double bed,out double drainageSlope,out double lost);
                    lo=math.min(lo,bed);slope+=drainageSlope;attenuation+=lost;
                    // The older incision operator is bounded by the captured recipe floor. Do not
                    // claim a halo-local drainage interval from a centre-only support query.
                    if(regional)priorLo=math.min(priorLo,f.SourceRecipe.MinimumHeight);
                }
            }
            double regionalStructuralAttenuation=attenuation;
            if(regional)
            {
                // SAME finite base on both sides isolates regional transport from the separately
                // accounted canonical-to-render structural loss. Stamps are applied later and cancel.
                priorLo-=attenuation+intrinsicAmplitude+intrinsicLoss;priorHi+=attenuation+intrinsicAmplitude+intrinsicLoss;
                regionalFilteringMetres=RegionalBound(view,center,haloAngle,footprint,priorLo,priorHi,
                    intrinsicAmplitude,maximumWork,ref work,ref incomplete,out double canonicalGain,out double intrinsicGain,cancelled);
                regionalStructuralAttenuation=Product(attenuation,canonicalGain)+Product(intrinsicLoss,intrinsicGain);
            }
            for(int r=0;canonicalRegionsRelevant&&r<regions.Length;r++)
            {
                if(!Spend(maximumWork,ref work,cancelled))
                {
                    incomplete=true;minimum=source.MinimumHeight;maximum=source.MaximumHeight;
                    interpolation=maximum-minimum+jump;attenuation=regional?regionalStructuralAttenuation:attenuation+intrinsicLoss;
                    return math.isfinite(interpolation)&&math.isfinite(attenuation)&&math.isfinite(regionalFilteringMetres);
                }
                var region=view.Regions[r];var p=region.Projection;
                if(!SurfaceRegionFilterBounds.HasGeometry(region)||
                    !SurfaceRegionFilterBounds.TryCanonicalDomain(region,center,haloAngle,out var domain))continue;
                var extent=p.MaximumMetres-p.MinimumMetres;
                var b=regions[r].Query((domain.Low-p.MinimumMetres)/extent,(domain.High-p.MinimumMetres)/extent,out var mask,
                    maximumWork,ref work,ref incomplete,cancelled);
                var certificate=new SurfaceRegionFilterBounds.Raw(b.Min,b.Max,b.Slope,mask.Min,mask.Max,mask.Slope);
                SurfaceRegionFilterBounds.Coverage(region,domain,certificate,out double amin,out double amax,out double metricMaskSlope);
                var reach=math.max(math.abs(p.MinimumMetres),math.abs(p.MaximumMetres));
                double jacobian=(1+math.lengthsq(reach/p.Radius))*(1+1e-9);
                double mslope=metricMaskSlope*jacobian,hslope=b.Slope*jacobian;
                jump+=(math.max(math.abs(lo),math.abs(hi))+b.Magnitude+intrinsicAmplitude)*(128*2.22044604925031308085e-16);
                if(region.Mode==SurfaceRegionMode.Replace)
                {
                    double oldLo=lo,oldHi=hi;
                    lo=math.min(math.lerp(oldLo,b.Min,amin),math.lerp(oldLo,b.Min,amax));
                    hi=math.max(math.lerp(oldHi,b.Max,amin),math.lerp(oldHi,b.Max,amax));
                    double difference=math.max(math.abs(b.Min-oldHi),math.abs(b.Max-oldLo));
                    slope=Product(1-amin,slope)+Product(amax,hslope)+Product(mslope,difference);
                    if(!regional)attenuation*=1-amin;
                }
                else {lo+=math.min(amin*b.Min,amax*b.Min);hi+=math.max(amin*b.Max,amax*b.Max);slope+=Product(amax,hslope)+Product(mslope,b.Magnitude);}
                if(source.OrogenDetail!=null&&(region.Mode==SurfaceRegionMode.Replace||region.DetailPolicy==SurfaceDetailPolicy.Suppress))
                {
                    intrinsicSlope=Product(1-amin,intrinsicSlope)+Product(mslope,intrinsicAmplitude);
                    intrinsicAmplitude*=1-amin;
                    intrinsicReserve*=1-amin;
                    if(!regional)intrinsicLoss*=1-amin;
                }
            }
            if(regional)attenuation=regionalStructuralAttenuation;else attenuation+=intrinsicLoss;
            // Keep macro/region operators separate from intrinsic synthesis, so Replace+Suppress
            // feathers it once and a zero-height Delta+Suppress can remove it without widening the macro.
            foreach(var stamp in source.Stamps)
            {
                if(localSupport&&!Spend(maximumWork,ref work,cancelled))
                {
                    incomplete=true;lo=source.MinimumHeight;hi=source.MaximumHeight;slope=double.PositiveInfinity;break;
                }
                double separation=Arc(center,stamp.CenterDirection)*source.Recipe.Radius;
                if(separation-(localSupport?haloAngle:angle)*source.Recipe.Radius>stamp.RadiusMetres+stamp.RimWidthMetres)continue;
                lo-=stamp.DepthMetres;hi+=stamp.RimHeightMetres;
                slope+=8*stamp.DepthMetres/stamp.RadiusMetres+(stamp.RimWidthMetres>0?8*stamp.RimHeightMetres/stamp.RimWidthMetres:0);
            }
            minimum=lo-intrinsicAmplitude;maximum=hi+intrinsicAmplitude;
            // Triangle radial intersection uses positive weights. Its variable-height deviation is bounded by
            // the local range, or by field variation across the stitched two-cell diameter, plus sphere sagitta separately.
            // These are independent additive fields, with their own range and variation bounds.
            // A short-wavelength child's large certified slope cannot make the unrelated macro
            // contribution consume its entire (possibly multi-kilometre) metadata block range.
            double baseInterpolation=math.min(hi-lo,slope*cellMetres+jump);
            double intrinsicInterpolation=intrinsicAmplitude==0?0:
                math.min(2*intrinsicAmplitude,intrinsicSlope*cellMetres+2*intrinsicReserve);
            interpolation=baseInterpolation+intrinsicInterpolation;
            return math.isfinite(interpolation)&&interpolation>=0&&math.isfinite(attenuation)&&math.isfinite(regionalFilteringMetres);
        }

        static bool Spend(int maximumWork,ref int work,Func<bool> cancelled)
        {
            if(cancelled!=null&&cancelled())throw new OperationCanceledException();
            if(work>=maximumWork)return false;work++;return true;
        }

        static void CapAabb(double3 center,double angle,out double3 low,out double3 high)
        {
            SurfaceRegionFilterBounds.SphereDotInterval(center,angle,new double3(1,0,0),out double lx,out double hx);
            SurfaceRegionFilterBounds.SphereDotInterval(center,angle,new double3(0,1,0),out double ly,out double hy);
            SurfaceRegionFilterBounds.SphereDotInterval(center,angle,new double3(0,0,1),out double lz,out double hz);
            low=new double3(lx,ly,lz);high=new double3(hx,hy,hz);
        }
        static void IncludeRegionCap(SurfaceRegionProjection projection,double2 extent,ref double3 low,ref double3 high)
        {
            CubeSurface.TryNormalize(projection.AnchorDirection,out var anchor);
            // Projection admits Gram-matrix residuals <1e-10. This angular enclosure
            // includes that captured basis tolerance as well as double endpoint roundoff.
            double angle=math.min(Math.PI,Math.Atan(math.length(extent)/projection.Radius)+1e-8);
            CapAabb(anchor,angle,out var capLow,out var capHigh);low=math.min(low,capLow);high=math.max(high,capHigh);
        }
        bool RegionsMayIntersect(double3 center,double angle,bool filtered)
        {
            if(!hasGeometryRegions)return false;
            CapAabb(center,angle,out var low,out var high);
            var supportLow=filtered?renderRegionMinimum:canonicalRegionMinimum;
            var supportHigh=filtered?renderRegionMaximum:canonicalRegionMaximum;
            return !math.any(high<supportLow)&&!math.any(low>supportHigh);
        }

        // Unit directions assigned to a cube face have its positive dominant component >=1/sqrt(3).
        // Intersect that certificate with a Cartesian cap AABB before bounding the rational face UV.
        // Unlike a single-face corner estimate this includes the stitched halo across all seams.
        void CapBaseRange(double3 center,double angle,int maximumWork,ref int work,ref bool incomplete,
            out Range range,out Range rawRange,Func<bool> cancelled)
        {
            double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity,slope=0;
            double rawMinimum=double.PositiveInfinity,rawMaximum=double.NegativeInfinity,rawSlope=0;
            range=rawRange=default;
            double chord=2*Math.Sin(angle*.5),epsilon=128*2.22044604925031308085e-16;
            for(int face=0;face<6;face++)
            {
                if(!Spend(maximumWork,ref work,cancelled))
                {incomplete=true;CapFallback(out range,out rawRange);return;}
                double denom=face<2?center.x:face<4?center.y:center.z;if((face&1)!=0)denom=-denom;
                double dlo=math.max(1/Math.Sqrt(3)-epsilon,denom-chord-epsilon),dhi=math.min(1+epsilon,denom+chord+epsilon);
                if(dhi<dlo)continue;
                double a=face==0?-center.z:face==1?center.z:face==5?-center.x:center.x;
                double b=face==2?-center.z:face==3?center.z:center.y;
                RatioInterval(a,chord+epsilon,dlo,dhi,out double ax,out double bx);
                RatioInterval(b,chord+epsilon,dlo,dhi,out double ay,out double by);
                var faceLow=math.clamp((new double2(ax,ay)+1)*.5-epsilon,0,1);
                var faceHigh=math.clamp((new double2(bx,by)+1)*.5+epsilon,0,1);
                double covered=0;bool touched=false;
                for(int t=0;t<tiles.Length;t++)
                {
                    if(!Spend(maximumWork,ref work,cancelled))
                    {incomplete=true;CapFallback(out range,out rawRange);return;}
                    var tile=source.Tiles[t];if(tile.Key.Face!=face)continue;
                    double count=1L<<tile.Key.Level;var tileLow=new double2(tile.Key.X,tile.Key.Y)/count;
                    var low=math.max(faceLow,tileLow);var high=math.min(faceHigh,tileLow+1/count);
                    if(math.any(high<low))continue;
                    touched=true;covered+=(high.x-low.x)*(high.y-low.y);
                    var q=tiles[t].Query((low-tileLow)*count,(high-tileLow)*count,out _,maximumWork,ref work,ref incomplete,cancelled);
                    minimum=math.min(minimum,q.Min);maximum=math.max(maximum,q.Max);slope=math.max(slope,q.Slope*3);
                    if(raw!=null)
                    {
                        var full=raw[t].Query((low-tileLow)*count,(high-tileLow)*count,out _,maximumWork,ref work,ref incomplete,cancelled);
                        rawMinimum=math.min(rawMinimum,full.Min);rawMaximum=math.max(rawMaximum,full.Max);rawSlope=math.max(rawSlope,full.Slope*3);
                    }
                }
                if(!touched||covered<(faceHigh.x-faceLow.x)*(faceHigh.y-faceLow.y)*(1-1e-10))
                {incomplete=true;CapFallback(out range,out rawRange);return;}
            }
            if(!math.isfinite(minimum)||!math.isfinite(maximum))
            {incomplete=true;CapFallback(out range,out rawRange);return;}
            range=new Range(minimum,maximum,slope);rawRange=new Range(rawMinimum,rawMaximum,rawSlope);
        }
        void CapFallback(out Range range,out Range rawRange)
        {
            BaseFallback(out double minimum,out double maximum);range=new Range(minimum,maximum,double.PositiveInfinity);
            var recipe=source.StructuralField?.SourceRecipe??source.Recipe;
            rawRange=new Range(math.min(recipe.MinimumHeight,(double)(float)recipe.MinimumHeight),
                math.max(recipe.MaximumHeight,(double)(float)recipe.MaximumHeight),double.PositiveInfinity);
        }
        void BaseFallback(out double minimum,out double maximum)
        {
            // Source recipe bounds are an admission envelope, not the actual immutable
            // baked field. Preserve a globally constant base even after query work runs out.
            minimum=globalBaseMinimum;maximum=globalBaseMaximum;
        }
        static void RatioInterval(double value,double radius,double dlo,double dhi,out double minimum,out double maximum)
        {
            double a=(value-radius)/dlo,b=(value-radius)/dhi,c=(value+radius)/dlo,d=(value+radius)/dhi;
            minimum=math.min(math.min(a,b),math.min(c,d));maximum=math.max(math.max(a,b),math.max(c,d));
        }
        double RegionalBound(in NativeSurfaceView view,double3 center,double angle,SurfaceSamplingFootprint footprint,
            double priorMinimum,double priorMaximum,double intrinsicAmplitude,int maximumWork,ref int work,ref bool incomplete,
            out double canonicalGain,out double intrinsicGain,Func<bool> cancelled)
        {
            double error=0,suppressionError=0;canonicalGain=intrinsicGain=1;
            double initialMinimum=priorMinimum,initialMaximum=priorMaximum;
            for(int r=0;r<regions.Length;r++)
            {
                if(!Spend(maximumWork,ref work,cancelled))
                {incomplete=true;canonicalGain=intrinsicGain=1;return GlobalRegionalBound(view,initialMinimum,initialMaximum);}
                var region=view.Regions[r];if(!SurfaceRegionFilterBounds.HasGeometry(region))continue;
                bool full=SurfaceRegionFilterBounds.TryCanonicalDomain(region,center,angle,out var canonical);
                bool filtered=SurfaceRegionFilterBounds.TryDomain(region,center,angle,footprint,out var domain);
                if(!full&&!filtered)continue;
                var query=filtered?domain:canonical;var p=region.Projection;var extent=p.MaximumMetres-p.MinimumMetres;
                var b=regions[r].Query((query.Low-p.MinimumMetres)/extent,(query.High-p.MinimumMetres)/extent,out var mask,
                    maximumWork,ref work,ref incomplete,cancelled);
                if(work>=maximumWork&&incomplete)
                {canonicalGain=intrinsicGain=1;return GlobalRegionalBound(view,initialMinimum,initialMaximum);}
                var raw=new SurfaceRegionFilterBounds.Raw(b.Min,b.Max,b.Slope,mask.Min,mask.Max,mask.Slope);
                double amin=0,amax=0;
                if(full)SurfaceRegionFilterBounds.Coverage(region,canonical,raw,out amin,out amax,out _);
                var local=filtered?SurfaceRegionFilterBounds.Bound(region,domain,raw,priorMinimum,priorMaximum):
                    SurfaceRegionFilterBounds.Alias(region,priorMinimum,priorMaximum,amin);
                if(!math.isfinite(local.Height) || !math.isfinite(local.Gain))
                {incomplete=true;local=SurfaceRegionFilterBounds.Global(region,priorMinimum,priorMaximum);}
                if(region.Mode==SurfaceRegionMode.Replace)error=SurfaceRegionFilterBounds.Product(local.Gain,error)+local.Height;
                else
                {
                    error+=local.Height;
                    if(region.DetailPolicy==SurfaceDetailPolicy.Suppress)
                        error+=SurfaceRegionFilterBounds.Product(intrinsicAmplitude,local.Coverage);
                }
                if(region.DetailPolicy==SurfaceDetailPolicy.Suppress)
                    suppressionError=SurfaceRegionFilterBounds.Product(local.Gain,suppressionError)+local.Coverage;
                if(full)
                {
                    ApplyCanonical(region,b,amin,amax,ref priorMinimum,ref priorMaximum);
                    if(region.Mode==SurfaceRegionMode.Replace)canonicalGain*=1-amin;
                    if(region.Mode==SurfaceRegionMode.Delta&&region.DetailPolicy==SurfaceDetailPolicy.Suppress)
                    {priorMinimum-=amax*intrinsicAmplitude;priorMaximum+=amax*intrinsicAmplitude;}
                    if(region.Mode==SurfaceRegionMode.Replace||region.DetailPolicy==SurfaceDetailPolicy.Suppress)
                    {intrinsicGain*=1-amin;intrinsicAmplitude*=1-amin;}
                }
            }
            double detail=source.Detail.AmplitudeMetres*footprint.DetailWeight(source.Detail.WavelengthMetres);
            return SurfaceRegionFilterBounds.Outward(error+SurfaceRegionFilterBounds.Product(detail,suppressionError));
        }
        static void ApplyCanonical(SurfaceRegionHeader region,Range raw,double amin,double amax,ref double minimum,ref double maximum)
        {
            if(region.Mode==SurfaceRegionMode.Replace)
            {
                double lo=minimum,hi=maximum;
                minimum=math.min(math.lerp(lo,raw.Min,amin),math.lerp(lo,raw.Min,amax));
                maximum=math.max(math.lerp(hi,raw.Max,amin),math.lerp(hi,raw.Max,amax));
            }
            else
            {minimum+=math.min(amin*raw.Min,amax*raw.Min);maximum+=math.max(amin*raw.Max,amax*raw.Max);}
        }
        double GlobalRegionalBound(in NativeSurfaceView view,double minimum,double maximum)
        {
            return SurfaceRegionFilterBounds.Outward(globalRegionalConstant+
                globalRegionalCoefficient*math.max(math.abs(minimum),math.abs(maximum))+
                (source.Detail.AmplitudeMetres+(source.OrogenDetail?.MaximumAmplitude??0))*globalSuppression);
        }
        static bool LandformRange(in NativeSurfaceView view,double3 center,double angle,SurfaceSamplingFootprint footprint,
            int maximumWork,out Range range,out double attenuation,out double arithmeticJump,out int work,out bool incomplete,Func<bool> cancelled)
        {
            range=default;attenuation=0;arithmeticJump=0;work=0;incomplete=false;
            var full=view.StructuralField.LandformField;var filter=view.StructuralField.LandformFilter;
            // Metadata cannot monopolize a worker on a whole-planet root. Every skipped support
            // remains covered by the global envelope and explicit incomplete status.
            int budget=math.min(maximumWork,16384);
            var status=SurfaceLandformBounds.TryBound(full,filter,center,angle,SurfaceSamplingFootprint.Full,
                budget,out var canonical,out int used,cancelled);work+=used;
            if(status==SurfaceErrorStatus.MissingData)return false;
            incomplete=status!=SurfaceErrorStatus.Complete;
            // Spatial variation alone cannot bound the discontinuous last-bit roundoff of independent
            // PU calls. Positive vertex weights preserve their per-call reserve: point + interpolant,
            // with a second pair reserved for arithmetic in the normalized triangle-height expression.
            arithmeticJump=4*canonical.ArithmeticErrorMetres;
            bool parentAlias=footprint.Metres==0||(filter.Matches(full)&&(filter.MaximumLevel==0||
                2*footprint.Metres<=full.Radius/(2*(1<<filter.MaximumLevel))));
            if(parentAlias){range=new Range(canonical.MinimumHeight,canonical.MaximumHeight,canonical.MaximumSlope);return true;}
            bool fineAlias=filter.Matches(full)&&footprint.Metres<=full.Radius/(2*(1<<filter.MaximumLevel));
            var fine=canonical;
            if(!fineAlias)
            {
                status=SurfaceLandformBounds.TryBound(full,filter,center,angle,footprint,
                    budget-work,out fine,out used,cancelled);work+=used;
                if(status==SurfaceErrorStatus.MissingData)return false;incomplete|=status!=SurfaceErrorStatus.Complete;
            }
            status=SurfaceLandformBounds.TryBound(full,filter,center,angle,new SurfaceSamplingFootprint(footprint.Metres*2),
                budget-work,out var parent,out used,cancelled);work+=used;
            if(status==SurfaceErrorStatus.MissingData)return false;incomplete|=status!=SurfaceErrorStatus.Complete;
            // Bound canonical Full interpolation, then account for the independent Full-to-render
            // vertex loss below. A varying parent stitch blend cannot inherit a filtered-only slope.
            range=new Range(canonical.MinimumHeight,canonical.MaximumHeight,canonical.MaximumSlope);
            // The original and derived PU surfaces are distinct. Bound their difference locally;
            // never apply the old analytic-band attenuation to an already reconstructed macro.
            attenuation=math.max(fineAlias?0:canonical.MaximumDifference(fine),canonical.MaximumDifference(parent));
            return true;
        }
        bool HasCanonicalCover(SurfaceTileKey key)
        {
            double count=1L<<key.Level;var low=new double2(key.X,key.Y)/count;var high=low+1/count;double area=0;
            foreach(var tile in source.Tiles)
            {
                if(tile.Key.Face!=key.Face)continue;double tc=1L<<tile.Key.Level;var a=new double2(tile.Key.X,tile.Key.Y)/tc;
                var overlap=math.max(0,math.min(high,a+1/tc)-math.max(low,a));area+=overlap.x*overlap.y;
            }
            return area>=(1/count)*(1/count)*(1-1e-10);
        }
        void StructuralRange(double3 center,double angle,double chord,out Range macro,out Range ridge,out double slope,out double jump)
        {
            var field=source.StructuralField;var recipe=field.SourceRecipe;double radius=recipe.Radius,reach=angle*radius;
            if(field.LandformField!=null)
            {
                throw new InvalidOperationException("Landform intervals require the retained render view and bounded support query.");
            }
            if(field.DrainageField!=null)
            {StructuralRangeFour(center,reach,out macro,out slope);ridge=new Range(0,0,0);jump=2e-7*math.max(1,math.max(math.abs(recipe.MinimumHeight),math.abs(recipe.MaximumHeight)));return;}
            double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight),land=recipe.MaximumHeight-sea,ocean=sea-recipe.MinimumHeight;
            double mountain=math.min(1,field.MountainFraction/.3),amplitude=land*.1*mountain;
            double best=-2;
            for(int i=0;i<field.Provinces.Count;i++){double dot=math.dot(center,field.Provinces[i].Center);if(dot>best)best=dot;}
            bool canLand=false,canOcean=false;double numeratorMin=0,numeratorMax=0,denominatorMin=0,denominatorMax=0;
            for(int i=0;i<field.Provinces.Count;i++)
            {
                var p=field.Provinces[i];double dot=math.dot(center,p.Center);
                if(dot+chord>=best-chord){canLand|=p.Continental;canOcean|=!p.Continental;}
                double wlo=Math.Exp(-math.max(0,best-dot+2*chord)*radius/field.ShelfWidthMetres);
                double whi=Math.Exp(-math.max(0,best-dot-2*chord)*radius/field.ShelfWidthMetres);
                numeratorMin+=wlo*p.Buoyancy;numeratorMax+=whi*p.Buoyancy;denominatorMin+=wlo;denominatorMax+=whi;
            }
            double basinLo=math.max(0,numeratorMin/math.max(1e-300,denominatorMax)),basinHi=math.min(1,numeratorMax/math.max(1e-300,denominatorMin));
            double coastLo=field.ShelfWidthMetres*4+math.abs(field.CoastThresholdMetres),coastHi=coastLo;
            double upLo=0,upHi=0,riftLo=0,riftHi=0,weightLo=0,weightHi=0,weightSlope=0;
            foreach(var edge in field.Boundaries)
            {
                double distance=Distance(edge,center)*radius;
                double low=math.max(0,distance-reach),high=distance+reach;
                bool a=field.Provinces[edge.ProvinceA].Continental,b=field.Provinces[edge.ProvinceB].Continental;
                if(a!=b){coastLo=math.min(coastLo,low);coastHi=math.min(coastHi,high);}
                if(!a&&!b&&edge.Convergence<0){riftLo=math.max(riftLo,Envelope(high/field.BeltWidthMetres)*-edge.Convergence);riftHi=math.max(riftHi,Envelope(low/field.BeltWidthMetres)*-edge.Convergence);}
                if(edge.Convergence<=.08||(!a&&!b))continue;
                double strength=(edge.Convergence-.08)/.92,continental=a&&b?1:.75;
                upLo=math.max(upLo,Envelope(high/field.BeltWidthMetres)*strength*continental);upHi=math.max(upHi,Envelope(low/field.BeltWidthMetres)*strength*continental);
                weightLo+=Envelope(high/(field.BeltWidthMetres*1.8))*strength;weightHi+=Envelope(low/(field.BeltWidthMetres*1.8))*strength;
                if(low<field.BeltWidthMetres*5.4)weightSlope+=2.4*strength/(field.BeltWidthMetres*1.8);
            }
            double lowHeight=double.PositiveInfinity,highHeight=double.NegativeInfinity;
            if(canLand)
            {
                double cl=coastLo-field.CoastThresholdMetres,ch=coastHi-field.CoastThresholdMetres;
                LandOcean(cl,ch,basinLo,basinHi,upLo,upHi,riftLo,riftHi,sea,land,ocean,mountain,field.ShelfWidthMetres,ref lowHeight,ref highHeight);
            }
            if(canOcean)
                LandOcean(-coastHi-field.CoastThresholdMetres,-coastLo-field.CoastThresholdMetres,basinLo,basinHi,upLo,upHi,riftLo,riftHi,sea,land,ocean,mountain,field.ShelfWidthMetres,ref lowHeight,ref highHeight);
            // Portable polynomial roundoff is bounded independently of the support grid.
            double epsilon=1e-7*math.max(1,math.max(math.abs(recipe.MinimumHeight),math.abs(recipe.MaximumHeight)));
            macro=new Range(math.max(recipe.MinimumHeight,lowHeight-epsilon),math.min(recipe.MaximumHeight,highHeight+epsilon),0);
            double influence=math.min(1,weightHi/math.max(1,weightLo));
            double ridgeRoundoff=weightHi>0?epsilon:0;
            ridge=new Range(-1.15*amplitude*influence-ridgeRoundoff,.75*amplitude*influence+ridgeRoundoff,0);
            // Exp(-q^2) slope <1/width, min branch width=.12scale. The 80/scale bound also covers
            // tributary/trunk/foothill factors, belt coordinates and varying normalized edge weights.
            double ridgeSlope=amplitude*((80/field.RegionalFeatureScaleMetres)*weightHi+4*weightSlope)/math.max(1,weightLo);
            double macroSlope=(land+2*ocean)*(16/field.ShelfWidthMetres+4/field.BeltWidthMetres);
            slope=macroSlope+ridgeSlope;
            // Branch-window changes discard only Gaussian tails beyond |q|>=4.6. This covers their finite jump,
            // multiple crossed slots and portable-polynomial roundoff instead of claiming global smoothness.
            jump=amplitude*1e-7*field.Boundaries.Count*(1+4*reach/field.RegionalFeatureScaleMetres)+epsilon*2;
        }
        void StructuralRangeFour(double3 center,double reach,out Range macro,out double slope)
        {
            var f=source.StructuralField;var recipe=f.SourceRecipe;var drainage=f.DrainageField;
            double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight),land=recipe.MaximumHeight-sea,ocean=sea-recipe.MinimumHeight;
            double coast=ManagedCoast(drainage,center),cl=coast-reach,ch=coast+reach,ulo=0,uhi=0,rlo=0,rhi=0;
            foreach(var edge in f.Boundaries)
            {
                double distance=Distance(edge,center)*recipe.Radius,low=math.max(0,distance-reach),high=distance+reach;
                if(edge.Convergence>.08){double a=(edge.Convergence-.08)/.92;ulo=math.max(ulo,Envelope(high/f.BeltWidthMetres)*a);uhi=math.max(uhi,Envelope(low/f.BeltWidthMetres)*a);}
                if(edge.Convergence<-.08){rlo=math.max(rlo,Envelope(high/f.BeltWidthMetres)*-edge.Convergence);rhi=math.max(rhi,Envelope(low/f.BeltWidthMetres)*-edge.Convergence);}
            }
            double lo=double.PositiveInfinity,hi=double.NegativeInfinity,mountain=math.min(1,f.MountainFraction/.3),shelf=f.ShelfWidthMetres;
            double basin=0,weight=0;
            foreach(var p in f.Provinces){double w=SurfaceStructuralMath.ExpMinus(math.max(0,12*(1-math.dot(center,p.Center))));basin+=w*math.clamp((p.Buoyancy-.12)/.15,0,1);weight+=w;}
            basin/=weight;double blo=math.max(0,basin-24*reach/recipe.Radius),bhi=math.min(1,basin+24*reach/recipe.Radius);
            if(ch>=0)
            {
                double a=math.max(0,cl);
                lo=math.min(lo,sea+land*(.02+.10*blo)*Smooth(a/(2*shelf))+land*.78*mountain*ulo*Smooth(a/(.4*shelf)));
                hi=math.max(hi,sea+land*(.02+.10*bhi)*Smooth(ch/(2*shelf))+land*.78*mountain*uhi*Smooth(ch/(.4*shelf)));
            }
            if(cl<0)
            {
                double a=math.max(0,-ch),b=-cl;
                lo=math.min(lo,sea-ocean*(.06*Smooth(b/shelf)+.84*Smooth(b/(4*shelf)))+ocean*.18*rlo*Smooth(a/(4*shelf)));
                hi=math.max(hi,sea-ocean*(.06*Smooth(a/shelf)+.84*Smooth(a/(4*shelf)))+ocean*.18*rhi*Smooth(b/(4*shelf)));
            }
            double epsilon=1e-7*math.max(1,math.max(math.abs(recipe.MinimumHeight),math.abs(recipe.MaximumHeight)));
            macro=new Range(math.max(recipe.MinimumHeight,lo-epsilon),math.min(recipe.MaximumHeight,hi+epsilon),0);
            // Signed distance is 1-Lipschitz and max/min preserve that bound. The envelope's complete
            // compact tail has derivative <=2.4; smoothstep derivative <=1.5.
            slope=land*(.09/shelf+.10*24/recipe.Radius+.78*mountain*(3.75/shelf+2.4/f.BeltWidthMetres))+
                ocean*(.09/shelf+.315/shelf+.18*(.375/shelf+2.4/f.BeltWidthMetres));
        }
        static double ManagedCoast(SurfaceDrainageField field,double3 d)
        {
            var squared=new Unity.Collections.FixedList512Bytes<double>();double chord=2*SurfaceStructuralMath.Sin(field.CoastInfluenceMetres/(2*field.SourceRecipe.Radius));
            for(int i=0;i<field.Landmasses.Count;i++)squared.Add(chord*chord);
            int cursor=0;
            for(int step=0;step<=SurfaceDrainageField.MaximumIndexDepth;step++)
            {
                var node=field.CoastIndexNodeAt(cursor);
                if(!node.IsLeaf){cursor=d[node.Axis]<node.Split?node.Left:node.Right;continue;}
                for(int j=0;j<node.Count;j++)
                {
                    var segment=field.CoastSegments[field.CoastSpatialReferenceAt(node.First+j)];
                    if(math.any(d<segment.Minimum)||math.any(d>segment.Maximum))continue;
                    var a=field.CoastDirectionAt(segment.First);var b=field.CoastDirectionAt(segment.Last);var normal=math.normalize(math.cross(a,b));
                    var p=math.normalizesafe(d-normal*math.dot(d,normal),a);if(math.dot(p,a+b)<0)p=-p;
                    bool inside=math.dot(math.cross(a,p),normal)>=-1e-13&&math.dot(math.cross(p,b),normal)>=-1e-13;
                    var closest=inside?p:math.dot(d,a)>=math.dot(d,b)?a:b;
                    squared[segment.Landmass]=math.min(squared[segment.Landmass],math.lengthsq(d-closest));
                }
                break;
            }
            double best=-field.CoastInfluenceMetres;
            for(int m=0;m<field.Landmasses.Count;m++)
            {
                var mass=field.Landmasses[m];double facing=math.dot(d,mass.Center);bool inside=false;
                var p=facing>0?new double2(math.dot(d,mass.Right),math.dot(d,mass.Forward))*(field.SourceRecipe.Radius/facing):new double2(0);
                if(facing>0)for(int j=0;j<mass.VertexCount;j++)
                {var a=field.CoastVertices[mass.FirstVertex+j];var b=field.CoastVertices[mass.FirstVertex+(j+1)%mass.VertexCount];if((a.y>p.y)!=(b.y>p.y)&&p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;}
                double distance=squared[m]>=chord*chord?field.CoastInfluenceMetres:2*SurfaceStructuralMath.Asin(math.min(1,Math.Sqrt(squared[m])*.5))*field.SourceRecipe.Radius;
                best=math.max(best,inside?distance:-distance);
            }
            return best;
        }
        static void DrainageRange(SurfaceDrainageField field,double3 center,double chord,double maximum,
            SurfaceSamplingFootprint footprint,out double bed,out double slope,out double lost)
        {
            bed=double.PositiveInfinity;slope=lost=0;var stack=new Unity.Collections.FixedList512Bytes<int>();stack.Add(0);int work=0;
            while(stack.Length>0)
            {
                int cursor=stack[stack.Length-1];stack.RemoveAt(stack.Length-1);var node=field.IndexNodeAt(cursor);
                if(!node.IsLeaf)
                {if(center[node.Axis]-chord<=node.Split)stack.Add(node.Left);if(center[node.Axis]+chord>=node.Split)stack.Add(node.Right);continue;}
                for(int j=0;j<node.Count;j++)
                {
                    // A coarse cell may intersect much of the planet. Stop worker work explicitly and
                    // fall back to the admitted full field floor; this never declares support complete.
                    if(++work>8192){bed=field.SourceRecipe.MinimumHeight;slope=double.PositiveInfinity;lost=footprint.Metres>0?math.max(0,maximum-bed):0;return;}
                    var segment=field.Segments[field.SpatialReferenceAt(node.First+j)];
                    if(math.any(center+chord<segment.Minimum)||math.any(center-chord>segment.Maximum))continue;
                    var a=field.Nodes[segment.Parent];var b=field.Nodes[segment.Child];
                    double low=math.min(a.BedHeight,b.BedHeight),width=math.min(a.HillslopeWidth,b.HillslopeWidth),length=math.length(a.Direction-b.Direction)*field.SourceRecipe.Radius;
                    bed=math.min(bed,low);
                    double amplitude=math.max(0,maximum-low),widthSlope=2*math.abs(a.HillslopeWidth-b.HillslopeWidth)/length;
                    double along=2*(math.abs(a.BedHeight-b.BedHeight)+math.abs(a.DivideHeight-b.DivideHeight))/length;
                    slope=math.max(slope,along+6*amplitude/width*(1+2*widthSlope));
                    lost=math.max(lost,amplitude*(1-footprint.DetailWeight(width*4)));
                }
            }
        }
        static void LandOcean(double cl,double ch,double blo,double bhi,double ulo,double uhi,double rlo,double rhi,
            double sea,double land,double ocean,double mountain,double shelf,ref double lo,ref double hi)
        {
            if(ch>=0)
            { double a=math.max(0,cl);lo=math.min(lo,sea+land*blo*Smooth(a/shelf)+land*.72*mountain*ulo*Smooth(a/(shelf*.25)));
                hi=math.max(hi,sea+land*bhi*Smooth(ch/shelf)+land*.72*mountain*uhi*Smooth(ch/(shelf*.25))); }
            if(cl<0)
            { double a=math.max(0,-ch),b=-cl;lo=math.min(lo,sea-ocean*(.06*Smooth(b/shelf)+(.65+bhi)*Smooth(b/(shelf*4)))+ocean*.22*rlo*Smooth(a/(shelf*4)));
                hi=math.max(hi,sea-ocean*(.06*Smooth(a/shelf)+(.65+blo)*Smooth(a/(shelf*4)))+ocean*.22*rhi*Smooth(b/(shelf*4))); }
        }
        static double Product(double a,double b)=>a==0||b==0?0:a*b;
        static double Envelope(double d)=>d>=3?0:Math.Exp(-d*d)*(1-Smooth(d-2));
        static double Smooth(double t){t=math.clamp(t,0,1);return t*t*(3-2*t);}
        internal static double PatchAngle(SurfaceTileKey key,double3 center)
        {double angle=0;for(int y=0;y<2;y++)for(int x=0;x<2;x++){CubeSurface.TryDirection(key,new double2(x,y),out var d);angle=math.max(angle,Arc(center,d));}return angle;}
        static double Arc(double3 a,double3 b)=>math.atan2(math.length(math.cross(a,b)),math.dot(a,b));
        static double Distance(SurfaceGeologicalBoundary edge,double3 d)
        {
            double signed=math.dot(d,edge.Normal);var projected=d-edge.Normal*signed;
            if(math.lengthsq(projected)<1e-24)projected=edge.Start;else projected=math.normalize(projected);
            if(math.dot(projected,edge.Start+edge.End)<0)projected=-projected;
            bool inside=math.dot(math.cross(edge.Start,projected),edge.Normal)>=-1e-13&&math.dot(math.cross(projected,edge.End),edge.Normal)>=-1e-13;
            var closest=inside?projected:(math.dot(d,edge.Start)>=math.dot(d,edge.End)?edge.Start:edge.End);
            return Arc(d,closest);
        }
    }
}
