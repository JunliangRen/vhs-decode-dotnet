namespace VHSDecode.Core.Dsp;

// SciPy make_interp_spline cubic not-a-knot construction and de Boor evaluation.
internal sealed class ScipyCubicNotAKnotInterpolator
{
    private const int Degree = 3;

    private readonly double[] _knots;
    private readonly double[] _coefficients;

    public ScipyCubicNotAKnotInterpolator(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length || x.Length < Degree + 1)
        {
            throw new ArgumentException("Cubic interpolation requires at least four matching x/y values.");
        }

        for (int i = 1; i < x.Length; i++)
        {
            if (!(x[i] > x[i - 1]))
            {
                throw new ArgumentException("Cubic interpolation x values must be strictly increasing.", nameof(x));
            }
        }

        _knots = BuildNotAKnotKnots(x);
        _coefficients = BuildCoefficients(x, y, _knots);
    }

    public double Evaluate(double position)
    {
        if (position < _knots[Degree] || position > _knots[_knots.Length - Degree - 1])
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        int interval = FindInterval(_knots, position);
        Span<double> work = stackalloc double[(Degree + 1) * 2];
        ComputeBasis(_knots, position, interval, work);
        double value = 0.0;
        for (int i = 0; i <= Degree; i++)
        {
            value += _coefficients[interval + i - Degree] * work[i];
        }

        return value;
    }

    private static double[] BuildNotAKnotKnots(ReadOnlySpan<double> x)
    {
        var knots = new double[x.Length + Degree + 1];
        for (int i = 0; i <= Degree; i++)
        {
            knots[i] = x[0];
            knots[knots.Length - 1 - i] = x[^1];
        }

        int target = Degree + 1;
        for (int i = 2; i < x.Length - 2; i++)
        {
            knots[target++] = x[i];
        }

        return knots;
    }

    private static double[] BuildCoefficients(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        double[] knots)
    {
        int coefficientCount = knots.Length - Degree - 1;
        var band = new double[(3 * Degree) + 1, coefficientCount];
        var basisBuffer = new double[(2 * Degree) + 2];
        for (int row = 0; row < x.Length; row++)
        {
            int interval = FindInterval(knots, x[row]);
            ComputeBasis(knots, x[row], interval, basisBuffer);
            for (int basis = 0; basis <= Degree; basis++)
            {
                int column = interval - Degree + basis;
                int bandRow = (2 * Degree) + row - column;
                band[bandRow, column] = basisBuffer[basis];
            }
        }

        double[] rightHandSide = y.ToArray();
        SolveGeneralBand(band, Degree, Degree, rightHandSide);
        return rightHandSide;
    }

    private static void SolveGeneralBand(
        double[,] band,
        int lowerBands,
        int upperBands,
        double[] rightHandSide)
    {
        int count = rightHandSide.Length;
        int diagonalRow = upperBands + lowerBands;
        var pivots = new int[count];
        int lastUpdatedColumn = 0;
        for (int column = 0; column < count; column++)
        {
            if (column + diagonalRow < count)
            {
                for (int row = 0; row < lowerBands; row++)
                {
                    band[row, column + diagonalRow] = 0.0;
                }
            }

            int multiplierCount = Math.Min(lowerBands, count - 1 - column);
            int pivotOffset = 0;
            double pivotMagnitude = Math.Abs(band[diagonalRow, column]);
            for (int i = 1; i <= multiplierCount; i++)
            {
                double magnitude = Math.Abs(band[diagonalRow + i, column]);
                if (magnitude > pivotMagnitude)
                {
                    pivotMagnitude = magnitude;
                    pivotOffset = i;
                }
            }

            pivots[column] = column + pivotOffset;
            if (band[diagonalRow + pivotOffset, column] == 0.0)
            {
                throw new InvalidOperationException("Spline collocation matrix is singular.");
            }

            lastUpdatedColumn = Math.Max(
                lastUpdatedColumn,
                Math.Min(column + upperBands + pivotOffset, count - 1));
            if (pivotOffset != 0)
            {
                for (int offset = 0; offset <= lastUpdatedColumn - column; offset++)
                {
                    int targetColumn = column + offset;
                    int firstRow = diagonalRow + pivotOffset - offset;
                    int secondRow = diagonalRow - offset;
                    (band[firstRow, targetColumn], band[secondRow, targetColumn]) =
                        (band[secondRow, targetColumn], band[firstRow, targetColumn]);
                }
            }

            if (multiplierCount == 0)
            {
                continue;
            }

            double scale = 1.0 / band[diagonalRow, column];
            for (int row = 1; row <= multiplierCount; row++)
            {
                band[diagonalRow + row, column] *= scale;
            }

            for (int targetColumn = column + 1; targetColumn <= lastUpdatedColumn; targetColumn++)
            {
                int columnOffset = targetColumn - column - 1;
                double multiplier = -band[diagonalRow - 1 - columnOffset, targetColumn];
                for (int row = 1; row <= multiplierCount; row++)
                {
                    band[diagonalRow + row - 1 - columnOffset, targetColumn] +=
                        band[diagonalRow + row, column] * multiplier;
                }
            }
        }

        for (int column = 0; column < count - 1; column++)
        {
            int multiplierCount = Math.Min(lowerBands, count - 1 - column);
            int pivot = pivots[column];
            if (pivot != column)
            {
                (rightHandSide[pivot], rightHandSide[column]) =
                    (rightHandSide[column], rightHandSide[pivot]);
            }

            double multiplier = -rightHandSide[column];
            for (int row = 1; row <= multiplierCount; row++)
            {
                rightHandSide[column + row] += band[diagonalRow + row, column] * multiplier;
            }
        }

        int upperTriangleBands = lowerBands + upperBands;
        for (int column = count - 1; column >= 0; column--)
        {
            rightHandSide[column] /= band[diagonalRow, column];
            double multiplier = -rightHandSide[column];
            int firstRow = Math.Max(0, column - upperTriangleBands);
            for (int row = firstRow; row < column; row++)
            {
                rightHandSide[row] +=
                    band[diagonalRow + row - column, column] * multiplier;
            }
        }
    }

    private static int FindInterval(double[] knots, double position)
    {
        int coefficientCount = knots.Length - Degree - 1;
        int interval = Degree;
        while (position < knots[interval] && interval != Degree)
        {
            interval--;
        }

        interval++;
        while (position >= knots[interval] && interval != coefficientCount)
        {
            interval++;
        }

        return interval - 1;
    }

    private static void ComputeBasis(
        double[] knots,
        double position,
        int interval,
        Span<double> work)
    {
        Span<double> values = work[..(Degree + 1)];
        Span<double> previous = work[(Degree + 1)..];
        values[0] = 1.0;
        for (int degree = 1; degree <= Degree; degree++)
        {
            values[..degree].CopyTo(previous);
            values[0] = 0.0;
            for (int basis = 1; basis <= degree; basis++)
            {
                int index = interval + basis;
                double upperKnot = knots[index];
                double lowerKnot = knots[index - degree];
                if (upperKnot == lowerKnot)
                {
                    values[basis] = 0.0;
                    continue;
                }

                double weight = previous[basis - 1] / (upperKnot - lowerKnot);
                values[basis - 1] += weight * (upperKnot - position);
                values[basis] = weight * (position - lowerKnot);
            }
        }
    }
}
