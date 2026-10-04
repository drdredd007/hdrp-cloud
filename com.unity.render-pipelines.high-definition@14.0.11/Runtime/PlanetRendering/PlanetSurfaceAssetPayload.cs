using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Storage-only compression of the unchanged portable surface codec. Content identity
    /// still belongs to SurfaceSnapshot; this envelope is never used by archives or GPU bindings.</summary>
    public static class PlanetSurfaceAssetPayload
    {
        public const int MaximumDecodedBytes = 512 * 1024 * 1024;
        public const int MaximumStoredBytes = 512 * 1024 * 1024;
        public const uint Magic = 0x315A5350; // PSZ1
        public const int FormatVersion = 1;
        public const int HeaderBytes = 92;
        const uint RawMagic = 0x31534653;
        const int GZipKind = 1, ChunkBytes = 64 * 1024;

        public static byte[] Encode(SurfaceSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!snapshot.MaterialsReady) throw new InvalidOperationException("Prepare complete automatic material rules before storing a surface.");
            using (var raw = new LimitedMemoryStream(MaximumDecodedBytes))
            {
                using (var writer = new BinaryWriter(raw, Encoding.UTF8, true)) SurfaceSnapshotCodec.Write(writer, snapshot);
                int rawCount = checked((int)raw.Length);
                byte[] rawBuffer = raw.GetBuffer();
                byte[] rawHash = Hash(rawBuffer, 0, rawCount);
                using (var stored = new LimitedMemoryStream((long)HeaderBytes + MaximumStoredBytes))
                {
                    stored.SetLength(HeaderBytes); stored.Position = HeaderBytes;
                    using (var gzip = new GZipStream(stored, System.IO.Compression.CompressionLevel.Optimal, true))
                        for (int offset = 0; offset < rawCount; offset += ChunkBytes)
                            gzip.Write(rawBuffer, offset, Math.Min(ChunkBytes, rawCount - offset));
                    int compressedCount = checked((int)stored.Length - HeaderBytes);
                    byte[] compressedHash = Hash(stored.GetBuffer(), HeaderBytes, compressedCount);
                    stored.Position = 0;
                    using (var writer = new BinaryWriter(stored, Encoding.UTF8, true))
                    {
                        writer.Write(Magic); writer.Write(FormatVersion); writer.Write(GZipKind);
                        writer.Write((long)rawCount); writer.Write((long)compressedCount);
                        writer.Write(rawHash); writer.Write(compressedHash);
                    }
                    return stored.ToArray(); // The single owned byte[] required by Unity serialization.
                }
            }
        }

        public static SurfaceSnapshot Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 4) throw new InvalidDataException("Planet surface payload is truncated.");
            uint magic = ReadUInt32(payload, 0);
            if (magic == RawMagic)
            {
                if (payload.Length > MaximumDecodedBytes) throw new InvalidDataException("Raw surface payload exceeds the explicit 512MiB storage limit.");
                using (var raw = new MemoryStream(payload, false)) return ReadSnapshot(raw);
            }
            if (magic != Magic || payload.Length < HeaderBytes) throw new InvalidDataException("Unsupported or truncated planet surface storage envelope.");
            long rawLength, storedLength; byte[] rawHash, storedHash;
            using (var header = new BinaryReader(new MemoryStream(payload, 0, HeaderBytes, false)))
            {
                header.ReadUInt32();
                if (header.ReadInt32() != FormatVersion || header.ReadInt32() != GZipKind)
                    throw new InvalidDataException("Unsupported planet surface storage envelope version or compression.");
                rawLength = header.ReadInt64(); storedLength = header.ReadInt64();
                rawHash = header.ReadBytes(32); storedHash = header.ReadBytes(32);
            }
            if (rawLength < 8 || rawLength > MaximumDecodedBytes || storedLength < 18 || storedLength > MaximumStoredBytes)
                throw new InvalidDataException("Planet surface storage lengths exceed the explicit 512MiB limit or minimum header size.");
            if (storedLength != (long)payload.Length - HeaderBytes)
                throw new InvalidDataException("Truncated or trailing data after the planet surface storage envelope.");
            int storedCount = checked((int)storedLength), rawCount = checked((int)rawLength);
            if (!EqualHash(storedHash, Hash(payload, HeaderBytes, storedCount)))
                throw new InvalidDataException("Compressed planet surface checksum differs from its payload.");
            // This envelope writes a single gzip member without optional headers. Validate framing
            // before allocating the declared raw buffer; the inflater also checks its CRC/footer.
            if (payload[HeaderBytes] != 0x1f || payload[HeaderBytes + 1] != 0x8b || payload[HeaderBytes + 2] != 8 || payload[HeaderBytes + 3] != 0 ||
                ReadUInt32(payload, payload.Length - 4) != (uint)rawCount)
                throw new InvalidDataException("Invalid single-member gzip framing or declared surface size.");
            try
            {
                using (var compressed = new SingleMemberInput(payload, HeaderBytes, storedCount))
                using (var raw = new LimitedMemoryStream(rawCount, rawCount))
                {
                    var chunk = new byte[ChunkBytes];
                    using (var gzip = new GZipStream(compressed, CompressionMode.Decompress, true))
                    {
                        while (raw.Length < rawLength)
                        {
                            int count = gzip.Read(chunk, 0, (int)Math.Min(ChunkBytes, rawLength - raw.Length));
                            if (count == 0) throw new InvalidDataException("Compressed surface ends before its declared raw length.");
                            raw.Write(chunk, 0, count);
                        }
                        if (gzip.Read(chunk, 0, 1) != 0) throw new InvalidDataException("Compressed surface exceeds its declared raw length.");
                    }
                    if (compressed.Position != storedLength) throw new InvalidDataException("Unexpected data after the single compressed surface member.");
                    if (!EqualHash(rawHash, Hash(raw.GetBuffer(), 0, rawCount)))
                        throw new InvalidDataException("Decoded planet surface checksum differs from its payload.");
                    raw.Position = 0; return ReadSnapshot(raw);
                }
            }
            catch (IOException exception)
            { throw new InvalidDataException("Invalid or truncated compressed planet surface.", exception); }
        }

        static SurfaceSnapshot ReadSnapshot(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var snapshot = SurfaceSnapshotCodec.Read(reader);
                if (!snapshot.MaterialsReady) throw new InvalidDataException("Published surface contains an incomplete automatic material cache.");
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected data after the surface snapshot.");
                return snapshot;
            }
        }

        static byte[] Hash(byte[] bytes, int offset, int count)
        { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes, offset, count); }
        static bool EqualHash(byte[] a, byte[] b)
        { int different = a.Length ^ b.Length; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) different |= a[i] ^ b[i]; return different == 0; }
        static uint ReadUInt32(byte[] bytes, int offset) => (uint)bytes[offset] | (uint)bytes[offset + 1] << 8 | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24;

        sealed class LimitedMemoryStream : MemoryStream
        {
            readonly long limit;
            public LimitedMemoryStream(long limit, int initialCapacity = 0) : base(initialCapacity) { this.limit = limit; }
            void Require(long end)
            {
                if (end < 0 || end > limit) throw new InvalidDataException("Planet surface storage exceeds its explicit bounded buffer size.");
                // MemoryStream's default doubling can allocate beyond the declared cap. Bound its
                // capacity as well as its length, before the underlying write allocates anything.
                if (end > Capacity) Capacity = checked((int)Math.Min(limit, Math.Max(end, Math.Max(256L, (long)Capacity * 2))));
            }
            public override void Write(byte[] buffer, int offset, int count) { Require(Position + (long)count); base.Write(buffer, offset, count); }
            public override void Write(ReadOnlySpan<byte> buffer) { Require(Position + (long)buffer.Length); base.Write(buffer); }
            public override void WriteByte(byte value) { Require(Position + 1); base.WriteByte(value); }
            public override void SetLength(long value) { Require(value); base.SetLength(value); }
        }

        // A normal GZipStream may read past its member into a buffer and hide unused trailing bytes.
        // One-byte input preserves the exact consumed-member boundary across Unity's Mono backend.
        // This is offline storage decoding, not frame-by-frame surface sampling.
        sealed class SingleMemberInput : Stream
        {
            readonly byte[] bytes; readonly int start, count; int position;
            public SingleMemberInput(byte[] bytes, int start, int count) { this.bytes = bytes; this.start = start; this.count = count; }
            public override int Read(byte[] buffer, int offset, int requested)
            { if (requested == 0 || position == count) return 0; buffer[offset] = bytes[start + position++]; return 1; }
            public override int ReadByte() => position == count ? -1 : bytes[start + position++];
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => count;
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
