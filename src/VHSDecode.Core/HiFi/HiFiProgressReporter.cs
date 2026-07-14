using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VHSDecode.Core.HiFi;

internal sealed class HiFiProgressReporter
{
    private const int ProgressWidth = 40;

    private readonly TextWriter _output;
    private readonly double _inputRateHz;
    private readonly int _audioRateHz;
    private readonly long? _totalInputSamples;
    private readonly Func<TimeSpan> _elapsedProvider;
    private readonly object _writeLock = new();
    private long _audioFrames;

    public HiFiProgressReporter(
        TextWriter output,
        double inputRateHz,
        int audioRateHz,
        long? totalInputSamples,
        Func<TimeSpan>? elapsedProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(audioRateHz);

        _output = output;
        _inputRateHz = inputRateHz;
        _audioRateHz = audioRateHz;
        _totalInputSamples = totalInputSamples;
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
        long audioFrames = Interlocked.Read(ref _audioFrames);
        lock (_writeLock)
        {
            if (_totalInputSamples is > 0)
            {
                _output.WriteLine(FormatProgressBar(inputSamples, _totalInputSamples.Value));
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

    public void ReportOutput(long inputSamples, long audioFrames, int blocksEnqueued)
    {
        Interlocked.Exchange(ref _audioFrames, audioFrames);
        lock (_writeLock)
        {
            _output.Write(FormatStatus(
                inputSamples,
                audioFrames,
                blocksEnqueued,
                _inputRateHz,
                _audioRateHz,
                _elapsedProvider()));
        }
    }

    internal static string FormatProgressBar(long value, long maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        int completed = checked((int)Math.Round(
            value * (double)ProgressWidth / maximum,
            MidpointRounding.ToEven));
        int remaining = ProgressWidth - completed;
        string percentage = (value * 100.0 / maximum).ToString(
            "F2",
            CultureInfo.InvariantCulture);
        return "Progress ["
            + new string('#', Math.Max(0, completed))
            + new string(' ', Math.Max(0, remaining))
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
