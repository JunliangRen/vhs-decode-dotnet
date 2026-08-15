using System.Buffers.Binary;
using System.Numerics;

namespace VHSDecode.Preview;

internal static class RawFlacSampleCountProbe
{
    private const int StreamInfoLength = 34;
    private const int TailScanBytes = 16 * 1024 * 1024;

    internal static bool TryGetTotalSamples(string path, out long totalSamples)
    {
        totalSamples = 0;
        try
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!TryReadStreamInfo(input, out FlacStreamInfo info, out long audioOffset)
                || input.Length <= audioOffset)
            {
                return false;
            }

            long scanStart = Math.Max(audioOffset, input.Length - TailScanBytes);
            int length = checked((int)(input.Length - scanStart));
            var buffer = new byte[length];
            input.Position = scanStart;
            input.ReadExactly(buffer);

            long maximumEndSample = 0;
            int validHeaders = 0;
            ReadOnlySpan<byte> data = buffer;
            for (int offset = 0; offset <= data.Length - 6; offset++)
            {
                if (data[offset] != 0xff
                    || (data[offset + 1] & 0xfc) != 0xf8
                    || !TryParseFrameHeader(
                        data[offset..],
                        info,
                        out long endSample))
                {
                    continue;
                }

                validHeaders++;
                maximumEndSample = Math.Max(maximumEndSample, endSample);
            }

            if (validHeaders == 0 || maximumEndSample <= 0)
            {
                return false;
            }

            totalSamples = Math.Max(maximumEndSample, info.HeaderTotalSamples);
            return totalSamples > 0;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or OverflowException)
        {
            totalSamples = 0;
            return false;
        }
    }

    private static bool TryReadStreamInfo(
        Stream input,
        out FlacStreamInfo info,
        out long audioOffset)
    {
        info = default;
        audioOffset = 0;
        Span<byte> signature = stackalloc byte[4];
        Span<byte> blockHeader = stackalloc byte[4];
        Span<byte> streamInfo = stackalloc byte[StreamInfoLength];
        if (!TryReadExactly(input, signature)
            || !signature.SequenceEqual("fLaC"u8)
            || !TryReadExactly(input, blockHeader))
        {
            return false;
        }

        bool isLast = (blockHeader[0] & 0x80) != 0;
        int type = blockHeader[0] & 0x7f;
        int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
        if (type != 0 || length != StreamInfoLength || !TryReadExactly(input, streamInfo))
        {
            return false;
        }

        int minimumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(streamInfo);
        int maximumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(streamInfo[2..]);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo[10..]);
        int sampleRate = checked((int)(packed >> 44));
        int channels = checked((int)((packed >> 41) & 0x07)) + 1;
        int bitsPerSample = checked((int)((packed >> 36) & 0x1f)) + 1;
        long headerTotalSamples = checked((long)(packed & 0x0000000fffffffffUL));
        if (minimumBlockSize <= 0
            || maximumBlockSize <= 0
            || sampleRate <= 0
            || channels is < 1 or > 8
            || bitsPerSample is < 4 or > 32)
        {
            return false;
        }

        info = new FlacStreamInfo(
            minimumBlockSize,
            maximumBlockSize,
            sampleRate,
            channels,
            bitsPerSample,
            headerTotalSamples);
        while (!isLast)
        {
            if (!TryReadExactly(input, blockHeader))
            {
                return false;
            }

            isLast = (blockHeader[0] & 0x80) != 0;
            length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            input.Seek(length, SeekOrigin.Current);
        }

        audioOffset = input.Position;
        return true;
    }

    private static bool TryParseFrameHeader(
        ReadOnlySpan<byte> data,
        FlacStreamInfo info,
        out long endSample)
    {
        endSample = 0;
        if (data.Length < 6
            || data[0] != 0xff
            || (data[1] & 0xfc) != 0xf8
            || (data[3] & 0x01) != 0)
        {
            return false;
        }

        bool variableBlock = (data[1] & 0x01) != 0;
        int blockSizeCode = data[2] >> 4;
        int sampleRateCode = data[2] & 0x0f;
        int channelAssignment = data[3] >> 4;
        int sampleSizeCode = (data[3] >> 1) & 0x07;
        if (blockSizeCode == 0
            || sampleRateCode == 15
            || channelAssignment > 10
            || !ChannelAssignmentMatches(channelAssignment, info.Channels)
            || !SampleSizeMatches(sampleSizeCode, info.BitsPerSample))
        {
            return false;
        }

        int offset = 4;
        if (!TryReadUtf8Integer(data, ref offset, out ulong codedNumber)
            || !TryReadBlockSize(data, ref offset, blockSizeCode, out int blockSize)
            || !TryReadSampleRate(data, ref offset, sampleRateCode, info.SampleRate, out int sampleRate)
            || sampleRate != info.SampleRate
            || offset >= data.Length)
        {
            return false;
        }

        byte expectedCrc = data[offset];
        if (CalculateCrc8(data[..offset]) != expectedCrc)
        {
            return false;
        }

        try
        {
            ulong startSample = variableBlock
                ? codedNumber
                : checked(codedNumber * (ulong)info.MinimumBlockSize);
            ulong end = checked(startSample + (uint)blockSize);
            if (end > long.MaxValue)
            {
                return false;
            }

            endSample = (long)end;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadUtf8Integer(
        ReadOnlySpan<byte> data,
        ref int offset,
        out ulong value)
    {
        value = 0;
        if (offset >= data.Length)
        {
            return false;
        }

        byte first = data[offset++];
        if ((first & 0x80) == 0)
        {
            value = first;
            return true;
        }

        int leadingOnes = BitOperations.LeadingZeroCount((uint)(~first & 0xff)) - 24;
        if (leadingOnes is < 2 or > 7 || offset + leadingOnes - 1 > data.Length)
        {
            return false;
        }

        int payloadBits = 7 - leadingOnes;
        value = payloadBits == 0 ? 0UL : (ulong)(first & ((1 << payloadBits) - 1));
        for (int i = 1; i < leadingOnes; i++)
        {
            byte continuation = data[offset++];
            if ((continuation & 0xc0) != 0x80)
            {
                return false;
            }

            value = (value << 6) | (uint)(continuation & 0x3f);
        }

        return true;
    }

    private static bool TryReadBlockSize(
        ReadOnlySpan<byte> data,
        ref int offset,
        int code,
        out int blockSize)
    {
        blockSize = code switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (code - 2),
            >= 8 and <= 15 => 256 << (code - 8),
            _ => 0
        };
        if (code == 6)
        {
            if (offset >= data.Length)
            {
                return false;
            }

            blockSize = data[offset++] + 1;
        }
        else if (code == 7)
        {
            if (offset + 2 > data.Length)
            {
                return false;
            }

            blockSize = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) + 1;
            offset += 2;
        }

        return blockSize > 0;
    }

    private static bool TryReadSampleRate(
        ReadOnlySpan<byte> data,
        ref int offset,
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
            if (offset >= data.Length)
            {
                return false;
            }

            sampleRate = data[offset++] * 1_000;
        }
        else if (code is 13 or 14)
        {
            if (offset + 2 > data.Length)
            {
                return false;
            }

            sampleRate = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (code == 14)
            {
                sampleRate *= 10;
            }
        }

        return sampleRate > 0;
    }

    private static bool ChannelAssignmentMatches(int assignment, int channels)
        => assignment <= 7
            ? assignment + 1 == channels
            : channels == 2;

    private static bool SampleSizeMatches(int code, int bitsPerSample)
        => code switch
        {
            0 => true,
            1 => bitsPerSample == 8,
            2 => bitsPerSample == 12,
            4 => bitsPerSample == 16,
            5 => bitsPerSample == 20,
            6 => bitsPerSample == 24,
            _ => false
        };

    private static byte CalculateCrc8(ReadOnlySpan<byte> data)
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

    private static bool TryReadExactly(Stream input, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = input.Read(destination[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private readonly record struct FlacStreamInfo(
        int MinimumBlockSize,
        int MaximumBlockSize,
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long HeaderTotalSamples);
}
