using System.Globalization;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Preview;

public enum PreviewSourceFormat { Vhs, LaserDisc }
public enum PreviewVideoStandard { Ntsc, Pal }

public sealed record PreviewFileOptions(
    string Path,
    PreviewSourceFormat SourceFormat,
    PreviewVideoStandard Standard,
    double InputSampleRateHz = 40_000_000,
    TimeSpan Start = default,
    int FrameCount = 1,
    DspBackend Backend = DspBackend.Exact)
{
    public PreviewRegion Region { get; init; } = PreviewRegion.ActiveVideo;
}

public sealed record PreviewFieldOptions(
    string Path,
    PreviewSourceFormat SourceFormat,
    PreviewVideoStandard Standard,
    double InputSampleRateHz = 40_000_000,
    TimeSpan Start = default,
    int FieldCount = 1,
    DspBackend Backend = DspBackend.Exact,
    bool IncludeTbcSamples = false);

/// <summary>Decodes video-only preview frames and fields from a seekable RF file.</summary>
public static class PreviewFileDecoder
{
    internal const double PrerollSeconds = 0.12;

    public static int Decode(PreviewFileOptions options, Action<PreviewFrame> frameSink,
        CancellationToken cancellationToken = default)
        => DecodeFrames(options, frameSink, cancellationToken);

    /// <summary>Calls the sink synchronously with owned woven frames, without end-of-file padding.</summary>
    public static int DecodeFrames(PreviewFileOptions options, Action<PreviewFrame> frameSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(frameSink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FrameCount);
        if (!Enum.IsDefined(options.Region))
            throw new ArgumentOutOfRangeException(nameof(options.Region));

        int count = 0;
        DecodeWindow(options, options.FrameCount, (session, fps, rate, target) =>
        {
            bool full = options.Region == PreviewRegion.FullRaster;
            (int width, int height) = Dimensions(session, options.Region);
            var weaver = new PreviewFrameWeaver(session, width, height, full);
            long halfFrame = Math.Max(1L, checked((long)Math.Round(rate / (fps * 2), MidpointRounding.AwayFromZero)));
            PreviewRectangle active = full ? WeaveRectangle(ActiveFieldRectangle(session))
                : new PreviewRectangle(0, 0, width, height);
            PreviewRectangle raster = full
                ? new(0, 0, session.TbcFrameSpec.OutputLineLength, session.TbcFrameSpec.OutputLineCount * 2)
                : active;
            return (field, order) =>
            {
                byte[]? pixels = weaver.AddField(field, order.IsFirstField, out long start);
                long midpoint = start + halfFrame;
                if (pixels is null || midpoint < target)
                    return false;

                frameSink(new PreviewFrame(pixels, width, height, TimeSpan.FromSeconds(midpoint / rate),
                    fps, active, raster));
                return ++count == options.FrameCount;
            };
        }, cancellationToken);
        return count;
    }

    /// <summary>Calls the sink synchronously with owned fields. Decoder-inserted duplicate fields are omitted.</summary>
    public static int DecodeFields(PreviewFieldOptions options, Action<PreviewField> fieldSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fieldSink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FieldCount);
        int frames = options.FieldCount / 2 + options.FieldCount % 2;
        var input = new PreviewFileOptions(options.Path, options.SourceFormat, options.Standard,
            options.InputSampleRateHz, options.Start, frames, options.Backend);
        int count = 0;
        DecodeWindow(input, frames, (session, fps, rate, target) =>
        {
            TbcFrameSpec spec = session.TbcFrameSpec;
            var raster = new PreviewRectangle(0, 0, spec.OutputLineLength, spec.OutputLineCount);
            PreviewRectangle active = ActiveFieldRectangle(session);
            var renderer = new PreviewFieldRenderer(session, raster.Width, raster.Height * 2,
                fullRaster: true, nativeChroma: true);
            return (field, order) =>
            {
                if (order.IsDuplicateField || field.StartSample < target)
                    return false;

                var converter = field.OutputConverter ?? session.VideoOutput;
                double zeroIre = converter.OutputZero - converter.VSyncIre * converter.OutputScale;
                fieldSink(new PreviewField(renderer.RenderFullFieldBgra(field, order.IsFirstField),
                    raster, active, order.IsFirstField, TimeSpan.FromSeconds(field.StartSample / rate),
                    fps * 2, options.IncludeTbcSamples ? field.Samples.AsSpan(0, spec.FieldSampleCount).ToArray() : default,
                    options.IncludeTbcSamples && field.ChromaSamples is { } chroma
                        ? chroma.AsSpan(0, spec.FieldSampleCount).ToArray() : default,
                    spec.OutputSampleRateHz, zeroIre, converter.OutputScale));
                return ++count == options.FieldCount;
            };
        }, cancellationToken);
        return count;
    }

    private static void DecodeWindow(PreviewFileOptions options, int requestedFrames,
        Func<DecodeSession, double, double, long, Func<TbcDecodedField, TbcFieldOrderDecision, bool>> createConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Start.Ticks);
        if (!double.IsFinite(options.InputSampleRateHz) || options.InputSampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.InputSampleRateHz));

        string backend = options.Backend switch
        {
            DspBackend.Exact => "exact",
            DspBackend.IppFast => "ipp-fast",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Backend))
        };
        DecodeCommandSpec spec = options.SourceFormat switch
        {
            PreviewSourceFormat.Vhs => CliSpecs.Vhs,
            PreviewSourceFormat.LaserDisc => CliSpecs.LaserDisc,
            _ => throw new ArgumentOutOfRangeException(nameof(options.SourceFormat))
        };
        string standard = options.Standard switch
        {
            PreviewVideoStandard.Ntsc => "--ntsc",
            PreviewVideoStandard.Pal => "--pal",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Standard))
        };

        cancellationToken.ThrowIfCancellationRequested();
        ParsedCommand command = new CommandLineParser().Parse(spec,
            ["--preview-server", standard, "--frequency",
                (options.InputSampleRateHz / 1_000_000).ToString("R", CultureInfo.InvariantCulture),
                "--dsp-backend", backend, "--", Path.GetFullPath(options.Path)]);
        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(command);
        double targetSeconds = options.Start.TotalSeconds;
        double decodeStartSeconds = Math.Max(0, targetSeconds - PrerollSeconds);
        int prerollFrames = checked((int)Math.Ceiling((targetSeconds - decodeStartSeconds) * 30));
        int frames = checked(requestedFrames + prerollFrames + 8);
        ParsedCommand window = PreviewDecodeCommandFactory.ForWindow(template, decodeStartSeconds,
            options.InputSampleRateHz, frames);
        using DecodeSession session = DecodeSessionFactory.CreateForPreview(window, pinnedApproxProviderSelection: null);
        using var input = new FileStream(session.InputFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        double fps = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        double rate = session.DecodeSampleRateHz;
        long target = checked((long)Math.Round(targetSeconds * rate, MidpointRounding.AwayFromZero));
        var consume = createConsumer(session, fps, rate, target);
        bool stop = false;
        var engine = new TbcFieldSequenceDecodeEngine(cancellationToken: cancellationToken);
        _ = engine.DecodeToSink(session, input, writes =>
        {
            foreach ((TbcDecodedField field, TbcFieldOrderDecision order) in writes)
            {
                if (stop)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                stop = consume(field, order);
            }
        }, () => stop, checked(frames * 2));
    }

    internal static (int Width, int Height) Dimensions(string system)
        => (FormatCatalog.NormalizeSystem(system).StartsWith("NTSC", StringComparison.Ordinal)
            || FormatCatalog.ParentSystem(system) == "NTSC")
            ? (640, 480)
            : (768, 576);

    internal static (int Width, int Height) Dimensions(DecodeSession session, PreviewRegion region)
    {
        if (region == PreviewRegion.ActiveVideo)
            return Dimensions(session.System);
        TbcFrameSpec spec = session.TbcFrameSpec;
        // Keep every source sample. Interlaced 4:2:0 needs even field height and width.
        return ((spec.OutputLineLength + 1) & ~1, ((spec.OutputLineCount + 1) & ~1) * 2);
    }

    internal static PreviewRectangle ActiveFieldRectangle(DecodeSession session)
    {
        TbcFrameSpec spec = session.TbcFrameSpec;
        int x = Math.Clamp(spec.ActiveVideoStart ?? 0, 0, spec.OutputLineLength - 1);
        int end = Math.Clamp(spec.ActiveVideoEnd ?? spec.OutputLineLength, x + 1, spec.OutputLineLength);
        int height = Dimensions(session.System).Height / 2;
        int y = Math.Min(height == 240 ? 20 : 22, spec.OutputLineCount - 1);
        return new PreviewRectangle(x, y, end - x, Math.Min(height, spec.OutputLineCount - y));
    }

    private static PreviewRectangle WeaveRectangle(PreviewRectangle value)
        => value with { Y = value.Y * 2, Height = value.Height * 2 };
}
