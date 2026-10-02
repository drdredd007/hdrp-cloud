using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Pure synchronous offline bake. No live asset is modified; consumers publish only a completed immutable result.</summary>
    public static partial class SurfaceBaker
    {
        public static SurfaceBakeResult Bake(SurfaceRecipe recipe, SurfaceBakeSettings settings,
            Action<SurfaceBakeProgress> progress = null, Func<bool> cancelled = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var captured = settings.Clone();
            if (!captured.Validate(recipe, out var error)) throw new ArgumentException(error, nameof(settings));
            var clock = Stopwatch.StartNew(); Report(progress, cancelled, "Topology", 0, 1);
            var graph = new SurfaceBakeGraph(captured.FaceResolution, recipe.Radius, cancelled);
            Report(progress, cancelled, "Topology", 1, 1);
            var state = new BakeState(graph);
            GenerateMacro(recipe, captured, graph, state, progress, cancelled);
            double initial = Volume(graph, state.Ground), eroded = 0, deposited = 0;
            int hydro = recipe.Style == SurfaceStyle.Rocky && !captured.EnableHydraulicOnRocky ? 0 : captured.HydraulicIterations;
            for (int iteration = 0; iteration < hydro; iteration++)
            {
                Report(progress, cancelled, "Hydraulic", iteration, hydro);
                Hydraulic(recipe, captured, graph, state, ref eroded, ref deposited, cancelled);
            }
            Report(progress, cancelled, "Hydraulic", hydro, hydro);
            for (int iteration = 0; iteration < captured.ThermalIterations; iteration++)
            {
                Report(progress, cancelled, "Thermal", iteration, captured.ThermalIterations);
                Thermal(captured, graph, state, ref eroded, ref deposited, cancelled);
            }
            Report(progress, cancelled, "Thermal", captured.ThermalIterations, captured.ThermalIterations);
            // End the offline wet simulation with all suspended ground deposited; no hidden sediment pool is lost.
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i); double volume = state.Sediment[i];
                state.Ground[i] += volume / graph.Areas[i]; state.Deposition[i] += volume / graph.Areas[i]; deposited += volume; state.Sediment[i] = 0;
            }
            Report(progress, cancelled, "Climate", 0, 1);
            ComputeAttributes(recipe, captured, graph, state, cancelled);
            Report(progress, cancelled, "Climate", 1, 1);
            Report(progress, cancelled, "LOD", 0, 1);
            var pyramid = BuildPyramid(recipe, graph, state, cancelled);
            var finest = pyramid[0]; double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            foreach (var tile in finest.Tiles) { minimum = math.min(minimum, tile.MinimumHeight); maximum = math.max(maximum, tile.MaximumHeight); }
            var outputRecipe = new SurfaceRecipe(recipe.Seed, recipe.Style, recipe.Radius, minimum, maximum, recipe.SeaLevel, recipe.AlgorithmVersion, recipe.SchemaVersion);
            var config = captured.ConfigurationDigest(recipe);
            var baseDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); SurfaceHashing.WriteHash(writer, config);
                foreach (var tile in finest.Tiles) SurfaceHashing.WriteHash(writer, tile.ContentHash);
            });
            var snapshot = new SurfaceSnapshot(outputRecipe, new SurfaceRevision(SurfaceHashing.Recipe(outputRecipe), baseDigest, 1), 0,
                finest.Resolution, finest.Tiles, automaticMaterialProfile: SurfaceAutomaticMaterialProfile.FromBakeSettings(captured));
            snapshot = SurfaceMaterialRepair.Rebuild(snapshot,progress:progress,cancelled:cancelled);
            var hydrology = BuildHydrology(recipe, captured, graph, state, baseDigest, hydro, cancelled);
            double maximumSlope = MaxSlope(graph, state.Ground), area = Sum(graph.Areas), final = Volume(graph, state.Ground);
            var diagnostics = new SurfaceBakeDiagnostics(clock.Elapsed.TotalSeconds, graph.NodeCount, graph.EdgeCount,
                SurfaceBakeSettings.EstimateWorkingBytes(captured.FaceResolution), eroded, deposited, initial, final, 0,
                pyramid[pyramid.Count - 1].MeasuredErrorMetres, area, maximumSlope, hydro, PublishedVolume(graph, state.Ground));
            Report(progress, cancelled, "Complete", 1, 1);
            return new SurfaceBakeResult(snapshot, diagnostics, pyramid, hydrology);
        }

        internal static void CheckCancelled(Func<bool> cancelled, int index = 0)
        { if ((index & 1023) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException("Surface bake cancelled before publication."); }
        static void Report(Action<SurfaceBakeProgress> progress, Func<bool> cancelled, string stage, int done, int total)
        { CheckCancelled(cancelled); progress?.Invoke(new SurfaceBakeProgress(stage, done, total)); CheckCancelled(cancelled); }
        sealed class BakeState
        {
            public readonly double[] Ground, Bedrock, Water, Sediment, DeltaWater, DeltaSediment, DeltaGround, Outgoing, Throughflow, Flux, Wear, Deposition, Flow;
            public readonly double[] IntegratedWater, IntegratedSediment, IntegratedDepth;
            public readonly float4[] Weights, Erosion;
            public readonly bool[] FixedGround;
            public double ExportedGround;
            public BakeState(SurfaceBakeGraph graph)
            {
                int n = graph.NodeCount;
                Ground = new double[n]; Bedrock = new double[n]; Water = new double[n]; Sediment = new double[n];
                DeltaWater = new double[n]; DeltaSediment = new double[n]; Outgoing = new double[n]; Flux = new double[graph.EdgeCount];
                DeltaGround = new double[n]; Throughflow = new double[n];
                IntegratedWater = new double[graph.EdgeCount]; IntegratedSediment = new double[graph.EdgeCount]; IntegratedDepth = new double[n];
                Wear = new double[n]; Deposition = new double[n]; Flow = new double[n]; Weights = new float4[n]; Erosion = new float4[n];
                FixedGround = graph.FixedBoundary;
            }
        }

        static void GenerateMacro(SurfaceRecipe recipe, SurfaceBakeSettings settings, SurfaceBakeGraph graph, BakeState state,
            Action<SurfaceBakeProgress> progress, Func<bool> cancelled)
        {
            Report(progress, cancelled, "Macro", 0, graph.NodeCount);
            var values = new double[graph.NodeCount]; var order = new int[graph.NodeCount];
            double scale = settings.ContinentScaleMetres > 0 ? settings.ContinentScaleMetres : recipe.Radius * 1.3;
            double mountain = settings.MountainScaleMetres > 0 ? settings.MountainScaleMetres : recipe.Radius * .22;
            var continentNoise = new SurfaceDetailRecipe(scale, 1, recipe.Seed);
            var warpA = new SurfaceDetailRecipe(scale * .65, 1, recipe.Seed ^ 0x51ed);
            var warpB = new SurfaceDetailRecipe(scale * .65, 1, recipe.Seed ^ 0x1721);
            var warpC = new SurfaceDetailRecipe(scale * .65, 1, recipe.Seed ^ 0x7712);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i); var d = graph.Directions[i];
                double3 warp = new double3(Noise(warpA, d, recipe.Radius), Noise(warpB, d, recipe.Radius), Noise(warpC, d, recipe.Radius));
                CubeSurface.TryNormalize(d + warp * .18, out var warped);
                values[i] = Noise(continentNoise, warped, recipe.Radius) + .22 * Noise(new SurfaceDetailRecipe(scale * .35, 1, recipe.Seed ^ 0x15c5), d, recipe.Radius);
                order[i] = i;
            }
            Array.Sort(order, (a, b) => { int c = values[a].CompareTo(values[b]); return c != 0 ? c : a.CompareTo(b); });
            double accumulated = 0, target = Sum(graph.Areas) * (1 - settings.LandFraction), threshold = values[order[0]];
            foreach (int node in order) { accumulated += graph.Areas[node]; threshold = values[node]; if (accumulated >= target) break; }
            double span = math.max(1e-9, values[order[order.Length - 1]] - values[order[0]]);
            double sea = math.clamp(recipe.SeaLevel, recipe.MinimumHeight, recipe.MaximumHeight);
            var ridgeNoise = new SurfaceDetailRecipe(mountain, 1, recipe.Seed ^ 0x38a7);
            var provinceNoise = new SurfaceDetailRecipe(scale * .8, 1, recipe.Seed ^ 0x713f);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i); var d = graph.Directions[i];
                double signed = values[i] - threshold;
                double t = math.clamp(math.abs(signed) / (span * .55), 0, 1);
                double height;
                if (recipe.Style == SurfaceStyle.Rocky)
                {
                    double broad = math.clamp(.5 + .6 * values[i], 0, 1);
                    double ridge = math.pow(1 - math.abs(Noise(ridgeNoise, d, recipe.Radius)), 3);
                    height = math.lerp(recipe.MinimumHeight, recipe.MaximumHeight, math.clamp(.6 * broad + .4 * ridge, 0, 1));
                }
                else if (signed <= 0) height = math.lerp(sea, recipe.MinimumHeight, math.pow(t, .7));
                else
                {
                    double province = Smooth(-.3, .5, Noise(provinceNoise, d, recipe.Radius));
                    double ridge = math.pow(1 - math.abs(Noise(ridgeNoise, d, recipe.Radius)), 4) * province;
                    double relief = math.clamp(t * (.65 + settings.MountainFraction * ridge), 0, 1);
                    height = math.lerp(sea, recipe.MaximumHeight, relief);
                }
                state.Ground[i] = height;
                state.Bedrock[i] = math.max(-recipe.Radius * .99, height - (recipe.MaximumHeight - recipe.MinimumHeight) * settings.MaximumErosionDepthFraction);
            }
            Report(progress, cancelled, "Macro", graph.NodeCount, graph.NodeCount);
        }
        static double Noise(SurfaceDetailRecipe noise, double3 direction, double radius)
        {
            if (noise.TryHeight(direction, radius, out var height) != SurfaceSampleStatus.Ready) throw new ArgumentException("Requested generator scales exceed stable metric noise coordinates.");
            return height;
        }
        static double Smooth(double minimum, double maximum, double value)
        { double t = math.clamp((value - minimum) / (maximum - minimum), 0, 1); return t * t * (3 - 2 * t); }

        static void Hydraulic(SurfaceRecipe recipe, SurfaceBakeSettings settings, SurfaceBakeGraph graph, BakeState state,
            ref double eroded, ref double deposited, Func<bool> cancelled)
        {
            double dt = settings.TimeStepSeconds;
            Array.Clear(state.Outgoing, 0, graph.NodeCount); Array.Clear(state.DeltaWater, 0, graph.NodeCount); Array.Clear(state.DeltaSediment, 0, graph.NodeCount);
            Array.Clear(state.Throughflow, 0, graph.NodeCount);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i);
                // Wet weather follows latitude, not a camera/observer frame.
                double rain = settings.RainMetresPerSecond * (.35 + .65 * math.pow(1 - math.abs(graph.Directions[i].y), 2));
                state.Water[i] += rain * dt * graph.Areas[i];
            }
            for (int edge = 0; edge < graph.EdgeCount; edge++)
            {
                CheckCancelled(cancelled, edge); var pair = graph.Edges[edge]; int a = pair.x, b = pair.y;
                double da = state.Water[a] / graph.Areas[a], db = state.Water[b] / graph.Areas[b];
                double head = state.Ground[a] + da - state.Ground[b] - db;
                double crossArea = graph.Widths[edge] * (da + db) * .5;
                double q = state.Flux[edge] * (1 - settings.FlowDamping) + dt * 9.81 * crossArea * head / graph.Distances[edge];
                // A dry donor cannot supply water; cap all its outgoing pipes together below.
                if ((q > 0 && da <= 0) || (q < 0 && db <= 0)) q = 0;
                state.Flux[edge] = q; state.Outgoing[q >= 0 ? a : b] += math.abs(q) * dt;
            }
            for (int edge = 0; edge < graph.EdgeCount; edge++)
            {
                CheckCancelled(cancelled, edge); var pair = graph.Edges[edge]; double q = state.Flux[edge];
                int donor = q >= 0 ? pair.x : pair.y, receiver = q >= 0 ? pair.y : pair.x;
                double factor = state.Outgoing[donor] > state.Water[donor] ? state.Water[donor] / state.Outgoing[donor] : 1;
                double movedWater = math.abs(q) * dt * factor;
                double movedSediment = state.Water[donor] > 0 ? state.Sediment[donor] * movedWater / state.Water[donor] : 0;
                state.Flux[edge] *= factor;
                double sign = q >= 0 ? 1 : -1;
                state.IntegratedWater[edge] += movedWater * sign; state.IntegratedSediment[edge] += movedSediment * sign;
                state.DeltaWater[donor] -= movedWater; state.DeltaWater[receiver] += movedWater;
                state.DeltaSediment[donor] -= movedSediment; state.DeltaSediment[receiver] += movedSediment;
                state.Throughflow[donor] += movedWater; state.Throughflow[receiver] += movedWater;
                state.Flow[donor] += movedWater / graph.Areas[donor]; state.Flow[receiver] += movedWater / graph.Areas[receiver];
            }
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i);
                state.Water[i] = math.max(0, state.Water[i] + state.DeltaWater[i]);
                state.Sediment[i] = math.max(0, state.Sediment[i] + state.DeltaSediment[i]);
                state.DeltaGround[i] = 0;
                if (state.FixedGround[i])
                {
                    state.Water[i] *= math.exp(-settings.EvaporationPerSecond * dt);
                    state.IntegratedDepth[i] += state.Water[i] / graph.Areas[i]; continue;
                }
                double slope = 0;
                for (int j = 0; j < graph.Degrees[i]; j++)
                {
                    int next = graph.Neighbours[i][j]; double distance = Arc(graph.Directions[i], graph.Directions[next], recipe.Radius);
                    slope = math.max(slope, (state.Ground[i] - state.Ground[next]) / distance);
                }
                double depth = state.Water[i] / graph.Areas[i];
                double speed = depth > 1e-12 ? state.Throughflow[i] / dt / (math.sqrt(graph.Areas[i]) * depth * 4) : 0;
                double capacity = settings.Capacity * state.Water[i] * math.min(speed, 100) * math.min(slope, 1);
                if (state.Sediment[i] < capacity)
                {
                    double removed = math.min((capacity - state.Sediment[i]) * (1 - math.exp(-settings.ErosionPerSecond * dt)),
                        math.max(0, state.Ground[i] - state.Bedrock[i]) * graph.Areas[i]);
                    state.DeltaGround[i] = -removed / graph.Areas[i]; state.Sediment[i] += removed; state.Wear[i] += removed / graph.Areas[i]; eroded += removed;
                }
                else
                {
                    double added = (state.Sediment[i] - capacity) * (1 - math.exp(-settings.DepositionPerSecond * dt));
                    state.DeltaGround[i] = added / graph.Areas[i]; state.Sediment[i] -= added; state.Deposition[i] += added / graph.Areas[i]; deposited += added;
                }
                state.Water[i] *= math.exp(-settings.EvaporationPerSecond * dt);
                state.IntegratedDepth[i] += state.Water[i] / graph.Areas[i];
            }
            for (int i = 0; i < graph.NodeCount; i++) state.Ground[i] += state.DeltaGround[i];
        }

        static void Thermal(SurfaceBakeSettings settings, SurfaceBakeGraph graph, BakeState state, ref double eroded, ref double deposited, Func<bool> cancelled)
        {
            Array.Clear(state.DeltaSediment, 0, graph.NodeCount); Array.Clear(state.Outgoing, 0, graph.NodeCount);
            double repose = math.tan(settings.AngleOfReposeDegrees * Math.PI / 180);
            for (int edge = 0; edge < graph.EdgeCount; edge++)
            {
                CheckCancelled(cancelled, edge); var pair = graph.Edges[edge]; double difference = state.Ground[pair.x] - state.Ground[pair.y];
                double excess = math.max(0, math.abs(difference) - graph.Distances[edge] * repose);
                double volume = excess * settings.ThermalRate * math.min(graph.Areas[pair.x], graph.Areas[pair.y]) * .125;
                state.Flux[edge] = difference >= 0 ? volume : -volume; state.Outgoing[difference >= 0 ? pair.x : pair.y] += volume;
            }
            for (int edge = 0; edge < graph.EdgeCount; edge++)
            {
                CheckCancelled(cancelled, edge); var pair = graph.Edges[edge]; double volume = state.Flux[edge];
                int donor = volume >= 0 ? pair.x : pair.y, receiver = volume >= 0 ? pair.y : pair.x;
                double available = state.FixedGround[donor] ? 0 : math.max(0, state.Ground[donor] - state.Bedrock[donor]) * graph.Areas[donor];
                double moved = math.abs(volume) * (state.Outgoing[donor] > available ? available / state.Outgoing[donor] : 1);
                state.DeltaSediment[donor] -= moved;
                if (state.FixedGround[receiver]) state.ExportedGround += moved; else state.DeltaSediment[receiver] += moved;
                state.Wear[donor] += moved / graph.Areas[donor]; state.Deposition[receiver] += moved / graph.Areas[receiver];
                eroded += moved; deposited += moved;
            }
            for (int i = 0; i < graph.NodeCount; i++) state.Ground[i] += state.DeltaSediment[i] / graph.Areas[i];
            Array.Clear(state.Flux, 0, graph.EdgeCount);
        }

        static void ComputeAttributes(SurfaceRecipe recipe, SurfaceBakeSettings settings, SurfaceBakeGraph graph, BakeState state, Func<bool> cancelled, double[] inheritedWetness = null)
        {
            var profile=SurfaceAutomaticMaterialProfile.FromBakeSettings(settings);
            var distances = OceanDistances(recipe, graph, state.Ground, cancelled);
            double moistureScale = settings.MoistureDistanceMetres > 0 ? settings.MoistureDistanceMetres : recipe.Radius * .25;
            double maxFlow = 0; for (int i = 0; i < graph.NodeCount; i++) maxFlow = math.max(maxFlow, state.Flow[i]);
            double relief = math.max(1, recipe.MaximumHeight - recipe.MinimumHeight);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                CheckCancelled(cancelled, i); double slope = 0;
                for (int j = 0; j < graph.Degrees[i]; j++)
                {
                    int next = graph.Neighbours[i][j]; double distance = Arc(graph.Directions[i], graph.Directions[next], recipe.Radius);
                    slope = math.max(slope, math.abs(state.Ground[i] - state.Ground[next]) / distance);
                }
                double latitude = math.abs(graph.Directions[i].y);
                double wetness = recipe.Style == SurfaceStyle.Rocky ? 0 : math.clamp(math.exp(-distances[i] / moistureScale) * (.35 + .65 * (1 - latitude)) +
                    state.Water[i] / graph.Areas[i] * .05, 0, 1);
                if (recipe.Style != SurfaceStyle.Rocky && inheritedWetness != null) wetness = math.max(wetness, inheritedWetness[i]);
                state.Weights[i] = profile.Evaluate(recipe,graph.Directions[i],state.Ground[i],slope,wetness);
                state.Erosion[i] = new float4((float)(maxFlow > 0 ? math.log(1 + state.Flow[i]) / math.log(1 + maxFlow) : 0), (float)wetness,
                    (float)math.clamp(state.Wear[i] / (relief * .1), 0, 1), (float)math.clamp(state.Deposition[i] / (relief * .1), 0, 1));
            }
        }
        static double[] OceanDistances(SurfaceRecipe recipe, SurfaceBakeGraph graph, double[] height, Func<bool> cancelled)
        {
            var distances = new double[graph.NodeCount]; var heap = new MinHeap();
            for (int i = 0; i < graph.NodeCount; i++)
            {
                distances[i] = double.PositiveInfinity;
                if (recipe.Style != SurfaceStyle.Rocky && height[i] <= recipe.SeaLevel) { distances[i] = 0; heap.Push(i, 0); }
            }
            int visits = 0;
            while (heap.Count > 0)
            {
                CheckCancelled(cancelled, visits++); heap.Pop(out int node, out double distance);
                if (distance != distances[node]) continue;
                for (int j = 0; j < graph.Degrees[node]; j++)
                {
                    int next = graph.Neighbours[node][j]; double candidate = distance + Arc(graph.Directions[node], graph.Directions[next], recipe.Radius);
                    if (candidate < distances[next]) { distances[next] = candidate; heap.Push(next, candidate); }
                }
            }
            return distances;
        }
        sealed class MinHeap
        {
            readonly List<int> nodes = new List<int>(); readonly List<double> values = new List<double>(); public int Count => nodes.Count;
            static bool Less(double a, int ai, double b, int bi) => a < b || (a == b && ai < bi);
            public void Push(int node, double value)
            {
                int i = Count; nodes.Add(node); values.Add(value);
                while (i > 0)
                {
                    int p = (i - 1) / 2; if (!Less(value, node, values[p], nodes[p])) break;
                    nodes[i] = nodes[p]; values[i] = values[p]; i = p;
                }
                nodes[i] = node; values[i] = value;
            }
            public void Pop(out int node, out double value)
            {
                node = nodes[0]; value = values[0]; int last = Count - 1; int n = nodes[last]; double v = values[last];
                nodes.RemoveAt(last); values.RemoveAt(last); if (Count == 0) return;
                int i = 0;
                while (2 * i + 1 < Count)
                {
                    int child = 2 * i + 1;
                    if (child + 1 < Count && Less(values[child + 1], nodes[child + 1], values[child], nodes[child])) child++;
                    if (!Less(values[child], nodes[child], v, n)) break;
                    nodes[i] = nodes[child]; values[i] = values[child]; i = child;
                }
                nodes[i] = n; values[i] = v;
            }
        }

        static List<SurfaceLodLevel> BuildPyramid(SurfaceRecipe recipe, SurfaceBakeGraph graph, BakeState state, Func<bool> cancelled)
        {
            int finest = graph.Resolution; var levels = new List<SurfaceLodLevel>();
            for (int resolution = finest; resolution >= 1; resolution /= 2)
            {
                var tiles = new SurfaceTileData[6]; double error = 0; int stride = finest / resolution;
                for (int face = 0; face < 6; face++)
                {
                    CheckCancelled(cancelled); int count = (resolution + 1) * (resolution + 1);
                    var heights = new float[count]; var weights = new float4[count]; var erosion = new float4[count];
                    for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
                    {
                        int node = graph.FaceNode(face, x * stride, y * stride), index = y * (resolution + 1) + x;
                        float height = (float)state.Ground[node]; if (!math.isfinite(height) || height <= -recipe.Radius) throw new InvalidOperationException("Bake generated invalid signed ground.");
                        heights[index] = height; weights[index] = state.Weights[node]; erosion[index] = state.Erosion[node];
                    }
                    double faceError = 0;
                    if (resolution < finest)
                        for (int y = 0; y <= finest; y++) for (int x = 0; x <= finest; x++)
                        {
                            CheckCancelled(cancelled, y * (finest + 1) + x);
                            int cx = math.min(resolution - 1, x / stride), cy = math.min(resolution - 1, y / stride);
                            double fx = (double)x / stride - cx, fy = (double)y / stride - cy;
                            double3 a = Position(graph, state, recipe.Radius, face, cx * stride, cy * stride);
                            double3 b = Position(graph, state, recipe.Radius, face, (cx + 1) * stride, cy * stride);
                            double3 c = Position(graph, state, recipe.Radius, face, cx * stride, (cy + 1) * stride);
                            double3 d = Position(graph, state, recipe.Radius, face, (cx + 1) * stride, (cy + 1) * stride);
                            double3 coarse = fx + fy <= 1 ? a * (1 - fx - fy) + b * fx + c * fy : d * (fx + fy - 1) + b * (1 - fy) + c * (1 - fx);
                            faceError = math.max(faceError, math.distance(coarse, Position(graph, state, recipe.Radius, face, x, y)));
                        }
                    error = math.max(error, faceError);
                    tiles[face] = new SurfaceTileData(new SurfaceTileKey(face, 0, 0, 0), resolution, heights, weights, erosion, faceError,SurfaceMaterialProvenance.Automatic);
                }
                levels.Add(new SurfaceLodLevel(resolution, tiles, error));
            }
            return levels;
        }
        static double3 Position(SurfaceBakeGraph graph, BakeState state, double radius, int face, int x, int y)
        { int node = graph.FaceNode(face, x, y); return graph.Directions[node] * (radius + (float)state.Ground[node]); }
        static SurfaceHydrologyField BuildHydrology(SurfaceRecipe recipe, SurfaceBakeSettings settings, SurfaceBakeGraph graph, BakeState state,
            SurfaceContentHash baseDigest, int iterations, Func<bool> cancelled)
        {
            var water = new double3[graph.NodeCount]; var sediment = new double3[graph.NodeCount];
            if (iterations > 0)
                for (int edge = 0; edge < graph.EdgeCount; edge++)
                {
                    CheckCancelled(cancelled, edge); var pair = graph.Edges[edge]; var a = graph.Directions[pair.x]; var b = graph.Directions[pair.y];
                    CubeSurface.TryNormalize(b - a * math.dot(a, b), out var alongA);
                    CubeSurface.TryNormalize(b * math.dot(a, b) - a, out var alongB);
                    double qw = state.IntegratedWater[edge] / (iterations * settings.TimeStepSeconds * graph.Widths[edge]);
                    double qs = state.IntegratedSediment[edge] / (iterations * settings.TimeStepSeconds * graph.Widths[edge]);
                    water[pair.x] += alongA * (2 * qw / graph.Degrees[pair.x]); water[pair.y] += alongB * (2 * qw / graph.Degrees[pair.y]);
                    sediment[pair.x] += alongA * (2 * qs / graph.Degrees[pair.x]); sediment[pair.y] += alongB * (2 * qs / graph.Degrees[pair.y]);
                }
            int resolution = graph.Resolution, row = resolution + 1; var samples = new SurfaceHydrologySample[6 * row * row];
            for (int face = 0; face < 6; face++) for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
            {
                int i = graph.FaceNode(face, x, y); CheckCancelled(cancelled, i);
                double rain = iterations > 0 ? settings.RainMetresPerSecond * (.35 + .65 * math.pow(1 - math.abs(graph.Directions[i].y), 2)) : 0;
                samples[face * row * row + y * row + x] = new SurfaceHydrologySample(water[i], sediment[i], iterations > 0 ? state.IntegratedDepth[i] / iterations : 0, rain);
            }
            return new SurfaceHydrologyField(baseDigest, recipe.Radius, resolution, samples);
        }
        static double Arc(double3 a, double3 b, double radius) => 2 * math.asin(math.clamp(math.length(a - b) * .5, 0, 1)) * radius;
        static double MaxSlope(SurfaceBakeGraph graph, double[] height)
        { double value = 0; for (int i = 0; i < graph.EdgeCount; i++) value = math.max(value, math.abs(height[graph.Edges[i].x] - height[graph.Edges[i].y]) / graph.Distances[i]); return value; }
        static double Volume(SurfaceBakeGraph graph, double[] height)
        { double sum = 0, correction = 0; for (int i = 0; i < height.Length; i++) Add(ref sum, ref correction, height[i] * graph.Areas[i]); return sum; }
        static double PublishedVolume(SurfaceBakeGraph graph, double[] height)
        { double sum = 0, correction = 0; for (int i = 0; i < height.Length; i++) Add(ref sum, ref correction, (float)height[i] * graph.Areas[i]); return sum; }
        static double Sum(double[] values)
        { double sum = 0, correction = 0; foreach (double value in values) Add(ref sum, ref correction, value); return sum; }
        static void Add(ref double sum, ref double correction, double value)
        { double adjusted = value - correction, next = sum + adjusted; correction = (next - sum) - adjusted; sum = next; }
    }
}
