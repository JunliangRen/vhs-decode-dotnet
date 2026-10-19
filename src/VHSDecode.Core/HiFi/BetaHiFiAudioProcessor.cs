using System.Buffers.Binary;

namespace VHSDecode.Core.HiFi;

/// <summary>
/// The frozen preview-16 empirical Beta backend, operating at 48 kHz stereo.
/// This is a capture-derived approximation, not a reconstruction of Sony NR.
/// </summary>
internal static class BetaHiFiAudioProcessor
{
    private const int Rate = 48_000;
    private const int TickFrames = 48;
    private const double Pcm24Scale = 8_388_608.0;
    private const double EqInitialState = 1.9260771186235819;
    private const double NotchInitialState = 0.002933411243631223;

    // SciPy coefficients of the immutable preview-16 profiles. The oracle fixture
    // records both original profile hashes and the independent coefficient designs.
    private static readonly Coefficients Eq = new(
        0.09835496353257453, 0.0722833497364509, 0, -0.9157105270297415, 0);
    private static readonly Coefficients Notch = new(
        0.9970665887563688, 0.9360736023894386, 0.9970665887563688,
        0.9360736023894386, 0.9941331775127376);

    /// <summary>
    /// Consumes exactly <paramref name="frames"/> interleaved little-endian
    /// float32 frames from the current stream position, leaving the stream open.
    /// Two owned temporary files keep the whole-file zero-phase notch bounded in
    /// memory. Emission is ordered and synchronous, after all static filtering.
    /// User output gain, mode conversion and normalization belong after this API.
    /// </summary>
    internal static void ProcessFile(
        Stream interleavedFloat32Input,
        long frames,
        Action<HiFiPostProcessedBlock> emit,
        bool enableDeemphasis = true,
        bool enableExpander = true,
        double referenceOffsetDb = 0,
        CancellationToken token = default,
        int outputBlockFrames = 4800)
    {
        ArgumentNullException.ThrowIfNull(interleavedFloat32Input);
        ArgumentNullException.ThrowIfNull(emit);
        if (!interleavedFloat32Input.CanRead)
        {
            throw new ArgumentException("The Beta audio input must be readable.", nameof(interleavedFloat32Input));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (frames > (long.MaxValue / 16) - 18)
        {
            throw new ArgumentOutOfRangeException(nameof(frames));
        }

        if (outputBlockFrames is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBlockFrames));
        }

        if (!double.IsFinite(referenceOffsetDb) || Math.Abs(referenceOffsetDb) > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceOffsetDb), "The reference offset must be finite and within +/-120 dB.");
        }

        token.ThrowIfCancellationRequested();
        if (frames == 0)
        {
            return;
        }

        var samples = new double[outputBlockFrames * 2];
        var bytes = new byte[outputBlockFrames * 16];
        using FileStream stage = CreateTemporaryFile();
        PrepareInput(interleavedFloat32Input, stage, frames, enableDeemphasis, samples, bytes, token);
        if (enableDeemphasis)
        {
            using FileStream forward = CreateTemporaryFile();
            ApplyZeroPhaseNotch(stage, forward, frames, samples, bytes, token);
        }

        stage.Position = 0;
        var dynamics = enableExpander ? new Dynamics(referenceOffsetDb) : null;
        for (long done = 0; done < frames;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(outputBlockFrames, frames - done);
            ReadDoubles(stage, samples, count, bytes);
            var left = new float[count];
            var right = new float[count];
            var stereo = new float[count * 2];
            float leftPeak = 0, rightPeak = 0;
            for (int i = 0; i < count; i++)
            {
                double l = samples[i * 2], r = samples[(i * 2) + 1];
                dynamics?.Process(ref l, ref r);
                float lf = (float)l, rf = (float)r;
                if (!float.IsFinite(lf) || !float.IsFinite(rf))
                {
                    throw new InvalidDataException("Beta audio processing produced a nonfinite sample.");
                }

                left[i] = stereo[i * 2] = lf;
                right[i] = stereo[(i * 2) + 1] = rf;
                leftPeak = MathF.Max(leftPeak, MathF.Abs(lf));
                rightPeak = MathF.Max(rightPeak, MathF.Abs(rf));
            }

            emit(new HiFiPostProcessedBlock(left, right, stereo, leftPeak, rightPeak));
            done += count;
        }
    }

    private static FileStream CreateTemporaryFile()
        => new(Path.Combine(Path.GetTempPath(), $"vhsdecode-beta-{Guid.NewGuid():N}.tmp"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose | FileOptions.RandomAccess,
                BufferSize = 65_536
            });

    private static void PrepareInput(
        Stream input, FileStream stage, long frames, bool equalize,
        double[] values, byte[] bytes, CancellationToken token)
    {
        var eq = new StereoFilter(Eq);
        bool initial = true;
        int capacity = values.Length / 2;
        for (long done = 0; done < frames;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(capacity, frames - done);
            input.ReadExactly(bytes.AsSpan(0, count * 8));
            for (int i = 0; i < count; i++)
            {
                double left = QuantizePcm24(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 8, 4)));
                double right = QuantizePcm24(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((i * 8) + 4, 4)));
                if (equalize)
                {
                    if (initial)
                    {
                        eq.Initialize(EqInitialState, 0, left, right);
                    }

                    eq.Process(ref left, ref right);
                    left = QuantizePcm24(left);
                    right = QuantizePcm24(right);
                }

                initial = false;
                values[i * 2] = left;
                values[(i * 2) + 1] = right;
            }

            WriteDoubles(stage, values.AsSpan(0, count * 2), bytes);
            done += count;
        }

        stage.Flush();
    }

    private static double QuantizePcm24(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) >= 1)
        {
            throw new InvalidDataException("The calibrated Beta audio interstage is nonfinite or would clip PCM24.");
        }

        // libsndfile rounds to signed32 and stores the high 24 bits. The shift
        // floors negative values, too; truncation toward zero changes the oracle.
        long pcm32 = (long)Math.Clamp(Math.Round(value * 2_147_483_648.0, MidpointRounding.ToEven),
            int.MinValue, int.MaxValue);
        return (pcm32 >> 8) / Pcm24Scale;
    }

    private static void ApplyZeroPhaseNotch(
        FileStream stage, FileStream forward, long frames,
        double[] input, byte[] bytes, CancellationToken token)
    {
        // SciPy filtfilt's default padlen is 3*max(len(a),len(b)) = 9. For
        // shorter files use all available samples; a one-frame file has pad 0.
        int pad = (int)Math.Min(9, frames - 1);
        var first = new double[(pad + 1) * 2];
        var last = new double[first.Length];
        var edgeBytes = new byte[first.Length * 8];
        stage.Position = 0;
        ReadDoubles(stage, first, pad + 1, edgeBytes);
        stage.Position = (frames - pad - 1) * 16;
        ReadDoubles(stage, last, pad + 1, edgeBytes);
        stage.Position = 0;
        var output = new double[input.Length];
        var filter = new StereoFilter(Notch);
        int buffered = 0;
        bool initial = true;

        void ForwardFrame(double l, double r)
        {
            if (initial)
            {
                filter.Initialize(NotchInitialState, NotchInitialState, l, r);
                initial = false;
            }

            filter.Process(ref l, ref r);
            output[buffered * 2] = l;
            output[(buffered * 2) + 1] = r;
            buffered++;
            if (buffered == output.Length / 2)
            {
                WriteDoubles(forward, output, bytes);
                buffered = 0;
            }
        }

        for (int i = 0; i < pad; i++)
        {
            ForwardFrame((2 * first[0]) - first[(pad - i) * 2],
                (2 * first[1]) - first[((pad - i) * 2) + 1]);
        }

        for (long done = 0; done < frames;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(input.Length / 2, frames - done);
            ReadDoubles(stage, input, count, bytes);
            for (int i = 0; i < count; i++)
            {
                ForwardFrame(input[i * 2], input[(i * 2) + 1]);
            }

            done += count;
        }

        for (int i = 0; i < pad; i++)
        {
            ForwardFrame((2 * last[pad * 2]) - last[(pad - i - 1) * 2],
                (2 * last[(pad * 2) + 1]) - last[((pad - i - 1) * 2) + 1]);
        }

        if (buffered > 0)
        {
            WriteDoubles(forward, output.AsSpan(0, buffered * 2), bytes);
        }

        forward.Flush();
        long paddedFrames = frames + (pad * 2L);
        forward.Position = (paddedFrames - 1) * 16;
        ReadDoubles(forward, first, 1, edgeBytes);
        filter = new StereoFilter(Notch);
        filter.Initialize(NotchInitialState, NotchInitialState, first[0], first[1]);
        // The original static stage is no longer needed. Write reversed-filter
        // results back at their chronological offsets, then emit in order later.
        for (long end = paddedFrames; end > 0;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(input.Length / 2, end);
            long start = end - count;
            forward.Position = start * 16;
            ReadDoubles(forward, input, count, bytes);
            for (int i = count - 1; i >= 0; i--)
            {
                double l = input[i * 2], r = input[(i * 2) + 1];
                filter.Process(ref l, ref r);
                input[i * 2] = l;
                input[(i * 2) + 1] = r;
            }

            long keepStart = Math.Max(start, pad);
            long keepEnd = Math.Min(end, pad + frames);
            if (keepEnd > keepStart)
            {
                stage.Position = (keepStart - pad) * 16;
                WriteDoubles(stage, input.AsSpan((int)(keepStart - start) * 2, (int)(keepEnd - keepStart) * 2), bytes);
            }

            end = start;
        }

        stage.Flush();
    }

    private static void ReadDoubles(Stream stream, double[] values, int frames, byte[] bytes)
    {
        stream.ReadExactly(bytes.AsSpan(0, frames * 16));
        for (int i = 0; i < frames * 2; i++)
        {
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * 8, 8));
        }
    }

    private static void WriteDoubles(Stream stream, ReadOnlySpan<double> values, byte[] bytes)
    {
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
        }

        stream.Write(bytes.AsSpan(0, values.Length * 8));
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2);

    private sealed class StereoFilter(Coefficients c)
    {
        private double _left1, _left2, _right1, _right2;

        internal void Initialize(double z1, double z2, double left, double right)
        {
            _left1 = z1 * left;
            _left2 = z2 * left;
            _right1 = z1 * right;
            _right2 = z2 * right;
        }

        internal void Process(ref double left, ref double right)
        {
            double l = (c.B0 * left) + _left1;
            double r = (c.B0 * right) + _right1;
            _left1 = (c.B1 * left) - (c.A1 * l) + _left2;
            _right1 = (c.B1 * right) - (c.A1 * r) + _right2;
            _left2 = (c.B2 * left) - (c.A2 * l);
            _right2 = (c.B2 * right) - (c.A2 * r);
            left = l;
            right = r;
        }
    }

    private sealed class Dynamics
    {
        private const double Reference = -22.4264;
        private const double TopReference = -55.54927441601673;
        private const double Trim = -2.530979500375161;
        private const double WideSlope = 0.6434244433339258;
        private const double ShelfIntercept = 2.0820134934911185;
        private const double ShelfSlope = 0.04346560836625418;
        private const double BalanceSlope = 0.18803176557402168;
        private const double TopSlope = 0.26618843764941263;
        private static readonly double HighWeight = Math.Pow(10, 1.8);
        private readonly double _reference;
        private readonly double _topReference;
        private readonly StereoFilter[] _high =
        [
            new(new(0.1866943331163784, 0.3733886662327568, 0.1866943331163784, -0.16292179235145482, 0.32719796013797375)),
            new(new(1, -2, 1, -1.482760782667161, 0.6409433532780014))
        ];
        private readonly StereoFilter[] _fallback =
        [
            new(new(0.4136239424029401, 0.8272478848058802, 0.4136239424029401, 0.45893731156438367, 0.21104050922719947)),
            new(new(1, -2, 1, -1.9814884366082797, 0.9816596948720536))
        ];
        private readonly StereoFilter[] _top =
        [
            new(new(0.07223087532575317, -0.14446175065150635, 0.07223087532575317, -0.28296446826868, 0.626020355471084)),
            new(new(1, 2, 1, 0.49771451813574963, 0.6360053478162506))
        ];
        private readonly StereoFilter _low = new(new(0.061511768503621556, 0.061511768503621556, 0, -0.8769764629927568, 0));
        private readonly StereoFilter _topLow = new(new(0.4341737512063021, 0.4341737512063021, 0, -0.13165249758739583, 0));
        private readonly StereoFilter _upper = new(new(0.7158873405487501, -0.7984346476182613, 0.286893473882612, -1.2667540248817235, 0.47110019169482453));
        private double _widePower, _highPower, _fallbackPower, _topPower;
        private double _wideSum, _highSum, _fallbackSum, _topSum;
        private double _gain = Trim, _shelf, _topGain;
        private double _startGain = Trim, _startShelf, _startTop;
        private int _inTick;

        internal Dynamics(double referenceOffsetDb)
        {
            _reference = Reference + referenceOffsetDb;
            _topReference = TopReference + referenceOffsetDb;
            _widePower = Math.Pow(10, _reference / 10);
            _highPower = Math.Pow(10, (_reference - 18) / 10);
            _fallbackPower = Math.Pow(10, (_reference - 18 + 10) / 10);
            _topPower = Math.Pow(10, _topReference / 10);
        }

        internal void Process(ref double left, ref double right)
        {
            double hl = left, hr = right, fl = left, fr = right, tl = left, tr = right;
            foreach (StereoFilter section in _high) section.Process(ref hl, ref hr);
            foreach (StereoFilter section in _fallback) section.Process(ref fl, ref fr);
            foreach (StereoFilter section in _top) section.Process(ref tl, ref tr);
            double hp = ((hl * hl) + (hr * hr)) / 2;
            double fp = ((fl * fl) + (fr * fr)) / 2;
            double tp = ((tl * tl) + (tr * tr)) / 2;
            _wideSum += fp + (HighWeight * hp);
            _highSum += hp;
            _fallbackSum += fp;
            _topSum += tp;
            double ll = left, lr = right;
            _low.Process(ref ll, ref lr);
            double fraction = (_inTick + 1.0) / TickFrames;
            double gain = Math.Pow(10, (_startGain + (fraction * (_gain - _startGain))) / 20);
            double shelf = Math.Pow(10, (_startShelf + (fraction * (_shelf - _startShelf))) / 20);
            double top = Math.Pow(10, (_startTop + (fraction * (_topGain - _startTop))) / 20);
            left = (left + ((shelf - 1) * (left - ll))) * gain;
            right = (right + ((shelf - 1) * (right - lr))) * gain;
            _upper.Process(ref left, ref right);
            ll = left;
            lr = right;
            _topLow.Process(ref ll, ref lr);
            left += (top - 1) * (left - ll);
            right += (top - 1) * (right - lr);
            if (++_inTick == TickFrames)
            {
                AdvanceTick();
            }
        }

        private void AdvanceTick()
        {
            _widePower = Envelope(_widePower, _wideSum / TickFrames, 5, 20);
            _highPower = Envelope(_highPower, _highSum / TickFrames, 5, 35);
            _fallbackPower = Envelope(_fallbackPower, _fallbackSum / TickFrames, 5, 35);
            _topPower = Envelope(_topPower, _topSum / TickFrames, 5, 35);
            double level = Db(_widePower), high = Db(_highPower), fallback = Db(_fallbackPower);
            double wide = Trim + Math.Clamp((level - _reference) * WideSlope, -12, 6);
            double balance = high - fallback + 10;
            double shelf = Math.Clamp(ShelfIntercept + (ShelfSlope * (level - _reference)) + (BalanceSlope * balance), -6, 3);
            double top = Math.Clamp(TopSlope * (Db(_topPower) - _topReference), -1.5, 1.5);
            _startGain = _gain;
            _startShelf = _shelf;
            _startTop = _topGain;
            _gain = Smooth(_gain, wide, 3, 50);
            _shelf = Smooth(_shelf, shelf, 3, 50);
            _topGain = Smooth(_topGain, top, 3, 50);
            _wideSum = _highSum = _fallbackSum = _topSum = 0;
            _inTick = 0;
        }

        private static double Envelope(double previous, double power, double attack, double release)
        {
            double a = Math.Exp(-1 / (power > previous ? attack : release));
            return (a * previous) + ((1 - a) * power);
        }

        private static double Smooth(double previous, double target, double opening, double closing)
        {
            double a = Math.Exp(-1 / (target > previous ? opening : closing));
            return (a * previous) + ((1 - a) * target);
        }

        private static double Db(double power) => 10 * Math.Log10(Math.Max(power, 1e-30));
    }
}
