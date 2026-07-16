namespace VHSDecode.Core.Decode;

internal static class DecodeOutputFile
{
    public static FileStream Create(string path)
    {
        // CPython's open(..., "wb") uses deny-none sharing on Windows.
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);
    }
}
