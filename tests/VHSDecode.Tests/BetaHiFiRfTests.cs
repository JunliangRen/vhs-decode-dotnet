using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class BetaHiFiRfTests
{
    [Theory]
    [InlineData(20_000_000, 5_000_624, 12001)]
    [InlineData(20_000_000, 5_000_625, 12002)]
    [InlineData(20_000_000, 5_000_626, 12002)]
    [InlineData(20_000_000, 5_001_875, 12004)]
    [InlineData(40_000_000, 10_001_249, 12001)]
    [InlineData(40_000_000, 10_001_250, 12002)]
    [InlineData(40_000_000, 10_001_251, 12002)]
    [InlineData(40_000_000, 10_003_750, 12004)]
    public void AudioClockRoundsExactHalfFramesToEven(int rate, long samples, long expected)
        => Assert.Equal(expected, BetaHiFiRfDecoder.CalculateAudioFrames(samples, rate));

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void AudioClockTelescopesAcrossBlocksAndSupportsLargeSampleCounts(int rate)
    {
        long[] counts = [rate * 2L, rate * 2L, rate * 2L, rate / 4L + rate / 32000L];
        long start = 0, frames = 0;
        foreach (long count in counts)
        {
            frames += BetaHiFiRfDecoder.CalculateAudioFrames(start + count, rate)
                - BetaHiFiRfDecoder.CalculateAudioFrames(start, rate);
            start += count;
        }

        Assert.Equal(300002, frames);
        // BigInteger is an independent exact oracle beyond double integer range.
        long samples = long.MaxValue;
        System.Numerics.BigInteger numerator = new System.Numerics.BigInteger(samples) * 48_000;
        System.Numerics.BigInteger quotient = System.Numerics.BigInteger.DivRem(numerator, rate, out System.Numerics.BigInteger remainder);
        if (remainder * 2 > rate || (remainder * 2 == rate && !quotient.IsEven)) quotient++;
        Assert.Equal((long)quotient, BetaHiFiRfDecoder.CalculateAudioFrames(samples, rate));
        Assert.Equal(0, BetaHiFiRfDecoder.CalculateAudioFrames(0, rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiRfDecoder.CalculateAudioFrames(-1, rate));
    }

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void EverySubIqEofTailIsAcceptedWithoutInventingAudioFrames(int rate)
    {
        var decoder = new BetaHiFiRfDecoder(rate);
        int rfSamplesPerIq = rate / BetaHiFiRfDecoder.IqRate;
        BetaHiFiIqBlock context = AnchorTestIq(2 * BetaHiFiRfDecoder.ContextIqSamples);
        long emitted = rate * 8L;
        long before = BetaHiFiRfDecoder.CalculateAudioFrames(emitted, rate);

        for (int tail = 1; tail <= rfSamplesPerIq; tail++)
        {
            BetaHiFiIqBlock block = context with { RfSamples = 2 * decoder.ContextRfSamples + tail };
            HiFiDecodedBlock decoded = decoder.Decode(block, AnchorTestCalibration(), tail, emitted);

            Assert.Empty(decoded.Left);
            Assert.Empty(decoded.Right);
            Assert.Equal(before, BetaHiFiRfDecoder.CalculateAudioFrames(emitted + tail, rate));
        }
    }

    [Theory]
    [InlineData(20_000_000, 1)]
    [InlineData(20_000_000, 41)]
    [InlineData(40_000_000, 1)]
    [InlineData(40_000_000, 81)]
    public void ActualRfAnalysisRetainsLegalSubIqEofGeometry(int rate, int tail)
    {
        var decoder = new BetaHiFiRfDecoder(rate, threads: 2);
        var rf = new float[rate / 2 + tail];
        BetaHiFiIqBlock block = decoder.Analyze(rf);
        int expectedIqLength = (int)Math.Ceiling(rf.Length / (rate / 500_000.0)) - 1;

        Assert.Equal(expectedIqLength, block.Length);
        HiFiDecodedBlock decoded = decoder.Decode(block, AnchorTestCalibration(), tail, rate * 2L);
        Assert.Empty(decoded.Left);
        Assert.Empty(decoded.Right);
    }

    [Theory]
    [InlineData(20_000_000, 208, 0)]
    [InlineData(20_000_000, 209, 1)]
    [InlineData(20_000_000, 624, 1)]
    [InlineData(20_000_000, 625, 2)]
    [InlineData(20_000_000, 626, 2)]
    [InlineData(20_000_000, 1875, 4)]
    [InlineData(40_000_000, 416, 0)]
    [InlineData(40_000_000, 417, 1)]
    [InlineData(40_000_000, 1249, 1)]
    [InlineData(40_000_000, 1250, 2)]
    [InlineData(40_000_000, 1251, 2)]
    [InlineData(40_000_000, 3750, 4)]
    public void EofDecodeUsesExactAudioClockAtFirstFrameAndHalfFrames(int rate, int tail, int expectedFrames)
    {
        var decoder = new BetaHiFiRfDecoder(rate);
        int rfLength = rate / 2 + tail;
        int iqLength = (int)Math.Ceiling(rfLength / (rate / 500_000.0)) - 1;
        BetaHiFiIqBlock block = AnchorTestIq(iqLength) with { RfSamples = rfLength };
        long emitted = rate * 8L;

        HiFiDecodedBlock decoded = decoder.Decode(block, AnchorTestCalibration(), tail, emitted);

        Assert.Equal(expectedFrames, decoded.Left.Length);
        Assert.Equal(expectedFrames, decoded.Right.Length);
        Assert.All(decoded.Left, sample => Assert.Equal(0, sample));
        Assert.All(decoded.Right, sample => Assert.Equal(0, sample));
        Assert.Equal(384_000 + expectedFrames, BetaHiFiRfDecoder.CalculateAudioFrames(emitted + tail, rate));
    }

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void EofTailStillRejectsMismatchedDiscriminatorGeometry(int rate)
    {
        var decoder = new BetaHiFiRfDecoder(rate);
        int tail = rate / BetaHiFiRfDecoder.IqRate + 1;
        BetaHiFiIqBlock block = AnchorTestIq(2 * BetaHiFiRfDecoder.ContextIqSamples)
            with { RfSamples = rate / 2 + tail };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            decoder.Decode(block, AnchorTestCalibration(), tail, rate * 2L));
    }

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void FourCarrierFrontEndMatchesIndependentScipyToneOracle(int rate)
    {
        // Independent SciPy 1.18 float32 resample_poly/complex64 discriminator.
        // The two programme offsets differ; neither may be auto-cancelled as DC.
        var decoder = new BetaHiFiRfDecoder(rate, threads: 2);
        double[] carriers = BetaHiFiRfDecoder.CarrierFrequenciesHz;
        var rf = new float[rate / 2 + rate / 500];
        for (int i = 0; i < rf.Length; i++)
        {
            double t = i / (double)rate;
            rf[i] = (float)(.1 * Math.Cos(2 * Math.PI * (carriers[0] + 1200) * t)
                + .08 * Math.Cos(2 * Math.PI * (carriers[2] - 2300) * t));
        }

        BetaHiFiIqBlock actual = decoder.Analyze(rf);
        int[] indices = [125000, 125111, 125999];
        double[][] expectedFm = rate == 20_000_000
            ? [[1200.078369140625, 1199.9552001953125, 1199.9183349609375], [-2300.383056640625, -2299.74072265625, -2299.697021484375]]
            : [[1200.0743408203125, 1199.943359375, 1199.9393310546875], [-2300.3779296875, -2299.7451171875, -2299.733154296875]];
        double[][] expectedPower = rate == 20_000_000
            ? [[.002499801805242896, .0024998055305331945, .0024998022709041834], [.0016054685693234205, .0016054834704846144, .0016054700827226043]]
            : [[.0024999321904033422, .0024999382440000772, .002499933820217848], [.0016056241001933813, .0016056372551247478, .0016056210733950138]];
        for (int channel = 0; channel < 2; channel++)
        {
            for (int i = 0; i < indices.Length; i++)
            {
                Assert.InRange(Math.Abs(actual.FmHz[channel * 2][indices[i]] - expectedFm[channel][i]), 0, .2);
                Assert.InRange(Math.Abs(actual.InstantPower[channel * 2][indices[i]] - expectedPower[channel][i]), 0, 5e-9);
            }
        }
    }

    [Fact]
    public void CenteredDecimationMatchesIndependentScipyBoundaryOracle()
    {
        // scipy.signal.resample_poly, SciPy 1.18, float32, default Kaiser(5).
        float[] input = [0, 1, -2, 3, .5f, -.7f, .2f, 2, -1, .3f, .8f, -.9f, 1.2f, .4f, -.1f, .6f, .7f];
        float[] expected = [.0939185321f, .5698256493f, .2169359773f, .3181685209f, .3344020844f];
        float[] taps = BetaHiFiSignalDsp.FirWin(81, .25, 5).Select(x => (float)x).ToArray();
        float[] actual = BetaHiFiSignalDsp.Decimate(input, 4, taps);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 2e-7);
        }
    }

    [Fact]
    public void AudioRationalResamplingMatchesIndependentScipyBoundaryOracle()
    {
        double[] input = [0, 1, -2, 3, .5, -.7, .2, 2, -1, .3, .8, -.9, 1.2, .4, -.1, .6, .7];
        double[] expected = [.18606545218020945, .37212384152282774];
        double[] actual = BetaHiFiSignalDsp.Resample(input, 12, 125);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 2e-14);
        }
    }

    [Fact]
    public void MissingAfmIsRejectedRatherThanAcceptedAsAudio()
    {
        var decoder = new BetaHiFiRfDecoder(40_000_000);
        BetaHiFiIqBlock block = SyntheticIq(alternating: false);
        Assert.Throws<InvalidDataException>(() => decoder.Calibrate([block], [10_000_000]));
    }

    [Fact]
    public void CalibrationUsesCapturePowerAndPreservesProgrammeDc()
    {
        var decoder = new BetaHiFiRfDecoder(40_000_000);
        BetaHiFiIqBlock block = SyntheticIq(alternating: true);
        BetaHiFiRfCalibration calibration = decoder.Calibrate([block], [10_000_000]);
        Assert.InRange(calibration.CalibrationSwitchCount, 14, 16);
        Assert.True(calibration.TimingAnchorUsable);
        Assert.Equal<double>([1, 2, 3, 4], calibration.CarrierReferencePower);
        Assert.Equal(0, calibration.AToBGap.Left);
        Assert.Equal(0, calibration.AToBGap.Right);
        bool[] heads = BetaHiFiRfDecoder.HeadsFromPower(block, calibration);
        (double[] left, double[] right) = decoder.SelectAndRepair(block, heads, calibration);
        Assert.All(left, x => Assert.Equal(1234, x));
        Assert.All(right, x => Assert.Equal(-2345, x));
    }

    [Fact]
    public void InvalidRepairUsesReliableAnchorsAndMutesLongOrBoundaryLoss()
    {
        double[] audio = Enumerable.Range(0, 160).Select(x => (double)x).ToArray();
        bool[] invalid = new bool[audio.Length];
        invalid[0] = true;
        invalid[159] = true;
        invalid[20] = true;
        Array.Fill(invalid, true, 50, 60);
        audio[20] = double.NaN;
        BetaHiFiRfDecoder.RepairInvalid(audio, invalid);
        Assert.Equal(0, audio[0]);
        Assert.Equal(0, audio[159]);
        Assert.Equal(20, audio[20]);
        Assert.All(audio.AsSpan(75, 10).ToArray(), x => Assert.Equal(0, x));
        Assert.Equal(49, audio[49]);
        Assert.Equal(110, audio[110]);
        Assert.All(audio, x => Assert.True(double.IsFinite(x)));
    }

    [Theory]
    [InlineData("full", 1234)]
    [InlineData("mute", 0)]
    [InlineData("off", 300000)]
    public void DropoutModesDistinguishShortInterpolationMuteAndUnmodifiedSamples(string mode, double expected)
    {
        var decoder = new BetaHiFiRfDecoder(40_000_000, dropoutCompensation: mode != "off", muteDropouts: mode == "mute");
        BetaHiFiIqBlock block = SyntheticIq(alternating: false);
        bool[] heads = Enumerable.Repeat(true, block.Length).ToArray();
        var calibration = new BetaHiFiRfCalibration(0, -40, 40, [1, 2, 3, 4], "A", -20,
            false, default, default, 0, 1);
        Array.Fill(block.FmHz[0], 300000, 200, 3);

        (double[] left, double[] right) = decoder.SelectAndRepair(block, heads, calibration);

        Assert.Equal(expected, left[201]);
        Assert.Equal(1234, left[199]);
        Assert.Equal(1234, left[203]);
        Assert.Equal(-2345, right[201]);
    }

    [Fact]
    public void LearnedGapUsesOnlyRepeatedHeadLockedGlitches()
    {
        var left = new double[200_000];
        var right = new double[left.Length];
        bool[] heads = Enumerable.Range(0, left.Length).Select(i => (i / 8333 & 1) == 0).ToArray();
        for (int edge = 8333; edge < left.Length; edge += 8333)
        {
            left[edge] = 30_000;
            right[edge] = -40_000;
        }

        BetaHiFiGap gap = BetaHiFiRfDecoder.LearnGap(left, right, heads, incomingA: true);
        Assert.Equal(-5, gap.Left);
        Assert.Equal(6, gap.Right);
        Assert.True(gap.Events >= 4);
        Assert.Equal(new BetaHiFiGap(0, 0, 0), BetaHiFiRfDecoder.LearnGap(left, right, new bool[left.Length], true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WeakRfCorruptGapAnchorMovesOnlyToNearbyReliableSamples(bool corruptLeft)
    {
        var left = Enumerable.Repeat(1000.0, 256).ToArray();
        var right = Enumerable.Repeat(-500.0, 256).ToArray();
        bool[] heads = Enumerable.Repeat(true, left.Length).ToArray();
        BetaHiFiIqBlock block = AnchorTestIq(left.Length);
        int anchor = corruptLeft ? 99 : 120;
        left[anchor] = 50_000;
        int lowStart = corruptLeft ? 95 : 120;
        for (int branch = 0; branch < 4; branch += 2)
            Array.Fill(block.InstantPower[branch], .01f, lowStart, 5);

        (int start, int end) = BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 100, 120);

        Assert.Equal(corruptLeft ? 95 : 100, start);
        Assert.Equal(corruptLeft ? 120 : 125, end);
        Assert.InRange(end - start, 1, 80);
        Assert.Equal(50_000, left[anchor]); // Finding anchors never modifies audio.
    }

    [Fact]
    public void StrongRfTransientAndWeakRfSmoothProgrammeDoNotExpandGap()
    {
        var left = Enumerable.Repeat(1000.0, 256).ToArray();
        var right = Enumerable.Repeat(-500.0, 256).ToArray();
        bool[] heads = Enumerable.Repeat(true, left.Length).ToArray();
        BetaHiFiIqBlock block = AnchorTestIq(left.Length);
        left[99] = 50_000;
        Assert.Equal((100, 120), BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 100, 120));
        left[99] = 1000;
        for (int branch = 0; branch < 4; branch += 2)
            Array.Fill(block.InstantPower[branch], .01f, 90, 35);
        Assert.Equal((100, 120), BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 100, 120));
    }

    [Fact]
    public void GapInteriorGlitchCannotContaminateOutwardAnchorDerivative()
    {
        var left = Enumerable.Repeat(1000.0, 256).ToArray();
        var right = Enumerable.Repeat(-500.0, 256).ToArray();
        bool[] heads = Enumerable.Repeat(true, left.Length).ToArray();
        BetaHiFiIqBlock block = AnchorTestIq(left.Length);
        left[100] = 90_000;
        left[119] = -80_000;
        for (int branch = 0; branch < 4; branch += 2)
            Array.Fill(block.InstantPower[branch], .01f, 90, 35);
        Assert.Equal((100, 120), BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 100, 120));
    }

    [Fact]
    public void AnchorSearchCannotExceedMaximumConcealmentOrUseUnavailableContext()
    {
        var left = Enumerable.Repeat(1000.0, 256).ToArray();
        var right = Enumerable.Repeat(-500.0, 256).ToArray();
        bool[] heads = Enumerable.Repeat(true, left.Length).ToArray();
        BetaHiFiIqBlock block = AnchorTestIq(left.Length);
        left[99] = 50_000;
        for (int branch = 0; branch < 4; branch += 2)
            Array.Fill(block.InstantPower[branch], .01f, 90, 10);
        Assert.Equal((100, 175), BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 100, 175));
        Assert.Equal((1, 20), BetaHiFiRfDecoder.RefineGapAnchors(left, right, heads, block, AnchorTestCalibration(), 1, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CorruptOneChannelAnchorCannotExpandOtherChannelsRealHighFrequencyProgramme(int damagedChannel)
    {
        double[] damaged = Enumerable.Repeat(1000.0, 512).ToArray();
        double[] programme = Enumerable.Range(0, damaged.Length)
            .Select(i => 25_000 * Math.Sin(2 * Math.PI * 18_000 * i / 500_000)).ToArray();
        damaged[179] = 50_000;
        programme[179] = -60_000; // Strong-RF programme transient must not trigger expansion.
        double[] left = damagedChannel == 0 ? damaged : programme;
        double[] right = damagedChannel == 0 ? programme : damaged;
        double[] expectedLeft = left.ToArray();
        double[] expectedRight = right.ToArray();
        bool[] heads = Enumerable.Range(0, left.Length).Select(i => i >= 200).ToArray();
        BetaHiFiIqBlock block = AnchorTestIq(left.Length);
        Array.Fill(block.InstantPower[(damagedChannel * 2) + 1], .01f, 175, 5);
        BetaHiFiRfCalibration calibration = AnchorTestCalibration() with { BToAGap = new BetaHiFiGap(-20, 13, 20) };
        BetaHiFiRfDecoder.ApplyGaps(expectedLeft, expectedRight, heads, calibration);

        BetaHiFiRfDecoder.ApplyGaps(left, right, heads, calibration, block);

        Assert.Equal<double>(damagedChannel == 0 ? expectedRight : expectedLeft,
            damagedChannel == 0 ? right : left);
        Assert.NotEqual(50_000, (damagedChannel == 0 ? left : right)[179]);
        Assert.Equal(1000, (damagedChannel == 0 ? left : right)[174]);
    }

    private static BetaHiFiIqBlock AnchorTestIq(int length)
        => new(Enumerable.Range(0, 4).Select(_ => new float[length]).ToArray(),
            Enumerable.Range(0, 4).Select(_ => Enumerable.Repeat(1.0, length).ToArray()).ToArray(),
            Enumerable.Range(0, 4).Select(_ => Enumerable.Repeat(1f, length).ToArray()).ToArray(), 0);

    private static BetaHiFiRfCalibration AnchorTestCalibration()
        => new(0, -40, 40, [1, 1, 1, 1], "A", -20, false, default, default, 0, 1);

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void SupportedRatesUseTrueNtscFourCarrierDefinitions(double inputRate)
    {
        var decoder = new BetaHiFiRfDecoder(inputRate);
        Assert.Equal((int)inputRate / 4, decoder.ContextRfSamples);
        Assert.Equal((int)inputRate * 2, decoder.EmitRfSamples);
        Assert.Equal(1_380_681.8181818181, BetaHiFiRfDecoder.CarrierFrequenciesHz[0], 6);
        Assert.Equal(1_829_108.3916083917, BetaHiFiRfDecoder.CarrierFrequenciesHz[3], 6);
        Assert.Throws<ArgumentException>(() => decoder.Analyze([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40000)]
    [InlineData(5_000_000)]
    [InlineData(double.NaN)]
    public void UnsupportedOrContainerOnlyRatesAreRejected(double rate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BetaHiFiRfDecoder(rate));

    private static BetaHiFiIqBlock SyntheticIq(bool alternating)
    {
        // 30,000,000 RF / 80, then one sample consumed by phase difference.
        const int length = 374_999;
        float[][] frequency = Enumerable.Range(0, 4).Select(branch => Enumerable.Repeat(branch < 2 ? 1234f : -2345f, length).ToArray()).ToArray();
        double[][] power = Enumerable.Range(0, 4).Select(_ => new double[length]).ToArray();
        float[][] instant = Enumerable.Range(0, 4).Select(_ => new float[length]).ToArray();
        for (int branch = 0; branch < 4; branch++)
        {
            for (int i = 0; i < length; i++)
            {
                bool headA = (int)Math.Floor(i / 500_000.0 * (60_000.0 / 1001)) % 2 == 0;
                bool active = !alternating || headA == (branch is 0 or 2);
                power[branch][i] = (branch + 1) * (active ? 1 : .0001);
                instant[branch][i] = (float)power[branch][i];
            }
        }

        return new BetaHiFiIqBlock(frequency, power, instant, 30_000_000);
    }
}
