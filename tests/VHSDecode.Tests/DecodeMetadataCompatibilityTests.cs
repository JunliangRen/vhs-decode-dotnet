using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DecodeMetadataCompatibilityTests
{
    [Theory(DisplayName = "VHS fallback VSync implicit formats match v0.4.0")]
    [InlineData("NTSC", "TYPEC")]
    [InlineData("NTSC", "EIAJ")]
    [InlineData("405", "BETAMAX")]
    [InlineData("819", "QUADRUPLEX")]
    public void VhsFallbackVsyncImplicitFormatsMatchV040(string system, string tapeFormat)
    {
        using DecodeSession session = CreateVhsSession(system, tapeFormat, "fallback");

        Assert.True(session.SyncDetectionOptions.UseFallbackVSync);
    }

    [Theory(DisplayName = "VHS PAL-parent metadata system matches v0.4.0")]
    [InlineData("MESECAM", "VHS")]
    [InlineData("405", "BETAMAX")]
    [InlineData("819", "QUADRUPLEX")]
    public void VhsPalParentMetadataSystemMatchesV040(string system, string tapeFormat)
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using DecodeSession session = CreateVhsSession(
                system,
                tapeFormat,
                Path.Combine(tempDirectory, "capture"));
            JsonObject header = TbcOutputMetadataWriter.BuildHeader(session, fieldCount: 1);
            JsonObject videoParameters = header["videoParameters"]?.AsObject()
                ?? throw new InvalidOperationException("The metadata header did not contain video parameters.");
            Assert.Equal("PAL", videoParameters["system"]?.GetValue<string>());

            string databasePath = Path.Combine(tempDirectory, "capture.tbc.db");
            TbcSqliteMetadataWriter.Write(session, [BuildField()], databasePath);
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT system FROM capture LIMIT 1";
            Assert.Equal("PAL", command.ExecuteScalar() as string);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "VHS NTSC fallback field phase uses detected parity like v0.4.0")]
    public void VhsNtscFallbackFieldPhaseUsesDetectedParityLikeV040()
    {
        using DecodeSession session = CreateVhsSession("NTSC", "VHS", "phase");
        var builder = new TbcOutputMetadataWriter.FieldObjectBuilder(session);
        TbcFieldOrderDecision correctedDecision = BuildDecision(
            seqNo: 2,
            isFirstField: false,
            detectedFirstField: true,
            syncConfidence: 0,
            decodeFaults: 4);

        JsonObject field = builder.Add(BuildField() with { SyncConfidence = 5 }, correctedDecision);

        Assert.Equal(3, field["fieldPhaseID"]?.GetValue<int>());
    }

    [Theory(DisplayName = "Progressive field correction forces sync confidence 10 like v0.4.0")]
    [InlineData(1)]
    [InlineData(3)]
    public void ProgressiveFieldCorrectionForcesSyncConfidenceTenLikeV040(int decodeFaults)
    {
        using DecodeSession session = CreateVhsSession("NTSC", "VHS", "sync-confidence");
        var builder = new TbcOutputMetadataWriter.FieldObjectBuilder(session);

        JsonObject corrected = builder.Add(
            BuildField() with { SyncConfidence = 5 },
            BuildDecision(
                seqNo: 2,
                isFirstField: false,
                detectedFirstField: true,
                syncConfidence: 10,
                decodeFaults: decodeFaults));
        JsonObject uncorrected = builder.Add(
            BuildField() with { SyncConfidence = 5 },
            BuildDecision(
                seqNo: 3,
                isFirstField: true,
                detectedFirstField: true,
                syncConfidence: 100,
                decodeFaults: 0));

        Assert.Equal(10, corrected["syncConf"]?.GetValue<int>());
        Assert.Equal(5, uncorrected["syncConf"]?.GetValue<int>());
    }

    private static DecodeSession CreateVhsSession(string system, string tapeFormat, string outputBase)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--system",
            system,
            "--tape_format",
            tapeFormat,
            "input.u8",
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField()
    {
        return new TbcDecodedField(
            StartSample: 0,
            Samples: [],
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: true,
            DetectedFirstFieldConfidence: 100);
    }

    private static TbcFieldOrderDecision BuildDecision(
        int seqNo,
        bool isFirstField,
        bool detectedFirstField,
        int syncConfidence,
        int decodeFaults)
    {
        return new TbcFieldOrderDecision(
            SeqNo: seqNo,
            IsFirstField: isFirstField,
            DetectedFirstField: detectedFirstField,
            IsDuplicateField: false,
            WriteField: true,
            SyncConfidence: syncConfidence,
            DecodeFaults: decodeFaults);
    }
}
