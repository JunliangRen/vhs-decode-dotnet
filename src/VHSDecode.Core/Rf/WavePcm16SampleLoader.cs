using System.Buffers.Binary;
using System.Text;

namespace VHSDecode.Core.Rf;

public sealed class WavePcm16SampleLoader : IRfSampleLoader
{
    private WaveDataInfo? _cachedInfo;

    public double[]? Read(Stream stream, long sample, int readLength)
    {
        WaveDataInfo info = _cachedInfo ??= ReadWaveInfo(stream);
        return new Pcm16StreamSampleLoader(info.DataOffset, info.DataLengthBytes).Read(
            stream,
            sample,
            readLength);
    }

    public static WaveDataInfo ReadWaveInfo(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[12];
        ReadExactlyOrThrow(stream, header, "RIFF/WAVE header");
        if (!AsciiEquals(header[..4], "RIFF") || !AsciiEquals(header[8..12], "WAVE"))
        {
            throw new InvalidDataException("Input is not a RIFF/WAVE file.");
        }

        bool foundFormat = false;
        ushort audioFormat = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        long dataOffset = -1;
        long dataLength = -1;
        Span<byte> chunkHeader = stackalloc byte[8];

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactlyOrThrow(stream, chunkHeader, "WAVE chunk header");
            string chunkId = Encoding.ASCII.GetString(chunkHeader[..4]);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            long chunkDataOffset = stream.Position;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("WAVE fmt chunk is too short.");
                }

                byte[] fmt = new byte[chunkSize];
                ReadExactlyOrThrow(stream, fmt, "WAVE fmt chunk");
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                foundFormat = true;
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataOffset;
                dataLength = chunkSize;
                stream.Seek(chunkSize, SeekOrigin.Current);
            }
            else
            {
                stream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) != 0)
            {
                stream.Seek(1, SeekOrigin.Current);
            }
        }

        if (!foundFormat)
        {
            throw new InvalidDataException("WAVE file does not contain a fmt chunk.");
        }

        if (dataOffset < 0)
        {
            throw new InvalidDataException("WAVE file does not contain a data chunk.");
        }

        if (audioFormat != 1)
        {
            throw new NotSupportedException($"Only PCM WAVE input is supported natively; format tag was {audioFormat}.");
        }

        if (channels != 1)
        {
            throw new NotSupportedException($"Only mono PCM WAVE input is supported natively; channel count was {channels}.");
        }

        if (bitsPerSample != 16)
        {
            throw new NotSupportedException($"Only 16-bit PCM WAVE input is supported natively; bits per sample was {bitsPerSample}.");
        }

        return new WaveDataInfo(dataOffset, dataLength, sampleRate);
    }

    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer, string description)
    {
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read != buffer.Length)
        {
            throw new EndOfStreamException($"Unexpected end of stream while reading {description}.");
        }
    }

    private static bool AsciiEquals(ReadOnlySpan<byte> value, string expected)
    {
        return value.SequenceEqual(Encoding.ASCII.GetBytes(expected));
    }
}

public readonly record struct WaveDataInfo(long DataOffset, long DataLengthBytes, uint SampleRate);
