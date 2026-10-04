using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct NativeWorldOrogenDetailView
    {
        public readonly int Version,Resolution;
        public readonly WorldOrogenDetailRecipe Recipe;
        public readonly double Radius,SeaLevel,MaximumAmplitude;
        public readonly SurfaceContentHash SourceBaseDigest,ContentDigest;
        public readonly NativeArray<float4>.ReadOnly Geometry,Flow,Environment;
        public readonly NativeArray<WorldOrogenDetailRangeLevel>.ReadOnly RangeLevels;
        public readonly NativeArray<float2>.ReadOnly Ranges;
        internal NativeWorldOrogenDetailView(WorldOrogenDetailField f,NativeArray<float4> geometry,NativeArray<float4> flow,NativeArray<float4> environment,
            NativeArray<WorldOrogenDetailRangeLevel> levels,NativeArray<float2> ranges)
        {
            Version=f?.Version??0;Resolution=f?.Resolution??0;Recipe=f?.Recipe??WorldOrogenDetailRecipe.Disabled;Radius=f?.Radius??0;SeaLevel=f?.SeaLevel??0;
            MaximumAmplitude=f?.MaximumAmplitude??0;SourceBaseDigest=f?.SourceBaseDigest??default;ContentDigest=f?.ContentDigest??default;
            Geometry=geometry.AsReadOnly();Flow=flow.AsReadOnly();Environment=environment.AsReadOnly();RangeLevels=levels.AsReadOnly();Ranges=ranges.AsReadOnly();
        }
        public bool Enabled=>Version==WorldOrogenDetailField.CurrentVersion&&Recipe.Enabled;
        public bool IsValid=>Enabled&&Recipe.IsValid&&Resolution==Recipe.ConditioningResolution&&math.isfinite(Radius)&&Radius>0&&math.isfinite(SeaLevel)&&
            SourceBaseDigest.IsValid&&ContentDigest.IsValid&&Geometry.IsCreated&&Flow.IsCreated&&Environment.IsCreated&&RangeLevels.IsCreated&&Ranges.IsCreated&&
            Geometry.Length==6*(Resolution+1)*(Resolution+1)&&Flow.Length==Geometry.Length&&Environment.Length==Geometry.Length&&RangeLevels.Length>0;
    }
    /// <summary>Empty canonical owners allocate valid zero-length containers; creating a view never builds derived data.</summary>
    public sealed class NativeWorldOrogenDetailData:IDisposable
    {
        readonly WorldOrogenDetailField field;bool disposed;
        NativeArray<float4> geometry,flow,environment;
        NativeArray<WorldOrogenDetailRangeLevel> levels;
        NativeArray<float2> ranges;
        public NativeWorldOrogenDetailView View=>!disposed?new NativeWorldOrogenDetailView(field,geometry,flow,environment,levels,ranges):throw new ObjectDisposedException(nameof(NativeWorldOrogenDetailData));
        public long EstimatedNativeBytes=>field?.EstimatedResidentBytes??0;
        public NativeWorldOrogenDetailData(WorldOrogenDetailField field,Allocator allocator)
        {
            if(allocator==Allocator.Invalid||allocator==Allocator.None)throw new ArgumentException("An owning allocator is required.");this.field=field;
            try
            {
                int n=field?.SampleCount??0;geometry=new NativeArray<float4>(n,allocator);flow=new NativeArray<float4>(n,allocator);environment=new NativeArray<float4>(n,allocator);
                levels=new NativeArray<WorldOrogenDetailRangeLevel>(field?.RangeLevels.Count??0,allocator);ranges=new NativeArray<float2>(field?.RangeCount??0,allocator);
                for(int i=0;i<n;i++){geometry[i]=field.GeometryAt(i);flow[i]=field.FlowAt(i);environment[i]=field.EnvironmentAt(i);}
                for(int i=0;i<levels.Length;i++)levels[i]=field.RangeLevels[i];for(int i=0;i<ranges.Length;i++)ranges[i]=field.RangeAt(i);
            }
            catch{Dispose();throw;}
        }
        public void Dispose(){if(disposed)return;disposed=true;if(geometry.IsCreated)geometry.Dispose();if(flow.IsCreated)flow.Dispose();if(environment.IsCreated)environment.Dispose();if(levels.IsCreated)levels.Dispose();if(ranges.IsCreated)ranges.Dispose();}
        public JobHandle Dispose(JobHandle readers)
        {
            if(disposed)return readers;disposed=true;var result=readers;
            if(geometry.IsCreated)result=JobHandle.CombineDependencies(result,geometry.Dispose(readers));if(flow.IsCreated)result=JobHandle.CombineDependencies(result,flow.Dispose(readers));
            if(environment.IsCreated)result=JobHandle.CombineDependencies(result,environment.Dispose(readers));if(levels.IsCreated)result=JobHandle.CombineDependencies(result,levels.Dispose(readers));
            if(ranges.IsCreated)result=JobHandle.CombineDependencies(result,ranges.Dispose(readers));return result;
        }
    }
}
