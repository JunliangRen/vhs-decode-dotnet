# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-30.02 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,122** standard xUnit v3 tests
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
intentionally not published. Each .NET gain is measured against Python
v0.4.0 at the same requested worker count; compatibility is evaluated
separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: |
| default (5) | 16.983 s | 6.416 s / 2.647x | 7.678 s / 2.212x | 5.667 s / 2.997x | 7.184 s / 2.364x |
| `--threads 1` | 21.263 s | 20.547 s / 1.035x | 22.961 s / 0.926x | 20.129 s / 1.056x | 22.320 s / 0.953x |
| `--threads 5` | 16.880 s | 6.173 s / 2.735x | 7.802 s / 2.164x | 5.802 s / 2.909x | 7.186 s / 2.349x |
| `--threads 10` | 17.612 s | 4.880 s / 3.609x | 6.118 s / 2.879x | 4.636 s / 3.799x | 5.744 s / 3.066x |
| `--threads 20` | 18.330 s | 3.997 s / 4.586x | 4.767 s / 3.845x | 3.966 s / 4.622x | 4.919 s / 3.726x |
<!-- LATEST_PERFORMANCE_END -->

The latest Exact pass reuses two non-escaping, worker-local complex-demodulation
buffers while retained diagnostics remain independent. Data types, expressions,
operation order, FFT behavior, and ordered state commits are unchanged. All
profile/thread gates and all 60 refreshed matrix runs above remained exact.
Four opposite-order current/20-worker 500-frame pairs moved median wall time by
only +0.26% while reducing CPU time by 1.82%; the longer default and v0.4.0
pairs ranged from -0.32% to +1.10%, so throughput is classified as neutral.
Two opposite-order 1,000-frame counter pairs reduced allocation from 111.461
to 88.022 GiB (21.03%) and GC pause from 0.994 to 0.791 s (20.43%). A
3,000-frame run kept its first/middle/final 1,000-frame totals at
72.00/69.11/68.50 s with non-monotonic 722/1,305/602/864 MiB working-set
quarter medians, showing bounded memory and no progressive slowdown.

Each .NET cell shows median wall time followed by speedup versus Python in the
same row; values below `1.000x` are slower. The default is **5 workers**.
Nonzero-thread Python rows are throughput comparisons only; strict
compatibility still uses Python `--threads 0`.

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
  --no-build --no-restore --minimum-expected-tests 1122
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
