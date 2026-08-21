# Linux x64 release

Source repository: <https://github.com/JunliangRen/vhs-decode-dotnet>

## Supported target

The Linux package targets only 64-bit x86 Linux:

- .NET runtime identifier: `linux-x64`
- C library: glibc, with Ubuntu 22.04 / glibc 2.35 as the release-build ceiling
- deployment: self-contained, multi-file `tar.gz`
- compatibility backend: `exact`
- bundled native sidecars: `libsndfile.so`, `libsoxr.so`, and the SQLite
  `libe_sqlite3.so` asset selected by the pinned NuGet graph

The package does not target 32-bit x86, ARM/ARM64, or musl distributions. The
Windows-only `ipp-fast` and `cuda-fast` backends and their DLLs are deliberately
absent. Linux GPU acceleration is not part of this release gate. The external
FFmpeg CPU/libx264 preview fallback is exercised when FFmpeg is available.

## Runtime requirements

The .NET runtime is included, but its operating-system libraries and FFmpeg are
not. On Ubuntu 22.04, install them with:

```bash
sudo apt-get update
sudo apt-get install -y \
  ca-certificates ffmpeg libc6 libgcc-s1 libgssapi-krb5-2 libicu70 \
  libssl3 libstdc++6 tzdata zlib1g
```

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
```

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
  without OpenMP.

The resulting archive and checksum are written to `artifacts/release/`. The tar
contains the corresponding source archives, LGPL texts, source hashes, and a
build-provenance manifest.

`-AllowNewerGlibc` exists only for local functional investigation. Output built
with that switch is not certified for the Ubuntu 22.04 baseline. `-SkipTests`
and `-SkipNativeBuild` likewise produce validation artifacts, not a release
candidate.

## Release gates

The builder fails unless all of the following pass:

- host and output architecture are ELF64 x86-64 on glibc;
- app-local libsndfile/libsoxr exports and dynamic dependencies match the
  required ABI, and their maximum GLIBC symbol version does not exceed 2.35;
- the full solution builds and at least 1,551 xUnit v3 cases run after excluding
  18 method-scoped (41-case) frozen-bit oracles tied to Windows UCRT, Windows
  libsoxr, or Windows-generated transcendental inputs; functional, structural,
  tolerance, native-loader, and final-artifact gates remain enabled;
- the multi-file publish contains the Linux SQLite, libsndfile, and libsoxr
  assets, the project license, the complete application NuGet graph manifest
  and its license/notice files, the third-party license texts explicitly
  referenced by this repository, and the exact .NET and ASP.NET Core runtime
  pack licenses/notices, while containing no Windows
  sndfile/soxr/IPP/CUDA/cuFFT binaries;
- every command alias starts from the final extracted tar;
- the final extracted tar performs two one-frame PAL `exact` decodes with
  SQLite output, producing identical TBC, database, JSON, and normalized
  console/log results; the fixed input, TBC, SQLite database, and OS-normalized
  JSON are also checked against pinned SHA-256 oracles;
- two independent tar constructions have the same SHA-256.

These gates establish build, fixture, native-loader, packaging, determinism,
and synthetic Exact/SQLite coverage. They do not claim that every real RF
capture has been certified on Linux; real-capture parity remains a separate
evidence requirement.
