using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

public sealed class Pcm16StreamSampleLoader : IRfSampleLoader
{
    private readonly long _dataOffset;
    private readonly long? _dataLengthBytes;

    public Pcm16StreamSampleLoader(long dataOffset = 0, long? dataLengthBytes = null)
    {
        _dataOffset = dataOffset;
        _dataLengthBytes = dataLengthBytes;
    }

    public double[]? Read(Stream stream, long sample, int readLength)
    {
        if (sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Sample offset must be non-negative.");
        }

        if (readLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readLength), "Read length must be non-negative.");
        }

        long relativeOffset = checked(sample * 2);
        int byteCount = checked(readLength * 2);
        if (_dataLengthBytes is long length && relativeOffset + byteCount > length)
        {
            return null;
        }

        stream.Seek(checked(_dataOffset + relativeOffset), SeekOrigin.Begin);
        byte[] buffer = new byte[byteCount];
        int read = stream.ReadAtLeast(buffer, byteCount, throwOnEndOfStream: false);
        if (read != byteCount)
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
