namespace VHSDecode.Core.Decode;

public sealed class VhsDiskSpaceGuard
{
    internal const long MinimumFreeBytes = 10L * 1024L * 1024L * 1024L;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.0);
    private readonly Func<string, long> _availableFreeBytes;
    private readonly Action<TimeSpan> _wait;

    public VhsDiskSpaceGuard(
        Func<string, long>? availableFreeBytes = null,
        Action<TimeSpan>? wait = null)
    {
        _availableFreeBytes = availableFreeBytes ?? AvailableFreeBytes;
        _wait = wait ?? Thread.Sleep;
    }

    public void Check(string outputBase, int fieldsWritten, DecodeRuntimeReporter? reporter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBase);
        ArgumentOutOfRangeException.ThrowIfNegative(fieldsWritten);
        if (reporter is null || !ShouldCheck(fieldsWritten))
        {
            return;
        }

        try
        {
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputBase))!;
            if (_availableFreeBytes(outputDirectory) >= MinimumFreeBytes)
            {
                return;
            }

            reporter.WriteDirectErrorLine(string.Empty);
            reporter.WriteDirectErrorLine(
                "Less than 10GB of free disk space is remaining, decoding paused. "
                + "Decoding will resume once there is more space, or press Ctrl+C to exit.");
            do
            {
                _wait(PollInterval);
            }
            while (_availableFreeBytes(outputDirectory) < MinimumFreeBytes);

            reporter.WriteDirectErrorLine(string.Empty);
            reporter.WriteDirectErrorLine("Disk space available, resuming decode.");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
        }
    }

    internal static bool ShouldCheck(int fieldsWritten)
        => TbcOutputMetadataWriter.ShouldWriteRecoverySnapshot(fieldsWritten);

    private static long AvailableFreeBytes(string outputDirectory)
    {
        string? root = Path.GetPathRoot(outputDirectory);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException($"Could not resolve a drive for '{outputDirectory}'.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
