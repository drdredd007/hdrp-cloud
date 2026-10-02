using System;
using System.IO;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Time-averaged physical transport, expressed in canonical planet axes; no observer basis.</summary>
    public readonly struct SurfaceHydrologySample
    {
        /// <summary>Water and solid-volume flux per metre of boundary width [m²/s].</summary>
        public readonly double3 WaterDischarge, SedimentDischarge;
        public readonly double MeanWaterDepth, RainRate;
        public SurfaceHydrologySample(double3 waterDischarge, double3 sedimentDischarge, double meanWaterDepth, double rainRate)
        {
            WaterDischarge = math.select(waterDischarge, 0, waterDischarge == 0);
            SedimentDischarge = math.select(sedimentDischarge, 0, sedimentDischarge == 0);
            MeanWaterDepth = meanWaterDepth == 0 ? 0 : meanWaterDepth; RainRate = rainRate == 0 ? 0 : rainRate;
        }
        public bool IsValid => math.all(math.isfinite(WaterDischarge)) && math.all(math.isfinite(SedimentDischarge)) &&
            math.isfinite(MeanWaterDepth) && MeanWaterDepth >= 0 && math.isfinite(RainRate) && RainRate >= 0;
    }

    /// <summary>Complete immutable global boundary context. Its base digest prevents applying one planet's runoff to another bake.</summary>
    public sealed class SurfaceHydrologyField
    {
        readonly SurfaceHydrologySample[] samples;
        public SurfaceContentHash SourceBaseDigest { get; }
        public SurfaceContentHash ContentDigest { get; }
        public double Radius { get; }
        public int Resolution { get; }
        public int SampleCount => samples.Length;
        public SurfaceHydrologyField(SurfaceContentHash sourceBaseDigest, double radius, int resolution, SurfaceHydrologySample[] samples)
        {
            if (!sourceBaseDigest.IsValid || !math.isfinite(radius) || radius <= 0 || !CubeSurface.ValidResolution(resolution) || resolution > 512 ||
                samples == null || samples.Length != 6 * (resolution + 1) * (resolution + 1)) throw new ArgumentException("Invalid complete hydrology grid or provenance.");
            SourceBaseDigest = sourceBaseDigest; Radius = radius; Resolution = resolution; this.samples = (SurfaceHydrologySample[])samples.Clone();
            foreach (var sample in this.samples)
                if (!sample.IsValid || math.cmax(math.abs(sample.WaterDischarge)) > 1e60 || math.cmax(math.abs(sample.SedimentDischarge)) > 1e60 || sample.MeanWaterDepth > 1e60 || sample.RainRate > 1e60)
                    throw new ArgumentException("Hydrology values must be finite and bounded; depth and rain nonnegative.");
            ContentDigest = SurfaceHashing.Compute(writer =>
            {
                writer.Write(1); SurfaceHashing.WriteHash(writer, SourceBaseDigest); writer.Write(Radius); writer.Write(Resolution);
                foreach (var sample in this.samples) WriteSample(writer, sample);
            });
        }
        public SurfaceHydrologySample SampleAt(int index) => samples[index];
        public SurfaceHydrologySample[] CopySamples() => (SurfaceHydrologySample[])samples.Clone();
        public SurfaceSampleStatus TrySample(double3 direction, out SurfaceHydrologySample sample)
        {
            sample = default;
            if (!CubeSurface.TryLocate(direction, 0, out var key, out var uv) || !CubeSurface.TryNormalize(direction, out var unit)) return SurfaceSampleStatus.InvalidInput;
            double2 p = uv * Resolution; int x = (int)math.min(Resolution - 1, math.floor(p.x)), y = (int)math.min(Resolution - 1, math.floor(p.y));
            double2 f = p - new double2(x, y); int row = Resolution + 1, i = key.Face * row * row + y * row + x;
            var value = Lerp(Lerp(samples[i], samples[i + 1], f.x), Lerp(samples[i + row], samples[i + row + 1], f.x), f.y);
            sample = new SurfaceHydrologySample(value.WaterDischarge - unit * math.dot(unit, value.WaterDischarge),
                value.SedimentDischarge - unit * math.dot(unit, value.SedimentDischarge), value.MeanWaterDepth, value.RainRate);
            return SurfaceSampleStatus.Ready;
        }
        static SurfaceHydrologySample Lerp(SurfaceHydrologySample a, SurfaceHydrologySample b, double t) => new SurfaceHydrologySample(
            math.lerp(a.WaterDischarge, b.WaterDischarge, t), math.lerp(a.SedimentDischarge, b.SedimentDischarge, t),
            math.lerp(a.MeanWaterDepth, b.MeanWaterDepth, t), math.lerp(a.RainRate, b.RainRate, t));
        internal static void WriteSample(BinaryWriter writer, SurfaceHydrologySample value)
        { SurfaceHashing.WriteVector(writer, value.WaterDischarge); SurfaceHashing.WriteVector(writer, value.SedimentDischarge); writer.Write(value.MeanWaterDepth); writer.Write(value.RainRate); }
    }

    public static class SurfaceHydrologyCodec
    {
        const uint Magic = 0x31485953;
        public const int FormatVersion = 1;
        public static void Write(BinaryWriter writer, SurfaceHydrologyField field)
        {
            if (writer == null || field == null) throw new ArgumentNullException();
            writer.Write(Magic); writer.Write(FormatVersion); SurfaceHashing.WriteHash(writer, field.SourceBaseDigest);
            SurfaceHashing.WriteHash(writer, field.ContentDigest); writer.Write(field.Radius); writer.Write(field.Resolution); writer.Write(field.SampleCount);
            for (int i = 0; i < field.SampleCount; i++) SurfaceHydrologyField.WriteSample(writer, field.SampleAt(i));
        }
        public static SurfaceHydrologyField Read(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            try
            {
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion) throw new InvalidDataException("Unsupported hydrology format.");
                var source = Hash(reader); var digest = Hash(reader); double radius = reader.ReadDouble(); int resolution = reader.ReadInt32(), count = reader.ReadInt32();
                if (!source.IsValid || !digest.IsValid || !math.isfinite(radius) || radius <= 0 || !CubeSurface.ValidResolution(resolution) || resolution > 512 || count != 6 * (resolution + 1) * (resolution + 1))
                    throw new InvalidDataException("Invalid hydrology grid or source identity.");
                if (reader.BaseStream.CanSeek && reader.BaseStream.Length - reader.BaseStream.Position < (long)count * 64) throw new InvalidDataException("Truncated hydrology samples.");
                var samples = new SurfaceHydrologySample[count];
                for (int i = 0; i < count; i++) samples[i] = new SurfaceHydrologySample(Vector(reader), Vector(reader), reader.ReadDouble(), reader.ReadDouble());
                var field = new SurfaceHydrologyField(source, radius, resolution, samples);
                if (field.ContentDigest != digest) throw new InvalidDataException("Hydrology content digest differs from payload.");
                return field;
            }
            catch (ArgumentException exception) { throw new InvalidDataException("Invalid hydrology values.", exception); }
            catch (EndOfStreamException exception) { throw new InvalidDataException("Truncated hydrology payload.", exception); }
        }
        static SurfaceContentHash Hash(BinaryReader reader) => new SurfaceContentHash(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        static double3 Vector(BinaryReader reader) => new double3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }
}
