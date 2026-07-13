using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

public sealed class UInt8SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        byte[] buffer = ReadExactOrNull(stream, sample, readLength, 1);
        if (buffer.Length != readLength)
        {
            return null;
        }

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = buffer[i];
        }

        return output;
    }

    internal static byte[] ReadExactOrNull(Stream stream, long sample, int readLength, int sampleBytes)
    {
        if (sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Sample offset must be non-negative.");
        }

        if (readLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readLength), "Read length must be non-negative.");
        }

        long byteOffset = checked(sample * sampleBytes);
        int byteCount = checked(readLength * sampleBytes);
        stream.Seek(byteOffset, SeekOrigin.Begin);
        byte[] buffer = new byte[byteCount];
        int read = stream.ReadAtLeast(buffer, byteCount, throwOnEndOfStream: false);
        return read == byteCount ? buffer : [];
    }
}

public sealed class Int8SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        byte[] buffer = UInt8SampleLoader.ReadExactOrNull(stream, sample, readLength, 1);
        if (buffer.Length != readLength)
        {
            return null;
        }

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = unchecked((sbyte)buffer[i]);
        }

        return output;
    }
}

public sealed class Int16SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        byte[] buffer = UInt8SampleLoader.ReadExactOrNull(stream, sample, readLength, 2);
        if (buffer.Length != readLength * 2)
        {
            return null;
        }

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
        }

        return output;
    }
}

public sealed class UInt16SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        byte[] buffer = UInt8SampleLoader.ReadExactOrNull(stream, sample, readLength, 2);
        if (buffer.Length != readLength * 2)
        {
            return null;
        }

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i * 2, 2));
        }

        return output;
    }
}

public sealed class Float32SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        byte[] buffer = UInt8SampleLoader.ReadExactOrNull(stream, sample, readLength, 4);
        if (buffer.Length != readLength * 4)
        {
            return null;
        }

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i * 4, 4));
            output[i] = BitConverter.Int32BitsToSingle(bits) * 32768.0;
        }

        return output;
    }
}
