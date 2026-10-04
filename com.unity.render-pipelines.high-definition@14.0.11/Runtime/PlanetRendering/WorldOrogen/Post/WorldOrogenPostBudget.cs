// World Orogen GPU port admission; upstream cc2662b4, GPL-3.0-only.
using System;
namespace UnityEngine.Rendering.HighDefinition
{
    public readonly struct WorldOrogenWorkingEstimate
    {
        public readonly long RetainedGpuBytes,AdditionalGpuBytes,SourceManagedBytes,ManagedScratchReserveBytes;
        public long ProspectiveGpuBytes=>checked(RetainedGpuBytes+AdditionalGpuBytes);
        public long WorkingBytes=>checked(ProspectiveGpuBytes+SourceManagedBytes+ManagedScratchReserveBytes);
        internal WorldOrogenWorkingEstimate(long retained,long gpu,long source,long scratch)
        {RetainedGpuBytes=retained;AdditionalGpuBytes=gpu;SourceManagedBytes=source;ManagedScratchReserveBytes=scratch;}
    }
    /// <summary>Module allocations, not whole-engine RSS. Counts every retained graph/GPU field.
    /// Existing named output fields are deliberately double-counted as a conservative re-entry reserve.</summary>
    public static class WorldOrogenPostBudget
    {
        public const long DefaultMaximumWorkingBytes=4L*1024*1024*1024;
        public static WorldOrogenWorkingEstimate EstimateTerrain(WorldOrogenGpuState state)
        {
            if(state==null)throw new ArgumentNullException(nameof(state));long n=state.RegionCount;
            // Seventeen scalar banks, three 512-entry permutations, twelve FP64 parameters.
            // CPU: five retained arrays (20N), flood labels/stack/heap/keys/visited,
            // cloned Surface/Drain/order, both List capacity + output copies, stable-sort bookkeeping.
            return new WorldOrogenWorkingEstimate(state.EstimatedBytes,checked(n*68+3*512*4+96),state.Graph.Source.EstimatedBytes,checked(n*84+1024*1024));
        }
        public static WorldOrogenWorkingEstimate EstimateClimate(WorldOrogenGpuState state)
        {
            if(state==null)throw new ArgumentNullException(nameof(state));long n=state.RegionCount;
            // Actual distinct field strides: 428 bytes/region plus fixed bins/ITCZ/spline/parameters.
            // CPU arrays and geographic context are reserved separately; sparse occupancy rows
            // are admitted incrementally before insertion under the caller's remaining allowance.
            return new WorldOrogenWorkingEstimate(state.EstimatedBytes,checked(n*428+2593*4+144*40+64+4),state.Graph.Source.EstimatedBytes,checked(n*224+1024*1024));
        }
        internal static void Require(WorldOrogenWorkingEstimate estimate,long capacity)
        {
            if(capacity<=0||estimate.WorkingBytes>capacity)throw new InvalidOperationException("World Orogen stage exceeds the explicit working-memory admission: "+estimate.WorkingBytes+" > "+capacity+" bytes.");
            long device=SystemInfo.graphicsMemorySize>0?(long)SystemInfo.graphicsMemorySize*1024*1024:0;
            if(device>0&&estimate.ProspectiveGpuBytes>device*3/4)throw new InvalidOperationException("World Orogen GPU fields exceed the 75% device-memory admission.");
            long host=SystemInfo.systemMemorySize>0?(long)SystemInfo.systemMemorySize*1024*1024:0;
            if(host>0&&estimate.WorkingBytes>host/2)throw new InvalidOperationException("World Orogen module working set exceeds half of reported host memory.");
        }
    }
}
