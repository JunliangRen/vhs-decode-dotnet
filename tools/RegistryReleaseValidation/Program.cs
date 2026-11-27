using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Preview;

long origin = long.Parse(args[0]);
long pos = origin;
int threads = int.Parse(args[1]);
int count = 0;
using var stream = Console.OpenStandardInput();
using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
using var decoder = new RealtimePreviewSession(new(PreviewSourceFormat.Vhs, PreviewVideoStandard.Ntsc, 20_000_000)
    { DecodeThreads = threads }, field =>
{
    count++;
    digest.AppendData(BitConverter.GetBytes(field.Timestamp.Ticks));
    digest.AppendData(new byte[] { (byte)(field.IsTopField ? 1 : 0) });
    digest.AppendData(field.Bgra.Span);
    Console.WriteLine($"{field.Timestamp.Ticks},{(field.IsTopField ? 1 : 0)},{Convert.ToHexString(SHA256.HashData(field.Bgra.Span))}");
});
var samples = new short[65536];
int n;
while ((n = stream.ReadAtLeast(MemoryMarshal.AsBytes(samples.AsSpan()), samples.Length * 2, false)) > 0)
{
    if (n % 2 != 0) throw new InvalidDataException("Truncated int16 input");
    decoder.Process(samples.AsSpan(0, n / 2), pos);
    pos += n / 2;
}
decoder.Finish();
Console.WriteLine($"end,{pos},{count},{Convert.ToHexString(digest.GetHashAndReset())}");
