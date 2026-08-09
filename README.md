# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-04.03 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,382** standard xUnit v3 tests
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

This is a short, startup-inclusive 40-frame snapshot on one fixed private local
40 MHz PAL VHS `.ldf` fixture; the filename is intentionally not published.
Python and v0.4.0-profile cells retain the same-fixture medians from the
immediately preceding full refresh. This candidate changes only the `current`
parallel VSync path, so both `current` .NET columns were rerun three times in
reordered Release passes. The candidate is based on merged main `845d8d1`.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 17.401 s | 19.791 s | 4.341 s / 4.008x | 4.505 s / 4.393x | 3.549 s / 4.904x | 3.116 s / 6.351x |
| `--threads 1` | 19.177 s | 21.052 s | 11.626 s / 1.649x | 13.073 s / 1.610x | 8.361 s / 2.294x | 9.404 s / 2.239x |
| `--threads 5` | 17.458 s | 20.209 s | 4.043 s / 4.318x | 4.257 s / 4.748x | 3.654 s / 4.778x | 3.089 s / 6.543x |
| `--threads 10` | 17.230 s | 19.480 s | 3.526 s / 4.886x | 3.950 s / 4.932x | 2.983 s / 5.775x | 2.556 s / 7.621x |
| `--threads 20` | 17.558 s | 19.928 s | 2.772 s / 6.334x | 3.449 s / 5.778x | 2.632 s / 6.672x | 2.038 s / 9.779x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-refresh=90 current-refresh=30 repeats=3 candidate-ab=20 thread-gates=24 candidate-long=4 strict-candidate-runs=48 current-determinism=30 python-v040-runs=15 python-v040-hashes=15 python-v040-nondefault-runs=12 python-v040-nondefault-hashes=12 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**. An older public table used a
different private NTSC Betamax HiFi fixture and is not directly comparable.
The 40-frame cells include startup cost and measurement noise; changes versus
an older screenshot are not regression evidence. This candidate merges each
worker's private VSync radix histogram contiguously and uses AVX2 integer adds
where available, while preserving worker order and an exact scalar fallback.

The stable paired result comes from two opposite-order 1,000-frame IPP-fast
`current --threads 20` comparisons. Combined wall time fell from 72.914 to
71.790 seconds (1.54% lower, 1.0157x throughput), CPU time fell from 426.328 to
422.594 seconds (0.88%), and peak working set stayed near 373 MiB without
progressive growth. This longer paired gate, not movement in the short table,
is the causal performance evidence for this change.

All 48 strict baseline/candidate runs matched luma, chroma, raw JSON, ordered
`fileLoc`, normalized stderr, and normalized logs; stdout also matched in the
short and thread-matrix gates where it was captured. All 30 refreshed `current`
matrix runs were cross-thread deterministic. IPP-fast remains an explicit
numerically close backend, so its artifacts are not claimed to match Exact byte
for byte.
Merged Python PR341 was deterministic here; Python v0.4.0 produced distinct
luma, chroma, and JSON hashes in all 15 matrix runs, including all 12 explicit
non-default-worker runs. Python v0.4.0 `g4315520 --threads 0` therefore remains
the strict oracle. Commands,
hardware, hashes, memory bounds, and historical measurements are in the
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
  --no-build --no-restore --minimum-expected-tests 1382
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
