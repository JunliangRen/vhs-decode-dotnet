# LaserDisc real extraction and dropout comparisons — 2026-11-04

Baseline: merged main `3c113fc208016e24b216a19e1ea9c7ef654d4991`,
including the [fourth performance iteration](LD_PERFORMANCE_V4.md).
Candidate branch: `codex/ld-field-kernels-speedup-v5`.
Windows x64, .NET 11 RC2 (`11.0.0-rc.2.26504.105`), 20 logical processors,
Intel IPP 2026.0.0. Baseline and candidate use the same native IPP bridge
and Release settings. Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, NTSC,
with default video, analog audio, EFM, AGC, dropout and automatic MTF enabled.

## Complete decode measurements

The primary protocol was fixed before seeing results: one warm-up pair
followed by seven alternating measured pairs per backend, 240 frames per
process, four threads, both `exact` and `ipp-fast`. Wall time includes
startup and output writing; complete-file hashing happens after exit.
CPU time is main-process user plus kernel time and excludes FFmpeg child
processes; it is not total decoder-system CPU.

| Backend | Baseline median wall | Candidate median wall | Median paired wall-time reduction | Faster wall pairs | Median paired CPU reduction |
| --- | --- | --- | --- | --- | --- |
| exact | 25.931 s | 25.345 s | 3.85% | 6 / 7 | 3.81% |
| ipp-fast | 20.727 s | 20.022 s | 5.48% | 5 / 7 | 2.61% |

Exact wall reductions range from -2.38% to 5.80%; IPP from -1.48% to
7.47%. Negative pairs are retained. Main-process CPU medians are
61.125/59.797 s for exact and 41.672/40.859 s for IPP; paired CPU reductions
are positive in five of seven exact and six of seven IPP pairs. The final
pair regresses for both backends in wall and CPU time.

All 32 processes produced 480 fields. Complete `.tbc`, `.pcm`, `.efm`,
`.tbc.json` and `.tbc.db` SHA-256 sets match before/after and across backends.
These are incremental measurements against v4, not cumulative percentages
from the original implementation.

### Independent longer replication

The primary data show a positive median with negative pairs, so a separate
protocol was fixed before its first timing: 480 frames, five measured
alternating pairs per backend and one warm-up pair, same four-thread
defaults. All primary data remain; the holdout does not replace them.

| Backend | Baseline median wall | Candidate median wall | Median paired wall-time reduction | Faster wall pairs | Median paired CPU reduction |
| --- | --- | --- | --- | --- | --- |
| exact | 47.948 s | 47.721 s | 0.85% | 3 / 5 | 0.94% |
| ipp-fast | 38.223 s | 37.628 s | 1.48% | 4 / 5 | -3.01% |

Exact wall reductions range from -1.82% to 3.38%; IPP from -2.79% to
4.28%. All 24 holdout processes produced 960 fields, with identical
complete output hash sets within this protocol before/after and across
backends. Both independent wall-time medians are positive, but the longer
range measures a much smaller benefit than the primary range.

CPU evidence is mixed. Holdout main-process CPU medians are
111.344/112.109 s for exact and 74.141/75.359 s for IPP. Paired CPU time
improves in three of five exact pairs and only one of five IPP pairs; the
IPP median paired CPU time increases 3.01%. No CPU-saving or energy-saving
claim follows from these results. Wall time was the declared primary
metric. Retention is based on reproducible kernel improvements, unchanged
outputs and positive wall-time medians in both fixed protocols; it does
not establish a universal 3–5% decode gain or a whole-side improvement.

## Retained mechanisms and constraints

- Extract full-precision real components from owned complex FFT outputs
  using AVX2 unpack/shuffle operations. The allocating paths for RFHPF,
  video, video lowpass and enabled reference channels use the same factory.
  It allocates an owned `double[]` without redundant clearing and writes
  every element before return, including scalar tails. This is pure bit
  movement: no arithmetic, float32 conversion or early LD quantization.
  Real signaling/quiet NaN payloads and signs, signed zeros, subnormals and
  infinities retain their original bits; imaginary components are ignored.
  No view of complex input or shared scratch escapes. Existing caller-owned
  and pooled destination overloads, their length checks, filter/deemphasis
  order and the point of LD quantization are unchanged. Unsupported AVX2
  hardware uses scalar real extraction and still completely fills output.
- Compare four demodulated samples at once while preserving the original
  threshold types and ordered comparisons. Video and video lowpass samples
  and thresholds remain float32. Raw demodulation first rounds each sample
  to float32, then widens it for comparison with the original **double**
  bound; that bound is not newly rounded. Per-sample sync-area minima stay
  identical, NaN ordered comparisons remain false, and four result bits
  become four canonical Boolean bytes in the existing error map, including
  false writes. Caller presence checks, threshold/stddev evaluation and
  source-array lifetimes stay in their original order.
- Keep RFHPF detection as a separate second pass. Its float32 comparisons
  only set mapped `i + offset` entries true after the complete base map is
  written. Original integer mapping, bounds checks and ascending source
  order remain intact. No later base write can erase a positive-offset
  hit. Short or malformed shapes use the original checked scalar accesses,
  including exception and partial-write order. Unsupported AVX hardware
  uses the same scalar path. There is no additional mask array, persistent
  buffer or state shared between fields.

The existing LD `BuildErrorRanges` implementation is unchanged. It still
consumes true indices in their original order and retains its existing
1.7 extrapolation, cap, merge distance and padding rules. False samples do
not close this LD merge envelope; it is not a generic contiguous-run scan.

## Correctness evidence

- Complete suite: **2,409 passed, 15 platform/hardware skips, zero failures**
  (2,424 total).
- Focused numerical/ownership suite: **86 passed normally and 86 with managed
  AVX disabled**, zero failures or skips. The Windows scalar-fallback CI
  selection includes both new test classes: all 135 tests passed locally
  with AVX disabled and native IPP required.
- Ten seek/options/system cases across exact and IPP match the original
  `ff03b9b` oracle outputs and normalized diagnostics. They cover middle
  reads, one thread, verbose VITS, combined options and PAL command mode.
  `.tbc`, `.pcm`, `.efm`, `.tbc.json` and `.tbc.db` are compared by SHA-256;
  RF-TBC Ogg/FLAC uses decoded PCM when emitted because container serials
  can vary. Corresponding exact/IPP outputs match each other.
- Real-extraction tests check every f64 bit, including signaling NaNs, with
  finite, random-bit and special-value data, offsets and tails, odd-double
  input alignment, poison destinations, canaries, complete writes, retained
  results and parallel independent outputs. Existing destination-tail and
  malformed-length behavior are checked separately.
- Dropout tests compare complete Boolean map bytes with an independent
  scalar reference. Coverage includes raw-double threshold rounding traps,
  float32 boundary values, mixed/noncanonical sync bytes, ordered NaNs and
  infinities, absent/mismatched channels, positive/negative/overflow offsets,
  true-only RFHPF writes, dirty maps, inactive entries, source immutability,
  short shapes, empty loops, exceptions and already-written prefixes.

## Kernel measurements

These are synthetic helper benchmarks, not captured per-call data or
complete-decode measurements. All positive and negative pairs are retained.

For the allocating real-extraction factory, seven interleaved pairs compare
new-zeroed scalar extraction with the selected uninitialized AVX2 factory:

| Complex samples | Median paired time reduction | Faster pairs |
| --- | --- | --- |
| 16,384 | 26.47% | 7 / 7 |
| 32,768 | 18.39% | 7 / 7 |
| 65,536 | 27.95% | 6 / 7 |
| 1,048,576 | 42.22% | 7 / 7 |

The 64K factory includes one negative pair (-23.34%); it was not discarded.
At 32K, baseline/candidate median times are 24.68/20.45 microseconds.
Both factories still allocate `N * 8 + 24` bytes at the measured sizes:
skipping clearing is not a reduction in allocated bytes. Separate
caller-owned and new-zeroed AVX2 ablations remain in the raw reports.
Normal and AVX-disabled extraction harnesses each passed 126 raw-bit
scenarios plus 12 parallel/retained-output cases; corresponding timed
output hashes all agree without NaN-payload relaxation.

The comparison-only dropout candidate retains the actual v4 Core range
builder in both measured paths. Timing excludes RFHPF stddev, sync-mask
construction and TBC mapping. All-clear, sparse and dense synthetic inputs
use the same ordered threshold and offset arithmetic. Seven-pair results:

| Span capacity / profile | Default tiering reduction | TieredCompilation=0 reduction |
| --- | --- | --- |
| 32,768 / all-clear | 60.61% | 60.67% |
| 32,768 / sparse | 56.04% | 52.62% |
| 32,768 / dense | 43.87% | 45.42% |
| 983,072 / all-clear | 41.96% | 41.09% |
| 983,072 / sparse | 38.93% | 36.64% |
| 983,072 / dense | 31.75% | 31.24% |

Every row was 7/7 positive in each independent configuration. Allocations
match the scalar path; only the existing range-output lists allocate.
The 983,072 capacity is derived from the default NTSC read-window alignment,
not an observed capture-wide distribution of dropout scan lengths.
Normal and AVX-disabled harnesses each passed 315 scene checks and 465 range
checks; the independent tiering-disabled processes passed the same checks.
The fixed v4 Core hash is checked at process startup and recorded in each
rerun, together with actual loaded path, runtime, ISA and JIT settings.

## Rejected range-scan experiment

An independent eight-byte all-zero skip in LD range enumeration improved
some clear/sparse profiles but slowed dense synthetic data: median paired
time increased by 11.50% at 32K and 7.07% at 983,072. Dense errors can occur
in real input, so **none of this range-scan change is included**. The
comparison-only results above retain the original range builder.

That exploratory four-way ablation initially used an older copied Core
binary; its range source was unchanged, but it is not presented as v4-binary
validation. The retained comparison candidate was rerun against the fixed
v4 artifact, then independently with tiered compilation disabled. Original
reports and the anchor correction remain alongside the reruns.

## Evidence and artifact identity

Ignored complete-decode evidence is in `artifacts/ld-speed-v5/`:
`environment.json`, `planned-protocol.json`, `ab-field/`,
`planned-holdout-protocol.json`, `ab-field-holdout/`,
`validation-field/runs.json`, `tests-full/`, and
`tests-focused{,-noavx}/` with their corresponding console logs.
Real-extraction evidence is in `artifacts/ld-real-extraction/`, including
`verify{,-noavx}.json`, `bench.json` and `summary.json`.
Dropout evidence is in `artifacts/ld-dropout-mask-simd/`:
`verify-v4*.json`, `benchmark-v4{,-tiered0}.json`, their summaries, and the
retained initial ablation plus `benchmark-summary-initial-v3-anchor.json`.

Baseline/candidate Core SHA-256 values are
`40f29cb33fc235fd62a98bdc931abdec98d35bc107f8136d44bece2862eded1e` and
`c816af34de5c4a1221844307c21bce7b806de610f7f9e54aade37af6677a2615`.
The native IPP bridge is identical between versions. Correctness evidence
is bounded by the listed tests and decode ranges; kernel improvements do
not establish whole-side throughput or a speed guarantee for other
captures, CPUs or systems.
