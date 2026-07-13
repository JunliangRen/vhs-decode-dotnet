using System.Text.Json;
using System.Text.Json.Nodes;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Decode;

public static class VhsParamsFileOverride
{
    private static readonly string[] DecoderLevelKeys = ["ire0", "hz_ire", "vsync_ire", "track_ire0_offset"];

    public static FormatParameterSet Apply(FormatParameterSet parameters, string? paramsFile)
    {
        if (string.IsNullOrWhiteSpace(paramsFile))
        {
            return parameters;
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

        OverrideExistingGroup(root, sysParams, "sys_params");
        OverrideExistingGroup(root, rfParams, "rf_params");
        CopyDecoderLevelKeys(sysParams, rfParams);

        using JsonDocument sysDocument = JsonDocument.Parse(sysParams.ToJsonString());
        using JsonDocument rfDocument = JsonDocument.Parse(rfParams.ToJsonString());
        return new FormatParameterSet(
            parameters.System,
            parameters.TapeFormat,
            parameters.TapeSpeed,
            sysDocument.RootElement,
            rfDocument.RootElement,
            parameters.Warnings);
    }

    private static void OverrideExistingGroup(JsonObject root, JsonObject target, string groupName)
    {
        if (root[groupName] is not JsonObject overrides)
        {
            return;
        }

        foreach ((string key, JsonNode? value) in overrides)
        {
            if (!target.ContainsKey(key))
            {
                continue;
            }

            target[key] = CloneNode(value);
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
}
