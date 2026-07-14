namespace VHSDecode.Core.HiFi;

public sealed record HiFiAfeParameters
{
    public required string TapeFormat { get; init; }
    public required string Standard { get; init; }
    public required double FieldRateHz { get; init; }
    public required double? HorizontalFrequencyHz { get; init; }
    public required double LeftCarrierDeviationHz { get; init; }
    public required double RightCarrierDeviationHz { get; init; }
    public required double LeftNotchWidthHz { get; init; }
    public required double RightNotchWidthHz { get; init; }
    public required double LeftCarrierHz { get; init; }
    public required double RightCarrierHz { get; init; }

    public double LeftBandPassLowHz => LeftCarrierHz - LeftNotchWidthHz;
    public double LeftBandPassHighHz => LeftCarrierHz + LeftNotchWidthHz;
    public double RightBandPassLowHz => RightCarrierHz - RightNotchWidthHz;
    public double RightBandPassHighHz => RightCarrierHz + RightNotchWidthHz;

    public static HiFiAfeParameters FromOptions(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        double fieldRateHz;
        double? horizontalFrequencyHz;
        double leftCarrierDeviationHz;
        double rightCarrierDeviationHz;
        double leftNotchWidthHz;
        double rightNotchWidthHz;
        double leftCarrierHz;
        double rightCarrierHz;

        switch (options.TapeFormat, options.Standard)
        {
            case ("vhs", "p"):
                fieldRateHz = 50.0;
                horizontalFrequencyHz = null;
                leftCarrierDeviationHz = 150_000.0;
                rightCarrierDeviationHz = 150_000.0;
                leftNotchWidthHz = 371_506.25;
                rightNotchWidthHz = 371_506.25;
                leftCarrierHz = 1_400_000.0;
                rightCarrierHz = 1_800_000.0;
                break;
            case ("vhs", "n"):
                fieldRateHz = 59.94;
                horizontalFrequencyHz = 15_750.0;
                leftCarrierDeviationHz = 150_000.0;
                rightCarrierDeviationHz = 150_000.0;
                leftNotchWidthHz = 371_506.25;
                rightNotchWidthHz = 371_506.25;
                leftCarrierHz = 1_300_000.0;
                rightCarrierHz = 1_700_000.0;
                break;
            case ("8mm", "p"):
                fieldRateHz = 50.0;
                horizontalFrequencyHz = 15_625.0;
                leftCarrierDeviationHz = 100_000.0;
                rightCarrierDeviationHz = 50_000.0;
                leftNotchWidthHz = 240_000.0;
                rightNotchWidthHz = 75_000.0;
                leftCarrierHz = 1_500_000.0;
                rightCarrierHz = 1_700_000.0;
                break;
            case ("8mm", "n"):
                fieldRateHz = 59.94;
                horizontalFrequencyHz = 15_750.0;
                leftCarrierDeviationHz = 100_000.0;
                rightCarrierDeviationHz = 50_000.0;
                leftNotchWidthHz = 240_000.0;
                rightNotchWidthHz = 75_000.0;
                leftCarrierHz = 1_500_000.0;
                rightCarrierHz = 1_700_000.0;
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported HiFi format/standard combination: {options.TapeFormat}/{options.Standard}.",
                    nameof(options));
        }

        // Release 4.0 applies deviation overrides after calculating the notch widths.
        if (options.AfeLeftCarrierDeviationHz != 0.0)
        {
            leftCarrierDeviationHz = options.AfeLeftCarrierDeviationHz;
        }

        if (options.AfeRightCarrierDeviationHz != 0.0)
        {
            rightCarrierDeviationHz = options.AfeRightCarrierDeviationHz;
        }

        if (options.AfeLeftCarrierHz != 0.0)
        {
            leftCarrierHz = options.AfeLeftCarrierHz;
        }

        if (options.AfeRightCarrierHz != 0.0)
        {
            rightCarrierHz = options.AfeRightCarrierHz;
        }

        return new HiFiAfeParameters
        {
            TapeFormat = options.TapeFormat,
            Standard = options.Standard,
            FieldRateHz = fieldRateHz,
            HorizontalFrequencyHz = horizontalFrequencyHz,
            LeftCarrierDeviationHz = leftCarrierDeviationHz,
            RightCarrierDeviationHz = rightCarrierDeviationHz,
            LeftNotchWidthHz = leftNotchWidthHz,
            RightNotchWidthHz = rightNotchWidthHz,
            LeftCarrierHz = leftCarrierHz,
            RightCarrierHz = rightCarrierHz
        };
    }
}
