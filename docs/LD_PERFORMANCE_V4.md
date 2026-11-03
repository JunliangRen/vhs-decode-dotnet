# LaserDisc shared DSP kernels — 2026-11-04

Baseline: merged main `ec0e0b6`, including the [third performance iteration](LD_PERFORMANCE_V3.md).
Windows x64, .NET 11 RC2 (`11.0.0-rc.2.26504.105`), 20 logical processors,
Intel IPP 2026.0.0. Both versions use the same native bridge and Release
settings. Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, NTSC, four threads,
with default video, analog audio, EFM, AGC, dropout and automatic MTF enabled.

## Repeated complete decode

One warm-up pair followed by five alternating measured pairs per backend;
each process writes 240 frames. Wall time includes startup and output
writing. Hashing happens after the timed process exits.

| Backend | Baseline median | Candidate median | Median paired time reduction |
| --- | --- | --- | --- |
| exact | 25.998 s | 25.252 s | 2.63% |
| ipp-fast | 20.816 s | 19.794 s | 4.51% |

All ten measured pairs improved. Exact reductions ranged from 0.30% to
7.32%; IPP reductions ranged from 2.90% to 6.39%. These are incremental
changes over the previous version, not cumulative percentages from the
original implementation.

All 24 processes produced 480 fields. Complete `.tbc`, `.pcm`, `.efm`,
`.tbc.json` and `.tbc.db` SHA-256 sets match before/after and between backends.
Ten additional seek/options/PAL cases match the original `ff03b9b` baseline
and timestamp-normalized diagnostics, including one and 16 threads,
verbose VITS, disabled AGC, RF-TBC, cubic interpolation and wow smoothing.
RF-TBC Ogg/FLAC is compared by decoded PCM because container serials vary.

## Retained changes

- Vectorize the ordinary complex products used by LD EFM filtering and
  analog audio stage 1. The four separate multiplications and the actual
  runtime operator's real subtraction/imaginary addition order are retained;
  no FMA is introduced. Any nonfinite vector result falls back to the
  original operator, including its NaN recovery. Shifted aliases, tails,
  unsupported hardware and existing malformed filter-length behavior retain
  their scalar paths. The separate NumPy/FMA multiplication is unchanged.
- Stage real input for the native IPP FFT with exact AVX unpack/shuffle
  operations. The factory owns a fresh uninitialized complex array and
  writes every real and positive-zero imaginary lane before returning it.
  This skips redundant clearing, rather than reducing allocated bytes.
  Only the allocating IPP branch changes; existing FFT Plan staging stays
  unchanged. Scalar fallback also completely fills the new array.
- Vectorize independent variance preprocessing in the mixed-precision RF
  standard deviation. The original sequential float mean is unchanged;
  conversion, subtraction and squaring keep their float32 intermediate
  precision. Each product then widens and enters the double sum in original
  sample order. There is no horizontal reduction, FMA or reassociation.
  Nonfinite means and unsupported hardware retain scalar variance processing.

## Kernel evidence and verification

Independent seven-pair microbenchmarks measured a 47.38% time reduction for
32K ordinary complex multiplication and 25.70% for the allocating 32K IPP
staging factory. Ordered variance preprocessing reduced standard-deviation
kernel time by approximately 71–72%; an independent process with tiered
compilation disabled reproduced the result against both the actual Core
method and a frozen scalar reference. These kernel results are separate
from the much smaller complete-decode gains above.

- Release builds: zero warnings/errors; test and CLI Core DLL hashes match.
- Complete suite with native IPP required: 2,347 passed, 15 platform/hardware
  skips, zero failures (2,362 total).
- Focused numerical-contract suite: 73 passed normally and 73 with managed
  AVX disabled, zero failures or skips. Windows CI explicitly runs the same
  scalar-fallback selection with IPP required.
- Added coverage includes non-FMA rounding traps, runtime complex recovery,
  overflow, aliases, tails, raw staging bits including signaling NaNs,
  complete writes, actual IPP FFT parity, malformed filter lengths, float
  mean/product overflow, sequential variance rounding and RFHPF thresholds.
- Finite values, signed zeros and infinities use raw-bit comparisons. Math
  NaN classification and the original platform empty-input result remain
  unchanged. Multi-NaN payload/sign differences also reproduced between
  unchanged scalar references in different JIT contexts; raw diagnostics
  are retained. Pure input staging retains all raw NaN bits.

Ignored complete-decode evidence is in `artifacts/ld-speed-v4/`:
`environment.json`, `ab-dsp/{runs,summary}.json`, `validation-dsp/runs.json`,
`tests-full/` and `tests-focused{,-noavx}/`. Kernel evidence is under
`artifacts/ld-speed-v3/{ordinary-multiply-scratch,ipp-real-staging-scratch,stddev-ordered-simd}/`,
including the separate tiering-disabled standard-deviation reports.

Baseline/candidate Core SHA-256 values are
`51827459770d28e3f601a2948e55872114db496e7380ace58afbfcf6f656b495` and
`40f29cb33fc235fd62a98bdc931abdec98d35bc107f8136d44bece2862eded1e`.
Timings describe this machine and bounded input ranges; they are not a
whole-side measurement or a guarantee for other captures, CPUs or systems.
