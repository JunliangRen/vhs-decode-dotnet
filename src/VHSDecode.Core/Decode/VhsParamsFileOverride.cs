using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Decode;

public static class VhsParamsFileOverride
{
    private static readonly string[] DecoderLevelKeys = ["ire0", "hz_ire", "vsync_ire", "track_ire0_offset"];

    public static FormatParameterSet Apply(FormatParameterSet parameters, string? paramsFile)
        => ApplyWithDiagnostics(parameters, paramsFile).Parameters;

    internal static VhsParamsFileOverrideResult ApplyWithDiagnostics(
        FormatParameterSet parameters,
        string? paramsFile)
    {
        if (string.IsNullOrWhiteSpace(paramsFile))
        {
            return new VhsParamsFileOverrideResult(parameters, []);
        }

        JsonNode? parsedInput;
        if (paramsFile == "-")
        {
            parsedInput = JsonNode.Parse(Console.In.ReadToEnd());
        }
        else
        {
            using FileStream stream = File.OpenRead(paramsFile);
            parsedInput = JsonNode.Parse(stream);
        }

        JsonNode jsonInput = parsedInput
            ?? throw new FormatParameterException($"Params JSON file '{paramsFile}' was empty.");
        JsonObject root = jsonInput.AsObject();
        JsonObject sysParams = JsonNode.Parse(parameters.SysParams.GetRawText())!.AsObject();
        JsonObject rfParams = JsonNode.Parse(parameters.RfParams.GetRawText())!.AsObject();
        var diagnostics = new List<DecodeInitializationDiagnostic>();

        OverrideExistingGroup(root, sysParams, "sys_params", diagnostics);
        OverrideExistingGroup(root, rfParams, "rf_params", diagnostics);
        CopyDecoderLevelKeys(sysParams, rfParams);

        using JsonDocument sysDocument = JsonDocument.Parse(sysParams.ToJsonString());
        using JsonDocument rfDocument = JsonDocument.Parse(rfParams.ToJsonString());
        return new VhsParamsFileOverrideResult(
            new FormatParameterSet(
                parameters.System,
                parameters.TapeFormat,
                parameters.TapeSpeed,
                sysDocument.RootElement,
                rfDocument.RootElement,
                parameters.Warnings),
            diagnostics);
    }

    private static void OverrideExistingGroup(
        JsonObject root,
        JsonObject target,
        string groupName,
        ICollection<DecodeInitializationDiagnostic> diagnostics)
    {
        if (root[groupName] is not JsonObject overrides)
        {
            return;
        }

        var changed = new List<(string Key, JsonNode? Value)>();
        foreach ((string key, JsonNode? value) in overrides)
        {
            if (!target.ContainsKey(key))
            {
                diagnostics.Add(new DecodeInitializationDiagnostic(
                    "INFO",
                    $"Item {key} in params json not in group {groupName}. Not changed!"));
                continue;
            }

            JsonNode? clonedValue = CloneNode(value);
            changed.Add((key, clonedValue));
            target[key] = clonedValue;
        }

        if (changed.Count > 0)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic(
                "DEBUG",
                $"Changed {FormatPythonDictionary(changed)} in {groupName}"));
        }
    }

    private static void CopyDecoderLevelKeys(JsonObject sysParams, JsonObject rfParams)
    {
        foreach (string key in DecoderLevelKeys)
        {
            if (sysParams.TryGetPropertyValue(key, out JsonNode? value))
            {
                rfParams[key] = CloneNode(value);
            }
        }
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString());
    }

    private static string FormatPythonDictionary(IReadOnlyList<(string Key, JsonNode? Value)> values)
        => "{" + string.Join(
            ", ",
            values.Select(item =>
                $"{PythonNamespaceFormatter.FormatString(item.Key)}: {FormatPythonValue(item.Value)}")) + "}";

    private static string FormatPythonValue(JsonNode? value)
    {
        if (value is null)
        {
            return "None";
        }

        if (value is JsonObject objectValue)
        {
            return FormatPythonDictionary(objectValue
                .Select(item => (item.Key, item.Value))
                .ToArray());
        }

        if (value is JsonArray arrayValue)
        {
            return "[" + string.Join(", ", arrayValue.Select(FormatPythonValue)) + "]";
        }

        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        JsonElement element = document.RootElement;
        return element.ValueKind switch
        {
            JsonValueKind.String => PythonNamespaceFormatter.FormatString(element.GetString()!),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "None",
            JsonValueKind.Number => FormatPythonNumber(element),
            _ => throw new FormatParameterException(
                $"Unsupported JSON value kind '{element.ValueKind}' in params file.")
        };
    }

    private static string FormatPythonNumber(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) >= 0
            ? PythonNamespaceFormatter.FormatValue(value.GetDouble())
            : BigInteger.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
    }
}

internal readonly record struct VhsParamsFileOverrideResult(
    FormatParameterSet Parameters,
    IReadOnlyList<DecodeInitializationDiagnostic> Diagnostics);
