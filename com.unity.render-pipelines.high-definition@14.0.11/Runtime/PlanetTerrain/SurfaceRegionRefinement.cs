using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    [Serializable]
    public sealed class SurfaceRegionRefinementSettings
    {
        public SurfaceRegionProjection Projection;
        public int2 Resolution = new int2(64);
        public double ContextMetres = 2000, BlendMetres = 1000;
        public double DetailWavelengthMetres = 128, DetailAmplitudeMetres;
        public int Priority;
        public SurfaceBakeSettings Erosion = SurfaceBakeSettings.Preview;
        public SurfaceRegionRefinementSettings Clone()
        { var copy = (SurfaceRegionRefinementSettings)MemberwiseClone(); copy.Erosion = Erosion?.Clone(); return copy; }
        public bool Validate(SurfaceSnapshot source, SurfaceHydrologyField hydrology, out string error)
        {
            error = null;
            if (source == null || hydrology == null || !Projection.IsValid || Projection.Radius != source.Recipe.Radius || hydrology.Radius != source.Recipe.Radius ||
                hydrology.SourceBaseDigest != source.Revision.BaseDigest) error = "Regional refinement requires exact global base hydrology and a matching metric projection.";
            else if (source.Stamps.Count != 0) error = "Instance craters must remain a separate final layer; never bake them into generated refinement.";
            else if (Erosion == null || !Erosion.ValidateErosion(source.Recipe, out error)) { if (error == null) error = "Missing erosion settings."; }
            else if (math.any(Resolution < 2) || math.any(Resolution > 512) || !math.isfinite(ContextMetres) || ContextMetres <= 0 ||
                !math.isfinite(BlendMetres) || BlendMetres < 0 || !math.isfinite(DetailAmplitudeMetres) || DetailAmplitudeMetres < 0 ||
                !math.isfinite(DetailWavelengthMetres) || DetailWavelengthMetres <= 0) error = "Invalid regional dimensions, context or detail scale.";
            else
            {
                double2 pitch = (Projection.MaximumMetres - Projection.MinimumMetres) / (double2)Resolution;
                if (!math.all(math.isfinite(pitch)) || math.any(pitch <= 0) || math.cmax(math.ceil(ContextMetres / pitch)) > 4096)
                    error = "Regional grid pitch or context-cell count exceeds numeric bounds.";
                else if (source.StructuralField != null && source.StructuralField.RegionalFeatureScaleMetres < 4 * math.cmax(pitch))
                    error = "A refined algorithm-three region must resolve its inherited structural feature with at least four cells.";
                else if (ContextMetres < 4 * math.cmax(pitch) || BlendMetres > ContextMetres || BlendMetres > math.cmin(Projection.MaximumMetres - Projection.MinimumMetres) * .5 ||
                    (DetailAmplitudeMetres > 0 && DetailWavelengthMetres < 2 * math.cmax(pitch)))
                    error = "Context must include at least four cells; feather fits the core/context, and baked detail must resolve at least two cells per wavelength.";
                else if (math.cmax(math.abs(Projection.MinimumMetres)) + ContextMetres > source.Recipe.Radius || math.cmax(math.abs(Projection.MaximumMetres)) + ContextMetres > source.Recipe.Radius ||
                    DetailAmplitudeMetres > source.Recipe.Radius * .25) error = "Regional context must remain in the bounded tangent hemisphere.";
                else
                {
                    var halo = (int2)math.ceil(ContextMetres / pitch); long count = ((long)Resolution.x + 2 * halo.x + 1) * ((long)Resolution.y + 2 * halo.y + 1);
                    if (count > 1024L * 1024 || EstimatedWorkingBytes(source) > Erosion.MaximumWorkingBytes) error = "Regional context exceeds its explicit working-memory budget.";
                }
            }
            return error == null;
        }
        public long EstimatedWorkingBytes(SurfaceSnapshot source)
        {
            double2 pitch = (Projection.MaximumMetres - Projection.MinimumMetres) / (double2)Resolution;
            var halo = (int2)math.ceil(ContextMetres / pitch);
            long count = checked(((long)Resolution.x + 2 * halo.x + 1) * ((long)Resolution.y + 2 * halo.y + 1));
            long bytes = checked(count * 768 + 8L * 1024 * 1024);
            foreach (var tile in source.Tiles) bytes = checked(bytes + tile.SampleCount * 36L);
            foreach (var region in source.Regions) if (region.Kind == SurfaceRegionKind.Authored) bytes = checked(bytes + region.SampleCount * 40L);
            // RefineRegion retains the immutable managed source, then CreateNative duplicates its
            // raw reference, province/edge index and captured drainage/coast buffers. The enclosing
            // generation owns the managed source reservation; charge this simultaneous native copy.
            if(source.StructuralField!=null)bytes=checked(bytes+source.StructuralField.EstimatedResidentBytes);
            if(source.OrogenDetail!=null)bytes=checked(bytes+source.OrogenDetail.EstimatedResidentBytes);
            return bytes;
        }
        public SurfaceContentHash ConfigurationDigest(SurfaceContentHash source, SurfaceContentHash hydrology) => SurfaceHashing.Compute(writer =>
        {
            writer.Write(1); SurfaceHashing.WriteHash(writer, source); SurfaceHashing.WriteHash(writer, hydrology); SurfaceHashing.WriteProjection(writer, Projection);
            writer.Write(Resolution.x); writer.Write(Resolution.y); writer.Write(ContextMetres); writer.Write(BlendMetres);
            writer.Write(DetailWavelengthMetres); writer.Write(DetailAmplitudeMetres); writer.Write(Priority);
            // Erosion config is included using the source recipe by RefineRegion, not a placement-derived recipe.
        });
    }
    public readonly struct SurfaceRegionRefinementDiagnostics
    {
        public readonly double ElapsedSeconds, InitialGroundVolume, FinalGroundVolume, PublishedGroundVolume;
        public readonly double ImportedSedimentVolume, ExportedSedimentVolume, ImportedWaterVolume, ExportedWaterVolume, ErodedVolume, DepositedVolume;
        public readonly double MassResidual, QuantizationVolume;
        public readonly int NodeCount, EdgeCount;
        public readonly long EstimatedWorkingBytes;
        public readonly double2 ActualContextMetres;
        internal SurfaceRegionRefinementDiagnostics(double seconds, int nodes, int edges, long bytes, double initial, double final, double published,
            double importedSediment, double exportedSediment, double importedWater, double exportedWater, double eroded, double deposited, double2 context)
        {
            ElapsedSeconds = seconds; NodeCount = nodes; EdgeCount = edges; EstimatedWorkingBytes = bytes;
            InitialGroundVolume = initial; FinalGroundVolume = final; PublishedGroundVolume = published;
            ImportedSedimentVolume = importedSediment; ExportedSedimentVolume = exportedSediment;
            ImportedWaterVolume = importedWater; ExportedWaterVolume = exportedWater; ErodedVolume = eroded; DepositedVolume = deposited;
            MassResidual = final + exportedSediment - initial - importedSediment; QuantizationVolume = published - final; ActualContextMetres = context;
        }
    }
    public sealed class SurfaceRegionRefinementResult
    {
        public SurfaceRegionData Region { get; }
        public SurfaceRegionRefinementDiagnostics Diagnostics { get; }
        public SurfaceContentHash ConfigurationDigest { get; }
        internal SurfaceRegionRefinementResult(SurfaceRegionData region, SurfaceRegionRefinementDiagnostics diagnostics, SurfaceContentHash configurationDigest)
        { Region = region; Diagnostics = diagnostics; ConfigurationDigest = configurationDigest; }
    }
    public static partial class SurfaceBaker
    {
        /// <summary>Refines intrinsic authored terrain using global time-averaged boundary flux. Generated neighbours never become recursive input.</summary>
        public static SurfaceRegionRefinementResult RefineRegion(SurfaceSnapshot source, SurfaceHydrologyField hydrology, SurfaceRegionRefinementSettings settings,
            Action<SurfaceBakeProgress> progress = null, Func<bool> cancelled = null, SurfaceGeomorphology geomorphology = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings)); var captured = settings.Clone();
            if (!captured.Validate(source, hydrology, out var error)) throw new ArgumentException(error, nameof(settings));
            bool structural = source.Recipe.AlgorithmVersion == SurfaceRecipe.StructuralAlgorithmVersion;
            if (structural && (geomorphology == null || !geomorphology.HasGlobalGrid || geomorphology.SourceBaseDigest != source.Revision.BaseDigest ||
                geomorphology.Radius != source.Recipe.Radius || geomorphology.Seed != source.Recipe.Seed))
                throw new ArgumentException("Algorithm-two refinement requires the completed global geomorphology controls, not a locally regenerated noise field.");
            var clock = Stopwatch.StartNew(); Report(progress, cancelled, "Regional topology", 0, 1);
            var authored = new List<SurfaceRegionData>();
            foreach (var region in source.Regions)
                if (region.Kind == SurfaceRegionKind.Authored && region.Priority < captured.Priority) authored.Add(region);
            // Later authored layers belong after this replacement. Embedding their deltas here would apply them twice.
            var input = new SurfaceSnapshot(source.Recipe, source.Revision, source.CanonicalTileLevel, source.Resolution, source.Tiles, source.Detail, authored,
                automaticMaterialProfile:source.AutomaticMaterialProfile,structuralField:source.StructuralField,orogenDetail:source.OrogenDetail);
            double2 pitch = (captured.Projection.MaximumMetres - captured.Projection.MinimumMetres) / (double2)captured.Resolution;
            if (structural && geomorphology.RegionalFeatureScaleMetres < 4 * math.cmax(pitch))
                throw new ArgumentException("The structural ridge/drainage band requires at least four regional cells per kilometre-scale feature.");
            int2 halo = (int2)math.ceil(captured.ContextMetres / pitch), domain = captured.Resolution + 2 * halo; double2 actualContext = (double2)halo * pitch;
            var p = captured.Projection;
            var contextProjection = new SurfaceRegionProjection(p.AnchorDirection, p.Right, p.Forward, p.Radius, p.MinimumMetres - actualContext, p.MaximumMetres + actualContext);
            var graph = new SurfaceBakeGraph(contextProjection, domain, cancelled); var state = new BakeState(graph);
            var coarse = new SurfaceHydrologySample[graph.NodeCount]; var background = new SurfaceAttributes[graph.NodeCount];
            var detail = new SurfaceDetailRecipe(captured.DetailWavelengthMetres, captured.DetailAmplitudeMetres, source.Recipe.Seed ^ 0x1d03);
            using (var native = input.CreateNative(Allocator.Persistent))
                for (int i = 0; i < graph.NodeCount; i++)
                {
                    CheckCancelled(cancelled, i); var direction = graph.Directions[i];
                    if (SurfaceSampler.TrySampleHeight(native.View, direction, out var height) != SurfaceSampleStatus.Ready ||
                        hydrology.TrySample(direction, out coarse[i]) != SurfaceSampleStatus.Ready) throw new InvalidOperationException("Required regional source or hydrology is unavailable.");
                    SurfaceSampler.TrySampleAttributes(native.View, direction, out background[i]);
                    // Detail fades only at the outer context boundary. The complete core gets the same planet-local metric field.
                    if (structural || (!SurfaceRecipe.HasStructuralAuthority(source.Recipe.AlgorithmVersion) && captured.DetailAmplitudeMetres > 0))
                    {
                        int x = i % (domain.x + 1), y = i / (domain.x + 1);
                        double border = math.min(math.min(x, domain.x - x) * pitch.x, math.min(y, domain.y - y) * pitch.y);
                        double displacement;
                        if (structural)
                        {
                            if (!geomorphology.TryMacroRefinement(direction, out var macro)) throw new InvalidOperationException("The completed structural source grid is missing.");
                            displacement = (macro + geomorphology.RegionalDisplacement(direction)) * IntrinsicWeight(authored, direction);
                        }
                        else if (detail.TryHeight(direction, p.Radius, out displacement) != SurfaceSampleStatus.Ready) throw new ArgumentException("Regional detail exceeds stable metric lattice coordinates.");
                        height += displacement * Smooth(0, captured.ContextMetres, border);
                    }
                    state.Ground[i] = height;
                    state.Bedrock[i] = math.max(-p.Radius * .99, height - (input.MaximumHeight - input.MinimumHeight) * captured.Erosion.MaximumErosionDepthFraction);
                    state.Water[i] = coarse[i].MeanWaterDepth * graph.Areas[i];
                }
            Report(progress, cancelled, "Regional topology", 1, 1);
            double initial = Volume(graph, state.Ground), eroded = 0, deposited = 0, importedSediment = 0, exportedSediment = 0, importedWater = 0, exportedWater = 0;
            int iterations = source.Recipe.Style == SurfaceStyle.Rocky && !captured.Erosion.EnableHydraulicOnRocky ? 0 : captured.Erosion.HydraulicIterations;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Report(progress, cancelled, "Regional hydraulic", iteration, iterations);
                ApplyBoundary(graph, state, coarse, captured.Erosion.TimeStepSeconds, ref importedSediment, ref exportedSediment, ref importedWater, ref exportedWater);
                Hydraulic(input.Recipe, captured.Erosion, graph, state, ref eroded, ref deposited, cancelled);
            }
            for (int iteration = 0; iteration < captured.Erosion.ThermalIterations; iteration++)
            {
                Report(progress, cancelled, "Regional thermal", iteration, captured.Erosion.ThermalIterations);
                Thermal(captured.Erosion, graph, state, ref eroded, ref deposited, cancelled);
            }
            exportedSediment += state.ExportedGround;
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i);
                if (state.FixedGround[i]) exportedSediment += state.Sediment[i];
                else { state.Ground[i] += state.Sediment[i] / graph.Areas[i]; state.Deposition[i] += state.Sediment[i] / graph.Areas[i]; deposited += state.Sediment[i]; }
                state.Sediment[i] = 0;
            }
            Report(progress, cancelled, "Regional attributes", 0, 1);
            var inheritedWetness = new double[graph.NodeCount];
            for (int i = 0; i < graph.NodeCount; i++)
                if ((background[i].Channels & SurfaceChannels.ErosionData) != 0) inheritedWetness[i] = background[i].ErosionData.y * .7;
            ComputeAttributes(input.Recipe, captured.Erosion, graph, state, cancelled, inheritedWetness);
            int count = (captured.Resolution.x + 1) * (captured.Resolution.y + 1); var heights = new float[count]; var weights = new float4[count]; var erosionData = new float4[count];
            double coreQuantization = 0, quantizationCorrection = 0;
            for (int y = 0; y <= captured.Resolution.y; y++) for (int x = 0; x <= captured.Resolution.x; x++)
            {
                int i = (y + halo.y) * (domain.x + 1) + x + halo.x, target = y * (captured.Resolution.x + 1) + x;
                CheckCancelled(cancelled, target); heights[target] = (float)state.Ground[i];
                if (!math.isfinite(heights[target]) || heights[target] <= -p.Radius) throw new InvalidOperationException("Regional solve generated invalid signed ground.");
                weights[target] = state.Weights[i]; erosionData[target] = state.Erosion[i];
                Add(ref coreQuantization, ref quantizationCorrection, (heights[target] - state.Ground[i]) * graph.Areas[i]);
            }
            var output = new SurfaceRegionData(p, captured.Resolution, heights, blendMetres: captured.BlendMetres,
                baseDigest: source.Revision.BaseDigest, detailPolicy: SurfaceDetailPolicy.Suppress, priority: captured.Priority, materialWeights: weights,
                kind: SurfaceRegionKind.GeneratedRefinement, sourceContentDigest: input.ContentDigest, contextMetres: math.cmin(actualContext), erosionData: erosionData,
                materialProvenance:SurfaceMaterialProvenance.Automatic,automaticMaterialProfile:SurfaceAutomaticMaterialProfile.FromBakeSettings(captured.Erosion));
            var config = SurfaceHashing.Compute(writer =>
            {
                SurfaceHashing.WriteHash(writer, captured.ConfigurationDigest(input.ContentDigest, hydrology.ContentDigest));
                SurfaceHashing.WriteHash(writer, captured.Erosion.ConfigurationDigest(input.Recipe));
                if (structural) SurfaceHashing.WriteHash(writer, geomorphology.ContentDigest);
            });
            double finalVolume = Volume(graph, state.Ground);
            var diagnostics = new SurfaceRegionRefinementDiagnostics(clock.Elapsed.TotalSeconds, graph.NodeCount, graph.EdgeCount, captured.EstimatedWorkingBytes(input),
                initial, finalVolume, finalVolume + coreQuantization, importedSediment, exportedSediment, importedWater, exportedWater, eroded, deposited, actualContext);
            Report(progress, cancelled, "Regional complete", 1, 1);
            return new SurfaceRegionRefinementResult(output, diagnostics, config);
        }
        // A replacement imported from Gaea is an actual final shape; structural refinement must not double-apply its base.
        // Delta imports retain the underlying structural band. Later authored layers are applied after this generated layer.
        static double IntrinsicWeight(List<SurfaceRegionData> authored, double3 direction)
        {
            double weight = 1;
            foreach (var region in authored)
            {
                if (region.Mode != SurfaceRegionMode.Replace || !region.Projection.TryProject(direction, out var metres) ||
                    math.any(metres < region.Projection.MinimumMetres) || math.any(metres > region.Projection.MaximumMetres)) continue;
                var uv = (metres - region.Projection.MinimumMetres) / (region.Projection.MaximumMetres - region.Projection.MinimumMetres);
                var grid = uv * (double2)region.Resolution;
                int x = (int)math.min(region.Resolution.x - 1, math.floor(grid.x)), y = (int)math.min(region.Resolution.y - 1, math.floor(grid.y));
                double2 f = grid - new double2(x, y); int row = region.Resolution.x + 1, index = y * row + x;
                double mask = math.lerp(math.lerp(region.MaskAt(index), region.MaskAt(index + 1), f.x), math.lerp(region.MaskAt(index + row), region.MaskAt(index + row + 1), f.x), f.y);
                if (region.BlendMetres > 0) mask *= Smooth(0, region.BlendMetres, math.cmin(math.min(metres - region.Projection.MinimumMetres, region.Projection.MaximumMetres - metres)));
                weight *= 1 - math.clamp(mask, 0, 1);
            }
            return weight;
        }
        static void ApplyBoundary(SurfaceBakeGraph graph, BakeState state, SurfaceHydrologySample[] source, double dt,
            ref double importedSediment, ref double exportedSediment, ref double importedWater, ref double exportedWater)
        {
            foreach (var boundary in graph.BoundaryFlux)
            {
                int node = boundary.Node; var value = source[node]; double q = math.dot(value.WaterDischarge, boundary.Outward) * boundary.Width;
                if (q < 0)
                {
                    double water = -q * dt, sediment = math.max(0, -math.dot(value.SedimentDischarge, boundary.Outward) * boundary.Width * dt);
                    state.Water[node] += water; state.Sediment[node] += sediment; importedWater += water; importedSediment += sediment;
                }
                else if (q > 0)
                {
                    double water = math.min(state.Water[node], q * dt), sediment = state.Water[node] > 0 ? state.Sediment[node] * water / state.Water[node] : 0;
                    state.Water[node] -= water; state.Sediment[node] -= sediment; exportedWater += water; exportedSediment += sediment;
                }
            }
        }
    }
}
