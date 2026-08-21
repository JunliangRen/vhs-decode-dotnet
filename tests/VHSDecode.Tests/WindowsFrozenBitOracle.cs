namespace VHSDecode.Tests;

internal static class WindowsFrozenBitOracle
{
    internal static void Equal<T>(T expected, T actual)
    {
        if (OperatingSystem.IsWindows())
        {
            Xunit.Assert.Equal(expected, actual);
        }
    }
}
