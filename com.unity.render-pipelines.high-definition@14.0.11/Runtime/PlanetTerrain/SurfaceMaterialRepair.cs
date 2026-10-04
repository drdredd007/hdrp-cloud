using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    [Serializable]
    public sealed class SurfaceMaterialRepairSettings
    {
        public int MaximumSamples = 2 * 1024 * 1024;
        public long MaximumWorkingBytes = 512L * 1024 * 1024;
        public long MaximumCompositionChecks = 128L * 1024 * 1024;
        public SurfaceMaterialRepairSettings Clone() => (SurfaceMaterialRepairSettings)MemberwiseClone();
    }
    public readonly struct SurfaceMaterialRepairCost
    {
        public readonly long Samples, EstimatedWorkingBytes, ResolvedBytes, CompositionChecks;
        internal SurfaceMaterialRepairCost(long samples,long bytes,long resolved,long checks)
        {Samples=samples;EstimatedWorkingBytes=bytes;ResolvedBytes=resolved;CompositionChecks=checks;}
    }

    /// <summary>Offline immutable repair. Final height/normal rules first, explicit source masks once afterwards.</summary>
    public static class SurfaceMaterialRepair
    {
        /// <summary>Allocation-free in sample count; used before a caller allocates a coarse snapshot or output arrays.</summary>
        public static SurfaceMaterialRepairCost EstimateCost(IReadOnlyList<SurfaceTileData> tiles,IReadOnlyList<SurfaceRegionData> regions,SurfaceAutomaticMaterialProfile profile)
        {
            if(tiles==null||regions==null||!profile.IsValid)throw new ArgumentException("Material repair estimate requires resident grids and captured rules.");
            long sourceCount=0,count=0;
            foreach(var tile in tiles)sourceCount=checked(sourceCount+tile.SampleCount);
            count=sourceCount;
            foreach(var region in regions)
            {
                sourceCount=checked(sourceCount+region.SampleCount);
                SurfaceResolvedMaterials.RequiredLayerLayout(profile,region,out _,out var size,out _);
                count=checked(count+((long)size.x+1)*(size.y+1));
            }
            return new SurfaceMaterialRepairCost(count,checked(sourceCount*160+count*96+1024*1024),
                checked(count*16+(tiles.Count+regions.Count)*256L+256),checked(count*(8L*(regions.Count+1)*(regions.Count+1)+1)));
        }
        public static SurfaceSnapshot MigrateFromRecipe(SurfaceSnapshot source, SurfaceAutomaticMaterialProfile profile)
        {
            if (source == null || !profile.IsValid) throw new ArgumentException("Explicit recipe-backed migration requires a valid captured material profile.");
            if (source.HasAutomaticMaterials) throw new InvalidOperationException("This dataset already has a captured automatic material profile.");
            var tiles = new List<SurfaceTileData>(); var regions = new List<SurfaceRegionData>();
            foreach(var tile in source.Tiles) tiles.Add(new SurfaceTileData(tile.Key,tile.Resolution,tile.CopyHeights(),tile.CopyMaterialWeights(),tile.CopyErosionData(),tile.MeasuredLodErrorMetres,
                tile.MaterialProvenance == SurfaceMaterialProvenance.LegacyBaked ? SurfaceMaterialProvenance.Automatic : tile.MaterialProvenance));
            foreach(var region in source.Regions) regions.Add(CopyRegion(region,region.MaterialProvenance == SurfaceMaterialProvenance.LegacyBaked ?
                (region.Kind == SurfaceRegionKind.Authored && region.HasMaterialWeights ? SurfaceMaterialProvenance.Authored : SurfaceMaterialProvenance.Automatic) : region.MaterialProvenance));
            return new SurfaceSnapshot(source.Recipe,new SurfaceRevision(source.Revision.RecipeDigest,source.Revision.BaseDigest,checked(source.Revision.Epoch+1)),source.CanonicalTileLevel,source.Resolution,
                tiles,source.Detail,regions,source.Stamps,profile,structuralField:source.StructuralField,orogenDetail:source.OrogenDetail);
        }
        static SurfaceRegionData CopyRegion(SurfaceRegionData region,SurfaceMaterialProvenance provenance) => new SurfaceRegionData(region.Projection,region.Resolution,
            region.CopyHeights(),region.CopyBlendMask(),region.BlendMetres,region.Mode,region.BaseDigest,region.DetailPolicy,region.Priority,region.CopyMaterialWeights(),region.Kind,
            region.SourceContentDigest,region.ContextMetres,region.CopyErosionData(),provenance,region.AutomaticMaterialProfile);

        public static SurfaceSnapshot Rebuild(SurfaceSnapshot source, SurfaceMaterialRepairSettings settings = null,
            Action<SurfaceBakeProgress> progress = null, Func<bool> cancelled = null)
        {
            if(source == null)throw new ArgumentNullException(nameof(source));
            if(!source.HasAutomaticMaterials)throw new InvalidOperationException("Automatic mask repair requires the original captured material profile. Explicitly migrate this legacy dataset from its Terrain Recipe; custom climate rules cannot be inferred from baked weights.");
            CheckCancelled(cancelled); if(source.MaterialsReady)return source;
            if(source.Stamps.Count != 0)throw new InvalidOperationException("Runtime crater mask repair requires an instance-specific publication plan; authoring repair cannot publish instance stamps.");
            var captured=(settings??new SurfaceMaterialRepairSettings()).Clone();
            if(captured.MaximumSamples < 1 || captured.MaximumWorkingBytes < 1 || captured.MaximumCompositionChecks < 1)throw new ArgumentException("Material repair requires positive work and memory budgets.");
            var projections=new List<SurfaceRegionProjection>(); var resolutions=new List<int2>(); var guards=new List<double>();
            var cost=EstimateCost(source.Tiles,source.Regions,source.AutomaticMaterialProfile);long count=cost.Samples;
            if(source.Regions.Count>0 && source.Regions[source.Regions.Count-1].Priority>int.MaxValue-source.Regions.Count)
                throw new InvalidOperationException("Reserve the highest priorities for resolved material overlays before publishing.");
            foreach(var region in source.Regions)
            {
                SurfaceResolvedMaterials.RequiredLayerLayout(source.AutomaticMaterialProfile,region,out var projection,out var resolution,out var guard);
                projections.Add(projection);resolutions.Add(resolution); guards.Add(guard);
            }
            // Native source, cloned resolved output/hash encoding and temporary sample buffers; bounded before any NativeArray allocation.
            long bytes=checked(cost.EstimatedWorkingBytes+(source.OrogenDetail?.EstimatedResidentBytes??0));
            // Each active custom rule can sample its own four-point normal through all height layers.
            long checks=cost.CompositionChecks;
            if(count>captured.MaximumSamples || bytes>captured.MaximumWorkingBytes || checks>captured.MaximumCompositionChecks)throw new InvalidOperationException($"Automatic material repair requires {count} samples, {checks} composition checks and about {bytes} bytes; split the region or explicitly raise its offline budgets.");
            if(count>int.MaxValue)throw new InvalidOperationException("Material repair work exceeds its progress counter.");
            var tiles=new List<SurfaceMaterialTileData>(); var layers=new List<SurfaceMaterialLayerData>(); int completed=0;
            // This preparation view contains source attrs, not the previous resolved cache.
            using(var native=source.CreateNative(Allocator.Persistent))
            {
                var view=native.View;
                foreach(var tile in source.Tiles)
                {
                    var weights=new float4[tile.SampleCount];
                    for(int y=0;y<=tile.Resolution;y++)for(int x=0;x<=tile.Resolution;x++)
                    {
                        Work(progress,cancelled,completed++,(int)count); CubeSurface.TrySampleDirection(tile.Key,tile.Resolution,x,y,out var direction);
                        weights[y*(tile.Resolution+1)+x]=Evaluate(source,view,direction);
                    }
                    tiles.Add(new SurfaceMaterialTileData(tile.Key,tile.Resolution,weights));
                }
                for(int i=0;i<source.Regions.Count;i++)
                {
                    var p=projections[i]; var resolution=resolutions[i]; var weights=new float4[(resolution.x+1)*(resolution.y+1)];
                    for(int y=0;y<=resolution.y;y++)for(int x=0;x<=resolution.x;x++)
                    {
                        Work(progress,cancelled,completed++,(int)count); p.TryDirection(math.lerp(p.MinimumMetres,p.MaximumMetres,new double2((double)x/resolution.x,(double)y/resolution.y)),out var direction);
                        weights[y*(resolution.x+1)+x]=Evaluate(source,view,direction);
                    }
                    layers.Add(new SurfaceMaterialLayerData(p,resolution,guards[i],source.Regions[i].Priority,weights));
                }
            }
            CheckCancelled(cancelled); var resolved=new SurfaceResolvedMaterials(source.GeometryDigest,source.MaterialRulesDigest,tiles,layers);
            var result=new SurfaceSnapshot(source.Recipe,source.Revision,source.CanonicalTileLevel,source.Resolution,source.Tiles,source.Detail,source.Regions,source.Stamps,source.AutomaticMaterialProfile,resolved,source.StructuralField,source.OrogenDetail);
            progress?.Invoke(new SurfaceBakeProgress("Automatic material repair",completed,(int)count)); CheckCancelled(cancelled);return result;
        }
        internal static SurfaceMaterialRepairCost EstimateInstanceCost(SurfaceSnapshot source,SurfaceSnapshot previous,double cellMetres)
        {
            if (source.StructuralField != null) return new SurfaceMaterialRepairCost(0,0,0,0);
            var baseline=EstimateCost(source.Tiles,source.Regions,source.AutomaticMaterialProfile);long stampSamples=0,retained=0;
            foreach(var stamp in source.Stamps)
            {
                SurfaceStampMaterialData.RequiredLayout(source.Recipe,source.AutomaticMaterialProfile,source.Regions,stamp,cellMetres,out _,out var size,out _);
                stampSamples=checked(stampSamples+((long)size.x+1)*(size.y+1));
            }
            if(previous.ResolvedMaterials!=null)
            {
                foreach(var tile in previous.ResolvedMaterials.Tiles)retained=checked(retained+tile.SampleCount*16L);
                foreach(var layer in previous.ResolvedMaterials.Layers)retained=checked(retained+layer.SampleCount*16L);
                foreach(var layer in previous.ResolvedMaterials.StampLayers)retained=checked(retained+layer.SampleCount*16L);
            }
            long count=checked(baseline.Samples+stampSamples);
            return new SurfaceMaterialRepairCost(count,checked(baseline.EstimatedWorkingBytes+stampSamples*96+retained+source.Stamps.Count*256L+(source.OrogenDetail?.EstimatedResidentBytes??0)),
                checked(baseline.ResolvedBytes+stampSamples*16+source.Stamps.Count*256L),checked(count*(8L*(source.Regions.Count+1)*(source.Regions.Count+source.Stamps.Count+1)+1)));
        }
        internal static SurfaceSnapshot RebuildInstance(SurfaceSnapshot source,SurfaceSnapshot previous,SurfaceCraterStamp changed,
            double cellMetres,SurfaceMaterialRepairSettings settings,Action<SurfaceBakeProgress> progress,Func<bool> cancelled,out long evaluated)
        {
            CheckCancelled(cancelled);evaluated=0;
            if (source.StructuralField != null) return source;
            var cost=EstimateInstanceCost(source,previous,cellMetres);
            if(cost.Samples>settings.MaximumSamples||cost.EstimatedWorkingBytes>settings.MaximumWorkingBytes||cost.CompositionChecks>settings.MaximumCompositionChecks||cost.Samples>int.MaxValue)
                throw new InvalidOperationException($"Instance material preparation requires {cost.Samples} samples, {cost.CompositionChecks} composition checks and about {cost.EstimatedWorkingBytes} bytes; the published instance remains unchanged.");
            int derivedCount=checked(source.Regions.Count+source.Stamps.Count);
            if(source.Regions.Count>0&&source.Regions[source.Regions.Count-1].Priority>int.MaxValue-derivedCount)
                throw new InvalidOperationException("Reserve priorities for the complete instance material cache.");
            var tiles=new List<SurfaceMaterialTileData>();var layers=new List<SurfaceMaterialLayerData>();var stamps=new List<SurfaceStampMaterialData>();int completed=0;
            double normalSupport=SurfaceStampMaterialData.NormalSupport(source.AutomaticMaterialProfile,source.Regions);
            // Source height/erosion only; previous final weights never feed back into authored-mask evaluation.
            using(var native=source.CreateNative(Allocator.Persistent))
            {
                var view=native.View;
                for(int i=0;i<source.Tiles.Count;i++)
                {
                    var tile=source.Tiles[i];var old=previous.ResolvedMaterials?.Tiles[i];var weights=old?.CopyWeights()??new float4[tile.SampleCount];
                    for(int y=0;y<=tile.Resolution;y++)for(int x=0;x<=tile.Resolution;x++)
                    {
                        Work(progress,cancelled,completed++,(int)cost.Samples);CubeSurface.TrySampleDirection(tile.Key,tile.Resolution,x,y,out var direction);
                        if(old==null||Affected(direction,changed,source.Recipe.Radius,normalSupport))
                        {weights[y*(tile.Resolution+1)+x]=Evaluate(source,view,direction);evaluated++;}
                    }
                    tiles.Add(new SurfaceMaterialTileData(tile.Key,tile.Resolution,weights));
                }
                for(int i=0;i<source.Regions.Count;i++)
                {
                    SurfaceResolvedMaterials.RequiredLayerLayout(source.AutomaticMaterialProfile,source.Regions[i],out var p,out var size,out var blend);
                    var old=previous.ResolvedMaterials?.Layers[i];var weights=old?.CopyWeights()??new float4[(size.x+1)*(size.y+1)];
                    for(int y=0;y<=size.y;y++)for(int x=0;x<=size.x;x++)
                    {
                        Work(progress,cancelled,completed++,(int)cost.Samples);p.TryDirection(math.lerp(p.MinimumMetres,p.MaximumMetres,new double2((double)x/size.x,(double)y/size.y)),out var direction);
                        if(old==null||Affected(direction,changed,source.Recipe.Radius,normalSupport))
                        {weights[y*(size.x+1)+x]=Evaluate(source,view,direction);evaluated++;}
                    }
                    layers.Add(new SurfaceMaterialLayerData(p,size,blend,source.Regions[i].Priority,weights));
                }
                foreach(var stamp in source.Stamps)
                {
                    SurfaceStampMaterialData.RequiredLayout(source.Recipe,source.AutomaticMaterialProfile,source.Regions,stamp,cellMetres,out var p,out var size,out var blend);
                    SurfaceStampMaterialData old=null;
                    if(previous.ResolvedMaterials!=null)foreach(var layer in previous.ResolvedMaterials.StampLayers)
                        if(layer.IdHigh==stamp.IdHigh&&layer.IdLow==stamp.IdLow&&layer.CellMetres==cellMetres){old=layer;break;}
                    // A tangent square's corners have greater angular reach than the radial stamp itself.
                    double reach=source.Recipe.Radius*math.atan(math.length(p.MaximumMetres)/source.Recipe.Radius);
                    double changedReach=changed.RadiusMetres+changed.RimWidthMetres+normalSupport*2;
                    bool touches=SurfaceScatterExclusionSampler.ArcMetres(stamp.CenterDirection,changed.CenterDirection,source.Recipe.Radius)<=reach+changedReach;
                    if(old!=null&&!touches){stamps.Add(old);completed=checked(completed+old.SampleCount);continue;}
                    var weights=new float4[(size.x+1)*(size.y+1)];
                    for(int y=0;y<=size.y;y++)for(int x=0;x<=size.x;x++)
                    {
                        Work(progress,cancelled,completed++,(int)cost.Samples);p.TryDirection(math.lerp(p.MinimumMetres,p.MaximumMetres,new double2((double)x/size.x,(double)y/size.y)),out var direction);
                        weights[y*(size.x+1)+x]=Evaluate(source,view,direction);evaluated++;
                    }
                    stamps.Add(new SurfaceStampMaterialData(stamp.IdHigh,stamp.IdLow,cellMetres,p,size,blend,weights));
                }
            }
            CheckCancelled(cancelled);var resolved=new SurfaceResolvedMaterials(source.GeometryDigest,source.MaterialRulesDigest,tiles,layers,stamps);
            var result=new SurfaceSnapshot(source.Recipe,source.Revision,source.CanonicalTileLevel,source.Resolution,source.Tiles,source.Detail,source.Regions,source.Stamps,source.AutomaticMaterialProfile,resolved,source.StructuralField,source.OrogenDetail);
            progress?.Invoke(new SurfaceBakeProgress("Instance material preparation",completed,(int)cost.Samples));CheckCancelled(cancelled);return result;
        }
        static bool Affected(double3 direction,SurfaceCraterStamp stamp,double radius,double normalSupport)
        {
            double angle=(stamp.RadiusMetres+stamp.RimWidthMetres+normalSupport*2)/radius;
            return angle>=Math.PI||math.lengthsq(direction-stamp.CenterDirection)<=4*math.pow(math.sin(angle*.5),2);
        }
        static float4 Evaluate(SurfaceSnapshot source,in NativeSurfaceView view,double3 direction)
        {
            var status=SurfaceSampler.TrySampleHeight(view,direction,out var height);
            if(status!=SurfaceSampleStatus.Ready)throw new InvalidDataException("Final height is unavailable during automatic material repair: "+status);
            var profile=source.AutomaticMaterialProfile;
            double slope=Slope(view,direction,profile.NormalSampleMetres);
            status=SurfaceSampler.TrySampleAttributes(view,direction,out var attributes,SurfaceChannels.ErosionData);
            if(status!=SurfaceSampleStatus.Ready && status!=SurfaceSampleStatus.NotReady)throw new InvalidDataException("Inherited erosion context is incompatible: "+status);
            // Absent historical moisture is explicit dry context; it is not synthesized hydraulic flow.
            double wetness=status==SurfaceSampleStatus.Ready?attributes.ErosionData.y:0;
            float4 weights=profile.Evaluate(source.Recipe,direction,height,slope,wetness);
            foreach(var region in source.Regions)if(region.AutomaticMaterialProfile.IsValid && RegionWeight(region,direction,out _,out var weight))
                weights=math.lerp(weights,region.AutomaticMaterialProfile.Evaluate(source.Recipe,direction,height,
                    region.AutomaticMaterialProfile.NormalSampleMetres==profile.NormalSampleMetres?slope:Slope(view,direction,region.AutomaticMaterialProfile.NormalSampleMetres),wetness),(float)weight);
            if(CubeSurface.TryLocate(direction,source.CanonicalTileLevel,out var key,out var tileUv))
                foreach(var tile in source.Tiles)if(tile.Key.Equals(key))
                { if(tile.HasMaterialWeights && tile.MaterialProvenance==SurfaceMaterialProvenance.Authored)weights=Bilinear(tile,tileUv);break; }
            // Explicit masks override automatic rules in their original source priority/blend order, once.
            foreach(var region in source.Regions)if(region.HasMaterialWeights && region.MaterialProvenance==SurfaceMaterialProvenance.Authored && RegionWeight(region,direction,out var uv,out var weight))
                weights=math.lerp(weights,Bilinear(region,uv),(float)weight);
            return weights/math.csum(weights);
        }
        static double Slope(in NativeSurfaceView view,double3 direction,double step)
        {
            var status=SurfaceSampler.TrySampleNormal(view,direction,step,out var normal);
            if(status!=SurfaceSampleStatus.Ready)throw new InvalidDataException("Final normal/context is unavailable during automatic material repair: "+status);
            double cosine=math.clamp(math.dot(normal,direction),1e-12,1);return math.sqrt(math.max(0,1-cosine*cosine))/cosine;
        }
        static bool RegionWeight(SurfaceRegionData region,double3 direction,out double2 uv,out double weight)
        {
            uv=default;weight=0;var p=region.Projection;
            if(!p.TryProject(direction,out var metres)||math.any(metres<p.MinimumMetres)||math.any(metres>p.MaximumMetres))return false;
            uv=(metres-p.MinimumMetres)/(p.MaximumMetres-p.MinimumMetres); var grid=uv*(double2)region.Resolution;
            int2 cell=(int2)math.min((double2)(region.Resolution-1),math.floor(grid)); var f=grid-(double2)cell;int row=region.Resolution.x+1,i=cell.y*row+cell.x;
            weight=math.lerp(math.lerp(region.MaskAt(i),region.MaskAt(i+1),f.x),math.lerp(region.MaskAt(i+row),region.MaskAt(i+row+1),f.x),f.y);
            if(region.BlendMetres>0){double t=math.clamp(math.cmin(math.min(metres-p.MinimumMetres,p.MaximumMetres-metres))/region.BlendMetres,0,1);weight*=t*t*(3-2*t);}
            return weight>0;
        }
        static float4 Bilinear(SurfaceTileData tile,double2 uv)
        {
            var grid=uv*tile.Resolution;int2 cell=(int2)math.min(tile.Resolution-1,math.floor(grid));var f=(float2)(grid-(double2)cell);int row=tile.Resolution+1,i=cell.y*row+cell.x;
            return math.lerp(math.lerp(tile.MaterialWeightsAt(i),tile.MaterialWeightsAt(i+1),f.x),math.lerp(tile.MaterialWeightsAt(i+row),tile.MaterialWeightsAt(i+row+1),f.x),f.y);
        }
        static float4 Bilinear(SurfaceRegionData region,double2 uv)
        {
            var grid=uv*(double2)region.Resolution;int2 cell=(int2)math.min((double2)(region.Resolution-1),math.floor(grid));var f=(float2)(grid-(double2)cell);int row=region.Resolution.x+1,i=cell.y*row+cell.x;
            return math.lerp(math.lerp(region.MaterialWeightsAt(i),region.MaterialWeightsAt(i+1),f.x),math.lerp(region.MaterialWeightsAt(i+row),region.MaterialWeightsAt(i+row+1),f.x),f.y);
        }
        static void Work(Action<SurfaceBakeProgress> progress,Func<bool> cancelled,int completed,int total)
        {if((completed&255)!=0)return;CheckCancelled(cancelled);progress?.Invoke(new SurfaceBakeProgress("Automatic material repair",completed,total));CheckCancelled(cancelled);}
        static void CheckCancelled(Func<bool> cancelled){if(cancelled!=null&&cancelled())throw new OperationCanceledException("Material repair cancelled before publication.");}
    }
}
