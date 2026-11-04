# LaserDisc bounded audio channel scheduling - 2026-11-04

V7 retains a narrow internal analog-audio phase2 scheduling change. On the
measured Windows host and capture range, median paired whole-process wall
time fell 2.43% for exact at 4 threads and 2.95% at 20 threads. IPP at 4 threads
was near zero (+0.074%, 4/7 positive), while IPP at 16 threads fell 3.35% with
3/5 positive.
These results do not establish a stable general IPP gain. Main-process CPU
time increased at the paired median in all four configurations.

The comparison is incremental against the accepted [V6 binary](LD_PERFORMANCE_V6.md).
The [thread tuning study](LD_THREAD_TUNING.md) measured configuration changes
in that same V6 binary. Those percentages are separate and must not be added
to the code-change results here. The LD default remains 4 threads.

## Immutable baseline and candidate

- Baseline: built source `c151419e29a2109696ee20b8b944b4ea3d5a4971`, V6 merge
  commit `2aca4b570bee923e05fe2a8ea7dfb1d37644ef1c`. Its accepted 72-file payload
  was copied unchanged to `artifacts/ld-speed-v7/baseline`.
- Baseline Core SHA256:
  `2d4edb1fda74a4a17fa5e71a14e36bbeb5b2b668cad4cd73c948279d22809afc`.
- Candidate: reviewed E5DF working-tree patch on docs-only main
  `9e6a88f4507256847031ea3b162168863d221718`. That main commit is not claimed
  to contain the new audio code. Official CLI and test builds produced the
  identical candidate Core SHA256:
  `90f8ee39f4598b9f528747aa682437ab947aa9307a9b7364643cf892743760f0`.
- Candidate 72-file selection:
  `artifacts/ld-audio2-production-r1/candidate-selected.json`, manifest SHA256
  `d2b8cc0a76779baff83f2c80236b8729740b8bfbbadbafcede256d3034c6ce94`.
- Host: Windows 11 x64 (`Windows-11-10.0.26220-SP0`), 20 logical processors,
  .NET 11 RC2 (`11.0.0-rc.2.26504.105`), Intel IPP 2026.0.0. Release settings,
  runtime/native payloads and normal environment were held fixed; no ISA,
  tiering, PGO, GC or native-thread override was introduced.
- Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, 75,165,807,979 bytes, NTSC,
  `--start 0`, 480 frames, default video/analog-audio/EFM/AGC/dropout/automatic
  MTF options. Input path, length and mtime were guarded; the complete large
  RF file was not rehashed.

## Whole-process measurements

The protocol was frozen before timing: one warmup pair followed by seven
alternating measured pairs per backend at 4 threads, then a separately fixed
one-warmup/five-pair protocol at exact 20 / IPP 16. Standard and scaled protocols
ran serially; all 56 fresh processes and all signed observations are retained.
The higher thread counts came from the earlier V6 configuration study, not
from selecting a winner after these candidate results.

Wall time is primary and includes process startup, initial sync recovery and
output writing. Hashing occurs after process exit. Positive percentages mean
reductions: `100*(before-after)/before`, calculated for each pair, then
summarized by their median. This is not the ratio of separately calculated
before/after medians. In particular, IPP at 4 threads has a slightly positive
paired median despite a higher candidate median wall time.

| Protocol | Backend / threads | Baseline median s | Candidate median s | Median paired wall reduction | Faster pairs | Signed wall range |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| standard | exact / 4 | 42.516 | 41.691 | +2.432923% | 6 / 7 | -1.605416% to +4.370971% |
| standard | ipp-fast / 4 | 33.163 | 33.617 | +0.073953% | 4 / 7 | -3.170879% to +1.081294% |
| scaled | exact / 20 | 29.758 | 28.644 | +2.953669% | 4 / 5 | -1.102088% to +6.084512% |
| scaled | ipp-fast / 16 | 27.526 | 26.720 | +3.348803% | 3 / 5 | -0.800024% to +4.681706% |

The exact result is favorable in both fixed thread configurations, with one
negative pair in each. IPP at 4 threads is indistinct and IPP at 16 threads has
two negative pairs; the retained change is not described as a stable general
IPP improvement.
These are bounded 480-frame results, not a whole-side, cross-platform or
universal-capture claim.

CPU is main-process user+kernel time from `GetProcessTimes`, including its
worker threads. Peak working set is that same owned process's OS-maintained
`PeakWorkingSetSize`. Both exclude FFmpeg children and are neither total
system CPU nor an energy measurement. Available peak samples were nonzero
WinAPI observations; they do not measure managed allocation.

| Protocol | Backend / threads | Baseline median CPU s | Candidate median CPU s | Median paired CPU reduction | Lower-CPU pairs | Signed CPU range |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| standard | exact / 4 | 102.562 | 102.703 | -0.761731% | 2 / 7 | -3.600429% to +2.915799% |
| standard | ipp-fast / 4 | 70.594 | 71.656 | -2.102700% | 1 / 7 | -4.866180% to +2.267819% |
| scaled | exact / 20 | 144.594 | 148.219 | -0.531280% | 2 / 5 | -6.066176% to +3.499174% |
| scaled | ipp-fast / 16 | 102.078 | 103.562 | -1.454156% | 2 / 5 | -5.317991% to +8.363095% |

| Protocol | Backend / threads | Baseline median peak MiB | Candidate median peak MiB | Median paired peak reduction | Lower-peak pairs |
| --- | --- | ---: | ---: | ---: | ---: |
| standard | exact / 4 | 1030.152 | 1029.594 | +0.242226% | 4 / 7 |
| standard | ipp-fast / 4 | 1004.891 | 1014.086 | -0.352715% | 2 / 7 |
| scaled | exact / 20 | 1289.906 | 1301.648 | -0.061620% | 2 / 5 |
| scaled | ipp-fast / 16 | 1148.766 | 1178.508 | -2.782330% | 1 / 5 |

All four median paired CPU reductions are negative: the candidate used more
main-process CPU at the paired median in this experiment. There is no CPU or
energy-saving claim. Peak values and every outlier remain included; the
large positive peak pair for IPP at 16 threads is not discarded or interpreted
as a reliable memory improvement.

### All 24 signed measured pairs

Each row gives before/after wall seconds and signed wall/CPU/peak reductions.
Negative values mean increased time or working set. Pair 1 starts after then
before; subsequent pairs alternate. Warmups remain in raw evidence and all
correctness gates but are excluded from these statistics.

| Protocol | Backend / threads | Pair | Wall before / after s | Wall reduction | Main CPU reduction | Main peak reduction |
| --- | --- | ---: | --- | ---: | ---: | ---: |
| standard | exact / 4 | 1 | 42.679765 / 40.894104 | +4.183859% | +2.613293% | -2.148595% |
| standard | exact / 4 | 2 | 43.596353 / 41.690769 | +4.370971% | +2.915799% | +0.242226% |
| standard | exact / 4 | 3 | 42.048728 / 42.723785 | -1.605416% | -3.600429% | -0.101163% |
| standard | exact / 4 | 4 | 42.150259 / 41.124776 | +2.432923% | -0.030731% | -2.964610% |
| standard | exact / 4 | 5 | 42.516059 / 42.111694 | +0.951089% | -1.293563% | +1.215641% |
| standard | exact / 4 | 6 | 41.935476 / 41.905068 | +0.072513% | -1.701996% | +3.434819% |
| standard | exact / 4 | 7 | 42.747942 / 41.573486 | +2.747399% | -0.761731% | +2.740417% |
| standard | ipp-fast / 4 | 1 | 33.163431 / 33.736181 | -1.727054% | -4.866180% | -0.352715% |
| standard | ipp-fast / 4 | 2 | 33.169154 / 33.144625 | +0.073953% | -1.482629% | -0.070092% |
| standard | ipp-fast / 4 | 3 | 33.900989 / 33.616878 | +0.838061% | -2.102700% | +0.108149% |
| standard | ipp-fast / 4 | 4 | 32.830421 / 32.475427 | +1.081294% | -1.455538% | -0.620050% |
| standard | ipp-fast / 4 | 5 | 33.009916 / 32.855699 | +0.467187% | -2.485314% | +0.028131% |
| standard | ipp-fast / 4 | 6 | 33.216707 / 34.269969 | -3.170879% | -2.246441% | -1.033617% |
| standard | ipp-fast / 4 | 7 | 33.160588 / 33.791988 | -1.904066% | +2.267819% | -1.036023% |
| scaled | exact / 20 | 1 | 29.813071 / 27.999091 | +6.084512% | +3.450382% | -0.910313% |
| scaled | exact / 20 | 2 | 29.757669 / 28.307804 | +4.872240% | -0.531280% | -0.061620% |
| scaled | exact / 20 | 3 | 28.847485 / 29.165410 | -1.102088% | -6.066176% | +2.323546% |
| scaled | exact / 20 | 4 | 29.841034 / 28.959628 | +2.953669% | +3.499174% | +0.212199% |
| scaled | exact / 20 | 5 | 29.117541 / 28.643913 | +1.626606% | -2.582667% | -3.045416% |
| scaled | ipp-fast / 16 | 1 | 27.525627 / 26.236958 | +4.681706% | +8.363095% | -2.782330% |
| scaled | ipp-fast / 16 | 2 | 26.936828 / 27.132884 | -0.727837% | -1.454156% | -2.286428% |
| scaled | ipp-fast / 16 | 3 | 27.646121 / 26.720307 | +3.348803% | -5.317991% | -4.960438% |
| scaled | ipp-fast / 16 | 4 | 27.243410 / 27.461364 | -0.800024% | -3.188623% | -3.082113% |
| scaled | ipp-fast / 16 | 5 | 27.899560 / 26.662642 | +4.433466% | +7.125193% | +15.297458% |

## Retained mechanism and exact-output boundary

Only three production files change:
[audio phase2](../src/VHSDecode.Core/Decode/LaserDiscAnalogAudioPhase2.cs),
[RF pipeline integration](../src/VHSDecode.Core/Decode/RfBlockDecodePipeline.cs)
and [stream decoder budget handoff](../src/VHSDecode.Core/Decode/RfBlockStreamDecoder.cs).
The new internal `ApplyForDecode` handles one padded block only when the
existing worker budget is at least 2, AVX2 is supported, both raw channels
have the same positive length below 32768, and both stage2 filters have exactly
32768 elements. The actual diagnostic shape receipt contained 929 blocks with
raw 30721 and 36 with raw 29730, each padded to 32768; it supplied shapes, not a
captured-value replay or proof of every block's eligibility.

The original preparation order runs once: prepare left, find left peaks,
suppress left, prepare right, suppress right using those peaks. A complete
integer raw-bit scan then requires finite prepared float values, both filter
components and both centers with absolute magnitude at most 2^32. Zero and
subnormal values remain eligible. This conservative bound keeps the fixed
32K double FFT/multiply/inverse graph far from overflow; it does not quantize
or otherwise change its arithmetic. No eligibility result is cached for a
mutable filter array.

Qualified calls schedule the right channel as one Task while the calling
thread filters the left channel. The task is always joined; captured left
errors take precedence over right errors without an AggregateException
wrapper. Inputs/filters remain unchanged and returned channel arrays are fresh,
independent owners. DecimationFactor and double-storage flags are preserved.
Task creation, scheduling, join and the complete eligibility scan are part
of the candidate's measured work.

The public `Apply` and every existing preparation/math method body remain
byte-for-byte unchanged. Budget/CPU/shape rejection immediately calls that
original public serial implementation. Prepared-value rejection filters
left then right serially without preparing twice. Full blocks, multi-block
inputs, empty/malformed shapes and unsupported hardware retain their old
paths. The original pipeline one-argument entry is unchanged; the appended
budget overload also returns the same field reference when no audio filters
exist. The stream decoder passes its existing worker budget; constructor,
CLI defaults and thread-option mapping do not change.

Finite accepted-domain comparisons remain strict raw-bit comparisons. Special
values keep the original serial FFT contract, including its existing
JIT-dependent NaN payload/sign behavior. No NaN-to-defined-value change or
new universal NaN-payload equality promise is accepted. The earlier
unbounded scalar probe's old-serial-self NaN-sign failure remains preserved;
it was not normalized away or used as permission to relax the finite gate.

## Component evidence and resource limits

The isolated bounded scheduling probe called the actual immutable V6 private
`FilterChannel` method twice; it did not rebuild Core or benchmark whole
`Apply`/Audio2/decoding. With the guard and Task costs included, four padded
profiles (1024, 16384, 28672, 30721 under N32768) showed 38.73-41.03% median
elapsed-time reduction, 7/7 positive each. The full 32768 real32 fallback control was near zero
(+0.328%, 4/7), with its negative pairs preserved. Synthetic values/filters
were prebuilt; the 30721 shape came from the actual correctness-gated trace.

That helper measured process-wide Task/runtime allocation, including about
237.75 additional bytes per channel pair at 30721. It did not establish an
allocation saving, and the whole-decoder protocol records no allocation
counters. Helper CPU observations are coarse/quantized and include negative
controls. Neither helper wall gains nor resource observations substitute for
the complete-decoder measurements above.

Earlier narrow sinc and PCM16 production candidates were withdrawn after
their complete-decoder protocols lacked repeatable scaled wall benefit.
They are not bundled into this candidate. Original `TbcLineResampler.cs`
SHA256 is `d86501f42182cf033ab82e85ce15018e276c8c0e456880c4a3d204d8c47f40e0`;
original `FfmpegPcm16SampleLoader.cs` is
`a67b54202ce5d62a8f60457677c58d358a015eaf52f2e39366b238721f0d3d78`.
Their experimental tests are archived, absent from the production test tree,
and all historical signed/failed reports remain intact. Terminal256 and the
current Hypot dispatch prototypes are not included either.

## Correctness and immutable guards

- Official Release CLI and test builds: 0 warnings / 0 errors, identical Core 90f8.
- Full suite: 2540 passed, 15 skipped, 0 failed (2555 total). Focused audio suite:
  68/68 passed. Extended `DOTNET_EnableAVX=0` suite: 287/287 passed, including all
  50 new scheduling cases; existing CI's 182-case minimum was extended by 50 to 232.
- New tests compare actual old public `Apply` to the internal entry at
  budgets 0/1/2/4/20, actual 29730/30721 shapes, short/full/long blocks, value
  bounds, signed zero/subnormals, malformed inputs, readonly filters/input,
  output flags/ownership/retention and pipeline no-filter reference identity.
- All 10 original-baseline CLI cases passed: middle range, single-thread distant
  start, verbose VITS, combined noAGC/RF-TBC/cubic/wow options and synthetic
  PAL, on both exact/IPP. Complete hash dictionaries and timestamp-only
  normalized diagnostic logs matched the original `ff03b9b` baseline. Optional
  RF-TBC compared decoded PCM because Ogg container serials vary per encode.
- All 56 macro processes exited 0 and produced 960 fields. Every `.tbc`, `.pcm`,
  `.efm`, `.tbc.json` and `.tbc.db` raw SHA256 matched: 280 file checks, including
  warmups, before/after, both backends and both thread protocols. They also
  matched the previously retained V6 480-frame oracle. No JSON/DB/output
  normalization was used.
- Before/after every macro child and at both protocol ends, all 72 relative
  runtime files were checked, including nested SQLite payloads: 228 tree
  observations. Harness hashes, normal runtime environment, PATH, input stat
  and external FFmpeg stat were guarded; FFmpeg content SHA matched at start
  and finish. Its SHA256 was
  `589e50b766d251afdf181dd664d40bd94407e200b019989fd7468c7d118a28d0`.
- The independent metadata/TRX/source audit passed, recalculating all 24 signed
  pairs and all test counters. It checked captured hashes/guards and small
  source/report files without repeating the expensive large-output/full-tree/
  FFmpeg/RF content hashes. AVX0 assignment is retained runner provenance;
  it is not inferred from a TRX label alone.

The following selected payload hashes are unchanged between baseline and
candidate. All other runtime/native files are listed in the complete manifests.

| Payload | SHA256 |
| --- | --- |
| `vhsdecode_ipp.dll` | `69f93937c685292e76c7aea2ae723cb739c60242b57765e3fda78e6ceead7fcd` |
| `sndfile.dll` | `4e3bd2de8e1485110eaebef8e1239471f73d608773831c323bf528e05645655e` |
| `soxr.dll` | `1708dc9beb53b050efcf77c5f758dc23658b055ba79abb01c2d8db872eca6bbe` |
| `runtimes/win-x64/native/e_sqlite3.dll` | `b082e8a72ad4ce908234eac970b09b7d328d16157cec34a3ebccfd9dfe2221c6` |
| `decode.runtimeconfig.json` | `37aa990da2ad627206bf5e28029ffb4d16b7dacf0725a785eb8992acc7ca148c` |
| `decode.deps.json` | `c72224698a3f4f74992f0344845459f45ba5c7cee6109da15deea8246623b59d` |

Reviewed production/test/CI source SHA256 values:

| Repository path | SHA256 |
| --- | --- |
| `src/VHSDecode.Core/Decode/LaserDiscAnalogAudioPhase2.cs` | `9dea29966dd55d60188a89077fa4890adf5fc09e0c603c414c71f08a07567864` |
| `src/VHSDecode.Core/Decode/RfBlockDecodePipeline.cs` | `cfaf7cc977aef27723859143cb3ddfc6fa2ee8f85112a4cd9464680ed17769f0` |
| `src/VHSDecode.Core/Decode/RfBlockStreamDecoder.cs` | `770bac3bc23c4e02db53d289c73232617bd2e9f30a9abdadd9a5e4310953c3c7` |
| `tests/VHSDecode.Tests/LaserDiscAudioPhase2SchedulingTests.cs` | `34a41eaa57b1faaafb705d7605c1593e81d84b753ca3210e75af63a1d0c4dd8d` |
| `.github/workflows/release-build.yml` | `6cfb29227b3ef78096d1c6fed07fb4bc65356eddc5d8048616bef6d8e42a2925` |

## Local evidence and reproduction

These repository-relative paths are ignored local evidence, not public
download links:

- `artifacts/ld-audio2-production-r1/{application-audit,build-runs,test-runs}.json`
  and `results-{focused,full,noavx}/`: official builds and TRX records.
- `artifacts/ld-audio2-production-r1/validation-original-ten/{runs,cases}.json`:
  original 10 hash/log/field comparisons and command rows.
- `artifacts/ld-audio2-production-r1/ab-{standard,scaled}/{runs,summary}.json`:
  all 56 raw process rows, complete order/argv/environment, metrics, hashes,
  guards, stdout/stderr and outputs.
- `artifacts/ld-audio2-production-r1/independent-audit-r1/{audit.json,SIGNED_PAIRS.md}`:
  independent recorded-evidence audit, all 24 signed pairs and scopes.
- `artifacts/ld-audio2-production-r1/validation-macro-protocol.json`:
  fixed commands/protocol before timing. `scratch/ld-speed-v7-macro.py` SHA256:
  `fd0a6695b2a136f05fb3c5951803d089e97f451c9dadeabc2a8b72a8e3ecaa0f`.
- `scratch/ld-audio2-scheduling-production-candidate/`: reviewed patch/tests,
  exact three-file undo/source audit and E5DF source manifest.
- `artifacts/ld-audio2-bounded-scheduling/r1/`: all 70 helper measurements,
  separate normal/AVX0 contracts, retained special diagnostics, signed
  allocation/CPU controls and fresh cold-process context (not a latency gain).
- `artifacts/ld-speed-v7/standalone-sinc-{decision,withdrawal}.json`,
  `artifacts/ld-speed-v7/standalone-pcm16-withdrawal.json` and their original
  report directories: preserved withdrawn production candidates.

From the repository root, keep both full selected binary directories intact
and use unused output directories. The following retain the actual measured
arguments and pair protocols; running them intentionally starts a fresh
experiment, so coordinate an exclusive CPU window first.

```powershell
python scratch/ld-audio2-production-validation/validate-original-ten.py --execute --output artifacts/ld-audio2-production-r1/validation-original-ten-new
python scratch/ld-speed-v7-macro.py --before-manifest artifacts/ld-speed-v7/thread-study/selected-binary.json --after-manifest artifacts/ld-audio2-production-r1/candidate-selected.json --profile standard --output artifacts/ld-audio2-production-r1/ab-standard-new --execute
python scratch/ld-speed-v7-macro.py --before-manifest artifacts/ld-speed-v7/thread-study/selected-binary.json --after-manifest artifacts/ld-audio2-production-r1/candidate-selected.json --profile scaled --output artifacts/ld-audio2-production-r1/ab-scaled-new --execute
```

The macro's per-process argv is:

```text
<selected-before-or-after>/decode.exe ld --NTSC --start 0 -l 480 -t <4,20,16> --dsp-backend <exact,ipp-fast> D:\BmdFiles\DDD\10e_lxm_side1.ldf <new-output-prefix>
```
