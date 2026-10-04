using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    public enum SurfaceStyle { EarthLike = 1, Rocky = 2 }
    public enum SurfaceSampleStatus { Ready, NotReady, InvalidInput, IncompatibleData }
    public enum SurfaceRegionMode { Replace, Delta }
    public enum SurfaceDetailPolicy { Preserve, Suppress }
    public enum SurfaceRegionKind { Authored = 0, GeneratedRefinement = 1 }
    [Flags] public enum SurfaceChannels { None = 0, MaterialWeights = 1, ErosionData = 2 }
    public readonly struct SurfaceAttributes
    {
        public readonly SurfaceChannels Channels;
        /// <summary>Normalised grass, sand, rock, snow fractions.</summary>
        public readonly float4 MaterialWeights;
        /// <summary>Normalised flow, wetness, cumulative wear and deposition.</summary>
        public readonly float4 ErosionData;
        public SurfaceAttributes(SurfaceChannels channels, float4 weights, float4 erosion)
        { Channels = channels; MaterialWeights = weights; ErosionData = erosion; }
    }

    /// <summary>Intrinsic planet-local recipe. Placement and observer state never enter terrain identity.</summary>
    public readonly struct SurfaceRecipe
    {
        public const int CurrentSchemaVersion = 1;
        public const int CurrentAlgorithmVersion = 1;
        // Opt-in authoring algorithm; retained version-one snapshots are never regenerated implicitly.
        public const int StructuralAlgorithmVersion = 2;
        public const int StructuralAuthorityAlgorithmVersion = 3;
        public const int DrainageAuthorityAlgorithmVersion = 4;
        public const int LandformAuthorityAlgorithmVersion = 5;
        public static bool HasStructuralAuthority(int algorithm) => algorithm == StructuralAuthorityAlgorithmVersion || algorithm == DrainageAuthorityAlgorithmVersion || algorithm == LandformAuthorityAlgorithmVersion;
        public readonly int SchemaVersion, AlgorithmVersion, Seed;
        public readonly SurfaceStyle Style;
        public readonly double Radius, SeaLevel, MinimumHeight, MaximumHeight;

        public SurfaceRecipe(int seed, SurfaceStyle style, double radius, double minHeight, double maxHeight,
            double seaLevel = 0, int algorithmVersion = CurrentAlgorithmVersion, int schemaVersion = CurrentSchemaVersion)
        {
            Seed = seed; Style = style; Radius = radius; SeaLevel = seaLevel;
            MinimumHeight = minHeight; MaximumHeight = maxHeight;
            AlgorithmVersion = algorithmVersion; SchemaVersion = schemaVersion;
        }
        public bool IsValid => SchemaVersion == CurrentSchemaVersion && AlgorithmVersion > 0 &&
            (Style == SurfaceStyle.EarthLike || Style == SurfaceStyle.Rocky) && math.isfinite(Radius) && Radius > 0 &&
            math.isfinite(SeaLevel) && SeaLevel > -Radius && math.isfinite(MinimumHeight) &&
            math.isfinite(MaximumHeight) && MinimumHeight > -Radius && MinimumHeight <= MaximumHeight && math.isfinite(Radius + MaximumHeight);
    }

    /// <summary>SHA-256 content identity; also blittable for jobs and GPU binding metadata.</summary>
    public readonly struct SurfaceContentHash : IEquatable<SurfaceContentHash>, IComparable<SurfaceContentHash>
    {
        public readonly ulong A, B, C, D;
        public SurfaceContentHash(ulong a, ulong b, ulong c, ulong d) { A = a; B = b; C = c; D = d; }
        public bool IsValid => (A | B | C | D) != 0;
        public bool Equals(SurfaceContentHash other) => A == other.A && B == other.B && C == other.C && D == other.D;
        public override bool Equals(object other) => other is SurfaceContentHash value && Equals(value);
        public override int GetHashCode() { unchecked { return ((int)A * 397 ^ (int)B) * 397 ^ (int)C ^ (int)D; } }
        public int CompareTo(SurfaceContentHash other)
        {
            int n = A.CompareTo(other.A); if (n != 0) return n;
            n = B.CompareTo(other.B); if (n != 0) return n;
            n = C.CompareTo(other.C); return n != 0 ? n : D.CompareTo(other.D);
        }
        public static bool operator ==(SurfaceContentHash a, SurfaceContentHash b) => a.Equals(b);
        public static bool operator !=(SurfaceContentHash a, SurfaceContentHash b) => !a.Equals(b);
        public static SurfaceContentHash Compute(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (var sha = SHA256.Create()) return FromBytes(sha.ComputeHash(bytes));
        }
        public static SurfaceContentHash FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 32) throw new ArgumentException("A SHA-256 digest has 32 bytes.", nameof(bytes));
            return new SurfaceContentHash(Read(bytes, 0), Read(bytes, 8), Read(bytes, 16), Read(bytes, 24));
        }
        static ulong Read(byte[] bytes, int offset)
        {
            ulong value = 0; for (int i = 0; i < 8; i++) value |= (ulong)bytes[offset + i] << (8 * i); return value;
        }
        public byte[] ToBytes()
        {
            var bytes = new byte[32]; Write(bytes, 0, A); Write(bytes, 8, B); Write(bytes, 16, C); Write(bytes, 24, D); return bytes;
        }
        static void Write(byte[] bytes, int offset, ulong value)
        { for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        public override string ToString()
        {
            var result = new StringBuilder(64);
            foreach (byte value in ToBytes()) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
        public static bool TryParse(string text, out SurfaceContentHash hash)
        {
            hash = default; if (text == null || text.Length != 64) return false;
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                if (!byte.TryParse(text.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i])) return false;
            hash = FromBytes(bytes); return hash.IsValid;
        }
    }

    /// <summary>Logical revision of a surface. BaseDigest names the complete bake manifest, not resident tiles.</summary>
    public readonly struct SurfaceRevision
    {
        public readonly SurfaceContentHash RecipeDigest, BaseDigest;
        public readonly ulong Epoch;
        public SurfaceRevision(SurfaceContentHash recipeDigest, SurfaceContentHash baseDigest, ulong epoch)
        { RecipeDigest = recipeDigest; BaseDigest = baseDigest; Epoch = epoch; }
        public bool IsValid => RecipeDigest.IsValid && BaseDigest.IsValid && Epoch > 0;
    }

    /// <summary>Vertex-grid tile on the cube sphere. Face axes exactly match the existing PlanetField convention.</summary>
    public readonly struct SurfaceTileKey : IEquatable<SurfaceTileKey>, IComparable<SurfaceTileKey>
    {
        public const int MaximumLevel = 30;
        public readonly int Face, Level, X, Y;
        public SurfaceTileKey(int face, int level, int x, int y) { Face = face; Level = level; X = x; Y = y; }
        public bool IsValid => Face >= 0 && Face < 6 && Level >= 0 && Level <= MaximumLevel &&
            X >= 0 && Y >= 0 && X < (1L << Level) && Y < (1L << Level);
        public bool Equals(SurfaceTileKey other) => Face == other.Face && Level == other.Level && X == other.X && Y == other.Y;
        public override bool Equals(object other) => other is SurfaceTileKey key && Equals(key);
        public override int GetHashCode() { unchecked { return ((Face * 397 + Level) * 397 + X) * 397 + Y; } }
        public int CompareTo(SurfaceTileKey other)
        {
            int n = Face.CompareTo(other.Face); if (n != 0) return n;
            n = Level.CompareTo(other.Level); if (n != 0) return n;
            n = Y.CompareTo(other.Y); return n != 0 ? n : X.CompareTo(other.X);
        }
        public override string ToString() => $"{Face}/{Level}/{X}/{Y}";
    }

    public readonly struct SurfaceTileHeader
    {
        public readonly SurfaceTileKey Key;
        public readonly int Resolution, HeightOffset, AttributeOffset;
        public readonly SurfaceChannels Channels;
        public readonly double MeasuredLodErrorMetres;
        public readonly double MinimumHeight, MaximumHeight;
        public readonly SurfaceContentHash ContentHash;
        public SurfaceTileHeader(SurfaceTileKey key, int resolution, int heightOffset, double min, double max, SurfaceContentHash hash,
            SurfaceChannels channels = SurfaceChannels.None, int attributeOffset = 0, double measuredLodErrorMetres = 0)
        { Key = key; Resolution = resolution; HeightOffset = heightOffset; MinimumHeight = min; MaximumHeight = max; ContentHash = hash;
            Channels = channels; AttributeOffset = attributeOffset; MeasuredLodErrorMetres = measuredLodErrorMetres; }
    }
}
