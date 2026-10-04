using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public enum SurfaceErrorStatus { Complete, SupportBudgetExceeded, MeasurementBudgetExceeded, MissingData, Pending }

    /// <summary>Measured signed-height residual, separately from spherical geometry and procedural bandwidth.</summary>
    public readonly struct SurfaceLodError
    {
        public readonly double MeasuredBakedMetres, UnmeasuredBakedMetres, CurvatureMetres;
        public readonly double ProceduralAttenuationMetres, ProceduralInterpolationMetres;
        public readonly double RegionalFilteringMetres, StructuralAttenuationMetres;
        public readonly int SampleCount;
        public readonly SurfaceErrorStatus Status;
        public readonly double MinimumRenderedHeightMetres, MaximumRenderedHeightMetres;
        public readonly bool HasRenderedHeightRange;
        public bool IsComplete => Status == SurfaceErrorStatus.Complete;
        // Pending is not a completed estimate. Missing authority must never become refinement evidence.
        public bool HasConservativeBound => Status != SurfaceErrorStatus.MissingData && Status != SurfaceErrorStatus.Pending &&
            math.isfinite(TotalMetres) && TotalMetres >= 0;
        public double TotalMetres => math.max(MeasuredBakedMetres, UnmeasuredBakedMetres) + CurvatureMetres +
            ProceduralAttenuationMetres + ProceduralInterpolationMetres + RegionalFilteringMetres + StructuralAttenuationMetres;
        internal SurfaceLodError(double measured, double unmeasured, double curvature, double attenuation,
            double interpolation, int samples, SurfaceErrorStatus status, double regionalFiltering = 0, double structuralAttenuation = 0)
            : this(measured,unmeasured,curvature,attenuation,interpolation,samples,status,regionalFiltering,structuralAttenuation,0,0,false) { }
        SurfaceLodError(double measured,double unmeasured,double curvature,double attenuation,double interpolation,
            int samples,SurfaceErrorStatus status,double regionalFiltering,double structuralAttenuation,
            double minimumRenderedHeight,double maximumRenderedHeight,bool hasRenderedHeightRange)
        { MeasuredBakedMetres = measured; UnmeasuredBakedMetres = unmeasured; CurvatureMetres = curvature;
            ProceduralAttenuationMetres = attenuation; ProceduralInterpolationMetres = interpolation; RegionalFilteringMetres = regionalFiltering; SampleCount = samples; Status = status; StructuralAttenuationMetres = structuralAttenuation;
            MinimumRenderedHeightMetres=minimumRenderedHeight;MaximumRenderedHeightMetres=maximumRenderedHeight;HasRenderedHeightRange=hasRenderedHeightRange; }
        public SurfaceLodError WithRegionalFiltering(double bound) => new SurfaceLodError(MeasuredBakedMetres, UnmeasuredBakedMetres,
            CurvatureMetres, ProceduralAttenuationMetres, ProceduralInterpolationMetres, SampleCount, Status, bound, StructuralAttenuationMetres,
            MinimumRenderedHeightMetres-math.max(0,bound-RegionalFilteringMetres),MaximumRenderedHeightMetres+math.max(0,bound-RegionalFilteringMetres),HasRenderedHeightRange);
        internal SurfaceLodError WithRenderedHeightRange(double minimum,double maximum) =>
            new SurfaceLodError(MeasuredBakedMetres,UnmeasuredBakedMetres,CurvatureMetres,ProceduralAttenuationMetres,
                ProceduralInterpolationMetres,SampleCount,Status,RegionalFilteringMetres,StructuralAttenuationMetres,
                minimum,maximum,HasConservativeBound&&math.isfinite(minimum)&&math.isfinite(maximum)&&minimum<=maximum);
    }

    public readonly struct SurfaceErrorBuildSettings
    {
        public readonly int MaximumSupportSamples;
        public readonly long MaximumPageMetadataBytes,MaximumPageSourceNodes;
        public SurfaceErrorBuildSettings(int maximumSupportSamples,long maximumPageMetadataBytes=16L*1024*1024,long maximumPageSourceNodes=16L*1024*1024)
        { MaximumSupportSamples=maximumSupportSamples;MaximumPageMetadataBytes=maximumPageMetadataBytes;MaximumPageSourceNodes=maximumPageSourceNodes; }
        public static SurfaceErrorBuildSettings Default => new SurfaceErrorBuildSettings(524288);
        public bool IsValid => MaximumSupportSamples >= 1 && MaximumSupportSamples <= 4194304 &&
            MaximumPageMetadataBytes>=4096 && MaximumPageMetadataBytes<=16L*1024*1024 && MaximumPageSourceNodes>=1 && MaximumPageSourceNodes<=16L*1024*1024;
    }

    /// <summary>
    /// Immutable content-addressed support hierarchy. Probes include canonical and regional vertices and cell centres,
    /// region feather lines and crater rings. Large support grids are represented by immutable pages rather than
    /// truncation. Per-patch finite measurements have separate continuous interval/variation and spherical-chord bounds.
    /// A caller must retain the matching native view while measuring a patch.
    /// </summary>
    public sealed class SurfaceErrorHierarchy
    {
        readonly Probe[] probes;
        readonly SurfaceSnapshot pagedSnapshot;
        readonly SurfacePatchHeightBounds pageBounds;
        public bool UsesPagedSupport => pagedSnapshot != null;
        public long RepresentedSupportSamples { get; }
        public bool PageMetadataBudgetExceeded => pageBounds != null && pageBounds.MetadataBudgetExceeded;
        public long PreparationNodes => probes.LongLength + (pageBounds?.PreparationNodes ?? 0);
        readonly double bakedRange, outerRadius, suppressionSlope;
        readonly SurfaceDetailRecipe detail;
        readonly bool missingData, boundsFirst;
        public SurfaceContentHash ContentDigest { get; }
        public bool SupportComplete { get; }
        public int SupportSampleCount => probes.Length;
        public long EstimatedBytes => (long)probes.Length * 32 + (pagedSnapshot == null ? 0 : 128L) + (boundsFirst ? 8L : 0) + (pageBounds?.EstimatedBytes ?? 0);
        readonly struct Probe
        {
            public readonly int Face;
            public readonly double2 Uv;
            public readonly double Height;
            public Probe(int face, double2 uv, double height) { Face = face; Uv = uv; Height = height; }
        }
        SurfaceErrorHierarchy(SurfaceSnapshot snapshot, Probe[] probes, bool complete, bool missing, bool paged = false, long represented = 0, Func<bool> cancelled = null, SurfaceErrorBuildSettings settings=default)
        {
            this.probes = probes; SupportComplete = complete; missingData = missing;
            boundsFirst = snapshot.Regions.Count > 0 || snapshot.OrogenDetail != null;
            pagedSnapshot = paged ? snapshot : null;
            // Regional filtering needs local raw support even when the finite probe bank fits.
            // Keep the probe/paged policy unchanged; the same worker-owned metadata covers both paths.
            pageBounds = paged || snapshot.Regions.Count > 0 || snapshot.OrogenDetail != null ? new SurfacePatchHeightBounds(snapshot,settings,cancelled) : null;
            RepresentedSupportSamples = represented == 0 ? probes.LongLength : represented;
            ContentDigest = snapshot.ContentDigest; detail = snapshot.Detail;
            bakedRange = snapshot.MaximumHeight - snapshot.MinimumHeight;
            outerRadius = snapshot.Recipe.Radius + math.max(math.abs(snapshot.MinimumHeight), math.abs(snapshot.MaximumHeight));
            if(pageBounds!=null){suppressionSlope=pageBounds.SuppressionSlope;return;}
            foreach(var region in snapshot.Regions)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                if(region.DetailPolicy!=SurfaceDetailPolicy.Suppress)continue;
                var extent=region.Projection.MaximumMetres-region.Projection.MinimumMetres;
                double dx=0,dy=0;int row=region.Resolution.x+1;
                for(int y=0;y<=region.Resolution.y;y++)
                {
                    if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                    for(int x=0;x<=region.Resolution.x;x++)
                    {
                    int i=y*row+x;
                    if(x<region.Resolution.x)dx=math.max(dx,math.abs(region.MaskAt(i+1)-region.MaskAt(i))*region.Resolution.x/extent.x);
                    if(y<region.Resolution.y)dy=math.max(dy,math.abs(region.MaskAt(i+row)-region.MaskAt(i))*region.Resolution.y/extent.y);
                }
                }
                double feather=region.BlendMetres>0?1.5/region.BlendMetres:0;
                var reach=math.max(math.abs(region.Projection.MinimumMetres),math.abs(region.Projection.MaximumMetres))/snapshot.Recipe.Radius;
                suppressionSlope+=(math.sqrt(dx*dx+dy*dy)+feather)*(1+math.lengthsq(reach));
            }
        }

        public static SurfaceErrorHierarchy Build(SurfaceSnapshot snapshot, in NativeSurfaceView view,
            SurfaceErrorBuildSettings settings, Func<bool> cancelled = null)
        {
            if (snapshot == null || snapshot.ContentDigest != view.ContentDigest || !settings.IsValid)
                throw new ArgumentException("Error support requires the same immutable snapshot and a bounded sample budget.");
            long expected=0;
            foreach(var tile in snapshot.Tiles)expected+=tile.SampleCount+(long)tile.Resolution*tile.Resolution;
            foreach(var region in snapshot.Regions)
                expected+=region.SampleCount+(long)region.Resolution.x*region.Resolution.y+
                    (region.BlendMetres>0?4L*(region.Resolution.x+region.Resolution.y+2):0);
            expected+=(long)snapshot.Stamps.Count*513;
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            // A page represents the entire immutable source grid; it is not a truncated array of sampled heights.
            // Large support and analytic structure are sampled only for a requested patch on a retained worker view.
            if ((expected > settings.MaximumSupportSamples && settings.MaximumSupportSamples >= SurfaceErrorBuildSettings.Default.MaximumSupportSamples) ||
                snapshot.StructuralField != null)
                return new SurfaceErrorHierarchy(snapshot, Array.Empty<Probe>(), true, false, true, expected, cancelled,settings);
            var list = new List<Probe>((int)Math.Min((long)settings.MaximumSupportSamples,expected));
            bool complete = true, missing = false;
            var native = view; // Local copy allows the preparation callback to borrow this view.
            var noDetail = new SurfaceSamplingFootprint(view.Detail.WavelengthMetres);
            Action<double3> add = direction =>
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                if (list.Count >= settings.MaximumSupportSamples) { complete = false; return; }
                if (!CubeSurface.TryLocate(direction, 0, out var key, out var uv) ||
                    SurfaceSampler.TrySampleHeight(native, direction, noDetail, out var height) != SurfaceSampleStatus.Ready)
                { missing = true; return; }
                list.Add(new Probe(key.Face, uv, height));
            };
            foreach (var tile in snapshot.Tiles)
            {
                int n = tile.Resolution;
                for (int y = 0; y <= n && complete; y++) for (int x = 0; x <= n && complete; x++)
                { CubeSurface.TrySampleDirection(tile.Key, n, x, y, out var direction); add(direction); }
                for (int y = 0; y < n && complete; y++) for (int x = 0; x < n && complete; x++)
                { CubeSurface.TryDirection(tile.Key, new double2(x + .5, y + .5) / n, out var direction); add(direction); }
            }
            foreach (var region in snapshot.Regions)
            {
                var min = region.Projection.MinimumMetres; var extent = region.Projection.MaximumMetres - min;
                for (int y = 0; y <= region.Resolution.y && complete; y++) for (int x = 0; x <= region.Resolution.x && complete; x++)
                { region.Projection.TryDirection(min + extent * (new double2(x, y) / (double2)region.Resolution), out var direction); add(direction); }
                for (int y = 0; y < region.Resolution.y && complete; y++) for (int x = 0; x < region.Resolution.x && complete; x++)
                { region.Projection.TryDirection(min + extent * (new double2(x + .5, y + .5) / (double2)region.Resolution), out var direction); add(direction); }
                // A narrow authored feather need not cross a source-grid centre. Sample its half/full width explicitly.
                if (region.BlendMetres > 0)
                    for (int edge = 0; edge < 4 && complete; edge++) for (int band = 1; band <= 2 && complete; band++)
                    {
                        int axis = edge & 1, along = 1 - axis, count = region.Resolution[along];
                        double inset = math.min(region.BlendMetres * band * .5, extent[axis] * .5);
                        for (int i = 0; i <= count && complete; i++)
                        {
                            var metres = min; metres[along] += extent[along] * i / count;
                            metres[axis] += edge < 2 ? inset : extent[axis] - inset;
                            region.Projection.TryDirection(metres, out var direction); add(direction);
                        }
                    }
            }
            foreach (var stamp in snapshot.Stamps)
            {
                var unit = stamp.CenterDirection;
                var tangent = math.normalize(math.cross(math.abs(unit.y) < .9 ? new double3(0, 1, 0) : new double3(1, 0, 0), unit));
                var bitangent = math.cross(unit, tangent); add(unit);
                for (int ring = 1; ring <= 16 && complete; ring++) for (int angle = 0; angle < 32 && complete; angle++)
                {
                    double arc = ring <= 8 ? stamp.RadiusMetres * ring / 8 : stamp.RadiusMetres + stamp.RimWidthMetres * (ring - 8) / 8;
                    double a = angle * (2 * Math.PI / 32), distance = arc / snapshot.Recipe.Radius;
                    add(unit * math.cos(distance) + (tangent * math.cos(a) + bitangent * math.sin(a)) * math.sin(distance));
                }
            }
            list.Sort((a, b) => { int n = a.Face.CompareTo(b.Face); if (n != 0) return n;
                n = a.Uv.x.CompareTo(b.Uv.x); return n != 0 ? n : a.Uv.y.CompareTo(b.Uv.y); });
            return new SurfaceErrorHierarchy(snapshot, list.ToArray(), complete, missing, cancelled:cancelled,settings:settings);
        }

        /// <summary>Finite metadata-only fallback while data/error preparation is pending; never implies measured accuracy.</summary>
        public static SurfaceLodError Conservative(in NativeSurfaceView view, SurfaceTileKey key, int meshResolution,
            SurfaceErrorStatus status = SurfaceErrorStatus.Pending)
        {
            if(!key.IsValid||!CubeSurface.ValidResolution(meshResolution)||!view.Recipe.IsValid)
                throw new ArgumentException("Invalid conservative error request.");
            double outer=view.Recipe.Radius+math.max(math.abs(view.MinimumHeight),math.abs(view.MaximumHeight));
            double curvature=ConservativeCurvature(outer,key,meshResolution);
            // Snapshot min/max already include the whole procedural/stamp range. Keep that amplitude in
            // UnmeasuredBakedMetres here; splitting components requires the prepared support metadata.
            return new SurfaceLodError(0,view.MaximumHeight-view.MinimumHeight,curvature,0,0,0,status);
        }

        /// <summary>32-cell far topology uses the b-c diagonal. Work counts all actual sampler calls and probe comparisons.</summary>
        public SurfaceLodError Measure(in NativeSurfaceView view, SurfaceTileKey key, int meshResolution,
            SurfaceSamplingFootprint footprint, int maximumWork, out int work, Func<bool> cancelled = null)
        {
            if (view.ContentDigest != ContentDigest || !key.IsValid || !CubeSurface.ValidResolution(meshResolution) ||
                !footprint.IsValid || maximumWork < 0) throw new ArgumentException("Invalid or incompatible error measurement.");
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            // A source page can be arbitrarily denser than a rendered patch. Metadata bounds cover the
            // omitted support; one request never becomes a multimillion-query analytic solve.
            // Regional raw certificates need a share of this same work budget before probes can
            // consume it. An exhausted probe bank must not erase useful local support metadata.
            int metadataWork=0; bool metadataIncomplete=false, metadataReady=true;
            double metadataResidual=0,metadataStructural=0,regional=0,minimumHeight=0,maximumHeight=0;
            if(boundsFirst)
                metadataReady=pageBounds.TryBound(view,key,meshResolution,footprint,maximumWork,
                    out metadataWork,out metadataIncomplete,out metadataResidual,out metadataStructural,out minimumHeight,out maximumHeight,out regional,cancelled);
            if(metadataWork<0||metadataWork>maximumWork)throw new InvalidOperationException("Support metadata exceeded its declared work budget.");
            int remainingWork=maximumWork-metadataWork;
            int measurementLimit=UsesPagedSupport?math.min(remainingWork,16384):remainingWork;
            var evaluator = new Evaluator(view, key, meshResolution, measurementLimit, cancelled);
            double count = 1L << key.Level;
            var min = new double2(key.X, key.Y) / count; var max = min + 1 / count;
            if (UsesPagedSupport) MeasurePages(view,key,evaluator,cancelled);
            int first = LowerBound(key.Face, min.x);
            for (int i = first; i < probes.Length; i++)
            {
                var p = probes[i]; if (p.Face != key.Face || p.Uv.x > max.x) break;
                if (p.Uv.y < min.y || p.Uv.y > max.y) continue;
                if (!evaluator.Compare((p.Uv - min) * count, p.Height)) break;
            }
            // Fine render patches can fall between all source-grid vertices. Cell centres measure the actual bilinear
            // field/triangle mismatch there too, rather than reporting an empty probe set as zero terrain error.
            for (int y = 0; y < meshResolution && !evaluator.Exhausted; y++)
                for (int x = 0; x < meshResolution && !evaluator.Exhausted; x++)
                    evaluator.Compare(new double2(x + .5, y + .5) / meshResolution);
            work = evaluator.Work + metadataWork;
            var status = evaluator.Missing || missingData ? SurfaceErrorStatus.MissingData :
                evaluator.Exhausted ? SurfaceErrorStatus.MeasurementBudgetExceeded :
                !SupportComplete ? SurfaceErrorStatus.SupportBudgetExceeded : SurfaceErrorStatus.Complete;
            // Includes two-cell stitched edges. The normalized cube chord is bounded before computing sagitta.
            double curvature = ConservativeCurvature(outerRadius,key,meshResolution);
            double w = footprint.DetailWeight(detail.WavelengthMetres);
            // Stitched bands also contain parent-footprint vertices. The parent loses at least
            // as much procedural detail as the fine band, while fine w bounds interpolation.
            double parentWeight = new SurfaceSamplingFootprint(footprint.Metres * 2).DetailWeight(detail.WavelengthMetres);
            double attenuation = detail.AmplitudeMetres * (1 - parentWeight);
            // Quintic value-noise derivative <= 1.875, corner range <= 2A, three axes and triangle diameter.
            double interpolation = detail.AmplitudeMetres == 0 ? 0 : w * math.min(2 * detail.AmplitudeMetres,
                detail.AmplitudeMetres * (4 * Math.Sqrt(2) * outerRadius / (count * meshResolution)) *
                (12 / detail.WavelengthMetres + 2 * suppressionSlope));
            // Finite probes do not bound between-node analytic features. Retain their published full interval
            // until a tighter analytic interval is available; this is deliberately conservative, never zero.
            double residual = boundsFirst ? metadataResidual : status == SurfaceErrorStatus.Complete ? 0 : bakedRange;
            double structural = boundsFirst ? metadataStructural : StructuralAttenuation(view,footprint);
            if (pageBounds != null)
            {
                bool boundIncomplete=metadataIncomplete;
                if(!boundsFirst)
                {
                    metadataReady=pageBounds.TryBound(view,key,meshResolution,footprint,maximumWork-work,out int boundWork,out boundIncomplete,
                        out residual,out structural,out minimumHeight,out maximumHeight,out regional,cancelled);
                    work+=boundWork;
                }
                if(!metadataReady){status=SurfaceErrorStatus.MissingData;residual=bakedRange;}
                // Paged measurements never substitute a finite probe maximum for their between-support bound.
                // Metadata budget fallback remains finite but explicitly incomplete.
                if (pageBounds.MetadataBudgetExceeded && status==SurfaceErrorStatus.Complete) status=SurfaceErrorStatus.SupportBudgetExceeded;
                if(boundIncomplete&&status==SurfaceErrorStatus.Complete)status=SurfaceErrorStatus.MeasurementBudgetExceeded;
            }
            var result=new SurfaceLodError(evaluator.Maximum, residual,
                curvature, attenuation, interpolation, evaluator.Comparisons, status,
                regionalFiltering: regional, structuralAttenuation: structural);
            if(pageBounds!=null&&metadataReady)
            {
                // The raw certificate encloses canonical points. Both fine and parent filtering
                // losses widen it before using it for actual rendered vertices. Convex stitched
                // triangles can sag inward by the separately certified two-cell curvature.
                double expansion=detail.AmplitudeMetres+structural+regional;
                double roundoff=64*2.22044604925031308085e-16*
                    (view.Recipe.Radius+math.max(math.abs(minimumHeight),math.abs(maximumHeight))+expansion+curvature);
                result=result.WithRenderedHeightRange(minimumHeight-expansion-curvature-roundoff,maximumHeight+expansion+roundoff);
            }
            return result;
        }

        static double ConservativeCurvature(double outer,SurfaceTileKey key,int n)
        {
            // Across a stitched cell the unnormalised cube diagonal is <=4*sqrt(2)/(count*n).
            // Norm >=1 gives angle <=2*asin(2*sqrt(2)/(count*n)); 1-cos(angle)=2*sin(angle/2)^2.
            double halfChord=math.min(1,2*Math.Sqrt(2)/((1L<<key.Level)*(double)n));
            return outer*2*halfChord*halfChord;
        }
        static double StructuralAttenuation(in NativeSurfaceView view,SurfaceSamplingFootprint footprint)
        {
            if (!view.StructuralField.Enabled || footprint.Metres==0) return 0;
            var field=view.StructuralField;var recipe=field.SourceRecipe;
            double rawMin=math.min(recipe.MinimumHeight,(double)(float)recipe.MinimumHeight);
            double rawMax=math.max(recipe.MaximumHeight,(double)(float)recipe.MaximumHeight);
            double correction=math.max(recipe.MaximumHeight-rawMin,rawMax-recipe.MinimumHeight);
            double sea=math.clamp(recipe.SeaLevel,recipe.MinimumHeight,recipe.MaximumHeight);
            double ridges=(recipe.MaximumHeight-sea)*.1*math.min(1,field.MountainFraction/.3)*1.15;
            return correction*(1-footprint.DetailWeight(math.min(field.ShelfWidthMetres,field.BeltWidthMetres)))+
                ridges*(1-footprint.DetailWeight(field.FeatureScaleMetres*.2));
        }
        void MeasurePages(in NativeSurfaceView view,SurfaceTileKey key,Evaluator evaluator,Func<bool> cancelled)
        {
            double count=1L<<key.Level;double2 patchMin=new double2(key.X,key.Y)/count,patchMax=patchMin+1/count;
            var native=view;
            Action<double3> compare=direction=>
            {
                if(!evaluator.Spend())return;
                if(CubeSurface.TryLocate(direction,0,out var address,out var uv)&&address.Face==key.Face&&
                    math.all(uv>=patchMin-1e-14)&&math.all(uv<=patchMax+1e-14))
                    evaluator.Compare(math.clamp((uv-patchMin)*count,0,1));
            };
            foreach(var tile in pagedSnapshot.Tiles)
            {
                if(evaluator.Exhausted||!evaluator.Spend())break;if(tile.Key.Face!=key.Face)continue;
                double sourceCount=1L<<tile.Key.Level;var tileMin=new double2(tile.Key.X,tile.Key.Y)/sourceCount;
                var low=math.max(patchMin,tileMin);var high=math.min(patchMax,tileMin+1/sourceCount);
                if(math.any(high<low))continue;
                var first=(int2)math.max(0,math.floor((low-tileMin)*sourceCount*tile.Resolution)-1);
                var last=(int2)math.min(tile.Resolution,math.ceil((high-tileMin)*sourceCount*tile.Resolution)+1);
                for(int y=first.y;y<=last.y&&!evaluator.Exhausted;y++)for(int x=first.x;x<=last.x&&!evaluator.Exhausted;x++)
                {CubeSurface.TrySampleDirection(tile.Key,tile.Resolution,x,y,out var direction);compare(direction);}
                for(int y=first.y;y<last.y&&!evaluator.Exhausted;y++)for(int x=first.x;x<last.x&&!evaluator.Exhausted;x++)
                {CubeSurface.TryDirection(tile.Key,new double2(x+.5,y+.5)/tile.Resolution,out var direction);compare(direction);}
            }
            foreach(var region in pagedSnapshot.Regions)
            {
                if(evaluator.Exhausted||!evaluator.Spend())break;
                if(!RegionRange(region.Projection,key,out var minimum,out var maximum))continue;
                var extent=region.Projection.MaximumMetres-region.Projection.MinimumMetres;
                var first=(int2)math.max(0,math.floor((minimum-region.Projection.MinimumMetres)/extent*region.Resolution)-1);
                var last=(int2)math.min(region.Resolution,math.ceil((maximum-region.Projection.MinimumMetres)/extent*region.Resolution)+1);
                for(int y=first.y;y<=last.y&&!evaluator.Exhausted;y++)for(int x=first.x;x<=last.x&&!evaluator.Exhausted;x++)
                {region.Projection.TryDirection(region.Projection.MinimumMetres+extent*new double2(x,y)/region.Resolution,out var direction);compare(direction);}
                for(int y=first.y;y<last.y&&!evaluator.Exhausted;y++)for(int x=first.x;x<last.x&&!evaluator.Exhausted;x++)
                {region.Projection.TryDirection(region.Projection.MinimumMetres+extent*new double2(x+.5,y+.5)/region.Resolution,out var direction);compare(direction);}
                if(region.BlendMetres>0)
                    for(int edge=0;edge<4&&!evaluator.Exhausted;edge++)for(int band=1;band<=2&&!evaluator.Exhausted;band++)
                    {
                        int axis=edge&1,along=1-axis;
                        double inset=math.min(region.BlendMetres*band*.5,extent[axis]*.5);
                        for(int i=first[along];i<=last[along]&&!evaluator.Exhausted;i++)
                        {
                            var metres=region.Projection.MinimumMetres;metres[along]+=extent[along]*i/region.Resolution[along];
                            metres[axis]+=edge<2?inset:extent[axis]-inset;region.Projection.TryDirection(metres,out var direction);compare(direction);
                        }
                    }
            }
            foreach(var stamp in pagedSnapshot.Stamps)
            {
                if(evaluator.Exhausted||!evaluator.Spend())break;
                double3 unit=stamp.CenterDirection,tangent=math.normalize(math.cross(math.abs(unit.y)<.9?new double3(0,1,0):new double3(1,0,0),unit));
                var bitangent=math.cross(unit,tangent);compare(unit);
                for(int ring=1;ring<=16&&!evaluator.Exhausted;ring++)for(int angle=0;angle<32&&!evaluator.Exhausted;angle++)
                {
                    double arc=ring<=8?stamp.RadiusMetres*ring/8:stamp.RadiusMetres+stamp.RimWidthMetres*(ring-8)/8;
                    double a=angle*(2*Math.PI/32),distance=arc/native.Recipe.Radius;
                    compare(unit*math.cos(distance)+(tangent*math.cos(a)+bitangent*math.sin(a))*math.sin(distance));
                }
            }
            if(cancelled!=null&&cancelled())throw new OperationCanceledException();
        }
        internal static bool RegionRange(SurfaceRegionProjection region,SurfaceTileKey key,out double2 minimum,out double2 maximum)
        {
            minimum=new double2(double.PositiveInfinity);maximum=new double2(double.NegativeInfinity);
            CubeSurface.TryDirection(key,new double2(.5),out var patchCenter);
            region.TryDirection((region.MinimumMetres+region.MaximumMetres)*.5,out var regionCenter);
            double patchAngle=0;
            for(int cornerY=0;cornerY<2;cornerY++)for(int cornerX=0;cornerX<2;cornerX++)
            {
                CubeSurface.TryDirection(key,new double2(cornerX,cornerY),out var corner);
                patchAngle=math.max(patchAngle,math.atan2(math.length(math.cross(patchCenter,corner)),math.dot(patchCenter,corner)));
            }
            // Unit directions of a convex cube rectangle stay in its positive corner cone.
            double regionHalfChord=math.min(1,math.length((region.MaximumMetres-region.MinimumMetres)*.5)/region.Radius);
            double regionAngle=2*math.asin(regionHalfChord);
            double separation=math.atan2(math.length(math.cross(patchCenter,regionCenter)),math.dot(patchCenter,regionCenter));
            if(separation>patchAngle+regionAngle+1e-13)return false;
            bool allPositive=true,anyPositive=false;
            for(int y=0;y<2;y++)for(int x=0;x<2;x++)
            {
                CubeSurface.TryDirection(key,new double2(x,y),out var direction);double denominator=math.dot(direction,region.AnchorDirection);
                allPositive&=denominator>0;anyPositive|=denominator>0;
                if(denominator>0)
                {
                    var metres=new double2(math.dot(direction,region.Right),math.dot(direction,region.Forward))*(region.Radius/denominator);
                    minimum=math.min(minimum,metres);maximum=math.max(maximum,metres);
                }
            }
            if(!anyPositive)return false;
            // Ratios of linear cube coordinates attain extrema at corners while denominator stays positive.
            // A horizon crossing uses the whole page rather than guessing a finite rectangle.
            if(!allPositive){minimum=region.MinimumMetres;maximum=region.MaximumMetres;return true;}
            double padding=region.Radius*1.4210854715202004e-14;
            minimum=math.max(region.MinimumMetres,minimum-padding);maximum=math.min(region.MaximumMetres,maximum+padding);
            return math.all(maximum>=minimum);
        }

        internal static bool RegionContains(SurfaceRegionProjection region,SurfaceTileKey key)
        {
            for(int y=0;y<2;y++)for(int x=0;x<2;x++)
            {
                CubeSurface.TryDirection(key,new double2(x,y),out var direction);
                if(!region.TryProject(direction,out var metres)||math.any(metres<region.MinimumMetres)||math.any(metres>region.MaximumMetres))return false;
            }
            // The gnomonic coordinate ratio is linear-fractional over the cube rectangle; corner containment
            // is sufficient only on the positive projection hemisphere, established by TryProject above.
            return true;
        }

        int LowerBound(int face, double x)
        {
            int low = 0, high = probes.Length;
            while (low < high) { int mid = low + (high - low) / 2; var p = probes[mid];
                if (p.Face < face || (p.Face == face && p.Uv.x < x)) low = mid + 1; else high = mid; }
            return low;
        }
        sealed class Evaluator
        {
            readonly NativeSurfaceView view;
            readonly SurfaceTileKey key;
            readonly int n, budget;
            readonly SurfaceSamplingFootprint noDetail;
            readonly Dictionary<int, double> vertices = new Dictionary<int, double>();
            public int Work, Comparisons;
            public double Maximum;
            public bool Exhausted, Missing;
            readonly Func<bool> cancelled;
            public Evaluator(NativeSurfaceView view, SurfaceTileKey key, int n, int budget, Func<bool> cancelled)
            { this.view = view; this.key = key; this.n = n; this.budget = budget; this.cancelled = cancelled; noDetail = new SurfaceSamplingFootprint(view.Detail.WavelengthMetres); }
            public bool Spend() { if (cancelled != null && cancelled()) throw new OperationCanceledException(); if (Work >= budget) { Exhausted = true; return false; } Work++; return true; }
            bool Height(double2 uv, out double height)
            {
                height = 0; if (!Spend()) return false;
                if (!CubeSurface.TryDirection(key, uv, out var dir) ||
                    SurfaceSampler.TrySampleHeight(view, dir, noDetail, out height) != SurfaceSampleStatus.Ready)
                { Missing = true; return false; } return true;
            }
            bool Vertex(int x, int y, out double height)
            {
                int index = y * (n + 1) + x;
                if (vertices.TryGetValue(index, out height)) return true;
                if (!Height(new double2(x, y) / n, out height)) return false; vertices.Add(index, height); return true;
            }
            public bool Compare(double2 uv) { return Height(uv, out var height) && Compare(uv, height); }
            public bool Compare(double2 uv, double height)
            {
                if (!Spend()) return false;
                var grid = math.clamp(uv, 0, 1) * n;
                int x = (int)math.min(n - 1, math.floor(grid.x)), y = (int)math.min(n - 1, math.floor(grid.y));
                var f = grid - new double2(x, y);
                double aHeight,bHeight,cHeight,aWeight,bWeight,cWeight;
                double2 aUv,bUv,cUv;
                if (f.x + f.y <= 1)
                { if (!Vertex(x, y, out aHeight) || !Vertex(x + 1, y, out bHeight) || !Vertex(x, y + 1, out cHeight)) return false;
                    aWeight=1-f.x-f.y;bWeight=f.x;cWeight=f.y;
                    aUv=new double2(x,y)/n;bUv=new double2(x+1,y)/n;cUv=new double2(x,y+1)/n; }
                else
                { if (!Vertex(x+1,y+1,out aHeight)||!Vertex(x+1,y,out bHeight)||!Vertex(x,y+1,out cHeight))return false;
                    aWeight=f.x+f.y-1;bWeight=1-f.y;cWeight=1-f.x;
                    aUv=new double2(x+1,y+1)/n;bUv=new double2(x+1,y)/n;cUv=new double2(x,y+1)/n; }
                // A normalized cube mesh is not affine in face UV. Intersect the radial ray with the actual
                // triangle plane. Subtract its zero-height sphere intersection algebraically, without losing
                // metre residuals through subtraction of two planetary-radius positions.
                double r=view.Recipe.Radius;
                double la=aWeight*CubeLength(aUv),lb=bWeight*CubeLength(bUv),lc=cWeight*CubeLength(cUv);
                double qa=r/(r+aHeight),qb=r/(r+bHeight),qc=r/(r+cHeight);
                double inverse=la*qa+lb*qb+lc*qc;
                double difference=la*aHeight*qa+lb*bHeight*qb+lc*cHeight*qc;
                double coarse=CubeLength(uv)*(difference/((la+lb+lc)*inverse));
                if(!math.isfinite(coarse)){Missing=true;return false;}
                Maximum = math.max(Maximum, math.abs(height - coarse)); Comparisons++; return true;
            }
            double CubeLength(double2 uv)
            { var ab=2*(new double2(key.X,key.Y)+uv)/(1L<<key.Level)-1;return math.sqrt(1+math.lengthsq(ab)); }
        }
    }
}
