# vhs-decode-dotnet

**[English](README.md)** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-20.15 -->

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
- Exact 40.0 MHz `.s16` inputs use the native signed-16 loader instead of a
  no-op FFmpeg pass-through. Other formats and actual resampling keep their
  existing FFmpeg paths.
- Packed `.lds` input decodes directly into the requested result array,
  including Python-compatible partial tail groups, instead of allocating and
  copying a second fully unpacked array.
- A stream-scoped decoded RF cache avoids duplicate FFT work across overlapping
  field reads while keeping memory bounded.
- VHS uses a bounded continuous RF pipeline. One producer owns ordered input
  reads, at most 32 lookahead slots are retained, and no more than eight blocks
  decode concurrently. Each completed block is published independently, so a
  field waits only for the blocks it needs instead of an entire batch. Seek,
  stream changes, and disposal cancel and drain the producer before another
  reader can touch the FFmpeg/GNU Radio stream. Completed blocks copy their
  disjoint trimmed ranges into the final RF span in parallel under the same
  worker bound; serial and stateful block paths retain ordered assembly.
- VSync envelope/minima work and harmonic power-ratio search run concurrently
  over one shared read-only padded input. Candidate arbitration and detector
  state updates remain ordered after both branches complete. NumPy-compatible
  float64 medians retain full sorting for small inputs and use bit-exact
  introselect from 32K samples.
- VSync serration measurement reads its candidate window through a read-only
  span and applies an `Enumerable.Min`-compatible float64 scan, avoiding an
  extra full-window copy. Median scratch ownership and NaN/signed-zero bit
  semantics remain unchanged.
- VHS field decode overlaps luma TBC rendering with chroma field decoding when
  workers are enabled. Only one chroma task can be in flight, and its state is
  committed on the calling thread before the next field advances.
- Long TBC sinc-resampling jobs share the worker budget and preserve output
  order; `--threads 0` and `--threads 1` retain deterministic serial paths.
- Linear wow adjustment evaluates the constant derivative once per line,
  expands it only after median/MAD repair, and overlaps source-position and
  level preparation with a fixed two-way task when workers are enabled.
- VHS heterodyne and carrier tables use bounded parallel construction and a
  session-owned one-entry cache. Exact-key hits reuse the original arrays;
  sample-shape, carrier, phase, or AFC changes replace the prior entry instead
  of growing retained state. Phase analysis reads the field-owned resampled
  array directly. Decode borrows that same read-only array when no chroma
  prefilter is configured; configured filtering still returns owned output,
  and the public prefilter API retains its independent-copy contract.
- Internal VHS chroma comb and automatic gain share one line-sized stack
  workspace, and the decode-only path maps scaled samples directly into the
  final `ushort[]`. AVX2/SSE4.1 handles the saturating body while an exact scalar
  fallback preserves unsupported-CPU and tail semantics. Public comb, gain, and
  conversion APIs retain their independent-output contracts.
- HiFi uses bounded parallel block decoding followed by ordered
  post-processing and writing.
- Managed real FFTs reuse pooled packing and scratch buffers. Float32 SOS
  forward/backward filtering rents one extended buffer, operates in place, and
  returns it synchronously; returned output arrays retain normal ownership.
- Double-precision BA IIR forward/backward filtering also operates on one
  in-place padded workspace. Its private pool retains at most three arrays per
  bucket through 4M samples, returns them synchronously, and keeps every result
  in an independently owned exact-length array.
- RF span assembly writes directly into the requested output window instead of
  allocating whole-block field arrays and slicing a second copy.
- Default linear TBC resampling rents its per-field source-position and
  level-adjust workspaces, uses exact spans, and returns both after every
  synchronous serial or parallel resample.
- VHS diff-demod spike repair reuses one full-length complex scratch array
  inside the existing 16-slot real-FFT workspace pool. Returned analytic arrays
  retain independent ownership; non-VHS paths keep their allocating fallback.
- On little-endian hosts, TBC and chroma samples stream directly from their
  `ushort` spans without allocating a full-field byte copy. The big-endian
  fallback uses one returned pooled buffer, so repeated writes remain bounded.
- Real multi-worker VHS sessions use a dedicated capacity-one payload writer.
  It writes luma and chroma concurrently while the producer decodes the next
  field, and owns payload, metadata-snapshot, and completion ordering. Shutdown
  drains the queue; serial and public custom-reader paths retain synchronous
  ordered writes.
- Standard VHS field decode reuses at most two exact-length RF span buffer sets,
  matching the only two block counts a fixed read window can cover. Buffers are
  returned after synchronous field decode; public `Read` results, deferred CVBS
  rendering, and retained LD VITS sources keep independent ownership.
- VHS sync-level DC adjustment reuses at most two exact-length low-pass
  workspaces. The stateful pipeline owns those private buffers; original video,
  public results, and deferred-render inputs remain untouched and independently
  owned.
- VHS drops block-local raw input, raw demodulation, analytic, and RF high-pass
  results after their last block-local consumer. Compact real-FFT blocks feed
  their split real/imaginary workspaces directly into the FM unwrap. This omits
  the unused RF high-pass inverse FFT, three RF-span copies, and one full-length
  `Complex[]`; LD, CVBS, and direct decoder construction retain full-channel
  behavior.
- Compact VHS stream blocks also retain their already-quantized SOS chroma in
  `float[]` form. RF span assembly widens it once into the reusable field buffer
  with AVX or an exact scalar fallback; full/direct blocks keep the public
  `double[] Chroma` contract.
- AVX/FMA kernels accelerate exact float32 conversion, VHS RF-envelope
  preparation, VHS Rust-style FM angle approximation, LD quantization, VHS
  chroma rotation, and complex frequency filtering. The forward/inverse radix-4
  FFT kernels use pinned pointer indexing. The 16-tap TBC sinc interior computes
  independent float weights and products with AVX/FMA, then accumulates them in
  original tap order; clamped edges, short inputs, and unsupported hardware keep
  the scalar path. Differential tests preserve exact transform bits and hashes.
- Recovery metadata is disk-streamed; its snapshot queue has capacity one, and
  field-order history and RF caches have hard limits. Long decodes therefore do
  not retain every decoded field or enqueue an unbounded amount of future work.
- CUDA/OpenCL is not a runtime dependency. Current traces do not justify moving
  isolated 32K FFTs across the host/device boundary; any future optional GPU
  backend must batch a device-resident DSP stage and retain an exact CPU fallback.

The current thread matrix used an Intel Core Ultra 7 265K (20 logical
processors), Windows 11 build 26220, .NET SDK/runtime
`11.0.100-preview.6.26359.118`, port checkpoint `a45d433`, and Python v0.4.0
commit `43155200da87c0d49eb37d8ec09b1372075ee8e4` (reported as `g4315520`).
The isolated Python environment used NumPy 2.4.6, SciPy 1.18.0, Numba 0.66.0,
and python-soxr 1.1.0. Each value is the median of three interleaved Release
runs:

| CLI mode | Effective workers | This port | Python | Speedup | Wall-time reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| default | 5 | 4.646 s | 13.112 s | 2.82x | 64.6% |
| `--threads 1` | 1 | 9.203 s | 14.111 s | 1.53x | 34.8% |
| `--threads 5` | 5 | 4.544 s | 12.799 s | 2.82x | 64.5% |
| `--threads 10` | 10 | 4.074 s | 13.560 s | 3.33x | 70.0% |
| `--threads 20` | 20 | 3.779 s | 14.046 s | 3.72x | 73.1% |

The default remains **5 workers**, matching Release 4.0 CLI semantics; explicit
20-worker mode was fastest on this 20-logical-processor fixture. The matrix used
`RF-Sample_2026-07-19_23-58-20.lds` with `--system pal
--detect_chroma_track_phase --ire0_adjust --tape_format VHS --frequency 40
--start_fileloc 620000000 -l 40 --overwrite`, plus the row's thread option.

All 15 port runs produced one identical luma TBC, chroma TBC, and JSON hash set
across every worker count. Three additional Python `--threads 0` controls were
mutually identical and exactly matched every port run. Upstream Python's
default/nonzero matrix modes were not a reliable byte-exact baseline: its 15
runs produced two luma/chroma pairs. Twelve runs matched the serial reference;
all three `--threads 5` runs produced the alternate pair. The matrix therefore
compares observed throughput, while Python `--threads 0` is the strict
compatibility baseline.

The compatibility baseline for this 40-frame fixture is Python v0.4.0
`g4315520` with `--threads 0`:

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `64C518A03B208F7CF950916BC01A997021CB0F76B3D6F131FBEE74E9035FD30C` |
| Chroma TBC | `70112719879FB64FA95DC8F3ED6E5FA335D4F8B62C50FC2AF3C26D2C2098F26F` |
| JSON | `C223671830D0105271F24172923B280A96C8D0D427567C49E9C0E562D38FA881` |

A longer exact-output checkpoint used an Intel Core Ultra 7 265K (20 logical
processors), Windows 11 build 26220, and .NET SDK/runtime
`11.0.100-preview.6.26359.118`:

| PAL VHS, 1,000 frames / 2,000 fields | Wall time | CPU time | Peak working set |
| --- | ---: | ---: | ---: |
| This port, Release (two runs) | 218.00 / 218.63 s | 238.72 / 239.50 s | 829.6-838.2 MiB |
| Python v0.4.0 (`g4315520`) | 417.37 s | not captured | not captured |

Both runs used `RF-Sample_2026-07-19_09-12-03.lds` and
`--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 281303040 --threads 0 -l 1000 --overwrite`.
Both port runs were about 1.91x as fast (47.7-47.8% lower wall time), and all
three paired SHA-256 values were byte-identical across Python and both port
runs. `--threads 0` selected deterministic serial mode in both implementations.

An independent no-seek startup checkpoint used
`RF-Sample_2026-07-19_23-58-20.lds` with the same PAL VHS options,
`--threads 0`, and `-l 1000`. Python and this port produced byte-identical luma
SHA-256 `E6616B63BD7DD1DB6C093FC6D1DCA7D23AABEF34EFD52089338D992F2DDCD0CD`
and chroma SHA-256
`A292BD77A8EB3373B6C631CE4552F77B6D4E5AF2228A85F01C63EDBBBFB4C0EF`.
All 2,000 field records, 135 startup recovery steps, and the 1,000-entry file
frame sequence (`22..1021`) also matched. The packaged Python baseline wrote
the eight-character identity `g43155200`, while this port uses `g4315520`;
those `gitCommit`/`version` identity strings were the only JSON differences.
This correctness run overlapped another decode process, so its timing is not a
benchmark result.

These numbers are fixture-specific, not universal benchmarks. The 40-frame
tuning A/B runs below used .NET SDK/runtime `11.0.100-preview.6.26359.118`,
`--threads 20`, default chroma, and default resampling. On a reproducible
40-frame PAL probe,
the saved pre-continuous-pipeline baseline median was 11.60 s and the latest
median was 4.228 s, a 63.6% cumulative gain. The newest exact-kernel checkpoint
alone moved matched wall/CPU/peak-working-set medians from
4.434 s/16.516 s/1.314 GiB to 4.228 s/15.328 s/1.069 GiB
(4.6%/7.2%/18.6%). Process CPU divided by wall time is about 3.63 active cores,
so further work still targets state-safe field-stage parallelism. All 14 runs
produced identical paired TBC, JSON, and chroma SHA-256 values.

Earlier 40/160/320-frame sustained runs completed in 7.65/26.58/52.51 s. Peak
working sets were 1.76/1.88/1.67 GiB, while second-half medians were
1.42/1.30/1.28 GiB. The full 320 frames were written, and memory showed no
growth with decode length. Earlier allocation work also reduced a PAL LD
four-field probe from 5.12 GiB to 1.96 GiB.

The bounded VHS field-stage overlap reduced a 160-frame run from 20.13 s to
18.55 s (7.8%). TBC, chroma, and JSON SHA-256 values matched exactly; the task
is awaited within the current field, so memory cannot grow with decode length.

The zero-copy little-endian TBC writer removed about 455 MB of full-field
temporary byte-array payload across the same 160-frame output. Its xUnit v3
allocation probe writes 400,000 samples with less than 1 KiB of thread-local
allocation after warm-up. A fresh 160-frame run retained the exact luma and
chroma SHA-256 values; wall time remained within run-to-run noise.

<details>
<summary>Kernel and allocation benchmark history</summary>

The pinned PAL-sized TBC sinc A/B reduced the median from 3.929 ms to 3.727 ms
per field, a 5.1% kernel gain, and the interior-window path added 1.6%. An
AVX/FMA follow-up retained scalar clamps and ordered double accumulation. Five
interleaved PAL-field A/B runs reduced serial/20-worker medians from
21.588/5.579 to 18.741/5.330 ms (13.2%/4.5%). Five 40-frame full-path pairs
reduced median wall/CPU time from 5.511/19.297 to 5.478/17.922 s (0.6%/7.1%).
Two reversed 204-frame pairs were 1.1-1.3% faster with bounded memory; TBC,
chroma, JSON, and the isolated field hash remained exact.

A session-owned VHS chroma-table cache retains one exact-key heterodyne set and
one burst-carrier set. Matched 40-frame GC traces reduced sampled allocation
from 13.854 to 12.579 GiB, `Double[]` allocation from 12,611.83 to 11,311.73
MiB, and Gen2 collections from 38 to 31. Five interleaved A/B pairs reduced
median wall/CPU time from 5.49/19.23 to 5.30/18.05 s (3.5%/6.1%). Two reversed
204-frame pairs were 4.4% and 4.8% faster; memory was non-monotonic with a
2.0 GiB maximum, and all 409 fields and output hashes remained exact. Removing
the two remaining read-only field copies further reduced matched sampled/
`Double[]` allocation from 12.580 GiB/11,309.71 MiB to 12.147 GiB/10,871.59
MiB. Five interleaved runs reduced median wall/CPU time from 5.209/18.188 to
5.175/17.094 s (0.7%/6.0%); two reversed 204-frame pairs were 1.8% and 1.9%
faster with non-monotonic memory at or below 2.05 GiB and exact 408-field
`--length 204` outputs.

Parallel RF span assembly uses completed immutable blocks and disjoint final
window ranges, with analog-audio phase work left ordered. Five interleaved
40-frame runs reduced median wall time from 5.165 to 4.878 s (5.6%) while CPU
time rose from 18.172 to 18.875 s (3.9%), converting more core use into
throughput. Two reversed `--length 204` pairs completed baseline/current in
21.31/20.35 s and 21.84/20.18 s (4.5% and 7.6% faster). Current memory was
non-monotonic with 1.93/2.06 GiB peaks, and all 408 fields and hashes remained
exact.

Parallel VHS payload output overlaps each field's independent luma and chroma
stream writes, while joining both before the next field. Five interleaved
40-frame runs reduced median wall time from 4.98 to 4.87 s (2.2%); median CPU
time rose from 18.20 to 19.50 s as both writes used otherwise idle capacity.
Two reversed `--length 204` pairs completed baseline/current in 20.451/20.181 s
and 20.483/20.353 s (1.3% and 0.6% faster). Current memory was non-monotonic
with 2.03/2.06 GiB peaks, and all 408 fields and hashes remained exact.

The compact VHS RF-channel path releases raw input, raw demodulation, and RF
high-pass block arrays before caching, skips their field assembly, and does not
run the unused RF high-pass inverse FFT. Five interleaved 40-frame A/B runs
reduced median wall/CPU time from 6.01/18.86 to 5.02/17.45 s (16.5%/7.5%). Two
reversed 204-frame pairs completed baseline/current in 20.48/20.28 s and
20.61/19.87 s; CPU time was 79.88/68.91 s and 77.17/72.44 s. Peak working set
moved from 2.05-2.08 GiB to 1.58-1.67 GiB, with non-monotonic quarter samples;
all 408 fields and luma, chroma, and JSON hashes remained exact.

The compact analytic follow-up feeds the pooled real and imaginary arrays
directly into VHS FM unwrap, SIMD-normalizes four frequency differences at a
time, and materializes `Analytic` only for the full direct API. Five interleaved
40-frame pairs were wall-time neutral at 5.02/5.03 s, while median CPU time fell
from 17.73 to 17.28 s and median peak working set from 1.47 to 1.26 GiB. Two
reversed 204-frame pairs remained within wall-time noise; current peaks were
1.32-1.41 GiB with non-monotonic quarter samples, and all three hashes remained
exact.

The compact chroma follow-up keeps float32 SOS output narrow until RF field
assembly. Matched 10-frame allocation traces reduced sampled managed allocation
from 2.95 to 2.89 GiB and `Double[]` allocation from 2.75 to 2.60 GiB, while
`Single[]` rose from 0.03 to 0.11 GiB. Five interleaved 40-frame pairs reduced
median wall/CPU time from 4.831/16.50 to 4.769/15.75 s (1.3%/4.5%). Two reversed
204-frame pairs were wall-time neutral at baseline/current 19.73/19.83 and
19.87/19.73 s; current peaks were 1.46/1.39 GiB and remained within the existing
bounded working-set envelope. All luma, chroma, and JSON hashes remained exact.

The bounded payload-writer follow-up overlaps the next VHS field decode with the
current field's luma/chroma write through a capacity-one queue. Payloads remain
ordered before their recovery JSON snapshot, completion drains the writer, and
worker failures return to the decode thread. Five interleaved 40-frame pairs
reduced median wall/CPU time from 4.90/16.09 to 4.79/15.47 s (2.2%/3.9%). Two
reversed 204-frame pairs completed baseline/current in 20.23/19.54 s and
20.05/19.19 s (3.4%/4.3% faster). Current quarter peaks were
1.35/0.74/0.96/1.14 and 1.27/0.95/0.97/1.09 GiB, with no monotonic growth; all
408 fields and luma, chroma, and JSON hashes remained exact.

The native-rate `.s16` input path now bypasses FFmpeg only when the declared
rate is exactly 40.0 MHz. A fresh trace contained no FFmpeg pass-through or
input-pump frame in its top 300 inclusive methods. Five interleaved 40-frame
pairs reduced median wall/CPU time from 5.33/17.11 to 4.97/15.94 s
(6.8%/6.8%), and median peak working set from 1.23 to 1.13 GiB. Two reversed
204-frame pairs completed baseline/current in 21.50/20.86 and 21.67/21.54 s;
candidate peaks were 1.39/1.35 GiB, and all output hashes remained exact.

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

The forward radix-4 kernel now uses the same pinned indexing as the inverse;
its isolated 32768-point median fell from 204.7 us to 195.9 us (4.3%) with exact
bits. The 384-block RF composite was neutral at 841.96/841.19 ms, so no
whole-block speedup is claimed for this change.

The subsequent float32 SOS pass preserves sample-major arithmetic order while
keeping one-, two-, and four-section cascade states in locals. Other cascade
sizes use flat bounded state: stack storage through 32 sections and a heap
fallback above that limit. Five-run isolated 32K medians for two/four sections
fell from 110.2/155.4 ms to 75.3/83.3 ms (31.7%/46.4%); five/eight/ten-section
medians fell by 38.8%/40.2%/42.7%. Across two 160-frame A/B pairs, median wall
time fell from 21.22 to 20.57 s (3.1%) and CPU time from 73.31 to 68.73 s
(6.3%). TBC, JSON, and chroma hashes remained exact. The current pair's median
private-memory peak was 1.71 GiB, and quarter-run memory was not monotonic.

A follow-up pass pooled the float32 SOS padded workspace. Matched 40-frame GC
traces reduced total sampled allocation from 16.772 to 16.178 GiB and
`Single[]` allocation from 651.68 to 47.25 MiB. Five interleaved full-path A/B
runs were wall-time neutral at 5.541/5.537 s, while median CPU time moved from
20.000 to 19.438 s; all three output hashes remained exact. The current
fixture-limited 204-frame run completed in 23.39 s with 1.147/0.886/0.888/0.917
GiB private-memory quarter medians and a 1.755 GiB peak.

The next pass pooled the default linear TBC resampler's two per-field double
workspaces. Matched 40-frame GC traces reduced total sampled allocation from
16.178 to 14.892 GiB and `Double[]` allocation from 13.601 to 12.316 GiB. Five
interleaved A/B runs reduced median wall time from 5.684 to 5.571 s (2.0%) and
CPU time from 19.031 to 18.891 s; all three hashes remained exact. A repeated
204-frame run had flat 1.025/1.047/1.007/1.042 GiB private-memory quarter
medians and a 1.869 GiB peak.

The VHS diff-demod repair pass now keeps its transient full-length `Complex[]`
in the existing capped FFT workspace. Matched 10-frame GC traces reduced total
sampled allocation from 4.134 to 3.861 GiB and `Complex[]` allocation from
622.63 to 340.02 MiB. Ten interleaved 40-frame pairs and two reversed 204-frame
pairs were wall-time neutral within run noise, so no speedup is claimed;
long-run memory remained bounded and all 409-field hashes stayed exact.

The current double-SOS and BA-IIR pass fuses the common two- and four-section
double cascades and reuses the BA filter's padded workspace through a private
bounded pool. Isolated two/four-section SOS medians improved by 37.5%/58.9%.
Across 32K-sample high-pass orders 4/9/20, the current IIR path was
23.7%/30.3%/26.6% faster than the old allocating reference and reduced warm
thread allocation from about 1.05 MB to 262 KB. Seven interleaved 40-frame
full-path pairs produced the 4.6% wall, 7.2% CPU, and 18.6% peak-working-set
improvements above. A fixture-limited 409-field run completed in 17.431 s;
25-50%, 50-75%, and 75-100% output intervals were 4.06/4.02/4.27 s, while
second-half median working/private memory rose by only 10.8/7.4 MiB. Every
recorded luma, chroma, and JSON hash remained exact.

The packed `.lds` loader now writes decoded samples directly into its requested
output and preserves Python's partial-tail-group behavior. Five interleaved
40-frame real-capture pairs moved default wall/CPU medians from
4.687/12.422 s to 4.610/12.188 s and 20-worker medians from 3.813/14.469 s to
3.743/13.109 s. Three 160-frame default pairs moved wall time from 15.281 to
14.993 s; a separate five-pair 20-worker repeat moved wall/CPU medians from
12.655/46.297 s to 12.601/46.156 s and peak working set from 1.319 GiB to
1.198 GiB. All 42 recorded real-capture runs produced one exact luma, chroma,
and JSON hash set per fixture.

The VHS sync-reference DC-offset pass now reuses at most two exact-length
low-pass workspaces. A matched 10-field GC trace reduced sampled managed
allocation from 2.639 to 2.466 GiB, `Double[]` allocation from 2,469.42 to
2,291.86 MiB, and Gen2 collections from 17 to 15. Five interleaved 40-field
pairs were wall-time neutral within run noise (default 4.473/4.522 s;
20-worker 3.736/3.778 s), while CPU medians moved from 12.719 to 11.969 s and
14.375 to 13.859 s. Three 160-field pairs moved default/20-worker wall medians
from 15.272/12.560 to 15.113/12.378 s. A 400-field 20-worker A/B moved
wall/CPU from 28.937/106.984 to 28.296/105.344 s; candidate private-memory
quarter medians were 1.076/0.766/1.025/0.726 GiB with a 1.463 GiB peak, showing
no monotonic growth. Every recorded luma, chroma, and JSON A/B hash remained
exact.

The VSync serration-window pass removes the full-window copy made before level
measurement. A matched 10-field GC trace reduced sampled managed allocation
from 2.465 to 2.434 GiB and `Double[]` allocation from 2,291.20 to 2,266.54
MiB, a 24.7 MiB reduction, without adding retained buffers. Five interleaved
40-field pairs were wall/CPU neutral within run noise (default
4.508/12.188 to 4.556/12.422 s; 20-worker 3.719/14.203 to
3.696/14.531 s). Three 160-field pairs were also neutral (default
14.847/40.484 to 14.904/40.406 s; 20-worker 12.319/45.172 to
12.361/45.391 s). A conservative candidate-first 400-field 20-worker A/B
moved wall/CPU from 28.015/107.828 to 27.865/108.547 s and peak working set
from 1.481 GiB to 1.465 GiB. The change is retained for lower long-run allocation
pressure rather than a claimed CPU-speed gain; every recorded luma, chroma,
and JSON hash remained exact.

The VHS chroma-prefilter ownership pass borrows the immutable field input when
no prefilter is configured, while configured filters and the public
`ApplyChromaPreFilter` API continue to return independently owned arrays. A
matched 10-field GC trace reduced sampled managed allocation from 2.440 to
2.384 GiB and `Double[]` allocation from 2,267.10 to 2,207.39 MiB, removing the
59.629 MiB `ApplyChromaPreFilter` allocation stack; both runs performed 15 Gen2
collections. Five interleaved 40-field pairs moved default wall/CPU medians
from 4.475/12.312 to 4.433/12.219 s and 20-worker medians from
3.694/14.531 to 3.638/14.531 s. Three 160-field pairs moved default medians
from 15.104/41.297 to 14.732/40.344 s; 20-worker wall time remained neutral at
12.179/12.206 s while CPU time moved from 49.312 to 46.094 s. Two reversed-order
400-field pairs completed candidate/baseline in 28.039/28.553 s and
baseline/candidate in 28.224/28.308 s; candidate peaks were
1.474/1.475 GiB. Every recorded luma, chroma, and JSON hash remained exact.

The VHS chroma comb/gain pass fuses those two internal stages with one
line-sized stack workspace while leaving the public stage APIs unchanged. A
matched 10-field GC trace reduced sampled managed allocation from 2.360 to
2.322 GiB and `Double[]` allocation from 2,197.06 to 2,147.33 MiB. The
59.629 MiB `ApplyComb` allocation stack disappeared, the final gain-owned
59.629 MiB output remained, and both runs performed 14 Gen2 collections. Five
interleaved 40-field pairs moved default wall/CPU medians from 4.455/12.250 to
4.366/12.125 s and 20-worker medians from 3.721/15.719 to 3.657/14.094 s. A
separate five-pair 160-field 20-worker run moved wall/CPU medians from
12.180/47.922 to 12.064/44.031 s. Two reversed-order 400-field pairs completed
candidate/baseline in 26.916/27.468 s and baseline/candidate in
27.398/27.664 s; candidate peaks were 1.484/1.481 GiB. Every recorded luma,
chroma, and JSON hash remained exact. An earlier line-history in-place
prototype was fully removed after its 160-field wall medians regressed from
15.20 to 15.53 s by default and from 12.45 to 12.68 s with 20 workers.

The subsequent VHS chroma gain-to-U16 pass removes the remaining gain-owned
double field from internal decode while leaving the public gain and conversion
APIs unchanged. A matched final 10-field GC trace reduced sampled managed
allocation from 2.320069 to 2.266559 GiB and `Double[]` allocation from
2,147.315 to 2,086.828 MiB. The 59.629 MiB
`ApplyAutomaticChromaGainWithComb` allocation stack disappeared, `UInt16[]`
allocation remained 29.815 MiB, and Gen2 collections moved from 15 to 14. Five
interleaved 40-field pairs moved default wall/CPU medians from
4.461/12.781 to 4.403/12.047 s and 20-worker medians from
3.706/14.406 to 3.665/12.906 s. A separate five-pair 160-field 20-worker run
moved wall/CPU medians from 12.196/46.047 to 11.985/45.625 s. Two
reversed-order 400-field pairs completed candidate/baseline wall/CPU in
27.566/27.877 s and 107.531/105.828 CPU-s, then baseline/candidate in
28.120/27.263 s and 105.422/107.594 CPU-s; candidate peaks were
1.355/1.474 GiB. The longer runs therefore used more total CPU while finishing
sooner, and every recorded luma, chroma, and JSON hash remained exact. An
initial full-field neutral-fill form was reworked after 160-field wall medians
regressed from 14.71 to 14.76 s by default and from 12.05 to 12.26 s with 20
workers. The scalar line-span form was also not retained as final after its
first 400-field pair completed candidate/baseline in 28.353/27.647 s; only the
AVX2/SSE4.1 form passed the final long-run gate.

</details>

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
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

The current formal Release build has zero warnings and errors. The xUnit v3
project exposes **813** independently discoverable tests to both
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
