using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Derived final weights around one instance stamp. This is not an authored height region.</summary>
    public sealed class SurfaceStampMaterialData
    {
        readonly float4[] weights;
        public ulong IdHigh { get; }
        public ulong IdLow { get; }
        public double CellMetres { get; }
        public SurfaceRegionProjection Projection { get; }
        public int2 Resolution { get; }
        public double BlendMetres { get; }
        public int SampleCount => weights.Length;
        public SurfaceStampMaterialData(ulong idHigh,ulong idLow,double cellMetres,SurfaceRegionProjection projection,
            int2 resolution,double blendMetres,float4[] weights)
        {
            if((idHigh|idLow)==0 || !math.isfinite(cellMetres) || cellMetres<=0 || !projection.IsValid ||
                math.any(resolution<2) || math.any(resolution>4096) || !math.isfinite(blendMetres) || blendMetres<=0)
                throw new ArgumentException("Invalid stamp material grid.");
            IdHigh=idHigh;IdLow=idLow;CellMetres=cellMetres;Projection=projection;Resolution=resolution;BlendMetres=blendMetres;
            this.weights=SurfaceDataValidation.Attributes(weights,checked((resolution.x+1)*(resolution.y+1)),true)??throw new ArgumentNullException(nameof(weights));
        }
        public float4 WeightAt(int index)=>weights[index];
        public float4[] CopyWeights()=>(float4[])weights.Clone();
        internal int CompareId(SurfaceStampMaterialData other)
        {int c=IdHigh.CompareTo(other.IdHigh);return c!=0?c:IdLow.CompareTo(other.IdLow);}
        internal void Write(BinaryWriter writer)
        {
            writer.Write(IdHigh);writer.Write(IdLow);writer.Write(CellMetres);SurfaceHashing.WriteProjection(writer,Projection);
            writer.Write(Resolution.x);writer.Write(Resolution.y);writer.Write(BlendMetres);SurfaceHashing.WriteAttributes(writer,weights);
        }
        internal static double NormalSupport(SurfaceAutomaticMaterialProfile profile,IReadOnlyList<SurfaceRegionData> regions)
        {
            double support=profile.NormalSampleMetres;
            foreach(var region in regions)if(region.AutomaticMaterialProfile.IsValid)support=math.max(support,region.AutomaticMaterialProfile.NormalSampleMetres);
            return support;
        }
        internal static void RequiredLayout(SurfaceRecipe recipe,SurfaceAutomaticMaterialProfile profile,IReadOnlyList<SurfaceRegionData> regions,
            SurfaceCraterStamp stamp,double cellMetres,out SurfaceRegionProjection projection,out int2 resolution,out double blend)
        {
            if(!recipe.IsValid || !profile.IsValid || regions==null || !stamp.IsValid || !math.isfinite(cellMetres) || cellMetres<=0)
                throw new ArgumentException("Stamp materials require captured rules and a positive metric cell size.");
            // Preserve at least eight cells across the bowl; a narrow nonzero rim needs four cells across it.
            double pitch=math.min(cellMetres,stamp.RadiusMetres*.25);
            if(stamp.RimHeightMetres>0)pitch=math.min(pitch,stamp.RimWidthMetres*.25);
            double support=stamp.RadiusMetres+stamp.RimWidthMetres;
            double halo=math.max(NormalSupport(profile,regions)*2,pitch*2);
            double angle=(support+halo)/recipe.Radius;
            if(!math.isfinite(angle) || angle>=Math.PI*.25)throw new ArgumentException("Stamp support and normal halo exceed a bounded tangent patch; split the event.");
            double inner=recipe.Radius*math.tan(support/recipe.Radius),outer=recipe.Radius*math.tan(angle);
            double cells=math.ceil((2*outer)/pitch);
            if(!math.isfinite(cells)||cells>4096)throw new InvalidOperationException("Stamp material patch exceeds 4096 cells; reduce the event or explicitly use a coarser supported profile.");
            int n=math.max(2,(int)cells);if((n&1)!=0)n++;resolution=new int2(n);
            var up=stamp.CenterDirection;var reference=math.abs(up.y)<.9?new double3(0,1,0):new double3(1,0,0);
            var forward=math.normalize(reference-up*math.dot(reference,up));var right=math.cross(up,forward);
            projection=new SurfaceRegionProjection(up,right,forward,recipe.Radius,new double2(-outer),new double2(outer));blend=outer-inner;
            if(!projection.IsValid || !math.isfinite(blend) || blend<=0)throw new ArgumentException("Stamp material patch cannot be represented at the requested metric precision.");
        }
        internal bool Matches(SurfaceRecipe recipe,SurfaceAutomaticMaterialProfile profile,IReadOnlyList<SurfaceRegionData> regions,SurfaceCraterStamp stamp)
        {
            if(IdHigh!=stamp.IdHigh||IdLow!=stamp.IdLow)return false;
            RequiredLayout(recipe,profile,regions,stamp,CellMetres,out var p,out var size,out var blend);
            return math.all(size==Resolution)&&blend==BlendMetres&&Projection.Radius==p.Radius&&
                math.all(Projection.AnchorDirection==p.AnchorDirection)&&math.all(Projection.Right==p.Right)&&math.all(Projection.Forward==p.Forward)&&
                math.all(Projection.MinimumMetres==p.MinimumMetres)&&math.all(Projection.MaximumMetres==p.MaximumMetres);
        }
    }
}
