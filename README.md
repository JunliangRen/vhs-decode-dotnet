# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-30.01 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,118** standard xUnit v3 tests
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
| default (5) | 16.983 s | 5.999 s / 2.831x | 7.904 s / 2.149x | 5.888 s / 2.884x | 7.421 s / 2.289x |
| `--threads 1` | 21.263 s | 19.450 s / 1.093x | 22.287 s / 0.954x | 18.770 s / 1.133x | 21.251 s / 1.001x |
| `--threads 5` | 16.880 s | 6.363 s / 2.653x | 7.653 s / 2.206x | 5.850 s / 2.886x | 7.540 s / 2.239x |
| `--threads 10` | 17.612 s | 4.600 s / 3.829x | 5.864 s / 3.003x | 4.642 s / 3.794x | 6.061 s / 2.906x |
| `--threads 20` | 18.330 s | 3.684 s / 4.976x | 4.854 s / 3.777x | 3.760 s / 4.875x | 4.769 s / 3.843x |
<!-- LATEST_PERFORMANCE_END -->

The latest Exact allocation follow-up transfers the already-owned NTSC
burst-deemphasis field buffer directly into current phase compensation instead
of cloning the complete field once more. Filtering, float32 conversion points,
arithmetic order, and caller input ownership remain unchanged. Twelve
profile/thread gates, six interleaved 160-frame pairs, and two opposite-order
1,000-frame pairs remained exact. The idle long pairs reduced combined counter
allocation from 140.055 to 132.532 GiB (5.37%); combined wall time was neutral
at 142.323/142.893 s and GC pause at 1.100/1.116 s. Post-startup 100-frame
intervals stayed between 6.794 and 6.978 s without progressive slowdown.
Because no stable whole-pipeline speedup is claimed, the table above remains
the last valid idle 60-run matrix.

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
  --no-build --no-restore --minimum-expected-tests 1118
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
