# LD thread tuning on the measured Windows sample

This guide records thread configuration measurements using the merged V6 binary. It measures the existing thread option on that immutable snapshot. Nothing here changes the shipping LD default of 4 threads or the v0.4.0 compatibility contract.

On the measured host and sample, explicit `-t 20` for `exact` and `-t 16` for `ipp-fast` reduced elapsed time compared with `-t 4`. Independent 480-frame confirmation found median paired time reductions of 27.51% and 14.82%, respectively, with all five measured pairs positive. These are configuration gains in the same binary. They are not V7 code gains, cumulative V6 gains, or evidence of a universal optimal thread count.

## Measured binary, host and input

- Binary: immutable `artifacts/ld-speed-v7/baseline`, all 72 files copied byte-for-byte from the accepted final V6 bundle. Before and after every process, the harness checked the full relative file set and SHA256, including nested SQLite native payloads.
- Core SHA256: `2d4edb1fda74a4a17fa5e71a14e36bbeb5b2b668cad4cd73c948279d22809afc`.
- Built source: `c151419e29a2109696ee20b8b944b4ea3d5a4971`; V6 merge commit: `2aca4b570bee923e05fe2a8ea7dfb1d37644ef1c`. The source and merge identifiers differ; the measured byte payload is the selected final V6 payload.
- Host: Windows 11 x64 (`Windows-11-10.0.26220-SP0`), 20 logical processors reported. This count is metadata, not an instruction to map thread count automatically.
- Framework target: .NET 11 RC2, `11.0.0-rc.2.26504.105`, as recorded in the immutable CLI runtimeconfig; repository SDK `11.0.100-rc.2.26504.105`.
- Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, 75,165,807,979 bytes; NTSC, start 0, default video/analog-audio/EFM options. Input path/length/mtime were checked around every process; the large RF content was not rehashed.
- Runtime/JIT/ISA/native-thread environment settings and PATH hash were recorded without changes. Each job was a fresh CLI process; warmups do not imply JIT state persists between processes.

Binary evidence: `artifacts/ld-speed-v7/baseline-source.json` (baseline source and 72-file hashes), `artifacts/ld-speed-v7/thread-study/selected-binary.json` (selected immutable manifest), `artifacts/ld-speed-v7/baseline/decode.runtimeconfig.json` (runtimeconfig).

## Discovery: 240 frames, three predeclared rounds

Both backends used explicit 1/4/8/16/20 threads, one warmup for each of the 10 configurations, then three seeded rotated/randomized measured rounds. The order was frozen before measurements. Each round contained every configuration exactly once. Total 40 processes. First 4-thread warmup per backend established its raw output oracle; all other configurations and repeated 4-thread runs had to match it.

Wall includes startup and output writing; binary/output hashes are outside the wall interval. Main CPU is user+kernel CPU from `GetProcessTimes` across the main decode process threads. Peak is the OS-maintained `PeakWorkingSetSize` for that same owned process. CPU and peak exclude FFmpeg and every other child; they do not represent whole-machine totals or energy. The controlled 32 MiB / 0.1 CPU-second API smoke passed; every study peak query succeeded and was nonzero.

The table reports medians of three measured processes per configuration. Paired wall reduction is `100*(1 - candidate_seconds / same_round_4t_seconds)`, then median across the three rounds. Negative values mean longer elapsed time; 4 threads is the reference, not a losing configuration.

| Backend | Threads | Median wall s | Median paired wall reduction vs 4 threads | Faster pairs | Median main CPU s | Median main peak MiB |
|---|---:|---:|---:|---:|---:|---:|
| exact | 1 | 48.291 | -102.62% | 0/3 | 47.688 | 841.83 |
| exact | 4 | 23.973 | 0.00% | reference | 54.734 | 1031.52 |
| exact | 8 | 20.696 | 14.16% | 3/3 | 59.250 | 1066.25 |
| exact | 16 | 18.142 | 24.75% | 3/3 | 80.578 | 1209.03 |
| exact | 20 | 17.750 | 26.38% | 3/3 | 83.328 | 1269.55 |
| ipp-fast | 1 | 32.065 | -65.80% | 0/3 | 31.375 | 846.37 |
| ipp-fast | 4 | 19.588 | 0.00% | reference | 38.750 | 1014.87 |
| ipp-fast | 8 | 18.148 | 8.09% | 3/3 | 43.391 | 1040.74 |
| ipp-fast | 16 | 16.307 | 15.30% | 3/3 | 51.922 | 1157.90 |
| ipp-fast | 20 | 17.036 | 12.86% | 3/3 | 58.562 | 1164.38 |

All three measured wall times are preserved here to show the spread, in the fixed round order. Per-process CPU/peak values, commands and complete interleaving remain in the raw runs.

| Backend | Threads | Round 1 wall s | Round 2 wall s | Round 3 wall s |
|---|---:|---:|---:|---:|
| exact | 1 | 47.915 | 48.573 | 48.291 |
| exact | 4 | 24.110 | 23.973 | 23.762 |
| exact | 8 | 20.696 | 20.470 | 21.047 |
| exact | 16 | 18.142 | 18.440 | 17.813 |
| exact | 20 | 17.750 | 17.615 | 17.801 |
| ipp-fast | 1 | 32.065 | 30.686 | 32.476 |
| ipp-fast | 4 | 19.252 | 19.852 | 19.588 |
| ipp-fast | 8 | 18.274 | 18.148 | 18.004 |
| ipp-fast | 16 | 16.307 | 16.089 | 16.845 |
| ipp-fast | 20 | 17.036 | 16.632 | 17.068 |

The predeclared selection rule required all three paired wall reductions positive, then selected the strongest paired median per backend. It chose exact 20 threads and IPP 16 threads; both were fixed before the independent confirmation. Discovery did not launch confirmation automatically.

Discovery evidence: `artifacts/ld-speed-v7/thread-study/discovery/runs.json` (raw 40-job rows and environment), `artifacts/ld-speed-v7/thread-study/discovery/summary.json` (summary), `artifacts/ld-speed-v7/thread-study/discovery/review-validation.json` (recorded guard review).

## Independent confirmation: 480 frames, five alternating pairs

Each backend used one warmup per 4-thread/candidate configuration, then five fixed alternating measured pairs: 12 processes per backend, 24 total. The harness required the completed discovery, recomputed its already-declared winner, and checked the same binary/input/source/order identity. A new matching 480-frame 4-thread oracle was established for each backend; no winner was replaced after results were observed.

| Backend | Threads | Median wall s | Median paired wall reduction vs 4 threads | Faster pairs | Median main CPU s | Median main peak MiB |
|---|---:|---:|---:|---:|---:|---:|
| exact | 4 | 45.376 | 0.00% | reference | 103.938 | 1007.90 |
| exact | 20 | 32.740 | 27.51% | 5/5 | 147.016 | 1286.24 |
| ipp-fast | 4 | 36.600 | 0.00% | reference | 72.250 | 1004.78 |
| ipp-fast | 16 | 31.297 | 14.82% | 5/5 | 102.406 | 1156.83 |

Exact 20 threads paired wall reductions: 26.740%, 28.572%, 27.236%, 27.510%, 28.095%. IPP 16 threads: 14.559%, 18.912%, 14.822%, 13.727%, 15.267%. Each candidate was faster in 5/5 pairs.

This reduces elapsed time at a resource cost. Median paired main-process CPU time increased 43.76% for exact 20 threads and 41.89% for IPP 16 threads. Differences between the measured median main-process peak working sets were +278.34 MiB and +152.05 MiB, respectively. CPU percentages are medians of within-pair ratios; peak differences are differences of configuration medians. None of these metrics include FFmpeg children.

Confirmation evidence: `artifacts/ld-speed-v7/thread-study/exact-t20-confirm/runs.json` (exact raw 12-job rows), `artifacts/ld-speed-v7/thread-study/ipp-t16-confirm/runs.json` (IPP raw 12-job rows), `artifacts/ld-speed-v7/thread-study/confirmation-review.md` (combined readable review), `artifacts/ld-speed-v7/thread-study/confirmation-review.json` (combined machine checks).

## Output identity and scope

All 64 processes succeeded: 40 discovery jobs at 240 frames / 480 fields and 24 confirmation jobs at 480 frames / 960 fields. Every job checked raw SHA256 for `.tbc`, `.pcm`, `.efm`, `.tbc.json`, and `.tbc.db` against the matching backend 4-thread oracle at the same frame length: 320 raw file checks. Each frame protocol also had identical exact and IPP oracle hashes. No JSON/DB/decoded output normalization was performed. The 240-frame and 480-frame outputs are separate oracle sets; their lengths differ.

All binary/source/order/input guards passed. Raw logs, commands, order, timing and metrics remain intact. The initial plan-only scaffold error (missing JSON import) occurred before any decode, was retained, and was fixed with a new no-launch plan regression test; there were no decoding failures. Six pure protocol tests and the WinAPI smoke passed before execution.

This study covers one sample, one NTSC start/length/options set and one Windows host. It does not establish PAL/other captures/full-side optimality, ARM/Linux behavior, a memory/energy optimum, or performance of other code revisions. Do not add these percentages to V6 code-speedup percentages: the denominator and mechanism differ.

## Normal use: the existing thread option

These examples use the shipping `decode.exe` in the current directory. They illustrate an existing option; the measurements above apply specifically to the immutable V6 snapshot, not to unmeasured code revisions or arbitrary future binaries. Use an unused output prefix. The 480-frame length matches confirmation; change it only when intentionally decoding a different scope.

```powershell
.\decode.exe ld --NTSC --start 0 -l 480 -t 20 --dsp-backend exact "D:\BmdFiles\DDD\10e_lxm_side1.ldf" "D:\BmdFiles\DDD\10e_lxm_side1_exact_t20_new"
.\decode.exe ld --NTSC --start 0 -l 480 -t 16 --dsp-backend ipp-fast "D:\BmdFiles\DDD\10e_lxm_side1.ldf" "D:\BmdFiles\DDD\10e_lxm_side1_ipp_t16_new"
```

Without an explicit `-t`, LD keeps its native default 4. This guide does not change that compatibility contract or interpret `-t 0` as automatic scaling. `-t` controls decode worker budgets; it is not a cap on every OS thread created by the runtime or external FFmpeg. The LD audio/EFM/phase2 path adds no nested task/parallel fan-out; the relevant RF block loops use the requested worker budget.

## Reproducing the measured snapshot

The study used the complete immutable directory `artifacts/ld-speed-v7/baseline`, selected in `artifacts/ld-speed-v7/thread-study/selected-binary.json`. The directory name marks the V7 experiment workspace; its binary bytes remain the accepted V6 source/merge/Core identified above. Keep all 72 relative payload files intact, including SQLite runtime subdirectories. The following examples assume the repository root and fresh output prefixes; they reproduce an individual confirmed configuration, not the entire fixed measurement protocol.

```powershell
& ".\artifacts\ld-speed-v7\baseline\decode.exe" ld --NTSC --start 0 -l 480 -t 20 --dsp-backend exact "D:\BmdFiles\DDD\10e_lxm_side1.ldf" ".\artifacts\ld-speed-v7\thread-reproduce-exact-new"
& ".\artifacts\ld-speed-v7\baseline\decode.exe" ld --NTSC --start 0 -l 480 -t 16 --dsp-backend ipp-fast "D:\BmdFiles\DDD\10e_lxm_side1.ldf" ".\artifacts\ld-speed-v7\thread-reproduce-ipp-new"
```

The complete protocol, selection, environment, order, raw hashes and metrics are retained under the repository-relative audit paths above. Those ignored artifacts are local evidence, not files available from a public repository link. Frozen preparation sources are listed below; their execution results belong to the separate study reports.

Owning source: [LD option default 4 and -t/--threads](../src/VHSDecode.Core/CommandLine/CliSpecs.cs#L228), [requested-thread execution mapping](../src/VHSDecode.Core/Decode/DecodeSession.cs#L897), [RF block parallelism bound](../src/VHSDecode.Core/Decode/RfBlockStreamDecoder.cs#L895), [LD EFM and analog channel stages](../src/VHSDecode.Core/Decode/RfBlockDecodePipeline.cs#L809), [analog audio phase2](../src/VHSDecode.Core/Decode/LaserDiscAnalogAudioPhase2.cs).

Protocol source: `scratch/ld-thread-sweep/ld-thread-sweep.py` (immutable sweep harness), `scratch/ld-thread-sweep/sweep-order.json` (frozen preseeded order), `scratch/ld-thread-sweep/windows_metrics.py` (main-process WinAPI metrics), `scratch/ld-thread-sweep/source-manifest.json` (frozen source manifest). This guide is separate from the frozen harness manifest; the manifest and executable scripts were not modified.
