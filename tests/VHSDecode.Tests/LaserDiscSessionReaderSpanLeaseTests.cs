using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscSessionReaderSpanLeaseTests
{
    [Theory(DisplayName = "LD session reader leases preserve retained fields and verbose VITS ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public void LaserDiscSessionReaderLeasesPreserveRetainedFieldsAndVerboseVitsOwnership(bool verboseVits)
    {
        string directory = CreateTempDirectory();
        try
        {
            using DecodeSession eagerSession = CreateSession(Path.Combine(directory, "eager"), verboseVits);
            using DecodeSession leasedSession = CreateSession(Path.Combine(directory, "leased"), verboseVits);
            byte[] inputBytes = BuildPalLaserDiscRf();
            using var eagerInput = new MemoryStream(inputBytes, writable: false);
            using var leasedInput = new MemoryStream(inputBytes, writable: false);
            var eagerEngine = new TbcFieldSequenceDecodeEngine(readField: ReadEagerField)
            {
                UseSessionFieldNumberingForCustomReader = true
            };

            IReadOnlyList<TbcDecodedField> expected = eagerEngine.DecodeFields(eagerSession, eagerInput, maxFields: 2);
            IReadOnlyList<TbcDecodedField> actual = new TbcFieldSequenceDecodeEngine()
                .DecodeFields(leasedSession, leasedInput, maxFields: 2);

            Assert.Equal(2, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(0, eagerSession.StreamDecoder.CachedReusableSpanBufferSetCount);
            Assert.Equal(verboseVits, leasedSession.TbcFieldDecoder.PreservesRawMetricSources);
            Assert.InRange(
                leasedSession.StreamDecoder.CachedReusableSpanBufferSetCount,
                verboseVits ? 0 : 1,
                verboseVits ? 0 : 2);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Samples, actual[i].Samples);
                Assert.Equal(expected[i].Efm, actual[i].Efm);
                Assert.Equal(expected[i].AudioPcm, actual[i].AudioPcm);
                Assert.Equal(expected[i].RfTbc, actual[i].RfTbc);
                Assert.NotEmpty(actual[i].Efm!);
                Assert.NotEmpty(actual[i].AudioPcm!);
                Assert.NotEmpty(actual[i].RfTbc!);
                Assert.Equal(expected[i].RawInputSamples, actual[i].RawInputSamples);
                Assert.Equal(expected[i].PreTbcVideoSamples, actual[i].PreTbcVideoSamples);
                Assert.Equal(verboseVits, actual[i].RawInputSamples is not null);
                Assert.Equal(verboseVits, actual[i].PreTbcVideoSamples is not null);
                Assert.Equal(DescribeField(expected[i]), DescribeField(actual[i]));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD session reader returns span buffers after recovery and EOF")]
    public void LaserDiscSessionReaderReturnsSpanBuffersAfterRecoveryAndEof()
    {
        string directory = CreateTempDirectory();
        try
        {
            using DecodeSession session = CreateSession(Path.Combine(directory, "recovery"), verboseVits: false);
            byte[] inputBytes = BuildPalLaserDiscRf();
            int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(session, extraReadLines: 3);
            using var noSyncInput = new MemoryStream(new byte[inputBytes.Length], writable: false);
            Assert.Throws<TbcFieldDecodeRecoveryException>(() => ReadSessionField(session, noSyncInput, readLength));
            Assert.Equal(1, session.StreamDecoder.CachedReusableSpanBufferSetCount);
            session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();

            using var validInput = new MemoryStream(inputBytes, writable: false);
            TbcDecodedField first = Assert.IsType<TbcDecodedField>(ReadSessionField(session, validInput, readLength));
            ushort[] expectedSamples = first.Samples.ToArray();
            short[] expectedEfm = first.Efm!.ToArray();
            short[] expectedAudio = first.AudioPcm!.ToArray();
            short[] expectedRfTbc = first.RfTbc!.ToArray();
            Assert.Equal(1, session.StreamDecoder.CachedReusableSpanBufferSetCount);

            using var emptyInput = new MemoryStream();
            Assert.Null(ReadSessionField(session, emptyInput, readLength));
            Assert.Equal(1, session.StreamDecoder.CachedReusableSpanBufferSetCount);
            session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
            TbcDecodedField repeated = Assert.IsType<TbcDecodedField>(ReadSessionField(session, validInput, readLength));

            Assert.Equal(expectedSamples, repeated.Samples);
            Assert.Equal(expectedSamples, first.Samples);
            Assert.Equal(expectedEfm, first.Efm);
            Assert.Equal(expectedAudio, first.AudioPcm);
            Assert.Equal(expectedRfTbc, first.RfTbc);
            Assert.Equal(1, session.StreamDecoder.CachedReusableSpanBufferSetCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TbcDecodedField? ReadEagerField(
        DecodeSession session,
        Stream input,
        long begin,
        int readLength,
        int fieldNumber)
    {
        DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(session, begin, readLength);
        RfDecodedSpan? span = session.StreamDecoder.Read(input, window.StartSample, window.SampleCount);
        return span is null ? null : session.TbcFieldDecoder.Decode(span, fieldNumber: fieldNumber);
    }

    private static TbcDecodedField? ReadSessionField(DecodeSession session, Stream input, int readLength)
    {
        MethodInfo reader = typeof(TbcFieldSequenceDecodeEngine).GetMethod(
            "ReadFieldFromSession",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return (TbcDecodedField?)reader.Invoke(null, [session, input, 0L, readLength, 0, true]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static string DescribeField(TbcDecodedField field)
        => JsonSerializer.Serialize(field with
        {
            Samples = [],
            Efm = null,
            AudioPcm = null,
            RfTbc = null,
            RawInputSamples = null,
            PreTbcVideoSamples = null
        }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });

    private static DecodeSession CreateSession(string outputBase, bool verboseVits)
    {
        List<string> arguments = ["--PAL", "--threads", "2", "--noAGC", "--RF_TBC"];
        if (verboseVits)
        {
            arguments.Add("--verboseVITS");
        }

        arguments.Add("input.s16");
        arguments.Add(outputBase);
        return DecodeSessionFactory.Create(new CommandLineParser().Parse(CliSpecs.LaserDisc, arguments));
    }

    private static byte[] BuildPalLaserDiscRf()
    {
        const int lineSamples = 2_560;
        const int lineCount = 800;
        var samples = new short[lineSamples * lineCount];
        double phase = 0.0;
        for (int line = 0; line < lineCount; line++)
        {
            int fieldLine = line >= 10 ? (line - 10) % 313 : -1;
            for (int sample = 0; sample < lineSamples; sample++)
            {
                bool sync;
                if (fieldLine is >= 0 and <= 7)
                {
                    int firstLength = fieldLine is 3 or 4 or 5 ? 1_092 : 94;
                    int secondLength = fieldLine is 3 or 4 ? 1_092 : 94;
                    sync = sample < firstLength || (sample >= 1_280 && sample < 1_280 + secondLength);
                }
                else
                {
                    sync = sample < 188;
                }

                double frequencyHz = sync ? 6_757_143.0 : 7_100_000.0;
                if (!sync && fieldLine is not (>= 0 and <= 7) && sample >= 400)
                {
                    frequencyHz += 150_000.0 + (line * 500.0) + ((sample % 320) * 150.0);
                }

                phase += Math.Tau * frequencyHz / 40_000_000.0;
                samples[(line * lineSamples) + sample] = (short)Math.Round(12_000.0 * Math.Cos(phase));
            }
        }

        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vhsdecode-ld-lease-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
