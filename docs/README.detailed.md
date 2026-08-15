# vhs-decode-dotnet detailed reference

[Project overview](../README.md)

**[English](README.detailed.md)** | [简体中文](README.detailed.zh-CN.md) | [日本語](README.detailed.ja.md)

<!-- README_SYNC: 2026-08-13.01 -->

.NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), focused on
release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

> [!IMPORTANT]
> This is a work-in-progress compatibility port. The top-level decode paths are
> implemented and heavily tested, but the project does not yet claim
> byte-for-byte parity for every real capture and rare option interaction.

> [!NOTE]
> `--dsp-backend ipp-fast` is an experimental extension in this .NET port; it
> does not exist in upstream `oyvindln/vhs-decode` v0.4.0. On the two measured
> 400-frame VHS captures at `--threads 5`, it was about 5% faster end to end.
> NTSC-J luma, chroma, JSON, `fileLoc`, stdout, and normalized logs had zero
> differences. PAL JSON and all 800 `fileLoc` values also had zero differences,
> while 0.000794% of luma samples and 0.003226% of chroma samples differed
> (0.00201% combined); every changed sample was confined to one heavily damaged
> field out of 800. `exact` remains the default for compatibility-sensitive use.

## Contents

- [Scope](#scope)
- [Status](#status)
- [Compatibility coverage](#compatibility-coverage)
- [Upstream behavior profiles](#upstream-behavior-profiles)
- [Performance](#performance)
- [Build and test](#build-and-test)
- [Usage](#usage)
- [Outputs and live preview](#outputs-and-live-preview)
- [Verification](#verification)
- [Remaining work](#remaining-work)
- [Detailed evidence](#detailed-evidence)
- [License](#license)

<!-- SECTION: scope -->

## Scope

This port implements only the decode applications:

- `decode.py vhs`
- `decode.py cvbs`
- `decode.py ld`
- `decode.py hifi`
- standalone aliases equivalent to `vhs-decode`, `cvbs-decode`,
  `ld-decode`, and `hifi-decode`

The following are intentionally out of scope:

- TBC utility tools and unrelated helper applications
- the double-click user-operation GUI
- Matplotlib `--debug_plot` windows and line-profiler UI/report rendering
- filter-tuning UI that is not required by the decode pipeline

Decode-side options that reference those tools are still parsed where upstream
CLI compatibility requires it.

<!-- SECTION: status -->

## Status

| Area | Status | Current boundary |
| --- | --- | --- |
| Solution and tests | Implemented | .NET 11 `.slnx`; standard xUnit v3 tests work in Visual Studio Test Explorer and with `dotnet test`. |
| CLI and arguments | Implemented and snapshot-tested | Facade and standalone help, aliases, defaults, validation, diagnostics, and exit behavior target v0.4.0. |
| Upstream behavior profiles | Staged | `v0.4.0` remains the default; `current` is an opt-in profile with five merged PR 341 stages: VHS HSync detection, VSync level refinement, NTSC chroma group-delay correction, asymmetric Super-Gaussian final filtering, and the current color-under burst/ACC/CTI chain. |
| VHS and tape families | Implemented; rare capture gaps remain | VHS, S-VHS, Betamax, Video8/Hi8, U-matic, Type C, EIAJ, and supported PAL/NTSC variants share the release-compatible decode path. |
| CVBS | Implemented for release-supported systems | PAL and NTSC paths run; uncommon vblank and cross-option cases need more real-capture fixtures. |
| LaserDisc | Implemented; rare capture gaps remain | Video, VBI, EFM, analog audio, AC3, RF-TBC, metadata, recovery, and PAL/NTSC paths are connected. |
| HiFi | Implemented; more real-capture verification remains | Typed v0.4.0 CLI, bounded parallel decode, post-processing, WAV/FLAC output, preview, and GNU Radio mode are connected; a bounded four-second NTSC Betamax explicit-carrier gate matches Python WAV bytes and decoded FLAC PCM. |
| Inputs | Broadly implemented | Raw input, narrowly gated direct raw FLAC through bundled libsndfile, and common FFmpeg/PyAV-equivalent container paths are covered; rare codec and timestamp cases remain. |
| Outputs and recovery | Implemented; edge cases remain | Streaming TBC/audio output, JSON snapshots, SQLite, logs, disk-space handling, and recovery ordering are covered. |
| Interactive UI | Out of scope | Decode user UI and developer plotting/report windows are intentionally not implemented. |

“Implemented” means the runtime path exists and has focused compatibility tests.
It does not mean that every possible capture has already been proven identical.

<!-- SECTION: coverage -->

## Compatibility coverage

### Commands and parameters

- Traditional `Program.Main` entry point and `decode.py`-style dispatch.
- `decode.exe`, `vhs-decode.exe`, `cvbs-decode.exe`, `ld-decode.exe`,
  and `hifi-decode.exe` apphosts.
- Release 4.0 option names, aliases, defaults, positional arguments, help text,
  validation ordering, Python-style numeric behavior, and error formatting.
- VHS format catalogs and parameter files for the supported tape families and
  color systems.
- Standard stdin/stdout behavior and upstream-compatible file validation.

### Decode pipeline

- RF filtering, FM demodulation, sync and level detection, line-zero recovery,
  field parity, HSync refinement, TBC resampling, dropout detection, chroma,
  wow correction, AGC, and metadata generation.
- `--use_saved_levels` reuses prior sync levels, retries failed saved levels,
  and forces full detection on the next VHS field after at least 30
  line-location errors, matching v0.4.0 state behavior.
- VHS/S-VHS/Betamax/Video8/Hi8/U-matic/Type C/EIAJ routing and PAL, NTSC,
  PAL-M, PAL-N, MESECAM, NTSC-J, 405-line, and 819-line compatibility paths
  where supported by upstream release 4.0.
- LaserDisc VBI, CAV/CLV interpretation, analog audio, EFM/pre-EFM, AC3,
  automatic MTF, AGC, VITS, player-skip detection, and recovery state.
- HiFi carrier decode, dropout compensation, head-switch interpolation,
  normalization, preview, GNU Radio transport, and ordered output.

### Runtime and output behavior

- Exact or normalized upstream diagnostics for covered branches, including
  recovery offsets, field-order actions, parameter-file logging, and partial
  output finalization.
- Streaming `.tbc`, `_chroma.tbc`, JSON, SQLite, PCM, EFM, pre-EFM, RF-TBC,
  AC3, WAV, and FLAC paths where applicable.
- Periodic recovery JSON snapshots and upstream-style partial-file lifecycle.
- Active TBC, chroma, JSON, and raw audio sidecars remain concurrently readable
  while decode continues, so preview tools do not need to wait for completion.

<!-- SECTION: upstream-behavior -->

## Upstream behavior profiles

VHS decode accepts `--compat-version v0.4.0|current`:

| Profile | Default | Pinned upstream source | Active behavior |
| --- | --- | --- | --- |
| `v0.4.0` | Yes | release v0.4.0, commit `43155200da87c0d49eb37d8ec09b1372075ee8e4` | Existing release-compatible decode path. |
| `current` | No | merged PR 341, commit `2f21e8ed6018b14561396cc95f1f6828054470b8` | Exact-first VHS HSync candidate extraction, MAD rejection, multi-grid lock, calibrated levels, subpixel pulse synthesis, robust VSync level refinement, NTSC chroma group-delay correction, asymmetric zero-phase Super-Gaussian final filtering, fitted burst frequency/DC tracking, phase-compensated upconversion, current ACC/noise estimation, and four-pass CTI. |

`current` is deliberately staged and opt-in. For color-under formats it now
uses the official PR 341 CTI defaults, `--cti_mix 1` and `--cti_width 2`.
`--cti_mix 0` disables CTI. As in upstream, width zero does not disable it:
it retains the minimum four-sample sweep radius and a zero noise threshold.
The embedded baseline catalog records this stage explicitly.

The HSync, VSync-level, and group-delay stages are checked against the original
upstream functions with deterministic synthetic fixtures. The chroma stage
keeps burst and track-phase analysis unshifted, then adds the pinned float64
source-coordinate shift only to final 16-tap chroma resampling. Its tests cover
the Numba output bits, zero-shift legacy path, actual parallel threshold, and
the upstream all-positive phase-truthiness behavior. The Super-Gaussian stage
replaces the final color-under SOS filter. Its deterministic SciPy 1.18
oracles cover symmetric reflection padding, `next_fast_len`, float32
DUCC-compatible mixed-radix rFFT/irFFT, a float64 response, complex64
multiplication, and the production NTSC, PAL, PAL-M/NLINE, and MESECAM field
lengths. The current chroma stage adds the pinned four-parameter Gauss-Newton
burst fit, per-line frequency/DC phase compensation, MAD-clamped interpolated
gain, sync-tip noise estimation, and CTI. Component tests compare against the
PR 341 Numba output; BLAS-dependent fit intermediates use exact downstream
float32 gates plus tight float64 tolerances rather than claiming portable
float64 bit identity. HSync also retains its upstream PAL fixture gate. The
pinned VSync behavior intentionally includes its upstream back-porch assignment
quirk; a correction would require a separate profile change. Separate
end-to-end gates keep the default `v0.4.0` profile identical across
`--threads 0`, default, and `--threads 20`.

A natural-start 20-frame gate on a private local PAL VHS RF capture
also matches merged Python PR 341 and Exact `current` across all 40 fields:
luma/chroma bytes, ordered `fileLoc`, stdout, timing-normalized stderr,
754 timestamp-normalized log lines, and JSON after normalizing only source
identity. Exact `current` remains deterministic at `--threads 0`, default,
and `--threads 20` on that gate.

### 1,000-frame release gate

The same private local NTSC `.ldf` capture was decoded twice with the Exact
backend and the default 5 workers under both behavior profiles:

| Profile | Python oracle | This port, two runs | Peak working set | Result |
| --- | ---: | ---: | ---: | --- |
| default `v0.4.0` | 499.80 s | 95.37 / 93.17 s | 1.14 / 1.11 GiB | All six comparisons exact and deterministic |
| `current` | 459.34 s | 141.85 / 143.13 s | 923 / 942 MiB | All six comparisons exact and deterministic |

For each run, luma TBC, chroma TBC, JSON/`fileLoc`, and stdout SHA-256 matched;
stderr matched after removing only the elapsed-time line, and logs matched
after timestamp normalization. All runs completed 1,000 frames without an
interruption. The default oracle is upstream v0.4.0 `--threads 0`. The pinned
PR 341 `current` source reaches its own float-slice `TypeError` after 438
frames, so the remainder uses an otherwise source-identical oracle with only
six slice bounds converted to `int`. IPP was excluded from this release gate.

<!-- SECTION: performance -->

## Performance

Performance work is part of the implementation, while deterministic output and
release compatibility remain the first constraint.

The DSP backend is selected explicitly with
`--dsp-backend exact|ipp-fast`. This option is an experimental extension in
this .NET port and is not part of the upstream `oyvindln/vhs-decode` v0.4.0
CLI. `exact` is the default and retains the existing managed compatibility
path without probing or loading Intel IPP. `ipp-fast` is an opt-in Windows x64
backend; Intel CPUs are the officially supported target, while compatible
non-Intel x64 CPUs are a best-effort experimental path in this project. A
positive IPP non-Intel vendor warning is accepted only when the reported
feature mask includes SSE4.2. The backend loads the statically linked
`vhsdecode_ipp.dll`, reports the IPP version and selected ISA, and fails clearly
if the bridge, ABI, or CPU is unavailable. It never silently falls back to
`exact`. Current IPP routing covers the VHS real-RF FFT, the `current` profile's
color-under Super-Gaussian DFT, and the power-of-two double-precision
full-complex FFT stages used by LaserDisc video, EFM, and analog audio. The LD
route uses native bridge ABI v1.3. CVBS and HiFi reject `ipp-fast` as unsupported
instead of quietly benchmarking their Exact kernels.

`ipp-fast` is a numerically close performance mode, not a byte-compatibility
mode. Different FFT and vector-math evaluation can change floating-point bits
and may affect threshold decisions, metadata, recovery, logs, and output files.
Use `exact` whenever release-compatible hashes or behavior are required.

Five interleaved and reverse-order A/B pairs were measured for each real
capture after one warm-up pair, using 400 frames and `--threads 5`:

| Capture | Median end-to-end wall-time gain | Compatibility result |
| --- | ---: | --- |
| Local NTSC-J VHS capture | 4.73% | Luma, chroma, JSON, all 800 `fileLoc` values, stdout, and normalized logs were identical. |
| Local PAL VHS capture | 5.00% | JSON and all 800 `fileLoc` values were identical. Luma differed in 0.000794% of samples and chroma in 0.003226% (0.00201% combined), all within one heavily damaged field; normalized logs also differed. |

These results describe the tested captures and machine, not a universal speed
or compatibility guarantee. The PAL zero-output-difference experiment required
falling back to the Exact inverse FFT and reduced the paired median gain to
-1.05%, so that fallback is not enabled.

- `-t` / `--threads` drives bounded parallel RF demodulation and filtering;
  stream, FFmpeg, and GNU Radio reads stay ordered.
- Exact 40.0 MHz `.s16` inputs use the native signed-16 loader instead of a
  no-op FFmpeg pass-through. Other formats and actual resampling keep their
  existing FFmpeg paths.
- Packed `.lds` input decodes directly into the requested result array,
  including Python-compatible partial tail groups, instead of allocating and
  copying a second fully unpacked array. Its loader reuses one private packed
  byte buffer up to 1,048,576 bytes; concurrent callers never share a borrowed
  buffer, and larger reads are not retained.
- A stream-scoped decoded RF cache avoids duplicate FFT work across overlapping
  field reads while keeping memory bounded.
- VHS uses a bounded continuous RF pipeline. One producer owns ordered input
  reads, at most 32 lookahead slots are retained, and no more than eight blocks
  decode concurrently. Each completed block is published independently, so a
  field waits only for the blocks it needs instead of an entire batch. Seek,
  stream changes, and disposal cancel and drain the producer before another
  reader can touch the FFmpeg/GNU Radio stream. Completed blocks copy their
  disjoint trimmed ranges into the final RF span in parallel under the same
  worker bound; serial and stateful block paths retain ordered assembly.
- VSync envelope/minima work and harmonic power-ratio search run concurrently
  over one shared read-only padded input. Candidate arbitration and detector
  state updates remain ordered after both branches complete. NumPy-compatible
  float64 medians retain full sorting below 4K samples and use bit-exact
  introselect from 4K samples.
- VSync's private forward/reverse envelope and harmonic BA-IIR chains filter
  their owned arrays in place. The envelope branches write directly into the
  reduced result instead of materializing a combined padded array; public IIR
  results retain independent ownership and identical bits. The stateful
  detector retains the two most recently used exact-sized six-array analysis
  workspaces when the padded input is at most 1,048,576 samples (about 48 MiB
  per entry and 96 MiB total at the cap). Exact-shape hits promote an entry and
  a third shape evicts the least-recently-used entry; larger inputs use an
  unretained workspace.
- VSync serration measurement reads its candidate window through a read-only
  span and applies an `Enumerable.Min`-compatible float64 scan, avoiding an
  extra full-window copy. Median scratch ownership and NaN/signed-zero bit
  semantics remain unchanged.
- Pulse detection uses AVX comparisons only to skip sample runs without a
  threshold transition. Ordered scalar code still commits each state change,
  validates pulse lengths, and appends results; unsupported CPUs retain the
  original scalar path.
- VHS field decode overlaps luma TBC rendering with chroma field decoding when
  workers are enabled. Only one chroma task can be in flight, and its state is
  committed on the calling thread before the next field advances. Exact-mode
  tape-envelope dropout detection shares this bounded field overlap: at most
  one read-only dropout task exists, and it is joined before the field returns.
- Long TBC sinc-resampling jobs share the worker budget and preserve output
  order; `--threads 0` and `--threads 1` retain deterministic serial paths.
- Linear wow adjustment evaluates the constant derivative once per line,
  expands it only after median/MAD repair, and overlaps source-position and
  level preparation with a fixed two-way task when workers are enabled.
- Under `current` color-under processing, phase analysis resamples only the
  first `BurstStart + BurstEnd` samples that its burst probe can read from each
  output line. Linear interpolation uses compact pooled source-position and
  level-adjust arrays; wow smoothing still advances through every omitted
  sample with the original FMA recurrence, and non-linear interpolation keeps
  the full-plan fallback.
- VHS heterodyne and carrier tables use bounded parallel construction and a
  session-owned one-entry cache. Exact-key hits reuse the original arrays;
  sample-shape, carrier, phase, or AFC changes replace the prior entry instead
  of growing retained state. Phase analysis reads the field-owned resampled
  array directly. Decode borrows that same read-only array when no chroma
  prefilter is configured; configured filtering still returns owned output,
  and the public prefilter API retains its independent-copy contract.
- Internal VHS chroma comb and automatic gain share one line-sized stack
  workspace, and the decode-only path maps scaled samples directly into the
  final `ushort[]`. AVX2/SSE4.1 handles the saturating body while an exact scalar
  fallback preserves unsupported-CPU and tail semantics. Public comb, gain, and
  conversion APIs retain their independent-output contracts.
- HiFi uses bounded parallel block decoding followed by ordered
  post-processing and writing.
- Managed real FFTs reuse pooled packing and scratch buffers. Float32 SOS
  forward/backward filtering rents one extended buffer, operates in place, and
  returns it synchronously; returned output arrays retain normal ownership.
- Double-precision BA IIR forward/backward filtering also operates on one
  in-place padded workspace. Its private pool retains at most three arrays per
  bucket through 4M samples, returns them synchronously, and keeps every result
  in an independently owned exact-length array.
- RF span assembly writes directly into the requested output window instead of
  allocating whole-block field arrays and slicing a second copy.
- Default linear TBC resampling rents its per-field source-position and
  level-adjust workspaces, uses exact spans, and returns both after every
  synchronous serial or parallel resample.
- The stateful VHS CLI sequence path retains one exact-length luma field
  workspace and one separate chroma field workspace because luma rendering can
  overlap chroma decoding. Public `Decode()` and retained `DecodeFields()`
  results still own independent `ChromaBurstSamples`; only the internal
  non-retaining CLI path omits them. The direct UInt16 path needs no double
  field, and public resampling APIs retain independent-output ownership.
- VHS diff-demod spike repair reuses one full-length complex scratch array
  inside the existing 16-slot real-FFT workspace pool. Returned analytic arrays
  retain independent ownership; non-VHS paths keep their allocating fallback.
- On little-endian hosts, TBC and chroma samples stream directly from their
  `ushort` spans without allocating a full-field byte copy. The big-endian
  fallback uses one returned pooled buffer, so repeated writes remain bounded.
- Real multi-worker VHS sessions use a dedicated capacity-one payload writer.
  It writes luma and chroma concurrently while the producer decodes the next
  field, and owns payload, metadata-snapshot, and completion ordering. Shutdown
  drains the queue; serial and public custom-reader paths retain synchronous
  ordered writes.
- Standard VHS field decode reuses at most two exact-length RF span buffer sets,
  matching the only two block counts a fixed read window can cover. Buffers are
  returned after synchronous field decode; public `Read` results, deferred CVBS
  rendering, and retained LD VITS sources keep independent ownership.
- VHS sync-level DC adjustment reuses at most two exact-length low-pass
  workspaces. The stateful pipeline owns those private buffers; original video,
  public results, and deferred-render inputs remain untouched and independently
  owned.
- VHS drops block-local raw input, raw demodulation, analytic, and RF high-pass
  results after their last block-local consumer. Compact real-FFT blocks feed
  their split real/imaginary workspaces directly into the FM unwrap. This omits
  the unused RF high-pass inverse FFT, three RF-span copies, and one full-length
  `Complex[]`; LD, CVBS, and direct decoder construction retain full-channel
  behavior.
- Compact VHS stream blocks also retain their already-quantized SOS chroma in
  `float[]` form. RF span assembly widens it once into the reusable field buffer
  with AVX or an exact scalar fallback; full/direct blocks keep the public
  `double[] Chroma` contract.
- AVX/FMA kernels accelerate exact float32 conversion, VHS RF-envelope
  preparation, VHS Rust-style FM angle approximation, LD quantization, VHS
  chroma rotation, and complex frequency filtering. The forward/inverse radix-4
  FFT kernels use pinned pointer indexing. The 16-tap TBC sinc interior computes
  independent float weights and products with AVX/FMA, then accumulates them in
  original tap order; clamped edges, short inputs, and unsupported hardware keep
  the scalar path. Differential tests preserve exact transform bits and hashes.
- Recovery metadata is disk-streamed; its snapshot queue has capacity one, and
  field-order history and RF caches have hard limits. Long decodes therefore do
  not retain every decoded field or enqueue an unbounded amount of future work.
- CUDA/OpenCL is not a runtime dependency. Current traces do not justify moving
  isolated 32K FFTs across the host/device boundary; any future optional GPU
  backend must batch a device-resident DSP stage and retain an exact CPU fallback.

A strict Exact-mode benchmark of the dropout overlap used the same synthetic
packed PAL RF fixture, 160 requested frames, one warm-up pair, and four
alternating Release pairs per mode. Against the pre-change main build, median
end-to-end wall time fell by 2.8% at `--threads 5` and 4.5% at
`--threads 20`; the candidate won all eight pairs. Luma TBC, chroma TBC, JSON,
stdout, normalized stderr/logs, and all 320 ordered `fileLoc` values were
identical. `--threads 0` also passed the same gate on its unchanged serial path.
A 200-frame default-worker run had no second-half slowdown; only one dropout
task can exist per field, and sampled memory fell again in the final quarter.

The subsequent owned-buffer VHS chroma SOS pass runs the same float32
forward/backward filter in place only when the current stage exclusively owns
the `double[]`; conversion points, odd padding, section order, and public
ownership remain unchanged. A matched 40-frame trace reduced sampled managed
allocation from 3.143 to 2.865 GiB (8.9%), `Double[]` allocation from
2,637.6 to 2,351.7 MiB (10.8%), and Gen2 collections from 18 to 17. Four
alternating 160-frame pairs reduced wall-time medians by 2.2% at
`--threads 5` and 3.5% at `--threads 20`; two serial pairs improved by 1.0%,
and the candidate was faster in all ten pairs. Luma TBC, chroma TBC, JSON,
ordered `fileLoc`, stdout, normalized stderr/logs, and cross-thread hashes were
identical. A monitored 200-frame default-worker run also had no second-half
slowdown or monotonic memory growth.

The VSync serration detector now keeps one fixed 60-line median scratch buffer
inside each of its at-most-two exact-shape workspaces; the public static
measurement API retains its independently allocated result path. A matched
40-frame default-5 `gc-verbose` trace removed all 213
`ReadOnlySpan<double>.ToArray()` samples attributed to that median
(77.234 MiB), reduced total sampled allocation from 2.863362 GiB to
2.790004 GiB (2.6%), and reduced Gen2 collections from 16 to 15. The observed
160-frame medians improved by 0.2% at default-5, 1.5% at 20 workers, and 1.4%
in a clean serial retry, but pair wins were mixed at 20 workers, so the claimed
result is the allocation reduction rather than a fixed throughput percentage.
Luma, chroma, JSON, all 320 ordered `fileLoc` values, stdout, normalized
stderr/logs, and cross-thread hashes were exact at `--threads 0`, default-5,
and `--threads 20`. A matched 200-frame default-worker check completed in
15.303 s versus 15.385 s on main, ran its first/second halves at 77.22/70.87 ms
per frame, and peaked at 0.993 GiB; retained median storage remains bounded to
two 60-line buffers per detector.

### VHS sync-analysis workspace reuse

Each exclusive VHS sync-detector workspace now retains five non-escaping
scratch arrays: sync and porch candidates, one shared statistics buffer used
sequentially for sorting, MAD, and slopes, grid-support counts, and the final
mask. Every sort is limited to the current logical length, grid counts are
fully initialized, and the mask is cleared before reuse. Arithmetic order,
thresholds, pulse ownership, and the independently owned result array are
unchanged.

A valid-pulse allocation benchmark moved from 46,362.2 to 28,642.2 bytes per
call, a 38.2% reduction. In matched 200-frame `gc-verbose` traces, main sampled
7.71 MiB over 76 recurring `double[]` ticks and 0.20 MiB over two `bool[]` ticks
directly in `DetectFiltered`; the candidate removed both recurring call sites.
The retained workspace arrays appeared only as initial capacity growth at
second zero. Escaping edge and pulse result arrays remain independently owned.

Three interleaved 200-frame Exact `current --threads 20` pairs and two
opposite-order 1,000-frame pairs matched luma, chroma, raw JSON, stdout,
normalized stderr/logs, field count, and every ordered `fileLoc`. Across the
two long pairs, main/candidate wall time was 67.258/67.004 seconds, a neutral
0.38% reduction; CPU time was 569.563/578.985 seconds, so no CPU or fixed
throughput improvement is claimed. Candidate peak working set stayed between
355.1 and 357.0 MiB in both long runs.

An additional 24-run screen covered Exact and IPP-fast, both v0.4.0 and
`current`, and `--threads 0`, default-five, and `--threads 20`. Every candidate
matched main in the same configuration for luma, chroma, raw JSON, stdout,
normalized stderr/logs, and ordered `fileLoc`; Exact also remained identical
to its profile's main `--threads 0` oracle across all three thread modes. The
full xUnit v3 suite exposes 1,437 tests, including dirty large-small-large
workspace reuse, shared-detector concurrency, and valid-pulse warm-allocation
coverage. The public six-path table is not refreshed by this pass because an
unrelated foreground CPU load was active during the gate and the matched long
result was throughput-neutral.

### Parallel final current VHS radix collection

The compact three-stage `current` VHS level selector now reuses its final
worker-local histograms to collect the two selected radix buckets in parallel.
A fixed worker-order prefix sum converts the final bucket counts into stable
scratch offsets. Every worker scans one contiguous source partition and writes
only to its two disjoint destination ranges, so concatenating worker ranges
preserves the exact serial source order. Sortable-prefix conversion, float64
values, bucket and rank selection, final quickselect expressions, and all
v0.4.0, serial, dense, and exceptional-value paths remain unchanged.

The focused regression compares both selected results and the complete
post-collection scratch bit patterns across 2, 3, 4, 5, 10, and 20 workers and
two data distributions. The full standard xUnit v3 suite passed 1,437/1,437
with the IPP runtime present. The 36 current VHS sync tests also passed with all
hardware intrinsics disabled; the one AVX-only case was skipped by design. A
24-run real-RF gate covered Exact and IPP-fast, v0.4.0 and `current`, and
`--threads 0`, default-five, and `--threads 20`. Luma, chroma, raw JSON, stdout,
normalized stderr/logs, and all ordered `fileLoc` values matched main, and each
candidate profile remained deterministic across its three thread modes.

Three interleaved 200-frame Exact `current --threads 20` pairs changed wall time
by -4.54%, -3.51%, and +0.36%, for a 3.51% median reduction. Two opposite-order
1,000-frame pairs moved from 34.911 to 33.184 seconds and from 34.461 to 33.263
seconds, reductions of 4.95% and 3.48%. Across those long pairs, average active
cores rose from 8.34 to 8.87 while aggregate CPU time increased by 1.9%; every
captured output and diagnostic surface remained exact.

A separate 2,000-frame baseline/candidate resource gate completed all 4,000
fields and matched luma, chroma, raw JSON, and stdout. Wall time moved from
60.097 to 59.450 seconds (1.08% lower), CPU time from 501.750 to 503.234 seconds,
and average active cores from 8.349 to 8.465. Candidate private-memory quarter
medians were 370/621/796/691 MiB, versus 364/364/364/413 MiB on main; peaks were
890.5 and 425.8 MiB respectively. Candidate memory was non-monotonic and ended
below its peak, with no OOM, so this is bounded-run evidence rather than a
memory-reduction or unlimited-duration claim.

### Pooled IPP VHS envelope SOS

IPP-fast now routes the full-length, one-section VHS RF envelope SOS through a
bounded `IppSos32FilterPool`; Exact keeps the existing managed float32
expression and order. The pool retains at most 12 native contexts and is
disposed with its RF pipeline. A dedicated xUnit v3 integration test verifies
pipeline routing, context creation, retention bounds, and disposal.

On the fixed private 200-frame PAL VHS trace, the roughly 1.50 seconds of
managed SOS CPU attributed to RF envelope filtering disappeared; the remaining
0.44 seconds of managed SOS belonged to chroma processing. Two opposite-order
1,000-frame IPP-fast pairs kept every captured artifact and log surface exact.
The v0.4.0 profile was wall-neutral at 46.282/46.331 seconds while average CPU
fell from 199.695 to 198.227 seconds (0.74%). `current` improved from
34.463 to 34.224 seconds (0.69%) and from 216.430 to 210.336 CPU seconds
(2.82%).

A separate 1,000-frame runtime-counter run sampled working set 34 times. Its
first/final-third medians were 386.8/387.6 MiB, the peak was 390.8 MiB, total
allocation was 677.1 MiB, and GC pause time was 35 ms. The 12-run deterministic
gate covered Exact and IPP-fast, v0.4.0 and `current`, at `--threads 0`,
default-five, and `--threads 20`; luma, chroma, raw JSON, stdout, normalized
stderr/logs, and ordered `fileLoc` matched within every profile.

### Allocation-free internal VHS burst-probe results

The Phase 22 candidate keeps the public `ChromaBurstDemodulationResult` as its
original sealed record class. A direct public class-to-struct prototype was
rejected during review because it would have changed CLR signatures, binary
binding, `null`/`default`, identity, boxing, and generic behavior. The final path
uses a separate internal readonly record struct for the eleven scalar fields and
converts to the public class only at an actual public API boundary. A focused
xUnit test locks both type categories, signed zero, NaN payloads, infinities,
init-only fields, exact conversion, and nondestructive `with` behavior.

Seven reverse-order focused pairs preserved the exact checksum while reducing
median allocated bytes from 114,244,800 to 67,256,448 serially (41.13%) and from
183,595,176 to 137,502,920 with four workers (25.11%). In a matched 200-frame
Exact `current --threads 20` GC trace, total allocation ticks fell from 2,099 to
1,998 (4.81%), and sampled `ChromaBurstDemodulationResult` ticks fell from 108 to
zero. The trace also matched luma, chroma, raw JSON, stdout, normalized
stderr/logs, and ordered `fileLoc`.

Twenty reverse-order 200-frame pairs, eight opposite-order 1,000-frame pairs, and
the 60-run matrix below kept every captured surface exact. The long-run paired
wall changes ranged from 0.37% faster to 1.01% slower, so this is retained as an
allocation and GC-pressure improvement, not a claimed end-to-end throughput or
peak-memory win.

### Bounded libsndfile PCM16 overlap rewind

Parallel VHS block batches read overlapping 32,768-sample RF windows. The direct
libsndfile path previously sought backward and decoded that overlap again for
each reusable block. Phase 24 adds one lazily allocated 1,048,576-frame circular
PCM16 rewind window, exactly 2 MiB, to each libsndfile loader. `ReadReusable`
copies a cached prefix and requests only the fresh tail from libsndfile. Ordinary
`Read` keeps its prior seek behavior; mapped restart, a real native seek,
fallback, and disposal clear the ring. PCM16 conversion still uses the same
short-to-double expression and AVX2 conversion path.

Six interleaved 200-frame pairs across Exact and IPP-fast matched luma, chroma,
raw JSON, stdout, normalized stderr/logs, and every ordered `fileLoc`. Exact wall
median moved from 9.231 to 9.223 seconds while CPU median fell from 88.141 to
84.906 seconds. IPP-fast wall median moved from 7.749 to 7.757 seconds while CPU
median fell from 45.953 to 45.406 seconds; the short wall deltas are classified
as neutral screening evidence.

Four opposite-order 1,000-frame pairs kept the same surfaces exact. Exact
baseline/candidate mean wall time was 35.383/35.027 seconds, a 1.00% reduction;
mean CPU time was 293.797/290.023 seconds, a 1.28% reduction. IPP-fast mean wall
time was 33.112/32.830 seconds, a 0.85% reduction; mean CPU time was
197.367/196.844 seconds, a 0.27% reduction. Candidate working-set peaks stayed
within 353.0-357.5 MiB and private-memory peaks within 366.9-371.5 MiB. The ring
is fixed-size, is released on fallback/disposal, and cannot grow with decode
length.

A separate 12-run baseline/candidate gate covered Exact v0.4.0 and `current` at
`--threads 0`, omitted/default-five, and `--threads 20`. All captured surfaces
matched main and each profile remained deterministic across worker counts. The
zero-warning Release build, all 1,442 standard xUnit v3 tests, the 64-test
libsndfile class, and the 15-test native IPP smoke gate passed.

### Early strict analytic preparation staging

For Exact VHS `current` above the existing 12-worker threshold, the decoder now
queues the strict NumPy-compatible full-complex analytic preparation before the
main worker starts its real RF FFT. The companion and main paths use disjoint
workspace arrays and the same existing FFT plans, data types, expressions,
padding, and conversion points. The join still preserves real-inverse exception
priority and ordered block commit. v0.4.0, default-five, `--threads 1/5/10`,
GNRC, sharpness, and IPP-fast retain their prior paths.

Three interleaved 1,000-frame Exact `current --threads 20` pairs matched exit
status, luma, chroma, raw JSON, stdout, normalized stderr/logs, every ordered
`fileLoc`, and the other saved metadata. Baseline/candidate medians were
37.876/37.403 seconds, a 1.25% wall-time reduction. Median CPU time rose from
301.375 to 319.250 seconds, or 5.93%, while effective core use rose from 7.96
to 8.54. Median peak working set moved from 402.34 to 392.10 MiB and median
peak private bytes from 414.81 to 403.58 MiB. The candidate remained bounded
and did not add a per-block allocation.

The startup-heavy public 160-frame window remained noisy: three interleaved
pairs had baseline/candidate medians of 6.930/7.437 seconds, 7.30% slower, while
their 6.598-7.618 and 6.635-7.467 second ranges overlapped and individual pair
directions differed. A separate candidate-only three-run refresh measured
6.710-7.900 seconds with a 6.782-second median and one hash for each of the seven
captured compatibility surfaces. The table uses that refresh, but cross-date
host state is not treated as causal evidence; the same-moment long A/B remains
the candidate gate. Another 12-run gate covered Exact v0.4.0 and `current`
at `--threads 0`, default-five, and `--threads 20`, with all surfaces matching
and each profile deterministic. The zero-warning Release build and 1,443-test
standard xUnit v3 suite gate passed: 1,439 tests passed, zero failed, and four
local IPP-only cases were skipped because that test output lacked the native runtime.

### Parallel managed real-FFT inverse preparation

The latest Exact VHS `current` path divides the large managed real-FFT inverse's
independent conjugate-pair preparation across the requested workers. Every pair
still uses the same float32 inputs, twiddle lookup, arithmetic expressions,
write locations, and center-bin overwrite as the serial loop. The transform,
normalization, packet scheduling, and bounded worker-owned workspace are
unchanged. Small transforms and one-worker calls remain serial.

Three interleaved 1,000-frame Exact `current --threads 20` release-binary pairs
matched exit status, luma, chroma, raw JSON, stdout, normalized stderr/logs, and
every ordered `fileLoc`. Each pair favored the candidate by 6.25%, 5.29%, and
1.04%. Baseline/candidate medians were 35.822/33.715 seconds, a 5.88% wall-time
reduction and 1.063x throughput gain. Median CPU time fell from 298.891 to
284.063 seconds (4.96%) while effective core use moved from 8.34 to 8.43.
Median peak working set fell from 359.28 to 348.45 MiB (3.01%), and median peak
private bytes fell from 373.59 to 359.74 MiB (3.71%).

The startup-heavy 160-frame apphost matrix was intentionally retained as a
counterexample rather than presented as a universal gain. Across 15
same-moment baseline/candidate pairs, candidate medians were 0.08%, 0.44%,
1.85%, and 2.59% faster at default, 1, 5, and 10 workers, but 4.14% slower at
20 workers. All seven captured surfaces stayed exact. A separate three-pair
one-worker audit measured the older `bdccd58` binary at 41.77 seconds and
current main at 41.04 seconds. The lower ratios than the previous cross-date
snapshot therefore reflect host-state drift, not a Phase 24 or Phase 25
revision regression. A 12-run gate covered Exact v0.4.0 and `current` at
`--threads 0`, omitted/default-five, and `--threads 20`; every surface matched
main and each profile remained deterministic.

A three-case xUnit v3 theory now spans supported real-FFT lengths 32,256,
32,768, and 33,600: below, at, and above the 8,192-pair parallel threshold.
Each 20-worker workspace inverse remained byte-identical to the serial result.
The full local suite discovers 1,446 tests: 1,442 passed, zero failed, and four
IPP-only cases skipped without the native runtime.

### Bounded cross-field VHS wavefront

The latest candidate adds a capacity-two wavefront to production VHS sequence
decoding. Ordered sync/state planning still runs on the sequence thread. Luma
rendering, prepared chroma completion, and tape-dropout mapping may finish in
parallel, while the next field's RF read begins only after every task that still
references the current RF span has completed. Output, JSON, `fileLoc`, recovery,
and diagnostics commit in input order. Diagnostic capture is scoped per field
and restored before the next ordered commit.

The large leased RF span is returned before lookahead; only the independently
owned chroma tail and small pooled field outputs cross the field boundary. The
chroma, render, and output pools have capacity two. A 500-frame memory pair
measured 354.4/473.2 MiB baseline/candidate peak working set and
365.7/525.0 MiB private bytes. At 1,000 frames those values were
353.6/468.9 MiB and 367.2/502.8 MiB, confirming a fixed window rather than
per-field growth. The first prototype retained two complete RF spans and peaked
near 884 MiB; that version was rejected before publication.

Exact `current` is intentionally excluded from this wavefront: three
interleaved 500-frame pairs measured 19.74/19.86-second medians after the gate,
with no useful gain. Retained paths did benefit. Exact v0.4.0 moved from
33.55 to 32.31 seconds (3.7% less wall time), and IPP-fast `current` moved from
19.77 to 17.26 seconds (12.7% less wall time); the latter raised effective core
use from 5.66 to 6.58 while adding about 1.5% CPU time.

The final 1,000-frame `--threads 20` release-binary gate matched luma, chroma,
raw JSON, ordered `fileLoc`, stdout, normalized stderr, and normalized logs for
all four Exact/IPP-fast and v0.4.0/`current` combinations. Exact v0.4.0 moved
from 46.047 to 42.575 seconds, IPP-fast v0.4.0 from 44.980 to 42.084 seconds,
and IPP-fast `current` from 31.009 to 25.260 seconds. On the last path CPU time
moved from 188.20 to 197.34 seconds while effective cores rose from 6.07 to
7.81. A separate 24-run gate covered `--threads 0`, omitted/default-five, and
20 workers. The standard xUnit v3 suite passed all 1,459 tests.

### Exact-current sync-candidate scratch audit

A diagnostic full cross-field Exact-current wavefront was technically viable,
but it was rejected rather than enabled. Two opposite-order 1,000-frame
`--threads 20` pairs measured 33.91/35.96-second baseline/candidate medians:
the candidate was 6.05% slower, CPU time rose 6.13%, and effective core use was
unchanged at 8.51/8.52. This demonstrated contention rather than useful added
parallelism, so the production Exact-current gate remains in place.

The retained change instead replaces each VBlank candidate's temporary
`List<ClassifiedSyncPulse>` with a bounded stack span of at most 26 entries and
copies accepted entries only after the state machine completes. A matched
500-frame runtime-counter pair reduced managed allocation from 1,058,682,656 to
572,202,104 bytes (46.0%), Gen0 collections from 60 to 30, and GC pause from
44.4 to 24.2 ms. Peak working set moved from 415.3 to 411.9 MiB and measured
CPU time from 161.5 to 158.9 seconds. Six interleaved 160-frame pairs and two
opposite-order 1,000-frame pairs classified wall throughput as neutral, so the
public Python/.NET speed table was not rewritten.

A 24-run release-binary gate covered Exact/IPP-fast, v0.4.0/`current`, and
`--threads 0`/omitted-default-five/20 workers. Luma, chroma, raw JSON, ordered
`fileLoc`, stdout, normalized stderr, and normalized logs matched in every run.
The focused 10,000-rejected-candidate allocation test and all 1,460 standard
xUnit v3 tests passed.

### Field-local sync-pulse workspace reuse

The next Exact-current pass keeps caller-owned classified and refined pulse
lists on `TbcFieldDecodePipeline`. The public `SyncAnalyzer` methods retain
their existing owned-result behavior; only the internal sequential field path
clears and reuses its two lists. Pulse order and values, rescue mutation,
VBlank state, diagnostics, and every cross-field transition are unchanged.
If a damaged field grows a backing array beyond 65,536 entries, the next
normal-sized field releases that oversized retention before reuse.

A worker-local PocketFFT scratch prototype was rejected before this change.
It remained bit-exact, but two opposite-order 1,000-frame pairs measured
32.62/32.64-second baseline/candidate group medians and 297.09/299.14 seconds
of CPU time, so the extra API surface had no stable end-to-end value.

For the retained pulse-list change, a matched 160-frame `gc-verbose` trace
reduced sampled managed allocation from 680,536,552 to 621,643,144 bytes
(8.65%) and removed the 65,614,400-byte sampled `ClassifiedSyncPulse[]`
hotspot. Six short pairs were scheduling-noisy, but two opposite-order
1,000-frame `--threads 20` pairs reduced median wall time from 32.95 to 32.11
seconds (2.55%) and CPU time from 298.40 to 288.02 seconds (3.48%); effective
core use moved from 9.06 to 8.97.

Both opposite-order memory pairs gave the candidate a lower peak. The
conservative reverse-order comparison measured baseline/candidate peak working
set at 390.8/360.5 MiB and private bytes at 409.9/374.4 MiB.
The baseline-first pair contained a 628.7 MiB baseline GC peak, so that larger
difference is reported as an outlier rather than a percentage claim. A 24-run
release-binary gate covered all four backend/profile combinations at
`--threads 0`, default-five, and 20 workers; every captured surface matched.
The refreshed 60-run public matrix retained one hash per surface in every cell,
and all 1,463 standard xUnit v3 tests passed.

### Latest six-path thread matrix

The latest public summary is a startup-inclusive `--start 100 --length 160`
snapshot comparing Python v0.4.0, merged Python PR341, Exact v0.4.0, Exact
`current`, IPP-fast v0.4.0, and IPP-fast `current` on the same private local
40 MHz PAL VHS `.ldf` fixture. The source filename is intentionally not
published. The active table retains 30 fixed Python reference measurements from
2026-08-12. All 60 .NET measurements were refreshed together on 2026-08-15
with the latest candidate based on main `508b4f6`. Each .NET cell gives the
median wall time, speedup, and wall-time reduction against its profile-matched
Python column. Historical
matrices that used another batch, format, or fixture are not directly comparable:

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 52.811 s | 54.243 s | 12.625 s / 4.183x / 76.09% | 11.841 s / 4.581x / 78.17% | 11.140 s / 4.741x / 78.91% | 8.530 s / 6.359x / 84.27% |
| `--threads 1` | 57.067 s | 56.762 s | 32.879 s / 1.736x / 42.39% | 36.092 s / 1.573x / 36.42% | 23.205 s / 2.459x / 59.34% | 25.135 s / 2.258x / 55.72% |
| `--threads 5` | 52.920 s | 55.722 s | 12.842 s / 4.121x / 75.73% | 11.720 s / 4.754x / 78.97% | 11.120 s / 4.759x / 78.99% | 8.489 s / 6.564x / 84.77% |
| `--threads 10` | 52.965 s | 54.949 s | 10.233 s / 5.176x / 80.68% | 8.783 s / 6.256x / 84.02% | 9.137 s / 5.797x / 82.75% | 6.473 s / 8.488x / 88.22% |
| `--threads 20` | 53.555 s | 54.842 s | 8.183 s / 6.545x / 84.72% | 7.624 s / 7.194x / 86.10% | 7.890 s / 6.788x / 85.27% | 5.220 s / 10.507x / 90.48% |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-15 dotnet-current-date=2026-08-15 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

The three-run wall-time ranges were:

<!-- LATEST_PERFORMANCE_RANGES_BEGIN -->
| CLI mode | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 52.583-62.222 s | 53.893-58.195 s | 12.300-13.214 s | 10.791-11.889 s | 10.821-11.256 s | 8.493-8.553 s |
| `--threads 1` | 56.709-60.521 s | 56.335-58.991 s | 31.842-32.947 s | 35.314-36.434 s | 22.932-23.407 s | 25.057-25.195 s |
| `--threads 5` | 52.845-53.977 s | 53.696-58.437 s | 12.468-12.960 s | 11.554-12.030 s | 11.002-11.184 s | 8.444-8.590 s |
| `--threads 10` | 51.797-53.088 s | 52.649-56.775 s | 10.115-10.315 s | 8.690-9.143 s | 9.134-9.154 s | 6.387-6.524 s |
| `--threads 20` | 52.967-55.987 s | 53.005-55.618 s | 8.080-8.198 s | 7.528-7.879 s | 7.694-7.908 s | 5.202-5.406 s |
<!-- LATEST_PERFORMANCE_RANGES_END -->

The 30 retained Python measurements come from the fixed-condition 2026-08-12
campaign. All twenty .NET cells contain 60 complete 2026-08-15 runs from one
candidate binary. The active runs produced
one luma, chroma, raw-JSON, stdout, normalized-stderr, and normalized-log hash
set per backend/profile across all thread modes. The separate A/B gates also
matched ordered `fileLoc`.
Python v0.4.0 produced 15 distinct luma, chroma, JSON, and normalized-log hash
sets in 15 runs; its strict oracle therefore remains `g4315520 --threads 0`.

All refreshed .NET cells use the latest candidate based on main `508b4f6`; its
single-file `decode.exe` SHA-256 is
`3ACDAEC6E641D4ED81E649BCE0C9EA4BFBCAF551860543BA2A5026FBF4E2DA27`.
The host was an Intel Core Ultra 7 265K with 20 logical processors, Windows 11
build 26220, and .NET SDK/runtime `11.0.100-preview.6.26359.118`. Raw directories
stay local because they contain the private fixture path; these are reported
local measurements, not an independently reproducible public corpus.

The three-run ranges expose ordinary startup, thermal, scheduler, and system
variation. Ratio cells move when either the Python numerator or .NET denominator
moves. They are not used to attribute a revision regression or speedup; the
same-moment interleaved revision A/B evidence below is the causal gate.

### Managed AVX Super-Gaussian spectrum mask

The managed Exact chroma final filter now applies its Super-Gaussian spectrum
mask to four `Complex32` values at a time with AVX. Each float component is
widened to double, and the original scalar multiply, subtract/add, and final
double-to-float conversion points are retained lane by lane. The loop uses no
FMA or reduction. Before any vector is stored, both double results are checked;
if one lane is NaN or infinity, that vector and the remaining tail run through
the original scalar JIT shape. Unaligned spans and one-, two-, and three-value
tails keep the same scalar behavior. The IPP mask path is unchanged.

Eight alternating process-level kernel pairs each applied a 178,201-point mask
2,000 times. All 16 runs produced the same SHA-256. Scalar median wall time was
1,210.850 ms and AVX median was 167.083 ms, an 86.20% reduction and 7.247x
throughput gain; median CPU time fell 86.18%. Release disassembly contained
`vmulpd`, `vsubpd`, and `vaddpd` with no fused multiply-add. Focused tests cover
539 finite combinations, all vector-tail lengths, unaligned slices and
sentinels, plus the full 12-by-12-by-12 exceptional-value cross. Default JIT,
`TieredCompilation=0`, forced AVX-off, and all-hardware-intrinsics-off evidence
matched the baseline's corresponding scalar results.

Six interleaved 1,000-frame Exact `current --threads 20` release-binary pairs
matched exit status, luma, chroma, raw JSON, stdout, normalized stderr/logs, and
every ordered `fileLoc`. The candidate won five pairs; the sixth was 0.03%
slower. Independent medians moved from 35.879 to 35.608 seconds wall time
(0.76%, 1.008x), from 298.148 to 295.141 seconds CPU time (1.01%), and from
8.18 to 8.38 effective cores (2.43%). Median peak working set moved from
352.5 to 345.9 MiB and private bytes from 364.8 to 357.9 MiB.

The final release binary passed 12 Exact and 12 IPP-fast profile/thread gates,
plus native, AVX-disabled, and all-intrinsics-disabled Exact gates. All seven
artifact/log surfaces and ordered `fileLoc` matched. A fresh 60-run public
matrix used three complete runs for every backend/profile/thread cell and kept
one hash set per cell. An earlier batch was discarded after unrelated high-CPU
processes appeared; the accepted batch required a low external-CPU sample before
every decode. The full xUnit v3 suite passed all 1,448 tests.

### Final real-inverse radix-4 vectorization and analytic copy removal

The analytic-difference repair no longer copies the full analytic buffer into a
second array before overwriting it. A descending in-place pass preserves the
same source value for every subtraction and keeps allocation, padding, and
downstream ownership unchanged. Four interleaved 1,000-frame Exact
`current --threads 20` pairs matched every compatibility surface. Median wall
time moved from 39.174 to 39.152 seconds, a neutral 0.06%, while median CPU time
moved from 315.130 to 310.140 seconds, a 1.58% reduction.

The common final `ido == 1` stage of the 32K real inverse FFT now processes four
independent radix-4 butterflies with AVX. Loads are transposed into the original
butterfly layout, and every lane retains the scalar operand and multiplication
order. The path uses no FMA or horizontal reduction and leaves the scalar tail
and AVX-disabled fallback unchanged. A dedicated xUnit v3 oracle compares the
full inverse output against the scalar implementation with signed zeros,
subnormals, maximum finite values, infinities, and signaling and quiet NaNs.

Eight alternating process-level kernel pairs, each performing 4,000 warmed 32K
inverses, produced one SHA-256. Median wall time moved from 697.821 to 676.900 ms
(3.00%). Four interleaved 1,000-frame Exact `current --threads 20` release-binary
pairs then matched luma, chroma, raw JSON, stdout, normalized stderr/logs, and
every ordered `fileLoc`; the candidate won all four. Median wall time moved from
32.971 to 32.323 seconds (1.96%, 1.020x), and CPU time from 278.195 to 267.500
seconds (3.84%).

A separate 1,000-frame sampling run peaked at 394.2 MiB working set and
404.5 MiB private bytes. First-third versus last-third medians fell rather than
grew, so no retained-buffer or OOM trend was observed. Exact and IPP-fast,
v0.4.0 and `current`, and `--threads 0`, default-five, and `--threads 20` then
passed 24 release-binary compatibility gates, including native and scalar FFT
paths. The refreshed 60-run public matrix retained one hash set per profile
across all five worker modes. The full xUnit v3 suite passed all 1,448 tests.

### AVX2 CTI reciprocal-table gather

The `current` CTI distance stage keeps its pinned 2,048-entry reciprocal
mantissa table and the same float32 bit construction. On AVX2 hosts, eight
independent bucket indices now use one `GatherVector256`; sign and exponent
fields, zero/subnormal-to-infinity behavior, high-exponent-to-signed-zero
behavior, and quieted NaN payloads are reconstructed lane by lane with integer
bit operations. There is no FMA, reduction, reassociation, shared scratch, or
new sample-level allocation. The original scalar lookup remains the fallback.

The focused xUnit v3 test covers all 2,048 table buckets, both signs, zero,
subnormals, exponent boundaries, infinities, signaling/quiet NaN payloads, and
a scalar tail. Native AVX2 and forced AVX2-disabled runs both passed. Eight
alternating 40-iteration kernel pairs produced one SHA-256 across all 16 runs.
Median wall time moved from 1,763.352 to 912.073 ms (48.28%, 1.933x), and median
CPU time from 1,710.938 to 906.250 ms (47.03%).

Four interleaved 1,000-frame Exact `current --threads 20` release-binary pairs
matched exit status, 2,000 fields, luma, chroma, raw JSON, stdout, normalized
stderr/logs, and every ordered `fileLoc`; the candidate won all four. Median wall
time moved from 34.068 to 33.462 seconds (1.78%, 1.018x), CPU time from 291.781
to 286.734 seconds (1.73%), and effective cores from 8.56 to 8.57. Median peak
working set moved from 345.9 to 347.4 MiB and private bytes from 358.1 to
359.1 MiB; observed peaks stayed bounded across the four independent runs.

The final release-binary matrix added Exact and IPP-fast, v0.4.0 and `current`,
`--threads 0`, default-five, and `--threads 20`, each on native and forced
AVX2-disabled candidate paths. All 36 runs matched the nine compatibility
surfaces and cross-thread determinism. The refreshed 30-run `current` public
matrix retained one hash set per profile across all five worker modes. The full
xUnit v3 suite passed all 1,447 tests with the native IPP runtime available.

### Bounded current VHS inverse companion scheduling

High-worker VHS `current` previously launched each analytic companion inverse
with `Task.Run` from an RF block worker and then synchronously waited. Sampling
showed nested ThreadPool scheduling and blocked outer workers. The isolated
change uses a decoder-owned bounded queue with fixed background workers while
keeping both inverse FFT implementations, expressions, buffers, exception
precedence, and ordered commits unchanged. It activates only without an input
processor and above the existing 12-block prefetch cap; `--threads 20` therefore
configures eight companion workers. They start lazily on the first eligible
inverse, while stateful sharpness clamps the sequential block path to one.
Queue or worker creation failure preserves the original serial
real-then-companion order.

Three interleaved 200-frame Exact `current --threads 20` A/B pairs matched exit
status, luma, chroma, raw JSON, stdout, normalized stderr/logs, and every ordered
`fileLoc`. The paired wall-time-change median was 1.61% lower. A separate
1,000-frame pair matched the same surfaces and moved from 34.732 to 34.000
seconds (2.11%, 1.022x); CPU time moved from 284.766 to 279.500 seconds and
effective cores from 8.199 to 8.221. Candidate working-set maxima were
352.2/351.4 MiB in the first/second half, so the bounded scheduler showed no
progressive growth or OOM.

Default-five and `--threads 0` Exact runs also matched every surface. A
200-frame IPP-fast pair matched every surface and moved from 7.137 to 7.102
seconds; that 0.49% screening delta is treated as noise, not an attributed IPP
speedup. All 1,435 xUnit v3 tests passed with the native IPP runtime available.

The final review follow-up made worker creation lazy, clamped stateful sharpness
to one companion, and waited for completion signaling to return before disposing
each signal. Three interleaved 200-frame pairs against the table candidate again
matched every surface. Their wall deltas were -4.47%, +2.53%, and -2.31%; because
another foreground workload was active, these timings are used only to reject a
regression and are excluded from the table. A final 1,000-frame run matched all
saved artifacts and normalized diagnostics; first/second-half working-set peaks
were 355.2/353.8 MiB and the final sample was 353.6 MiB.

### Ordered AVX TBC sinc accumulation

The 16-tap interior TBC sinc path already computes its interpolated weights and
products with AVX/FMA. The isolated change keeps those instructions, float32
products, double accumulation, cast points, and left-to-right addition order.
It emits all 16 additions through scalar SSE2 intrinsics with each new tap as
the left operand, matching the baseline NaN-payload order, and suppresses
redundant clearing of the fully overwritten stack buffer. The scalar and
boundary paths are unchanged.

Eight interleaved single-thread kernel pairs kept the same SHA-256 and moved
median batch time from 259.624 to 233.064 ms (10.23%, 1.114x); the candidate won
8/8. The product decision still uses full-decoder evidence: three
interleaved 1,000-frame Exact `current --threads 20` pairs matched exit status,
field count, luma, chroma, raw JSON, stdout, normalized stderr/logs, and ordered
`fileLoc`. The candidate won 3/3; mean wall time moved from 35.242 to 35.179
seconds (0.18%, 1.002x), while mean CPU time rose 1.55% from 281.84 to 286.22
seconds and mean active cores moved from 8.00 to 8.14.

The 24-run deterministic gate covered Exact and IPP-fast, v0.4.0 and `current`,
and `--threads 0`, default-five, and `--threads 20`; every product surface
matched baseline and every thread mode. Default, no-FMA, no-AVX, and fully
scalar real-RF runs also matched exactly. Distinct positive/negative NaN
payloads, infinities, and signed zero matched the saved main binary bit for bit.
All 34 focused TBC tests passed with
normal intrinsics, AVX disabled, and every hardware intrinsic disabled. The
full xUnit v3 suite passed 1,429 tests with four expected local IPP-runtime
skips.

A separate 2,000-frame counter gate completed all 4,000 fields exactly and
improved wall time from 68.724 to 68.473 seconds (0.37%, 1.004x). Candidate
working set was 353.1 MiB median and 359.6 MiB maximum; first/final-third
medians were 352.0/357.2 MiB. Its nine post-startup 200-frame intervals stayed
between 6.446 and 6.699 seconds, so throughput did not decay progressively.
Total allocation was 1,302.4 MiB and GC pause time was 73 ms.

### Managed AVX CTI line snapshots

CTI refreshes a float32 snapshot of each float64 chroma line before every one
of its four passes. The isolated change reuses the validated managed AVX
conversion helper to convert eight doubles to eight floats per iteration. It
retains the same casts, padding, scalar tail, destination buffer, pass boundary,
and non-AVX behavior; no CTI arithmetic, state, scheduling, or allocation changed.

All 18 focused CTI tests passed with normal intrinsics, AVX disabled, and every
hardware intrinsic disabled. The conversion special-value test also passed on
the native and AVX-disabled paths, comparing exact float bits for signed zero,
subnormals, infinities, NaNs, unaligned destinations, and scalar tails. The full
xUnit v3 suite passed 1,428 tests with four expected local IPP-runtime skips.

Eight interleaved long CTI kernel pairs preserved one output hash and the same
allocation count. The candidate won 8/8: median wall time moved from 2,522.737
to 2,396.830 ms (4.99% lower, 1.053x throughput), while median CPU time moved
from 2,515.625 to 2,390.625 ms (4.97% lower).

Three reverse-order 1,000-frame Exact `current --threads 20` pairs matched exit
status, field count, luma, chroma, raw JSON, stdout, normalized stderr/logs, and
ordered `fileLoc`. The candidate won all three; mean wall time moved from 36.603
to 35.631 seconds (2.66% lower, 1.027x throughput) and process CPU time from
297.333 to 283.583 seconds (4.62% lower). Another 24 runs covered Exact and
IPP-fast, v0.4.0 and `current`, at `--threads 0`, default-five, and
`--threads 20`; every baseline/candidate pair and every per-profile worker mode
matched on all compatibility surfaces.

A separate 2,000-frame candidate run completed all 4,000 fields in 69.119
seconds. Peak working set was 355.75 MiB, final working set was 353.52 MiB, and
the first/final-quarter means were 341.80/350.72 MiB; there was no progressive
peak growth or OOM. This is local compute reduction, not a multicore-utilization
claim.

### Previous managed AVX double-complex inverse normalization

This retained historical candidate was based on merged main `ea1bb8e`.

Inverse transforms multiply every real and imaginary component by the same
double-precision normalization factor. The candidate processes two independent
complex values per `Vector256<double>` while retaining the scalar operand order.
There is no FMA, reduction, reassociation, shared state, or new allocation; the
original scalar loop remains the tail and non-AVX path. JIT disassembly contained
one `vmulpd` and no FMA instruction.

The dedicated bit-equivalence test covers transform lengths 2, 4, 8, 32, and
512 with signed zero, subnormal, minimum-normal, signed-one, maximum-finite,
infinity, and distinct NaN payload inputs. All five storage tests passed with
normal intrinsics, AVX disabled, and all hardware intrinsics disabled. The full
xUnit v3 suite passed 1,426 tests with four expected IPP-runtime skips.

Eight opposite-order length-32,768 inverse-transform kernel pairs, with 3,000
transforms per measurement, reduced median wall time from 986.10 to 727.61 ms
(26.2%) and median CPU time from 1,000 to 750 ms (25.0%). The output hash was
identical and both revisions allocated 616 bytes.

Three opposite-order 1,000-frame Exact `current --threads 20` pairs matched all
nine compatibility surfaces: luma, chroma, JSON, stdout, normalized stderr and
logs, ordered `fileLoc`, frame count, and metadata. Mean wall time moved from
39.867 to 39.515 seconds (0.88% lower), while CPU time moved from 315.260 to
305.969 seconds (2.95% lower). All three wall-time pair directions favored the
candidate (-2.10%, -0.20%, and -0.35%). Average active cores moved from 7.91 to
7.74; the change is a local compute reduction rather than a multicore-scaling
claim. Peak working set remained bounded, with observed maxima of 650.6 MiB for
the baseline and 396.2 MiB for the candidate, and no OOM.

### Managed AVX/AVX2 radix-11 PocketFFT butterflies

The managed float32 PocketFFT radix-11 pass now evaluates four contiguous
frequency indices with AVX. When radix 11 is the final stage, AVX2 gathers four
independent packets and writes their outputs contiguously. Every lane preserves
the scalar pair, coefficient multiply, add/subtract, complex twiddle, and
conversion order. Finite inputs above a conservative overflow bound, non-finite
inputs, scalar tails, and hosts without the required ISA execute the original
scalar path. No FMA, reduction, reassociation, shared state, or new sample-sized
allocation was introduced; unsafe code is limited to pinned packet gather/store.

All 61 mixed-radix compatibility tests pass with normal intrinsics, AVX2
disabled, AVX disabled, and all hardware intrinsics disabled. The dedicated
direct-plan test covers lengths 55, 121, and 363 in both directions, including
signed zero, subnormal, minimum-normal, maximum-finite, infinity, and distinct
NaN payload patterns. Two independent 10-pair length-363 kernel processes kept
identical output bits and moved the median-of-medians from 103.415 to 34.925 ms
(66.23% lower). This is isolated-kernel evidence, not an end-to-end claim.

Three opposite-order 1,000-frame Exact `current --threads 20` pairs against
main `dbc617e` matched all nine compatibility surfaces and favored the candidate
3/3. Mean wall time moved from 37.328 to 36.734 seconds (1.59% lower, 1.016x
throughput), process CPU time from 295.823 to 293.208 seconds (0.88% lower), and
average active cores from 7.925 to 7.982. A separate 2,000-frame candidate run
completed all 4,000 fields with 4,000 unique ordered `fileLoc` values in 70.139
seconds. Peak working set was 647.43 MiB, final working set was 604.34 MiB, the
three working-set means were 590.37/598.75/601.05 MiB, and the post-20% linear
slope was -24.81 MiB/min; no progressive growth or OOM was observed.

### Previous managed AVX radix-3 PocketFFT butterflies

The managed float32 PocketFFT radix-3 pass now evaluates four independent
complex indices in one `Vector256<float>`. Each lane retains the scalar pair,
coefficient multiply, add/subtract, and complex-twiddle order. Packets containing
non-finite values or magnitudes above a conservative overflow bound execute the
four original scalar indices; tails and AVX-disabled hosts also retain scalar
execution. The vector path introduces no FMA, reduction, reassociation, shared
scratch state, allocation, or cross-transform state.

All 60 mixed-radix compatibility tests pass with normal intrinsics, AVX disabled,
and all hardware intrinsics disabled. The dedicated direct-plan test covers
lengths 33, 726, and 990 in both directions. It separately exercises AVX-safe
signed zero, subnormal, minimum-normal, and signed-one patterns plus
scalar-fallback maximum finite, infinity, and distinct NaN payloads. Optimized
JIT disassembly contains only separate `vmulps`, `vaddps`, and `vsubps`
arithmetic for the butterfly and twiddles, with no FMA instruction.

Six opposite-order kernel pairs moved the mean length-726 transform from 712.391
to 705.693 ms (0.94% lower) and the mean length-990 transform from 740.479 to
709.507 ms (4.18% lower), with identical output hashes. Three opposite-order
1,000-frame Exact `current --threads 20` pairs matched all nine compatibility
surfaces. Mean wall time moved from 37.288 to 36.909 seconds (1.02% lower), CPU
time from 301.984 to 292.766 seconds (3.05% lower), and average active cores from
8.10 to 7.93. Pair wall directions were -0.79%, +0.21%, and -2.46%; the bounded
maximum working sets were 443.4 MiB and 355.6 MiB for baseline and candidate. Three
`--threads 0` pairs also matched all surfaces and moved mean wall time from
41.030 to 40.219 seconds. The v0.4.0 and IPP-fast profiles do not call this
managed current-profile FFT path.

### Previous managed AVX radix-5 PocketFFT butterflies

The managed float32 PocketFFT radix-5 pass now evaluates four independent
complex indices in one `Vector256<float>`. Each lane retains the scalar add,
subtract, coefficient multiply, complex twiddle, and conversion order. Packets
containing non-finite values or magnitudes above the conservative overflow bound
use four scalar indices. Tails and hosts without AVX also retain the scalar
path. There is no FMA, reduction, reassociation, shared scratch state, new
allocation, or cross-transform state.

All 59 mixed-radix compatibility tests passed with normal intrinsics, AVX
disabled, and all hardware intrinsics disabled. The dedicated scalar-reference
test uses a direct length-55 plan and separately covers AVX-safe signed zero,
subnormal, minimum-normal, and signed-one inputs plus scalar-fallback maximum
finite, infinity, and distinct NaN payloads in forward and backward transforms.
The existing double radix-8 AVX path also preflights its input against a
plan-specific overflow bound and falls back for extreme/non-finite values; four
storage/overlap tests cover its vector and fallback paths in all three intrinsic
modes. JIT disassembly of the radix-5 storage butterfly contained four
`vmulps`, four `vaddps`, one `vsubps`, and no FMA instruction.

A 128-iteration production-size forward/back kernel loop moved from 757.745 to
600.026 ms (20.8% lower wall time) and from 765.625 to 671.875 ms CPU (12.2%
lower), with identical hashes and no GC. Two opposite-order 1,000-frame Exact
`current --threads 20` pairs matched all nine compatibility surfaces. Mean wall
time moved from 38.483 to 37.800 seconds (1.78% lower), CPU time was effectively
flat at 308.664 versus 308.188 seconds, average active cores moved from 8.02 to
8.15, and average peak working set moved from 361.6 to 353.1 MiB. The two pair
directions were -0.54% and -3.01% wall time, so the conservative end-to-end
claim is the aggregate 1.78% result. Memory remained bounded without OOM.

### Previous managed AVX radix-8 PocketFFT butterflies

The managed float32 PocketFFT radix-8 pass now evaluates four independent
complex indices in one `Vector256<float>`. Each lane retains the scalar add,
subtract, multiply, rotate, and twiddle order. Tails and hosts without AVX use
the unchanged scalar loop. The vector path has no FMA, reduction,
reassociation, shared scratch state, allocation, or cross-transform state.

All 58 mixed-radix compatibility tests passed with normal intrinsics, AVX
disabled, and all hardware intrinsics disabled. A new frozen scalar-oracle test
uses a 3,840-value transform and covers positive/negative zero, the smallest
positive/negative subnormal and normal values, positive/negative one third,
and positive/negative one. Forward and backward SHA-256 values match in every
intrinsic mode.

Two opposite-order 1,000-frame Exact `current --threads 20` pairs moved combined
mean wall time from 36.800 to 36.149 seconds (1.77% lower) and CPU time from
308.508 to 298.828 seconds (3.14% lower). Two interleaved 160-frame Exact pairs
also favored the candidate; two IPP-fast pairs were wall-time neutral. Opposite-
order `--threads 0` and `--threads 1` checks were output-exact, with the combined
single-worker CPU result effectively flat, so no serial speedup is claimed.

Every one of the 30 refreshed matrix runs matched luma, chroma, raw JSON,
stdout, normalized stderr/logs, and ordered `fileLoc` within each backend across
default, 1, 5, 10, and 20 workers. The candidate's 1,000-frame peaks were
352.1 and 450.5 MiB versus 370.6 and 370.2 MiB for baseline. The matrix maximum
was 817.0 MiB in the single-worker case for both revisions, with no progressive
growth or OOM.

### AVX current chroma ACC segment scaling

The `current` VHS automatic chroma gain path now scales four independent samples
with managed AVX. The gain recurrence remains scalar and advances in the exact
original order before each four-value vector is built. Each finite group keeps
the original double-to-float32 conversion, double multiply, and result-to-float32
conversion points. Non-finite groups and hosts without AVX execute the original
scalar expressions. There is no FMA, reduction, reassociation, state-boundary
change, new array, or additional pooled lifetime.

The 15 focused xUnit v3 tests passed with normal intrinsics, AVX disabled, and
all hardware intrinsics disabled. The bit-pattern test covers vector tails,
signed zero, subnormals, maximum finite values, infinities, and distinct NaN
payloads. JIT inspection confirmed scalar gain additions, AVX conversion and
multiply instructions, and no FMA instruction.

Five interleaved 1,000-frame Exact `current --threads 20` pairs across two
independent sessions favored the candidate in four pairs. The two-pair session
moved median wall time from 34.782 to 34.446 seconds (0.97% lower) and CPU time
from 295.266 to 291.602 seconds (1.24% lower). The three-pair session moved wall
time from 35.255 to 35.052 seconds (0.57% lower); its CPU median was noisy and
2.20% higher, so no CPU-efficiency claim is made for Exact. Two IPP-fast pairs
moved wall time from 31.764 to 31.247 seconds (1.63% lower) and CPU time from
202.539 to 195.578 seconds (3.44% lower).

All paired runs matched exit status, luma, chroma, raw JSON, stdout, normalized
stderr/logs, and ordered `fileLoc`. Additional gates covered `--threads 1`,
explicit zero, default five, and `--threads 20` for both Exact and IPP-fast.
Candidate and baseline memory peaks both remained inside the existing roughly
715 MiB observed process window, with no progressive growth or OOM.

### Worker-local current VHS radix scans

The three-stage `current` VHS level quantile path now scans each worker's private
radix histogram through fixed source and histogram pointers. Worker partitions,
sortable-prefix conversion, exceptional-value fallback, bucket selection, and
integer increments are unchanged; the candidate removes only repeated managed
array bounds checks and address calculations.

Six long-process microbenchmark pairs over 1,048,576 values and four workers
moved the median scan from 1.2953 to 1.1646 ms (10.1% lower), with the same
result bits. Four interleaved 320-frame Exact `current --threads 20` pairs all
favored the candidate: median wall time moved from 16.4905 to 16.2279 seconds
(1.59% lower), CPU time from 132.99 to 127.28 seconds (4.29% lower), and median
peak working set from 403.6 to 401.4 MiB. Two order-reversed 1,000-frame pairs
also both favored the candidate: wall time moved from 40.321 to 39.842 seconds
(1.19% lower), CPU time from 325.28 to 317.73 seconds (2.32% lower), and candidate
peak working set remained bounded at about 406 MiB. A default-five 320-frame
pair was wall-neutral at 31.483/31.506 seconds while CPU time fell 2.50%.

All paired runs matched exit status, luma, chroma, raw JSON, stdout, normalized
stderr/logs, and ordered `fileLoc`. All 34 focused xUnit v3 tests passed with
hardware intrinsics enabled. With intrinsics disabled, 33 passed and the one
AVX-specific edge-scan test was skipped by design. Baseline/candidate checks covered `--threads 0`,
default-five, and `--threads 20`; repeated high-worker runs were deterministic
within that mode. Different requested worker modes are not treated as one shared
hash oracle.

### Direct segmented VHS envelope analysis

At 20 or more requested workers, staged VHS payload assembly no longer copies
the complete RF Envelope into its span workspace before tape dropout analysis.
The materializer instead keeps the already retained decoder blocks alive and
reads their envelope segments directly. Mean calculation retains the existing
128-sample recursive split, eight-lane float32 leaf sums, cast points, and
addition order. Dropout scanning retains index order, thresholds, hysteresis,
merge distance, minimum length, and final range order. Lower worker counts keep
the original contiguous copy and scan. Explicit full materialization remains
available for existing callers.
A shared atomic idle/ordinary/staged state prevents an ordinary cache operation
and staged acquisition from entering together. Ordinary reads and explicit
cache invalidation are rejected until that staged lease is disposed, preventing
cache eviction from returning a referenced pooled block while segmented
analysis still owns its arrays.

The high-worker test crosses block boundaries, leaves the destination Envelope
uninitialized during payload assembly, compares exact float bits for the mean,
checks multiple scan windows and thresholds against the contiguous detector,
and then verifies deferred full materialization. It also verifies that ordinary
reads and cache invalidation are rejected while the lease is active and resume
after disposal. A blocking-loader regression starts an ordinary read first,
then verifies staged acquisition is rejected and loader concurrency remains one
until the ordinary operation finishes. The lower-bound test uses one worker below the gate and verifies
the original contiguous behavior. The full
1,425-test suite passed, as did the 13 reduction and 34 block-cache tests with
all .NET hardware intrinsics disabled.

Two order-reversed 1,000-frame Exact `current --threads 20` pairs produced 2,000
ordered fields each and matched exit status, luma, chroma, raw JSON, stdout,
normalized stderr/logs, and ordered `fileLoc`. Median wall time moved from
43.001 to 42.880 seconds, CPU time from 342.50 to 324.50 seconds (5.26% lower),
and peak working set from 753.0 to 721.2 MiB. Both paired wall changes were
negative, but only a 0.28% median wall improvement is claimed. Two 160-frame
v0.4.0 pairs moved wall median from 10.893 to 10.670 seconds (2.05% lower), CPU
from 78.44 to 77.49 seconds, and peak working set from 795.1 to 752.9 MiB.

Read-only local review found the cache-ownership race before publication. Four
opposite-order 1,000-frame pre/post-gate pairs matched all nine compatibility
surfaces. Baseline/candidate wall medians were 39.770/39.932 seconds; paired
wall changes were +0.94%, +1.15%, -1.04%, and -0.03%, splitting two wins each.
Median CPU time moved from 320.23 to 314.67 seconds (1.74% lower), and median
peak working set from 405.25 to 399.84 MiB. The final atomic gate is therefore
classified as performance-neutral rather than a wall-time speedup or regression.

A separate release-like four-pair 160-frame Exact `current` A/B split two wins
each. Median wall time was 7.691/7.909 seconds and CPU time 67.50/68.02 seconds
for baseline/candidate, with wide contradictory paired changes; it is classified
as inconclusive rather than a short-window speedup or regression. The refreshed
public matrix reports its measured Exact `current --threads 20` median without
using that cross-batch cell as causal evidence. All 60 .NET matrix runs and all
15 Python PR341 runs produced one hash set per profile; Python v0.4.0 produced
15 distinct luma, chroma, JSON, and normalized-log hashes in 15 runs, so the
strict oracle remains `g4315520 --threads 0`.

### Direct-output managed double PocketFFT

The managed double-precision complex FFT plan now selects caller output or its
worker-local scratch buffer as the initial source from the factor-pass parity.
Every pass still alternates the same two spans, but the final pass always lands
in caller output. This removes the former final full-array copy and the second
worker-local `Value[]` buffer. Factorization, twiddles, radix order, data type,
normalization, and every floating-point expression remain unchanged; no FMA or
reassociation was introduced. Complex input/output overlap continues to use
memmove-equivalent span copy semantics. Real input storage that overlaps complex
output uses the old two-buffer result path and copies only after all input has
been consumed.

Three standard xUnit v3 tests freeze forward, inverse, and real-forward hashes at
lengths 2, 4, 8, 64, 512, 32768, and 131072 and cover exact plus forward/backward
shifted overlap at lengths 8, 64, and 512. Their special-value path-consistency
case compares signed zero, subnormal, maximum finite, infinity, and distinct NaN
payloads bit for bit across repeated, caller-output, and in-place storage paths;
the real case also crosses the guarded two-buffer overlap fallback. Because NaN
payload selection can vary between CPUs, this is intentionally not a
machine-specific absolute special-value oracle. The class passes on native
hardware, with AVX disabled, and with every .NET hardware intrinsic disabled.

Ten paired product-shape microbenchmarks at the default 32768-sample RF block
moved the real-forward plus complex-inverse median from 1768.90 to 1515.88 ms
(14.30% lower, 1.167x throughput), with ten candidate wins and identical hashes.
The later overlap-aware final build measured 1498.46, 1516.92, and 1560.97 ms,
bracketing that candidate median and confirming that the normal non-overlap hot
path retained the gain.

Four final 160-frame Exact `current` 5-worker pairs matched exit status, field
count, luma, chroma, raw JSON, stdout, normalized stderr/log, and ordered
`fileLoc`. The candidate won three pairs. Median wall time moved from 20.72 to
20.36 seconds (1.73% lower), process CPU time from 84.20 to 83.51 seconds (0.83%
lower), and median peak working set from 370.5 to 364.2 MiB. This same-moment A/B
is the causal gate for the 5-worker path; lower ratios in the refreshed public
table are cross-batch denominator/timing movement, not a candidate regression.

Two order-reversed 1000-frame Exact `current --threads 20` pairs on the normal
production path produced 2000 ordered fields each. Median wall time moved from
41.10 to 40.12 seconds (2.38% lower), CPU time from 336.77 to 328.91 seconds
(2.33% lower), and peak working set from about 439.8 to 416.7 MiB. All nine
compatibility surfaces matched. The public 90-run matrix also produced one hash
set per .NET profile/mode and one per Python PR341 profile/mode. Python v0.4.0
produced a different luma, chroma, JSON, and normalized-log hash in all 15 runs;
the strict oracle therefore remains `g4315520 --threads 0`.

### Bounds-check-free managed float32 SOS state access

The generic managed Exact float32 SOS kernels retain the validated two-floats-
per-section state shape but address recursive state through a tracked span
reference. JIT disassembly confirms that the two state bounds branches are gone
from both forward and backward inner loops while scalar multiply, add, and
subtract order remains unchanged. There is no FMA, SIMD reassociation,
conversion, padding, lifetime, or ownership change.

Six interleaved 160-frame Exact `current --threads 20` pairs matched all nine
artifact, metadata, console, and normalized-log surfaces. The candidate won five
pairs. Median wall time moved from 11.499 to 11.373 seconds, 1.10% lower;
throughput rose 1.12%, and CPU time moved from 92.133 to 90.695 seconds, 1.56%
lower. Four default-five pairs moved from 21.651 to 21.555 seconds, 0.44% lower,
with three candidate wins; the 0.74% CPU increase and working-set movement were
within the short-run noise floor.
These A/B scripts omit `--start` and therefore use the CLI default `--start 0`;
their 160-frame times must not be compared directly with the frame-100 matrix.

The 1,000-frame Exact `current --threads 20` pair moved from 42.463 to 42.221
seconds, a 0.57% wall-time reduction. CPU time moved from 351.297 to 347.672
seconds, 1.03% lower, and peak working set moved from 439.2 to 437.7 MiB. This
establishes bounded behavior for this gate, not a general memory-reduction claim.
Six baseline/candidate thread-gate runs covered `--threads 0`, default-five, and
`--threads 32`; all nine surfaces matched.

An independent checked-index float32 reference test covers 5, 8, 10, 31, 32,
33, and 64 sections with a fixed input bit pattern. Its aggregate SHA-256 is
`61A8966C55B1331509E0A60D6B731FA125F9406185C2C4AAF4E4CBC42F16C80D`, and it
passes both normally and with all .NET hardware intrinsics disabled.

### Reverse-free managed float32 SOS second pass

The managed Exact float32 forward/backward SOS path previously filtered
forward, reversed the entire work span, filtered forward again, and reversed
the span back. The second pass now walks the original span from its last sample
to its first and writes each result at that same index. It therefore observes
the identical sample sequence and initial endpoint while removing two complete
buffer reversals. The one-, two-, four-, and generic-section kernels retain the
same per-sample section order, expressions, conversions, and recursive-state
updates; no FMA, SIMD reassociation, or precision change was introduced.
This earlier candidate was based on merged main `73dd014`; its measured source
blob was `c3aac76922ad525884cf26779b2035db76248547`, and its baseline blob was
`766e0926fec7c07bb2bc0a20c0c39824b8f05996`.

Twenty-one opposite-order 32K microbenchmark pairs covered two, four, and five
SOS sections. Baseline/candidate medians were 154.540/151.793 microseconds
(1.78% lower), 195.178/190.025 microseconds (2.64% lower), and
278.516/275.540 microseconds (1.07% lower), respectively. Every output SHA-256
matched. The focused 40-test DSP class passed both normally and with all .NET
hardware intrinsics disabled, for 80 standard xUnit v3 executions.

Across eight interleaved 160-frame Exact `current` pairs, the candidate won six
pairs. Paired-median wall time fell 2.43%, throughput rose 2.49%, and CPU time
fell 2.69%. A separate Exact v0.4.0 160-frame pair also matched. In every pair,
luma, chroma, raw JSON, stdout, normalized stderr/log, and ordered `fileLoc`
were identical.

The 1,000-frame Exact `current` pair moved from 39.242 to 37.908 seconds, a
3.40% wall-time reduction, while CPU time moved from 330.656 to 310.141
seconds, a 6.20% reduction. Baseline first/final-third working-set medians were
385.7/387.7 MiB with a 391.3 MiB peak; candidate medians were 380.9/384.2 MiB
with a 387.8 MiB peak. This establishes bounded behavior for this gate, not a
general memory-reduction claim. A six-run thread gate covered `--threads 0`,
default-five, `1`, `5`, `10`, and `20`; all seven compatibility surfaces were
identical across every mode.

### Preserving real-FFT first pass

The preserving real-FFT path previously copied the complete input into a pooled
packed array before the first radix pass copied and transformed it into a second
pooled array. It now rents both work arrays up front and executes that first pass
directly from the caller's read-only span. Later radix passes, packed-spectrum
materialization, binary64 expressions, and arithmetic order are unchanged. The
owned-input path is unchanged. The final packed array is returned to the pool
last to retain the previous hot-buffer reuse order.

Two opposite-order final pairs ran 20,000 warmed 32K forward-plus-inverse
iterations per process. The preserving forward average fell from 111.773 to
107.062 microseconds, a 4.22% time reduction; complete forward-plus-inverse
time fell from 250.744 to 248.181 microseconds, a 1.02% reduction. Forward and
inverse SHA-256 values were identical in every run. A focused bitwise test also
covers lengths 2, 4, 4,096, and 32,768, verifies the caller input byte-for-byte,
checks output-tail sentinels, and compares the preserving path with the existing
owned-input path.

The final v0.4.0 two-pair 160-frame A/B moved from a baseline median of 8.282 to
8.237 seconds, 0.54% lower. One final `current` 400-frame pair moved from 16.184
to 16.010 seconds, 1.08% lower. Luma, chroma, raw JSON, stdout, normalized
stderr/log, and ordered `fileLoc` matched in every pair. A separate 1,000-frame
`current` run matched the same seven surfaces and remained bounded: baseline
first/final-third working-set medians were 649.1/643.9 MiB with a 681.9 MiB
peak; candidate medians were 644.3/641.5 MiB with a 686.2 MiB peak. This is not
a general memory-reduction claim.

The 12-run determinism matrix covered Exact v0.4.0 and `current` at
`--threads 0`, default-five, `1`, `5`, `10`, and `20`; every surface was stable
within each profile. Four fixed-hash tests also passed with AVX and AVX2
disabled. The full Release suite passed all 1,416 xUnit v3 tests.

### Fused Exact VHS analytic-spectrum traversal

The Exact VHS analytic path previously traversed its full complex spectrum once
for the RF video filter, once for the MTF filter, and once for the real Hilbert
scale. It now performs those three ordered stages in one traversal. Both complex
multiplies retain the existing binary64 FMA expressions and rounding points,
followed by the same real multiply. Non-finite pairs use the old scalar sequence,
and aliased spans fall back to the original three-pass implementation.

Twelve alternating pairs over 1,048,576 elements reduced the warmed kernel
median from 4.280 to 2.976 ms, a 30.47% time reduction or 1.438x throughput,
with bit-identical output and zero steady-state allocation. Four 400-frame
interleaved pairs matched every captured surface and reduced end-to-end median
wall time from 16.50 to 16.33 seconds, about 1.0%.
After the alias-guard review, one final-source 12-pair recheck measured 4.246
versus 2.833 ms (1.4984x). It confirms the normal non-alias kernel remained
intact; it does not replace the pinned full-batch measurements above.

The long compatibility observation used one baseline/candidate 1,000-frame pair
for each Exact behavior profile at `--threads 20`. `current` measured 38.198
versus 37.951 seconds (0.65% lower); v0.4.0 measured 48.146 versus 47.135 seconds
(2.10% lower). These are single-pair observations, not sustained throughput
claims. Candidate working-set medians for the first/final thirds were
462.4/462.9 MiB for `current` and 434.5/437.5 MiB for v0.4.0: bounded and nearly
stable, with small measured increases. CPU and peak working set varied by run,
so no general CPU or memory-reduction claim is made.

Luma, chroma, raw JSON, stdout, normalized stderr/log, and ordered `fileLoc`
matched in every A/B. A 12-run candidate matrix covered both Exact profiles and
`--threads 0`, default-five, `1`, `5`, `10`, and `20`; two more runs disabled
all hardware intrinsics and matched the normal `--threads 1` artifacts. All 60
.NET runs in the refreshed six-path table also retained one hash per captured
surface and profile/thread group. The Release build passed all 1,416 standard
xUnit v3 tests; the focused 28-test class passed again in a fully scalar process.

### Direct managed complex real-input staging

The caller-buffer managed complex FFT previously filled a complete `Complex`
destination from real doubles and then copied that destination into the plan's
thread-local working values. The real-input entry point now writes those doubles
directly into the same working values before calling the unchanged FFT execute
path. Factorization, butterfly arithmetic and order, normalization, scratch
selection, and the final `Complex` output conversion are unchanged.

Eight opposite-order microbenchmark pairs ran 512 warmed 32K transforms per
measurement with zero steady-state allocation. The baseline median was
437.804 us per call and the candidate median was 424.065 us, a 3.14% reduction;
the checksum and sampled output bits were identical. Four 200-frame interleaved
pairs matched every captured surface and reduced median wall time from 9.93 to
9.52 seconds, but the longer gate is used for the causal claim.

Two opposite-order 1,000-frame Exact `current --threads 20` pairs produced 2,000
ordered fields per run. Median wall time fell from 40.030 to 39.448 seconds
(1.45%), and median CPU time fell from 323.492 to 317.922 seconds (1.72%). Median
sampled peak working set was 444.3 MiB for the baseline and 406.0 MiB for the
candidate; individual peaks varied with process placement, so this is treated as
bounded-memory evidence rather than a general memory-reduction claim.

Luma, chroma, raw JSON, stdout, normalized stderr/log, and ordered `fileLoc`
matched in every short and long run. A separate 24-run gate covered Exact and
IPP-fast, both behavior profiles, and `--threads 0`, default-five, and
`--threads 20`; four more runs covered default, no-FMA, no-AVX, and fully scalar
hardware-intrinsic settings. All surfaces matched. The native bridge smoke used
only system DLL dependencies, and all 1,403 standard xUnit v3 tests passed.

### Owned managed real-FFT input

Compact VHS blocks do not expose raw demodulation output. After sharpness,
chroma-trap, and optional video clipping have selected the final source, the
managed Exact real FFT now consumes that exclusively owned array in place. The
arithmetic, packing, spectrum write order, filters, and inverse transforms are
unchanged. Diagnostic/raw-output calls retain the copying API, and IPP-fast
still invokes the same native forward FFT.

Twelve alternating microbenchmark pairs transformed 512 independent 32K input
arrays exactly once per measured batch. The owned path won 11 pairs and reduced
the median from 54.953 to 53.732 ms (2.22%). Four 160-frame end-to-end pairs were
throughput-neutral. The sustained gate used two opposite-order 1,000-frame
Exact `current --threads 20` pairs: combined wall time fell from 79.259 to
78.562 seconds (0.88% lower, 1.0089x throughput), with both candidate runs
winning. Combined CPU time rose 0.63%, so this is not a CPU-efficiency claim.

All four long runs produced 2,000 ordered fields and identical luma, chroma,
raw JSON, stdout, normalized stderr/log, and ordered `fileLoc` surfaces. The
candidate's first/final-third working-set medians were 441.6/443.7 MiB and
579.7/563.4 MiB in the two runs. Peaks varied substantially with process
placement, so no memory-reduction claim is made; neither run showed progressive
growth. A separate 24-run gate covered Exact/IPP-fast, both behavior profiles,
and `--threads 0`, default-five, and `--threads 20`, with every captured surface
matching the baseline and remaining deterministic across thread settings.
The native bridge smoke passed with only system DLL dependencies, and all 1,402
xUnit v3 tests passed with the IPP cases executing rather than skipping.

### Managed AVX real radix-4 FFT

The Exact real-FFT forward and backward radix-4 stages now process two
independent complex points per AVX block. Each lane retains the scalar
multiply, add, and subtract order; the implementation does not use FMA, and a
scalar tail plus the original no-AVX path remain in place. Ninety-six
forward/inverse bit-pattern cases spanning lengths 4 through 131,072 matched
the baseline byte for byte. JIT disassembly contained separate `vmulpd`,
`vaddpd`, and `vsubpd` operations and no fused multiply-add instruction.

Six opposite-order microbenchmark pairs over 3,000 forward/inverse transforms
at length 131,072 reduced median wall time from 3.7935 to 3.6182 seconds (4.62%
lower, 1.0485x throughput). Four 160-frame Exact `current --threads 20` pairs
reduced median wall time by 4.27% and CPU time by 12.49%; the equivalent
v0.4.0 and IPP-fast checks were neutral to slightly faster. Every run matched
all nine captured compatibility surfaces. The independent 30-run matrix above
is used for public absolute timings rather than substituting the causal A/B
percentage into every cell.

The two opposite-order 1,000-frame pairs were much closer to neutral: combined
wall time fell from 80.222 to 79.549 seconds (0.84%), while combined CPU time
rose from 651.172 to 658.203 seconds (1.08%). The 160-frame result is therefore
short-window hotspot evidence, not a claim that sustained throughput improves
by the same percentage.

A 1,000-frame sampled run completed 2,000 fields with identical luma, chroma,
and JSON hashes. Peak working set was 415.3 MiB; the final-third median was
409.9 MiB, only 2.3 MiB above the first-third median, so the optimization did
not introduce progressive memory growth. The standard xUnit v3 suite contains
1,401 passing tests, including four pinned radix-4 hashes that also pass with
AVX disabled.

The preceding 40-frame table made fixed startup cost, especially Python's,
disproportionately large. For example, the default IPP-fast `current` ratio fell
from 6.351x to 5.450x when the window grew to 160 frames: Python grew only from
19.791 to 53.288 seconds while .NET grew from 3.116 to 9.778 seconds. This is
startup-cost dilution, not evidence that the candidate regressed. The older
NTSC Betamax HiFi table used another private fixture and is not comparable at
all. Future refreshes retain this 160-frame, three-pass method; causal claims
still require matched longer A/B runs.

All 60 .NET matrix runs were deterministic on every captured surface. All 15
merged-Python-PR341 runs were also deterministic. Python v0.4.0 produced 15
distinct luma, chroma, raw JSON, and normalized-log hashes in 15
runs, while stdout, normalized stderr, and ordered `fileLoc` remained stable.
The strict oracle therefore remains Python v0.4.0 `g4315520 --threads 0`, not an
arbitrary multi-worker run.

### Split-input VHS Rust phase-difference AVX

The caller-buffer real/imaginary VHS Rust FM path now evaluates eight finite
float32 lanes per AVX block. The double-to-float conversion, signed-minimum
adjustment, atan polynomial multiply/add order, floor-based tau wrapping,
frequency scaling, and float-to-double output conversion are unchanged. A block
with any non-finite lane takes the scalar fallback. With AVX available, the
existing four-lane loop handles remainders; hosts without AVX use the scalar
path. Interleaved
`Complex` paths deliberately remain four-wide: an otherwise identical
eight-lane experiment reduced CPU time but regressed Exact `current --threads
20` wall-time median by 6.83%.

With tiered compilation disabled, six alternating-process microbenchmark pairs
over 32,768 samples and 30,000 calls reduced the split-kernel median from 1.675
to 1.131 seconds (32.5% lower, 1.481x throughput) with the same checksum. A
final split-only six-pair 160-frame IPP-fast `current --threads 20` check was
near-neutral: the candidate won four pairs, reduced the conventional median
from 6.541 to 6.504 seconds (0.57%), and increased median CPU time by 0.89%.

The more stable two opposite-order 1,000-frame pairs reduced combined wall time
from 71.280 to 70.645 seconds (0.89% lower, 1.0090x throughput), CPU time from
416.906 to 415.891 seconds (0.24% lower), and allocation from 1.326 to 1.319
GiB. Maximum sampled working set was 383.5 MiB versus 384.6 MiB on main, and
first/final-third samples showed no progressive growth. The 24-run Exact and
IPP-fast thread gate covered both behavior profiles at `--threads 0`, default-5,
and `--threads 20`; every captured luma, chroma, raw JSON, stdout, normalized
stderr/log, and ordered `fileLoc` surface matched. The 13 focused xUnit v3 cases
passed with native hardware, AVX2 disabled, and all hardware intrinsics disabled;
the complete 1,385-test suite and zero-warning Release build also passed.

### Compact Exact VSync radix staging

The Exact `current` parallel VSync quantile selector now resolves the same
32-bit sortable prefix through 11+11+10-bit radix stages instead of two 16-bit
stages. The final candidate prefix, source-order filter, Quickselect input,
floating-point expressions, and detector state are unchanged. The sequential
path is unchanged as well. Worker-private maximum histogram storage falls from
512 KiB to 16 KiB, or from 2 MiB to 64 KiB at the detector's four-worker cap.

IPP-fast explicitly keeps the previous 16+16-bit route. Applying the compact
route to both backends initially made the four-pair IPP gate 1.6% slower in wall
time and 1.8% higher in CPU time. After routing only Exact through the compact
stages, the final four-pair IPP check was neutral: wall time was 1.31% lower and
CPU time about 0.4% higher, with exact baseline/candidate artifacts within the
IPP contract.

In the fixed production-size selection probe, eight of eight paired batches
favored the compact form. Median wall time fell from 0.966511 to 0.891624
seconds (7.75% lower, about 1.084x throughput), with checksum
`000000002704317B` unchanged. This is hotspot evidence, not an end-to-end claim.
Four interleaved 160-frame Exact pairs reduced balanced total wall time from
34.113 to 33.114 seconds (2.93%) and CPU time from 303.11 to 278.11 seconds
(8.25%); two of four individual pairs won, so the short result remains noisy.

The causal end-to-end gate was two opposite-order 1,000-frame Exact `current
--threads 20` pairs. Combined wall time fell from 82.260 to 80.387 seconds
(2.28% lower, 1.0233x throughput), and CPU time fell from 644.594 to 640.391
seconds (0.65%). Candidate working-set samples stayed bounded within each run;
placement variability moved observed peaks between 387.5 and 418.7 MiB, with no
progressive growth or OOM behavior.

The final strict gate comprised 44 baseline/candidate runs: eight short
interleaved pairs, two opposite-order long pairs, and 24 Exact/IPP-fast thread
runs spanning both behavior profiles at `--threads 0`, default-5, and
`--threads 20`. Luma, chroma, raw JSON, ordered `fileLoc`, stdout, normalized
stderr, and normalized logs matched throughout. The focused detector suite
passed 25/25 tests; all 1,382 xUnit v3 tests passed with native hardware, AVX2
disabled, and all hardware intrinsics disabled. The Release solution build
completed with zero warnings and zero errors.

### Contiguous AVX2 VSync radix histogram merge

This previously merged candidate was based on main `845d8d1`; its executable
had SHA-256
`7BAC056495F42BF4327F6E9D99AF4168F0FA585319BC9CF9F9D09E84B4A3E632`.
The `current` VSync quantile path now merges each worker-private radix histogram
as one contiguous span. The first worker is copied into the destination and
later workers are added in the original worker order; AVX2 handles eight exact
integer buckets at a time, with the same scalar order for the tail and for CPUs
without AVX2. No floating-point expression, quantile target, detector state, or
ordered commit boundary changes.

In the isolated production-size radix probe, the cache-local merge beat the old
bucket-major merge in all eight pairs, reducing median wall time from 1.169541
to 1.034975 seconds (11.51%). Adding the explicit AVX2 integer loop then beat
the cache-local scalar form in all eight pairs, reducing 1.043777 to 0.971152
seconds (6.96%); checksums remained exact. These kernel results identify the
hotspot but are not presented as end-to-end speedups.

The stable end-to-end result is two opposite-order 1,000-frame IPP-fast
`current --threads 20` pairs. Combined wall time fell from 72.913607 to
71.789921 seconds (1.54% lower, 1.0157x throughput), and CPU time fell from
426.328125 to 422.593750 seconds (0.88%). Peak working set stayed near 373 MiB,
and first-/last-third samples showed no progressive growth. All four long runs
produced one luma, chroma, raw JSON, ordered `fileLoc`, normalized stderr, and
normalized-log hash set.

The complete strict gate comprised 48 baseline/candidate runs: ten short
interleaved pairs, two opposite-order long pairs, and 24 Exact/IPP-fast thread
matrix runs spanning both behavior profiles at `--threads 0`, default-5, and
`--threads 20`. Luma, chroma, raw JSON, ordered `fileLoc`, normalized stderr,
and normalized logs matched throughout; stdout also matched in the short and
thread-matrix gates where it was captured. The 30 refreshed `current` matrix
runs were cross-thread deterministic. All 1,382 xUnit v3 tests passed with
native hardware, AVX2 disabled, and all hardware intrinsics disabled.

### Deterministic PAL VSync median selection

This historical candidate was based on merged main `ae3722d`; its executable
had SHA-256
`AAAB4B0A884D0F22B361E369A55A2C475DD2D042806043B25EAFB5DF188B7860`.
`NumpyReduction` now sends arrays of 4,096 or more values through its existing
deterministic introselect instead of waiting until 32,768 values. This avoids
full sorting for the roughly 6K- and 15K-sample PAL VSync MAD groups. Inputs
below the threshold and arrays containing both positive and negative zero keep
full sorting. NaNs are still returned before selection, and even-length inputs
still select the upper middle value, scan the lower partition for its maximum,
and evaluate `(lower + upper) / 2.0` in the same order. Focused tests cover
4,095, 4,096, and 4,097 values through both allocation and caller-scratch APIs.

Four interleaved baseline/candidate pairs on the fixed 160-frame IPP-fast
`current --threads 20` window reduced median wall time from 7.253 to 6.542
seconds (9.80% lower, 10.87% more throughput); the candidate won all four
pairs. Median CPU time was effectively flat at 39.453 versus 39.469 seconds.
The fixed 1,000-frame gate moved from 38.478 to 35.487 seconds (7.77% lower),
with CPU time falling from 215.016 to 212.422 seconds (1.21%) and peak working
set remaining bounded at 368.3 versus 368.1 MiB. Candidate first-/last-third
working-set medians were 361.6/366.3 MiB, with no progressive growth.

Exact and IPP-fast baseline/candidate matrices covered both compatibility
profiles at `--threads 0`, default-5, and `--threads 20`. Across those 24 runs,
the eight short A/B runs, and the two long-gate runs, luma, chroma, raw JSON,
ordered `fileLoc`, stdout, normalized stderr, and normalized logs all matched.
The 60 .NET performance-matrix runs retained one hash per profile across every
thread mode. Merged Python PR341 was deterministic here; Python v0.4.0 produced
15 distinct luma, chroma, and JSON hashes in 15 runs, including 12 distinct
sets in the 12 explicit non-default-worker runs. The strict oracle therefore
remains Python v0.4.0 `g4315520 --threads 0`.

The private NTSC VHS fixture previously used for the 1,000-frame
`--fallback_vsync` gate was unavailable during this audit. That gate was not
rerun, and no current fallback claim is inferred from its older evidence.

### Oversized raw-FLAC mapped seeking

The raw-FLAC parser now admits mapped libsndfile reads only for 40 MHz mono
PCM16 streams that exceed the signed 32-bit sample range, have a complete
metadata chain, use one fixed block size and fixed blocking strategy, have a
matching first-frame header and CRC-8, and have no seektable. The mapper
reproduces the first-frame PTS and RF-position rounding used by the pinned
FFmpeg/PyAV path with integer arithmetic. It is enabled only for ordinary
parallel VHS decode. `--threads 0/1`, debug plots, GNU Radio AFE input, nonzero
`--sharpness`, resampling, variable-block streams, streams with a seektable,
and all unrecognized layouts keep FFmpeg. Mapping, seek, read, or
length-boundary failure activates a one-way FFmpeg fallback at the same logical
sample. Direct probes at the beginning, middle, signed 32-bit boundary, and
near EOF matched both FFmpeg and the reference FLAC decoder.

Thirty-six interleaved baseline/candidate real-RF runs matched luma, chroma,
raw JSON, ordered `fileLoc`, stdout, timing-normalized stderr, and
timestamp-normalized logs. Exact `current --threads 20` moved from 14.876 to
11.135 seconds over 200 frames (25.15% lower); effective cores rose from 6.99
to 8.06 and peak working set fell from 803.4 to 407.0 MiB. Two reverse-order
1,000-frame pairs moved from 53.230 to 44.055 seconds (17.24% lower), effective
cores from 6.76 to 7.41, and peak working set from 773.2 to 402.4 MiB. At 32
workers, 100 frames moved from 10.030 to 6.679 seconds (33.41% lower), with
effective cores rising from 6.80 to 8.88. The unchanged single-worker route
was neutral at 13.901 versus 13.877 seconds.

Separate 200-frame gates improved Exact v0.4.0 by 22.24% and IPP-fast
`current` by 24.64%. This mapped-seeking change shipped in
`v0.4.0-1.5.3`; none of these historical timings were carried into the latest
table above.

### Managed current CTI quotient and finish AVX

The existing eight-lane AVX/FMA CTI distance path now carries the same lanes
through pinned reciprocal refinement, threshold gating, lower/upper weighting,
target-delta construction, and rounded output. Every lane retains the original
float subtraction and FMA sequence, float-to-double conversion points,
double-precision weighting and FMA sequence, and final float rounding before
storage as double. The scalar tail and hosts without AVX, FMA, or SSE4.1 retain
the preceding path. JIT disassembly confirms the two four-lane finish groups
are inlined into the hot loop as `vfmadd*`, `vblendv*`, and conversion
instructions rather than eight scalar `FinishSample` calls.

Eight interleaved production-size kernel pairs retained one SHA-256 and moved
median wall time from 416.746 to 336.922 ms (19.16% lower); median process CPU
time fell from 2578.125 to 1742.188 ms (32.42%). Six 160-frame Exact
`current --threads 20` pairs retained every compared surface and moved median
wall time from 14.08 to 13.78 seconds (2.1% lower), with five candidate wins.
Two reverse-order 1,000-frame Exact pairs moved the combined median from 54.42
to 52.76 seconds (3.05% lower) and CPU time from 377.59 to 373.18 seconds;
effective cores rose from 6.94 to 7.07. Candidate sampled peak working set had
a 17.3 MiB higher median and stayed below 799 MiB without progressive growth.

Six matching 160-frame IPP-fast pairs split three wins each. Their paired mean
wall-time change was +0.13%, so the IPP path is classified as throughput-neutral
and no causal IPP speedup is claimed. The newer IPP cells in the table are a
fresh current-build snapshot, not an attribution of their full difference from
the preceding table to this patch.

Twenty-four baseline/candidate RF gates covered Exact and IPP-fast, v0.4.0 and
`current`, and `--threads 0`, default-five, and `--threads 20`. Luma, chroma,
raw JSON, stdout, timing-normalized stderr, timestamp-normalized logs, ordered
`fileLoc`, and cross-thread determinism all matched. All 1,349 xUnit v3 tests
passed; the 18 pinned CTI cases also passed with AVX disabled and with SSE4.1
disabled. The Release solution built with no warnings or errors.

### Managed radix-8 PocketFFT AVX pairs

The double-precision radix-8 kernel now evaluates two independent butterflies
per iteration with managed AVX. Each lane retains the original add, subtract,
multiply, and conversion order; the implementation uses no FMA or reassociated
reduction. Odd tails and hosts without AVX execute the original scalar path.

Two reverse-order 1,000-frame Exact pairs moved v0.4.0 from 64.106 to 63.742
seconds (0.57% lower) and CPU time from 342.547 to 336.578 seconds (1.74%
lower). `current` moved from 52.405 to 52.259 seconds (0.28% lower) while CPU
time moved from 392.438 to 374.859 seconds (4.48% lower). Candidate sampled
peak working sets stayed near 778 MiB in both profiles. The short matrix is a
current throughput snapshot; these long direct main/candidate pairs are the
causal evidence for this change.

All 1,349 xUnit v3 tests passed both normally and with AVX disabled. Twenty-four
strict baseline/candidate runs covered Exact and IPP-fast, both profiles, and
`--threads 0`, default-five, and `--threads 20`. Luma, chroma, raw JSON,
stdout, timing-normalized stderr, timestamp-normalized logs, ordered `fileLoc`,
and cross-thread determinism all matched. The 60-run matrix executable had
SHA-256 `33F39E01AD16CB2053AB6A4AF1F27064D90981AD1661F315C51B86E99E9F6E79`.

The latest compact VHS path first assembles the complete low-pass sync
reference, then materializes `Video`, `Envelope`, and `Chroma` while sync-only
field work continues. Exactly one staged span may own decoded blocks and pooled
destination buffers per stream decoder. Serial, single-block, diagnostic, and
stateful sharpness paths remain eager. The copy expressions, float32 chroma
widening point, DC-offset order, field-state boundaries, and ordered commit path
are unchanged.

Two reverse-order 1,000-frame Exact pairs moved v0.4.0 from 49.195 to 47.886
seconds (2.66% lower, 1.027x) and `current` from 43.986 to 42.862 seconds
(2.55% lower, 1.026x). Average active cores rose from 5.70 to 5.82 and from
7.38 to 7.81 respectively. Two reverse-order 600-frame IPP-fast pairs moved
v0.4.0 from 28.758 to 28.226 seconds (1.85% lower); `current` was neutral at
23.415 versus 23.422 seconds (-0.03%). Candidate peak working sets across the
long pairs stayed within the same observed 0.38-0.71 GB range as main, with one
active staged span and no retained-growth path.

Every long pair matched luma, chroma, raw JSON, stdout, timing-normalized
stderr, timestamp-normalized logs, and ordered `fileLoc`. A separate 100-frame
matrix matched the same surfaces at `--threads 0`, default-5, and
`--threads 20` for Exact/IPP-fast and both behavior profiles. All 60 refreshed
40-frame matrix runs were deterministic. Focused sequence tests additionally
cover eager versus staged `current`, fallback VSync, saved levels, clamp/DC
offset, retry ownership, and disposal.

The managed `current` CTI distance stage now evaluates eight independent
float32 lanes with AVX/FMA. Each lane preserves the original subtraction,
multiplication, fused multiply-add, square-root, threshold, reciprocal,
weighting, and write order. Vector tails and hosts without AVX/FMA use the
original scalar expressions. The 18 pinned PR341 xUnit v3 cases pass both the
hardware path and a separate AVX-disabled process. Six interleaved
production-size kernel pairs retained one SHA-256 and reduced median wall time
from 4,969.497 to 4,387.421 ms (11.71%) and CPU time from 4,812.500 to
4,289.063 ms (10.88%).

Two reverse-order 1,000-frame Exact `current --threads 20` pairs were neutral:
baseline/candidate wall medians were 50.687/50.793 s (-0.21%) and CPU medians
were 378.680/381.266 s, so no Exact end-to-end speedup is claimed. Two matching
IPP-fast pairs both favored the candidate, moving wall medians from 43.730 to
42.872 s (1.96% lower) and CPU medians from 255.273 to 251.797 s (1.36% lower).
Maximum sampled working set in those IPP pairs was 736.8/734.6 MiB. All eight
measured runs matched
luma, chroma, raw JSON, ordered `fileLoc`, stdout, normalized stderr, and
timestamp-normalized logs. The refreshed 60-run matrix was deterministic and
produced one hash set per backend/profile across default-5 and
`--threads 1/5/10/20`.

The current Super-Gaussian staging path uses AVX for the center conversion while
building reflected float32 input, for the existing IPP spectrum mask, and for
expanding float32 output to float64. Every lane retains the original conversion points and the
mask's multiply, explicit multiply-by-zero, add, and subtract order. It uses no
FMA or reassociation, and adds no allocation or retained buffer. Vector tails
and hosts without AVX use the original scalar expressions. Focused tests cover
NaN payloads, infinities, subnormals, signed zero, unaligned destinations, and
non-vector lengths against independent scalar references.

Seven alternating production-size filter microbenchmark pairs retained one
identical output SHA-256 and moved the median from 1.95 to 1.18 ms (39.5%
lower, 1.653x throughput). Three alternating fixed 200-frame
`ipp-fast + current --threads 20` pairs moved median wall time from 9.064 to
8.503 seconds (6.19% lower) and process CPU time from 48.312 to 46.609 seconds
(3.52% lower); effective core use rose from 5.33 to 5.48. Every pair matched
exit status, field count, luma, chroma, raw JSON, stdout, timing-normalized
stderr, timestamp-normalized logs, and ordered `fileLoc`. All 30 refreshed
matrix runs were deterministic within each backend and matched the preceding
audited matrix hashes. The Release solution built without warnings or errors,
and all 1,324 xUnit v3 tests passed.

The `current` VHS nine-tap sync boxcar now evaluates four adjacent output
samples per AVX vector. Each lane performs the same nine multiplications and
additions in the original ascending source order. It introduces no FMA,
reassociation, worker-cap change, allocation, or retained buffer; vector tails
and hosts without AVX use the original scalar loop. The standard xUnit v3 test
uses an independent scalar reference and checks bit patterns at lengths 9, 10,
and 10,003 with two, three, and four workers. It also covers NaN payloads,
infinities, subnormals, minimum normals, and signed zero. CI reruns all 25
focused sync tests with `DOTNET_EnableHWIntrinsic=0` to exercise the scalar
fallback in a separate process.

Five alternating fixed 40-frame `ipp-fast + current --threads 20` pairs moved
median wall time from 2.452 to 2.359 seconds (3.81% lower, 1.040x throughput)
and median process CPU time from 12.063 to 11.031 seconds (8.55% lower). The
candidate won four of five pairs, while median sampled peak working set stayed
effectively unchanged at 356.4 versus 355.6 MiB. One final 200-frame pair moved
wall time from 8.904 to 8.777 seconds (1.43% lower) and CPU time from 47.219 to
44.891 seconds (4.93% lower), with both peak working sets at 363.8 MiB. Every
pair matched exit status, field count, luma, chroma, raw JSON, stdout,
timing-normalized stderr, timestamp-normalized logs, and ordered `fileLoc`.
All 30 refreshed matrix runs produced one hash set per backend across default,
1, 5, 10, and 20 workers. The Release solution built without warnings or
errors, and all 1,319 xUnit v3 tests passed.

The preceding merged IPP inverse-staging optimization applies at more than 12
requested workers. Each pooled IPP VHS workspace owns one
private companion IPP real-FFT plan. The real RF inverse and independent
Hilbert imaginary inverse can therefore run concurrently without sharing a
stateful native plan. DSP expressions, transform inputs, output ordering, and
the 16-workspace retention limit are unchanged; lower worker counts, v0.4.0,
GNRC, and nonzero RF high boost retain serial staging. Five alternating fixed
100-frame pairs reduced mean wall time from 9.347 to 9.035 seconds (3.33% lower,
1.034x throughput), with a 4.22% paired median and four candidate wins. Mean
effective core use rose from 4.74 to 4.88. Mean peak working set rose from
402.1 to 414.7 MiB, a bounded 12.5 MiB cost for the companion plans.

At that checkpoint, all three documented 40-frame pairs were faster:
decoder-reported median time moved from 2.290 to 2.180 seconds (4.80% lower).
That historical cell was 8.438x faster than profile-matched Python PR341 and
reduced its wall time by 88.15%.
A separate no-start 1,000-frame candidate run completed in 46.359 seconds with
5.27 effective cores. Sampled working set peaked at 414.9 MiB and averaged
400.8 MiB early versus 387.9 MiB in the final quarter, showing no progressive
growth.

The preceding reduction kernel accelerates the NumPy-compatible float32 mean used in
VHS RF processing. Each leaf still uses the original 128-element pairwise
boundary, recursive split points, eight independent accumulators, and final
addition tree; AVX only converts eight float64 inputs and advances those same
eight lanes together. It uses no FMA, reassociation, allocation, or new retained
buffer. Eight alternating production-sized microbenchmark runs moved median
time from 82.23 to 42.30 ms (48.6% lower, 1.944x throughput). Three interleaved
fixed 160-frame `ipp-fast + current --threads 20` pairs moved median wall time
from 11.855 to 11.620 seconds (1.98% lower, 1.020x throughput); the candidate
won two pairs and lost one. Median process CPU time rose from 55.297 to 57.422
seconds while median effective core use rose from 4.66 to 4.94, so the wall-time
gain comes with measurably higher but useful CPU occupancy. All paired runs
matched luma, chroma, raw JSON, stdout, timing-normalized stderr,
timestamp-normalized logs, and ordered `fileLoc`.

Six candidate real-RF gates covered IPP-fast v0.4.0 and `current` at explicit
zero, default-five, and 20 workers. Within each profile, luma, chroma, raw JSON,
stdout, timing-normalized stderr, timestamp-normalized logs, and ordered
`fileLoc` all matched across worker counts. Every baseline/candidate 100-frame
and documented 40-frame pair matched those same surfaces. The Release solution
built with zero warnings/errors and all **1,319** xUnit v3 tests passed.

The preceding merged parallel `current` VHS sync kernel specializes the fixed nine-tap
boxcar. Its nine multiply-add statements preserve the previous ascending
source order and float64 conversion points; it introduces no SIMD, FMA,
reassociation, worker-cap change, or new retained buffer. Three interleaved
fixed 160-frame `ipp-fast + current --threads 20` pairs moved median wall time
from 12.667 to 12.512 seconds (1.22% lower, 1.012x throughput), and every
candidate run was faster. Median CPU time moved from 58.438 to 58.141 seconds,
while median effective core use moved from 4.61 to 4.65. All runs matched luma,
chroma, raw JSON, stdout, timing-normalized stderr, timestamp-normalized logs,
and ordered `fileLoc`. The 24-cell affected matrix refresh was deterministic
within both current backends. Exact v0.4.0 zero/default/20 baseline-candidate
gates also remained exact; current/default passed its paired gate, current/20
passed the longer A/B gate, and `--threads 0` never dispatches this kernel.
The Release solution built with zero warnings/errors and all 1,302 xUnit v3
tests passed. Three focused bit-identity cases also passed with AVX2 disabled
and with all hardware intrinsics disabled.

The preceding merged PCM16 candidate converts native libsndfile input to
`double` eight samples
at a time with AVX2. The scalar tail and non-AVX2 fallback retain the original
signed integer conversion exactly; no extra buffer, multiply, reduction, FMA,
or sample reordering is introduced. A focused xUnit v3 test covers all 65,536
PCM16 values and every vector tail; all 31 loader tests pass under native,
AVX2-disabled, and all-intrinsics-disabled execution. Eight paired
conversion-microbenchmark runs moved the median from 72.241 to 44.311 ms
(38.66% lower, 1.630x throughput).
Three interleaved fixed 100-frame `ipp-fast + current --threads 20` pairs had
baseline times of 5.02/5.00/5.01 seconds and candidate times of
4.91/5.22/4.96 seconds. The candidate won two pairs and lost one; its median
moved from 5.01 to 4.96 seconds while its arithmetic mean moved from 5.01 to
5.03 seconds. This noisy end-to-end result is classified as near-neutral, not
as a general speed claim. Luma, chroma, raw JSON, stdout, timing-normalized stderr,
timestamp-normalized logs, and ordered `fileLoc` matched in all six runs.
Twelve strict Exact baseline/candidate gates also matched both profiles at
zero, default, and 20 workers, including cross-thread determinism. At that
checkpoint, the IPP-fast/current 20-worker matrix cell was 2.378 seconds,
7.735x faster than the profile-matched Python PR341 measurement on this fixed
window.

The preceding current CTI optimization precomputes the pinned RCPSS mantissa approximation
into one process-wide 2,048-entry read-only table (8 KiB of payload), replacing
integer division and remainder in the per-sample hot path without changing any
conversion point or arithmetic result. A 21-pair production-sized CTI
microbenchmark moved from 1.590 to 1.401 ms (11.88% lower, 1.135x throughput).
Three interleaved runs per build on a fixed 100-frame
`current --dsp-backend ipp-fast --threads 20` window moved the decoder-reported
median from 5.14 to 5.07 seconds (1.36% lower, 1.014x throughput). Luma, chroma,
raw JSON, stdout, timing-normalized stderr, and timestamp-normalized logs all
matched. Exact matrix hashes matched the earlier strict current gate, and IPP
matrix hashes matched the merged SOS32 baseline.

The SOS32 candidate gives long VHS chroma-burst blocks worker-local IPP
single-precision biquad contexts and retains at most 12 idle contexts. Three
interleaved runs per build on a fixed 200-frame
`current --dsp-backend ipp-fast --threads 20` window moved median wall time
from 9.740 to 9.480 seconds (2.67% lower, 1.027x throughput) and process CPU
time from 52.160 to 48.590 seconds (6.84% lower). The v0.4.0 and `current`
40-frame gates each produced one artifact/stdout/`fileLoc` hash at zero,
default, and 20 workers; sampled peak working set stayed bounded at
281.2-707.4 MiB. Six Exact main/candidate pairs covering both profiles at one,
default, and 20 workers matched luma, chroma, raw JSON, stdout,
timing-normalized stderr, timestamp-normalized logs, and all ordered `fileLoc`
values byte for byte.

IPP BiQuad uses a different float32 evaluation order from the previous managed
SOS loop, so this is intentionally an `ipp-fast` numerical change. Against
main on the 200-frame window, 245,115 of 142,102,000 luma samples (0.172492%)
differed, with 0.031664 mean absolute error, 11.216850 RMS error, and a 26,666
maximum caused by a changed dropout decision in one field. Chroma changed in
18,467,547 samples (12.995980%), with 0.154614 mean absolute error, 1.817706 RMS
error, and a 1,661 maximum. Field count, `seqNo`, stdout, normalized stderr,
and ordered `fileLoc` matched; raw JSON differed only in that one field's
`dropOuts`, and numerical IRE log lines changed accordingly. These figures are
not an Exact compatibility claim.

The DFT32 candidate was also screened against the previous release on one fixed
200-frame `current --dsp-backend ipp-fast --threads 20` pair. Wall time moved
from 10.815 to 9.470 seconds (12.43% lower, 1.142x throughput), process CPU time
from 65.797 to 51.047 seconds (22.42% lower), and peak working set from 352.9 to
351.2 MiB. Field count, stdout, ABI-normalized stderr/logs, and ordered `fileLoc`
matched. Luma and raw JSON hashes also matched. Chroma was numerically compared
over 142,102,000 unsigned samples: 0.1627% differed, 99.99987% of all samples
were within one code value, RMSE was 0.063, and 124 samples differed by more
than four. This is an `ipp-fast` numerical result, not an Exact byte-compatibility
claim. Separate 40-frame Exact v0.4.0 and `current` release/candidate pairs
matched all nine byte, metadata, console, log, and `fileLoc` gates.
Python v0.4.0 was commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`; merged PR341 was commit
`2f21e8ed6018b14561396cc95f1f6828054470b8` (`v0.4.0-40-g2f21e8ed`).
Python 3.14.0 used NumPy 2.4.6,
SciPy 1.18.0, Numba 0.66.0, and python-soxr 1.1.0. The shared arguments were:

```text
--system pal --detect_chroma_track_phase --ire0_adjust
--tape_format VHS --frequency 40 --length 40 --start 100 --overwrite
```

The shared `--start 100` selects the same bounded frame window for every
profile; no `--start_fileloc` option was used. The default is **5 workers** in
both implementations.

The sync-scan candidate was also gated outside the short public matrix with
Exact `current`, 20 workers, and no start offset. Interleaved 160- and
400-frame medians moved from 17.23 to 16.80 s and from 33.14 to 32.57 s while
median active cores rose from 5.27 to 5.60 and from 5.25 to 5.51. Two
opposite-order 1,000-frame runs moved from 72.01/72.03 s to 70.57/70.59 s.
Maximum working set stayed within 435.8-437.4 MiB and allocation stayed within
3.02-3.04 GiB. Luma, chroma, JSON, stdout/stderr, normalized logs, and all
2,000 ordered `fileLoc` values matched the pre-change build.

The later Exact-only precise-scan stage replaced the second serial million-
sample threshold pass with parallel crossing extraction, then rebuilt the
original state machine and grid decisions in input order. Opposite-order
1,000-frame runs moved from 70.06/69.32 s to 68.26/68.67 s, a 0.9-2.6%
reduction. Allocation stayed at 3.02-3.05 GiB and maximum working set at
435.5-437.2 MiB. The paired 40-frame results were mixed, so no fixed short-run
gain is claimed. IPP-fast keeps its original serial second scan after a
six-pair 160-frame check was neutral at 11.64/11.67 s.

A separate 40-frame `--threads 0` gate made Exact v0.4.0 match Python
`g4315520` for luma, chroma, raw JSON, all 80 ordered `fileLoc` values, stdout,
normalized stderr, and timestamp-normalized logs. Exact `current` matched
Python PR341 on the same surfaces after excluding expected build-identity JSON
fields. The v0.4.0 strict-oracle artifacts were:

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `37B799282A82770461AD9DB8EC2E471AB86F9C05F145D411C2FCA5A6D695CACE` |
| Chroma TBC | `DC2E3C6FAC3323F05080F22CBEB1236A9EBFB3F0A8CB58B6D498F42EA1AFD794` |
| JSON | `9FB6DC1FAE18024B63B93E1165C5C3F7858AC6A01A786043F7A0E4BF5EAEC30C` |

Every .NET profile and Python PR341 produced one deterministic hash set per
mode. Each Python v0.4.0 default/nonzero mode produced three distinct
luma/chroma/JSON/log hash sets across its three repetitions, while ordered
`fileLoc`, stdout, and normalized stderr stayed stable. Those Python rows are
therefore throughput comparisons only; Python v0.4.0 `g4315520 --threads 0`
remains the strict oracle. Before the SOS32 optimization documented above,
IPP-fast matched its corresponding Exact luma and chroma hashes on this
fixture; that historical, sample-specific observation is not a general
byte-compatibility promise.

A previous Exact-only thread matrix used an Intel Core Ultra 7 265K (20 logical
processors), Windows 11 build 26220, .NET SDK/runtime
`11.0.100-preview.6.26359.118`, and Python v0.4.0 commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4` (reported as `g4315520`).
The isolated Python environment used NumPy 2.4.6, SciPy 1.18.0, Numba 0.66.0,
and python-soxr 1.1.0. Each value is the median of three interleaved Release
runs:

| CLI mode | Effective workers | This port | Python | Speedup | Wall-time reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| default | 5 | 3.861 s | 12.021 s | 3.114x | 67.9% |
| `--threads 1` | 1 | 8.052 s | 13.700 s | 1.701x | 41.2% |
| `--threads 5` | 5 | 3.964 s | 11.924 s | 3.008x | 66.8% |
| `--threads 10` | 10 | 3.379 s | 12.344 s | 3.653x | 72.6% |
| `--threads 20` | 20 | 3.152 s | 12.649 s | 4.013x | 75.1% |

The default remains **5 workers**, matching Release 4.0 CLI semantics; explicit
20-worker mode was fastest on this 20-logical-processor fixture. The matrix used
a local PAL `.lds` capture with `--system pal
--detect_chroma_track_phase --ire0_adjust --tape_format VHS --frequency 40
--start_fileloc 620000000 -l 40 --overwrite`, plus the row's thread option.

All 15 port runs produced one identical luma TBC, chroma TBC, and JSON hash set
across every worker count. Three additional Python `--threads 0` controls were
mutually identical and exactly matched every port run. Upstream Python's
default/nonzero matrix modes were not a reliable byte-exact baseline: its 15
runs produced 14 distinct luma/chroma pairs and 10 distinct JSON hashes; only
two runs matched the serial luma/chroma reference. The matrix therefore
compares observed throughput only, while Python `--threads 0` is the strict
compatibility baseline for hashes, metadata, console output, and normalized
logs.

The compatibility baseline for this 40-frame fixture is Python v0.4.0
`g4315520` with `--threads 0`:

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `6F4DD4ABE1D05A5030846DEA550758A79E7737D680A2B06024CFA06C83BF5185` |
| Chroma TBC | `BB91833B7575C003AEC9853ED75D4CFF82C1125690B226E0A79D539B6594169C` |
| JSON | `2F4C27FB9F3A9F4E8467BB49E89D660132DA5A2DCCC99AE897A072B1DD099EE5` |

A longer exact-output checkpoint used an Intel Core Ultra 7 265K (20 logical
processors), Windows 11 build 26220, and .NET SDK/runtime
`11.0.100-preview.6.26359.118`:

| PAL VHS, 1,000 frames / 2,000 fields | Wall time | CPU time | Peak working set | Speedup vs Python |
| --- | ---: | ---: | ---: | ---: |
| Python v0.4.0 (`g4315520`, `--threads 0`) | 405.63 s | 402.88 s | 0.74 GiB | 1.00x |
| This port, default (5 workers) | 76.78 s | 215.66 s | 1.11 GiB | 5.28x |
| This port, `--threads 20` | 60.58 s | 244.95 s | 1.45 GiB | 6.70x |

All three runs used the same local PAL `.lds` capture and
`--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 620000000 -l 1000 --overwrite`, plus the row's
thread option. Both port modes exactly matched Python `--threads 0` for luma,
chroma, JSON and stdout SHA-256, every aligned `fileLoc`, and all 5,132
timestamp-normalized log lines. The first and last emitted `fileLoc` values
were `620421120` and `2219612160` in every run.

The long run also showed no progressive slowdown: the default mode's first and
second 500-frame halves took 38.03 s and 37.72 s, while `--threads 20` took
30.42 s and 29.37 s. Peak working set remained bounded throughout both port
runs.

An independent native-container checkpoint exercised a large nonzero seek in
the same local NTSC-J `.ldf` capture for 1,000 frames / 2,000 fields:

| NTSC-J VHS mode | Wall time | Speedup vs Python |
| --- | ---: | ---: |
| Python v0.4.0 (`g4315520`, `--threads 0`) | 397.158 s | 1.00x |
| This port, `--threads 0` | 175.531 s | 2.26x |
| This port, default (5 workers) | 80.761 s | 4.92x |
| This port, `--threads 20` | 58.527 s | 6.79x |

Every port mode exactly matched the strict Python baseline for luma, chroma,
JSON, and stdout SHA-256, all 2,000 ordered `fileLoc` values, and all 3,473
timestamp-normalized log lines. This checkpoint also verifies the native
`.ldf` loader's upstream PyAV first-frame PTS behavior after a large seek.

A fresh strict recheck of the AVX pulse-transition pass used the same local
NTSC-J `.ldf` capture. Current Python v0.4.0 `--threads 0` completed in
390.077 s and the port at `--threads 20` in 57.609 s (6.77x; 85.2% less wall
time). Luma, chroma, JSON, stdout, all 2,000 ordered `fileLoc` values, and all
3,413 timestamp-normalized log lines matched exactly; the port peaked at
1.323 GiB working set. A direct Python rerun and clean merged main both
produced that current 3,413-line log, so the 3,473-line record above remains a
historical checkpoint. Python CPU and memory are omitted because its launcher
delegates work to child processes.

An independent no-seek startup checkpoint used a second local PAL `.lds`
capture with the same PAL VHS options,
`--threads 0`, and `-l 1000`. Python and this port produced byte-identical luma
SHA-256 `E6616B63BD7DD1DB6C093FC6D1DCA7D23AABEF34EFD52089338D992F2DDCD0CD`
and chroma SHA-256
`A292BD77A8EB3373B6C631CE4552F77B6D4E5AF2228A85F01C63EDBBBFB4C0EF`.
All 2,000 field records, 135 startup recovery steps, and the 1,000-entry file
frame sequence (`22..1021`) also matched. The packaged Python baseline wrote
the eight-character identity `g43155200`, while this port uses `g4315520`;
those `gitCommit`/`version` identity strings were the only JSON differences.
This correctness run overlapped another decode process, so its timing is not a
benchmark result.

These numbers are fixture-specific, not universal benchmarks. In a three-run
same-binary 160-frame NTSC-J A/B at `--threads 20`, scalar/AVX wall medians
were 12.029/11.854 s (1.5% faster), and CPU medians were 46.984/46.250 s
(1.6% lower). All luma, chroma, JSON, stdout, normalized-log hashes, and
`fileLoc` ranges matched. The 40-frame tuning A/B runs below used .NET
SDK/runtime `11.0.100-preview.6.26359.118`,
`--threads 20`, default chroma, and default resampling. On a reproducible
40-frame PAL probe,
the saved pre-continuous-pipeline baseline median was 11.60 s and the latest
median was 4.228 s, a 63.6% cumulative gain. The newest exact-kernel checkpoint
alone moved matched wall/CPU/peak-working-set medians from
4.434 s/16.516 s/1.314 GiB to 4.228 s/15.328 s/1.069 GiB
(4.6%/7.2%/18.6%). Process CPU divided by wall time is about 3.63 active cores,
so further work still targets state-safe field-stage parallelism. All 14 runs
produced identical paired TBC, JSON, and chroma SHA-256 values.

Earlier 40/160/320-frame sustained runs completed in 7.65/26.58/52.51 s. Peak
working sets were 1.76/1.88/1.67 GiB, while second-half medians were
1.42/1.30/1.28 GiB. The full 320 frames were written, and memory showed no
growth with decode length. Earlier allocation work also reduced a PAL LD
four-field probe from 5.12 GiB to 1.96 GiB.

The bounded VHS field-stage overlap reduced a 160-frame run from 20.13 s to
18.55 s (7.8%). TBC, chroma, and JSON SHA-256 values matched exactly; the task
is awaited within the current field, so memory cannot grow with decode length.

The zero-copy little-endian TBC writer removed about 455 MB of full-field
temporary byte-array payload across the same 160-frame output. Its xUnit v3
allocation probe writes 400,000 samples with less than 1 KiB of thread-local
allocation after warm-up. A fresh 160-frame run retained the exact luma and
chroma SHA-256 values; wall time remained within run-to-run noise.

### In-place Complex32 second-pass packets

The Exact float32 PocketFFT large-transform layout already owns disjoint,
contiguous source packets during its second pass. It now transforms each source
packet in place and scatters that result to the distinct destination buffer,
instead of allocating or renting a temporary `Complex32` packet and copying the
complete source packet into it first. Parallel packets do not overlap. Factorization,
roots, twiddles, transform arithmetic, normalization, scatter order, data type,
and final buffer ownership are unchanged.

Three frozen forward/backward baseline pairs cover lengths 11,025, 119,790, and
131,072 with one and 20 requested workers. A fourth test pins old-main hashes
for positive and negative zero, subnormals, and minimum normal values across
preserving, owned, and owned-plus-scratch storage at one and 20 workers. It also
compares maximum finite values, infinities, and distinct NaN payloads bit for
bit across those storage paths in the same process; non-finite payload selection
is not frozen to a machine-specific absolute hash. These four independently
discoverable xUnit v3 cases pass both on native hardware and with all .NET
hardware intrinsics disabled.

Six interleaved 160-frame Exact `current --threads 20` pairs against merged
main `a059580` matched luma, chroma, raw JSON, stdout, normalized stderr/logs,
ordered `fileLoc`, field count, and exit behavior. Median wall time moved from
11.59 to 11.27 seconds (2.8% lower), throughput rose 2.8%, CPU time fell 1.9%,
and the candidate won five pairs. Four one-worker pairs moved from 69.56 to
68.98 seconds (0.8% lower) with effectively neutral CPU time and peak memory.

Three 1,000-frame 20-worker pairs remained exact but changed ordering with host
scheduling. Their paired-median wall change was +0.14% and paired-median CPU
change was +1.18%, so long-run throughput and CPU efficiency are explicitly
classified as neutral. Candidate peak working set remained bounded near
443 MiB without progressive growth.

A supporting sampled trace reduced `MemmoveNative` attribution from 8.391 to
5.454 CPU-seconds (35.0%). Mean effective cores moved from 7.72 to 8.43 and P90
from 9.83 to 11.22. That older trace baseline predates `a059580`, so it is used
only to confirm hotspot removal and multicore shape; the exact-main interleaved
A/B above is the causal performance gate. The refreshed 90-run matrix covered
all six Python/.NET profiles at default, 1, 5, 10, and 20 workers. All 60 .NET
runs and all 15 Python PR341 runs were deterministic. Python v0.4.0 produced a
different luma, chroma, JSON, and normalized-log hash in each of its 15 runs;
the strict oracle therefore remains `g4315520 --threads 0`.

<details>
<summary>Kernel and allocation benchmark history</summary>

The pinned PAL-sized TBC sinc A/B reduced the median from 3.929 ms to 3.727 ms
per field, a 5.1% kernel gain, and the interior-window path added 1.6%. An
AVX/FMA follow-up retained scalar clamps and ordered double accumulation. Five
interleaved PAL-field A/B runs reduced serial/20-worker medians from
21.588/5.579 to 18.741/5.330 ms (13.2%/4.5%). Five 40-frame full-path pairs
reduced median wall/CPU time from 5.511/19.297 to 5.478/17.922 s (0.6%/7.1%).
Two reversed 204-frame pairs were 1.1-1.3% faster with bounded memory; TBC,
chroma, JSON, and the isolated field hash remained exact.

A session-owned VHS chroma-table cache retains one exact-key heterodyne set and
one burst-carrier set. Matched 40-frame GC traces reduced sampled allocation
from 13.854 to 12.579 GiB, `Double[]` allocation from 12,611.83 to 11,311.73
MiB, and Gen2 collections from 38 to 31. Five interleaved A/B pairs reduced
median wall/CPU time from 5.49/19.23 to 5.30/18.05 s (3.5%/6.1%). Two reversed
204-frame pairs were 4.4% and 4.8% faster; memory was non-monotonic with a
2.0 GiB maximum, and all 409 fields and output hashes remained exact. Removing
the two remaining read-only field copies further reduced matched sampled/
`Double[]` allocation from 12.580 GiB/11,309.71 MiB to 12.147 GiB/10,871.59
MiB. Five interleaved runs reduced median wall/CPU time from 5.209/18.188 to
5.175/17.094 s (0.7%/6.0%); two reversed 204-frame pairs were 1.8% and 1.9%
faster with non-monotonic memory at or below 2.05 GiB and exact 408-field
`--length 204` outputs.

Parallel RF span assembly uses completed immutable blocks and disjoint final
window ranges, with analog-audio phase work left ordered. Five interleaved
40-frame runs reduced median wall time from 5.165 to 4.878 s (5.6%) while CPU
time rose from 18.172 to 18.875 s (3.9%), converting more core use into
throughput. Two reversed `--length 204` pairs completed baseline/current in
21.31/20.35 s and 21.84/20.18 s (4.5% and 7.6% faster). Current memory was
non-monotonic with 1.93/2.06 GiB peaks, and all 408 fields and hashes remained
exact.

Parallel VHS payload output overlaps each field's independent luma and chroma
stream writes, while joining both before the next field. Five interleaved
40-frame runs reduced median wall time from 4.98 to 4.87 s (2.2%); median CPU
time rose from 18.20 to 19.50 s as both writes used otherwise idle capacity.
Two reversed `--length 204` pairs completed baseline/current in 20.451/20.181 s
and 20.483/20.353 s (1.3% and 0.6% faster). Current memory was non-monotonic
with 2.03/2.06 GiB peaks, and all 408 fields and hashes remained exact.

The compact VHS RF-channel path releases raw input, raw demodulation, and RF
high-pass block arrays before caching, skips their field assembly, and does not
run the unused RF high-pass inverse FFT. Five interleaved 40-frame A/B runs
reduced median wall/CPU time from 6.01/18.86 to 5.02/17.45 s (16.5%/7.5%). Two
reversed 204-frame pairs completed baseline/current in 20.48/20.28 s and
20.61/19.87 s; CPU time was 79.88/68.91 s and 77.17/72.44 s. Peak working set
moved from 2.05-2.08 GiB to 1.58-1.67 GiB, with non-monotonic quarter samples;
all 408 fields and luma, chroma, and JSON hashes remained exact.

The compact analytic follow-up feeds the pooled real and imaginary arrays
directly into VHS FM unwrap, SIMD-normalizes four frequency differences at a
time, and materializes `Analytic` only for the full direct API. Five interleaved
40-frame pairs were wall-time neutral at 5.02/5.03 s, while median CPU time fell
from 17.73 to 17.28 s and median peak working set from 1.47 to 1.26 GiB. Two
reversed 204-frame pairs remained within wall-time noise; current peaks were
1.32-1.41 GiB with non-monotonic quarter samples, and all three hashes remained
exact.

The compact chroma follow-up keeps float32 SOS output narrow until RF field
assembly. Matched 10-frame allocation traces reduced sampled managed allocation
from 2.95 to 2.89 GiB and `Double[]` allocation from 2.75 to 2.60 GiB, while
`Single[]` rose from 0.03 to 0.11 GiB. Five interleaved 40-frame pairs reduced
median wall/CPU time from 4.831/16.50 to 4.769/15.75 s (1.3%/4.5%). Two reversed
204-frame pairs were wall-time neutral at baseline/current 19.73/19.83 and
19.87/19.73 s; current peaks were 1.46/1.39 GiB and remained within the existing
bounded working-set envelope. All luma, chroma, and JSON hashes remained exact.

The bounded payload-writer follow-up overlaps the next VHS field decode with the
current field's luma/chroma write through a capacity-one queue. Payloads remain
ordered before their recovery JSON snapshot, completion drains the writer, and
worker failures return to the decode thread. Five interleaved 40-frame pairs
reduced median wall/CPU time from 4.90/16.09 to 4.79/15.47 s (2.2%/3.9%). Two
reversed 204-frame pairs completed baseline/current in 20.23/19.54 s and
20.05/19.19 s (3.4%/4.3% faster). Current quarter peaks were
1.35/0.74/0.96/1.14 and 1.27/0.95/0.97/1.09 GiB, with no monotonic growth; all
408 fields and luma, chroma, and JSON hashes remained exact.

The native-rate `.s16` input path now bypasses FFmpeg only when the declared
rate is exactly 40.0 MHz. A fresh trace contained no FFmpeg pass-through or
input-pump frame in its top 300 inclusive methods. Five interleaved 40-frame
pairs reduced median wall/CPU time from 5.33/17.11 to 4.97/15.94 s
(6.8%/6.8%), and median peak working set from 1.23 to 1.13 GiB. Two reversed
204-frame pairs completed baseline/current in 21.50/20.86 and 21.67/21.54 s;
candidate peaks were 1.39/1.35 GiB, and all output hashes remained exact.

AVX RF-envelope preparation reduced the isolated 32K-block median from 57.5 us
to 13.3 us, a 76.9% kernel gain. The 40-frame median moved from 7.55 s to 7.39 s,
and the 160-frame run from 26.95 s to 25.70 s. Its private-memory quarter medians
were 1.34/1.48/1.50/1.45 GiB with a 1.72 GiB peak; all three hashes stayed exact.

The four-lane AVX/SSE VHS Rust-style FM unwrap reduced its isolated 32K-block
median from 610.1 us to 130.7 us, a 78.6% kernel gain. In a five-pair interleaved
40-frame full-path A/B, median wall time moved from 7.43 s to 7.41 s while median
CPU time fell from 27.88 s to 26.36 s, a 5.5% reduction. TBC, JSON, and chroma
hashes remained exact. A 160-frame run completed in 26.48 s with private-memory
quarter medians of 1.45/1.47/1.40/1.23 GiB and a 1.79 GiB peak.

The latest FFmpeg stream pass replaced per-read 16 MiB rewind reconstruction with
one bounded circular buffer. The isolated 384-read median fell from 695.4 ms to
48.7 ms, while allocations fell from 4.31 GB to 142.6 MB. In a three-run
40-frame A/B, median wall/CPU time moved from 8.98/28.47 s to 7.40/22.33 s;
all three output hashes remained exact. Sampled `byte[]` allocation fell from
36.3 GB to 209 MB. A 160-frame run finished in 25.86 s with private-memory
quarter medians of 0.76/1.15/1.42/1.14 GiB and a 1.67 GiB peak.

The latest VHS real-FFT pass reuses exact-length half spectra, Hilbert buffers,
the raw envelope, and rotation inputs through a decoder-owned pool capped at 16
workspaces. In five isolated 384-block A/B runs, median time fell from 1,140.6 ms
to 1,054.0 ms (7.6%), allocation fell from 2.216 GB to 906.8 MB (59.1%), and
median Gen2 collections fell from 168 to 56. A 160-frame full-path A/B remained
wall-time neutral at 24.54/24.57 s while CPU time fell from 78.03 s to 70.13 s
(10.1%). The current run peaked at 1.68 GiB; its private-memory quarter medians
were 0.88/1.55/0.78/1.51 GiB rather than a monotonic rise. TBC, JSON, chroma, and
isolated block hashes remained exact.

The forward radix-4 kernel now uses the same pinned indexing as the inverse;
its isolated 32768-point median fell from 204.7 us to 195.9 us (4.3%) with exact
bits. The 384-block RF composite was neutral at 841.96/841.19 ms, so no
whole-block speedup is claimed for this change.

The subsequent float32 SOS pass preserves sample-major arithmetic order while
keeping one-, two-, and four-section cascade states in locals. Other cascade
sizes use flat bounded state: stack storage through 32 sections and a heap
fallback above that limit. Five-run isolated 32K medians for two/four sections
fell from 110.2/155.4 ms to 75.3/83.3 ms (31.7%/46.4%); five/eight/ten-section
medians fell by 38.8%/40.2%/42.7%. Across two 160-frame A/B pairs, median wall
time fell from 21.22 to 20.57 s (3.1%) and CPU time from 73.31 to 68.73 s
(6.3%). TBC, JSON, and chroma hashes remained exact. The current pair's median
private-memory peak was 1.71 GiB, and quarter-run memory was not monotonic.

A follow-up pass pooled the float32 SOS padded workspace. Matched 40-frame GC
traces reduced total sampled allocation from 16.772 to 16.178 GiB and
`Single[]` allocation from 651.68 to 47.25 MiB. Five interleaved full-path A/B
runs were wall-time neutral at 5.541/5.537 s, while median CPU time moved from
20.000 to 19.438 s; all three output hashes remained exact. The current
fixture-limited 204-frame run completed in 23.39 s with 1.147/0.886/0.888/0.917
GiB private-memory quarter medians and a 1.755 GiB peak.

The next pass pooled the default linear TBC resampler's two per-field double
workspaces. Matched 40-frame GC traces reduced total sampled allocation from
16.178 to 14.892 GiB and `Double[]` allocation from 13.601 to 12.316 GiB. Five
interleaved A/B runs reduced median wall time from 5.684 to 5.571 s (2.0%) and
CPU time from 19.031 to 18.891 s; all three hashes remained exact. A repeated
204-frame run had flat 1.025/1.047/1.007/1.042 GiB private-memory quarter
medians and a 1.869 GiB peak.

The VHS diff-demod repair pass now keeps its transient full-length `Complex[]`
in the existing capped FFT workspace. Matched 10-frame GC traces reduced total
sampled allocation from 4.134 to 3.861 GiB and `Complex[]` allocation from
622.63 to 340.02 MiB. Ten interleaved 40-frame pairs and two reversed 204-frame
pairs were wall-time neutral within run noise, so no speedup is claimed;
long-run memory remained bounded and all 409-field hashes stayed exact.

The current double-SOS and BA-IIR pass fuses the common two- and four-section
double cascades and reuses the BA filter's padded workspace through a private
bounded pool. Isolated two/four-section SOS medians improved by 37.5%/58.9%.
Across 32K-sample high-pass orders 4/9/20, the current IIR path was
23.7%/30.3%/26.6% faster than the old allocating reference and reduced warm
thread allocation from about 1.05 MB to 262 KB. Seven interleaved 40-frame
full-path pairs produced the 4.6% wall, 7.2% CPU, and 18.6% peak-working-set
improvements above. A fixture-limited 409-field run completed in 17.431 s;
25-50%, 50-75%, and 75-100% output intervals were 4.06/4.02/4.27 s, while
second-half median working/private memory rose by only 10.8/7.4 MiB. Every
recorded luma, chroma, and JSON hash remained exact.

The packed `.lds` loader now writes decoded samples directly into its requested
output and preserves Python's partial-tail-group behavior. Five interleaved
40-frame real-capture pairs moved default wall/CPU medians from
4.687/12.422 s to 4.610/12.188 s and 20-worker medians from 3.813/14.469 s to
3.743/13.109 s. Three 160-frame default pairs moved wall time from 15.281 to
14.993 s; a separate five-pair 20-worker repeat moved wall/CPU medians from
12.655/46.297 s to 12.601/46.156 s and peak working set from 1.319 GiB to
1.198 GiB. All 42 recorded real-capture runs produced one exact luma, chroma,
and JSON hash set per fixture.

A follow-up packed-input pass reuses one loader-owned read buffer. In a
1,024-block 32K probe, median time moved from 68.20 to 65.17 us per block
(4.4% faster) and managed allocation from 310.49 to 268.52 MB (13.5% lower).
Matched 160-frame runtime counters reduced total allocation from 22.248 to
22.113 GiB, about 139 MiB (0.61%). Five 40-frame pairs moved default wall/CPU
medians from 4.380/12.016 to 4.325/11.594 s and 20-worker medians from
3.645/14.813 to 3.586/14.188 s. Three 160-frame pairs were wall-neutral at
14.173/11.692 versus 14.231/11.701 s for default/20-worker. Two reversed-order
400-frame pairs completed candidate/baseline in 26.229/26.403 s and
baseline/candidate in 26.395/26.540 s. The pass is retained for lower long-run
allocation; the 160/400-frame results do not establish a stable full-path CPU
speedup. Every luma, chroma, and JSON hash remained exact.

The VHS sync-reference DC-offset pass now reuses at most two exact-length
low-pass workspaces. A matched 10-field GC trace reduced sampled managed
allocation from 2.639 to 2.466 GiB, `Double[]` allocation from 2,469.42 to
2,291.86 MiB, and Gen2 collections from 17 to 15. Five interleaved 40-field
pairs were wall-time neutral within run noise (default 4.473/4.522 s;
20-worker 3.736/3.778 s), while CPU medians moved from 12.719 to 11.969 s and
14.375 to 13.859 s. Three 160-field pairs moved default/20-worker wall medians
from 15.272/12.560 to 15.113/12.378 s. A 400-field 20-worker A/B moved
wall/CPU from 28.937/106.984 to 28.296/105.344 s; candidate private-memory
quarter medians were 1.076/0.766/1.025/0.726 GiB with a 1.463 GiB peak, showing
no monotonic growth. Every recorded luma, chroma, and JSON A/B hash remained
exact.

The VSync serration-window pass removes the full-window copy made before level
measurement. A matched 10-field GC trace reduced sampled managed allocation
from 2.465 to 2.434 GiB and `Double[]` allocation from 2,291.20 to 2,266.54
MiB, a 24.7 MiB reduction, without adding retained buffers. Five interleaved
40-field pairs were wall/CPU neutral within run noise (default
4.508/12.188 to 4.556/12.422 s; 20-worker 3.719/14.203 to
3.696/14.531 s). Three 160-field pairs were also neutral (default
14.847/40.484 to 14.904/40.406 s; 20-worker 12.319/45.172 to
12.361/45.391 s). A conservative candidate-first 400-field 20-worker A/B
moved wall/CPU from 28.015/107.828 to 27.865/108.547 s and peak working set
from 1.481 GiB to 1.465 GiB. The change is retained for lower long-run allocation
pressure rather than a claimed CPU-speed gain; every recorded luma, chroma,
and JSON hash remained exact.

The VHS chroma-prefilter ownership pass borrows the immutable field input when
no prefilter is configured, while configured filters and the public
`ApplyChromaPreFilter` API continue to return independently owned arrays. A
matched 10-field GC trace reduced sampled managed allocation from 2.440 to
2.384 GiB and `Double[]` allocation from 2,267.10 to 2,207.39 MiB, removing the
59.629 MiB `ApplyChromaPreFilter` allocation stack; both runs performed 15 Gen2
collections. Five interleaved 40-field pairs moved default wall/CPU medians
from 4.475/12.312 to 4.433/12.219 s and 20-worker medians from
3.694/14.531 to 3.638/14.531 s. Three 160-field pairs moved default medians
from 15.104/41.297 to 14.732/40.344 s; 20-worker wall time remained neutral at
12.179/12.206 s while CPU time moved from 49.312 to 46.094 s. Two reversed-order
400-field pairs completed candidate/baseline in 28.039/28.553 s and
baseline/candidate in 28.224/28.308 s; candidate peaks were
1.474/1.475 GiB. Every recorded luma, chroma, and JSON hash remained exact.

The VHS chroma comb/gain pass fuses those two internal stages with one
line-sized stack workspace while leaving the public stage APIs unchanged. A
matched 10-field GC trace reduced sampled managed allocation from 2.360 to
2.322 GiB and `Double[]` allocation from 2,197.06 to 2,147.33 MiB. The
59.629 MiB `ApplyComb` allocation stack disappeared, the final gain-owned
59.629 MiB output remained, and both runs performed 14 Gen2 collections. Five
interleaved 40-field pairs moved default wall/CPU medians from 4.455/12.250 to
4.366/12.125 s and 20-worker medians from 3.721/15.719 to 3.657/14.094 s. A
separate five-pair 160-field 20-worker run moved wall/CPU medians from
12.180/47.922 to 12.064/44.031 s. Two reversed-order 400-field pairs completed
candidate/baseline in 26.916/27.468 s and baseline/candidate in
27.398/27.664 s; candidate peaks were 1.484/1.481 GiB. Every recorded luma,
chroma, and JSON hash remained exact. An earlier line-history in-place
prototype was fully removed after its 160-field wall medians regressed from
15.20 to 15.53 s by default and from 12.45 to 12.68 s with 20 workers.

The subsequent VHS chroma gain-to-U16 pass removes the remaining gain-owned
double field from internal decode while leaving the public gain and conversion
APIs unchanged. A matched final 10-field GC trace reduced sampled managed
allocation from 2.320069 to 2.266559 GiB and `Double[]` allocation from
2,147.315 to 2,086.828 MiB. The 59.629 MiB
`ApplyAutomaticChromaGainWithComb` allocation stack disappeared, `UInt16[]`
allocation remained 29.815 MiB, and Gen2 collections moved from 15 to 14. Five
interleaved 40-field pairs moved default wall/CPU medians from
4.461/12.781 to 4.403/12.047 s and 20-worker medians from
3.706/14.406 to 3.665/12.906 s. A separate five-pair 160-field 20-worker run
moved wall/CPU medians from 12.196/46.047 to 11.985/45.625 s. Two
reversed-order 400-field pairs completed candidate/baseline wall/CPU in
27.566/27.877 s and 107.531/105.828 CPU-s, then baseline/candidate in
28.120/27.263 s and 105.422/107.594 CPU-s; candidate peaks were
1.355/1.474 GiB. The longer runs therefore used more total CPU while finishing
sooner, and every recorded luma, chroma, and JSON hash remained exact. An
initial full-field neutral-fill form was reworked after 160-field wall medians
regressed from 14.71 to 14.76 s by default and from 12.05 to 12.26 s with 20
workers. The scalar line-span form was also not retained as final after its
first 400-field pair completed candidate/baseline in 28.353/27.647 s; only the
AVX2/SSE4.1 form passed the final long-run gate.

The VSync in-place BA-IIR pass keeps the same filtering arithmetic while
reusing each private chain's owned array and writing the envelope blend
directly into its final reduced output. On the pinned PAL field fixture, the
isolated median moved from 6.610 to 5.080 ms per field (23.1% faster), while
managed allocation fell from 15.60 to 8.50 MiB per field (45.5%). A matched
10-frame GC trace reduced sampled allocation from 2.264 to 1.947 GiB (14.0%)
and Gen2 collections from 15 to 11. Five interleaved 40-frame pairs moved
default wall/CPU medians from 4.455/12.547 to 4.319/12.156 s and 20-worker
medians from 3.819/14.094 to 3.606/14.625 s. Five 160-frame 20-worker pairs
moved wall/CPU/peak-working-set medians from 12.059 s/45.406 s/1.475 GiB to
11.796 s/45.922 s/1.058 GiB. Two 400-frame pairs completed candidate/baseline
in 26.776/27.438 s and baseline/candidate in 27.214/26.785 s; candidate peaks
were 1.448/1.439 GiB. The 400-frame candidate used 1.4-5.0% more CPU while
finishing 1.6-2.4% sooner. Every recorded luma, chroma, and JSON hash remained
exact.

A follow-up detector-owned VSync workspace pass reuses the six exact-sized
analysis arrays across fields. On the same isolated fixture, median time moved
from 5.080 to 4.325 ms per field (14.9% faster), while warm-call allocation fell
from 8.50 MiB to about 3.8 KiB per field. A matched 10-frame trace reduced
sampled allocation from 1.947 to 1.720 GiB and sampled `Double[]` allocation
from 1,760.85 to 1,524.33 MiB. Three 160-frame default-worker pairs moved
wall/CPU/peak medians from 14.44 s/40.94 s/1.03 GiB to
14.21 s/39.56 s/0.77 GiB; five 20-worker pairs were neutral at
11.63 s/45.17 s/1.19 GiB versus 11.67 s/44.77 s/1.21 GiB. Two 400-frame
20-worker pairs finished 0.8-1.7% sooner with bounded 1.508/1.534 GiB candidate
peaks versus 1.451/1.404 GiB baselines. Every luma, chroma, and JSON hash was
exact.

The shared final-field TBC resampling plan now computes source positions and
wow level adjustments once, uses the same read-only plan for chroma and luma,
and returns its bounded buffers to `ArrayPool` immediately after rendering.
Two reversed-order 400-frame default-worker pairs moved median wall/CPU time
from 33.690/97.734 s to 32.805/93.609 s (2.6% less wall time and 4.2% less CPU).
Two 20-worker pairs were wall-neutral at 26.713 versus 26.760 s while reducing
median CPU time from 106.563 to 105.266 s; candidate peaks were bounded at
1.411/1.445 GiB. All recorded luma, chroma, and JSON hashes remained exact.

The fallback serration-level search now decimates each field once into one
bounded `ArrayPool` buffer and reuses one pulse list across the ordered 30-step,
5-IRE search. Its final full-resolution retry, threshold sequence, scalar
comparisons, and pulse ordering remain unchanged. Against main `4a67ae9` on
the same local PAL `.lds` capture (`--start_fileloc 620000000 -l 160`), two
interleaved default-worker pairs moved average wall/CPU time from
13.991/41.492 s to 13.595/39.773 s
(2.8%/4.1% lower); two 20-worker pairs moved from 11.152/48.508 s to
10.838/47.180 s (2.8%/2.7% lower). Across those pairs and the final clean-source
replay, candidate peak working sets stayed bounded at or below 1.14 GiB, and
all ten runs produced one exact luma, chroma, and JSON hash set. An AVX
pulse-state prototype was removed after it failed the 160-frame gate.

Default linear TBC source positions are now filled one output line at a time.
The implementation caches each line's two location values while retaining the
original per-sample division, subtraction, multiplication, and addition order.
Randomized tests compare every generated double bit-for-bit with the previous
scalar interpolation. Against baseline `c51f059` on that same local PAL `.lds`
capture's 160-frame window, two interleaved default-worker pairs moved average
wall/CPU time from 14.060/40.164 s to 13.598/40.438 s (3.3% less wall time;
CPU was 0.7% higher within run noise). Two 20-worker pairs moved from
10.907/45.039 s to 10.771/43.414 s (1.2% less wall time and 3.6% less CPU). The matching default
trace reduced sampled `BuildSourcePositions` self time from 711.35 to 257.61 ms
(63.8%). Candidate peak working sets stayed at or below 1.13 GiB, and all eight
runs produced one exact luma, chroma, and JSON hash set.

VSync analysis now retains a two-entry exact-shape LRU instead of replacing its
only workspace when normal field lengths alternate. Array types, populated
ranges, padding, filter arithmetic, and detector-state ordering are unchanged,
and each entry keeps the existing 1,048,576-sample cap. A matched real PAL
10-frame GC trace
reduced sampled managed allocation from 1.633 to 1.463 GiB (10.4%), sampled
`Double[]` allocation from 1,464.83 to 1,295.74 MiB (11.5%), and
`AnalysisWorkspace` allocation from 205.69 to 34.28 MiB (83.3%). Five
interleaved 160-frame `--threads 20` pairs moved wall medians from 10.188 to
10.029 s (1.6%) and means from 10.217 to 10.030 s (1.8%); every pair was
0.9-3.7% faster, while the CPU median was 2.1% higher and the peak-working-set
median fell from 1.375 to 0.936 GiB. A 400-frame gate moved wall/CPU/peak from
24.032 s/101.969 s/1.455 GiB to 23.722 s/97.828 s/0.958 GiB. Candidate
quarter-working-set medians were 0.705/0.752/0.776/0.654 GiB, so memory did not
grow with progress. PAL serial/default/20/64-worker runs matched all six output
and normalized diagnostic hashes, and the established 1,000-frame NTSC-J
large-seek gate exactly matched Python v0.4.0 `--threads 0` for luma, chroma,
JSON, stdout, normalized stderr/log, all 2,000 `fileLoc` values, and all 52
startup recovery diagnostics.

The common order-3, order-4, and order-5 BA-IIR paths now use fixed-order
scalar kernels. Coefficient types, per-sample expressions, arithmetic order,
state updates, and public buffer ownership are unchanged; other filter shapes
continue through the generic implementation. Isolated kernel medians improved
by 1.77-1.88x with unchanged managed allocation. On the same local PAL RF
capture, five interleaved 160-frame `--threads 20` pairs moved wall medians
from 14.116 to 12.897 s (8.6% less) and CPU medians from 53.156 to 49.813 s
(6.3% less), with every candidate run faster. Luma, chroma, JSON, stdout,
normalized stderr, and timestamp-normalized log hashes were exact in all ten
runs. Two reversed-order 400-frame pairs were 7.0-7.7% faster, remained at or
below 1.551 GiB peak working set, and matched the same six artifacts. Candidate
serial/default-5/20/64-worker runs were also exact across all six artifacts.
A fresh 160-frame large-seek gate on the same local NTSC-J RF capture also
matched all six artifacts between the main baseline at 20 workers and the
candidate at serial/default-5/20/64 workers. PAL and NTSC-J construct the same
three specialized filter orders, the independent scalar xUnit oracle covers
all three paths, all 848 tests pass, and the established 1,000-frame NTSC-J
gate above remains unchanged.

The native `.ldf` PCM16 loader now keeps its 2 MiB rewind history in one fixed
circular buffer, reads fresh bytes directly into the requested block, and
reuses its forward-discard buffer. FFmpeg launch, seek, padding, byte order,
rewind boundaries, partial-EOF advancement, and sample conversion remain
unchanged. In one controlled idle 400-frame `--threads 20` pair on the same
private local NTSC `.ldf` capture, baseline/candidate wall time moved from
26.242 to 24.741 s (5.7% less), CPU time from 145.516 to 136.484 s (6.2%
less), and peak working set from 1,204.5 to 1,190.6 MiB. Luma, chroma, JSON,
stdout, normalized stderr, and timestamp-normalized logs all matched. The
candidate also completed 1,000 frames / 2,000 fields without interruption
under default `v0.4.0` and opt-in `current`; both profiles matched their stored
Python oracles in the same six comparisons. IPP was excluded.

On native-input routes, direct raw `fLaC` `.ldf`/`.flac` input uses bundled
libsndfile when the first metadata block is a complete 34-byte STREAMINFO
describing 40 kHz mono PCM16 data with a known nonzero sample count no greater
than `Int32.MaxValue`. The handle opens lazily; sequential reads remain
seek-free, random reads use exact frame seeks, and one pooled PCM16 workspace
feeds the unchanged `short`-to-`double` conversion.

An additional oversized-input gate is available only to ordinary parallel VHS
decode. It requires a complete metadata chain, one fixed nonzero STREAMINFO
block size, a fixed-blocking first audio frame, no SEEKTABLE, and more than
`Int32.MaxValue` samples. At every decoder restart, integer time-base and block
arithmetic maps the logical RF sample to the same first native FLAC sample as
the pinned FFmpeg/PyAV path; the established 2 MiB rewind window and 40 MiB
byte-distance restart threshold are preserved. `--threads 0/1`, debug-plot and
GNU Radio AFE modes, nonzero `--sharpness`, LD, CVBS, and all inputs outside
this gate retain FFmpeg.
Unavailable or unsupported native opens, seek/decode errors, mapping or length
boundary failures switch once to the established FFmpeg/PyAV-compatible loader
at the same logical sample. A clean reported EOF remains EOF when FFmpeg is not
installed. Default VHS `.flac`, Ogg/FLAC, stereo, PCM24, other rates, unknown
totals, rejected headers, `.vhs`, `.wav`, and `raw.oga` also retain FFmpeg.

On the same private local RF window, Release 1.4.4 through FFmpeg and the
candidate through libsndfile matched luma, chroma, raw JSON, stdout, normalized
stderr/logs, and every ordered `fileLoc` under default, `--threads 0`, and
`--threads 20`. Three interleaved 20-frame pairs at 20 workers moved median
wall time from 3.88 to 2.99 seconds; default-worker medians were 4.36 and 4.30
seconds with overlapping ranges. A longer single 100-frame/200-field pair at
20 workers moved wall time from 8.319 s to 7.345 s (11.71% less; 1.133x
throughput) and sampled aggregate `decode` plus FFmpeg peak working set from
797.0 to 724.9 MiB. The long result is a scoped single-pair observation, not a
universal speed percentage.

RF nonlinear and sub-deemphasis now retain one exact-key, read-only high-pass
response per `RfDemodulator`. Block length and the immutable high-pass parameters
form the key; a miss replaces the sole entry under a lock, so concurrent blocks
share the completed response without accumulating arbitrary block shapes. On the
same private local 40 MHz NTSC `BETAMAX_HIFI` `.lds` capture, five interleaved
and reversed 160-frame `--threads 20` pairs against baseline `846ad28` moved wall
medians from 16.294 to 16.220 s (0.5% less) and means from 16.603 to 16.257 s
(2.1% less); the candidate won four of five pairs. CPU medians moved from
116.641 to 114.234 s (2.1% less) and means from 117.633 to 113.417 s (3.6%
less), with the candidate winning all five pairs. Peak-working-set medians fell
from 1.868 to 1.753 GiB, although individual peaks remained noisy.

Single serial and default-worker checks on that fixture reduced wall/CPU time
from 94.518/96.641 to 83.767/86.547 s and from 26.180/108.828 to
24.063/99.781 s respectively; these are single-pair observations, not universal
percentages. All ten 20-worker runs plus the serial/default candidates matched
one luma, chroma, JSON, stdout, normalized-stderr, and timestamp-normalized-log
set. A separate opt-in `current` pair matched the same six surfaces within that
profile while CPU time fell from 122.750 to 115.141 s. Two 400-frame candidate
runs were deterministic; the measured run completed in 37.012 s, peaked at
1.949 GiB, and had quarter working-set medians of
0.879/1.208/1.266/1.187 GiB, with no progressive end-of-run growth.

The opt-in `current` VHS HSync boxcar now divides large, independent output
ranges among at most four workers while retaining each sample's original
left-to-right float64 multiply-add order. On the same private local 40 MHz NTSC
`BETAMAX_HIFI` capture, two reversed-order 160-frame default-worker pairs were
0.5% and 0.9% faster, averaging 26.075 s versus 26.260 s; candidate CPU time
averaged 104.045 s versus 102.290 s as idle capacity became active. Two
`--threads 20` pairs were 10.1% and 5.6% faster, averaging 16.255 s versus
17.655 s, while average CPU time fell from 120.745 to 114.130 s. Candidate
working sets stayed bounded at or below 1.344 GiB. Across 16 short and eight
long runs covering `--threads 0`, `1`, default 5, and `20`, luma, chroma, JSON,
stdout, normalized stderr, and timestamp-normalized logs each remained exact.

The opt-in `current` Super-Gaussian float32 real-FFT path now transfers its
newly built `Complex32[]` buffers into large multipass transforms, avoiding
three redundant whole-buffer clones per field. Input-preserving APIs, FFT
plans, float32 conversion points, packet layout, and arithmetic order remain
unchanged. On the same private local 40 MHz NTSC `BETAMAX_HIFI` `.lds`
capture, four interleaved 160-frame `--threads 20` pairs averaged
16.30 s versus 16.52 s; paired gains were 2.85%, -0.63%, 0.89%, and 2.38%,
with a 1.64% median. A single candidate-first 400-frame pair completed in
35.84/39.54 s candidate/baseline and 260.45/269.59 CPU-seconds; this
order-sensitive observation is not a universal percentage. Candidate quarter
working-set peaks were 1.459/1.231/0.820/1.422 GiB, with no progressive
growth. Both gates matched luma, chroma, JSON, stdout, normalized stderr, and
timestamp-normalized logs; all 320/800 ordered `fileLoc` values also matched.
`current` outputs were deterministic at `--threads 0`, `1`, default 5, and
`20`, and a separate `v0.4.0` profile regression matched the same six
surfaces.

RF prefetch at high worker counts now permits up to 12 active block workers
while retaining the existing bounded lookahead calculation:
`min(effectiveWorkers + min(effectiveWorkers, 8), 32)`, where
`effectiveWorkers = min(requestedThreads, logicalProcessorCount)`. On the
20-logical-processor benchmark host, `--threads 20` therefore still holds 28
prefetch slots rather than expanding the cache. On the same private local
40 MHz NTSC `BETAMAX_HIFI` sample, four interleaved 160-frame
`current --threads 20` pairs all favored the candidate. Average wall time moved
from 16.401 to 15.650 s (4.57% less), the paired median gain was 3.47%, and
average CPU time moved from 117.664 to 116.902 s (0.65% less). Two
reversed-order 400-frame pairs improved by 0.63% and 3.11%, averaging
37.053/36.362 s baseline/candidate. Their average CPU time increased from
260.422 to 283.047 s as effective occupancy rose from 7.03 to 7.78 cores.
Candidate peaks stayed within 1.542-1.572 GiB and their quarter samples were
non-monotonic, with no growth by decode progress. Luma, chroma, JSON, stdout,
normalized stderr, timestamp-normalized logs, and all 320/800 ordered
`fileLoc` values matched. Candidate `current` output was deterministic at
`--threads 0`, `1`, default 5, and `20`; a separate 160-frame `v0.4.0`
baseline regression matched the same six surfaces and all 320 `fileLoc`
values.

The VHS full-spectrum analytic paths now cache their read-only Hilbert
multiplier by FFT length instead of rebuilding the same array for every RF
block. The multiplier values, float64 type, consumers, and element evaluation
order are unchanged. In separate 80-frame `gc-verbose` traces on the same
private local 40 MHz NTSC `BETAMAX_HIFI` sample, sampled managed allocation
fell from 43.073 to 42.195 GiB (0.878 GiB, or 2.04% less) and Gen2 collections
fell from 134 to 126. A six-pair 40-frame gate was noisy and favored the
baseline in four pairs, so no short-run gain is claimed. Four interleaved
160-frame `current --threads 20` pairs all favored the candidate: the paired
median wall-time gain was 4.85%, the average gain was 5.24%, average CPU time
fell 3.59%, and median peak working set moved from 1.520 to 1.479 GiB. Two
reversed-order 400-frame pairs improved by 3.82% and 3.80%; average CPU time
fell 1.50% and median peak working set moved from 1.555 to 1.502 GiB. Luma,
chroma, JSON, stdout, normalized stderr, timestamp-normalized logs, and all
ordered `fileLoc` values matched in every A/B gate. Candidate `current` output
was also deterministic at `--threads 0`, `1`, default 5, and `20`, and a
separate 160-frame `v0.4.0` regression matched the same surfaces.

The two full-spectrum VHS `ForwardReal` calls now write into full-length
worker-owned arrays that were already idle at those points in the RF block
lifetime. FFT plans, float64 conversion, element evaluation order, and output
ownership are unchanged. On the same private local 40 MHz NTSC
`BETAMAX_HIFI` sample, matched 80-frame `gc-verbose` traces reduced sampled
managed allocation from 42.1717 to 35.1848 GiB (6.9869 GiB, or 16.57% less);
Gen2 collections were 116 and 115. The six-pair 40-frame gate was noisy: the
candidate won three pairs and its paired median throughput was 1.63% lower, so
no short-run gain is claimed. Four interleaved 160-frame
`current --threads 20` pairs all favored the candidate, with a 2.99% paired
median throughput gain, a 2.57% average gain, 2.15% less average CPU time, and
median peak working set moving from 1.510 to 1.484 GiB. Two reverse-order
400-frame pairs were strongly order-sensitive at -5.20% and +8.94%; after
balancing both orders, aggregate throughput was 1.37% higher, average CPU time
was 2.95% lower, and median peak working set moved from 1.550 to 1.529 GiB.
Luma, chroma, JSON, stdout, normalized stderr, timestamp-normalized logs, and
all ordered `fileLoc` values matched in every A/B gate. Candidate `current`
output was deterministic at `--threads 0`, `1`, default 5, and `20`; a separate
160-frame `v0.4.0` regression matched the same surfaces.

The VHS complex high-boost path now reuses both worker-owned full-length
analytic FFT buffers in non-overlapping phases instead of allocating a copied
spectrum and inverse output for each analytic-signal construction. FFT plans,
float64 arithmetic, expression order, padding, and returned-output ownership
remain unchanged. On the same private local 40 MHz NTSC `BETAMAX_HIFI` sample,
matched 80-frame `gc-verbose` traces reduced sampled managed allocation from
35.1777 to 28.1943 GiB (6.9834 GiB, or 19.85% less), `Complex[]` allocation
from 17,135.251 to 9,974.767 MiB (41.79% less), and Gen2 collections from 108
to 88. The candidate won five of six paired 40-frame runs, with 1.67% median
and 1.75% average throughput gains. Across four interleaved 160-frame
`current --threads 20` pairs it won three, with 3.06% median, 2.73% average,
and 2.71% balanced aggregate throughput gains; aggregate CPU time fell 2.16%,
and median peak working set moved from 1.491 to 1.457 GiB. Both reverse-order
400-frame pairs favored the candidate at 1.96% and 1.86%; balanced aggregate
throughput rose 1.91%, aggregate CPU time fell 3.19%, and median peak working
set moved from 1.529 to 1.445 GiB. Luma, chroma, JSON, stdout, normalized
stderr, timestamp-normalized logs, and every ordered `fileLoc` matched in all
A/B gates. Candidate `current` output was identical across two runs each at
`--threads 0`, `1`, default 5, and `20`; a separate 160-frame `v0.4.0`
regression matched the same surfaces.

RF deemphasis now writes its final per-sample subtraction into the decoder-owned
video array, while public helpers still return independent arrays. VHS RF
high-band scaling also reuses its filter output after the old samples become
dead. Data types, FFT/filter work, per-sample expression order, and public
ownership remain unchanged. On the same private local 40 MHz NTSC
`BETAMAX_HIFI` sample, matched 80-frame `gc-verbose` traces reduced sampled
managed allocation from 28.183423 to 26.445196 GiB (1.738227 GiB, or 6.17%),
`Double[]` allocation from 14,999.178 to 13,209.475 MiB (11.93% less), and Gen2
collections from 86 to 66. Four 40-frame pairs were noisy: the candidate won
two, with -1.49% median and -3.82% balanced throughput, so no short-run gain is
claimed. Four 160-frame pairs also split two/two, with +0.15% median and +0.82%
balanced throughput while aggregate CPU time was 0.27% higher. Two
reverse-order 400-frame pairs measured -0.77% and +1.89%; balanced throughput
was 0.54% higher and aggregate CPU time was 0.32% lower. Sampled peak working
set did not improve, so no resident-memory reduction is claimed. Luma, chroma,
JSON, stdout, normalized stderr, timestamp-normalized logs, and every ordered
`fileLoc` matched in all A/B gates. Candidate `current` output was identical
across two runs each at `--threads 0`, `1`, default 5, and `20`; a separate
160-frame `v0.4.0` regression matched the same surfaces.

The complex VHS high-boost path now stores its unboosted analytic-real and
filtered-real intermediates in three existing worker-owned workspace arrays
after their earlier phases finish. It still uses the same out-of-place
`PocketFftComplex.Inverse(input, output)` implementation; data types, FFT
arithmetic, expression order, returned-block ownership, and the workspace-pool
cap are unchanged. On the same private local 40 MHz NTSC `BETAMAX_HIFI` sample,
matched 80-frame `gc-verbose` traces reduced sampled managed allocation from
26.446234 to 22.918228 GiB (3.528006 GiB, or 13.34% less), `Double[]`
allocation by 1,791.025 MiB (13.56%), `Complex[]` allocation by 1,793.958 MiB
(17.98%), and Gen2 collections from 82 to 80. One 40-frame smoke pair favored
the baseline by 9.16%, so no short-run gain is claimed. The candidate won three
of four interleaved 160-frame pairs, with 2.44% median and 2.43% balanced
throughput gains; aggregate CPU time fell 0.27% and median peak working set
moved from 1.429 to 1.303 GiB. Both reverse-order 400-frame pairs favored the
candidate by 1.98% and 1.55%; balanced throughput rose 1.77%, aggregate CPU
time fell 0.22%, and median peak working set moved from 1.423 to 1.350 GiB. A
candidate-first 1,000-frame pair was wall-time neutral/slightly negative at
-0.32%, while candidate CPU time fell 0.31% and peak working set moved from
1.437 to 1.381 GiB. Its four 250-frame intervals were
28.44/26.40/25.83/26.22 seconds, with no progressive slowdown or memory growth.
Luma, chroma, JSON, stdout, normalized stderr, timestamp-normalized logs, and
all ordered `fileLoc` values matched in every A/B gate. Candidate `current`
output was identical across two runs each at `--threads 0`, `1`, default 5,
and `20`; a separate 160-frame `v0.4.0` regression matched the same surfaces.

VHS real-FFT sub-deemphasis now writes its high spectrum, inverse high part,
and analytic magnitude into three worker-owned workspace arrays after their
earlier values become dead. The public helper still returns independent
arrays, and the same PocketFFT transforms, SOS filter, data types, and
per-sample expression order remain in use. Matched 80-frame `gc-verbose`
traces reduced sampled managed allocation from 22.934976 to 20.311490 GiB
(2.623486 GiB, or 11.44%), `Double[]` allocation from 11,420.375 to
9,629.972 MiB (15.68%), and `Complex[]` allocation from 8,179.967 to
7,285.327 MiB (10.94%). Gen2 collections moved from 79 to 81, so no GC-count
improvement is claimed.

Four interleaved 160-frame `current --threads 20` pairs were noisy
(-4.44% to +7.97%): paired median throughput was +0.02%, balanced aggregate
throughput was +0.92%, aggregate CPU time was 0.12% higher, and median peak
working set moved from 1.201 to 1.122 GiB. Four 400-frame pairs had a -0.78%
paired median and -1.08% balanced throughput result, essentially flat CPU
(0.03% higher), and median peak working set moved from 1.414 to 1.134 GiB.
The candidate-first 1,000-frame `current` pair improved wall throughput by
0.78%, reduced CPU time by 0.91%, and moved peak working set from 1.318 to
1.127 GiB. Its four 250-frame intervals were
20.53/18.39/17.78/17.77 seconds, with sampled working set
0.597/0.761/0.621/0.788 GiB and no progressive slowdown or growth.

Short 160-frame v0.4.0 pairs were order-sensitive, so no short-run gain is
claimed. Two opposite-order 1,000-frame v0.4.0 pairs both favored the
candidate by 4.28% and 9.78%; balanced throughput rose 7.03%, aggregate CPU
time fell 2.90%, and candidate peak working set stayed at 1.14-1.34 GiB
versus 1.75-1.78 GiB for baseline. Every A/B gate matched luma, chroma, JSON,
stdout, normalized stderr/log, and all ordered `fileLoc` values. The final
five-path matrix also kept Exact v0.4.0 identical to Python `--threads 0` and
all four .NET profile/backend combinations deterministic across default,
1, 5, 10, and 20 workers.

Linear-TBC plan preparation now rents three per-line MAD scratch buffers,
reuses its median scratch, and writes corrected factors directly into the
pooled level-adjust buffer. Derivative, median/MAD, threshold, smoothing,
position, conversion, 16-tap sinc, and ownership semantics remain unchanged.
A 200-plan, 273-line allocation probe fell from 2,240,768 to 30,400 bytes
(98.64%). Three interleaved, order-reversed 160-frame Exact v0.4.0
`--threads 20` A/B pairs against released v1.3.3 moved median wall time from
12.084 to 11.766 s (2.63% lower; 2.71% higher throughput) and average CPU time
from 102.385 to 100.432 s (1.91% lower); peak-memory noise does not support a
memory-reduction claim. Luma, chroma, JSON, stdout, normalized stderr/logs,
and all 320 ordered `fileLoc` values were exact. Separate 40-frame
`--threads 0`, default, and `--threads 20` gates were also exact and
cross-thread deterministic.

Current-mode VHS multi-grid support counting now evaluates each unordered pulse
pair once and updates both counters, instead of testing the two symmetric
ordered pairs separately. Ordered pulse locations, tolerance comparisons,
integer counts, candidate selection, floating-point stages, and detector state
ordering remain unchanged. Five interleaved, order-reversed 160-frame Exact
current `--threads 20` pairs against released v1.3.4 moved median wall time from
15.012 to 14.755 s (1.71% lower), with 1.06% higher balanced aggregate
throughput; the candidate won four of five pairs. Aggregate CPU time was 0.63%
higher, so no CPU-time reduction is claimed. Matched 80-frame sampling traces
moved inclusive `VhsSyncDetector.Detect` time from 2,493 to 2,346 ms (5.89%
lower), which attributes the hotspot change but is not a second end-to-end
claim. Luma, chroma, JSON, stdout, normalized stderr/logs, and every ordered
`fileLoc` matched across all A/B runs. Separate current and v0.4.0
`--threads 0`, default, and `--threads 20` gates were deterministic and exact;
the v0.4.0 serial result also matched the Python oracle on every surface.

The current-mode Super-Gaussian chroma final filter now retains one reusable
instance-local FFT workspace. Concurrent callers receive separate workspaces,
and only one is retained after they finish. Reflection padding, float32
conversion points, DUCC/PocketFFT packet order, mask arithmetic, inverse
normalization, and output ownership are unchanged. All 52 focused FFT/filter
xUnit v3 tests passed, including dirty-buffer reuse and concurrent-call gates.

On the same private local capture, matched 80-frame runtime-counter runs reduced
sampled managed allocation from 20.241 to 17.580 GiB and Gen2 collections from
75 to 73; sampled peak working set did not consistently improve, so no
resident-memory reduction is claimed. Five interleaved 160-frame pairs were
short-run noise and slightly favored the baseline, so no short-run speedup is
claimed. Both opposite-order 400-frame pairs favored the candidate by 2.82%
and 3.00%; balanced aggregate throughput rose 3.00% and aggregate CPU time
fell 1.66%. The 400-frame counter series was non-monotonic rather than growing
with progress. Luma, chroma, JSON, stdout, normalized stderr/logs, and every
ordered `fileLoc` matched in every A/B gate. Separate `current` and `v0.4.0`
checks at `--threads 0`, default 5, and `--threads 20` matched all seven
surfaces; Exact v0.4.0 also matched the Python serial oracle.

The mixed-radix float32 PocketFFT Plan now retains one thread-local value
workspace and one scratch workspace, growing either only when a larger Plan is
encountered. Transform outputs remain caller-owned. Input conversion,
factorization, packet order, per-sample arithmetic, inverse normalization, and
copy boundaries are unchanged. A production-length 239,580-point xUnit v3
probe reduced warm per-call allocation from 11,594,232 to 3,878,568 bytes
(66.55%) while retaining the exact output SHA-256.

Against released v1.3.6, six order-reversed, interleaved 160-frame Exact
`current` `--threads 20` pairs reduced median wall time from 15.009 to
14.553 s (3.04% lower; 3.13% higher throughput) and median CPU time from
110.820 to 107.227 s (3.24% lower). Matched 400-frame runtime-counter runs
reduced sampled managed allocation from 91.712 to 85.902 GiB (6.34%), GC pause
from 1.066 to 0.760 s, Gen0 collections from 765 to 306, and Gen2 collections
from 327 to 313. The retained worker-local buffers raised sampled median
working set from 674 to 742 MiB, so no resident-memory reduction is claimed;
the candidate moved from a 757 MiB first-third median to 699 MiB in the final
third and showed no progressive growth. Its four successive 100-frame
intervals after startup were 8.350, 7.628, 7.495, and 7.599 s.

Every 160-frame A/B run matched luma, chroma, JSON, stdout, normalized
stderr/logs, and all ordered `fileLoc` values. The 400-frame pair also matched
luma, chroma, JSON, normalized logs, and all 800 ordered `fileLoc` values.
Separate Exact `current` and v0.4.0 gates at `--threads 0`, default 5, and
`--threads 20` matched all seven surfaces and remained deterministic.

The Betamax FSC notch now filters the decoder-owned video array in place after
its previous contents become dead. The public helper still returns an
independent array. Notch design, padding choice, pooled odd-extension buffer,
forward/backward IIR arithmetic, reversal order, and final copy-back are
unchanged. Against released v1.3.7, the candidate won five of six
order-reversed, interleaved 160-frame Exact `current --threads 20` pairs.
Median wall time moved from 14.667 to 14.495 s (1.17% lower; 1.19% higher
throughput), median CPU time moved from 107.227 to 106.398 s (0.77% lower),
and balanced aggregate throughput improved 1.02%.

Matched 80-frame `gc-verbose` traces reduced sampled managed allocation from
18.245 to 17.388 GiB (4.70%), sampled `Double[]` allocation from 9.690 to
8.820 GiB (8.98%), and Gen2 collections from 73 to 64. Matched 400-frame
runtime-counter runs reduced total allocation from 84.082 to 79.278 GiB
(5.71%) and Gen2 collections from 325 to 294. Their wall times were
33.179/33.382 s and median working sets were 647.4/649.1 MiB
baseline/candidate; the candidate also had one 1.392 GiB transient sample, so
neither a long-run speedup nor a resident-memory reduction is claimed from
that pair. Candidate 100-frame intervals were
10.365/7.632/7.594/7.638 s including startup, with no progressive slowdown or
growth.

Every 160-frame A/B run matched luma, chroma, JSON, stdout, normalized
stderr/logs, and all 320 ordered `fileLoc` values. The 400-frame outputs also
matched luma, chroma, JSON, normalized logs, and all 800 ordered `fileLoc`
values. Twelve additional Exact gates covered `current` and v0.4.0 at
`--threads 0`, default 5, and `--threads 20`; all seven surfaces matched and
remained cross-thread deterministic. All 60 refreshed Exact/IPP overview
matrix runs also passed their recorded compatibility references.

The VHS real-FFT sub-deemphasis analytic stage now reuses two worker-owned
full-length complex buffers after their earlier contents become dead. A new
preallocated DUCC real-to-full-spectrum overload keeps the same input packing,
plan/packetized transform selection, root multiplication, conjugate mirroring,
negative-zero handling, Hilbert mask, inverse transform, data types, and
per-sample magnitude expression. Public helpers retain their independent
array ownership. Focused xUnit v3 coverage checks both the 1,024-point Plan and
32,768-point packetized branches, invalid/overlapping buffers, SciPy bit
hashes, and warm allocation.

Against released v1.3.8, matched 80-frame `gc-verbose` traces reduced sampled
managed allocation from 17.388 to 13.900 GiB (20.06%), sampled `Complex[]`
allocation from 7.122 to 3.622 GiB (49.14%), and Gen2 collections from 64 to
57 (10.94%). Six order-reversed, interleaved 160-frame Exact `current
--threads 20` pairs had 13.523/13.654 s baseline/candidate wall medians;
balanced aggregate wall and CPU time changed by only +0.14% and +0.08%.
The candidate won four of six wall-time pairs, so no repeatable throughput
gain is claimed.

Across two opposite-order 400-frame runtime-counter pairs, total allocation
fell from 161.302 to 125.900 GiB (21.95%) and Gen2 collections fell from 656
to 487 (25.76%). Aggregate candidate wall time was 1.52% higher, so no
long-run speedup is claimed either. One candidate run had a transient
1,381 MiB working-set sample that did not repeat; the reverse-order candidate
peaked at 731 MiB. A separate 1,000-frame candidate run completed all 2,000
fields in 73.267 s, with 648 MiB median and 1,032 MiB maximum sampled working
set. Its first/last-third medians were 664/642 MiB; after the 9.247 s startup
interval, nine successive 100-frame intervals stayed between 7.038 and
7.152 s. This supports bounded memory and no progressive slowdown, not a
resident-memory reduction claim.

Every A/B run matched luma, chroma, raw JSON, stdout, normalized stderr/logs,
and all ordered `fileLoc` values. Twelve additional Exact gates covered
v0.4.0 and `current` at `--threads 0`, default 5, and `--threads 20`; all
seven surfaces matched baseline and remained cross-thread deterministic. All
60 refreshed Exact/IPP matrix runs also passed their recorded compatibility
references.

The following VHS complex-RF filter workspace pass keeps the full filtered
spectrum in the existing capped worker workspace and applies the optional RF
MTF multiply in place. It removes up to two per-block `Complex[]` outputs while
retaining the exact complex-multiply expression and fallback, full-complex
FFT/Hilbert lifetimes, coefficients, data types, and ordered output state.
General and special-value in-place tests cover lengths 0, 1, 2, 3, and 32,769.
The warm PAL RF block allocated 2,098,184 bytes under its new 2,400,000-byte
ceiling.

Against released v0.4.0-1.3.9, a matched 80-frame `gc-verbose` trace reduced
sampled managed allocation from 13.900 to 10.415 GiB (25.1%), sampled
`Complex[]` allocation from 3.622 GiB to 135 MiB (96.4%), and Gen2
collections from 57 to 44 (22.8%). Six order-reversed 160-frame pairs matched
all seven compatibility surfaces and had 14.069/14.167 s baseline/candidate
wall medians; median CPU time changed by -0.44%, so this pass remains
throughput-neutral.

Two opposite-order 400-frame runtime-counter pairs reduced aggregate
allocation from 127.255 to 93.795 GiB (26.29%) and Gen2 collections from 496
to 261 (47.38%). Counter-instrumented aggregate wall time was 3.57% higher,
so no long-run speedup is claimed. The candidate's highest sampled working
set was 1.23 GiB; in the reverse-order run its first/last-third medians were
905/908 MiB. A separate 1,000-frame candidate run matched the prior release
checkpoint for luma, chroma, raw JSON, normalized logs, and all 2,000 ordered
`fileLoc` values while reducing allocation from 160.525 to 119.650 GiB
(25.46%) and Gen2 collections from 628 to 353 (43.79%). Its first/last-third
working-set medians were 747/782 MiB, its maximum was 1,198 MiB, and the nine
post-startup 100-frame intervals stayed between 7.239 and 7.493 s. The
workspace is therefore bounded and does not progressively slow down, but the
lower GC pressure can produce higher periodic resident-memory samples; this
is not a resident-memory reduction claim.

The next Exact pass pools the padded double-precision SOS odd-extension
workspace. Every logical element is initialized before use; padding,
double-precision arithmetic, section/sample order, forward/reverse order, and
the independent exact-length result remain unchanged. The temporary array is
returned in `finally`, while the no-padding path retains its previous owned
copy. The existing bit-exact section-major test now also requires a warm
4,096-sample call to allocate less than 40,000 bytes.

Against released v0.4.0-1.4.0, a matched Exact `current --threads 20`
80-frame `gc-verbose` trace reduced sampled managed allocation from 10.415 to
8.659 GiB (16.9%), sampled `Double[]` allocation from 8.837 to 7.083 GiB
(19.8%), and Gen2 collections from 44 to 26. The former 1.054 GiB
`OddExtension` allocation caller disappeared. Six order-reversed 160-frame
pairs matched luma, chroma, raw JSON, stdout, normalized stderr/logs, and every
ordered `fileLoc`; 13.682/13.602 s baseline/candidate wall medians are treated
as throughput-neutral. Twelve additional gates covered v0.4.0 and `current`
at `--threads 0`, default 5, and `--threads 20`.

A separate 1,000-frame candidate run matched the released checkpoint for
luma, chroma, raw JSON, normalized stderr/logs, and all 2,000 ordered
`fileLoc` values. Total allocation fell from 119.650 to 98.679 GiB (17.5%),
GC pause from 1.124 to 1.029 s, and Gen2 collections from 353 to 319. Its
first/last-third working-set medians were 680/727 MiB; a transient 1,420 MiB
peak later returned below 900 MiB. The nine post-startup 100-frame intervals
stayed between 6.800 and 6.989 s. This supports bounded memory and no
progressive slowdown, not a resident-memory or stable throughput claim.

The following Exact pass adds an internal destination form of double SOS
forward/backward filtering while leaving the public independently owned result
API unchanged. In the VHS RF high-boost paths, the worker-owned `RawEnvelope`
buffer has finished its envelope-input role and is not used again until the
later demodulation stage, so the SOS result and unchanged float32-envelope
scaling are written there before the existing FFT. Padding, double arithmetic,
section/sample order, reversal order, FFT input, and output ownership remain
unchanged, and no additional retained array is introduced. The destination
path is section-major bit-exact and its warm 4,096-sample allocation is gated
below 4,096 bytes.

Against main `a184450`, matched Exact `current --threads 20` 80-frame
`gc-verbose` traces reduced sampled managed allocation from 8.667 to
7.797 GiB (10.0%), sampled `Double[]` allocation from 7.091 to 6.221 GiB
(12.3%), and Gen2 collections from 36 to 33. The roughly 0.9 GiB high-boost
SOS result-allocation chain disappeared. Six order-reversed 160-frame pairs
matched all seven compatibility surfaces; 13.297/13.375 s baseline/candidate
wall medians and 101.750/101.273 s CPU medians are treated as
throughput-neutral. Twelve additional gates covered v0.4.0 and `current` at
`--threads 0`, default 5, and `--threads 20`.

Matched 1,000-frame counter runs also matched luma, chroma, raw JSON,
normalized stderr/logs, and all 2,000 ordered `fileLoc` values. Total
allocation fell from 98.021 to 88.428 GiB (9.8%); GC pause was
0.994/0.998 s and Gen2 collections were 289/286. Baseline/candidate wall times
were 72.129/71.352 s, but a single ordered pair does not establish a speedup.
The candidate's first/last-third working-set medians were 676/874 MiB and a
late 1,484 MiB peak reflected collection timing; no resident-memory reduction
is claimed. Its nine post-startup 100-frame intervals stayed between 6.787
and 7.035 s, supporting bounded memory and no progressive slowdown.

The latest Exact pass reuses one more worker-local buffer without changing
numeric semantics. Sub-deemphasis first keeps its high-pass signal in
`Real`, analytic magnitude in `Imaginary`, and FFT scratch in the existing
complex workspace. Once the demodulated input has been transformed into
`demodSpectrum`, the old `RawEnvelope`/compact-demod contents are dead.
The unchanged double-precision amplitude SOS result is therefore written to
`RawEnvelope` through the existing destination API instead of allocating a
new full-block `Double[]`. The non-workspace public API, padding choice,
section/sample and reversal order, post-SOS expressions, and returned output
ownership are unchanged. An xUnit v3 warm-block allocation test covers this
lifetime and deterministic output.

Against main `583d062`, a matched Exact `current --threads 20` 80-frame
`gc-verbose` trace reduced sampled managed allocation from 7.797 to
6.926 GiB (11.2%), sampled `Double[]` allocation from 6.221 to 5.342 GiB
(14.1%), and Gen2 collections from 33 to 29. The sub-deemphasis SOS
result-allocation caller disappeared. Six order-reversed 160-frame pairs
matched luma, chroma, raw JSON, stdout, normalized stderr/logs, and every
ordered `fileLoc`. Their baseline/candidate wall medians were
13.845/13.404 s and CPU medians were 103.258/100.133 s, but the longer runs
below did not reproduce a stable throughput gain. Twelve additional gates
covered v0.4.0 and `current` at `--threads 0`, default 5, and `--threads 20`.

Two opposite-order 1,000-frame counter pairs also matched luma, chroma, raw
JSON, normalized stderr/logs, and all 2,000 ordered `fileLoc` values.
Allocation fell by 11.87-12.12%, GC pause by 1.28-7.44%, and Gen2
collections by 21.50-24.35%. Candidate wall time was 0.13% and 1.35% higher,
so this pass is classified as throughput-neutral. Candidate first/last-third
working-set medians were 679/773 MiB and 804/659 MiB, with 1,517/1,516 MiB
peaks; sampled resident memory did not improve. The combined post-startup
100-frame intervals stayed between 6.924 and 7.195 s and neither run grew
progressively, supporting bounded memory rather than a resident-memory claim.

The subsequent Exact PocketFFT pass removes one final full-buffer copy from
each complex transform. The mixed-radix plan now returns whichever
worker-local value buffer contains the last radix pass, and the existing
sample-by-sample `Complex` conversion consumes that buffer immediately.
Radix selection, packet order, arithmetic expressions, normalization order,
data types, and thread-local ownership are unchanged. A deterministic
32,768-point odd-pass xUnit case locks forward and inverse bit hashes to
`950264D00BFBB9E577539DD1CD8BAE660B3EA9EAC82DD131794CAA108341061B`
and
`3CC982F0D601FD7B484FECAF26FC912F0DDF77593B378E1E45ED9B9EBB1EF5B5`.

Twelve real gates covered v0.4.0 and `current` at one, default-five, and
20 workers; six interleaved 160-frame pairs also matched luma, chroma, raw
JSON, stdout, normalized stderr/logs, and every ordered `fileLoc`. A CPU trace
removed the 374.725 ms `Memmove` caller attributed to the old final copy.
Across two opposite-order 1,000-frame counter pairs, combined process CPU time
fell from 1,122.171 to 1,111.078 s (0.99%), while combined wall time was
effectively unchanged at 151.782/151.650 s. Sampled allocation was also
effectively unchanged at 7.437/7.420 GiB with no new allocation type. This
pass is therefore retained as a CPU and memory-bandwidth reduction and
classified as throughput-neutral.

The next Exact PocketFFT pass changes only integer indexing in radix-8's
zero-frequency butterfly. `Pass8FirstIndex` now receives the input/output
base and stride calculated once by its caller, then advances through the eight
locations by addition instead of recomputing `InputIndex` and `OutputIndex`
for every load and store. The `i > 0` twiddled loop, every floating-point
expression, butterfly and rotation order, normalization, data type, and buffer
owner remain unchanged. The existing 64-point and 32,768-point forward/inverse
bit-hash tests cover both even- and odd-pass mixed-radix plans.

Eight order-reversed independent-process microbenchmark pairs over 2,000
preallocated 32,768-point transforms all favored the candidate. Their
baseline/candidate medians were 1,445.939/1,296.320 ms (10.35% less), with
identical checksums and 40 bytes of timing overhead. Twelve real gates covered
v0.4.0 and `current` at one, default-five, and 20 workers. Six order-reversed
160-frame pairs matched luma, chroma, raw JSON, stdout, normalized
stderr/logs, and every ordered `fileLoc`; baseline/candidate wall medians were
15.613/15.586 s, while CPU medians fell from 107.594 to 103.797 s (3.53%).
A sampled CPU trace shifted attribution between `Execute` and `Pass8`, so the
stable aggregate is used: mixed-radix Plan self-time fell from 22.338 to
20.553 s (8.0%), and total double-precision PocketFFT self-time fell 5.0%.

Two opposite-order 1,000-frame counter pairs also matched luma, chroma, raw
JSON, normalized stderr/logs, and all 2,000 ordered `fileLoc` values.
Combined process CPU time fell from 1,056.06 to 1,017.89 s (3.61%), while
combined wall time moved from 138.962 to 138.308 s (0.47%). Sampled allocation
was effectively unchanged at 154.186/153.769 GiB; GC and resident-memory
samples varied with collection timing, so no allocation or resident-memory
reduction is claimed. Candidate post-startup 100-frame intervals stayed
between 6.607 and 6.794 s in both orders without progressive growth. The pass
is retained as a repeatable CPU reduction and classified as
whole-pipeline-throughput-neutral.

The following Exact allocation pass keeps float32 SOS initial state in two
flat spans. Up to 32 sections, the steady-state and scaled-state spans are
stack-backed; larger uncommon filters retain bounded heap fallbacks. The
scaled span is overwritten for the backward pass instead of allocating a
second scaled matrix. SOS coefficients, float32 conversion points, steady-state
expressions, scale operations, sample-major section order, reversal order, and
output ownership are unchanged. Existing one-, two-, four-, and generic-section
bit-hash tests remain exact, and the warm in-place allocation gate is tightened
from 4,096 to 512 bytes.

Eight order-reversed independent-process microbenchmark pairs each ran 10,000
preallocated 4,096-sample two-section filters. Six favored the candidate;
baseline/candidate medians were 238.658/234.193 ms (1.87% less), while
allocation fell from 2,400,040 to 720,040 bytes, or about 240 to 72 bytes per
call. Twelve real gates covered v0.4.0 and `current` at one, default-five, and
20 workers. Six order-reversed 160-frame pairs matched luma, chroma, raw JSON,
stdout, normalized stderr/logs, and every ordered `fileLoc`; wall medians were
13.134/13.257 s and CPU medians were 98.500/100.969 s, so no whole-pipeline
speedup is claimed.

In matched 80-frame allocation traces, the former 18.511 MiB across 182 sampled
`System.Single[,]` events disappeared. Total sampled allocation was neutral at
6.918/6.920 GiB and Gen2 starts were 27/28 because unrelated large arrays
dominated. Two opposite-order 1,000-frame counter pairs also matched every
applicable artifact/log surface and all 2,000 ordered `fileLoc` values.
Combined counter-reported allocation fell from 155.200 to 154.364 GiB (0.54%);
Gen0/Gen2 collections moved from 1,515/492 to 1,491/486 and GC pause from
1.857 to 1.777 s. Combined CPU time was effectively unchanged at
1,078.188/1,077.734 s. Combined wall time moved from 145.978 to 144.539 s,
but the shorter pairs did not confirm a stable throughput gain. Candidate
post-startup 100-frame intervals stayed between 6.877 and 7.121 s in both
orders. Working-set samples varied with collection timing, so no resident-
memory reduction is claimed. The pass is retained as a bounded small-object
and GC reduction and classified as whole-pipeline-throughput-neutral.

The next Exact allocation pass converts float32 SOS coefficients directly into
a destination span. Up to 32 sections, the coefficient span is stack-backed;
larger uncommon filters retain the previous heap allocation as a bounded
fallback. The six coefficient casts, steady-state expressions, scale
operations, sample-major section order, reversal order, output ownership, and
filter object lifetime are unchanged. Focused tests cover the existing one-,
two-, four-, and generic-section bit hashes plus the 33-section fallback. The
warm in-place allocation gate is tightened from 512 to 64 bytes.

Eight order-reversed independent-process microbenchmark pairs each ran 10,000
preallocated 4,096-sample two-section filters. Baseline/candidate allocation
was 720,040/40 bytes, or about 72/0 bytes per call. Their medians were
231.419/231.810 ms and only two pairs favored the candidate, so timing is
classified as neutral. Twelve real gates covered v0.4.0 and `current` at one,
default-five, and 20 workers. Six interleaved 160-frame pairs matched luma,
chroma, raw JSON, stdout, normalized stderr/logs, and every ordered `fileLoc`;
baseline/candidate wall medians were 13.390/13.410 s and CPU medians were
101.609/101.477 s.

Matched 40-frame/80-field allocation traces removed the baseline's 2.538 MiB
across 25 sampled `FloatSosSection[]` events. Total sampled allocation moved from
3.723 to 3.714 GiB, with identical 30/1/18 Gen0/Gen1/Gen2 starts, but unrelated
large arrays dominate that sample. Two opposite-order 1,000-frame counter
pairs also matched every applicable artifact/log surface and all 2,000 ordered
`fileLoc` values. Combined wall time was 145.840/145.455 s.
Counter-reported allocation was 154.112/154.379 GiB and varied at a scale far
larger than the eliminated arrays, so no whole-pipeline allocation, resident-
memory, or speedup claim is made. Candidate post-startup 100-frame intervals
stayed between 6.905 and 7.159 s without progressive slowdown. The pass is
retained as a bounded small-object elimination and classified as
whole-pipeline-throughput-neutral.

The next Exact PocketFFT pass writes each mixed-radix packet result back into
its existing worker-local `Complex32` packet span. The plan still copies input
into its thread-static `Value[]` workspace, runs the same `Execute`, and then
writes the result back to the packet. Roots, twiddles, arithmetic, packet
order, normalization, data type, and ownership are unchanged. Existing SciPy
fixture, serial/parallel, and owned-output hashes remain exact, while the warm
large-multipass allocation gate is tightened from 4 MiB to 64 KiB.

Eight order-reversed independent-process microbenchmark pairs each ran 200
239,580-point transforms with 20 workers. Baseline/candidate median allocation
was 776,701,680/5,062,408 bytes, a 99.35% reduction, with identical output
hashes. Wall medians were 649.18/631.10 ms, but the split was four wins each,
so isolated timing is classified as neutral. Twelve real gates covered
v0.4.0/current at one, default-five, and 20 workers. Six interleaved 160-frame
pairs matched luma, chroma, raw JSON, stdout, normalized stderr/logs, and every
ordered `fileLoc`. Short-pair wall medians were 14.03/13.66 s with three wins
each, and CPU medians were 102.97/103.95 s, so short timing is also neutral.

Matched 40-frame/80-field allocation traces reduced sampled total allocation
from 3.706 to 3.366 GiB, events from 15,543 to 12,026, and Gen0 starts from 30
to 6. Sampled `Complex32[]` allocation fell from 364.328 MiB across 3,559
events to 2.747 MiB across three events; the baseline's 163.263 MiB across
1,606 `Plan.Transform` caller events disappeared. Gen1 starts were 1/1 and
Gen2 starts were 14/17, so no Gen2 improvement is claimed across differing
collection timing. The traces matched every output, JSON, ordered `fileLoc`,
and normalized-log surface.

Two opposite-order 1,000-frame pairs reduced combined wall time from 144.526
to 136.988 s, a 5.22% reduction and 1.055x throughput. Counter allocation fell
from 154.553 to 140.373 GiB (9.17%), GC pause from 1.688 to 0.920 s (45.49%),
and Gen0 collections from 1,483 to 304 (79.5%). Every artifact/log surface and
all 2,000 ordered `fileLoc` values remained exact. Candidate post-startup
100-frame intervals stayed between 6.500 and 6.860 s, sampled working sets
remained bounded, and there was no progressive slowdown. No Gen2-timing or
resident-memory reduction is claimed. The pass is retained as an
allocation/GC and stable long-run throughput improvement. The five-path
overview above was refreshed with 60 .NET runs, all of which passed the same
compatibility gate.

The next Exact current-chroma allocation pass retains the exclusive
`double[]` returned by NTSC burst deemphasis and transfers that buffer directly
to current phase compensation. The previous path exposed the same array only
as a read-only span and then cloned the complete field before an in-place
upconversion. The removed operation was therefore a pure ownership copy:
filtering, float32 conversion points, phase arithmetic, sample order, and
caller-owned input remain unchanged. PAL, v0.4.0, disabled phase correction,
and missing-field-parity paths retain their previous behavior. A
production-sized xUnit v3 allocation gate verifies that only two field-sized
`double[]` outputs plus the final `ushort[]` fit within the allowed budget; the
old third field copy exceeds that bound.

The local Release solution built with zero warnings and errors, and all 1,118
tests passed. Final GitHub Actions discovered the same 1,118 tests: 1,117
passed and one optional AC3 dependency test was skipped, with zero failures.
Twelve real gates covered v0.4.0/current at one, default-five, and 20 workers.
Six idle interleaved 160-frame pairs matched luma, chroma, raw JSON, stdout,
normalized stderr/logs, and every ordered `fileLoc`; baseline/candidate wall
medians were 13.513/13.224 s and CPU medians were 103.375/101.617 s. Candidate
wall time won four of six pairs, but short-run timing is not used as a
whole-pipeline speedup claim.

Two idle opposite-order 1,000-frame counter pairs also matched every
artifact/log surface and all 2,000 ordered `fileLoc` values. Combined
counter-reported allocation fell from 140.055 to 132.532 GiB, a 7.523 GiB or
5.37% reduction. Combined wall time was neutral at 142.323/142.893 s, GC pause
was neutral at 1.100/1.116 s, and sampled working sets remained bounded.
Candidate post-startup 100-frame intervals stayed between 6.794 and 6.981 s
without progressive slowdown. The pass is retained as a deterministic
full-field allocation reduction and classified as whole-pipeline-throughput
neutral, so the five-path overview remains the preceding valid idle 60-run
matrix.

The following Exact/current pass applies the 1H or 2H chroma comb directly to
the decoder-owned field buffer. The public `ApplyNtscComb` and `ApplyPalComb`
APIs still copy their caller input. The internal path retains at most one NTSC
line or two PAL lines in `ArrayPool<double>` storage so each delayed source
line remains available while the field is overwritten in forward order.
Float32 conversion points and the PAL/NTSC subtraction order are unchanged.
A bit-exact xUnit v3 test covers both systems at both precision modes, and the
production-sized allocation gate now permits only one field-sized `double[]`
plus the final `ushort[]`. The former extra comb field exceeds that budget.

The local Release solution built with zero warnings and errors, and all 1,119
tests passed. Twelve real gates covered v0.4.0/current at one, default-five,
and 20 workers. They matched luma, chroma, raw JSON, stdout, normalized
stderr/logs, and every ordered `fileLoc` across baseline/candidate and all
thread counts. Six interleaved 160-frame current pairs were also exact; their
baseline/candidate wall medians were 13.299/13.318 s and CPU medians were
101.664/103.000 s, with two candidate wins, so short timing is classified as
neutral.

Two opposite-order 1,000-frame current counter pairs matched every checked
artifact/log surface and all 2,000 ordered `fileLoc` values in all four runs.
Combined counter-reported allocation fell from 132.089 to 125.320 GiB, a
6.769 GiB or 5.12% reduction. Combined wall time fell from 142.414 to
140.016 s, a 1.68% reduction and 1.017x throughput; GC pause fell from 1.067
to 1.000 s. Candidate post-startup 100-frame intervals stayed between 6.68
and 6.89 s. Maximum sampled working set was 1,481.6 MiB versus 1,467.4 MiB,
so no resident-memory reduction is claimed, but both runs remained bounded
without progressive slowdown. The refreshed five-path overview used 60
interleaved .NET runs, and all 60 passed the existing compatibility references.

The latest Exact field-resampling allocation pass adds destination-buffer
forms without changing the 16-tap sinc expression, float32 conversion point,
sample order, or resampling plan. The stateful VHS CLI sequence decoder owns
one exact-length luma workspace and one separate chroma workspace because its
bounded chroma task can overlap luma rendering. Public `Decode()` and retained
`DecodeFields()` results still allocate and return independent
`ChromaBurstSamples`; only internal non-retaining CLI sequence results omit
that buffer. The direct UInt16 path remains allocation-free for the double
field, and public resampling APIs retain their independent-output contract.
Shape changes replace the single retained buffer in each role, so
retained memory is bounded rather than proportional to decode length.

The Release solution built with zero warnings and errors, and all 1,120 xUnit
v3 tests passed. Six real profile/thread gates covered Exact v0.4.0 and
`current` at `--threads 0`, default-five, and `--threads 20`. The candidate
matched baseline luma, chroma, raw JSON, stdout, normalized stderr/logs, and
every ordered `fileLoc`; public consecutive `Decode()` results also retain
distinct chroma-burst arrays. The refreshed five-path matrix ran all four
Exact/IPP profile combinations at default, 1, 5, 10, and 20 workers three
times each; all 60 runs matched their existing compatibility references.

Two opposite-order Exact-current/20-worker 1,000-frame pairs reduced combined
counter allocation from 126.226 to 110.873 GiB (12.16%), GC pause from 1.015
to 0.885 s (12.83%), and wall time from 141.015 to 140.405 s (0.43%, 1.004x
throughput). Two opposite-order current/default 500-frame pairs reduced
allocation from 62.638 to 55.606 GiB (11.23%), GC pause by 11.91%, and wall
time by 1.05%. The corresponding v0.4.0/default pairs reduced allocation by
10.80%, GC pause by 22.65%, and wall time by 0.88%; v0.4.0/20-worker
1,000-frame pairs reduced allocation by 10.40% and wall time by 2.11%.

A candidate-only v0.4.0/20-worker 3,000-frame run completed in 164.593 s.
Its four working-set-quarter medians were 793.94, 799.89, 747.98, and
772.25 MiB; the maximum was 1,400.8 MiB and the final sample was 957.3 MiB.
The first and last steady ten 100-frame intervals had 5.492 and 5.440 s
medians. This supports bounded retained memory and no progressive slowdown;
collection-timing peaks are not presented as a resident-memory reduction.

The next Exact complex-demodulation workspace pass writes the compact
non-escaping raw FM result into the worker's exact-length `RawEnvelope` buffer
and the optional diff-demod repair result into its otherwise-dead `Real`
buffer. Retained `DemodRaw` and analytic diagnostics still own independent
arrays. Data types, atan approximation, sample expressions, SIMD lane order,
FFT behavior, repair order, pool cap, and serial state/output commit are
unchanged. Two focused xUnit v3 tests compare the destination-buffer repair
against an independently reconstructed allocating oracle and verify retained
arrays bit for bit after repeated workspace leases. The full Release suite
passed all 1,122 tests.

Six short real profile/thread gates covered Exact v0.4.0 and `current` at
`--threads 0`, default-five, and `--threads 20`. Eight current/20-worker
500-frame runs, four current/20-worker 1,000-frame runs, and four 500-frame
runs for each of v0.4.0/20-worker, current/default, and v0.4.0/default matched
baseline luma, chroma, raw JSON, stdout, normalized stderr/logs, and every
ordered `fileLoc`. The refreshed five-path matrix also completed 60/60
compatible runs, with three runs in each of its 20 cells. A self-contained
validation `decode.exe` built from the same production code had SHA-256
`333D051E361FE425EA893EE819129BB1CFC9249CF77E29746C94252F263D19D0`;
a 100-frame strict gate against the measured `98ADB0...7A1` executable matched
all seven artifact/log surfaces and ordered `fileLoc`.

The four opposite-order current/20-worker 500-frame pairs moved median wall
time from 37.905 to 38.002 s (+0.26%) while median CPU fell from 294.055 to
288.711 s (-1.82%). The longer v0.4.0/20-worker, current/default, and
v0.4.0/default pair medians were respectively 30.834/31.169,
67.850/67.632, and 59.756/59.871 s. Their wall changes therefore ranged from
-0.32% to +1.10%, so this pass is classified as throughput-neutral rather
than a speedup.

Across two opposite-order current/20-worker 1,000-frame counter pairs,
allocation fell from 111.461 to 88.022 GiB (21.03%) and GC pause from 0.994
to 0.791 s (20.43%). Instrumented wall time moved from 147.734 to 150.428 s
(+1.82%), and maximum sampled working set was 1,472.19/1,507.75 MiB, so no
resident-memory or wall-time improvement is claimed. A candidate-only
current/20-worker 3,000-frame run completed in 209.767 s with 133.2 GiB
allocated and 1.146 s GC pause. Its first/middle/final 1,000-frame totals
were 72.00/69.11/68.50 s; working-set-quarter medians were
721.87/1,304.73/601.61/864.18 MiB with a 1,462.11 MiB maximum. The
non-monotonic working set and faster final third support bounded memory and
no progressive slowdown.

The latest Exact NTSC chroma ownership pass transfers the internal,
non-retained sequence-resampling buffer directly into burst deemphasis. Public
decode APIs and retained diagnostic paths still copy their input and return
independent arrays; in-place deemphasis and the subsequent phase/comb stages
are used only for an explicitly owned internal buffer. Data types,
multiplication expressions, loop order, phase/comb order, and ordered field
commit are unchanged.

Candidate commit `0270f101aa21d2f2a3c5679365eb4a4e9655d77c` produced a
self-contained `decode.exe` with SHA-256
`2626A0F82B89D7F4025F41600C034048E61F89F399B08746071968B6E3E619B5`;
the measured baseline executable was
`FFCB821C0E46885B7735A9ADCA1AA1ACD6454DC43C84ED671EC7B2EB31DA261C`.
The Release build completed with zero warnings and errors, and all 1,126
standard xUnit v3 tests passed. Exact v0.4.0 and `current` both passed strict
`--threads 0`, omitted/default-five, and `--threads 20` gates. Cross-thread
luma, chroma, raw JSON, stdout, normalized stderr/logs, and every ordered
`fileLoc` were identical. The refreshed five-path homepage matrix also matched
its compatibility references in all 60 candidate runs.

Matched 100-frame allocation traces reduced total sampled allocation from
4,994,031,752 to 4,611,843,896 bytes (7.65%) and sampled `Double[]` allocation
from 4,061,498,384 to 3,679,431,952 bytes (9.41%). Across the two
burst-deemphasis call sites, the baseline trace recorded 202 full-field
`double[]` allocation events of roughly 1.9 MiB each; both sites disappeared
from the candidate trace. The 100-frame output itself contains 200 fields.

Six opposite-order current/20-worker 160-frame pairs moved median wall time
from 13.398 to 13.451 s (+0.40%) while CPU time fell from 103.133 to 101.398 s
(-1.68%), so the short-run throughput result is neutral. The corresponding
v0.4.0 pairs improved wall time from 10.672 to 10.563 s (1.02%, 1.010x) and
CPU time from 97.453 to 95.141 s (2.37%). Two opposite-order 1,000-frame pairs
for each profile remained exact and completed without interruption:
`current` improved from 72.151 to 70.557 s (2.21%, 1.023x) with effectively
neutral CPU time, while v0.4.0 improved from 55.700 to 55.200 s (0.90%,
1.009x) and reduced CPU time by 2.00%. The pass is retained primarily for its
allocation reduction, with a modest long-run throughput benefit. The refreshed
homepage matrix contains 60 new candidate runs; its Python columns retain the
existing fixed-sample oracle measurements.

The latest Exact PocketFFT cache pass prevents duplicate cold-key plan
construction. `ConcurrentDictionary.GetOrAdd` can invoke an expensive value
factory more than once when workers race on a missing key, even though only one
value is published. The four PocketFFT implementations now use a shared
single-creation cache: populated reads retain the concurrent fast path, while
cold misses coordinate through independent per-key gates so unrelated lengths
can still build concurrently. Same-key factory reentrancy is rejected, and
factory exceptions are not cached and can be retried. Cache keys, retained plan
cardinality, FFT data types, coefficients, arithmetic order, and thread-local
scratch remain unchanged.

A matched 100-frame allocation trace reduced complex plan construction from
30 calls and 9,746,000 sampled bytes to one call and 458,672 bytes. Real plan
construction fell from 30 calls and 3,031,704 sampled bytes to one call and
131,120 bytes. This removes 12,187,912 bytes (12.19 MB) of duplicate sampled
construction; total sampled allocation fell from 4,611,843,896 to
4,598,751,544 bytes. The two float-real plan builds represented two actual
lengths and remained unchanged.

The candidate passed the standard Release build and all 1,130 xUnit v3 tests,
including focused concurrent-factory, retry-after-failure, mixed-radix,
workspace, and FFT tests. Twelve strict main/candidate runs covered Exact
v0.4.0 and `current` with `--threads 0`, omitted/default-five workers, and
`--threads 20`. Luma, chroma, raw JSON, stdout, normalized stderr/logs, and
ordered `fileLoc` matched within every profile. All 60 refreshed
Exact/IPP-fast, v0.4.0/`current`, and default/1/5/10/20-worker matrix runs also
matched their backend/profile references. This round used the current main
binary as its byte-exact oracle; main's direct Python v0.4.0 `g4315520
--threads 0` evidence remains the transitive upstream reference rather than
being rerun.

Five alternating 100-frame Exact `current`/20-worker pairs moved median decode
time from 9.12 to 8.58 seconds (5.9% lower). Mean decode time moved from 9.150
to 8.756 seconds (4.3% lower) because run-to-run variance remained visible, so
the result is classified as a modest cold-start/end-to-end gain. A separate
1,000-frame candidate run completed all 2,000 fields in 69.862 seconds and
matched the current-main luma, chroma, raw JSON, normalized stderr/log, and
ordered `fileLoc` references. Sampled working set had a 1,479.6 MiB maximum and
first/final-third medians of 659.9/593.2 MiB, supporting bounded memory without
progressive growth.

### RF stream output buffer reuse

The current Exact candidate gives the VHS stream decoder ownership of four
block-sized outputs: demodulated video, RF envelope, low-pass video, and
float32 chroma. A block keeps exclusive ownership while it is cached or is
still participating in span assembly. Only after both uses end can its buffer
set return to a concurrent pool. Public `DecodePreparedBlock` results remain
independently allocated. Failed workers, cancelled prefetch, cache
invalidation, replacement, eviction, stream changes, and disposal all return
eligible sets; an eviction needed by the active parallel assembly is deferred
until copying completes. Serial assembly likewise defers a low-numbered block
that evicts itself from a full cache. A throwing deferred diagnostic returns
its harvested block before propagating the exception, and an in-flight
prefetch cancellation test holds a later input read open while verifying every
completed output is reclaimed. The idle pool retains at most 48 sets, matching the
decoded cache's maximum configured capacity: 16 base entries plus up to 32
prefetch allowance entries. Active decoded or prefetched blocks and the current
span's leases are not part of that retained count, so total live sets can
temporarily exceed 48. DSP types, coefficients, expressions, operation order,
padding, and field commit order are unchanged.

On the same 100-frame Exact `current`/20-worker trace, total sampled allocation
fell from 4,598,751,544 to 566,944,304 bytes: 4,031,807,240 fewer bytes, or
87.67%. The final trace created 60 output sets during initial concurrency and
then reused them; the retained subset never exceeds 48. The corresponding
1,000-frame counter run reduced integrated allocation from 40.641 to
3.844 GiB. This optimization reduces GC traffic rather than changing the
decoder's numerical path.

Ten opposite-order 100-frame Exact `current`/20-worker pairs moved median
decode time from 8.72 to 8.58 seconds (1.61% lower) and mean decode time from
8.710 to 8.617 seconds (1.07% lower). Median wall time was 1.72% lower. The
gain is deliberately classified as modest because run-to-run variance remains
visible; the allocation reduction is the primary result.

The reviewed candidate passed a zero-warning Release build and all 1,169 xUnit
v3 tests locally. The GitHub Actions command requires at least 1,169
discoverable tests; a clean runner may skip the optional LD AC3
reference-pipeline test when the external AC3 tools are unavailable. Twelve
strict main/candidate runs covered Exact v0.4.0 and `current`
with `--threads 0`, default-five, and `--threads 20`; all luma, chroma, raw
JSON, stdout, normalized stderr/logs, and ordered `fileLoc` values matched.
All 60 refreshed Exact/IPP-fast, v0.4.0/`current`, and default/1/5/10/20-worker
matrix runs also matched their profile/backend references.

The final 1,000-frame Exact `current`/20-worker run completed 2,000 fields in
69.475 seconds and matched the current-main artifacts, normalized diagnostics,
and ordered `fileLoc`. Working set had a 711.6 MiB maximum and
first/final-third medians of 627.5/703.7 MiB, so the bounded-memory gate passed
without progressive unbounded growth. As in the preceding pass, current main
is the direct A/B oracle and its verified Python v0.4.0 `g4315520 --threads 0`
evidence remains the transitive upstream reference.

### Retained Exact hot-path specialization

The next retained Exact pass combines four independently gated changes. The
PocketFFT radix-8 kernel selects forward or inverse direction outside its hot
loop. VHS chroma UInt16 conversion uses AVX2/SSE4.1 while preserving the
`+32767`, finite check, saturation, truncation, and scalar-tail rules. The
`current` burst fitter uses a pinned 16-lane AVX/FMA dot-product shape with the
same lane expressions and reduction tree as the verified scalar form. The
`current` sync quantiles reuse their partition and narrow the 32-bit sortable
prefix with deterministic 16+16 radix selection before the existing final
Quickselect; signed zero, infinity, and NaN use the original sequential path.
The worker-local histogram is fixed at about 768 KiB per workspace. No
cross-field state, output ordering, data type, or numerical operation order was
moved.

The performance commits passed all 1,169 xUnit v3 tests with native hardware,
with AVX2 disabled, and with all hardware intrinsics disabled. Twelve native
and twelve scalar strict profile/thread runs covered Exact v0.4.0 and
`current` at `--threads 0`, default-five, and `--threads 20`; all luma, chroma,
raw JSON, stdout, normalized stderr/log, and ordered `fileLoc` surfaces matched,
including 84 cross-native/scalar comparisons. The final reviewed source also
passed a zero-warning Release build and all 1,169 tests locally.

Against release `v0.4.0-1.4.2`, six balanced 100-frame Exact
`current`/20-worker pairs reduced median wall time from 9.637 to 8.627 seconds
(10.48% lower, 11.71% more throughput), with the candidate faster in all six.
Four serial pairs reduced the median from 50.377 to 37.300 seconds (25.96%
lower, 35.06% more throughput), with four wins. Every compared artifact and
diagnostic surface was exact.

Two opposite-order 1,000-frame pairs each completed 2,000 fields and matched
luma, chroma, raw JSON, normalized logs, and all ordered `fileLoc` values.
Mean wall time fell from 72.405 to 62.752 seconds (13.33% lower, 15.38% more
throughput). Mean integrated allocation changed from 3.799 to 3.806 GiB
(+0.18%), GC pause stayed effectively neutral, and candidate working set
remained below 706 MiB. Its later five 100-frame intervals were faster than
the first five in both run orders, so the bounded-memory gate showed no
progressive slowdown.

The final six-path table uses executable
`7F3434744E2120282C9888CF66AF730A184A103465561DE5A2B3F63B0022202F`
built from `d526ef5`. All 60 final runs passed their complete profile/backend
references. The Python columns retain the same fixed-host measurements:
v0.4.0 remains the strict `g4315520 --threads 0` oracle, and merged Python
PR341 remains the profile peer for `current`.

### Double PocketFFT packet-buffer reuse

The double-precision DUCC packet path now reuses its first- and second-pass
`Complex[]` packets per worker thread. Each buffer grows only to that worker's
largest requested packet and is sliced to the exact active length. Gather,
transform, twiddle, scatter, normalization, data type, and arithmetic order are
unchanged; no decoder or field state crosses a worker boundary.

The latest-main candidate passed a zero-warning Release build and all 1,224
xUnit v3 tests on native hardware and with AVX2 disabled. Twelve private local
PAL VHS RF runs covered Exact v0.4.0 and `current` at `--threads 0`, default,
and `--threads 20`; luma, chroma, raw JSON, stdout, normalized stderr/log, and
ordered `fileLoc` matched across baseline, candidate, and worker counts.

Two opposite-order 1,000-frame Exact `current`/20-worker pairs completed 4,000
fields per build with every compared surface identical. Combined allocation
fell 0.18% and GC pause fell 7.94%; combined wall time changed from 156.090 to
156.173 seconds (+0.05%), so throughput is explicitly classified as neutral.
Candidate working set remained bounded below 834 MiB. First-half and last-half
100-frame medians stayed between 7.07 and 7.09 seconds, with no progressive
slowdown, OOM, or unbounded growth.

### Bounded libsndfile RF input reuse

The compact streaming pipeline now exposes one internal reusable-input
ownership contract to the packed LDS and direct raw-FLAC loaders. The
libsndfile implementation retains at most 48 exact-length 32,768-sample
`double[]` blocks, or about 12 MiB, which covers one PAL field's parallel block
batch plus prefetch without accepting oversized arrays. Every native PCM16
sample is still converted by the same `output[i] = samples[i]` assignment.
Fallback output is copied into loader-owned storage before it can enter the
pool, and public or diagnostic reads continue to return independent arrays.

An initial candidate also reused libsndfile blocks during sequential decode;
four default-worker pairs showed an approximately 6% regression, so that path
was rejected. The retained implementation enables libsndfile reuse only for
parallel block decode and prefetch. After this guard, four default-worker pairs
were mixed and changed median wall time from 61.43 to 61.61 seconds (+0.29%),
which is classified as neutral. Packed LDS retains its existing sequential
reuse policy.

On the same private local PAL VHS RF capture, an exact-parameter 100-frame
allocation trace fell from 3,561,003,024 to 986,114,768 sampled bytes (72.31%).
Luma, chroma, raw JSON, normalized logs, and ordered `fileLoc` matched. Two
opposite-order 1,000-frame `current`/Exact/20-worker pairs then completed 4,000
fields per build with every compared surface identical. Combined allocation
fell from 43.96 to 16.63 GiB (62.18%), GC pause from 0.380 to 0.253 seconds
(33.46%), and Gen2 collections from 151 to 40. Combined wall time changed from
155.74 to 155.31 seconds (0.28%), so throughput remains neutral. Candidate
working set stayed below 772 MiB; its steady 100-frame intervals remained near
7.0 seconds without progressive slowdown, OOM, or unbounded growth.

Twelve baseline/candidate gates covered Exact v0.4.0 and `current` at explicit
`--threads 0`, omitted/default threads, and `--threads 20`. Luma, chroma, raw
JSON, stdout, normalized stderr/log, and ordered `fileLoc` matched both the
pre-change build and every worker-count reference.

### Deterministic parallel current VHS sync quantiles

The Exact `current` VHS detector now distributes its high- and middle-prefix
radix histogram scans across at most four worker-local slabs for fields of at
least 524,288 samples. Reductions use a fixed worker order. Exceptional-value
fallback, final source-order prefix collection, Quickselect, floating-point
expressions, and all cross-field state remain unchanged and serial. Each
simultaneously active or retained parallel workspace adds at most about 2 MiB
and is cleared before reuse; product concurrency remains bounded by the
existing field-worker scheduler. Smaller fields and one-worker decoding keep
the previous serial path.

The three new focused tests cover bit-exact serial/parallel quantiles while
reusing dirty workspaces across one- and two-bucket cases, exceptional values
at worker boundaries, ignored poison values beyond the active backing-array
length, and warm caller-thread allocation. The zero-warning Release build and
all 1,234 xUnit v3 tests passed on native hardware, with AVX2 disabled, and
with every hardware intrinsic disabled; separate local logs retain each test
summary. Twelve real-RF gates covered Exact
v0.4.0 and `current` at explicit `--threads 0`, omitted/default threads, and
`--threads 20`; luma, chroma, raw JSON, stdout, normalized stderr/log, ordered
`fileLoc`, and cross-thread determinism matched the pre-change build.

On the same private local PAL VHS RF capture, the one-worker short gate and
four 160-frame default-five pairs were throughput-neutral. Four 10-worker
pairs improved median wall time from 20.029 to 19.766 seconds (1.31%), with
three candidate wins. Six 20-worker pairs all favored the candidate and moved
the median from 18.622 to 17.739 seconds (4.74% less wall time; 4.97% more
throughput). A sampled trace showed the former serial selector work split into
the two worker-local histogram loops while final selection remained serial.

Two opposite-order 1,000-frame/2,000-field pairs matched luma, chroma, raw
JSON, normalized stderr/log, and every ordered `fileLoc`, and reduced mean wall
time from 78.559 to 76.495 seconds (2.63%; 2.70% more throughput). Separate
thread gates also matched stdout and cross-thread determinism. Combined sampled
allocation moved from 16.570 to 16.661 GiB and GC pause from 0.247 to 0.259
seconds, so no allocation or GC improvement is claimed. The maximum
once-per-second candidate working-set sample was 779.98 MiB, Gen2 collections
moved from 42 to 40, and both runs completed without progressive sampled
growth or OOM.

### Decoder-owned PAL chroma upconversion

The latest Exact `current` PAL path applies the heterodyne multiplier directly
to the decoder-owned resampled chroma field. The public read-only API still
allocates an independent output. Before mutating internal storage, the new path
validates phase-table lengths and proves that the normalized NumPy-style line
ranges are non-overlapping and either sorted or contain one legal tail-to-head
wrap. Unsupported sequences fall back before the first write. Multiplication,
the `(double)(float)` conversion point, line order, gap zeroing, later filters,
comb, gain, and cross-field state are unchanged.

Focused xUnit v3 coverage compares every output `double` bit with the allocating
reference for sorted ranges, gaps, and the real PAL single-wrap layout. A
duplicate-range case proves fallback leaves its input untouched, and a
production-sized complete PAL decode matches the copying path while allocating
less than 256 KiB after warm-up with caller-owned UInt16 output. The zero-warning
Release build and all 1,236 tests passed on native hardware, with AVX2 disabled,
and with every hardware intrinsic disabled.

On the same private local PAL VHS RF sample, a matched 80-frame allocation trace
reduced sampled object bytes from 872,958,736 to 415,617,272 (52.39%) and sampled
allocation amount from 2,065,610,264 to 1,606,599,696 (22.22%). The baseline's
235,891,312-byte `UpconvertChroma` and 221,894,168-byte field-copy allocation
stacks disappeared. Six balanced 80-frame Exact `current`/20-worker pairs were
mixed at four candidate wins and two losses. Mean wall time changed from 11.942
to 11.776 seconds (1.39%), so short throughput is classified as near-neutral.

A matched 1,000-frame/2,000-field counter pair reduced managed allocation from
8.254 to 3.009 GiB (63.54%), GC pause from 0.134 to 0.113 seconds (15.63%), Gen2
collections from 20 to 2, and maximum sampled working set from 773.29 to
439.86 MiB (43.12%). Candidate first/last-quarter working-set medians were
405.84/406.51 MiB, with no progressive growth or OOM. Wall time changed from
74.130 to 73.217 seconds (1.23%; 1.25% more throughput), which remains a scoped
single-pair observation. Luma, chroma, raw JSON, normalized stderr/log, and all
2,000 ordered `fileLoc` values matched. Twelve separate gates covered Exact
v0.4.0/current at explicit zero, omitted/default, and 20 workers and also matched
stdout and cross-thread determinism.

### Bounded parallel current burst-prefix analysis

The `current` chroma phase pass now probes the state-independent line prefix in
fixed contiguous ranges across at most four workers. The final 16-line track
rotation check, phase-sequence assembly, color-killer summary, all reductions,
and every cross-field state transition remain in input order on the decode
thread. A worker exception discards the speculative prefix and reruns the
original serial path, preserving the prior exception and recovery behavior.
v0.4.0 and one-worker decoding retain the serial path.

An earlier 20-way prototype raised short-run CPU use but regressed 40-frame wall
time, so it was rejected. Direct ten-pair 4-versus-8-worker testing was
throughput-neutral; the four-worker internal cap was retained to leave RF and
FFT work runnable under `--threads 20`. Six interleaved 160-frame Exact
`current`/20-worker pairs all favored the retained candidate. Median wall time
fell from 11.94 to 10.68 seconds (10.6%) while median active cores rose from
6.11 to 6.69 and median process CPU time fell from 72.98 to 71.43 seconds.

Two opposite-order 1,000-frame/2,000-field Exact `current`/20-worker comparisons
moved wall time from 64.36/65.15 to 56.31/56.05 seconds, a 12.5-14.0% reduction.
Luma, chroma, raw JSON, stdout, normalized stderr/log, and every ordered
`fileLoc` matched across baseline, candidate, explicit-zero/default/20-worker
gates, and the refreshed Exact/IPP-fast matrix. The final matrix also produced
one deterministic hash set per current backend across default, 5, 10, and 20
workers. All 1,244 xUnit v3/Microsoft.Testing.Platform tests passed.

No allocation improvement is claimed: sampled baseline allocation was
2.079/2.085 GiB versus 2.125/2.237 GiB for the candidate. Candidate maximum
working-set samples were 393.8/515.0 MiB, but first/last-third medians stayed
at 386.4/390.1 and 490.0/493.6 MiB. Both long runs completed without progressive
growth or OOM; the extra parallel results and scheduling work remain bounded per
field.

### Bounded parallel current ACC segments

The `current` automatic chroma-gain pass now processes independent, monotonic
chroma segments in fixed contiguous ranges across at most eight workers. Raw-gain
construction, outlier limits, smoothing, final noise FMA reduction, mean
amplitude, cross-field state, and output submission remain in input order.
Parallel work is enabled only when each sync-tip window is wholly inside its own
segment and scratch is at most 4,096 samples; unusual or overlapping input,
v0.4.0, and one-worker calls retain the serial path. The prior public CLR method
signature is unchanged. Worker exceptions are captured and rethrown in
input-partition order. Worker-local float/double median scratch is rented before
output mutation and returned on every exit.

Six interleaved 160-frame Exact `current`/20-worker pairs reduced median wall
time from 11.15 to 10.47 seconds (6.1%) while median active cores rose from 6.67
to 7.58. A separate 1,000-frame/2,000-field pair moved from 55.851 to 53.003
seconds (5.1%); luma, chroma, raw JSON, stdout, normalized stderr/log where
applicable, and all ordered `fileLoc` values matched. Candidate allocation was
1.918 GiB and its first/last-third working-set medians were 382.9/383.0 MiB,
with a 386.7 MiB maximum and no progressive growth or OOM.

The cap-8 audit's then-current matrix used six interleaved runs for default, 1,
5, 10, and 20 workers in Exact and IPP-fast, for 60 Release runs. Every compatibility
hash set contained one value. The zero-warning Release build and all 1,262 xUnit
v3/Microsoft.Testing.Platform tests passed.

### Bounded current Super-Gaussian FFT parallelism

The existing packet-independent PocketFFT stages in the `current`
Super-Gaussian chroma final filter now use at most twelve internal workers when
the requested worker count permits it. Packet decomposition,
padding, masks, arithmetic, transform order, output order, and the serial path
are unchanged. The filter still retains one bounded instance-local workspace;
concurrent calls obtain isolated temporary workspaces.

Across twelve matched short cap-4/cap-8 pairs, combined median wall time fell
from 14.67 to 14.29 seconds (2.57%) while median active cores rose from 6.50 to
6.88. Two opposite-order 1,000-frame/2,000-field comparisons reduced the
combined median from 54.345 to 52.408 seconds (3.56%). Luma, chroma, raw JSON,
stdout, normalized stderr/log, and every ordered `fileLoc` matched. Candidate
first/last-third working-set medians were 428.6/433.1 MiB and 382.4/382.7 MiB
across the two runs, with maxima of 435.9 and 386.5 MiB and no progressive
growth or OOM.

An earlier cap-12 experiment against an older pipeline was rejected. After the
later bounded pipeline and output-buffer changes, a fresh exact-HEAD cap-8 versus
cap-12 audit retained cap 12: it won five of six interleaved 160-frame pairs and
reduced median wall time from 14.43 to 13.88 seconds (3.8%). Median process CPU
time also fell from 109.33 to 103.95 seconds, while median peak working set stayed
effectively flat at 787.95 versus 787.69 MiB. Luma, chroma, raw JSON, stdout,
normalized stderr/log, and ordered `fileLoc` matched in every run. The refreshed
30-run `current` matrix also produced one hash for every compatibility surface in
every cell.

### Decoder-local current burst-probe buffers

The `current` chroma phase analyzer now reuses exact-length padded-burst arrays
from four decoder-local, lock-free slots. The four-slot bound matches the
existing burst-probe worker cap. Every active sample is overwritten before the
same SOS filter and fitter run; arithmetic, filter order, worker count,
cross-field state, and exception ordering are unchanged. Buffers are returned
on every exit, and no more than four arrays remain retained.

One 80-frame Exact `current --threads 20` baseline/candidate screen was
wall-neutral at 9.393/9.371 seconds. Process CPU time moved from 74.406 to
67.922 seconds and peak working set from 435.1 to 430.5 MiB. This single pair
is retained as CPU/allocation evidence, not a throughput claim, so the full
performance matrix above is unchanged. Luma TBC, chroma TBC, raw JSON, stdout,
normalized stderr/log, and zero/default/20-worker determinism all matched.

### Value-type classified sync pulses

`ClassifiedSyncPulse` is now an immutable record struct. Kind, pulse geometry,
ordering flags, list order, and all downstream numerical expressions remain
unchanged; pulse classification and VBlank refinement no longer allocate one
managed object for every accepted pulse. A focused xUnit v3 test locks the
value-type contract and stored values.

One matched 80-frame Exact `current --threads 20` baseline/candidate screen
reduced wall time from 10.309 to 10.003 seconds (2.97%). Process CPU time rose
from 69.453 to 70.797 seconds (1.93%), while peak working set stayed flat at
435.3/435.0 MiB. Luma TBC, chroma TBC, raw JSON, stdout, normalized stderr/log,
and every ordered `fileLoc` matched. This single pair is recent optimization
evidence and does not replace the full matrix medians above.

### Parallel high-worker VHS inverse staging

For Exact VHS `current` requests above the existing 12-worker outer prefetch
cap, the decoder now prepares the NumPy-compatible analytic spectrum in the
same serial order and then overlaps its complex inverse FFT with the independent
real inverse FFT. Each operation reads a disjoint source spectrum and writes a
disjoint worker-local destination. The default, 1- through 12-worker, v0.4.0,
non-VHS, `--gnrc`, and `ipp-fast` paths retain serial staging. No sample-length
buffer was added; outer block concurrency remains bounded at 12. The serial
path's real inverse exception priority is also retained, and a synchronous task
scheduling failure falls back to the original serial sequence.

One fixed 200-frame Exact `current --threads 20` pair on the same private local
PAL VHS fixture measured main `eec3658` against the branch candidate:

| Metric | main `eec3658` | Branch candidate | Change |
| --- | ---: | ---: | ---: |
| Wall time | 11.945 s | 11.480 s | 3.89% lower / 1.041x throughput |
| Process CPU time | 95.359 s | 90.828 s | 4.75% lower |
| Active cores | 7.98 | 7.91 | effectively unchanged |

Exit status, field count, luma TBC SHA-256, chroma TBC SHA-256, raw JSON
SHA-256, stdout SHA-256, normalized stderr, normalized log, and every ordered
`fileLoc` matched. The harness did not return a usable peak-working-set value,
so this audit makes no measured-memory claim. Its different 200-frame scope is
kept separate from the repeated 40-frame matrix.

### Reused float32 FFT root tables

`PocketFftReal32.Plan` now retains its immutable unity-root table for factor
construction and every complexified forward/inverse recombination.
`PocketFftComplex32` shares immutable `SinCos2PiByN` tables by root length
across large multipass transforms and rooted packet plans. That cache uses a
32-entry FIFO bound; eviction only drops the cache reference, so an active
transform keeps its immutable table safely. Scratch arrays, transform state,
data types, operation order, and float conversion points are unchanged. The
root keys match the already retained plan dimensions.

Matched final 200-frame Exact `current --threads 20` traces reduced sampled
allocation amount from 579,283,536 to 541,701,824 bytes (6.49%) and sampled
object bytes from 391,712,736 to 373,960,808 (4.53%). The final bounded-cache
three-pair 1,000-frame comparison against main `ced6afb` was mixed and is
treated as throughput-neutral: 1/3 pairs were faster, median main/candidate
wall time was 44.924/44.985 seconds (+0.14%, 0.999x), and median CPU time was
332.672/333.734 seconds (+0.32%). Median peak working set was 391.9/396.5 MiB;
one candidate sampling outlier reached 614.3 MiB, so no memory-reduction claim
is made from these short-lived process samples. Every run matched all nine
compatibility surfaces. The final 60-run matrix and 12-run determinism matrix
also passed. The focused 16 KiB unit-test thresholds measure warmed allocations
on the calling thread only; process-wide allocation statements above come from
the matched trace, not those unit tests.

### Managed precise VHS threshold-scan AVX

The scalar second-pass current VHS threshold scan now classifies four adjacent
double comparisons per AVX step with ordered non-signaling predicates. Falling
and rising masks are still committed in increasing scalar index order, and the
unchanged valid-grid predicate retains its candidate order, modulo expression,
thresholds, and early exit. The scalar tail and no-AVX fallback use the original
expressions. No FMA, reduction reordering, worker, or retained sample buffer was
introduced.

The focused scalar/AVX oracle covers eight lengths plus a fixed vector/tail case
with quiet/signaling NaNs, infinities, signed zero, and exact crossing indices.
The 34-case detector class passes on normal hardware; with all hardware
intrinsics disabled, 33 pass and the AVX-only case is explicitly skipped. In
matched 400-frame
IPP-fast traces, sampled `VhsSyncDetector.DetectFiltered` CPU time fell from
1263.307 to 1173.588 ms (7.1%). Four opposite-order 400-frame pairs had
16.98/16.47-second baseline/candidate medians; the conservative paired-difference
gain was 1.3%, so the larger unpaired gap is not presented as causal.

Two opposite-order 1,000-frame IPP-fast `current --threads 20` pairs reduced
median wall time from 36.341 to 35.822 seconds (1.43%, 1.0145x throughput) and
median process CPU time from 222.078 to 220.789 seconds (0.58%). Median active
cores moved from 6.11 to 6.16. All four 2,000-field runs matched luma, chroma,
raw JSON, stdout, normalized stderr/log, and every ordered `fileLoc`; no
progressive memory growth or OOM occurred, although two pairs are insufficient
for a memory-reduction claim. The refreshed 30-run current matrix also produced
one hash for each of those seven compatibility surfaces in every backend and
worker setting.

### Direct Complex32 final-workspace return

The float32 mixed-radix packet plan alternates each radix pass between two
worker-local `Value[]` arrays. When the final pass lands in scratch, `Execute`
now returns that array directly instead of copying the complete result back to
the input workspace before the existing `Complex32` writeback. Input loading,
factor and packet order, roots, twiddles, every arithmetic expression, float32
conversion points, output ownership, and thread-static workspace bounds are
unchanged. A 1,000-frame CPU trace attributed 6,432 samples, about 1.0% of all
samples, to the removed `Plan.Execute` memmove.

All 53 mixed-radix xUnit v3 cases passed both normally and with all hardware
intrinsics disabled. They include SciPy fixture hashes, odd/even radix counts,
serial/parallel transforms, owned buffers, repeatable warm workspaces, and the
239,580-point field transform. Eight reversed-order 200-transform microbenchmark
runs retained one output hash but were scheduling-noisy and are classified as
throughput-neutral rather than used for the end-to-end claim.

Four interleaved 160-frame Exact `current --threads 20` pairs matched luma,
chroma, raw JSON, stdout, normalized stderr/log, and every ordered `fileLoc`;
two pairs favored each side, while the candidate wall median was 1.93% lower
and its CPU median was 12.85% lower. The deciding two opposite-order 1,000-frame
pairs both favored the candidate. Combined wall time fell from 79.767 to 78.634
seconds (1.42% lower, 1.0144x throughput), and combined process CPU time fell
from 650.047 to 629.078 seconds (3.23%). Candidate peak working set was
387.8-393.7 MiB versus 391.8-395.3 MiB for main, with no progressive growth or
OOM; this is bounded-memory evidence, not a resident-memory reduction claim.

Twenty-four short gates covered Exact and IPP-fast, v0.4.0 and `current`, at
explicit zero, default-five, and 20 workers. Every baseline/candidate and
cross-thread artifact/log surface matched. The refreshed 30-run current matrix
used default, 1, 5, 10, and 20 workers in three reordered passes; each backend
produced one deterministic hash set. The unchanged Python and .NET v0.4.0
columns retain their previous same-host measurements, while the two current
columns at that checkpoint used that candidate's single-file executable.

### AVX2 Hilbert real-spectrum scaling

The VHS/LD analytic-signal preparation pass multiplies every double-precision
complex spectrum value by a real Hilbert multiplier. Its managed AVX2 path now
loads four multipliers, duplicates each into the matching real/imaginary lanes,
and performs two independent four-double multiplies. It does not use FMA or a
reduction and does not change expression order. If any complex component or
real multiplier in the group is `NaN` or infinity, the complete four-value
group uses the original `Complex * double` expression so .NET exceptional-value
semantics remain exact. Hosts without AVX2 and scalar tails use that same
expression.

The final isolated 32,768-value kernel median fell from 711.718 to 47.936 ms
across eight reversed-order trials (14.847x), with exact output bits and no
warm allocation. The ranges were scheduling-noisy, and this remains kernel
evidence only. Two opposite-order 1,000-frame Exact
`current --threads 20` pairs split one win each. Combined wall time moved from
83.678 to 83.419 seconds (0.31%, 1.0031x throughput) and combined process CPU
time from 661.375 to 660.375 seconds (0.15%), so end-to-end throughput is
classified as neutral. Candidate peak working set stayed bounded at or below
393.6 MiB with no progressive growth or OOM; this does not establish a
resident-memory reduction.

Fifteen focused xUnit v3 cases passed with native AVX2, AVX2 disabled, and all
hardware intrinsics disabled. Three final real-input intrinsic gates matched
the native candidate against both disabled modes. Twenty-four Exact/IPP-fast
gates covered v0.4.0/current at explicit zero, default-five, and 20 workers;
all baseline/candidate and cross-thread luma, chroma, raw JSON, stdout,
normalized stderr/log, and ordered `fileLoc` surfaces matched. Forty-five
candidate matrix runs refreshed Exact v0.4.0, Exact `current`, and IPP-fast
`current` at default, 1, 5, 10, and 20 workers in three reordered passes; each
profile/backend set produced one deterministic hash set. IPP-fast v0.4.0 kept
its unaffected prior measurements. The final executable was built from commit
`3740bf1`, based on `8409b1f`, and its SHA-256 was
`0F119B82507E8ACB5FF0CF8EE4C407436671828B1981CC9FCDC824B2F34ACD19`.

### Current chroma-burst fitter loop fusion

The opt-in `current` chroma-burst least-squares fitter now prepares cosine,
sine, residual, and Jacobian values in one ascending-index loop instead of
five separate passes. It also accumulates the four non-vector dot sums in
that same original ascending order and removes the temporary theta and sine
buffers.
Data types, per-sample expressions, conversion points, the OpenBLAS-compatible
dot reduction, solver order, worker boundaries, and state transitions are
unchanged; no FMA or reassociation was introduced.

Nine alternating process runs of 100,000 complete `Fit` calls reduced the
median from 1,625.603 to 1,489.642 ms (8.36%, 1.091x throughput). Every run kept
the same checksum and 2,080-byte process-level allocation count. This is
isolated-kernel evidence, not the end-to-end claim.

Three interleaved 1,000-frame Exact `current --threads 20` pairs on the same
private local 40 MHz PAL VHS sample numerically favored the candidate. Mean
wall time moved from 39.572 to 39.416 seconds (0.39% lower), but the 95%
paired-difference interval of -0.011 to 0.322 seconds includes zero and mean
process CPU time moved from 322.464 to 326.474 seconds (1.24% higher).
End-to-end throughput is therefore classified as neutral, with no
CPU-efficiency claim. Sampled peak working set remained below the previously established
741 MiB bound and showed no unbounded allocation path; the noisy samples do
not support a resident-memory reduction claim.

All six paired long runs and one final-source confirmation run matched luma,
chroma, raw JSON, stdout, timing-normalized stderr, timestamp-normalized logs,
and every one of 2,000 ordered `fileLoc` values. Twelve additional short gates covered Exact v0.4.0 and `current` at
explicit zero, default-five, and 20 workers; every baseline/candidate and
cross-thread surface was exact. The three pinned fitter tests passed both
normally and with AVX disabled, and the full Release suite completed with
1,397 passes and four IPP-runtime-dependent skips. The homepage table remains
at its last complete five-path matrix refresh because substituting this
different 1,000-frame window would make its startup-inclusive ratios
incomparable.

### Bounded sync and chroma phase containers

The sync analyzer now seeds four `List<ClassifiedSyncPulse>`/`List<double>`
instances from already known input or slice bounds. Pulse order, filtering,
VBlank state, and every numerical expression are unchanged. Initial capacity is
capped at 65,536 entries, so malformed high-noise input cannot reserve an
unbounded classified-pulse backing array up front; a genuinely larger accepted
result grows normally. The existing focused xUnit v3 case now feeds one million
rejected pulses and gates classification below 2 MiB and refinement, including
its required raw-pulse copy, below 12 MiB. The `current`
chroma phase builder now allocates its final `ChromaPhaseLine[]` once and fills
the independent prefix directly in parallel before the original ordered state
machine continues in that same array. The former prefix array, list backing
array, and final `ToArray()` copy are gone. Parallel probe exception slots are
created only after an exception and still rethrow the lowest input-line failure.

In seven alternating 10,000-iteration sync microbenchmark pairs, median wall
time fell from 264.306 to 251.985 ms (4.66%) and allocation from 739,600,040 to
401,680,040 bytes (45.69%), with one checksum in all runs. Three reordered
1,000-frame Exact `current --threads 20` pairs were scheduling-noisy and are
classified as end-to-end throughput-neutral. The chroma phase microbenchmark's
five 100,000-call pairs reduced median allocation from 6,547,233,136 to
5,881,975,184 bytes (10.16%); three pairs favored the candidate and the paired
median wall-time change was 1.23% lower.

Two opposite-order 1,000-frame chroma pairs both favored the candidate. Combined
wall time moved from 79.645 to 79.056 seconds (0.74% lower), while combined CPU
time rose 0.43%, so no CPU-efficiency claim is made. Every long run matched luma,
chroma, raw JSON, stdout, normalized stderr/log, and all 2,000 ordered `fileLoc`
values. A supporting fixed-duration 12-second steady trace reduced sampled
allocation amount from 247,776,232 to 228,539,336 bytes (7.76%) and removed the
former classified-pulse `AddWithResize` leaf from the leading sites; because the
trace windows were not normalized to an identical field count, that percentage
is not presented as a complete-pipeline allocation claim.

A broader sync scratch-workspace experiment was rejected despite 36.62% lower
microbenchmark allocation: its two opposite-order real pairs were 1.02% and
1.74% slower. That code was fully reverted rather than trading throughput for a
better allocation-only number. The public Python comparison matrix remains
unchanged because these modest same-revision results do not constitute a full
five-profile refresh.

The final Release build completed with zero warnings and errors. The standard
xUnit v3/Microsoft.Testing.Platform run discovered all 1,401 tests: 1,397 passed,
none failed, and four IPP-runtime-dependent cases were explicitly skipped.

### Eight-way current burst probing and double-path Exact SOS reuse

The latest `current` chroma phase prefix can use up to eight independent burst
workers instead of four. The decoder-local exact-length burst cache has the
same eight-slot hard bound; every active sample is overwritten before use, all
buffers are returned on every exit, and retained storage cannot grow with file
length. This bounded worker-cap change accounts for the measured CLI gain
below. The production CLI already filtered its owned float32 burst buffer in
place before this phase. Separately, the public double-precision Exact
`AnalyzeFieldPhase` path now writes its final burst SOS back into that
exclusively owned buffer through the existing destination API, avoiding one
temporary result allocation in that API path. No CLI speed or memory claim is
attributed to the double-path change. Coefficients, odd extension,
forward/reverse order, floating-point expressions, fitter order, exception
priority, and every cross-field state transition are unchanged.

Seven process-level burst-prefix trials per build retained one checksum. Median
wall time moved from 197.780 to 153.813 ms, a 22.2% reduction. One preliminary
eight-worker memory run reached 617.3 MiB peak working set and was rejected as
non-representative after it did not reproduce under the controlled final
source. The accepted sampled baseline/candidate pair measured 382.6/400.7 MiB
peak working set and 398.9/405.9 MiB peak private bytes, while wall time moved
from 30.169 to 28.426 seconds. The roughly 7 MiB private-byte increase is a
bounded scheduling tradeoff rather than a memory-reduction claim.

Three interleaved 1,000-frame Exact `current --threads 20` release-binary pairs
all favored the candidate. Independent medians moved from 29.576 to 28.673
seconds (3.05%, 1.031x throughput), process CPU time from 273.375 to 275.297
seconds (+0.70%), and effective cores from 9.24 to 9.60 (+3.87%). All six long
runs matched luma TBC, chroma TBC, raw JSON, stdout, timing-normalized stderr,
timestamp-normalized logs, and every ordered `fileLoc`.

A separate six-run gate covered the unmodified `--threads 0` path, omitted
default-five workers, and `--threads 20`. Baseline and candidate hashes matched,
and all three worker modes remained deterministic. The refreshed 60-run
Exact/IPP-fast, v0.4.0/`current`, default/1/5/10/20-worker matrix produced one
hash for all seven captured surfaces in every cell. Focused SOS workspace tests
passed 45/45 and current chroma parallel tests passed 10/10; the standard xUnit
v3 suite passed all 1,448 tests.

</details>

<!-- SECTION: build -->

## Build and test

Requirements:

- .NET SDK `11.0.100-preview.6.26359.118` (pinned by `global.json`)
- Visual Studio 2026 for IDE use
- Visual Studio C++ Build Tools and a Windows SDK when building the optional
  Intel IPP bridge
- `ffmpeg` and `ffprobe` on `PATH` for container inputs outside the narrowly
  gated direct 40 kHz mono PCM16 raw-FLAC native-input route (including streams
  above `Int32.MaxValue` samples), and for that
  route's recovery fallback after a native open/seek/decode failure or a
  reported-length boundary
- clean size-eligible raw-FLAC RF input on a native-input route, default HiFi FLAC
  output, and LD `--write-test-ldf` use the bundled libsndfile without FFmpeg;
  all retain their documented fallback or compatibility boundaries

```powershell
.\tools\build-ipp-native.ps1
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore --minimum-expected-tests 1485
dotnet test --project tests\VHSDecode.Tests\VHSDecode.Tests.csproj -c Release --no-build --no-restore --coverage --coverage-output coverage.cobertura.xml --coverage-output-format cobertura
```

The first command includes the optional `ipp-fast` native artifact; omit it for
an Exact-only build. The script uses `vswhere` to locate MSBuild, restores the pinned
`intelipp.static.win-x64` NuGet package, builds the sequential static bridge,
and rejects external IPP, OpenMP, oneTBB, or Visual C++ runtime DLL
dependencies. Intel oneAPI does not need to be installed on the development or
deployment computer. Binary-only single-file releases embed
`vhsdecode_ipp.dll` and the applicable third-party notices without adding
sidecar license files. An Exact-only build may omit the native build step.

The current formal Release build has zero warnings and errors. The xUnit v3
project exposes **1,485** independently discoverable tests to both
`dotnet test` and Visual Studio Test Explorer.

<!-- SECTION: usage -->

## Usage

Show facade or standalone help:

```powershell
dotnet run --project src/VHSDecode.Cli -- vhs --help
dotnet run --project src/VHSDecode.Cli -- cvbs --help
dotnet run --project src/VHSDecode.Cli -- ld --help
dotnet run --project src/VHSDecode.Cli -- hifi --help
```

After a Release build, use either facade dispatch or an apphost alias:

```powershell
src\VHSDecode.Cli\bin\Release\net11.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net11.0\vhs-decode.exe [upstream options] input output
decode.exe vhs --dsp-backend ipp-fast [upstream options] input output
```

The last form selects the optional fast backend explicitly when approximate
output is acceptable.

Use the matching `cvbs`, `ld`, or `hifi` command and its upstream v0.4.0
arguments. Run `--help` for the exact accepted surface.

<!-- SECTION: preview -->

## Outputs and live preview

Video decode output files are opened with read/write sharing compatible with
upstream Python behavior. While a decode is active:

- `.tbc` and `_chroma.tbc` can be opened and read as they grow.
- published `.tbc.json` recovery snapshots can be parsed by another process.
- LD `.pcm`, `.efm`, and `.prefm` sidecars can be read concurrently.
- allowing readers does not add a copy or lock on the write hot path; practical
  performance depends mainly on competing storage I/O from the preview tool.

The writer remains the authority for file length and snapshot publication.
Readers must tolerate a growing TBC file and replace/reopen JSON snapshots.

Snapshot publication retries transient sharing and access failures after
100 ms, 500 ms, and 2 seconds. A failed checkpoint no longer stops later
snapshots. If the final canonical JSON still cannot be replaced, decode exits
nonzero with `OUTPUT INCOMPLETE`, preserves the append-only
`.tbc.json.fields.tmp` journal, and keeps a complete generated snapshot as
`.tbc.json.final` (or a numbered `.final.N`) when one is available. The
previous canonical snapshot is left intact. Successful final JSON bytes and
the v0.4.0 completion lifecycle remain unchanged.

<!-- SECTION: verification -->

## Verification

The test suite is standard xUnit v3, not a custom test executable. Coverage
includes:

- CLI/help/error snapshots and format/parameter matrices
- deterministic DSP and floating-point compatibility fixtures
- serial/worker output and state-transition comparisons
- TBC, chroma, JSON, SQLite, audio, and sidecar lifecycle tests
- recovery, seek, parity, field-order, and diagnostic ordering
- active-output sharing and partial-file readability
- differential fixtures generated against upstream release 4.0

Verified fixtures include byte-exact outputs and stable SHA-256 baselines.
The full per-algorithm inventory and hashes are kept in the shared evidence
document linked below.

<!-- SECTION: remaining -->

## Remaining work

These are bounded parity and verification gaps, not missing top-level commands:

- rare container codec and timestamp behavior outside current fixtures
- additional HiFi real-capture end-to-end baselines
- PAL LaserDisc, AC3, and verbose VITS real-capture edge cases
- uncommon VHS/CVBS vblank, chroma track-phase, and cross-option interactions
- additional capture-wide certification of the complete opt-in `current`
  profile across other formats and option combinations before default promotion
- rare first-HSync/vblank recovery and complete JSON/SQLite field metadata
- remaining TBC writer bit-compatibility edges and output parity across every
  format, option combination, and real capture
- continued CPU utilization, allocation, SIMD, and worker-scheduling profiling
  after compatibility is protected by fixtures

Interactive decode UI and TBC utility tools are outside this goal and are not
tracked as remaining decode compatibility work.

<!-- SECTION: evidence -->

## Detailed evidence

The previous long-form implementation and differential verification inventory
is preserved in
[`COMPATIBILITY_EVIDENCE.md`](COMPATIBILITY_EVIDENCE.md). It contains
the detailed algorithm notes, numerical boundaries, output hashes, and fixture
results shared by all language versions of this README.

<!-- SECTION: license -->

## License

GPL-3.0. See [`LICENSE`](../LICENSE).
