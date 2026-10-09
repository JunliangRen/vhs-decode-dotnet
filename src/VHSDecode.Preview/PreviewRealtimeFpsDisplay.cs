using System.Diagnostics;
using System.Globalization;

namespace VHSDecode.Preview;

internal closed record PreviewWindowGenerationUpdate(int WindowIndex, long StartedTimestamp)
{
    internal sealed record StartedUpdate(int WindowIndex, long StartedTimestamp)
        : PreviewWindowGenerationUpdate(WindowIndex, StartedTimestamp);

    internal sealed record CompletedUpdate(int WindowIndex, int FrameCount, long StartedTimestamp, long CompletedTimestamp)
        : PreviewWindowGenerationUpdate(WindowIndex, StartedTimestamp);

    internal sealed record AbandonedUpdate(int WindowIndex, long StartedTimestamp)
        : PreviewWindowGenerationUpdate(WindowIndex, StartedTimestamp);

    internal static PreviewWindowGenerationUpdate Started(
        int windowIndex,
        long startedTimestamp)
        => new StartedUpdate(windowIndex, startedTimestamp);

    internal static PreviewWindowGenerationUpdate Completed(
        int windowIndex,
        int frameCount,
        long startedTimestamp,
        long completedTimestamp)
        => new CompletedUpdate(windowIndex, frameCount, startedTimestamp, completedTimestamp);

    internal static PreviewWindowGenerationUpdate Abandoned(
        int windowIndex,
        long startedTimestamp)
        => new AbandonedUpdate(windowIndex, startedTimestamp);
}

internal sealed class PreviewRealtimeFpsDisplay
{
    private const string WaitingWindowText = "Preview windows: waiting for the first preview window...";
    private const string WaitingFpsText = "Realtime FPS: pending";
    private const string CursorUpOneLine = "\u001b[1A";
    private const string CursorDownOneLine = "\u001b[1B";
    private readonly TextWriter _writer;
    private readonly double _sourceFramesPerSecond;
    private readonly object _gate = new();
    private readonly Dictionary<int, PreviewWindowGenerationUpdate> _windows = [];
    private int _windowDisplayWidth;
    private int _fpsDisplayWidth;
    private bool _started;
    private bool _completed;

    internal PreviewRealtimeFpsDisplay(
        TextWriter writer,
        double sourceFramesPerSecond)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (!double.IsFinite(sourceFramesPerSecond) || sourceFramesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFramesPerSecond));
        }

        _sourceFramesPerSecond = sourceFramesPerSecond;
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _completed)
            {
                return;
            }

            _writer.WriteLine(WaitingWindowText);
            _writer.Write(WaitingFpsText);
            _writer.Flush();
            _windowDisplayWidth = WaitingWindowText.Length;
            _fpsDisplayWidth = WaitingFpsText.Length;
            _started = true;
        }
    }

    internal void Report(PreviewWindowGenerationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (!_started)
            {
                Start();
            }

            Apply(update);
            (string windowText, string fpsText) = BuildText();
            _writer.Write('\r');
            _writer.Write(CursorUpOneLine);
            _writer.Write(windowText.PadRight(_windowDisplayWidth));
            _writer.Write('\r');
            _writer.Write(CursorDownOneLine);
            _writer.Write(fpsText.PadRight(_fpsDisplayWidth));
            _writer.Flush();
            _windowDisplayWidth = Math.Max(_windowDisplayWidth, windowText.Length);
            _fpsDisplayWidth = Math.Max(_fpsDisplayWidth, fpsText.Length);
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (_started)
            {
                _writer.WriteLine();
                _writer.Flush();
            }

            _completed = true;
        }
    }

    private void Apply(PreviewWindowGenerationUpdate update)
    {
        switch (update)
        {
            case PreviewWindowGenerationUpdate.StartedUpdate:
                foreach (int completedWindow in _windows
                    .Where(pair => pair.Value is PreviewWindowGenerationUpdate.CompletedUpdate)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _windows.Remove(completedWindow);
                }

                _windows[update.WindowIndex] = update;
                break;
            case PreviewWindowGenerationUpdate.CompletedUpdate completed:
                if (completed.FrameCount > 0
                    && completed.CompletedTimestamp > completed.StartedTimestamp)
                {
                    _windows[update.WindowIndex] = update;
                }

                break;
            case PreviewWindowGenerationUpdate.AbandonedUpdate:
                _windows.Remove(update.WindowIndex);
                break;
        }
    }

    private (string WindowText, string FpsText) BuildText()
    {
        if (_windows.Count == 0)
        {
            return (WaitingWindowText, WaitingFpsText);
        }

        PreviewWindowGenerationUpdate[] windows = _windows.Values
            .OrderBy(update => update.StartedTimestamp)
            .ThenBy(update => update.WindowIndex)
            .ToArray();
        IEnumerable<string> windowText = windows.Select(update =>
            string.Format(
                CultureInfo.InvariantCulture,
                "W{0}",
                update.WindowIndex));
        IEnumerable<string> fpsText = windows.Select(update => update switch
        {
            PreviewWindowGenerationUpdate.CompletedUpdate completed => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.00}",
                    WindowFramesPerSecond(completed)),
            PreviewWindowGenerationUpdate.StartedUpdate => "decoding...",
            PreviewWindowGenerationUpdate.AbandonedUpdate => throw new InvalidOperationException(
                "An abandoned preview window cannot remain in the display.")
        });
        PreviewWindowGenerationUpdate.CompletedUpdate[] completedWindows = windows
            .OfType<PreviewWindowGenerationUpdate.CompletedUpdate>()
            .ToArray();
        string total = completedWindows.Length == windows.Length
            ? BuildTotalText(completedWindows)
            : "Total pending";
        return (
            $"Preview windows: {string.Join(" | ", windowText)}",
            $"Realtime FPS: {string.Join(" | ", fpsText)} | {total}");
    }

    private string BuildTotalText(PreviewWindowGenerationUpdate.CompletedUpdate[] windows)
    {
        long start = windows.Min(update => update.StartedTimestamp);
        long end = windows.Max(update => update.CompletedTimestamp);
        double elapsedSeconds = Stopwatch.GetElapsedTime(start, end).TotalSeconds;
        int frameCount = windows.Sum(update => update.FrameCount);
        double totalFps = frameCount / elapsedSeconds;
        return string.Format(
            CultureInfo.InvariantCulture,
            "Total {0:0.00} ({1:0.00}x source)",
            totalFps,
            totalFps / _sourceFramesPerSecond);
    }

    private static double WindowFramesPerSecond(PreviewWindowGenerationUpdate.CompletedUpdate update)
        => update.FrameCount
            / Stopwatch.GetElapsedTime(
                update.StartedTimestamp,
                update.CompletedTimestamp).TotalSeconds;
}
