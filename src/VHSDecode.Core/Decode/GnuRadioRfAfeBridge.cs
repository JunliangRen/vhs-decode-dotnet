using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetMQ;
using NetMQ.Sockets;

namespace VHSDecode.Core.Decode;

public interface IRfInputProcessor : IDisposable
{
    double[] Process(ReadOnlySpan<double> input);
}

public sealed class GnuRadioRfAfeBridge : IRfInputProcessor
{
    public const int DefaultSendPort = 5555;
    public const int FirstReceivePort = 5555;
    public const int LastReceivePort = 6666;

    private readonly ResponseSocket _sendSocket;
    private readonly RequestSocket _receiveSocket;
    private readonly TextWriter _output;
    private readonly object _socketLock = new();
    private bool _disposed;

    public GnuRadioRfAfeBridge(
        int sendPort = DefaultSendPort,
        int? receivePort = null,
        TextWriter? output = null)
    {
        ValidatePort(sendPort, nameof(sendPort));
        if (receivePort.HasValue)
        {
            ValidatePort(receivePort.Value, nameof(receivePort));
        }

        _output = output ?? Console.Out;
        SendPort = sendPort;
        _output.WriteLine($"Initializing ZMQSend (REP) at pid {Environment.ProcessId}, port {SendPort}");
        _sendSocket = new ResponseSocket();
        _sendSocket.Options.Linger = TimeSpan.Zero;
        _sendSocket.Bind($"tcp://*:{SendPort}");

        try
        {
            ReceivePort = receivePort ?? FindAvailablePort(FirstReceivePort, LastReceivePort, SendPort)
                ?? throw new InvalidOperationException(
                    $"No available GNU Radio ZMQ receive port was found from {FirstReceivePort} through {LastReceivePort}.");
            _output.WriteLine($"Initializing ZMQReceive (REQ) at pid {Environment.ProcessId}, port {ReceivePort}");
            _receiveSocket = new RequestSocket();
            _receiveSocket.Options.Linger = TimeSpan.Zero;
            _receiveSocket.Connect($"tcp://localhost:{ReceivePort}");
        }
        catch
        {
            _sendSocket.Dispose();
            throw;
        }

        _output.WriteLine(
            $"Open GNURadio with ZMQ REQ source set at tcp://localhost:{SendPort} and ZMQ REP sink set at tcp://*:{ReceivePort}");
        _output.WriteLine("The data stream will be of the float type at 40MSPS (40MHz sample rate)");
        _output.WriteLine(
            "It will send the raw RF for further processing prior to demodulation (useful for RF EQ discovery and group delay compensation)");
        _output.WriteLine(
            "You might want to do this in single threaded decode mode (-t 1 parameter) - TODO: might not work correctly with --no_resample yet.");
    }

    public int SendPort { get; }

    public int ReceivePort { get; }

    public double[] Process(ReadOnlySpan<double> input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_socketLock)
        {
            byte[] rawBytes = ToFloat32Bytes(input);
            _sendSocket.ReceiveFrameBytes();
            _sendSocket.SendFrame(rawBytes);

            var processed = new List<double>(input.Length);
            while (processed.Count < input.Length)
            {
                try
                {
                    _receiveSocket.SendFrame([(byte)'0']);
                    byte[] byteStream = _receiveSocket.ReceiveFrameBytes();
                    AppendFloat32(byteStream, processed);
                }
                catch (NetMQException ex)
                {
                    _output.WriteLine($"Got ZMQ error, {ex.Message}");
                    break;
                }
            }

            return processed.ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _receiveSocket.Dispose();
        _sendSocket.Dispose();
    }

    public static int? FindAvailablePort(int startPort, int endPort, int? excludedPort = null)
    {
        ValidatePort(startPort, nameof(startPort));
        ValidatePort(endPort, nameof(endPort));
        if (endPort < startPort)
        {
            throw new ArgumentOutOfRangeException(nameof(endPort));
        }

        for (int port = startPort; port <= endPort; port++)
        {
            if (port == excludedPort)
            {
                continue;
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }

        return null;
    }

    private static byte[] ToFloat32Bytes(ReadOnlySpan<double> input)
    {
        var output = new byte[checked(input.Length * sizeof(float))];
        for (int i = 0; i < input.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(i * sizeof(float), sizeof(float)), (float)input[i]);
        }

        return output;
    }

    private static void AppendFloat32(ReadOnlySpan<byte> bytes, List<double> destination)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException("GNU Radio ZMQ response length must be a multiple of four bytes.");
        }

        for (int offset = 0; offset < bytes.Length; offset += sizeof(float))
        {
            destination.Add(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, sizeof(float))));
        }
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
