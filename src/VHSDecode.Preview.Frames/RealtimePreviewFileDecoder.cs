using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;

namespace VHSDecode.Preview;

internal static class RealtimePreviewFileDecoder
{
    internal static int Decode(PreviewFieldOptions options, Action<PreviewField> sink,
        CancellationToken cancellationToken)
    {
        if (options.InputSampleRateHz is not (20_000_000 or 40_000_000))
            throw new NotSupportedException("Realtime video requires 20 or 40 MSPS input.");
        if (options.IncludeTbcSamples)
            throw new NotSupportedException("Realtime preview does not produce 16-bit TBC planes.");
        if (options.Backend != DspBackend.Exact)
            throw new NotSupportedException("The Backend option applies only to TBC preview.");

        var streamOptions = new RealtimePreviewOptions(options.SourceFormat, options.Standard, (int)options.InputSampleRateHz)
        {
            Region = options.Region,
            Cvbs50Ohms = options.Cvbs50Ohms,
            BetamaxProfile = options.BetamaxProfile,
            DecodeThreads = options.DecodeThreads ?? 1
        };
        RealtimePreviewSession.Validate(streamOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Start.Ticks);
        cancellationToken.ThrowIfCancellationRequested();

        string path = Path.GetFullPath(options.Path);
        bool useFfmpeg = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".s16" or ".s16le" or ".r16" or ".raw" => false,
            ".flac" or ".ldf" => true,
            _ => throw new NotSupportedException("Realtime preview requires signed 16-bit little-endian RF or FLAC.")
        };

        long target = checked((long)Math.Round(options.Start.TotalSeconds * streamOptions.InputSampleRateHz));
        long position = Math.Max(0, target - (long)(PreviewFileDecoder.PrerollSeconds * streamOptions.InputSampleRateHz));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var loader = useFfmpeg ? new FfmpegPcm16SampleLoader(path, fastInputSeek: true) : null;
        if (loader is null)
            input.Position = checked(position * sizeof(short));

        int count = 0;
        using var session = new RealtimePreviewSession(streamOptions, fieldSink: field =>
        {
            sink(field);
            count++;
        });
        var samples = new short[RealtimePreviewSession.BlockSamples];
        while (count < options.FieldCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readSamples;
            if (loader is null)
            {
                Span<byte> bytes = MemoryMarshal.AsBytes(samples.AsSpan());
                int read = input.ReadAtLeast(bytes, bytes.Length, false);
                if ((read & 1) != 0)
                    throw new InvalidDataException("The RF file ends with an incomplete 16-bit sample.");
                readSamples = read / sizeof(short);
            }
            else if (!loader.TryReadInt16(input, position, samples, out readSamples))
            {
                throw new NotSupportedException("Could not read signed 16-bit RF samples from the container.");
            }
            if (readSamples == 0)
                break;

            session.ProcessCore(samples.AsSpan(0, readSamples), position, cancellationToken, target, options.FieldCount - count);
            position = checked(position + readSamples);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }
}
