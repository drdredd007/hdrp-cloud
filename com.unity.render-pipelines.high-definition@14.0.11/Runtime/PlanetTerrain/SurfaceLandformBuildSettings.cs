using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Captured algorithm-five resolution and reconstruction policy; no renderer LOD affects this graph.</summary>
    [Serializable]
    public sealed class SurfaceLandformBuildSettings
    {
        public double HighlandSpacingMetres=4000, FoothillSpacingMetres=16000, PlainSpacingMetres=128000;
        public double HighlandUpliftThreshold=.3, FoothillUpliftThreshold=.05;
        public int MaximumCells=SurfaceLandformField.MaximumCells;
        public long MaximumWorkingBytes=512L*1024*1024;
        public bool Validate(SurfaceRecipe recipe,out string error)
        {
            error=null;
            if(!recipe.IsValid||recipe.AlgorithmVersion!=5)error="Landform generation requires algorithm five.";
            else if(!Positive(HighlandSpacingMetres)||!Positive(FoothillSpacingMetres)||!Positive(PlainSpacingMetres)||
                HighlandSpacingMetres>FoothillSpacingMetres||FoothillSpacingMetres>PlainSpacingMetres||
                HighlandUpliftThreshold<=FoothillUpliftThreshold||HighlandUpliftThreshold>=1||FoothillUpliftThreshold<=0||
                !math.isfinite(HighlandUpliftThreshold)||!math.isfinite(FoothillUpliftThreshold))error="Landform spacings and uplift strata must be finite and ordered.";
            else if(MaximumCells<6||MaximumCells>SurfaceLandformField.MaximumCells||MaximumWorkingBytes<=0)error="Invalid explicit landform admission caps.";
            else if(2*recipe.Radius/HighlandSpacingMetres>(1<<12))error="Requested highland metric spacing exceeds the portable maximum adaptive level twelve.";
            return error==null;
        }
        public SurfaceLandformBuildSettings Clone()=>(SurfaceLandformBuildSettings)MemberwiseClone();
        internal void Write(System.IO.BinaryWriter writer)
        {
            writer.Write(SurfaceLandformBuilder.GeneratorRevision);writer.Write(HighlandSpacingMetres);writer.Write(FoothillSpacingMetres);writer.Write(PlainSpacingMetres);
            writer.Write(HighlandUpliftThreshold);writer.Write(FoothillUpliftThreshold);
            // Admission caps affect support/rejection, never geometry identity of an admitted graph.
        }
        static bool Positive(double v)=>math.isfinite(v)&&v>0;
    }
    public readonly struct SurfaceLandformBuildDiagnostics
    {
        public readonly int Cells,Controls,SharedDivides,Channels,HighestLevel;
        /// <summary>Point classifications, not whole-support ocean certificates.</summary>
        public readonly int OceanChannels,OceanGuardedMidpoints,MacroIslandMidpoints;
        public readonly long EstimatedWorkingBytes,ResidentBytes;
        public readonly double MaximumChannelRise,MaximumSharedProminence;
        internal SurfaceLandformBuildDiagnostics(SurfaceLandformField field,int level,long working,double rise,double prominence,int oceanChannels,int oceanMidpoints,int macroIslandMidpoints)
        {Cells=field.Cells.Count;Controls=field.Controls.Count;SharedDivides=field.Divides.Count;Channels=field.Channels.Count;HighestLevel=level;EstimatedWorkingBytes=working;ResidentBytes=field.EstimatedResidentBytes;MaximumChannelRise=rise;MaximumSharedProminence=prominence;OceanChannels=oceanChannels;OceanGuardedMidpoints=oceanMidpoints;MacroIslandMidpoints=macroIslandMidpoints;}
    }
}
