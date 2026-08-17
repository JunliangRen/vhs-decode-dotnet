#include "vhsdecode_cuda_fast.h"

#include <cstdio>
#include <cstring>

int main() {
    if (vhsdecode_cuda_fast_get_abi_version()
        != VHSDECODE_CUDA_FAST_ABI_VERSION) {
        std::fprintf(stderr, "CUDA-fast ABI mismatch.\n");
        return 1;
    }

    vhsdecode_cuda_fast_runtime_info_v1 info{};
    info.struct_size = sizeof(info);
    const int32_t status = vhsdecode_cuda_fast_get_runtime_info(-1, &info);
    if (status == VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE) {
        std::printf("CUDA-fast ABI loaded; no CUDA device is available.\n");
        return 0;
    }
    if (status != VHSDECODE_CUDA_FAST_STATUS_OK) {
        std::fprintf(
            stderr,
            "CUDA-fast probe failed: %s\n",
            vhsdecode_cuda_fast_get_last_error());
        return 2;
    }
    if (info.abi_version != VHSDECODE_CUDA_FAST_ABI_VERSION
        || info.device_name[0] == '\0'
        || info.total_vram_bytes == 0) {
        std::fprintf(stderr, "CUDA-fast returned incomplete runtime information.\n");
        return 3;
    }

    std::printf(
        "CUDA-fast: %s, compute %d.%d, %llu MiB VRAM\n",
        info.device_name,
        info.compute_major,
        info.compute_minor,
        static_cast<unsigned long long>(info.total_vram_bytes / (1024 * 1024)));
    return 0;
}
