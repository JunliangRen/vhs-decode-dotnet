# libsndfile 1.2.2

The Windows package uses
`src/VHSDecode.Core/runtimes/win-x64/native/sndfile.dll`, the unmodified 64-bit
DLL from the official libsndfile 1.2.2 Windows release:

- release: https://github.com/libsndfile/libsndfile/releases/tag/1.2.2
- archive: `libsndfile-1.2.2-win64.zip`
- archive SHA-256:
  `2173935c0c1ed13cf627951d34483f9d405ead2eb473190461c42ba220643a3f`
- DLL SHA-256:
  `4e3bd2de8e1485110eaebef8e1239471f73d608773831c323bf528e05645655e`
- corresponding source:
  https://github.com/libsndfile/libsndfile/tree/1.2.2

The glibc `linux-x64` package builds the unmodified libsndfile 1.2.2 source
archive and deploys it as an app-local `libsndfile.so`. The release builder
verifies this source archive SHA-256:

`3799ca9924d3125038880367bf1468e53a1b7e3686a934f098b7e1d286cdb80e`.

That Linux shared library statically links PIC builds of libogg 1.3.5,
libvorbis 1.3.7, opus 1.4, and FLAC 1.4.2. MPEG support is disabled, so the
Linux sidecar does not link LAME or mpg123. The release tar carries all five
verified source archives and a source-hash manifest.

The application dynamically loads the platform library to write HiFi PCM24
FLAC without starting FFmpeg. It applies the same `SFC_SET_CLIPPING` and
compression-level commands as Python SoundFile before writing samples. A
10,000-sample deterministic PCM24 gate is identical to the pinned Python
environment. The official Windows build reports libsndfile 1.2.2, reference
libFLAC 1.4.2, libopus 1.4, and LAME 3.100. That DLL also contains support code
for formats the application does not request. Corresponding upstream sources
and license information are available from:

- libFLAC: https://github.com/xiph/flac
- LAME: https://sourceforge.net/p/lame/svn/HEAD/tree/
- mpg123: https://www.mpg123.de/trunk/
- libogg: https://github.com/xiph/ogg
- libopus: https://github.com/xiph/opus
- libvorbis: https://github.com/xiph/vorbis

libsndfile is licensed under LGPL 2.1 or later. `COPYING.LGPL` contains the
complete license text. libFLAC, libogg, libopus, and libvorbis use BSD-style
licenses for the linked library components; LAME and mpg123 use LGPL terms.
