# LaserDisc performance validation — 2026-11-04

Baseline: `ff03b9b` on `main`. Windows x64, .NET 11 RC2
(`11.0.0-rc.2.26504.105`), 20 logical processors, Intel IPP 2026.0.0.
Both versions used the same native IPP bridge and Release build settings.
Input: `D:\BmdFiles\DDD\10e_lxm_side1.ldf` (75,165,807,979 bytes), NTSC,
with analog audio, EFM, dropout detection, AGC and automatic MTF enabled.

## Measured decode time

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

## Regression checks

- Release build: zero warnings/errors.
- Focused LD/FFT/direct-conversion suite: 192 passed, zero skipped or failed.
- Complete suite with IPP required: 2,218 passed, 15 platform/hardware skips,
  zero failures (2,233 total).
- 24 added cases cover frozen PLL output and chunk boundaries, FFT ownership,
  signed zeros and nonfinite payloads, audio allocation/bit parity, direct
  rendering, retained field payloads, verbose VITS, recovery and EOF.

Local evidence is retained below ignored `artifacts/ld-speed/`: baseline and
optimized binaries, `environment.json` with their hashes, CPU sampling traces,
`ab-final-default/{runs,summary}.json`, `ab-final-16threads/{runs,summary}.json`,
logs and complete decode artifacts.
`benchmark.py` retains the before/after harness; for example:

```powershell
python artifacts/ld-speed/benchmark.py --before artifacts/ld-speed/baseline/decode.exe --after artifacts/ld-speed/candidate-final/decode.exe --input D:\BmdFiles\DDD\10e_lxm_side1.ldf --output artifacts/ld-speed/new-run --frames 120 --pairs 5 --threads 4
```

These timings describe this machine and bounded capture ranges, rather than a
whole-side decode or a guarantee for other captures, operating systems or CPUs.
