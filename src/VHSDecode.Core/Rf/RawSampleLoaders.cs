using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

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

public sealed class Int16SampleLoader : IReusableRfSampleLoader
{
    internal const int MaximumRetainedDecodedBufferLength = 32 * 1024;
    internal const int MaximumRetainedDecodedBufferCount = 48;
    private readonly object _decodedBufferLock = new();
    private readonly double[]?[] _decodedBuffers =
        new double[]?[MaximumRetainedDecodedBufferCount];
    private int _decodedBufferCount;

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

    bool IReusableRfSampleLoader.ReuseForSequentialDecode => true;

    internal double[]? ReadReusable(Stream stream, long sample, int readLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        ArgumentOutOfRangeException.ThrowIfNegative(readLength);

        long byteOffset = checked(sample * sizeof(short));
        int byteCount = checked(readLength * sizeof(short));
        stream.Seek(byteOffset, SeekOrigin.Begin);
        if (readLength == 0)
        {
            return [];
        }

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
        double[]? output = null;
        bool completed = false;
        try
        {
            int bytesRead = stream.ReadAtLeast(
                readBuffer.AsSpan(0, byteCount),
                byteCount,
                throwOnEndOfStream: false);
            if (bytesRead != byteCount)
            {
                return null;
            }

            output = TakeDecodedBuffer(readLength);
            if (BitConverter.IsLittleEndian)
            {
                LibsndfilePcm16SampleLoader.ConvertPcm16ToDouble(
                    MemoryMarshal.Cast<byte, short>(readBuffer.AsSpan(0, byteCount)),
                    output);
            }
            else
            {
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] = BinaryPrimitives.ReadInt16LittleEndian(
                        readBuffer.AsSpan(i * sizeof(short), sizeof(short)));
                }
            }

            completed = true;
            return output;
        }
        finally
        {
            if (output is not null && !completed)
            {
                ReturnReusable(output);
            }

            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    double[]? IReusableRfSampleLoader.ReadReusable(
        Stream stream,
        long sample,
        int readLength)
        => ReadReusable(stream, sample, readLength);

    internal int CachedReusableDecodedBufferCount
    {
        get
        {
            lock (_decodedBufferLock)
            {
                return _decodedBufferCount;
            }
        }
    }

    internal void ReturnReusable(double[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_decodedBufferLock)
        {
            if (buffer.Length <= MaximumRetainedDecodedBufferLength
                && _decodedBufferCount < _decodedBuffers.Length)
            {
                _decodedBuffers[_decodedBufferCount++] = buffer;
            }
        }
    }

    void IReusableRfSampleLoader.ReturnReusable(double[] buffer)
        => ReturnReusable(buffer);

    private double[] TakeDecodedBuffer(int length)
    {
        lock (_decodedBufferLock)
        {
            for (int i = _decodedBufferCount - 1; i >= 0; i--)
            {
                double[] candidate = _decodedBuffers[i]!;
                if (candidate.Length == length)
                {
                    int last = --_decodedBufferCount;
                    _decodedBuffers[i] = _decodedBuffers[last];
                    _decodedBuffers[last] = null;
                    return candidate;
                }
            }
        }

        return GC.AllocateUninitializedArray<double>(length);
    }
}

internal sealed class DirectInt16SampleLoader : IInt16RfSampleLoader
{
    private readonly Int16SampleLoader _fallback = new();

    public double[]? Read(Stream stream, long sample, int readLength)
        => _fallback.Read(stream, sample, readLength);

    public bool TryReadInt16(
        Stream stream,
        long sample,
        Span<short> destination,
        out int samplesRead)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        samplesRead = 0;
        if (destination.IsEmpty)
        {
            return true;
        }
        if (!BitConverter.IsLittleEndian)
        {
            return false;
        }

        long byteOffset = checked(sample * sizeof(short));
        stream.Seek(byteOffset, SeekOrigin.Begin);
        Span<byte> bytes = MemoryMarshal.AsBytes(destination);
        int bytesRead = stream.ReadAtLeast(
            bytes,
            bytes.Length,
            throwOnEndOfStream: false);
        samplesRead = bytesRead / sizeof(short);
        return true;
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
