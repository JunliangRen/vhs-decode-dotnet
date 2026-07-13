using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Decode;

public readonly record struct DecodeReadWindow(long StartSample, int SampleCount);

public static class DecodeReadWindowPlanner
{
    private const int NtscReadLines = 350;
    private const int PalReadLines = 400;
    private const int System819ReadLines = 500;
    private const int NtscReadAlignment = 16_384;
    private const int ExtraBlocks = 2;

    public static int EstimateReadSampleCount(DecodeSession session, int extraReadLines)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (extraReadLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraReadLines));
        }

        double linePeriodUs = session.Parameters.SysParams.GetProperty("line_period").GetDouble();
        int lineLength = checked((int)Math.Round(
            linePeriodUs * (session.DecodeSampleRateHz / 1_000_000.0),
            MidpointRounding.ToEven));
        string parentSystem = FormatCatalog.ParentSystem(session.System);
        long baseReadLength = string.Equals(session.System, "819", StringComparison.Ordinal)
            ? checked((long)lineLength * System819ReadLines)
            : string.Equals(parentSystem, "PAL", StringComparison.Ordinal)
                ? checked((long)lineLength * PalReadLines)
                : checked(((long)lineLength * NtscReadLines / NtscReadAlignment) * NtscReadAlignment);
        long blockCount = (baseReadLength / session.BlockLength) + ExtraBlocks;
        return checked((int)(blockCount * session.BlockLength));
    }

    public static DecodeReadWindow Resolve(DecodeSession session, long intendedStartSample, int requestedSampleCount)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (intendedStartSample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intendedStartSample));
        }

        if (requestedSampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSampleCount));
        }

        if (requestedSampleCount == 0)
        {
            return new DecodeReadWindow(intendedStartSample, requestedSampleCount);
        }

        long readLocation = Math.Max(0L, intendedStartSample - session.BlockCut);
        long alignedRequestStart = (readLocation / session.BlockLength) * session.BlockLength;
        long alignedRequestEnd = checked(alignedRequestStart + requestedSampleCount);
        long firstBlock = alignedRequestStart / session.StreamDecoder.BlockStride;
        long lastBlock = alignedRequestEnd / session.StreamDecoder.BlockStride;
        long spanStart = checked(firstBlock * session.StreamDecoder.BlockStride);
        long spanLength = checked((lastBlock - firstBlock + 1) * session.StreamDecoder.BlockStride);
        return new DecodeReadWindow(spanStart, checked((int)spanLength));
    }
}
