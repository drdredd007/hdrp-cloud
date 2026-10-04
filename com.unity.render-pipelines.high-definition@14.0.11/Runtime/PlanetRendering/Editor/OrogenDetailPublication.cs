using System;
using System.IO;
using SpaceRunner.PlanetTerrain;
using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Publish a detail child without rewriting the retained World Orogen height or colour map.</summary>
    public static class OrogenDetailPublication
    {
        public static SurfaceSnapshot RetainedBase(PlanetSurfaceDataAsset source)
        {
            if (!source || source.WorldOrogenSource == null || !source.TryCreateSnapshot(out var snapshot, out _))
                throw new ArgumentException("A saved World Orogen map is required.");
            if (snapshot.StructuralField != null || snapshot.Detail.AmplitudeMetres != 0 || snapshot.Regions.Count != 0 ||
                snapshot.Stamps.Count != 0 || snapshot.HasAutomaticMaterials)
                throw new ArgumentException("Capture detail from the unmodified World Orogen base map before adding authored regions or impacts.");
            return snapshot.OrogenDetail == null ? snapshot : new SurfaceSnapshot(snapshot.Recipe, snapshot.Revision,
                snapshot.CanonicalTileLevel, snapshot.Resolution, snapshot.Tiles);
        }

        public static PlanetSurfaceDataAsset Publish(PlanetGeneratorAsset generator, PlanetSurfaceDataAsset source,
            WorldOrogenDetailRecipe captured, WorldOrogenDetailField detail)
        {
            if (!generator || !source) throw new ArgumentNullException("generator/source");
            var retained = RetainedBase(source); var sourceSettings = source.WorldOrogenSource;
            CheckCurrent(generator, source, captured, sourceSettings, retained.Recipe.Radius);
            if (detail != null && (detail.Recipe.ContentDigest != captured.ContentDigest || detail.SourceBaseDigest != retained.Revision.BaseDigest))
                throw new ArgumentException("Detail belongs to different capture parameters or a different base map.");
            var candidate = new SurfaceSnapshot(retained.Recipe, retained.Revision, retained.CanonicalTileLevel,
                retained.Resolution, retained.Tiles, orogenDetail: detail);
            string target = AssetDatabase.GetAssetPath(generator).Replace('\\','/');
            if (!target.StartsWith("Assets/", StringComparison.Ordinal)) throw new InvalidOperationException("Save the generator in Assets first.");
            string path = AssetDatabase.GenerateUniqueAssetPath(Path.GetDirectoryName(target).Replace('\\','/') + "/" +
                Path.GetFileNameWithoutExtension(target) + (detail != null ? "_WorldOrogen_Detail.asset" : "_WorldOrogen_Undetailed.asset"));
            var data = ScriptableObject.CreateInstance<PlanetSurfaceDataAsset>(); bool created = false, bound = false;
            try
            {
                data.name = generator.name + " World Orogen" + (detail != null ? " detail" : " base");
                data.SetSnapshot(candidate);
                if (data.EncodedPayloadBytes >= WorldOrogenPublication.MaximumAssetBytes - 1024*1024)
                    throw new InvalidOperationException("Detail dataset exceeds saved-file admission; lower conditioning resolution.");
                data.SetBaseColour(source.BaseColour); data.SetWorldOrogenSource(sourceSettings);
                AssetDatabase.CreateAsset(data, path); created = true; EditorUtility.SetDirty(data); Save(data, path);
                Resources.UnloadAsset(data); data = AssetDatabase.LoadAssetAtPath<PlanetSurfaceDataAsset>(path);
                if (!data || !data.TryCreateSnapshot(out var verified, out var error) || verified.ContentDigest != candidate.ContentDigest ||
                    data.BaseColour != source.BaseColour) throw new InvalidDataException("Detail asset failed its disk round-trip: " + (data ? "payload / colour mismatch" : "missing asset"));
                CheckCurrent(generator, source, captured, sourceSettings, retained.Recipe.Radius);
                Undo.RecordObject(generator, "Publish World Orogen terrain detail"); generator.SurfaceData = data; bound = true;
                EditorUtility.SetDirty(generator); Save(generator, target); SceneView.RepaintAll(); return data;
            }
            catch
            {
                if (bound)
                {
                    generator.SurfaceData = source; EditorUtility.SetDirty(generator);
                    // If restoration cannot be saved, retain the complete candidate instead of deleting its target.
                    try { Save(generator, target); } catch { throw; }
                }
                if (created) AssetDatabase.DeleteAsset(path); else if (data) UnityEngine.Object.DestroyImmediate(data);
                throw;
            }
        }
        internal static void CheckCurrent(PlanetGeneratorAsset generator, PlanetSurfaceDataAsset source,
            WorldOrogenDetailRecipe captured, WorldOrogenSettings settings, double radius)
        {
            if (!generator || !source || generator.SurfaceData != source || generator.Radius != radius ||
                generator.TerrainDetail == null || generator.TerrainDetail.Capture(generator.Seed).ContentDigest != captured.ContentDigest ||
                generator.CaptureOrogen().ConfigurationDigest() != settings.ConfigurationDigest())
                throw new InvalidOperationException("Planet parameters changed during detail capture; saved map retained.");
        }
        static void Save(UnityEngine.Object asset, string path)
        {
            AssetDatabase.SaveAssetIfDirty(asset);
            string absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), path));
            if (EditorUtility.IsDirty(asset) || !File.Exists(absolute) || new FileInfo(absolute).Length <= 0 ||
                new FileInfo(absolute).Length >= WorldOrogenPublication.MaximumAssetBytes)
                throw new IOException("Could not save terrain detail asset: " + path);
        }
    }
}
