# LaserDisc private Envelope omission - 2026-11-05

V14 retains a single internal default-runner change: omit an RF Envelope that the immediate LD field consumer does not use. On the measured Windows x64 host and 480-frame capture range, median paired whole-process wall time fell **2.247039% for exact / 4 threads** and **2.217902% for IPP / 4 threads**, with all seven pairs faster in each backend and both execution-order strata positive. Main-process CPU medians also fell. The LD default remains four threads.

Exact / 20 threads improved in four of five pairs. IPP / 16 threads is **neutral/uncertain for elapsed time**: +0.130396% median reduction, 3/5 positive, with a negative before-first stratum. This is not a stable high-thread IPP wall gain or a universal capture/platform guarantee. The complete factor was retained without adding a post-measurement backend/thread gate.

## Scope and ownership

- Only the default Runner can create the fresh private session/reader capability. Activation occurs after ordinary session construction, before the first read, and depends on actual LD field/DSP capability and the already resolved preview/output mode.
- A private, one-way cache domain prevents full and omitted blocks from being mixed. It does not upgrade, reset, replay, skip, or reorder sample-loader calls. All worker/producer branches use the same mode.
- Accepted producers use a shared length-N shape placeholder without computing magnitude or allocating the ordinary Envelope array. The placeholder is never an output destination; the existing immediate field projection omits Envelope from the leased span. Blocks that need full Envelope computation retain their separate full result.
- Public/default session APIs, custom runners, preview, raw-metric consumers and incompatible DSP paths retain full behavior. The ordinary six-argument session-reader method body remains unchanged. Arithmetic in the consumed video/audio/EFM/RF-TBC paths is unchanged.
- Tests cover no-I/O rejection, poisoned shape buffers, serial/parallel producer paths, lease lifetime, retained independent fields, cancellation, preview/manual command behavior and the actual PAL field/PCM/EFM/metadata comparisons.

Ordinary public/VHS read entries now execute additional constant domain/atomic checks. Their throughput was not measured here; this LD campaign does not establish universal non-regression for VHS or short hot reads. Public exception/dataflow compatibility is covered by the regression suite, not by a new public performance promise.

## Immutable baseline, candidate and runtime

- Baseline: clean published **2.16.0**, main `e4769bc85a95d7c5d936d3f6264e27f2cb6ebef9`, natural CLI `artifacts/ld-speed-v14/baseline-build-r1/cli`. Core SHA256 `8bf055b73bc84f798d7ee44ac5c3dde68d46e8e8dddd801c24772ce4682ef19b`; build owner SHA256 `a38ae7334848363fdd6270b1d0348c4d533ec5e04d0c97072a0b6d62ddddb38e`.
- Measured candidate: the reviewed five-production-file Envelope factor on that same main, natural CLI `artifacts/ld-speed-v14/candidate-build-r1/cli`. Core SHA256 `f9118a074da6668848f169661af7acb879559e26ef7bc10da17e5ac3f2c3c6f8`; CLI and test Core match. Build owner SHA256 `8a96f78216fa2ef786a2f6df132545923123f7d776f361282fea180c72748e82`.
- The natural CLI was compiled from source-r1 map `c9d34c96a8ed6438d3f08ebf7275476dfedd1616afda0ab3de209c929821a8a8`. The measured current 474-file source-r2 map is `a16c02ad9ef2001d738bc7ea5826a86bb5554d2b555ac4ba0a59a0819c6cd7de`. Only the Stream test fixture changed between these maps: its LD sample rate was corrected. All five production files and selected CLI72 bytes are unchanged. The CLI is not claimed to have been rebuilt from the later test map.
- Explicit fixture qualification SHA256 `63e4538d7cab84917aa60b15f3cf8ee5efa5c6cc0572c8c02850408f2c8eb9b3` and test-only rebuild owner SHA256 `483afb750ebaa6dc8c60d5590a152b0fbe2bfacf26f931fce8970f4c313253f6` bind the later tests to the same Core. The baseline source content map `5401e3921ad31fe60906d50c20bde3da1045b0c05a267679f0f0bfc17fa817f2` keeps an earlier 8F910 label; the actual baseline build is E476, whose source content is identical.
- Both sides use the same private **.NET 11 RC3 `11.0.0-rc.3.26522.148`** NETCore + ASP.NET runtime snapshot (336 files). Snapshot receipt SHA256 `320a11efadfbddd7f1d6db6a48b7be8db63f9c267b5d0d0e1b568e2b3d26f558`. Each child invokes its `dotnet.exe --fx-version 11.0.0-rc.3.26522.148 --roll-forward Disable .../decode.dll`, with both DOTNET_ROOT variables pointing there and `DOTNET_ROLL_FORWARD=Disable`.
- The selected 72-file CLI payloads, private runtime, current source map, input identity and FFmpeg are guarded before/after each process. No parent JIT/ISA/GC/tiering override was present. The IPP bridge remains SHA256 `69f93937c685292e76c7aea2ae723cb739c60242b57765e3fda78e6ceead7fcd`.

These hashes identify the measured pre-version-bump modules. The 2.17 version/documentation commit is validated separately; a different rebuilt module hash must not be substituted into the historical timing records. Compatibility remains Python v0.4.0 `g4315520 --threads 0`; the application version does not change that identity.

## Correctness

- Final full suite: **2,598 passed + the original 15 skipped = 2,613 total**. All 58 added producer/stream/runner cases pass in that suite.
- AVX-disabled selection: **376/376 passed**.
- Original10: both backends across five fixed cases (middle, distant start, verbose VITS, option/RF-TBC combination, synthetic PAL); 10 decodes + 2 FFmpeg auxiliaries all exited zero, with **368 fields**, complete old-gold raw outputs, timestamp-only log normalization, cross-backend equality and RF-to-PCM comparison.
- Root correctness qualification: `artifacts/ld-speed-v14/correctness-qualification-r1.json`, SHA256 `a0ae31bdffaf9e9b9ad622f8c5b1942f2e292516b3232bad0f819acbda496e90`. Completed tests owner SHA256 `df4154fc80473876baf21386fd1ae62835452ec243dc71132bea79bcc4800d3d`.

All five complete files in every warmup and measured whole run match these original hashes (no rounding or tolerance):

| File | SHA256 |
| --- | --- |
| `.tbc` | `b85d052261c72e429672c9c3f750b3db62cc39cef6e49b42182a9b3dab8ff3f0` |
| `.pcm` | `7226bcf87bb1a920609c97ca2e1240c600205d3517209500bae70997eee18e4d` |
| `.efm` | `49915bc1d566ec6760771974111069e34c5c53cf5f722419207cffb692dcc0b1` |
| `.tbc.json` | `6eb771849bd17cb7c7c34c24aa5e586c09718117e79f9287730320f58703b2ac` |
| `.tbc.db` | `fc8d6711c8192a664c2c3a96712df92d80f6ed85f2f8c0770c6d669ef41f3d4d` |

The whole oracle range contains 960 fields per process. Original10 uses the same retained pre-optimization gold authority `4398a314e4ed540c3b3c89405793f46c65936a49283697a5352167171f06b24a`.

Initial analyzer/fixture failures are preserved below ignored `artifacts/ld-speed-v14/`, including `candidate-initial-build-r1`, `candidate-tests-r1` and the later fixture-correction/rebuild records. The initial Stream fixture used an invalid LD sample rate and also failed the ordinary full path. Correcting that test did not alter production Core or discard a performance observation. Final tests refer to actual later passing owners; failed receipts remain failed.

## Whole-process protocol and measurements

The protocol was frozen before timing: standard32 at exact4 / IPP4, seven alternating measured pairs per backend after one warmup pair each; then scaled24 at exact20 / IPP16, five measured pairs per backend after one warmup pair each. Every process decodes NTSC `--start 0 -l 480` from `D:\BmdFiles\DDD\10e_lxm_side1.ldf` (75,165,807,979 bytes), with the same default video/audio/EFM/AGC/dropout/automatic-MTF options. Input size and exact integer mtime were guarded; the large RF input was not rehashed. The 20/16 thread counts are predeclared historical host/sample profiles, not new defaults or a claim of an RC3-wide optimum.

All **56 owned processes** exited zero: **8 warmups + 48 measured rows = 24 pairs / 72 signed changes**. Warmups are excluded from the tables. No trimming, substituted pair, retiming or parameter adjustment was used. Each pair uses `100 × (before − after) / before`, followed by the median of those pair percentages; positive means a reduction. This differs from taking a ratio of independent before/after medians.

Wall includes private-host startup, sync recovery, decode and output. Main CPU is `GetProcessTimes` user+kernel time across that process's threads, excluding FFmpeg children. Peak is the completed-handle OS peak working set, not managed allocation or energy. Output fsync, hashing and deduplication happen after exit, outside those decoder metrics.

| Configuration | Pairs | Median paired wall reduction; faster pairs | Median paired CPU reduction; lower-CPU pairs | Median paired peak reduction; lower-peak pairs |
| --- | --- | --- | --- | --- |
| exact / 4 (standard) | 7 | +2.247039%; 7/7 | +2.932961%; 5/7 | +1.203971%; 7/7 |
| ipp-fast / 4 (standard) | 7 | +2.217902%; 7/7 | +4.334365%; 6/7 | +3.199417%; 7/7 |
| exact / 20 (scaled) | 5 | +2.344197%; 4/5 | +2.152932%; 4/5 | +0.778163%; 4/5 |
| ipp-fast / 16 (scaled) | 5 | +0.130396%; 3/5 | +2.049119%; 4/5 | +0.033208%; 3/5 |

### Execution-order strata

Pair1 is candidate-first; later pairs alternate. These smaller strata are descriptive checks, not additional independent experiments.

| Configuration | First | Wall median; positive/count | CPU median; positive/count | Peak median; positive/count |
| --- | --- | --- | --- | --- |
| exact / 4 | before | +2.247039%; 3/3 | +2.611715%; 2/3 | +2.990932%; 3/3 |
| exact / 4 | after | +2.490254%; 4/4 | +3.879666%; 3/4 | +0.821967%; 4/4 |
| ipp-fast / 4 | before | +3.292689%; 3/3 | +4.334365%; 2/3 | +3.256806%; 3/3 |
| ipp-fast / 4 | after | +2.167625%; 4/4 | +4.099080%; 4/4 | +2.913625%; 4/4 |
| exact / 20 | before | +3.874101%; 2/2 | +5.698318%; 2/2 | -0.488629%; 1/2 |
| exact / 20 | after | +1.322540%; 2/3 | +1.137561%; 2/3 | +0.778163%; 3/3 |
| ipp-fast / 16 | before | -0.174157%; 1/2 | +3.139072%; 1/2 | -0.007623%; 1/2 |
| ipp-fast / 16 | after | +1.319914%; 2/3 | +2.049119%; 3/3 | +1.547798%; 2/3 |

Both default4 backends have seven faster wall pairs and positive wall strata in both orders. Exact20 has four positive pairs and positive order medians, with an adverse pair retained. IPP16 wall is near zero: **+0.130396%, 3/5**, before-first **−0.174157%** and candidate-first **+1.319914%**. Its CPU median is favorable, but that does not establish a stable high-thread elapsed benefit. Scaled peak results are also weak/order-sensitive; no reliable high-thread memory reduction is claimed.

### All 24 signed pairs / 72 changes

Values are rounded for display; raw owners and statistics retain full precision. Negative values remain included.

| Configuration | Pair | First | Wall reduction % | Main CPU reduction % | Peak reduction % |
| --- | --- | --- | --- | --- | --- |
| exact / 4 | 1 | after | +1.934633 | +4.952438 | +0.660324 |
| exact / 4 | 2 | before | +2.683891 | +2.611715 | +1.203971 |
| exact / 4 | 3 | after | +0.094667 | -0.617951 | +3.980814 |
| exact / 4 | 4 | before | +2.247039 | +2.932961 | +2.990932 |
| exact / 4 | 5 | after | +3.045875 | +3.716165 | +0.255911 |
| exact / 4 | 6 | before | +1.567793 | -0.718525 | +4.451162 |
| exact / 4 | 7 | after | +3.889227 | +4.043168 | +0.983609 |
| ipp-fast / 4 | 1 | after | +1.364582 | +2.305282 | +3.199417 |
| ipp-fast / 4 | 2 | before | +3.717022 | +4.575163 | +4.186914 |
| ipp-fast / 4 | 3 | after | +2.217902 | +4.548525 | +0.631586 |
| ipp-fast / 4 | 4 | before | +0.562790 | -1.378556 | +3.256806 |
| ipp-fast / 4 | 5 | after | +2.117347 | +3.649635 | +2.627832 |
| ipp-fast / 4 | 6 | before | +3.292689 | +4.334365 | +2.865085 |
| ipp-fast / 4 | 7 | after | +2.659380 | +6.244635 | +3.228835 |
| exact / 20 | 1 | after | +3.203036 | +2.152932 | +0.778163 |
| exact / 20 | 2 | before | +2.344197 | +7.358413 | -1.771234 |
| exact / 20 | 3 | after | +1.322540 | +1.137561 | +1.008734 |
| exact / 20 | 4 | before | +5.404005 | +4.038224 | +0.793976 |
| exact / 20 | 5 | after | -1.934307 | -7.301730 | +0.694806 |
| ipp-fast / 16 | 1 | after | -0.829522 | +1.061164 | -1.134702 |
| ipp-fast / 16 | 2 | before | -0.478711 | +7.175282 | -0.048453 |
| ipp-fast / 16 | 3 | after | +1.319914 | +2.479214 | +8.809663 |
| ipp-fast / 16 | 4 | before | +0.130396 | -0.897138 | +0.033208 |
| ipp-fast / 16 | 5 | after | +2.602379 | +2.049119 | +1.547798 |

In particular, exact20 pair5 is **−1.934307% wall / −7.301730% CPU**; IPP16 wall pairs1 and2 are negative. No outlier was removed to obtain the reported medians.

## Storage, component evidence and limits

Each decoder writes all five complete original files. After successful exit, the owner fsyncs them and verifies all five SHA256 values and 960 fields before replacing byte-identical trial files with same-campaign NTFS hardlinks. Canonical bytes are rehashed; a temporary same-directory hardlink is made before atomic replacement. Failed/partial originals are retained. All trial paths and independent logs remain. This is the already qualified V13 storage protocol in a fresh V14 campaign; earlier V12 absolute timings used a different postprocessing protocol and must not be pooled.

The independent same-candidate leaf study observes approximately **256 KiB fewer managed bytes per call** when Envelope is omitted (32,768 × 8-byte payload; measured paired medians about 262,142 bytes exact / 262,217 bytes IPP). Its exact elapsed result is mixed (+0.325827%, 4/7); IPP leaf elapsed is +10.929723%, 6/7. Those leaf measurements exclude Stream/domain lifetime, field/audio/EFM/output and are not an old/new whole-decode comparison. They do not measure whole allocated bytes and cannot be multiplied into a whole speed estimate. Leaf CPU sits on an observed 15.625 ms grid; small CPU percentages are coarse.

The full factor is retained for the consistent default4 whole result, supported by CPU, while the high-thread IPP wall uncertainty and every negative pair remain explicit. No posthoc backend/thread gate was added. This one Windows host/capture range does not establish a whole-side, cross-platform, every-capture, VHS, energy or total process-tree CPU improvement. Unlike the earlier V11 ambient-runtime campaign, this campaign used guarded private RC3 runtime bytes throughout.

## Reproduction and retained evidence

Local evidence is below ignored `artifacts/ld-speed-v14/`; the binary/source selections, all warmups and measured rows, raw trial paths/hardlink receipts, logs and failures are retained. Important authorities:

| Authority | SHA256 |
| --- | --- |
| `campaign-r1/ab-standard-r1/owner.json` (32 processes) | `5fad0c3b96c36e9303baa2ae37cb8a95ae5fc8db09a75c906ec2ca4cbb79b7c0` |
| `campaign-r1/ab-scaled-r1/owner.json` (24 processes) | `42278df843018e080fc7982136dbc05861b355d55392d25ec1d332e38d72156b` |
| `whole-root-stats-r1.json` (all 72 signed values) | `e39ca936a48185ee12ff9e2fb2406e1075b58ad73b47b0736b6c4c84d4764bfa` |
| `whole-audio-author-stats-peer-r1.json` (442 checks) | `8c092511fcb2bccbcb6d2a81556d8e0b1f18d2ba57075c3529be6c5708767050` |
| `retention-assessment-rf-peer-r1.json` (60 checks) | `bbc17b27bff808e848084f75f5d40e5826f1d29d821e7a79997c6663406c7df3` |
| `decision-retain-r1.json` | `96e6d91b5ed58b54743412880c4557e35412438e39050e9591ddf247f15aa845` |
| `leaf-ablation-audio-stats-peer-r1.json` (component only) | `53281331a4aae0ddfd5d558e4409f7ebeb045d1e2f39ea45cbfb25c93ca9a8fa` |

Frozen source owner: `scratch/ld-v14-paired-owner-r1/source-manifest.json` SHA256 `8638239b03cfa52ec983c2ecb588835abee92550c68c6450e6f1b4b6a6b247cd`. Binding SHA256 `d6568290ee5e3d07e974f474db21ec06526dc5870b4b9f4c3a597e8131ba30dc`. It reuses unchanged FD0A workload/metric/statistics bodies and byte-identical storage helper. A reproduction must choose new bounded output/canonical directories, preserve the private runtime/input/native selections, and use the completed standard owner's actual SHA as the scaled predecessor. Default owner invocation is plan-only; explicit execution authorization is required.
