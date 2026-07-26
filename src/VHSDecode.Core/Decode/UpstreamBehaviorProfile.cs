namespace VHSDecode.Core.Decode;

public enum UpstreamBehaviorProfile
{
    V040 = 0,
    Current = 1
}

public static class UpstreamBehaviorProfileParser
{
    public const string V040Value = "v0.4.0";
    public const string CurrentValue = "current";

    public static UpstreamBehaviorProfile Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Equals(V040Value, StringComparison.OrdinalIgnoreCase))
        {
            return UpstreamBehaviorProfile.V040;
        }

        if (value.Equals(CurrentValue, StringComparison.OrdinalIgnoreCase))
        {
            return UpstreamBehaviorProfile.Current;
        }

        throw new ArgumentException(
            $"Unknown upstream behavior profile '{value}'. Expected '{V040Value}' or '{CurrentValue}'.",
            nameof(value));
    }

    public static bool TryParse(string? value, out UpstreamBehaviorProfile profile)
    {
        if (value is not null)
        {
            if (value.Equals(V040Value, StringComparison.OrdinalIgnoreCase))
            {
                profile = UpstreamBehaviorProfile.V040;
                return true;
            }

            if (value.Equals(CurrentValue, StringComparison.OrdinalIgnoreCase))
            {
                profile = UpstreamBehaviorProfile.Current;
                return true;
            }
        }

        profile = default;
        return false;
    }

    public static string ToCommandLineValue(UpstreamBehaviorProfile profile)
        => profile switch
        {
            UpstreamBehaviorProfile.V040 => V040Value,
            UpstreamBehaviorProfile.Current => CurrentValue,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
}
