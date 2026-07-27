using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsUnclassifiedPulseCadenceCompatibilityTests
{
    [Fact(DisplayName = "VHS unclassified raw pulses advance cadence before line-zero recovery")]
    public void UnclassifiedRawPulsesAdvanceCadenceBeforeRecovery()
    {
        string outputBase = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--system",
            "ntsc",
            "--frequency",
            "40",
            "--fallback_vsync",
            "--threads",
            "0",
            "input.s16",
            outputBase
        ]);
        using DecodeSession session = DecodeSessionFactory.Create(command);

        double[] malformedPulses = new double[50_000];
        for (int pulse = 0; pulse < 24; pulse++)
        {
            Array.Fill(malformedPulses, -40.0, 100 + (pulse * 1_500), 20);
        }

        var span = new RfDecodedSpan(
            StartSample: 0,
            Input: malformedPulses,
            Video: malformedPulses,
            DemodRaw: malformedPulses);

        TbcFieldDecodeRecoveryException exception =
            Assert.Throws<TbcFieldDecodeRecoveryException>(() =>
                session.TbcFieldDecoder.Decode(span, syncThresholdHz: -20.0));

        Assert.Equal(TbcFieldDecodeRecoveryKind.NoFirstHSync, exception.Kind);
        Assert.True(session.TbcFieldDecoder.CaptureState().PreviousDetectedFirstField);
    }
}
