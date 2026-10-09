using System.ComponentModel;
using System.Diagnostics;

namespace VHSDecode.Core.Processes;

internal static class ExternalToolProcess
{
    internal static void Configure(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            startInfo.KillOnParentExit = true;
        }

        // Standard handles are included by Process.Start. Other handles require an explicit list.
        startInfo.InheritedHandles ??= [];
    }

    internal static ProcessTextOutput Capture(ProcessStartInfo startInfo, TimeSpan? timeout = null)
    {
        Configure(startInfo);
        return Process.RunAndCaptureText(startInfo, timeout);
    }

    internal static async Task<ProcessTextOutput> CaptureAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Configure(startInfo);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("External tool did not start.");
        try
        {
            (string standardOutput, string standardError) = await process.ReadAllTextAsync(
                cancellationToken).ConfigureAwait(false);
            ProcessExitStatus status = await process.WaitForExitStatusAsync(
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessTextOutput(status, standardOutput, standardError, process.Id);
        }
        catch
        {
            // The static capture helper kills only the launcher on cancellation.
            Stop(process);
            throw;
        }
    }

    internal static void Stop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // Kill descendants while their launcher is still alive and identifiable.
            process.Kill(entireProcessTree: true);
            process.WaitForExitStatus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Best effort when another thread has already stopped or disposed the process.
        }
    }

    internal static string DescribeFailure(ProcessExitStatus status)
        => status.Signal is { } signal
            ? $"signal {signal}"
            : $"exit code {status.ExitCode}";
}
