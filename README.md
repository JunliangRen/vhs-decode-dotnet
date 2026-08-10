# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-10.02 -->

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
- The Visual Studio 2026 `.slnx` solution has **1,416** standard xUnit v3 tests
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

This is a startup-inclusive 160-frame snapshot on one fixed private local
40 MHz PAL VHS `.ldf` fixture; the filename is intentionally not published.
All 90 Python and .NET Release runs were measured together in forward, reverse,
and mixed passes on this candidate, based on merged main `0306db8`. The measured
production blob and three-run ranges are pinned in the detailed notes.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 44.296 s | 43.805 s | 12.763 s / 3.471x | 11.858 s / 3.694x | 11.251 s / 3.937x | 9.487 s / 4.617x |
| `--threads 1` | 53.404 s | 53.632 s | 32.537 s / 1.641x | 38.751 s / 1.384x | 22.896 s / 2.332x | 26.221 s / 2.045x |
| `--threads 5` | 44.283 s | 43.593 s | 12.600 s / 3.514x | 12.468 s / 3.496x | 11.196 s / 3.955x | 9.393 s / 4.641x |
| `--threads 10` | 45.330 s | 45.943 s | 9.941 s / 4.560x | 9.959 s / 4.613x | 9.385 s / 4.830x | 7.611 s / 6.036x |
| `--threads 20` | 46.551 s | 46.697 s | 8.336 s / 5.584x | 7.608 s / 6.138x | 8.084 s / 5.759x | 5.806 s / 8.043x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 fused-spectrum-microbench-pairs=12 fused-spectrum-short-ab-pairs=8 fused-spectrum-long-ab-pairs=2 fused-spectrum-thread-gate-runs=12 fused-spectrum-scalar-runs=2 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). Every column
was measured in the same interleaved batch, but a ratio still moves when its
Python denominator moves. Same-fixture .NET revision A/B runs, not old ratio
cells, are therefore used to judge .NET regressions. Every .NET median in this
refresh is lower than in the previous table; several displayed speedups still
fell because the corresponding Python median fell by a larger fraction.
For example, default IPP-fast/current improved from 9.778 s to 9.487 s while
its Python median moved from 53.288 s to 43.805 s, so the ratio moved from
5.450x to 4.617x despite the lower .NET wall time.

Exact VHS now applies its two complex RF filters and Hilbert real scale in one
ordered spectrum traversal. The binary64 expressions and stage order are
unchanged; non-finite and aliased inputs retain the old scalar/sequential
behavior. Twelve alternating 1M-element microbenchmark pairs improved this
kernel from 4.280 to 2.976 ms (1.438x). Four 400-frame pairs improved the
end-to-end median from 16.50 to 16.33 seconds; profile-matched 1,000-frame runs
observed 0.65% lower wall time for `current` and 2.10% lower wall time for
v0.4.0, one pair each rather than a sustained claim. Every artifact and log
surface matched across the A/B, six thread modes, and fully scalar fallback;
working set remained bounded with small first-to-final-third variation.

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
  --no-build --no-restore --minimum-expected-tests 1416
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
