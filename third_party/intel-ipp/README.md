# Intel IPP provenance

Windows uses the pinned NuGet package described in `THIRD-PARTY-NOTICES.md`.
Linux x64 builds use Intel's official APT packages, downloaded and extracted
without installing oneAPI into the build machine. Exact URLs, versions and
SHA-256 hashes are in `tools/linux-ipp-packages.json`.

`tools/build-ipp-native-linux.ps1` verifies the downloaded bytes before
extraction and links the PIC sequential `libipps.a`, `libippvm.a` and
`libippcore.a` archives into `libvhsdecode_ipp.so`. It hides vendor exports and
requires all symbols to resolve at link time. The shipped bridge needs the
system C++ runtime; it has no oneAPI, IPP, OpenMP or TBB shared dependency.

The Linux package version is `2026.0.1-51`; its IPP runtime-info reports
`2026.0.0`. These are separately recorded and checked. The original licensing
package's `license.htm`, IPP package's `third-party-programs.txt`, this
repository's `LICENSE.txt`, build manifest and ELF audit are staged beside the
bridge and included under `licenses/intel-ipp/` in the final archive.

Linux releases are built on Ubuntu 22.04. Every shipped ELF is audited against
GLIBC 2.34, GLIBCXX 3.4.29, CXXABI 1.3.13 and GCC 4.2.0 ceilings. The complete
tar is then run on multiple glibc distributions. These checks establish the
project's package contract separately from Intel's supported compiler and OS
matrix: [Intel IPP 2026 release notes](https://www.intel.com/content/www/us/en/developer/articles/release-notes/ipp/2026.html).
