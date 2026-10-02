using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Host instance identity, independent of terrain content and of its moving graph pose.</summary>
    public readonly struct SurfaceScatterPlanetId : IEquatable<SurfaceScatterPlanetId>, IComparable<SurfaceScatterPlanetId>
    {
        public readonly ulong High, Low;
        public SurfaceScatterPlanetId(ulong high, ulong low) { High=high; Low=low; }
        public bool IsValid => (High|Low)!=0;
        public bool Equals(SurfaceScatterPlanetId other)=>High==other.High && Low==other.Low;
        public override bool Equals(object value)=>value is SurfaceScatterPlanetId other && Equals(other);
        public override int GetHashCode(){unchecked{return (int)High*397^(int)Low;}}
        public int CompareTo(SurfaceScatterPlanetId other){int n=High.CompareTo(other.High);return n!=0?n:Low.CompareTo(other.Low);}
    }
    /// <summary>The complete identity is authoritative. StableId is only a GPU lookup fingerprint.</summary>
    public readonly struct SurfaceScatterKey : IEquatable<SurfaceScatterKey>, IComparable<SurfaceScatterKey>
    {
        public readonly SurfaceScatterPlanetId Planet;
        public readonly uint SpeciesId;
        public readonly SurfaceTileKey Cell;
        public readonly int Slot;
        public SurfaceScatterKey(SurfaceScatterPlanetId planet,uint speciesId,SurfaceTileKey cell,int slot)
        {Planet=planet;SpeciesId=speciesId;Cell=cell;Slot=slot;}
        public bool IsValid=>Planet.IsValid && SpeciesId!=0 && Cell.IsValid && Slot>=0 && Slot<SurfaceScatterSpecies.MaximumCandidatesPerCell;
        public uint4 StableId=>new uint4(SurfaceScatterRandom.Word(this,0,101),SurfaceScatterRandom.Word(this,0,102),SurfaceScatterRandom.Word(this,0,103),SurfaceScatterRandom.Word(this,0,104));
        public bool Equals(SurfaceScatterKey other)=>Planet.Equals(other.Planet)&&SpeciesId==other.SpeciesId&&Cell.Equals(other.Cell)&&Slot==other.Slot;
        public override bool Equals(object value)=>value is SurfaceScatterKey other && Equals(other);
        public override int GetHashCode()=>unchecked((int)SurfaceScatterRandom.Fold(this));
        public int CompareTo(SurfaceScatterKey other)
        {int n=Planet.CompareTo(other.Planet);if(n!=0)return n;n=SpeciesId.CompareTo(other.SpeciesId);if(n!=0)return n;n=Cell.CompareTo(other.Cell);return n!=0?n:Slot.CompareTo(other.Slot);}
    }
    /// <summary>Explicit bounded proposal count. Terrain/filters may change acceptance without changing a candidate key.</summary>
    public readonly struct SurfaceScatterSpecies
    {
        public const int MaximumCandidatesPerCell=4096;
        public readonly uint SpeciesId, Seed;
        public readonly int FixedLevel, CandidatesPerCell;
        public readonly double DensityPerSquareMetre, MinimumHeight, MaximumHeight, MaximumSlopeDegrees, MinimumWetness, MaximumWetness, NormalSampleMetres;
        public readonly double MinimumReferenceChordSpacingMetres;
        public readonly float4 MaterialAffinity;
        public readonly float2 ScaleRange;
        public readonly SurfaceChannels RequiredChannels;
        public readonly bool ExcludeSurfaceStamps;
        public SurfaceScatterSpecies(uint speciesId,int fixedLevel,int candidatesPerCell,double densityPerSquareMetre,
            double minimumHeight=-1e12,double maximumHeight=1e12,double maximumSlopeDegrees=45,double minimumWetness=0,double maximumWetness=1,
            float4? materialAffinity=null,float2? scaleRange=null,double normalSampleMetres=1,uint seed=0,
            SurfaceChannels requiredChannels=SurfaceChannels.MaterialWeights|SurfaceChannels.ErosionData,bool excludeSurfaceStamps=true,double minimumReferenceChordSpacingMetres=0)
        {
            SpeciesId=speciesId;FixedLevel=fixedLevel;CandidatesPerCell=candidatesPerCell;DensityPerSquareMetre=densityPerSquareMetre;
            MinimumHeight=minimumHeight;MaximumHeight=maximumHeight;MaximumSlopeDegrees=maximumSlopeDegrees;MinimumWetness=minimumWetness;MaximumWetness=maximumWetness;
            MaterialAffinity=materialAffinity??new float4(1);ScaleRange=scaleRange??new float2(.8f,1.2f);NormalSampleMetres=normalSampleMetres;Seed=seed;
            RequiredChannels=requiredChannels;ExcludeSurfaceStamps=excludeSurfaceStamps;
            MinimumReferenceChordSpacingMetres=minimumReferenceChordSpacingMetres;
            if(!IsValid)throw new ArgumentException("Scatter species requires a stable nonzero id, bounded fixed grid/proposals and finite density/filter/scale settings.");
        }
        public bool IsValid=>SpeciesId!=0 && FixedLevel>=0 && FixedLevel<=SurfaceTileKey.MaximumLevel && CandidatesPerCell>0 && CandidatesPerCell<=MaximumCandidatesPerCell &&
            math.isfinite(DensityPerSquareMetre)&&DensityPerSquareMetre>=0 && math.isfinite(MinimumHeight)&&math.isfinite(MaximumHeight)&&MinimumHeight<=MaximumHeight &&
            math.isfinite(MaximumSlopeDegrees)&&MaximumSlopeDegrees>=0&&MaximumSlopeDegrees<=90 && math.isfinite(MinimumWetness)&&math.isfinite(MaximumWetness)&&MinimumWetness>=0&&MaximumWetness<=1&&MinimumWetness<=MaximumWetness &&
            math.all(math.isfinite(MaterialAffinity))&&math.all(MaterialAffinity>=0)&&math.all(MaterialAffinity<=1) && math.all(math.isfinite(ScaleRange))&&ScaleRange.x>0&&ScaleRange.x<=ScaleRange.y&&ScaleRange.y<=1e6 &&
            math.isfinite(NormalSampleMetres)&&NormalSampleMetres>0 && math.isfinite(MinimumReferenceChordSpacingMetres)&&MinimumReferenceChordSpacingMetres>=0 &&
            (RequiredChannels&~(SurfaceChannels.MaterialWeights|SurfaceChannels.ErosionData))==0;
    }
    public sealed class SurfaceScatterProfile
    {
        public const int AlgorithmVersion=1, MaximumSpecies=64;
        public IReadOnlyList<SurfaceScatterSpecies> Species {get;}
        public SurfaceContentHash ContentDigest {get;}
        public SurfaceScatterProfile(IEnumerable<SurfaceScatterSpecies> species)
        {
            if(species==null)throw new ArgumentNullException(nameof(species));var captured=new List<SurfaceScatterSpecies>();
            foreach(var item in species){if(!item.IsValid)throw new ArgumentException("Invalid scatter species.");captured.Add(item);if(captured.Count>MaximumSpecies)throw new ArgumentException("Scatter profile exceeds its bounded species count.");}
            if(captured.Count==0)throw new ArgumentException("A scatter profile requires at least one species.");captured.Sort((a,b)=>a.SpeciesId.CompareTo(b.SpeciesId));
            for(int i=1;i<captured.Count;i++)if(captured[i].SpeciesId==captured[i-1].SpeciesId)throw new ArgumentException("Scatter species ids must be unique.");
            Species=captured.AsReadOnly();ContentDigest=SurfaceHashing.Compute(writer=>
            {
                bool spacing=false;foreach(var item in captured)spacing|=item.MinimumReferenceChordSpacingMetres>0;
                writer.Write(spacing?2:AlgorithmVersion);writer.Write(captured.Count);
                foreach(var item in captured)
                {
                    writer.Write(item.SpeciesId);writer.Write(item.Seed);writer.Write(item.FixedLevel);writer.Write(item.CandidatesPerCell);writer.Write(item.DensityPerSquareMetre);
                    writer.Write(item.MinimumHeight);writer.Write(item.MaximumHeight);writer.Write(item.MaximumSlopeDegrees);writer.Write(item.MinimumWetness);writer.Write(item.MaximumWetness);writer.Write(item.NormalSampleMetres);
                    writer.Write(item.MaterialAffinity.x);writer.Write(item.MaterialAffinity.y);writer.Write(item.MaterialAffinity.z);writer.Write(item.MaterialAffinity.w);
                    writer.Write(item.ScaleRange.x);writer.Write(item.ScaleRange.y);writer.Write((int)item.RequiredChannels);writer.Write(item.ExcludeSurfaceStamps);
                    if(spacing)writer.Write(item.MinimumReferenceChordSpacingMetres);
                }
            });
        }
    }
    /// <summary>Readiness belongs to the captured snapshot, not to the presence of some raw mask arrays.</summary>
    public readonly struct SurfaceScatterSurfaceView
    {
        public readonly NativeSurfaceView Surface;
        public readonly bool MaterialsReady;
        public SurfaceScatterSurfaceView(in NativeSurfaceView surface,bool materialsReady){Surface=surface;MaterialsReady=materialsReady;}
    }
    /// <summary>Convenience owner for offline jobs. Runtime can wrap an existing retained native surface lease instead.</summary>
    public sealed class SurfaceScatterSurfaceLease : IDisposable
    {
        readonly NativeSurfaceSnapshot owner;
        readonly bool ready;
        public SurfaceScatterSurfaceView View=>new SurfaceScatterSurfaceView(owner.View,ready);
        public SurfaceScatterSurfaceLease(SurfaceSnapshot snapshot,Allocator allocator)
        {if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));ready=snapshot.MaterialsReady;owner=snapshot.CreateNative(allocator);}
        public void Dispose()=>owner.Dispose();
        public Unity.Jobs.JobHandle Dispose(Unity.Jobs.JobHandle readers)=>owner.Dispose(readers);
    }
    public enum SurfaceScatterDecision { NotEvaluated, Accepted, Density, Height, Slope, Material, Wetness, Excluded, Spacing }
    public readonly struct SurfaceScatterCandidate
    {
        public readonly SurfaceScatterKey Key;
        public readonly double3 Direction,Position,Normal;
        public readonly quaternion Rotation;
        public readonly float Scale;
        public readonly double Height,SlopeDegrees,YawRadians;
        public readonly SurfaceAttributes Attributes;
        public uint4 StableId=>Key.StableId;
        internal SurfaceScatterCandidate(SurfaceScatterKey key,double3 direction,double3 position,double3 normal,quaternion rotation,float scale,double height,double slope,double yaw,SurfaceAttributes attributes)
        {Key=key;Direction=direction;Position=position;Normal=normal;Rotation=rotation;Scale=scale;Height=height;SlopeDegrees=slope;YawRadians=yaw;Attributes=attributes;}
    }
}
