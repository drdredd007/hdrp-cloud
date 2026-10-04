using System;
using System.Collections.Generic;
using System.IO;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Portable authored layers with metric projection and a verified delta baseline.</summary>
    public static class PlanetTerrainExchangeService
    {
        public static PlanetTerrainExchangeManifest Export(SurfaceSnapshot snapshot, string planetInstanceId,
            string directory, PlanetSurfaceAddress address, double widthMetres, int cells, int guardSamples,
            Func<bool> cancelled = null)
        {
            if (snapshot == null || !address.IsValid || !math.isfinite(widthMetres) || widthMetres <= 0 ||
                cells < 2 || cells > 4096 || guardSamples < 0 || guardSamples * 2 >= cells)
                throw new ArgumentException("Use a finite metric region and a vertex grid of 2–4096 cells.");
            if (snapshot.Stamps.Count != 0)
                throw new InvalidOperationException("Authoring exchange requires a bake snapshot without runtime impact commands.");
            double spacing = widthMetres / (cells - guardSamples * 2);
            double fullWidth = widthMetres + guardSamples * spacing * 2;
            var manifest = new PlanetTerrainExchangeManifest
            {
                PlanetInstanceId = planetInstanceId, BaseDigest = snapshot.Revision.BaseDigest.ToString(),
                RecipeDigest = snapshot.Revision.RecipeDigest.ToString(), SourceContentDigest = snapshot.ContentDigest.ToString(),
                LayerId = Guid.NewGuid().ToString("N"), Width = cells + 1, Height = cells + 1, GuardSamples = guardSamples,
                Radius = snapshot.Recipe.Radius, Latitude = address.Latitude, Longitude = address.Longitude, Heading = address.Heading,
                MinimumX = -fullWidth * .5, MaximumX = fullWidth * .5, MinimumZ = -fullWidth * .5, MaximumZ = fullWidth * .5,
                HeightOffset = snapshot.MinimumHeight, HeightScale = math.max(1, snapshot.MaximumHeight - snapshot.MinimumHeight),
                IncludedDetailBands = snapshot.Detail.AmplitudeMetres > 0 ? "MetricDetail" : "None",
                SuppressRuntimeDetail = snapshot.Detail.AmplitudeMetres > 0
            };
            manifest.Validate(); var samples = new float[checked(manifest.Width * manifest.Height)];
            float4[] materials=null,erosion=null;SurfaceChannels? channels=null;
            using (var native = snapshot.CreateNative(Allocator.TempJob))
            {
                var view = native.View;
                for (int y = 0; y < manifest.Height; y++)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException("Terrain export cancelled.");
                    for (int x = 0; x < manifest.Width; x++)
                    {
                        var direction=manifest.Direction(x,y);
                        var status = SurfaceSampler.TrySampleHeight(view, direction, out var height);
                        if (status != SurfaceSampleStatus.Ready) throw new InvalidOperationException("Required export tile is not ready: " + status);
                        samples[y * manifest.Width + x] = (float)height;
                        var attributeStatus=SurfaceSampler.TrySampleAttributes(view,direction,out var attributes);
                        var present=attributeStatus==SurfaceSampleStatus.Ready?attributes.Channels:SurfaceChannels.None;
                        if(channels==null)
                        {
                            channels=present;
                            if((present&SurfaceChannels.MaterialWeights)!=0)materials=new float4[samples.Length];
                            if((present&SurfaceChannels.ErosionData)!=0)erosion=new float4[samples.Length];
                        }
                        if(channels.Value!=present)throw new InvalidOperationException("The export crosses incomplete attribute residency. Load the whole region before exporting masks.");
                        if(materials!=null)materials[y*manifest.Width+x]=attributes.MaterialWeights;
                        if(erosion!=null)erosion[y*manifest.Width+x]=attributes.ErosionData;
                    }
                }
            }
            // Export only after every required sample has succeeded.
            PlanetTerrainExchange.Export(directory, manifest, samples);
            if(materials!=null)manifest.MaterialWeightFiles=PlanetTerrainMaskExchange.Export(directory,manifest,materials,true);
            if(erosion!=null)manifest.ErosionFiles=PlanetTerrainMaskExchange.Export(directory,manifest,erosion,false);
            File.WriteAllText(Path.Combine(directory,"manifest.json"),JsonUtility.ToJson(manifest,true),new System.Text.UTF8Encoding(false));
            return manifest;
        }

        public static SurfaceRegionData Import(SurfaceSnapshot current, string planetInstanceId, string manifestPath,
            string editedHeightPath, SurfaceRegionMode mode, int priority,string editedMaterialMaskDirectory=null,string editedErosionMaskDirectory=null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            var manifest = PlanetTerrainExchange.ReadManifest(manifestPath);
            bool delta = mode == SurfaceRegionMode.Delta;
            manifest.ValidateAgainst(planetInstanceId, current.Revision.BaseDigest.ToString(), current.Recipe.Radius, delta);
            var heights = PlanetTerrainExchange.ReadHeight(editedHeightPath, manifest);
            var materials=string.IsNullOrWhiteSpace(editedMaterialMaskDirectory)?null:PlanetTerrainMaskExchange.ImportMaterials(editedMaterialMaskDirectory,manifest);
            var erosion=string.IsNullOrWhiteSpace(editedErosionMaskDirectory)?null:PlanetTerrainMaskExchange.ImportErosion(editedErosionMaskDirectory,manifest);
            if (delta)
            {
                if (!string.Equals(manifest.SourceContentDigest, current.ContentDigest.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The delta baseline includes different authored layers or detail. Export this revision again.");
                if (string.IsNullOrWhiteSpace(manifest.ExportedHeightFile) || Path.GetFileName(manifest.ExportedHeightFile) != manifest.ExportedHeightFile)
                    throw new InvalidDataException("The original exported height file is missing or invalid.");
                string originalPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath)), manifest.ExportedHeightFile);
                var originalBytes = File.ReadAllBytes(originalPath);
                if (!string.Equals(PlanetTerrainExchange.Digest(originalBytes), manifest.ExportedHeightSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The exported baseline was changed. A delta requires the original verified samples.");
                var original = PlanetTerrainExchange.DecodeRaw(originalBytes, manifest);
                for (int i = 0; i < heights.Length; i++) heights[i] -= original[i];
            }
            var up = PlanetSurfaceCoordinates.Direction(manifest.Latitude, manifest.Longitude);
            double longitude = math.radians(manifest.Longitude % 360);
            var east = new double3(-math.sin(longitude), 0, math.cos(longitude));
            var north = math.normalize(math.cross(east, up)); var right = math.normalize(math.cross(up, north));
            double a = math.radians(manifest.Heading % 360), s = math.sin(a), c = math.cos(a);
            var projection = new SurfaceRegionProjection(up, right * c - north * s, north * c + right * s,
                manifest.Radius, new double2(manifest.MinimumX, manifest.MinimumZ), new double2(manifest.MaximumX, manifest.MaximumZ));
            double guardMetres = manifest.GuardSamples * (manifest.MaximumX - manifest.MinimumX) / (manifest.Width - 1);
            return new SurfaceRegionData(projection, new int2(manifest.Width - 1, manifest.Height - 1), heights,
                blendMetres: guardMetres, mode: mode, baseDigest: current.Revision.BaseDigest,
                detailPolicy: delta || !manifest.SuppressRuntimeDetail ? SurfaceDetailPolicy.Preserve : SurfaceDetailPolicy.Suppress,
                priority: priority,materialWeights:materials,erosionData:erosion,
                materialProvenance:materials==null?SurfaceMaterialProvenance.Automatic:SurfaceMaterialProvenance.Authored);
        }

        public static SurfaceSnapshot PublishLayer(SurfaceSnapshot current, SurfaceRegionData layer, bool replacePriority = false,
            bool repairAutomaticMaterials = true,Action<SurfaceBakeProgress> progress=null,Func<bool> cancelled=null)
        {
            if (current == null || layer == null) throw new ArgumentNullException();
            if(!current.HasAutomaticMaterials && layer.MaterialProvenance==SurfaceMaterialProvenance.Automatic)
                throw new InvalidOperationException("Height-only import needs the original captured automatic material profile. Explicitly migrate this legacy dataset from its Terrain Recipe before publishing; supplying a mask is an authored override, not an inferred climate profile.");
            var regions = new List<SurfaceRegionData>();
            foreach (var existing in current.Regions)
            {
                if (existing.Priority != layer.Priority) regions.Add(existing);
                else if (!replacePriority) throw new InvalidOperationException("Layer priority is already occupied. Choose another priority or explicitly replace this authored layer.");
            }
            regions.Add(layer);
            var candidate = new SurfaceSnapshot(current.Recipe, new SurfaceRevision(current.Revision.RecipeDigest,
                current.Revision.BaseDigest, checked(current.Revision.Epoch + 1)), current.CanonicalTileLevel,
                current.Resolution, current.Tiles, current.Detail, regions, current.Stamps,current.AutomaticMaterialProfile,structuralField:current.StructuralField,orogenDetail:current.OrogenDetail);
            return current.HasAutomaticMaterials && repairAutomaticMaterials ? SurfaceMaterialRepair.Rebuild(candidate,progress:progress,cancelled:cancelled) : candidate;
        }

        public static SurfaceSnapshot PreserveAuthoredLayers(SurfaceSnapshot newBake, SurfaceSnapshot previous)
        {
            if (newBake == null) throw new ArgumentNullException(nameof(newBake));
            if (previous == null) return newBake;
            if (newBake.Recipe.Radius != previous.Recipe.Radius)
                throw new InvalidOperationException("Changing the radius requires explicit migration of authored metric regions.");
            var authored=new List<SurfaceRegionData>();
            foreach (var layer in previous.Regions)
            {
                if(layer.Kind!=SurfaceRegionKind.Authored)continue;
                if (layer.Mode == SurfaceRegionMode.Delta && layer.BaseDigest != newBake.Revision.BaseDigest)
                    throw new InvalidOperationException("Rebake would invalidate an authored delta. Export and rebase it explicitly before publishing.");
                if(newBake.HasAutomaticMaterials && layer.HasMaterialWeights && layer.MaterialProvenance==SurfaceMaterialProvenance.LegacyBaked)
                    authored.Add(new SurfaceRegionData(layer.Projection,layer.Resolution,layer.CopyHeights(),layer.CopyBlendMask(),layer.BlendMetres,layer.Mode,layer.BaseDigest,
                        layer.DetailPolicy,layer.Priority,layer.CopyMaterialWeights(),layer.Kind,layer.SourceContentDigest,layer.ContextMetres,layer.CopyErosionData(),
                        SurfaceMaterialProvenance.Authored));
                else authored.Add(layer);
            }
            if (previous.Stamps.Count != 0) throw new InvalidOperationException("Runtime impact snapshots cannot be replaced by an authoring rebake.");
            return new SurfaceSnapshot(newBake.Recipe, new SurfaceRevision(newBake.Revision.RecipeDigest,
                newBake.Revision.BaseDigest, checked(previous.Revision.Epoch + 1)), newBake.CanonicalTileLevel,
                newBake.Resolution, newBake.Tiles, newBake.Detail, authored,automaticMaterialProfile:newBake.AutomaticMaterialProfile,structuralField:newBake.StructuralField,orogenDetail:newBake.OrogenDetail);
        }
    }
}
