# NTSC Betamax HiFi audio

Decode an NTSC Betamax HiFi RF capture with:

```powershell
decode-dotnet hifi --betamax --ntsc -f 40 input.flac output.flac
```

`--betamax` selects the four-carrier Beta audio decoder and enables the complete compensation chain used for the sample 16 listening experiment. No MOV, hardware audio track, downloaded profile, or additional compensation argument is required. VHS remains the default when neither `--betamax` nor `--8mm` is selected.

`-f` specifies the real RF sampling frequency in MHz. The first implementation supports 20 and 40 MSPS input and produces 48 kHz audio. The nominal audio sampling frequency stored in an RF FLAC header can be a placeholder: a header reporting 40 kHz does not establish a 40 kHz RF capture rate.

## Default processing

The RF path separates the four NTSC Beta HiFi carriers, demodulates their frequency variation, selects alternating heads, repairs short dropouts and head switches, and converts the result to audio. Carrier powers and head-switch calibration are measured from the input capture. Head-switch interpolation checks the reliability of its anchors and may extend a short gap locally when weak RF and abnormal curvature provide evidence of a contaminated anchor.

The audio path applies the frozen sample 16 empirical frequency compensation, narrow line-frequency interference suppression, stereo-linked dynamic expansion, and high-frequency dynamics. The controller maintains its envelope and gain state across processing blocks. Output gain, channel mapping, and optional whole-file normalization follow the compensation, so changing listening volume does not alter the compander working point.

These compensation coefficients approximate hardware playback using earlier RF/hardware comparisons. They do not constitute a verified reconstruction of a particular Sony circuit. No periodic noise waveform learned from another capture is injected or subtracted.

## Optional controls

| Option | Default | Effect |
| --- | --- | --- |
| `--deemphasis on` | On | Enables the empirical frequency compensation. Use `off` for diagnostics. |
| `--expander on` | On | Enables dynamic expansion and high-frequency dynamics. Use `off` for diagnostics. |
| `--beta-reference-offset 0` | 0 dB | Adjusts the detector working point; permitted range is -12 to +12 dB. This is not output volume. |
| `--gain 1` | 1 | Changes final output volume after compensation. |
| `--normalize` | Off | Normalizes the completed output after compensation. |

Both compensation switches are already on in a normal `--betamax` command. A bypass disables processing; it is not an extra feature needed to obtain the default sound. Disabling both switches creates a diagnostic output and is not the normal Beta playback preset.

In Beta mode, `--doc full` interpolates RF losses up to 80 microseconds only when reliable anchors exist, and locally mutes longer losses. `--doc mute` locally mutes invalid RF, including short losses; `--doc off` disables dropout repair. Beta does not copy the other audio channel to fill a loss. `--head_switching_interpolation off` separately disables the capture-calibrated head-switch gap repair.

## Current scope

This path is an offline decoder. It uses RF context and temporary disk storage for the audio stages; a long recording needs free disk space in addition to its final output. Temporary files are removed on completion or failure. The first samples of a file have no preceding RF or audio history, so startup behavior can differ from playback with a real preroll.

The initial implementation supports NTSC only, the exact CPU backend, and 48 kHz output. PAL, 8mm selection together with Betamax, live preview, GNU Radio output, the old bias-guess/fine-tune path, and a second spectral NR stage are rejected. Nonzero VHS/8mm AFE overrides and explicitly supplied expander/de-emphasis coefficient options do not configure the Beta preset and are rejected. Existing stereo and mono output modes remain available.

Synthetic and numerical reference tests establish implementation behavior. Real-capture validation establishes behavior for the tested excerpts; it does not establish every tape's noise level, subjective quality, or equivalence to a hardware deck.
