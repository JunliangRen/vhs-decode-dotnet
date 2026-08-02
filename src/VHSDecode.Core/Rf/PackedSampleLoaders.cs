using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

public sealed class PackedDdD4To40SampleLoader : IReusableRfSampleLoader
{
    private const int MaximumRetainedBufferLength = 1024 * 1024;
    internal const int MaximumRetainedDecodedBufferLength = MaximumRetainedBufferLength / sizeof(double);
    internal const int MaximumRetainedDecodedBufferCount = 32;
    private byte[]? _readBuffer;
    private readonly object _decodedBufferLock = new();
    private readonly double[]?[] _decodedBuffers = new double[]?[MaximumRetainedDecodedBufferCount];
    private int _decodedBufferCount;

    public double[]? Read(Stream stream, long sample, int readLength)
        => ReadCore(stream, sample, readLength, reuseDecodedBuffer: false);

    bool IReusableRfSampleLoader.ReuseForSequentialDecode => true;

    internal double[]? ReadReusable(Stream stream, long sample, int readLength)
        => ReadCore(stream, sample, readLength, reuseDecodedBuffer: true);

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

    private double[]? ReadCore(Stream stream, long sample, int readLength, bool reuseDecodedBuffer)
    {
        long start = (sample / 4) * 5;
        int offset = (int)(sample % 4);
        int needed = checked(((readLength * 5) / 4) + 5);

        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = TakeReadBuffer(needed);
        double[]? output = null;
        bool completed = false;
        try
        {
            int read = stream.ReadAtLeast(
                buffer.AsSpan(0, needed),
                needed,
                throwOnEndOfStream: false);
            if (read != needed)
            {
                return null;
            }

            int fullGroups = needed / 5;
            int partialGroupSamples = Math.Max(0, (needed % 5) - 1);
            int availableSamples = checked((fullGroups * 4) + partialGroupSamples);
            if (offset + readLength > availableSamples)
            {
                return null;
            }

            output = reuseDecodedBuffer
                ? TakeDecodedBuffer(readLength)
                : new double[readLength];
            int outputIndex = 0;
            int firstCount = Math.Min(4 - offset, readLength);
            for (int sampleIndex = offset; sampleIndex < offset + firstCount; sampleIndex++)
            {
                output[outputIndex++] = DecodeSample(buffer, 0, sampleIndex);
            }

            int group = 1;
            while (outputIndex <= readLength - 4)
            {
                int i = group * 5;
                int s0 = (buffer[i] << 2) | ((buffer[i + 1] >> 6) & 0x03);
                int s1 = ((buffer[i + 1] & 0x3F) << 4) | ((buffer[i + 2] >> 4) & 0x0F);
                int s2 = ((buffer[i + 2] & 0x0F) << 6) | ((buffer[i + 3] >> 2) & 0x3F);
                int s3 = ((buffer[i + 3] & 0x03) << 8) | buffer[i + 4];
                output[outputIndex] = ToSignedDdD16(s0);
                output[outputIndex + 1] = ToSignedDdD16(s1);
                output[outputIndex + 2] = ToSignedDdD16(s2);
                output[outputIndex + 3] = ToSignedDdD16(s3);
                outputIndex += 4;
                group++;
            }

            int remaining = readLength - outputIndex;
            for (int sampleIndex = 0; sampleIndex < remaining; sampleIndex++)
            {
                output[outputIndex++] = DecodeSample(buffer, group * 5, sampleIndex);
            }

            completed = true;
            return output;
        }
        finally
        {
            if (reuseDecodedBuffer && output is not null && !completed)
            {
                ReturnReusable(output);
            }

            ReturnReadBuffer(buffer);
        }
    }

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

        return new double[length];
    }

    private byte[] TakeReadBuffer(int needed)
    {
        if (needed > MaximumRetainedBufferLength)
        {
            return new byte[needed];
        }

        byte[]? cached = Interlocked.Exchange(ref _readBuffer, null);
        return cached is not null && cached.Length >= needed
            ? cached
            : new byte[needed];
    }

    private void ReturnReadBuffer(byte[] buffer)
    {
        if (buffer.Length <= MaximumRetainedBufferLength)
        {
            _ = Interlocked.CompareExchange(ref _readBuffer, buffer, null);
        }
    }

    private static short DecodeSample(byte[] buffer, int index, int sampleIndex)
        => sampleIndex switch
        {
            0 => ToSignedDdD16((buffer[index] << 2) | ((buffer[index + 1] >> 6) & 0x03)),
            1 => ToSignedDdD16(((buffer[index + 1] & 0x3F) << 4) | ((buffer[index + 2] >> 4) & 0x0F)),
            2 => ToSignedDdD16(((buffer[index + 2] & 0x0F) << 6) | ((buffer[index + 3] >> 2) & 0x3F)),
            3 => ToSignedDdD16(((buffer[index + 3] & 0x03) << 8) | buffer[index + 4]),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleIndex))
        };

    private static short ToSignedDdD16(int tenBitSample)
    {
        return (short)((tenBitSample - 512) << 6);
    }
}

public sealed class Packed3To32SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        long start = (sample / 3) * 4;
        int offset = (int)(sample % 3);
        int groups = (offset + readLength + 2) / 3;
        int needed = checked(groups * 4);

        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[needed];
        int read = stream.ReadAtLeast(buffer, needed, throwOnEndOfStream: false);
        if (read != needed)
        {
            return null;
        }

        var unpacked = new double[groups * 3];
        for (int group = 0; group < groups; group++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(group * 4, 4));
            int o = group * 3;
            unpacked[o] = word & 0x3FF;
            unpacked[o + 1] = (word >> 10) & 0x3FF;
            unpacked[o + 2] = (word >> 20) & 0x3FF;
        }

        var output = new double[readLength];
        Array.Copy(unpacked, offset, output, 0, readLength);
        return output;
    }
}
