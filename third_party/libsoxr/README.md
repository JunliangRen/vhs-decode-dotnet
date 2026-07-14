# Bundled libsoxr

The Windows x64 `soxr.dll` used by the HiFi decoder is built without source
changes from:

- Repository: https://github.com/dofuuz/soxr
- Commit: `a66f3eeeeb62a32403ff143b756eed92b1ec6b62`
- Reported version: `libsoxr-0.1.3`
- DLL SHA-256: `1708DC9BEB53B050EFCF77C5F758DC23658B055BA79ABB01C2D8DB872ECA6BBE`

This is the same libsoxr revision embedded by python-soxr 1.1.0, which is used
by vhs-decode v0.4.0. The library was built with Visual Studio 2026 using:

```powershell
cmake -S . -B build-vs -G "Visual Studio 18 2026" -A x64 `
  -DBUILD_SHARED_LIBS=ON `
  -DBUILD_TESTS=OFF `
  -DBUILD_LSR_TESTS=OFF `
  -DWITH_OPENMP=OFF
cmake --build build-vs --config Release --target soxr
```

`soxr-a66f3ee-source.zip` is a complete `git archive` of that revision. Its
SHA-256 is
`D43F523965810AB91337D86DA2ECBFFAAEDA0001609E48A14BB1FCC8E864D4D2`.
The application loads `soxr.dll` dynamically, so recipients can rebuild and
replace the library without rebuilding the managed decoder.

See `LICENCE` and `COPYING.LGPL` for the applicable LGPL 2.1-or-later terms.
