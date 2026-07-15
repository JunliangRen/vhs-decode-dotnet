using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NetMQ;
using NetMQ.Sockets;

namespace VHSDecode.Core.HiFi;

internal sealed class HiFiGnuRadioSink : IDisposable
{
    public const int DefaultPort = 5555;

    private readonly BlockingCollection<SendRequest> _requests = new();
    private readonly CancellationTokenSource _stopSource;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _workerFailure;
    private bool _disposed;

    public HiFiGnuRadioSink(
        TextWriter output,
        CancellationToken cancellationToken,
        int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = new Thread(() => Run(output))
        {
            IsBackground = true,
            Name = "hifi_gnuradio_zmq"
        };
        _worker.Start();
        try
        {
            _ready.Wait(cancellationToken);
        }
        catch
        {
            Dispose();
            throw;
        }

        Exception? startupFailure = Volatile.Read(ref _workerFailure);
        if (startupFailure is not null)
        {
            Dispose();
            throw new InvalidOperationException(
                "Unable to initialize the HiFi GNU Radio ZMQ output.",
                startupFailure);
        }
    }

    public int Port { get; }

    public void Send(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? workerFailure = Volatile.Read(ref _workerFailure);
        if (workerFailure is not null)
        {
            throw new InvalidOperationException("HiFi GNU Radio output failed.", workerFailure);
        }

        var request = new SendRequest(samples);
        try
        {
            _requests.Add(request, _stopSource.Token);
        }
        catch (InvalidOperationException) when (
            Volatile.Read(ref _workerFailure) is { } failure)
        {
            throw new InvalidOperationException("HiFi GNU Radio output failed.", failure);
        }

        request.Wait(_stopSource.Token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requests.CompleteAdding();
        _stopSource.Cancel();
        _worker.Join();
        _ready.Dispose();
        _requests.Dispose();
        _stopSource.Dispose();
    }

    private void Run(TextWriter output)
    {
        try
        {
            output.WriteLine(
                $"Initializing ZMQSend (REP) at pid {Environment.ProcessId}, port {Port}");
            using var socket = new ResponseSocket();
            socket.Options.Linger = TimeSpan.Zero;
            socket.Bind($"tcp://*:{Port}");
            _ready.Set();

            foreach (SendRequest request in _requests.GetConsumingEnumerable(_stopSource.Token))
            {
                while (!socket.TryReceiveFrameBytes(
                    TimeSpan.FromMilliseconds(100),
                    out _))
                {
                    _stopSource.Token.ThrowIfCancellationRequested();
                }

                socket.SendFrame(MemoryMarshal.AsBytes(request.Samples.AsSpan()).ToArray());
                request.Complete();
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            _ready.Set();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _workerFailure, ex);
            _requests.CompleteAdding();
            while (_requests.TryTake(out SendRequest? request))
            {
                request.Fail(ex);
            }

            _ready.Set();
        }
    }

    private sealed class SendRequest(float[] samples)
    {
        private readonly TaskCompletionSource<bool> _completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;

        public float[] Samples { get; } = samples;

        public void Complete() => _completed.TrySetResult(true);

        public void Fail(Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _completed.TrySetResult(false);
        }

        public void Wait(CancellationToken cancellationToken)
        {
            _completed.Task.Wait(cancellationToken);
            Exception? failure = Volatile.Read(ref _failure);
            if (failure is not null)
            {
                throw new InvalidOperationException("HiFi GNU Radio output failed.", failure);
            }
        }
    }
}
