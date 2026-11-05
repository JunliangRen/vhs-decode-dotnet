# LaserDisc EFM storage reuse - 2026-11-05

V16 retains one internal LD storage change: reuse a dead demodulated spectrum as the EFM filter destination. On the measured Windows x64 host and 480-frame capture range, median paired whole-process wall time fell **+2.635677% for exact / 4 threads** (6/7 faster pairs) and **+2.673677% for IPP / 4 threads** (4/7 faster pairs). Both execution-order strata have positive wall medians in each default configuration. Main-process CPU medians also fell.

**IPP4 wall consistency is limited:** three of seven pairs were slower, and candidate-first has only two faster pairs out of four. Its positive paired median, positive order medians and supporting CPU observations justify a bounded retention decision, not a strong or uniform IPP4 win. Exact20 and IPP16 were predeclared nonregression/scalability checks, not required speedup targets. The default remains four threads. Per-run, other-capture, other-platform and VHS throughput gains are not established.

## Scope and ownership

- The eligible domain is the existing closed owned immediate LD route, with EFM enabled, a matching private Envelope shape, N = 32,768 and actual Exact or IppFast backend. The existing clipping, ConjugateProduct, no-analytic-export, no-output-buffer and null/±0 high-boost requirements remain. Public/full/preview/custom and incompatible calls retain the allocating path.
- S0 is the original shared full input spectrum. It remains read-only for EFM and subsequent analog stage1. D is the second full complex forward result; video/LPF/Burst/Pilot finish reading it before the producer publishes it. Extracted public channels are separate arrays.
- A fresh call-local `LaserDiscEfmScratchHandoff` holds only owner and array references. Publication and take require matching owner/length; take clears the reference before diagnostics/chroma. It never enters a public record, cache, pool or TLS state.
- EFM performs the same ordinary `ComplexMultiply.Apply(S0, efmFilter, D)`, the same `InverseLaserDiscFullSpectrumInPlace(D, useDuccExact: false)` and the unchanged int16 conversion, in that order. Every N element is written. No FFT, multiply, quantizer, native kernel, loader read, native workspace policy or thread default is replaced.
- Destination overlap with S0 or the EFM filter is rejected before any store, including same-start aliases. Missing publication, wrong owner/shape or a malformed filter falls back to the original EFM allocation and original error position. No read retry or catch-and-repeat computation is added.

The structural saving is one `Complex[32768]` allocation and its initial clearing: **524,288 bytes (512 KiB) of array payload per eligible RF block**. Array headers are not included, and a small per-call holder is added. The holder's size was not measured here. D stays live through EFM, so this payload count is not a whole-process allocated-byte or peak-memory measurement and cannot be converted into a throughput estimate.

Owning sources: [RfDemodulator.cs](../src/VHSDecode.Core/Dsp/RfDemodulator.cs), [RfBlockDecodePipeline.cs](../src/VHSDecode.Core/Decode/RfBlockDecodePipeline.cs) and [LaserDiscEfmScratchHandoff.cs](../src/VHSDecode.Core/Dsp/LaserDiscEfmScratchHandoff.cs). The unique internal 35-parameter `DemodulateCore` name is preserved for reflection/delegate callers and forwards to the differently named implementation with no recipient. Ordinary public/VHS callers may incur a forwarding call/frame if it is not inlined. Their throughput was not measured by this LD campaign; full stack-trace identity is not promised.

## Immutable baseline, candidate and runtime

- Baseline: clean published **2.17.0**, main `ea5aaa0a4ef7d52c1cf7c74cac223e0e073ab1fc`, natural FDD CLI `artifacts/ld-speed-v16/baseline-build-r1/cli`. Core SHA256 `e8fbddd34fc888852ffda88af3ed7245c53488b29ff12e6cbf029c812222d3c6`; baseline build owner SHA256 `0cb019eb7381851ed9bf7a42245310b681d7fb3730ad6f4c6c4958c6e49ad6f0`.
- Measured candidate: `artifacts/ld-speed-v16/candidate-build-r2/cli`, Core SHA256 `f0d50c269091cfa0b393567034f25fefe5a56c494b9a34317c762f7f650c5b5e`. CLI and test Core are identical, with zero warnings/errors in both final natural builds. Candidate build owner SHA256 `f9de57fcdbd52f39a29c4ad1f2fed0d34cacffe274e9fb976f6e97249f0ca11f`.
- The measured 477-file source-r2 authority is `artifacts/ld-speed-v16/candidate-source-r2.json`, SHA256 `7bd1012e79e88c9cb9364f45dfa66a748354200e7b7ef678e835116ed3bc49f3`: two modified production files, one new holder and one new 47-case test file over the 475-file published baseline. Original Phase2 remains unchanged; the rejected V15 packed-audio code is absent.
- Both sides use the same guarded private **.NET 11 RC3 `11.0.0-rc.3.26522.148`** NETCore + ASP.NET snapshot (336 files), receipt SHA256 `320a11efadfbddd7f1d6db6a48b7be8db63f9c267b5d0d0e1b568e2b3d26f558`. Each process launches its `dotnet.exe --fx-version 11.0.0-rc.3.26522.148 --roll-forward Disable .../decode.dll` with both DOTNET_ROOT variables set to that snapshot and `DOTNET_ROLL_FORWARD=Disable`.
- Both complete CLI72 maps, private runtime, source authority, input identity and FFmpeg are guarded before/after every child. There was no parent JIT/ISA/GC/tiering override. The local IPP bridge stays SHA256 `69f93937c685292e76c7aea2ae723cb739c60242b57765e3fda78e6ceead7fcd`.

These are the measured **pre-version-bump 2.17 modules**. The later 2.18 version/documentation commit and its rebuilt modules require separate validation; a rebuilt 2.18 SHA must not be substituted into these timing records. Compatibility remains Python v0.4.0 `g4315520 --threads 0`. Results are incremental against the published 2.17 baseline, not added to earlier V7/V14 percentages.

Both measured `decode.deps.json` files are byte-identical (SHA256 `062ec821ea722814b7d92d94a99419ddfd3b2f3bbcc6f925a542b1651c7818fc`). Their logical project-library IDs are `decode/2.17.0`, `VHSDecode.Core/2.15.0` and `VHSDecode.Preview/2.15.0`, reflecting local `--no-restore` history. The actual measured Core/Preview assembly and file versions are **2.17.0.0**; the bound paths and E8FB/F0D Core hashes identify the compiled code. These local logical labels are separate from the published 2.17 CI-asset validation. Versioned 2.18 validation will explicitly restore and check project-library IDs as 2.18.0 and assembly/file versions as 2.18.0.0, with the nonproject dependency graph unchanged; the historical timing modules remain preserved.

Initial build `candidate-build-r1` is preserved as failed at the outer zero-warning gate: all three compiler/SDK children exited zero, CLI had zero warnings, and the new test emitted one `xUnit2031` warning. Only `Assert.Single(collection.Where(predicate))` changed to the identical predicate overload `Assert.Single(collection, predicate)`. Final r2 has zero warnings/errors; its 72-file CLI map equals the retained r1 CLI observations. Production and all 47 cases are unchanged by that test-only correction.

## Correctness and numerical scope

- Local focused validation: **105/105 passed**. Full suite: **2,645 passed + the same original 15 skipped = 2,660 total**. AVX-disabled selection: **423/423 passed**. All 47 new cases pass in every context, including 11 actual IPP-labelled cases per run with required IPP enabled. They cover accepted/declined ownership, full-N poison overwrite, EFM shorts, aliases, errors, read-only input/filter state, retained channels and distinct concurrent owners. Managed AVX disabling does not disable native IPP SIMD.
- Six actual-module correctness processes ran 68 cases each. The complete inventory contains **1,224 comparisons**: 272 cross-module, 136 same-candidate, 408 old-self and 408 same-module-role comparisons. Defined bits and unconditional raw surfaces, NaN classification, shapes, final EFM shorts, errors and metadata have **zero differences**.
- Original10: two backends across five fixed cases (middle, distant start, verbose VITS, options/RF-TBC, synthetic PAL). All **10 decodes + 2 FFmpeg auxiliaries exited zero**, producing **368 fields**, complete old five-file gold dictionaries, timestamp-prefix-only log comparisons, RF decoded-PCM hashes and cross-backend equality. The independent results peer rehashed all 50 actual raw output files and ten logs, and checked the two saved RF PCM hash outputs; it did not run a second decode.
- Correctness qualification SHA256 `cc7c61f5546356231fa49accb89d0cd49f29e8aaafb38ed4c303c7aa17cca21a` binds the actual source/build/tests/module comparison/original10 evidence before whole timing.

The synthetic computed FFT outputs also contain **365,502 raw NaN sign/payload differences**, including control/old-self contexts. Every location and hexadecimal value is retained in `actual-module-r1/cross-module-comparison.raw-differences.jsonl` (114,902,672 bytes; recorded SHA256 `fae0459b18068468b85403c5408e7b2368cfa1aad34d45e298073768094e11cd`). These observations are not described as raw equality. The published EA5 contract ([scheduling regressions](../tests/VHSDecode.Tests/LaserDiscAudioPhase2SchedulingTests.cs), [V6](LD_PERFORMANCE_V6.md), [V7](LD_PERFORMANCE_V7.md)) requires exact defined finite/±0/Inf bits and NaN classification for these FFT components; it does not create a universal JIT-context NaN payload promise. This contract was frozen before V16 module/unit/whole execution. No buffer was normalized or rewritten, and no NaN-to-defined change is accepted. Input/S0/filter/poison/retained snapshots, prescribed finite destination states, every EFM short and all real-sample files remain strict. The separate rejected V15 records are unchanged.

All five complete output files in every warmup and measured whole run match the original 480-frame gold hashes, including JSON and SQLite bytes without metadata normalization:

| File | SHA256 |
| --- | --- |
| `.tbc` | `b85d052261c72e429672c9c3f750b3db62cc39cef6e49b42182a9b3dab8ff3f0` |
| `.pcm` | `7226bcf87bb1a920609c97ca2e1240c600205d3517209500bae70997eee18e4d` |
| `.efm` | `49915bc1d566ec6760771974111069e34c5c53cf5f722419207cffb692dcc0b1` |
| `.tbc.json` | `6eb771849bd17cb7c7c34c24aa5e586c09718117e79f9287730320f58703b2ac` |
| `.tbc.db` | `fc8d6711c8192a664c2c3a96712df92d80f6ed85f2f8c0770c6d669ef41f3d4d` |

Each whole process produces 960 fields. Original10 retains gold authority SHA256 `4398a314e4ed540c3b3c89405793f46c65936a49283697a5352167171f06b24a`. Unit tests' same-candidate legacy-entry oracles and the separate actual-old-module comparison have distinct scopes; neither is inferred from equal final outputs alone.

## Predeclared whole-process protocol

E993 predeclared **both default4 backends (Exact4 and IPP4) as primary stable-wall targets**. Exact20 and IPP16 are nonregression/scalability checks; they need not improve, while a material repeatable regression would block retention. Acceptance inspects all paired signs/ranges, both first-order strata and CPU. A positive aggregate median or structural allocation count alone is insufficient. No result-dependent backend/thread gate, trimming, replacement pair, rescue rerun, PGO/ISA tuning or new significance cutoff was used.

The fixed protocol is standard32: seven alternating measured pairs per backend after one warmup pair each, followed by scaled24: five measured pairs per backend after one warmup pair each. All children decode NTSC `--start 0 -l 480` from `D:\BmdFiles\DDD\10e_lxm_side1.ldf` (75,165,807,979 bytes), with unchanged default video/audio/EFM/AGC/dropout/automatic-MTF options. Input path/size/exact integer mtime are guarded; the 75 GB input was not rehashed. Historical 20/16 thread profiles are not new defaults or a claim of an RC3-wide optimum.

All **56 owned children exited zero: 8 warmups + 48 measured rows = 24 pairs / 72 signed changes**. Pair0 records remain retained and are excluded from the tables as predeclared. Each value is `100 * (before - after) / before`, followed by the median of pair percentages; positive means lower elapsed, main CPU or peak. It is not the ratio of independent before/after medians.

Wall includes private-host startup, synchronization, decode and output. Main CPU is GetProcessTimes user+kernel across that process's threads, excluding FFmpeg children. Peak is completed-handle OS peak working set, not managed allocation, total process-tree memory or energy. Output fsync/hash/hardlink processing occurs after decoder exit and outside these metrics.

| Configuration | Role | Pairs | Wall median; faster pairs | Main CPU median; lower-CPU pairs | Peak median; lower-peak pairs |
| --- | --- | --- | --- | --- | --- |
| exact / 4 | primary | 7 | +2.635677%; 6/7 | +2.073441%; 5/7 | +0.573206%; 4/7 |
| ipp-fast / 4 | primary | 7 | +2.673677%; 4/7 | +3.303714%; 6/7 | +0.889729%; 5/7 |
| exact / 20 | nonregression | 5 | +1.241501%; 3/5 | +0.542594%; 4/5 | +2.394733%; 3/5 |
| ipp-fast / 16 | nonregression | 5 | +3.415775%; 5/5 | +2.332273%; 5/5 | +0.645310%; 3/5 |

### Execution-order strata

Pair1 is candidate-first; later measured pairs alternate. These smaller strata are descriptive checks within the same experiment.

| Configuration | First | Wall median; positive/count | CPU median; positive/count | Peak median; positive/count |
| --- | --- | --- | --- | --- |
| exact / 4 | before | +2.859023%; 2/3 | +2.636148%; 2/3 | +0.573206%; 2/3 |
| exact / 4 | after | +2.612152%; 4/4 | +1.584023%; 3/4 | +0.950248%; 2/4 |
| ipp-fast / 4 | before | +2.673677%; 2/3 | +3.303714%; 3/3 | +0.753864%; 2/3 |
| ipp-fast / 4 | after | +1.777520%; 2/4 | +4.050180%; 3/4 | +0.902370%; 3/4 |
| exact / 20 | before | +0.596133%; 1/2 | +2.309591%; 2/2 | +2.428556%; 1/2 |
| exact / 20 | after | +1.241501%; 2/3 | +0.542594%; 2/3 | +2.394733%; 2/3 |
| ipp-fast / 16 | before | +3.244216%; 2/2 | +2.521005%; 2/2 | -0.081238%; 1/2 |
| ipp-fast / 16 | after | +3.726023%; 3/3 | +2.232573%; 3/3 | +9.494417%; 2/3 |

Exact4 has six faster wall pairs and positive wall medians in both orders. IPP4 has only four faster pairs: before-first is 2/3 positive, candidate-first 2/4. Its CPU is 6/7 positive, with both-order CPU medians favorable. This supports credible but modest/noisy default IPP benefit in the measured scope; it does not establish a reliable per-run wall win. Scaled Exact is mixed but has positive wall medians in both orders; scaled IPP wall/CPU are each 5/5 positive. No material repeatable regression is evident in those predeclared scaled checks.

### All 24 signed pairs / 72 changes

Tables are generated directly from frozen statistics, rounded to six decimals for display. Owners/statistics retain full precision and every negative, tie and large observation.

| Configuration | Pair | First | Wall reduction % | Main CPU reduction % | Peak reduction % |
| --- | --- | --- | --- | --- | --- |
| exact / 4 | 1 | after | +2.859910 | +1.094605 | +3.523681 |
| exact / 4 | 2 | before | +4.707379 | +2.636148 | +1.931632 |
| exact / 4 | 3 | after | +0.571537 | -0.420712 | -1.105149 |
| exact / 4 | 4 | before | -1.883561 | -2.330066 | +0.573206 |
| exact / 4 | 5 | after | +2.635677 | +2.073441 | +2.010931 |
| exact / 4 | 6 | before | +2.859023 | +4.476220 | -0.321065 |
| exact / 4 | 7 | after | +2.588627 | +3.396994 | -0.110436 |
| ipp-fast / 4 | 1 | after | -2.547920 | -1.698514 | +0.889729 |
| ipp-fast / 4 | 2 | before | -0.767694 | +0.297960 | +2.115217 |
| ipp-fast / 4 | 3 | after | +3.802490 | +5.858311 | -0.097970 |
| ipp-fast / 4 | 4 | before | +2.673677 | +7.106711 | -0.151489 |
| ipp-fast / 4 | 5 | after | +7.918013 | +8.423560 | +0.915011 |
| ipp-fast / 4 | 6 | before | +3.505567 | +3.303714 | +0.753864 |
| ipp-fast / 4 | 7 | after | -0.247451 | +2.242050 | +2.940225 |
| exact / 20 | 1 | after | +2.305119 | -5.345560 | +3.231340 |
| exact / 20 | 2 | before | -0.902837 | +0.374235 | +4.894695 |
| exact / 20 | 3 | after | +1.241501 | +0.594904 | +2.394733 |
| exact / 20 | 4 | before | +2.095103 | +4.244948 | -0.037584 |
| exact / 20 | 5 | after | -0.070713 | +0.542594 | -4.591285 |
| ipp-fast / 16 | 1 | after | +4.980079 | +2.232573 | -0.182997 |
| ipp-fast / 16 | 2 | before | +3.415775 | +2.709737 | -0.807785 |
| ipp-fast / 16 | 3 | after | +3.726023 | +11.394482 | +18.615005 |
| ipp-fast / 16 | 4 | before | +3.072657 | +2.332273 | +0.645310 |
| ipp-fast / 16 | 5 | after | +1.928951 | +2.145062 | +9.494417 |

Default IPP wall losses remain: pair1 **-2.547920%**, pair2 **-0.767694%**, pair7 **-0.247451%**. Its large positive pair5 **+7.918013% wall / +8.423560% CPU** is also retained. Exact4 pair4 is **-1.883561% wall / -2.330066% CPU**. Exact20 pair1 CPU is **-5.345560%**; its wall pairs2 and5 are negative. IPP16 pair3 has large **+11.394482% CPU / +18.615005% peak** observations. None was removed or retimed.

## Storage and interpretation

Every decoder writes all five complete original files. After successful exit the owner fsyncs them, verifies all five hashes and 960 fields, rehashes the same-campaign canonical files and replaces byte-identical trial files using temporary NTFS hardlinks plus atomic replacement. Original metadata is retained as receipts; all trial paths, logs and failures remain. This is the unchanged F26 storage helper in a fresh V16 campaign, not a new optimization factor.

The retained factor is limited to eligible LD EFM storage reuse. Default Exact evidence is more consistent than default IPP; the latter's three slower pairs and modest order evidence must accompany its median. Peak results are mixed and include large observations, so no stable whole-memory saving is inferred from the 512 KiB structural payload. No component timing is substituted for whole elapsed time, and CPU is not a total process-tree/energy measurement. This one host/range does not establish whole-side, every-capture, cross-platform, VHS or per-run throughput improvement.

## Reproduction and retained evidence

Local evidence is under ignored `artifacts/ld-speed-v16/`; frozen source helpers are under `scratch/ld-v16-paired-owner-r1/`. The owner reuses the unchanged FD0A workload/metric/statistics bodies and F26 storage. Its default is plan-only. Reproduction requires the exact selected source/binaries/runtime/native/input identities, new bounded campaign/output paths and the completed standard owner's actual SHA as scaled predecessor. Later versioned source must not be silently substituted into the frozen source477 campaign.

| Authority below artifacts/ld-speed-v16 | SHA256 |
| --- | --- |
| `campaign-r1/ab-standard-r1/owner.json (32 children)` | `449cd0060e9c1ddd5fbb17a076433d9fdd9ec52f043600fd8b4eafa832fe07af` |
| `campaign-r1/ab-scaled-r1/owner.json (24 children)` | `d01147624ba008552e93a79925e4d8eda65e690670015c17b26ea06e3f362bd0` |
| `statistics-r1/statistics.json (all 24 pairs / 72 changes)` | `7efdd372e1689004044ff84e764a8d1c6f1b925d87da6917c34110932ee71339` |
| `whole-stats-audio-peer-r1.json (189 checks)` | `3b0a3be3e30db8c9d288bf203ef2ef166f6b8679a94afed3bbdaad8f187b5e3d` |
| `candidate-tests-r1/owner.json` | `164343a47edccf26394fc4706da9bc57c83ce53ddc2209a992457f224d370f75` |
| `correctness-qualification-r1.json` | `cc7c61f5546356231fa49accb89d0cd49f29e8aaafb38ed4c303c7aa17cca21a` |
| `actual-module-r1/cross-module-comparison.json (1,224 comparisons)` | `7e3ea14373cb6b8d699e8354137060b235f610995b0a2e3347af3799e85a5f94` |
| `original10-r1/owner.json (12 children)` | `7835815ef1155a556734eaeae07ac4a89bb11d28aa9b84571dc03bdadc71eae7` |
| `original10-r1/runs.json (complete gold / logs / RF PCM)` | `1172aadf9360a34374a74d9ac44104d393bffeb3a908f3ff5c342f41d2d0bf60` |
| `actual-results-audio-peer-r1.json (223 checks)` | `1602be7c1c0997b93e053c6ae48ee497f94666ce73f549314c4ceac0ffa283db` |

Predeclaration manifest: `scratch/ld-efm-dead-spectrum-v16-protocol-plan-r1/source-manifest.json` SHA256 `e993aa505e659dba7ca736deb60a8547eea511d629ad8bd82eac7cf37159a008`. Frozen whole-owner manifest SHA256 `2a1f514809a25edfa2b05d95a6bb208e28cec6fb21561c2b86f9762b7a51a1d0`; actual binding SHA256 `1bb3f2b7598d34e64f89a4bac7687f9c9b58679e35d72b62cce553639a5f8749`. The initial warning owner `443a176d5b5837e075aead9a382d336251a5330b896908e7a1db0cd54acee1c5` and every numerical raw-difference artifact remain preserved separately.
