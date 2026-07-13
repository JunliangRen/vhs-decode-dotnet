namespace VHSDecode.Core.CommandLine;

public static class VideoSystemSelector
{
    public static string Select(ParsedCommand command)
    {
        bool pal = command.Values.TryGetValue("pal", out object? palValue) && palValue is true;
        bool ntsc = command.Values.TryGetValue("ntsc", out object? ntscValue) && ntscValue is true;
        bool palm = command.Values.TryGetValue("palm", out object? palmValue) && palmValue is true;

        if (pal && ntsc)
        {
            throw new ArgumentException("ERROR: Can only be PAL or NTSC");
        }

        if (palm && pal)
        {
            throw new ArgumentException("ERROR: Can only be PAL-M or PAL");
        }

        if (palm && ntsc)
        {
            throw new ArgumentException("ERROR: Can only be PAL-M or NTSC");
        }

        if (pal)
        {
            return "PAL";
        }

        if (palm)
        {
            return "PAL_M";
        }

        if (ntsc)
        {
            return "NTSC";
        }

        string system = command.Get<string>("system");
        return system == "PALM" ? "PAL_M" : system;
    }
}
