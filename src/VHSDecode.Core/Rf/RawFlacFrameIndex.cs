using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;

namespace VHSDecode.Core.Rf;

internal sealed class RawFlacFrameIndex
{
    private const int StreamInfoLength = 34;
    private const int ProbeRadiusBytes = 2 * 1024 * 1024;
    private const int TailScanBytes = 16 * 1024 * 1024;
    private const int MaximumProbeCount = 24;

    private static readonly ConcurrentDictionary<CacheKey, RawFlacFrameIndex> Cache = new();

    private readonly string _path;
    private readonly object _gate = new();
    private readonly SortedDictionary<long, FramePoint> _pointsBySample = new();

    private RawFlacFrameIndex(
        string path,
        byte[] metadata,
        long fileLength,
        FlacStreamInfo streamInfo,
        FramePoint firstFrame,
        FramePoint lastFrame)
    {
        _path = path;
        Metadata = metadata;
        FileLength = fileLength;
        StreamInfo = streamInfo;
        FirstFrame = firstFrame;
        LastFrame = lastFrame;
        TotalSamples = Math.Max(
            streamInfo.HeaderTotalSamples,
            checked(lastFrame.StartSample + lastFrame.BlockSize));
        _pointsBySample[firstFrame.StartSample] = firstFrame;
        _pointsBySample[lastFrame.StartSample] = lastFrame;
    }

    internal byte[] Metadata { get; }

    internal long FileLength { get; }

    internal FlacStreamInfo StreamInfo { get; }

    internal FramePoint FirstFrame { get; }

    internal FramePoint LastFrame { get; }

    internal long TotalSamples { get; }

    internal static bool TryOpen(string path, out RawFlacFrameIndex? index)
    {
        index = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return false;
            }

            var key = new CacheKey(
                Path.GetFullPath(path),
                file.Length,
                file.LastWriteTimeUtc.Ticks);
            if (Cache.TryGetValue(key, out index))
            {
                return true;
            }

            index = Create(key.Path);
            if (index is null)
            {
                return false;
            }

            index = Cache.GetOrAdd(key, index);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            return false;
        }
    }

    internal FramePoint LocateFrameAtOrBefore(long targetSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetSample);
        long clampedTarget = Math.Min(targetSample, TotalSamples - 1);
        lock (_gate)
        {
            FramePoint low = FirstFrame;
            FramePoint high = LastFrame;
            foreach ((long sample, FramePoint point) in _pointsBySample)
            {
                if (sample <= clampedTarget)
                {
                    low = point;
                }
                else
                {
                    high = point;
                    break;
                }
            }

            if (clampedTarget < high.StartSample
                && high.StartSample - low.StartSample <= StreamInfo.MaximumBlockSize)
            {
                return low;
            }

            for (int probe = 0; probe < MaximumProbeCount; probe++)
            {
                long predictedOffset = InterpolateOffset(low, high, clampedTarget);
                if (!TryProbeAround(predictedOffset, clampedTarget, out ProbeResult result))
                {
                    predictedOffset = low.ByteOffset + ((high.ByteOffset - low.ByteOffset) / 2);
                    if (!TryProbeAround(predictedOffset, clampedTarget, out result))
                    {
                        break;
                    }
                }

                foreach (FramePoint point in result.Points)
                {
                    _pointsBySample[point.StartSample] = point;
                }

                if (result.TargetPredecessor is { } predecessor
                    && result.TargetSuccessor is { } successor)
                {
                    if (successor.StartSample - predecessor.StartSample
                        <= StreamInfo.MaximumBlockSize)
                    {
                        return predecessor;
                    }
                }

                FramePoint previousLow = low;
                FramePoint previousHigh = high;
                foreach (FramePoint point in result.Points)
                {
                    if (point.StartSample <= clampedTarget
                        && point.StartSample > low.StartSample)
                    {
                        low = point;
                    }
                    else if (point.StartSample > clampedTarget
                        && point.StartSample < high.StartSample)
                    {
                        high = point;
                    }
                }

                if (clampedTarget < high.StartSample
                    && high.StartSample - low.StartSample <= StreamInfo.MaximumBlockSize)
                {
                    return low;
                }

                if (low == previousLow && high == previousHigh)
                {
                    break;
                }
            }

            return low;
        }
    }

    private static RawFlacFrameIndex? Create(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.RandomAccess);
        if (!TryReadMetadata(input, out FlacStreamInfo streamInfo, out byte[] metadata)
            || streamInfo.MinimumBlockSize != streamInfo.MaximumBlockSize
            || streamInfo.SampleRate != FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            || streamInfo.Channels != 1
            || streamInfo.BitsPerSample != 16
            || input.Length <= metadata.Length)
        {
            return null;
        }

        long audioOffset = metadata.Length;
        if (!TryScan(
                input,
                audioOffset,
                Math.Min(input.Length, audioOffset + ProbeRadiusBytes),
                streamInfo,
                out IReadOnlyList<FramePoint> firstPoints)
            || firstPoints.Count == 0)
        {
            return null;
        }

        long tailStart = Math.Max(audioOffset, input.Length - TailScanBytes);
        if (!TryScan(
                input,
                tailStart,
                input.Length,
                streamInfo,
                out IReadOnlyList<FramePoint> lastPoints)
            || lastPoints.Count == 0)
        {
            return null;
        }

        FramePoint first = firstPoints.MinBy(static point => point.StartSample);
        FramePoint last = lastPoints.MaxBy(static point => point.StartSample);
        if (first.StartSample != 0
            || last.StartSample <= first.StartSample
            || last.ByteOffset <= first.ByteOffset)
        {
            return null;
        }

        return new RawFlacFrameIndex(
            path,
            metadata,
            input.Length,
            streamInfo,
            first,
            last);
    }

    private bool TryProbeAround(
        long byteOffset,
        long targetSample,
        out ProbeResult result)
    {
        long start = Math.Max(Metadata.Length, byteOffset - ProbeRadiusBytes);
        long end = Math.Min(FileLength, byteOffset + ProbeRadiusBytes);
        using var input = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.RandomAccess);
        if (!TryScan(input, start, end, StreamInfo, out IReadOnlyList<FramePoint> points)
            || points.Count == 0)
        {
            result = default;
            return false;
        }

        FramePoint? predecessor = null;
        FramePoint? successor = null;
        foreach (FramePoint point in points)
        {
            if (point.StartSample <= targetSample)
            {
                if (!predecessor.HasValue
                    || point.StartSample > predecessor.Value.StartSample)
                {
                    predecessor = point;
                }
            }
            else if (!successor.HasValue
                || point.StartSample < successor.Value.StartSample)
            {
                successor = point;
            }
        }

        result = new ProbeResult(points, predecessor, successor);
        return true;
    }

    private static long InterpolateOffset(
        FramePoint low,
        FramePoint high,
        long targetSample)
    {
        long sampleSpan = high.StartSample - low.StartSample;
        long byteSpan = high.ByteOffset - low.ByteOffset;
        if (sampleSpan <= 0 || byteSpan <= 0)
        {
            return low.ByteOffset;
        }

        long sampleOffset = Math.Clamp(targetSample - low.StartSample, 0, sampleSpan);
        UInt128 scaled = (UInt128)(ulong)sampleOffset * (ulong)byteSpan;
        long relative = checked((long)(scaled / (ulong)sampleSpan));
        return checked(low.ByteOffset + relative);
    }

    private static bool TryReadMetadata(
        FileStream input,
        out FlacStreamInfo info,
        out byte[] metadata)
    {
        info = default;
        metadata = [];
        input.Position = 0;
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
        if (type != 0
            || length != StreamInfoLength
            || !TryReadExactly(input, streamInfo))
        {
            return false;
        }

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo[10..]);
        info = new FlacStreamInfo(
            BinaryPrimitives.ReadUInt16BigEndian(streamInfo),
            BinaryPrimitives.ReadUInt16BigEndian(streamInfo[2..]),
            checked((int)(packed >> 44)),
            checked((int)((packed >> 41) & 0x07)) + 1,
            checked((int)((packed >> 36) & 0x1f)) + 1,
            checked((long)(packed & 0x0000000fffffffffUL)));
        if (info.MinimumBlockSize <= 0
            || info.MaximumBlockSize <= 0
            || info.SampleRate <= 0)
        {
            return false;
        }

        while (!isLast)
        {
            if (!TryReadExactly(input, blockHeader))
            {
                return false;
            }

            isLast = (blockHeader[0] & 0x80) != 0;
            length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            if (length < 0 || input.Position > input.Length - length)
            {
                return false;
            }

            input.Position += length;
        }

        if (input.Position > int.MaxValue)
        {
            return false;
        }

        int metadataLength = checked((int)input.Position);
        metadata = new byte[metadataLength];
        input.Position = 0;
        input.ReadExactly(metadata);
        return true;
    }

    private static bool TryScan(
        FileStream input,
        long start,
        long end,
        FlacStreamInfo info,
        out IReadOnlyList<FramePoint> points)
    {
        points = [];
        if (start < 0 || end <= start || end > input.Length)
        {
            return false;
        }

        int length = checked((int)(end - start));
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        input.Position = start;
        input.ReadExactly(buffer);
        var found = new List<FramePoint>();
        ReadOnlySpan<byte> data = buffer;
        for (int offset = 0; offset <= data.Length - 6; offset++)
        {
            if (data[offset] != 0xff
                || (data[offset + 1] & 0xfc) != 0xf8
                || !TryParseFrameHeader(
                    data[offset..],
                    info,
                    out long startSample,
                    out int blockSize))
            {
                continue;
            }

            found.Add(new FramePoint(
                startSample,
                checked(start + offset),
                blockSize));
        }

        points = found;
        return true;
    }

    private static bool TryParseFrameHeader(
        ReadOnlySpan<byte> data,
        FlacStreamInfo info,
        out long startSample,
        out int blockSize)
    {
        startSample = 0;
        blockSize = 0;
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
        if (variableBlock
            || blockSizeCode == 0
            || sampleRateCode == 15
            || channelAssignment > 10
            || !ChannelAssignmentMatches(channelAssignment, info.Channels)
            || !SampleSizeMatches(sampleSizeCode, info.BitsPerSample))
        {
            return false;
        }

        int offset = 4;
        if (!TryReadUtf8Integer(data, ref offset, out ulong codedNumber)
            || !TryReadBlockSize(data, ref offset, blockSizeCode, out blockSize)
            || blockSize != info.MinimumBlockSize
            || !TryReadSampleRate(data, ref offset, sampleRateCode, info.SampleRate, out int sampleRate)
            || sampleRate != info.SampleRate
            || offset >= data.Length
            || CalculateCrc8(data[..offset]) != data[offset])
        {
            return false;
        }

        try
        {
            ulong value = checked(codedNumber * (ulong)info.MinimumBlockSize);
            if (value > long.MaxValue)
            {
                return false;
            }

            startSample = (long)value;
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

    internal readonly record struct FramePoint(
        long StartSample,
        long ByteOffset,
        int BlockSize);

    internal readonly record struct FlacStreamInfo(
        int MinimumBlockSize,
        int MaximumBlockSize,
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long HeaderTotalSamples);

    private readonly record struct CacheKey(
        string Path,
        long Length,
        long LastWriteTicks);

    private readonly record struct ProbeResult(
        IReadOnlyList<FramePoint> Points,
        FramePoint? TargetPredecessor,
        FramePoint? TargetSuccessor);
}
