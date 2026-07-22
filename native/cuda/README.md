# Optional CUDA RF compute sidecar

`vhsdecode_cuda.dll` is an optional Windows x64 acceleration component. The
managed decoder discovers it dynamically; CPU-only builds and deployments do
not need CUDA, this directory, or the DLL.

## Supported toolchain

- Windows x64
- CUDA Toolkit 13.0 Update 2 (`nvcc` 13.0.88)
- Toolkit subpackages `nvcc_13.0`, `crt_13.0`, `nvvm_13.0`, `cudart_13.0`,
  `cufft_13.0`, and `cufft_dev_13.0` (runtime-only packages are insufficient)
- CMake 3.26 or newer and Visual Studio 2022
- NVIDIA GPU with compute capability 7.5 or newer
- A driver supporting CUDA 13.0 (the release baseline is 580.95 or newer)

The build emits SASS for SM 7.5, 8.6, and 8.9 and compute_89 PTX as the
forward-compatible fallback. It dynamically links `cudart` and `cuFFT`.

## Configure and build

From this directory:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
cmake --install build --config Release --prefix package
```

If `nvcc` is absent, configuration succeeds and reports that the optional
target was skipped. To explicitly configure a CPU-only tree:

```powershell
cmake -S . -B build-cpu-only -DVHSDECODE_CUDA_ENABLE=OFF
```

The install step can copy the redistributable CUDA runtime and cuFFT runtime
artifacts from the local Toolkit. Before distributing that package, follow
[`THIRD-PARTY-NOTICES-CUDA.md`](THIRD-PARTY-NOTICES-CUDA.md) and include the
NVIDIA license files required by the CUDA 13.0 Update 2 EULA.

## ABI and execution model

The exported C ABI is versioned by `VHSDECODE_CUDA_ABI_VERSION`. Discovery,
device inspection, context creation, the 32K numerical self-test, and error
retrieval are safe to call without linking the main application to CUDA.

A context owns two nonblocking CUDA streams and a per-stream cuFFT plan cache.
Callers can keep RF batches resident in opaque device buffers through FP64
R2C/C2R/C2C transforms, frequency multiplication, Hilbert conversion,
envelope calculation, and conjugate-product FM phase extraction. Pinned host
allocations are exposed for overlapped transfers. Calls submitted to one
stream remain ordered; different streams can be used for double buffering.
The synchronous high-level RF entry point splits batches of at least two
blocks into two contiguous, ordered sub-batches and runs them concurrently on
the two streams before returning the combined outputs. Managed callers still
wait for that whole call, so host-side submit/read pipelining remains future
work.

The synchronous high-level RF batch entry point advertises its exact modes via
`vhsdecode_cuda_get_capabilities`:

- Standard conjugate-product mode implements the two-stage R2C-safe graph. The
  managed backend must first prove that every full-spectrum CPU filter can be
  represented by its Hermitian half-spectrum. PAL LD V4300D removal, demod
  clipping, IIR/chroma traps, and stateful sharpness processing stay on CPU.
  Optional LD EFM and analog-audio frequency branches reuse the uploaded R2C
  spectrum. CUDA returns FP64 pre-clamp EFM samples and normalized complex
  audio slices; the managed pipeline retains the legacy clamp/quantization,
  phase unwrap, low-frequency offset, and phase-2 behavior.
- CVBS mode implements R2C/C2R reconstruction, optional raw-to-Hz affine
  mapping, absolute-value envelope, direct video/demod outputs, and video
  low-pass. Chroma trap, notch, output roll, and burst extraction remain CPU
  work and must be capability-gated by the caller.
- VHS Rust mode is deliberately a hybrid first stage: RF filters, analytic
  real/Hilbert components, and the FP32 Rust atan/difference demodulator run on
  CUDA. It rejects envelope and second-stage video outputs because the legacy
  SOS envelope, RF high-boost feedback, diff repair, and later filters must run
  on CPU to preserve behavior.

`vhsdecode_cuda_rf_batch_execute` copies all requested outputs and synchronizes
before it returns, so managed pins are not retained by the native library.

The sidecar never silently switches to CPU. Errors are returned to the managed
backend, which decides before output creation whether `auto` may select the
unchanged CPU path. After CUDA decoding begins, an error is terminal.
