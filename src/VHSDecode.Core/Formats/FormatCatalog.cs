using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VHSDecode.Core.Formats;

public sealed class FormatCatalog
{
    private const string ResourceName = "VHSDecode.Core.Formats.format-params.snapshot.json";
    private static readonly Lazy<FormatCatalog> LazyDefault = new(Load);

    private readonly Dictionary<string, FormatCaseDto> _tapeCases;
    private readonly Dictionary<string, CvbsCaseDto> _cvbsCases;
    private readonly Dictionary<string, LaserDiscCaseDto> _laserDiscCases;

    private FormatCatalog(FormatSnapshotDto snapshot)
    {
        Source = snapshot.Source;
        Commit = snapshot.Commit;
        Systems = snapshot.Systems;
        TapeFormats = snapshot.TapeFormats;
        TapeSpeeds = snapshot.TapeSpeeds;
        _tapeCases = snapshot.Cases.ToDictionary(
            item => TapeCaseKey(item.System, item.TapeFormat, item.TapeSpeed),
            StringComparer.Ordinal);
        _cvbsCases = snapshot.CvbsCases.ToDictionary(
            item => NormalizeSystem(item.System),
            StringComparer.Ordinal);
        _laserDiscCases = snapshot.LaserDiscCases.ToDictionary(
            item => LaserDiscCaseKey(item.System, item.LowBand),
            StringComparer.Ordinal);
    }

    public static FormatCatalog Default => LazyDefault.Value;

    public string Source { get; }

    public string Commit { get; }

    public IReadOnlyList<string> Systems { get; }

    public IReadOnlyList<string> TapeFormats { get; }

    public IReadOnlyDictionary<string, int> TapeSpeeds { get; }

    public FormatParameterSet GetTapeParameters(string system, string tapeFormat, string tapeSpeed)
    {
        string normalizedSystem = NormalizeSystem(system);
        string normalizedFormat = tapeFormat.ToUpperInvariant();
        string normalizedSpeed = NormalizeTapeSpeedName(tapeSpeed);
        string key = TapeCaseKey(normalizedSystem, normalizedFormat, normalizedSpeed);

        if (!_tapeCases.TryGetValue(key, out FormatCaseDto? item))
        {
            throw new FormatParameterException(
                $"Unknown tape format parameter case: system={normalizedSystem}, tape_format={normalizedFormat}, tape_speed={normalizedSpeed}");
        }

        if (item.Status == "error")
        {
            throw new FormatParameterException(item.Error ?? "Unknown upstream format parameter error.");
        }

        return new FormatParameterSet(
            item.System,
            item.TapeFormat,
            item.TapeSpeed,
            item.SysParams,
            item.RfParams,
            item.Warnings);
    }

    public FormatParameterSet GetCvbsParameters(string system)
    {
        string normalizedSystem = NormalizeSystem(system);
        if (!_cvbsCases.TryGetValue(normalizedSystem, out CvbsCaseDto? item))
        {
            throw new FormatParameterException($"Unknown CVBS parameter case: system={normalizedSystem}");
        }

        if (item.Status == "error")
        {
            throw new FormatParameterException(item.Error ?? "Unknown upstream CVBS parameter error.");
        }

        return new FormatParameterSet(
            item.System,
            "CVBS",
            null,
            item.SysParams,
            item.RfParams,
            []);
    }

    public FormatParameterSet GetLaserDiscParameters(string system, bool lowBand)
    {
        string normalizedSystem = NormalizeLaserDiscSystem(system);
        string key = LaserDiscCaseKey(normalizedSystem, lowBand);
        if (!_laserDiscCases.TryGetValue(key, out LaserDiscCaseDto? item))
        {
            throw new FormatParameterException(
                $"Unknown LaserDisc parameter case: system={normalizedSystem}, lowband={lowBand}");
        }

        return new FormatParameterSet(
            item.System,
            "LD",
            null,
            item.SysParams,
            item.RfParams,
            []);
    }

    public static int ParseTapeSpeed(string tapeSpeed)
    {
        return NormalizeTapeSpeedName(tapeSpeed) switch
        {
            "sp" => 0,
            "lp" => 1,
            "ep" => 2,
            "vp" => 3,
            _ => 0
        };
    }

    public static string NormalizeTapeSpeedName(string tapeSpeed)
    {
        string normalized = tapeSpeed.ToLowerInvariant();
        return normalized == "slp" ? "ep" : normalized;
    }

    public static string NormalizeSystem(string system)
    {
        string normalized = system.ToUpperInvariant();
        return normalized == "PALM" ? "PAL_M" : normalized;
    }

    public static string ParentSystem(string system)
    {
        return NormalizeSystem(system) switch
        {
            "PAL_M" or "NLINHA" => "NTSC",
            "MESECAM" or "SECAM" or "405" or "819" => "PAL",
            string normalized => normalized
        };
    }

    public static bool IsColorUnder(string tapeFormat)
    {
        string normalized = tapeFormat.ToUpperInvariant();
        return normalized is not ("TYPEC" or "TYPEB" or "QUADRUPLEX" or "VHD");
    }

    private static string NormalizeLaserDiscSystem(string system)
    {
        string normalized = NormalizeSystem(system);
        return normalized == "PAL" ? "PAL" : "NTSC";
    }

    private static string TapeCaseKey(string system, string tapeFormat, string tapeSpeed)
    {
        return $"{NormalizeSystem(system)}|{tapeFormat.ToUpperInvariant()}|{NormalizeTapeSpeedName(tapeSpeed)}";
    }

    private static string LaserDiscCaseKey(string system, bool lowBand)
    {
        return $"{NormalizeLaserDiscSystem(system)}|{lowBand}";
    }

    private static FormatCatalog Load()
    {
        Assembly assembly = typeof(FormatCatalog).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            string available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException($"Could not load embedded resource {ResourceName}. Available: {available}");
        }

        var snapshot = JsonSerializer.Deserialize<FormatSnapshotDto>(stream)
            ?? throw new InvalidOperationException("Embedded format parameter snapshot was empty.");

        return new FormatCatalog(snapshot);
    }

    private sealed class FormatSnapshotDto
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        public string Commit { get; set; } = string.Empty;

        [JsonPropertyName("systems")]
        public string[] Systems { get; set; } = [];

        [JsonPropertyName("tape_formats")]
        public string[] TapeFormats { get; set; } = [];

        [JsonPropertyName("tape_speeds")]
        public Dictionary<string, int> TapeSpeeds { get; set; } = [];

        [JsonPropertyName("cases")]
        public FormatCaseDto[] Cases { get; set; } = [];

        [JsonPropertyName("cvbs_cases")]
        public CvbsCaseDto[] CvbsCases { get; set; } = [];

        [JsonPropertyName("ld_cases")]
        public LaserDiscCaseDto[] LaserDiscCases { get; set; } = [];
    }

    private sealed class FormatCaseDto
    {
        [JsonPropertyName("system")]
        public string System { get; set; } = string.Empty;

        [JsonPropertyName("tape_format")]
        public string TapeFormat { get; set; } = string.Empty;

        [JsonPropertyName("tape_speed")]
        public string TapeSpeed { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("warnings")]
        public string[] Warnings { get; set; } = [];

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("sysparams")]
        public JsonElement SysParams { get; set; }

        [JsonPropertyName("rfparams")]
        public JsonElement RfParams { get; set; }
    }

    private sealed class CvbsCaseDto
    {
        [JsonPropertyName("system")]
        public string System { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("sysparams")]
        public JsonElement SysParams { get; set; }

        [JsonPropertyName("rfparams")]
        public JsonElement RfParams { get; set; }
    }

    private sealed class LaserDiscCaseDto
    {
        [JsonPropertyName("system")]
        public string System { get; set; } = string.Empty;

        [JsonPropertyName("lowband")]
        public bool LowBand { get; set; }

        [JsonPropertyName("sysparams")]
        public JsonElement SysParams { get; set; }

        [JsonPropertyName("rfparams")]
        public JsonElement RfParams { get; set; }
    }
}
