using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.HiFi;

public enum HiFiRawSampleFormat
{
    U8,
    U10Le,
    U12Le,
    U16Le,
    S8,
    S10Le,
    S12Le,
    S16Le,
    F32Le
}

public static class HiFiSampleNormalizer
{
    public static HiFiRawSampleFormat ParseFormat(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant() switch
        {
            "u8" => HiFiRawSampleFormat.U8,
            "u10le" => HiFiRawSampleFormat.U10Le,
            "u12le" => HiFiRawSampleFormat.U12Le,
            "u16le" => HiFiRawSampleFormat.U16Le,
            "s8" => HiFiRawSampleFormat.S8,
            "s10le" => HiFiRawSampleFormat.S10Le,
            "s12le" => HiFiRawSampleFormat.S12Le,
            "s16le" or "raw" => HiFiRawSampleFormat.S16Le,
            "f32le" => HiFiRawSampleFormat.F32Le,
            _ => throw new ArgumentException($"Unsupported format: {value}", nameof(value))
        };
    }

    public static int BytesPerSample(HiFiRawSampleFormat format)
        => format is HiFiRawSampleFormat.U8 or HiFiRawSampleFormat.S8 ? 1
            : format == HiFiRawSampleFormat.F32Le ? 4
            : 2;

    public static float[] Normalize(ReadOnlySpan<byte> input, HiFiRawSampleFormat format)
    {
        var output = new float[input.Length / BytesPerSample(format)];
        Normalize(input, output, format);
        return output;
    }

    public static int Normalize(
        ReadOnlySpan<byte> input,
        Span<float> output,
        HiFiRawSampleFormat format)
    {
        int count = input.Length / BytesPerSample(format);
        if (output.Length < count)
        {
            throw new ArgumentException("Output span is shorter than the complete input sample count.", nameof(output));
        }

        float signedScale = format switch
        {
            HiFiRawSampleFormat.S10Le => 1.0f / 512.0f,
            HiFiRawSampleFormat.S12Le => 1.0f / 2048.0f,
            HiFiRawSampleFormat.S16Le => 1.0f / 32768.0f,
            _ => 0.0f
        };
        if (signedScale != 0.0f)
        {
            NormalizeSigned16BitStorage(input, output, count, signedScale);
            return count;
        }

        for (int i = 0; i < count; i++)
        {
            output[i] = format switch
            {
                HiFiRawSampleFormat.U8 => NormalizeUnsigned(input[i], byte.MaxValue),
                HiFiRawSampleFormat.S8 => unchecked((sbyte)input[i]) * (1.0f / 128.0f),
                HiFiRawSampleFormat.U10Le => NormalizeUnsigned(ReadUInt16(input, i), 1023),
                HiFiRawSampleFormat.U12Le => NormalizeUnsigned(ReadUInt16(input, i), 4095),
                HiFiRawSampleFormat.U16Le => NormalizeUnsigned(ReadUInt16(input, i), ushort.MaxValue),
                HiFiRawSampleFormat.F32Le => BitConverter.Int32BitsToSingle(ReadInt32(input, i)),
                _ => throw new InvalidOperationException($"Unsupported HiFi sample format {format}.")
            };
        }

        return count;
    }

    private static void NormalizeSigned16BitStorage(
        ReadOnlySpan<byte> input, Span<float> output, int count, float scale)
    {
        int i = 0;
        // Overlapping buffers need the original one-sample-at-a-time read/write order.
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian
            && count <= int.MaxValue / sizeof(float)
            && !input[..(count * sizeof(short))].Overlaps(MemoryMarshal.AsBytes(output[..count])))
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(input);
            var scales = new Vector<float>(scale);
            int width = Vector<short>.Count;
            int halfWidth = Vector<int>.Count;
            for (; i <= count - width; i += width)
            {
                Vector.Widen(new Vector<short>(samples[i..]), out Vector<int> lower, out Vector<int> upper);
                (Vector.ConvertToSingle(lower) * scales).CopyTo(output[i..]);
                (Vector.ConvertToSingle(upper) * scales).CopyTo(output[(i + halfWidth)..]);
            }
        }

        for (; i < count; i++)
        {
            output[i] = ReadInt16(input, i) * scale;
        }
    }

    private static float NormalizeUnsigned(int value, int maximum)
        => (float)Math.FusedMultiplyAdd(value, 2.0 / maximum, -1.0);

    private static short ReadInt16(ReadOnlySpan<byte> input, int index)
        => BinaryPrimitives.ReadInt16LittleEndian(input.Slice(index * sizeof(short), sizeof(short)));

    private static ushort ReadUInt16(ReadOnlySpan<byte> input, int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(index * sizeof(ushort), sizeof(ushort)));

    private static int ReadInt32(ReadOnlySpan<byte> input, int index)
        => BinaryPrimitives.ReadInt32LittleEndian(input.Slice(index * sizeof(int), sizeof(int)));
}
