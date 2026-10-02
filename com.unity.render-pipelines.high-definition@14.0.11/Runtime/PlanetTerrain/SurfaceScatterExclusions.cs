using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct SurfaceScatterCircle : IComparable<SurfaceScatterCircle>, IEquatable<SurfaceScatterCircle>
    {
        public readonly ulong IdHigh,IdLow;
        public readonly double3 CenterDirection;
        public readonly double RadiusMetres;
        public SurfaceScatterCircle(ulong idHigh,ulong idLow,double3 centerDirection,double radiusMetres)
        {IdHigh=idHigh;IdLow=idLow;CenterDirection=math.all(math.isfinite(centerDirection))&&math.abs(math.lengthsq(centerDirection)-1)<1e-14?centerDirection:CubeSurface.TryNormalize(centerDirection,out var unit)?unit:default;RadiusMetres=radiusMetres;}
        public bool IsValid=>(IdHigh|IdLow)!=0 && math.all(math.isfinite(CenterDirection)) && math.abs(math.lengthsq(CenterDirection)-1)<1e-10 && math.isfinite(RadiusMetres)&&RadiusMetres>0;
        public int CompareTo(SurfaceScatterCircle other){int n=IdHigh.CompareTo(other.IdHigh);return n!=0?n:IdLow.CompareTo(other.IdLow);}
        public bool Equals(SurfaceScatterCircle other)=>IdHigh==other.IdHigh&&IdLow==other.IdLow&&math.all(CenterDirection==other.CenterDirection)&&RadiusMetres==other.RadiusMetres;
        public override bool Equals(object value)=>value is SurfaceScatterCircle other && Equals(other);
        public override int GetHashCode()=>unchecked((int)IdHigh*397^(int)IdLow);
    }
    /// <summary>Instance state, deliberately independent of current height revision. Entries are sorted/idempotent commands.</summary>
    public sealed class SurfaceScatterExclusions
    {
        public const int MaximumKeys=1024*1024, MaximumCircles=65536;
        public SurfaceScatterPlanetId Planet {get;}
        public ulong Epoch {get;}
        public IReadOnlyList<SurfaceScatterKey> Keys {get;}
        public IReadOnlyList<SurfaceScatterCircle> Circles {get;}
        public SurfaceContentHash ContentDigest {get;}
        public SurfaceScatterExclusions(SurfaceScatterPlanetId planet,ulong epoch,IEnumerable<SurfaceScatterKey> keys=null,IEnumerable<SurfaceScatterCircle> circles=null)
        {
            if(!planet.IsValid||epoch==0)throw new ArgumentException("Exclusions require a stable planet instance and a nonzero independent epoch.");Planet=planet;Epoch=epoch;
            var capturedKeys=new List<SurfaceScatterKey>();var capturedCircles=new List<SurfaceScatterCircle>();
            if(keys!=null)foreach(var key in keys){if(!key.IsValid||!key.Planet.Equals(planet))throw new ArgumentException("Suppressed keys must belong to this planet instance.");capturedKeys.Add(key);if(capturedKeys.Count>MaximumKeys)throw new ArgumentException("Exclusion key budget exceeded.");}
            if(circles!=null)foreach(var circle in circles){if(!circle.IsValid)throw new ArgumentException("Invalid exclusion circle.");capturedCircles.Add(circle);if(capturedCircles.Count>MaximumCircles)throw new ArgumentException("Exclusion circle budget exceeded.");}
            capturedKeys.Sort();for(int i=capturedKeys.Count-1;i>0;i--)if(capturedKeys[i].Equals(capturedKeys[i-1]))capturedKeys.RemoveAt(i);
            capturedCircles.Sort();for(int i=capturedCircles.Count-1;i>0;i--)if(capturedCircles[i].CompareTo(capturedCircles[i-1])==0)
            {if(!capturedCircles[i].Equals(capturedCircles[i-1]))throw new ArgumentException("An exclusion circle id cannot name conflicting geometry.");capturedCircles.RemoveAt(i);}
            Keys=capturedKeys.AsReadOnly();Circles=capturedCircles.AsReadOnly();ContentDigest=SurfaceHashing.Compute(writer=>SurfaceScatterExclusionCodec.WriteData(writer,this));
        }
        public NativeSurfaceScatterExclusions CreateNative(Allocator allocator)=>new NativeSurfaceScatterExclusions(this,allocator);
    }
    public readonly struct NativeSurfaceScatterExclusionsView
    {
        public readonly SurfaceScatterPlanetId Planet;
        public readonly ulong Epoch;
        public readonly NativeArray<SurfaceScatterKey>.ReadOnly Keys;
        public readonly NativeArray<SurfaceScatterCircle>.ReadOnly Circles;
        public readonly bool IsReady;
        public bool IsPresent=>Planet.IsValid;
        internal NativeSurfaceScatterExclusionsView(SurfaceScatterPlanetId planet,ulong epoch,NativeArray<SurfaceScatterKey>.ReadOnly keys,NativeArray<SurfaceScatterCircle>.ReadOnly circles,bool ready)
        {Planet=planet;Epoch=epoch;Keys=keys;Circles=circles;IsReady=ready;}
        public static NativeSurfaceScatterExclusionsView Pending(SurfaceScatterPlanetId planet,ulong epoch)
        {if(!planet.IsValid||epoch==0)throw new ArgumentException("Pending exclusion state requires its planet and epoch.");return new NativeSurfaceScatterExclusionsView(planet,epoch,default,default,false);}
    }
    /// <summary>Use an allocated empty owner for scheduled jobs; default absent views contain no NativeArray safety handles.</summary>
    public sealed class NativeSurfaceScatterExclusions : IDisposable
    {
        readonly SurfaceScatterPlanetId planet;readonly ulong epoch;
        NativeArray<SurfaceScatterKey> keys;NativeArray<SurfaceScatterCircle> circles;bool disposed;
        public NativeSurfaceScatterExclusionsView View
        {get{if(disposed)throw new ObjectDisposedException(nameof(NativeSurfaceScatterExclusions));return new NativeSurfaceScatterExclusionsView(planet,epoch,keys.AsReadOnly(),circles.AsReadOnly(),true);}}
        internal NativeSurfaceScatterExclusions(SurfaceScatterExclusions source,Allocator allocator)
        {
            if(allocator==Allocator.None||allocator==Allocator.Invalid)throw new ArgumentException("Exclusion native state requires an owning allocator.");planet=source.Planet;epoch=source.Epoch;
            try{keys=new NativeArray<SurfaceScatterKey>(source.Keys.Count,allocator);circles=new NativeArray<SurfaceScatterCircle>(source.Circles.Count,allocator);for(int i=0;i<keys.Length;i++)keys[i]=source.Keys[i];for(int i=0;i<circles.Length;i++)circles[i]=source.Circles[i];}
            catch{Dispose();throw;}
        }
        public void Dispose(){if(disposed)return;if(keys.IsCreated)keys.Dispose();if(circles.IsCreated)circles.Dispose();disposed=true;}
        public JobHandle Dispose(JobHandle readers)
        {if(disposed)return readers;var result=readers;if(keys.IsCreated)result=JobHandle.CombineDependencies(result,keys.Dispose(readers));if(circles.IsCreated)result=JobHandle.CombineDependencies(result,circles.Dispose(readers));disposed=true;return result;}
    }
    public static class SurfaceScatterExclusionSampler
    {
        public static double ArcMetres(double3 a,double3 b,double radius)=>2*math.asin(math.clamp(math.length(a-b)*.5,0,1))*radius;
        public static SurfaceSampleStatus TryExcluded(in NativeSurfaceScatterExclusionsView view,in SurfaceScatterKey key,double3 unitDirection,double radius,out bool excluded)
        {
            excluded=false;if(!key.IsValid||!math.isfinite(radius)||radius<=0||!math.all(math.isfinite(unitDirection))||math.abs(math.lengthsq(unitDirection)-1)>1e-10)return SurfaceSampleStatus.InvalidInput;
            if(!view.IsPresent)return SurfaceSampleStatus.Ready;
            if(!view.Planet.Equals(key.Planet)||view.Epoch==0)return SurfaceSampleStatus.InvalidInput;
            if(!view.IsReady||!view.Keys.IsCreated||!view.Circles.IsCreated)return SurfaceSampleStatus.NotReady;
            int low=0,high=view.Keys.Length-1;
            while(low<=high){int middle=low+(high-low)/2;int order=view.Keys[middle].CompareTo(key);if(order==0){excluded=true;return SurfaceSampleStatus.Ready;}if(order<0)low=middle+1;else high=middle-1;}
            for(int i=0;i<view.Circles.Length;i++){var circle=view.Circles[i];if(!circle.IsValid)return SurfaceSampleStatus.IncompatibleData;if(ArcMetres(unitDirection,circle.CenterDirection,radius)<=circle.RadiusMetres){excluded=true;break;}}
            return SurfaceSampleStatus.Ready;
        }
    }
    public static class SurfaceScatterExclusionCodec
    {
        public const int FormatVersion=1;
        const uint Magic=0x53435831;
        public static void Write(BinaryWriter writer,SurfaceScatterExclusions source)
        {if(writer==null||source==null)throw new ArgumentNullException();writer.Write(Magic);WriteData(writer,source);SurfaceHashing.WriteHash(writer,source.ContentDigest);}
        internal static void WriteData(BinaryWriter writer,SurfaceScatterExclusions source)
        {
            writer.Write(FormatVersion);writer.Write(source.Planet.High);writer.Write(source.Planet.Low);writer.Write(source.Epoch);writer.Write(source.Keys.Count);writer.Write(source.Circles.Count);
            foreach(var key in source.Keys){writer.Write(key.SpeciesId);SurfaceHashing.WriteKey(writer,key.Cell);writer.Write(key.Slot);}
            foreach(var circle in source.Circles){writer.Write(circle.IdHigh);writer.Write(circle.IdLow);SurfaceHashing.WriteVector(writer,circle.CenterDirection);writer.Write(circle.RadiusMetres);}
        }
        public static SurfaceScatterExclusions Read(BinaryReader reader)
        {
            if(reader==null)throw new ArgumentNullException(nameof(reader));if(reader.ReadUInt32()!=Magic||reader.ReadInt32()!=FormatVersion)throw new InvalidDataException("Unsupported scatter exclusion archive.");
            var planet=new SurfaceScatterPlanetId(reader.ReadUInt64(),reader.ReadUInt64());ulong epoch=reader.ReadUInt64();int keyCount=reader.ReadInt32(),circleCount=reader.ReadInt32();
            if(keyCount<0||keyCount>SurfaceScatterExclusions.MaximumKeys||circleCount<0||circleCount>SurfaceScatterExclusions.MaximumCircles)throw new InvalidDataException("Scatter exclusion archive exceeds bounded record counts.");
            var keys=new List<SurfaceScatterKey>(keyCount);var circles=new List<SurfaceScatterCircle>(circleCount);
            for(int i=0;i<keyCount;i++){uint species=reader.ReadUInt32();var cell=new SurfaceTileKey(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());keys.Add(new SurfaceScatterKey(planet,species,cell,reader.ReadInt32()));}
            for(int i=0;i<circleCount;i++)circles.Add(new SurfaceScatterCircle(reader.ReadUInt64(),reader.ReadUInt64(),new double3(reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble()),reader.ReadDouble()));
            var digest=new SurfaceContentHash(reader.ReadUInt64(),reader.ReadUInt64(),reader.ReadUInt64(),reader.ReadUInt64());
            try{var source=new SurfaceScatterExclusions(planet,epoch,keys,circles);if(source.ContentDigest!=digest)throw new InvalidDataException("Scatter exclusion content digest mismatch.");return source;}
            catch(ArgumentException exception){throw new InvalidDataException("Invalid scatter exclusion archive.",exception);}
        }
    }
}
