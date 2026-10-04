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
        public const int FormatVersion = 10;
        public const int OrogenBandsFormatVersion = 9;
        public const int LandformFormatVersion = 8;
        public const int RetainedFormatVersion = 7;
        public const int MaximumTiles = 65536, MaximumRegions = 4096, MaximumStamps = 65536;
        public const int MaximumSamples = 64 * 1024 * 1024;

        public static void Write(BinaryWriter writer, SurfaceSnapshot snapshot)
        {
            if (writer == null || snapshot == null) throw new ArgumentNullException(writer == null ? nameof(writer) : nameof(snapshot));
            ValidateCounts(snapshot);
            int format = snapshot.OrogenDetail != null ? (snapshot.OrogenDetail.Recipe.Morphology == 1 ? OrogenBandsFormatVersion : FormatVersion) : snapshot.Recipe.AlgorithmVersion == SurfaceRecipe.LandformAuthorityAlgorithmVersion ? LandformFormatVersion : RetainedFormatVersion;
            writer.Write(Magic); writer.Write(format); SurfaceHashing.WriteRecipe(writer, snapshot.Recipe);
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
            writer.Write(snapshot.StructuralField != null);
            if (snapshot.StructuralField != null) WriteStructure(writer, snapshot.StructuralField);
            if (format >= 9) { writer.Write(snapshot.OrogenDetail != null); if (snapshot.OrogenDetail != null) WriteOrogenDetail(writer, snapshot.OrogenDetail, format); }
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
            var structure = format >= 6 && reader.ReadBoolean() ? ReadStructure(reader, ref samples,format) : null;
            var orogenDetail = format >= 9 && reader.ReadBoolean() ? ReadOrogenDetail(reader, ref samples, format) : null;
            var snapshot = new SurfaceSnapshot(recipe, revision, level, resolution, tiles, detail, regions, stamps,materialProfile,resolved,structure,orogenDetail);
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
            if (snapshot.StructuralField != null) AddSamples(ref count, checked(6 * (snapshot.StructuralField.RawMacroResolution + 1) * (snapshot.StructuralField.RawMacroResolution + 1)));
            if (snapshot.OrogenDetail != null) AddSamples(ref count, checked(snapshot.OrogenDetail.SampleCount * 12));
            if(snapshot.ResolvedMaterials!=null)
            {
                if(snapshot.ResolvedMaterials.Tiles.Count>MaximumTiles||snapshot.ResolvedMaterials.Layers.Count>MaximumRegions)throw new InvalidDataException("Resolved material cache exceeds record limits.");
                if(snapshot.ResolvedMaterials.StampLayers.Count>MaximumStamps)throw new InvalidDataException("Resolved stamp cache exceeds record limits.");
                foreach(var tile in snapshot.ResolvedMaterials.Tiles)AddSamples(ref count,checked(tile.SampleCount*4));
                foreach(var layer in snapshot.ResolvedMaterials.Layers)AddSamples(ref count,checked(layer.SampleCount*4));
                foreach(var layer in snapshot.ResolvedMaterials.StampLayers)AddSamples(ref count,checked(layer.SampleCount*4));
            }
        }
        static void WriteOrogenDetail(BinaryWriter writer, WorldOrogenDetailField field, int format)
        {
            var r = field.Recipe;
            writer.Write(field.Version); writer.Write(r.Version); writer.Write(r.Enabled); writer.Write(r.Seed);
            writer.Write(r.Strength); writer.Write(r.MinimumWavelengthMetres); writer.Write(r.ConditioningResolution);
            writer.Write(r.MaximumAmplitudeMetres); writer.Write(r.ReliefFraction); writer.Write(r.CoastFadeMetres);
            if (format >= 10) writer.Write(r.Morphology);
            writer.Write(field.SourceRadius); writer.Write(field.SeaLevel); SurfaceHashing.WriteHash(writer, field.SourceBaseDigest);
            writer.Write(field.Resolution); SurfaceHashing.WriteHash(writer, field.ContentDigest);
            SurfaceHashing.WriteAttributes(writer, field.CopyGeometry()); SurfaceHashing.WriteAttributes(writer, field.CopyFlow()); SurfaceHashing.WriteAttributes(writer, field.CopyEnvironment());
        }
        static WorldOrogenDetailField ReadOrogenDetail(BinaryReader reader, ref int samples, int format)
        {
            if (reader.ReadInt32() != WorldOrogenDetailField.CurrentVersion || reader.ReadInt32() != WorldOrogenDetailRecipe.CurrentVersion)
                throw new InvalidDataException("Unsupported World Orogen detail version.");
            bool enabled = reader.ReadBoolean(); int seed = reader.ReadInt32();
            double strength = reader.ReadDouble(), wavelength = reader.ReadDouble(); int conditioningResolution = reader.ReadInt32();
            double amplitude = reader.ReadDouble(), reliefFraction = reader.ReadDouble(), coastFade = reader.ReadDouble();
            int morphology = format >= 10 ? reader.ReadInt32() : 1;
            var recipe = new WorldOrogenDetailRecipe(enabled, seed, strength, wavelength,
                conditioningResolution, amplitude, reliefFraction, coastFade, morphology);
            double radius = reader.ReadDouble(), sea = reader.ReadDouble(); var source = ReadHash(reader);
            int resolution = reader.ReadInt32(); var digest = ReadHash(reader);
            if (!recipe.IsValid || resolution != recipe.ConditioningResolution || !math.isfinite(radius) || radius <= 0 ||
                !math.isfinite(sea) || sea <= -radius || !source.IsValid) throw new InvalidDataException("Invalid World Orogen conditioning header.");
            int count = checked(6 * (resolution + 1) * (resolution + 1));
            if (checked(count * 12) > MaximumSamples - samples) throw new InvalidDataException("World Orogen conditioning exceeds the sample budget.");
            RequireRemaining(reader, checked((long)count * 48));
            var geometry = ReadAttributes(reader, count, ref samples); var flow = ReadAttributes(reader, count, ref samples);
            var environment = ReadAttributes(reader, count, ref samples);
            var field = new WorldOrogenDetailField(recipe, radius, sea, source, resolution, geometry, flow, environment);
            if (field.ContentDigest != digest) throw new InvalidDataException("World Orogen detail digest differs from its payload.");
            return field;
        }
        static void WriteStructure(BinaryWriter writer, SurfaceStructuralField field)
        {
            writer.Write(field.MorphologyVersion); SurfaceHashing.WriteRecipe(writer, field.SourceRecipe);
            SurfaceHashing.WriteHash(writer, field.SourceBaseDigest); SurfaceHashing.WriteHash(writer, field.ContentDigest);
            writer.Write(field.RawMacroResolution); writer.Write(field.ShelfWidthMetres); writer.Write(field.BeltWidthMetres);
            writer.Write(field.RegionalFeatureScaleMetres); writer.Write(field.CoastThresholdMetres); writer.Write(field.MountainFraction);
            writer.Write(field.MinimumErosionResidual); writer.Write(field.MaximumErosionResidual);
            writer.Write(field.Provinces.Count);
            foreach (var province in field.Provinces)
            {
                SurfaceHashing.WriteVector(writer, province.Center); SurfaceHashing.WriteVector(writer, province.AngularMotion);
                writer.Write(province.Continental); writer.Write(province.Buoyancy);
            }
            writer.Write(field.Boundaries.Count);
            foreach (var edge in field.Boundaries)
            {
                writer.Write(edge.ProvinceA); writer.Write(edge.ProvinceB); writer.Write(edge.StartVertex); writer.Write(edge.EndVertex);
                SurfaceHashing.WriteVector(writer, edge.Start); SurfaceHashing.WriteVector(writer, edge.End); writer.Write(edge.Convergence);
            }
            int count = checked(6 * (field.RawMacroResolution + 1) * (field.RawMacroResolution + 1)); writer.Write(count);
            for (int i = 0; i < count; i++) writer.Write(field.RawMacroAt(i));
            if(field.DrainageField!=null)WriteDrainage(writer,field.DrainageField);
            if(field.LandformField!=null)WriteLandform(writer,field.LandformField);
        }
        static SurfaceStructuralField ReadStructure(BinaryReader reader, ref int samples,int format)
        {
            int morphology=reader.ReadInt32();
            if(morphology<1||morphology>3||(morphology==2&&format<7)||(morphology==3&&format<8))throw new InvalidDataException("Unsupported captured structural authority.");
            int schema = reader.ReadInt32(), algorithm = reader.ReadInt32(); var style = (SurfaceStyle)reader.ReadInt32(); int seed = reader.ReadInt32();
            double radius = reader.ReadDouble();
            double sea = reader.ReadDouble(), minimum = reader.ReadDouble(), maximum = reader.ReadDouble();
            var source = new SurfaceRecipe(seed, style, radius, minimum, maximum, sea, algorithm, schema);
            var baseDigest = ReadHash(reader); var digest = ReadHash(reader); int resolution = reader.ReadInt32();
            if (!source.IsValid || algorithm != (morphology==1?SurfaceRecipe.StructuralAuthorityAlgorithmVersion:morphology==2?SurfaceRecipe.DrainageAuthorityAlgorithmVersion:SurfaceRecipe.LandformAuthorityAlgorithmVersion) || !CubeSurface.ValidResolution(resolution) || resolution > 512)
                throw new InvalidDataException("Invalid captured structural grid before allocation.");
            double shelf = reader.ReadDouble(), belt = reader.ReadDouble(), feature = reader.ReadDouble(), coast = reader.ReadDouble(), mountain = reader.ReadDouble();
            double minResidual = reader.ReadDouble(), maxResidual = reader.ReadDouble();
            int count = ReadCount(reader, 64); if (count < 8) throw new InvalidDataException("Invalid structural province count.");
            RequireRemaining(reader, (long)count * 57); var provinces = new SurfaceGeologicalProvince[count];
            for (int i = 0; i < count; i++) provinces[i] = new SurfaceGeologicalProvince(ReadVector(reader), ReadVector(reader), reader.ReadBoolean(), reader.ReadDouble());
            count = ReadCount(reader, 186); if (count != 3 * provinces.Length - 6) throw new InvalidDataException("Incomplete structural edge graph.");
            RequireRemaining(reader, (long)count * 72); var edges = new SurfaceGeologicalBoundary[count];
            for (int i = 0; i < count; i++)
            {
                int a = reader.ReadInt32(), b = reader.ReadInt32(), first = reader.ReadInt32(), last = reader.ReadInt32();
                var start = ReadVector(reader); var end = ReadVector(reader); double convergence = reader.ReadDouble();
                if (a < 0 || b <= a || b >= provinces.Length) throw new InvalidDataException("Invalid structural adjacency.");
                edges[i] = new SurfaceGeologicalBoundary(a, b, first, last, start, end, convergence, provinces[a].Continental, provinces[b].Continental);
            }
            var raw = ReadSamples(reader, checked(6 * (resolution + 1) * (resolution + 1)), ref samples);
            var drainage=morphology==2?ReadDrainage(reader,source):null;
            var landform=morphology==3?ReadLandform(reader,source):null;
            var field = new SurfaceStructuralField(source, baseDigest, resolution, raw, provinces, edges, shelf, belt, feature, coast, mountain, minResidual, maxResidual,drainageField:drainage,landformField:landform);
            if (field.ContentDigest != digest) throw new InvalidDataException("Captured structural payload digest differs from its manifest.");
            return field;
        }
        static void WriteDrainage(BinaryWriter writer,SurfaceDrainageField field)
        {
            writer.Write(SurfaceDrainageField.CurrentVersion);SurfaceHashing.WriteHash(writer,field.ContentDigest);
            writer.Write(field.CoastInfluenceMetres);writer.Write(field.Landmasses.Count);
            foreach(var mass in field.Landmasses)
            {SurfaceHashing.WriteVector(writer,mass.Center);SurfaceHashing.WriteVector(writer,mass.Right);SurfaceHashing.WriteVector(writer,mass.Forward);writer.Write(mass.FirstVertex);writer.Write(mass.VertexCount);}
            writer.Write(field.CoastVertices.Count);foreach(var p in field.CoastVertices){writer.Write(p.x);writer.Write(p.y);}
            writer.Write(field.Nodes.Count);foreach(var node in field.Nodes)
            {SurfaceHashing.WriteVector(writer,node.Direction);writer.Write(node.BedHeight);writer.Write(node.DrainageArea);writer.Write(node.HillslopeWidth);writer.Write(node.DivideHeight);writer.Write(node.Parent);writer.Write(node.Outlet);writer.Write(node.StrahlerOrder);}
        }
        static SurfaceDrainageField ReadDrainage(BinaryReader reader,SurfaceRecipe recipe)
        {
            if(reader.ReadInt32()!=SurfaceDrainageField.CurrentVersion)throw new InvalidDataException("Unsupported captured catchment authority.");
            var digest=ReadHash(reader);double influence=reader.ReadDouble();int count=ReadCount(reader,SurfaceDrainageField.MaximumLandmasses);
            if(count<1)throw new InvalidDataException("Captured coast needs a landmass.");RequireRemaining(reader,count*80L);
            var masses=new SurfaceCoastLandmass[count];
            for(int i=0;i<count;i++)masses[i]=new SurfaceCoastLandmass(ReadVector(reader),ReadVector(reader),ReadVector(reader),reader.ReadInt32(),reader.ReadInt32());
            count=ReadCount(reader,SurfaceDrainageField.MaximumCoastVertices);RequireRemaining(reader,count*16L);var vertices=new double2[count];
            for(int i=0;i<count;i++)vertices[i]=new double2(reader.ReadDouble(),reader.ReadDouble());
            count=ReadCount(reader,SurfaceDrainageField.MaximumNodes);RequireRemaining(reader,count*68L);var nodes=new SurfaceDrainageNode[count];
            for(int i=0;i<count;i++)nodes[i]=new SurfaceDrainageNode(ReadVector(reader),reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());
            var field=new SurfaceDrainageField(recipe,masses,vertices,nodes,coastInfluenceMetres:influence);
            if(field.ContentDigest!=digest)throw new InvalidDataException("Captured drainage payload digest differs from its manifest.");
            return field;
        }
        static void WriteLandform(BinaryWriter writer,SurfaceLandformField field)
        {
            writer.Write(SurfaceLandformField.CurrentVersion);SurfaceHashing.WriteHash(writer,field.ContentDigest);
            writer.Write(field.Controls.Count);writer.Write(field.Channels.Count);writer.Write(field.Divides.Count);writer.Write(field.Cells.Count);writer.Write(field.CellDivideReferenceCount);
            foreach(var c in field.Controls){SurfaceHashing.WriteVector(writer,c.Direction);SurfaceHashing.WriteVector(writer,c.Gradient);writer.Write(c.Height);writer.Write(c.SupportMetres);writer.Write(c.VariationLimit);}
            foreach(var c in field.Channels){writer.Write(c.Control);writer.Write(c.Parent);writer.Write(c.Outlet);writer.Write(c.Strahler);writer.Write(c.Area);writer.Write(c.RiseScale);}
            foreach(var d in field.Divides){writer.Write(d.Control);writer.Write(d.FirstChannel);writer.Write(d.SecondChannel);writer.Write(d.Flags);}
            foreach(var c in field.Cells){SurfaceHashing.WriteKey(writer,c.Key);writer.Write(c.Channel);writer.Write(c.FirstDivide);writer.Write(c.DivideCount);}
            for(int i=0;i<field.CellDivideReferenceCount;i++)writer.Write(field.CellDivideAt(i));
        }
        static SurfaceLandformField ReadLandform(BinaryReader reader,SurfaceRecipe recipe)
        {
            if(reader.ReadInt32()!=SurfaceLandformField.CurrentVersion)throw new InvalidDataException("Unsupported captured landform authority.");
            var digest=ReadHash(reader);int controlCount=ReadCount(reader,SurfaceLandformField.MaximumControls),channelCount=ReadCount(reader,SurfaceLandformField.MaximumCells);
            int divideCount=ReadCount(reader,SurfaceLandformField.MaximumControls),cellCount=ReadCount(reader,SurfaceLandformField.MaximumCells),refCount=ReadCount(reader,8*SurfaceLandformField.MaximumCells);
            long authority=SurfaceLandformField.EstimateAuthorityBytes(controlCount,channelCount,divideCount,cellCount,refCount);
            if(controlCount<6||channelCount!=cellCount||cellCount<6||authority+SurfaceLandformField.MaximumIndexNodes*32L+SurfaceLandformField.MaximumReferences*4L>SurfaceLandformField.MaximumResidentBytes)
                throw new InvalidDataException("Captured landform counts exceed authority/index admission before allocation.");
            RequireRemaining(reader,authority-512);
            var controls=new SurfaceLandformControl[controlCount];var channels=new SurfaceLandformChannel[channelCount];var divides=new SurfaceLandformDivide[divideCount];
            var cells=new SurfaceLandformCell[cellCount];var refs=new int[refCount];
            for(int i=0;i<controls.Length;i++){var d=ReadVector(reader);var g=ReadVector(reader);controls[i]=new SurfaceLandformControl(d,reader.ReadDouble(),g,reader.ReadDouble(),reader.ReadDouble());}
            for(int i=0;i<channels.Length;i++)channels[i]=new SurfaceLandformChannel(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadDouble(),reader.ReadDouble());
            for(int i=0;i<divides.Length;i++)divides[i]=new SurfaceLandformDivide(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());
            for(int i=0;i<cells.Length;i++){var key=new SurfaceTileKey(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());cells[i]=new SurfaceLandformCell(key,reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());}
            for(int i=0;i<refs.Length;i++)refs[i]=reader.ReadInt32();
            var field=new SurfaceLandformField(recipe,controls,channels,divides,cells,refs);
            if(field.ContentDigest!=digest)throw new InvalidDataException("Captured landform authority differs from its content digest.");
            return field;
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
