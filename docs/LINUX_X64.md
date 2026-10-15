# Linux x64 release

Source repository: <https://github.com/JunliangRen/vhs-decode-dotnet>

## Supported target

The Linux package targets only 64-bit x86 Linux:

- .NET runtime identifier: `linux-x64`
- C library: glibc; built on Ubuntu 22.04, with every shipped ELF requiring
  GLIBC 2.34 or older, GLIBCXX 3.4.29 or older, CXXABI 1.3.13 or older, and
  GCC 4.2.0 or older
- deployment: self-contained, multi-file `tar.gz`
- default compatibility backend: `exact`; additional VHS/LD backend: `ipp-fast`
- bundled native sidecars: `libsndfile.so`, `libsoxr.so`, and the SQLite
  `libe_sqlite3.so` asset selected by the pinned NuGet graph, plus
  `libvhsdecode_ipp.so`

The package does not target 32-bit x86, ARM/ARM64, or musl distributions. The
`cuda-fast` backend and Windows DLLs are absent. Linux GPU acceleration is not
part of this release gate. The external
FFmpeg CPU/libx264 preview fallback is exercised when FFmpeg is available.

The package is independent of Ubuntu's package manager. CI runs the same final
tar on Ubuntu 22.04, Debian 12/13, Fedora 44 and AlmaLinux 9, including IPP
initialization, VHS/LD RF decode, and Approx provider selection. AlmaLinux 9
supplies the RHEL 9 family compatibility check; this is not a separate RHEL
hardware certification. RHEL 8's glibc 2.28 is below this package's baseline.
Alpine/musl and ARM64 require separate native builds. NixOS needs a compatible
loader environment such as `nix-ld` or a Nix wrapper and has no dedicated gate.

## Runtime requirements

The .NET runtime is included, but its operating-system libraries and FFmpeg are
not. On Ubuntu 22.04, install them with:

```bash
sudo apt-get update
sudo apt-get install -y \
  ca-certificates ffmpeg libc6 libgcc-s1 libgssapi-krb5-2 libicu70 \
  libssl3 libstdc++6 tzdata zlib1g
```

On Fedora or RHEL 9 family systems, the corresponding runtime libraries are
`glibc`, `libgcc`, `libstdc++`, `libicu`, `openssl-libs`, `krb5-libs`, `zlib`,
`ca-certificates`, and `tzdata` (install with `dnf`). FFmpeg availability varies
by distribution and enabled repositories. On Arch Linux the corresponding
packages include `glibc`, `gcc-libs`, `icu`, `openssl`, `krb5`, `zlib`,
`ca-certificates`, `tzdata`, and `ffmpeg`; Arch has no dedicated CI gate.

Both `ffmpeg` and `ffprobe` must be available on `PATH`. They remain necessary
for container inputs and fallback paths outside the narrowly gated direct raw
FLAC reader. `VHSDECODE_FFMPEG` and `VHSDECODE_FFPROBE` can select explicit
binaries. Ubuntu 22.04's FFmpeg/ffprobe 4.4.2 is the minimum release-gate
version. Because FFmpeg's audio conversion details can change between versions,
the gate compares old FFmpeg output sample-for-sample with an independent full
decode and frame-geometry reconstruction; FFmpeg 8.x additionally retains the
Release 4.0 frozen hashes. For preview video, an older ffprobe stream-level
`field_order=unknown` is accepted only when every decoded frame reports
`interlaced_frame=0`.

Do not enable invariant globalization as a substitute for ICU. The final-tar
gate intentionally starts the self-contained application with normal ICU-backed
globalization.

## Install and run

Verify the adjacent checksum, extract the archive, and run from the extracted
directory:

```bash
sha256sum -c vhs-decode-dotnet-linux-x64.tar.gz.sha256
tar -xzf vhs-decode-dotnet-linux-x64.tar.gz
cd vhs-decode-dotnet-linux-x64

./vhs-decode [upstream options] input.lds output
./cvbs-decode [upstream options] input.lds output
./ld-decode [upstream options] input.lds output
./hifi-decode [upstream options] input.lds output.flac
```

The `decode` facade is also included, for example:

```bash
./decode vhs --pal --dsp-backend exact input.lds output
./decode vhs --pal --dsp-backend ipp-fast input.lds output-ipp
./decode ld --pal --dsp-backend ipp-fast input.lds output-ld
```

IPP is linked into the app-local bridge using PIC sequential static libraries;
users do not install oneAPI or configure `LD_LIBRARY_PATH`. The system C++
runtime remains a dependency. IPP needs an x86-64 CPU with SSE4.2 support, and
the loader reports an explicit failure if an explicitly selected IPP backend
cannot initialize. Normal decode still defaults to Exact. `approx-fast`
automatically selects its IPP provider when available; use
`--approx-provider managed` to select managed float32 transforms. Automatic
CPU preview also selects IPP when available; `--dsp-backend exact` pins Exact.
IPP and Exact have a numerical compatibility contract, not a bit-for-bit one.

Keep the extracted files together. This is intentionally a multi-file package;
moving only an apphost such as `vhs-decode` will leave its managed runtime and
native sidecars behind.

## Reproduce the package

Use Ubuntu 22.04 x64 with the pinned .NET SDK from `global.json`, PowerShell 7,
and these build tools:

```bash
sudo apt-get update
sudo apt-get install -y \
  build-essential ca-certificates cmake ninja-build pkg-config python3 xz-utils \
  binutils ffmpeg libicu-dev

pwsh ./tools/build-linux-x64-release.ps1
```

The builder downloads only fixed source archives, verifies every SHA-256, and
builds:

- libogg 1.3.5, libvorbis 1.3.7, opus 1.4, and FLAC 1.4.2 as PIC static
  libraries linked into libsndfile;
- unmodified libsndfile 1.2.2 as an app-local shared library, with MPEG support
  disabled;
- unmodified libsoxr commit
  `a66f3eeeeb62a32403ff143b756eed92b1ec6b62` as an app-local shared library,
  without OpenMP;
- the C++ IPP bridge from the SHA-256 pinned Intel APT packages in
  `tools/linux-ipp-packages.json`, with no IPP, OpenMP, or TBB shared dependency.

The resulting archive and checksum are written to `artifacts/release/`. The tar
contains the corresponding source archives, LGPL texts, source hashes, and a
build-provenance manifest.
It also includes the original Intel license, third-party notices, package and
bridge hashes, and separate package/library version records. The Linux package
`2026.0.1-51` reports IPP library version `2026.0.0`; this is checked at runtime.

`-AllowNewerGlibc` exists only for local functional investigation. Output built
with that switch is not certified for the Ubuntu 22.04 baseline. `-SkipTests`
and `-SkipNativeBuild` likewise produce validation artifacts, not a release
candidate.

## Release gates

The builder fails unless all of the following pass:

- host and output architecture are ELF64 x86-64 on glibc;
- all shipped ELF files, including .NET and SQLite, satisfy the GLIBC,
  GLIBCXX, CXXABI and GCC ceilings above; required imports resolve, and RPATH/RUNPATH
  cannot contain a build-machine or current-directory search path;
- IPP exports match the complete C ABI, runtime-info layout is 240 bytes,
  initialization and CPU dispatch succeed, and only allowed system libraries
  remain as dynamic dependencies;
- the full solution builds and at least 1,857 xUnit v3 cases run after excluding
  18 method-scoped (41-case) frozen-bit oracles tied to Windows UCRT, Windows
  libsoxr, or Windows-generated transcendental inputs; functional, structural,
  tolerance, native-loader, and final-artifact gates remain enabled;
- `VHSDECODE_REQUIRE_IPP=1` makes missing IPP fail the suite instead of silently
  skipping native tests; loader fixtures exercise missing exports, wrong ABI,
  wrong structure size, runtime/initialization rejection, invalid ELF, and
  nonfatal warnings, alongside FFT, IIR/SOS, state, concurrency and DSP tests;
- the multi-file publish contains the Linux SQLite, libsndfile, libsoxr and
  IPP assets, the project license, the complete application NuGet graph manifest
  and its license/notice files, the third-party license texts explicitly
  referenced by this repository, and the exact .NET and ASP.NET Core runtime
  pack licenses/notices, while containing no Windows
  sndfile/soxr/IPP/CUDA/cuFFT binaries;
- every command alias starts from the final extracted tar;
- that same tar performs complete synthetic PAL VHS/LD Exact and IPP decodes,
  with deterministic repeated IPP output, compatible field geometry and bounded
  pixel differences; automatic/explicit IPP and explicit managed Approx
  providers are exercised from an unrelated directory containing spaces, with
  SDK and oneAPI loader overrides removed;
- the final extracted tar performs two one-frame PAL `exact` decodes with
  SQLite output, producing identical TBC, JSON, and normalized console/log results;
  databases must pass integrity checks and have identical schemas (including
  user_version) and canonical SQL exports. Fixed input, TBC, OS-normalized JSON,
  database schema and SQL-export digests are checked against pinned SHA-256
  oracles derived from the released fixture; database file bytes may differ;
- two independent tar constructions have the same SHA-256.

These gates establish build, fixture, native-loader, packaging, determinism,
and synthetic Exact/IPP/SQLite coverage. They do not claim that every real RF
capture has been certified on Linux; real-capture parity remains a separate
evidence requirement.

The synthetic VHS fixture gates active-picture luminance at a maximum of 2 IRE
and RMS of 0.05 IRE, with separate whole-raster limits (8192 code values maximum
and 0.1 IRE RMS). IPP's existing FFT/SOS evaluation differences can change an
HSync decision at an RF block boundary; whole-raster pixel identity is therefore
not its contract. Metadata must match and repeated IPP luminance, chroma and JSON
must be identical. SQLite schema and canonical SQL contents must match, with
successful integrity checks; physical database bytes may differ. Chroma byte count must match the field
geometry. This fixture has no color burst and does not certify real color quality.

CoreCLR's optional LTTng tracing provider is audited against the same ABI
ceilings. Its `liblttng-ust.so.0` dependency may be absent and is explicitly
recorded in `ELF-ABI.json`; the runtime skips that provider when unavailable.
Decoding and EventPipe diagnostics do not require LTTng. See the pinned
[CoreCLR provider loader](https://github.com/dotnet/runtime/blob/v11.0.0-rc.2.26504.105/src/coreclr/pal/src/misc/tracepointprovider.cpp).

## Process cleanup and crash diagnostics

FFmpeg, ffprobe, and LD AC3 tools use .NET 11 `KillOnParentExit` on Linux and
Windows. If the decoder terminates unexpectedly, its directly launched tools
also terminate. Preview cancellation terminates the process tree while the
launcher is still alive, including workers started by configured FFmpeg or
ffprobe wrappers. Normal RF seek and decode completion retain their existing
behavior.

To collect .NET 11 in-process crash reports on Linux, set these variables
before starting the decoder:

```bash
export DOTNET_EnableCrashReport=1
export DOTNET_CrashReportRootPath="$(pwd)"
./decode vhs --ntsc -f 20 input.flac output
```

The root path must be an existing writable absolute directory. RC2 writes
reports under `$DOTNET_CrashReportRootPath/.dotnet/crash-reports/` using
`report-*.crashreport.json` names, and also writes diagnostic information to
stderr. These reports describe fatal runtime failures; ordinary command errors
continue to use the existing console/log diagnostics. Enabling
`DOTNET_DbgEnableMiniDump` selects the existing `createdump` path instead.
Windows continues to use its existing crash diagnostics.

Linux CI enables the reporter before launching .NET, collects available crash
reports, and preserves TRX test results. Both release test runs have a 15-minute
execution timeout and use the SDK's `per-module` results layout. The additional
process fixtures cover parent death, cancellation, pipe EOF, concurrent stdout
and stderr capture, Unix signal termination, and structured Linux crash reports.
See [.NET 11 runtime diagnostics](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime#in-process-crash-report-logging)
and the [RC2 report lifecycle implementation](https://github.com/dotnet/runtime/blob/v11.0.0-rc.2.26504.105/src/coreclr/debug/crashreport/inproccrashreportlifecycle.cpp).
