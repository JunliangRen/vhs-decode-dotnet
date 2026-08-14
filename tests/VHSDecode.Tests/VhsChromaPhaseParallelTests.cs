using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaPhaseParallelTests
{
    [Fact(DisplayName = "Public burst results stay classes while internal values preserve exact fields")]
    public void PublicBurstResultsStayClassesWhileInternalValuesPreserveExactFields()
    {
        const long NegativeZeroBits = unchecked((long)0x8000_0000_0000_0000UL);
        const long NanPayloadBits = unchecked((long)0x7FF8_0000_0000_1234UL);
        const long PositiveInfinityBits = unchecked((long)0x7FF0_0000_0000_0000UL);
        const long NegativeInfinityBits = unchecked((long)0xFFF0_0000_0000_0000UL);
        const long PositiveSubnormalBits = 0x0000_0000_0000_0001L;
        const long NegativeSubnormalBits = unchecked((long)0x8000_0000_0000_0001UL);
        const long MaximumFiniteBits = 0x7FEF_FFFF_FFFF_FFFFL;
        const long NegativeFiniteBits = unchecked((long)0xC01E_0000_0000_0000UL);
        const long FrequencyBits = 0x414B_50B0_1000_0000L;
        var value = new ChromaBurstDemodulationValue(
            PhaseDegrees: BitConverter.Int64BitsToDouble(NegativeZeroBits),
            PhaseOffsetDegrees: BitConverter.Int64BitsToDouble(NanPayloadBits),
            Magnitude: BitConverter.Int64BitsToDouble(PositiveInfinityBits),
            I: BitConverter.Int64BitsToDouble(NegativeInfinityBits),
            Q: BitConverter.Int64BitsToDouble(PositiveSubnormalBits))
        {
            Start = 17,
            End = 41,
            Center = BitConverter.Int64BitsToDouble(NegativeSubnormalBits),
            Amplitude = BitConverter.Int64BitsToDouble(MaximumFiniteBits),
            Dc = BitConverter.Int64BitsToDouble(NegativeFiniteBits),
            FrequencyHz = BitConverter.Int64BitsToDouble(FrequencyBits)
        };
        ChromaBurstDemodulationResult result = value.ToPublicResult();

        Assert.False(typeof(ChromaBurstDemodulationResult).IsValueType);
        Assert.True(typeof(ChromaBurstDemodulationResult).IsSealed);
        Assert.True(typeof(ChromaBurstDemodulationValue).IsValueType);
        Assert.Equal(
            typeof(ChromaBurstDemodulationResult),
            typeof(ChromaBurstProbe).GetMethod("Invoke")!.ReturnType);
        Assert.Equal(NegativeZeroBits, BitConverter.DoubleToInt64Bits(result.PhaseDegrees));
        Assert.Equal(NanPayloadBits, BitConverter.DoubleToInt64Bits(result.PhaseOffsetDegrees));
        Assert.Equal(PositiveInfinityBits, BitConverter.DoubleToInt64Bits(result.Magnitude));
        Assert.Equal(NegativeInfinityBits, BitConverter.DoubleToInt64Bits(result.I));
        Assert.Equal(PositiveSubnormalBits, BitConverter.DoubleToInt64Bits(result.Q));
        Assert.Equal(17, result.Start);
        Assert.Equal(41, result.End);
        Assert.Equal(NegativeSubnormalBits, BitConverter.DoubleToInt64Bits(result.Center));
        Assert.Equal(MaximumFiniteBits, BitConverter.DoubleToInt64Bits(result.Amplitude));
        Assert.Equal(NegativeFiniteBits, BitConverter.DoubleToInt64Bits(result.Dc));
        Assert.Equal(FrequencyBits, BitConverter.DoubleToInt64Bits(result.FrequencyHz));

        ChromaBurstDemodulationValue changedValue = value with { Amplitude = 12.5 };
        ChromaBurstDemodulationResult changed = result with { Amplitude = 12.5 };
        Assert.Equal(MaximumFiniteBits, BitConverter.DoubleToInt64Bits(value.Amplitude));
        Assert.Equal(12.5, changedValue.Amplitude);
        Assert.Equal(MaximumFiniteBits, BitConverter.DoubleToInt64Bits(result.Amplitude));
        Assert.Equal(12.5, changed.Amplitude);
        Assert.NotSame(result, changed);
    }

    [Fact(DisplayName = "Current burst probes reuse eight decoder buffers after filter failures")]
    public void CurrentBurstProbesReuseEightExactLengthDecoderBuffers()
    {
        const int LineLength = 64;
        const int LineCount = 80;
        var options = new VhsChromaFieldOptions(
            ColorSystem: "NTSC",
            OutputLineLength: LineLength,
            OutputLineCount: LineCount,
            OutputSampleRateHz: 4_000_000.0,
            FscMHz: 1.0,
            ColorUnderCarrierHz: 250_000.0,
            BurstStart: 8,
            BurstEnd: 24,
            BurstAbsRef: 72.0,
            ChromaRotation: [-1, 1],
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false)
        {
            WorkerThreads = 8,
            UseCurrentChromaProcessing = true,
            FinalSosFilter = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)]
        };
        double[] chroma = Enumerable.Range(0, LineLength * LineCount)
            .Select(static index =>
                (double)(float)((((index * 37) % 1000) - 500) / 16.0f))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, LineCount + 1)
            .Select(static line => (double)(line * LineLength))
            .ToArray();
        var cache = new VhsChromaCarrierTableCache();

        VhsChromaPhaseAnalysis first = Analyze(options);
        int firstAnalysisCreationCount = cache.BurstProbeBufferCreationCount;
        double[][] dirtyBuffers = Enumerable
            .Range(0, VhsChromaCarrierTableCache.BurstProbeBufferCapacity)
            .Select(_ => cache.RentBurstProbeBuffer(32))
            .ToArray();
        foreach (double[] buffer in dirtyBuffers)
        {
            Array.Fill(buffer, double.NaN);
            cache.ReturnBurstProbeBuffer(buffer);
        }

        int seededCreationCount = cache.BurstProbeBufferCreationCount;
        VhsChromaFieldOptions invalidOptions = options with
        {
            FinalSosFilter = Enumerable.Repeat(
                    new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0),
                    16)
                .ToArray()
        };
        Assert.Throws<ArgumentException>(() => Analyze(invalidOptions));
        Assert.Equal(seededCreationCount, cache.BurstProbeBufferCreationCount);
        Assert.Equal(
            VhsChromaCarrierTableCache.BurstProbeBufferCapacity,
            cache.RetainedBurstProbeBufferCount);

        VhsChromaPhaseAnalysis second = Analyze(options);

        Assert.InRange(
            firstAnalysisCreationCount,
            1,
            VhsChromaCarrierTableCache.BurstProbeBufferCapacity);
        Assert.Equal(
            VhsChromaCarrierTableCache.BurstProbeBufferCapacity,
            seededCreationCount);
        Assert.Equal(seededCreationCount, cache.BurstProbeBufferCreationCount);
        Assert.Equal(
            VhsChromaCarrierTableCache.BurstProbeBufferCapacity,
            cache.RetainedBurstProbeBufferCount);
        AssertPhaseAnalysisEqual(first.Phase, second.Phase);

        VhsChromaPhaseAnalysis Analyze(VhsChromaFieldOptions selectedOptions)
            => VhsChromaDecoder.AnalyzeFieldPhaseWithWorkspace(
                chroma,
                selectedOptions,
                lineLocations,
                inputLineLength: LineLength,
                carrierTableCache: cache,
                useFloat32Samples: true);
    }

    [Fact(DisplayName = "Explicit current burst filters retain independent input arrays")]
    public void ExplicitCurrentBurstFiltersRetainIndependentInputArrays()
    {
        const int LineLength = 64;
        const int LineCount = 80;
        var options = new VhsChromaFieldOptions(
            ColorSystem: "NTSC",
            OutputLineLength: LineLength,
            OutputLineCount: LineCount,
            OutputSampleRateHz: 4_000_000.0,
            FscMHz: 1.0,
            ColorUnderCarrierHz: 250_000.0,
            BurstStart: 8,
            BurstEnd: 24,
            BurstAbsRef: 72.0,
            ChromaRotation: [-1, 1],
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false)
        {
            WorkerThreads = 1,
            UseCurrentChromaProcessing = true
        };
        double[] chroma = Enumerable.Range(0, LineLength * LineCount)
            .Select(static index =>
                (double)(float)((((index * 37) % 1000) - 500) / 16.0f))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, LineCount + 1)
            .Select(static line => (double)(line * LineLength))
            .ToArray();
        var cache = new VhsChromaCarrierTableCache();
        var retained = new List<(double[] Buffer, double[] Snapshot)>();

        _ = VhsChromaDecoder.AnalyzeFieldPhaseWithWorkspace(
            chroma,
            options,
            lineLocations,
            inputLineLength: LineLength,
            burstFilter: values =>
            {
                retained.Add((values, values.ToArray()));
                return values;
            },
            carrierTableCache: cache,
            useFloat32Samples: true);

        Assert.Equal(LineCount * 2, retained.Count);
        Assert.Equal(0, cache.BurstProbeBufferCreationCount);
        for (int index = 0; index < retained.Count; index++)
        {
            AssertDoubleBitsEqual(retained[index].Snapshot, retained[index].Buffer);
            for (int previous = 0; previous < index; previous++)
            {
                Assert.NotSame(retained[previous].Buffer, retained[index].Buffer);
            }
        }
    }

    [Fact(DisplayName = "Current burst probe buffer retention stays bounded at eight")]
    public void CurrentBurstProbeBufferRetentionStaysBoundedAtEight()
    {
        var cache = new VhsChromaCarrierTableCache();
        double[][] buffers = Enumerable.Range(0, 16)
            .Select(index => cache.RentBurstProbeBuffer(32 + index))
            .ToArray();

        foreach (double[] buffer in buffers)
        {
            cache.ReturnBurstProbeBuffer(buffer);
        }

        Assert.Equal(16, cache.BurstProbeBufferCreationCount);
        Assert.Equal(8, cache.RetainedBurstProbeBufferCount);
    }

    [Fact(DisplayName = "Current phase analysis reads only resampled line prefixes")]
    public void CurrentPhaseAnalysisReadsOnlyResampledLinePrefixes()
    {
        const int LineLength = 128;
        const int LineCount = 80;
        const int BurstStart = 12;
        const int BurstEnd = 36;
        const int PhasePrefixSamples = BurstStart + BurstEnd;
        double[] source = Enumerable.Range(0, 17_000)
            .Select(static index =>
                Math.Sin(index * 0.013) + (0.25 * Math.Cos(index * 0.003)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, LineCount + 1)
            .Select(static line => 200.25 + (line * 192.125) + ((line % 7) * 0.01))
            .ToArray();
        var resampler = new TbcLineResampler(
            LineLength,
            TbcLineInterpolationMethod.Linear,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 192.125,
            workerThreads: 8);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            LineCount);
        double[] full = resampler.ResamplePrepared(source, plan);
        var sparse = new double[plan.DestinationLength];
        Array.Fill(sparse, double.NaN);
        resampler.ResampleLinePrefixes(
            source,
            lineLocations,
            firstLine: 0,
            LineCount,
            PhasePrefixSamples,
            sparse);
        var options = new VhsChromaFieldOptions(
            ColorSystem: "NTSC",
            OutputLineLength: LineLength,
            OutputLineCount: LineCount,
            OutputSampleRateHz: 4_000_000.0,
            FscMHz: 1.0,
            ColorUnderCarrierHz: 250_000.0,
            BurstStart,
            BurstEnd,
            BurstAbsRef: 72.0,
            ChromaRotation: [-1, 1],
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: true)
        {
            WorkerThreads = 8,
            UseCurrentChromaProcessing = true,
            FinalSosFilter = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)]
        };

        VhsChromaPhaseAnalysis expected = Analyze(full);
        VhsChromaPhaseAnalysis actual = Analyze(sparse);

        AssertPhaseAnalysisEqual(expected.Phase, actual.Phase);
        AssertDoubleBitsEqual(expected.HeterodyneCarrierHz, actual.HeterodyneCarrierHz);
        AssertDoubleBitsEqual(expected.HeterodynePhaseRadians, actual.HeterodynePhaseRadians);
        Assert.All(
            Enumerable.Range(0, LineCount),
            line => Assert.All(
                sparse.AsSpan(
                        (line * LineLength) + PhasePrefixSamples,
                        LineLength - PhasePrefixSamples)
                    .ToArray(),
                static value => Assert.True(double.IsNaN(value))));

        VhsChromaPhaseAnalysis Analyze(double[] samples)
            => VhsChromaDecoder.AnalyzeFieldPhaseWithWorkspace(
                samples,
                options,
                lineLocations,
                inputLineLength: 192,
                carrierTableCache: new VhsChromaCarrierTableCache(),
                useFloat32Samples: true);
    }

    [Theory(DisplayName = "Parallel current phase prefix matches serial phase analysis")]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelCurrentPhasePrefixMatchesSerialPhaseAnalysis(
        bool detectChromaTrackPhase)
    {
        const int LineOffset = 3;
        const int LinesOut = 80;
        double[] lineLocations = Enumerable.Range(0, LineOffset + LinesOut + 1)
            .Select(static line => (line * 100.0) + ((line % 5) * 0.125))
            .ToArray();

        static ChromaBurstDemodulationResult Probe(
            int lineNumber,
            int phaseRotation,
            double lineScale)
        {
            double phaseDegrees = (phaseRotation * 90.0)
                + (lineNumber >= 70 ? 120.0 : lineNumber * 0.125);
            double phaseRadians = phaseDegrees * Math.PI / 180.0;
            double magnitude = 30_000.0 + lineNumber;
            return new ChromaBurstDemodulationResult(
                phaseDegrees,
                lineScale,
                magnitude,
                Math.Cos(phaseRadians) * magnitude,
                Math.Sin(phaseRadians) * magnitude)
            {
                Start = lineNumber * 100,
                End = (lineNumber + 1) * 100,
                Center = lineNumber + 0.5,
                Amplitude = magnitude / 4.0,
                Dc = lineScale,
                FrequencyHz = 3_579_545.0 + lineNumber
            };
        }

        ChromaPhaseSequenceResult serial = Analyze(workerThreads: 1);
        ChromaPhaseSequenceResult parallel = Analyze(workerThreads: 8);

        AssertPhaseAnalysisEqual(serial, parallel);

        ChromaPhaseSequenceResult Analyze(int workerThreads)
            => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                LineOffset,
                LinesOut,
                inputLineLength: 100,
                Probe,
                detectChromaTrackPhase,
                rotationCheckStartLine: LineOffset + LinesOut - 16,
                enableColorKiller: true,
                prevBurstDetectedLine: 0,
                colorSystem: "NTSC",
                workerThreads);
    }

    [Fact(DisplayName = "Parallel current phase prefix preserves probe exceptions")]
    public void ParallelCurrentPhasePrefixPreservesProbeExceptions()
    {
        double[] lineLocations = Enumerable.Range(0, 65)
            .Select(static line => line * 100.0)
            .ToArray();
        int[] probeCounts = new int[64];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                lineOffset: 0,
                linesOut: 64,
                inputLineLength: 100,
                burstProbe: (lineNumber, _, _) => Probe(lineNumber),
                detectChromaTrackPhase: false,
                rotationCheckStartLine: 48,
                enableColorKiller: false,
                prevBurstDetectedLine: 0,
                colorSystem: "NTSC",
                workerThreads: 8));

        Assert.Equal("first pinned probe failure", exception.Message);
        Assert.All(probeCounts, static count => Assert.Equal(1, count));

        ChromaBurstDemodulationResult Probe(int lineNumber)
        {
            Interlocked.Increment(ref probeCounts[lineNumber]);
            if (lineNumber == 7)
            {
                Thread.Sleep(10);
                throw new InvalidOperationException("first pinned probe failure");
            }

            if (lineNumber == 20)
            {
                throw new InvalidOperationException("later pinned probe failure");
            }

            return new ChromaBurstDemodulationResult(
                        0.0,
                        0.0,
                        30_000.0,
                        30_000.0,
                        0.0);
        }
    }

    [Theory(DisplayName = "Parallel current phase prefix matches serial after track-phase flip")]
    [InlineData("NTSC")]
    [InlineData("PAL")]
    public void ParallelCurrentPhasePrefixMatchesSerialAfterTrackPhaseFlip(string colorSystem)
    {
        const int LinesOut = 80;
        double[] lineLocations = Enumerable.Range(0, LinesOut + 1)
            .Select(static line => line * 100.0)
            .ToArray();

        ChromaPhaseSequenceResult serial = Analyze(workerThreads: 1);
        ChromaPhaseSequenceResult parallel = Analyze(workerThreads: 8);

        AssertPhaseAnalysisEqual(serial, parallel);

        ChromaPhaseSequenceResult Analyze(int workerThreads)
            => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                lineOffset: 0,
                LinesOut,
                inputLineLength: 100,
                burstProbe: (lineNumber, phaseRotation, _) =>
                    new ChromaBurstDemodulationResult(
                        colorSystem == "NTSC" ? lineNumber * 180.0 : 0.0,
                        phaseRotation * 90.0,
                        30_000.0,
                        30_000.0,
                        0.0),
                detectChromaTrackPhase: false,
                rotationCheckStartLine: LinesOut - 16,
                enableColorKiller: false,
                prevBurstDetectedLine: 0,
                colorSystem,
                workerThreads);
    }

    private static void AssertPhaseAnalysisEqual(
        ChromaPhaseSequenceResult expected,
        ChromaPhaseSequenceResult actual)
    {
        Assert.Equal(expected.NextChromaRotationIndex, actual.NextChromaRotationIndex);
        Assert.Equal(expected.BurstDetectedLine, actual.BurstDetectedLine);
        AssertDoubleBitsEqual(expected.BurstMagnitudeAverage, actual.BurstMagnitudeAverage);
        AssertDoubleBitsEqual(expected.BurstPhaseAverageDegrees, actual.BurstPhaseAverageDegrees);
        AssertDoubleBitsEqual(expected.EvenBurstPhaseAverageDegrees, actual.EvenBurstPhaseAverageDegrees);
        AssertDoubleBitsEqual(expected.OddBurstPhaseAverageDegrees, actual.OddBurstPhaseAverageDegrees);
        Assert.Equal(expected.PhaseSequence.Length, actual.PhaseSequence.Length);
        for (int index = 0; index < expected.PhaseSequence.Length; index++)
        {
            ChromaPhaseLine expectedLine = expected.PhaseSequence[index];
            ChromaPhaseLine actualLine = actual.PhaseSequence[index];
            Assert.Equal(expectedLine.LineNumber, actualLine.LineNumber);
            Assert.Equal(expectedLine.PhaseRotation, actualLine.PhaseRotation);
            Assert.Equal(expectedLine.BurstStart, actualLine.BurstStart);
            Assert.Equal(expectedLine.BurstEnd, actualLine.BurstEnd);
            AssertDoubleBitsEqual(expectedLine.BurstPhaseDegrees, actualLine.BurstPhaseDegrees);
            AssertDoubleBitsEqual(expectedLine.BurstPhaseOffsetDegrees, actualLine.BurstPhaseOffsetDegrees);
            AssertDoubleBitsEqual(expectedLine.BurstMagnitude, actualLine.BurstMagnitude);
            AssertDoubleBitsEqual(expectedLine.I, actualLine.I);
            AssertDoubleBitsEqual(expectedLine.Q, actualLine.Q);
            AssertDoubleBitsEqual(expectedLine.BurstCenter, actualLine.BurstCenter);
            AssertDoubleBitsEqual(expectedLine.BurstAmplitude, actualLine.BurstAmplitude);
            AssertDoubleBitsEqual(expectedLine.BurstDc, actualLine.BurstDc);
            AssertDoubleBitsEqual(expectedLine.BurstFrequencyHz, actualLine.BurstFrequencyHz);
        }
    }

    private static void AssertDoubleBitsEqual(double expected, double actual)
        => Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected),
            BitConverter.DoubleToInt64Bits(actual));

    private static void AssertDoubleBitsEqual(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            AssertDoubleBitsEqual(expected[index], actual[index]);
        }
    }
}
