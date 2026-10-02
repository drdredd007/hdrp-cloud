using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>One closed graph, not six independently eroded images. Shared corners/edges have one state.</summary>
    internal sealed class SurfaceBakeGraph
    {
        public readonly int Resolution;
        public readonly int[] FaceNodes;
        public readonly double3[] Directions;
        public readonly double[] Areas;
        public readonly int2[] Edges;
        public readonly double[] Distances, Widths;
        public readonly int4[] Neighbours;
        public readonly int[] Degrees;
        public readonly bool[] FixedBoundary;
        public readonly SurfaceBoundaryFlux[] BoundaryFlux;
        public int NodeCount => Directions.Length;
        public int EdgeCount => Edges.Length;
        public int FaceNode(int face, int x, int y) => FaceNodes[face * (Resolution + 1) * (Resolution + 1) + y * (Resolution + 1) + x];
        public SurfaceBakeGraph(int resolution, double radius, Func<bool> cancelled)
        {
            Resolution = resolution;
            var map = new Dictionary<int3, int>(); var directions = new List<double3>();
            FaceNodes = new int[6 * (resolution + 1) * (resolution + 1)];
            for (int face = 0; face < 6; face++) for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
            {
                SurfaceBaker.CheckCancelled(cancelled, FaceNodeIndex(face, x, y));
                int a = 2 * x - resolution, b = 2 * y - resolution;
                var key = Cube(face, resolution, a, b);
                if (!map.TryGetValue(key, out int node))
                { node = directions.Count; map.Add(key, node); CubeSurface.TryNormalize((double3)key, out var direction); directions.Add(direction); }
                FaceNodes[FaceNodeIndex(face, x, y)] = node;
            }
            Directions = directions.ToArray(); Areas = new double[NodeCount];
            var edgeSet = new HashSet<ulong>();
            for (int face = 0; face < 6; face++) for (int y = 0; y <= resolution; y++) for (int x = 0; x <= resolution; x++)
            {
                int a = FaceNode(face, x, y);
                if (x < resolution) AddEdge(edgeSet, a, FaceNode(face, x + 1, y));
                if (y < resolution) AddEdge(edgeSet, a, FaceNode(face, x, y + 1));
                if (x == resolution || y == resolution) continue;
                int b = FaceNode(face, x + 1, y), c = FaceNode(face, x, y + 1), d = FaceNode(face, x + 1, y + 1);
                double area = (SolidAngle(Directions[a], Directions[b], Directions[c]) + SolidAngle(Directions[b], Directions[d], Directions[c])) * radius * radius * .25;
                Areas[a] += area; Areas[b] += area; Areas[c] += area; Areas[d] += area;
            }
            var sorted = new List<ulong>(edgeSet); sorted.Sort();
            Edges = new int2[sorted.Count]; Distances = new double[EdgeCount]; Widths = new double[EdgeCount];
            Neighbours = new int4[NodeCount]; Degrees = new int[NodeCount];
            FixedBoundary = new bool[NodeCount]; BoundaryFlux = Array.Empty<SurfaceBoundaryFlux>();
            for (int i = 0; i < EdgeCount; i++)
            {
                SurfaceBaker.CheckCancelled(cancelled, i);
                int a = (int)(sorted[i] >> 32), b = (int)(sorted[i] & 0xffffffffu); Edges[i] = new int2(a, b);
                Distances[i] = 2 * math.asin(math.clamp(math.length(Directions[a] - Directions[b]) * .5, 0, 1)) * radius;
                Widths[i] = (Areas[a] + Areas[b]) * .5 / Distances[i];
                var na = Neighbours[a]; na[Degrees[a]++] = b; Neighbours[a] = na;
                var nb = Neighbours[b]; nb[Degrees[b]++] = a; Neighbours[b] = nb;
            }
            if (NodeCount != 6 * resolution * resolution + 2 || EdgeCount != 12 * resolution * resolution)
                throw new InvalidOperationException("Closed cube-sphere topology is inconsistent.");
        }
        public SurfaceBakeGraph(SurfaceRegionProjection projection, int2 resolution, Func<bool> cancelled)
        {
            Resolution = 0; int row = resolution.x + 1, count = row * (resolution.y + 1);
            FaceNodes = Array.Empty<int>(); Directions = new double3[count]; Areas = new double[count]; FixedBoundary = new bool[count];
            var edges = new List<int2>();
            for (int y = 0; y <= resolution.y; y++) for (int x = 0; x <= resolution.x; x++)
            {
                int i = y * row + x; SurfaceBaker.CheckCancelled(cancelled, i);
                double2 metres = math.lerp(projection.MinimumMetres, projection.MaximumMetres, new double2((double)x / resolution.x, (double)y / resolution.y));
                if (!projection.TryDirection(metres, out Directions[i])) throw new ArgumentException("Invalid regional metric projection.");
                FixedBoundary[i] = x == 0 || y == 0 || x == resolution.x || y == resolution.y;
                if (x < resolution.x) edges.Add(new int2(i, i + 1));
                if (y < resolution.y) edges.Add(new int2(i, i + row));
            }
            for (int y = 0; y < resolution.y; y++) for (int x = 0; x < resolution.x; x++)
            {
                int a = y * row + x, b = a + 1, c = a + row, d = c + 1;
                double area = (SolidAngle(Directions[a], Directions[b], Directions[c]) + SolidAngle(Directions[b], Directions[d], Directions[c])) * projection.Radius * projection.Radius * .25;
                Areas[a] += area; Areas[b] += area; Areas[c] += area; Areas[d] += area;
            }
            edges.Sort((a, b) => { int c = a.x.CompareTo(b.x); return c == 0 ? a.y.CompareTo(b.y) : c; });
            Edges = edges.ToArray(); Distances = new double[EdgeCount]; Widths = new double[EdgeCount]; Neighbours = new int4[count]; Degrees = new int[count];
            for (int i = 0; i < EdgeCount; i++)
            {
                var p = Edges[i]; Distances[i] = Arc(Directions[p.x], Directions[p.y], projection.Radius);
                Widths[i] = (Areas[p.x] + Areas[p.y]) * .5 / Distances[i];
                var a = Neighbours[p.x]; a[Degrees[p.x]++] = p.y; Neighbours[p.x] = a;
                var b = Neighbours[p.y]; b[Degrees[p.y]++] = p.x; Neighbours[p.y] = b;
            }
            var boundary = new List<SurfaceBoundaryFlux>();
            for (int y = 0; y <= resolution.y; y++)
            {
                AddBoundary(boundary, y * row, y > 0 ? (y - 1) * row : -1, y < resolution.y ? (y + 1) * row : -1, y * row + 1, projection.Radius);
                AddBoundary(boundary, y * row + resolution.x, y > 0 ? (y - 1) * row + resolution.x : -1, y < resolution.y ? (y + 1) * row + resolution.x : -1, y * row + resolution.x - 1, projection.Radius);
            }
            for (int x = 0; x <= resolution.x; x++)
            {
                AddBoundary(boundary, x, x > 0 ? x - 1 : -1, x < resolution.x ? x + 1 : -1, x + row, projection.Radius);
                AddBoundary(boundary, resolution.y * row + x, x > 0 ? resolution.y * row + x - 1 : -1, x < resolution.x ? resolution.y * row + x + 1 : -1, (resolution.y - 1) * row + x, projection.Radius);
            }
            BoundaryFlux = boundary.ToArray();
        }
        void AddBoundary(List<SurfaceBoundaryFlux> output, int node, int previous, int next, int inner, double radius)
        {
            double3 along = previous < 0 ? Directions[next] - Directions[node] : next < 0 ? Directions[node] - Directions[previous] : Directions[next] - Directions[previous];
            CubeSurface.TryNormalize(math.cross(Directions[node], along), out var normal);
            if (math.dot(normal, Directions[inner] - Directions[node]) > 0) normal = -normal;
            double width = (previous < 0 ? 0 : Arc(Directions[node], Directions[previous], radius) * .5) + (next < 0 ? 0 : Arc(Directions[node], Directions[next], radius) * .5);
            output.Add(new SurfaceBoundaryFlux(node, normal, width));
        }
        static double Arc(double3 a, double3 b, double radius) => 2 * math.asin(math.clamp(math.length(a - b) * .5, 0, 1)) * radius;
        int FaceNodeIndex(int face, int x, int y) => face * (Resolution + 1) * (Resolution + 1) + y * (Resolution + 1) + x;
        static void AddEdge(HashSet<ulong> edges, int a, int b)
        { if (a > b) { int swap = a; a = b; b = swap; } edges.Add(((ulong)(uint)a << 32) | (uint)b); }
        static double SolidAngle(double3 a, double3 b, double3 c) => 2 * math.atan2(math.abs(math.dot(a, math.cross(b, c))), 1 + math.dot(a, b) + math.dot(b, c) + math.dot(c, a));
        static int3 Cube(int face, int n, int a, int b)
        {
            switch (face)
            {
                case 0: return new int3(n, b, -a);
                case 1: return new int3(-n, b, a);
                case 2: return new int3(a, n, -b);
                case 3: return new int3(a, -n, b);
                case 4: return new int3(a, b, n);
                default: return new int3(-a, b, -n);
            }
        }
    }
    internal readonly struct SurfaceBoundaryFlux
    {
        public readonly int Node; public readonly double3 Outward; public readonly double Width;
        public SurfaceBoundaryFlux(int node, double3 outward, double width) { Node = node; Outward = outward; Width = width; }
    }
}
