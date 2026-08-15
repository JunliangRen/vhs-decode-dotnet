namespace VHSDecode.Preview;

internal static class PreviewDropoutConcealer
{
    internal static int Apply(
        Span<byte> plane,
        ReadOnlySpan<bool> dropouts,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || plane.Length != checked(width * height))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (dropouts.Length != plane.Length)
        {
            throw new ArgumentException(
                "Dropout mask dimensions must match the preview plane.",
                nameof(dropouts));
        }

        int repaired = 0;
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = rowOffset + x;
                if (!dropouts[index])
                {
                    continue;
                }

                int pairedLine = (y & 1) == 0 ? y + 1 : y - 1;
                int previousSameField = y - 2;
                int nextSameField = y + 2;
                if (TryCopyFromLine(plane, dropouts, width, height, x, pairedLine, index)
                    || TryCopyFromLine(plane, dropouts, width, height, x, previousSameField, index)
                    || TryCopyFromLine(plane, dropouts, width, height, x, nextSameField, index)
                    || TryCopyHorizontal(plane, dropouts, width, x, rowOffset, index))
                {
                    repaired++;
                }
            }
        }

        return repaired;
    }

    private static bool TryCopyFromLine(
        Span<byte> plane,
        ReadOnlySpan<bool> dropouts,
        int width,
        int height,
        int x,
        int sourceY,
        int destination)
    {
        if ((uint)sourceY >= (uint)height)
        {
            return false;
        }

        int source = (sourceY * width) + x;
        if (dropouts[source])
        {
            return false;
        }

        plane[destination] = plane[source];
        return true;
    }

    private static bool TryCopyHorizontal(
        Span<byte> plane,
        ReadOnlySpan<bool> dropouts,
        int width,
        int x,
        int rowOffset,
        int destination)
    {
        for (int distance = 1; distance <= 8; distance++)
        {
            int leftX = x - distance;
            int rightX = x + distance;
            bool hasLeft = leftX >= 0 && !dropouts[rowOffset + leftX];
            bool hasRight = rightX < width && !dropouts[rowOffset + rightX];
            if (hasLeft && hasRight)
            {
                plane[destination] = (byte)((plane[rowOffset + leftX]
                    + plane[rowOffset + rightX]
                    + 1) / 2);
                return true;
            }

            if (hasLeft)
            {
                plane[destination] = plane[rowOffset + leftX];
                return true;
            }

            if (hasRight)
            {
                plane[destination] = plane[rowOffset + rightX];
                return true;
            }
        }

        return false;
    }
}
