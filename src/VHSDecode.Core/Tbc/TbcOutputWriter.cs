using System.Buffers.Binary;

namespace VHSDecode.Core.Tbc;

public enum TbcOutputSampleFormat
{
    UInt16,
    Float32
}

public sealed record TbcOutputPayload(byte[] Bytes, TbcOutputSampleFormat SampleFormat)
{
    public int BytesPerSample => SampleFormat switch
    {
        TbcOutputSampleFormat.UInt16 => sizeof(ushort),
        TbcOutputSampleFormat.Float32 => sizeof(float),
        _ => throw new ArgumentOutOfRangeException(nameof(SampleFormat), SampleFormat, "Unsupported TBC output sample format.")
    };

    public int SampleCount => Bytes.Length / BytesPerSample;
}

public static class TbcOutputWriter
{
    public static byte[] ToLittleEndianBytes(ReadOnlySpan<ushort> samples)
    {
        var output = new byte[checked(samples.Length * sizeof(ushort))];
        WriteSamples(output, samples);
        return output;
    }

    public static byte[] ToLittleEndianFloat32Bytes(ReadOnlySpan<double> samples)
    {
        var output = new byte[checked(samples.Length * sizeof(float))];
        WriteFloat32Samples(output, samples);
        return output;
    }

    public static void WriteFrame(
        Stream destination,
        ReadOnlySpan<ushort> samples,
        TbcFrameSpec frameSpec,
        TbcOutputPayload? payload = null)
    {
        if (samples.Length != frameSpec.FieldSampleCount)
        {
            throw new ArgumentException("TBC field sample count did not match the configured frame shape.", nameof(samples));
        }

        if (payload is not null)
        {
            if (payload.Bytes.Length % payload.BytesPerSample != 0 || payload.SampleCount != frameSpec.FieldSampleCount)
            {
                throw new ArgumentException("TBC payload sample count did not match the configured frame shape.", nameof(payload));
            }

            destination.Write(payload.Bytes, 0, payload.Bytes.Length);
            return;
        }

        WriteSamples(destination, samples);
    }

    public static void WriteSamples(Stream destination, ReadOnlySpan<ushort> samples)
    {
        byte[] bytes = ToLittleEndianBytes(samples);
        destination.Write(bytes, 0, bytes.Length);
    }

    private static void WriteSamples(Span<byte> destination, ReadOnlySpan<ushort> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * sizeof(ushort), sizeof(ushort)), samples[i]);
        }
    }

    private static void WriteFloat32Samples(Span<byte> destination, ReadOnlySpan<double> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int bits = BitConverter.SingleToInt32Bits((float)samples[i]);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * sizeof(float), sizeof(float)), bits);
        }
    }
}
