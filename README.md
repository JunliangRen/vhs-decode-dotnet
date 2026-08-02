# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-01.01 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,223** standard xUnit v3 tests
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
| default (5) | 16.983 s | 14.414 s | 5.623 s / 3.020x | 6.730 s / 2.142x | 5.295 s / 3.207x | 6.688 s / 2.155x |
| `--threads 1` | 21.263 s | 19.881 s | 15.275 s / 1.392x | 17.979 s / 1.106x | 14.625 s / 1.454x | 17.008 s / 1.169x |
| `--threads 5` | 16.880 s | 14.329 s | 5.475 s / 3.083x | 6.514 s / 2.200x | 5.361 s / 3.149x | 6.841 s / 2.094x |
| `--threads 10` | 17.612 s | 15.149 s | 4.640 s / 3.795x | 5.890 s / 2.572x | 4.900 s / 3.594x | 5.631 s / 2.690x |
| `--threads 20` | 18.330 s | 15.447 s | 3.809 s / 4.812x | 4.388 s / 3.520x | 3.722 s / 4.924x | 4.819 s / 3.206x |
<!-- LATEST_PERFORMANCE_END -->

The latest Exact pass specializes PocketFFT radix-8 direction, vectorizes the
bit-exact VHS chroma UInt16 conversion and `current` burst fit, and narrows
`current` sync quantiles with deterministic radix selection. Scalar fallbacks,
data types, and the original numerical operation order remain available.

Native and scalar thread/profile gates and all 60 final matrix runs matched
their references. Six balanced `current`/20-worker pairs reduced median wall
time from 9.637 to 8.627 seconds (10.48%; 11.71% more throughput); four serial
pairs reduced it from 50.377 to 37.300 seconds (25.96%; 35.06% more throughput).
Across two opposite-order 1,000-frame pairs, mean wall time fell from 72.405 to
62.752 seconds (13.33%; 15.38% more throughput). Allocation changed by only
0.18%, candidate working set stayed below 706 MiB, and no progressive slowdown
was observed.

The new direct raw `fLaC` input path does not affect the fixed `.lds` table.
On one private 100-frame, 20-worker RF window, the Release 1.4.4 FFmpeg baseline
and bundled-libsndfile candidate took 8.319 s and 7.345 s respectively (11.71%
less wall time; 1.133x throughput), while luma, chroma, raw JSON, stdout,
normalized stderr/logs, and all 200 ordered `fileLoc` values matched. This is a
scoped single-pair observation, not a universal decoder speed claim.

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

On native-input routes, direct raw `fLaC` `.ldf`/`.flac` inputs that are 40 kHz
mono PCM16 use the bundled libsndfile reader. This includes default 40 MHz VHS
`.ldf`, VHS `--no_resample`, and LD without `--inputfreq`; default VHS `.flac`
and all CVBS inputs still use the FFmpeg/PyAV-compatible path. Ogg/FLAC,
stereo, PCM24, other sample rates, and unfinished headers also retain FFmpeg.

<!-- SECTION: build -->

## Build and test

The pinned SDK is .NET `11.0.100-preview.6.26359.118`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1223
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
