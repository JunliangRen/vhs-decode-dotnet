# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-15.01 -->

A .NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), targeting
upstream release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

The current .NET port release is `v0.4.0-2.1.0` (application version `2.1.0`).

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
- The Visual Studio 2026 `.slnx` solution has **1,485** standard xUnit v3 tests
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

### Seekable RF preview server

VHS and LaserDisc can expose a local, seekable HTTP preview without an output
base name:

Run `decode.exe vhs --preview-server --pal input.lds` for tape RF, or
`decode.exe ld --preview-server --pal input.ldf` for LaserDisc RF.

The command prints a loopback player URL and a standard HLS/fMP4 playlist URL.
`--preview-port 0` (the default) selects a free port; specify another port when
an external player needs a stable URL. Preview mode creates no TBC, JSON,
SQLite, EFM, audio, or decoder log artifacts.

This is intentionally a low-accuracy navigation mode. It retains colour through
a cheap 4fSC one-dimensional demodulator, applies lightweight dropout
concealment, skips audio and the expensive export comb/repair stages, and
samples four decoded source frames per two-second preview window. NTSC is
served as top-field-first 640x480 at 30000/1001 fps; PAL is top-field-first
768x576 at 25 fps. `--preview-crf` accepts 0 through 51 and defaults to 31;
lower values trade bitrate and encode time for quality. The preview
automatically uses `ipp-fast` when available and otherwise remains portable
through the managed backend. FFmpeg with `libx264` is required on `PATH`;
`VHSDECODE_FFMPEG` and `VHSDECODE_FFPROBE` can select explicit binaries.

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

This startup-inclusive `--start 100 --length 160` snapshot uses one fixed private
local 40 MHz PAL VHS `.ldf` fixture; its filename is intentionally not published.
It retains 30 fixed Python reference measurements from 2026-08-12. All 60 .NET
measurements were refreshed together on 2026-08-15 with the published
`v0.4.0-2.1.0` release binary from main `94504dc`. Every cell has three complete
runs.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 52.811 s | 54.243 s | 13.716 s / 3.850x | 12.758 s / 4.252x | 11.976 s / 4.410x | 9.003 s / 6.025x |
| `--threads 1` | 57.067 s | 56.762 s | 35.444 s / 1.610x | 39.498 s / 1.437x | 25.376 s / 2.249x | 28.158 s / 2.016x |
| `--threads 5` | 52.920 s | 55.722 s | 13.631 s / 3.882x | 12.943 s / 4.305x | 12.114 s / 4.368x | 9.320 s / 5.979x |
| `--threads 10` | 52.965 s | 54.949 s | 11.127 s / 4.760x | 9.704 s / 5.663x | 10.093 s / 5.248x | 7.174 s / 7.659x |
| `--threads 20` | 53.555 s | 54.842 s | 9.745 s / 5.495x | 8.382 s / 6.543x | 8.903 s / 6.016x | 6.093 s / 9.000x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-15 dotnet-current-date=2026-08-15 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). A ratio moves
when either the Python numerator or .NET denominator moves, and historical tables
using another fixture or window are not directly comparable. Same-moment .NET
revision A/B runs, rather than old ratio cells, determine causal regressions.

The 2.1.0 release adds a bounded two-field VHS wavefront. Ordered state,
output, metadata, and diagnostics remain serial; only input-independent field
tails overlap the next RF read. Exact `current` stays on its previous path after
interleaved A/B found no benefit. The completed render/dropout work releases its
large RF span before lookahead, keeping the window bounded.

The final 1,000-frame `--threads 20` gate matched every compatibility surface.
Wall time moved from 46.047 to 42.575 seconds for Exact v0.4.0, 44.980 to 42.084
seconds for IPP-fast v0.4.0, and 31.009 to 25.260 seconds for IPP-fast current.
Doubling the sampled memory run from 500 to 1,000 frames left candidate peak
working set effectively flat at 473/469 MiB versus 354/354 MiB for main.

A later Exact-current audit rejected a full cross-field wavefront after a
1,000-frame A/B measured 6.05% more wall time with unchanged effective core use.
The retained sync-analysis change is throughput-neutral, but a matched 500-frame
counter pair reduced managed allocation by 46.0%, Gen0 collections from 60 to
30, and GC pause from 44.4 to 24.2 ms. The public speed table is therefore
unchanged.

The next Exact-current pass reuses field-local classified/refined pulse lists
without changing public API ownership. Two opposite-order 1,000-frame pairs
moved median wall time from 32.95 to 32.11 seconds and CPU time from 298.40 to
288.02 seconds. Sampled allocation fell 8.65%; the conservative reverse-order
memory pair moved peak working set from 390.8 to 360.5 MiB and private bytes
from 409.9 to 374.4 MiB.

A 24-run `--threads 0`/default-five/20-worker gate and the refreshed 60-run
Exact/IPP-fast matrix each retained one hash for luma, chroma, raw JSON, stdout,
normalized stderr/logs, and ordered `fileLoc`. The standard xUnit v3 suite
passed all **1,485** tests.

Every .NET profile/thread cell was deterministic across its three refreshed
runs. Merged Python PR341 was deterministic in its pinned reference set; Python
v0.4.0 produced 15 distinct luma, chroma, JSON, and normalized-log hashes in 15
runs, so the strict oracle remains Python v0.4.0 `g4315520 --threads 0`.
Commands, ranges, binary hashes, memory bounds, and historical measurements are in the
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

The pinned SDK is .NET `11.0.100-preview.7.26381.103`.

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1485
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
