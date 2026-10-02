using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public enum SurfaceRegionFilterStatus { Pending, Ready, InvalidData, BudgetExceeded }

    /// <summary>Derived render support limits. They do not change the authoritative surface recipe.</summary>
    public readonly struct SurfaceRegionFilterSettings
    {
        public const int AlgorithmVersion = 1;
        public readonly long MaximumSupportSamples, MaximumBytes;
        public SurfaceRegionFilterSettings(long maximumSupportSamples, long maximumBytes)
        { MaximumSupportSamples = maximumSupportSamples; MaximumBytes = maximumBytes; }
        public static SurfaceRegionFilterSettings Default => new SurfaceRegionFilterSettings(8L * 1024 * 1024, 128L * 1024 * 1024);
        public bool IsValid => MaximumSupportSamples > 0 && MaximumSupportSamples <= 64L * 1024 * 1024 &&
            MaximumBytes >= 64 && MaximumBytes <= 1024L * 1024 * 1024;
    }

    public readonly struct SurfaceRegionFilterMip
    {
        public readonly int2 Resolution;
        public readonly int Offset, RegionIndex, Level;
        public SurfaceRegionFilterMip(int2 resolution, int offset, int regionIndex, int level)
        { Resolution = resolution; Offset = offset; RegionIndex = regionIndex; Level = level; }
        // Two support nodes outside either side; the outer node is a zero boundary.
        public int Row => Resolution.x + 5;
        public int SampleCount => (Resolution.x + 5) * (Resolution.y + 5);
    }

    /// <summary>Borrowed immutable premultiplied height/coverage render data. Never persisted as authority.</summary>
    public readonly struct NativeSurfaceRegionFilterView
    {
        public readonly SurfaceContentHash ContentDigest;
        public readonly int PolicyVersion;
        public readonly NativeArray<int2>.ReadOnly Ranges;
        public readonly NativeArray<SurfaceRegionFilterMip>.ReadOnly Mips;
        public readonly NativeArray<float2>.ReadOnly Samples;
        internal NativeSurfaceRegionFilterView(SurfaceContentHash digest, NativeArray<int2> ranges,
            NativeArray<SurfaceRegionFilterMip> mips, NativeArray<float2> samples)
        { ContentDigest = digest; PolicyVersion = SurfaceRegionFilterSettings.AlgorithmVersion;
            Ranges = ranges.AsReadOnly(); Mips = mips.AsReadOnly(); Samples = samples.AsReadOnly(); }
        public bool Matches(in NativeSurfaceView source) => PolicyVersion == SurfaceRegionFilterSettings.AlgorithmVersion &&
            ContentDigest == source.ContentDigest && Ranges.IsCreated && Mips.IsCreated && Samples.IsCreated && Ranges.Length == source.Regions.Length;

        /// <summary>Metadata-only conservative deviation from canonical geometry, separate from measured tessellation error.</summary>
        public double ConservativeDeviation(in NativeSurfaceView source, SurfaceTileKey patch, SurfaceSamplingFootprint footprint)
        {
            if (!Matches(source) || !patch.IsValid || !footprint.IsValid) return double.PositiveInfinity;
            if (footprint.Metres == 0) return 0;
            CubeSurface.TryDirection(patch, new double2(.5), out var center);
            double patchAngle = math.min(math.PI, 2 * math.sqrt(2) / (1L << patch.Level));
            double bound = 0, prior = math.max(math.abs(source.MinimumHeight), math.abs(source.MaximumHeight));
            bool suppression = false;
            for (int i = 0; i < Ranges.Length; i++)
            {
                if (Ranges[i].y == 0) continue;
                var region = source.Regions[i]; var p = region.Projection;
                var reach = math.max(math.abs(p.MinimumMetres), math.abs(p.MaximumMetres));
                var spacing = (p.MaximumMetres - p.MinimumMetres) / (double2)region.Resolution;
                // The maximum Jacobian over the whole support is intentionally conservative.
                double maxScale = 1 + math.lengthsq((reach + 2 * (p.MaximumMetres - p.MinimumMetres)) / p.Radius);
                if (footprint.Metres * maxScale <= math.cmin(spacing)) continue;
                double regionAngle = math.atan(math.length(reach + 2 * (p.MaximumMetres - p.MinimumMetres)) / p.Radius);
                double cosine = math.dot(center, p.AnchorDirection);
                if (patchAngle + regionAngle < math.PI && cosine < math.cos(patchAngle + regionAngle) - 1e-12) continue;
                double height = math.max(math.abs(region.MinimumHeight), math.abs(region.MaximumHeight));
                bound += 2 * height + (region.Mode == SurfaceRegionMode.Replace ? prior : 0);
                suppression |= region.DetailPolicy == SurfaceDetailPolicy.Suppress;
            }
            if (suppression) bound += 2 * source.Detail.AmplitudeMetres * footprint.DetailWeight(source.Detail.WavelengthMetres);
            return bound;
        }

        public bool TrySample(in NativeSurfaceView source, int regionIndex, double2 metres,
            SurfaceSamplingFootprint footprint, out double2 premultiplied)
        {
            premultiplied = default;
            if (!Matches(source) || regionIndex < 0 || regionIndex >= Ranges.Length || !footprint.IsValid) return false;
            var region = source.Regions[regionIndex]; var range = Ranges[regionIndex];
            if (footprint.Metres == 0 || range.y == 0)
                return SurfaceRegionFilterMath.TryFull(source, region, metres, out premultiplied);
            var extent = region.Projection.MaximumMetres - region.Projection.MinimumMetres;
            double spacing = math.cmin(extent / (double2)region.Resolution);
            // Largest singular value of the inverse gnomonic map, bounded conservatively.
            double projectionScale = 1 + math.lengthsq(metres / region.Projection.Radius);
            double lod = math.clamp(math.log2(math.max(1, footprint.Metres * projectionScale / spacing)), 0, range.y);
            int lower = (int)math.floor(lod), upper = math.min(range.y, lower + 1);
            if (!TryLevel(source, regionIndex, metres, range, lower, out var a) ||
                !TryLevel(source, regionIndex, metres, range, upper, out var b)) return false;
            premultiplied = math.lerp(a, b, lod - lower);
            return math.all(math.isfinite(premultiplied));
        }
        bool TryLevel(in NativeSurfaceView source, int regionIndex, double2 metres, int2 range, int level, out double2 value)
        {
            if (level == 0) return SurfaceRegionFilterMath.TryFull(source, source.Regions[regionIndex], metres, out value);
            if (range.x < 0 || range.x + level - 1 >= Mips.Length) { value = default; return false; }
            return SurfaceRegionFilterMath.TryMip(Samples, Mips[range.x + level - 1], source.Regions[regionIndex].Projection, metres, out value);
        }
    }

    /// <summary>One bounded asynchronous derivation. The caller retains the source lease until this owner is disposed.</summary>
    public sealed class NativeSurfaceRegionFilter : IDisposable
    {
        NativeArray<int2> ranges;
        NativeArray<SurfaceRegionFilterMip> mips;
        NativeArray<float2> samples;
        NativeArray<int> result;
        JobHandle job;
        readonly SurfaceContentHash digest;
        bool completed, disposed;
        public long EstimatedBytes { get; }
        public long SupportSamples { get; }
        public SurfaceRegionFilterStatus Status { get; private set; } = SurfaceRegionFilterStatus.Pending;
        public NativeSurfaceRegionFilterView View => !disposed && Status == SurfaceRegionFilterStatus.Ready
            ? new NativeSurfaceRegionFilterView(digest, ranges, mips, samples)
            : throw new InvalidOperationException("Derived regional render support is not ready.");

        NativeSurfaceRegionFilter(in NativeSurfaceView source, int2[] ranges, SurfaceRegionFilterMip[] headers,
            int count, long bytes, long support, Allocator allocator)
        {
            digest = source.ContentDigest; EstimatedBytes = bytes; SupportSamples = support;
            try
            {
                this.ranges = new NativeArray<int2>(ranges, allocator); mips = new NativeArray<SurfaceRegionFilterMip>(headers, allocator);
                samples = new NativeArray<float2>(count, allocator, NativeArrayOptions.UninitializedMemory);
                result = new NativeArray<int>(1, allocator);
                job = new BuildJob { Source = source, Mips = mips, Samples = samples, Result = result }.Schedule();
                JobHandle.ScheduleBatchedJobs();
            }
            catch { Dispose(); throw; }
        }

        public static bool TryEstimate(in NativeSurfaceView source, SurfaceRegionFilterSettings settings,
            out long bytes, out long supportSamples, out SurfaceRegionFilterStatus status)
        { return Layout(source, settings, false, out _, out _, out _, out bytes, out supportSamples, out status); }

        public static bool TrySchedule(in NativeSurfaceView source, SurfaceRegionFilterSettings settings,
            Allocator allocator, out NativeSurfaceRegionFilter owner, out SurfaceRegionFilterStatus status)
        {
            owner = null;
            if (!Layout(source, settings, false, out _, out _, out _, out _, out _, out status)) return false;
            if (!Layout(source, settings, true, out var ranges, out var headers, out var count, out var bytes, out var support, out status)) return false;
            owner = new NativeSurfaceRegionFilter(source, ranges, headers, count, bytes, support, allocator);
            status = SurfaceRegionFilterStatus.Pending; return true;
        }
        static bool Layout(in NativeSurfaceView source, SurfaceRegionFilterSettings settings, bool allocate,
            out int2[] ranges, out SurfaceRegionFilterMip[] headers, out int count, out long bytes, out long support,
            out SurfaceRegionFilterStatus status)
        {
            ranges = null; headers = null; count = 0; bytes = 0; support = 0; status = SurfaceRegionFilterStatus.InvalidData;
            if (!settings.IsValid || !source.Regions.IsCreated || !source.RegionHeights.IsCreated || !source.RegionMasks.IsCreated) return false;
            var list = allocate ? new List<SurfaceRegionFilterMip>() : null;
            if (allocate) ranges = new int2[source.Regions.Length];
            long nodes = 0, headerCount = 0;
            for (int i = 0; i < source.Regions.Length; i++)
            {
                var region = source.Regions[i]; var resolution = region.Resolution;
                long sourceCount = ((long)resolution.x + 1) * (resolution.y + 1);
                if (!region.Projection.IsValid || math.any(resolution < 1) || region.HeightOffset < 0 || region.MaskOffset < 0 ||
                    sourceCount <= 0 || sourceCount + region.HeightOffset > source.RegionHeights.Length || sourceCount + region.MaskOffset > source.RegionMasks.Length) return false;
                int first = (int)headerCount, level = 0;
                // Resolved material overlays have no geometric effect. Do not derive millions of zero-height texels for them.
                bool geometry = region.Mode == SurfaceRegionMode.Replace || region.MinimumHeight != 0 || region.MaximumHeight != 0 || region.DetailPolicy == SurfaceDetailPolicy.Suppress;
                if (geometry)
                {
                    support += sourceCount;
                    while (resolution.x > 1 || resolution.y > 1)
                    {
                        resolution = math.max(new int2(1), (resolution + 1) / 2); level++;
                        long n = ((long)resolution.x + 5) * (resolution.y + 5);
                        support += n * 9;
                        if (nodes + n > int.MaxValue || support > settings.MaximumSupportSamples)
                        { status = SurfaceRegionFilterStatus.BudgetExceeded; return false; }
                        list?.Add(new SurfaceRegionFilterMip(resolution, (int)nodes, i, level)); nodes += n; headerCount++;
                    }
                }
                if (allocate) ranges[i] = new int2(first, level);
                bytes = nodes * 8 + headerCount * 24 + (long)source.Regions.Length * 8 + 4;
                if (bytes > settings.MaximumBytes) { status = SurfaceRegionFilterStatus.BudgetExceeded; return false; }
            }
            count = (int)nodes; headers = list?.ToArray(); status = SurfaceRegionFilterStatus.Pending; return true;
        }
        public bool CompleteIfReady()
        {
            if (disposed) return false;
            if (!completed)
            {
                if (!job.IsCompleted) return false;
                job.Complete(); completed = true;
                Status = result[0] == 1 ? SurfaceRegionFilterStatus.Ready : SurfaceRegionFilterStatus.InvalidData;
            }
            return Status == SurfaceRegionFilterStatus.Ready;
        }
        // Explicit offline/test boundary; rendering uses CompleteIfReady and never waits for a cold build.
        public void Complete() { if (disposed) throw new ObjectDisposedException(nameof(NativeSurfaceRegionFilter)); job.Complete(); completed = true; Status = result[0] == 1 ? SurfaceRegionFilterStatus.Ready : SurfaceRegionFilterStatus.InvalidData; }
        public void CopySamples(int start, int count, float2[] target)
        {
            if (Status != SurfaceRegionFilterStatus.Ready || disposed || target == null || count > target.Length || start < 0 || count < 0 || (long)start + count > samples.Length)
                throw new ArgumentException("A bounded copy requires ready filter support and a valid range.");
            for (int i = 0; i < count; i++) target[i] = samples[start + i];
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; job.Complete();
            if (ranges.IsCreated) ranges.Dispose(); if (mips.IsCreated) mips.Dispose(); if (samples.IsCreated) samples.Dispose(); if (result.IsCreated) result.Dispose();
        }
        [BurstCompile]
        struct BuildJob : IJob
        {
            [ReadOnly] public NativeSurfaceView Source;
            [ReadOnly] public NativeArray<SurfaceRegionFilterMip> Mips;
            public NativeArray<float2> Samples;
            public NativeArray<int> Result;
            public void Execute()
            {
                for (int m = 0; m < Mips.Length; m++)
                {
                    var header = Mips[m]; var region = Source.Regions[header.RegionIndex];
                    var extent = region.Projection.MaximumMetres - region.Projection.MinimumMetres;
                    var step = extent / (double2)header.Resolution;
                    for (int y = -2; y <= header.Resolution.y + 2; y++) for (int x = -2; x <= header.Resolution.x + 2; x++)
                    {
                        double2 sum = default;
                        // The outer support node is always zero, rather than a clamped edge that extends forever.
                        if (x != -2 && y != -2 && x != header.Resolution.x + 2 && y != header.Resolution.y + 2)
                            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
                            {
                                var p = region.Projection.MinimumMetres + step * (new double2(x, y) + new double2(i, j) * .5);
                                double2 pair;
                                bool valid = header.Level == 1
                                    ? SurfaceRegionFilterMath.TryFull(Source, region, p, out pair)
                                    : SurfaceRegionFilterMath.TryMip(Samples.AsReadOnly(), Mips[m - 1], region.Projection, p, out pair);
                                if (!valid) { Result[0] = -1; return; }
                                sum += pair * ((i == 0 ? .5 : .25) * (j == 0 ? .5 : .25));
                            }
                        var packed = (float2)sum;
                        if (!math.all(math.isfinite(packed))) { Result[0] = -1; return; }
                        Samples[header.Offset + (y + 2) * header.Row + x + 2] = packed;
                    }
                }
                Result[0] = 1;
            }
        }
    }

    static class SurfaceRegionFilterMath
    {
        internal static bool TryFull(in NativeSurfaceView source, SurfaceRegionHeader region, double2 metres, out double2 pair)
        {
            pair = default; var p = region.Projection;
            if (!math.all(math.isfinite(metres))) return false;
            if (math.any(metres < p.MinimumMetres) || math.any(metres > p.MaximumMetres)) return true;
            var uv = (metres - p.MinimumMetres) / (p.MaximumMetres - p.MinimumMetres);
            if (!Bilinear(source.RegionHeights, region.HeightOffset, region.Resolution, uv, out var height) ||
                !Bilinear(source.RegionMasks, region.MaskOffset, region.Resolution, uv, out var mask)) return false;
            double alpha = math.clamp(mask, 0, 1);
            if (region.BlendMetres > 0)
            {
                double t = math.clamp(math.cmin(math.min(metres - p.MinimumMetres, p.MaximumMetres - metres)) / region.BlendMetres, 0, 1);
                alpha *= t * t * (3 - 2 * t);
            }
            pair = new double2(height * alpha, alpha); return math.all(math.isfinite(pair));
        }
        static bool Bilinear(NativeArray<float>.ReadOnly values, int offset, int2 size, double2 uv, out double value)
        {
            var grid = uv * (double2)size;
            int x = (int)math.min(size.x - 1, math.floor(grid.x)), y = (int)math.min(size.y - 1, math.floor(grid.y));
            var f = grid - new double2(x, y); int index = offset + y * (size.x + 1) + x;
            value = math.lerp(math.lerp((double)values[index], values[index + 1], f.x),
                math.lerp((double)values[index + size.x + 1], values[index + size.x + 2], f.x), f.y);
            return math.isfinite(value);
        }
        internal static bool TryMip(NativeArray<float2>.ReadOnly values, SurfaceRegionFilterMip mip, SurfaceRegionProjection projection,
            double2 metres, out double2 value)
        {
            value = default;
            var grid = (metres - projection.MinimumMetres) / (projection.MaximumMetres - projection.MinimumMetres) * (double2)mip.Resolution + 2;
            if (!math.all(math.isfinite(grid))) return false;
            if (math.any(grid < 0) || grid.x > mip.Resolution.x + 4 || grid.y > mip.Resolution.y + 4) return true;
            int x = (int)math.min(mip.Resolution.x + 3, math.floor(grid.x)), y = (int)math.min(mip.Resolution.y + 3, math.floor(grid.y));
            var f = grid - new double2(x, y); int index = mip.Offset + y * mip.Row + x;
            if (index < 0 || index + mip.Row + 1 >= values.Length) return false;
            value = math.lerp(math.lerp((double2)values[index], values[index + 1], f.x),
                math.lerp((double2)values[index + mip.Row], values[index + mip.Row + 1], f.x), f.y);
            return math.all(math.isfinite(value));
        }
    }
}
