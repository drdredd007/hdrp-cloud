using System;
using System.Collections.Generic;
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
            if(result==null)throw new ArgumentNullException(nameof(result));
            long payload=EstimateSnapshotBytes(result.Snapshot);
            foreach(var level in result.LodPyramid)if(level.Resolution!=result.Snapshot.Resolution)
                foreach(var tile in level.Tiles)payload=checked(payload+TileBytes(tile));
            if(result.Hydrology!=null)payload=checked(payload+result.Hydrology.SampleCount*64L);
            return payload;
        }
        /// <summary>Conservative retained managed payload, including separately resolved float4 arrays. Not measured process RSS.</summary>
        public static long EstimateSnapshotBytes(SurfaceSnapshot snapshot)
        {
            if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
            long payload=512+snapshot.Stamps.Count*128L;
            foreach(var tile in snapshot.Tiles)payload=checked(payload+TileBytes(tile));
            foreach(var region in snapshot.Regions)payload=checked(payload+256L+(long)region.SampleCount*(8+(region.HasMaterialWeights?16:0)+(region.HasErosionData?16:0)));
            if(snapshot.ResolvedMaterials!=null)
            {
                foreach(var tile in snapshot.ResolvedMaterials.Tiles)payload=checked(payload+256L+tile.SampleCount*16L);
                foreach(var layer in snapshot.ResolvedMaterials.Layers)payload=checked(payload+256L+layer.SampleCount*16L);
            }
            return payload;
        }
        static long TileBytes(SurfaceTileData tile)=>checked(256L+(long)tile.SampleCount*(4+(tile.HasMaterialWeights?16:0)+(tile.HasErosionData?16:0)));
        public static void Clear(){lock(gate){entries.Clear();bytes=0;}}
    }
}
