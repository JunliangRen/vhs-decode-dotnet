using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VHSDecode.Core.Processes;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ExternalToolProcessTests
{
    [Fact]
    public async Task CaptureDrainsBothPipesAndPreservesExitStatus()
    {
        ProcessTextOutput result = await ExternalToolProcess.CaptureAsync(
            CreateStartInfo("emit", capture: true), TestContext.Current.CancellationToken);
        Assert.Equal(7, result.ExitStatus.ExitCode);
        Assert.Null(result.ExitStatus.Signal);
        Assert.Equal(new string('O', 128 * 4096), result.StandardOutput);
        Assert.Equal(new string('E', 128 * 4096), result.StandardError);
    }

    [Fact]
    public async Task CaptureCancellationTerminatesChild()
    {
        string pidPath = Path.GetTempFileName();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int childId = 0;
        try
        {
            Task<ProcessTextOutput> capture = ExternalToolProcess.CaptureAsync(
                CreateStartInfo("sleep", pidPath, capture: true), cancellation.Token);
            childId = await ReadChildIdAsync(pidPath);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            await AssertExitedAsync(childId);
        }
        finally
        {
            cancellation.Cancel();
            StopChild(childId);
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task CaptureCancellationTerminatesLauncherAndDescendant()
    {
        string pidPath = Path.GetTempFileName();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int launcherId = 0;
        int childId = 0;
        try
        {
            Task<ProcessTextOutput> capture = ExternalToolProcess.CaptureAsync(
                CreateStartInfo("launcher", pidPath, capture: true), cancellation.Token);
            launcherId = await ReadChildIdAsync(pidPath + ".launcher");
            childId = await ReadChildIdAsync(pidPath);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            await AssertExitedAsync(launcherId);
            await AssertExitedAsync(childId);
        }
        finally
        {
            cancellation.Cancel();
            StopChild(launcherId);
            StopChild(childId);
            File.Delete(pidPath);
            File.Delete(pidPath + ".launcher");
        }
    }

    [Fact]
    public async Task StopTerminatesLauncherAndDescendant()
    {
        string pidPath = Path.GetTempFileName();
        int childId = 0;
        using Process launcher = Process.Start(CreateStartInfo("launcher", pidPath))!;
        try
        {
            childId = await ReadChildIdAsync(pidPath);
            ExternalToolProcess.Stop(launcher);
            Assert.True(launcher.HasExited);
            await AssertExitedAsync(childId);
        }
        finally
        {
            ExternalToolProcess.Stop(launcher);
            StopChild(childId);
            File.Delete(pidPath);
            File.Delete(pidPath + ".launcher");
        }
    }

    [Fact]
    public async Task ParentTerminationDoesNotLeaveChildRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
            "KillOnParentExit is available on Windows and Linux.");
        string pidPath = Path.GetTempFileName();
        int childId = 0;
        using Process parent = Process.Start(CreateStartInfo("parent", pidPath))!;
        try
        {
            childId = await ReadChildIdAsync(pidPath);
            parent.Kill();
            await parent.WaitForExitStatusAsync(TestContext.Current.CancellationToken);
            await AssertExitedAsync(childId);
        }
        finally
        {
            ExternalToolProcess.Stop(parent);
            StopChild(childId);
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task AnonymousStandardHandlesDeliverBytesAndEof()
    {
        SafeFileHandle.CreateAnonymousPipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite);
        SafeFileHandle.CreateAnonymousPipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite);
        using (inputRead)
        using (inputWrite)
        using (outputRead)
        using (outputWrite)
        {
            ProcessStartInfo startInfo = CreateStartInfo("copy");
            startInfo.StandardInputHandle = inputRead;
            startInfo.StandardOutputHandle = outputWrite;
            ExternalToolProcess.Configure(startInfo);
            using Process process = Process.Start(startInfo)!;
            inputRead.Dispose();
            outputWrite.Dispose();
            byte[] expected = Enumerable.Range(0, 131072).Select(i => (byte)i).ToArray();
            using var output = new FileStream(outputRead, FileAccess.Read);
            using var actual = new MemoryStream();
            Task drain = output.CopyToAsync(actual, TestContext.Current.CancellationToken);
            using (var input = new FileStream(inputWrite, FileAccess.Write))
            {
                input.Write(expected);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await drain.WaitAsync(timeout.Token);
                ProcessExitStatus status = await process.WaitForExitStatusAsync(timeout.Token);
                Assert.Equal(0, status.ExitCode);
                Assert.Equal(expected, actual.ToArray());
            }
            finally
            {
                ExternalToolProcess.Stop(process);
            }
        }
    }

    [Fact]
    public async Task StopTerminatesRunningTool()
    {
        string pidPath = Path.GetTempFileName();
        using Process process = Process.Start(CreateStartInfo("sleep", pidPath))!;
        try
        {
            await ReadChildIdAsync(pidPath);
            ExternalToolProcess.Stop(process);
            Assert.True(process.HasExited);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Assert.NotNull(process.WaitForExitStatus().Signal);
            }
        }
        finally
        {
            ExternalToolProcess.Stop(process);
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task LinuxCrashReporterWritesStructuredReport()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "In-process crash reporting is a Unix runtime feature.");
        string directory = Path.Combine(Path.GetTempPath(), "vhsdecode-crash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo("crash", capture: true);
            startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            startInfo.Environment["DOTNET_EnableCrashReport"] = "1";
            startInfo.Environment["DOTNET_CrashReportRootPath"] = directory;
            ProcessTextOutput result = await ExternalToolProcess.CaptureAsync(
                startInfo, TestContext.Current.CancellationToken);
            Assert.NotNull(result.ExitStatus.Signal);
            string report = Assert.Single(Directory.GetFiles(
                Path.Combine(directory, ".dotnet", "crash-reports"), "*.crashreport.json"));
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(
                report, TestContext.Current.CancellationToken));
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
            Assert.Contains("Intentional process test host crash", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string scenario, string? pidPath = null, bool capture = false)
    {
        string hostDirectory = Path.Combine(AppContext.BaseDirectory, "process-test-host");
        var startInfo = new ProcessStartInfo(Path.Combine(hostDirectory,
            OperatingSystem.IsWindows() ? "VHSDecode.ProcessTestHost.exe" : "VHSDecode.ProcessTestHost"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture
        };
        startInfo.ArgumentList.Add(scenario);
        if (pidPath is not null)
        {
            startInfo.ArgumentList.Add(pidPath);
        }
        return startInfo;
    }

    private static async Task<int> ReadChildIdAsync(string pidPath)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                await using var stream = new FileStream(pidPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);
                if (int.TryParse(await reader.ReadToEndAsync(timeout.Token), out int pid))
                {
                    return pid;
                }
            }
            catch (IOException) { }
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task AssertExitedAsync(int pid)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }

    private static void StopChild(int pid)
    {
        if (pid == 0) return;
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15000);
            }
        }
        catch (ArgumentException) { }
    }
}
