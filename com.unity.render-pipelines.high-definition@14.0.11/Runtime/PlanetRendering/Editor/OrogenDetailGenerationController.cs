using System;
using System.Diagnostics;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class OrogenDetailGenerationController : IDisposable
    {
        OrogenDetailCaptureSession session;
        PlanetGeneratorAsset target;
        PlanetSurfaceDataAsset source;
        WorldOrogenDetailRecipe captured;
        SurfaceContentHash capturedSourceDigest, capturedSourceSettings;
        Texture2DArray capturedColour;
        bool cancelled;
        public bool IsRunning => session != null;
        public string Stage { get; private set; } = "Ready";
        public string Error { get; private set; }
        public double Progress01 { get; private set; }
        public event Action Changed;
        public void Start(PlanetGeneratorAsset generator)
        {
            if (IsRunning) throw new InvalidOperationException("Wait for detail capture or cancel it first.");
            if (!generator || generator.TerrainDetail == null) throw new ArgumentException("A generator with detail settings is required.");
            var recipe = generator.TerrainDetail.Capture(generator.Seed);
            if (!recipe.IsValid) throw new ArgumentException("Invalid terrain detail settings. Wavelength must be at least 64 m; conditioning resolution must be a power of two from 2 to 512.");
            var retained = OrogenDetailPublication.RetainedBase(generator.SurfaceData);
            OrogenDetailPublication.CheckCurrent(generator, generator.SurfaceData, recipe, generator.SurfaceData.WorldOrogenSource, retained.Recipe.Radius);
            if (!AssetDatabase.GetAssetPath(generator).StartsWith("Assets/", StringComparison.Ordinal)) throw new InvalidOperationException("Save the generator in Assets first.");
            session = OrogenDetailCaptureSession.Begin(retained, recipe); target = generator; source = generator.SurfaceData;
            if (!source.TryCreateSnapshot(out var published, out var sourceError)) { session.Dispose(); session = null; throw new InvalidOperationException(sourceError); }
            capturedSourceDigest = published.ContentDigest; capturedSourceSettings = source.WorldOrogenSource.ConfigurationDigest(); capturedColour = source.BaseColour;
            captured = recipe; cancelled = false; Error = null; Stage = "Starting detail capture"; Progress01 = 0; Changed?.Invoke();
        }
        public void Cancel() { cancelled = true; }
        public void Tick()
        {
            if (!IsRunning) return;
            try
            {
                if (cancelled) throw new OperationCanceledException();
                var watch = Stopwatch.StartNew(); int steps = 0;
                do
                {
                    session.Step(() => cancelled); Stage = session.Stage; Progress01 = session.Progress01 * .95;
                    if (!string.IsNullOrEmpty(session.Error)) throw new InvalidOperationException(session.Error);
                } while (!session.IsCompleted && ++steps < 8 && watch.ElapsedMilliseconds < 4);
                if (session.IsCompleted)
                {
                    var field = session.TakeResult(); session.Dispose(); session = null;
                    if (!source || !source.TryCreateSnapshot(out var latest, out _) || latest.ContentDigest != capturedSourceDigest ||
                        source.WorldOrogenSource == null || source.WorldOrogenSource.ConfigurationDigest() != capturedSourceSettings || source.BaseColour != capturedColour)
                        throw new InvalidOperationException("The saved source map changed during detail capture; candidate was not published.");
                    OrogenDetailPublication.Publish(target, source, captured, field); Stage = "Saved terrain detail"; Progress01 = 1;
                }
            }
            catch (Exception e)
            {
                session?.Dispose(); session = null; Stage = e is OperationCanceledException ? "Cancelled" : "Failed";
                Error = e is OperationCanceledException ? null : e.Message;
                if (!(e is OperationCanceledException)) UnityEngine.Debug.LogError("Terrain detail capture failed: " + e);
            }
            Changed?.Invoke();
        }
        public void Dispose() { cancelled = true; session?.Dispose(); session = null; }
    }
}
