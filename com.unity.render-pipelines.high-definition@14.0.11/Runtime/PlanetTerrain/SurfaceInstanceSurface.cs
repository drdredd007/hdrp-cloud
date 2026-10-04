using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct SurfaceInstanceStampRequest
    {
        public readonly SurfaceScatterPlanetId Instance;
        public readonly SurfaceContentHash ExpectedContentDigest;
        public readonly ulong ExpectedEpoch;
        public readonly SurfaceCraterStamp Stamp;
        public SurfaceInstanceStampRequest(SurfaceScatterPlanetId instance,SurfaceContentHash expectedContentDigest,ulong expectedEpoch,SurfaceCraterStamp stamp)
        {Instance=instance;ExpectedContentDigest=expectedContentDigest;ExpectedEpoch=expectedEpoch;Stamp=stamp;}
        public bool IsValid=>Instance.IsValid&&ExpectedContentDigest.IsValid&&ExpectedEpoch>0&&Stamp.IsValid;
    }
    [Serializable]
    public sealed class SurfaceInstancePreparationSettings
    {
        public double MaterialCellMetres=1;
        public int MaximumStampCount=256;
        public SurfaceMaterialRepairSettings Materials=new SurfaceMaterialRepairSettings();
        public SurfaceInstancePreparationSettings Clone()=>new SurfaceInstancePreparationSettings
        {MaterialCellMetres=MaterialCellMetres,MaximumStampCount=MaximumStampCount,Materials=Materials?.Clone()};
    }
    /// <summary>Prepared candidate only. No live frame, asset, renderer or physical registry is changed.</summary>
    public sealed class SurfaceInstancePreparationResult
    {
        public SurfaceScatterPlanetId Instance { get; }
        public SurfaceContentHash SourceContentDigest { get; }
        public SurfaceSnapshot Snapshot { get; }
        public bool Changed { get; }
        public double3 CenterDirection { get; }
        public double AffectedAngularRadius { get; }
        public long EvaluatedMaterialSamples { get; }
        public SurfaceMaterialRepairCost Cost { get; }
        /// <summary>Conservative complete signed bounds, including all bowls and rims; not estimated from sparse collider samples.</summary>
        public double MinimumHeight=>Snapshot.MinimumHeight;
        public double MaximumHeight=>Snapshot.MaximumHeight;
        internal SurfaceInstancePreparationResult(SurfaceScatterPlanetId instance,SurfaceContentHash source,SurfaceSnapshot snapshot,bool changed,
            double3 center,double affected,long evaluated,SurfaceMaterialRepairCost cost)
        {Instance=instance;SourceContentDigest=source;Snapshot=snapshot;Changed=changed;CenterDirection=center;AffectedAngularRadius=affected;EvaluatedMaterialSamples=evaluated;Cost=cost;}
    }
    public static class SurfaceInstanceSurface
    {
        /// <summary>Pure bounded preparation, suitable for a background worker. Host must compare SourceContentDigest
        /// again at its fixed-step commit and bind Instance to its actual planet entity. Erosion is inherited context.</summary>
        public static SurfaceInstancePreparationResult Prepare(SurfaceSnapshot source,SurfaceInstanceStampRequest request,
            SurfaceInstancePreparationSettings settings=null,Action<SurfaceBakeProgress> progress=null,Func<bool> cancelled=null)
        {
            if(source==null)throw new ArgumentNullException(nameof(source));
            if(!request.IsValid)throw new ArgumentException("Instance stamp requires a planet identity, expected source digest and valid command.");
            CheckCancelled(cancelled);
            if(!source.MaterialsReady)throw new InvalidOperationException("The current published surface must have complete final material data before preparing a deformation.");
            foreach(var command in source.Stamps)if(command.CompareTo(request.Stamp)==0)
            {
                if(!command.Equals(request.Stamp))throw new InvalidOperationException("An existing instance stamp ID cannot be reused for a different impact.");
                // Retrying an already committed command is harmless even if its optimistic source token is old.
                return new SurfaceInstancePreparationResult(request.Instance,source.ContentDigest,source,false,command.CenterDirection,0,0,default);
            }
            if(request.ExpectedContentDigest!=source.ContentDigest||request.ExpectedEpoch!=source.Revision.Epoch)
                throw new InvalidOperationException("The instance surface changed while this impact was being prepared; retry against its current published revision.");
            if(!source.HasAutomaticMaterials)throw new InvalidOperationException("Runtime deformation requires explicitly captured material rules. Migrate this legacy dataset from its Terrain Recipe before enabling impacts; existing baked weights cannot identify custom climate rules.");
            var captured=(settings??new SurfaceInstancePreparationSettings()).Clone();
            if(!math.isfinite(captured.MaterialCellMetres)||captured.MaterialCellMetres<=0||captured.MaximumStampCount<1||captured.MaximumStampCount>SurfaceSnapshotCodec.MaximumStamps||
                captured.Materials==null||captured.Materials.MaximumSamples<1||captured.Materials.MaximumWorkingBytes<1||captured.Materials.MaximumCompositionChecks<1)
                throw new ArgumentException("Instance preparation requires positive bounded work and metric sampling settings.");
            if(source.Stamps.Count>=captured.MaximumStampCount)throw new InvalidOperationException("This instance reached its explicit deformation command budget; its published surface remains unchanged.");
            var stamps=new List<SurfaceCraterStamp>(source.Stamps){request.Stamp};
            var revision=new SurfaceRevision(source.Revision.RecipeDigest,source.Revision.BaseDigest,checked(source.Revision.Epoch+1));
            // Discard only the stale derived cache. Source masks, authored order, erosion and base arrays remain immutable.
            var pending=new SurfaceSnapshot(source.Recipe,revision,source.CanonicalTileLevel,source.Resolution,source.Tiles,source.Detail,source.Regions,stamps,source.AutomaticMaterialProfile,structuralField:source.StructuralField,orogenDetail:source.OrogenDetail);
            var cost=SurfaceMaterialRepair.EstimateInstanceCost(pending,source,captured.MaterialCellMetres);
            var result=SurfaceMaterialRepair.RebuildInstance(pending,source,request.Stamp,captured.MaterialCellMetres,captured.Materials,progress,cancelled,out var evaluated);
            if(!result.MaterialsReady)throw new InvalidOperationException("A complete instance cache was not prepared; no candidate can be published.");
            double support=SurfaceStampMaterialData.NormalSupport(source.AutomaticMaterialProfile,source.Regions);
            double affected=math.min(Math.PI,(request.Stamp.RadiusMetres+request.Stamp.RimWidthMetres+support*2)/source.Recipe.Radius);
            CheckCancelled(cancelled);
            return new SurfaceInstancePreparationResult(request.Instance,source.ContentDigest,result,true,request.Stamp.CenterDirection,affected,evaluated,cost);
        }
        static void CheckCancelled(Func<bool> cancelled)
        {if(cancelled!=null&&cancelled())throw new OperationCanceledException("Instance preparation cancelled; the published surface is unchanged.");}
    }
}
