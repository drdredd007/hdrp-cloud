using System;
using System.Collections.Generic;
using System.IO;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetTerrainMapMode { Height, MaterialGRSS, Grass, Sand, Rock, Snow, Flow, Wetness, Wear, Deposition }

    public readonly struct PlanetTerrainPreviewRequest
    {
        public readonly double Latitude, Longitude, Heading, WidthMetres, ObserverHeight;
        public readonly int Samples;
        public readonly PlanetTerrainMapMode Mode;
        public readonly long MaximumWorkingBytes, MaximumCompositionChecks;
        public PlanetTerrainPreviewRequest(double latitude, double longitude, double heading, double widthMetres,
            int samples = 64, PlanetTerrainMapMode mode = PlanetTerrainMapMode.Height, double observerHeight = 100,
            long maximumWorkingBytes = 128L * 1024 * 1024, long maximumCompositionChecks = 16L * 1024 * 1024)
        {
            Latitude = latitude; Longitude = longitude; Heading = heading; WidthMetres = widthMetres;
            Samples = samples; Mode = mode; ObserverHeight = observerHeight;
            MaximumWorkingBytes = maximumWorkingBytes; MaximumCompositionChecks = maximumCompositionChecks;
        }
        public bool IsValid => math.isfinite(Latitude) && math.abs(Latitude) <= 90 && math.isfinite(Longitude) &&
            math.isfinite(Heading) && math.isfinite(WidthMetres) && WidthMetres > 0 &&
            (Samples == 64 || Samples == 128) && (int)Mode >= (int)PlanetTerrainMapMode.Height && (int)Mode <= (int)PlanetTerrainMapMode.Deposition &&
            math.isfinite(ObserverHeight) && ObserverHeight >= 0 && MaximumWorkingBytes > 0 && MaximumCompositionChecks > 0;
        public SurfaceRegionProjection Projection(double radius)
        {
            if (!IsValid || !math.isfinite(radius) || radius <= 0 || WidthMetres > radius * 2)
                throw new ArgumentException("Preview requires 64/128 samples, a finite metric region no wider than twice the radius, and positive budgets.");
            double lat = Latitude * Math.PI / 180, lon = (Longitude % 360) * Math.PI / 180, angle = (Heading % 360) * Math.PI / 180;
            var anchor = new double3(math.cos(lat) * math.cos(lon), math.sin(lat), math.cos(lat) * math.sin(lon));
            var east = new double3(-math.sin(lon), 0, math.cos(lon)); var north = math.cross(east, anchor);
            return new SurfaceRegionProjection(anchor, east * math.cos(angle) - north * math.sin(angle),
                north * math.cos(angle) + east * math.sin(angle), radius, new double2(-WidthMetres * .5), new double2(WidthMetres * .5));
        }
    }

    /// <summary>Copied on the editor thread; contains no Unity object or GPU resource.</summary>
    public readonly struct PlanetTerrainScatterPreviewSpecies
    {
        public readonly SurfaceScatterSpecies Placement;
        public readonly double RenderDistance, ShadowDistance, BoundingRadius;
        public PlanetTerrainScatterPreviewSpecies(SurfaceScatterSpecies placement, double renderDistance, double shadowDistance, double boundingRadius)
        {
            Placement = placement; RenderDistance = renderDistance; ShadowDistance = shadowDistance; BoundingRadius = boundingRadius;
            if (!placement.IsValid || !math.isfinite(renderDistance) || renderDistance <= 0 || !math.isfinite(shadowDistance) ||
                shadowDistance < renderDistance || !math.isfinite(boundingRadius) || boundingRadius < 0)
                throw new ArgumentException("A numerical scatter preview requires captured valid placement and metric interest.");
        }
    }
    public readonly struct PlanetTerrainScatterPreviewBudget
    {
        public readonly int MaximumCells, MaximumCandidates, NewCellsPerFrame;
        public readonly long MaximumBytes;
        public PlanetTerrainScatterPreviewBudget(int cells, int candidates, long bytes, int newCellsPerFrame)
        { MaximumCells = cells; MaximumCandidates = candidates; MaximumBytes = bytes; NewCellsPerFrame = newCellsPerFrame; }
        public bool IsValid => MaximumCells > 0 && MaximumCandidates > 0 && MaximumBytes > 0 && NewCellsPerFrame > 0;
    }

    public sealed class PlanetTerrainPreviewInput
    {
        public SurfaceSnapshot Source { get; }
        public PlanetTerrainPreviewRequest Request { get; }
        public IReadOnlyList<PlanetTerrainScatterPreviewSpecies> Species { get; }
        public PlanetTerrainScatterPreviewBudget Budget { get; }
        public SurfaceContentHash ConfigurationDigest { get; }
        public PlanetTerrainPreviewInput(SurfaceSnapshot source, PlanetTerrainPreviewRequest request,
            IEnumerable<PlanetTerrainScatterPreviewSpecies> species = null, PlanetTerrainScatterPreviewBudget budget = default)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); request.Projection(source.Recipe.Radius); Request = request;
            var captured = species == null ? new List<PlanetTerrainScatterPreviewSpecies>() : new List<PlanetTerrainScatterPreviewSpecies>(species);
            if (captured.Count > SurfaceScatterProfile.MaximumSpecies || captured.Count != 0 && !budget.IsValid)
                throw new ArgumentException("Numerical preview requires bounded species and residency budgets.");
            foreach (var item in captured)
                if (!item.Placement.IsValid || !math.isfinite(item.RenderDistance) || item.RenderDistance <= 0 ||
                    !math.isfinite(item.ShadowDistance) || item.ShadowDistance < item.RenderDistance ||
                    !math.isfinite(item.BoundingRadius) || item.BoundingRadius < 0 ||
                    SurfaceScatterSpacing.Validate(item.Placement,source.Recipe.Radius,out _)!=SurfaceSampleStatus.Ready)
                    throw new ArgumentException("Preview species must be valid immutable captures.");
            captured.Sort((a, b) => a.Placement.SpeciesId.CompareTo(b.Placement.SpeciesId));
            for (int i = 1; i < captured.Count; i++) if (captured[i].Placement.SpeciesId == captured[i - 1].Placement.SpeciesId)
                throw new ArgumentException("Preview species identities must be unique.");
            Species = captured.AsReadOnly(); Budget = budget;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(1); writer.Write(request.Latitude); writer.Write(request.Longitude); writer.Write(request.Heading);
                    writer.Write(request.WidthMetres); writer.Write(request.Samples); writer.Write((int)request.Mode); writer.Write(request.ObserverHeight);
                    writer.Write(request.MaximumWorkingBytes); writer.Write(request.MaximumCompositionChecks);
                    writer.Write(budget.MaximumCells); writer.Write(budget.MaximumCandidates); writer.Write(budget.MaximumBytes); writer.Write(budget.NewCellsPerFrame);
                    writer.Write(captured.Count);
                    foreach (var item in captured)
                    {
                        writer.Write(new SurfaceScatterProfile(new[] { item.Placement }).ContentDigest.ToString());
                        writer.Write(item.RenderDistance); writer.Write(item.ShadowDistance); writer.Write(item.BoundingRadius);
                    }
                }
                ConfigurationDigest = SurfaceContentHash.Compute(stream.ToArray());
            }
        }
    }

    public readonly struct PlanetTerrainScatterPreviewRow
    {
        public readonly uint SpeciesId;
        public readonly SurfaceSampleStatus SamplingStatus;
        public readonly double Density, FilteredFraction, ExpectedObjects, MinimumReferenceChordSpacingMetres;
        public readonly int RenderCells, BankCells;
        public readonly long Proposals, EstimatedBufferBytes;
        public readonly bool CapacityValid;
        internal PlanetTerrainScatterPreviewRow(uint id, SurfaceSampleStatus status, double density, double fraction, double objects,
            int renderCells, int bankCells, long proposals, long bytes, bool capacityValid,double minimumReferenceChordSpacingMetres=0)
        { SpeciesId = id; SamplingStatus = status; Density = density; FilteredFraction = fraction; ExpectedObjects = objects;
            RenderCells = renderCells; BankCells = bankCells; Proposals = proposals; EstimatedBufferBytes = bytes; CapacityValid = capacityValid;
            MinimumReferenceChordSpacingMetres=minimumReferenceChordSpacingMetres; }
    }
    public sealed class PlanetTerrainPreviewResult
    {
        public SurfaceContentHash SourceContentDigest { get; }
        public SurfaceContentHash ConfigurationDigest { get; }
        public PlanetTerrainPreviewRequest Request { get; }
        public SurfaceRegionProjection Projection { get; }
        public PlanetTerrainScatterPreviewBudget Budget { get; }
        public IReadOnlyList<double> Heights { get; }
        public IReadOnlyList<float4> MaterialWeights { get; }
        public IReadOnlyList<float4> ErosionData { get; }
        public IReadOnlyList<float4> Pixels { get; }
        public IReadOnlyList<PlanetTerrainScatterPreviewRow> Scatter { get; }
        public double MinimumHeight { get; }
        public double MaximumHeight { get; }
        public double RegionArea { get; }
        public long EstimatedWorkingBytes { get; }
        public int BankCells { get; }
        public long Proposals { get; }
        public long EstimatedBufferBytes { get; }
        public bool CompleteCellEstimate { get; }
        public bool FitsSingleBank { get; }
        public bool FitsActiveAndReplacement { get; }
        public long MinimumPreparationFrames { get; }
        internal PlanetTerrainPreviewResult(PlanetTerrainPreviewInput input, double[] heights, float4[] weights, float4[] erosion,
            float4[] pixels, List<PlanetTerrainScatterPreviewRow> scatter, double minimum, double maximum, double area, long bytes)
        {
            SourceContentDigest = input.Source.ContentDigest; ConfigurationDigest = input.ConfigurationDigest; Request = input.Request; Budget = input.Budget;
            Projection = input.Request.Projection(input.Source.Recipe.Radius); Heights = Array.AsReadOnly(heights);
            MaterialWeights = Array.AsReadOnly(weights); ErosionData = Array.AsReadOnly(erosion); Pixels = Array.AsReadOnly(pixels); Scatter = scatter.AsReadOnly();
            MinimumHeight = minimum; MaximumHeight = maximum; RegionArea = area; EstimatedWorkingBytes = bytes;
            bool complete = true; int cells = 0; long proposals = 0, gpu = 0;
            foreach (var row in scatter)
            {
                complete &= row.BankCells >= 0 && row.CapacityValid;
                if (row.BankCells >= 0) { cells = checked(cells + row.BankCells); proposals = checked(proposals + row.Proposals); gpu = checked(gpu + row.EstimatedBufferBytes); }
            }
            CompleteCellEstimate = complete; BankCells = complete ? cells : -1; Proposals = complete ? proposals : -1; EstimatedBufferBytes = complete ? gpu : -1;
            var budget = input.Budget;
            FitsSingleBank = complete && (scatter.Count == 0 || cells <= budget.MaximumCells && proposals <= budget.MaximumCandidates && gpu <= budget.MaximumBytes);
            FitsActiveAndReplacement = complete && (scatter.Count == 0 || 2L * cells <= budget.MaximumCells && 2L * proposals <= budget.MaximumCandidates && 2L * gpu <= budget.MaximumBytes);
            MinimumPreparationFrames = complete && scatter.Count != 0 ? (cells + (long)budget.NewCellsPerFrame - 1) / budget.NewCellsPerFrame : 0;
        }
    }

    /// <summary>Numerical estimates only: no GPU readback, scene edits or Unity object access.</summary>
    public static class PlanetTerrainPreview
    {
        public const int MaximumCostCells = 8192;
        public static PlanetTerrainPreviewResult Build(PlanetTerrainPreviewInput input, Func<bool> cancelled = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input)); CheckCancelled(cancelled);
            var source = input.Source; var request = input.Request; var projection = request.Projection(source.Recipe.Radius);
            int count = checked(request.Samples * request.Samples);
            long layers = source.Tiles.Count + source.Regions.Count + source.Stamps.Count + 1L;
            if (source.ResolvedMaterials != null) layers = checked(layers + source.ResolvedMaterials.Layers.Count + source.ResolvedMaterials.StampLayers.Count);
            long checks = checked((long)count * (2 + 6L * input.Species.Count) * layers);
            long retained = PlanetTerrainBakeCache.EstimateSnapshotBytes(source);
            if (source.ResolvedMaterials != null) foreach (var layer in source.ResolvedMaterials.StampLayers) retained = checked(retained + layer.SampleCount * 16L + 256);
            long working = checked(retained * 3 + count * 96L + MaximumCostCells * 64L + input.Species.Count * 512L);
            if (working > request.MaximumWorkingBytes || checks > request.MaximumCompositionChecks)
                throw new InvalidOperationException($"Preview requires about {working} bytes and {checks} composition checks; reduce samples/species or explicitly raise its offline limit.");
            var heights = new double[count]; var materials = new float4[count]; var erosion = new float4[count]; var pixels = new float4[count];
            var sums = new double[input.Species.Count]; var statuses = new SurfaceSampleStatus[input.Species.Count];
            for (int i = 0; i < statuses.Length; i++) statuses[i] = SurfaceSampleStatus.Ready;
            double min = double.PositiveInfinity, max = double.NegativeInfinity, totalAreaWeight = 0, centerHeight;
            using (var native = source.CreateNative(Allocator.Persistent))
            {
                Require(SurfaceSampler.TrySampleHeight(native.View, projection.AnchorDirection, out centerHeight));
                for (int y = 0; y < request.Samples; y++) for (int x = 0; x < request.Samples; x++)
                {
                    int index = y * request.Samples + x; if ((index & 63) == 0) CheckCancelled(cancelled);
                    var point = math.lerp(projection.MinimumMetres, projection.MaximumMetres, new double2(x, y) / (request.Samples - 1.0));
                    if (!projection.TryDirection(point, out var direction)) throw new InvalidOperationException("Preview metric projection is not ready.");
                    Require(SurfaceSampler.TrySampleHeight(native.View, direction, out double height)); heights[index] = height;
                    min = math.min(min, height); max = math.max(max, height);
                    var attributeStatus = SurfaceSampler.TrySampleAttributes(native.View, direction, out var attributes);
                    materials[index] = attributes.MaterialWeights; erosion[index] = attributes.ErosionData;
                    var required = RequiredMapChannel(request.Mode);
                    if (required != SurfaceChannels.None)
                    { Require(attributeStatus); if ((attributes.Channels & required) != required) throw new InvalidOperationException("The selected map channel is not ready in this published surface."); }
                    double jacobian = math.pow(1 + math.lengthsq(point) / (source.Recipe.Radius * source.Recipe.Radius), -1.5);
                    double edge = (x == 0 || x == request.Samples - 1 ? .5 : 1) * (y == 0 || y == request.Samples - 1 ? .5 : 1);
                    double areaWeight = jacobian * edge; totalAreaWeight += areaWeight;
                    for (int i = 0; i < input.Species.Count; i++)
                    {
                        if (statuses[i] != SurfaceSampleStatus.Ready) continue;
                        var rules = input.Species[i].Placement;
                        var channels = rules.RequiredChannels | SurfaceChannels.MaterialWeights;
                        if (rules.MinimumWetness > 0 || rules.MaximumWetness < 1) channels |= SurfaceChannels.ErosionData;
                        if (!source.MaterialsReady || attributeStatus != SurfaceSampleStatus.Ready || (attributes.Channels & channels) != channels)
                        { statuses[i] = SurfaceSampleStatus.NotReady; continue; }
                        var status = SurfaceSampler.TrySampleNormal(native.View, direction, rules.NormalSampleMetres, out var normal);
                        if (status != SurfaceSampleStatus.Ready) { statuses[i] = status; continue; }
                        bool excluded = false;
                        if (rules.ExcludeSurfaceStamps) foreach (var stamp in source.Stamps)
                            excluded |= SurfaceScatterExclusionSampler.ArcMetres(direction, stamp.CenterDirection, source.Recipe.Radius) <= stamp.RadiusMetres + stamp.RimWidthMetres;
                        if (excluded || height < rules.MinimumHeight || height > rules.MaximumHeight ||
                            math.dot(direction, normal) < math.cos(rules.MaximumSlopeDegrees * Math.PI / 180) ||
                            (attributes.Channels & SurfaceChannels.ErosionData) != 0 && (attributes.ErosionData.y < rules.MinimumWetness || attributes.ErosionData.y > rules.MaximumWetness)) continue;
                        sums[i] += areaWeight * math.dot(attributes.MaterialWeights, rules.MaterialAffinity);
                    }
                }
            }
            double pixelArea = math.pow(request.WidthMetres / (request.Samples - 1), 2), area = totalAreaWeight * pixelArea;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = MapPixel(request.Mode, heights[i], materials[i], erosion[i], min, max);
            var rows = new List<PlanetTerrainScatterPreviewRow>(); int remaining = MaximumCostCells;
            double3 camera = projection.AnchorDirection * (source.Recipe.Radius + centerHeight + request.ObserverHeight);
            double relief = math.max(math.abs(source.MinimumHeight), math.abs(source.MaximumHeight));
            for (int i = 0; i < input.Species.Count; i++)
            {
                CheckCancelled(cancelled); var species = input.Species[i]; double fraction = statuses[i] == SurfaceSampleStatus.Ready ? sums[i] / totalAreaWeight : double.NaN;
                int render = CountCells(camera, source.Recipe.Radius, relief, species.RenderDistance + species.BoundingRadius * species.Placement.ScaleRange.y,
                    species.Placement, remaining, out _);
                int bank = CountCells(camera, source.Recipe.Radius, relief, species.ShadowDistance + species.BoundingRadius * species.Placement.ScaleRange.y,
                    species.Placement, remaining, out bool capacity);
                if (bank > 0) remaining -= bank;
                long proposals = bank >= 0 ? (long)bank * species.Placement.CandidatesPerCell : -1;
                long buffers = bank > 0 ? checked(bank * 20L + proposals * (PlanetScatterCandidateGpu.Stride + PlanetScatterPoseGpu.Stride + 20L) + 176) : bank;
                rows.Add(new PlanetTerrainScatterPreviewRow(species.Placement.SpeciesId, statuses[i], species.Placement.DensityPerSquareMetre,
                    fraction, species.Placement.DensityPerSquareMetre * fraction * area, render, bank, proposals, buffers, capacity,species.Placement.MinimumReferenceChordSpacingMetres));
            }
            CheckCancelled(cancelled);
            return new PlanetTerrainPreviewResult(input, heights, materials, erosion, pixels, rows, min, max, area, working);
        }
        static int CountCells(double3 camera, double radius, double relief, double reach, SurfaceScatterSpecies species, int maximum, out bool capacity)
        {
            capacity = true;
            if (!PlanetScatterRenderer.TryInterest(camera, radius, relief, reach, out var anchor, out double angle)) return 0;
            if (maximum < 1) return -1;
            var cells = new List<SurfaceTileKey>();
            if (!SurfaceScatterCells.TryCollect(anchor, angle, species.FixedLevel, maximum, cells, out _)) return -1;
            double width = 2.0 / (1L << species.FixedLevel);
            foreach (var cell in cells)
            {
                var low = 2 * new double2(cell.X, cell.Y) / (1L << cell.Level) - 1;
                var closest = math.clamp(double2.zero, low, low + width);
                capacity &= species.DensityPerSquareMetre * radius * radius * width * width / math.pow(1 + math.lengthsq(closest), 1.5) <= species.CandidatesPerCell;
            }
            return cells.Count;
        }
        static SurfaceChannels RequiredMapChannel(PlanetTerrainMapMode mode) => mode == PlanetTerrainMapMode.Height ? SurfaceChannels.None :
            (int)mode <= (int)PlanetTerrainMapMode.Snow ? SurfaceChannels.MaterialWeights : SurfaceChannels.ErosionData;
        static float4 MapPixel(PlanetTerrainMapMode mode, double height, float4 material, float4 erosion, double minimum, double maximum)
        {
            if (mode == PlanetTerrainMapMode.MaterialGRSS)
                return new float4(material.x * new float3(.12f, .7f, .16f) + material.y * new float3(.9f, .65f, .25f) +
                    material.z * new float3(.4f) + material.w * new float3(1), 1);
            float value = mode == PlanetTerrainMapMode.Height ? (float)(maximum > minimum ? (height - minimum) / (maximum - minimum) : .5) :
                (int)mode <= (int)PlanetTerrainMapMode.Snow ? material[(int)mode - (int)PlanetTerrainMapMode.Grass] : erosion[(int)mode - (int)PlanetTerrainMapMode.Flow];
            return new float4(value, value, value, 1);
        }
        static void Require(SurfaceSampleStatus status) { if (status != SurfaceSampleStatus.Ready) throw new InvalidOperationException("Canonical preview sample is " + status + "; the previous complete preview is retained."); }
        static void CheckCancelled(Func<bool> cancelled) { if (cancelled != null && cancelled()) throw new OperationCanceledException("Terrain preview cancelled before publication."); }
    }

    /// <summary>Pure publication gate; cancellation/stale work never replaces a completed result.</summary>
    public sealed class PlanetTerrainPreviewPublication
    {
        long generation;
        public PlanetTerrainPreviewResult Current { get; private set; }
        public long Begin() => ++generation;
        public void Cancel() => generation++;
        public bool TryPublish(long token, PlanetTerrainPreviewResult result, SurfaceContentHash source, SurfaceContentHash configuration)
        {
            if (token != generation || result == null || result.SourceContentDigest != source || result.ConfigurationDigest != configuration) return false;
            Current = result; return true;
        }
    }
}
