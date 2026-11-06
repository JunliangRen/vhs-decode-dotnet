using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;
using OwnedReader = VHSDecode.Core.Decode.RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader;

namespace VHSDecode.Tests;

public sealed class OwnedLaserDiscDefaultRunnerTests
{
    private delegate DecodeSession OwnedSessionFactory(ParsedCommand command, out OwnedReader? reader);
    private delegate TbcDecodedField? FieldRead(
        DecodeSession session, Stream input, long begin, int length, int number, bool retainChroma,
        bool overlapLaserDiscRenderDropout);
    private static readonly OwnedSessionFactory CreateOwned = typeof(DecodeRunner)
        .GetMethod("CreatePrivateDefaultRunnerSession", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<OwnedSessionFactory>();

    [Fact(DisplayName = "Only the public default Runner has the owned factory identity and ordinary reader stays unique")]
    public void DefaultIdentityAndOrdinaryReaderRemainDistinct()
    {
        object expected = typeof(DecodeRunner).GetField("DefaultEngineFactory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        FieldInfo field = typeof(DecodeRunner).GetField("_engineFactory", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Same(expected, field.GetValue(new DecodeRunner()));
        Assert.NotSame(expected, field.GetValue(new DecodeRunner(ct => new TbcFieldSequenceDecodeEngine(cancellationToken: ct))));
        MethodInfo ordinary = typeof(TbcFieldSequenceDecodeEngine).GetMethod("ReadFieldFromSession", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(7, ordinary.GetParameters().Length);
    }

    [Theory(DisplayName = "Private default factory uses constructed preview and actual field capability")]
    [InlineData("normal", true)]
    [InlineData("preview", false)]
    [InlineData("verbose", false)]
    [InlineData("disabled", false)]
    [InlineData("vhs", false)]
    [InlineData("cvbs", false)]
    public void PrivateFactoryGatesOnActualConstructedMode(string mode, bool expected)
    {
        using var directory = new TempDirectory();
        using DecodeSession session = CreateOwned(Command(directory.File("factory"), mode), out OwnedReader? reader);
        Assert.Equal(expected, reader is not null);
        Assert.Equal(mode == "preview", session.ExecutionOptions.SuppressFileOutputs);
        Assert.Equal(expected, GetOwnedReader(session.StreamDecoder) is not null);
        if (reader is not null)
        {
            Assert.True(reader.IsFor(session.StreamDecoder));
            Assert.True(session.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
            Assert.Equal(session.BlockLength, reader.EnvelopeShape.Length);
        }
    }

    [Fact(DisplayName = "Public Session factory stays full even when its LD field capability could omit Envelope")]
    public void PublicFactoryKeepsOrdinaryFullDomain()
    {
        using var directory = new TempDirectory();
        using DecodeSession session = DecodeSessionFactory.Create(Command(directory.File("public")));
        Assert.True(session.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
        Assert.Null(GetOwnedReader(session.StreamDecoder));
        using var input = new MemoryStream(BuildPalLaserDiscRf(), writable: false);
        RfDecodedSpan span = Assert.IsType<RfDecodedSpan>(session.StreamDecoder.Read(input, 0, 64));
        Assert.Equal(64, Assert.IsType<double[]>(span.Envelope).Length);
    }

    [Theory(DisplayName = "Private factory adds no preview dictionary lookup for manual missing null or nonboolean values")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("nonbool")]
    public void ManualPreviewValuesPreserveOldFactoryLookupSequence(string mode)
    {
        using var directory = new TempDirectory();
        ParsedCommand parsed = Command(directory.File("manual"));
        var oldComparer = new TrackingComparer();
        var newComparer = new TrackingComparer();
        ParsedCommand oldCommand = ManualCommand(parsed, mode, oldComparer);
        ParsedCommand newCommand = ManualCommand(parsed, mode, newComparer);
        oldComparer.Calls.Clear();
        using DecodeSession oldSession = DecodeSessionFactory.Create(oldCommand);
        string[] expectedCalls = oldComparer.Calls.ToArray();
        newComparer.Calls.Clear();
        using DecodeSession newSession = CreateOwned(newCommand, out OwnedReader? reader);
        Assert.Equal(expectedCalls, newComparer.Calls.ToArray());
        Assert.False(oldSession.ExecutionOptions.SuppressFileOutputs);
        Assert.False(newSession.ExecutionOptions.SuppressFileOutputs);
        Assert.True(newSession.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
        Assert.NotNull(reader);
    }

    [Fact(DisplayName = "An injected Runner factory observes the ordinary full Session at its actual custom reader")]
    public void InjectedRunnerFactoryUsesOrdinarySession()
    {
        using var directory = new TempDirectory();
        string path = directory.File("custom-input.s16");
        File.WriteAllBytes(path, BuildPalLaserDiscRf());
        int reads = 0;
        bool? capturedPrivateDomain = null;
        int? capturedEnvelopeLength = null;
        var runner = new DecodeRunner(ct => new TbcFieldSequenceDecodeEngine(
            readField: (session, input, _, _, _) =>
            {
                reads++;
                capturedPrivateDomain = GetOwnedReader(session.StreamDecoder) is not null;
                capturedEnvelopeLength = session.StreamDecoder.Read(input, 0, 64)?.Envelope?.Length;
                return null;
            }, cancellationToken: ct));
        using var output = new StringWriter();
        using var error = new StringWriter();
        _ = runner.Run(Command(directory.File("custom"), input: path), output, error, TestContext.Current.CancellationToken);
        // Run translates reader exceptions into a failure result, so assertions
        // live outside the callback and cannot be swallowed by that boundary.
        Assert.True(reads > 0);
        Assert.True(capturedPrivateDomain is false);
        Assert.Equal(64, capturedEnvelopeLength);
    }

    [Fact(DisplayName = "Actual private engine matches the ordinary session controller and retains PAL payloads")]
    public void OwnedReaderMatchesOrdinaryFieldsAndRetainsIndependentPayloads()
    {
        using var directory = new TempDirectory();
        using DecodeSession eager = DecodeSessionFactory.Create(Command(directory.File("ordinary")));
        using DecodeSession owned = CreateOwned(Command(directory.File("owned")), out OwnedReader? optional);
        OwnedReader reader = Assert.IsType<OwnedReader>(optional);
        const long poisonBits = 0x7FF8_0D14_0BAD_F00D;
        double poison = BitConverter.Int64BitsToDouble(poisonBits);
        Array.Fill(reader.EnvelopeShape, poison);
        byte[] shapeBytes = MemoryMarshal.AsBytes(reader.EnvelopeShape.AsSpan()).ToArray();
        byte[] bytes = BuildPalLaserDiscRf();
        using var eagerInput = new MemoryStream(bytes, writable: false);
        using var ownedInput = new MemoryStream(bytes, writable: false);
        // Same default controller on both sides (_usesSessionReader=true).
        // Only the private field-reader capability differs. Ordinary block
        // production retains its original complete Envelope.
        var eagerEngine = new TbcFieldSequenceDecodeEngine(cancellationToken: TestContext.Current.CancellationToken);
        var engine = TbcFieldSequenceDecodeEngine.CreateForOwnedDefaultImmediateLaserDisc(reader, TestContext.Current.CancellationToken);
        Assert.True((bool)typeof(TbcFieldSequenceDecodeEngine).GetField("_usesSessionReader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!);
        // Record identity is intentionally not the contract: this compatible clone
        // keeps the same owning StreamDecoder and actual compatible LD decoder.
        DecodeSession compatible = owned with { DropoutOptions = TbcDropoutDetectionOptions.DefaultDdd };
        IReadOnlyList<TbcDecodedField> expected = eagerEngine.DecodeFields(eager, eagerInput, maxFields: 2);
        IReadOnlyList<TbcDecodedField> actual = engine.DecodeFields(compatible, ownedInput, maxFields: 2);
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) AssertFieldsEqual(expected[i], actual[i]);
        // This check is after both controller comparisons; no further baseline
        // fields are decoded after this independent public full-span read.
        RfDecodedSpan full = Assert.IsType<RfDecodedSpan>(eager.StreamDecoder.Read(eagerInput, 0, 64));
        double[] fullEnvelope = Assert.IsType<double[]>(full.Envelope);
        Assert.Equal(64, fullEnvelope.Length);
        Assert.Contains(fullEnvelope, value => value > 0.0);
        Assert.Null(GetOwnedReader(eager.StreamDecoder));
        Assert.True(shapeBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(reader.EnvelopeShape.AsSpan())));
        var samples = actual.Select(field => field.Samples.ToArray()).ToArray();
        var efm = actual.Select(field => field.Efm!.ToArray()).ToArray();
        var audio = actual.Select(field => field.AudioPcm!.ToArray()).ToArray();
        var rf = actual.Select(field => field.RfTbc!.ToArray()).ToArray();
        string[] metadata = actual.Select(DescribeField).ToArray();
        int length = DecodeReadWindowPlanner.EstimateReadSampleCount(owned, extraReadLines: 3);
        DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(owned, 0, length);
        using (var next = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(reader.Read(ownedInput, window.StartSample, window.SampleCount)))
        {
            Assert.Null(next.Span.Envelope);
            Array.Fill(next.Span.Input, poison);
            Array.Fill(next.Span.Video, poison);
            if (next.Span.Efm is { } leasedEfm) Array.Fill(leasedEfm, (short)-321);
        }
        Assert.True(shapeBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(reader.EnvelopeShape.AsSpan())));
        for (int i = 0; i < actual.Count; i++)
        {
            Assert.Equal(samples[i], actual[i].Samples);
            Assert.Equal(efm[i], actual[i].Efm);
            Assert.Equal(audio[i], actual[i].AudioPcm);
            Assert.Equal(rf[i], actual[i].RfTbc);
            Assert.Equal(metadata[i], DescribeField(actual[i]));
            AssertFieldsEqual(expected[i], actual[i]);
        }
    }

    [Fact(DisplayName = "Owned and ordinary controllers honor pre-canceled operation before input")]
    public void OwnedControllerKeepsExistingCancellationBoundary()
    {
        using var directory = new TempDirectory();
        using DecodeSession ordinary = DecodeSessionFactory.Create(Command(directory.File("cancel-ordinary")));
        using DecodeSession owner = CreateOwned(Command(directory.File("cancel-owned")), out OwnedReader? optional);
        OwnedReader reader = Assert.IsType<OwnedReader>(optional);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var oldEngine = new TbcFieldSequenceDecodeEngine(cancellationToken: cancellation.Token);
        var newEngine = TbcFieldSequenceDecodeEngine.CreateForOwnedDefaultImmediateLaserDisc(reader, cancellation.Token);
        using var oldInput = new CountingInput();
        using var newInput = new CountingInput();
        Assert.ThrowsAny<OperationCanceledException>(() => oldEngine.DecodeFields(ordinary, oldInput, maxFields: 1));
        Assert.ThrowsAny<OperationCanceledException>(() => newEngine.DecodeFields(owner, newInput, maxFields: 1));
        Assert.Equal(0, oldInput.ReadCalls);
        Assert.Equal(0, newInput.ReadCalls);
        Assert.Equal(0, oldInput.SeekCalls);
        Assert.Equal(0, newInput.SeekCalls);
    }

    [Theory(DisplayName = "Actual private read closure rejects incompatible capability or decoder owner before input")]
    [InlineData("stream-decoder")]
    [InlineData("non-ld")]
    [InlineData("raw-metrics")]
    public void ActualReadClosureRejectsInvalidBoundaryBeforeInput(string mismatch)
    {
        using var directory = new TempDirectory();
        using DecodeSession owner = CreateOwned(Command(directory.File("owner")), out OwnedReader? optional);
        OwnedReader token = Assert.IsType<OwnedReader>(optional);
        using DecodeSession other = DecodeSessionFactory.Create(Command(directory.File("other"), "verbose"));
        DecodeSession changed = mismatch switch
        {
            "stream-decoder" => owner with { StreamDecoder = other.StreamDecoder },
            "non-ld" => owner with { Spec = CliSpecs.Vhs },
            "raw-metrics" => owner with { TbcFieldDecoder = other.TbcFieldDecoder },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var engine = TbcFieldSequenceDecodeEngine.CreateForOwnedDefaultImmediateLaserDisc(token, TestContext.Current.CancellationToken);
        FieldRead read = GetActualReadDelegate(engine);
        using var input = new CountingInput();
        int length = DecodeReadWindowPlanner.EstimateReadSampleCount(owner, extraReadLines: 3);
        foreach (bool overlapLaserDiscRenderDropout in new[] { false, true })
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => read(changed, input, 0, length, 0, true, overlapLaserDiscRenderDropout));
            Assert.Equal("The private LD reader's immutable field capability changed.", error.Message);
            Assert.Equal(0, input.ReadCalls);
            Assert.Equal(0, input.SeekCalls);
        }
        // Only the original owner and other are disposed, never their sharing record copy.
    }

    private static FieldRead GetActualReadDelegate(TbcFieldSequenceDecodeEngine engine)
    {
        Delegate callback = Assert.IsAssignableFrom<Delegate>(typeof(TbcFieldSequenceDecodeEngine)
            .GetField("_readField", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine));
        return callback.Target is null
            ? callback.Method.CreateDelegate<FieldRead>()
            : callback.Method.CreateDelegate<FieldRead>(callback.Target);
    }

    private static OwnedReader? GetOwnedReader(RfBlockStreamDecoder decoder)
        => (OwnedReader?)typeof(RfBlockStreamDecoder).GetField("_ownedImmediateReader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(decoder);

    private static ParsedCommand Command(string output, string mode = "normal", string input = "input.s16")
    {
        DecodeCommandSpec spec = mode == "vhs" ? CliSpecs.Vhs : mode == "cvbs" ? CliSpecs.Cvbs : CliSpecs.LaserDisc;
        List<string> arguments = ["--pal", "--threads", "2"];
        if (spec == CliSpecs.LaserDisc)
        {
            arguments.AddRange(["--noAGC", "--length", "1"]);
            if (mode != "preview") arguments.Add("--RF_TBC");
            if (mode == "preview") arguments.Add("--preview-server");
            if (mode == "verbose") arguments.Add("--verboseVITS");
            if (mode == "disabled") arguments.Add("--noDOD");
        }
        arguments.Add(input);
        if (mode != "preview") arguments.Add(output);
        return new CommandLineParser().Parse(spec, arguments);
    }

    private static ParsedCommand ManualCommand(ParsedCommand original, string mode, TrackingComparer comparer)
    {
        var values = new Dictionary<string, object?>(comparer);
        foreach (var entry in original.Values) values.Add(entry.Key, entry.Value);
        if (mode == "missing") values.Remove("preview_server");
        else values["preview_server"] = mode == "null" ? null : "not-a-boolean";
        return new ParsedCommand(original.Spec, values, original.Positionals.ToList(), original.ProgramName, original.OptionSources);
    }

    private static void AssertFieldsEqual(TbcDecodedField expected, TbcDecodedField actual)
    {
        Assert.Equal(expected.Samples, actual.Samples);
        Assert.Equal(expected.Efm, actual.Efm);
        Assert.Equal(expected.AudioPcm, actual.AudioPcm);
        Assert.Equal(expected.RfTbc, actual.RfTbc);
        Assert.NotEmpty(actual.Efm!);
        Assert.NotEmpty(actual.AudioPcm!);
        Assert.NotEmpty(actual.RfTbc!);
        Assert.Null(actual.RawInputSamples);
        Assert.Null(actual.PreTbcVideoSamples);
        Assert.Equal(DescribeField(expected), DescribeField(actual));
        Assert.True(MemoryMarshal.AsBytes(expected.LineLocations.Locations.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(actual.LineLocations.Locations.AsSpan())));
        AssertDoubleProperties(expected, actual);
        AssertDoubleProperties(expected.Timing, actual.Timing);
        if (expected.OutputConverter is not null && actual.OutputConverter is not null)
            AssertDoubleProperties(expected.OutputConverter, actual.OutputConverter);
        Assert.Equal(expected.VitsMetrics?.Keys.Order().ToArray(), actual.VitsMetrics?.Keys.Order().ToArray());
        if (expected.VitsMetrics is { } metrics)
            foreach (var value in metrics)
                Assert.Equal(BitConverter.DoubleToInt64Bits(value.Value), BitConverter.DoubleToInt64Bits(actual.VitsMetrics![value.Key]));
        if (expected.ChromaBurstSamples is { } burst)
            Assert.True(MemoryMarshal.AsBytes(burst.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.ChromaBurstSamples!.AsSpan())));
    }

    private static void AssertDoubleProperties<T>(T expected, T actual)
    {
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(double) && property.PropertyType != typeof(double?)) continue;
            object? first = property.GetValue(expected), second = property.GetValue(actual);
            Assert.Equal(first is null, second is null);
            if (first is double value)
                Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)second!));
        }
    }

    private sealed class CountingInput : MemoryStream
    {
        public int ReadCalls { get; private set; }
        public int SeekCalls { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { ReadCalls++; return base.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) { SeekCalls++; return base.Seek(offset, origin); }
    }

    private sealed class TrackingComparer : IEqualityComparer<string>
    {
        public List<string> Calls { get; } = [];
        public bool Equals(string? x, string? y) { Calls.Add("equals:" + x + ":" + y); return StringComparer.Ordinal.Equals(x, y); }
        public int GetHashCode(string value) { Calls.Add("hash:" + value); return StringComparer.Ordinal.GetHashCode(value); }
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _directory;
        public TempDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "vhsdecode-owned-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }
        public string File(string name) => Path.Combine(_directory, name);
        public void Dispose()
        {
            string full = Path.GetFullPath(_directory);
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("vhsdecode-owned-runner-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its owned temporary directory.");
            Directory.Delete(full, recursive: true);
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
}
