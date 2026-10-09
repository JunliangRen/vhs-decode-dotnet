using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        ProcessTextOutput result = await Process.RunAndCaptureTextAsync(
            startInfo,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static void Stop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    process.Signal(PosixSignal.SIGTERM);
                    if (process.TryWaitForExitStatus(TimeSpan.FromMilliseconds(250), out _))
                    {
                        return;
                    }
                }
                catch (Win32Exception)
                {
                    // Fall back to tree termination if the signal could not be sent.
                }
            }

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
