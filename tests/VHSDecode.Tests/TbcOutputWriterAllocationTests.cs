using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcOutputWriterAllocationTests
{
    [Fact(DisplayName = "Little-endian TBC streaming avoids per-field copies")]
    public void LittleEndianTbcStreamingAvoidsPerFieldCopies()
    {
        if (!BitConverter.IsLittleEndian)
        {
            return;
        }

        var samples = new ushort[400_000];
        TbcOutputWriter.WriteSamples(Stream.Null, samples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TbcOutputWriter.WriteSamples(Stream.Null, samples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_024, $"TBC streaming allocated {allocated:N0} bytes.");
    }
}
