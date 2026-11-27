namespace VHSDecode.Preview;

/// <summary>A supported input and output combination in the packaged win-x64 decoder.</summary>
public readonly record struct RealtimePreviewCombination(PreviewSourceFormat SourceFormat,
    PreviewVideoStandard Standard, int InputSampleRateHz, BetamaxVideoProfile BetamaxProfile,
    PreviewAudioTrack AudioTrack)
{
    public bool SupportsCvbs50Ohms => SourceFormat == PreviewSourceFormat.Cvbs;
    public bool HasVideo => AudioTrack != PreviewAudioTrack.VhsHiFi;
}

/// <summary>Format capabilities of the shipping decoder. Queries do not load the native DLL.</summary>
public static class RealtimePreviewCapabilities
{
    public static IReadOnlyList<RealtimePreviewCombination> SupportedCombinations { get; } =
        Array.AsReadOnly(Build().ToArray());

    public static bool IsSupported(RealtimePreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var combination = new RealtimePreviewCombination(options.SourceFormat, options.Standard,
            options.InputSampleRateHz, options.BetamaxProfile, options.AudioTrack);
        return options.DecodeThreads is >= 1 and <= 64 && Enum.IsDefined(options.Region)
            && (!options.Cvbs50Ohms || combination.SupportsCvbs50Ohms)
            && (!options.IncludeSignalScopes || combination.HasVideo)
            && SupportedCombinations.Contains(combination);
    }

    private static IEnumerable<RealtimePreviewCombination> Build()
    {
        const BetamaxVideoProfile normal = BetamaxVideoProfile.Standard;
        const PreviewAudioTrack silent = PreviewAudioTrack.None;
        foreach (int rate in new[] { 20_000_000, 40_000_000 })
            foreach (var source in new[] { PreviewSourceFormat.SecamVhs, PreviewSourceFormat.MesecamVhs })
            {
                yield return new(source, PreviewVideoStandard.Secam, rate, normal, silent);
                // Existing hosts used PAL to select the 625/50 timing of these explicit sources.
                yield return new(source, PreviewVideoStandard.Pal, rate, normal, silent);
            }

        foreach (var standard in new[] { PreviewVideoStandard.Ntsc, PreviewVideoStandard.Pal })
        {
            foreach (int rate in new[] { 8_000_000, 8_192_000, 10_000_000, 20_000_000, 40_000_000 })
                yield return new(PreviewSourceFormat.Vhs, standard, rate, normal, PreviewAudioTrack.VhsHiFi);
            foreach (int rate in new[] { 20_000_000, 40_000_000 })
            {
                foreach (var source in new[] { PreviewSourceFormat.Vhs, PreviewSourceFormat.SVhs,
                    PreviewSourceFormat.Cvbs, PreviewSourceFormat.Betamax })
                    yield return new(source, standard, rate, normal, silent);
                if (standard == PreviewVideoStandard.Pal)
                {
                    yield return new(PreviewSourceFormat.LaserDisc, standard, rate, normal, silent);
                    yield return new(PreviewSourceFormat.LaserDisc, standard, rate, normal, PreviewAudioTrack.LaserDiscAnalog);
                    continue;
                }
                foreach (var profile in new[] { normal, BetamaxVideoProfile.HiFi,
                    BetamaxVideoProfile.SuperBeta, BetamaxVideoProfile.SuperBetaHiFi, BetamaxVideoProfile.EdBeta })
                {
                    if (profile != normal)
                        yield return new(PreviewSourceFormat.Betamax, standard, rate, profile, silent);
                    // Standard + audio retains the existing implicit HiFi profile selection.
                    if (profile != BetamaxVideoProfile.SuperBeta)
                        yield return new(PreviewSourceFormat.Betamax, standard, rate, profile, PreviewAudioTrack.BetamaxHiFi);
                }
                foreach (var track in new[] { silent, PreviewAudioTrack.LaserDiscAnalog, PreviewAudioTrack.LaserDiscDigitalPcm,
                    PreviewAudioTrack.LaserDiscDts, PreviewAudioTrack.LaserDiscAc3Rf })
                    yield return new(PreviewSourceFormat.LaserDisc, standard, rate, normal, track);
            }
        }
    }
}
