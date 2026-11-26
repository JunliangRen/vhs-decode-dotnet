using System.Globalization;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Preview;

public enum PreviewDecodeMode { Tbc, Realtime }

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
    public int? DecodeThreads { get; init; }
}

public sealed record PreviewFieldOptions(
    string Path,
    PreviewSourceFormat SourceFormat,
    PreviewVideoStandard Standard,
    double InputSampleRateHz = 40_000_000,
    TimeSpan Start = default,
    int FieldCount = 1,
    DspBackend Backend = DspBackend.Exact,
    bool IncludeTbcSamples = false)
{
    /// <summary>Selects the field decoding pipeline.</summary>
    public PreviewDecodeMode DecodeMode { get; init; } = PreviewDecodeMode.Tbc;

    /// <summary>Enable baseline restoration for AC-coupled DdD composite input.</summary>
    public bool Cvbs50Ohms { get; init; }

    /// <summary>Selects the NTSC Betamax video carrier profile in Realtime mode.</summary>
    public BetamaxVideoProfile BetamaxProfile { get; init; }

    /// <summary>Selects the BGRA region. Requested TBC planes always cover the full raster.</summary>
    public PreviewRegion Region { get; init; } = PreviewRegion.FullRaster;

    /// <summary>Decoder worker count. Null selects one thread for Realtime or automatic for TBC.</summary>
    public int? DecodeThreads { get; init; }
}

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
        if (options.DecodeThreads is int threads)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads, nameof(options.DecodeThreads));
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
        }, cancellationToken, options.DecodeThreads);
        return count;
    }

    /// <summary>Calls the sink synchronously with owned fields. Decoder-inserted duplicate fields are omitted.</summary>
    public static int DecodeFields(PreviewFieldOptions options, Action<PreviewField> fieldSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fieldSink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FieldCount);
        if (!Enum.IsDefined(options.Region))
            throw new ArgumentOutOfRangeException(nameof(options.Region));
        if (options.DecodeThreads is int threads)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads, nameof(options.DecodeThreads));

        if (!Enum.IsDefined(options.DecodeMode))
            throw new ArgumentOutOfRangeException(nameof(options.DecodeMode));
        if (options.DecodeMode == PreviewDecodeMode.Realtime)
            return RealtimePreviewFileDecoder.Decode(options, fieldSink, cancellationToken);
        if (options.Cvbs50Ohms || options.SourceFormat == PreviewSourceFormat.Cvbs)
            throw new NotSupportedException("CVBS field preview requires Realtime mode.");
        if (options.BetamaxProfile != BetamaxVideoProfile.Standard)
            throw new NotSupportedException("The Betamax video profile option requires Realtime mode.");

        int frames = options.FieldCount / 2 + options.FieldCount % 2;
        var input = new PreviewFileOptions(options.Path, options.SourceFormat, options.Standard,
            options.InputSampleRateHz, options.Start, frames, options.Backend);
        int count = 0;
        DecodeWindow(input, frames, (session, fps, rate, target) =>
        {
            TbcFrameSpec spec = session.TbcFrameSpec;
            var raster = new PreviewRectangle(0, 0, spec.OutputLineLength, spec.OutputLineCount);
            PreviewRectangle active = ActiveFieldRectangle(session);
            bool crop = options.Region == PreviewRegion.ActiveVideo;
            PreviewRectangle output = crop ? new(0, 0, active.Width, active.Height) : raster;
            var renderer = new PreviewFieldRenderer(session, raster.Width, raster.Height * 2,
                fullRaster: true, nativeChroma: true);
            return (field, order) =>
            {
                if (order.IsDuplicateField || field.StartSample < target)
                    return false;

                var converter = field.OutputConverter ?? session.VideoOutput;
                double zeroIre = converter.OutputZero - converter.VSyncIre * converter.OutputScale;
                fieldSink(new PreviewField(renderer.RenderFieldBgra(field, order.IsFirstField,
                        crop ? active : raster),
                    output, crop ? output : active, order.IsFirstField, TimeSpan.FromSeconds(field.StartSample / rate),
                    fps * 2, options.IncludeTbcSamples ? field.Samples.AsSpan(0, spec.FieldSampleCount).ToArray() : default,
                    options.IncludeTbcSamples && field.ChromaSamples is { } chroma
                        ? chroma.AsSpan(0, spec.FieldSampleCount).ToArray() : default,
                    spec.OutputSampleRateHz, zeroIre, converter.OutputScale, raster));
                return ++count == options.FieldCount;
            };
        }, cancellationToken, options.DecodeThreads);
        return count;
    }

    private static void DecodeWindow(PreviewFileOptions options, int requestedFrames,
        Func<DecodeSession, double, double, long, Func<TbcDecodedField, TbcFieldOrderDecision, bool>> createConsumer,
        CancellationToken cancellationToken, int? decodeThreads = null)
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
        List<string> arguments =
        [
            "--preview-server", standard, "--frequency",
            (options.InputSampleRateHz / 1_000_000).ToString("R", CultureInfo.InvariantCulture),
            "--dsp-backend", backend
        ];
        if (decodeThreads is int threads)
        {
            arguments.Add("--threads");
            arguments.Add(threads.ToString(CultureInfo.InvariantCulture));
        }
        arguments.Add("--");
        arguments.Add(Path.GetFullPath(options.Path));
        ParsedCommand command = new CommandLineParser().Parse(spec, arguments);
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
