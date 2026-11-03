# LaserDisc clipping, field buffers and packet FFT — 2026-11-04

Baseline: merged main `32f8459a6835128130968c5c9a8786e094b29f10`,
including the [fifth performance iteration](LD_PERFORMANCE_V5.md).
Baseline Core SHA-256:
`c816af34de5c4a1221844307c21bce7b806de610f7f9e54aade37af6677a2615`.
Candidate branch: `codex/ld-packet-and-field-speedup-v6`.
Complete candidate Core SHA-256:
`2d4edb1fda74a4a17fa5e71a14e36bbeb5b2b668cad4cd73c948279d22809afc`.
Windows x64, .NET 11 RC2 (`11.0.0-rc.2.26504.105`), 20 logical processors,
Intel IPP 2026.0.0. Baseline and candidate use the same native IPP bridge
and Release settings. Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf`, NTSC,
with default video, analog audio, EFM, AGC, dropout and automatic MTF enabled.

## Complete decode measurements

The protocol was fixed before any v6 complete-decode timing: one warm-up
pair followed by seven alternating measured pairs per backend, 480 frames
per process, four threads, both `exact` and `ipp-fast`. This longer primary
range was chosen in advance because v5's 240/480-frame protocols produced
different gain sizes and mixed main-process CPU results. The fixed protocol
is recorded in `artifacts/ld-speed-v6/planned-protocol.json`; complete
measurements are in `artifacts/ld-speed-v6/ab-bundle-retry/`.

Wall time is primary and includes startup and output writing. Complete-file
hashing happens after exit. CPU time is auxiliary main-process user plus
kernel time; it excludes FFmpeg child processes and is not total system CPU
or an energy measurement. Each percentage is computed per paired process
comparison, then the seven percentages are summarized by their median.
It is not the ratio of two separately computed time medians.

| Backend | Baseline median wall | Candidate median wall | Median paired wall-time reduction | Faster wall pairs | Median paired CPU reduction |
| --- | --- | --- | --- | --- | --- |
| exact | 47.045 s | 45.016 s | 5.03% | 7 / 7 | 5.66% |
| ipp-fast | 37.269 s | 36.892 s | 1.99% | 6 / 7 | 1.22% |

All seven exact wall/CPU pairs improve. Exact wall reductions range from
1.78% to 6.27%. IPP wall reductions range from -1.51% to 2.38%, with six
positive pairs; the negative pair remains included. IPP CPU time improves
in six pairs and regresses 1.92% in one. Main-process CPU medians are
110.703/103.734 s for exact and 74.156/73.109 s for IPP.

All 32 processes produced 960 fields. Complete `.tbc`, `.pcm`, `.efm`,
`.tbc.json` and `.tbc.db` SHA-256 sets match before/after and across
backends. These are incremental results against v5 on this machine and
input range; no cumulative percentage or whole-side speedup is inferred.

The four production changes are retained together after strict numerical,
public/default-read, ownership, native-IPP, original-oracle and complete
decode checks. The packet128 path has a consistent target-size kernel
signal and the complete decode improves, but its negative large-size
micro controls remain unresolved and fully reported below. Retention does
not assert universally faster FFTs, prove why those controls changed, or
attribute the complete-decode percentage to one component. All 32 successful warm-up/measured process rows are retained.

The first `ab-bundle/` attempt failed during baseline startup because an
incomplete directory copy omitted the SQLite runtime DLL. It produced no
decode/timing rows. The baseline was recopied with subdirectories, and all
72 relative file SHA-256 values were verified identical to the immutable
v5 candidate source, including Core `c816...`. The failed directory/log is
retained and the cause recorded in `environment.json`. The replacement
artifact directory does not change the fixed 480-frame/seven-pair protocol
or discard a measured pair; no candidate/source/user-data change was made.

## Retained mechanisms and strict constraints

### Full-precision demodulated video clipping

`DemodVideoClipper` replaces the private allocating `ClipDemodForVideo`
loop. It retains the original `double upper = sampleRateHz * 0.75`, the
1,500,000 Hz lower bound and f64 precision. AVX uses ordered comparisons
and bit blends to implement the original `Math.Clamp` branch semantics.
It does not substitute floating min/max instructions, quantize to float32,
fuse later processing, approximate arithmetic or mutate demodRaw.

Input quiet/signaling NaN payloads and signs remain raw-bit identical;
ordered comparisons also preserve infinities, subnormals, signed-zero
handling and exact/adjacent bound behavior. Inverted bounds take the old
scalar loop, preserving its first-iteration exception type/message/parameter
and the empty-input no-throw behavior. A NaN upper bound still permits the
original lower-bound-only comparison. Unsupported hardware and tails use
that same scalar reference operation.

The result is a fresh owned `double[]` allocated without redundant initial
clearing. Every element is written before return. There is no pooled or
shared output and no allocation-byte reduction: normally N*8+24 bytes per
call. The unsafe destination writer is private and only receives the fresh
same-length output; tests separately poison that destination and its guards.

### Internal LD field Envelope projection

The immediate sequence reader may omit copying the per-block Envelope
into its full field span only when the actual field decoder has ordinal
`decodeType == ld`, `!PreservesRawMetricSources`, `Enabled == true` and
`Mode == LaserDiscDemod`. Disabled, TapeEnvelope, unknown enum, non-LD and
verbose decoders decline this opt-in. The gate reads the actual decoder's
constructor-selected options: a public `DecodeSession with` can replace
metadata options and the decoder independently.

Only the explicitly named internal immediate-LD lease entry point returns
this temporary span with `Envelope == null`. Synchronous field decoding
finishes before the using lease disposes. Public `Read`, default
`ReadLeased`, first-field owned reads, VHS/CVBS, custom readers and verbose
raw-metric ownership retain their existing complete behavior. No span or
Envelope escapes through retained field outputs, metrics or deferred work.

This first candidate removes assembly copying only. The reusable field
Envelope allocation and two-slot pool remain. RF block Envelope computation,
RF high boost, weak-signal diagnostics, source loading, cache/MTF history,
optional audio/EFM and block lifetimes remain unchanged.

Serial and parallel append still invoke the Envelope helper at the original
channel position. Its source-length/cut checks, checked block/window ends
and empty-intersection handling keep their old order. A skipped copy first
proves the real source index, destination index and count valid. An invalid
range falls through to the original `Array.Copy`, preserving its remaining
validation/errors and existing `Parallel.For` exception wrapping. Empty
destinations or Span checks are not used to replace the old validation.

Omission can leave stale bytes in the unused pooled Envelope. Every later
successful full read still overwrites all published slots: adjacent block
stride intervals, intersected with the requested window, partition the
entire destination. Missing/short blocks or copy errors abort before span
publication. Exact-length pool matching, overlap offsets and grow/shrink do
not introduce gaps. LDF native/FFmpeg reads require complete requested input
or return null at EOF. No extra clear hides a destination-history dependency.

### Local dropout map initial clearing

The local LD error map now uses `GC.AllocateUninitializedArray<bool>`.
`FillErrorMap` assigns every entry in the active sample interval, including
false, before its RF high-pass true-only OR pass. The unchanged
`BuildErrorRanges` reads only that interval and returns numeric range values;
the temporary map never escapes. The source has a short invariant comment
so a future true-only first pass cannot silently depend on uninitialized
bytes. Tests poison external bytes with noncanonical 0xA5 and assert known
range results, active canonical bytes, half-open bounds and empty intervals.

The sparsely marked sync-area mask still requires its initial zeroing.
No range-scanning or merge/padding arithmetic changes. The same logical
array capacity is allocated, and the runtime may still return cleared
storage; no independent allocation-byte or decode-speed claim is made.

### Packet128 fixed schedule and terminal SIMD candidate

Only the DUCC packet entry selects the new schedule when its plan length is
128. It retains the original input CopyTo, full-root twiddles, factor order
2/8/8, source/destination swaps and inverse normalization position. The
original input guard runs once before the passes. AVX handles two independent
terminal butterflies in separate 128-bit halves, in the exact scalar
FirstIndex arithmetic/rotation order, without unity multiplication, FMA,
reassociated sums or float conversion. Nonfinite/oversized input and
unsupported AVX retain the old scalar terminal pass.

Other packet lengths call the unchanged generic executor through the thin
entry decision. Ordinary Plan.Transform, Plan.TransformOwned and
Plan.TransformReal entry points remain unchanged. DUCC real wrappers still
use the packet route when their complex transform length exceeds 10,000.
The source audit confirms unchanged constructor, generic Execute,
Pass8Core, Pass2/4 and scalar FirstIndex; the fixed executor and terminal
arithmetic match the measured scratch candidate. Source equality does not
promise unchanged JIT layout or performance for the fallback controls.

Rooted plans are tested against an old packet reference that invokes the
unchanged generic Plan.Transform with the full transform's root table,
not standalone 128 roots that can round differently. Defined finite, zero
and infinity bits remain strict. Existing JIT-dependent FFT NaN payload/sign
behavior is not turned into a new cross-platform raw-bit promise; NaN
classification must agree, and no NaN-to-finite/Inf change is accepted.
Windows frozen hashes retain their platform gate; local generic/scalar
references, alias, offsets and guard tests apply on every tested platform.

## Correctness evidence

- Unified Release build: zero warnings and errors. Full suite: 2,490 passed,
  15 skipped, zero failed (2,505 total).
- Focused retry: 84/84 passed. Combined `DOTNET_EnableAVX=0` validation:
  219/219 passed, comprising the 182-case CI class subset plus the
  31-case Envelope projection and six-case session-reader fixture.
- The common-kernel checkpoint before the packet change and the complete
  candidate each passed the ten original-baseline CLI cases: middle range,
  one thread, verbose VITS, combined options and PAL on both exact/IPP.
  Complete output/decoded RF PCM hashes and normalized diagnostics match
  the original baseline, and exact/IPP outputs match each other in those
  tested ranges. These short cases establish bounded correctness, not
  throughput or whole-side performance.
- Clipping's isolated normal, AVX-disabled, TieredCompilation=0 normal and
  TieredCompilation=0 AVX-disabled runs each passed 924 strict scenarios,
  546 poison/canary checks, 12 parallel cases and eight immutable-Core
  frozen oracles. Strict NaN payload/sign checking required no relaxation.
- FFT scratch normal, AVX-disabled and TieredCompilation=0 each passed
  142 strict scenes and eight parallel checks. Timed hashes agree across
  224 normal and 224 Tiered0 comparisons; warmed allocation measurements
  are zero within those preallocated scratch kernels; public allocating
  FFT APIs still own and allocate their results. Shipping tests also compare
  old full-root generic packet math,
  ordinary 128 generic/scalar behavior, large-to-small active TLS dimensions,
  in-place/preserving APIs and source/destination canaries.
- Envelope coverage includes negative gates, both directions of session
  metadata/actual-decoder divergence, null projected Envelope, retained
  payload/metric independence, malformed block Envelope length/null,
  validation/checked-overflow precedence, parallel wrapping, recovery/EOF,
  offset windows and subsequent full reads after poisoned omission.
  First-field owned reads leave released pool Envelope/Video canaries
  untouched; subsequent default full leases refresh every Envelope slot.

### Initial fixture failure retained

The first focused run was 80 passed / two failed. Both failures came from
a test requiring every alternating buffer length to retain object identity.
The pool has two slots and no eviction: after retaining lengths 17/25,
lengths 9/49 can legitimately be discarded. No output-bit, validation,
integration or production failure was observed.

The test now separates guaranteed same-length reuse in fresh fixtures from
a persistent 17/25/9/17/49/9 capacity-overflow sequence. Strong reuse cases
first verify B's Video identity proves it took A's exact buffer set, then
check untouched Envelope poison and C's identity/full refresh. The overflow
cases always compare every C bit to both baseline and fresh public reads,
require actual retained and discarded histories, and assert identity only
when reuse occurred. This added two cases without changing production pool
or clear behavior. The corrected 84-case retry, full suite and AVX-disabled
run pass. Core remains the same `2d4edb1f...`; the failed log/TRX and subsequent
passing reports are all retained under `artifacts/ld-speed-v6/tests-focused*`.

## Isolated clipping measurements

The baseline delegate invokes the actual old private Core helper, pinned
to SHA `40F29CB33FC235FD62A98BDC931ABDEC98D35BC107F8136D44BECE2862EDED1E`
and MVID `e53638d9-b856-46fa-a83f-e5927a08af6f`; a frozen scalar independently
checks it. Three allocating variants use rotating/reversed triples after
one-second alternating warmup and original-Core calibration, seven measured
pairs per comparison. Input profiles are synthetic finite in-range/mixed
clipping, not measured capture distributions.

Both new+AVX and uninitialized+AVX improve all eight profiles with 7/7
positive pairs in normal and independent Tiered0 processes. The selected
uninitialized+AVX version has these median paired time reductions:

| Length | Normal in-range / mixed | Tiered0 in-range / mixed |
| --- | --- | --- |
| 1,024 | 59.75% / 59.55% | 59.02% / 59.08% |
| 16,384 | 51.78% / 36.64% | 58.61% / 60.51% |
| 32,768 | 58.05% / 40.90% | 59.09% / 59.28% |
| 65,536 | 27.14% / 31.89% | 33.24% / 34.86% |

All timed output hashes match. A default 64K mixed measurement has about
four extra runtime allocation bytes per call; this remains in raw data.
Array payload size is unchanged. These are helper-only measurements.

## Packet FFT measurements and unresolved controls

The same immutable experiment kernel DLL
`3E8B1656B064F9717B90677BAC723A9B4A734F18B6212DDA412DD882323EAD53`
was used for normal, Tiered0 and fresh controls. It is not the shipping Core
DLL. Each cell below is median paired time reduction (positive pairs / 7).
Negative data and outliers remain in the original reports.

| Scope | Normal forward | Normal inverse | Tiered0 forward | Tiered0 inverse |
| --- | --- | --- | --- | --- |
| Packet 16,384 | +18.700% (7) | +17.022% (7) | +8.670% (7) | +9.721% (7) |
| Packet 32,768 | +11.159% (7) | +10.366% (7) | +4.672% (7) | +4.808% (5) |
| Packet 65,536 control | +5.261% (7) | +3.487% (7) | -0.188% (3) | +0.351% (6) |
| Packet 1,048,576 control | +1.876% (4) | **-4.660% (0)** | -0.639% (3) | +0.783% (5) |
| Gather 16,384 / plan 128 | +20.717% (7) | +25.654% (7) | +14.605% (7) | +13.404% (7) |
| Gather 32,768 / plan 256 control | -0.350% (3) | -0.485% (3) | -0.077% (3) | +2.157% (4) |
| Gather 1,048,576 / plan 1024 control | +1.509% (6) | -1.520% (2) | +2.735% (5) | +4.095% (4) |
| Terminal 128 only | +60.896% (7) | +65.203% (7) | +64.425% (7) | +64.392% (7) |

Normal 1M inverse is a clear negative with zero positive pairs. The explicitly
authorized Tiered0 diagnostic does not erase that result. Fresh independent
normal-tiering processes then invoked only one requested packet size and
direction, with one target-only frozen preflight per version, one-second
alternating warmup, original-only 180 ms calibration and seven fixed pairs:

| Fresh scope | Paired reduction | Positive pairs | Warm packet calls/version | Original calibration calls |
| --- | --- | --- | --- | --- |
| 65,536 forward | -0.474% | 2 / 7 | 512 | 384 |
| 65,536 inverse | **-4.708%** | **0 / 7** | 452 | 384 |
| 1,048,576 forward | +4.560% | 5 / 7 | 25 | 12 |
| 1,048,576 inverse | +3.018% | 5 / 7 | 22 | 12 |

The fresh selector loads that unchanged DLL without invoking its original
Main or verification/all-size prescan. No explicit other-size warmup is
performed. Internal packet plans still necessarily execute: 64K uses 256x256,
1M uses 1024x1024. Process/heap state, driver and warm counts differ from the
sequential benchmark. Fresh 1M inverse becoming positive while fresh 64K
inverse becomes negative does not establish cross-size PGO as the sole
cause or mean the control issue is fixed.

Separate post-timing normal-tiering 64K JIT probes found normalized shared
Pass8Core instructions identical in both directions (593/600 instruction
lines). Both terminal directions use 741 code bytes, a 120-byte frame and
highest YMM12. XMM6–XMM12 saves/restores occur in Windows ABI prolog/epilog;
the loop has zero stack references and zero calls, with no observed vector
spill. These are facts about those isolated probes, not proof about
caller/layout/PGO behavior in the timed processes or universal fallback
performance. Retention still requires the real LD macro and explicit review
of the unresolved negative controls.

## Rejected magnitude clearing experiment

An isolated experiment called the actual current Core magnitude helpers
with null destination, external new storage and external uninitialized
storage. Both Complex-span and split-real/imaginary overloads used the same
actual Core magnitude body; no approximation, float32 quantization,
replacement kernel or packed Complex[] intermediate was introduced.
Normal/AVX-disabled each passed 120 strict numerical/ownership scenarios.
Seven rotating triples at 16K/32K/64K did not give stable positive feedback:

| Length | Complex-span uninitialized | Split-span uninitialized |
| --- | --- | --- |
| 16,384 | -1.10% (3 / 7 positive) | +8.15% (5 / 7) |
| 32,768 | +0.94% (4 / 7) | -0.52% (3 / 7) |
| 65,536 | -0.78% (1 / 7) | +0.46% (4 / 7) |

External-new controls were mixed too. This experiment was rejected and is
not in production; no Tiered0 repeat was used to search for a positive sign.
Allocation byte counts and all negative/outlier timings remain. RF magnitude
still computes its full block Envelope; field projection does not remove
that calculation. The split overload's `new Complex(real, imaginary)` is a
value temporary, not a per-element heap allocation.

## Thread defaults and future scope

LD's default remains four threads, including the v0.4.0 CLI Namespace
compatibility expectation. It is not an established optimum for this
machine. The positive thread value bounds RF block decode/append and TBC
sample work; loading and field-state updates remain ordered. EFM and analog
audio channels/phase2/PLL do not add a separate nested worker fan-out, and
the IPP bridge uses sequential libraries. Runtime/GC and external FFmpeg
threads are outside that per-loop bound.

No 1/4/8/16/20 throughput sweep ran in this iteration. Older 4/16-thread patch
comparisons used different frame counts, and short single-thread seek
oracles are not scaling evidence. A future immutable-binary sweep with
complete hash checks and a separately fixed paired holdout can test this
knob; no default change or additional speed claim follows from the current
evidence.

## Evidence locations

- `artifacts/ld-speed-v6/`: fixed protocol, common-source snapshot, full and
  focused normal/AVX-disabled logs/TRX, common and complete-bundle CLI
  validations, and complete fixed-protocol macro output in `ab-bundle-retry/`.
- `artifacts/ld-clip-demod/`: four strict verification configurations,
  normal/Tiered0 raw benchmarks and summaries, original hashes and logs.
- `artifacts/ld-fft-terminal128-plan/`: raw normal/Tiered0/fresh results,
  fresh-scope manifest, JIT probes, production source audit and candidacy
  document with the retained negative controls.
- `scratch/ld-envelope-projection/`: reviewed separate Envelope/GAU patches,
  source manifest, full-read overwrite proof and original fixture history.
- `artifacts/ld-magnitude-clear/`: rejected numerical verification,
  same-Core-helper allocation-placement timings and all raw signs.