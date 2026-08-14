using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaCurrentProcessingIntegrationTests
{
    [Fact(DisplayName = "Current chroma field routes fitted burst data through gain and CTI")]
    public void CurrentChromaFieldRoutesFittedBurstDataThroughGainAndCti()
    {
        const int LineLength = 64;
        const int LineCount = 48;
        const int LineOffset = 1;
        double[] chroma = BuildInput(LineLength * LineCount);
        double[] lineLocations = Enumerable.Range(0, LineOffset + LineCount + 1)
            .Select(static line => line * (double)LineLength)
            .ToArray();
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 1.0);

        ChromaPhaseSequenceResult phase = VhsChromaDecoder.AnalyzeFieldPhase(
            chroma,
            options,
            lineLocations,
            inputLineLength: LineLength,
            lineOffset: LineOffset,
            burstFilter: static samples => samples);

        Assert.Equal(LineCount, phase.PhaseSequence.Length);
        for (int index = 0; index < phase.PhaseSequence.Length; index++)
        {
            ChromaPhaseLine line = phase.PhaseSequence[index];
            Assert.Equal(index * LineLength, line.BurstStart);
            Assert.True(double.IsFinite(line.BurstAmplitude));
            Assert.True(double.IsFinite(line.BurstDc));
            Assert.True(double.IsFinite(line.BurstFrequencyHz));
            Assert.NotEqual(0.0, line.BurstFrequencyHz);
        }

        VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);
        VhsChromaFieldResult repeated = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);
        VhsChromaFieldResult ctiDisabled = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options with { CtiMix = 0.0 },
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);

        Assert.Equal(result.Samples, repeated.Samples);
        Assert.Equal(
            "42367AD445A47D4DFFE06144DAFF3386051B448A1D94648B2E53F3264BD71517",
            Sha256(result.Samples));
        Assert.Equal(
            "9A735F22A8BB44A6723B8D9736596203AA1714B9007B5C3EA51EEFD0801A0E29",
            Sha256(ctiDisabled.Samples));
    }

    [Fact(DisplayName = "Legacy chroma field does not route current processing")]
    public void LegacyChromaFieldDoesNotRouteCurrentProcessing()
    {
        VhsChromaFieldOptions current = CreateOptions(ctiMix: 1.0);
        VhsChromaFieldOptions legacy = current with
        {
            UseCurrentChromaProcessing = false
        };

        Assert.True(current.UseCurrentChromaProcessing);
        Assert.False(legacy.UseCurrentChromaProcessing);
    }

    [Theory(DisplayName = "Owned NTSC chroma storage matches copying decode bit-exactly")]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedNtscChromaStorageMatchesCopyingDecodeBitExactly(bool useCurrentProcessing)
    {
        const int LineLength = 64;
        const int LineCount = 48;
        double[] chroma = BuildInput(LineLength * LineCount);
        double[] original = chroma.ToArray();
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 0.0) with
        {
            UseCurrentChromaProcessing = useCurrentProcessing
        };
        ChromaPhaseLine[] phaseLines = Enumerable.Range(0, LineCount)
            .Select(line => new ChromaPhaseLine(
                LineNumber: line,
                PhaseRotation: line & 3,
                BurstPhaseDegrees: (line & 1) == 0 ? 12.5 : -7.25)
            {
                BurstStart = line * LineLength,
                BurstAmplitude = 72.0,
                BurstDc = (line % 5) * 0.125,
                BurstFrequencyHz = options.FscMHz * 1_000_000.0
            })
            .ToArray();
        var phase = new ChromaPhaseSequenceResult(
            NextChromaRotationIndex: 0,
            PhaseSequence: phaseLines,
            BurstDetectedLine: 0,
            BurstMagnitudeAverage: 72.0,
            BurstPhaseAverageDegrees: 0.0,
            EvenBurstPhaseAverageDegrees: 12.5,
            OddBurstPhaseAverageDegrees: -7.25);

        VhsChromaFieldResult expected = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0);
        double[] ownedChroma = chroma.ToArray();
        VhsChromaFieldResult actual = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            ownedChroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0);

        Assert.Equal(
            original.Select(BitConverter.DoubleToUInt64Bits),
            chroma.Select(BitConverter.DoubleToUInt64Bits));
        Assert.Equal(expected.Samples, actual.Samples);
        Assert.Equal(expected.FieldPhaseId, actual.FieldPhaseId);
        Assert.Equal(expected.NextChromaRotationIndex, actual.NextChromaRotationIndex);
        Assert.Equal(expected.BurstDetectedLine, actual.BurstDetectedLine);
    }

    [Theory(DisplayName = "Prepared owned chroma completion matches synchronous decode bit-exactly")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PreparedOwnedChromaCompletionMatchesSynchronousDecodeBitExactly(
        bool useCurrentProcessing,
        bool trackCarrier)
    {
        const int LineLength = 64;
        const int LineCount = 48;
        double[] chroma = BuildInput(LineLength * LineCount);
        VhsChromaFieldOptions options = CreateOptions(ctiMix: useCurrentProcessing ? 0.75 : 0.0) with
        {
            UseCurrentChromaProcessing = useCurrentProcessing,
            ChromaAfcTrackCarrier = trackCarrier,
            ChromaAfcLineFrequencyHz = 15_625.0,
            ChromaAfcFineTuneStepHz = 100.0
        };
        ChromaPhaseLine[] phaseLines = Enumerable.Range(0, LineCount)
            .Select(line => new ChromaPhaseLine(
                LineNumber: line,
                PhaseRotation: line & 3,
                BurstPhaseDegrees: (line & 1) == 0 ? 12.5 : -7.25)
            {
                BurstStart = line * LineLength,
                BurstAmplitude = 72.0,
                BurstDc = (line % 5) * 0.125,
                BurstFrequencyHz = options.FscMHz * 1_000_000.0
            })
            .ToArray();
        var phase = new ChromaPhaseSequenceResult(
            NextChromaRotationIndex: 3,
            PhaseSequence: phaseLines,
            BurstDetectedLine: 2,
            BurstMagnitudeAverage: 72.0,
            BurstPhaseAverageDegrees: 0.0,
            EvenBurstPhaseAverageDegrees: 12.5,
            OddBurstPhaseAverageDegrees: -7.25);
        var analysis = new VhsChromaPhaseAnalysis(
            phase,
            VhsChromaDecoder.BuildHeterodyneTable(
                chroma.Length,
                options.FscMHz,
                options.ColorUnderCarrierHz / 1_000_000.0,
                options.FscMHz * 4.0,
                phaseDriftRadians: 0.125,
                workerThreads: options.WorkerThreads),
            options.ColorUnderCarrierHz,
            HeterodynePhaseRadians: 0.125);
        var expectedDestination = new ushort[chroma.Length];
        VhsChromaFieldResult expected = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            chroma.ToArray(),
            options,
            analysis,
            isFirstField: true,
            fieldNumber: 7,
            previousChromaAfcCarrierHz: 249_500.0,
            previousChromaAfcPhaseRadians: 0.125,
            outputDestination: expectedDestination);
        var actualDestination = new ushort[chroma.Length];
        VhsChromaDecoder.PreparedOwnedField prepared =
            VhsChromaDecoder.PrepareOwnedFieldWithPhase(
                chroma.ToArray(),
                options,
                analysis,
                isFirstField: true,
                fieldNumber: 7,
                previousChromaAfcCarrierHz: 249_500.0,
                previousChromaAfcPhaseRadians: 0.125,
                outputDestination: actualDestination);

        Assert.Equal(expected.BurstDetectedLine, prepared.BurstDetectedLine);
        Assert.Equal(expected.NextChromaRotationIndex, prepared.NextChromaRotationIndex);
        Assert.Equal(expected.CarrierEstimate, prepared.CarrierEstimate);

        VhsChromaFieldResult actual = prepared.Complete();

        Assert.Same(actualDestination, actual.Samples);
        Assert.Equal(expected.Samples, actual.Samples);
        Assert.Equal(expected.FieldPhaseId, actual.FieldPhaseId);
        Assert.Equal(expected.BurstDetectedLine, actual.BurstDetectedLine);
        Assert.Equal(expected.NextChromaRotationIndex, actual.NextChromaRotationIndex);
        Assert.Equal(expected.CarrierEstimate, actual.CarrierEstimate);
        Assert.Throws<InvalidOperationException>(() => prepared.Complete());
    }

    [Fact(DisplayName = "Prepared color-killer field defers neutral output until completion")]
    public void PreparedColorKillerFieldDefersNeutralOutputUntilCompletion()
    {
        const int LineLength = 64;
        const int LineCount = 48;
        double[] chroma = BuildInput(LineLength * LineCount);
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 0.0);
        var phase = new ChromaPhaseSequenceResult(
            NextChromaRotationIndex: 2,
            PhaseSequence: [],
            BurstDetectedLine: -1,
            BurstMagnitudeAverage: 0.0,
            BurstPhaseAverageDegrees: 0.0,
            EvenBurstPhaseAverageDegrees: 0.0,
            OddBurstPhaseAverageDegrees: 0.0);
        var analysis = new VhsChromaPhaseAnalysis(
            phase,
            [[], [], [], []],
            options.ColorUnderCarrierHz,
            HeterodynePhaseRadians: 0.0);
        var destination = Enumerable.Repeat((ushort)123, chroma.Length).ToArray();

        VhsChromaDecoder.PreparedOwnedField prepared =
            VhsChromaDecoder.PrepareOwnedFieldWithPhase(
                chroma,
                options,
                analysis,
                outputDestination: destination);

        Assert.All(destination, sample => Assert.Equal((ushort)123, sample));
        Assert.Equal(-1, prepared.BurstDetectedLine);
        Assert.Equal(2, prepared.NextChromaRotationIndex);
        Assert.Null(prepared.CarrierEstimate);

        VhsChromaFieldResult actual = prepared.Complete();

        Assert.Same(destination, actual.Samples);
        Assert.All(actual.Samples, sample => Assert.Equal((ushort)32_767, sample));
    }

    [Fact(DisplayName = "Owned PAL chroma upconversion matches the copying path bit-exactly")]
    public void OwnedPalChromaUpconversionMatchesCopyingPathBitExactly()
    {
        const int LineLength = 17;
        const int LineCount = 9;
        const int LineOffset = 2;
        double[] input = BuildInput(LineLength * LineCount);
        ChromaPhaseLine[] phaseLines = [
            new(LineNumber: 3, PhaseRotation: 0),
            new(LineNumber: 5, PhaseRotation: 1),
            new(LineNumber: 6, PhaseRotation: 2),
            new(LineNumber: 8, PhaseRotation: 3)
        ];
        double[][] heterodyne = Enumerable.Range(0, 4)
            .Select(phase => Enumerable.Range(0, input.Length)
                .Select(index => 0.25 + phase + (index * 0.0001))
                .ToArray())
            .ToArray();

        double[] expected = VhsChromaDecoder.UpconvertChroma(
            input,
            LineOffset,
            LineLength,
            phaseLines,
            heterodyne);
        double[] actual = input.ToArray();

        Assert.True(VhsChromaDecoder.TryUpconvertChromaInPlace(
            actual,
            LineOffset,
            LineLength,
            phaseLines,
            heterodyne));
        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            actual.Select(BitConverter.DoubleToUInt64Bits));

        ChromaPhaseLine[] wrappedPhaseLines = Enumerable.Range(1, LineCount)
            .Select(line => new ChromaPhaseLine(
                LineNumber: line,
                PhaseRotation: line & 3))
            .ToArray();
        double[] wrappedExpected = VhsChromaDecoder.UpconvertChroma(
            input,
            lineOffset: 3,
            LineLength,
            wrappedPhaseLines,
            heterodyne);
        double[] wrappedActual = input.ToArray();
        Assert.True(VhsChromaDecoder.TryUpconvertChromaInPlace(
            wrappedActual,
            lineOffset: 3,
            LineLength,
            wrappedPhaseLines,
            heterodyne));
        Assert.Equal(
            wrappedExpected.Select(BitConverter.DoubleToUInt64Bits),
            wrappedActual.Select(BitConverter.DoubleToUInt64Bits));

        ChromaPhaseLine[] overlapping = [phaseLines[0], phaseLines[0]];
        double[] rejected = input.ToArray();
        Assert.False(VhsChromaDecoder.TryUpconvertChromaInPlace(
            rejected,
            LineOffset,
            LineLength,
            overlapping,
            heterodyne));
        Assert.Equal(
            input.Select(BitConverter.DoubleToUInt64Bits),
            rejected.Select(BitConverter.DoubleToUInt64Bits));

        ChromaPhaseLine[] aliasedPhaseLines = [new(LineNumber: 2, PhaseRotation: 0)];
        double[] aliased = input.ToArray();
        double[][] aliasedHeterodyne = heterodyne.ToArray();
        aliasedHeterodyne[0] = aliased;
        Assert.False(VhsChromaDecoder.TryUpconvertChromaInPlace(
            aliased,
            LineOffset,
            LineLength,
            aliasedPhaseLines,
            aliasedHeterodyne));
        Assert.Equal(
            input.Select(BitConverter.DoubleToUInt64Bits),
            aliased.Select(BitConverter.DoubleToUInt64Bits));
    }

    [Fact(DisplayName = "Current NTSC burst deemphasis uses the PR 341 boundary")]
    public void CurrentNtscBurstDeemphasisUsesPr341Boundary()
    {
        double[] samples = Enumerable.Repeat(1.0, 16).ToArray();

        double[] legacy = VhsChromaDecoder.ApplyBurstDeemphasis(
            samples,
            lineOffset: 1,
            linesOut: 2,
            lineLength: 8,
            burstStart: 1,
            burstEnd: 2);
        double[] current = VhsChromaDecoder.ApplyBurstDeemphasis(
            samples,
            lineOffset: 1,
            linesOut: 2,
            lineLength: 8,
            burstStart: 1,
            burstEnd: 2,
            samplesAfterBurst: 4);
        double[] currentOwned = samples.ToArray();
        VhsChromaDecoder.ApplyBurstDeemphasisInPlace(
            currentOwned,
            lineOffset: 1,
            linesOut: 2,
            lineLength: 8,
            burstStart: 1,
            burstEnd: 2,
            samplesAfterBurst: 4);

        Assert.Equal(1.0, legacy[6]);
        Assert.Equal(2.0, current[6]);
        Assert.Equal(2.0, legacy[7]);
        Assert.Equal(2.0, current[7]);
        Assert.Equal(
            current.Select(BitConverter.DoubleToUInt64Bits),
            currentOwned.Select(BitConverter.DoubleToUInt64Bits));
    }

    [Fact(DisplayName = "Current chroma comb in-place path matches the copying reference bit-exactly")]
    public void CurrentChromaCombInPlaceMatchesCopyingReferenceBitExactly()
    {
        const int LineLength = 37;
        const int LineCount = 41;
        double[] input = BuildInput(LineLength * LineCount);

        foreach (bool retainFloat32 in new[] { false, true })
        {
            double[] expectedNtsc = ApplyCombReference(
                input,
                LineLength,
                lineDistance: 1,
                retainFloat32);
            double[] copyingNtsc = VhsChromaDecoder.ApplyNtscComb(
                input,
                LineLength,
                retainFloat32);
            double[] actualNtsc = input.ToArray();
            VhsChromaDecoder.ApplyNtscCombInPlace(
                actualNtsc,
                LineLength,
                retainFloat32);
            Assert.Equal(
                expectedNtsc.Select(BitConverter.DoubleToUInt64Bits),
                copyingNtsc.Select(BitConverter.DoubleToUInt64Bits));
            Assert.Equal(
                expectedNtsc.Select(BitConverter.DoubleToUInt64Bits),
                actualNtsc.Select(BitConverter.DoubleToUInt64Bits));

            double[] expectedPal = ApplyCombReference(
                input,
                LineLength,
                lineDistance: 2,
                retainFloat32);
            double[] copyingPal = VhsChromaDecoder.ApplyPalComb(
                input,
                LineLength,
                retainFloat32);
            double[] actualPal = input.ToArray();
            VhsChromaDecoder.ApplyPalCombInPlace(
                actualPal,
                LineLength,
                retainFloat32);
            Assert.Equal(
                expectedPal.Select(BitConverter.DoubleToUInt64Bits),
                copyingPal.Select(BitConverter.DoubleToUInt64Bits));
            Assert.Equal(
                expectedPal.Select(BitConverter.DoubleToUInt64Bits),
                actualPal.Select(BitConverter.DoubleToUInt64Bits));
        }
    }

    [Fact(DisplayName = "Current NTSC phase compensation and comb reuse owned storage")]
    public void CurrentNtscPhaseCompensationAndCombReuseOwnedStorage()
    {
        const int LineLength = 1_135;
        const int LineCount = 273;
        int sampleCount = LineLength * LineCount;
        double[] chroma = BuildInput(sampleCount);
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 0.0) with
        {
            OutputLineLength = LineLength,
            OutputLineCount = LineCount
        };
        ChromaPhaseLine[] phaseLines = Enumerable.Range(0, LineCount)
            .Select(line => new ChromaPhaseLine(
                LineNumber: line,
                PhaseRotation: line & 3,
                BurstPhaseDegrees: (line & 1) == 0 ? 12.5 : -7.25)
            {
                BurstStart = line * LineLength,
                BurstAmplitude = 72.0,
                BurstDc = (line % 5) * 0.125,
                BurstFrequencyHz = options.FscMHz * 1_000_000.0
            })
            .ToArray();
        var phase = new ChromaPhaseSequenceResult(
            NextChromaRotationIndex: 0,
            PhaseSequence: phaseLines,
            BurstDetectedLine: 0,
            BurstMagnitudeAverage: 72.0,
            BurstPhaseAverageDegrees: 0.0,
            EvenBurstPhaseAverageDegrees: 12.5,
            OddBurstPhaseAverageDegrees: -7.25);

        _ = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            chroma.ToArray(),
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0);
        VhsChromaFieldResult expected = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0);
        double[] ownedChroma = chroma.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsChromaFieldResult result = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            ownedChroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(result);
        Assert.Equal(expected.Samples, result.Samples);
        long maximumExpected =
            ((long)sampleCount * sizeof(ushort))
            + (256 * 1024);
        Assert.True(
            allocated < maximumExpected,
            $"Current NTSC chroma decode allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "Current PAL upconversion and comb reuse owned storage")]
    public void CurrentPalUpconversionAndCombReuseOwnedStorage()
    {
        const int LineLength = 1_135;
        const int LineCount = 273;
        int sampleCount = LineLength * LineCount;
        double[] chroma = BuildInput(sampleCount);
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 0.0) with
        {
            ColorSystem = "PAL",
            OutputLineLength = LineLength,
            OutputLineCount = LineCount
        };
        ChromaPhaseLine[] phaseLines = Enumerable.Range(0, LineCount)
            .Select(line => new ChromaPhaseLine(
                LineNumber: line,
                PhaseRotation: line & 3,
                BurstPhaseDegrees: (line & 1) == 0 ? 12.5 : -7.25)
            {
                BurstStart = line * LineLength,
                BurstAmplitude = 72.0,
                BurstDc = (line % 5) * 0.125,
                BurstFrequencyHz = options.FscMHz * 1_000_000.0
            })
            .ToArray();
        var phase = new ChromaPhaseSequenceResult(
            NextChromaRotationIndex: 0,
            PhaseSequence: phaseLines,
            BurstDetectedLine: 0,
            BurstMagnitudeAverage: 72.0,
            BurstPhaseAverageDegrees: 0.0,
            EvenBurstPhaseAverageDegrees: 12.5,
            OddBurstPhaseAverageDegrees: -7.25);
        double[][] heterodyne = VhsChromaDecoder.BuildHeterodyneTable(
            sampleCount,
            options.FscMHz,
            options.ColorUnderCarrierHz / 1_000_000.0,
            options.FscMHz * 4.0,
            workerThreads: options.WorkerThreads);
        var analysis = new VhsChromaPhaseAnalysis(
            phase,
            heterodyne,
            options.ColorUnderCarrierHz,
            HeterodynePhaseRadians: 0.0);

        _ = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            chroma.ToArray(),
            options,
            analysis,
            outputDestination: new ushort[sampleCount]);
        var expectedDestination = new ushort[sampleCount];
        VhsChromaFieldResult expected = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            analysis,
            outputDestination: expectedDestination);
        double[] ownedChroma = chroma.ToArray();
        var actualDestination = new ushort[sampleCount];
        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsChromaFieldResult actual = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            ownedChroma,
            options,
            analysis,
            outputDestination: actualDestination);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(actual);
        Assert.Same(actualDestination, actual.Samples);
        Assert.Equal(expected.Samples, actual.Samples);
        Assert.NotEqual(
            chroma.Select(BitConverter.DoubleToUInt64Bits),
            ownedChroma.Select(BitConverter.DoubleToUInt64Bits));
        Assert.True(
            allocated < 256 * 1024,
            $"Current PAL chroma decode allocated {allocated:N0} bytes.");

        double[] retainedOwnedChroma = chroma.ToArray();
        double[]? retainedFilterInput = null;
        _ = VhsChromaDecoder.DecodeOwnedFieldWithPhase(
            retainedOwnedChroma,
            options,
            analysis,
            finalFilter: values =>
            {
                retainedFilterInput = values;
                return values;
            },
            outputDestination: new ushort[sampleCount]);
        Assert.NotNull(retainedFilterInput);
        Assert.NotSame(retainedOwnedChroma, retainedFilterInput);
    }

    private static VhsChromaFieldOptions CreateOptions(double ctiMix)
        => new(
            ColorSystem: "NTSC",
            OutputLineLength: 64,
            OutputLineCount: 48,
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
            UseCurrentChromaProcessing = true,
            SyncTipLength = 20,
            CtiMix = ctiMix,
            CtiWidth = 2
        };

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

    private static double[] ApplyCombReference(
        ReadOnlySpan<double> chroma,
        int lineLength,
        int lineDistance,
        bool retainFloat32)
    {
        double[] output = chroma.ToArray();
        int lineCount = chroma.Length / lineLength;
        for (int line = 16; line < lineCount - 2; line++)
        {
            int lineStart = line * lineLength;
            int advancedStart = (line + lineDistance) * lineLength;
            int delayedStart = (line - lineDistance) * lineLength;
            for (int index = 0; index < lineLength; index++)
            {
                double combined = !retainFloat32 && lineDistance == 2
                    ? ((chroma[lineStart + index] * 2.0)
                        - chroma[delayedStart + index]
                        - chroma[advancedStart + index]) / 4.0
                    : ((chroma[lineStart + index] * 2.0)
                        - chroma[advancedStart + index]
                        - chroma[delayedStart + index]) / 4.0;
                output[lineStart + index] =
                    retainFloat32 ? (double)(float)combined : combined;
            }
        }

        return output;
    }

    private static string Sha256(ushort[] samples)
        => Convert.ToHexString(
            SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan())));
}
