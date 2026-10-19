using System.Globalization;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.HiFi;

/// <summary>
/// Offline, bounded-memory Beta path. RF windows are independent; the audio
/// detector runs once, in order, across the complete recording. Temporary audio
/// files permit an exact two-pass notch and final common headroom without keeping
/// a whole recording (or its RF) resident in memory.
/// </summary>
internal sealed class BetaHiFiStreamingDecoder
{
    internal HiFiStreamingDecodeResult Decode(
        HiFiDecodeOptions options,
        IHiFiSampleReader input,
        HiFiOutputWriter output,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default,
        Func<TimeSpan>? elapsedProvider = null)
    {
        var decoder = new BetaHiFiRfDecoder(options.InputRateHz,
            Math.Min(options.Threads, 4), options.HeadSwitchingInterpolation,
            options.DropoutCompensation != HiFiConstants.DropoutCompensationDisabled,
            options.DropoutCompensation == HiFiConstants.DropoutCompensationMute);
        var framer = new BetaHiFiBlockFramer(checked((int)options.InputRateHz), input);
        var reporter = new HiFiProgressReporter(diagnostics, options.InputRateHz,
            48_000, input.TotalSamples, elapsedProvider);
        using FileStream native = CreateTemporaryFile();
        using FileStream compensated = CreateTemporaryFile();
        var calibrationBlocks = new List<BetaHiFiIqBlock>(3);
        var calibrationLengths = new List<int>(3);
        var calibrationStarts = new List<long>(3);
        int blockCount = 0;
        for (int i = 0; i < 3 && framer.ReadNext(cancellationToken) is { } window; i++)
        {
            calibrationBlocks.Add(decoder.Analyze(window.Samples.Span));
            calibrationLengths.Add(window.EmitSamples);
            calibrationStarts.Add(window.StartSample);
            reporter.ReportInput(framer.InputSamplesRead, ++blockCount);
        }

        if (calibrationBlocks.Count == 0)
            throw new InvalidDataException("Betamax HiFi input contains no RF samples.");

        BetaHiFiRfCalibration calibration = decoder.Calibrate(calibrationBlocks, calibrationLengths);
        diagnostics.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Beta HiFi: calibration complete; {calibration.TimingAnchorGroup} timing anchor, "
            + $"A/B gaps {calibration.AToBGap.Right - calibration.AToBGap.Left}/"
            + $"{calibration.BToAGap.Right - calibration.BToAGap.Left} samples at 500 kHz."));
        float rawPeak = 0;
        long frames = 0;
        void SaveNative(BetaHiFiIqBlock iq, int emit, long start)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HiFiDecodedBlock decoded = decoder.Decode(iq, calibration, emit, start);
            var stereo = new float[checked(decoded.Left.Length * 2)];
            for (int n = 0; n < decoded.Left.Length; n++)
            {
                float left = decoded.Left[n], right = decoded.Right[n];
                if (!float.IsFinite(left) || !float.IsFinite(right)
                    || MathF.Abs(left) >= 1 || MathF.Abs(right) >= 1)
                    throw new InvalidDataException("Beta native audio exceeds its calibrated PCM24 range before compensation.");
                rawPeak = Math.Max(rawPeak, Math.Max(MathF.Abs(left), MathF.Abs(right)));
                stereo[n * 2] = left;
                stereo[n * 2 + 1] = right;
            }
            native.Write(MemoryMarshal.AsBytes(stereo.AsSpan()));
            frames = checked(frames + decoded.Left.Length);
        }

        for (int i = 0; i < calibrationBlocks.Count; i++)
            SaveNative(calibrationBlocks[i], calibrationLengths[i], calibrationStarts[i]);
        calibrationBlocks.Clear();

        while (framer.ReadNext(cancellationToken) is { } window)
        {
            SaveNative(decoder.Analyze(window.Samples.Span), window.EmitSamples, window.StartSample);
            reporter.ReportInput(framer.InputSamplesRead, ++blockCount);
        }

        long expectedFrames = BetaHiFiRfDecoder.CalculateAudioFrames(framer.InputSamplesRead, options.InputRateHz);
        if (frames != expectedFrames)
            throw new InvalidDataException("Beta HiFi RF/audio frame accounting does not match.");

        diagnostics.WriteLine("Beta HiFi: applying continuous default compensation (offline audio pass).");
        native.Position = 0;
        float processedPeak = 0;
        long processedFrames = 0;
        BetaHiFiAudioProcessor.ProcessFile(native, frames, block =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedPeak = Math.Max(processedPeak, Math.Max(block.LeftPeak, block.RightPeak));
            compensated.Write(MemoryMarshal.AsBytes(block.Stereo.AsSpan()));
            processedFrames = checked(processedFrames + block.Left.Length);
        }, options.EnableDeemphasis, options.EnableExpander, options.BetaReferenceOffsetDb, cancellationToken);
        if (processedFrames != frames)
            throw new InvalidDataException("Beta compensation changed the number of audio frames.");

        double commonHeadroom = Math.Min(1.0, .90 / Math.Max(rawPeak, processedPeak));
        compensated.Position = 0;
        var samples = new float[4_800 * 2];
        long writtenFrames = 0;
        float leftPeak = 0, rightPeak = 0;
        while (writtenFrames < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(4_800, frames - writtenFrames));
            compensated.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan(0, count * 2)));
            var left = new float[count];
            var right = new float[count];
            for (int i = 0; i < count; i++)
            {
                left[i] = samples[i * 2];
                right[i] = samples[i * 2 + 1];
            }
            HiFiPostProcessedBlock final = PrepareOutput(
                new HiFiPostProcessedBlock(left, right, [], 0, 0), options.AudioMode,
                commonHeadroom * options.Gain);
            output.Write(final);
            leftPeak = Math.Max(leftPeak, final.LeftPeak);
            rightPeak = Math.Max(rightPeak, final.RightPeak);
            writtenFrames += count;
            if (writtenFrames == frames || writtenFrames % 48_000 == 0)
                reporter.ReportOutput(framer.InputSamplesRead, writtenFrames, blockCount);
        }

        return new HiFiStreamingDecodeResult(framer.InputSamplesRead, writtenFrames,
            blockCount, leftPeak, rightPeak);
    }

    // Gain and channel selection follow the shared stereo detector, never feed it.
    internal static HiFiPostProcessedBlock PrepareOutput(HiFiPostProcessedBlock block, string mode, double gain)
    {
        if (block.Left.Length != block.Right.Length || !double.IsFinite(gain) || gain < 0)
            throw new ArgumentException("Invalid Beta output channels or gain.");
        var left = new float[block.Left.Length];
        var right = new float[block.Left.Length];
        var stereo = new float[checked(block.Left.Length * 2)];
        float lp = 0, rp = 0;
        for (int i = 0; i < left.Length; i++)
        {
            double l = block.Left[i], r = block.Right[i];
            (l, r) = mode switch
            {
                "s" or "d" => (l, r),
                "ms" or "dms" => ((l + r) * .5, (l - r) * .5),
                "l" => (l, l),
                "r" => (r, r),
                "sum" => ((l + r) * .5, (l + r) * .5),
                _ => throw new ArgumentException("Unsupported Beta audio mode.", nameof(mode))
            };
            left[i] = (float)(l * gain);
            right[i] = (float)(r * gain);
            if (!float.IsFinite(left[i]) || !float.IsFinite(right[i]))
                throw new InvalidDataException("Beta output gain produced nonfinite samples.");
            stereo[i * 2] = left[i];
            stereo[i * 2 + 1] = right[i];
            lp = Math.Max(lp, MathF.Abs(left[i]));
            rp = Math.Max(rp, MathF.Abs(right[i]));
        }
        return new HiFiPostProcessedBlock(left, right, stereo, lp, rp);
    }

    private static FileStream CreateTemporaryFile()
        => new(Path.Combine(Path.GetTempPath(), "vhsdecode-beta-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
}

internal sealed record BetaHiFiRfWindow(ReadOnlyMemory<float> Samples, int EmitSamples, long StartSample);

/// <summary>Two seconds of new RF with real 250 ms context on both sides.</summary>
internal sealed class BetaHiFiBlockFramer
{
    private readonly IHiFiSampleReader _reader;
    private readonly int _context;
    private readonly int _emit;
    private readonly float[] _buffer;
    private int _available;
    private int _previousEmit;
    private bool _eof;
    private long _position;

    internal BetaHiFiBlockFramer(int rate, IHiFiSampleReader reader)
    {
        if (rate <= 0 || rate % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(rate));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _context = rate / 4;
        _emit = checked(rate * 2);
        _buffer = new float[checked(_context * 2 + _emit)];
    }

    internal long InputSamplesRead { get; private set; }

    internal BetaHiFiRfWindow? ReadNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_previousEmit > 0)
        {
            _buffer.AsSpan(_previousEmit, _context + _available - _previousEmit).CopyTo(_buffer);
            _available -= _previousEmit;
            _position += _previousEmit;
            _previousEmit = 0;
        }
        int target = _emit + _context;
        while (!_eof && _available < target)
        {
            int requested = target - _available;
            int count = _reader.Read(_buffer.AsSpan(_context + _available, requested), cancellationToken);
            if (count < 0 || count > requested)
                throw new InvalidDataException("Beta RF reader returned an invalid sample count.");
            if (count == 0) _eof = true;
            _available += count;
            InputSamplesRead = checked(InputSamplesRead + count);
        }
        if (_available == 0) return null;
        int emit = Math.Min(_emit, _available);
        int length = checked(_context * 2 + emit);
        _buffer.AsSpan(_context + _available, length - (_context + _available)).Clear();
        _previousEmit = emit;
        return new BetaHiFiRfWindow(_buffer.AsMemory(0, length), emit, _position);
    }
}
