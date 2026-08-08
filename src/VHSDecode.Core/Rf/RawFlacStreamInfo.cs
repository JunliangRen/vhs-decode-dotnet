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
    private const int InvalidMetadataBlockType = 127;
    private const int MinimumFlacBlockSize = 16;
    private const int MaximumFlacBlockSize = 65_535;

    internal bool IsNativeRfPcm16
        => SampleRateHz == FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            && Channels == 1
            && BitsPerSample == 16
            && TotalSamples is > 0;

    internal bool SupportsExactLibsndfileSeeking
        => IsNativeRfPcm16 && TotalSamples <= int.MaxValue;

    internal int? FixedBlockSize
        => MinimumBlockSize is >= MinimumFlacBlockSize and <= MaximumFlacBlockSize
            && MinimumBlockSize == MaximumBlockSize
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
        bool usesFixedBlockingStrategy = metadataChainComplete
            && minimumBlockSize is >= MinimumFlacBlockSize and <= MaximumFlacBlockSize
            && minimumBlockSize == maximumBlockSize
            && TryReadFirstFixedFrameHeader(
                input,
                minimumBlockSize,
                sampleRate,
                channels,
                bitsPerSample);
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
            if (blockType is StreamInfoBlockType or InvalidMetadataBlockType)
            {
                return false;
            }

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

    private static bool TryReadFirstFixedFrameHeader(
        Stream input,
        int expectedBlockSize,
        int expectedSampleRate,
        int expectedChannels,
        int expectedBitsPerSample)
    {
        Span<byte> header = stackalloc byte[16];
        if (!TryReadExactly(input, header[..4])
            || header[0] != 0xff
            || header[1] != 0xf8
            || !TryDecodeFixedBlockSize(header[2] >> 4, out int blockSize)
            || blockSize != expectedBlockSize
            || (header[3] >> 4) != expectedChannels - 1
            || (header[3] & 0x01) != 0)
        {
            return false;
        }

        int sampleSizeCode = (header[3] >> 1) & 0x07;
        if (expectedBitsPerSample != 16 || sampleSizeCode is not (0 or 4))
        {
            return false;
        }

        int length = 4;
        if (!TryAppendByte(input, header, ref length)
            || header[length - 1] != 0)
        {
            return false;
        }

        if (!TryResolveFrameSampleRate(
                input,
                header,
                ref length,
                header[2] & 0x0f,
                expectedSampleRate,
                out int sampleRate)
            || sampleRate != expectedSampleRate
            || !TryAppendByte(input, header, ref length))
        {
            return false;
        }

        return CalculateFlacCrc8(header[..(length - 1)]) == header[length - 1];
    }

    private static bool TryDecodeFixedBlockSize(int code, out int blockSize)
    {
        blockSize = code switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (code - 2),
            >= 8 and <= 15 => 256 << (code - 8),
            _ => 0
        };
        return blockSize != 0;
    }

    private static bool TryResolveFrameSampleRate(
        Stream input,
        Span<byte> header,
        ref int length,
        int code,
        int streamInfoSampleRate,
        out int sampleRate)
    {
        sampleRate = code switch
        {
            0 => streamInfoSampleRate,
            1 => 88_200,
            2 => 176_400,
            3 => 192_000,
            4 => 8_000,
            5 => 16_000,
            6 => 22_050,
            7 => 24_000,
            8 => 32_000,
            9 => 44_100,
            10 => 48_000,
            11 => 96_000,
            _ => 0
        };
        if (code == 12)
        {
            if (!TryAppendByte(input, header, ref length))
            {
                return false;
            }

            sampleRate = header[length - 1] * 1_000;
        }
        else if (code is 13 or 14)
        {
            if (!TryAppendByte(input, header, ref length)
                || !TryAppendByte(input, header, ref length))
            {
                return false;
            }

            sampleRate = BinaryPrimitives.ReadUInt16BigEndian(header[(length - 2)..length]);
            if (code == 14)
            {
                sampleRate *= 10;
            }
        }

        return code != 15 && sampleRate > 0;
    }

    private static bool TryAppendByte(Stream input, Span<byte> buffer, ref int length)
    {
        if (length >= buffer.Length)
        {
            return false;
        }

        int value = input.ReadByte();
        if (value < 0)
        {
            return false;
        }

        buffer[length++] = checked((byte)value);
        return true;
    }

    private static byte CalculateFlacCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc << 1);
            }
        }

        return crc;
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
