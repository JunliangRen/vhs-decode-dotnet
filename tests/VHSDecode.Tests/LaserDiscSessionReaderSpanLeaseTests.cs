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

    [Theory(DisplayName = "LD Envelope projection follows actual decoder options and never escapes a field")]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvelopeProjectionUsesActualOptionsAcrossDivergentSessionRecords(bool actualDemod)
    {
        string directory = CreateTempDirectory();
        try
        {
            using DecodeSession owner = CreateSession(Path.Combine(directory, "projection"), verboseVits: false);
            TbcFieldDecodePipeline actualDecoder = actualDemod
                ? owner.TbcFieldDecoder
                : new TbcFieldDecodePipeline(
                    VHSDecode.Core.Dsp.SyncAnalyzer.FromParameters(owner.Parameters, owner.DecodeSampleRateHz),
                    owner.TbcRenderer, owner.VideoOutput, owner.System,
                    TbcDropoutDetectionOptions.DefaultDdd, decodeType: "ld");
            DecodeSession session = owner with
            {
                TbcFieldDecoder = actualDecoder,
                DropoutOptions = actualDemod
                    ? TbcDropoutDetectionOptions.DefaultDdd
                    : TbcDropoutDetectionOptions.LaserDisc
            };
            Assert.Equal(actualDemod, session.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
            // Only owner is disposed: the record copy shares all its RF resources.
            using var input = new MemoryStream(BuildPalLaserDiscRf(), writable: false);
            int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(session, extraReadLines: 3);
            DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(session, 0, readLength);
            double[] pooledEnvelope;
            double[] expectedEnvelope;
            const long poisonBits = 0x7FF8_0123_4567_89AB;
            double poison = BitConverter.Int64BitsToDouble(poisonBits);
            using (var warmup = session.StreamDecoder.ReadLeased(input, window.StartSample, window.SampleCount)!)
            {
                pooledEnvelope = warmup.Span.Envelope!;
                expectedEnvelope = pooledEnvelope.ToArray();
                Array.Fill(pooledEnvelope, poison);
            }
            TbcDecodedField field = Assert.IsType<TbcDecodedField>(ReadSessionField(session, input, readLength));
            if (actualDemod)
                Assert.All(pooledEnvelope, sample => Assert.Equal(poisonBits, BitConverter.DoubleToInt64Bits(sample)));
            else
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expectedEnvelope.AsSpan())
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pooledEnvelope.AsSpan())));
            ushort[] samples = field.Samples.ToArray();
            short[]? efm = field.Efm?.ToArray(), pcm = field.AudioPcm?.ToArray(), rf = field.RfTbc?.ToArray();
            string description = DescribeField(field);
            using (var next = session.StreamDecoder.ReadLeased(input, window.StartSample, window.SampleCount)!)
            {
                Assert.Same(pooledEnvelope, next.Span.Envelope);
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expectedEnvelope.AsSpan())
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(next.Span.Envelope!.AsSpan())));
                Array.Fill(next.Span.Video, poison);
                Array.Fill(next.Span.Input, poison);
                Array.Fill(next.Span.Envelope!, poison);
                if (next.Span.Efm is { } nextEfm) Array.Fill(nextEfm, (short)-321);
            }
            Assert.Equal(samples, field.Samples);
            Assert.Equal(efm, field.Efm);
            Assert.Equal(pcm, field.AudioPcm);
            Assert.Equal(rf, field.RfTbc);
            Assert.Equal(description, DescribeField(field));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD first-field owned read ignores poisoned lease buffers and retains independent payloads")]
    public void FirstFieldOwnedReadIgnoresPoisonedLeaseBuffersAndRetainsIndependentPayloads()
    {
        string directory = CreateTempDirectory();
        try
        {
            using DecodeSession eagerSession = CreateSession(Path.Combine(directory, "first-eager"), verboseVits: false);
            using DecodeSession actualSession = CreateSession(Path.Combine(directory, "first-poisoned"), verboseVits: false);
            byte[] bytes = BuildPalLaserDiscRf();
            using var eagerInput = new MemoryStream(bytes, writable: false);
            using var actualInput = new MemoryStream(bytes, writable: false);
            int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(actualSession, extraReadLines: 3);
            DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(actualSession, 0, readLength);
            const long poisonBits = 0x7FF8_0123_4567_89AB;
            double poison = BitConverter.Int64BitsToDouble(poisonBits);
            // Match both readers' RF/cache/MTF histories; only the released pool bytes differ.
            using (var warmup = eagerSession.StreamDecoder.ReadLeased(
                eagerInput, window.StartSample, window.SampleCount)!)
                Assert.NotNull(warmup.Span.Envelope);
            double[] pooledEnvelope, pooledVideo;
            using (var warmup = actualSession.StreamDecoder.ReadLeased(
                actualInput, window.StartSample, window.SampleCount)!)
            {
                pooledEnvelope = warmup.Span.Envelope!;
                pooledVideo = warmup.Span.Video;
                Array.Fill(pooledEnvelope, poison);
                Array.Fill(pooledVideo, poison);
            }

            var engine = new TbcFirstFieldDecodeEngine();
            TbcDecodedField expected = engine.DecodeFirstField(eagerSession, eagerInput);
            TbcDecodedField actual = engine.DecodeFirstField(actualSession, actualInput);
            Assert.Equal(expected.Samples, actual.Samples);
            Assert.Equal(expected.Efm, actual.Efm);
            Assert.Equal(expected.AudioPcm, actual.AudioPcm);
            Assert.Equal(expected.RfTbc, actual.RfTbc);
            Assert.Equal(DescribeField(expected), DescribeField(actual));
            Assert.All(pooledEnvelope, value => Assert.Equal(poisonBits, BitConverter.DoubleToInt64Bits(value)));
            Assert.All(pooledVideo, value => Assert.Equal(poisonBits, BitConverter.DoubleToInt64Bits(value)));
            ushort[] samples = actual.Samples.ToArray();
            short[]? efm = actual.Efm?.ToArray(), pcm = actual.AudioPcm?.ToArray(), rf = actual.RfTbc?.ToArray();
            string description = DescribeField(actual);

            using (var expectedC = eagerSession.StreamDecoder.ReadLeased(
                eagerInput, window.StartSample, window.SampleCount)!)
            using (var actualC = actualSession.StreamDecoder.ReadLeased(
                actualInput, window.StartSample, window.SampleCount)!)
            {
                Assert.Same(pooledEnvelope, actualC.Span.Envelope);
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expectedC.Span.Envelope!.AsSpan())
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(actualC.Span.Envelope!.AsSpan())));
                Assert.All(actualC.Span.Envelope!, value => Assert.NotEqual(poisonBits, BitConverter.DoubleToInt64Bits(value)));
                Array.Fill(actualC.Span.Input, poison);
                Array.Fill(actualC.Span.Video, poison);
                Array.Fill(actualC.Span.Envelope!, poison);
                if (actualC.Span.Efm is { } sourceEfm) Array.Fill(sourceEfm, (short)-321);
            }
            Assert.Equal(samples, actual.Samples);
            Assert.Equal(efm, actual.Efm);
            Assert.Equal(pcm, actual.AudioPcm);
            Assert.Equal(rf, actual.RfTbc);
            Assert.Equal(description, DescribeField(actual));
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
            return (TbcDecodedField?)reader.Invoke(null, [session, input, 0L, readLength, 0, true, false]);
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
