using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Content/policy-addressed, bounded derived support. It never registers a new authoritative revision.</summary>
    public static class PlanetSurfaceRegionFiltering
    {
        public const long MaximumResidentBytes = 128L * 1024 * 1024;
        public const int MaximumUploadSamplesPerFrame = 65536;
        static readonly Dictionary<PlanetSurfaceDescriptor, Entry> entries = new Dictionary<PlanetSurfaceDescriptor, Entry>();
        static readonly List<Entry> retired = new List<Entry>();
        static long residentBytes;
        static int generation;
        static int uploadFrame;
        static bool installed;
        static Entry building;
        static PlanetSurfaceDescriptor Key(PlanetSurfaceDescriptor descriptor) => new PlanetSurfaceDescriptor
        {ContentDigestA=descriptor.ContentDigestA,ContentDigestB=descriptor.ContentDigestB,Epoch=SurfaceRegionFilterSettings.AlgorithmVersion};
        public static long ResidentBytes => residentBytes;
        public static int ResidentDescriptors => entries.Count + retired.Count;

        internal sealed class Entry
        {
            internal readonly PlanetSurfaceDescriptor Descriptor;
            internal readonly PlanetSurfaceDataLease Source;
            internal readonly long Bytes;
            internal NativeSurfaceRegionFilter Native;
            internal GraphicsBuffer Ranges, Mips, Samples;
            internal SurfaceRegionFilterStatus Status = SurfaceRegionFilterStatus.Pending;
            internal int References, Uploaded, LastUploadFrame = -1, Generation;
            internal float2[] Upload;
            internal bool Released,Disposed;
            internal Entry(PlanetSurfaceDescriptor descriptor, PlanetSurfaceDataLease source, long bytes)
            { Descriptor = descriptor; Source = source; Bytes = bytes; }
            internal bool Poll()
            {
                if(Disposed)return false;
                if (Status != SurfaceRegionFilterStatus.Pending || Native == null) return Status == SurfaceRegionFilterStatus.Ready;
                if (!Native.CompleteIfReady())
                { if (Native.Status == SurfaceRegionFilterStatus.InvalidData) Status = Native.Status; return false; }
                if (Released) return false;
                if (LastUploadFrame == uploadFrame) return false;
                LastUploadFrame = uploadFrame;
                var view = Native.View;
                if (Ranges == null)
                {
                    var ranges = new int2[math.max(1, view.Ranges.Length)];
                    for (int i = 0; i < view.Ranges.Length; i++) ranges[i] = view.Ranges[i];
                    var mips = new int4[math.max(1, view.Mips.Length)];
                    for (int i = 0; i < view.Mips.Length; i++)
                    { var h = view.Mips[i]; mips[i] = new int4(h.Resolution, h.Offset, h.Level); }
                    Ranges = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ranges.Length, 8); Ranges.SetData(ranges);
                    Mips = new GraphicsBuffer(GraphicsBuffer.Target.Structured, mips.Length, 16); Mips.SetData(mips);
                    Samples = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, view.Samples.Length), 8);
                    Upload = new float2[math.min(MaximumUploadSamplesPerFrame, math.max(1, view.Samples.Length))];
                    if (view.Samples.Length == 0) Samples.SetData(Upload, 0, 0, 1);
                }
                int count = math.min(MaximumUploadSamplesPerFrame, view.Samples.Length - Uploaded);
                if (count > 0) { Native.CopySamples(Uploaded, count, Upload); Samples.SetData(Upload, 0, Uploaded, count); Uploaded += count; }
                if (Uploaded < view.Samples.Length) return false;
                Upload = null; Status = SurfaceRegionFilterStatus.Ready; Generation = ++generation; return true;
            }
            internal void Dispose()
            { if(Disposed)return;Disposed=true;Status=SurfaceRegionFilterStatus.InvalidData;Native?.Dispose(); Native = null; Ranges?.Dispose(); Mips?.Dispose(); Samples?.Dispose(); Ranges = Mips = Samples = null; Upload = null; Source.Dispose(); residentBytes -= Bytes; }
        }

        public static bool TryAcquire(PlanetSurfaceDescriptor descriptor, out PlanetSurfaceRegionFilterLease lease, out SurfaceRegionFilterStatus status)
        {
            PlanetSurfaceDataRegistry.CheckMainThread(); lease = null; InstallPump(); Collect();
            var key=Key(descriptor);
            if (!PlanetSurfaceDataRegistry.TryAcquire(descriptor, out var source)) { status = SurfaceRegionFilterStatus.InvalidData; return false; }
            try
            {
                // Content sharing is eligible only after exact authoritative registration is verified.
                if (entries.TryGetValue(key, out var entry))
                { entry.References++; lease = new PlanetSurfaceRegionFilterLease(entry); Poll(entry); status = entry.Status; return true; }
                if (!NativeSurfaceRegionFilter.TryEstimate(source.View, SurfaceRegionFilterSettings.Default, out var nativeBytes, out _, out status)) return false;
                // Native support, GPU support, temporary packed headers and the reusable upload chunk.
                long bytes = checked(nativeBytes * 3 + MaximumUploadSamplesPerFrame * 8L);
                if (bytes > MaximumResidentBytes - residentBytes) { status = SurfaceRegionFilterStatus.BudgetExceeded; return false; }
                entry = new Entry(descriptor, source, bytes) { References = 1 }; entries.Add(key, entry); residentBytes += bytes;
                source = null; lease = new PlanetSurfaceRegionFilterLease(entry); Poll(entry); status = entry.Status; return true;
            }
            finally { source?.Dispose(); }
        }
        internal static bool Poll(Entry entry)
        {
            PlanetSurfaceDataRegistry.CheckMainThread();if(entry.Disposed)return false; Collect();
            if (building != null && building.Native != null && building.Native.CompleteIfReady()) building = null;
            else if (building != null && building.Native != null && building.Native.Status == SurfaceRegionFilterStatus.InvalidData)
            { building.Status = SurfaceRegionFilterStatus.InvalidData; building = null; }
            if (entry.Native == null && entry.Status == SurfaceRegionFilterStatus.Pending && building == null)
            {
                if (!NativeSurfaceRegionFilter.TrySchedule(entry.Source.View, SurfaceRegionFilterSettings.Default, Allocator.Persistent, out entry.Native, out var status)) entry.Status = status;
                else building = entry;
            }
            return entry.Poll();
        }
        internal static void Release(Entry entry)
        {
            PlanetSurfaceDataRegistry.CheckMainThread(); if (--entry.References != 0) return;
            if(entry.Disposed)return;
            var key=Key(entry.Descriptor);if(entries.TryGetValue(key,out var current)&&ReferenceEquals(current,entry))entries.Remove(key); entry.Released = true;
            if (entry.Native != null && entry.Native.Status == SurfaceRegionFilterStatus.Pending && !entry.Native.CompleteIfReady()) retired.Add(entry);
            else { if (ReferenceEquals(building, entry)) building = null; entry.Dispose(); }
            Collect();
        }
        static void Collect()
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                var entry = retired[i];
                if (!entry.Native.CompleteIfReady() && entry.Native.Status == SurfaceRegionFilterStatus.Pending) continue;
                if (ReferenceEquals(building, entry)) building = null;
                retired.RemoveAt(i); entry.Dispose();
            }
        }
        static void InstallPump()
        {
            if(installed)return;installed=true;RenderPipelineManager.beginFrameRendering+=OnFrame;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update+=OnEditorUpdate;
#endif
        }
        static void OnFrame(ScriptableRenderContext context,Camera[] cameras){uploadFrame++;Pump();}
        static void Pump(){Collect();foreach(var entry in entries.Values)Poll(entry);}
#if UNITY_EDITOR
        static void OnEditorUpdate(){if(!Application.isPlaying){uploadFrame++;Pump();}}
#endif
        internal static int Generation(PlanetSurfaceDescriptor descriptor)
        { if (entries.TryGetValue(Key(descriptor), out var entry)) { Poll(entry); return entry.Generation; } return 0; }
        internal static long EstimateGpuBytes(in NativeSurfaceView source)
        {
            if (!NativeSurfaceRegionFilter.TryEstimate(source, SurfaceRegionFilterSettings.Default, out var bytes, out _, out _)) return 0;
            return bytes; // Conservative header allowance; reserved whether cold or ready.
        }
        internal static bool TryBorrow(PlanetSurfaceDescriptor descriptor, out Entry entry)
        { if (entries.TryGetValue(Key(descriptor), out entry)) { Poll(entry); return entry.Status == SurfaceRegionFilterStatus.Ready; } return false; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            foreach (var entry in entries.Values) entry.Dispose(); foreach (var entry in retired) entry.Dispose(); entries.Clear(); retired.Clear(); building = null; residentBytes = 0; generation = 0;uploadFrame=0;
            if(installed){RenderPipelineManager.beginFrameRendering-=OnFrame;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update-=OnEditorUpdate;
#endif
                installed=false;}
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallCleanup() { UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Reset; }
#endif
    }

    public sealed class PlanetSurfaceRegionFilterLease : IDisposable
    {
        PlanetSurfaceRegionFiltering.Entry entry;
        internal PlanetSurfaceRegionFilterLease(PlanetSurfaceRegionFiltering.Entry entry) { this.entry = entry; }
        public SurfaceRegionFilterStatus Status => entry != null ? entry.Status : SurfaceRegionFilterStatus.InvalidData;
        public bool IsDisposed=>entry==null||entry.Disposed;
        public bool IsReady => entry != null && PlanetSurfaceRegionFiltering.Poll(entry);
        public int Generation => entry?.Generation ?? 0;
        public long EstimatedBytes => entry?.Bytes ?? 0;
        public NativeSurfaceRegionFilterView View => entry != null && IsReady ? entry.Native.View : throw new InvalidOperationException("Regional filtering is pending.");
        internal PlanetSurfaceRegionFiltering.Entry Entry => entry;
        public void Dispose() { if (entry == null) return; var old = entry; entry = null; PlanetSurfaceRegionFiltering.Release(old); }
    }
}
