# Optional CUDA RF acceleration

CUDA support is a separately built Windows x64 sidecar. The normal
`decode.exe` distribution has no CUDA DLL dependency and continues to run on
systems without an NVIDIA driver or GPU.

## Release status

CUDA is currently an explicit preview component. The release policy keeps
`--compute-backend auto` on the unchanged CPU implementation until the same
representative VHS and LaserDisc captures both reach at least **1.25x**
end-to-end speedup on an RTX 4070 and pass the complete output differential
gate. A faster kernel or an unverified synthetic benchmark does not satisfy
that gate.

The package manifest therefore has `autoEligible: false`. This must not be
changed merely because the sidecar builds or its fixed-vector self-test passes.
Use `--compute-backend cuda` to opt in while qualification is still in
progress.

The preview also does not yet enable the CPU guard-band recomputation needed
to protect compatibility-sensitive threshold decisions from a near-boundary
cuFFT rounding difference. That ordered, side-effect-free preview/recompute
path and representative real-capture evidence are both required before
`autoEligible` can change. Explicit CUDA runs are qualification runs, not a
release-equivalence promise.

For batches with at least two blocks, the native high-level entry point splits
the ordered batch across stream 0 and stream 1 so the two sub-batches can
overlap transfer and computation. The managed integration still makes one
synchronous submit/wait call, however, so it cannot overlap the next host read
with the current GPU batch. A fully asynchronous submit/collect pipeline and a
measured end-to-end benefit remain performance blockers for the 1.25x `auto`
gate. This is not a correctness claim.

Before each prepared CUDA batch, the backend refreshes `cudaMemGetInfo`, keeps
at least 512 MiB or 1/16 of total VRAM in reserve, applies additional cuFFT and
fragmentation headroom, and splits the ordered work into memory-limited
sub-batches. An older ABI-v1 sidecar that does not publish the compatible
memory-information flag is conservatively limited to one block per batch.

## Runtime selection

The video commands (`vhs`, `cvbs`, and `ld`) accept:

```text
--compute-backend auto|cpu|cuda
--cuda-device N
```

- `cpu` uses the existing managed RF implementation and does not load the
  optional component.
- `cuda` requires a compatible sidecar, driver, selected device, capability
  set, parameter combination, allocation probe, and numerical self-test. A
  failed requirement is a startup error.
- `auto` is the release-policy choice. Before the performance gate is enabled,
  it chooses CPU. Once that gate is enabled in a qualified release, any CUDA
  startup or capability failure still falls back to CPU before output files
  are opened.
- `--cuda-device` defaults to visible device 0. The first supported release is
  single-GPU only.

The selected backend is fixed for the whole decode. If a kernel, cuFFT call,
allocation, or device fails after CUDA output processing starts, decoding
stops with the existing incomplete-output semantics. It never switches a
partially completed job to CPU.

Unsupported filter or state combinations are rejected as a whole during
preflight. With `auto` they select the CPU pipeline; with `cuda` they produce a
specific error. Recursive IIR/SOS filtering, synchronization and field state,
chroma field processing, and TBC resampling remain CPU work in this first
component. VHS may use a CUDA first stage while compatibility-sensitive
recursive and repair stages remain ordered on CPU.

For supported LaserDisc configurations, the EFM and analog-audio frequency
slices reuse the uploaded RF spectrum on CUDA. Their existing clamp,
quantization, phase unwrap, low-frequency offset, and phase-2 state remain
ordered managed work.

## Binary package

Install the optional archive by extracting these three DLLs beside
`decode.exe`:

```text
vhsdecode_cuda.dll
cudart64_13.dll
cufft64_12.dll
```

Do not install them into a system directory. Removing these DLLs restores the
CPU-only deployment. The NVIDIA display driver is not included.

Each archive also contains:

- `cuda-component.json`, including ABI/toolchain versions, architecture
  targets, the release-policy flag, source commit, dirty-tree flag and source
  tree SHA-256, sizes, and packaged-file SHA-256 hashes;
- the repository GPL-3.0 license for the sidecar;
- NVIDIA's CUDA Toolkit license and Toolkit version manifest; and
- the CUDA third-party notice.

CUDA binaries are generated only in the ignored `artifacts` directory and are
never committed to the source repository.

## Supported build target

- Windows x64 and one NVIDIA GPU
- CUDA Toolkit 13.0 Update 2 (SDK 13.0.2, `nvcc` 13.0.88)
- Visual Studio 2022-compatible MSVC 14.44 host tools, Ninja, and CMake 3.26
  or newer
- compute capability 7.5 or newer
- driver 580.95 or newer

The sidecar contains SASS for SM 7.5, 8.6, and 8.9 plus compute_89 PTX as the
forward-compatible fallback. RF data and filters use FP64 unless the existing
CPU stage explicitly quantizes to FP32.

## Build and verify the package

Install the CUDA Runtime and cuFFT development components, then run from the
repository root:

```powershell
pwsh -File eng/cuda/Build-CudaPackage.ps1
```

The script configures, builds, runs the native smoke/self-test (or records a
CTest skip on a machine without a GPU), creates the sidecar package, writes the
manifest, verifies every listed SHA-256, and emits the zip under
`artifacts/cuda-package`. The GPU CI passes `-RequireGpuTest`, which turns a
missing device or skipped hardware test into an error. The script deliberately
copies only the two CUDA redistributable runtime DLLs required by this
component. Packaging must continue to comply with the CUDA Toolkit EULA
installed with the pinned Toolkit release.

Useful options include:

```powershell
pwsh -File eng/cuda/Build-CudaPackage.ps1 `
  -CudaToolkitRoot 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0' `
  -BuildDirectory artifacts\cuda-native `
  -OutputDirectory artifacts\cuda-package

pwsh -File eng/cuda/Test-CudaPackage.ps1 `
  -PackagePath artifacts\cuda-package\vhs-decode-dotnet_cuda-win-x64_cuda-13.0.2
```

The independent `cuda-sidecar.yml` workflow runs only on a trusted,
self-hosted Windows x64 GPU runner labelled `cuda-13-0-u2` and `gpu`. It does
not add CUDA Toolkit or GPU requirements to the ordinary release workflow.
The GPU job covers native build/self-test, the managed CUDA fault-injection
tests, package verification, and artifact upload. Real-capture field-aligned
differential and end-to-end performance reports remain mandatory release
evidence outside that smoke job.

## CPU/CUDA decode qualification harness

`eng/cuda/Compare-CudaDecode.ps1` runs the same decode twice, changing only
the selected backend and each run's isolated output basename. For example:

```powershell
pwsh -File eng/cuda/Compare-CudaDecode.ps1 `
  -DecodeExecutable artifacts/release/decode.exe `
  -DecodeCommand vhs `
  -InputFile D:\captures\sample.lds `
  -OutputDirectory artifacts\cuda-qualification `
  -AdditionalArguments @('--threads', '20', '--length', '100') `
  -CudaDevice 0
```

`DecodeCommand` can be `vhs`, `cvbs`, or `ld` when the facade `decode.exe` is
used. Leave it empty for a standalone command executable. Arguments that set
`--compute-backend` or `--cuda-device` are rejected because the harness owns
those options. Input and argument paths are passed through .NET's process
argument list, so quoting is preserved without building a shell command.

Every invocation creates a new
`qualification-<UTC timestamp>-<unique id>` directory. It never removes or
overwrites an earlier run. Decoder outputs are separated under `outputs/cpu`
and `outputs/cuda`; captured stdout/stderr and GPU samples are under the
matching `diagnostics` directories.

The generated `qualification.json` records:

- the input SHA-256, executable, exact argument vectors, exit codes, and UTC
  start/completion times;
- size and SHA-256 for `decode.exe` and every adjacent CUDA sidecar, sampled
  before and after both runs so a replaced binary invalidates qualification;
- wall time, main decoder process CPU time, and main decoder peak working set;
- the single-run `cpuWall / cudaWall` speedup as an informational sample. It
  records whether the number crosses 1.25x, but is explicitly ineligible for a
  release decision without repeated cold/warm representative VHS and LD runs;
- periodic NVIDIA GPU utilization and used/total VRAM summaries, plus the raw
  per-GPU CSV samples when `nvidia-smi` is available;
- SHA-256 and size for every recursively discovered decoder output; and
- raw match, mismatch, or missing status for every CPU/CUDA relative output
  path, together with normalized equality for decoder `.log` files; and
- little-endian 16-bit sample count, differing-sample count/rate, and maximum
  absolute LSB difference for `.tbc`, `_chroma.tbc`, `.pcm`, `.efm`, and
  `.tbc.ldf`. TBC/chroma samples are UInt16; PCM, EFM, and LD RF-TBC samples are
  Int16; and
- Float32 normalized maximum error, NRMSE, and NaN/Inf layout equality for the
  main `.tbc` output when `--export_raw_tbc` is selected, plus ordered
  `fileLoc`-aligned JSON metadata and SQLite logical schema/row comparisons.

CPU time and peak working set cover the main `decode` process only. Wall time
includes time spent waiting for FFmpeg or other child processes and is the
authoritative end-to-end duration. Raw SHA-256 records are retained for decoder
`.log`, stdout, and stderr, alongside normalized SHA-256 and equality.
Normalization is limited to line-leading timestamps, the exact
`RF compute backend selected:` detail, and the decoder's known final
`Took ... seconds to decode ... frames (... FPS post-setup)`, `Completed in`,
and `Elapsed time` statistics. Generic duration, FPS, and sample-rate text is
not normalized because it may describe source or dropout semantics. Normalized
console files are retained beside their raw captures for inspection.

The harness sets `qualificationPassed` only when both processes exit zero;
the executable/sidecar artifact fingerprint remains unchanged and all three
required CUDA DLLs plus `cuda-component.json` are adjacent to the executable,
and the manifest's ABI/source identity and DLL size/SHA-256 entries validate;
the input size/SHA-256 fingerprint remains unchanged across both runs;
each process emits at least one backend-selection diagnostic and reports the
backend that was actually requested; at least one common non-log decoder output
is non-empty; standard `vhs`, `cvbs`, and `ld` runs contain a non-empty main
`<output>.tbc`; `.log`, stdout, and stderr are raw-identical or differ solely by
those explicit normalization rules; and every output passes its implemented
comparison. A CUDA request that silently ran CPU cannot qualify. The current
artifact-bound report schema is version 4.
Final integer `.tbc`, `_chroma.tbc`, `.pcm`, `.efm`, and `.tbc.ldf` outputs may
differ only when byte lengths are equal, both files contain complete
little-endian 16-bit samples in their declared format, the maximum absolute
difference is at most 1 LSB, and the differing-sample fraction is at most
`0.0001` (0.01%). The comparator treats TBC/chroma as UInt16 and PCM, EFM, and
LD RF-TBC as Int16, including across their respective signedness boundaries.
Raw hashes remain in the report even when an engineering or logical tolerance
is the qualification basis. Raw Float32 TBC uses the selected FP32 limits
(`2e-6` normalized maximum absolute error and `2e-7` NRMSE), with equal length
and identical NaN/Inf positions. JSON comparison requires equal root metadata,
field count, field order, `fileLoc` sequence, and recursively canonicalized
field metadata. SQLite comparison requires equal `user_version`, logical schema,
column metadata, table set, and per-table logical row multisets. Other binary
outputs require exact size and SHA-256.

`qualificationPassed` still is not the complete release gate.
`qualification.json` reports implemented comparison coverage as complete while
keeping `releaseGateCoverage.complete: false` and
`fullReleaseGatePassed: false`. The remaining blockers are the CPU near-threshold
guard-band/recompute path and an aggregated representative real-capture VHS and
LD suite in which both families pass compatibility and reach at least 1.25x.

The normalization and failure boundary have a self-contained fake decoder
regression:

```powershell
pwsh -File eng/cuda/tests/Test-CompareCudaDecode.ps1
```

It verifies strict diagnostic normalization, actual backend proof, required
non-empty output evidence, executable/sidecar enforcement, stable input
fingerprints, accepted and rejected integer/Float32 tolerances,
format-insensitive but `fileLoc`-ordered JSON equality, SQLite physical-layout
independence, and rejection of changed JSON fields, field order, logical DB
rows, semantic duration messages, empty streams/inventories, and non-integer
payloads.

## Compatibility gate

The fixed-vector startup self-test uses the engineering thresholds selected
for FP64 (`1e-9` normalized maximum absolute error and `1e-11` NRMSE). The
complete release gate must additionally compare CPU and CUDA captures aligned
by `fileLoc`, including integer TBC/chroma/audio output, field count/order,
skip/recovery decisions, dropout/VBI metadata, JSON, SQLite logical rows, and
timestamp-normalized diagnostics. The harness now implements those output
comparisons, but the CPU near-threshold recomputation path and representative
real-capture evidence remain mandatory. Passing a startup self-test or one
qualification report alone is not a complete output-compatibility claim.
