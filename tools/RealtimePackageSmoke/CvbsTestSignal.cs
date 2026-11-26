internal static class CvbsTestSignal
{
    public static short[] Create(bool pal)
    {
        const int rate = 20_000_000;
        double halfLine = pal ? 640 : rate * 1001.0 / (30_000 * 1050);
        int halves = pal ? 625 : 525, equalizers = pal ? 5 : 6;
        var samples = new short[(int)(halfLine * halves * 7)];
        Array.Fill(samples, (short)3500);
        for (int half = 0; half < halves * 7; half++)
        {
            int local = half % halves;
            bool vertical = local < equalizers * 3;
            if (!vertical && half % 2 != 0) continue;
            int start = (int)Math.Round(half * halfLine);
            int pulse = vertical ? local / equalizers == 1 ? (int)halfLine - 94 : 47 : 94;
            Array.Fill(samples, (short)-4000, start, pulse);
            if (!vertical && start + 1200 < samples.Length)
                Array.Fill(samples, (short)16000, start + 210, 990);
        }
        return samples;
    }
}
