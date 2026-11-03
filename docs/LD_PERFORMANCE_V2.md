# LaserDisc storage and conversion performance — 2026-11-04

This iteration compares against `b372f53` on `main`, which already contains
the [first LD performance changes](LD_PERFORMANCE.md). Timings below are
incremental improvements over that version.

Windows x64, .NET 11 RC2 (`11.0.0-rc.2.26504.105`), 20 logical processors,
Intel IPP 2026.0.0. Both versions used the same native IPP bridge and Release
build settings. Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, NTSC, with analog
audio, EFM, dropout detection, AGC and automatic MTF enabled.

## Repeated complete decode

One warm-up pair followed by five alternating measured pairs per backend.
Each process decodes 160 frames with four threads (the CLI default). Wall
time includes startup, synchronization and output writing; SHA-256 hashing
happens after the process exits.

| Backend | Baseline median | Optimized median | Median paired time reduction |
| --- | --- | --- | --- |
| exact | 21.549 s | 20.884 s | 2.24% |
| ipp-fast | 15.419 s | 14.814 s | 3.58% |

All five measured pairs improved on each backend. Exact paired reductions
ranged from 0.12% to 17.34%, including one unusually large improvement;
IPP ranged from 2.64% to 5.93%. The paired medians above are used instead
of the best run. These are small, bounded gains on this machine, not a
whole-side decode measurement or a guarantee for other systems.

All 24 processes produced 320 fields. Complete `.tbc`, `.pcm`, `.efm`,
`.tbc.json` and `.tbc.db` hashes match the baseline and the other backend.
Additional comparisons at frames 300, 1,000 and 10,000 cover one and 16
threads, `verboseVITS`, RF-TBC, disabled AGC, cubic interpolation, wow
smoothing and synthetic PAL input. All ten cases match the original
pre-optimization `ff03b9b` outputs and timestamp-normalized diagnostics.
RF-TBC Ogg/FLAC is compared by decoded PCM because stream serials vary.

## Retained changes

- The allocating full real exact FFT uses its existing thread-local packed
  buffer in place. A warmed 32K transform allocates only its returned full
  spectrum, saving 524,352 bytes without retaining another buffer.
- LD RF filtering reuses the dead high-pass complex inverse buffer after
  extracting the independently owned real diagnostic channel.
- Internal LD stream blocks omit the unconsumed analytic output. When RF
  high boost is absent or zero, the analytic transform reuses the filtered
  spectrum until envelope calculation, demodulation and repair finish.
  Public block diagnostics still retain independent analytic data; public
  stream spans retain all their channels, including the envelope.
- EFM conversion reads the disposable complex inverse directly, removing
  the real-only intermediate. AVX2 handles finite/clipped groups of four;
  groups containing real NaNs and non-AVX2 systems use the original scalar
  clamp/cast behavior. A 32K conversion saves 262,168 allocated bytes.
- Short phase-2 audio returns its newly owned filtered channels directly,
  avoiding another allocation/copy pair. Existing short double storage and
  long float32 storage semantics are preserved.

Earlier scalar-fusion and SIMD-only checkpoints had inconclusive or negative
complete-decode results. Only the combined version above passed the repeated
positive-gain gate. Microbenchmarks alone were not used as the delivery gate.

## Regression checks and evidence

- Release build: zero warnings/errors.
- Complete suite with native IPP required: 2,276 passed, 15 platform/hardware
  skips, zero failures (2,291 total).
- EFM conversion: 20 cases pass normally and with AVX2 disabled; the scalar
  fallback suite is also run explicitly in Windows CI.
- Added coverage includes frozen real FFT hashes, signed zeros/nonfinite
  classification, retained results, input preservation, concurrency, EFM
  clamp boundaries/NaNs, audio ownership, active high boost, repair,
  deemphasis, PAL spur removal, and independent high-pass FFT results.
- Allocation checks warm the measured path and use steady-state samples;
  per-call allocations still fail their original bounds. NaN payload/sign
  follows the existing FFT contract: even the unchanged baseline can vary
  across JIT tiers. Finite values, infinities and signed zeros retain strict
  bit comparisons; complete decode output files retain full hash checks.

Ignored local evidence is under `artifacts/ld-speed-v2/`:
`candidate-storage-environment.json`, `ab-storage/{runs,summary}.json`,
`validation-storage/runs.json`, `tests-storage-full/`, and
`candidate-c-tests-results/`. Baseline/candidate binaries and the complete
decode artifacts are retained alongside the reports. The measured Core
DLLs have SHA-256 `2b468d57cac328a1d75eeec3bc080b8b289644707565e511fbf2a2fe22532146`
and `1227d97c0692a35bb697c25f03a496a8584c47a4417af59b9792c28f12ff2609`.
