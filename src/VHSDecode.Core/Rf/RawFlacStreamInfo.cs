using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

internal readonly record struct RawFlacStreamInfo(
    int SampleRateHz,
    int Channels,
    int BitsPerSample,
    long? TotalSamples)
{
    private const int StreamInfoBlockType = 0;
    private const int StreamInfoBlockLength = 34;

    internal bool IsNativeRfPcm16
        => SampleRateHz == FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            && Channels == 1
            && BitsPerSample == 16
            && TotalSamples is > 0;

    internal bool SupportsExactLibsndfileSeeking
        => IsNativeRfPcm16 && TotalSamples <= int.MaxValue;

    internal static bool TryRead(string path, out RawFlacStreamInfo info)
    {
        try
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return TryRead(input, out info);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            info = default;
            return false;
        }
    }

    internal static bool TryRead(Stream input, out RawFlacStreamInfo info)
    {
        info = default;
        Span<byte> signature = stackalloc byte[4];
        Span<byte> blockHeader = stackalloc byte[4];
        if (!TryReadExactly(input, signature)
            || !signature.SequenceEqual("fLaC"u8)
            || !TryReadExactly(input, blockHeader))
        {
            return false;
        }

        int blockType = blockHeader[0] & 0x7f;
        int blockLength = (blockHeader[1] << 16)
            | (blockHeader[2] << 8)
            | blockHeader[3];
        if (blockType != StreamInfoBlockType || blockLength != StreamInfoBlockLength)
        {
            return false;
        }

        Span<byte> streamInfo = stackalloc byte[StreamInfoBlockLength];
        if (!TryReadExactly(input, streamInfo))
        {
            return false;
        }

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo[10..]);
        int sampleRate = checked((int)(packed >> 44));
        int channels = checked((int)((packed >> 41) & 0x7)) + 1;
        int bitsPerSample = checked((int)((packed >> 36) & 0x1f)) + 1;
        long totalSamples = checked((long)(packed & 0x0000000FFFFFFFFFUL));
        info = new RawFlacStreamInfo(
            sampleRate,
            channels,
            bitsPerSample,
            totalSamples == 0 ? null : totalSamples);
        return true;
    }

    private static bool TryReadExactly(Stream input, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = input.Read(destination[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
