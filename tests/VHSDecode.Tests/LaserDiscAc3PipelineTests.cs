using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAc3PipelineTests
{
    [Fact(DisplayName = "LD AC3 native pipeline matches Release 4.0 OS pipe output")]
    public void LaserDiscAc3NativePipelineMatchesRelease40OsPipeOutput()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The reference pipeline uses cmd.exe binary pipes.");
        Assert.SkipUnless(
            CommandsAreAvailable("sox", "ld-ac3-demodulate", "ld-ac3-decode"),
            "sox, ld-ac3-demodulate, and ld-ac3-decode must be available on PATH.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-ac3-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string nativeDirectory = Path.Combine(directory, "output with spaces");
            Directory.CreateDirectory(nativeDirectory);
            string nativeOutputPath = Path.Combine(nativeDirectory, "capture.ac3");
            string referenceOutputPath = Path.Combine(directory, "reference.ac3");
            byte[] input = CreateDeterministicInput(1_000_000);
            using (Stream native = LaserDiscAc3Pipe.Open(nativeOutputPath))
            {
                native.Write(input);
            }

            Assert.True(File.Exists(nativeOutputPath));
            Assert.True(File.Exists(nativeOutputPath + ".log"));
            byte[] nativeOutput = File.ReadAllBytes(nativeOutputPath);
            string nativeLog = File.ReadAllText(nativeOutputPath + ".log");
            Assert.NotEmpty(nativeLog);

            RunReferencePipeline(referenceOutputPath, input);

            Assert.True(File.Exists(referenceOutputPath));
            Assert.True(File.Exists(referenceOutputPath + ".log"));
            Assert.Equal(nativeOutput, File.ReadAllBytes(referenceOutputPath));
            string referenceLog = File.ReadAllText(referenceOutputPath + ".log");
            Assert.Contains("Lost sync at symbol", nativeLog, StringComparison.Ordinal);
            Assert.Contains("Lost sync at symbol", referenceLog, StringComparison.Ordinal);
            Assert.Equal(
                NormalizeLog(nativeLog),
                NormalizeLog(referenceLog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool CommandsAreAvailable(params string[] commands)
    {
        foreach (string command in commands)
        {
            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-h");
            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return false;
                }

                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
                Task<string> standardError = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] CreateDeterministicInput(int length)
    {
        var input = new byte[length];
        uint state = 0x4C444143;
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = (byte)(state >> 24);
        }

        return input;
    }

    private static string NormalizeLog(string log)
    {
        string normalized = Regex.Replace(
            log,
            @"(?m)^using output file: .+\r?$",
            "using output file: <output>",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?m)^\[SYNC\]\t\d+ms\tLost sync at symbol \d+\r?\n",
            string.Empty,
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"\t\d+ms\t",
            "\t<elapsed>ms\t",
            RegexOptions.CultureInvariant);
    }

    private static void RunReferencePipeline(string outputPath, byte[] input)
    {
        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("AC3 output path has no parent directory.", nameof(outputPath));
        string command = "sox -r 40000000 -b 8 -c 1 -e signed -t raw - "
            + "-b 8 -r 46080000 -e unsigned -c 1 -t raw - "
            + "| ld-ac3-demodulate -v 3 - - "
            + $"| ld-ac3-decode - {outputPath} "
            + $"> {outputPath}.log 2>&1";
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the AC3 reference pipeline.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            throw new InvalidOperationException(
                $"Reference AC3 pipeline closed stdin early with exit code {process.ExitCode}: "
                + standardError.Result);
        }

        process.WaitForExit();
        Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"Reference AC3 pipeline exited with {process.ExitCode}: {standardError.Result}");
    }
}
