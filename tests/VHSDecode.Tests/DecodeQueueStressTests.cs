using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NetMQ;
using NetMQ.Sockets;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DecodeQueueStressTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task GnuRadioQueuePreservesEveryPayloadAndEachProducerOrder(int producers)
    {
        const int requestsPerProducer = 512;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        using HiFiGnuRadioSink sink = CreateSink(stop.Token);
        Task[] senders = Enumerable.Range(0, producers).Select(producer => Task.Factory.StartNew(() =>
        {
            for (int index = 0; index < requestsPerProducer; index++)
            {
                sink.Send([producer, index, producer * requestsPerProducer + index]);
            }
        }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            // This socket stays on this thread for its entire request/reply loop.
            using (var requester = new RequestSocket())
            {
                requester.Options.Linger = TimeSpan.Zero;
                requester.Connect($"tcp://localhost:{sink.Port}");
                int[] next = new int[producers];
                for (int received = 0; received < producers * requestsPerProducer; received++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    Assert.True(requester.TrySendFrame(TimeSpan.FromSeconds(5), Array.Empty<byte>()));
                    Assert.True(requester.TryReceiveFrameBytes(TimeSpan.FromSeconds(5), out byte[]? bytes));
                    Assert.NotNull(bytes);
                    float[] payload = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
                    Assert.Equal(3, payload.Length);
                    int producer = checked((int)payload[0]);
                    Assert.InRange(producer, 0, producers - 1);
                    Assert.Equal(next[producer], payload[1]);
                    Assert.Equal(producer * requestsPerProducer + next[producer], payload[2]);
                    next[producer]++;
                }
                Assert.All(next, count => Assert.Equal(requestsPerProducer, count));
            }
            await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task GnuRadioCancellationReleasesBlockedProducersWithoutAReceiver(int producers)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        using HiFiGnuRadioSink sink = CreateSink(stop.Token);
        using var entered = new CountdownEvent(producers);
        Task[] senders = Enumerable.Range(0, producers).Select(producer => Task.Factory.StartNew(() =>
        {
            entered.Signal();
            Assert.ThrowsAny<OperationCanceledException>(() => sink.Send([producer]));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.All(senders, sender => Assert.False(sender.IsCompleted));
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(4096)]
    public void RepeatedMetadataSnapshotsPreserveAllOrderedFieldsAtCompletion(int count)
    {
        string directory = Path.Combine(Path.GetTempPath(), "decode queue " + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "capture");
        try
        {
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs,
                ["--system", "NTSC", "input.u8", output]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            using (var writer = new TbcOutputMetadataWriter.StreamingWriter(session, output + ".tbc.json"))
            {
                for (int index = 0; index < count; index++)
                {
                    writer.Add(Field(index * 100L, index % 2 == 0), new TbcFieldOrderDecision(
                        index + 1, index % 2 == 0, index % 2 == 0, false, true, 100, 0));
                    writer.WriteSnapshot();
                }
                writer.Complete();
                Assert.Equal(count, writer.FieldCount);
                Assert.Null(writer.LastSnapshotFailure);
            }
            JsonArray fields = JsonNode.Parse(File.ReadAllText(output + ".tbc.json"))!["fields"]!.AsArray();
            Assert.Equal(count, fields.Count);
            for (int index = 0; index < count; index++)
            {
                Assert.Equal(index + 1, fields[index]!["seqNo"]!.GetValue<int>());
                Assert.Equal(index * 100.0, fields[index]!["fileLoc"]!.GetValue<double>());
            }
            Assert.False(File.Exists(output + ".tbc.json.fields.tmp"));
            Assert.False(File.Exists(output + ".tbc.json.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static TbcDecodedField Field(long start, bool first) => new(
        StartSample: start, Samples: [], LineLocations: new LineLocationResult([], []),
        Timing: new SyncTiming(0, 0, 0, new SyncRange(0, 0), new SyncRange(0, 0), new SyncRange(0, 0)),
        SyncThresholdHz: 0, MeanLineLength: 0, RawPulseCount: 0, ClassifiedPulseCount: 0,
        DetectedFirstField: first, DetectedFirstFieldConfidence: 100);

    private static HiFiGnuRadioSink CreateSink(CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            int port;
            using (var reservation = new ResponseSocket())
            {
                reservation.Options.Linger = TimeSpan.Zero;
                port = reservation.BindRandomPort("tcp://*");
            }
            try { return new HiFiGnuRadioSink(TextWriter.Null, token, port); }
            catch (InvalidOperationException ex) when (
                ex.InnerException is AddressAlreadyInUseException && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                token.ThrowIfCancellationRequested();
            }
        }
    }
}
