using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VHSDecode.Core.HiFi;

internal sealed class HiFiProgressReporter : IDisposable
{
    private const int ProgressWidth = 40;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(125);

    private readonly TextWriter _output;
    private readonly double _inputRateHz;
    private readonly int _audioRateHz;
    private readonly long? _totalInputSamples;
    private readonly Func<TimeSpan> _elapsedProvider;
    private readonly object _writeLock = new();
    private readonly HiFiTerminalProgress? _terminal;
    private long _audioFrames;
    private long _inputSamples;
    private int _blocksEnqueued;
    private string? _latestBias;
    private TimeSpan? _lastRefresh;
    private bool _statusPending;

    public HiFiProgressReporter(
        TextWriter output,
        double inputRateHz,
        int audioRateHz,
        long? totalInputSamples,
        Func<TimeSpan>? elapsedProvider = null,
        HiFiTerminalProgress? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(audioRateHz);

        _output = output;
        _inputRateHz = inputRateHz;
        _audioRateHz = audioRateHz;
        _totalInputSamples = totalInputSamples;
        _terminal = terminal;
        if (elapsedProvider is null)
        {
            long startTimestamp = Stopwatch.GetTimestamp();
            _elapsedProvider = () => Stopwatch.GetElapsedTime(startTimestamp);
        }
        else
        {
            _elapsedProvider = elapsedProvider;
        }
    }

    public void ReportInput(long inputSamples, int blocksEnqueued)
    {
        lock (_writeLock)
        {
            if (_terminal is not null)
            {
                UpdateTerminal(inputSamples, blocksEnqueued);
                return;
            }

            if (_totalInputSamples is > 0 && inputSamples <= _totalInputSamples.Value)
            {
                _output.WriteLine(FormatProgressBar(inputSamples, _totalInputSamples.Value));
            }

            _output.Write(FormatStatus(
                inputSamples,
                _audioFrames,
                blocksEnqueued,
                _inputRateHz,
                _audioRateHz,
                _elapsedProvider()));
        }
    }

    public void ReportOutput(long inputSamples, long audioFrames, int blocksEnqueued)
    {
        lock (_writeLock)
        {
            _audioFrames = audioFrames;
            if (_terminal is not null)
            {
                UpdateTerminal(inputSamples, blocksEnqueued);
                return;
            }

            _output.Write(FormatStatus(
                inputSamples,
                audioFrames,
                blocksEnqueued,
                _inputRateHz,
                _audioRateHz,
                _elapsedProvider()));
        }
    }

    internal void ReportBias(HiFiDecodedBlock block, string audioMode, HiFiAfeParameters afe)
    {
        lock (_writeLock)
        {
            double leftKhz = block.LeftDc * afe.LeftCarrierDeviationHz / 1000.0;
            double rightKhz = block.RightDc * afe.RightCarrierDeviationHz / 1000.0;
            string bias = audioMode switch
            {
                HiFiConstants.AudioModeMonoLeft => $"Bias L {leftKhz:F2} kHz ",
                HiFiConstants.AudioModeMonoRight => $"Bias R {rightKhz:F2} kHz ",
                _ => $"Bias L {leftKhz:F2} kHz, R {rightKhz:F2} kHz "
            };
            bool calibrated = Math.Abs(leftKhz) < 9.0 && Math.Abs(rightKhz) < 9.0;
            if (calibrated)
            {
                string status = bias + "(good player/recorder calibration)";
                if (_terminal is not null)
                {
                    _latestBias = status;
                    _statusPending = true;
                    return;
                }

                _output.WriteLine(status);
                return;
            }

            _terminal?.Clear();
            _latestBias = bias.TrimEnd();
            if ((Math.Abs(leftKhz) is >= 9.0 and < 10.0)
                || (Math.Abs(rightKhz) is >= 9.0 and < 10.0))
            {
                _output.WriteLine(bias + "(maybe marginal player/recorder calibration)");
            }
            else
            {
                _output.WriteLine(bias);
                _output.WriteLine("WARN: the player or the recorder may be uncalibrated and/or");
                _output.WriteLine("the standard and/or the sample rate specified are wrong");
            }
        }
    }

    internal void ReportMessage(string message)
    {
        lock (_writeLock)
        {
            _terminal?.Clear();
            _output.WriteLine(message);
        }
    }

    private void UpdateTerminal(long inputSamples, int blocksEnqueued)
    {
        _inputSamples = Math.Max(_inputSamples, inputSamples);
        _blocksEnqueued = Math.Max(_blocksEnqueued, blocksEnqueued);
        _statusPending = true;
        RefreshTerminal(force: false);
    }

    private void RefreshTerminal(bool force)
    {
        if (_terminal is null || !_statusPending)
        {
            return;
        }

        TimeSpan elapsed = _elapsedProvider();
        if (!force && _lastRefresh is { } last && elapsed - last < RefreshInterval)
        {
            return;
        }

        string progress = _totalInputSamples is > 0 && _inputSamples <= _totalInputSamples.Value
            ? FormatProgressBar(_inputSamples, _totalInputSamples.Value)
            : "Progress (total unknown)";
        _terminal.Render(progress + Environment.NewLine
            + FormatStatus(_inputSamples, _audioFrames, _blocksEnqueued,
                _inputRateHz, _audioRateHz, elapsed)
            + (_latestBias ?? "Bias --") + Environment.NewLine);
        _lastRefresh = elapsed;
        _statusPending = false;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            try
            {
                RefreshTerminal(force: true);
            }
            finally
            {
                _terminal?.Dispose();
            }
        }
    }

    internal static string FormatProgressBar(
        long value,
        long maximum,
        string label = "Progress")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        ArgumentException.ThrowIfNullOrEmpty(label);
        double fraction = Math.Min(value, maximum) / (double)maximum;
        int completed = (int)Math.Round(
            fraction * ProgressWidth,
            MidpointRounding.ToEven);
        int remaining = ProgressWidth - completed;
        string percentage = (fraction * 100.0).ToString(
            "F2",
            CultureInfo.InvariantCulture);
        return label + " ["
            + new string('#', completed)
            + new string(' ', remaining)
            + "] " + percentage + "%";
    }

    internal static string FormatStatus(
        long inputSamples,
        long audioFrames,
        int blocksEnqueued,
        double inputRateHz,
        int audioRateHz,
        TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(audioFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(blocksEnqueued);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(audioRateHz);

        double elapsedSeconds = RoundToMicroseconds(elapsed.TotalSeconds);
        if (elapsedSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        double inputSeconds = inputSamples / inputRateHz;
        double audioSeconds = audioFrames / (double)audioRateHz;
        double bufferSeconds = Math.Max(0.0, inputSeconds - audioSeconds);
        long kiloFramesPerSecond = checked((long)Math.Round(
            inputSamples / (1_000.0 * elapsedSeconds),
            MidpointRounding.ToEven));
        double relativeSpeed = inputSeconds / elapsedSeconds;

        var result = new StringBuilder();
        result.Append("- Decoding speed: ");
        result.Append(kiloFramesPerSecond.ToString(CultureInfo.InvariantCulture));
        result.Append(" kFrames/s (");
        result.Append(relativeSpeed.ToString("F2", CultureInfo.InvariantCulture));
        result.Append("x), ");
        result.Append(blocksEnqueued.ToString(CultureInfo.InvariantCulture));
        result.AppendLine(" blocks enqueued");
        result.Append("- Input position: ");
        result.AppendLine(FormatSeconds(inputSeconds));
        result.Append("- Audio position: ");
        result.AppendLine(FormatSeconds(audioSeconds));
        result.Append("- Audio buffer  : ");
        result.AppendLine(FormatSeconds(bufferSeconds));
        result.Append("- Wall time     : ");
        result.AppendLine(FormatSeconds(elapsedSeconds));
        return result.ToString();
    }

    internal static string FormatSeconds(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        long totalMicroseconds = checked((long)Math.Round(
            seconds * 1_000_000.0,
            MidpointRounding.ToEven));
        long hours = totalMicroseconds / 3_600_000_000L;
        long remainder = totalMicroseconds % 3_600_000_000L;
        long minutes = remainder / 60_000_000L;
        remainder %= 60_000_000L;
        double secondPart = remainder / 1_000_000.0;
        string secondText = secondPart
            .ToString("F3", CultureInfo.InvariantCulture)
            .PadLeft(6, '0');
        return hours.ToString(CultureInfo.InvariantCulture)
            + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture)
            + ":" + secondText;
    }

    private static double RoundToMicroseconds(double seconds)
        => Math.Round(seconds * 1_000_000.0, MidpointRounding.ToEven) / 1_000_000.0;
}
