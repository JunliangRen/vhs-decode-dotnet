using System.Globalization;
using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Rf;

namespace VHSDecode.Preview;

public sealed class DecodePreviewSegmentProvider : IPreviewSegmentProvider
{
    private const double PrerollSeconds = 0.12;
    private readonly ParsedCommand _template;
    private readonly PreviewServerOptions _options;
    private readonly double _baseStartSeconds;
    private readonly double _decodeSampleRateHz;
    private readonly SemaphoreSlim _windowConcurrency;
    private bool _disposed;

    private DecodePreviewSegmentProvider(
        ParsedCommand template,
        PreviewServerOptions options,
        double baseStartSeconds,
        double decodeSampleRateHz,
        PreviewTimeline timeline,
        PreviewMediaInfo mediaInfo)
    {
        _template = template;
        _options = options;
        _baseStartSeconds = baseStartSeconds;
        _decodeSampleRateHz = decodeSampleRateHz;
        Timeline = timeline;
        MediaInfo = mediaInfo;
        _windowConcurrency = new SemaphoreSlim(
            options.MaximumConcurrentWindowBuilds,
            options.MaximumConcurrentWindowBuilds);
    }

    public PreviewMediaInfo MediaInfo { get; }

    public PreviewTimeline Timeline { get; }

    public static async Task<DecodePreviewSegmentProvider> CreateAsync(
        ParsedCommand command,
        PreviewServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(command);
        await PreviewSourceProbe.VerifyFfmpegAsync(
            options.FfmpegPath,
            cancellationToken).ConfigureAwait(false);

        using DecodeSession session = DecodeSessionFactory.Create(template);
        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        double inputSampleRateHz = ResolveInputSampleRateHz(command);
        double sourceDuration = await PreviewSourceProbe.GetDurationSecondsAsync(
            command.InputFile,
            inputSampleRateHz,
            options.FfprobePath,
            cancellationToken).ConfigureAwait(false);
        double baseStartSeconds = ResolveBaseStartSeconds(command, framesPerSecond, session.DecodeSampleRateHz);
        if (baseStartSeconds >= sourceDuration)
        {
            throw new ArgumentException(
                $"Preview start {baseStartSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s is beyond the {sourceDuration.ToString("0.###", CultureInfo.InvariantCulture)} s RF input.");
        }

        double duration = sourceDuration - baseStartSeconds;
        BigInteger requestedLength = command.Get<BigInteger>("length");
        if (requestedLength >= BigInteger.Zero)
        {
            double requestedSeconds = ToFiniteDouble(requestedLength) / framesPerSecond;
            duration = Math.Min(duration, requestedSeconds);
        }

        if (duration <= 0.0)
        {
            throw new ArgumentException("Preview length must include at least one video frame.");
        }

        var timeline = new PreviewTimeline(
            duration,
            framesPerSecond,
            options.SegmentSeconds,
            options.SegmentsPerWindow);
        (int width, int height) = PreviewDimensions(session.System);
        string backend = template.Get<string>("dsp_backend");
        var mediaInfo = new PreviewMediaInfo(
            command.Spec.Name == "ld" ? "LaserDisc" : "VHS",
            session.System,
            framesPerSecond,
            timeline.DurationSeconds,
            width,
            height,
            options.Crf,
            Interlaced: true,
            backend,
            $"low-accuracy-color/{options.DecodedFramesPerWindow}-frame-sampling/dropout-conceal/no-audio");
        return new DecodePreviewSegmentProvider(
            template,
            options,
            baseStartSeconds,
            session.DecodeSampleRateHz,
            timeline,
            mediaInfo);
    }

    public async Task<PreviewSegmentWindow> GenerateWindowAsync(
        int windowIndex,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)windowIndex >= (uint)Timeline.WindowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(windowIndex));
        }

        await _windowConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => GenerateWindow(windowIndex, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _windowConcurrency.Release();
        }
    }

    private PreviewSegmentWindow GenerateWindow(
        int windowIndex,
        CancellationToken cancellationToken)
    {
        double targetSeconds = _baseStartSeconds + Timeline.WindowStartSeconds(windowIndex);
        double decodeStartSeconds = Math.Max(
            _baseStartSeconds,
            targetSeconds - PrerollSeconds);
        long decodeStartSample = checked((long)Math.Round(
            decodeStartSeconds * _decodeSampleRateHz,
            MidpointRounding.AwayFromZero));
        long targetSample = checked((long)Math.Round(
            targetSeconds * _decodeSampleRateHz,
            MidpointRounding.AwayFromZero));
        int outputFrameCount = Timeline.FrameCountInWindow(windowIndex);
        int decodedFrameCount = Math.Min(
            outputFrameCount,
            _options.DecodedFramesPerWindow);
        int prerollFieldCount = checked((int)Math.Ceiling(
            (targetSeconds - decodeStartSeconds) * Timeline.FramesPerSecond * 2.0));
        int prerollFrameCount = (prerollFieldCount + 1) / 2;
        int maximumFields = checked((decodedFrameCount * 2) + prerollFieldCount + 8);
        ParsedCommand windowCommand = PreviewDecodeCommandFactory.ForWindow(
            _template,
            decodeStartSample,
            decodedFrameCount + prerollFrameCount + 8);
        using DecodeSession session = DecodeSessionFactory.Create(windowCommand);
        using FileStream input = new(
            session.InputFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var encoder = new FfmpegHlsWindowEncoder(
            _options.FfmpegPath,
            MediaInfo.Width,
            MediaInfo.Height,
            MediaInfo.Crf,
            MediaInfo.System,
            Timeline);
        return encoder.Encode(
            windowIndex,
            rawVideo =>
            {
                var assembler = new PreviewFrameAssembler(
                    session,
                    rawVideo,
                    MediaInfo.Width,
                    MediaInfo.Height,
                    targetSample,
                    outputFrameCount);
                var engine = new TbcFieldSequenceDecodeEngine(cancellationToken: cancellationToken);
                _ = engine.DecodeToSink(
                    session,
                    input,
                    assembler.Accept,
                    maximumFields);
                assembler.Complete();
            },
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _windowConcurrency.Dispose();
        return ValueTask.CompletedTask;
    }

    private static double ResolveInputSampleRateHz(ParsedCommand command)
    {
        if (command.Spec.Name == "vhs" && command.Get<bool>("cxadc"))
        {
            return FrequencyParser.CxAdcMHz * 1_000_000.0;
        }

        double value = command.Values.TryGetValue("inputfreq", out object? parsed)
            && parsed is double frequency
                ? frequency
                : FrequencyParser.DddMHz;
        return (value > 0.0 ? value : FrequencyParser.DddMHz) * 1_000_000.0;
    }

    internal static (int Width, int Height) PreviewDimensions(string system)
        => (FormatCatalog.NormalizeSystem(system).StartsWith("NTSC", StringComparison.Ordinal)
            || FormatCatalog.ParentSystem(system) == "NTSC")
            ? (640, 480)
            : (768, 576);

    private static double ResolveBaseStartSeconds(
        ParsedCommand command,
        double framesPerSecond,
        double decodeSampleRateHz)
    {
        double startFileLocation = command.Get<double>("start_fileloc");
        if (startFileLocation != -1.0)
        {
            return Math.Max(0.0, startFileLocation / decodeSampleRateHz);
        }

        object? start = command.Values.TryGetValue("start", out object? value) ? value : null;
        double frames = start switch
        {
            int integer => integer,
            BigInteger integer => ToFiniteDouble(integer),
            double floating => floating,
            _ => 0.0
        };
        return Math.Max(0.0, frames / framesPerSecond);
    }

    private static double ToFiniteDouble(BigInteger value)
    {
        double converted = (double)value;
        return double.IsFinite(converted) ? converted : double.MaxValue;
    }
}
