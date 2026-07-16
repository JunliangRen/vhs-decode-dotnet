using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscNtscVitsNumericsCompatibilityTests
{
    [Fact(DisplayName = "NTSC LD line-19 VITS statistics match Release 4.0 float32 bits")]
    public void NtscLaserDiscLine19VitsStatisticsMatchReleaseFourBits()
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
        [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            "outbase"
        ]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        ushort[] samples = Enumerable.Repeat(
                (ushort)70,
                session.TbcFrameSpec.FieldSampleCount)
            .ToArray();

        int lineStart = 18 * session.TbcFrameSpec.OutputLineLength;
        int lineEnd = (int)Math.Round(
            lineStart + (40.0 * session.TbcFrameSpec.OutputSampleRateHz / 1_000_000.0),
            MidpointRounding.ToEven);
        Assert.Equal(573, lineEnd - lineStart);

        ulong state = 1;
        for (int index = lineStart; index < lineEnd; index++)
        {
            state = unchecked(
                (state * 6_364_136_223_846_793_005UL)
                + 1_442_695_040_888_963_407UL);
            samples[index] = (ushort)(41 + ((state >> 32) % 59));
        }

        bool computed = TbcOutputMetadataWriter.TryComputeNtscLine19ColorInfo(
            session,
            samples,
            fieldPhaseId: 1,
            previousSamples: null,
            previousFieldPhaseId: null,
            out TbcOutputMetadataWriter.LaserDiscNtscLine19ColorInfo info);

        Assert.True(computed);
        Assert.Equal(0x3FA2ACE616DB6DB7UL, BitConverter.DoubleToUInt64Bits(info.Level));
        Assert.Equal(0x406F20EEA0000000UL, BitConverter.DoubleToUInt64Bits(info.PhaseDegrees));
        Assert.Equal(0x401ADB1B40000000UL, BitConverter.DoubleToUInt64Bits(info.RawSnr));
    }
}
