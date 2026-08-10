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
- The Visual Studio 2026 `.slnx` solution has **1,417** standard xUnit v3 tests
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
private local 40 MHz PAL VHS `.ldf` fixture; the filename is intentionally not published.
All 90 Python and .NET Release runs were measured together in forward, reverse,
and mixed passes on this candidate, based on merged main `3e01fdd`. The measured
production blob and three-run ranges are pinned in the detailed notes.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 46.064 s | 45.418 s | 13.109 s / 3.514x | 12.868 s / 3.530x | 11.499 s / 4.006x | 9.725 s / 4.670x |
| `--threads 1` | 55.170 s | 55.257 s | 33.228 s / 1.660x | 39.682 s / 1.393x | 23.625 s / 2.335x | 26.775 s / 2.064x |
| `--threads 5` | 46.247 s | 45.834 s | 13.377 s / 3.457x | 12.807 s / 3.579x | 11.552 s / 4.003x | 9.842 s / 4.657x |
| `--threads 10` | 47.318 s | 48.504 s | 10.567 s / 4.478x | 9.664 s / 5.019x | 9.637 s / 4.910x | 7.933 s / 6.114x |
| `--threads 20` | 48.349 s | 48.631 s | 8.675 s / 5.574x | 8.134 s / 5.979x | 8.290 s / 5.832x | 6.110 s / 7.959x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 sos-state-current-160-ab-pairs=6 sos-state-default-160-ab-pairs=4 sos-state-current-1000-ab-pairs=1 sos-state-thread-gate-runs=6 sos-state-jit-disassemblies=2 sos-state-focused-test-runs=2 sos-reverse-32k-microbench-pairs=21 sos-reverse-current-160-ab-pairs=8 sos-reverse-v040-160-ab-pairs=1 sos-reverse-current-1000-ab-pairs=1 sos-reverse-thread-gate-runs=6 sos-focused-test-runs=80 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). Every column
was measured in the same interleaved batch, but a ratio still moves when its
Python denominator moves. Same-fixture .NET revision A/B runs, not old ratio
cells, are therefore used to judge .NET regressions.

The generic managed Exact float32 SOS kernels now address their two recursive
state values per section through a tracked span reference, removing two bounds
branches from each forward and backward inner loop without changing arithmetic
order. Across six interleaved 160-frame `current --threads 20` pairs, median wall
time fell from 11.499 to 11.373 seconds (1.10%), throughput rose 1.12%, and CPU
time fell 1.56%; the candidate won five pairs and all nine compatibility surfaces
matched. A separate 1,000-frame pair improved from 42.463 to 42.221 seconds
(0.57%) with 1.03% less CPU time and bounded memory.

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
  --no-build --no-restore --minimum-expected-tests 1417
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
