# Third-Party Notices

## pocketfft

`VHSDecode.Core.Dsp.PocketFftReal`, `PocketFftReal32`, and
`PocketFftComplex` contain C# adaptations of pocketfft's radix-2/radix-4
real-transform path and radix-2/radix-4/radix-8 complex-transform path, as
used by NumPy 2.4.6 and SciPy 1.18.0.

Copyright (C) 2010-2021 Max-Planck-Society

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## NumPy

`VHSDecode.Core.Dsp.NumpyComplex64Fft` and the NumPy-compatible magnitude
path in `VHSDecode.Core.HiFi.HiFiDropoutCompensator` contain modified C#
adaptations of numerical dispatch and SIMD behavior from NumPy 2.4.6.

Copyright (c) 2005-2025, NumPy Developers. All rights reserved.

NumPy is distributed under the BSD 3-Clause License. The complete license
text is in `third_party/licenses/numpy-LICENSE.txt`.

## SciPy

`VHSDecode.Core.Dsp.IirFilterDesign`,
`VHSDecode.Core.HiFi.SciPyPeakFinder`, and the STFT, ISTFT, temporal-filter,
and mask-convolution paths in `HiFiSpectralNoiseReduction` contain modified
C# adaptations of filter-design, peak-finding, and signal-processing behavior
from SciPy 1.18.0.

Copyright (c) 2001-2002 Enthought, Inc. 2003, SciPy Developers.
All rights reserved.

SciPy is distributed under the BSD 3-Clause License. The complete license
text is in `third_party/licenses/scipy-LICENSE.txt`.

## x86-simd-sort

`VHSDecode.Core.HiFi.NumpyAvx2ArgSort` contains a modified scalar C#
adaptation of the partition and bitonic argsort paths from Intel
x86-simd-sort commit `5adb33411f3cea8bdbafa9d91bd75bc4bf19c7dd`, as used
by NumPy 2.4.6.

Copyright (c) 2022, Intel. All rights reserved.

x86-simd-sort is distributed under the BSD 3-Clause License. The complete
license text is in `third_party/licenses/x86-simd-sort-LICENSE.md`.

## Microsoft C++ Standard Library

The exhausted-partition fallback in
`VHSDecode.Core.HiFi.NumpyAvx2ArgSort` contains a modified C# adaptation of
the insertion-sort, median partition, introsort, and heap-sort routines from
the Microsoft C++ Standard Library shipped with Visual C++ 14.44.

Copyright (c) Microsoft Corporation.

The Microsoft C++ Standard Library is distributed under the Apache License
2.0 with LLVM Exception. The complete license and exception text is in
`third_party/licenses/msvc-stl-LICENSE.txt`.

## libsoxr

`VHSDecode.Core.Dsp.SoxrQuickResampler` contains a C# adaptation of
libsoxr's quick-quality fixed-point clock and `cubic_stage_fn` interpolation
formula. The HiFi decoder also dynamically loads an unmodified Windows x64
build of libsoxr commit `a66f3eeeeb62a32403ff143b756eed92b1ec6b62`, the
revision embedded by python-soxr 1.1.0 and used by vhs-decode v0.4.0. Build
provenance, the complete corresponding source archive, and license texts are
under `third_party/libsoxr` and are copied into release output.

Copyright (c) 2007-2018 robs@users.sourceforge.net

libsoxr is free software; you can redistribute it and/or modify it under the
terms of the GNU Lesser General Public License as published by the Free
Software Foundation; either version 2.1 of the License, or (at your option)
any later version. See `third_party/libsoxr/COPYING.LGPL` for the complete
license text.
