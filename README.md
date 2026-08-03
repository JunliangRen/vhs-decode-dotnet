# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-03.01 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,296** standard xUnit v3 tests
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

The table uses one fixed private local 40 MHz PAL VHS `.ldf` fixture and the
same 40-frame window for every run; the filename is intentionally not
published. On 2026-08-04, the 10 affected `current` cells were refreshed from
three interleaved runs of this branch candidate based on main `9042d9a` (30
Release runs). Python and the unchanged v0.4.0 .NET columns retain their
audited same-window measurements. Compatibility is evaluated separately from
speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 15.207 s | 16.780 s | 4.332 s / 3.510x | 5.067 s / 3.312x | 3.414 s / 4.454x | 3.274 s / 5.125x |
| `--threads 1` | 17.694 s | 19.414 s | 9.867 s / 1.793x | 11.990 s / 1.619x | 7.163 s / 2.470x | 8.069 s / 2.406x |
| `--threads 5` | 15.719 s | 17.801 s | 4.268 s / 3.683x | 5.216 s / 3.413x | 3.530 s / 4.453x | 3.203 s / 5.558x |
| `--threads 10` | 16.037 s | 18.266 s | 3.579 s / 4.481x | 4.328 s / 4.221x | 3.007 s / 5.333x | 2.824 s / 6.469x |
| `--threads 20` | 16.405 s | 18.395 s | 2.951 s / 5.559x | 3.634 s / 5.062x | 2.618 s / 6.267x | 2.443 s / 7.530x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: current-refresh=30 repeats=3 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**. The pinned `current` CTI reciprocal
now uses one process-wide 2,048-entry mantissa table instead of repeating
integer division and remainder per sample. A production-sized CTI microbenchmark
improved from 1.590 to 1.401 ms (11.88%; 1.135x throughput); a three-pair fixed
100-frame `ipp-fast + current --threads 20` A/B improved from 5.14 to 5.07
seconds (1.36%; 1.014x throughput).

All 30 refreshed `current` cells were deterministic within each backend. The
100-frame A/B matched luma, chroma, raw JSON, stdout, timing-normalized stderr,
and timestamp-normalized logs. IPP-fast remains an explicit numerically close
backend, so its artifacts are not claimed to match Exact byte for byte. Python
v0.4.0 can produce
different output hashes with nonzero worker counts, so Python v0.4.0
`g4315520 --threads 0` remains the strict oracle. Commands, hardware, hashes,
memory bounds, and historical measurements are in the
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
  --no-build --no-restore --minimum-expected-tests 1296
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
