namespace VHSDecode.Core.CommandLine;

public sealed class CommandLineParseException(string message) : ArgumentException(message);
