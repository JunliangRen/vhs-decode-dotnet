using System.Diagnostics.CodeAnalysis;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Tests;

internal static class IppTestRuntime
{
    internal static bool IsRequired
        => Environment.GetEnvironmentVariable("VHSDECODE_REQUIRE_IPP") == "1";

    internal static bool TryProbe([NotNullWhen(true)] out IppRuntimeInfo? info)
    {
        if (IsRequired)
        {
            info = IppRuntime.ProbeRequired();
            return true;
        }

        return IppRuntime.TryProbe(out info);
    }
}
