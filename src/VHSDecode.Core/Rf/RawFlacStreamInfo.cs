using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

internal readonly record struct RawFlacStreamInfo(
    int SampleRateHz,
    int Channels,
    int BitsPerSample,
    long? TotalSamples,
    int MinimumBlockSize = 0,
    int MaximumBlockSize = 0,
    bool HasSeekTable = false,
    bool MetadataChainComplete = false,
    bool UsesFixedBlockingStrategy = false)
{
    private const int StreamInfoBlockType = 0;
    private const int StreamInfoBlockLength = 34;
    private const int SeekTableBlockType = 3;

    internal bool IsNativeRfPcm16
        => SampleRateHz == FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            && Channels == 1
            && BitsPerSample == 16
            && TotalSamples is > 0;

    internal bool SupportsExactLibsndfileSeeking
        => IsNativeRfPcm16 && TotalSamples <= int.MaxValue;

    internal int? FixedBlockSize
        => MinimumBlockSize > 0 && MinimumBlockSize == MaximumBlockSize
            ? MinimumBlockSize
            : null;

    internal bool SupportsPyAvMappedLibsndfileSeeking
        => IsNativeRfPcm16
            && TotalSamples > int.MaxValue
            && FixedBlockSize.HasValue
            && MetadataChainComplete
            && UsesFixedBlockingStrategy
            && !HasSeekTable;

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

        bool isLastMetadataBlock = (blockHeader[0] & 0x80) != 0;
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
        int minimumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(streamInfo);
        int maximumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(streamInfo[2..]);
        int sampleRate = checked((int)(packed >> 44));
        int channels = checked((int)((packed >> 41) & 0x7)) + 1;
        int bitsPerSample = checked((int)((packed >> 36) & 0x1f)) + 1;
        long totalSamples = checked((long)(packed & 0x0000000FFFFFFFFFUL));
        bool hasSeekTable = false;
        bool metadataChainComplete = isLastMetadataBlock
            || TryScanRemainingMetadata(input, ref hasSeekTable);
        Span<byte> frameHeader = stackalloc byte[2];
        bool usesFixedBlockingStrategy = metadataChainComplete
            && TryReadExactly(input, frameHeader)
            && frameHeader[0] == 0xff
            && frameHeader[1] == 0xf8;
        info = new RawFlacStreamInfo(
            sampleRate,
            channels,
            bitsPerSample,
            totalSamples == 0 ? null : totalSamples,
            minimumBlockSize,
            maximumBlockSize,
            hasSeekTable,
            metadataChainComplete,
            usesFixedBlockingStrategy);
        return true;
    }

    private static bool TryScanRemainingMetadata(Stream input, ref bool hasSeekTable)
    {
        Span<byte> blockHeader = stackalloc byte[4];
        Span<byte> skipBuffer = stackalloc byte[512];
        while (TryReadExactly(input, blockHeader))
        {
            bool isLastMetadataBlock = (blockHeader[0] & 0x80) != 0;
            int blockType = blockHeader[0] & 0x7f;
            int blockLength = (blockHeader[1] << 16)
                | (blockHeader[2] << 8)
                | blockHeader[3];
            hasSeekTable |= blockType == SeekTableBlockType;
            if (!TrySkipExactly(input, blockLength, skipBuffer))
            {
                return false;
            }

            if (isLastMetadataBlock)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipExactly(Stream input, int count, Span<byte> buffer)
    {
        while (count > 0)
        {
            int readLength = Math.Min(count, buffer.Length);
            if (!TryReadExactly(input, buffer[..readLength]))
            {
                return false;
            }

            count -= readLength;
        }

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
