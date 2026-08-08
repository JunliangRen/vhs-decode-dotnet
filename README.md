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
- The Visual Studio 2026 `.slnx` solution has **1,368** standard xUnit v3 tests
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

The table uses one fixed private local 40 MHz PAL VHS `.ldf` fixture and the
same 40-frame window for every run; the filename is intentionally not
published. Every cell is a fresh median of three reordered Release runs from
this candidate, based on merged main `63251d8`; no result is carried forward
from another fixture or build. Compatibility is evaluated separately from
speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 17.680 s | 20.173 s | 4.096 s / 4.316x | 4.618 s / 4.369x | 3.604 s / 4.906x | 3.392 s / 5.948x |
| `--threads 1` | 18.995 s | 21.062 s | 11.613 s / 1.636x | 13.183 s / 1.598x | 8.407 s / 2.259x | 9.340 s / 2.255x |
| `--threads 5` | 17.599 s | 19.493 s | 4.307 s / 4.086x | 4.805 s / 4.057x | 3.525 s / 4.993x | 3.203 s / 6.087x |
| `--threads 10` | 17.182 s | 19.727 s | 3.240 s / 5.303x | 3.764 s / 5.241x | 3.142 s / 5.468x | 2.830 s / 6.972x |
| `--threads 20` | 17.594 s | 19.583 s | 2.730 s / 6.445x | 3.168 s / 6.181x | 2.667 s / 6.598x | 2.327 s / 8.416x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-refresh=90 repeats=3 mapped-ab=36 mapped-long=4 dotnet-determinism=60 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**. An older public table used a
different private NTSC Betamax HiFi fixture and is not directly comparable.
On this PAL fixture, previous main also paid a real cost by routing oversized
raw FLAC through FFmpeg. The candidate maps only narrowly eligible fixed-block,
no-seektable files through libsndfile during ordinary parallel VHS decoding;
serial, diagnostic, resampled, and ineligible inputs keep the established
FFmpeg path with one-way fallback on any native-read failure.

Direct baseline/candidate gates reduced Exact `current --threads 20` wall time
from 14.876 to 11.135 seconds over 200 frames (25.15%) and from 53.230 to
44.055 seconds over 1,000 frames (17.24%); effective cores rose from 6.76 to
7.41 and peak working set fell from 773.2 to 402.4 MiB in the long pair.
At 32 workers, 100 frames fell 33.41%. Exact v0.4.0 and IPP-fast `current`
200-frame gates improved 22.24% and 24.64% respectively, while the unchanged
single-worker route was neutral.

All 36 strict A/B runs matched luma, chroma, raw JSON, ordered `fileLoc`,
stdout, normalized stderr, and normalized logs. All 60 .NET matrix runs were
cross-thread deterministic. IPP-fast remains an explicit numerically close
backend, so its artifacts are not claimed to match Exact byte for byte. Python
v0.4.0 produced 14 luma/chroma hashes across its 15 nonzero-worker matrix runs,
so Python v0.4.0 `g4315520 --threads 0` remains the strict oracle. Commands,
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
`--threads 0/1`, debug-plot and GNU Radio AFE modes, other command families,
default VHS `.flac`, CVBS, Ogg/FLAC, stereo, PCM24, other sample rates, and
unfinished or ineligible headers retain FFmpeg.

<!-- SECTION: build -->

## Build and test

The pinned SDK is .NET `11.0.100-preview.6.26359.118`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1368
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
