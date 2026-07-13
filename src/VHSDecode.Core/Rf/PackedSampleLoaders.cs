using System.Buffers.Binary;

namespace VHSDecode.Core.Rf;

public sealed class PackedDdD4To40SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        long start = (sample / 4) * 5;
        int offset = (int)(sample % 4);
        int needed = checked(((readLength * 5) / 4) + 5);

        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[needed];
        int read = stream.ReadAtLeast(buffer, needed, throwOnEndOfStream: false);
        if (read != needed)
        {
            return null;
        }

        int groups = buffer.Length / 5;
        var unpacked = new double[groups * 4];
        for (int group = 0; group < groups; group++)
        {
            int i = group * 5;
            int o = group * 4;
            int s0 = (buffer[i] << 2) | ((buffer[i + 1] >> 6) & 0x03);
            int s1 = ((buffer[i + 1] & 0x3F) << 4) | ((buffer[i + 2] >> 4) & 0x0F);
            int s2 = ((buffer[i + 2] & 0x0F) << 6) | ((buffer[i + 3] >> 2) & 0x3F);
            int s3 = ((buffer[i + 3] & 0x03) << 8) | buffer[i + 4];
            unpacked[o] = ToSignedDdD16(s0);
            unpacked[o + 1] = ToSignedDdD16(s1);
            unpacked[o + 2] = ToSignedDdD16(s2);
            unpacked[o + 3] = ToSignedDdD16(s3);
        }

        if (offset + readLength > unpacked.Length)
        {
            return null;
        }

        var output = new double[readLength];
        Array.Copy(unpacked, offset, output, 0, readLength);
        return output;
    }

    private static short ToSignedDdD16(int tenBitSample)
    {
        return (short)((tenBitSample - 512) << 6);
    }
}

public sealed class Packed3To32SampleLoader : IRfSampleLoader
{
    public double[]? Read(Stream stream, long sample, int readLength)
    {
        long start = (sample / 3) * 4;
        int offset = (int)(sample % 3);
        int groups = (offset + readLength + 2) / 3;
        int needed = checked(groups * 4);

        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[needed];
        int read = stream.ReadAtLeast(buffer, needed, throwOnEndOfStream: false);
        if (read != needed)
        {
            return null;
        }

        var unpacked = new double[groups * 3];
        for (int group = 0; group < groups; group++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(group * 4, 4));
            int o = group * 3;
            unpacked[o] = word & 0x3FF;
            unpacked[o + 1] = (word >> 10) & 0x3FF;
            unpacked[o + 2] = (word >> 20) & 0x3FF;
        }

        var output = new double[readLength];
        Array.Copy(unpacked, offset, output, 0, readLength);
        return output;
    }
}
