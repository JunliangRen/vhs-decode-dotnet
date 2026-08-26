# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-21.01 -->

A .NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), targeting
upstream release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

The current .NET port release is `v0.4.0-2.9.0` (application version `2.9.0`).

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
- The Visual Studio 2026 `.slnx` solution has **1,614** standard xUnit v3 tests
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

The release workflow also builds a self-contained, multi-file glibc
`linux-x64` tar. It supports only the portable `exact` backend and bundles the
Linux SQLite, libsndfile, and libsoxr native assets; Windows IPP/CUDA DLLs are
excluded. After installing FFmpeg and the documented OS libraries:

```bash
tar -xzf vhs-decode-dotnet-linux-x64.tar.gz
cd vhs-decode-dotnet-linux-x64
./vhs-decode --pal --dsp-backend exact input.lds output
```

See [Linux x64 release](docs/LINUX_X64.md) for the Ubuntu 22.04/glibc 2.35
baseline, runtime packages, checksums, build provenance, and release gates.

### Seekable RF preview server

VHS and LaserDisc can expose a local, seekable HTTP preview without an output
base name:

Run `decode.exe vhs --preview-server --pal input.lds` for tape RF, or
`decode.exe ld --preview-server --pal input.ldf` for LaserDisc RF.

The command prints a loopback player URL and a standard HLS/fMP4 playlist URL.
The default address starts at `127.0.0.1:8080`; if occupied, startup increments
the port through `8180` until one binds. An explicit `--preview-port` is strict,
while `--preview-port 0` asks the operating system for a dynamic port. Preview mode creates no TBC, JSON,
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
so bitrate is not identical across backends. Eligible native-rate 40 MSPS
PAL/NTSC VHS preview first performs a lightweight CUDA-driver/device preflight;
this does not load cuFFT, create a CUDA context, or initialize NVENC. A passing
device then gets one full CUDA/cuFFT/NVENC initialization attempt. If preflight
or full startup is unavailable, preview falls back to `ipp-fast` when available,
then to the portable managed backend. Other preview inputs start with that same
IPP-to-managed CPU order. Standard 40 MSPS VHS preview also applies a fixed anti-alias filter and
decodes its internal RF stream at 20 MSPS. Native 20 MSPS VHS input stays at
20 MSPS. In other words, supported VHS preview routes force the same behavior as
the full-decode `--decode-at-20msps` switch. Full VHS decode can opt into that
switch with `ipp-fast` or `cuda-fast`; Exact, S-VHS, other tape formats, and
LaserDisc retain their existing sample-rate behavior. Startup reports the selected video pipeline, IPP-FAST initialization,
active decoder thread count, and separate in-place window-ID and real-time-FPS
lines. A matching
FFmpeg build is required on `PATH`; `VHSDECODE_FFMPEG` and `VHSDECODE_FFPROBE`
can select explicit binaries.

Native-rate 40 MSPS PAL/NTSC VHS therefore selects the independent GPU preview
path automatically on a compatible machine. The same path can be pinned explicitly:

```powershell
decode.exe vhs --preview-server --dsp-backend cuda-fast --pal input.ldf
```

This keeps one CUDA context across windows, performs the anti-aliased 40-to-20
MSPS reduction, sync, FM/chroma/dropout processing, NV12 bob rendering, and
NVENC H.264 encoding on the GPU. The renderer writes a block-linear NV12 CUDA
array that NVENC registers directly, avoiding its pitch-linear conversion. Each bounded RF batch is uploaded once, while full luma, chroma, and NV12 frames are
never downloaded; only small sync/field-order control metadata and compressed
H.264 packets cross the host/device boundary. FFmpeg only copy-muxes the H.264
into HLS/fMP4. An explicit `--dsp-backend cuda-fast` request requires a compatible
NVIDIA GPU and never falls back to the CPU preview or another encoder. Automatic
default selection falls back only if GPU startup fails; it never changes backend
after a preview session has started. The existing GPU bob
deinterlacer is unchanged. Preview-only cross-field dropout substitution uses a
clean opposite-parity field when one exists in the bounded batch, and a
one-field 75/25 current/previous chroma blend resets at every seek window.

A sustained local resource matrix on 2026-08-20 used the same real 40 MSPS PAL
capture, an Intel Core Ultra 7 265K (20 logical processors), and an RTX 4070.
The first five rows came from source commit `41bfd92`; the corrected IPP
preview row came from `1fb1455`. Each row is the mean of two independent
process launches; ranges are the two observed source-frame rates.

| Path | Source fps (range) | `decode.exe` CPU | Whole-system CPU | GPU SM avg/peak | NVENC avg/peak | Peak GPU FB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Full, CUDA 40 MSPS | 35.30 (35.28-35.33) | 10.81% / 2.16 cores | 33.89% | 32.44% / 72% | 0% / 0% | 7,038 MiB |
| Full, IPP 40 MSPS | 23.79 (23.71-23.87) | 22.15% / 4.43 cores | 28.76% | 0.10% / 4% | 0% / 0% | 3,102 MiB |
| Full, CUDA 20 MSPS | 35.80 (35.60-35.99) | 10.92% / 2.18 cores | 34.74% | 27.67% / 54% | 0% / 0% | 5,288 MiB |
| Full, IPP 20 MSPS | 26.86 (26.25-27.48) | 24.19% / 4.84 cores | 48.39% | 0% / 0% | 0% / 0% | 3,102 MiB |
| Preview, CUDA 20 MSPS | 47.03 (46.59-47.48) | 4.06% / 0.81 cores | 24.75% | 26.91% / 61% | 3.12% / 8% | 4,795 MiB |
| Preview, IPP 20 MSPS | 34.33 (34.05-34.61) | 24.66% / 4.93 cores | 43.85% | 1.52% / 9% | 1.48% / 4% | 3,292 MiB |

Full runs requested 500 source frames and verified exactly 1,000 output fields.
Preview runs requested 20 distinct two-second windows, or 1,000 source frames,
after a separate cold W5. Source fps does not count the two output fields/bob
frames as two source frames. Process CPU is normalized against all 20 logical
processors; whole-system CPU includes FFmpeg, drivers, the sampler, and other
machine work. GPU values are global 100 ms NVML samples. The first five rows'
idle baseline was 5.05% system CPU, 0% GPU SM, 0% NVENC, and 3,103 MiB GPU FB;
the separately corrected IPP preview row used 6.85% system CPU and 3,110 MiB
GPU FB. CUDA delivered 1.48x/1.33x/1.37x the IPP source throughput in
full-40/full-20/preview-20 respectively. See the
[detailed method and evidence](docs/README.detailed.md#current-ippcuda-resource-matrix).

The final-source five-window quality recheck used corrected IPP coordinates.
After trimming the first IPP output frame for one-field alignment, default CUDA
preview averaged SSIM Y/U/V/All of 0.914657/0.957361/0.966698/0.930448. The
forced line-phase guard averaged 0.926692 combined and disabling cross-field
dropout plus chroma stabilization averaged 0.922357; default was higher on
every tested window. These are capture- and hardware-specific preview results,
not Exact-equivalence claims.

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
| `cuda-fast` | Experimental Windows x64 NVIDIA CUDA 13 full-signal VHS path. It has an independent numerical contract, supports PAL/NTSC VHS at 40 MSPS normally or GPU 40-to-20/native-20 MSPS with `--decode-at-20msps`, and never silently falls back to a CPU backend. |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
decode.exe vhs --dsp-backend ipp-fast --decode-at-20msps `
  --pal input.lds output-20msps
decode.exe vhs --dsp-backend cuda-fast --pal `
  --decode-at-20msps --start 100 --length 20 input.ldf output
```

`--decode-at-20msps` is a VHS preview-quality mode, not an Exact-equivalence
mode. A 40 MSPS source is anti-alias filtered and decoded internally at 20
MSPS; native 20 MSPS input is decoded without another reduction. TBC metadata
`fileLoc` remains in original input-sample coordinates.
This is a preview-quality rate choice, not a universal throughput switch. In
the current startup-inclusive 500-frame gate it raised CUDA throughput by
1.40% and IPP throughput by 12.91%. An earlier 100-frame gate showed IPP 6.83%
slower because fixed startup and reduction costs dominated that short request;
benchmark the intended capture and run length before selecting it for full
decode.

The default Windows release includes the small CUDA-fast bridge but does not
embed the 271 MiB cuFFT DLL. An explicit `--dsp-backend cuda-fast` request, or
an eligible automatic VHS preview after its lightweight driver/device preflight
passes, searches for a compatible CUDA 13/cuFFT 12 installation. If none is available,
it verifies the NVIDIA driver first, downloads the pinned 202.2 MiB NVIDIA
redistributable, validates both the archive and DLL with SHA-256, and installs
it once under `%LOCALAPPDATA%\vhs-decode-dotnet\cuda\cufft`. A failed lightweight
preflight never enters the resolver or accesses the network. Exact, IPP, and
preview inputs outside the automatic CUDA support surface also remain offline.
Set `VHSDECODE_CUDA_RUNTIME_PATH` for an
offline/system runtime, `VHSDECODE_CUDA_CACHE_PATH` for a different cache root,
or `VHSDECODE_CUDA_AUTO_DOWNLOAD=0` to disable automatic downloads.

LaserDisc now routes its video, EFM, and analog-audio full-complex FFT stages
through IPP. CVBS and HiFi still reject `ipp-fast`; use `exact` whenever
release-compatible behavior is required. See the
[detailed backend notes](docs/README.detailed.md#performance) before using IPP
or CUDA for compatibility-sensitive work. On the tested RTX 4070 and one real
PAL capture, the quality-corrected FP32 CUDA-full path is now visually much
closer to Exact. In the sustained matrix above, CUDA measured 35.30 source fps
versus IPP's 23.79 (1.48x) at 40 MSPS, and 35.80 versus 26.86 (1.33x) at
20 MSPS. A separate short same-source session measured materially higher CUDA
throughput, so these figures are descriptive snapshots rather than evidence
that later code alone reversed the older CUDA/IPP result. Each variant's
two luma/chroma/JSON output sets were byte-identical within that variant.
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
measurements were refreshed together on 2026-08-26 with one self-contained .NET
11 Preview 7 candidate based on PR commit `e0777e2` plus the wrapped-tail safety
adjustment described below. Every cell has three complete runs.
Compatibility is evaluated separately from speed.

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode (workers) | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default (5) | 52.811 s | 54.243 s | 11.763 s / 4.489x | 11.271 s / 4.813x | 10.394 s / 5.081x | 8.129 s / 6.673x |
| `--threads 1` | 57.067 s | 56.762 s | 33.318 s / 1.713x | 37.392 s / 1.518x | 22.578 s / 2.528x | 24.406 s / 2.326x |
| `--threads 5` | 52.920 s | 55.722 s | 11.746 s / 4.505x | 11.132 s / 5.006x | 10.215 s / 5.181x | 8.171 s / 6.820x |
| `--threads 10` | 52.965 s | 54.949 s | 9.071 s / 5.839x | 8.090 s / 6.792x | 8.446 s / 6.271x | 6.089 s / 9.024x |
| `--threads 20` | 53.555 s | 54.842 s | 7.267 s / 7.369x | 7.182 s / 7.636x | 7.024 s / 7.625x | 4.962 s / 11.052x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_PHASE66: trace-runs=2 rejected-candidates=4 160-ab-pairs=2 500-ab-pairs=2 1000-ab-pairs=2 screening-matrix-runs=60 public-cell-runs=60 tests=1614 -->
<!-- LATEST_PERFORMANCE_PHASE66_RUNS: dotnet-date=2026-08-26 dotnet-matrix-runs=60 dotnet-repeats=3 python-reference-date=2026-08-12 python-reference-runs=30 -->
<!-- LATEST_PERFORMANCE_PHASE66_EVIDENCE: 1000-pairs=2 1000-combined-wall=60.344/58.614s 1000-wall-gain=2.87% 1000-combined-cpu=575.500/555.969s 1000-cpu-gain=3.39% low-worker-json-regression-caught=1 wrapped-sinc-tail-guard=1 linux-allocation-ci-fix=1 -->
<!-- LATEST_PERFORMANCE_PHASE63: trace-runs=1 rejected-candidates=6 short-ab-pairs=3 500-ab-pairs=3 1000-ab-pairs=3 thread-gate-runs=16 public-cell-runs=60 tests=1613 -->
<!-- LATEST_PERFORMANCE_PHASE63_RUNS: dotnet-date=2026-08-26 dotnet-matrix-runs=60 dotnet-repeats=3 python-reference-date=2026-08-12 python-reference-runs=30 -->
<!-- LATEST_PERFORMANCE_PHASE63_EVIDENCE: 1000-pairs=3 1000-independent-wall-medians=30.213/29.576s 1000-paired-wall-gain-median=1.35% 1000-independent-cpu-medians=290.719/279.297s 1000-paired-cpu-gain-median=3.93% t0-frames=160 t0-pairs=3 t0-independent-wall-medians=37.492/36.962s t0-paired-wall-gain-median=0.43% -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-24 dotnet-current-date=2026-08-24 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 phase42-public-cell-runs=60 phase42-tests=1609 phase52-current-short-ab-pairs=8 phase52-v040-short-ab-pairs=4 phase52-long-ab-pairs=2 phase52-public-cell-runs=60 phase52-intrinsic-runs=3 phase52-tests=1610 phase59-short-ab-pairs=3 phase59-500-ab-pairs=3 phase59-1000-ab-pairs=3 phase59-public-cell-runs=60 phase59-intrinsic-runs=2 phase59-tests=1610 phase60-short-ab-pairs=3 phase60-500-ab-pairs=2 phase60-1000-ab-pairs=2 phase60-thread-gate-runs=4 phase60-memory-runs=1 phase60-public-cell-runs=60 phase60-tests=1613 phase61-trace-runs=1 phase61-rejected-candidates=2 phase61-short-ab-pairs=3 phase61-500-ab-pairs=2 phase61-1000-ab-pairs=2 phase61-thread-gate-runs=6 phase61-memory-runs=1 phase61-public-cell-runs=60 phase61-intrinsic-runs=2 phase61-tests=1613 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

Each .NET cell shows median wall time and speedup versus its profile-matched
Python column. The default is **5 workers**; three-run ranges are in the
[detailed performance notes](docs/README.detailed.md#performance). A ratio moves
when either the Python numerator or .NET denominator moves, and historical tables
using another fixture or window are not directly comparable. Same-moment .NET
revision A/B runs, rather than old ratio cells, determine causal regressions.

The current candidate avoids copying the unused tail of staged VHS Video/Chroma
payloads on the existing 20-worker segmented-envelope path. It extends the
prefix by whole source blocks when actual line locations require more, while
negative Python-style coordinates, wrapped 16-tap sinc reads, non-linear wow
interpolation, low-worker dropout means, raw metrics, and DC adjustment all
retain full materialization. Two opposite-order 1,000-frame Exact
`current --threads 20` pairs reduced combined wall time from 60.344 to 58.614
seconds (2.87%) and CPU time from 575.500 to 555.969 seconds (3.39%). Memory
stayed bounded, but no reduction is claimed.

The refreshed 60-run Exact/IPP-fast matrix retained one hash for luma, chroma,
raw JSON, stdout, normalized stderr/logs, and ordered `fileLoc` in every cell and
across worker counts. The latest standard xUnit v3 **1,614**-test suite passed
with 1,611 successes and 3 expected environment skips.

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
  --no-build --no-restore --minimum-expected-tests 1614
```

Open `VHSDecodeDotNet.slnx` in Visual Studio 2026 to build, debug, and run the
xUnit v3 suite through Test Explorer.

On Ubuntu 22.04 x64, the complete native-build, test, multi-file publish,
reproducible-tar, and final extracted-tar smoke pipeline is:

```bash
pwsh ./tools/build-linux-x64-release.ps1
```

<!-- SECTION: detail -->

## More detail

- [Detailed English reference](docs/README.detailed.md)
- [Compatibility evidence](docs/COMPATIBILITY_EVIDENCE.md)
- [Simplified Chinese overview](README.zh-CN.md)
- [Japanese overview](README.ja.md)

<!-- SECTION: license -->

## License

GPL-3.0. See [`LICENSE`](LICENSE).
