# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-19.6 -->

.NET 11 rewrite of the decode-facing parts of
[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode), focused on
release `v0.4.0` at commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`.

> [!IMPORTANT]
> This is a work-in-progress compatibility port. The top-level decode paths are
> implemented and heavily tested, but the project does not yet claim
> byte-for-byte parity for every real capture and rare option interaction.

## Contents

- [Scope](#scope)
- [Status](#status)
- [Compatibility coverage](#compatibility-coverage)
- [Performance](#performance)
- [Build and test](#build-and-test)
- [Usage](#usage)
- [Outputs and live preview](#outputs-and-live-preview)
- [Verification](#verification)
- [Remaining work](#remaining-work)
- [Detailed evidence](#detailed-evidence)
- [License](#license)

<!-- SECTION: scope -->

## Scope

This port implements only the decode applications:

- `decode.py vhs`
- `decode.py cvbs`
- `decode.py ld`
- `decode.py hifi`
- standalone aliases equivalent to `vhs-decode`, `cvbs-decode`,
  `ld-decode`, and `hifi-decode`

The following are intentionally out of scope:

- TBC utility tools and unrelated helper applications
- the double-click user-operation GUI
- Matplotlib `--debug_plot` windows and line-profiler UI/report rendering
- filter-tuning UI that is not required by the decode pipeline

Decode-side options that reference those tools are still parsed where upstream
CLI compatibility requires it.

<!-- SECTION: status -->

## Status

| Area | Status | Current boundary |
| --- | --- | --- |
| Solution and tests | Implemented | .NET 11 `.slnx`; standard xUnit v3 tests work in Visual Studio Test Explorer and with `dotnet test`. |
| CLI and arguments | Implemented and snapshot-tested | Facade and standalone help, aliases, defaults, validation, diagnostics, and exit behavior target v0.4.0. |
| VHS and tape families | Implemented; rare capture gaps remain | VHS, S-VHS, Betamax, Video8/Hi8, U-matic, Type C, EIAJ, and supported PAL/NTSC variants share the release-compatible decode path. |
| CVBS | Implemented for release-supported systems | PAL and NTSC paths run; uncommon vblank and cross-option cases need more real-capture fixtures. |
| LaserDisc | Implemented; rare capture gaps remain | Video, VBI, EFM, analog audio, AC3, RF-TBC, metadata, recovery, and PAL/NTSC paths are connected. |
| HiFi | Implemented; real-capture verification remains | Typed v0.4.0 CLI, bounded parallel decode, post-processing, WAV/FLAC output, preview, and GNU Radio mode are connected. |
| Inputs | Broadly implemented | Raw input plus common FFmpeg/PyAV-equivalent container paths are covered; rare codec and timestamp cases remain. |
| Outputs and recovery | Implemented; edge cases remain | Streaming TBC/audio output, JSON snapshots, SQLite, logs, disk-space handling, and recovery ordering are covered. |
| Interactive UI | Out of scope | Decode user UI and developer plotting/report windows are intentionally not implemented. |

“Implemented” means the runtime path exists and has focused compatibility tests.
It does not mean that every possible capture has already been proven identical.

<!-- SECTION: coverage -->

## Compatibility coverage

### Commands and parameters

- Traditional `Program.Main` entry point and `decode.py`-style dispatch.
- `decode.exe`, `vhs-decode.exe`, `cvbs-decode.exe`, `ld-decode.exe`,
  and `hifi-decode.exe` apphosts.
- Release 4.0 option names, aliases, defaults, positional arguments, help text,
  validation ordering, Python-style numeric behavior, and error formatting.
- VHS format catalogs and parameter files for the supported tape families and
  color systems.
- Standard stdin/stdout behavior and upstream-compatible file validation.

### Decode pipeline

- RF filtering, FM demodulation, sync and level detection, line-zero recovery,
  field parity, HSync refinement, TBC resampling, dropout detection, chroma,
  wow correction, AGC, and metadata generation.
- `--use_saved_levels` reuses prior sync levels, retries failed saved levels,
  and forces full detection on the next VHS field after at least 30
  line-location errors, matching v0.4.0 state behavior.
- VHS/S-VHS/Betamax/Video8/Hi8/U-matic/Type C/EIAJ routing and PAL, NTSC,
  PAL-M, PAL-N, MESECAM, NTSC-J, 405-line, and 819-line compatibility paths
  where supported by upstream release 4.0.
- LaserDisc VBI, CAV/CLV interpretation, analog audio, EFM/pre-EFM, AC3,
  automatic MTF, AGC, VITS, player-skip detection, and recovery state.
- HiFi carrier decode, dropout compensation, head-switch interpolation,
  normalization, preview, GNU Radio transport, and ordered output.

### Runtime and output behavior

- Exact or normalized upstream diagnostics for covered branches, including
  recovery offsets, field-order actions, parameter-file logging, and partial
  output finalization.
- Streaming `.tbc`, `_chroma.tbc`, JSON, SQLite, PCM, EFM, pre-EFM, RF-TBC,
  AC3, WAV, and FLAC paths where applicable.
- Periodic recovery JSON snapshots and upstream-style partial-file lifecycle.
- Active TBC, chroma, JSON, and raw audio sidecars remain concurrently readable
  while decode continues, so preview tools do not need to wait for completion.

<!-- SECTION: performance -->

## Performance

Performance work is part of the implementation, while deterministic output and
release compatibility remain the first constraint.

- `-t` / `--threads` drives bounded parallel RF demodulation and filtering;
  stream, FFmpeg, and GNU Radio reads stay ordered.
- A stream-scoped decoded RF cache avoids duplicate FFT work across overlapping
  field reads while keeping memory bounded.
- VHS uses a bounded continuous RF pipeline. One producer owns ordered input
  reads, at most 32 lookahead slots are retained, and no more than eight blocks
  decode concurrently. Each completed block is published independently, so a
  field waits only for the blocks it needs instead of an entire batch. Seek,
  stream changes, and disposal cancel and drain the producer before another
  reader can touch the FFmpeg/GNU Radio stream.
- VSync envelope/minima work and harmonic power-ratio search run concurrently
  over one shared read-only padded input. Candidate arbitration and detector
  state updates remain ordered after both branches complete.
- Long TBC sinc-resampling jobs share the worker budget and preserve output
  order; `--threads 0` and `--threads 1` retain deterministic serial paths.
- Linear wow adjustment evaluates the constant derivative once per line,
  expands it only after median/MAD repair, and overlaps source-position and
  level preparation with a fixed two-way task when workers are enabled.
- VHS heterodyne and carrier tables use bounded parallel construction. The
  phase-analysis table is reused by field decode only while carrier and phase
  parameters match; AFC changes force the original rebuild path.
- HiFi uses bounded parallel block decoding followed by ordered
  post-processing and writing.
- Managed real FFTs reuse pooled packing and scratch buffers, and float32 SOS
  forward/backward filtering operates in place over one extended buffer. This
  removes repeated large-object-heap churn while returning every rental after
  each transform.
- RF span assembly writes directly into the requested output window instead of
  allocating whole-block field arrays and slicing a second copy.
- Standard VHS field decode reuses at most two exact-length RF span buffer sets,
  matching the only two block counts a fixed read window can cover. Buffers are
  returned after synchronous field decode; public `Read` results, deferred CVBS
  rendering, and retained LD VITS sources keep independent ownership.
- AVX/FMA kernels accelerate exact float32 conversion, VHS RF-envelope
  preparation, VHS Rust-style FM angle approximation, LD quantization, VHS
  chroma rotation, and complex frequency filtering. The inverse radix-4 FFT
  and 16-tap TBC sinc kernels use pinned pointer indexing to remove bounds
  checks without changing arithmetic order; differential tests preserve exact
  transform bits and output hashes.
- Recovery metadata is disk-streamed; its snapshot queue has capacity one, and
  field-order history and RF caches have hard limits. Long decodes therefore do
  not retain every decoded field or enqueue an unbounded amount of future work.
- CUDA/OpenCL is not a runtime dependency. Current traces do not justify moving
  isolated 32K FFTs across the host/device boundary; any future optional GPU
  backend must batch a device-resident DSP stage and retain an exact CPU fallback.

On one Windows fixture machine, one-frame Release measurements were:

| Decode | This port | Python v0.4.0 |
| --- | ---: | ---: |
| NTSC VHS | 2.346 s | 7.193 s |
| NTSC LaserDisc | 1.651 s | 5.865 s |

These numbers are fixture-specific, not universal benchmarks. All current VHS
A/B runs used .NET SDK/runtime `11.0.100-preview.6.26359.118`, `--threads 20`,
default chroma, and default resampling. On a reproducible 40-frame PAL probe,
the saved pre-continuous-pipeline baseline median was 11.60 s and the current
median was 7.71 s, a 33.5% gain. Average active cores rose from roughly
2.2-2.5 to 3.3-3.7. Paired TBC, JSON, and chroma SHA-256 values were identical.

Current 40/160/320-frame sustained runs completed in 7.65/26.58/52.51 s. Peak
working sets were 1.76/1.88/1.67 GiB, while second-half medians were
1.42/1.30/1.28 GiB. The full 320 frames were written, and memory showed no
growth with decode length. Earlier allocation work also reduced a PAL LD
four-field probe from 5.12 GiB to 1.96 GiB.

The latest isolated PAL-sized TBC sinc A/B reduced the median from 3.929 ms to
3.727 ms per field, a 5.1% kernel gain. A fresh 160-frame full-path run finished
in 26.95 s; private-memory medians by quarter were 1.45/1.34/1.29/1.35 GiB with
a 1.71 GiB peak. TBC, JSON, and chroma SHA-256 values remained identical.

AVX RF-envelope preparation reduced the isolated 32K-block median from 57.5 us
to 13.3 us, a 76.9% kernel gain. The 40-frame median moved from 7.55 s to 7.39 s,
and the 160-frame run from 26.95 s to 25.70 s. Its private-memory quarter medians
were 1.34/1.48/1.50/1.45 GiB with a 1.72 GiB peak; all three hashes stayed exact.

The four-lane AVX/SSE VHS Rust-style FM unwrap reduced its isolated 32K-block
median from 610.1 us to 130.7 us, a 78.6% kernel gain. In a five-pair interleaved
40-frame full-path A/B, median wall time moved from 7.43 s to 7.41 s while median
CPU time fell from 27.88 s to 26.36 s, a 5.5% reduction. TBC, JSON, and chroma
hashes remained exact. A 160-frame run completed in 26.48 s with private-memory
quarter medians of 1.45/1.47/1.40/1.23 GiB and a 1.79 GiB peak.

The latest FFmpeg stream pass replaced per-read 16 MiB rewind reconstruction with
one bounded circular buffer. The isolated 384-read median fell from 695.4 ms to
48.7 ms, while allocations fell from 4.31 GB to 142.6 MB. In a three-run
40-frame A/B, median wall/CPU time moved from 8.98/28.47 s to 7.40/22.33 s;
all three output hashes remained exact. Sampled `byte[]` allocation fell from
36.3 GB to 209 MB. A 160-frame run finished in 25.86 s with private-memory
quarter medians of 0.76/1.15/1.42/1.14 GiB and a 1.67 GiB peak.

The latest VHS real-FFT pass reuses exact-length half spectra, Hilbert buffers,
the raw envelope, and rotation inputs through a decoder-owned pool capped at 16
workspaces. In five isolated 384-block A/B runs, median time fell from 1,140.6 ms
to 1,054.0 ms (7.6%), allocation fell from 2.216 GB to 906.8 MB (59.1%), and
median Gen2 collections fell from 168 to 56. A 160-frame full-path A/B remained
wall-time neutral at 24.54/24.57 s while CPU time fell from 78.03 s to 70.13 s
(10.1%). The current run peaked at 1.68 GiB; its private-memory quarter medians
were 0.88/1.55/0.78/1.51 GiB rather than a monotonic rise. TBC, JSON, chroma, and
isolated block hashes remained exact.

The subsequent float32 SOS pass preserves sample-major arithmetic order while
keeping one-, two-, and four-section cascade states in locals. Other cascade
sizes use flat bounded state: stack storage through 32 sections and a heap
fallback above that limit. Five-run isolated 32K medians for two/four sections
fell from 110.2/155.4 ms to 75.3/83.3 ms (31.7%/46.4%); five/eight/ten-section
medians fell by 38.8%/40.2%/42.7%. Across two 160-frame A/B pairs, median wall
time fell from 21.22 to 20.57 s (3.1%) and CPU time from 73.31 to 68.73 s
(6.3%). TBC, JSON, and chroma hashes remained exact. The current pair's median
private-memory peak was 1.71 GiB, and quarter-run memory was not monotonic.

<!-- SECTION: build -->

## Build and test

Requirements:

- .NET SDK `11.0.100-preview.6.26359.118` (pinned by `global.json`)
- Visual Studio 2026 for IDE use
- `ffmpeg` and `ffprobe` on `PATH` for FFmpeg-backed container inputs
- `ffmpeg` for default HiFi FLAC output

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

The current formal Release build has zero warnings and errors. The xUnit v3
project exposes **779** independently discoverable tests to both
`dotnet test` and Visual Studio Test Explorer.

<!-- SECTION: usage -->

## Usage

Show facade or standalone help:

```powershell
dotnet run --project src/VHSDecode.Cli -- vhs --help
dotnet run --project src/VHSDecode.Cli -- cvbs --help
dotnet run --project src/VHSDecode.Cli -- ld --help
dotnet run --project src/VHSDecode.Cli -- hifi --help
```

After a Release build, use either facade dispatch or an apphost alias:

```powershell
src\VHSDecode.Cli\bin\Release\net11.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net11.0\vhs-decode.exe [upstream options] input output
```

Use the matching `cvbs`, `ld`, or `hifi` command and its upstream v0.4.0
arguments. Run `--help` for the exact accepted surface.

<!-- SECTION: preview -->

## Outputs and live preview

Video decode output files are opened with read/write sharing compatible with
upstream Python behavior. While a decode is active:

- `.tbc` and `_chroma.tbc` can be opened and read as they grow.
- published `.tbc.json` recovery snapshots can be parsed by another process.
- LD `.pcm`, `.efm`, and `.prefm` sidecars can be read concurrently.
- allowing readers does not add a copy or lock on the write hot path; practical
  performance depends mainly on competing storage I/O from the preview tool.

The writer remains the authority for file length and snapshot publication.
Readers must tolerate a growing TBC file and replace/reopen JSON snapshots.

<!-- SECTION: verification -->

## Verification

The test suite is standard xUnit v3, not a custom test executable. Coverage
includes:

- CLI/help/error snapshots and format/parameter matrices
- deterministic DSP and floating-point compatibility fixtures
- serial/worker output and state-transition comparisons
- TBC, chroma, JSON, SQLite, audio, and sidecar lifecycle tests
- recovery, seek, parity, field-order, and diagnostic ordering
- active-output sharing and partial-file readability
- differential fixtures generated against upstream release 4.0

Verified fixtures include byte-exact outputs and stable SHA-256 baselines.
The full per-algorithm inventory and hashes are kept in the shared evidence
document linked below.

<!-- SECTION: remaining -->

## Remaining work

These are bounded parity and verification gaps, not missing top-level commands:

- rare container codec and timestamp behavior outside current fixtures
- additional HiFi real-capture end-to-end baselines
- PAL LaserDisc, AC3, and verbose VITS real-capture edge cases
- uncommon VHS/CVBS vblank, chroma track-phase, and cross-option interactions
- rare first-HSync/vblank recovery and complete JSON/SQLite field metadata
- remaining TBC writer bit-compatibility edges and output parity across every
  format, option combination, and real capture
- continued CPU utilization, allocation, SIMD, and worker-scheduling profiling
  after compatibility is protected by fixtures

Interactive decode UI and TBC utility tools are outside this goal and are not
tracked as remaining decode compatibility work.

<!-- SECTION: evidence -->

## Detailed evidence

The previous long-form implementation and differential verification inventory
is preserved in
[`docs/COMPATIBILITY_EVIDENCE.md`](docs/COMPATIBILITY_EVIDENCE.md). It contains
the detailed algorithm notes, numerical boundaries, output hashes, and fixture
results shared by all language versions of this README.

<!-- SECTION: license -->

## License

GPL-3.0. See [`LICENSE`](LICENSE).
