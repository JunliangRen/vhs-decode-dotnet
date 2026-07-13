using System.Text.Json;

namespace VHSDecode.Core.Formats;

public sealed class FormatParameterSet
{
    internal FormatParameterSet(
        string system,
        string tapeFormat,
        string? tapeSpeed,
        JsonElement sysParams,
        JsonElement rfParams,
        IReadOnlyList<string> warnings)
    {
        System = system;
        TapeFormat = tapeFormat;
        TapeSpeed = tapeSpeed;
        SysParams = sysParams.Clone();
        RfParams = rfParams.Clone();
        Warnings = warnings;
    }

    public string System { get; }

    public string TapeFormat { get; }

    public string? TapeSpeed { get; }

    public JsonElement SysParams { get; }

    public JsonElement RfParams { get; }

    public IReadOnlyList<string> Warnings { get; }
}
