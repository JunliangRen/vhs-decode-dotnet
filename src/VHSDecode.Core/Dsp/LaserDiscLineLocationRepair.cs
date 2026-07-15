using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Dsp;

public static class LaserDiscLineLocationRepair
{
    public static LineLocationResult MarkDerivativeErrors(LineLocationResult lineLocations)
    {
        double[] locations = lineLocations.Locations;
        bool[] errors = lineLocations.Filled.ToArray();
        for (int i = 0; i + 2 < locations.Length; i++)
        {
            double secondDerivative = locations[i + 2] - (2.0 * locations[i + 1]) + locations[i];
            if (Math.Abs(secondDerivative) > 4.0)
            {
                errors[i + 1] = true;
                errors[i + 2] = true;
            }
        }

        return lineLocations with { Filled = errors };
    }

    public static LineLocationResult FixBadLines(
        LineLocationResult lineLocations,
        string system,
        LineLocationResult? backup = null,
        bool markDerivativeErrors = true)
    {
        if (markDerivativeErrors)
        {
            lineLocations = MarkDerivativeErrors(lineLocations);
        }

        double[] locations = lineLocations.Locations.ToArray();
        bool[] errors = lineLocations.Filled.ToArray();
        if (backup is not null)
        {
            int count = Math.Min(locations.Length, backup.Locations.Length);
            for (int line = 0; line < count; line++)
            {
                if (double.IsNaN(locations[line]))
                {
                    locations[line] = backup.Locations[line];
                }
            }
        }

        int firstGoodLine = FormatCatalog.ParentSystem(system) == "PAL" ? 0 : 1;
        for (int line = 0; line < errors.Length; line++)
        {
            if (!errors[line])
            {
                continue;
            }

            int previousGood = line - 1;
            while (previousGood >= 0 && errors[previousGood])
            {
                previousGood--;
            }

            int nextGood = line + 1;
            while (nextGood < errors.Length && errors[nextGood])
            {
                nextGood++;
            }

            if (previousGood < firstGoodLine || nextGood >= locations.Length)
            {
                continue;
            }

            double gap = (locations[nextGood] - locations[previousGood]) / (nextGood - previousGood);
            locations[line] = locations[previousGood] + (gap * (line - previousGood));
        }

        return lineLocations with { Locations = locations, Filled = errors };
    }
}
