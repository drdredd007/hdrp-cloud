using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;
using Unity.Jobs;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum PlanetSurfaceLandformFilterStatus
    {
        Pending, Ready, Cancelled, BudgetExceeded, InvalidData, Failed
    }

    /// <summary>
    /// Renderer-only child-content cache. The module accounts retained source managed data, derived
    /// managed/native data and reserved worker scratch; this is not a total engine RSS budget.
    /// Build and native copies run on one worker. Poll never waits for a worker or native reader.
    /// </summary>
    public static class PlanetSurfaceLandformFiltering
    {
        public const long MaximumModuleBytes = 512L * 1024 * 1024;
        readonly struct Key : IEquatable<Key>
        {
            internal readonly SurfaceContentHash Source, Policy;
            internal Key(SurfaceContentHash source, SurfaceContentHash policy) { Source = source; Policy = policy; }
            public bool Equals(Key other) => Source == other.Source && Policy == other.Policy;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() { unchecked { return Source.GetHashCode() * 397 ^ Policy.GetHashCode(); } }
        }

        internal sealed class Entry
        {
            internal SurfaceLandformField Source;
            internal readonly SurfaceLandformFilterSettings Settings;
            internal readonly SurfaceContentHash SourceDigest, PolicyDigest;
            internal SurfaceLandformFilterHierarchy Hierarchy;
            internal NativeSurfaceLandformFilterData Native;
            internal Task<Prepared> Work;
            internal CancellationTokenSource Cancellation;
            internal JobHandle Readers;
            internal PlanetSurfaceLandformFilterStatus Status = PlanetSurfaceLandformFilterStatus.Pending;
            internal string Failure;
            internal long Bytes;
            internal int References, Generation;
            internal bool Retired, Disposed;
            internal Entry(SurfaceLandformField source, SurfaceLandformFilterSettings settings, long bytes)
            { Source = source; Settings = settings; SourceDigest = source.ContentDigest; PolicyDigest = settings.PolicyDigest; Bytes = bytes; }
        }

        internal sealed class Prepared
        {
            internal SurfaceLandformFilterHierarchy Hierarchy;
            internal NativeSurfaceLandformFilterData Native;
        }

        sealed class SourceIdentity : IEqualityComparer<SurfaceLandformField>
        {
            public bool Equals(SurfaceLandformField a, SurfaceLandformField b) => ReferenceEquals(a, b);
            public int GetHashCode(SurfaceLandformField field) => RuntimeHelpers.GetHashCode(field);
        }

        static readonly Dictionary<Key, Entry> entries = new Dictionary<Key, Entry>();
        static readonly Dictionary<SurfaceLandformField, int> sources = new Dictionary<SurfaceLandformField, int>(new SourceIdentity());
        static readonly Queue<Entry> queued = new Queue<Entry>();
        static readonly List<Entry> retired = new List<Entry>();
        static Entry building;
        static long residentBytes;
        static int generation, startedWorkers;
        static bool installed;

        /// <summary>Includes complete pending reservations and retired readers, not only ready buffers.</summary>
        public static long ResidentBytes => residentBytes;
        public static int ResidentEntries => entries.Count + retired.Count;
        public static int RetiredEntries => retired.Count;
        public static int ActiveWorkers => building == null ? 0 : 1;
        public static int StartedWorkers => startedWorkers;

        public static bool TryAcquire(SurfaceLandformField source, out PlanetSurfaceLandformFilterLease lease,
            out PlanetSurfaceLandformFilterStatus status) => TryAcquire(source, SurfaceLandformFilterSettings.Default, out lease, out status, out _);
        public static bool TryAcquire(SurfaceLandformField source, SurfaceLandformFilterSettings settings,
            out PlanetSurfaceLandformFilterLease lease, out PlanetSurfaceLandformFilterStatus status)
            => TryAcquire(source, settings, out lease, out status, out _);

        public static bool TryAcquire(SurfaceLandformField source, SurfaceLandformFilterSettings settings,
            out PlanetSurfaceLandformFilterLease lease, out PlanetSurfaceLandformFilterStatus status, out string failure)
        {
            PlanetSurfaceDataRegistry.CheckMainThread(); lease = null; failure = null;
            if (source == null || source.SourceRecipe.AlgorithmVersion != 5 || !source.ContentDigest.IsValid || !settings.IsValid)
            { status = PlanetSurfaceLandformFilterStatus.InvalidData; failure = "An immutable algorithm-five child and valid filter policy are required."; return false; }
            SurfaceLandformFilterEstimate estimate;
            long reserve;
            try
            {
                // Admission caps remain effective even when the same numerical policy is already cached.
                if (!SurfaceLandformFilterHierarchy.TryEstimate(source, settings, out estimate, out failure))
                { status = PlanetSurfaceLandformFilterStatus.BudgetExceeded; return false; }
                reserve = estimate.PeakWorkingBytes;
            }
            catch (Exception error) when (error is ArgumentException || error is OverflowException)
            { status = PlanetSurfaceLandformFilterStatus.InvalidData; failure = error.Message; return false; }

            var key = new Key(source.ContentDigest, settings.PolicyDigest);
            if (entries.TryGetValue(key, out var existing))
            { existing.References++; lease = new PlanetSurfaceLandformFilterLease(existing); status = existing.Status; failure = existing.Failure; return true; }
            long additionalSource = sources.ContainsKey(source) ? 0 : source.EstimatedResidentBytes;
            if (checked(reserve + additionalSource) > MaximumModuleBytes - residentBytes)
            {
                status = PlanetSurfaceLandformFilterStatus.BudgetExceeded;
                failure = $"Landform filter reservation {reserve + additionalSource} bytes exceeds the remaining module budget {MaximumModuleBytes - residentBytes}; poll/release existing readers before retrying.";
                return false;
            }
            var entry = new Entry(source, settings, reserve) { References = 1 };
            entries.Add(key, entry); queued.Enqueue(entry); residentBytes += reserve;
            if (sources.TryGetValue(source, out int sourceReferences)) sources[source] = sourceReferences + 1;
            else { sources.Add(source, 1); residentBytes += source.EstimatedResidentBytes; }
            lease = new PlanetSurfaceLandformFilterLease(entry); status = entry.Status;
            InstallPump(); ScheduleNext(); return true;
        }

        /// <summary>Borrow ready data with an independent held lease. No authority/native arrays are materialized here.</summary>
        public static bool TryBorrow(SurfaceContentHash sourceDigest, SurfaceContentHash policyDigest,
            out PlanetSurfaceLandformFilterLease lease, out PlanetSurfaceLandformFilterStatus status)
        {
            PlanetSurfaceDataRegistry.CheckMainThread(); lease = null;
            if (!sourceDigest.IsValid || !policyDigest.IsValid)
            { status = PlanetSurfaceLandformFilterStatus.InvalidData; return false; }
            if (!entries.TryGetValue(new Key(sourceDigest, policyDigest), out var entry))
            { status = PlanetSurfaceLandformFilterStatus.Pending; return false; }
            status = entry.Status;
            if (status != PlanetSurfaceLandformFilterStatus.Ready) return false;
            entry.References++; lease = new PlanetSurfaceLandformFilterLease(entry); return true;
        }
        public static bool TryBorrow(SurfaceLandformField source, out PlanetSurfaceLandformFilterLease lease,
            out PlanetSurfaceLandformFilterStatus status)
            => TryBorrow(source?.ContentDigest ?? default, SurfaceLandformFilterSettings.Default.PolicyDigest, out lease, out status);

        /// <summary>Main-thread bookkeeping only. An unfinished worker/reader remains pinned and reserved.</summary>
        public static void Poll()
        {
            PlanetSurfaceDataRegistry.CheckMainThread(); AcceptWorker(false); CollectRetired(false); ScheduleNext();
        }

        static void ScheduleNext()
        {
            if (building != null) return;
            while (queued.Count > 0)
            {
                var entry = queued.Dequeue();
                if (entry.Retired || entry.Disposed || entry.Status != PlanetSurfaceLandformFilterStatus.Pending) continue;
                entry.Cancellation = new CancellationTokenSource(); var token = entry.Cancellation.Token;
                var source = entry.Source; var settings = entry.Settings;
                try
                {
                    entry.Work = Task.Run(() => Prepare(source, settings, token));
                    building = entry; startedWorkers++; return;
                }
                catch (Exception error)
                {
                    entry.Cancellation.Dispose(); entry.Cancellation = null;
                    entry.Status = PlanetSurfaceLandformFilterStatus.Failed; entry.Failure = error.Message;
                    Resize(entry, 0); entry.Generation = ++generation;
                }
            }
        }

        static Prepared Prepare(SurfaceLandformField source, SurfaceLandformFilterSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var hierarchy = SurfaceLandformFilterHierarchy.Build(source, settings, () => token.IsCancellationRequested);
            token.ThrowIfCancellationRequested();
            NativeSurfaceLandformFilterData native = null;
            try
            {
                // The main-thread acceptance path never clones these potentially large derived arrays.
                native = new NativeSurfaceLandformFilterData(hierarchy, Allocator.Persistent);
                token.ThrowIfCancellationRequested(); return new Prepared { Hierarchy = hierarchy, Native = native };
            }
            catch { native?.Dispose(); throw; }
        }

        static void AcceptWorker(bool wait)
        {
            var entry = building;
            if (entry == null || (!wait && !entry.Work.IsCompleted)) return;
            try
            {
                var result = entry.Work.GetAwaiter().GetResult();
                entry.Hierarchy = result.Hierarchy; entry.Native = result.Native;
                long actual = checked(result.Hierarchy.EstimatedResidentBytes + result.Native.EstimatedNativeBytes);
                if (actual > entry.Bytes)
                {
                    // Keep the already-admitted allocation accounted until it is disposed; never publish an undercounted view.
                    entry.Native.Dispose(); entry.Native = null; entry.Hierarchy = null;
                    entry.Status = PlanetSurfaceLandformFilterStatus.Failed;
                    entry.Failure = "Derived filter exceeded its preflight reservation.";
                    Resize(entry, 0);
                }
                else
                { Resize(entry, actual); entry.Status = entry.Retired ? PlanetSurfaceLandformFilterStatus.Cancelled : PlanetSurfaceLandformFilterStatus.Ready; }
            }
            catch (OperationCanceledException)
            { entry.Native?.Dispose(); entry.Native = null; entry.Hierarchy = null; entry.Status = PlanetSurfaceLandformFilterStatus.Cancelled; entry.Failure = "Landform filter preparation was cancelled."; Resize(entry, 0); }
            catch (Exception error)
            { entry.Native?.Dispose(); entry.Native = null; entry.Hierarchy = null; entry.Status = PlanetSurfaceLandformFilterStatus.Failed; entry.Failure = error.Message; Resize(entry, 0); }
            finally
            { entry.Work = null; entry.Cancellation.Dispose(); entry.Cancellation = null; building = null; entry.Generation = ++generation; }
        }

        static void Resize(Entry entry, long bytes) { residentBytes += bytes - entry.Bytes; entry.Bytes = bytes; }

        internal static void Release(Entry entry)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if (entry.Disposed) return;
            if (--entry.References != 0) return;
            entries.Remove(new Key(entry.SourceDigest, entry.PolicyDigest)); entry.Retired = true;
            if (entry.Status == PlanetSurfaceLandformFilterStatus.Pending) entry.Status = PlanetSurfaceLandformFilterStatus.Cancelled;
            entry.Cancellation?.Cancel(); retired.Add(entry);
            // No Task.GetResult/JobHandle.Complete on an unfinished reader in a release/render callback.
            CollectRetired(false);
        }

        static void CollectRetired(bool wait)
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                var entry = retired[i];
                if (entry.Work != null || (!wait && !entry.Readers.IsCompleted)) continue;
                entry.Readers.Complete(); entry.Native?.Dispose(); entry.Native = null; entry.Hierarchy = null;
                int sourceReferences = sources[entry.Source] - 1;
                if (sourceReferences > 0) sources[entry.Source] = sourceReferences;
                else { sources.Remove(entry.Source); residentBytes -= entry.Source.EstimatedResidentBytes; }
                entry.Source = null;
                entry.Disposed = true; entry.Status = PlanetSurfaceLandformFilterStatus.Cancelled;
                residentBytes -= entry.Bytes; entry.Bytes = 0; retired.RemoveAt(i);
            }
        }

        /// <summary>Explicit blocking offline/editor prewarming. Render callbacks must use Poll instead.</summary>
        public static PlanetSurfaceLandformFilterStatus CompletePreparation(PlanetSurfaceLandformFilterLease lease)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if (lease == null || lease.IsDisposed) throw new ObjectDisposedException(nameof(PlanetSurfaceLandformFilterLease));
            var target = lease.Entry;
            while (target.Status == PlanetSurfaceLandformFilterStatus.Pending)
            {
                Poll();
                if (target.Status != PlanetSurfaceLandformFilterStatus.Pending) break;
                if (building == null) throw new InvalidOperationException("Queued filter preparation has no worker capable of progress.");
                // Completing the other dataset first also handles a target queued behind a retired/cancelled worker.
                AcceptWorker(true);
            }
            return target.Status;
        }

        static void InstallPump()
        {
            if (installed) return; installed = true;
            RenderPipelineManager.beginFrameRendering += OnFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += Poll;
#endif
        }
        static void OnFrame(ScriptableRenderContext context, Camera[] cameras) => Poll();

        /// <summary>Explicit shutdown/offline cleanup only; cancels and waits for every worker and native reader.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetOffline()
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            // Registry Task readers hold ordinary canonical leases rather than AddDependency jobs.
            // Cleanup hook order is undefined; finish those readers before invalidating borrowed filter banks.
            PlanetSurfaceDataRegistry.CompleteReadersBeforeFilterReset();
            PlanetSurfaceLandformGpuFiltering.ReleaseAllBeforeCpuReset();
            foreach (var entry in entries.Values)
            { entry.Retired = true; entry.Cancellation?.Cancel(); retired.Add(entry); }
            entries.Clear(); queued.Clear();
            AcceptWorker(true); CollectRetired(true);
            sources.Clear(); building = null; residentBytes = 0; generation = 0; startedWorkers = 0;
            if (!installed) return;
            RenderPipelineManager.beginFrameRendering -= OnFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= Poll;
#endif
            installed = false;
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallCleanup() => UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ResetOffline;
#endif
    }

    /// <summary>Derived-only native reader. A separate canonical lease supplies original negative control references.</summary>
    public sealed class PlanetSurfaceLandformFilterLease : IDisposable
    {
        PlanetSurfaceLandformFiltering.Entry entry;
        internal PlanetSurfaceLandformFilterLease(PlanetSurfaceLandformFiltering.Entry entry) { this.entry = entry; }
        internal PlanetSurfaceLandformFiltering.Entry Entry => entry;
        public bool IsDisposed => entry == null || entry.Disposed;
        public bool IsReady => !IsDisposed && entry.Status == PlanetSurfaceLandformFilterStatus.Ready;
        public PlanetSurfaceLandformFilterStatus Status => IsDisposed ? PlanetSurfaceLandformFilterStatus.Cancelled : entry.Status;
        public string Failure => entry?.Failure;
        public SurfaceContentHash SourceDigest => entry?.SourceDigest ?? default;
        public SurfaceContentHash PolicyDigest => entry?.PolicyDigest ?? default;
        public int Generation => entry?.Generation ?? 0;
        /// <summary>Per-entry allocation plus its retained source; aggregate ResidentBytes shares source references once.</summary>
        public long EstimatedBytes => (entry?.Bytes ?? 0) + (entry?.Source?.EstimatedResidentBytes ?? 0);
        public NativeSurfaceLandformFilterView NativeView => IsReady ? entry.Native.View : throw new InvalidOperationException("Landform filtering is pending, failed or released.");
        public bool Poll() { PlanetSurfaceLandformFiltering.Poll(); return IsReady; }
        public void AddDependency(JobHandle reader)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();
            if (!IsReady) throw new InvalidOperationException("A native reader requires a ready held landform filter lease.");
            entry.Readers = JobHandle.CombineDependencies(entry.Readers, reader);
        }
        public void Dispose()
        {
            if (entry == null) return; PlanetSurfaceDataRegistry.CheckMainThread();
            var old = entry; entry = null; PlanetSurfaceLandformFiltering.Release(old);
        }
    }
}
