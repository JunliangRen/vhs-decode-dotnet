using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VHSDecode.Core.CommandLine;

namespace VHSDecode.Core.Decode;

/// <summary>Opt-in process observations for maintainer diagnostic runs.</summary>
public sealed class DecodeProcessObservation : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;
    private readonly string[] _arguments;
    private readonly IReadOnlyDictionary<string, string?> _configuration;
    private readonly double _intervalMilliseconds;
    private string? _samplingError;
    private int? _exitCode;
    private int _disposed;

    internal DecodeProcessObservation(string reportPath, TimeSpan interval, string[] arguments, IReadOnlyDictionary<string, string?> configuration)
    {
        if (interval < TimeSpan.FromMilliseconds(50) || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _arguments = arguments;
        _configuration = configuration;
        _intervalMilliseconds = interval.TotalMilliseconds;
        try
        {
            string fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _writer = new StreamWriter(new FileStream(
                fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
            WriteSample("start");
            _sampling = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    {
                        WriteSample("sample");
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _samplingError = exception.Message;
                }
            });
        }
        catch
        {
            _writer?.Dispose();
            _process.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public static DecodeProcessObservation? StartIfRequested(ParsedCommand command)
        => Start(command, Environment.GetEnvironmentVariable("VHSDECODE_PROCESS_DIAGNOSTICS"),
            Environment.GetEnvironmentVariable("VHSDECODE_PROCESS_DIAGNOSTICS_INTERVAL_MS"));

    internal static DecodeProcessObservation? Start(ParsedCommand command, string? enabled, string? intervalText)
    {
        if (string.IsNullOrEmpty(enabled) || enabled == "0")
        {
            return null;
        }

        if (enabled != "1")
        {
            throw new ArgumentException("VHSDECODE_PROCESS_DIAGNOSTICS must be 0 or 1.");
        }

        if (command.Get<bool>("help") || string.IsNullOrEmpty(command.OutputBase))
        {
            return null;
        }

        int interval = 1000;
        if (!string.IsNullOrEmpty(intervalText)
            && (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval)
                || interval is < 50 or > 60_000))
        {
            throw new ArgumentException("VHSDECODE_PROCESS_DIAGNOSTICS_INTERVAL_MS must be 50..60000.");
        }

        return new DecodeProcessObservation(command.OutputBase + ".process.jsonl",
            TimeSpan.FromMilliseconds(interval), [command.Spec.Name, .. command.Positionals],
            new[] { "dsp_backend", "compat_version", "no_resample", "threads", "inputfreq", "start", "length" }
                .Where(command.Values.ContainsKey)
                .ToDictionary(key => key, key => Convert.ToString(command.Values[key], CultureInfo.InvariantCulture)));
    }

    public void SetExitCode(int exitCode) => _exitCode = exitCode;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _sampling.GetAwaiter().GetResult();
            WriteSample("stop");
        }
        finally
        {
            _writer.Dispose();
            _process.Dispose();
            _stop.Dispose();
        }
    }

    private void WriteSample(string kind)
    {
        _process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind,
            timestampUtc = DateTimeOffset.UtcNow,
            elapsedSeconds = _elapsed.Elapsed.TotalSeconds,
            processId = Environment.ProcessId,
            runtime = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            arguments = _arguments,
            configuration = _configuration,
            sampleIntervalMilliseconds = _intervalMilliseconds,
            scope = "decode process only; child processes excluded; no forced GC",
            workingSetBytes = _process.WorkingSet64,
            peakWorkingSetBytes = _process.PeakWorkingSet64,
            privateBytes = _process.PrivateMemorySize64,
            cpuSeconds = _process.TotalProcessorTime.TotalSeconds,
            threadCount = _process.Threads.Count,
            managedLiveBytes = GC.GetTotalMemory(forceFullCollection: false),
            managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            lastGcHeapBytes = gc.HeapSizeBytes,
            lastGcFragmentedBytes = gc.FragmentedBytes,
            totalAvailableMemoryBytes = gc.TotalAvailableMemoryBytes,
            gen0Collections = GC.CollectionCount(0),
            gen1Collections = GC.CollectionCount(1),
            gen2Collections = GC.CollectionCount(2),
            exitCode = kind == "stop" ? _exitCode : null,
            samplingError = _samplingError
        }));
    }
}
