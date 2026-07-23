using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppRfDemodulatorDifferentialTests
{
    private const int Length = 32_768;
    private const double SampleRateHz = 40_000_000.0;

    [Fact(DisplayName = "IPP fast 32768-point VHS real RF path remains numerically close to exact")]
    public void IppFastVhsRealRfPathRemainsNumericallyCloseToExact()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildPalVhsProbe();
        Complex[] identity = RfDemodulator.IdentityFilter(Length);
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];

        using var exactDemodulator = new RfDemodulator(SampleRateHz, DspBackend.Exact);
        using var ippDemodulator = new RfDemodulator(SampleRateHz, DspBackend.IppFast);
        RfDemodulatedBlock exact = Decode(
            exactDemodulator,
            input,
            identity,
            identitySos);
        RfDemodulatedBlock ipp = Decode(
            ippDemodulator,
            input,
            identity,
            identitySos);

        Assert.Equal(exact.VhsWeakRfSignal, ipp.VhsWeakRfSignal);
        Assert.Null(exact.Chroma);
        Assert.Null(ipp.Chroma);
        Assert.Null(exact.VideoBurst);
        Assert.Null(ipp.VideoBurst);
        Assert.Null(exact.VideoPilot);
        Assert.Null(ipp.VideoPilot);
        Assert.Null(exact.Efm);
        Assert.Null(ipp.Efm);
        Assert.Null(exact.AnalogAudio);
        Assert.Null(ipp.AnalogAudio);

        DiffMetrics[] metrics =
        [
            Measure("Video", exact.Video, ipp.Video),
            Measure("DemodRaw", exact.DemodRaw, ipp.DemodRaw),
            Measure("Analytic", exact.Analytic, ipp.Analytic),
            Measure("Envelope", exact.Envelope, ipp.Envelope),
            Measure("VideoLowPass", exact.VideoLowPass, ipp.VideoLowPass),
            Measure("RfHighPass", exact.RfHighPass, ipp.RfHighPass)
        ];

        Assert.All(metrics, metric => Assert.Equal(Length, metric.Length));
        Assert.All(metrics, metric => Assert.True(metric.AllFinite, metric.ToString()));

        const double MaximumScaleTolerance = 1e-12;
        const double RmsScaleTolerance = 1e-12;
        bool withinTolerance = metrics.All(metric =>
            metric.MaximumAbsoluteDelta
                <= MaximumScaleTolerance * Math.Max(1.0, metric.MaximumReferenceMagnitude)
            && metric.RmsDelta
                <= RmsScaleTolerance * Math.Max(1.0, metric.ReferenceRms));
        Assert.True(
            withinTolerance,
            string.Join(Environment.NewLine, metrics.Select(metric => metric.ToString()))
                + Environment.NewLine
                + $"exactBlockSha256={Hash(exact)}, ippBlockSha256={Hash(ipp)}");

        Assert.Equal(
            "2CE4D907DD94BE3ACB7A0412EB4921CD869DE4E56C751BB0AD498CF0028D0834",
            Hash(exact));
    }

    private static RfDemodulatedBlock Decode(
        RfDemodulator demodulator,
        double[] input,
        Complex[] identity,
        SosSection[] identitySos)
        => demodulator.Demodulate(
            input,
            identity,
            identity,
            ReadOnlySpan<Complex>.Empty,
            identity,
            identity,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation,
            vhsEnvelopeFilter: identitySos);

    private static double[] BuildPalVhsProbe()
    {
        var input = new double[Length];
        double phase = 0.0;
        for (int index = 0; index < input.Length; index++)
        {
            double linePhase = (index % 2560) / 2560.0;
            double video = (0.45 * Math.Sin(Math.Tau * linePhase))
                + (linePhase < 0.075 ? -0.75 : 0.0);
            double frequencyHz = 3_800_000.0 + (650_000.0 * video);
            phase += Math.Tau * frequencyHz / SampleRateHz;
            input[index] = (12_000.0 * Math.Cos(phase))
                + (1_300.0 * Math.Cos(Math.Tau * 627_000.0 * index / SampleRateHz));
        }

        return input;
    }

    private static DiffMetrics Measure(
        string name,
        ReadOnlySpan<double> exact,
        ReadOnlySpan<double> ipp)
    {
        Assert.Equal(exact.Length, ipp.Length);
        double maximumAbsoluteDelta = 0.0;
        ulong maximumUlpDelta = 0;
        double maximumReferenceMagnitude = 0.0;
        double deltaSquares = 0.0;
        double referenceSquares = 0.0;
        bool allFinite = true;
        for (int index = 0; index < exact.Length; index++)
        {
            allFinite &= double.IsFinite(exact[index]) && double.IsFinite(ipp[index]);
            double delta = ipp[index] - exact[index];
            maximumAbsoluteDelta = Math.Max(maximumAbsoluteDelta, Math.Abs(delta));
            maximumUlpDelta = Math.Max(maximumUlpDelta, UlpDistance(exact[index], ipp[index]));
            maximumReferenceMagnitude = Math.Max(maximumReferenceMagnitude, Math.Abs(exact[index]));
            deltaSquares += delta * delta;
            referenceSquares += exact[index] * exact[index];
        }

        return new DiffMetrics(
            name,
            exact.Length,
            allFinite,
            maximumAbsoluteDelta,
            maximumUlpDelta,
            Math.Sqrt(deltaSquares / exact.Length),
            maximumReferenceMagnitude,
            Math.Sqrt(referenceSquares / exact.Length),
            Hash(exact),
            Hash(ipp));
    }

    private static DiffMetrics Measure(
        string name,
        ReadOnlySpan<Complex> exact,
        ReadOnlySpan<Complex> ipp)
    {
        Assert.Equal(exact.Length, ipp.Length);
        double maximumAbsoluteDelta = 0.0;
        ulong maximumUlpDelta = 0;
        double maximumReferenceMagnitude = 0.0;
        double deltaSquares = 0.0;
        double referenceSquares = 0.0;
        bool allFinite = true;
        for (int index = 0; index < exact.Length; index++)
        {
            allFinite &= double.IsFinite(exact[index].Real)
                && double.IsFinite(exact[index].Imaginary)
                && double.IsFinite(ipp[index].Real)
                && double.IsFinite(ipp[index].Imaginary);
            double delta = Complex.Abs(ipp[index] - exact[index]);
            double reference = exact[index].Magnitude;
            maximumAbsoluteDelta = Math.Max(maximumAbsoluteDelta, delta);
            maximumUlpDelta = Math.Max(
                maximumUlpDelta,
                Math.Max(
                    UlpDistance(exact[index].Real, ipp[index].Real),
                    UlpDistance(exact[index].Imaginary, ipp[index].Imaginary)));
            maximumReferenceMagnitude = Math.Max(maximumReferenceMagnitude, reference);
            deltaSquares += delta * delta;
            referenceSquares += reference * reference;
        }

        return new DiffMetrics(
            name,
            exact.Length,
            allFinite,
            maximumAbsoluteDelta,
            maximumUlpDelta,
            Math.Sqrt(deltaSquares / exact.Length),
            maximumReferenceMagnitude,
            Math.Sqrt(referenceSquares / exact.Length),
            Hash(exact),
            Hash(ipp));
    }

    private static string Hash(RfDemodulatedBlock block)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, block.Video);
        Append(hash, block.DemodRaw);
        Append(hash, block.Analytic);
        Append(hash, block.Envelope);
        Append(hash, block.VideoLowPass);
        Append(hash, block.RfHighPass);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(ReadOnlySpan<double> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string Hash(ReadOnlySpan<Complex> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static void Append(IncrementalHash hash, ReadOnlySpan<double> values)
        => hash.AppendData(MemoryMarshal.AsBytes(values));

    private static void Append(IncrementalHash hash, ReadOnlySpan<Complex> values)
        => hash.AppendData(MemoryMarshal.AsBytes(values));

    private static ulong UlpDistance(double left, double right)
    {
        ulong leftOrdered = ToOrderedBits(left);
        ulong rightOrdered = ToOrderedBits(right);
        return leftOrdered >= rightOrdered
            ? leftOrdered - rightOrdered
            : rightOrdered - leftOrdered;
    }

    private static ulong ToOrderedBits(double value)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        const ulong SignBit = 1UL << 63;
        return (bits & SignBit) != 0
            ? (~bits) + 1UL
            : bits | SignBit;
    }

    private sealed record DiffMetrics(
        string Name,
        int Length,
        bool AllFinite,
        double MaximumAbsoluteDelta,
        ulong MaximumUlpDelta,
        double RmsDelta,
        double MaximumReferenceMagnitude,
        double ReferenceRms,
        string ExactSha256,
        string IppSha256)
    {
        public override string ToString()
            => $"{Name}: len={Length}, finite={AllFinite}, maxDelta={MaximumAbsoluteDelta:R}, "
                + $"maxUlp={MaximumUlpDelta}, rmsDelta={RmsDelta:R}, maxRef={MaximumReferenceMagnitude:R}, "
                + $"rmsRef={ReferenceRms:R}, exactSha256={ExactSha256}, ippSha256={IppSha256}";
    }
}
