namespace VHSDecode.Core.CommandLine;

public sealed class CommandLineParser
{
    public ParsedCommand Parse(
        DecodeCommandSpec spec,
        IReadOnlyList<string> args,
        string? programName = null,
        TextWriter? diagnosticOutput = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(args);

        var values = spec.Options.ToDictionary(option => option.Destination, option => option.DefaultValue);
        var optionSources = spec.Options.ToDictionary(
            option => option.Destination,
            _ => ParsedOptionSource.Default);
        OptionSpec? versionOption = spec.Options.FirstOrDefault(option => option.Destination == "version");
        if (versionOption is not null && args.Any(versionOption.Names.Contains))
        {
            values[versionOption.Destination] = true;
            optionSources[versionOption.Destination] = ParsedOptionSource.Flag;
            return new ParsedCommand(spec, values, [], programName, optionSources);
        }

        var positionals = new List<(int Index, string Value)>();
        var unknown = new List<(int Index, string Value)>();

        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (token == "--")
            {
                for (i++; i < args.Count; i++)
                {
                    AddPositional(spec, positionals, i, args[i], diagnosticOutput);
                }

                break;
            }

            if (!IsOptionToken(token) || LooksLikeNegativeNumber(token))
            {
                AddPositional(spec, positionals, i, token, diagnosticOutput);
                continue;
            }

            string optionName = token;
            string? inlineValue = null;
            int equalsIndex = token.IndexOf('=');
            if (equalsIndex > 0)
            {
                optionName = token[..equalsIndex];
                inlineValue = token[(equalsIndex + 1)..];
            }

            OptionSpec? option = ResolveOption(spec, optionName);
            if (option is null
                && inlineValue is null
                && TryProcessShortCluster(
                    spec,
                    args,
                    token,
                    values,
                    optionSources,
                    ref i,
                    out bool clusterHelp))
            {
                if (clusterHelp)
                {
                    return BuildResult(spec, values, optionSources, positionals, programName);
                }

                continue;
            }

            if (option is null)
            {
                unknown.Add((i, token));
                continue;
            }

            if (ApplyOption(args, option, inlineValue, values, optionSources, ref i))
            {
                return BuildResult(spec, values, optionSources, positionals, programName);
            }
        }

        if (diagnosticOutput is not null && spec.Name is "vhs" or "cvbs")
        {
            while (positionals.Count < spec.MaximumPositionals)
            {
                AddPositional(spec, positionals, args.Count + positionals.Count, string.Empty, diagnosticOutput);
            }
        }

        if (positionals.Count < spec.MinimumPositionals)
        {
            throw new CommandLineParseException("the following arguments are required: infile, outfile");
        }

        unknown.AddRange(positionals
            .Skip(spec.MaximumPositionals)
            .Select(item => (item.Index, item.Value)));
        if (unknown.Count > 0)
        {
            string arguments = string.Join(' ', unknown
                .OrderBy(item => item.Index)
                .Select(item => item.Value));
            throw new CommandLineParseException($"unrecognized arguments: {arguments}");
        }

        return BuildResult(spec, values, optionSources, positionals, programName);
    }

    private static void AddPositional(
        DecodeCommandSpec spec,
        List<(int Index, string Value)> positionals,
        int index,
        string value,
        TextWriter? diagnosticOutput)
    {
        string storedValue = value;
        if (diagnosticOutput is not null
            && spec.Name is "vhs" or "cvbs"
            && positionals.Count < spec.MaximumPositionals)
        {
            bool valid = positionals.Count == 0
                ? UpstreamIoArgumentValidator.ValidateInput(value, diagnosticOutput)
                : UpstreamIoArgumentValidator.ValidateOutput(value, diagnosticOutput);
            if (!valid)
            {
                storedValue = string.Empty;
            }
        }

        positionals.Add((index, storedValue));
    }

    private static ParsedCommand BuildResult(
        DecodeCommandSpec spec,
        Dictionary<string, object?> values,
        Dictionary<string, ParsedOptionSource> optionSources,
        List<(int Index, string Value)> positionals,
        string? programName)
    {
        return new ParsedCommand(
            spec,
            values,
            positionals.Take(spec.MaximumPositionals).Select(item => item.Value).ToList(),
            programName,
            optionSources);
    }

    private static OptionSpec? ResolveOption(DecodeCommandSpec spec, string optionName)
    {
        if (spec.TryGetOption(optionName, out OptionSpec? exact))
        {
            return exact;
        }

        if (!optionName.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var matches = spec.Options
            .SelectMany(option => option.Names
                .Where(name => name.StartsWith("--", StringComparison.Ordinal)
                    && name.StartsWith(optionName, StringComparison.Ordinal))
                .Select(name => (Option: option, Name: name)))
            .ToArray();
        OptionSpec[] matchingOptions = matches
            .Select(match => match.Option)
            .Distinct()
            .ToArray();
        if (matchingOptions.Length == 1)
        {
            return matchingOptions[0];
        }

        if (matchingOptions.Length > 1)
        {
            throw new CommandLineParseException(
                $"ambiguous option: {optionName} could match {string.Join(", ", matches.Select(match => match.Name))}");
        }

        return null;
    }

    private static bool TryProcessShortCluster(
        DecodeCommandSpec spec,
        IReadOnlyList<string> args,
        string token,
        Dictionary<string, object?> values,
        Dictionary<string, ParsedOptionSource> optionSources,
        ref int index,
        out bool help)
    {
        help = false;
        if (!token.StartsWith("-", StringComparison.Ordinal)
            || token.StartsWith("--", StringComparison.Ordinal)
            || token.Length <= 2)
        {
            return false;
        }

        OptionSpec? previous = null;
        for (int offset = 1; offset < token.Length; offset++)
        {
            string name = $"-{token[offset]}";
            if (!spec.TryGetOption(name, out OptionSpec? option))
            {
                if (offset == 1)
                {
                    return false;
                }

                throw new CommandLineParseException(
                    $"argument {previous!.DisplayName}: ignored explicit argument '{token[offset..]}'");
            }

            previous = option;
            if (option.Arity == OptionArity.Flag)
            {
                values[option.Destination] = true;
                optionSources[option.Destination] = ParsedOptionSource.Flag;
                if (option.Destination == "help")
                {
                    help = true;
                    return true;
                }

                continue;
            }

            string? inlineValue = offset + 1 < token.Length ? token[(offset + 1)..] : null;
            help = ApplyOption(args, option, inlineValue, values, optionSources, ref index);
            return true;
        }

        return true;
    }

    private static bool ApplyOption(
        IReadOnlyList<string> args,
        OptionSpec option,
        string? inlineValue,
        Dictionary<string, object?> values,
        Dictionary<string, ParsedOptionSource> optionSources,
        ref int index)
    {
        switch (option.Arity)
        {
            case OptionArity.Flag:
                if (inlineValue is not null)
                {
                    throw new CommandLineParseException(
                        $"argument {option.DisplayName}: ignored explicit argument '{inlineValue}'");
                }

                values[option.Destination] = true;
                optionSources[option.Destination] = ParsedOptionSource.Flag;
                return option.Destination == "help";

            case OptionArity.Value:
                string value = inlineValue ?? ReadRequiredValue(args, ref index, option);
                values[option.Destination] = ConvertOptionValue(option, value);
                optionSources[option.Destination] = ParsedOptionSource.ExplicitValue;
                return false;

            case OptionArity.OptionalValue:
                string? optionalValue = inlineValue ?? TryReadOptionalValue(args, ref index, option);
                values[option.Destination] = optionalValue is null
                    ? option.ConstValue
                    : ConvertOptionValue(option, optionalValue);
                optionSources[option.Destination] = optionalValue is null
                    ? ParsedOptionSource.Constant
                    : ParsedOptionSource.ExplicitValue;
                return false;

            default:
                throw new InvalidOperationException($"Unsupported option arity {option.Arity}");
        }
    }

    private static object? ConvertOptionValue(OptionSpec option, string raw)
    {
        try
        {
            return option.ConvertValue(raw);
        }
        catch (CommandLineParseException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            string message = ex.Message.StartsWith("argument ", StringComparison.Ordinal)
                || ex.Message.StartsWith("--", StringComparison.Ordinal)
                ? ex.Message
                : $"argument {option.DisplayName}: {ex.Message}";
            throw new CommandLineParseException(message);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            string typeName = option.ParseErrorTypeName ?? option.ValueKind.ToString().ToLowerInvariant();
            throw new CommandLineParseException(
                $"argument {option.DisplayName}: invalid {typeName} value: '{raw}'");
        }
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, OptionSpec option)
    {
        if (index + 1 >= args.Count || LooksLikeDifferentOption(args[index + 1]))
        {
            throw new CommandLineParseException($"argument {option.DisplayName}: expected one argument");
        }

        index++;
        return args[index];
    }

    private static string? TryReadOptionalValue(IReadOnlyList<string> args, ref int index, OptionSpec option)
    {
        if (index + 1 >= args.Count)
        {
            return null;
        }

        string candidate = args[index + 1];
        if (LooksLikeDifferentOption(candidate))
        {
            return null;
        }

        if (option.IsValidOptionalValue is not null && !option.IsValidOptionalValue(candidate))
        {
            return null;
        }

        index++;
        return candidate;
    }

    private static bool IsOptionToken(string token) => token.Length > 1 && token[0] == '-';

    private static bool LooksLikeDifferentOption(string token)
        => IsOptionToken(token) && !LooksLikeNegativeNumber(token);

    private static bool LooksLikeNegativeNumber(string token)
        => PythonNumericParser.LooksLikeArgparseNegativeNumber(token);
}
