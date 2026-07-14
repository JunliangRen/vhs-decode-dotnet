namespace VHSDecode.Core.HiFi;

public static class HiFiAudioProcessing
{
    public static float CancelDcAndTrim(Span<float> audio, int trimSamples)
    {
        if (trimSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trimSamples));
        }

        if (audio.Length <= trimSamples * 2)
        {
            throw new ArgumentException("HiFi audio block must be longer than both trim regions.", nameof(audio));
        }

        int end = audio.Length - trimSamples;
        float dc = NumbaFastMean(audio[trimSamples..end]);
        for (int i = trimSamples; i < end; i++)
        {
            audio[i] -= dc;
        }

        audio[..trimSamples].Clear();
        audio[end..].Clear();
        return dc;
    }

    internal static float NumbaFastMean(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return float.NaN;
        }

        int position = 0;
        float sum = 0.0f;
        int vectorizedLength = values.Length & ~31;
        if (vectorizedLength > 0)
        {
            Span<float> accumulators = stackalloc float[32];
            for (; position < vectorizedLength; position += 32)
            {
                for (int lane = 0; lane < 8; lane++)
                {
                    accumulators[lane] = values[position + lane] + accumulators[lane];
                    accumulators[8 + lane] = values[position + 8 + lane] + accumulators[8 + lane];
                    accumulators[16 + lane] = values[position + 16 + lane] + accumulators[16 + lane];
                    accumulators[24 + lane] = values[position + 24 + lane] + accumulators[24 + lane];
                }
            }

            Span<float> merged = stackalloc float[8];
            for (int lane = 0; lane < merged.Length; lane++)
            {
                float value = accumulators[8 + lane] + accumulators[lane];
                value += accumulators[16 + lane];
                merged[lane] = value + accumulators[24 + lane];
            }

            float pair0 = merged[4] + merged[0];
            float pair1 = merged[5] + merged[1];
            float pair2 = merged[6] + merged[2];
            float pair3 = merged[7] + merged[3];
            float quad0 = pair2 + pair0;
            float quad1 = pair3 + pair1;
            sum = quad1 + quad0;
        }

        int fourWideLength = (values.Length - position) & ~3;
        if (fourWideLength > 0)
        {
            float lane0 = sum;
            float lane1 = 0.0f;
            float lane2 = 0.0f;
            float lane3 = 0.0f;
            int fourWideEnd = position + fourWideLength;
            for (; position < fourWideEnd; position += 4)
            {
                lane0 = values[position] + lane0;
                lane1 = values[position + 1] + lane1;
                lane2 = values[position + 2] + lane2;
                lane3 = values[position + 3] + lane3;
            }

            float pair0 = lane2 + lane0;
            float pair1 = lane3 + lane1;
            sum = pair1 + pair0;
        }

        for (; position < values.Length; position++)
        {
            sum = values[position] + sum;
        }

        return (float)((double)sum / values.Length);
    }
}
