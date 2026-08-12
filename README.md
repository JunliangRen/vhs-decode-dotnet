# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-12.03 -->

A .NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), targeting
upstream release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

> [!IMPORTANT]
> This remains a compatibility work in progress. The top-level decode paths are
> implemented and heavily tested, but every real capture and rare option
> combination has not yet been certified byte-for-byte.

**[Read the detailed English reference](docs/README.detailed.md)** for the full
compatibility matrix, implementation notes, historical benchmarks, validation
evidence, and remaining gaps.

## Contents

- [Overview](#overview)
- [Get started](#get-started)
- [Profiles and backends](#profiles-and-backends)
- [Latest performance](#latest-performance)
- [Compatibility status](#compatibility-status)
- [Build and test](#build-and-test)

<!-- SECTION: overview -->

## Overview

- Decode-only scope: VHS, CVBS, LaserDisc, and HiFi.
- Release 4.0 command names, options, aliases, defaults, diagnostics, and output
  lifecycle are the compatibility target.
- VHS-family routing includes VHS/S-VHS, Betamax, Video8/Hi8, U-matic, Type C,
  EIAJ, and supported PAL/NTSC variants.
- TBC utility tools, the double-click GUI, and developer plotting windows are
  intentionally out of scope.
- The Visual Studio 2026 `.slnx` solution has **1,429** standard xUnit v3 tests
  that are visible in Test Explorer and runnable with `dotnet test`.

<!-- SECTION: start -->

## Get started

Download the current binary-only Windows x64 package from
[GitHub Releases](https://github.com/JunliangRen/vhs-decode-dotnet/releases).
The package is built as a single-file `decode.exe`.

```powershell
decode.exe vhs [upstream options] input.lds output
decode.exe cvbs [upstream options] input.lds output
decode.exe ld [upstream options] input.lds output
decode.exe hifi [upstream options] input.lds output.wav
```

Standalone command aliases such as `vhs-decode.exe` and `ld-decode.exe` are
also supported. Use `decode.exe <command> --help` for the complete compatible
option set.

<!-- SECTION: profiles -->

## Profiles and backends

`--compat-version` selects upstream behavior:

| Value | Meaning |
| --- | --- |
| `v0.4.0` | Default. Targets the pinned Python release behavior. |
| `current` | Opt-in staged behavior from upstream PR 341, including newer VHS sync and color-under processing. |

The strict compatibility oracle is Python v0.4.0 commit `g4315520` with
`--threads 0`. Python output hashes are not stable across its worker counts, so
multithreaded Python runs are used for speed measurements only.

`--dsp-backend` selects the DSP implementation:

| Value | Meaning |
| --- | --- |
| `exact` | Default managed path for compatibility-sensitive decoding. |
| `ipp-fast` | Experimental Windows x64 VHS and LaserDisc real-RF paths using Intel IPP. It can change floating-point bits and never silently falls back to `exact`. |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

LaserDisc now routes its video, EFM, and analog-audio full-complex FFT stages
through IPP. CVBS and HiFi still reject `ipp-fast`; use `exact` whenever
release-compatible behavior is required. See the
[detailed backend notes](docs/README.detailed.md#performance) before using IPP
for compatibility-sensitive work.

<!-- SECTION: performance -->

## Latest performance

This startup-inclusive `--start 100 --length 160` snapshot uses one fixed private
local 40 MHz PAL VHS `.ldf` fixture; its filename is intentionally not published.
All 90 Python and .NET Release measurements were completed in the same
2026-08-12 batch with the candidate based on main `bc73fa7`, using forward,
reverse, and mixed passes. This replaces the prior mixed-batch snapshot.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 47.039 s | 47.613 s | 13.895 s / 3.385x | 13.290 s / 3.583x | 11.942 s / 3.939x | 9.868 s / 4.825x |
| `--threads 1` | 56.448 s | 57.923 s | 37.293 s / 1.514x | 41.786 s / 1.386x | 24.533 s / 2.301x | 27.795 s / 2.084x |
| `--threads 5` | 48.105 s | 47.568 s | 13.596 s / 3.538x | 13.310 s / 3.574x | 11.910 s / 4.039x | 9.953 s / 4.779x |
| `--threads 10` | 48.819 s | 49.473 s | 10.781 s / 4.528x | 9.490 s / 5.213x | 9.972 s / 4.896x | 7.808 s / 6.336x |
| `--threads 20` | 50.003 s | 50.116 s | 8.616 s / 5.803x | 8.101 s / 6.187x | 8.439 s / 5.926x | 5.976 s / 8.387x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-12 dotnet-current-date=2026-08-12 radix5-avx-matrix-runs=90 radix5-avx-exact-1000-ab-pairs=2 radix5-avx-kernel-iterations=128 radix5-avx-tests=59 radix5-avx-intrinsic-modes=3 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). A ratio moves
when either the .NET time or its Python denominator moves, and historical tables
using another fixture or window are not directly comparable. Same-moment .NET
revision A/B runs, rather than old ratio cells, determine causal regressions.

The latest isolated change uses managed AVX to evaluate four independent
float32 radix-5 PocketFFT indices at once. Every complex lane keeps the scalar
expression order; unsafe-magnitude and non-finite packets use the scalar path,
with no FMA, reduction, or reassociation. A 128-iteration production-size
forward/back kernel loop moved from 757.745 to 600.026 ms (20.8% lower wall
time) and from 765.625 to 671.875 ms CPU (12.2% lower), with identical hashes
and no GC. Across two opposite-order 1,000-frame Exact
`current --threads 20` pairs, mean wall time moved from 38.483 to 37.800 seconds
(1.78% lower); CPU time was effectively flat at 308.664 versus 308.188 seconds,
while average active cores moved from 8.02 to 8.15. All nine compatibility
surfaces matched and memory remained bounded. The existing double radix-8 AVX
path also gained an extreme/non-finite scalar fallback; no speedup is attributed
to that compatibility hardening.

Every .NET profile/thread cell was deterministic across its three refreshed
runs. Merged Python PR341 was deterministic in its pinned reference set; Python
v0.4.0 produced 15 distinct luma, chroma, JSON, and normalized-log hashes in 15
runs, so the strict oracle remains Python v0.4.0 `g4315520 --threads 0`.
Commands, ranges, binary hashes, memory bounds, and historical measurements are in the
[detailed performance reference](docs/README.detailed.md#performance).

<!-- SECTION: compatibility -->

## Compatibility status

The main decode pipelines, streaming outputs, recovery behavior, and CLI
surface are implemented. Focused tests and real-RF gates cover luma, chroma,
JSON, ordered `fileLoc`, stdout, normalized stderr/logs, determinism, and
bounded memory. Rare captures and uncommon option interactions remain ongoing
work, so a successful build or equal file size alone is not treated as proof
of compatibility.

TBC, chroma, JSON, and log files are opened for concurrent reading while a
decode is running, allowing compatible preview tools to inspect partial output
without blocking the writer.

On native-input routes, direct raw `fLaC` `.ldf`/`.flac` inputs that are 40 kHz
mono PCM16 and contain at most `Int32.MaxValue` samples use the bundled
libsndfile reader. Ordinary parallel VHS decode can also use libsndfile for a
narrowly gated oversized fixed-block raw FLAC without a seek table; integer
mapping reproduces the pinned FFmpeg/PyAV frame starts and rewind/restart
boundaries, with one-way fallback at the same logical sample on any failure.
`--threads 0/1`, debug-plot and GNU Radio AFE modes, nonzero `--sharpness`,
other command families, default VHS `.flac`, CVBS, Ogg/FLAC, stereo, PCM24,
other sample rates, and unfinished or ineligible headers retain FFmpeg.

<!-- SECTION: build -->

## Build and test

The pinned SDK is .NET `11.0.100-preview.6.26359.118`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1429
```

Open `VHSDecodeDotNet.slnx` in Visual Studio 2026 to build, debug, and run the
xUnit v3 suite through Test Explorer.

<!-- SECTION: detail -->

## More detail

- [Detailed English reference](docs/README.detailed.md)
- [Compatibility evidence](docs/COMPATIBILITY_EVIDENCE.md)
- [Simplified Chinese overview](README.zh-CN.md)
- [Japanese overview](README.ja.md)

<!-- SECTION: license -->

## License

GPL-3.0. See [`LICENSE`](LICENSE).
