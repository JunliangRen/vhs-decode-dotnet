# CUDA sidecar third-party notices

The `vhsdecode_cuda` sidecar dynamically links NVIDIA CUDA Runtime (`cudart`)
and NVIDIA cuFFT. These libraries are NVIDIA software and are not covered by
the repository's GPL-3.0 license.

Binary redistribution must follow the NVIDIA CUDA Toolkit End User License
Agreement and the redistribution terms installed with CUDA Toolkit 13.0
Update 2. Package only libraries listed as redistributable by that Toolkit
release, and include NVIDIA's applicable license and notice files in the
binary distribution. Do not copy CUDA Toolkit binaries into this source tree.

The sidecar itself is distributed under the repository's GPL-3.0 license.
