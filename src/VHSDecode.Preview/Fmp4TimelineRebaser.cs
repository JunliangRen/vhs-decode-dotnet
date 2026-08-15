using System.Buffers.Binary;

namespace VHSDecode.Preview;

internal static class Fmp4TimelineRebaser
{
    private const uint Sidx = 0x73696478;
    private const uint Moof = 0x6d6f6f66;
    private const uint Traf = 0x74726166;
    private const uint Tfdt = 0x74666474;

    internal static void RebaseInPlace(byte[] fragment, double timelineOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (!double.IsFinite(timelineOffsetSeconds) || timelineOffsetSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineOffsetSeconds));
        }

        if (timelineOffsetSeconds == 0.0)
        {
            return;
        }

        Span<byte> data = fragment;
        BoxInfo sidx = FindTopLevelBox(data, Sidx)
            ?? throw new InvalidDataException("The fMP4 segment has no sidx box.");
        int sidxPayload = checked(sidx.Offset + sidx.HeaderSize);
        if (sidx.Size < sidx.HeaderSize + 20)
        {
            throw new InvalidDataException("The fMP4 sidx box is truncated.");
        }

        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(data[(sidxPayload + 8)..]);
        if (timescale == 0)
        {
            throw new InvalidDataException("The fMP4 sidx timescale is zero.");
        }

        long signedOffset = checked((long)Math.Round(
            timelineOffsetSeconds * timescale,
            MidpointRounding.AwayFromZero));
        ulong timelineOffset = checked((ulong)signedOffset);
        AddFullBoxTime(data, sidx, fieldOffset: 12, timelineOffset);
        int tfdtCount = RebaseDecodeTimes(data, 0, data.Length, timelineOffset);
        if (tfdtCount == 0)
        {
            throw new InvalidDataException("The fMP4 segment has no tfdt box.");
        }
    }

    private static BoxInfo? FindTopLevelBox(ReadOnlySpan<byte> data, uint expectedType)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            BoxInfo box = ReadBox(data, offset, data.Length);
            if (box.Type == expectedType)
            {
                return box;
            }

            offset = checked(offset + box.Size);
        }

        return null;
    }

    private static int RebaseDecodeTimes(
        Span<byte> data,
        int start,
        int end,
        ulong timelineOffset)
    {
        int count = 0;
        int offset = start;
        while (offset < end)
        {
            BoxInfo box = ReadBox(data, offset, end);
            if (box.Type == Tfdt)
            {
                AddFullBoxTime(data, box, fieldOffset: 4, timelineOffset);
                count++;
            }
            else if (box.Type is Moof or Traf)
            {
                count += RebaseDecodeTimes(
                    data,
                    checked(box.Offset + box.HeaderSize),
                    checked(box.Offset + box.Size),
                    timelineOffset);
            }

            offset = checked(offset + box.Size);
        }

        return count;
    }

    private static void AddFullBoxTime(
        Span<byte> data,
        BoxInfo box,
        int fieldOffset,
        ulong timelineOffset)
    {
        int payload = checked(box.Offset + box.HeaderSize);
        if (box.Size < box.HeaderSize + fieldOffset + 1)
        {
            throw new InvalidDataException("The fMP4 full box is truncated.");
        }

        byte version = data[payload];
        int valueOffset = checked(payload + fieldOffset);
        if (version == 0)
        {
            EnsureFieldFits(box, fieldOffset, sizeof(uint));
            uint value = BinaryPrimitives.ReadUInt32BigEndian(data[valueOffset..]);
            uint rebased = checked(value + checked((uint)timelineOffset));
            BinaryPrimitives.WriteUInt32BigEndian(data[valueOffset..], rebased);
        }
        else if (version == 1)
        {
            EnsureFieldFits(box, fieldOffset, sizeof(ulong));
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(data[valueOffset..]);
            BinaryPrimitives.WriteUInt64BigEndian(
                data[valueOffset..],
                checked(value + timelineOffset));
        }
        else
        {
            throw new InvalidDataException($"Unsupported fMP4 full-box version {version}.");
        }
    }

    private static void EnsureFieldFits(BoxInfo box, int fieldOffset, int fieldSize)
    {
        if (box.Size < box.HeaderSize + fieldOffset + fieldSize)
        {
            throw new InvalidDataException("The fMP4 full box is truncated.");
        }
    }

    private static BoxInfo ReadBox(ReadOnlySpan<byte> data, int offset, int end)
    {
        if (offset < 0 || end > data.Length || offset > end - 8)
        {
            throw new InvalidDataException("The fMP4 box header is truncated.");
        }

        uint shortSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
        int headerSize = 8;
        long size = shortSize;
        if (shortSize == 1)
        {
            if (offset > end - 16)
            {
                throw new InvalidDataException("The fMP4 large box header is truncated.");
            }

            ulong largeSize = BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]);
            if (largeSize > int.MaxValue)
            {
                throw new InvalidDataException("The fMP4 box is too large.");
            }

            headerSize = 16;
            size = (long)largeSize;
        }
        else if (shortSize == 0)
        {
            size = end - offset;
        }

        if (size < headerSize || size > end - offset)
        {
            throw new InvalidDataException("The fMP4 box size is invalid.");
        }

        return new BoxInfo(offset, checked((int)size), headerSize, type);
    }

    private readonly record struct BoxInfo(
        int Offset,
        int Size,
        int HeaderSize,
        uint Type);
}
