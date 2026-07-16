using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

// Modified FFmpeg n8.1.2 IMA WAV decoder adaptation; see THIRD-PARTY-NOTICES.md.
internal sealed class ImaWavPcm16Stream : Stream
{
    private static readonly int[] BlockSizes = [4, 12, 4, 20];
    private static readonly int[] BlockSamples = [16, 32, 8, 32];

    private static readonly int[][] IndexTables =
    [
        [-1, 2, -1, 2],
        [-1, -1, 1, 2, -1, -1, 1, 2],
        [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8],
        [
            -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16,
            -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16
        ]
    ];

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ];

    private readonly FileStream _input;
    private readonly int _channelCount;
    private readonly int _blockAlign;
    private readonly int _bitsPerSample;
    private readonly long _dataOffset;
    private readonly long _dataLength;
    private byte[] _frame = [];
    private int _frameOffset;
    private long _dataPosition;
    private long _logicalSamplePosition;
    private bool _disposed;

    private ImaWavPcm16Stream(FileStream input, ImaWavFormat format)
    {
        _input = input;
        _channelCount = format.ChannelCount;
        _blockAlign = format.BlockAlign;
        _bitsPerSample = format.BitsPerSample;
        _dataOffset = format.DataOffset;
        _dataLength = format.DataLength;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal static bool TryOpen(string filename, out ImaWavPcm16Stream? stream)
    {
        stream = null;
        FileStream? input = null;
        try
        {
            input = new FileStream(
                filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            if (!TryReadFormat(input, out ImaWavFormat format))
            {
                input.Dispose();
                return false;
            }

            stream = new ImaWavPcm16Stream(input, format);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            input?.Dispose();
            return false;
        }
    }

    internal PyAvAudioFrameGeometry? ReadNextFrameGeometry()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOffset != _frame.Length)
        {
            throw new InvalidOperationException(
                "The current IMA WAV frame must be consumed before requesting another frame.");
        }

        long remaining = _dataLength - _dataPosition;
        if (remaining <= 0)
        {
            return null;
        }

        int blockLength = checked((int)Math.Min(_blockAlign, remaining));
        byte[] block = new byte[blockLength];
        _input.Position = checked(_dataOffset + _dataPosition);
        int bytesRead = ReadAtMost(_input, block);
        if (bytesRead == 0)
        {
            return null;
        }

        if (bytesRead != block.Length)
        {
            Array.Resize(ref block, bytesRead);
        }

        _frame = DecodeBlock(block, out int logicalSamples);
        _frameOffset = 0;
        _dataPosition += bytesRead;
        long? presentationRfSample = _logicalSamplePosition <= long.MaxValue / 1000
            ? _logicalSamplePosition * 1000
            : null;
        _logicalSamplePosition = checked(_logicalSamplePosition + logicalSamples);
        return new PyAvAudioFrameGeometry(logicalSamples, presentationRfSample);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = Math.Min(buffer.Length, _frame.Length - _frameOffset);
        _frame.AsSpan(_frameOffset, count).CopyTo(buffer);
        _frameOffset += count;
        return count;
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _input.Dispose();
        }

        base.Dispose(disposing);
    }

    private byte[] DecodeBlock(ReadOnlySpan<byte> block, out int logicalSamples)
    {
        int headerLength = checked(_channelCount * 4);
        if (block.Length < headerLength)
        {
            throw new InvalidDataException("IMA WAV block is shorter than its channel headers.");
        }

        int tableIndex = _bitsPerSample - 2;
        int compressedBytesPerChannel = BlockSizes[tableIndex];
        int samplesPerGroup = BlockSamples[tableIndex];
        int compressedBytesPerGroup = checked(compressedBytesPerChannel * _channelCount);
        int groupCount = (block.Length - headerLength) / compressedBytesPerGroup;
        logicalSamples = checked(1 + (groupCount * samplesPerGroup));

        var predictors = new int[_channelCount];
        var stepIndices = new int[_channelCount];
        var channels = new short[_channelCount][];
        for (int channel = 0; channel < _channelCount; channel++)
        {
            int headerOffset = channel * 4;
            predictors[channel] = BinaryPrimitives.ReadInt16LittleEndian(block[headerOffset..]);
            stepIndices[channel] = block[headerOffset + 2];
            if ((uint)stepIndices[channel] > 88)
            {
                throw new InvalidDataException(
                    $"IMA WAV channel {channel} has invalid step index {stepIndices[channel]}.");
            }

            channels[channel] = new short[logicalSamples];
            channels[channel][0] = (short)predictors[channel];
        }

        byte[] compressed = new byte[compressedBytesPerChannel];
        int nibbleMask = (1 << _bitsPerSample) - 1;
        for (int group = 0; group < groupCount; group++)
        {
            int groupOffset = checked(headerLength + (group * compressedBytesPerGroup));
            for (int channel = 0; channel < _channelCount; channel++)
            {
                for (int index = 0; index < compressed.Length; index++)
                {
                    int sourceOffset = checked(
                        groupOffset
                        + (index % 4)
                        + ((index / 4) * _channelCount * 4)
                        + (channel * 4));
                    compressed[index] = block[sourceOffset];
                }

                int bitBuffer = 0;
                int bitsInBuffer = 0;
                int sourceIndex = 0;
                int outputOffset = checked(1 + (group * samplesPerGroup));
                for (int sample = 0; sample < samplesPerGroup; sample++)
                {
                    while (bitsInBuffer < _bitsPerSample)
                    {
                        bitBuffer |= compressed[sourceIndex++] << bitsInBuffer;
                        bitsInBuffer += 8;
                    }

                    int nibble = bitBuffer & nibbleMask;
                    bitBuffer >>= _bitsPerSample;
                    bitsInBuffer -= _bitsPerSample;
                    channels[channel][outputOffset + sample] = ExpandNibble(
                        ref predictors[channel],
                        ref stepIndices[channel],
                        nibble);
                }
            }
        }

        var output = new byte[checked(logicalSamples * sizeof(short))];
        for (int sample = 0; sample < logicalSamples; sample++)
        {
            int value = _channelCount == 1
                ? channels[0][sample]
                : (channels[0][sample] + channels[1][sample] + 1) >> 1;
            BinaryPrimitives.WriteInt16LittleEndian(
                output.AsSpan(sample * sizeof(short), sizeof(short)),
                (short)value);
        }

        return output;
    }

    private short ExpandNibble(ref int predictor, ref int stepIndex, int nibble)
    {
        int shift = _bitsPerSample - 1;
        int step = StepTable[stepIndex];
        int delta = nibble & ((1 << shift) - 1);
        int difference = step >> shift;
        for (int bit = 0; bit < shift; bit++)
        {
            if ((delta & (1 << bit)) != 0)
            {
                difference += step >> (shift - 1 - bit);
            }
        }

        predictor = (nibble & (1 << shift)) != 0
            ? predictor - difference
            : predictor + difference;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        stepIndex = Math.Clamp(stepIndex + IndexTables[_bitsPerSample - 2][nibble], 0, 88);
        return (short)predictor;
    }

    private static bool TryReadFormat(FileStream input, out ImaWavFormat format)
    {
        format = default;
        Span<byte> riffHeader = stackalloc byte[12];
        if (!TryReadExactly(input, 0, riffHeader)
            || (!riffHeader[..4].SequenceEqual("RIFF"u8)
                && !riffHeader[..4].SequenceEqual("RF64"u8))
            || !riffHeader[8..].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        bool isRf64 = riffHeader[..4].SequenceEqual("RF64"u8);
        ulong? rf64DataLength = null;
        WaveFormat? waveFormat = null;
        long? dataOffset = null;
        ulong? dataLength = null;
        long chunkOffset = 12;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> ds64 = stackalloc byte[16];
        while (chunkOffset <= input.Length - chunkHeader.Length)
        {
            if (!TryReadExactly(input, chunkOffset, chunkHeader))
            {
                return false;
            }

            ReadOnlySpan<byte> chunkId = chunkHeader[..4];
            uint chunkLength32 = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            long bodyOffset = checked(chunkOffset + chunkHeader.Length);
            if (chunkId.SequenceEqual("ds64"u8) && isRf64 && chunkLength32 >= 16)
            {
                if (!TryReadExactly(input, bodyOffset, ds64))
                {
                    return false;
                }

                rf64DataLength = BinaryPrimitives.ReadUInt64LittleEndian(ds64[8..]);
            }
            else if (chunkId.SequenceEqual("fmt "u8))
            {
                if (!TryReadWaveFormat(input, bodyOffset, chunkLength32, out WaveFormat parsed))
                {
                    return false;
                }

                waveFormat = parsed;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                ulong declaredLength = chunkLength32 == uint.MaxValue && isRf64
                    ? rf64DataLength ?? ulong.MaxValue
                    : chunkLength32;
                ulong availableLength = checked((ulong)Math.Max(0, input.Length - bodyOffset));
                if (declaredLength > availableLength)
                {
                    // Let FFmpeg handle files that are incomplete or still growing.
                    return false;
                }

                dataOffset = bodyOffset;
                dataLength = declaredLength;
            }

            if (waveFormat.HasValue && dataOffset.HasValue && dataLength.HasValue)
            {
                break;
            }

            if (chunkLength32 == uint.MaxValue)
            {
                return false;
            }

            long paddedLength = checked((long)chunkLength32 + (chunkLength32 & 1));
            chunkOffset = checked(bodyOffset + paddedLength);
        }

        if (!waveFormat.HasValue
            || !dataOffset.HasValue
            || !dataLength.HasValue
            || dataLength.Value > long.MaxValue)
        {
            return false;
        }

        WaveFormat value = waveFormat.Value;
        int headerLength = checked(value.ChannelCount * 4);
        if (value.BlockAlign < headerLength)
        {
            return false;
        }

        format = new ImaWavFormat(
            value.ChannelCount,
            value.BlockAlign,
            value.BitsPerSample,
            dataOffset.Value,
            checked((long)dataLength.Value));
        return true;
    }

    private static bool TryReadWaveFormat(
        FileStream input,
        long offset,
        uint chunkLength,
        out WaveFormat format)
    {
        format = default;
        if (chunkLength < 16 || chunkLength > 1024)
        {
            return false;
        }

        byte[] bytes = new byte[chunkLength];
        if (!TryReadExactly(input, offset, bytes))
        {
            return false;
        }

        ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (formatTag == 0xFFFE
            && bytes.Length >= 40
            && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)) >= 22)
        {
            uint subFormat = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            if (subFormat > ushort.MaxValue
                || !bytes.AsSpan(28, 12).SequenceEqual(
                    new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 }))
            {
                return false;
            }

            formatTag = (ushort)subFormat;
        }

        int channelCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
        if (formatTag != 0x0011
            || channelCount is < 1 or > 2
            || sampleRate != FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            || blockAlign == 0
            || bitsPerSample is < 2 or > 5)
        {
            return false;
        }

        format = new WaveFormat(channelCount, blockAlign, bitsPerSample);
        return true;
    }

    private static bool TryReadExactly(Stream input, long offset, Span<byte> buffer)
    {
        input.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static int ReadAtMost(Stream input, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private readonly record struct WaveFormat(
        int ChannelCount,
        int BlockAlign,
        int BitsPerSample);

    private readonly record struct ImaWavFormat(
        int ChannelCount,
        int BlockAlign,
        int BitsPerSample,
        long DataOffset,
        long DataLength);
}
