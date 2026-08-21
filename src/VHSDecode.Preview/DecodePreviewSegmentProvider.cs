using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.CudaFast;
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
    private readonly PreviewEncoderBackend _encoderBackend;
    private readonly CudaFastPreviewDecodeSession? _cudaSession;
    private readonly SemaphoreSlim _windowConcurrency;
    private bool _disposed;

    private DecodePreviewSegmentProvider(
        ParsedCommand template,
        PreviewServerOptions options,
        double baseStartSeconds,
        double decodeSampleRateHz,
        PreviewTimeline timeline,
        PreviewMediaInfo mediaInfo,
        PreviewEncoderBackend encoderBackend,
        CudaFastPreviewDecodeSession? cudaSession,
        int decoderThreads,
        bool ippFastEnabled,
        double sourceSampleRateHz)
    {
        _template = template;
        _options = options;
        _baseStartSeconds = baseStartSeconds;
        _decodeSampleRateHz = decodeSampleRateHz;
        _encoderBackend = encoderBackend;
        _cudaSession = cudaSession;
        Timeline = timeline;
        MediaInfo = mediaInfo;
        DecoderThreads = decoderThreads;
        IppFastEnabled = ippFastEnabled;
        SourceSampleRateHz = sourceSampleRateHz;
        DecodeSampleRateHz = decodeSampleRateHz;
        _windowConcurrency = new SemaphoreSlim(
            cudaSession is null ? options.MaximumConcurrentWindowBuilds : 1,
            cudaSession is null ? options.MaximumConcurrentWindowBuilds : 1);
    }

    public PreviewMediaInfo MediaInfo { get; }

    public PreviewTimeline Timeline { get; }

    public int DecoderThreads { get; }

    public bool IppFastEnabled { get; }

    public bool CudaFastEnabled => _cudaSession is not null;

    public double SourceSampleRateHz { get; }

    public double DecodeSampleRateHz { get; }

    internal event Action<PreviewWindowGenerationUpdate>? WindowGenerationUpdated;

    public static async Task<DecodePreviewSegmentProvider> CreateAsync(
        ParsedCommand command,
        PreviewServerOptions options,
        CancellationToken cancellationToken = default,
        TextWriter? diagnosticOutput = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        TextWriter output = diagnosticOutput ?? TextWriter.Null;
        return await PreviewBackendSelector.SelectAsync(
            command,
            (backend, token) => CreateForBackendAsync(
                command,
                options,
                backend,
                output,
                token),
            output,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DecodePreviewSegmentProvider> CreateForBackendAsync(
        ParsedCommand command,
        PreviewServerOptions options,
        DspBackend backend,
        TextWriter diagnosticOutput,
        CancellationToken cancellationToken)
    {
        bool automaticCuda = command.GetSource("dsp_backend") == ParsedOptionSource.Default
            && backend == DspBackend.CudaFast;
        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(
            command,
            backend);

        using DecodeSession session = DecodeSessionFactory.Create(template);
        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        double inputSampleRateHz = ResolveInputSampleRateHz(command);
        double sourceDuration = await PreviewSourceProbe.GetDurationSecondsAsync(
            command.InputFile,
            inputSampleRateHz,
            options.FfprobePath,
            cancellationToken).ConfigureAwait(false);
        double baseStartSeconds = ResolveBaseStartSeconds(command, framesPerSecond, inputSampleRateHz);
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
        bool cudaPreview = session.ExecutionOptions.DspBackend == DspBackend.CudaFast;
        PreviewEncoderBackend encoderBackend = cudaPreview
            ? PreviewEncoderBackend.Nvenc
            : await PreviewEncoderSelector.SelectAsync(
                options.FfmpegPath,
                width,
                height,
                options.Crf,
                session.System,
                framesPerSecond,
                cancellationToken).ConfigureAwait(false);
        CudaFastPreviewDecodeSession? cudaSession = null;
        if (cudaPreview)
        {
            if (command.Spec.Name != "vhs"
                || !string.Equals(
                    command.Get<string>("tape_format"),
                    "VHS",
                    StringComparison.OrdinalIgnoreCase)
                || Math.Abs(inputSampleRateHz - 40_000_000.0) > 0.5)
            {
                throw new NotSupportedException(
                    "CUDA-fast preview requires the VHS command, VHS tape format, and native 40 MSPS RF input; no CPU preview fallback was performed.");
            }
            if (!CudaFastDecodeRunner.TryGetInputSampleCount(
                    command.InputFile,
                    out long totalSourceSamples))
            {
                string message =
                    $"CUDA-fast preview cannot determine the RF sample count for '{command.InputFile}'.";
                if (automaticCuda)
                {
                    throw new AutomaticCudaPreviewUnavailableException(message);
                }

                throw new NotSupportedException(message);
            }
            try
            {
                cudaSession = CudaFastPreviewDecodeSession.Create(
                    command.InputFile,
                    totalSourceSamples,
                    session.System,
                    command.Get<string>("tape_speed"),
                    width,
                    height,
                    framesPerSecond * 2.0,
                    options.Crf,
                    checked(timeline.FramesPerSegment * 2),
                    diagnosticOutput,
                    cancellationToken);
            }
            catch (Exception ex) when (automaticCuda && IsCudaStartupFailure(ex))
            {
                throw new AutomaticCudaPreviewUnavailableException(ex.Message, ex);
            }
        }
        string backendValue = DspBackendParser.ToCommandLineValue(
            session.ExecutionOptions.DspBackend);
        bool twentyMspsRf = Math.Abs(
            session.DecodeSampleRateHz - 20_000_000.0) <= 1e-6;
        var mediaInfo = new PreviewMediaInfo(
            command.Spec.Name == "ld" ? "LaserDisc" : "VHS",
            session.System,
            framesPerSecond * 2.0,
            timeline.DurationSeconds,
            width,
            height,
            options.Crf,
            Interlaced: false,
            backendValue,
            cudaPreview
                ? "40-to-20-msps-gpu/fast-color/gpu-bob/cross-field-dropout/chroma-stabilized/no-audio"
                : (twentyMspsRf ? "20-msps-rf/" : string.Empty)
                    + "fast-color/full-frame-motion/dropout-conceal/no-audio",
            cudaPreview
                ? "NVENC block-linear CUDA array + FFmpeg copy-mux"
                : PreviewEncoderSelector.DisplayName(encoderBackend));
        return new DecodePreviewSegmentProvider(
            template,
            options,
            baseStartSeconds,
            session.DecodeSampleRateHz,
            timeline,
            mediaInfo,
            encoderBackend,
            cudaSession,
            session.ExecutionOptions.WorkerThreads,
            session.ExecutionOptions.DspBackend == DspBackend.IppFast,
            inputSampleRateHz);
    }

    private static bool IsCudaStartupFailure(Exception exception)
        => exception is DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException
            or InvalidOperationException
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException;

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
        long startedTimestamp = Stopwatch.GetTimestamp();
        NotifyWindowGenerationUpdated(
            PreviewWindowGenerationUpdate.Started(windowIndex, startedTimestamp));
        try
        {
            PreviewSegmentWindow window;
            try
            {
                window = await Task.Run(
                    () => GenerateWindow(windowIndex, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                NotifyWindowGenerationUpdated(
                    PreviewWindowGenerationUpdate.Abandoned(windowIndex, startedTimestamp));
                throw;
            }

            NotifyWindowGenerationUpdated(PreviewWindowGenerationUpdate.Completed(
                windowIndex,
                Timeline.FrameCountInWindow(windowIndex),
                startedTimestamp,
                Stopwatch.GetTimestamp()));
            return window;
        }
        finally
        {
            _windowConcurrency.Release();
        }
    }

    private void NotifyWindowGenerationUpdated(PreviewWindowGenerationUpdate update)
    {
        try
        {
            WindowGenerationUpdated?.Invoke(update);
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // Console progress is observational and must not fail a media request.
        }
    }

    private PreviewSegmentWindow GenerateWindow(
        int windowIndex,
        CancellationToken cancellationToken)
    {
        if (_cudaSession is not null)
        {
            return GenerateCudaWindow(windowIndex, cancellationToken);
        }

        double targetSeconds = _baseStartSeconds + Timeline.WindowStartSeconds(windowIndex);
        double decodeStartSeconds = Math.Max(
            _baseStartSeconds,
            targetSeconds - PrerollSeconds);
        long targetSample = checked((long)Math.Round(
            targetSeconds * _decodeSampleRateHz,
            MidpointRounding.AwayFromZero));
        int outputFrameCount = Timeline.FrameCountInWindow(windowIndex);
        int decodedFrameCount = outputFrameCount;
        int prerollFieldCount = checked((int)Math.Ceiling(
            (targetSeconds - decodeStartSeconds) * Timeline.FramesPerSecond * 2.0));
        int prerollFrameCount = (prerollFieldCount + 1) / 2;
        int maximumFields = checked((decodedFrameCount * 2) + prerollFieldCount + 8);
        ParsedCommand windowCommand = PreviewDecodeCommandFactory.ForWindow(
            _template,
            decodeStartSeconds,
            SourceSampleRateHz,
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
            Timeline,
            _encoderBackend);
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
                    () => assembler.SampledFrameCount >= outputFrameCount,
                    maximumFields);
                assembler.Complete();
            },
            cancellationToken);
    }

    private PreviewSegmentWindow GenerateCudaWindow(
        int windowIndex,
        CancellationToken cancellationToken)
    {
        CudaFastPreviewDecodeSession cudaSession = _cudaSession
            ?? throw new InvalidOperationException("CUDA preview session was not initialized.");
        double targetSeconds = _baseStartSeconds + Timeline.WindowStartSeconds(windowIndex);
        long targetSourceSample = checked((long)Math.Round(
            targetSeconds * SourceSampleRateHz,
            MidpointRounding.AwayFromZero));
        int outputFrameCount = checked(Timeline.FrameCountInWindow(windowIndex) * 2);
        var muxer = new FfmpegH264HlsWindowMuxer(_options.FfmpegPath, Timeline);
        return muxer.Mux(
            windowIndex,
            h264 => cudaSession.DecodeWindow(
                targetSourceSample,
                outputFrameCount,
                h264,
                cancellationToken),
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _cudaSession?.Dispose();
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

    internal static double ResolveBaseStartSeconds(
        ParsedCommand command,
        double framesPerSecond,
        double sourceSampleRateHz)
    {
        double startFileLocation = command.Get<double>("start_fileloc");
        if (startFileLocation != -1.0)
        {
            return Math.Max(0.0, startFileLocation / sourceSampleRateHz);
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
