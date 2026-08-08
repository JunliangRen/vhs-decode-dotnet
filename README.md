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
- The Visual Studio 2026 `.slnx` solution has **1,349** standard xUnit v3 tests
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
published. The Python columns retain their audited measurements. All twenty
.NET cells were refreshed with three Release runs on this candidate, based on
main `aceec7e`. This oversized raw-FLAC fixture now correctly uses FFmpeg
because it exceeds libsndfile 1.2.2's exact-seek gate, so the former
`ced6afb`-based table is not directly comparable. Compatibility is evaluated
separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 15.207 s | 16.780 s | 7.209 s / 2.109x | 7.914 s / 2.120x | 5.388 s / 2.822x | 5.279 s / 3.179x |
| `--threads 1` | 17.694 s | 19.414 s | 12.109 s / 1.461x | 14.143 s / 1.373x | 8.568 s / 2.065x | 9.757 s / 1.990x |
| `--threads 5` | 15.719 s | 17.801 s | 7.238 s / 2.172x | 7.651 s / 2.327x | 5.266 s / 2.985x | 5.125 s / 3.473x |
| `--threads 10` | 16.037 s | 18.266 s | 5.862 s / 2.736x | 6.720 s / 2.718x | 4.967 s / 3.229x | 4.645 s / 3.933x |
| `--threads 20` | 16.405 s | 18.395 s | 5.779 s / 2.839x | 6.323 s / 2.909x | 4.634 s / 3.540x | 4.361 s / 4.218x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: dotnet-full-refresh=60 repeats=3 cti-long-paired=8 determinism=60 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**. Multi-worker compact VHS decoding
now materializes `Video`, `Envelope`, and `Chroma` while low-pass-only sync work
continues, with one bounded staged span and eager fallbacks for serial or
stateful paths. Reverse-order 1,000-frame Exact pairs reduced wall time by
2.66% for v0.4.0 and 2.55% for `current`; 600-frame IPP-fast pairs improved
v0.4.0 by 1.85% and were neutral for `current` (-0.03%).

Managed AVX now evaluates eight independent `current` CTI distance lanes while
preserving the original float32 FMA order, scalar tail, and no-AVX fallback.
Two reverse-order 1,000-frame Exact pairs were neutral at 50.687/50.793 s
(baseline/candidate, -0.21%). Two IPP-fast pairs both favored the candidate:
43.730/42.872 s (1.96% lower), with CPU time falling from 255.273 to 251.797 s
(1.36%). IPP peak memory did not increase.

All 60 refreshed matrix runs were deterministic, with one luma, chroma, raw
JSON, stdout, normalized stderr, and normalized log hash per profile across
default-5 and `--threads 1/5/10/20`. The long CTI gates also matched ordered
`fileLoc` across baseline and candidate for Exact and IPP-fast.
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
mono PCM16 and contain at most `Int32.MaxValue` samples use the bundled
libsndfile reader. Larger raw-FLAC captures use FFmpeg because libsndfile 1.2.2
cannot provide exact random access beyond that boundary. Eligible inputs can
include default 40 MHz VHS `.ldf`, VHS `--no_resample`, and LD without
`--inputfreq`; default VHS `.flac`, all CVBS inputs, Ogg/FLAC, stereo, PCM24,
other sample rates, and unfinished headers retain FFmpeg.

<!-- SECTION: build -->

## Build and test

The pinned SDK is .NET `11.0.100-preview.6.26359.118`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1349
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
