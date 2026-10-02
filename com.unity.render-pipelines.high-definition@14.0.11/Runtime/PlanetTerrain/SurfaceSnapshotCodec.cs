using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Portable, bounded snapshot payload. Decoding prepares a candidate; it never publishes into a live world.</summary>
    public static class SurfaceSnapshotCodec
    {
        const uint Magic = 0x31534653;
        public const int FormatVersion = 5;
        public const int MaximumTiles = 65536, MaximumRegions = 4096, MaximumStamps = 65536;
        public const int MaximumSamples = 64 * 1024 * 1024;

        public static void Write(BinaryWriter writer, SurfaceSnapshot snapshot)
        {
            if (writer == null || snapshot == null) throw new ArgumentNullException(writer == null ? nameof(writer) : nameof(snapshot));
            ValidateCounts(snapshot);
            writer.Write(Magic); writer.Write(FormatVersion); SurfaceHashing.WriteRecipe(writer, snapshot.Recipe);
            SurfaceHashing.WriteHash(writer, snapshot.Revision.RecipeDigest); SurfaceHashing.WriteHash(writer, snapshot.Revision.BaseDigest);
            writer.Write(snapshot.Revision.Epoch); SurfaceHashing.WriteHash(writer, snapshot.ContentDigest);
            writer.Write(snapshot.CanonicalTileLevel); writer.Write(snapshot.Resolution);
            writer.Write(snapshot.Detail.WavelengthMetres); writer.Write(snapshot.Detail.AmplitudeMetres); writer.Write(snapshot.Detail.Seed);
            writer.Write(snapshot.Tiles.Count);
            foreach (var tile in snapshot.Tiles)
            {
                SurfaceHashing.WriteKey(writer, tile.Key); writer.Write(tile.Resolution); writer.Write(tile.SampleCount);
                for (int i = 0; i < tile.SampleCount; i++) writer.Write(tile.HeightAt(i));
                writer.Write((int)tile.Channels); writer.Write(tile.MeasuredLodErrorMetres);
                SurfaceHashing.WriteAttributes(writer, tile.CopyMaterialWeights()); SurfaceHashing.WriteAttributes(writer, tile.CopyErosionData());
                writer.Write((int)tile.MaterialProvenance);
            }
            writer.Write(snapshot.Regions.Count);
            foreach (var region in snapshot.Regions)
            {
                SurfaceHashing.WriteProjection(writer, region.Projection); writer.Write(region.Resolution.x); writer.Write(region.Resolution.y);
                writer.Write(region.BlendMetres); writer.Write((int)region.Mode); SurfaceHashing.WriteHash(writer, region.BaseDigest);
                writer.Write((int)region.DetailPolicy); writer.Write(region.Priority); writer.Write(region.SampleCount);
                for (int i = 0; i < region.SampleCount; i++) writer.Write(region.HeightAt(i));
                for (int i = 0; i < region.SampleCount; i++) writer.Write(region.MaskAt(i));
                writer.Write(region.HasMaterialWeights);
                SurfaceHashing.WriteAttributes(writer, region.CopyMaterialWeights());
                writer.Write((int)region.Kind); SurfaceHashing.WriteHash(writer, region.SourceContentDigest); writer.Write(region.ContextMetres);
                writer.Write(region.HasErosionData); SurfaceHashing.WriteAttributes(writer, region.CopyErosionData());
                writer.Write((int)region.MaterialProvenance); writer.Write(region.AutomaticMaterialProfile.IsValid);
                if(region.AutomaticMaterialProfile.IsValid)SurfaceAutomaticMaterialProfile.Write(writer,region.AutomaticMaterialProfile);
            }
            writer.Write(snapshot.Stamps.Count);
            foreach (var stamp in snapshot.Stamps)
            {
                writer.Write(stamp.IdHigh); writer.Write(stamp.IdLow); SurfaceHashing.WriteVector(writer, stamp.CenterDirection);
                writer.Write(stamp.RadiusMetres); writer.Write(stamp.DepthMetres); writer.Write(stamp.RimWidthMetres); writer.Write(stamp.RimHeightMetres);
            }
            writer.Write(snapshot.HasAutomaticMaterials);
            if(snapshot.HasAutomaticMaterials)SurfaceAutomaticMaterialProfile.Write(writer,snapshot.AutomaticMaterialProfile);
            writer.Write(snapshot.ResolvedMaterials != null);
            if(snapshot.ResolvedMaterials != null)
            {
                var resolved=snapshot.ResolvedMaterials;
                SurfaceHashing.WriteHash(writer,resolved.GeometryDigest); SurfaceHashing.WriteHash(writer,resolved.RulesDigest); SurfaceHashing.WriteHash(writer,resolved.ContentDigest);
                writer.Write(resolved.Tiles.Count);
                foreach(var tile in resolved.Tiles){SurfaceHashing.WriteKey(writer,tile.Key);writer.Write(tile.Resolution);SurfaceHashing.WriteAttributes(writer,tile.CopyWeights());}
                writer.Write(resolved.Layers.Count);
                foreach(var layer in resolved.Layers){SurfaceHashing.WriteProjection(writer,layer.Projection);writer.Write(layer.Resolution.x);writer.Write(layer.Resolution.y);writer.Write(layer.BlendMetres);writer.Write(layer.SourcePriority);SurfaceHashing.WriteAttributes(writer,layer.CopyWeights());}
                writer.Write(resolved.StampLayers.Count);foreach(var layer in resolved.StampLayers)layer.Write(writer);
            }
        }

        public static SurfaceSnapshot Read(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            try { return ReadCandidate(reader); }
            catch (ArgumentException exception) { throw new InvalidDataException("Invalid surface snapshot values.", exception); }
            catch (OverflowException exception) { throw new InvalidDataException("Surface snapshot exceeds numeric bounds.", exception); }
            catch (EndOfStreamException exception) { throw new InvalidDataException("Truncated surface snapshot.", exception); }
        }

        static SurfaceSnapshot ReadCandidate(BinaryReader reader)
        {
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unsupported surface snapshot format.");
            int format = reader.ReadInt32(); if (format < 1 || format > FormatVersion) throw new InvalidDataException("Unsupported surface snapshot format.");
            int schema = reader.ReadInt32(), algorithm = reader.ReadInt32(); var style = (SurfaceStyle)reader.ReadInt32(); int seed = reader.ReadInt32();
            double radius = reader.ReadDouble(), sea = reader.ReadDouble(), minimum = reader.ReadDouble(), maximum = reader.ReadDouble();
            var recipe = new SurfaceRecipe(seed, style, radius, minimum, maximum, sea, algorithm, schema);
            var revision = new SurfaceRevision(ReadHash(reader), ReadHash(reader), reader.ReadUInt64()); var digest = ReadHash(reader);
            int level = reader.ReadInt32(), resolution = reader.ReadInt32();
            var detail = new SurfaceDetailRecipe(reader.ReadDouble(), reader.ReadDouble(), reader.ReadInt32());
            if (!recipe.IsValid || !revision.IsValid || level < 0 || level > SurfaceTileKey.MaximumLevel || !CubeSurface.ValidResolution(resolution) || !detail.IsValid)
                throw new InvalidDataException("Invalid surface snapshot header.");
            var tiles = new List<SurfaceTileData>(); int samples = 0, count = ReadCount(reader, MaximumTiles);
            for (int i = 0; i < count; i++)
            {
                var key = new SurfaceTileKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                int tileResolution = reader.ReadInt32();
                if (!key.IsValid || key.Level != level || tileResolution != resolution) throw new InvalidDataException("Tile does not match the canonical grid.");
                int expected = checked((resolution + 1) * (resolution + 1));
                var heights = ReadSamples(reader, expected, ref samples);
                SurfaceChannels channels = format >= 2 ? (SurfaceChannels)reader.ReadInt32() : SurfaceChannels.None;
                double error = format >= 2 ? reader.ReadDouble() : 0;
                if ((channels & ~(SurfaceChannels.MaterialWeights | SurfaceChannels.ErosionData)) != 0) throw new InvalidDataException("Unknown attribute channel.");
                var weights = (channels & SurfaceChannels.MaterialWeights) != 0 ? ReadAttributes(reader, expected, ref samples) : null;
                var erosion = (channels & SurfaceChannels.ErosionData) != 0 ? ReadAttributes(reader, expected, ref samples) : null;
                var provenance=format>=4?(SurfaceMaterialProvenance)reader.ReadInt32():SurfaceMaterialProvenance.LegacyBaked;
                tiles.Add(new SurfaceTileData(key, resolution, heights, weights, erosion, error,provenance));
            }
            var regions = new List<SurfaceRegionData>(); count = ReadCount(reader, MaximumRegions);
            for (int i = 0; i < count; i++)
            {
                var projection = ReadProjection(reader); var size = new int2(reader.ReadInt32(), reader.ReadInt32());
                double blend = reader.ReadDouble(); var mode = (SurfaceRegionMode)reader.ReadInt32(); var baseDigest = ReadHash(reader);
                var policy = (SurfaceDetailPolicy)reader.ReadInt32(); int priority = reader.ReadInt32();
                if (!projection.IsValid || math.any(size < 1) || math.any(size > 4096)) throw new InvalidDataException("Invalid regional grid.");
                int expected = checked((size.x + 1) * (size.y + 1));
                var heights = ReadSamples(reader, expected, ref samples);
                AddSamples(ref samples, expected); RequireRemaining(reader, (long)expected * 4);
                var masks = new float[expected]; for (int j = 0; j < expected; j++) masks[j] = reader.ReadSingle();
                var weights = format >= 2 && reader.ReadBoolean() ? ReadAttributes(reader, expected, ref samples) : null;
                var kind = format >= 3 ? (SurfaceRegionKind)reader.ReadInt32() : SurfaceRegionKind.Authored;
                var sourceDigest = format >= 3 ? ReadHash(reader) : default; double context = format >= 3 ? reader.ReadDouble() : 0;
                var erosion = format >= 3 && reader.ReadBoolean() ? ReadAttributes(reader, expected, ref samples) : null;
                var provenance=format>=4?(SurfaceMaterialProvenance)reader.ReadInt32():SurfaceMaterialProvenance.LegacyBaked;
                var profile=format>=4&&reader.ReadBoolean()?SurfaceAutomaticMaterialProfile.ReadVersioned(reader):default;
                regions.Add(new SurfaceRegionData(projection, size, heights, masks, blend, mode, baseDigest, policy, priority, weights, kind, sourceDigest, context, erosion,provenance,profile));
            }
            var stamps = new List<SurfaceCraterStamp>(); count = ReadCount(reader, MaximumStamps);
            for (int i = 0; i < count; i++)
                stamps.Add(new SurfaceCraterStamp(reader.ReadUInt64(), reader.ReadUInt64(), ReadVector(reader),
                    reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
            var materialProfile=format>=4&&reader.ReadBoolean()?SurfaceAutomaticMaterialProfile.ReadVersioned(reader):default;
            SurfaceResolvedMaterials resolved=null;
            if(format>=4&&reader.ReadBoolean())
            {
                if(!materialProfile.IsValid)throw new InvalidDataException("Resolved weights require captured automatic material rules.");
                var geometry=ReadHash(reader);var rules=ReadHash(reader);var resolvedDigest=ReadHash(reader);
                var materialTiles=new List<SurfaceMaterialTileData>();count=ReadCount(reader,MaximumTiles);
                for(int i=0;i<count;i++)
                {
                    var key=new SurfaceTileKey(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());int size=reader.ReadInt32();
                    if(!key.IsValid||!CubeSurface.ValidResolution(size))throw new InvalidDataException("Invalid resolved material tile.");
                    materialTiles.Add(new SurfaceMaterialTileData(key,size,ReadAttributes(reader,checked((size+1)*(size+1)),ref samples)));
                }
                var materialLayers=new List<SurfaceMaterialLayerData>();count=ReadCount(reader,MaximumRegions);
                for(int i=0;i<count;i++)
                {
                    var projection=ReadProjection(reader);var size=new int2(reader.ReadInt32(),reader.ReadInt32());double blend=reader.ReadDouble();int priority=reader.ReadInt32();
                    if(!projection.IsValid||math.any(size<1)||math.any(size>4096))throw new InvalidDataException("Invalid resolved material layer.");
                    materialLayers.Add(new SurfaceMaterialLayerData(projection,size,blend,priority,ReadAttributes(reader,checked((size.x+1)*(size.y+1)),ref samples)));
                }
                var stampLayers=new List<SurfaceStampMaterialData>();
                if(format>=5)
                {
                    count=ReadCount(reader,MaximumStamps);
                    for(int i=0;i<count;i++)
                    {
                        ulong high=reader.ReadUInt64(),low=reader.ReadUInt64();double cell=reader.ReadDouble();
                        var projection=ReadProjection(reader);var size=new int2(reader.ReadInt32(),reader.ReadInt32());double blend=reader.ReadDouble();
                        if(!projection.IsValid||math.any(size<2)||math.any(size>4096))throw new InvalidDataException("Invalid stamp material grid.");
                        stampLayers.Add(new SurfaceStampMaterialData(high,low,cell,projection,size,blend,ReadAttributes(reader,checked((size.x+1)*(size.y+1)),ref samples)));
                    }
                }
                resolved=new SurfaceResolvedMaterials(geometry,rules,materialTiles,materialLayers,stampLayers);
                if(resolved.ContentDigest!=resolvedDigest)throw new InvalidDataException("Resolved material cache digest differs from its payload.");
            }
            var snapshot = new SurfaceSnapshot(recipe, revision, level, resolution, tiles, detail, regions, stamps,materialProfile,resolved);
            if (snapshot.ContentDigest != digest) throw new InvalidDataException("Surface snapshot content digest differs from its payload.");
            return snapshot;
        }
        static void ValidateCounts(SurfaceSnapshot snapshot)
        {
            if (snapshot.Tiles.Count > MaximumTiles || snapshot.Regions.Count > MaximumRegions || snapshot.Stamps.Count > MaximumStamps)
                throw new InvalidDataException("Surface snapshot exceeds record limits.");
            int count = 0; foreach (var tile in snapshot.Tiles)
            {
                AddSamples(ref count, tile.SampleCount);
                if (tile.HasMaterialWeights) AddSamples(ref count, checked(tile.SampleCount * 4));
                if (tile.HasErosionData) AddSamples(ref count, checked(tile.SampleCount * 4));
            }
            foreach (var region in snapshot.Regions)
            {
                AddSamples(ref count, region.SampleCount); AddSamples(ref count, region.SampleCount);
                if (region.HasMaterialWeights) AddSamples(ref count, checked(region.SampleCount * 4));
                if (region.HasErosionData) AddSamples(ref count, checked(region.SampleCount * 4));
            }
            if(snapshot.ResolvedMaterials!=null)
            {
                if(snapshot.ResolvedMaterials.Tiles.Count>MaximumTiles||snapshot.ResolvedMaterials.Layers.Count>MaximumRegions)throw new InvalidDataException("Resolved material cache exceeds record limits.");
                if(snapshot.ResolvedMaterials.StampLayers.Count>MaximumStamps)throw new InvalidDataException("Resolved stamp cache exceeds record limits.");
                foreach(var tile in snapshot.ResolvedMaterials.Tiles)AddSamples(ref count,checked(tile.SampleCount*4));
                foreach(var layer in snapshot.ResolvedMaterials.Layers)AddSamples(ref count,checked(layer.SampleCount*4));
                foreach(var layer in snapshot.ResolvedMaterials.StampLayers)AddSamples(ref count,checked(layer.SampleCount*4));
            }
        }
        static float[] ReadSamples(BinaryReader reader, int expected, ref int samples)
        {
            if (ReadCount(reader, MaximumSamples) != expected) throw new InvalidDataException("Sample count differs from vertex-grid dimensions.");
            AddSamples(ref samples, expected); RequireRemaining(reader, (long)expected * 4);
            var heights = new float[expected]; for (int i = 0; i < expected; i++) heights[i] = reader.ReadSingle(); return heights;
        }
        static void AddSamples(ref int samples, int count)
        {
            if (count < 0 || count > MaximumSamples - samples) throw new InvalidDataException("Surface snapshot exceeds its sample budget.");
            samples += count;
        }
        static float4[] ReadAttributes(BinaryReader reader, int expected, ref int samples)
        {
            AddSamples(ref samples, checked(expected * 4)); RequireRemaining(reader, (long)expected * 16);
            var values = new float4[expected]; for (int i = 0; i < expected; i++) values[i] = new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return values;
        }
        static int ReadCount(BinaryReader reader, int maximum)
        { int value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidDataException("Invalid surface record count."); return value; }
        static void RequireRemaining(BinaryReader reader, long count)
        {
            if (reader.BaseStream.CanSeek && reader.BaseStream.Length - reader.BaseStream.Position < count)
                throw new InvalidDataException("Surface snapshot is truncated before sample allocation.");
        }
        static SurfaceContentHash ReadHash(BinaryReader reader) => new SurfaceContentHash(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        static double3 ReadVector(BinaryReader reader) => new double3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        static SurfaceRegionProjection ReadProjection(BinaryReader reader) => new SurfaceRegionProjection(ReadVector(reader), ReadVector(reader), ReadVector(reader),
            reader.ReadDouble(), new double2(reader.ReadDouble(), reader.ReadDouble()), new double2(reader.ReadDouble(), reader.ReadDouble()));
    }
}
