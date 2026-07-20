# Compatibility evidence inventory

[English README](../README.md) | [简体中文](../README.zh-CN.md) |
[日本語](../README.ja.md)

This shared document preserves the detailed implementation, numerical-boundary,
fixture, and output-hash inventory that previously made the main README too
long. The three localized README files summarize this same project state and
link here for language-neutral technical evidence.

## Project baseline

.NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode).

Current upstream snapshot used for analysis:

- Repository: `oyvindln/vhs-decode`
- Release: `v0.4.0`
- Commit: `43155200da87c0d49eb37d8ec09b1372075ee8e4`

> [!IMPORTANT]
> This is a work-in-progress compatibility port, not yet a drop-in replacement
> for every upstream format, option, and real-world capture. The verified scope
> and known gaps are documented below.

## Table of contents

- [Scope](#scope)
- [Compatibility status](#compatibility-status)
  - [At a glance](#at-a-glance)
  - [Implemented and verified](#implemented-and-verified)
    - [Solution and command entry points](#solution-and-command-entry-points)
    - [HiFi decode](#hifi-decode)
    - [CLI parsing and format catalog](#cli-parsing-and-format-catalog)
    - [Input and container loading](#input-and-container-loading)
    - [DSP and filter foundations](#dsp-and-filter-foundations)
    - [LaserDisc video and audio](#laserdisc-video-and-audio)
    - [Metadata, databases, and sidecars](#metadata-databases-and-sidecars)
    - [VHS luma and RF processing](#vhs-luma-and-rf-processing)
    - [VHS chroma processing](#vhs-chroma-processing)
    - [Decode engine and field rendering](#decode-engine-and-field-rendering)
    - [Signal processing, sync, and recovery parity](#signal-processing-sync-and-recovery-parity)
    - [Runtime behavior and streaming output](#runtime-behavior-and-streaming-output)
  - [Differential verification](#differential-verification)
    - [VHS verification](#vhs-verification)
    - [CVBS verification](#cvbs-verification)
    - [LaserDisc verification](#laserdisc-verification)
  - [Remaining compatibility work](#remaining-compatibility-work)
- [Build and test](#build-and-test)
- [License](#license)

## Scope

This port targets the decode CLIs only:

- `decode.py vhs`
- `decode.py cvbs`
- `decode.py ld`
- `decode.py hifi`
- standalone aliases equivalent to `vhs-decode`, `cvbs-decode`, `ld-decode`,
  and `hifi-decode`

The double-click GUI launcher and other user-operation UI, filter tuning, and
TBC utility tools are intentionally outside the port scope unless they are
required by the decode pipeline itself.

## Compatibility status

### At a glance

| Area | Status | Current boundary |
| --- | --- | --- |
| Solution and tests | Implemented | .NET 11, `.slnx`, and xUnit v3 work with Visual Studio Test Explorer and `dotnet test`. |
| CLI and arguments | Implemented and snapshot-tested | `decode`, `vhs-decode`, `cvbs-decode`, `ld-decode`, and `hifi-decode` expose the v0.4.0 decode-facing command surface. |
| HiFi decode | Implemented; more real-capture verification remains | PAL VHS and NTSC 8mm synthetic RF baselines are byte-exact. |
| VHS decode | Implemented; rare parity gaps remain | All valid format/filter combinations and extensive NTSC field/output fixtures are covered. |
| CVBS decode | Implemented for release-supported runtime systems | PAL and NTSC execute as v0.4.0 does; uncommon vblank and option interactions still need broader fixtures. |
| LaserDisc decode | Implemented; rare parity gaps remain | Video, EFM, analog audio, AC3, RF-TBC, metadata, and sidecars are wired with PAL/NTSC differential coverage. |
| Input containers | Broadly implemented | Raw input and common FFmpeg/PyAV container paths are covered; rare codec/timestamp cases remain. |
| Output and recovery | Implemented; edge cases remain | Streaming TBC/audio writes, JSON recovery, SQLite, logs, and failure ordering are covered. |
| Interactive UI and developer tooling | Out of scope | The double-click GUI launcher, Matplotlib `--debug_plot` windows, and line-profiler report are intentionally not rendered. |

The status table is intentionally conservative. "Implemented" means the decode
path exists and has focused compatibility tests; it does not claim that every
possible capture has already been proven byte-for-byte identical.

### Implemented and verified

#### Solution and command entry points

- Visual Studio compatible `VHSDecodeDotNet.slnx`
- `net11.0` CLI and core library plus a standard
  `Microsoft.NET.Test.Sdk`/xUnit v3 test project discoverable in Visual Studio
  Test Explorer
- `decode.py`-style top-level dispatch for `vhs`, `cvbs`, `ld`, and `hifi`
- the CLI builds as `decode.exe` and also emits `vhs-decode.exe`,
  `cvbs-decode.exe`, `ld-decode.exe`, and `hifi-decode.exe` apphost aliases
  that infer their subcommand from the executable name
- compatibility option registry for VHS, CVBS, and LaserDisc decode commands

#### HiFi decode

- the complete v0.4.0 HiFi argparse surface is represented by a typed command
  spec: all 42 options, aliases, defaults, frequency parsing, VHS/8mm default
  selection, preview overrides, and both standalone/facade help snapshots are
  locked to upstream
- HiFi command execution now connects native raw/container input routing,
  Release 4.0 bias measurement behavior and per-block carrier progress, exact
  first/middle/final overlap framing, bounded parallel block decoding, ordered
  audio post-processing,
  dual-mono naming and padding, WAV PCM16 or FLAC PCM24 output, normalization,
  cancellation/error finalization, Release 4.0's per-block progress bar and
  five-line timing/buffer reports, and facade/standalone command dispatch
- a full-command 2,800,000-sample, 6 MHz PAL VHS HiFi synthetic RF baseline
  reproduces Release 4.0's single first/final block and Numba negative-index
  worker copy behavior; the resulting 22,796-frame PCM16 WAV is byte-for-byte
  identical with SHA-256
  `325A4ABFB4922FE814338BAB377A94E6C2FD96277244813433A72F6ED5723553`
- a matching 6 MHz NTSC 8mm HiFi baseline covers its asymmetric carrier
  deviations and the Nyquist-symmetric left AFE edge; its 22,796-frame PCM16
  WAV is byte-for-byte identical with SHA-256
  `E1AAF3F68DF1392617BC28D162D2E3DD2AFE6251E91E06D4D3191540C3EFA83F`
- HiFi bias pre-reading preserves Release 4.0's omission of `--raw_format`:
  raw files are measured according to their extension, while stdin prints the
  measurement heading and then raises the same required-format error even when
  an override was supplied
- HiFi FLAC input uses buffered FFmpeg decoding as the bit-exact stand-in for
  Release 4.0's libsndfile path, avoiding `nobuffer` short-file truncation;
  10,000 deterministic 24-bit samples match after int16 normalization and FLAC
  `STREAMINFO` supplies the exact progress total
- HiFi LDF and unknown-container routing now preserves Release 4.0's observable
  tool sequence and arguments: both LD readers are probed first, LDF prefers the
  `flac` raw decoder before FFmpeg, and fallback FFmpeg paths retain
  `-fflags nobuffer` plus their version/warning diagnostics
- HiFi `--gnuradio` now exposes the Release 4.0 REP endpoint on port 5555 and
  returns the summed filtered channels as native float32 after each client
  request; the streaming decoder forces the same single-worker ordering for
  this mode
- HiFi `--preview` now routes every post-processed stereo block to a native
  Windows WinMM 44.1 kHz PCM16 stream while continuing to write the requested
  output file; float32 conversion preserves Release 4.0's truncate-and-wrap
  behavior, and systems without an available device retain its existing
  `preview is not available` fallback
- HiFi raw-input normalization covers `u8`, `u10le`, `u12le`, `u16le`, `s8`,
  `s10le`, `s12le`, `s16le`/`raw`, and `f32le`; representative extrema and
  center values match all 54 upstream v0.4.0 float32 bit patterns exactly
- HiFi AFE and stream planning covers all VHS/8mm PAL/NTSC carrier,
  deviation, notch-width, field-rate, and override combinations plus the
  quadrature/Hilbert IF rates, exact `Fraction(float)` resampling ratios,
  soxr quality profiles, half-second/custom block sizing, overlap rounding,
  rate-sync warning state, and final-block output lengths from v0.4.0
- HiFi quadrature and Hilbert FM discriminator kernels preserve v0.4.0's
  carrier/deviation casts, finite oscillator geometry, conjugate phase
  differencing, analytic-signal multiplier quirk, unwrap behavior, float32
  clipping, and untouched final output sample; deterministic power-of-two and
  arbitrary-length vectors match all 29 upstream output/oscillator hashes
- HiFi block resampling dynamically uses the exact libsoxr revision embedded by
  python-soxr 1.1.0, with LQ/MQ/HQ/VHQ routing, exact large
  `Fraction(float)` rates, `last=True` flushing, per-block `clear()`, separate
  stereo state, and unchanged-rate bypasses; 11 vectors spanning every real
  HiFi rate stage match v0.4.0 float32 output byte for byte
- HiFi AFE execution reproduces the 22nd-order, 220 dB Chebyshev-II
  forward/backward band-pass path at the actual Rust float32 coefficient
  boundary for every VHS/8mm carrier-width pair, both 40 MHz quadrature and
  8,388,608 Hz Hilbert IF rates, the 6 MHz NTSC 8mm Nyquist-symmetric carrier
  pairing, and arbitrary carrier overrides
- the HiFi block demodulation stage connects exact IF resampling, independent
  left/right AFE filters, quadrature or Hilbert FM, 192 kHz resampling, and
  Numba-fastmath-compatible DC cancellation/trimming; two deterministic
  524,288-sample stereo chains and mono channel routing match v0.4.0 output
  bits, including reduction boundaries from 1 through 94,000 samples
- the standalone HiFi dropout-compensation kernel reproduces Release 4.0's
  128-point NumPy complex64 FFT and SIMD magnitude detector,
  Numba-fastmath mean/standard-deviation reductions, overlapping range
  routing, raised-cosine DC correction, and copy/mute fades for stereo,
  dual-mono, and single-channel modes bit for bit
- the standalone HiFi head-switch interpolation path reproduces Release 4.0's
  22nd-order, 200 dB Chebyshev-II high-pass filter, SciPy peak distance,
  prominence and width semantics, Windows NumPy AVX2 equal-priority order,
  boundary merging, linear gap repair, and Numba-compatible smoothing bit for
  bit
- the public HiFi block decoder now follows Release 4.0's complete in-block
  order through DOC, head-switch repair, final libsoxr conversion, stereo,
  mid/side and mono mixing, float32 gain, and next-block automatic carrier
  tuning; full RF-to-audio blocks covering all seven audio modes and a retuned
  second block match upstream output bits, including the mono alias/double-gain
  quirk
- the stateful HiFi audio post-processor reproduces Release 4.0's cascaded
  1 Hz DC blocker, VHS and 8mm deemphasis order, peak/RMS expander envelopes,
  first-block state priming, 1.5 ms startup mute, stereo interleave, and
  per-channel peak tracking; five two-block scenarios match upstream float32
  output and state bits exactly
- nonzero HiFi spectral noise reduction reproduces Release 4.0's stateful
  nonstationary gate, including SciPy float32 STFT/ISTFT, noisereduce mask
  geometry, forward/backward temporal smoothing, two-block history, end
  padding, and independent stereo state; three-block 44.1/48 kHz baselines at
  reduction amounts 0.25, 0.5, and 1.0 plus a two-block full post-processing
  chain match upstream float32 output byte for byte

#### CLI parsing and format catalog

- upstream `-h` / `--help` handling for all three decode commands, including
  zero-exit help before positional argument validation
- complete v0.4.0 argparse help snapshots for the three standalone and three
  `decode.py` facade invocations, preserving usage wrapping, metavar spelling,
  section grouping, descriptions, and normalized upstream program names
- parser coverage test that verifies current VHS/CVBS/LD specs accept the
  upstream decode-facing argparse option names and aliases
- VHS `--ire0_adjust` preserves Release 4.0's exact-name pre-normalization,
  abbreviated-option consumption, comma-part validation, and lowercased value
  shape rather than applying generic optional-argument behavior
- VHS `--params_file` validates by performing argparse-style read opens,
  preserving Python errno/path representations for missing, denied, invalid,
  and directory inputs while accepting standard input and Windows devices
- Python 3 numeric argument conversion for integer, float, and frequency
  options, including underscore separators, Unicode decimal digits,
  arbitrary-precision parsed integers, signed `NaN`/infinity, suffix-adjacent
  whitespace behavior, and argparse's `-\.?\d` negative-option boundary
- byte-for-byte regenerated v0.4.0 format parameter catalog covering all 560
  tape system/format/speed combinations, 7 CVBS systems, and 4 LD variants
- a full decode compatibility matrix constructs filters and demodulates one
  32768-sample RF block for all 357 valid tape combinations; all 1,428
  float32 channels match v0.4.0 byte for byte, including the 75 internal
  non-color-under burst references, with smoke coverage for all 7 CVBS systems
  and all 4 normal/lowband LD variants

#### Input and container loading

- native RF sample loader foundation for `.u8`, `.r8`, `.s16`, `.u16`, `.r16`,
  `.rf`, `.lds`, `.r30`, and mono PCM16 `.wav`
- FFmpeg-backed RF container loader path for upstream `.ldf`, `.flac`, `.vhs`,
  and `raw.oga` inputs, decoding them as mono signed 16-bit PCM with an
  upstream-style 2 MB rewind cache and 40 MB seek/restart threshold
- container seeks probe the decoded stream's actual sample rate and convert RF
  sample offsets to FFmpeg timestamps from that rate, so `--no_resample`
  captures such as 17.9 MHz FLAC do not silently seek as if they were 40 MHz
- converted container streams reproduce v0.4.0 PyAV `AudioResampler` plane
  geometry: `ffprobe` supplies stream rate/channel/format data, while FFmpeg
  frame metadata supplies every converted frame's sample count and first-frame
  PTS. The loader retains the observable 32-sample aligned plane padding,
  including FFmpeg's 64-byte zeroed safety area, `AudioResampler`'s
  non-shrinking high-water plane capacity, and its alternating recycled tail
  samples, and maps seeks in that padded sample space. Release 4.0 SHA and
  reconstructed plane baselines cover 40 kHz and 17.9 kHz mono/stereo WAV,
  PCM16/PCM24 FLAC,
  Ogg/FLAC LDF, AAC, MP3, ALAC, Vorbis, and stereo float WAV, including MP3's
  short initial frame, Vorbis large-frame-to-small-frame transitions, variable
  terminal frames, nonzero restarts beyond the 2 MB rewind window, and EOF
  frame flushing
- complete static 48 kHz IMA ADPCM WAV inputs use an internal FFmpeg
  n8.1.2-compatible 2-, 3-, 4-, and 5-bit mono/stereo block decoder, avoiding
  host-FFmpeg decoder drift; exact logical-frame, stereo-downmix, plane-padding,
  and random-read baselines cover all eight bit-depth/channel combinations,
  while other sample rates retain the FFmpeg resampling path
- upstream-style FFmpeg stdin fallback for unrecognized RF input containers
- upstream-style FFmpeg stdin resampling loader for raw RF inputs when
  `--inputfreq`, `-f`, or `--cxadc` requires conversion to 40 MHz, including
  a 16 MB rewind buffer for overlap-save reads
- VHS loader routing preserves the v0.4.0 quirk that the FFmpeg stdin path is
  used whenever `--no_resample` is absent, even for an already-40-MHz source;
  only lowercase `.lds` and `.ldf` bypass it at 40 MHz, while `.r30` requires
  `--no_resample`
- the positional input name `-` reads RF from standard input like v0.4.0;
  preflight does not require a file literally named `-`, and the decode engine
  leaves the caller-owned stdin stream open
- CVBS input loader selection now follows upstream sample-frequency handling,
  so `-f/--frequency` and `--cxadc` route through FFmpeg to produce 40 MHz RF
- LD explicit `-f/--frequency` loader selection now follows upstream
  `make_loader` behavior even when the value is already 40 MHz, including the
  FFmpeg stdin path and packed `.lds`/`.r30` resampling rejection
- frequency parsing compatible with upstream suffix handling, including `fsc`,
  `fscpal`, and `cxadc`
- VHS/CVBS system selection and conflict checks

#### DSP and filter foundations

- ports of upstream Rust helper math for complex angle, angle unwrap, and
  forward difference
- port of upstream `unwrap_hilbert` instantaneous-frequency demod helper
- PocketFFT-compatible radix-2/Bluestein real and complex transforms, analytic
  signal generation, and FM demodulation helpers
- reusable RF demod block skeleton with frequency-domain RF/video filters
- LD blocks reuse one full input spectrum across RF video/high-pass, EFM, and
  analog-audio branches when those side channels are active
- frequency-domain low-pass/band-pass super-Gaussian, ramp, mirror, apply, and
  roll helpers
- Butterworth low-pass/high-pass and notch IIR design plus BA/SOS frequency
  response helpers
- upstream-compatible constant-Q RF peaking and video de-emphasis shelf filters
- LD-style time-constant emphasis/de-emphasis IIR for post-demod video

#### LaserDisc video and audio

- LD NTSC `--NTSC_color_notch_filter`, applying the upstream-style
  post-demod video low-pass band-stop between the active `video_lpf_freq` and
  5 MHz
- LD PAL `--V4300D_notch_filter`, applying the upstream-style 8.42-8.6 MHz
  RF FFT anomaly snip before RF video filtering without mutating cached input
  spectra; anomaly detection now uses NumPy-compatible complex magnitudes and
  pairwise float64 `mean + 3*std` reduction rather than squared magnitudes
- LD post-demod video filtering now applies the upstream IEC 60856/60857
  group-delay all-pass equalizer on the output video path while leaving the
  sync/burst reference paths magnitude-compatible
- LD `--deemp_low`, `--deemp_high`, and `--deemp_strength` now preserve the
  v0.4.0 time-constant conversion and NumPy array-power dispatch, including
  native complex power, small-integer powers, and the scalar `0.5` square-root
  fast path; a custom PAL block locks all five float32 demod channels bit-exact
- LD video/reference output now follows upstream's clipped demod FFT source,
  limiting the post-demod video path to 1.5 MHz through 0.75 * sample-rate
  while preserving the unmodified `demod_raw` channel
- LD block output now applies v0.4.0's float32 storage boundary to filtered
  video, raw demod, 0.5 MHz video, burst/pilot references, RF high-pass data,
  and first-stage analog-audio channels before cross-block/field processing
- NTSC and PAL LD 32768-sample v0.4.0 reference blocks are bit-exact after
  float32 storage: NTSC covers `demod_raw`, `video`, `video05`, and
  `demod_burst`, while PAL also covers `demod_pilot`; the PAL regression locks
  SciPy-compatible real-input FFT, IIR response, analytic-signal, raw-demod,
  and pilot double bits before the float32 storage boundary
- LD 0.5 MHz sync/reference video path now follows upstream `FVideo05` by using
  `Fvideo_lpf * Fdeemp * F0_5`, excluding the output-only group-delay equalizer
  and `video_deemp_strength`
- LD color-burst and PAL pilot reference paths now generate upstream-shaped
  `demod_burst` / `demod_pilot` side channels from `Fvideo_lpf * Fdeemp`,
  including the 40-sample burst FIR delay compensation through block and stream
  decode
- LD PAL field decode now uses the `demod_pilot` side channel for upstream-style
  two-pass pilot phase line-location refinement after HSYNC refinement
- PAL pilot circular averaging reproduces NumPy complex128 pairwise blocks,
  recursive splitting, and reciprocal mean scaling before phase correction
- LD NTSC field decode now uses the `demod_burst` side channel for upstream-style
  two-pass burst zero-crossing line-location refinement and LD `fieldPhaseID`
  detection
- LD decode session construction now wires the PAL pilot and NTSC burst field
  refiners into real `ld-decode` sessions
- LD sync-loss recovery now follows the v0.4.0 written-field state in serial
  and worker configurations: an initial no-sync span skips one second, while a
  no-sync span after output logs `skipping one field`, advances by 200 nominal
  lines, and continues decoding; missing field starts log the upstream
  `dropping field` diagnostic and use the same 200-line advance. Every invalid
  LD field also clears the previous line-zero, parity, PAL/NTSC phase, and
  player-skip context before retrying, matching upstream's `prevfield=None`
- LD `--seek` now uses v0.4.0's ten-attempt field probe, advances through
  recoverable fields without consuming valid-field sequence state, scans later
  VBI pairs when the first pair has no frame code, and falls back to file start
  only after EOF at a nonzero probe location; seek progress, completion, and
  early-CLV diagnostics match the upstream messages. A successful match lands
  on the nominal field boundary containing the paired second field, including
  upstream's block-aligned `readloc` truncation. Probe demodulation uses upstream
  target MTF 0 (including `--MTF_offset`) and restores normal target MTF 1 on
  both success and failure
- LD `--MTF` and `--MTF_offset` RF compensation path, using upstream
  `MTF_freq`, `MTF_poledist`, and `MTF_basemult` format parameters before
  Hilbert FM demodulation, with bit-exact NumPy power coverage for fractional,
  positive-integer, and negative-integer levels; automatic MTF now tracks the
  rounded `blackToWhiteRFRatio` over 30 CAV or 900 CLV fields, applies NumPy's
  pairwise float64 mean and the v0.4.0 scaling formula, and transactionally
  re-decodes a field when the level moves by at least 0.05; smaller updates
  retain the source pipeline's one-field speculative-decode delay; CAV/CLV
  state is re-evaluated for every completed VBI field pair, including
  empty-code pairs, while metadata-rejected skip/filler fields leave it
  unchanged;
  the source black/white RF standard deviations use NumPy float64 pairwise
  reduction before the upstream four-decimal ratio rounding
- LD field construction now receives v0.4.0's pre-write `fields_written`
  count for speculative initial reads, the current count for same-field
  MTF/AGC retries, and a realigned current count after recovery
- LD EFM digital-audio front-end path, using the upstream 0-1.9 MHz
  amplitude/phase equalizer curve plus 20 kHz-1.6 MHz super-Gaussian band-pass,
  honoring `--noEFM`, and emitting clipped int16 EFM payloads from RF block and
  overlap-save stream decoding through field slicing, the upstream-style EFM
  PLL, `.efm` output, optional `--preEFM` `.prefm` output, and JSON
  `efmTValues` metadata
- LD audio option model for `--noEFM`, `--preEFM`,
  `--disable_analog_audio`, `--AC3`, `--RF_TBC`,
  `--analog_audio_frequency`, `--ntsc_audio_rate`, and
  `--audio_filterwidth`, including AC3 right-channel carrier and audio
  filter-width parameter overrides plus v0.4.0's unit quirk where the parsed
  MHz value is passed through as Hz (`150kHz` becomes `0.15`, while bare
  `150000` becomes 150 kHz), and the upstream PAL warning when
  `--ntsc_audio_rate` is ignored
- LD NTSC RF video now applies upstream-style left/right analog-audio carrier
  band-stop notches from the runtime `analog_audio` state, including the
  upstream behavior where `--disable_analog_audio` disables PCM output but keeps
  the NTSC RF video notch path active
- LD first-field AGC level tracking, enabled by default and disabled by
  `--noAGC`, estimating sync/blank/white from hsync/back-porch windows plus
  `LD_VITS_*` slices before field rendering
- LD analog-audio decode path, including upstream-style RF audio FFT slicing,
  first-stage Hilbert FM demodulation, whole-read second-stage 20 kHz
  low-pass/75 us de-emphasis overlap filtering with shared stereo peak
  suppression, line-location/wow-aware downscaling to interleaved int16
  PCM, `.pcm` sidecar output, JSON `audioSamples` metadata, and top-level
  `pcmAudioParameters` with resolved `--ntsc_audio_rate` sample rates
- LD analog-audio phase 2 preserves the upstream float32 in-place carrier
  subtraction before its complex FFT; on the NTSC issue-176 fixture this makes
  the default `.pcm` output bit-exact instead of differing by 1 LSB at 17
  samples
- LD `--RF_TBC` field RF sidecar path, using the upstream cubic per-line RF
  resampler with estimated video-white delay compensation, parity-specific
  263/262 or 312/313 line counts, NumPy-style rounded int16 wrapping, and
  default ffmpeg `.tbc.ldf` output
- LD `--AC3` RF audio path, including upstream-style AC3 band-pass FFT filter,
  internal RF_TBC generation when AC3 is enabled, signed 8-bit AC3 demod input
  scaling, and the default `sox | ld-ac3-demodulate | ld-ac3-decode` pipeline
  for `.ac3` sidecar output; on Windows, inherited native anonymous-pipe
  handles connect the three processes directly and the decoder's stdout/stderr
  share one `.ac3.log` handle, matching v0.4.0's process topology and stream
  boundaries. A real-tool integration baseline compares output bytes and
  stable diagnostics with the equivalent direct OS pipeline and covers output
  paths containing spaces
- LD AC3 front-end tests now use a deterministic 32768-sample RF_TBC fixture
  generated against SciPy's `butter`/`freqz`/FFT path, checking selected complex
  response bins and the SHA-256 of all 31744 emitted signed-8-bit samples

#### Metadata, databases, and sidecars

- LD `.tbc.json` now carries upstream-shaped field metadata keys for
  `diskLoc`, `medianBurstIRE`, `fieldPhaseID`, `vbi.vbiData`, and optional
  `vitsMetrics`, with field decode now filling upstream-style
  `medianBurstIRE` from the decoded color-burst window and default
  disk-location / field-phase derivation when detailed LD phase analysis has
  not populated them yet
- LD field metadata now computes default `vitsMetrics` from TBC samples when
  explicit metrics are not already present, including upstream default `wSNR`
  and `bPSNR` output keys from `LD_VITS_whitelocs` / `blacksnr_slice`
- LD `--verboseVITS` field metadata now adds the upstream TBC-derived verbose
  VITS metrics that can be computed from rendered fields, including NTSC
  `ntscWhiteFlagSNR`, `greyPSNR`, `greyIRE`, `ntscLine19Burst0IRE`,
  `whiteIRE`, `blackLinePostTBCIRE`, RF/pre-TBC metrics `greyRFLevel`,
  `whiteRFLevel`, `blackLineRFLevel`, `blackLinePreTBCIRE`,
  `blackToWhiteRFRatio`, NTSC line-19 comb/color metrics
  `ntscLine19ColorPhase`, `ntscLine19ColorRawSNR`,
  `ntscLine19Burst70IRE`, `ntscLine19Color3DRawSNR`, and PAL
  `palVITSBurst50Level`
- VHS/CVBS/LD `.tbc.json` `videoParameters` now preserves the upstream
  inherited `numberOfSequentialFields`, `osInfo`, `version`, and parsed git
  branch/commit keys when present
- LD `--verboseVITS` now follows upstream JSON formatting behavior: default
  `.tbc.json` output is compact, while verbose VITS writes indented metadata
- LD lead-out handling now follows upstream's two-code rule when decoded VBI
  line codes contain `0x80EEEE`, and `--ignoreleadout` keeps decoding past it
- LD field decode now extracts Philips VBI line codes from lines 16-18 using
  the upstream 24-crossing / 2 us spacing check, populating `.tbc.json`
  `vbi.vbiData` and feeding the lead-out detector
- LD verbose VITS metadata now interprets CAV frame codes and CLV
  minute/second/frame codes from paired field VBI data, emitting
  `cavFrameNr`, `clvMinutes`, `clvSeconds`, and `clvFrameNr`; CAV/CLV frame
  status log records retain v0.4.0's trailing space while special lead and
  pulldown statuses remain unpadded in the log message
- LD/CVBS `.tbc.json` `decodeFaults` now follows the upstream field metadata
  shape, including zero-valued LD/CVBS entries and LD's field-phase sequence
  mismatch bit when `fieldPhaseID` does not advance through the configured
  phase cycle
- LD/CVBS field objects are emitted in v0.4.0 insertion order; NTSC falls back to
  phase 1, PAL uses the full burst-presence and double-pass zero-crossing
  detector for its eight-field phase, and PAL-M/NLINHA core sessions use 0;
  phase and repeated-parity fault bits therefore combine in `decodeFaults`
- LD/CVBS `medianBurstIRE` now uses upstream `roundfloat(..., 3)` ties-to-even
  rounding in the shared metadata tree, so JSON and SQLite store the same
  three-decimal value
- LD/VHS/CVBS black setup now follows upstream entry-point behavior: selected
  NTSC non-NTSC-J sources use 7.5 IRE setup, while PAL/PAL-M/NTSC-J paths keep
  `blackIRE` at 0
- `.tbc.json` `videoParameters` now mirrors upstream JSON by omitting the
  SQLite-only/default `decoder` value
- `.tbc.json` field metadata now omits local debug-only pulse/line-threshold
  keys so the emitted field objects stay closer to upstream decode output
- LD/CVBS `.tbc.json` field metadata now omits VHS-only
  `detectedFirstField`/`isDuplicateField` keys while retaining them on VHS
- VHS/CVBS `.tbc.json` `vitsMetrics` now preserves upstream's object shape even
  when no metrics are measurable, while LD SQLite output skips empty
  `vits_metrics` rows like upstream
- zero-noise VITS slices preserve the decoder-specific override: VHS/CVBS emit
  a finite `0.0` PSNR value, while LD's infinite result is filtered from JSON
- `.tbc.db` capture rows now use upstream's empty-string defaults for missing
  `git_branch`/`git_commit` metadata rather than SQLite `NULL`
- VHS/CVBS `--write_db` now creates the upstream-style `.tbc.db` SQLite sidecar,
  with `capture`, `pcm_audio_parameters`, `field_record`, `vits_metrics`, `vbi`,
  and `drop_outs` rows derived from the same metadata tree used for `.tbc.json`
- LD creates `.tbc.db` unconditionally like v0.4.0 rather than gating it on
  the VHS/CVBS `--write_db` switch, including a capture row and no field rows
  when the input ends before a field can be decoded
- v0.4.0's shared VHS/CVBS DB writer argument-order behavior is preserved:
  CVBS and VHS `--noDOD` field/subtable rows use `capture_id = 0` while the
  capture row remains `1`; SQLite foreign-key checking is left disabled upstream
- shared VHS/CVBS DB field rows use `seqNo - 1` for `field_id` and leave the
  LD-only `audio_samples`, `efm_t_values`, and `median_burst_ire` columns `NULL`
- PAL-M `--write_db` retains v0.4.0's schema mismatch: JSON system `PAL-M` is
  inserted unchanged even though the SQLite CHECK accepts only `PAL_M`, so the
  capture insert fails with a constraint error
- VHS/CVBS `.tbc.json` now preserves the upstream inherited top-level
  `pcmAudioParameters` block with a zero sample rate
- VHS `diskLoc` now uses v0.4.0's exact
  `int(inputHz / (FPS * 2)) + 1` samples-per-field denominator, avoiding drift
  from output-line-count approximations on long captures
- VHS `File Frame N` deliberately reports the source RF timeline as
  `floor(diskLoc / 2)`, not the number of frames already emitted to TBC. A
  no-sync 100 ms jump advances this source position without writing a field,
  while completion progress continues to use `fieldsWritten / 2`, matching
  v0.4.0 when bad pre-roll makes the displayed file frame exceed TBC length
- VHS output `seqNo` is assigned from the number of fields already written, so
  initial second-field skips and later duplicate/drop repairs preserve the same
  repeated or non-contiguous sequence numbers as v0.4.0
- VHS field-order state advances only when metadata is actually written:
  ignored opening second fields and dropped fields do not become the next
  comparison anchor, duplicate fillers do, and the last-valid cache remains
  keyed by the field's originally detected parity even when metadata parity is
  repaired; luma, chroma, JSON, and SQLite all consume this one write sequence
- normal CLI `--length` termination counts that same written sequence, matching
  v0.4.0: dropped fields make decoding continue, while inserted fillers count
  toward the requested two-fields-per-frame target and can end the loop
- LD/CVBS `syncConf` now carries v0.4.0's line-zero anchor confidence: three
  local/next/previous estimates retain 100, a strong local-only estimate caps
  at 90, and previous-field recovery subtracts 10 with a floor of 10; the final
  positive second-difference test over `linelocs` can cap that value at 45,
  after which field-order repair confidence can lower it further
- JSON layout matches v0.4.0's streaming dumper: VHS/CVBS and ordinary LD output
  are compact, while LD `--verboseVITS` keeps root keys flush-left and indents
  each nested object by four spaces
- JSON double tokens use Python-compatible spelling, retaining `.0` and signed
  zero for integral floats and normalizing scientific notation to forms such as
  `1e-07`; non-finite values remain rejected like `allow_nan=False`
- VHS/CVBS `-f cxadc` resolves to v0.4.0's `28.636363... MHz` special value,
  while LD continues to reject it; interpolation and field-order choices remain
  case-sensitive like their argparse definitions
- `--field_order_confidence` applies v0.4.0's `0..100` clamp before the value
  reaches field-cadence detection, including exact handling of negative and
  greater-than-100 CLI values
- LD `--version`/`-v` is scanned before all other arguments, and VHS
  `--params_file` validates files during parsing while retaining argparse's `-`
  stdin input convention
- Cython field cadence resolution now retains first/second boundary confidence
  below the user threshold, continues the previous alternating cadence first,
  and permits fallback parity to override only when it exceeds both measurements;
  half-line boundary rounding uses C ties-away-from-zero behavior

#### VHS luma and RF processing

- initial decode filter-set builder that derives basic RF/video responses from
  embedded upstream format parameters
- VHS RF video filtering preserves v0.4.0's magnitude-only zero-phase response;
  the transfer-function phase is not carried into the Hilbert FM input
- main post-demod video response path that combines video LPF and VHS-style
  de-emphasis where upstream parameters provide it
- VHS/CVBS `--notch` and `--notch_q` common option support with the distinct
  v0.4.0 stage ordering: VHS applies the FFT notch to the input RF spectrum
  before RF video filtering/FM demodulation and repeats the BA notch in the
  color-under branch, while CVBS applies zero-phase BA filtering to direct luma
  after the optional chroma trap and derives its 0.5 MHz branch from that luma
- VHS `--fm_audio_notch` RFVideo dual-notch support at the upstream
  `fm_audio_channel_0_freq`/`fm_audio_channel_1_freq` carriers, including
  flag-only Q=10 parsing and HI8's upstream default auto-enable behavior;
  enabled formats without both carrier parameters emit the exact v0.4.0
  disabled-filter warning
- VHS format-parameter fallback warnings and PAL/NTSC VHS-field-class fallback
  diagnostics are emitted with the exact v0.4.0 text and initialization order;
  all constructor-time warnings precede the Sys/RF DEBUG records in `.log`
- `PAL_M`, `NLINHA`, and `MESECAM` retain v0.4.0's VHS-only field-class
  restriction: other tape formats preserve earlier initialization diagnostics,
  then fail before creating TBC or JSON output artifacts
- VHS `--high_boost` RF residual boost path, using command-line overrides or
  upstream `boost_bpf_mult` defaults and applying the RF top-band boost during
  Hilbert demodulation; a zero-valued RF envelope skips the boost and emits
  v0.4.0's exact weak-signal warning once for that block
- VHS diff-demod spike repair path, honoring `--no_diff_demod` and replacing
  out-of-range FM demod spikes with upstream-style diffed-Hilbert windows;
  replacement candidates are snapshotted before mutation like Numba's
  `np.where`, preserving overlapping repair windows
- VHS dropout detection defaults now follow upstream effective-sample-rate
  selection, using the cxadc threshold only for native ~28 MHz decode paths
- Betamax/BETAMAX_HIFI automatic `fsc` notch behavior, using upstream SciPy
  Q=2 forward/backward BA filtering after NLD/sub-deemphasis on the main video
  path without applying it to the 0.5 MHz DOD branch
- VHS `--chroma_trap` luma comb trap path, resampling to 8*fsc, applying the
  upstream-style 4-sample delayed average, then resampling back before
  post-demod video filtering; CVBS retains the same reusable core path but its
  CLI preserves v0.4.0's missing-`logger` constructor failure
- VHS `--sharpness` VideoEQ path, using upstream `video_eq.loband` parameters
  to high-pass the demodulated video and add the scaled high-frequency band
  back before chroma trap and post-demod video filtering
- VHS `--nld` / `--non_linear_deemphasis` path, extracting the configured
  SciPy-shaped Butterworth high-pass or band-pass branch from the same
  post-demod video spectrum, clipping it to upstream
  `nonlinear_highpass_limit_*`, and subtracting it from the main video
- VHS `--sd` / `--sub_deemphasis` nonlinear sub-deemphasis path, including
  format-parameter auto-enable via `use_sub_deemphasis`, PocketFFT/SciPy Hilbert
  analytic-envelope alignment, amplitude low-pass smoothing, and
  logistic/static factor controls
- VHS `--y_comb` field-output comb filter, applying the upstream adjacent-line
  limited blend to resampled Hz lines before 16-bit TBC conversion
- VHS/CVBS/LD `--wow_interpolation_method` line-location interpolation mode,
  selecting linear, SciPy-compatible quadratic not-a-knot, or natural-cubic
  coordinate interpolation during TBC field resampling
- VHS/CVBS/LD `--wow_level_adjust_smoothing` field-resampler amplitude
  compensation from line-location wow factors, including median/MAD outlier
  rejection, recursive smoothing, negative spline-derivative behavior, and
  VHS's upstream half-frame default; resampled pixels use the exact v0.4.0
  embedded 65,537-phase, 16-tap Kaiser-windowed sinc table rather than a
  runtime-generated approximation or linear sample interpolation
- VHS `--ire0_adjust` field-output level correction, recalculating field
  black/blanking from the middle-third backporch medians and optionally
  rescaling Hz/IRE from the hsync-to-backporch difference; successful
  measurements emit the exact ordered `calculated ire0: %.02f` and
  `calculated hz_ire: %.02f` DEBUG diagnostics
- VHS `--track_phase` field-output IRE0 compensation, applying
  `track_ire0_offset[next_track_phase ^ field_number]` after burst phase lock,
  falling back to the CLI-seeded phase when chroma analysis is skipped, and
  matching upstream SECAM/MESECAM ignore behavior plus 0/1 validation

#### VHS chroma processing

- VHS chroma option model for colour-under write/no-write decisions,
  `--skip_chroma`, `--chroma_AFC`, Betamax PAL CAFC auto-enable,
  SECAM/MESECAM comb disabling, Video8/Hi8 chroma de-emphasis, chroma audio
  notch detection, and burst/phase/color-killer flags
- VHS color-under `demod_burst` branch now carries an upstream-style chroma
  burst band-pass through RF block and overlap-save stream decoding. The input
  block uses the same SciPy-shaped Butterworth SOS forward/backward filter as
  v0.4.0, then optional input-rate chroma-audio and `--notch` filters in upstream
  order, followed by `chroma_offset` roll, DC removal, and TBC-resampled
  per-field `ChromaBurstSamples` consumed by the upconversion/ACC/comb stage
- VHS chroma output helpers now cover upstream-style signed chroma to uint16
  mapping, chroma roll/DC removal, NTSC/PAL simple chroma comb, burst
  deemphasis, and automatic chroma gain primitives
- VHS chroma upconversion primitives now include upstream-style four-phase
  heterodyne table generation plus line-by-line normal and NTSC phase-compensated
  upconversion helpers
- VHS chroma burst phase primitives now cover upstream-style I/Q burst
  demodulation, line-scale phase offset, phase-rotation sequencing, rotation
  flip checks, burst-level averaging, and color-killer line detection
- VHS chroma burst probing now mirrors upstream padded burst extraction,
  float32-heterodyne/float64-chroma multiplication, float64 zero-phase burst
  filtering, and carrier-table I/Q demodulation with Numba's double-FMA
  reduction order
- VHS chroma field decode now wires TBC-resampled `demod_burst` samples through
  phase probing, upconversion, comb/ACC, uint16 conversion, and
  `TbcDecodedField.ChromaSamples` for sidecar writes
- burst-locked VHS line refinement preserves v0.4.0's
  `phase_sequence[max(9, burst_detected_line):]` list-index slice, including
  its nonzero field-line-offset behavior, before applying PAL odd/even or NTSC
  average phase corrections
- VHS chroma field sessions now receive chroma options from
  `DecodeSessionFactory`, and color-under fields apply the upstream-style final
  chroma band-pass before comb/ACC via the new generic IIR forward/backward
  filter path
- Video8/Hi8 chroma deemphasis now ports the upstream constant-Q peaking biquad
  and applies it in the chroma field path before comb/ACC
- VHS `--chroma_AFC` now keeps RF-block chroma raw for TBC and applies the
  upstream-style post-TBC chroma band-pass, optional chroma audio/video notches,
  roll, and DC removal before burst probing/upconversion
- VHS `--chroma_AFC` carrier tracking now estimates the TBC-domain
  color-under peak and phase, clips/fine-tunes it with upstream line-rate
  rules, and feeds the dynamic carrier into heterodyne generation; whole-field
  non-power-of-two transforms use a Bluestein FFT, and peak selection follows
  v0.4.0 by choosing the closest strict local maximum above one third of the
  global power peak rather than simply choosing the strongest bin
- post-TBC chroma filter design, carrier probing, and heterodyne generation use
  v0.4.0's `4 * fsc_mhz` rate instead of the inherited TBC `outfreq`; this
  preserves MESECAM's distinct 17.624 MHz chroma path while its field output
  remains at the PAL-family 17.734475 MHz rate
- PAL Betamax's mandatory chroma AFC path preserves the upstream float64 SOS
  prefilter after TBC, then reproduces the rolled float64 Numba fast-math mean
  reduction before carrier estimation and float32 heterodyne processing
- PAL/NTSC Video8 AFC now preserves the complete optional chroma-audio/user
  notch chain, keeps NTSC phase-compensated multiplication in float64 until the
  float32 `uphet` write, and reproduces Numba's distinct float64 2H PAL and 1H
  NTSC comb subtraction orders
- VHS chroma burst phase now feeds upstream-style burst-locked line-location
  refinement before field rendering, with `--disable_burst_hsync` preserving
  the sync-only line positions
- VHS chroma phase probing now applies the same zero-phase `FChromaFinal`
  filter to each padded burst window as v0.4.0 before I/Q demodulation, and
  NTSC fields apply the upstream post-burst amplitude doubling before
  heterodyne upconversion
- VHS `--track_phase 0|1` now seeds the first field's chroma rotation index as
  well as the track-dependent luma `ire0` adjustment, with each rendered field
  consuming the same detected/alternated next-track index that v0.4.0 stores
  before Hz-to-TBC conversion
- VHS `--export_raw_tbc` raw TBC path, switching the TBC video source to
  demodulated RF, writing resampled fields as little-endian float32 samples,
  and emitting raw-Hz JSON video level metadata
- VHS `--field_order_confidence` and `--field_order_action` field-cadence
  handling, including TYPEC's upstream forced `none` action, disabled
  progressive flip, duplicate/drop compensation in TBC field writes, and JSON
  metadata; progressive correction forces `syncConf: 10` even when the raw
  field confidence is lower, while ordinary fields retain that raw lower value;
  progressive/manual flips and duplicate/drop repairs emit the exact v0.4.0
  error diagnostics, including TYPEC's intentional silent manual correction
- SVHS custom luma filter support for embedded upstream response files plus
  high/low shelf entries

#### Decode engine and field rendering

- RF block decode pipeline that connects a sample loader, filter set, and FM
  demodulator for one block
- CVBS direct-luma block path that bypasses RF FM demodulation, preserves
  the upstream SciPy 1.18 DUCC real-FFT/inverse-FFT input round-trip, preserves
  auto-sync composite samples as luma, and applies upstream-style
  `--no_auto_sync` raw-sample mapping before existing video/TBC processing
- CVBS `--clamp_agc` field-output clamp/gain path, including upstream blank
  and sync median windows, per-line blank ramp subtraction, AGC speed smoothing,
  `--agc_gain_factor`, and `--agc_set_gain`; measured sync/blank levels use the
  upstream speculative-field delay rather than becoming visible one field too
  early, and that delay participates in transactional field-state restore
- VHS `--clamp` blanking DC offset compensation during sync level detection,
  while keeping upstream's default `--noclamp`/`--no_clamping` no-op behavior
- CVBS `.tbc.json` field metadata now follows the upstream LD-shaped path,
  including `diskLoc`, `medianBurstIRE`, `fieldPhaseID`, `vitsMetrics`, and
  `vbi.vbiData` while omitting unused `audioSamples`; PAL-M CVBS now uses
  upstream's fixed `fieldPhaseID` 0 fallback
- decode session factory that turns parsed VHS/CVBS/LD commands into upstream
  format parameters, native loaders, filter sets, and RF block pipelines
- decode execution option model for upstream thread counts, debug/profiler
  toggles, VHS debug-plot worker-thread disabling, CVBS/LD `--seek`, and LD
  lead-out/VITS runtime flags
- LD `--seek` now performs upstream-style VBI frame-number probing with up to
  three coarse-location retries before normal field decoding starts
- CVBS `--seek` now follows upstream's current behavior by failing with
  `ERROR: Seeking failed`, since CVBS frame-number decoding returns no target
- VHS `--params_file` JSON overrides for existing `sys_params`/`rf_params`
  keys, including upstream-style decoder level key synchronization from system
  parameters into RF decoder parameters; unknown keys emit the exact v0.4.0
  INFO diagnostics, while changed dictionaries use Python `repr` in DEBUG log
  records and retain the original per-group construction order
- upstream-style decode run bounds for `--start`, `--start_fileloc`, and
  `--length`, converting frame requests into field/sample positions with
  v0.4.0's exact `int(sampleRate / (FPS * 2)) + 1` coarse field length for
  VHS/CVBS/LD sessions
- decode output preflight checks for VHS/CVBS existing `.tbc`, `_chroma.tbc`,
  `.log`, and `.tbc.json` outputs unless `--overwrite` is supplied, plus LD
  `--write-test-ldf` input/output self-overwrite protection
- the v0.4.0 `--orc` preflight quirk is retained: output is `.tbcy`/`.tbcc`, but
  conflict detection still checks only the legacy `.tbc`/`_chroma.tbc` names
- VHS/CVBS output preflight also verifies that the output directory is writable
  before decode startup, matching upstream's early output-file check
- decode preflight now reports missing input files before constructing a decode
  session
- LD `--write-test-ldf` bug-report capture export, reading from the actual
  decoded/seeked field start plus upstream-compatible 1,100,000-sample
  lookahead and writing an Ogg/FLAC `.ldf` through FFmpeg
- sequential overlap-save RF block stream decoder with upstream-style
  blockcut/blockcut_end trimming and stitching
- sync analysis foundation for raw pulse detection, HSYNC/EQ/VSYNC
  classification, mean line-length estimation, and line-location gap filling
- TBC line resampling foundation that maps detected line locations into fixed
  output-width lines
- upstream-compatible Hz/IRE to 16-bit TBC sample conversion with PAL/NTSC
  output scaling and round/clip behavior
- TBC frame specification and field renderer that combine line resampling,
  output conversion, and explicit little-endian `.tbc` field bytes
- v0.4.0 field downscaling follows upstream's physical output-line origin:
  NTSC begins at line 1, while PAL first/second fields begin at line 3/4;
  sinc scaling includes the skipped-line wow prefix, uses nominal input line
  length normalization, and quantizes through float32 like `scale_field`
- the nominal input line length used by wow-level compensation is the
  ties-to-even rounded format line period at the active decode sample rate;
  NTSC VHS fields also receive v0.4.0's unconditional 117.25-degree FSC line
  shift after burst refinement, including when no per-line correction exists
- decode sessions now expose the target TBC shape and renderer derived from
  upstream format parameters
- VHS `.tbc.json` video-level metadata honors `--level_adjust` (default `0.1`)
  and applies the resulting upstream-style black/white 16-bit IRE headroom;
  CVBS retains its hard-coded but unused `0.2` session value while its inherited
  LD metadata path leaves the levels unadjusted, and VHS includes
  `videoParameters.tapeFormat`
- initial decoded-span to TBC field pipeline that detects sync pulses, estimates
  line locations, fills missing line positions, and renders 16-bit field samples
- multi-field TBC decode engine that repeatedly reads field-sized decoded spans,
  advances with v0.4.0 `nextfieldoffset` semantics (next vblank EQ1 start minus
  eight nominal lines, or `linelocs[outlinecount - 7]` as fallback), and writes appended `.tbc`
  fields plus upstream-shaped `.tbc.json` field metadata including `diskLoc`
  `fieldPhaseID`, `burstStartLine`, and VHS `vitsMetrics` `wSNR`/`bPSNR`
  computed from upstream white/black TBC windows
- VHS/CVBS/LD field reads now use v0.4.0's inherited 350-line NTSC or 400-line
  PAL request formulas plus two cache blocks; VHS 819-line decoding uses its
  500-line override, and all three decode paths apply the upstream leading cut,
  block alignment, and inclusive effective-stride span
- invalid fields now preserve v0.4.0's recoverable decode advances instead of
  terminating the sequence: VHS no-pulse spans jump 100 ms, LD/CVBS initial
  no-pulse spans jump one second, missing first HSYNC advances 100 tape lines or
  200 LD/CVBS lines, CVBS no-pulse spans after output advance 200 lines and
  continue, and short trailing data backs up from line0 by 20 lines; after a
  valid VHS field, short spans also emit the exact two upstream INFO records
  with Python-formatted line counts and timing values
- VHS chroma sidecar output contract for decoded chroma fields, writing
  upstream-named `_chroma.tbc` files by default and `.tbcy`/`.tbcc` pairs when
  `--orc` is supplied
- field-order planner for upstream-style repeated-field handling, including
  `detect`, `duplicate`, `drop`, and `none` actions plus JSON
  `isDuplicateField`/`decodeFaults` metadata
- LD repeated-field handling now follows upstream's LD-specific metadata
  policy: close repeated parity flips the current field with fault bit 1, while
  wider skipped-field gaps insert the last opposite field before the current one
- LD filler lookup stays keyed to the raw detected parity after a close-parity
  correction, and a wider-gap skipped field retains v0.4.0's early-return
  metadata shape: no `decodeFaults`, `vitsMetrics`, or `vbi` JSON keys and no
  corresponding SQLite VITS/VBI rows, with `decode_faults` left `NULL`
- LD/CVBS/VHS filler writes reuse the previously serialized field metadata
  verbatim, while LD refreshes only the repeated PCM/EFM counts; this prevents
  filler insertion from inventing phase faults or advancing VITS/VBI state
- CVBS field-order repair follows its inherited LD state machine rather than
  VHS `detect/drop`: close repeated parity is corrected and written with fault
  bit 1, wider gaps insert the cached opposite raw parity, and the current
  skipped field uses the same sparse metadata shape
- sync-derived first/second-field parity detection from upstream-style vblank
  boundary consensus, falling back to VSYNC boundary gaps when only sparse
  pulses are available
- dropout detection now follows three distinct v0.4.0 paths: VHS/tape formats
  use only the RF envelope, LD uses only its demod/RFHPF error map, and CVBS
  keeps `doDOD=False` and suppresses `dropOuts` metadata
- VHS/tape DOD uses the NumPy-compatible float32 pairwise whole-read envelope
  mean and float32 percentage multiplication, or the supplied absolute
  threshold; it keeps the parity-specific `lineoffset + 1` field bounds and
  preserves v0.4.0's actual hysteresis/merge behavior plus its zero-width
  next-line boundary records
- LD DOD combines PAL/NTSC IRE validity windows, HSYNC/expected VSYNC minimums,
  raw demod excursions above Nyquist, and RFHPF excursions above
  `3 * std(rfhpf)` using Numba's float32 mean/squared deltas, float64 sum and
  result, then NumPy's float32 scalar-comparison boundary and NaN semantics;
  demod/0.5MHz validity bounds retain their upstream float32 arrays and active
  AGC levels, while field limits use Python integer truncation; dynamic range
  extension, last-range padding, ties-to-even endpoints, multi-line splitting,
  and JSON `dropOuts` coordinates are preserved separately
- block and stream decoding now carry a `demod_05`-style 0.5MHz video low-pass
  branch built from the upstream 65-tap FIR shape with the 32-sample output
  offset applied; DOD uses its upstream `-30..115 IRE` / sync-area validity
  window
- block and stream decoding also carry the RFHPF branch required by LD dropout
  detection
- RFHPF DOD now carries a `video_rot`-style offset estimated from a fake FM
  signal and zero-crossing probe, and applies that offset before mapping RFHPF
  anomalies into demod/TBC coordinates
- CLI runner initializes native raw decode sessions, performs multi-field
  `.tbc` output, writes `outbase.log` diagnostics, returns `0` on success and
  the conventional runtime failure code `1` without the former .NET-port
  disclaimer; argument errors remain exit code `2`
- release identity now matches v0.4.0's generated `vhsdecode._version` metadata
  as `vhs_decode:g4315520`; LD `--version`, JSON/SQLite `version` and git fields,
  and `outbase.log` all use that same upstream-compatible value and logging prefix

#### Signal processing, sync, and recovery parity

- SOS direct-form filtering plus forward/backward zero-phase filtering;
  high-order Butterworth band-pass design now follows SciPy's ZPK transform,
  NumPy/OpenBLAS complex-product rounding, and `np.poly` convolution order,
  including bit-exact NTSC VHS order-8 BA coefficients
- ports of upstream zero-crossing, pulse detection, Hilbert multiplier, and
  super-Gaussian envelope helpers
- ports of fallback v-sync location means and crude sync/blank level detection
- VHS sync threshold selection now estimates sync/blank levels from the 0.5 MHz
  branch when available, and honors upstream-style `--level_detect_divisor`
  bounds/capping and exact correction warnings before using
  `(sync + blank) / 2` for pulse detection;
  `--use_saved_levels` reuses the previous field's detected sync/blank levels
  when available, retries fresh level detection if the saved levels fail to
  produce usable sync/line locations, and proactively reruns full detection on
  the next field when the current field has at least 30 line-location errors;
  the cross-field decision is preserved by speculative/retry state snapshots
- VHS pulse classification now uses the wider v0.4.0 HSYNC +/-0.7 us and
  equalizing +/-0.9 us tolerances; LD/CVBS retain +/-0.5 us
- CVBS automatic sync now derives per-field sync/blank levels from the 0.5 MHz
  branch, updates `ire0`/`hz_ire`, and performs v0.4.0's second pulse-threshold
  pass from measured VSYNC and neighboring equalizing-pulse black levels;
  `--no_auto_sync` retains the static input conversion path
- CVBS block demodulation now preserves v0.4.0's direct-luma path rather than
  passing video through the LD/VHS `FVideo` response; its 65-tap `demod_05` and
  81-tap FSC +/- 0.2 MHz `demod_burst` branches are independent FIR outputs,
  including the upstream float32 quantization of burst samples; the DUCC real
  transforms and NumPy SIMD complex multiplication are double-bit exact for
  `demod` and `demod_05`, while deterministic full-block float32 hashes match
  all three upstream channels
- CVBS NTSC retains the inherited LD burst line-location refiner because the
  v0.4.0 class defines `_refine_linelocs_burst` instead of overriding
  `refine_linelocs_burst`; PAL CVBS has no corresponding burst refiner
- CVBS clamp AGC preserves per-line blank/sync ordering for piecewise DC
  correction and carries the previous field's final-third median levels into
  the next auto-sync pass as v0.4.0 `agc_blank_level`/`agc_sync_level` state;
  its median, in-place DC correction, gain division, and VSYNC subtraction
  retain NumPy's float32 staging before the final float64 output scaling
- CVBS clamp AGC tracks separate raw detected and smoothed used gain extrema;
  successful CLI runs print the four v0.4.0 statistics lines to stderr and the
  final `saving JSON and exiting` line to stdout (fixed `--agc_set_gain` does
  not create automatic-gain statistics)
- VHS long-pulse recovery now rechecks pulses between `hsync_max` and three
  times that length at a threshold 10 IRE below the merged pulse's first sample,
  recovering HSYNC pulses whose back porch was included by the first pass
- vblank group filtering preserves the separate v0.4.0 minimum spans: VHS uses
  the relaxed greater-than-9 threshold while LD/CVBS require greater than 12
- PAL CVBS bad-line repair preserves the release override that disables
  second-derivative error marking, while PAL LD still recomputes that mask after
  pilot refinement
- VHS/CVBS HSYNC line-location refinement now runs against the 0.5 MHz
  low-pass branch when available, including upstream-style sync/porch midpoint
  recrossing and sample-rate-scaled right-edge correction; `--skip_hsync_refine`
  disables it, and right-edge refinement is controlled by VHS
  `--disable_right_hsync` / CVBS `--right_hand_hsync`
- normal field `line0` selection now ignores the preserved pre-vblank read
  context, refines raw pulses through the upstream
  HSYNC-to-EQ1-to-VSYNC-to-EQ2-to-HSYNC state order with `numPulses`-scaled
  earliest transitions, and uses the upstream pairwise half-line distance
  consensus when enough markers survive; next-field offsets use the following
  EQ1 start and retain the upstream eight-line read lead-in
- when both the opening and following vblank groups are present, line0 now uses
  the v0.4.0 two-group consensus: six internal distances per group plus sixteen
  cross-group distances, accepted only when the two first-HSYNC estimates agree
  within 0.5H; source priority then matches upstream combined/fallback/single/
  previous-field ordering
- refined sync pulses now map to field lines with the v0.4.0 Cython nearest-fit
  walk: each pulse is consumed at most once within `meanLineLen / 1.5`, while
  lines without a nearby pulse retain their reference-grid estimate
- refined-pulse mean line length and parity use the same longest contiguous
  HSYNC run and boundary confidence ordering as v0.4.0, including its run-count
  edge semantics rather than averaging every detected pulse spacing
- vblank refinement retains accepted partial state transitions while scanning
  toward a complete HSYNC/EQ1/VSYNC/EQ2/HSYNC group, including direct partial
  transitions allowed by the Cython state machine instead of discarding the
  whole candidate at the first missing pulse class
- typed VHS/CVBS/LD decode fields now require a vblank, explicit fallback, or
  previous-field anchor for line0; an otherwise valid run of ordinary HSYNC
  pulses no longer becomes a silently misaligned field
- VHS `--fallback_vsync` now retains v0.4.0's previous first-HSYNC location and
  read offset, derives its approximate search center from
  `frame_lines / 2 - 8`, and snaps a damaged/missing line0 to a pulse within
  0.7H; `--relaxed_line0` forces that prediction when no nearby pulse is found
- fallback VSync is implicitly enabled for TYPEC, EIAJ, 405-line, and 819-line
  VHS decoding exactly as in v0.4.0, without requiring `--fallback_vsync`
- `--fallback_vsync` also ports the v0.4.0 first-field recovery path that needs
  no previous-field state: close duplicate-pulse filtering, four ordered
  HSYNC/EQ/VSYNC boundary patterns, 0.08H candidate validation, out-of-range
  backup selection, 0.7H prediction snapping, and the long-VSYNC 240p/288p
  fallback are shared by VHS and CVBS decoding; all five line0 backup,
  out-of-range, whole-block prediction, and long-pulse guess INFO diagnostics
  retain the exact upstream text and trigger branch
- ambiguous HSYNC-to-EQ fallback boundaries use NumPy-compatible float64
  pairwise means and standard deviations for their three content intervals, so
  a value just outside the 5% decision boundary is not falsely accepted as
  line0 by sequential-sum rounding
- when a later field has no usable vblank group, normal decoding now predicts
  first HSYNC from the previous relative first-HSYNC/readloc pair without
  requiring `--fallback_vsync`; NTSC uses the previous field length while PAL
  preserves v0.4.0's intentional current-field-length rule
- fallback and history anchors both pass through libc-style integer rounding
  and the upstream line-grid correction; only valid HSYNC pulses at or after
  the first post-vblank HSYNC line participate, including when readloc repeats
  or moves backwards during recovery
- cross-field prediction now also retains upstream `prev_hsync_diff`: a real
  sync location within +/-0.5H of its prediction records the residual timing
  drift, and the next history-only field reapplies it without allowing an
  estimated field to feed the error back into itself
- history-only first-HSYNC predictions preserve v0.4.0's two libc
  `round()` steps, including away-from-zero half-sample behavior before line0
  is reconstructed
- if a history-only estimate places first HSYNC at or before sample zero and
  the new span contains only EQ/VSYNC pulses, decoding now mirrors v0.4.0 by
  anchoring first HSYNC to the first valid pulse and retaining the resulting
  negative line0 instead of dropping the field; the sinc resampler uses its
  existing endpoint extension for source coordinates outside the read span
- FFmpeg-backed container/resampling loaders now distinguish normal EOF from
  process failure after stdout closes, surfacing non-zero ffmpeg exits with
  captured stderr instead of silently treating them as short reads
- VHS `--gnrc` / `--gnuradio_rf_afe` now implements the v0.4.0 bidirectional
  GNU Radio bridge: a REP raw-RF source on port 5555, an automatically selected
  REQ processed-RF sink on ports 5555..6666, little-endian float32 payloads,
  ASCII `0` pull requests, and multi-message receive assembly before RF demod
- VHS color-burst processing now uses the decoder's raw floor/ceil microsecond
  window before its -5/+10 padding; it no longer reuses the separately rounded
  `-1.4`-pixel JSON metadata coordinates, which shifted burst phase and ACC
- chroma AFC carrier measurement now runs through the v0.4.0 paired
  forward-backward Butterworth high/low-pass probe (`3 dB` pass, `30 dB` stop,
  `24 * fH` transition), searches the complete positive FFT for relative
  extrema, chooses the peak nearest the nominal carrier, and only then applies
  the `+/-2 fH` PLL limit
- VHS burst phase/track analysis now runs once on the raw TBC chroma before
  AFC output filtering, is reused for burst-based line correction and final
  chroma, and keeps the previous field's AFC carrier/phase for the next lock;
  this removes two redundant full chroma filter/FFT passes per field
- chroma heterodyne/FSC tables, mixed `uphet` samples, comb arithmetic, and
  final SOS filtering now retain the upstream float32 boundaries through to
  ACC, including `sci-rs` float32 state/coefficient arithmetic
- final chroma filtering now retains SciPy's four-section Butterworth SOS shape;
  the full upconverted field uses upstream `sci-rs` float32 filtering, while
  burst probing preserves the float64 TBC chroma path, upstream MHz cutoff
  arithmetic, double-FMA I/Q reduction, and platform C-runtime `hypot`
- RF color-under extraction likewise uses float32 `sci-rs` SOS filtering and
  reproduces Numba fastmath's vectorized float32 DC-mean reduction; when an
  optional SciPy BA audio/video notch promotes the path to double, float64 DC
  removal retains the corresponding Numba fast-math reduction order
- automatic chroma gain now mirrors `lddecode.utils.rms`: burst amplitude is
  the standard deviation after removing the burst window's DC mean, not the
  uncentered root-mean-square value
- VHS metadata now preserves v0.4.0's PAL-field workaround: PAL-parent tape
  formats emit `fieldPhaseID: 1` on every field, while PAL-M/NLINHA retain the
  NTSC field-class four-phase fallback; the NTSC fallback uses the originally
  detected parity even when progressive handling repairs `isFirstField`
- VHS JSON and SQLite capture metadata now use the inherited parent system like
  v0.4.0, so CLI-reachable MESECAM, 405-line, and 819-line captures identify as
  `PAL` while PAL-M/NLINHA preserve their existing `PAL-M` behavior
- JSON `osInfo` now follows Python `platform`'s `system:release:version` shape
  (for example `Windows:11:10.0.26220`) instead of .NET's distinguishable OS
  description and `VersionString`
- CVBS fields now decode the inherited three-line Philips VBI payload and
  burst median for JSON/SQLite metadata, while LD-only line repair, player-skip,
  lead-out, EFM, and audio behavior remains disabled
- LD/CVBS burst-median slices now use Python `lineslice` boundaries: positive
  starts truncate with `int()` and stops include the upstream `+1` sample,
  instead of rounding both endpoints and shortening fractional windows
- Philips VBI zero-crossing search now truncates fractional starts and counts
  exactly like `calczc_do`; its regression fixture runs at the real 40 MHz rate
  so the upstream 0.2-us follow-up window remains eight samples rather than zero
- LD AGC/VITS slicing now distinguishes upstream pre-TBC and post-TBC rules:
  pre-TBC starts scale with neighboring wow-adjusted line length and include
  `+1` at the stop, while output slices round the two unmodified endpoints
- LD internally computes the four-decimal `blackToWhiteRFRatio` even without
  `--verboseVITS`; the scalar is retained for automatic MTF without keeping
  every decoded field's full raw RF arrays in memory; its white-slice gate now
  shares the VITS path's `uint16` subtraction wrap and NumPy pairwise float64
  mean, including the exact 90-IRE acceptance boundary
- verbose LD VITS metadata keeps v0.4.0's raw-then-round calculation order,
  including the unrounded RF-level ratio, upstream metric insertion order,
  and the rule that `ntscLine19Burst0IRE` exists only when the 3D line-19
  colour measurement succeeds; white and white-flag gates use NumPy pairwise
  float64 means, while `blackLinePostTBCIRE` averages quantized output codes
  before performing the single scalar IRE conversion
- quantized LD VITS IRE conversion preserves the upstream dtype split: direct
  grey-level arrays retain `uint16` subtraction wrap and Numba's sequential
  mean, PAL `palVITSBurst50Level` and NTSC `ntscLine19Burst0IRE` retain
  sequential Numba RMS, and PSNR/RF-level `np.std` paths remain pairwise
- NTSC line-19 colour statistics preserve the release's literal 40..100
  `uint16` gate (including its rejection of ordinary scaled 70-IRE codes),
  float32 comb arithmetic, zeroed two-sample edges, IQ phase mapping, and
  110..230 statistics window; I/Q and magnitude reductions use NumPy's
  pairwise float32 order, while phase conversion and SNR retain float32
  `atan2`/`log10` arithmetic through their final stored values
- LD MTF and AGC adjustments share a one-retry field transaction: sync history,
  parity, chroma AFC, analog-audio timing, burst phase, and player-skip state
  roll back before the same input location is demodulated again, while the new
  MTF response and valid AGC calibration remain active; AGC uses the upstream
  0.5-IRE first-field and 2-IRE later-field thresholds
- PAL LD/CVBS burst medians now add the parity-specific physical line offset
  (`+2` first field, `+3` second field) just like `Field.lineslice`, instead of
  sampling the earlier logical line number directly
- LD PAL/NTSC burst-level and burst-median measurements now retain Numba's
  sequential float32 mean, centering, square, and RMS reductions; PAL's
  positive 30-IRE rejection gate compares the float32-centered peak, while
  burst phase detection shares the same reduction helpers
- NumPy float64 and Numba float32 median paths now propagate input NaNs instead
  of sorting them away, including the release's distinct signed quiet-NaN
  results for empty reductions; LD AGC/burst aggregation and the shared
  CVBS/VHS sync, TBC AGC, serration, and wow-correction paths use these helpers
- LD `computedelays` fake-signal generation now mirrors the upstream
  LPF/burst/pre-emphasis probe before estimating `video_white`, `video_sync`,
  and RF high-pass rotation offsets
- the delay probe also follows LD's clipped-demod `FVideo` path and measures
  zero crossings after the upstream float32 `demod` quantization
- all three LD delays are derived from one shared fake-RF decode containing the
  upstream five-sample zero gap; VHS/CVBS retain their v0.4.0 zero-delay
  overrides and avoid this LD-only startup FFT work
- LD `video_white` and `video_sync` delay estimates retain the fractional
  zero-crossing location from v0.4.0; RF-TBC cubic scaling uses the sub-sample
  white delay directly, while VITS raw slices apply Python-style integer
  truncation only at the call sites that do so upstream
- LD RF high-pass overlap-save blocks apply the upstream
  `blockcut - video_rot` crop before the later DOD error-map shift; serial and
  parallel block decoding preserve the same cross-block alignment
- LD sync detection now uses the 0.5 MHz branch for both pulse passes, rejects
  fields without a greater-than-10-us VSYNC pulse, recalibrates the first
  undecoded field's `ire0` from NumPy-style 15th-percentile video when the
  initial pass is empty, and applies the LD-specific 0.75..2.5 us neighboring
  equalizing-pulse window when rebuilding the threshold; that initial probe
  and the 50/85th-percentile AGC white slices retain NumPy's float64 virtual
  indexes, float32 weak-scalar interpolation, right-weight rounding, and NaN
  propagation, while each line's sync/blank calibration retains Numba's
  float32 median and even-midpoint rounding
- LD NTSC burst-refined line locations now apply the final upstream 117.25
  degree FSC phase shift, including fields where no per-line burst adjustment
  could be measured
- LD fields now carry upstream-style `linebad` state from two passes of the
  greater-than-4-sample second-derivative test; PAL pilot and NTSC burst paths
  linearly repair marked runs between surrounding good lines, with NTSC's
  line-zero anchor restriction and burst-missing lines preserved
- LD and CVBS now emit v0.4.0's exact possible-player-skip warning when field
  sync confidence is below 50 and normalized field length falls outside the
  inclusive `output_lines +/- 2` range, including NumPy NaN comparison behavior
- LD analog audio tick generation now uses the parity-specific field line
  count (NTSC 263/262 and PAL 312/313) instead of the maximum TBC field height,
  generates ticks by index like `numpy.arange`, and mutes an interval unless
  both demodulated channels cover its end sample; any such TBC failure emits
  v0.4.0's analog-audio muting warning once for the affected field
- 16 kHz-and-higher LD PCM now recomputes each field's fractional sample offset
  from the previous absolute RF read location and field number, including
  NumPy-compatible ties-to-even gap rounding; normally accepted fields advance
  that anchor even without current PCM, while skip/filler writes preserve the
  prior anchor. Lower-rate and HSYNC-locked modes retain the upstream
  zero-offset-per-field behavior
- right-edge HSYNC refinement now runs independently of the left edge and can
  clear a derivative-marked `linebad` entry; left and right validation use the
  distinct v0.4.0 IRE windows (`-65..110` and `-65..30`) and the right-derived
  location retains the sample-rate-scaled 2.25-at-40-MHz correction
- HSYNC dynamic level refinement now mirrors the Cython window arithmetic
  (whose `c_median` helper is actually an arithmetic mean), carries the last
  positive porch level across lines, uses it when overshoot exceeds 30 IRE,
  and retries a failed left-edge midpoint with that prior porch reference
- it also preserves v0.4.0's `c_max` initialization bug: the helper begins at
  `NaN`, making its nominal back-porch branch unreachable, so the first valid
  line uses the front porch and later lines reuse the previous positive porch
- LD `--preEFM` now remains nested under enabled digital EFM output like
  v0.4.0, so `--noEFM --preEFM` does not create an empty `.prefm` sidecar
- LD player-skip handling now mirrors the previous field's eight-line
  `skip_check` score, the 100-valid-pulse first-VBlank limit, shortened-field
  detection, and end-anchored HSYNC line-number repair after line 23; its
  full-line `nb_median` keeps float32 even-midpoint rounding before the
  VSync/blank IRE classifications
- LD line-location lookahead lengths now follow the parity-specific v0.4.0
  formulas: NTSC uses `outlinecount + 10`, while PAL includes its 2/3-line
  field offset plus three additional lookahead lines
- valid inputs that end before one complete field now follow the upstream
  zero-field path: TBC and enabled chroma/LD audio sidecars are empty, no final
  JSON is published, `.tbc.json.tmp` contains only `{`, and an enabled SQLite
  database contains its schema but no capture or field rows; VHS/LD report
  `Completed without handling any frames.` and exit successfully, while
  non-empty completion uses the exact upstream completion text

#### Runtime behavior and streaming output

- runtime reporting now mirrors v0.4.0 across LD, VHS, and CVBS: frame status
  text is logged at DEBUG while stdout receives the same 80-column padded
  carriage-return line, INFO-and-higher diagnostics go to stderr and first
  clear an active status line, and cleanup reports total decode time plus
  post-setup FPS; a one-frame PAL CVBS transcript is byte-exact on stdout, has
  the exact phase warning on stderr, and matches the upstream timing-line shape
- session-internal RF, sync, AGC, and audio diagnostics now use that same
  `.log` plus runtime-reporter route instead of being hidden from stderr
- LD, VHS, and CVBS streaming output now take the same periodic recovery JSON
  snapshots: every read while fewer than 100 fields have been written and again
  at each 500-field boundary; VHS then checks free output-disk space and, below
  10 GiB, emits the exact pause/resume messages and polls once per second until
  space returns, while disk-query errors are ignored and LD/CVBS never query it
- Ctrl+C now cancels LD, VHS, and CVBS field decoding plus VHS low-disk waits;
  after at least one committed field it finalizes partial TBC/JSON output
  without temporary metadata files, while pre-field cancellation preserves the
  zero-field JSON-dumper artifact; it then emits v0.4.0's exact termination
  line, reports cleanup timing, and exits with status 1
- unexpected LD, VHS, and CVBS field-read exceptions now emit v0.4.0's exact
  bug-report header, current sample, argparse `Namespace(...)` value spelling
  and ordering, and exception line on stderr, followed by a Python-shaped
  source traceback; default-versus-explicit numeric values, suppressed
  `noAGC`, quoted paths, and `params_file` handles retain Python repr behavior,
  while partial TBC/chroma/JSON/SQLite output is finalized before exit status 1
- `-t/--threads` now drives parallel RF block demodulation and filtering while
  keeping stream/FFmpeg/GNU Radio reads ordered; `-t 1` and debug-plot `0`
  retain the deterministic single-thread path, and parallel blocks are stitched
  in their original overlap-save order
- adjacent field reads now reuse a stream-scoped, 16-block decoded RF cache,
  avoiding duplicate FFT work across overlap windows while keeping memory
  bounded; backward seeks, input-stream changes, and dynamic LD MTF changes
  invalidate the cache before further decoding
- VHS RF production now uses one continuous ordered input producer with at
  most 32 lookahead slots and eight concurrent block decodes. Blocks publish
  independently, so the caller waits only for a requested block rather than
  draining the full lookahead batch. Stream changes, backward reads, cache
  invalidation, and disposal cancel and join the producer before direct input
  access, preserving the single-reader FFmpeg/GNU Radio contract
- a gate-controlled concurrency test blocks a later speculative input while a
  required earlier block completes; the next field returns before the gate is
  released, loader concurrency remains exactly one, diagnostics are still
  emitted only when ordered blocks are consumed, and 256-read stress tests keep
  decoded plus prefetched caches within their hard capacities
- on a 20-core .NET `11.0.100-preview.6.26359.118` fixture, a paired default
  chroma/resampling 40-frame PAL VHS probe at `--threads 20` improved from an
  11.60 s saved pre-continuous-pipeline median to 7.71 s, a 33.5% gain; average
  active cores increased from roughly 2.2-2.5 to 3.3-3.7, while TBC, JSON, and
  chroma SHA-256 remained byte-identical
- the current default-path 40/160/320-frame runs completed in
  7.65/26.58/52.51 s with 1.76/1.88/1.67 GiB peak working sets and
  1.42/1.30/1.28 GiB second-half medians; all 320 requested frames were written,
  showing a bounded working set rather than decode-length growth
- worker-enabled VHS field decode overlaps luma TBC rendering with chroma field
  decode, permits only one in-flight chroma task, then commits chroma state in
  field order before advancing. A 160-frame full-path A/B moved from 20.13 s to
  18.55 s (7.8%); TBC, chroma, and JSON SHA-256 remained byte-identical. The
  xUnit v3 field test also compares every serial and four-worker luma/chroma
  sample across two consecutive stateful fields
- little-endian TBC/chroma output now writes directly from `ushort` spans,
  eliminating about 455 MB of full-field temporary byte-array payload over a
  160-frame run; the big-endian fallback rents and returns one bounded buffer.
  The xUnit v3 allocation probe writes 400,000 samples with less than 1 KiB of
  thread-local allocation after warm-up. The fresh 160-frame outputs retained
  luma SHA-256
  `8AF14AEB2C40D65963DFBBC33947557CFB29154AD0011FF90DCD4C75EE12D6E5`
  and chroma SHA-256
  `3C53B4F22CB0E14BA3B86B4B39770E18A17C6E8BEE6A56E843A73AC27ECA3102`;
  the measured wall time remained within run-to-run noise
- a zero-copy `--length 320` longevity run reached fixture EOF after 204 frames
  in 24.26 s (8.54 FPS post-setup). Private-memory quarter medians were
  1.495/1.563/1.601/1.543 GiB with a 1.73 GiB peak, showing no monotonic
  late-run growth. Luma and chroma each contained 409 complete fields with no
  trailing bytes, and JSON contained the same 409 fields. SHA-256 values were
  luma `FCD83E68BDF6EFB3C2583349519B24E750155DE6B4C256D0B5F9CE4E76BE94E9`,
  chroma `B7CE0EF8768B7731FFAD9E7B8FE4162A24D0CC02620FC32F639A7ED078BF065B`,
  and JSON `EAACE43594DEC574360B664D932E23F10E1B428BA4268A91BA6A1858BFEDD4AD`
- the verified 40-frame output hashes are TBC
  `2F540BF1F9A132281A8D26C0EEADEBC7617A366E296EEB5FF69FF9346836CD05`, JSON
  `FCDCDEAA9D3BAD8949AAEFACBFDE2E8688A13568FF71836EBB37E758780CB67F`, and
  chroma `7811643DBFBEDC95E8401C1F8062B9D74630F7B15AE98DF1D2C0BD81DC5BE296`
- the managed DUCC FFT path now reuses per-worker scratch buffers, shares
  immutable root tables, writes packet transforms back in place, and performs
  discardable inverse transforms in place. On the deterministic PAL LD fixture
  with `--length 1 --threads 5 --disable_analog_audio --noEFM`, the three-run
  median fell from 1.307 s to 1.163 s; a four-field Core probe reduced managed
  allocation from 5.12 GiB to 1.96 GiB while preserving bit-exact output
- stride-aligned RF windows now return their assembled channel arrays without
  cloning every full field, and long TBC sinc resampling jobs share the decode
  worker budget while `--threads 0/1` retain the serial path; serial and
  parallel linear, quadratic, and cubic interpolation remain bit-exact
- linear TBC resampling now evaluates its constant derivative once per line,
  performs the same median/MAD outlier repair before expansion, and overlaps
  source-position generation with level preparation through a fixed two-way
  task; an isolated default-chroma probe improved from 9.46 to 7.88 s with all
  three output hashes unchanged
- VHS heterodyne/carrier tables use bounded parallel construction, and the
  phase-analysis heterodyne workspace is reused only when decode carrier and
  phase values still match; AFC changes rebuild the table. Dedicated xUnit v3
  tests compare serial/parallel table hashes and prepared/fallback decode output
- the managed real forward and inverse FFT radix-4 kernels use pinned pointer
  indexing while preserving arithmetic order. A 32768-point isolated inverse
  benchmark improved median throughput by about 6.5%; the forward median moved
  from 204.7 us to 195.9 us (4.3%) with the exact same hash. A 384-block PAL VHS
  composite was neutral at 841.96/841.19 ms, so no whole-block gain is claimed
- float32 SOS forward/backward filtering now rents its padded working array,
  processes only the exact requested span, and returns the rental synchronously.
  Matched 40-frame GC traces reduced sampled managed allocation from 16.772 to
  16.178 GiB and `Single[]` allocation from 651.68 to 47.25 MiB. Five interleaved
  full-path A/B runs were wall-time neutral at 5.541/5.537 s, while median CPU
  time moved from 20.000 to 19.438 s. TBC, chroma, and JSON hashes matched
  exactly. A fixture-limited 204-frame run completed in 23.39 s with
  1.147/0.886/0.888/0.917 GiB private-memory quarter medians and a 1.755 GiB
  peak; all 409-field outputs matched the previously recorded SHA-256 values
- the 16-tap TBC sinc kernel now pins its source and lookup table for pointer
  indexing while preserving clamp, FMA, float-conversion, and accumulation order;
  an isolated PAL-sized field median improved from 3.929 ms to 3.727 ms (5.1%),
  then an interior-window path removed redundant per-tap clamps while retaining
  the clamped path for edges and inputs shorter than 16 samples, improving its
  serial probe by another 1.6%. All three 40-frame hashes remained exact; a fresh
  160-frame run also matched TBC, JSON, and chroma hashes with 0.78/1.18/1.20/1.41
  GiB quarter private-memory medians and a 1.68 GiB peak
- VHS RF-envelope preparation now converts four doubles to float32, clears the
  sign bits, and writes the rotated float64 values through AVX while preserving
  the scalar tail and wrap path. A 32K-block isolated median improved from
  57.5 us to 13.3 us (76.9%); 40-frame median time moved from 7.55 s to 7.39 s,
  a 160-frame run moved from 26.95 s to 25.70 s, all three hashes remained exact,
  and quarter private-memory medians stayed bounded at 1.34/1.48/1.50/1.45 GiB
- VHS Rust-style FM unwrap now computes four independent atan approximations
  through AVX/SSE, then commits the phase differences in original sample order;
  non-finite groups and unsupported hardware retain the scalar path. The
  isolated 32K-block median moved from 610.1 us to 130.7 us (78.6%). In five
  interleaved 40-frame full-path pairs, median wall time moved from 7.43 s to
  7.41 s and median CPU time from 27.88 s to 26.36 s (5.5% less CPU), while TBC,
  JSON, and chroma hashes remained exact. A 160-frame run completed in 26.48 s
  with 1.45/1.47/1.40/1.23 GiB quarter private-memory medians and a 1.79 GiB peak
- Python's arbitrary-precision thread values now survive until their v0.4.0
  runtime use: VHS debug plots ignore even enormous positive or negative values,
  CVBS/LD negative values retain the nonzero request with zero demod workers for
  zero-field completion, active VHS negatives report `max_workers must be greater
  than 0`, and unrepresentably large active worker counts report `can't start new
  thread` after creating only the upstream empty log artifact. Differential
  zero-field VHS/CVBS/LD probes match output sets and sizes, and the LD SQLite and
  JSON temporary artifacts are byte-identical
- HiFi non-positive thread counts now retain v0.4.0's zero-worker queue state:
  `0` and `-1` consume their two or one idle input buffers before waiting,
  smaller values wait before reading, and an early EOF prints the finishing
  message but never reports success while preserving the empty audio artifact
- VHS `--cxadc` now emits v0.4.0's exact deprecation warning to stderr and as a
  timestamped `WARNING` record in `.log` before the Sys/RF debug records
- command help now uses the actual executable/script name and mirrors argparse's
  post-dispatch usage shape, so facade help no longer exposes the already-popped
  `vhs`/`cvbs`/`ld` subcommand
- LD `--write-test-ldf` now reports the upstream start/end range, short-read
  location, sample count, and success line after the completion message; the
  zero-field path also attempts the requested 1,100,000-sample lookahead file
- LD `--write-test-ldf` freezes its start offset immediately after rough/precise
  seek and uses the decoder's final offset after recovery skips for its end;
  `--length 0 --seek` still performs the upstream frame probe before exporting
- LD lead-out detection now scopes the two required `0x80EEEE` codes to one
  first/second-field pair, resets that scope even when a new first field has no
  decodable VBI code, preserves the ordered CAV/CLV early-return rules, and only
  stops after processing a paired second field, preventing sparse or later line
  codes from triggering termination across unrelated frames
- native `.wav` dispatch now follows v0.4.0's container-loader path alongside
  `.ldf`, `.flac`, `.vhs`, and `raw.oga`, retaining timed seek/rewind behavior
  and accepting WAV encodings beyond the low-level mono PCM16 helper
- VHS sync-level detection now includes v0.4.0's VBI serration path: decimated
  dual-direction vertical envelopes, line-harmonic power minima, original
  arbitrage and EQ-pulse duration gates, two-measurement moving levels, and
  upstream sanity checks; detected levels are then refined from long VSync
  pulse interiors and surrounding EQ-pulse back porches before falling back to
  the 30-step/5-IRE pulse-count search and finally the legacy level detector;
  `--fallback_vsync` enables the upstream abnormal-long-pulse candidate, while
  405/819-line systems retain the upstream serration bypass
- VSync envelope/minima work and harmonic power-ratio search now overlap over
  one shared read-only padded input; candidate arbitration, moving levels, and
  detector state updates remain ordered after both branches finish
- long VSync interiors and surrounding EQ back porches use NumPy-compatible
  float64 pairwise means during serration level refinement, preserving the
  calibrated sync and blank values instead of sequential-sum rounding
- serration half-amplitude splitting uses the upstream Numba float64 fast-math
  reduction order, preserving peak/valley classification at ULP boundaries
- CVBS and LaserDisc pulse recalibration uses NumPy-compatible float64 means,
  preserving the upstream 5-IRE acceptance boundary for long VSync pulses
- sequence decoding now commits each field as soon as field-order planning
  releases it: main/chroma TBC and LD EFM, pre-EFM, PCM, RF-TBC, and AC3
  sidecars stay open and preserve their cross-field state instead of retaining
  every decoded payload until EOF; VITS/3D metadata is calculated before the
  large RF/video/chroma/audio arrays are released, JSON fields stream through
  a temporary fragment, and SQLite rows are inserted incrementally, bounding
  sequence memory to the field-order/previous-field state
- active main/chroma TBC and raw LD PCM/EFM/pre-EFM handles use CPython's
  deny-none sharing on Windows, so a viewer can read them while decoding
  continues; recovery JSON snapshots remain complete, atomically published,
  and readable between field checkpoints
- recovery JSON checkpoints now run on a single background writer like v0.4.0:
  checkpoints are skipped while that writer is busy, each accepted snapshot is
  frozen at its current field boundary, and cleanup always queues and joins the
  final snapshot before SQLite and payload finalization continue
- SQLite capture/PCM rows are created lazily immediately before the first field
  row and committed first like v0.4.0; each accepted field then refreshes and
  commits `number_of_sequential_fields`, so recovery databases never retain the
  previous `0` count, and output-stage failures finalize earlier JSON/DB state
  while preserving the original error
- streaming output handles now open before the first field read like v0.4.0;
  VHS/CVBS `--write_db` creates the SQLite schema before main video and chroma,
  while LD creates main video and enabled PCM/EFM/RF/AC3 sidecars before its
  mandatory database, so startup failures retain exactly the earlier artifacts
  without starting JSON metadata
- output cleanup now publishes JSON and completes SQLite before closing payloads,
  then closes VHS chroma/main video or LD main video/PCM/EFM/RF/AC3 in v0.4.0
  order; first-field main TBC or chroma write failures retain the field already
  committed to JSON/SQLite even though `fields_written` has not advanced
- successful VHS/LD and CVBS completion notices now precede JSON/SQLite and
  payload finalization; LD test-LDF path/range lines are emitted before encoding,
  sample totals before the FFmpeg pipe closes, and the success line afterward
- LD streaming output now preserves v0.4.0's write order across failure points:
  pre-EFM/EFM precede JSON/SQLite metadata, main TBC follows metadata, and
  RF_TBC/AC3 plus analog PCM follow main TBC; a field with no analog payload
  records `audioSamples: 0` without failing, and disabled EFM records zero
  T-values even if stale counts are supplied; startup creates analog PCM before
  EFM/pre-EFM and then RF_TBC/AC3, retaining the same earlier empty artifacts
  when a later sidecar cannot be created

### Differential verification

#### VHS verification

- deterministic 32768-sample v0.4.0 block baselines cover all 357 valid
  system/format/speed combinations across PAL, PAL-M, NTSC, MESECAM, 405, 819,
  and NLINHA: all 1,428 `demod`, `demod_05`, `demod_burst`, and `envelope`
  channels are float32 byte-exact, including all 75 narrow internal burst
  references for non-color-under formats; SciPy SOS section ordering,
  real-input Hilbert transforms, and NumPy complex-magnitude rounding preserve
  the float64 source bits while those formats continue not to emit a chroma
  sidecar
- thirty independently discoverable deterministic full-field chroma baselines
  cover every explicitly routed PAL/NTSC color-under family: VHS, VHSHQ,
  S-VHS/S-VHS ET, U-matic variants, Betamax/HiFi/SuperBeta, Video8/Hi8, EIAJ,
  VCR/VCR-LP, and Video 2000, plus PAL-M, MESECAM, NLINHA, Betamax AFC, and
  PAL/NTSC Video8 AFC with chroma-audio plus user notches. All 9,150,099 output
  samples match v0.4.0 bit for bit, with staged hashes locking prefilters,
  notches, carrier estimates, heterodyne/phase compensation, final filters,
  deemphasis, comb, and automatic chroma gain
- complete deterministic 313-line PAL VHS burst-phase sequences with track
  detection both disabled and enabled match v0.4.0 bit for bit, including
  every per-line phase/I/Q/magnitude tuple, bottom-of-field lookahead and phase
  reuse, the next-track rotation index, burst detection, and
  odd/even/combined averages
- a two-field real NTSC VHS/FLAC fixture now matches the v0.4.0 checkout byte
  for byte: main TBC
  `60A6409696FD27F2012D9DF40DB97D141BE1F3D6315D3F6D4AD45A88B59FB1FF`,
  chroma TBC
  `D46FC4327DAE2D3389EEF456329EDF642C132D7872379D18BD2DA958521A9AC7`,
  and JSON
  `3A8B067383B6E3F9BCDDB77DC982F4ACC83BB4D9567FDEE161BC16029341737E`;
  NumPy-compatible `hypot`, SciPy linear-derivative ordering, Numba FMA wow
  smoothing, and float32 SOS/comb/ACC selection preserve the boundary bits
- that fixture's 7,271-byte `.log` also matches all 41 v0.4.0 records and raw
  line endings after replacing only the dynamic timestamps; the initial RF
  projection includes `hz_ire`, `ire0`, `track_ire0_offset`, and `vsync_ire`,
  sync-level failure reasons retain their upstream ordering, and VHS sequence
  completion performs the producer's final one-field lookahead before logging
  the completed file frame
- one-frame non-default NTSC VHS fixtures are also byte-exact for
  `--sharpness 20`, `--fm_audio_notch 10`, and the combined
  `--high_boost 1.3 --sharpness 20 --nld --sd` path; the stateful sharpness
  overlap is cached by RF block number so field retries and overlapping reads
  do not advance its leading-edge filter state twice
- the same fixture is byte-exact for `--notch 2.5 --notch_q 20`, `--y_comb 2`,
  `--no_comb`, `--disable_burst_hsync`, `--disable_phase_correction`, and both
  `--wow_interpolation_method quadratic` and `cubic`; y-comb retains NumPy's
  float32 ufunc boundaries, while the spline modes reproduce SciPy's knot
  construction, banded LU coefficients, de Boor evaluation, and full field
  lookahead
- the second non-default matrix is byte-exact for `--dctp`,
  `--skip_hsync_refine`, `--track_phase 1`,
  `--wow_level_adjust_smoothing 500`, `--dod_t_abs 5000`, `--noDOD`,
  `--ire0_adjust backporch`, and `--cafc`; the CAFC outputs are luma
  `07C32163B703C1EDD3F6FE6AA4F7BEF17A589175E16646FA5DC50D98E9851040`,
  chroma
  `75A63071BB180445FE159539618B9A6846866783DCF391B57A7AFCEF97C4B8FD`,
  and JSON
  `3A8B067383B6E3F9BCDDB77DC982F4ACC83BB4D9567FDEE161BC16029341737E`;
  this includes NumPy `rfftfreq` ordering, dynamic SciPy-compatible SOS
  coefficients, upstream's delayed AFC carrier used for burst locking,
  float32 roll/mean/subtract behavior, and Numba's four-sample output-cast
  boundary behavior
- the third NTSC VHS matrix is byte-exact for `--ct`, `--nodd`, `--clamp`,
  `--NTSCJ`, `-L`, `--ck`, `--skip_chroma`, `--drh`,
  `--level_detect_divisor`, `--fallback_vsync`, `--use_saved_levels`,
  `--export_raw_tbc`, `-D`, `--dod_h`, both HSYNC IRE-adjust combinations,
  both field-order controls, `--relaxed_line0`, `--debug`, the flag-only
  `--y_comb` and `--fm_audio_notch` defaults, and `--params_file`; raw TBC VITS
  measurements use the pre-quantization float32 payload, and unknown parameter
  file keys retain upstream's ignored-key behavior
- `--orc` produces byte-exact `.tbcy`, `.tbcc`, and JSON artifacts, while
  `--write_db` produces the byte-exact 69,632-byte v0.4.0 SQLite database
  `1E227CFBE8BD4CC62D04472F532995510469BC3D9F16DADF20B09A16A1314FF4`,
  including per-field commit sequencing, schema text, and SQLite header values

#### CVBS verification

- a deterministic two-field 40 MHz PAL CVBS capture is byte-exact for TBC
  `55D7A354F16DF188294B6F45D6047BF8FA35D07BAC559C420190DEDE1E2B5DC9`
  and JSON
  `71AA2B324AEE3D605BA64968C306BEE4E53E51D28AD8F058E4FBF3AA319A6E7F`;
  normalized logs also match the upstream 7-to-2 phase-sequence warning and
  frame status, while PAL vblank bad-line repair, pre-offset burst medians, and
  line-offset sync confidence reproduce the field internals that generate them
- the CVBS CLI preserves v0.4.0's constructor behavior: PAL and NTSC start, while
  accepted parser values PAL-M, MESECAM, 405, 819, and NLINHA fail with the
  upstream `Unknown video system!` tuple; their core parameter/filter baselines
  remain covered without exposing behavior the release executable did not have
- CVBS `--chroma_trap` preserves v0.4.0's
  `ChromaSepClass.__init__()` missing-`logger` failure, exits with status 1, and
  leaves only the empty `.log` created before construction
- on a deterministic varying-level PAL CVBS fixture, `--clamp_agc` now
  reproduces v0.4.0 byte for byte across all 710,510 TBC samples; the TBC and
  JSON SHA-256 hashes are respectively
  `96149002C71C87735AE9D609DCC5693F60A12AD361A09D2F26DCB3CBF9FAF624`
  and `783A0DAC238A72433523659AC73C6A2D11357684631071A0A8DEE206E323EBDC`,
  while phase, burst, VITS, location metadata, AGC statistics, and normalized
  logs also match
- fixed-gain CVBS with `--clamp_agc --agc_set_gain 1.25` now uses NumPy's
  float64 pairwise standard-deviation reduction for VITS metadata; TBC and JSON
  are byte-exact with respective SHA-256 hashes
  `6EDE21D03EF83CEB25231E57A79921023DD146B802BE081C2AA07F59E2DD302C`
  and `8139B8B89EB9516ACD0CA6B2502214A5DF0B2598527B8A76B132E6174BBBD60B`
- zero-field CVBS runs additionally preserve the
  `Unable to find any sync pulses, skipping one second` log entry alongside the
  shared LD/VHS/CVBS zero-field artifacts described above
- on the same varying-level fixture, default non-clamped CVBS with `--threads 0`
  now reproduces v0.4.0's synchronous speculative-field timing: each requested
  field is rendered with the next decoded field's `ire0`/`hz_ire`, the producer
  lookahead is not written past `--length`, all 710,510 TBC samples and the
  complete JSON are byte-exact with respective SHA-256 hashes
  `EA2060F1C50E450ECD68E41719F55060733BF1E0CF26DF1F784EF61E3513EF51`
  and `783A0DAC238A72433523659AC73C6A2D11357684631071A0A8DEE206E323EBDC`,
  and normalized logs match in order and content
- PAL CVBS `--start_fileloc` rough seeks now preserve v0.4.0's initial
  non-first-field drop, direct local-vblank line-zero anchor, previous-field
  projection, equal-distance pulse priority, and speculative `fields_written`
  numbering; at sample 768000 the TBC and JSON are byte-exact with respective
  SHA-256 hashes
  `A8F4B43D282FAE92DE2535AEF94215B23292AAA6A10643C553630EE633B3F66B`
  and `52688B18FC14F2BAA031A16C9BAA566B7BE8C089E62480BA8AABEFEFF841CF4E`,
  while normalized warnings and frame status also match
- worker-thread CVBS now reproduces v0.4.0's shared `ire0`/`hz_ire` timing as
  well: the next field starts before current-field resampling, the first
  producer handoff preserves Python's resampler warm-up window, later fields
  snapshot whichever shared levels are visible after resampling, and unused
  lookahead completes without being written past `--length`; two- and
  four-field runs are byte-exact for TBC and complete JSON, with the two-field
  TBC hash
  `995ACC39AE430A5B279D4E52750F8899C69B3B292A08E71BA5B6D507C298001A`
  and the same JSON hash
  `783A0DAC238A72433523659AC73C6A2D11357684631071A0A8DEE206E323EBDC`,
  while normalized logs also match in order and content
- multi-frame PAL CVBS now projects line zero from the exact final line of the
  previous valid field, matching v0.4.0's `prevfield` state instead of
  accumulating first-HSYNC approximation error; serial and worker four-field,
  serial six-field, `--skip_hsync_refine`, and combined AGC/notch/sharpness/wow
  runs are byte-exact, with default four-field TBC, JSON, and SQLite SHA-256
  hashes `9230E3BC7AECF8921A9AF1C6D2B045D73CC6768D5E01D2DBC70C0E6D4CDB5FCF`,
  `9138992DC2262BB0D1C8F01A21A0689B7FE003D0ABBCDD9F75681286B5731F45`,
  and `BEFE6A50C4962EC355698F91B59AAB999252619C300BCC9BB42D1C543EC57C33`
- PAL CVBS `--start_fileloc 900000` now rejects the partial leading field
  without committing its sync history, emits the exact upstream missing-data
  diagnostic, and resumes with byte-exact TBC, JSON, SQLite, and normalized
  logs; their hashes are
  `E18F221C706DE0460CF3FB4904C4129E9B591B28F8368857A3576968EFCC711A`,
  `8E49196977ABC45ACF283096E0CF6818BA66BC85411EBE1EDCDD9AF8D2604737`,
  and `2E5EFF842103DD2741AB6DC5796CBD04AA1C2A5EDCE7DAAD29D09F2D25643D56`
- a PAL CVBS valid-field/gap/valid-field sequence now discards v0.4.0's
  `prevfield` context after the missing-data recovery, retains CVBS float64
  demod precision for burst levels, renders a serial pending field with levels
  published by the failed lookahead, and reproduces the phase warning,
  `Skipped field`, filler fields, and frame-status ordering; serial and worker
  TBC hashes are respectively
  `0EAAFAE79BDD6B527F19C44C22D89B47307F14207191BF56E32FA9D844E81BFD`
  and `036186D8E0C4D8529452FC7DA7931BE40442D8BF24F79C1A34A08045A8F2E6F4`,
  their JSON hashes are
  `D6745F35F2A897328ABDB33352C39491049307181E33BB674D6F26545582A4B3`
  and `77BF4525761F33E080C9A77B987E4492CFB014858037ACB79B311D2D8310FE8C`,
  and both normalized logs match in order and content
- when the same PAL CVBS sequence contains a long region with no sync pulses,
  decoding after existing output now emits the v0.4.0 first-HSYNC and
  `skipping one field` diagnostics, advances by 200 nominal lines, and resumes
  at the later valid fields; serial and worker TBC hashes are respectively
  `AFD2CEE7A1904ACD7204DD06B7FC0B4D60842D80E7520CE7F1F191E04556EA9A`
  and `0A79CAE640BE95E70EC94222109FDA3052F434F5D7F394DF14FD89C5B205C93B`,
  their JSON hashes are
  `657E299D6C473FDEB8A5524BDEAB7442F8D56432948094713DFE0273F91F5F15`
  and `68E1CF7CE8FCE4E35560B7C90243E4EA672BFCDEBB697EB5C5EA0E9A577AC278`,
  and all 19 normalized log entries match in both modes

#### LaserDisc verification

- a deterministic one-frame 40 MHz PAL LD RF fixture now exercises the full
  pilot/burst/vblank, bad-line repair, eight-field phase, dropout, VITS,
  TBC/JSON/SQLite, and frame-status path; PAL pilot repair recomputes the final
  derivative-error mask like v0.4.0, restoring exact `syncConf` values 90/100
  and the exact second-field dropout coordinates; all 710,510 main TBC samples,
  the complete JSON, and SQLite now match v0.4.0 byte for byte with respective
  SHA-256 hashes
  `CEB246557CCEE237A1743D7D6D6CB456F8B9C434C2E32162BB53013B9BBE9E2B`,
  `36C054543634092C86DC1F1D21CDCBD88586A617E3D9872A86429C7258E179AE`,
  and `1BDF01C859AC0459BD7C1FB870E617B59BA002089A996A2C2160D27A5EA87550`,
  while normalized logs match in order and content
- on the same PAL fixture, default EFM and analog audio plus `--preEFM` now
  produce six byte-exact artifacts: main TBC
  `CEB246557CCEE237A1743D7D6D6CB456F8B9C434C2E32162BB53013B9BBE9E2B`,
  EFM
  `C339CC79B347DAAAB3AE91A96BEBA10E219CA88EB88A29FBAABA34AE9615BC28`,
  pre-EFM
  `4C6D6FA834D00E6F5252E56F00103CF88A15B9E6F1C8DF9817D298A12444FC43`,
  PCM
  `EF7D695A7C64035C468E2B7920463EBE5F8D96FE95CD899CB6886F1D42EFBE66`,
  JSON
  `7843C72518D0579736D070AB61B906C8B07B78ADCEC1F5930A82FE96D64EFF93`,
  and SQLite
  `F4F32D79CDEEBF99150C4D25FE4DE86D628C1677947DC3A3E17D78ED8A5519A6`
- PAL LD `--start_fileloc 768000` rough seeks now preserve v0.4.0's dropped
  initial second field, direct local and next-vblank anchors, previous-field
  end-line projection, speculative `fields_written` numbering, and pre-metadata
  AGC confidence. With analog audio and EFM disabled, TBC, JSON, and SQLite are
  byte-exact with respective SHA-256 hashes
  `73EF90BC04EB585ACA2448D276992FD94BCF3F1E98137E202D82D6CE9761249D`,
  `AEEFE8AB9874B5BE76740F34D6E60B0A27B7D8723674FD85B7FDD7D58E11CBEC`,
  and `450143AAB21235F964342ED1A4A799659927935821E6BB2670F7F9704CF033EE`;
  normalized logs also match the field-zero auto-level warning and frame status
- `--verboseVITS` on that PAL LD fixture retains the byte-exact main TBC and
  reproduces v0.4.0's centered burst RMS, including the exact second-field
  `palVITSBurst50Level` value `4.159`; the same centered calculation is used for
  NTSC `ntscLine19Burst0IRE`. Release 4.0's PAL verbose JSON dumper itself raises
  on NumPy `float32` metrics, so the differential used an analysis-only scalar
  adapter to expose the intended metric values while this port emits valid JSON
- a deterministic one-frame 40 MHz NTSC LD cadence fixture now follows
  v0.4.0's three-way local/next/previous line-zero median and its integer
  nominal-line-length burst timing, including the 16-sample zero-crossing scan
  limit and stop-on-miss behavior; field phases `3`/`4`, sync confidence,
  dropout coordinates, verbose VITS values, and the absence of a phase-sequence
  warning match upstream. All 478,660 main TBC samples, the complete JSON, and
  SQLite match v0.4.0 byte for byte with respective SHA-256 hashes
  `DF8E6DAA683029BADBA311083A88F3F65BCF29B2F0F2F3269A53B62EFE33922D`,
  `93B7123DE4322001E086961310EC9B1FDCB30DD173DD69A8224F615FFD7E7E4A`,
  and `B78F02B5CEA3577FCEEEA43C72CED2634968CFAD2D13DE18169C6384C2429A15`,
  while normalized logs match in order and content
- a one-frame real NTSC LD/LDF fixture with default EFM and analog audio also
  matches v0.4.0 byte for byte: main TBC
  `7F19286F84D563D58983C50326CE16433ED9DA90459ADA658532EB38A5AF686A`,
  JSON
  `BC03B954C7A031B0FD1CF93622DA2CA1AB1B17DF26B0D8B88D305123F7B4E95E`,
  EFM
  `666F71DB7CA1A6BE4B7181F83549E957AA78A5BCFD1B4BDB0E149908F55E4EEA`,
  and PCM
  `2E5CB3BFBD008213846433BAC078D07B1C5D79195965FBB3A4BB4C62EC152D41`

### Remaining compatibility work

These are bounded parity and verification gaps, not unimplemented top-level
decode commands.

#### Input and containers

- remaining rare container codec/timestamp edge cases outside the verified PCM
  WAV, IMA ADPCM WAV, native FLAC, Ogg/FLAC LDF, AAC, MP3, ALAC, Vorbis, and
  float WAV paths

#### HiFi

- remaining HiFi real-capture end-to-end output baselines; the command runner,
  Windows live preview, and GNU Radio path are wired

#### LaserDisc

- remaining real-capture PAL LD and AC3 end-to-end fixtures and verbose VITS
  edge cases beyond the deterministic PAL calibration

#### VHS and CVBS

- remaining non-default VHS/CVBS vblank edge cases, real-capture chroma
  track-phase transitions, and uncommon cross-option parity
- remaining rare real-capture first-HSYNC/vblank edge cases and complete
  upstream JSON/SQLite field metadata

#### Cross-cutting output parity

- remaining upstream TBC field-writer integration and bit-compat edge handling
- bit-compatible `.tbc`, `_chroma.tbc`, `.tbc.json`, `.log`, analog/AC3 audio,
  and optional test `.ldf` outputs across the remaining formats, options, and
  real-capture edge cases

## Build and test

```powershell
dotnet build VHSDecodeDotNet.slnx
dotnet test VHSDecodeDotNet.slnx --no-build
```

The current formal solution build completes with zero warnings and errors, and
the xUnit v3 project exposes 782 independently discoverable compatibility tests
to `dotnet test` and Visual Studio Test Explorer. On the
same Windows machine and fixtures, Release wall-clock measurements for one
frame were 2.346 s versus 7.193 s for NTSC VHS and 1.651 s versus 5.865 s for
NTSC LD (this port versus the v0.4.0 Python virtual environment); all output
hashes listed above remained identical.

`ffmpeg` and `ffprobe` must be available on `PATH` for FFmpeg-backed RF
container inputs; `ffmpeg` is also required for default HiFi FLAC output. HiFi
`.wav` and recognized raw input paths do not require either tool.

To regenerate the embedded format parameter snapshot from the checked-out
upstream source:

```powershell
python tools\generate_format_snapshot.py --upstream upstream-vhs-decode
```

## License

This derivative port is distributed under the GNU General Public License v3.0.
See [LICENSE](LICENSE). Adapted third-party components and their notices are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This project is an independent port and is not affiliated with or endorsed by
the upstream `vhs-decode` maintainers.
