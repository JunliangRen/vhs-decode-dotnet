# libsndfile 1.2.2

`src/VHSDecode.Core/runtimes/win-x64/native/sndfile.dll` is the unmodified
64-bit DLL from the official libsndfile 1.2.2 Windows release:

- release: https://github.com/libsndfile/libsndfile/releases/tag/1.2.2
- archive: `libsndfile-1.2.2-win64.zip`
- archive SHA-256:
  `2173935c0c1ed13cf627951d34483f9d405ead2eb473190461c42ba220643a3f`
- DLL SHA-256:
  `4e3bd2de8e1485110eaebef8e1239471f73d608773831c323bf528e05645655e`
- corresponding source:
  https://github.com/libsndfile/libsndfile/tree/1.2.2

The application dynamically loads this DLL to write HiFi PCM24 FLAC without
starting FFmpeg. It applies the same `SFC_SET_CLIPPING` and compression-level
commands as Python SoundFile before writing samples. A 10,000-sample
deterministic PCM24 gate is identical to the pinned Python environment. The
release build reports libsndfile 1.2.2, reference libFLAC 1.4.2, libopus 1.4,
and LAME 3.100. The distributed DLL also contains support code for formats the
application does not request. Corresponding upstream sources and license
information are available from:

- libFLAC: https://github.com/xiph/flac
- LAME: https://sourceforge.net/p/lame/svn/HEAD/tree/
- mpg123: https://www.mpg123.de/trunk/
- libogg: https://github.com/xiph/ogg
- libopus: https://github.com/xiph/opus
- libvorbis: https://github.com/xiph/vorbis

libsndfile is licensed under LGPL 2.1 or later. `COPYING.LGPL` contains the
complete license text. libFLAC, libogg, libopus, and libvorbis use BSD-style
licenses for the linked library components; LAME and mpg123 use LGPL terms.
