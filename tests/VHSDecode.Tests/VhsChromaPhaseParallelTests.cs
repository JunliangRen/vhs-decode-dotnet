using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaPhaseParallelTests
{
    [Fact(DisplayName = "Current burst probes reuse four exact-length decoder buffers")]
    public void CurrentBurstProbesReuseFourExactLengthDecoderBuffers()
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

        VhsChromaPhaseAnalysis first = Analyze();
        int creationCount = cache.BurstProbeBufferCreationCount;
        double[][] dirtyBuffers = Enumerable.Range(0, creationCount)
            .Select(_ => cache.RentBurstProbeBuffer(32))
            .ToArray();
        foreach (double[] buffer in dirtyBuffers)
        {
            Array.Fill(buffer, double.NaN);
            cache.ReturnBurstProbeBuffer(buffer);
        }

        VhsChromaPhaseAnalysis second = Analyze();

        Assert.InRange(creationCount, 1, 4);
        Assert.Equal(creationCount, cache.BurstProbeBufferCreationCount);
        Assert.Equal(creationCount, cache.RetainedBurstProbeBufferCount);
        AssertPhaseAnalysisEqual(first.Phase, second.Phase);

        VhsChromaPhaseAnalysis Analyze()
            => VhsChromaDecoder.AnalyzeFieldPhaseWithWorkspace(
                chroma,
                options,
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

    [Fact(DisplayName = "Current burst probe buffer retention stays bounded at four")]
    public void CurrentBurstProbeBufferRetentionStaysBoundedAtFour()
    {
        var cache = new VhsChromaCarrierTableCache();
        double[][] buffers = Enumerable.Range(0, 8)
            .Select(index => cache.RentBurstProbeBuffer(32 + index))
            .ToArray();

        foreach (double[] buffer in buffers)
        {
            cache.ReturnBurstProbeBuffer(buffer);
        }

        Assert.Equal(8, cache.BurstProbeBufferCreationCount);
        Assert.Equal(4, cache.RetainedBurstProbeBufferCount);
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
