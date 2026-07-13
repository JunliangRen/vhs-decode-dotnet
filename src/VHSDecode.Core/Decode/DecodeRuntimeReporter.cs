using System.Diagnostics;
using System.Globalization;

namespace VHSDecode.Core.Decode;

public sealed class DecodeRuntimeReporter
{
    private const int StatusColumns = 80;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<double> _elapsedSeconds;
    private readonly object _sync = new();
    private readonly double _startSeconds;
    private double? _postSetupStartSeconds;
    private int _fieldsWritten;
    private bool _statusWritten;
    private bool _statisticsWritten;

    public DecodeRuntimeReporter(
        TextWriter output,
        TextWriter error,
        Func<double>? elapsedSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _output = output;
        _error = error;
        _elapsedSeconds = elapsedSeconds ?? StopwatchElapsedSeconds;
        _startSeconds = _elapsedSeconds();
    }

    public void Status(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            _output.Write(message);
            _output.Write(new string(' ', Math.Max(0, StatusColumns - message.Length)));
            _output.Write('\r');
            _output.Flush();
            _statusWritten = true;
        }
    }

    public void Log(string level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(message);
        if (string.Equals(level, "DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_sync)
        {
            if (_statusWritten)
            {
                _output.WriteLine();
                _output.Flush();
                _statusWritten = false;
            }

            _error.WriteLine(message);
            _error.Flush();
        }
    }

    public void FieldsWritten(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_fieldsWritten == 0)
            {
                _postSetupStartSeconds = _elapsedSeconds();
            }

            _fieldsWritten = checked(_fieldsWritten + count);
        }
    }

    public void WriteDirectErrorLine(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            _error.WriteLine(message);
            _error.Flush();
        }
    }

    public void WriteStatistics()
    {
        lock (_sync)
        {
            if (_statisticsWritten || _fieldsWritten == 0)
            {
                return;
            }

            _statisticsWritten = true;
            double endSeconds = _elapsedSeconds();
            double totalSeconds = Math.Max(0.0, endSeconds - _startSeconds);
            double postSetupSeconds = Math.Max(
                double.Epsilon,
                endSeconds - (_postSetupStartSeconds ?? _startSeconds));
            int frames = _fieldsWritten / 2;
            double framesPerSecond = frames / postSetupSeconds;
            _error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Took {totalSeconds:F2} seconds to decode {frames} frames ({framesPerSecond:F2} FPS post-setup)"));
            _error.Flush();
        }
    }

    private static double StopwatchElapsedSeconds()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
