# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-31.03 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,141** standard xUnit v3 tests
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
| `ipp-fast` | Experimental Windows x64 VHS real-RF path using Intel IPP. It can change floating-point bits and never silently falls back to `exact`. |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

CVBS, LaserDisc, and HiFi currently reject `ipp-fast`; use `exact` for those
commands. See the
[detailed backend notes](docs/README.detailed.md#performance) before using IPP
for compatibility-sensitive work.

<!-- SECTION: performance -->

## Latest performance

The table below uses one fixed private local 40 MHz NTSC `BETAMAX_HIFI` `.lds`
sample and the same bounded frame range for every run. The sample filename is
intentionally not published. The v0.4.0 .NET profiles are compared with Python
v0.4.0; the `current` profiles are compared with merged Python PR341 at the
same requested worker count. Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 16.983 s | 14.414 s | 5.781 s / 2.938x | 7.216 s / 1.998x | 5.657 s / 3.002x | 7.092 s / 2.032x |
| `--threads 1` | 21.263 s | 19.881 s | 18.232 s / 1.166x | 20.627 s / 0.964x | 17.542 s / 1.212x | 20.201 s / 0.984x |
| `--threads 5` | 16.880 s | 14.329 s | 5.906 s / 2.858x | 7.463 s / 1.920x | 5.689 s / 2.967x | 7.459 s / 1.921x |
| `--threads 10` | 17.612 s | 15.149 s | 4.536 s / 3.883x | 5.887 s / 2.573x | 4.414 s / 3.990x | 5.537 s / 2.736x |
| `--threads 20` | 18.330 s | 15.447 s | 3.568 s / 5.138x | 5.049 s / 3.059x | 3.471 s / 5.281x | 4.496 s / 3.435x |
<!-- LATEST_PERFORMANCE_END -->

The latest Exact pass recycles the VHS RF stream outputs only after their
decoded blocks leave every cache and the current span assembly is complete.
Public block results keep independent ownership, arithmetic is unchanged, and
the idle retained pool has a 48-set hard limit. A matched 100-frame trace reduced
sampled allocation from 4.599 GB to 566.9 MB (87.7%).

All 12 strict main/candidate thread-profile gates and all 60 refreshed matrix
runs matched their references. Across ten alternating `current`/20-worker
pairs, median decode time moved from 8.72 to 8.58 seconds (1.61%); the mean
improved 1.07%, so the throughput gain is modest while allocation pressure is
substantially lower. A 1,000-frame gate remained exact, completed in 69.475
seconds, and kept working set bounded below 711.6 MiB.

Each .NET cell shows median wall time followed by speedup versus its
profile-matched Python column; values below `1.000x` are slower. Python PR341
is merge commit `2f21e8ed6018b14561396cc95f1f6828054470b8`, the upstream peer
for `current`. The default is **5 workers**. Nonzero-thread Python rows are
throughput comparisons only; strict compatibility still uses Python v0.4.0
`g4315520 --threads 0`.

Default worker counts, exact commands, build hashes, hardware, repeated-run
methodology, output hashes, and older measurements are recorded in the
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

<!-- SECTION: build -->

## Build and test

The pinned SDK is .NET `11.0.100-preview.6.26359.118`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1141
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
