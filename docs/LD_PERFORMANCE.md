# LaserDisc performance validation — 2026-11-04

Baseline: `ff03b9b` on `main`. Windows x64, .NET 11 RC2
(`11.0.0-rc.2.26504.105`), 20 logical processors, Intel IPP 2026.0.0.
Both versions used the same native IPP bridge and Release build settings.
Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf` (75,165,807,979 bytes), NTSC,
with analog audio, EFM, dropout detection, AGC and automatic MTF enabled.

## Complete change: repeated continuous decode

With the kernel/buffer changes and guarded indexed fallback together, one
warm-up pair followed by five alternating measured pairs gave:

| Threads | Frames | Backend | Baseline median | Optimized median | Median paired time reduction |
| --- | --- | --- | --- | --- | --- |
| 4 (CLI default) | 120 | exact | 21.023 s | 16.872 s | 20.03% |
| 4 (CLI default) | 120 | ipp-fast | 15.993 s | 12.224 s | 23.74% |

Every measured pair improved: exact reductions ranged from 16.15% to 22.57%,
IPP from 21.73% to 25.38%. All 24 processes produced 240 fields. Complete
`.tbc`, `.pcm`, `.efm`, `.tbc.json` and `.tbc.db` SHA-256 hashes matched both
their original baseline and the other backend in this range.

## Kernel/buffer checkpoint (`f4b153f`)

One warm-up pair followed by five measured pairs per backend, alternating
before/after order. Each process decodes the same frame range. Full process
wall time includes initialization, initial sync recovery and output writing;
hashing happens after the timed process exits.

| Threads | Frames | Backend | Baseline median | Optimized median | Median paired time reduction |
| --- | --- | --- | --- | --- | --- |
| 4 (CLI default) | 120 | exact | 20.657 s | 17.118 s | 16.52% |
| 4 (CLI default) | 120 | ipp-fast | 14.963 s | 12.689 s | 15.52% |
| 16 | 80 | exact | 11.739 s | 9.994 s | 12.75% |
| 16 | 80 | ipp-fast | 10.116 s | 8.891 s | 9.74% |

All five measured pairs improved in each configuration. Every warm-up and
measured run produced the requested field count. Within each backend, SHA-256 matched the baseline for
the complete `.tbc`, `.pcm`, `.efm`, `.tbc.json` and `.tbc.db` files.
The default-thread 120-frame range also produced identical complete files
between exact and IPP, including video samples and metadata.

## Indexed fallback for the sample's wrapped FLAC length

The capture declares 190,840,832 samples in STREAMINFO, while validated tail
frame positions give 107,565,023,232 samples. The declared value is the actual
count modulo 2^32. The original reader uses native reads up to the declared
boundary and then starts its legacy FFmpeg fallback; distant starts can spend
most of their time reaching the requested input position.

LD now chooses its fallback lazily. Only fixed-block mono PCM16/40 kHz FLAC
with complete metadata, no seek table, this exact 32-bit wrap signature, and
successful tail checksum/decoder checks uses the existing indexed FFmpeg path.
Other inputs retain their existing fallback. Native prefix reads and PyAV
restart coordinates are preserved, including overlap, rewind and restart
history. The source capture is unchanged.

Eleven independent PCM read sequences matched every byte. An end-to-end
single-thread exact decode starting at frame 10,000 and writing 20 frames
dropped from approximately 92.75 s to 6.40 s with identical baseline outputs
and timestamp-normalized diagnostics. This is one bounded seek comparison,
separate from the repeated continuous-decode benchmark.

Additional complete-decode comparisons cover frame 1,000, frame 10,000,
`verboseVITS`, `noAGC` with RF-TBC/cubic interpolation/wow smoothing, and a
synthetic PAL capture, on both backends. All match the original baseline;
exact and IPP also match each other in these ranges. RF-TBC's Ogg/FLAC
container has per-encode stream serials, so its decoded PCM hash is compared.

## Retained changes

- Cache immutable DUCC packet twiddles and rooted FFT plans, preserving the
  existing arithmetic and zero-index bypasses. The 32K plan retains an extra
  512 KiB table shared by workers.
- Reuse dead, block-local RF spectrum storage for video, lowpass and reference
  inverse transforms. Disposable EFM/audio spectra also transform in place.
- Reuse the existing two-slot RF span pool for synchronous LD field reads.
  `verboseVITS` retains independently owned raw arrays through its existing path.
- Use the existing direct sinc-to-UInt16 field renderer where its processing
  guards permit it, avoiding the full-field double intermediate.
- Keep PLL scan state in locals and use an equivalent signed crossing test.
- Build short audio FFT padding directly in complex storage and transform it
  in place; use the existing bit-compatible complex multiplication kernel.
- Use the guarded indexed fallback above for LD captures with wrapped FLAC
  sample counts, including a bounded tail decode to retain legacy behavior on
  malformed tails whose CRC residue alone happens to pass.

## Regression checks

- Release build: zero warnings/errors.
- Focused LD/FFT/direct-conversion suite: 192 passed, zero skipped or failed.
- I/O-focused suite after adding the indexed fallback: 121 passed, zero skipped
  or failed.
- Complete suite with IPP required: 2,224 passed, 15 platform/hardware skips,
  zero failures (2,239 total).
- 24 added cases cover frozen PLL output and chunk boundaries, FFT ownership,
  signed zeros and nonfinite payloads, audio allocation/bit parity, direct
  rendering, retained field payloads, verbose VITS, recovery and EOF.
- Six further cases cover fallback eligibility/lifetime/error propagation,
  native length transitions, truncated bodies and a missing final zero CRC byte.

Local evidence is retained below ignored `artifacts/ld-speed/`: baseline and
optimized binaries, `environment.json` with their hashes, CPU sampling traces,
`ab-final-default/{runs,summary}.json`, `ab-final-16threads/{runs,summary}.json`,
`ab-indexed-default/{runs,summary}.json`, `validation-indexed/runs.json`,
logs and complete decode artifacts.
`benchmark.py` retains the before/after harness; for example:

```powershell
python artifacts/ld-speed/benchmark.py --before artifacts/ld-speed/baseline/decode.exe --after artifacts/ld-speed/candidate-indexed/decode.exe --input D:\BmdFiles\DDD\10e_lxm_side1.ldf --output artifacts/ld-speed/new-run --frames 120 --pairs 5 --threads 4
```

These timings describe this machine and bounded capture ranges, rather than a
whole-side decode or a guarantee for other captures, operating systems or CPUs.
