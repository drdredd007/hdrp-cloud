using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Bounded editor reuse of immutable bakes; runtime has no dependency on this cache.</summary>
    public static class PlanetTerrainBakeCache
    {
        const long MaximumBytes=128L*1024*1024;
        const int MaximumEntries=3;
        sealed class Entry { internal SurfaceContentHash Key; internal SurfaceBakeResult Result; internal long Bytes; }
        static readonly object gate=new object();
        static readonly LinkedList<Entry> entries=new LinkedList<Entry>();
        static long bytes;
        public static bool TryGet(SurfaceContentHash key,out SurfaceBakeResult result)
        {
            lock(gate)
            {
                for(var node=entries.First;node!=null;node=node.Next)
                    if(node.Value.Key==key){result=node.Value.Result;entries.Remove(node);entries.AddFirst(node);return true;}
                result=null;return false;
            }
        }
        public static void Store(SurfaceContentHash key,SurfaceBakeResult result)
        {
            if(result==null||!key.IsValid)throw new ArgumentException("A completed immutable bake and configuration digest are required.");
            if(!result.Snapshot.MaterialsReady)throw new ArgumentException("Pending automatic material weights cannot enter the completed bake cache.");
            long payload=EstimateBakeBytes(result);
            if(payload>MaximumBytes)return;
            lock(gate)
            {
                for(var node=entries.First;node!=null;node=node.Next)
                    if(node.Value.Key==key){bytes-=node.Value.Bytes;entries.Remove(node);break;}
                while(entries.Count>=MaximumEntries||bytes+payload>MaximumBytes)
                {bytes-=entries.Last.Value.Bytes;entries.RemoveLast();}
                entries.AddFirst(new Entry{Key=key,Result=result,Bytes=payload});bytes+=payload;
            }
        }
        public static long EstimateBakeBytes(SurfaceBakeResult result)
        {
            var retained=new ResidentEstimate();retained.AddBake(result);return retained.Bytes;
        }
        /// <summary>Retained generation estimate. Only the immutable algorithm-five child field is
        /// shared by reference; distinct cold-decoded allocations count separately even with equal hashes.
        /// Other payloads retain the existing conservative estimates.</summary>
        public sealed class ResidentEstimate
        {
            sealed class LandformIdentity:IEqualityComparer<SurfaceLandformField>
            {
                internal static readonly LandformIdentity Instance=new LandformIdentity();
                public bool Equals(SurfaceLandformField a,SurfaceLandformField b)=>ReferenceEquals(a,b);
                public int GetHashCode(SurfaceLandformField field)=>RuntimeHelpers.GetHashCode(field);
            }
            readonly HashSet<SurfaceLandformField> landforms;
            public long Bytes {get;private set;}
            public ResidentEstimate(){landforms=new HashSet<SurfaceLandformField>(LandformIdentity.Instance);}
            ResidentEstimate(ResidentEstimate source)
            {landforms=new HashSet<SurfaceLandformField>(source.landforms,LandformIdentity.Instance);Bytes=source.Bytes;}
            public ResidentEstimate Clone()=>new ResidentEstimate(this);
            long Additional(long fullBytes,SurfaceLandformField field)=>field!=null&&landforms.Contains(field)?checked(fullBytes-field.EstimatedResidentBytes):fullBytes;
            void Retain(SurfaceLandformField field){if(field!=null)landforms.Add(field);}
            public long EstimateSnapshotBytes(SurfaceSnapshot snapshot)
                =>Additional(PlanetTerrainBakeCache.EstimateSnapshotBytes(snapshot),snapshot.StructuralField?.LandformField);
            public long EstimateLodSnapshotBytes(SurfaceSnapshot source,SurfaceLodLevel level)
                =>Additional(PlanetTerrainBakeCache.EstimateLodSnapshotBytes(source,level),source.StructuralField?.LandformField);
            public void AddSnapshot(SurfaceSnapshot snapshot)
            {Bytes=checked(Bytes+EstimateSnapshotBytes(snapshot));Retain(snapshot.StructuralField?.LandformField);}
            public void AddBake(SurfaceBakeResult result)
            {
                if(result==null)throw new ArgumentNullException(nameof(result));
                AddSnapshot(result.Snapshot);
                foreach(var level in result.LodPyramid)if(level.Resolution!=result.Snapshot.Resolution)
                    foreach(var tile in level.Tiles)Bytes=checked(Bytes+TileBytes(tile));
                if(result.Hydrology!=null)Bytes=checked(Bytes+result.Hydrology.SampleCount*64L);
                if(result.Geomorphology!=null)
                {
                    var field=result.Geomorphology.LandformField;
                    Bytes=checked(Bytes+Additional(result.Geomorphology.EstimatedResidentBytes,field));Retain(field);
                }
            }
        }
        /// <summary>Preserves the existing constructor/index scratch reservation. Striding the
        /// reference grid retains its algorithm-five child; it never clones that child as scratch.</summary>
        public static long EstimateLodPreparationBytes(SurfaceSnapshot source,SurfaceLodLevel level)
        {
            if(source==null||level==null)throw new ArgumentNullException(source==null?nameof(source):nameof(level));
            if(source.StructuralField==null)return 0;
            return checked((source.StructuralField.EstimatedResidentBytesAtResolution(level.Resolution)-
                (source.StructuralField.LandformField?.EstimatedResidentBytes??0))*4);
        }
        /// <summary>Conservative retained managed payload, including separately resolved float4 arrays. Not measured process RSS.</summary>
        public static long EstimateSnapshotBytes(SurfaceSnapshot snapshot)
        {
            if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
            long payload=checked(512+snapshot.Stamps.Count*128L+(snapshot.StructuralField?.EstimatedResidentBytes??0)+(snapshot.OrogenDetail?.EstimatedResidentBytes??0));
            foreach(var tile in snapshot.Tiles)payload=checked(payload+TileBytes(tile));
            foreach(var region in snapshot.Regions)payload=checked(payload+256L+(long)region.SampleCount*(8+(region.HasMaterialWeights?16:0)+(region.HasErosionData?16:0)));
            if(snapshot.ResolvedMaterials!=null)
            {
                foreach(var tile in snapshot.ResolvedMaterials.Tiles)payload=checked(payload+256L+tile.SampleCount*16L);
                foreach(var layer in snapshot.ResolvedMaterials.Layers)payload=checked(payload+256L+layer.SampleCount*16L);
            }
            return payload;
        }
        public static long EstimateLodSnapshotBytes(SurfaceSnapshot source,SurfaceLodLevel level)
        {
            if(source==null||level==null)throw new ArgumentNullException(source==null?nameof(source):nameof(level));
            long payload=EstimateSnapshotBytes(source);
            foreach(var tile in source.Tiles)payload=checked(payload-TileBytes(tile));
            foreach(var tile in level.Tiles)payload=checked(payload+TileBytes(tile));
            if(source.StructuralField!=null)payload=checked(payload-source.StructuralField.EstimatedResidentBytes+source.StructuralField.EstimatedResidentBytesAtResolution(level.Resolution));
            return payload;
        }
        static long TileBytes(SurfaceTileData tile)=>checked(256L+(long)tile.SampleCount*(4+(tile.HasMaterialWeights?16:0)+(tile.HasErosionData?16:0)));
        public static void Clear(){lock(gate){entries.Clear();bytes=0;}}
    }
}
