using System.Diagnostics;
using System.Globalization;
using VHSDecode.Core.Tbc;

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
    private bool _completionMessageWritten;
    private bool _cvbsCompletionWritten;
    private bool _testLdfReportStarted;
    private bool _testLdfNoSamplesWritten;
    private bool _testLdfShortReadWritten;
    private bool _testLdfSamplesWritten;
    private bool _testLdfSuccessWritten;
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

    internal void WriteCompletionMessage(int writtenFieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writtenFieldCount);
        lock (_sync)
        {
            if (_completionMessageWritten)
            {
                return;
            }

            _completionMessageWritten = true;
            DecodeRunner.WriteCompletionMessage(writtenFieldCount, _error);
            _error.Flush();
        }
    }

    internal void WriteCvbsCompletion(CvbsAgcStatistics? statistics)
    {
        lock (_sync)
        {
            if (_cvbsCompletionWritten)
            {
                return;
            }

            _cvbsCompletionWritten = true;
            DecodeRunner.WriteCvbsAgcStatistics(statistics, _error);
            _output.WriteLine("saving JSON and exiting");
            _output.Flush();
            _error.Flush();
        }
    }

    internal void BeginTestLdfReport(string outputPath, long startSample, long endSample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        lock (_sync)
        {
            if (_testLdfReportStarted)
            {
                return;
            }

            _testLdfReportStarted = true;
            DecodeRunner.WriteTestLdfReportStart(outputPath, startSample, endSample, _error);
            _error.Flush();
        }
    }

    internal void WriteTestLdfShortRead(long sample)
    {
        lock (_sync)
        {
            if (_testLdfShortReadWritten)
            {
                return;
            }

            _testLdfShortReadWritten = true;
            _error.WriteLine($"WARNING: Short read at sample {sample}");
            _error.Flush();
        }
    }

    internal void WriteTestLdfSamplesWritten(long samplesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samplesWritten);
        lock (_sync)
        {
            if (_testLdfSamplesWritten)
            {
                return;
            }

            _testLdfSamplesWritten = true;
            _error.WriteLine($"  Samples written: {samplesWritten}");
            _error.Flush();
        }
    }

    internal void CompleteTestLdfReport(LdTestLdfWriteResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputPath))
        {
            return;
        }

        lock (_sync)
        {
            if (!_testLdfReportStarted)
            {
                _testLdfReportStarted = true;
                DecodeRunner.WriteTestLdfReportStart(
                    result.OutputPath,
                    result.StartSample,
                    result.EndSample,
                    _error);
            }

            if (result.EndSample <= result.StartSample)
            {
                if (!_testLdfNoSamplesWritten)
                {
                    _testLdfNoSamplesWritten = true;
                    _error.WriteLine("WARNING: No samples to write");
                    _error.Flush();
                }

                return;
            }

            if (result.ShortReadSample.HasValue && !_testLdfShortReadWritten)
            {
                _testLdfShortReadWritten = true;
                _error.WriteLine($"WARNING: Short read at sample {result.ShortReadSample.Value}");
            }

            if (!_testLdfSamplesWritten)
            {
                _testLdfSamplesWritten = true;
                _error.WriteLine($"  Samples written: {result.SamplesWritten}");
            }

            if (!_testLdfSuccessWritten)
            {
                _testLdfSuccessWritten = true;
                _error.WriteLine($"Successfully wrote {result.OutputPath}");
            }

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
