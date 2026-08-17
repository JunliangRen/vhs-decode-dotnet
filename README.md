# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-18.01 -->

A .NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), targeting
upstream release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

The current .NET port release is `v0.4.0-2.3.1` (application version `2.3.1`).

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
- The Visual Studio 2026 `.slnx` solution has **1,562** standard xUnit v3 tests
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
a cheap 4fSC one-dimensional demodulator, derives the PAL V-switch from
neighbouring burst lines to avoid four-field hue flicker, applies lightweight dropout
concealment, skips audio and the expensive export comb/repair stages, and
decodes the full continuous frame count for every two-second preview window.
The muted web player starts automatically and keeps two windows of lookahead
buffered. Top-field-first source fields are deinterlaced at field rate: NTSC is
served as progressive 640x480 at 60000/1001 fps and PAL as progressive 768x576
at 50 fps. At startup the preview validates complete fMP4 pipelines in this
order: NVENC with CUDA YADIF, QSV with advanced VPP deinterlacing, AMF with CPU
YADIF, then libx264 with CPU YADIF. `--preview-crf` accepts 0 through 51 and
defaults to 31; hardware encoders map it to their closest quality/QP control,
so bitrate is not identical across backends. The preview automatically uses
`ipp-fast` when available and otherwise remains portable through the managed
backend. Standard 40 MSPS VHS preview also applies a fixed anti-alias filter and
decodes its internal RF stream at 20 MSPS. Native 20 MSPS VHS input stays at
20 MSPS; S-VHS, other tape formats, LaserDisc, and every normal decode/export
path retain their existing sample-rate behavior. This is automatic and adds no
user-facing option. Startup reports the selected video pipeline, IPP-FAST initialization,
active decoder thread count, and separate in-place window-ID and real-time-FPS
lines. A matching
FFmpeg build is required on `PATH`; `VHSDECODE_FFMPEG` and `VHSDECODE_FFPROBE`
can select explicit binaries.

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
| `cuda-fast` | Experimental Windows x64 NVIDIA CUDA 13 full-signal VHS path. It has an independent numerical contract, currently supports native-rate 40 MSPS PAL/NTSC VHS only, and never silently falls back to a CPU backend. |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
decode.exe vhs --dsp-backend cuda-fast --pal `
  --start 100 --length 20 input.ldf output
```

The default Windows release includes the small CUDA-fast bridge but does not
embed the 271 MiB cuFFT DLL. Only an explicit `--dsp-backend cuda-fast` request
searches for a compatible CUDA 13/cuFFT 12 installation. If none is available,
it verifies the NVIDIA driver first, downloads the pinned 202.2 MiB NVIDIA
redistributable, validates both the archive and DLL with SHA-256, and installs
it once under `%LOCALAPPDATA%\vhs-decode-dotnet\cuda\cufft`. Exact, IPP, and
preview runs never access the network. Set `VHSDECODE_CUDA_RUNTIME_PATH` for an
offline/system runtime, `VHSDECODE_CUDA_CACHE_PATH` for a different cache root,
or `VHSDECODE_CUDA_AUTO_DOWNLOAD=0` to disable automatic downloads.

LaserDisc now routes its video, EFM, and analog-audio full-complex FFT stages
through IPP. CVBS and HiFi still reject `ipp-fast`; use `exact` whenever
release-compatible behavior is required. See the
[detailed backend notes](docs/README.detailed.md#performance) before using IPP
or CUDA for compatibility-sensitive work. On the tested RTX 4070 and one real
PAL capture, the quality-corrected FP32 CUDA-full path is now visually much
closer to Exact, but it does not exceed the measured CPU throughput. For the
same `--start_fileloc 320000000 --length 500` request, a current same-session
interleaved comparison completed CUDA runs in 15.605/15.748 seconds and
`ipp-fast --threads 20` runs in 14.108/14.064 seconds. The medians are 15.676
and 14.086 seconds (31.895 and 35.495 output fps): CUDA takes 11.29% more wall
time and provides 0.8986x the IPP throughput. Against the immediately preceding
CUDA build, a separate A-B-B-A comparison reduced the CUDA median from 21.918
to 16.057 seconds (26.74% less wall time and 36.50% more throughput). Two final
CUDA outputs were byte-identical.
An aligned 79-frame lossless comparison with Exact using the default
export-side dropout correction measured SSIM Y/U/V/All of
0.954905/0.988109/0.991285/0.972301 and PSNR Y/U/V/average of
33.196867/41.243137/43.586266/35.699053 dB. Manual inspection retained closely
matching scene content, colour, and motion, while numerical equality is not
claimed. This narrow result is hardware- and capture-specific; `cuda-fast`
remains experimental and does not share the CPU numerical contract.

<!-- SECTION: performance -->

## Latest performance

This startup-inclusive `--start 100 --length 160` snapshot uses one fixed private
local 40 MHz PAL VHS `.ldf` fixture; its filename is intentionally not published.
It retains 30 fixed Python reference measurements from 2026-08-12. All 60 .NET
measurements were refreshed together on 2026-08-15 with a self-contained .NET
11 Preview 7 candidate built from commit `21b8b01`. Every cell has three complete
runs; this documentation/toolchain refresh does not publish a new tag or release.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 52.811 s | 54.243 s | 13.977 s / 3.778x | 12.556 s / 4.320x | 12.214 s / 4.324x | 9.419 s / 5.759x |
| `--threads 1` | 57.067 s | 56.762 s | 36.434 s / 1.566x | 40.155 s / 1.414x | 25.468 s / 2.241x | 27.561 s / 2.060x |
| `--threads 5` | 52.920 s | 55.722 s | 14.055 s / 3.765x | 12.655 s / 4.403x | 12.244 s / 4.322x | 9.104 s / 6.121x |
| `--threads 10` | 52.965 s | 54.949 s | 11.795 s / 4.491x | 10.198 s / 5.388x | 10.535 s / 5.027x | 7.467 s / 7.359x |
| `--threads 20` | 53.555 s | 54.842 s | 10.667 s / 5.021x | 9.533 s / 5.753x | 9.216 s / 5.811x | 6.991 s / 7.845x |
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
passed all **1,500** tests.

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
  --no-build --no-restore --minimum-expected-tests 1562
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
