using System.Buffers.Binary;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaAutomaticGainCurrentTests
{
    private const string InputHash =
        "72C48D82DC902269AAEC31392A59BCDA7E426BA5EF0E8C4B9190CEFD72D3D46A";
    private const string GainedHash =
        "6B820ADA3A312090353A58C9C5F72D9B48FB290894756CAA0AD76FF2A0E16D5E";

    [Fact(DisplayName = "Current chroma ACC matches pinned PR 341 Numba output")]
    public void CurrentChromaAccMatchesPinnedNumbaOutput()
    {
        const int LineCount = 24;
        const int LineLength = 64;
        const int LineOffset = 3;
        double[] samples = BuildInput(LineCount * LineLength);
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            LineOffset);
        Assert.Equal(InputHash, Float32BitsSha256(samples));

        CurrentAutomaticChromaGainResult result =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                samples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 20);

        Assert.Equal(GainedHash, Float32BitsSha256(samples));
        ulong[] expectedMads =
        [
            0x405C8402D8000000UL,
            0x406709FFD8000000UL,
            0x405CB5D098000000UL,
            0x4059CD1D48000000UL,
            0x40612AEE80000000UL,
            0x40626727D0000000UL,
            0x4059F15C54000000UL,
            0x4059F96D50000000UL,
            0x40628CFCA0000000UL,
            0x40629B2288000000UL,
            0x405A392550000000UL,
            0x405A4BD280000000UL,
            0x4065FA2900000000UL,
            0x405CDD4A62000000UL,
            0x405A821588000000UL,
            0x405CC42E70000000UL,
            0x4066D1355C000000UL,
            0x405C8DE6F0000000UL,
            0x405A3BB3F0000000UL
        ];
        Assert.Equal(
            0xC03ED8B020000000UL,
            BitConverter.DoubleToUInt64Bits(
                CalculateMedian(samples.AsSpan((9 * LineLength) - 16, 12))));
        for (int index = 0; index < expectedMads.Length; index++)
        {
            int syncStart = ((index + 6) * LineLength) - 16;
            Assert.Equal(
                expectedMads[index],
                BitConverter.DoubleToUInt64Bits(
                    CalculateSyncTipMad(samples.AsSpan(syncStart, 12))));
        }

        Assert.Equal(
            unchecked((long)0x4054A1AF286BCA1BUL),
            BitConverter.DoubleToInt64Bits(result.MeanBurstAmplitude));
        Assert.Equal(
            unchecked((long)0x4067B3DA611B790BUL),
            BitConverter.DoubleToInt64Bits(result.NoiseFloor));
    }

    [Fact(DisplayName = "Current chroma ACC rejects negative smoothing windows")]
    public void CurrentChromaAccRejectsNegativeSmoothingWindows()
    {
        double[] samples = BuildInput(2 * 64);
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            lines: 2,
            lineLength: 64,
            lineOffset: 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                samples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 0,
                syncTipLength: 20,
                smoothingWindow: -1));
    }

    [Theory(DisplayName = "Parallel current chroma ACC remains bit-exact")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(int.MaxValue)]
    public void ParallelCurrentChromaAccRemainsBitExact(int workerThreads)
    {
        const int LineCount = 300;
        const int LineLength = 1_024;
        const int LineOffset = 3;
        double[] expectedSamples = BuildInput(LineCount * LineLength);
        double[] actualSamples = (double[])expectedSamples.Clone();
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            LineOffset);

        CurrentAutomaticChromaGainResult expected =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                expectedSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: 1);
        CurrentAutomaticChromaGainResult actual =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                actualSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: workerThreads);

        Assert.Equal(expectedSamples, actualSamples);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.MeanBurstAmplitude),
            BitConverter.DoubleToInt64Bits(actual.MeanBurstAmplitude));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.NoiseFloor),
            BitConverter.DoubleToInt64Bits(actual.NoiseFloor));
    }

    [Fact(DisplayName = "Current chroma ACC gain segment SIMD preserves scalar float32 bits")]
    public void CurrentChromaAccGainSegmentSimdPreservesScalarFloat32Bits()
    {
        int[] lengths = [0, 1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 255, 256, 257];
        (double Gain, double Increment)[] gainCases =
        [
            (0.0, 0.0),
            (-0.0, double.Epsilon),
            (0.125, -0.000_031_25),
            (1e20, -1e12),
            (double.MaxValue, double.MaxValue),
            (double.PositiveInfinity, 1.0),
            (BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0341UL), 0.5)
        ];
        var random = new Random(341);
        foreach (int length in lengths)
        {
            var source = new double[length];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = (float)((random.NextDouble() - 0.5) * 1e30);
            }

            SetExceptionalValues(source);
            foreach ((double gainStart, double gainIncrement) in gainCases)
            {
                double[] expected = source.ToArray();
                double gain = gainStart;
                for (int index = 0; index < expected.Length; index++)
                {
                    expected[index] = (float)((float)expected[index] * gain);
                    gain += gainIncrement;
                }

                double[] actual = source.ToArray();
                VhsChromaDecoder.ApplyCurrentAutomaticChromaGainSegmentInPlace(
                    actual,
                    gainStart,
                    gainIncrement);
                Assert.Equal(
                    expected.Select(BitConverter.DoubleToUInt64Bits),
                    actual.Select(BitConverter.DoubleToUInt64Bits));
            }
        }

        foreach (double zeroGain in new[] { 0.0, -0.0 })
        {
            double[] expected =
            [
                double.MaxValue,
                -double.MaxValue,
                double.MaxValue / 2.0,
                -double.MaxValue / 2.0
            ];
            for (int index = 0; index < expected.Length; index++)
            {
                expected[index] = (float)((float)expected[index] * zeroGain);
            }

            double[] actual =
            [
                double.MaxValue,
                -double.MaxValue,
                double.MaxValue / 2.0,
                -double.MaxValue / 2.0
            ];
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainSegmentInPlace(
                actual,
                zeroGain,
                gainIncrement: 0.0);
            Assert.Equal(
                expected.Select(BitConverter.DoubleToUInt64Bits),
                actual.Select(BitConverter.DoubleToUInt64Bits));
        }
    }

    [Fact(DisplayName = "Parallel current chroma ACC preserves non-monotonic fallback behavior")]
    public void ParallelCurrentChromaAccPreservesNonMonotonicFallbackBehavior()
    {
        const int LineCount = 300;
        const int LineLength = 1_024;
        double[] expectedSamples = BuildInput(LineCount * LineLength);
        double[] actualSamples = (double[])expectedSamples.Clone();
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            lineOffset: 0);
        phaseSequence[151] = phaseSequence[151] with
        {
            BurstStart = phaseSequence[149].BurstStart
        };

        CurrentAutomaticChromaGainResult expected =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                expectedSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: 1);
        CurrentAutomaticChromaGainResult actual =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                actualSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: 20);

        Assert.Equal(expectedSamples, actualSamples);
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Parallel current chroma ACC falls back for overlapping sync-tip windows")]
    public void ParallelCurrentChromaAccFallsBackForOverlappingSyncTipWindows()
    {
        const int LineCount = 1_200;
        const int LineLength = 64;
        double[] expectedSamples = BuildInput(LineCount * LineLength);
        double[] actualSamples = (double[])expectedSamples.Clone();
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            lineOffset: 0);

        CurrentAutomaticChromaGainResult expected =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                expectedSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200);
        CurrentAutomaticChromaGainResult actual =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                actualSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: 20);

        Assert.Equal(expectedSamples, actualSamples);
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Parallel current chroma ACC bounds workers to the phase count")]
    public void ParallelCurrentChromaAccBoundsWorkersToPhaseCount()
    {
        const int LineCount = 3;
        const int LineLength = 30_000;
        double[] expectedSamples = BuildInput(LineCount * LineLength);
        double[] actualSamples = (double[])expectedSamples.Clone();
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            lineOffset: 0);

        CurrentAutomaticChromaGainResult expected =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                expectedSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 0,
                syncTipLength: 200);
        CurrentAutomaticChromaGainResult actual =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                actualSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 0,
                syncTipLength: 200,
                workerThreads: int.MaxValue);

        Assert.Equal(expectedSamples, actualSamples);
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Current chroma ACC retains its public CLR signature")]
    public void CurrentChromaAccRetainsItsPublicClrSignature()
    {
        Type[] expectedParameterTypes =
        [
            typeof(Span<double>),
            typeof(double),
            typeof(IReadOnlyList<ChromaPhaseLine>),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(double)
        ];

        Assert.NotNull(typeof(VhsChromaDecoder).GetMethod(
            nameof(VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace),
            expectedParameterTypes));
    }

    [Fact(DisplayName = "Parallel current chroma ACC preserves invalid phase exception side effects")]
    public void ParallelCurrentChromaAccPreservesInvalidPhaseExceptionSideEffects()
    {
        const int LineCount = 300;
        const int LineLength = 1_024;
        double[] expectedSamples = BuildInput(LineCount * LineLength);
        double[] actualSamples = (double[])expectedSamples.Clone();
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            lineOffset: 0);
        phaseSequence[150] = null!;

        Exception expected = Assert.Throws<NullReferenceException>(
            () => VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                expectedSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200));
        Exception actual = Assert.Throws<NullReferenceException>(
            () => VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                actualSamples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 200,
                workerThreads: 20));

        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expectedSamples, actualSamples);
    }

    private static double CalculateSyncTipMad(ReadOnlySpan<double> samples)
    {
        double median = CalculateMedian(samples);
        var values = samples.ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Math.Abs((float)values[index] - median);
        }

        return NumpyReduction.MedianFloat64(values);
    }

    private static void SetExceptionalValues(Span<double> values)
    {
        ulong[] bits =
        [
            0x0000_0000_0000_0000UL,
            0x8000_0000_0000_0000UL,
            0x0000_0000_0000_0001UL,
            0x8000_0000_0000_0001UL,
            0x7FEF_FFFF_FFFF_FFFFUL,
            0xFFEF_FFFF_FFFF_FFFFUL,
            0x7FF0_0000_0000_0000UL,
            0xFFF0_0000_0000_0000UL,
            0x7FF0_0000_0000_0341UL,
            0xFFF0_0000_0000_0341UL,
            0x7FF8_0000_0000_0341UL,
            0xFFF8_0000_0000_0341UL
        ];
        int count = Math.Min(values.Length, bits.Length);
        for (int index = 0; index < count; index++)
        {
            values[index] = BitConverter.UInt64BitsToDouble(bits[index]);
        }
    }

    private static double CalculateMedian(ReadOnlySpan<double> samples)
    {
        var values = new double[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            values[index] = (float)samples[index];
        }

        return NumbaReduction.MedianFloat32(values);
    }

    private static ChromaPhaseLine[] BuildPhaseSequence(
        int lines,
        int lineLength,
        int lineOffset)
    {
        const int BurstLength = 40;
        var phaseSequence = new ChromaPhaseLine[lines];
        for (int index = 0; index < lines; index++)
        {
            int lineNumber = lineOffset + index;
            int start = index * lineLength;
            double amplitude =
                82.0 + ((((index * 13) % 17) - 8) * 1.25);
            double magnitude = amplitude * (BurstLength / 2.0);
            phaseSequence[index] = new ChromaPhaseLine(
                lineNumber,
                PhaseRotation: 0,
                BurstPhaseDegrees: (index * 91) % 360,
                BurstMagnitude: magnitude,
                I: magnitude,
                Q: -magnitude / 3.0)
            {
                BurstStart = start,
                BurstEnd = start + BurstLength,
                BurstCenter = start + ((BurstLength - 1) / 2),
                BurstAmplitude = amplitude,
                BurstFrequencyHz = 3_579_545.0
            };
        }

        return phaseSequence;
    }

    private static double[] BuildInput(int length)
    {
        var samples = new double[length];
        for (int index = 0; index < samples.Length; index++)
        {
            int integer = ((index * 7_919 + 104_729) % 65_521) - 32_760;
            samples[index] = (float)integer * 0.01f;
        }

        return samples;
    }

    private static string Float32BitsSha256(ReadOnlySpan<double> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(int))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
