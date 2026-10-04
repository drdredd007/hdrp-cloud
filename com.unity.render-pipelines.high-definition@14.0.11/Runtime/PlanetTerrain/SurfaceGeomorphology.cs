using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct SurfaceGeologicalProvince
    {
        public readonly double3 Center, AngularMotion;
        public readonly bool Continental;
        public readonly double Buoyancy;
        internal SurfaceGeologicalProvince(double3 center, double3 motion, bool continental, double buoyancy)
        { Center = center; AngularMotion = motion; Continental = continental; Buoyancy = buoyancy; }
    }
    public readonly struct SurfaceGeologicalBoundary
    {
        public readonly int ProvinceA, ProvinceB, StartVertex, EndVertex;
        public readonly double3 Start, End, Normal;
        /// <summary>Positive closing motion, negative spreading; not a simulated plate velocity.</summary>
        public readonly double Convergence;
        public readonly bool ContinentalA, ContinentalB;
        internal SurfaceGeologicalBoundary(int a, int b, int start, int end, double3 first, double3 last,
            SurfaceGeologicalProvince pa, SurfaceGeologicalProvince pb)
        {
            ProvinceA = a; ProvinceB = b; StartVertex = start; EndVertex = end; Start = first; End = last;
            Normal = math.normalize(math.cross(first, last));
            var midpoint = math.normalize(first + last);
            var towardB = math.normalize(pb.Center - pa.Center - midpoint * math.dot(pb.Center - pa.Center, midpoint));
            Convergence = math.clamp(math.dot(math.cross(pa.AngularMotion, midpoint) - math.cross(pb.AngularMotion, midpoint), towardB), -1, 1);
            ContinentalA = pa.Continental; ContinentalB = pb.Continental;
        }
        public bool MountainBelt => Convergence > .08 && (ContinentalA || ContinentalB);
        public bool OceanRift => Convergence < -.08 && !ContinentalA && !ContinentalB;
        internal SurfaceGeologicalBoundary(int a, int b, int start, int end, double3 first, double3 last,
            double convergence, bool continentalA, bool continentalB)
        {
            ProvinceA = a; ProvinceB = b; StartVertex = start; EndVertex = end; Start = first; End = last;
            Normal = math.normalize(math.cross(first, last)); Convergence = convergence;
            ContinentalA = continentalA; ContinentalB = continentalB;
        }
    }
    public readonly struct SurfaceGeomorphologySample
    {
        public readonly int Province;
        public readonly double SignedCoastDistance, Uplift, Rift, Basin;
        internal SurfaceGeomorphologySample(int province, double coast, double uplift, double rift, double basin)
        { Province = province; SignedCoastDistance = coast; Uplift = uplift; Rift = rift; Basin = basin; }
    }

    /// <summary>
    /// Offline deterministic spherical Voronoi provinces and their exact shared edge graph.
    /// An explicit structural recipe, not tectonic simulation. Only its baked heights become runtime authority.
    /// Regional crest/tributary controls use the same metric edge frames; no independent patch noise seed.
    /// </summary>
    public sealed class SurfaceGeomorphology
    {
        readonly SurfaceGeologicalProvince[] provinces;
        readonly SurfaceGeologicalBoundary[] boundaries;
        readonly double3[] vertices;
        readonly SurfaceRecipe recipe;
        readonly float[] coarseMacro;
        readonly int coarseResolution;
        readonly double mountainFraction;
        public IReadOnlyList<SurfaceGeologicalProvince> Provinces { get; }
        public IReadOnlyList<SurfaceGeologicalBoundary> Boundaries { get; }
        public IReadOnlyList<double3> Vertices { get; }
        public SurfaceContentHash ContentDigest { get; }
        public double Radius => recipe.Radius;
        public int Seed => recipe.Seed;
        public SurfaceRecipe SourceRecipe => recipe;
        public SurfaceLandformField LandformField {get;}
        public double MountainUpliftFraction => mountainFraction;
        public int RawMacroResolution => coarseResolution;
        public float[] CopyRawMacroGrid() => coarseMacro == null ? Array.Empty<float>() : (float[])coarseMacro.Clone();
        public double ShelfWidthMetres { get; }
        public double BeltWidthMetres { get; }
        public double RegionalFeatureScaleMetres { get; }
        public double CoastThresholdMetres { get; }
        public SurfaceContentHash SourceBaseDigest { get; }
        public bool HasGlobalGrid => coarseMacro != null && SourceBaseDigest.IsValid;
        public long EstimatedResidentBytes => 256L + provinces.Length * 64L + boundaries.Length * 128L + vertices.Length * 24L + (coarseMacro?.LongLength ?? 0) * 4+(LandformField?.EstimatedResidentBytes??0);
        SurfaceGeomorphology(SurfaceRecipe source, SurfaceBakeSettings settings, SurfaceGeologicalProvince[] plates,
            SurfaceGeologicalBoundary[] edges, double3[] junctions, double coastThreshold)
        {
            recipe = source; provinces = plates; boundaries = edges; vertices = junctions; mountainFraction = settings.MountainFraction;
            Provinces = Array.AsReadOnly(provinces); Boundaries = Array.AsReadOnly(boundaries); Vertices = Array.AsReadOnly(vertices);
            ShelfWidthMetres = math.min(source.Radius * .2, settings.ShelfWidthMetres > 0 ? settings.ShelfWidthMetres : source.Radius * .04);
            double rangeScale = settings.MountainScaleMetres > 0 ? settings.MountainScaleMetres : source.Radius * .22;
            BeltWidthMetres = math.min(source.Radius * .2, settings.MountainBeltWidthMetres > 0 ? settings.MountainBeltWidthMetres : rangeScale * .15);
            RegionalFeatureScaleMetres = math.min(source.Radius * .025, settings.RegionalFeatureScaleMetres);
            CoastThresholdMetres = coastThreshold;
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(2); SurfaceHashing.WriteHash(writer, settings.ConfigurationDigest(source)); writer.Write(coastThreshold);
                foreach (var plate in provinces)
                { SurfaceHashing.WriteVector(writer, plate.Center); SurfaceHashing.WriteVector(writer, plate.AngularMotion); writer.Write(plate.Continental); writer.Write(plate.Buoyancy); }
                foreach (var edge in boundaries)
                { writer.Write(edge.ProvinceA); writer.Write(edge.ProvinceB); writer.Write(edge.StartVertex); writer.Write(edge.EndVertex); writer.Write(edge.Convergence); }
            });
        }
        SurfaceGeomorphology(SurfaceGeomorphology source, float[] grid, int resolution, SurfaceContentHash baseDigest,SurfaceLandformField landformField=null)
        {
            recipe = source.recipe; provinces = source.provinces; boundaries = source.boundaries; vertices = source.vertices;
            mountainFraction = source.mountainFraction; Provinces = source.Provinces; Boundaries = source.Boundaries; Vertices = source.Vertices;
            ShelfWidthMetres = source.ShelfWidthMetres; BeltWidthMetres = source.BeltWidthMetres; RegionalFeatureScaleMetres = source.RegionalFeatureScaleMetres;
            CoastThresholdMetres = source.CoastThresholdMetres; coarseMacro = grid; coarseResolution = resolution; SourceBaseDigest = baseDigest;
            LandformField=landformField??source.LandformField;
            ContentDigest = SurfaceHashing.Compute(writer => { SurfaceHashing.WriteHash(writer, source.ContentDigest); SurfaceHashing.WriteHash(writer, baseDigest); writer.Write(resolution);if(LandformField!=null)SurfaceHashing.WriteHash(writer,LandformField.ContentDigest); });
        }
        public static SurfaceGeomorphology Build(SurfaceRecipe recipe, SurfaceBakeSettings settings, Func<bool> cancelled = null)
        {
            if (settings == null || !settings.Validate(recipe, out _) || (recipe.AlgorithmVersion != SurfaceRecipe.StructuralAlgorithmVersion &&
                !SurfaceRecipe.HasStructuralAuthority(recipe.AlgorithmVersion)))
                throw new ArgumentException("An explicit valid structural recipe is required.");
            var captured = settings.Clone(); int count = captured.ProvinceCount;
            if (captured.ContinentScaleMetres > 0)
                count = (int)math.clamp(math.round(4 * Math.PI * recipe.Radius * recipe.Radius / (captured.ContinentScaleMetres * captured.ContinentScaleMetres)), 8, 64);
            var plates = new SurfaceGeologicalProvince[count]; var ranks = new int[count];
            for (int i = 0; i < count; i++) ranks[i] = i;
            Array.Sort(ranks, (a, b) => { int c = Word(recipe.Seed, a, 1).CompareTo(Word(recipe.Seed, b, 1)); return c != 0 ? c : a.CompareTo(b); });
            var land = new bool[count]; int lands = (int)math.clamp(math.round(count * captured.LandFraction), 1, count - 1);
            for (int i = 0; i < lands; i++) land[ranks[i]] = true;
            // A seeded rigid rotation and small tangent jitter retain even whole-sphere coverage.
            double3 axis = Unit(recipe.Seed, 17, 2); double angle = Random(recipe.Seed, 11, 3) * 2 * Math.PI;
            for (int i = 0; i < count; i++)
            {
                SurfaceBaker.CheckCancelled(cancelled, i);
                double y = 1 - 2 * (i + .5) / count, a = i * Math.PI * (3 - math.sqrt(5));
                var point = new double3(math.sqrt(1 - y * y) * math.cos(a), y, math.sqrt(1 - y * y) * math.sin(a));
                point = Rotate(point, axis, angle);
                var jitter = Unit(recipe.Seed, i, 4); point = math.normalize(point + (jitter - point * math.dot(jitter, point)) * (.1 / math.sqrt(count)));
                plates[i] = new SurfaceGeologicalProvince(point, Unit(recipe.Seed, i, 7) * .8, land[i], .12 + .15 * Random(recipe.Seed, i, 9));
            }
            var junctions = new List<double3>(); var edgeEnds = new SortedDictionary<long, List<int>>();
            // Every accepted triple is a spherical Voronoi vertex; pairs meet in two exact shared vertices.
            // At most 64 provinces: bounded O(P^4) offline work, never a renderer/collider query.
            for (int a = 0; a < count; a++) for (int b = a + 1; b < count; b++) for (int c = b + 1; c < count; c++)
            {
                SurfaceBaker.CheckCancelled(cancelled, 0);
                var cross = math.cross(plates[a].Center - plates[b].Center, plates[a].Center - plates[c].Center);
                if (math.lengthsq(cross) < 1e-24) continue;
                var center = math.normalize(cross);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var vertex = center * sign; double score = math.dot(vertex, plates[a].Center); bool inside = true;
                    for (int k = 0; k < count; k++) if (math.dot(vertex, plates[k].Center) > score + 1e-12) { inside = false; break; }
                    if (!inside) continue;
                    int index = junctions.Count; junctions.Add(vertex); AddEdge(edgeEnds, a, b, index); AddEdge(edgeEnds, a, c, index); AddEdge(edgeEnds, b, c, index);
                }
            }
            var edges = new List<SurfaceGeologicalBoundary>();
            foreach (var pair in edgeEnds)
            {
                if (pair.Value.Count != 2) throw new InvalidOperationException("Degenerate province topology: every shared edge requires exactly two junctions.");
                int a = (int)(pair.Key >> 32), b = (int)pair.Key, first = pair.Value[0], last = pair.Value[1];
                edges.Add(new SurfaceGeologicalBoundary(a, b, first, last, junctions[first], junctions[last], plates[a], plates[b]));
            }
            if (junctions.Count != 2 * count - 4 || edges.Count != 3 * count - 6) throw new InvalidOperationException("Incomplete closed spherical province graph.");
            return new SurfaceGeomorphology(recipe, captured, plates, edges.ToArray(), junctions.ToArray(), 0);
        }
        static void AddEdge(SortedDictionary<long, List<int>> target, int a, int b, int vertex)
        { long key = ((long)a << 32) | (uint)b; if (!target.TryGetValue(key, out var ends)) target.Add(key, ends = new List<int>(2)); ends.Add(vertex); }
        internal SurfaceGeomorphology CalibrateCoast(SurfaceBakeSettings settings, double threshold)
        { return new SurfaceGeomorphology(recipe, settings, provinces, boundaries, vertices, threshold); }
        internal SurfaceGeomorphology CaptureMacroGrid(SurfaceBakeGraph graph, double[] heights,SurfaceLandformField landformField=null)
        {
            var samples = new float[graph.FaceNodes.Length];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)heights[graph.FaceNodes[i]];
            return new SurfaceGeomorphology(this, samples, graph.Resolution, default,landformField);
        }
        internal SurfaceGeomorphology BindSource(SurfaceContentHash sourceBaseDigest)
        { return new SurfaceGeomorphology(this, coarseMacro, coarseResolution, sourceBaseDigest); }
        public bool TryMacroRefinement(double3 direction, out double displacement)
        {
            displacement = 0;
            if (!HasGlobalGrid || !CubeSurface.TryLocate(direction, 0, out var key, out var uv)) return false;
            double2 p = uv * coarseResolution; int x = (int)math.min(coarseResolution - 1, math.floor(p.x)), y = (int)math.min(coarseResolution - 1, math.floor(p.y));
            double2 f = p - new double2(x, y); int row = coarseResolution + 1, i = key.Face * row * row + y * row + x;
            double coarse = math.lerp(math.lerp(coarseMacro[i], coarseMacro[i + 1], f.x), math.lerp(coarseMacro[i + row], coarseMacro[i + row + 1], f.x), f.y);
            // Only the missing structural band is restored; the published coarse hydraulic erosion remains intact.
            // This is NOT subtraction of noise from imported/eroded terrain; the explicit pre-erosion control grid is retained.
            displacement = Height(direction) - coarse; return true;
        }
        public bool TryMountainRegion(out int boundaryIndex, out double3 direction, out double3 right, out double3 forward)
        {
            boundaryIndex = -1; direction = right = forward = default; double best = 0;
            if(recipe.AlgorithmVersion==SurfaceRecipe.LandformAuthorityAlgorithmVersion)
            {
                if(LandformField==null||!LandformField.TryMountainRegion(out direction))return false;
                right=math.normalize(math.cross(math.abs(direction.y)<.9?new double3(0,1,0):new double3(1,0,0),direction));forward=math.cross(direction,right);return true;
            }
            for (int i = 0; i < boundaries.Length; i++)
            {
                var edge = boundaries[i]; if (!edge.MountainBelt || !edge.ContinentalA || !edge.ContinentalB) continue;
                var midpoint = math.normalize(edge.Start + edge.End); double land = Sample(midpoint).SignedCoastDistance - CoastThresholdMetres;
                double score = edge.Convergence * Smooth(land / ShelfWidthMetres);
                if (score > best) { best = score; boundaryIndex = i; direction = midpoint; right = edge.Normal; forward = math.cross(edge.Normal, midpoint); }
            }
            return boundaryIndex >= 0;
        }

        public SurfaceGeomorphologySample Sample(double3 input)
        {
            if (!CubeSurface.TryNormalize(input, out var d)) throw new ArgumentException("A finite nonzero canonical direction is required.");
            int plate = 0; double best = -2;
            for (int i = 0; i < provinces.Length; i++) { double dot = math.dot(d, provinces[i].Center); if (dot > best) { plate = i; best = dot; } }
            double coast = recipe.Radius * Math.PI, uplift = 0, rift = 0, basin = 0, basinWeight = 0;
            foreach (var province in provinces)
            {
                double w = math.exp((math.dot(d, province.Center) - best) * recipe.Radius / ShelfWidthMetres);
                basin += w * province.Buoyancy; basinWeight += w;
            }
            foreach (var edge in boundaries)
            {
                double distance = Distance(edge, d, out _, out _);
                if (edge.ContinentalA != edge.ContinentalB) coast = math.min(coast, distance);
                if (edge.MountainBelt)
                {
                    double weight = Envelope(distance / BeltWidthMetres), strength = (edge.Convergence - .08) / .92;
                    // Belts share the Voronoi junctions; their hierarchy is inherited by regional crests/valleys.
                    uplift = math.max(uplift, weight * strength * (edge.ContinentalA && edge.ContinentalB ? 1 : .75));
                }
                if (edge.OceanRift) rift = math.max(rift, Envelope(distance / BeltWidthMetres) * -edge.Convergence);
            }
            coast *= provinces[plate].Continental ? 1 : -1;
            return new SurfaceGeomorphologySample(plate, coast, uplift, rift, basin / basinWeight);
        }
        public double Height(double3 direction)
        {
            if(recipe.AlgorithmVersion==SurfaceRecipe.LandformAuthorityAlgorithmVersion)
            {
                if(LandformField.TrySample(direction,out double h)!=SurfaceSampleStatus.Ready)throw new InvalidOperationException("Captured whole-cover reconstruction is required for algorithm-five height diagnostics.");return h;
            }
            var sample = Sample(direction); double coast = sample.SignedCoastDistance - CoastThresholdMetres;
            double sea = math.clamp(recipe.SeaLevel, recipe.MinimumHeight, recipe.MaximumHeight), landRelief = recipe.MaximumHeight - sea, oceanRelief = sea - recipe.MinimumHeight;
            double height;
            if (coast >= 0)
            {
                // Coast -> interior plateau, with convergent uplift rather than altitude-modulated noise.
                double interior = Smooth(coast / ShelfWidthMetres);
                height = sea + landRelief * sample.Basin * interior;
                height += landRelief * .72 * math.min(1, mountainFraction / .3) * sample.Uplift * Smooth(coast / (ShelfWidthMetres * .25));
            }
            else
            {
                double shelf = Smooth(-coast / ShelfWidthMetres), basin = Smooth(-coast / (ShelfWidthMetres * 4));
                height = sea - oceanRelief * (.06 * shelf + (.65 + sample.Basin) * basin);
                height += oceanRelief * .22 * sample.Rift * basin;
            }
            return math.clamp(height, recipe.MinimumHeight, recipe.MaximumHeight);
        }
        /// <summary>1..10 km crest/tributary hierarchy in metric belt frames; Full heights are baked once, not added at runtime.</summary>
        public double RegionalDisplacement(double3 input)
        {
            if (!CubeSurface.TryNormalize(input, out var d)) throw new ArgumentException("Invalid canonical direction.");
            double sum = 0, weightSum = 0, scale = RegionalFeatureScaleMetres;
            double amplitude = (recipe.MaximumHeight - math.clamp(recipe.SeaLevel, recipe.MinimumHeight, recipe.MaximumHeight)) * .1 * math.min(1, mountainFraction / .3);
            for (int edgeIndex = 0; edgeIndex < boundaries.Length; edgeIndex++)
            {
                var edge = boundaries[edgeIndex];
                if (!edge.MountainBelt) continue;
                double distance = Distance(edge, d, out double along, out double across);
                double weight = Envelope(distance / (BeltWidthMetres * 1.8)) * (edge.Convergence - .08) / .92;
                if (weight == 0) continue;
                // Repeated spurs originate on the shared primary crest and descend diagonally to basin trunks.
                // The half-period tributaries lie between them and join the axial valley on either side.
                double coordinate = along - math.abs(across) * .35;
                long slot = (long)math.floor(coordinate / scale);
                double crest = 0, tributary = 0;
                for (long branch = slot - 1; branch <= slot + 1; branch++)
                {
                    double root = BranchRoot(edgeIndex, branch, math.abs(across)), next = BranchRoot(edgeIndex, branch + 1, math.abs(across));
                    double width = scale * (.12 + .08 * BranchRandom(edgeIndex, branch, 23));
                    crest = math.max(crest, math.exp(-math.pow((along - root) / width, 2)) * (.65 + .35 * BranchRandom(edgeIndex, branch, 25)));
                    tributary = math.max(tributary, math.exp(-math.pow((along - (root + next) * .5) / (scale * .1), 2)));
                }
                double downstream = .45 + .55 * Smooth(math.abs(across) / (scale * 3));
                double trunk = math.exp(-math.pow((math.abs(across) - scale * 3) / (scale * .18), 2));
                double foothill = 1 - Smooth((math.abs(across) - scale * 3) / scale);
                sum += weight * amplitude * (.75 * crest * foothill - .55 * tributary * downstream * foothill - .6 * trunk);
                weightSum += weight;
            }
            return sum / math.max(1, weightSum);
        }
        public bool TryBeltFrame(int boundaryIndex, double3 input, out double alongMetres, out double acrossMetres)
        {
            alongMetres = acrossMetres = 0;
            if (boundaryIndex < 0 || boundaryIndex >= boundaries.Length || !CubeSurface.TryNormalize(input, out var d)) return false;
            Distance(boundaries[boundaryIndex], d, out alongMetres, out acrossMetres); return true;
        }
        public bool TryDirectionInBeltFrame(int boundaryIndex, double alongMetres, double acrossMetres, out double3 direction)
        {
            direction = default;
            if (boundaryIndex < 0 || boundaryIndex >= boundaries.Length || !math.isfinite(alongMetres) || !math.isfinite(acrossMetres) ||
                alongMetres < 0 || alongMetres > Arc(boundaries[boundaryIndex].Start, boundaries[boundaryIndex].End) * Radius || math.abs(acrossMetres) > Radius * .25) return false;
            var edge = boundaries[boundaryIndex]; var tangent = math.cross(edge.Normal, edge.Start);
            double u = alongMetres / Radius, v = acrossMetres / Radius;
            direction = (edge.Start * math.cos(u) + tangent * math.sin(u)) * math.cos(v) + edge.Normal * math.sin(v);
            return true;
        }
        /// <summary>Spur attachment and bend are stable along a global primary crest, independent of regional tile boundaries.</summary>
        public bool TryRegionalSpur(int boundaryIndex, long slot, double acrossMetres, out double alongMetres)
        {
            alongMetres = 0;
            if (boundaryIndex < 0 || boundaryIndex >= boundaries.Length || !boundaries[boundaryIndex].MountainBelt || !math.isfinite(acrossMetres) || math.abs(acrossMetres) > Radius * .25 ||
                slot < 0 || slot > Radius * Math.PI / RegionalFeatureScaleMetres) return false;
            alongMetres = BranchRoot(boundaryIndex, slot, math.abs(acrossMetres));
            return alongMetres >= 0 && alongMetres <= Arc(boundaries[boundaryIndex].Start, boundaries[boundaryIndex].End) * Radius;
        }
        double BranchRoot(int boundary, long slot, double across)
        {
            double scale = RegionalFeatureScaleMetres, phase = BranchRandom(boundary, slot, 19) * 2 * Math.PI;
            return (slot + .2 + .6 * BranchRandom(boundary, slot, 17)) * scale + .35 * across +
                scale * .12 * (math.sin(across / (scale * 2) + phase) - math.sin(phase));
        }
        double BranchRandom(int boundary, long slot, uint stream)
        { unchecked { return Random(recipe.Seed ^ (boundary * 7907), (int)slot ^ (int)(slot >> 32), stream); } }
        double Distance(SurfaceGeologicalBoundary edge, double3 direction, out double along, out double across)
        {
            double signed = math.dot(direction, edge.Normal); var projected = direction - edge.Normal * signed;
            if (math.lengthsq(projected) < 1e-24) projected = edge.Start; else projected = math.normalize(projected);
            if (math.dot(projected, edge.Start + edge.End) < 0) projected = -projected;
            double full = Arc(edge.Start, edge.End);
            double from = Arc(edge.Start, projected), to = Arc(projected, edge.End);
            var closest = from + to <= full + 1e-12 ? projected : (math.dot(direction, edge.Start) >= math.dot(direction, edge.End) ? edge.Start : edge.End);
            along = Arc(edge.Start, closest) * recipe.Radius;
            across = math.atan2(signed, math.dot(direction, projected)) * recipe.Radius;
            return Arc(direction, closest) * recipe.Radius;
        }
        static double Arc(double3 a, double3 b) => 2 * math.asin(math.min(1, math.length(a - b) * .5));
        static double Envelope(double distance)
        { if (distance >= 3) return 0; double fade = 1 - Smooth((distance - 2) / 1); return math.exp(-distance * distance) * fade; }
        static double Smooth(double value) { double x = math.clamp(value, 0, 1); return x * x * (3 - 2 * x); }
        static double3 Rotate(double3 p, double3 axis, double angle) => p * math.cos(angle) + math.cross(axis, p) * math.sin(angle) + axis * math.dot(axis, p) * (1 - math.cos(angle));
        static double3 Unit(int seed, int index, uint stream)
        { double y = Random(seed, index, stream) * 2 - 1, a = Random(seed, index, stream + 1) * 2 * Math.PI, r = math.sqrt(1 - y * y); return new double3(r * math.cos(a), y, r * math.sin(a)); }
        static double Random(int seed, int index, uint stream) => (Word(seed, index, stream) + .5) / 4294967296.0;
        static uint Word(int seed, int index, uint stream)
        { unchecked { uint x = (uint)seed ^ ((uint)index * 0x9e3779b9u) ^ (stream * 0x85ebca6bu); x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); } }
    }
}
