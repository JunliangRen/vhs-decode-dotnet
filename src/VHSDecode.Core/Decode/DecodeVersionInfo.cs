using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Decode;

public static class DecodeVersionInfo
{
    public const string Version = "vhs_decode:g43155200";

    public static string OsInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Version version = Environment.OSVersion.Version;
            string release = WindowsRelease(version);
            string platformVersion = version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
            return $"Windows:{release}:{platformVersion}";
        }

        string? system = Uname("-s");
        string? releaseName = Uname("-r");
        string? platformVersionName = Uname("-v");
        return system is not null && releaseName is not null && platformVersionName is not null
            ? $"{system}:{releaseName}:{platformVersionName}"
            : $"{RuntimeInformation.OSDescription}:{Environment.OSVersion.Version}:{Environment.OSVersion.VersionString}";
    }

    private static string WindowsRelease(System.Version version)
    {
        if (version.Major >= 10)
        {
            return version.Build >= 22_000 ? "11" : "10";
        }

        return (version.Major, version.Minor) switch
        {
            (6, 3) => "8.1",
            (6, 2) => "8",
            (6, 1) => "7",
            (6, 0) => "Vista",
            (5, 2) => "2003Server",
            (5, 1) => "XP",
            _ => version.ToString(2)
        };
    }

    private static string? Uname(string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "uname",
                Arguments = argument,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            string value = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static (string Branch, string Commit) ExtractGitVersionParts(string version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return ("", "");
        }

        if (version.Contains(':', StringComparison.Ordinal))
        {
            string[] parts = version.Split(':');
            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        if (version.Count(static c => c == '/') == 1)
        {
            string[] parts = version.Split('/', 2);
            return (parts[0], parts[1]);
        }

        const string GitMarker = "+git.";
        int gitMarkerIndex = version.IndexOf(GitMarker, StringComparison.Ordinal);
        if (gitMarkerIndex >= 0)
        {
            string afterMarker = version[(gitMarkerIndex + GitMarker.Length)..];
            string commit = afterMarker.Split('.', 2)[0];
            return ("release", commit);
        }

        return ("", "");
    }
}
