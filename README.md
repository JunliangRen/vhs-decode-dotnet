# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-11.01 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,421** standard xUnit v3 tests
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

This is a startup-inclusive `--start 100 --length 160` snapshot on one fixed
private local 40 MHz PAL VHS `.ldf` fixture; its filename is intentionally not
published. All 90 Python and .NET Release runs were measured together in
forward, reverse, and mixed passes on this candidate, based on merged main
`a059580`. The measured binary and three-run ranges are pinned in the detailed
notes. Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 45.605 s | 45.124 s | 12.838 s / 3.552x | 12.749 s / 3.539x | 11.419 s / 3.994x | 9.518 s / 4.741x |
| `--threads 1` | 53.818 s | 53.974 s | 33.276 s / 1.617x | 39.096 s / 1.381x | 23.695 s / 2.271x | 26.727 s / 2.019x |
| `--threads 5` | 46.906 s | 45.771 s | 12.832 s / 3.655x | 12.184 s / 3.757x | 11.334 s / 4.139x | 9.742 s / 4.698x |
| `--threads 10` | 46.271 s | 47.014 s | 10.500 s / 4.407x | 9.820 s / 4.788x | 9.460 s / 4.891x | 7.635 s / 6.158x |
| `--threads 20` | 47.342 s | 47.571 s | 8.284 s / 5.715x | 8.215 s / 5.791x | 8.071 s / 5.866x | 5.993 s / 7.938x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 complex32-current-160-ab-pairs=6 complex32-t1-160-ab-pairs=4 complex32-current-1000-ab-pairs=3 complex32-focused-tests=4 complex32-traces=2 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). Every column
was measured in the same interleaved batch, but a ratio still moves when its
Python denominator moves. Ratios from historical matrices that used another
format or fixture are not directly comparable. Same-fixture .NET revision A/B
runs, not old ratio cells, are therefore used to judge .NET regressions.

The Exact float32 PocketFFT second pass now transforms each already contiguous,
owned packet in place, removing its temporary packet allocation or rental and
full packet copy.
Factorization, roots, arithmetic, and scatter order are unchanged. Across six
interleaved 160-frame `current --threads 20` pairs, median wall time fell from
11.59 to 11.27 seconds (2.8%), throughput rose 2.8%, and CPU time fell 1.9%; the
candidate won five pairs and all nine compatibility surfaces matched. Four
one-worker pairs improved 0.8% with neutral CPU and peak memory. Three
1,000-frame pairs were scheduling-noisy and throughput-neutral (+0.14% paired
median); peak working set remained bounded near 443 MiB.

Merged Python PR341 was deterministic here; Python v0.4.0 produced 15 distinct
luma, chroma, JSON, and log hashes in 15 runs, so the strict oracle remains
Python v0.4.0 `g4315520 --threads 0`.
Commands, hardware, hashes, memory bounds, and historical measurements are in the
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
  --no-build --no-restore --minimum-expected-tests 1421
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
