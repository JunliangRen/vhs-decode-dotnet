using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VHSDecode.Preview;

internal static class PreviewSourceProbe
{
    internal static async Task<double> GetDurationSecondsAsync(
        string path,
        double inputSampleRateHz,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Preview RF input was not found.", path);
        }

        if (!double.IsFinite(inputSampleRateHz) || inputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        }

        long length = new FileInfo(path).Length;
        string lower = path.ToLowerInvariant();
        double? sampleCount = lower switch
        {
            _ when lower.EndsWith(".lds", StringComparison.Ordinal) => (length / 5L) * 4.0,
            _ when lower.EndsWith(".r30", StringComparison.Ordinal) => (length / 4L) * 3.0,
            _ when lower.EndsWith(".rf", StringComparison.Ordinal) => length / 4.0,
            _ when lower.EndsWith(".s16", StringComparison.Ordinal)
                || lower.EndsWith(".r16", StringComparison.Ordinal)
                || lower.EndsWith(".u16", StringComparison.Ordinal)
                || lower.EndsWith(".raw", StringComparison.Ordinal) => length / 2.0,
            _ when lower.EndsWith(".s8", StringComparison.Ordinal)
                || lower.EndsWith(".r8", StringComparison.Ordinal)
                || lower.EndsWith(".u8", StringComparison.Ordinal) => length,
            _ => null
        };
        if (sampleCount.HasValue)
        {
            return sampleCount.Value / inputSampleRateHz;
        }

        if (RawFlacSampleCountProbe.TryGetTotalSamples(path, out long flacSamples))
        {
            return flacSamples / inputSampleRateHz;
        }

        return await ProbeContainerDurationAsync(
            path,
            inputSampleRateHz,
            ffprobePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<double> ProbeContainerDurationAsync(
        string path,
        double inputSampleRateHz,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            ffprobePath,
            [
                "-v", "error",
                "-show_entries", "format=duration:stream=duration,sample_rate",
                "-of", "json",
                path
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe could not determine the RF input duration: {result.StandardError.Trim()}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        var candidates = new List<double>();
        double? streamSampleRate = null;
        if (document.RootElement.TryGetProperty("format", out JsonElement format)
            && format.TryGetProperty("duration", out JsonElement formatDuration)
            && TryReadDuration(formatDuration, out double parsedFormatDuration))
        {
            candidates.Add(parsedFormatDuration);
        }

        if (document.RootElement.TryGetProperty("streams", out JsonElement streams)
            && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                if (!streamSampleRate.HasValue
                    && stream.TryGetProperty("sample_rate", out JsonElement sampleRate)
                    && TryReadDuration(sampleRate, out double parsedSampleRate)
                    && parsedSampleRate > 0.0)
                {
                    streamSampleRate = parsedSampleRate;
                }

                if (stream.TryGetProperty("duration", out JsonElement duration)
                    && TryReadDuration(duration, out double parsedStreamDuration))
                {
                    candidates.Add(parsedStreamDuration);
                }
            }
        }

        double value = candidates.Where(candidate => candidate > 0.0).DefaultIfEmpty(0.0).Max();
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new InvalidOperationException(
                "ffprobe did not report a positive duration for the RF input.");
        }

        return streamSampleRate.HasValue
            ? value * streamSampleRate.Value / inputSampleRateHz
            : value;
    }

    private static bool TryReadDuration(JsonElement element, out double value)
    {
        value = 0.0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetDouble();
            return double.IsFinite(value);
        }

        return element.ValueKind == JsonValueKind.String
            && double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
            && double.IsFinite(value);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start '{executable}'.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Could not start '{executable}'. Install FFmpeg and ensure it is on PATH, or set VHSDECODE_FFMPEG and VHSDECODE_FFPROBE.",
                ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort during cancellation.
        }
    }

    private readonly record struct ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
