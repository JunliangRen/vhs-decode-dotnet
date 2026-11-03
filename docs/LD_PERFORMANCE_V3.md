# LaserDisc packet FFT locality — 2026-11-04

Baseline: merged main `763a558`, including the [second performance iteration](LD_PERFORMANCE_V2.md).
Windows x64, .NET 11 RC2 (`11.0.0-rc.2.26504.105`), 20 logical processors,
Intel IPP 2026.0.0. Both versions use identical native IPP binaries and Release
settings. Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, NTSC, four threads,
with the default video, audio, EFM, AGC, dropout and MTF processing enabled.

## Complete decode measurements

Each window uses one warm-up pair followed by five alternating measured
pairs. Wall time includes startup and output writing; complete-file hashing
occurs after process exit.

| Frames | Backend | Median paired time reduction | Faster measured pairs |
| --- | --- | --- | --- |
| 240 | exact | 12.04% | 5 / 5 |
| 240 | ipp-fast | -0.88% | 2 / 5 |
| 480, independent longer window | ipp-fast | 0.61% | 3 / 5 |

Exact median process time decreased from 30.021 s to 26.538 s. All five
exact reductions were positive, ranging from 10.50% to 14.69%.

IPP results are mixed: -2.49% to +2.09% in the first window and -2.35% to
+1.69% in the longer follow-up. These measurements establish neither a
consistent whole-decode IPP improvement nor a sustained regression. This
iteration is retained for its clear exact gain; no IPP speedup is claimed.
IPP's main RF FFTs still use the same native bridge. Shared DUCC work,
primarily analog audio phase 2, is a much smaller part of that backend.

All 36 processes produced their requested 480 or 960 fields. Within each
window, complete `.tbc`, `.pcm`, `.efm`, `.tbc.json` and `.tbc.db` SHA-256
hashes match before/after; the 240-frame outputs also match between backends.
Ten additional seek/options/PAL cases match the original `ff03b9b` baseline
and timestamp-normalized diagnostics. RF-TBC Ogg/FLAC is compared through
its decoded PCM because container stream serials vary.

## Change and compatibility

The packet FFT now completes its second-pass transforms in the existing
stage rows, then copies the result through a 16 x 16 tiled transpose. This
removes the second packet buffer/copy and gives contiguous output writes.
The arithmetic, twiddles, normalization and zero-index bypasses are unchanged.

The first pass consumes the complete input before any caller output is
written, preserving in-place operation. Transpose bounds use the active
first/second packet dimensions, including rectangular 32K transforms; a
larger retained thread-local stage capacity never enters the active matrix.

An independent caller-buffer microbenchmark with full warm-up measured
32K forward/inverse paired median reductions of 8.23% / 13.14%. The complete
exact decode result above is the delivery evidence. Removing just the
intermediate copy without changing the transpose was rejected after its
microbenchmark regressed common sizes.

## Verification and retained evidence

- Release build: zero warnings/errors.
- Focused suite with IPP required: 129 passed normally and 129 with AVX
  disabled; zero failures or skips in either run.
- Complete suite with IPP required: 2,278 passed, 15 platform/hardware skips,
  zero failures (2,293 total).
- Two new tests cover eight old forward/inverse hashes, rectangular/square
  dimensions, odd/even plan pass counts, repeated 1M-to-small stage reuse,
  input preservation, in-place/preserving APIs and parallel transforms.
  Frozen hashes follow the existing Windows oracle; every OS strictly
  compares its own preserving/in-place outputs.
- Independent old/new comparisons cover 368 scenarios each with AVX enabled
  and disabled. Finite values, signed zeros and infinities retain raw bits.
  NaN classification is preserved; payload/sign variation also reproduced
  within the unchanged baseline across JIT tiers, matching the existing
  FFT contract. Raw discrepancies are retained in the parity reports.

Ignored local evidence is in `artifacts/ld-speed-v3/`: `environment.json`,
`ab-tiled/{runs,summary}.json`, `ab-tiled-ipp-long/{runs,summary}.json`,
`validation-tiled/runs.json` and `tests-full/`. Independent kernel evidence
is in `artifacts/ld-speed/packet-copy-scratch/`, and focused logs are
`artifacts/ld-speed/v3-agent-focused-{normal,noavx}.log`.

Baseline/candidate Core DLL SHA-256 values are
`1227d97c0692a35bb697c25f03a496a8584c47a4417af59b9792c28f12ff2609` and
`51827459770d28e3f601a2948e55872114db496e7380ace58afbfcf6f656b495`.
These are bounded results on this machine, not a whole-side measurement
or a performance guarantee for other CPUs, operating systems or captures.
