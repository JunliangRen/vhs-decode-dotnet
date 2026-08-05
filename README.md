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
- The Visual Studio 2026 `.slnx` solution has **1,331** standard xUnit v3 tests
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
published. The Python columns retain their audited measurements. All twenty
.NET cells were refreshed with three Release runs on this candidate, based on
main `ced6afb`. Compatibility is evaluated separately from speed, and the raw
run directories remain local because they contain the private fixture path.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 15.207 s | 16.780 s | 4.126 s / 3.686x | 4.980 s / 3.369x | 3.480 s / 4.369x | 3.342 s / 5.021x |
| `--threads 1` | 17.694 s | 19.414 s | 9.767 s / 1.812x | 11.844 s / 1.639x | 7.104 s / 2.491x | 7.887 s / 2.462x |
| `--threads 5` | 15.719 s | 17.801 s | 4.221 s / 3.724x | 4.853 s / 3.668x | 3.555 s / 4.422x | 3.437 s / 5.179x |
| `--threads 10` | 16.037 s | 18.266 s | 3.427 s / 4.680x | 4.242 s / 4.306x | 3.036 s / 5.282x | 2.565 s / 7.121x |
| `--threads 20` | 16.405 s | 18.395 s | 2.928 s / 5.602x | 3.316 s / 5.548x | 2.601 s / 6.308x | 2.239 s / 8.217x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: dotnet-full-refresh=60 repeats=3 long-paired=6 compat=8 determinism=12 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**. Multi-worker compact VHS decoding
now materializes `Video`, `Envelope`, and `Chroma` while low-pass-only sync work
continues, with one bounded staged span and eager fallbacks for serial or
stateful paths. Reverse-order 1,000-frame Exact pairs reduced wall time by
2.66% for v0.4.0 and 2.55% for `current`; 600-frame IPP-fast pairs improved
v0.4.0 by 1.85% and were neutral for `current` (-0.03%).

Float32 PocketFFT plans now retain immutable real-root tables and share up to
32 complex root tables by root length. A final direct three-pair 1,000-frame
Exact `current` comparison against main `ced6afb` was throughput-neutral: 1/3
pairs were faster, median wall time was 44.924/44.985 seconds (main/candidate,
+0.14%, 0.999x), and median CPU time was 332.672/333.734 seconds (+0.32%).
Matched final 200-frame allocation traces reduced sampled allocation amount
from 579,283,536 to 541,701,824 bytes (6.49%).

All 60 refreshed matrix runs were deterministic. Separate `--threads 0`,
default-5, and `--threads 20` gates matched luma, chroma, raw JSON, stdout,
normalized stderr/logs, and ordered `fileLoc` across both profiles and backends.
IPP-fast remains an explicit numerically close backend, so its artifacts are
not claimed to match Exact byte for byte. Python v0.4.0 can change output hashes
with nonzero worker counts, so Python v0.4.0 `g4315520 --threads 0` remains the
strict oracle. Commands, hardware, hashes, memory bounds, and historical
measurements are in the
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
  --no-build --no-restore --minimum-expected-tests 1331
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
