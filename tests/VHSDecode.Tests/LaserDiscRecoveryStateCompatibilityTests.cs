using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscRecoveryStateCompatibilityTests
{
    [Theory(DisplayName = "LD recovery discards previous field context like v0.4.0")]
    [InlineData(TbcFieldDecodeRecoveryKind.NoSyncPulses)]
    [InlineData(TbcFieldDecodeRecoveryKind.NoFirstHSync)]
    [InlineData(TbcFieldDecodeRecoveryKind.InsufficientData)]
    public void LdRecoveryDiscardsPreviousFieldContextLikeV040(
        TbcFieldDecodeRecoveryKind recoveryKind)
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "recovery"));
            TbcFieldDecodeState initial = session.TbcFieldDecoder.CaptureState();
            session.TbcFieldDecoder.RestoreStateForRetry(initial with
            {
                PreviousAnalogAudioStartSample = 123_000,
                PreviousAnalogAudioFieldNumber = 7,
                PreviousFirstHSyncLocation = 1_000.0,
                PreviousFirstHSyncReadLocation = 100_000,
                PreviousSyncConfidence = 90,
                PreviousLaserDiscPalEndLineAbsoluteSample = 900_000.0,
                PreviousCvbsEndLineAbsoluteSample = 800_000.0,
                PreviousDetectedFirstField = true,
                PreviousHSyncDifference = 0.25,
                LaserDiscNtscPhaseAdjustMedian = 0.5,
                PreviousLaserDiscPalFieldPhaseId = 6,
                PreviousLaserDiscPalPhaseAdjustments = new Dictionary<int, double>
                {
                    [7] = 0.125
                },
                PreviousLaserDiscSkipCheckScore = 75
            });
            int attempts = 0;
            TbcFieldDecodeState? recoveredState = null;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (_, _, _, _, _) => ++attempts == 1
                    ? throw new TbcFieldDecodeRecoveryException(
                        recoveryKind,
                        suggestedOffsetSamples: 512_000,
                        message: "synthetic LD recovery",
                        stopAfterDecodedFields: recoveryKind == TbcFieldDecodeRecoveryKind.NoSyncPulses)
                    : CaptureRecoveredState());

            TbcDecodedField? CaptureRecoveredState()
            {
                recoveredState = session.TbcFieldDecoder.CaptureState();
                return null;
            }

            IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.Empty(fields);
            Assert.Equal(2, attempts);
            Assert.NotNull(recoveredState);
            Assert.Equal(123_000, recoveredState.PreviousAnalogAudioStartSample);
            Assert.Equal(7, recoveredState.PreviousAnalogAudioFieldNumber);
            Assert.Null(recoveredState.PreviousFirstHSyncLocation);
            Assert.Null(recoveredState.PreviousFirstHSyncReadLocation);
            Assert.Null(recoveredState.PreviousSyncConfidence);
            Assert.Null(recoveredState.PreviousLaserDiscPalEndLineAbsoluteSample);
            Assert.Equal(800_000.0, recoveredState.PreviousCvbsEndLineAbsoluteSample);
            Assert.Null(recoveredState.PreviousDetectedFirstField);
            Assert.Equal(-1.0, recoveredState.PreviousHSyncDifference);
            Assert.Equal(0.0, recoveredState.LaserDiscNtscPhaseAdjustMedian);
            Assert.Null(recoveredState.PreviousLaserDiscPalFieldPhaseId);
            Assert.Null(recoveredState.PreviousLaserDiscPalPhaseAdjustments);
            Assert.Equal(0, recoveredState.PreviousLaserDiscSkipCheckScore);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateSession(string outputBase)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--threads",
            "0",
            "--disable_analog_audio",
            "--noEFM",
            "input.s16",
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }
}
