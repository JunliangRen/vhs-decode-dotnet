using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscSeekRecoveryCompatibilityTests
{
    [Fact(DisplayName = "LD seek skips invalid fields without consuming valid-field state like v0.4.0")]
    public void LdSeekSkipsInvalidFieldsLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(tempDirectory);
            var error = new StringWriter();
            session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);
            DecodeSessionLogWriter.Write(session);
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            long initialProbe = 12L * 2L * nominalFieldSamples;
            var readBegins = new List<long>();
            var fieldNumbers = new List<int>();
            int attempts = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    readBegins.Add(begin);
                    fieldNumbers.Add(fieldNumber);
                    return ++attempts switch
                    {
                        1 => throw new TbcFieldDecodeRecoveryException(
                            TbcFieldDecodeRecoveryKind.NoFirstHSync,
                            suggestedOffsetSamples: 50,
                            message: "synthetic invalid seek field"),
                        2 => BuildField(activeSession, begin, fieldNumber, nominalFieldSamples),
                        3 => BuildField(
                            activeSession,
                            begin,
                            fieldNumber,
                            nominalFieldSamples,
                            EncodeCavFrameCode(12)),
                        _ => throw new InvalidOperationException("Unexpected LD seek read attempt.")
                    };
                });

            IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
                session,
                Stream.Null,
                maxFields: 0);

            Assert.Empty(fields);
            Assert.Equal(
                [initialProbe, initialProbe + 50, initialProbe + 50 + nominalFieldSamples],
                readBegins);
            Assert.Equal([0, 0, 1], fieldNumbers);
            Assert.Contains(
                "Unable to determine start of field - dropping field",
                error.ToString(),
                StringComparison.Ordinal);
            string[] messages = error.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Beginning seek", messages[0]);
            Assert.Equal("Unable to determine start of field - dropping field", messages[1]);
            Assert.StartsWith("seeking: file loc ", messages[2], StringComparison.Ordinal);
            Assert.EndsWith(" frame # 12", messages[2], StringComparison.Ordinal);
            Assert.Equal("Finished seek", messages[3]);
            Assert.Equal("Finished seeking, starting at frame 12", messages[4]);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD seek scans beyond the first valid VBI pair like v0.4.0")]
    public void LdSeekScansBeyondFirstValidPairLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(tempDirectory);
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            long initialProbe = 12L * 2L * nominalFieldSamples;
            var readBegins = new List<long>();
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    readBegins.Add(begin);
                    return BuildField(
                        activeSession,
                        begin,
                        fieldNumber,
                        nominalFieldSamples,
                        fieldNumber == 2 ? EncodeCavFrameCode(12) : null);
                });

            IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
                session,
                Stream.Null,
                maxFields: 0);

            Assert.Empty(fields);
            Assert.Equal(
                [initialProbe, initialProbe + nominalFieldSamples, initialProbe + (2 * nominalFieldSamples)],
                readBegins);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD seek falls back to file start within the same probe budget like v0.4.0")]
    public void LdSeekFallsBackToFileStartLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(tempDirectory);
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            long initialProbe = 12L * 2L * nominalFieldSamples;
            var readBegins = new List<long>();
            int attempts = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    readBegins.Add(begin);
                    attempts++;
                    if (attempts == 1)
                    {
                        return null;
                    }

                    return BuildField(
                        activeSession,
                        begin,
                        fieldNumber,
                        nominalFieldSamples,
                        attempts == 3 ? EncodeCavFrameCode(12) : null);
                });

            IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
                session,
                Stream.Null,
                maxFields: 0);

            Assert.Empty(fields);
            Assert.Equal([initialProbe, 0, nominalFieldSamples], readBegins);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD seek stops after ten decode attempts like v0.4.0")]
    public void LdSeekStopsAfterTenDecodeAttemptsLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(tempDirectory);
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            long initialProbe = 12L * 2L * nominalFieldSamples;
            Complex[] normalMtf = session.Filters.RfMtf.ToArray();
            Complex[] seekMtf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
                session.Parameters,
                session.FilterOptions,
                targetMtf: 0.0,
                session.DecodeSampleRateHz,
                session.BlockLength);
            var readBegins = new List<long>();
            var fieldNumbers = new List<int>();
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    Assert.True(seekMtf.SequenceEqual(activeSession.Filters.RfMtf));
                    readBegins.Add(begin);
                    fieldNumbers.Add(fieldNumber);
                    throw new TbcFieldDecodeRecoveryException(
                        TbcFieldDecodeRecoveryKind.NoFirstHSync,
                        suggestedOffsetSamples: 1,
                        message: "synthetic invalid seek field");
                });

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                engine.DecodeFields(session, Stream.Null, maxFields: 0));

            Assert.Equal("ERROR: Seeking failed", exception.Message);
            Assert.Equal(10, readBegins.Count);
            Assert.Equal(
                Enumerable.Range(0, 10).Select(offset => initialProbe + offset),
                readBegins);
            Assert.All(fieldNumbers, fieldNumber => Assert.Equal(0, fieldNumber));
            Assert.True(normalMtf.SequenceEqual(session.Filters.RfMtf));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD seek rejects early CLV without timecode like v0.4.0")]
    public void LdSeekRejectsEarlyClvLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(tempDirectory);
            var error = new StringWriter();
            session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);
            DecodeSessionLogWriter.Write(session);
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            int attempts = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                    BuildField(
                        activeSession,
                        begin,
                        fieldNumber,
                        nominalFieldSamples,
                        ++attempts == 2 ? 0xF0DD01 : null));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                engine.DecodeFields(session, Stream.Null, maxFields: 0));

            Assert.Equal("ERROR: Seeking failed", exception.Message);
            Assert.Equal(2, attempts);
            Assert.Contains(
                "Cannot seek in early CLV disks w/o timecode",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain("Finished seek", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD seek uses MTF level zero and restores normal MTF like v0.4.0")]
    public void LdSeekUsesTemporaryZeroMtfLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(
                tempDirectory,
                "--MTF",
                "1.75",
                "--MTF_offset",
                "0.25");
            Complex[] seekMtf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
                session.Parameters,
                session.FilterOptions,
                targetMtf: 0.0,
                session.DecodeSampleRateHz,
                session.BlockLength);
            Complex[] normalMtf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
                session.Parameters,
                session.FilterOptions,
                targetMtf: 1.0,
                session.DecodeSampleRateHz,
                session.BlockLength);
            Assert.True(normalMtf.SequenceEqual(session.Filters.RfMtf));
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            int reads = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    Assert.True(seekMtf.SequenceEqual(activeSession.Filters.RfMtf));
                    reads++;
                    return BuildField(
                        activeSession,
                        begin,
                        fieldNumber,
                        nominalFieldSamples,
                        fieldNumber == 1 ? EncodeCavFrameCode(12) : null);
                });

            IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
                session,
                Stream.Null,
                maxFields: 0);

            Assert.Empty(fields);
            Assert.Equal(2, reads);
            Assert.True(normalMtf.SequenceEqual(session.Filters.RfMtf));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateSession(
        string tempDirectory,
        params string[] options)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--seek",
            "12",
            "--length",
            "0",
            "--threads",
            "0",
            "--disable_analog_audio",
            "--noEFM",
            .. options,
            "input.s16",
            Path.Combine(tempDirectory, "seek")
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(
        DecodeSession session,
        long begin,
        int fieldNumber,
        long nominalFieldSamples,
        int? vbiCode = null)
    {
        return new TbcDecodedField(
            StartSample: begin,
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
            DetectedFirstField: (fieldNumber & 1) == 0,
            DetectedFirstFieldConfidence: 100,
            NextFieldOffsetSamples: nominalFieldSamples,
            NominalFieldLengthSamples: nominalFieldSamples)
        {
            VbiData = vbiCode.HasValue ? [vbiCode.Value] : []
        };
    }

    private static int EncodeCavFrameCode(int frameNumber)
    {
        int value = frameNumber;
        int bcd = 0;
        int shift = 0;
        do
        {
            bcd |= (value % 10) << shift;
            value /= 10;
            shift += 4;
        }
        while (value > 0);

        return 0xF00000 | bcd;
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
