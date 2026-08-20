cmake_minimum_required(VERSION 3.26)

project(VHSDecodeCudaFast LANGUAGES CXX CUDA)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CUDA_STANDARD 17)
set(CMAKE_CUDA_STANDARD_REQUIRED ON)

if(NOT DEFINED CMAKE_CUDA_ARCHITECTURES)
    set(CMAKE_CUDA_ARCHITECTURES "75-real;80-real;86-real;89-real;89-virtual")
endif()

find_package(CUDAToolkit 13.0 REQUIRED)
include(FetchContent)

set(VHSDECODE_CUVHS_COMMIT "c55e72073f44b27e8839efb842e4345af39887f7"
    CACHE STRING "Pinned cuVHS source commit")
set(VHSDECODE_CUVHS_SOURCE_DIR "" CACHE PATH
    "Existing cuVHS source checkout; when empty the pinned source is fetched")

if(VHSDECODE_CUVHS_SOURCE_DIR)
    get_filename_component(cuvhs_SOURCE_DIR "${VHSDECODE_CUVHS_SOURCE_DIR}" ABSOLUTE)
    if(NOT EXISTS "${cuvhs_SOURCE_DIR}/src/pipeline/pipeline.cu")
        message(FATAL_ERROR "VHSDECODE_CUVHS_SOURCE_DIR is not a cuVHS source tree: ${cuvhs_SOURCE_DIR}")
    endif()
else()
    FetchContent_Declare(cuvhs
        GIT_REPOSITORY https://github.com/hunterschuler/cuVHS.git
        GIT_TAG ${VHSDECODE_CUVHS_COMMIT}
        EXCLUDE_FROM_ALL)
    FetchContent_MakeAvailable(cuvhs)
endif()

set(VHSDECODE_NV_CODEC_HEADERS_COMMIT
    "1889e62e2d35ff7aa9baca2bceb14f053785e6f1"
    CACHE STRING "Pinned nv-codec-headers source commit")
set(VHSDECODE_NV_CODEC_HEADERS_SOURCE_DIR "" CACHE PATH
    "Existing nv-codec-headers checkout; when empty the pinned source is fetched")

if(VHSDECODE_NV_CODEC_HEADERS_SOURCE_DIR)
    get_filename_component(
        nv_codec_headers_SOURCE_DIR
        "${VHSDECODE_NV_CODEC_HEADERS_SOURCE_DIR}"
        ABSOLUTE)
    if(NOT EXISTS
        "${nv_codec_headers_SOURCE_DIR}/include/ffnvcodec/nvEncodeAPI.h")
        message(FATAL_ERROR
            "VHSDECODE_NV_CODEC_HEADERS_SOURCE_DIR is not an nv-codec-headers source tree: ${nv_codec_headers_SOURCE_DIR}")
    endif()
else()
    FetchContent_Declare(nv_codec_headers
        GIT_REPOSITORY https://github.com/FFmpeg/nv-codec-headers.git
        GIT_TAG ${VHSDECODE_NV_CODEC_HEADERS_COMMIT}
        EXCLUDE_FROM_ALL)
    FetchContent_MakeAvailable(nv_codec_headers)
endif()

function(vhsdecode_cuda_fast_convert_pipeline_to_fp32 variable_name)
    set(source_text "${${variable_name}}")

    # cuda-fast owns a single-precision GPU data plane. Keep file/sample
    # positions in size_t/uint64_t, but convert RF samples, line-local
    # coordinates, filters, FFT storage, and CUDA arithmetic to FP32.
    string(REPLACE "cufftDoubleComplex" "cufftComplex" source_text "${source_text}")
    string(REPLACE "cufftDoubleReal" "cufftReal" source_text "${source_text}")
    string(REPLACE "cufftExecD2Z" "cufftExecR2C" source_text "${source_text}")
    string(REPLACE "cufftExecZ2D" "cufftExecC2R" source_text "${source_text}")
    string(REPLACE "cufftExecZ2Z" "cufftExecC2C" source_text "${source_text}")
    string(REPLACE "CUFFT_D2Z" "CUFFT_R2C" source_text "${source_text}")
    string(REPLACE "CUFFT_Z2D" "CUFFT_C2R" source_text "${source_text}")
    string(REPLACE "CUFFT_Z2Z" "CUFFT_C2C" source_text "${source_text}")
    string(REPLACE "double" "float" source_text "${source_text}")

    # MSVC's complex templates require both operands to have the same scalar
    # type. These host-side filter-design expressions inherit unsuffixed
    # literals from the double implementation, so make the FP32 intent
    # explicit after the type conversion.
    string(REPLACE
        "src[lo] * (1.0 - frac) + src[hi] * frac"
        "src[lo] * (1.0f - frac) + src[hi] * frac"
        source_text
        "${source_text}")
    string(REPLACE "std::polar(1.0, theta)" "std::polar(1.0f, theta)" source_text "${source_text}")
    string(REPLACE
        "in.k * std::pow(wo, degree)"
        "in.k * static_cast<float>(std::pow(wo, degree))"
        source_text
        "${source_text}")
    string(REPLACE
        "in.k * std::pow(bw, degree)"
        "in.k * static_cast<float>(std::pow(bw, degree))"
        source_text
        "${source_text}")
    string(REPLACE "(2.0 * c[0])" "(2.0f * c[0])" source_text "${source_text}")
    string(REPLACE
        "const std::vector<float> a{1.0, 1.0 / (Ap * q_value), 1.0};"
        "const std::vector<float> a{1.0f, 1.0f / (Ap * q_value), 1.0f};"
        source_text
        "${source_text}")
    string(REPLACE
        "const std::complex<float> fs2{2.0 * fs, 0.0};"
        "const std::complex<float> fs2{2.0f * fs, 0.0f};"
        source_text
        "${source_text}")
    string(REPLACE
        "std::max(1.0, interlaced_detected)"
        "std::max(1.0f, interlaced_detected)"
        source_text
        "${source_text}")

    # Wall-clock reporting is control-plane data and should not lose range or
    # resolution merely because the signal data plane is single precision.
    string(REPLACE
        "std::chrono::duration<float>"
        "std::chrono::duration<double>"
        source_text
        "${source_text}")
    set(${variable_name} "${source_text}" PARENT_SCOPE)
endfunction()

function(vhsdecode_cuda_fast_generate_fp32_source output_path input_path)
    file(READ "${input_path}" generated_source)
    vhsdecode_cuda_fast_convert_pipeline_to_fp32(generated_source)
    file(WRITE "${output_path}" "${generated_source}")
endfunction()

function(vhsdecode_cuda_fast_protect_fm_filter_design variable_name)
    set(source_text "${${variable_name}}")
    set(first_kernel_marker "__global__ void k_copy_and_pad")
    set(init_marker "bool FMDemodState::init")
    set(destroy_marker "void FMDemodState::destroy")
    string(FIND "${source_text}" "${first_kernel_marker}" first_kernel_index)
    string(FIND "${source_text}" "${init_marker}" init_index)
    string(FIND "${source_text}" "${destroy_marker}" destroy_index)
    if(first_kernel_index EQUAL -1 OR init_index EQUAL -1 OR destroy_index EQUAL -1)
        message(FATAL_ERROR "Pinned cuVHS FM source no longer has the expected precision boundaries")
    endif()

    string(SUBSTRING "${source_text}" 0 ${first_kernel_index} design_prefix)
    math(EXPR kernel_length "${init_index} - ${first_kernel_index}")
    string(SUBSTRING "${source_text}" ${first_kernel_index} ${kernel_length} gpu_kernels)
    math(EXPR init_length "${destroy_index} - ${init_index}")
    string(SUBSTRING "${source_text}" ${init_index} ${init_length} init_source)
    string(SUBSTRING "${source_text}" ${destroy_index} -1 remaining_source)

    # The high-order Butterworth/ZPK coefficient construction is numerically
    # ill-conditioned in FP32. Keep only this one-time host-side design in
    # double, then explicitly quantize the finished arrays for the GPU.
    string(REPLACE "double" "VHSDECODE_CUDA_FAST_FILTER_FP64" design_prefix "${design_prefix}")
    string(REPLACE
        "cufftDoubleComplex"
        "VHSDECODE_CUDA_FAST_CUFFT_COMPLEX"
        init_source
        "${init_source}")
    string(REPLACE "double" "VHSDECODE_CUDA_FAST_FILTER_FP64" init_source "${init_source}")
    string(REPLACE
        "VHSDECODE_CUDA_FAST_CUFFT_COMPLEX"
        "cufftDoubleComplex"
        init_source
        "${init_source}")

    set(${variable_name}
        "${design_prefix}${gpu_kernels}${init_source}${remaining_source}"
        PARENT_SCOPE)
endfunction()

function(vhsdecode_cuda_fast_finish_fm_mixed_precision variable_name)
    set(source_text "${${variable_name}}")
    string(REPLACE
        "VHSDECODE_CUDA_FAST_FILTER_FP64"
        "double"
        source_text
        "${source_text}")
    string(REPLACE
        "std::polar(1.0f, theta)"
        "std::polar(1.0, theta)"
        source_text
        "${source_text}")
    string(REPLACE
        "in.k * static_cast<float>(std::pow(wo, degree))"
        "in.k * std::pow(wo, degree)"
        source_text
        "${source_text}")
    string(REPLACE
        "in.k * static_cast<float>(std::pow(bw, degree))"
        "in.k * std::pow(bw, degree)"
        source_text
        "${source_text}")

    set(env_coefficients [=[    first_order_lowpass_coeffs(fs, 700000.0, env_b0, env_b1, env_a1, env_zi);]=])
    string(FIND "${source_text}" "${env_coefficients}" env_coefficients_index)
    if(env_coefficients_index EQUAL -1)
        message(FATAL_ERROR "Pinned cuVHS FM init no longer has the expected envelope coefficient call")
    endif()
    string(REPLACE
        "${env_coefficients}"
        [=[    double env_b0_fp64 = 0.0;
    double env_b1_fp64 = 0.0;
    double env_a1_fp64 = 0.0;
    double env_zi_fp64 = 0.0;
    first_order_lowpass_coeffs(
        fs, 700000.0, env_b0_fp64, env_b1_fp64, env_a1_fp64, env_zi_fp64);
    env_b0 = static_cast<float>(env_b0_fp64);
    env_b1 = static_cast<float>(env_b1_fp64);
    env_a1 = static_cast<float>(env_a1_fp64);
    env_zi = static_cast<float>(env_zi_fp64);]=]
        source_text
        "${source_text}")

    set(filter_upload [=[    cudaMalloc(&d_rf_filter, freq_bins * sizeof(double));
    cudaMemcpy(d_rf_filter, h_rf.data(), freq_bins * sizeof(double), cudaMemcpyHostToDevice);
    cudaMalloc(&d_env_rf_filter, env_freq_bins * sizeof(double));
    cudaMemcpy(d_env_rf_filter, env_h_rf.data(), env_freq_bins * sizeof(double), cudaMemcpyHostToDevice);
    cudaMalloc(&d_burst_filter, env_freq_bins * sizeof(double));
    cudaMemcpy(d_burst_filter, h_burst.data(), env_freq_bins * sizeof(double), cudaMemcpyHostToDevice);]=])
    string(FIND "${source_text}" "${filter_upload}" filter_upload_index)
    if(filter_upload_index EQUAL -1)
        message(FATAL_ERROR "Pinned cuVHS FM init no longer has the expected filter upload block")
    endif()
    string(REPLACE
        "${filter_upload}"
        [=[    const std::vector<float> h_rf_fp32(h_rf.begin(), h_rf.end());
    const std::vector<float> env_h_rf_fp32(env_h_rf.begin(), env_h_rf.end());
    const std::vector<float> h_burst_fp32(h_burst.begin(), h_burst.end());
    cudaMalloc(&d_rf_filter, freq_bins * sizeof(float));
    cudaMemcpy(d_rf_filter, h_rf_fp32.data(), freq_bins * sizeof(float), cudaMemcpyHostToDevice);
    cudaMalloc(&d_env_rf_filter, env_freq_bins * sizeof(float));
    cudaMemcpy(d_env_rf_filter, env_h_rf_fp32.data(), env_freq_bins * sizeof(float), cudaMemcpyHostToDevice);
    cudaMalloc(&d_burst_filter, env_freq_bins * sizeof(float));
    cudaMemcpy(d_burst_filter, h_burst_fp32.data(), env_freq_bins * sizeof(float), cudaMemcpyHostToDevice);]=]
        source_text
        "${source_text}")

    string(REPLACE
        "(size_t)max_batch * (spf + 12) * sizeof(double)"
        "(size_t)max_batch * (spf + 12) * sizeof(float)"
        source_text
        "${source_text}")
    string(REPLACE
        "(size_t)max_batch * fs * sizeof(double)"
        "(size_t)max_batch * fs * sizeof(float)"
        source_text
        "${source_text}")
    string(REPLACE
        "(size_t)max_batch * env_fft_size * sizeof(double)"
        "(size_t)max_batch * env_fft_size * sizeof(float)"
        source_text
        "${source_text}")

    # cuda-fast never enters the host K4 source-reconstruction fallback. Its
    # legacy helpers are tied to double host buffers, so omit their definitions
    # instead of retaining a second full CPU signal path in this backend.
    set(cpu_burst_marker "bool demod_burst_cpu(const float* raw,")
    string(FIND "${source_text}" "${cpu_burst_marker}" cpu_burst_index)
    if(cpu_burst_index EQUAL -1)
        message(FATAL_ERROR "Pinned cuVHS FM source no longer has the expected CPU burst fallback")
    endif()
    string(SUBSTRING "${source_text}" 0 ${cpu_burst_index} gpu_source)
    string(SUBSTRING "${source_text}" ${cpu_burst_index} -1 cpu_burst_source)
    set(source_text "${gpu_source}#if 0  // cuda-fast has no host K4 fallback\n${cpu_burst_source}\n#endif\n")
    set(${variable_name} "${source_text}" PARENT_SCOPE)
endfunction()

function(vhsdecode_cuda_fast_protect_chroma_filter_design variable_name)
    set(source_text "${${variable_name}}")

    # The high-order ZPK construction is numerically ill-conditioned in FP32,
    # just like the FM filter design.  Keep only its small, one-time host-side
    # calculation in double.  Geometry, level adjustment, debug helpers, GPU
    # buffers, kernels, and FFTs remain FP32.
    set(type_start_marker "static const double PI_LOCAL")
    set(type_end_marker "static int next_pow2")
    string(FIND "${source_text}" "${type_start_marker}" type_start_index)
    string(FIND "${source_text}" "${type_end_marker}" type_end_index)
    if(type_start_index EQUAL -1 OR type_end_index EQUAL -1 OR type_end_index LESS_EQUAL type_start_index)
        message(FATAL_ERROR "Pinned cuVHS chroma source no longer has the expected filter type boundary")
    endif()
    string(SUBSTRING "${source_text}" 0 ${type_start_index} before_types)
    math(EXPR type_length "${type_end_index} - ${type_start_index}")
    string(SUBSTRING "${source_text}" ${type_start_index} ${type_length} filter_types)
    string(SUBSTRING "${source_text}" ${type_end_index} -1 after_types)
    string(REPLACE "double" "VHSDECODE_CUDA_FAST_CHROMA_FILTER_FP64" filter_types "${filter_types}")
    set(source_text "${before_types}${filter_types}${after_types}")

    set(design_start_marker "static DigitalZpkLocal buttap_zpk_local")
    set(design_end_marker "// Butterworth BPF magnitude")
    string(FIND "${source_text}" "${design_start_marker}" design_start_index)
    string(FIND "${source_text}" "${design_end_marker}" design_end_index)
    if(design_start_index EQUAL -1 OR design_end_index EQUAL -1 OR design_end_index LESS_EQUAL design_start_index)
        message(FATAL_ERROR "Pinned cuVHS chroma source no longer has the expected filter design boundary")
    endif()
    string(SUBSTRING "${source_text}" 0 ${design_start_index} before_design)
    math(EXPR design_length "${design_end_index} - ${design_start_index}")
    string(SUBSTRING "${source_text}" ${design_start_index} ${design_length} filter_design)
    string(SUBSTRING "${source_text}" ${design_end_index} -1 after_design)
    string(REPLACE "double" "VHSDECODE_CUDA_FAST_CHROMA_FILTER_FP64" filter_design "${filter_design}")
    set(${variable_name} "${before_design}${filter_design}${after_design}" PARENT_SCOPE)
endfunction()

function(vhsdecode_cuda_fast_finish_chroma_mixed_precision variable_name)
    set(source_text "${${variable_name}}")
    string(REPLACE
        "VHSDECODE_CUDA_FAST_CHROMA_FILTER_FP64"
        "double"
        source_text
        "${source_text}")
    string(REPLACE
        "std::polar(1.0f, theta)"
        "std::polar(1.0, theta)"
        source_text
        "${source_text}")
    string(REPLACE
        "in.k * static_cast<float>(std::pow(bw, degree))"
        "in.k * std::pow(bw, degree)"
        source_text
        "${source_text}")
    set(${variable_name} "${source_text}" PARENT_SCOPE)
endfunction()

file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline")

# Generate a complete, internally consistent FP32 view of the pipeline
# interfaces. The original pinned source remains untouched and Exact/IPP never
# include these headers.
set(CUVHS_FP32_PIPELINE_HEADERS
    fm_demod.h
    sync_pulses.h
    line_locs.h
    hsync_refine.h
    tbc_resample.h
    chroma_decode.h
    vsync_discover.h
    pipeline.h)
foreach(header_name IN LISTS CUVHS_FP32_PIPELINE_HEADERS)
    vhsdecode_cuda_fast_generate_fp32_source(
        "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline/${header_name}"
        "${cuvhs_SOURCE_DIR}/src/pipeline/${header_name}")
endforeach()

# PAL fields do not share the same first output line: Exact starts the first
# field at line 3 and the second field at line 4. Pass the already detected
# parity into K5 instead of using cuVHS's fixed line-1 origin.
set(CUVHS_TBC_HEADER_PATH
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline/tbc_resample.h")
file(READ "${CUVHS_TBC_HEADER_PATH}" CUVHS_TBC_HEADER_SOURCE)
set(CUVHS_TBC_HEADER_GEOMETRY [=[                  const float* d_linelocs,
                  const float* d_level_adjust,]=])
string(FIND
    "${CUVHS_TBC_HEADER_SOURCE}"
    "${CUVHS_TBC_HEADER_GEOMETRY}"
    CUVHS_TBC_HEADER_GEOMETRY_INDEX)
if(CUVHS_TBC_HEADER_GEOMETRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC header no longer has the expected geometry parameters")
endif()
string(REPLACE
    "${CUVHS_TBC_HEADER_GEOMETRY}"
    [=[                  const float* d_linelocs,
                  const int* d_is_first_field,
                  const float* d_level_adjust,]=]
    CUVHS_TBC_HEADER_SOURCE
    "${CUVHS_TBC_HEADER_SOURCE}")
file(WRITE "${CUVHS_TBC_HEADER_PATH}" "${CUVHS_TBC_HEADER_SOURCE}")

set(CUVHS_PIPELINE_HEADER_PATH
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline/pipeline.h")
file(READ "${CUVHS_PIPELINE_HEADER_PATH}" CUVHS_PIPELINE_HEADER_SOURCE)
set(CUVHS_PIPELINE_VECTOR_INCLUDE "#include <vector>")
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_VECTOR_INCLUDE}"
    CUVHS_PIPELINE_VECTOR_INCLUDE_INDEX)
if(CUVHS_PIPELINE_VECTOR_INCLUDE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected vector include")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_VECTOR_INCLUDE}"
    [=[#include <thread>
#include <vector>]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
string(REPLACE
    "float64"
    "FP32"
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
set(CUVHS_PIPELINE_CONSTRUCTOR_DECL [=[    Pipeline(const GPUDevice& gpu, const VideoFormat& fmt,
             RawReader& reader, TBCWriter& writer);]=])
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_CONSTRUCTOR_DECL}"
    CUVHS_PIPELINE_CONSTRUCTOR_DECL_INDEX)
if(CUVHS_PIPELINE_CONSTRUCTOR_DECL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected constructor")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_CONSTRUCTOR_DECL}"
    [=[    Pipeline(const GPUDevice& gpu, const VideoFormat& fmt,
             RawReader& reader, TBCWriter& writer,
             unsigned int maximum_output_fields);]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
set(CUVHS_PIPELINE_BATCH_STATE "    int batch_size = 0;")
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_BATCH_STATE}"
    CUVHS_PIPELINE_BATCH_STATE_INDEX)
if(CUVHS_PIPELINE_BATCH_STATE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected batch state")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_BATCH_STATE}"
    [=[    int batch_size = 0;
    unsigned int maximum_output_fields = 0;]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
set(CUVHS_PIPELINE_PARITY_STATE
    "    int k2b_prev_first_field = -1;")
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_PARITY_STATE}"
    CUVHS_PIPELINE_PARITY_STATE_INDEX)
if(CUVHS_PIPELINE_PARITY_STATE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected parity state")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_PARITY_STATE}"
    [=[    int k2b_prev_first_field = -1;
    int k2b_startup_hsync_streak = 0;
    bool output_have_parity = false;
    bool output_last_parity = false; // per-decode writer cadence state]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
set(CUVHS_PIPELINE_RAW_BUFFER_STATE
    "    void* d_raw = nullptr;         // raw input samples (batch)")
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_RAW_BUFFER_STATE}"
    CUVHS_PIPELINE_RAW_BUFFER_STATE_INDEX)
if(CUVHS_PIPELINE_RAW_BUFFER_STATE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected raw buffer state")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_RAW_BUFFER_STATE}"
    [=[    void* d_raw = nullptr;         // FP32 raw input samples (batch)
    void* d_raw_s16 = nullptr;     // optional native-rate PCM16 staging
    void* d_raw_source_f32 = nullptr; // optional native-rate FP32 staging
    std::vector<int16_t> h_half_rate_raw_s16;
    std::vector<float> h_half_rate_raw_f32;

    // The next seekable PCM16 batch is read while the current batch finishes
    // its GPU and output stages. This remains pageable memory because the
    // measured page-locked variant regressed end-to-end throughput.
    std::thread raw_prefetch_thread;
    int16_t* h_prefetch_raw_s16 = nullptr;
    size_t h_prefetch_capacity_samples = 0;
    size_t prefetch_raw_offset = 0;
    int prefetch_num_fields = 0;
    size_t prefetch_lead_samples = 0;
    size_t prefetch_total_samples = 0;
    size_t prefetch_tail_samples = 0;
    size_t prefetch_n_read = 0;
    size_t prefetch_tail_read = 0;
    size_t prefetch_half_rate_logical_samples_read = 0;
    bool prefetch_half_rate = false;
    bool prefetch_half_rate_read_ok = false;
    bool prefetch_valid = false;]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
set(CUVHS_PIPELINE_FREE_DECL "    void free_buffers();")
string(FIND
    "${CUVHS_PIPELINE_HEADER_SOURCE}"
    "${CUVHS_PIPELINE_FREE_DECL}"
    CUVHS_PIPELINE_FREE_DECL_INDEX)
if(CUVHS_PIPELINE_FREE_DECL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline header no longer has the expected free declaration")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_FREE_DECL}"
    [=[    void free_buffers();
    void start_raw_prefetch(size_t raw_offset, int num_fields);
    void stop_raw_prefetch();]=]
    CUVHS_PIPELINE_HEADER_SOURCE
    "${CUVHS_PIPELINE_HEADER_SOURCE}")
file(WRITE "${CUVHS_PIPELINE_HEADER_PATH}" "${CUVHS_PIPELINE_HEADER_SOURCE}")

# Cache the expensive field-wide chroma response in the pipeline-owned state.
# This is host control data, not part of the signal data plane, and survives
# across GPU batches of the same decode.
set(CUVHS_CHROMA_HEADER_PATH
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline/chroma_decode.h")
file(READ "${CUVHS_CHROMA_HEADER_PATH}" CUVHS_CHROMA_HEADER_SOURCE)
set(CUVHS_CHROMA_STATE_TAIL
    "    int prev_quadrant = -1;         // burst phase quadrant from last field (for phase delta)")
string(FIND
    "${CUVHS_CHROMA_HEADER_SOURCE}"
    "${CUVHS_CHROMA_STATE_TAIL}"
    CUVHS_CHROMA_STATE_TAIL_INDEX)
if(CUVHS_CHROMA_STATE_TAIL_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS chroma state no longer has the expected cache insertion point")
endif()
string(REPLACE
    "${CUVHS_CHROMA_STATE_TAIL}"
    [=[    int prev_quadrant = -1;         // burst phase quadrant from last field (for phase delta)
    int final_bpf_size = 0;
    std::vector<float> final_bpf_response; // cached host FP32 response for this format
    void* workspace = nullptr;             // persistent opaque GPU workspace]=]
    CUVHS_CHROMA_HEADER_SOURCE
    "${CUVHS_CHROMA_HEADER_SOURCE}")
string(REPLACE
    [=[};

// Kernel 6: Chroma Decode]=]
    [=[};

void chroma_state_release(ChromaState* state);

// Kernel 6: Chroma Decode]=]
    CUVHS_CHROMA_HEADER_SOURCE
    "${CUVHS_CHROMA_HEADER_SOURCE}")
set(CUVHS_CHROMA_DECODE_DECLARATION "void chroma_decode(")
string(FIND
    "${CUVHS_CHROMA_HEADER_SOURCE}"
    "${CUVHS_CHROMA_DECODE_DECLARATION}"
    CUVHS_CHROMA_DECODE_DECLARATION_INDEX)
if(CUVHS_CHROMA_DECODE_DECLARATION_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma header no longer has the expected decode declaration")
endif()
string(REPLACE
    "${CUVHS_CHROMA_DECODE_DECLARATION}"
    "bool chroma_decode("
    CUVHS_CHROMA_HEADER_SOURCE
    "${CUVHS_CHROMA_HEADER_SOURCE}")
file(WRITE "${CUVHS_CHROMA_HEADER_PATH}" "${CUVHS_CHROMA_HEADER_SOURCE}")
vhsdecode_cuda_fast_generate_fp32_source(
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include/pipeline/dropout_detect.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay/pipeline/dropout_detect.h")

file(READ "${cuvhs_SOURCE_DIR}/src/pipeline/fm_demod.cu" CUVHS_FM_DEMOD_SOURCE)
# The pinned source has trailing horizontal whitespace in this kernel. Normalize
# that non-semantic whitespace so exact overlay guards work identically with
# LF and CRLF checkouts.
string(REGEX REPLACE
    "[ \t]+\r\n"
    "\r\n"
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REGEX REPLACE
    "[ \t]+\n"
    "\n"
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    "static const double TAU = 2.0 * PI;"
    "static constexpr double TAU = 6.28318530717958647692;"
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

# cuVHS's generic PAL defaults are intentionally broad and differ materially
# from the PAL VHS profile used by the Exact backend.  Keep NTSC/MPAL's
# upstream overrides, but make the PAL base response follow the checked-in
# format profile: no aggregate band-pass, the same RF skirts, ramp, peaking,
# and post-demodulation low-pass.
set(CUVHS_GENERIC_PAL_FILTER_DEFAULTS [=[    double bpf_low          = 1300000.0;
    double bpf_high         = 7500000.0;
    int    bpf_order        = 8;
    double lpf_extra_freq   = 8000000.0;
    int    lpf_extra_ord    = 8;
    double hpf_extra_freq   = 1000000.0;
    int    hpf_extra_ord    = 8;
    double video_lpf_freq   = 3000000.0;
    int    video_lpf_order  = 9;
    bool   use_rf_ramp      = false;
    double ramp_start_freq  = 0.0;
    double ramp_boost_0     = 0.0;
    double ramp_boost_20    = 1.0;
    bool   ramp_double      = false;
    bool   use_rf_peak      = false;
    double rf_peak_freq     = 0.0;
    double rf_peak_gain     = 0.0;
    double rf_peak_bw       = 0.0;]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_GENERIC_PAL_FILTER_DEFAULTS}"
    CUVHS_GENERIC_PAL_FILTER_DEFAULTS_INDEX)
if(CUVHS_GENERIC_PAL_FILTER_DEFAULTS_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM demod source no longer has the expected PAL filter defaults")
endif()
string(REPLACE
    "${CUVHS_GENERIC_PAL_FILTER_DEFAULTS}"
    [=[    double bpf_low          = 1300000.0;
    double bpf_high         = 5780000.0;
    int    bpf_order        = 0;
    double lpf_extra_freq   = 5910000.0;
    int    lpf_extra_ord    = 20;
    double hpf_extra_freq   = 1320000.0;
    int    hpf_extra_ord    = 12;
    double video_lpf_freq   = 3400000.0;
    int    video_lpf_order  = 6;
    bool   use_rf_ramp      = true;
    double ramp_start_freq  = fmt.chroma_under;
    double ramp_boost_0     = 0.0;
    double ramp_boost_20    = 1.0;
    bool   ramp_double      = false;
    bool   use_rf_peak      = true;
    double rf_peak_freq     = 4300000.0;
    double rf_peak_gain     = 4.0;
    double rf_peak_bw       = 10000000.0;]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

# Upstream's analytic-difference kernel overwrites each sample while the next
# thread can still read it.  Use the already allocated full analytic buffer as
# an out-of-place destination so scheduling cannot change spike replacement.
set(CUVHS_ANALYTIC_DIFF_KERNEL [=[__global__ void k_diff_analytic_inplace(cufftDoubleComplex* analytic, int fft_size, int total_fields) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = total_fields * fft_size;
    if (idx >= total) return;
    int pos = idx % fft_size;
    cufftDoubleComplex out{};
    if (pos != 0) {
        cufftDoubleComplex cur = analytic[idx];
        cufftDoubleComplex prev = analytic[idx - 1];
        out.x = cur.x - prev.x;
        out.y = cur.y - prev.y;
    }
    analytic[idx] = out;
}]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_ANALYTIC_DIFF_KERNEL}"
    CUVHS_ANALYTIC_DIFF_KERNEL_INDEX)
if(CUVHS_ANALYTIC_DIFF_KERNEL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM demod source no longer has the expected in-place analytic difference kernel")
endif()
string(REPLACE
    "${CUVHS_ANALYTIC_DIFF_KERNEL}"
    [=[__global__ void k_diff_analytic(
    const cufftDoubleComplex* analytic,
    cufftDoubleComplex* difference,
    int fft_size,
    int total_fields)
{
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = total_fields * fft_size;
    if (idx >= total) return;
    int pos = idx % fft_size;
    cufftDoubleComplex out{};
    if (pos != 0) {
        cufftDoubleComplex cur = analytic[idx];
        cufftDoubleComplex prev = analytic[idx - 1];
        out.x = cur.x - prev.x;
        out.y = cur.y - prev.y;
    }
    difference[idx] = out;
}]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_ANALYTIC_DIFF_CALL [=[        k_diff_analytic_inplace<<<blocks, T>>>(state.d_kept_analytic, spf, num_fields);
        k_compute_angles<<<blocks, T>>>(state.d_kept_analytic, state.d_angle_work, total_trimmed);]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_ANALYTIC_DIFF_CALL}"
    CUVHS_ANALYTIC_DIFF_CALL_INDEX)
if(CUVHS_ANALYTIC_DIFF_CALL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM demod source no longer has the expected analytic difference call")
endif()
string(REPLACE
    "${CUVHS_ANALYTIC_DIFF_CALL}"
    [=[        k_diff_analytic<<<blocks, T>>>(
            state.d_kept_analytic, state.d_analytic, spf, num_fields);
        k_compute_angles<<<blocks, T>>>(state.d_analytic, state.d_angle_work, total_trimmed);]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
vhsdecode_cuda_fast_protect_fm_filter_design(CUVHS_FM_DEMOD_SOURCE)
vhsdecode_cuda_fast_convert_pipeline_to_fp32(CUVHS_FM_DEMOD_SOURCE)
vhsdecode_cuda_fast_finish_fm_mixed_precision(CUVHS_FM_DEMOD_SOURCE)
string(REPLACE
    [=[        while (delta > TAU) delta -= TAU;

        out[i] = delta * scale;]=]
    [=[        while (delta > TAU) delta -= TAU;
        out[i] = delta * scale;]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

# Phase differences are sample-local; the upstream kernel assigned an entire
# ~850k-sample field to one CUDA thread.  Map one output sample to one thread
# while retaining the exact same arithmetic and field-local indexing.
set(CUVHS_SERIAL_UNWRAP_KERNEL [=[__global__ void k_unwrap_to_hz(
    const float* angles, float* demod, int fft_size, int samples_per_field,
    float freq_hz, int num_fields, int overlap)
{
    int field = blockIdx.x * blockDim.x + threadIdx.x;
    if (field >= num_fields) return;

    const float* a = angles + (size_t)field * fft_size;
    float* out = demod + (size_t)field * samples_per_field;

    float scale = freq_hz / TAU;

    for (int i = 0; i < samples_per_field; i++) {
        int a_idx = overlap + i;
        if (a_idx <= 0) {
            out[i] = 0.0;
            continue;
        }
        float delta = a[a_idx] - a[a_idx - 1];

        while (delta < 0.0) delta += TAU;
        while (delta > TAU) delta -= TAU;
        out[i] = delta * scale;
    }
}]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_SERIAL_UNWRAP_KERNEL}"
    CUVHS_SERIAL_UNWRAP_KERNEL_INDEX)
if(CUVHS_SERIAL_UNWRAP_KERNEL_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS FM source no longer has the expected serial unwrap kernel")
endif()
string(REPLACE
    "${CUVHS_SERIAL_UNWRAP_KERNEL}"
    [=[__global__ void k_unwrap_to_hz_serial(
    const float* angles, float* demod, int fft_size, int samples_per_field,
    float freq_hz, int num_fields, int overlap)
{
    const int field = blockIdx.x * blockDim.x + threadIdx.x;
    if (field >= num_fields) return;

    const float* field_angles = angles + static_cast<size_t>(field) * fft_size;
    float* field_demod = demod + static_cast<size_t>(field) * samples_per_field;
    const float scale = freq_hz / TAU;
    for (int sample = 0; sample < samples_per_field; ++sample) {
        const int angle_index = overlap + sample;
        if (angle_index <= 0) {
            field_demod[sample] = 0.0f;
            continue;
        }

        float delta = field_angles[angle_index] - field_angles[angle_index - 1];
        while (delta < 0.0f) delta += TAU;
        while (delta > TAU) delta -= TAU;
        field_demod[sample] = delta * scale;
    }
}

__global__ void k_unwrap_to_hz_parallel(
    const float* angles, float* demod, int fft_size, int samples_per_field,
    float freq_hz, int num_fields, int overlap)
{
    const int index = blockIdx.x * blockDim.x + threadIdx.x;
    const int total = num_fields * samples_per_field;
    if (index >= total) return;

    const int field = index / samples_per_field;
    const int sample = index - field * samples_per_field;
    const int angle_index = overlap + sample;
    if (angle_index <= 0) {
        demod[index] = 0.0f;
        return;
    }

    const float* field_angles = angles + static_cast<size_t>(field) * fft_size;
    const float scale = freq_hz / TAU;
    float delta = field_angles[angle_index] - field_angles[angle_index - 1];
    while (delta < 0.0f) delta += TAU;
    while (delta > TAU) delta -= TAU;
    demod[index] = delta * scale;
}]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

# The RF-envelope filtfilt is a first-order affine recurrence.  Upstream runs
# both ~850k-sample passes on one thread per field.  Split each direction into
# chunks: summarize each chunk in parallel, scan the small chunk-state array,
# then apply each chunk from its correct entry state.  The old kernel remains
# available behind an internal environment switch for same-binary A/B gates.
string(REPLACE
    [=[__global__ void k_unwrap_to_hz_serial(]=]
    [=[constexpr int ENV_IIR_CHUNK_SAMPLES = 1024;

__global__ void k_prepare_env_extended(
    const float* __restrict__ raw_env,
    float* __restrict__ extended,
    int spf,
    int ext_len,
    int total_fields,
    int roll_shift)
{
    const int index = blockIdx.x * blockDim.x + threadIdx.x;
    const int total = total_fields * ext_len;
    if (index >= total) return;

    constexpr int edge = 6;
    const int field = index / ext_len;
    const int sample = index - field * ext_len;
    const float* field_input = raw_env + static_cast<size_t>(field) * spf;
    extended[index] = odd_ext_value_rolled(field_input, spf, edge, sample, roll_shift);
}

__global__ void k_summarize_env_iir_chunks(
    const float* __restrict__ samples,
    float* __restrict__ chunk_gains,
    float* __restrict__ chunk_biases,
    int samples_per_field,
    int chunks_per_field,
    int total_fields,
    float b0,
    float b1,
    float a1,
    bool reverse)
{
    const int global_chunk = blockIdx.x * blockDim.x + threadIdx.x;
    const int total_chunks = total_fields * chunks_per_field;
    if (global_chunk >= total_chunks) return;

    const int field = global_chunk / chunks_per_field;
    const int chunk = global_chunk - field * chunks_per_field;
    const int begin = chunk * ENV_IIR_CHUNK_SAMPLES;
    const int end = min(begin + ENV_IIR_CHUNK_SAMPLES, samples_per_field);
    const float* field_samples = samples + static_cast<size_t>(field) * samples_per_field;
    const float pole = -a1;
    float gain = 1.0f;
    float state = 0.0f;
    for (int logical = begin; logical < end; ++logical) {
        const int sample = reverse ? (samples_per_field - 1 - logical) : logical;
        const float input = field_samples[sample];
        const float output = b0 * input + state;
        state = b1 * input - a1 * output;
        gain *= pole;
    }

    chunk_gains[global_chunk] = gain;
    chunk_biases[global_chunk] = state;
}

__global__ void k_scan_env_iir_chunk_states(
    const float* __restrict__ samples,
    const float* __restrict__ chunk_gains,
    const float* __restrict__ chunk_biases,
    float* __restrict__ chunk_states,
    int samples_per_field,
    int chunks_per_field,
    int total_fields,
    float zi_base,
    bool reverse)
{
    const int field = blockIdx.x * blockDim.x + threadIdx.x;
    if (field >= total_fields) return;

    const size_t field_base = static_cast<size_t>(field) * samples_per_field;
    const int first_sample = reverse ? (samples_per_field - 1) : 0;
    float state = zi_base * samples[field_base + first_sample];
    const int chunk_base = field * chunks_per_field;
    for (int chunk = 0; chunk < chunks_per_field; ++chunk) {
        const int index = chunk_base + chunk;
        chunk_states[index] = state;
        state = chunk_gains[index] * state + chunk_biases[index];
    }
}

__global__ void k_apply_env_iir_chunks(
    float* __restrict__ samples,
    const float* __restrict__ chunk_states,
    int samples_per_field,
    int chunks_per_field,
    int total_fields,
    float b0,
    float b1,
    float a1,
    bool reverse)
{
    const int global_chunk = blockIdx.x * blockDim.x + threadIdx.x;
    const int total_chunks = total_fields * chunks_per_field;
    if (global_chunk >= total_chunks) return;

    const int field = global_chunk / chunks_per_field;
    const int chunk = global_chunk - field * chunks_per_field;
    const int begin = chunk * ENV_IIR_CHUNK_SAMPLES;
    const int end = min(begin + ENV_IIR_CHUNK_SAMPLES, samples_per_field);
    float* field_samples = samples + static_cast<size_t>(field) * samples_per_field;
    float state = chunk_states[global_chunk];
    for (int logical = begin; logical < end; ++logical) {
        const int sample = reverse ? (samples_per_field - 1 - logical) : logical;
        const float input = field_samples[sample];
        const float output = b0 * input + state;
        state = b1 * input - a1 * output;
        field_samples[sample] = output;
    }
}

__global__ void k_copy_env_core(
    const float* __restrict__ extended,
    float* __restrict__ envelope,
    int spf,
    int ext_len,
    int total_fields)
{
    const int index = blockIdx.x * blockDim.x + threadIdx.x;
    const int total = total_fields * spf;
    if (index >= total) return;
    constexpr int edge = 6;
    const int field = index / spf;
    const int sample = index - field * spf;
    envelope[index] = extended[static_cast<size_t>(field) * ext_len + edge + sample];
}

__global__ void k_unwrap_to_hz_serial(]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

# Preserve the upstream spike-replacement order exactly, but let a full block
# reject 256-sample tiles that contain no over-threshold samples.  Only tiles
# that can change output fall back to the original sequential inner logic.
string(REPLACE
    [=[__global__ void k_apply_rf_filter(]=]
    [=[__global__ void k_replace_spikes_tiled(
    float* demod,
    const float* demod_diffed,
    int spf,
    int total_fields,
    float max_value,
    int replace_start,
    int replace_end)
{
    const int field = blockIdx.x;
    if (field >= total_fields) return;

    float* field_demod = demod + static_cast<size_t>(field) * spf;
    const float* field_diffed = demod_diffed + static_cast<size_t>(field) * spf;
    for (int tile_start = 0; tile_start < spf; tile_start += blockDim.x) {
        const int sample = tile_start + threadIdx.x;
        const int tile_has_spike = __syncthreads_or(
            sample < spf && field_demod[sample] > max_value);
        if (threadIdx.x == 0 && tile_has_spike != 0) {
            const int tile_end = min(tile_start + static_cast<int>(blockDim.x), spf);
            for (int index = tile_start; index < tile_end; ++index) {
                if (field_demod[index] <= max_value) continue;
                int start = max(0, index - replace_start);
                int end = min(index + replace_end, spf - 1);
                float max_a = field_demod[start];
                float max_b = field_diffed[start];
                for (int nearby = start + 1; nearby < end; ++nearby) {
                    if (field_demod[nearby] > max_a) max_a = field_demod[nearby];
                    if (field_diffed[nearby] > max_b) max_b = field_diffed[nearby];
                }
                if (max_b < max_a) {
                    for (int nearby = start; nearby < end; ++nearby) {
                        field_demod[nearby] = field_diffed[nearby];
                    }
                }
            }
        }
        __syncthreads();
    }
}

__global__ void k_apply_rf_filter(]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_SERIAL_ENVELOPE_LAUNCH [=[        int field_blocks = (num_fields + T - 1) / T;
        k_roll_and_filtfilt_env_first_order<<<field_blocks, T>>>(
            d_envelope, d_envelope, state.d_env_work, spf, num_fields,
            state.env_b0, state.env_b1, state.env_a1, state.env_zi, 4);]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_SERIAL_ENVELOPE_LAUNCH}"
    CUVHS_SERIAL_ENVELOPE_LAUNCH_INDEX)
if(CUVHS_SERIAL_ENVELOPE_LAUNCH_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS FM source no longer has the expected serial envelope launch")
endif()
string(REPLACE
    "${CUVHS_SERIAL_ENVELOPE_LAUNCH}"
    [=[        if (force_serial_envelope) {
            const int field_blocks = (num_fields + T - 1) / T;
            k_roll_and_filtfilt_env_first_order<<<field_blocks, T>>>(
                d_envelope, d_envelope, state.d_env_work, spf, num_fields,
                state.env_b0, state.env_b1, state.env_a1, state.env_zi, 4);
        } else {
            constexpr int edge = 6;
            const int ext_len = spf + 2 * edge;
            const int total_extended = num_fields * ext_len;
            const int chunks_per_field =
                (ext_len + ENV_IIR_CHUNK_SAMPLES - 1) / ENV_IIR_CHUNK_SAMPLES;
            const int total_chunks = num_fields * chunks_per_field;
            float* chunk_gains = reinterpret_cast<float*>(state.d_fft_half);
            float* chunk_biases = chunk_gains + total_chunks;
            float* chunk_states = chunk_biases + total_chunks;

            k_prepare_env_extended<<<(total_extended + T - 1) / T, T>>>(
                d_envelope, state.d_env_work, spf, ext_len, num_fields, 4);
            for (int direction = 0; direction < 2; ++direction) {
                const bool reverse = direction != 0;
                k_summarize_env_iir_chunks<<<(total_chunks + T - 1) / T, T>>>(
                    state.d_env_work, chunk_gains, chunk_biases,
                    ext_len, chunks_per_field, num_fields,
                    state.env_b0, state.env_b1, state.env_a1, reverse);
                k_scan_env_iir_chunk_states<<<(num_fields + T - 1) / T, T>>>(
                    state.d_env_work, chunk_gains, chunk_biases, chunk_states,
                    ext_len, chunks_per_field, num_fields, state.env_zi, reverse);
                k_apply_env_iir_chunks<<<(total_chunks + T - 1) / T, T>>>(
                    state.d_env_work, chunk_states,
                    ext_len, chunks_per_field, num_fields,
                    state.env_b0, state.env_b1, state.env_a1, reverse);
            }
            k_copy_env_core<<<(total + T - 1) / T, T>>>(
                state.d_env_work, d_envelope, spf, ext_len, num_fields);
        }]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_MAIN_UNWRAP_LAUNCH [=[    {
        int blocks = (num_fields + T - 1) / T;
        k_unwrap_to_hz<<<blocks, T>>>(state.d_angle_work, d_demod, fs, spf, fmt.sample_rate, num_fields, OVERLAP_SAMPLES);
    }]=])
set(CUVHS_SYNC_UNWRAP_LAUNCH [=[    {
        int blocks = (num_fields + T - 1) / T;
        k_unwrap_to_hz<<<blocks, T>>>(state.d_angle_work, d_demod_05, spf, spf, fmt.sample_rate, num_fields, 0);]=])
foreach(unwrap_launch IN ITEMS CUVHS_MAIN_UNWRAP_LAUNCH CUVHS_SYNC_UNWRAP_LAUNCH)
    string(FIND
        "${CUVHS_FM_DEMOD_SOURCE}"
        "${${unwrap_launch}}"
        unwrap_launch_index)
    if(unwrap_launch_index EQUAL -1)
        message(FATAL_ERROR "Pinned cuVHS FM source no longer has an expected serial unwrap launch")
    endif()
endforeach()
string(REPLACE
    "${CUVHS_MAIN_UNWRAP_LAUNCH}"
    [=[    {
        if (force_serial_unwrap) {
            const int blocks = (num_fields + T - 1) / T;
            k_unwrap_to_hz_serial<<<blocks, T>>>(state.d_angle_work, d_demod, fs, spf, fmt.sample_rate, num_fields, OVERLAP_SAMPLES);
        } else {
            const int total = num_fields * spf;
            const int blocks = (total + T - 1) / T;
            k_unwrap_to_hz_parallel<<<blocks, T>>>(state.d_angle_work, d_demod, fs, spf, fmt.sample_rate, num_fields, OVERLAP_SAMPLES);
        }
        stage_mark("phase-main");
    }]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    "${CUVHS_SYNC_UNWRAP_LAUNCH}"
    [=[    {
        const int total = num_fields * spf;
        const int blocks = (total + T - 1) / T;
        if (force_serial_unwrap) {
            const int serial_blocks = (num_fields + T - 1) / T;
            k_unwrap_to_hz_serial<<<serial_blocks, T>>>(state.d_angle_work, d_demod_05, spf, spf, fmt.sample_rate, num_fields, 0);
        } else {
            k_unwrap_to_hz_parallel<<<blocks, T>>>(state.d_angle_work, d_demod_05, spf, spf, fmt.sample_rate, num_fields, 0);
        }
        stage_mark("phase-sync");]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

string(REPLACE
    [=[#include <complex>]=]
    [=[#include <complex>
#include <chrono>
#include <cstdlib>]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
set(CUVHS_FM_ENTRY [=[              int num_fields, size_t samples_per_field, const VideoFormat& fmt)
{]=])
string(FIND "${CUVHS_FM_DEMOD_SOURCE}" "${CUVHS_FM_ENTRY}" CUVHS_FM_ENTRY_INDEX)
if(CUVHS_FM_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS FM source no longer has the expected timing entry")
endif()
string(REPLACE
    "${CUVHS_FM_ENTRY}"
    [=[              int num_fields, size_t samples_per_field, const VideoFormat& fmt)
{
    const char* stage_env = std::getenv("CUVHS_STAGE_TIMING");
    const bool stage_timing = stage_env != nullptr && stage_env[0] != '\0' && stage_env[0] != '0';
    const char* serial_unwrap_env = std::getenv("CUVHS_FORCE_SERIAL_UNWRAP");
    const bool force_serial_unwrap = serial_unwrap_env != nullptr &&
        serial_unwrap_env[0] != '\0' && serial_unwrap_env[0] != '0';
    const char* serial_envelope_env = std::getenv("CUVHS_FORCE_SERIAL_ENVELOPE");
    const bool force_serial_envelope = serial_envelope_env != nullptr &&
        serial_envelope_env[0] != '\0' && serial_envelope_env[0] != '0';
    const char* serial_spikes_env = std::getenv("CUVHS_FORCE_SERIAL_SPIKES");
    const bool force_serial_spikes = serial_spikes_env != nullptr &&
        serial_spikes_env[0] != '\0' && serial_spikes_env[0] != '0';
    auto stage_last = std::chrono::steady_clock::now();
    auto stage_mark = [&](const char* name) {
        if (!stage_timing) return;
        cudaDeviceSynchronize();
        const auto now = std::chrono::steady_clock::now();
        const double milliseconds =
            std::chrono::duration<double, std::milli>(now - stage_last).count();
        std::fprintf(stderr, "  [cuda-fast stage] fm/%s %.3f ms\n", name, milliseconds);
        stage_last = now;
    };]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[    cufftExecC2C(state.plan_c2c_inv, state.d_analytic, state.d_analytic, CUFFT_INVERSE);

    if (d_envelope) {]=]
    [=[    cufftExecC2C(state.plan_c2c_inv, state.d_analytic, state.d_analytic, CUFFT_INVERSE);
    stage_mark("analytic");

    if (d_envelope) {]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[    if (d_demod_burst) {]=]
    [=[    stage_mark("envelope");
    if (d_demod_burst) {]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[    {
        int total = num_fields * fs;
        int blocks = (total + T - 1) / T;
        k_compute_angles<<<blocks, T>>>(state.d_analytic, state.d_angle_work, total);
    }]=]
    [=[    stage_mark("burst-source");
    {
        int total = num_fields * fs;
        int blocks = (total + T - 1) / T;
        k_compute_angles<<<blocks, T>>>(state.d_analytic, state.d_angle_work, total);
    }]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[    // ========== PART B: post-demod filtering ==========]=]
    [=[    stage_mark("phase-demod-and-spikes");

    // ========== PART B: post-demod filtering ==========]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[        k_replace_spikes<<<blocks, T>>>(d_demod, d_demod_05, spf, num_fields, 8800000.0, 8, 30);]=]
    [=[        if (force_serial_spikes) {
            const int spike_blocks = (num_fields + T - 1) / T;
            k_replace_spikes<<<spike_blocks, T>>>(
                d_demod, d_demod_05, spf, num_fields, 8800000.0, 8, 30);
        } else {
            k_replace_spikes_tiled<<<num_fields, T>>>(
                d_demod, d_demod_05, spf, num_fields, 8800000.0, 8, 30);
        }
        stage_mark("spike-replace");]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[    {
        int total = num_fields * fb;
        int blocks = (total + T - 1) / T;
        k_apply_complex_filter<<<blocks, T>>>(state.d_fft_half, state.d_post_fft, state.d_fvideo05, fb, total);
    }]=]
    [=[    stage_mark("post-main");
    {
        int total = num_fields * fb;
        int blocks = (total + T - 1) / T;
        k_apply_complex_filter<<<blocks, T>>>(state.d_fft_half, state.d_post_fft, state.d_fvideo05, fb, total);
    }]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
string(REPLACE
    [=[        cudaMemcpy(d_demod_05, d_scratch, static_cast<size_t>(total_dst) * sizeof(float), cudaMemcpyDeviceToDevice);
    }

}]=]
    [=[        cudaMemcpy(d_demod_05, d_scratch, static_cast<size_t>(total_dst) * sizeof(float), cudaMemcpyDeviceToDevice);
    }
    stage_mark("post-sync");

}]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_SERIAL_BURST_DC_KERNEL [=[__global__ void k_roll_and_remove_dc(
    const float* __restrict__ src,
    float* __restrict__ dst,
    int spf,
    int total_fields,
    int roll_shift)
{
    const int field = blockIdx.x;
    if (field >= total_fields) return;

    __shared__ float mean_value;
    if (threadIdx.x == 0) {
        float sum = 0.0;
        const float* field_src = src + static_cast<size_t>(field) * spf;
        for (int i = 0; i < spf; ++i) sum += field_src[i];
        mean_value = sum / static_cast<float>(spf);
    }
    __syncthreads();

    const float* field_src = src + static_cast<size_t>(field) * spf;
    float* field_dst = dst + static_cast<size_t>(field) * spf;
    for (int i = threadIdx.x; i < spf; i += blockDim.x) {
        int src_idx = i - roll_shift;
        while (src_idx < 0) src_idx += spf;
        while (src_idx >= spf) src_idx -= spf;
        field_dst[i] = (field_src[src_idx] - mean_value) / 256.0;
    }
}]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_SERIAL_BURST_DC_KERNEL}"
    CUVHS_SERIAL_BURST_DC_KERNEL_INDEX)
if(CUVHS_SERIAL_BURST_DC_KERNEL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM source no longer has the expected serial burst DC kernel")
endif()
string(REPLACE
    "${CUVHS_SERIAL_BURST_DC_KERNEL}"
    [=[__global__ void k_roll_and_remove_dc_serial(
    const float* __restrict__ src,
    float* __restrict__ dst,
    int spf,
    int total_fields,
    int roll_shift)
{
    const int field = blockIdx.x;
    if (field >= total_fields) return;

    __shared__ float mean_value;
    if (threadIdx.x == 0) {
        float sum = 0.0f;
        const float* field_src = src + static_cast<size_t>(field) * spf;
        for (int i = 0; i < spf; ++i) sum += field_src[i];
        mean_value = sum / static_cast<float>(spf);
    }
    __syncthreads();

    const float* field_src = src + static_cast<size_t>(field) * spf;
    float* field_dst = dst + static_cast<size_t>(field) * spf;
    for (int i = threadIdx.x; i < spf; i += blockDim.x) {
        int src_idx = i - roll_shift;
        while (src_idx < 0) src_idx += spf;
        while (src_idx >= spf) src_idx -= spf;
        field_dst[i] = (field_src[src_idx] - mean_value) / 256.0f;
    }
}

__global__ void k_compute_field_mean_parallel(
    const float* __restrict__ src,
    float* __restrict__ means,
    int spf,
    int total_fields)
{
    const int field = blockIdx.x;
    if (field >= total_fields) return;
    __shared__ double partial[256];
    const float* field_src = src + static_cast<size_t>(field) * spf;
    double sum = 0.0;
    for (int sample = threadIdx.x; sample < spf; sample += blockDim.x) {
        sum += static_cast<double>(field_src[sample]);
    }
    partial[threadIdx.x] = sum;
    __syncthreads();
    for (int stride = blockDim.x / 2; stride > 0; stride >>= 1) {
        if (threadIdx.x < stride) partial[threadIdx.x] += partial[threadIdx.x + stride];
        __syncthreads();
    }
    if (threadIdx.x == 0) {
        means[field] = static_cast<float>(partial[0] / static_cast<double>(spf));
    }
}

__global__ void k_roll_and_remove_dc_parallel(
    const float* __restrict__ src,
    float* __restrict__ dst,
    const float* __restrict__ means,
    int spf,
    int total_fields,
    int roll_shift)
{
    const int index = blockIdx.x * blockDim.x + threadIdx.x;
    const int total = total_fields * spf;
    if (index >= total) return;
    const int field = index / spf;
    const int sample = index - field * spf;
    int source_sample = sample - roll_shift;
    if (source_sample < 0) source_sample += spf;
    const size_t field_offset = static_cast<size_t>(field) * spf;
    dst[index] = (src[field_offset + source_sample] - means[field]) / 256.0f;
}]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_SERIAL_BURST_DC_ENV [=[    const char* serial_spikes_env = std::getenv("CUVHS_FORCE_SERIAL_SPIKES");
    const bool force_serial_spikes = serial_spikes_env != nullptr &&
        serial_spikes_env[0] != '\0' && serial_spikes_env[0] != '0';]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_SERIAL_BURST_DC_ENV}"
    CUVHS_SERIAL_BURST_DC_ENV_INDEX)
if(CUVHS_SERIAL_BURST_DC_ENV_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM source no longer has the expected timing environment block")
endif()
string(REPLACE
    "${CUVHS_SERIAL_BURST_DC_ENV}"
    [=[    const char* serial_spikes_env = std::getenv("CUVHS_FORCE_SERIAL_SPIKES");
    const bool force_serial_spikes = serial_spikes_env != nullptr &&
        serial_spikes_env[0] != '\0' && serial_spikes_env[0] != '0';
    const char* serial_burst_dc_env = std::getenv("CUVHS_FORCE_SERIAL_BURST_DC");
    const bool force_serial_burst_dc = serial_burst_dc_env != nullptr &&
        serial_burst_dc_env[0] != '\0' && serial_burst_dc_env[0] != '0';]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")

set(CUVHS_SERIAL_BURST_DC_LAUNCH [=[        {
            int roll_shift = static_cast<int>(5.0 * (fmt.sample_rate / 40.0e6));
            k_roll_and_remove_dc<<<num_fields, 256>>>(
                state.d_env_filtered, d_demod_burst, spf, num_fields, roll_shift);
        }]=])
string(FIND
    "${CUVHS_FM_DEMOD_SOURCE}"
    "${CUVHS_SERIAL_BURST_DC_LAUNCH}"
    CUVHS_SERIAL_BURST_DC_LAUNCH_INDEX)
if(CUVHS_SERIAL_BURST_DC_LAUNCH_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS FM source no longer has the expected burst DC launch")
endif()
string(REPLACE
    "${CUVHS_SERIAL_BURST_DC_LAUNCH}"
    [=[        {
            const int roll_shift = static_cast<int>(5.0 * (fmt.sample_rate / 40.0e6));
            if (force_serial_burst_dc) {
                k_roll_and_remove_dc_serial<<<num_fields, 256>>>(
                    state.d_env_filtered, d_demod_burst, spf, num_fields, roll_shift);
            } else {
                k_compute_field_mean_parallel<<<num_fields, 256>>>(
                    state.d_env_filtered, state.d_angle_work, spf, num_fields);
                const int total = num_fields * spf;
                const int blocks = (total + T - 1) / T;
                k_roll_and_remove_dc_parallel<<<blocks, T>>>(
                    state.d_env_filtered,
                    d_demod_burst,
                    state.d_angle_work,
                    spf,
                    num_fields,
                    roll_shift);
            }
        }]=]
    CUVHS_FM_DEMOD_SOURCE
    "${CUVHS_FM_DEMOD_SOURCE}")
file(WRITE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/fm_demod.cu"
    "${CUVHS_FM_DEMOD_SOURCE}")

# Floating-point atomic additions produce a different burst RMS when CUDA
# schedules the contributing threads in a different order.  The burst window
# is short, so one thread can sum it in a fixed order before the parallel
# output conversion without a material throughput cost.
file(READ "${cuvhs_SOURCE_DIR}/src/pipeline/chroma_decode.cu" CUVHS_CHROMA_SOURCE)

set(CUVHS_CHROMA_TRACK_STATE "    int detected_track = 0;")
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_TRACK_STATE}"
    CUVHS_CHROMA_TRACK_STATE_INDEX)
if(CUVHS_CHROMA_TRACK_STATE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected track state")
endif()
string(REPLACE
    "${CUVHS_CHROMA_TRACK_STATE}"
    [=[    int detected_track = 0;
    std::vector<int> pal_field_parity;]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

# The pinned prototype only applies per-line head-track rotation to the
# 525-line formats. PAL VHS uses chroma_rotation=[0,-1] as well: one head keeps
# the phase fixed and the other subtracts 90 degrees per line. Leaving PAL in
# phase_mode=0 produces the characteristic moving rainbow bands after export.
set(CUVHS_PAL_PHASE_KERNEL [=[    } else if (phase_mode == 2) {
        int track = field_track[field];
        int rot = track ? 3 : 0;
        int line_phase = (((out_line + 1) * rot) + line_phase_bias) & 3;
        phase_offset = (double)line_phase * (M_PI * 0.5);
    }]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_PAL_PHASE_KERNEL}"
    CUVHS_PAL_PHASE_KERNEL_INDEX)
if(CUVHS_PAL_PHASE_KERNEL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected line-phase kernel")
endif()
string(REPLACE
    "${CUVHS_PAL_PHASE_KERNEL}"
    [=[    } else if (phase_mode == 2) {
        int track = field_track[field];
        int rot = track ? 3 : 0;
        int line_phase = (((out_line + 1) * rot) + line_phase_bias) & 3;
        phase_offset = (double)line_phase * (M_PI * 0.5);
    } else if (phase_mode == 3) {
        // PAL VHS chroma_rotation=[0,-1]. Track 0 is stationary; track 1
        // rotates -90 degrees (quadrant 3) on every output line.
        int track = field_track[field];
        int rot = track ? 3 : 0;
        int line_phase = (((out_line + 1) * rot) + line_phase_bias) & 3;
        phase_offset = (double)line_phase * (M_PI * 0.5);
    }]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_PAL_PHASE_MODE [=[    if (fmt.profile == VideoProfile::NTSC_525_60_VHS) phase_mode = 1;
    else if (fmt.profile == VideoProfile::MPAL_525_60_VHS) phase_mode = 2;]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_PAL_PHASE_MODE}"
    CUVHS_PAL_PHASE_MODE_INDEX)
if(CUVHS_PAL_PHASE_MODE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected phase-mode selection")
endif()
string(REPLACE
    "${CUVHS_PAL_PHASE_MODE}"
    [=[    if (fmt.profile == VideoProfile::NTSC_525_60_VHS) phase_mode = 1;
    else if (fmt.profile == VideoProfile::MPAL_525_60_VHS) phase_mode = 2;
    else if (fmt.profile == VideoProfile::PAL_625_50_VHS) phase_mode = 3;]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_PAL_STATE_BRANCH [=[    } else if (!uses_ntsc_family_phase) {
        field_phase_ids.clear();
    } else if (state && state->valid) {]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_PAL_STATE_BRANCH}"
    CUVHS_PAL_STATE_BRANCH_INDEX)
if(CUVHS_PAL_STATE_BRANCH_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected PAL state branch")
endif()
string(REPLACE
    "${CUVHS_PAL_STATE_BRANCH}"
    [=[    } else if (!uses_ntsc_family_phase) {
        field_phase_ids.clear();
        pal_field_parity.assign((size_t)num_fields, -1);
        cudaMemcpy(
            pal_field_parity.data(),
            d_is_first_field,
            (size_t)num_fields * sizeof(int),
            cudaMemcpyDeviceToHost);
        detected_track = (state && state->valid) ? state->current_track : 0;
        for (int f = 0; f < num_fields; ++f) {
            const int parity = pal_field_parity[(size_t)f];
            if (parity != 0 && parity != 1) continue;
            const int field_track = parity == 0 ? 1 : 0;
            detected_track = (f & 1) ? (1 - field_track) : field_track;
            break;
        }
    } else if (state && state->valid) {]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_PAL_TRACK_ASSIGN [=[    } else {
        field_phase_ids.clear();
    }

    if (fmt.profile == VideoProfile::NTSC_525_60_VHS) {]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_PAL_TRACK_ASSIGN}"
    CUVHS_PAL_TRACK_ASSIGN_INDEX)
if(CUVHS_PAL_TRACK_ASSIGN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected PAL track assignment")
endif()
string(REPLACE
    "${CUVHS_PAL_TRACK_ASSIGN}"
    [=[    } else {
        field_phase_ids.clear();
        for (int f = 0; f < num_fields; ++f) {
            const int cadence_track = (f & 1) ? (1 - current_track) : current_track;
            const int parity = pal_field_parity[(size_t)f];
            h_track[f] = parity == 0 ? 1 : parity == 1 ? 0 : cadence_track;
        }
        cudaMemcpy(d_field_track, h_track.data(), num_fields * sizeof(int), cudaMemcpyHostToDevice);
        cudaMemset(d_field_phase_offset, 0, num_fields * sizeof(int));
    }

    if (fmt.profile == VideoProfile::NTSC_525_60_VHS) {]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_PAL_STATE_SAVE [=[    if (state && uses_ntsc_family_phase) {
        state->valid = true;
        // current_track is the base track for even-indexed fields in this batch.
        // If batch had odd field count, next batch's field 0 needs the opposite parity.
        state->current_track = (num_fields & 1) ? (1 - current_track) : current_track;
        state->good_metric_threshold = good_metric_threshold;
        // Advance cycle_start by num_fields so next batch continues the sequence
        state->cycle_start = (cycle_start + num_fields) % 8;
    } else if (state) {
        state->valid = false;
    }]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_PAL_STATE_SAVE}"
    CUVHS_PAL_STATE_SAVE_INDEX)
if(CUVHS_PAL_STATE_SAVE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected state-save block")
endif()
string(REPLACE
    "${CUVHS_PAL_STATE_SAVE}"
    [=[    if (state) {
        state->valid = true;
        // current_track is the base track for even-indexed fields in this batch.
        // If batch had odd field count, next batch's field 0 needs the opposite parity.
        state->current_track = (num_fields & 1) ? (1 - current_track) : current_track;
        state->good_metric_threshold = uses_ntsc_family_phase ? good_metric_threshold : 0.0;
        // Only 525-line formats use the 8-field colour-frame cycle.
        state->cycle_start = uses_ntsc_family_phase
            ? (cycle_start + num_fields) % 8
            : 0;
    }]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_BURST_REDUCTION [=[    if (threadIdx.x == 0) burst_sum_sq = 0.0;
    __syncthreads();

    int burst_len = burst_end - burst_start;
    for (int i = threadIdx.x; i < burst_len; i += blockDim.x) {
        double val = line_data[burst_start + i] * fft_scale;
        atomicAdd(&burst_sum_sq, val * val);
    }
    __syncthreads();]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_BURST_REDUCTION}"
    CUVHS_BURST_REDUCTION_INDEX)
if(CUVHS_BURST_REDUCTION_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected atomic burst reduction")
endif()
string(REPLACE
    "${CUVHS_BURST_REDUCTION}"
    [=[    int burst_len = burst_end - burst_start;
    if (threadIdx.x == 0) {
        double ordered_sum_sq = 0.0;
        for (int i = 0; i < burst_len; ++i) {
            double val = line_data[burst_start + i] * fft_scale;
            ordered_sum_sq += val * val;
        }
        burst_sum_sq = ordered_sum_sq;
    }
    __syncthreads();]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")
vhsdecode_cuda_fast_protect_chroma_filter_design(CUVHS_CHROMA_SOURCE)
vhsdecode_cuda_fast_convert_pipeline_to_fp32(CUVHS_CHROMA_SOURCE)
vhsdecode_cuda_fast_finish_chroma_mixed_precision(CUVHS_CHROMA_SOURCE)
set(CUVHS_CHROMA_DECODE_DEFINITION "void chroma_decode(")
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_DECODE_DEFINITION}"
    CUVHS_CHROMA_DECODE_DEFINITION_INDEX)
if(CUVHS_CHROMA_DECODE_DEFINITION_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected decode definition")
endif()
string(REPLACE
    "${CUVHS_CHROMA_DECODE_DEFINITION}"
    "bool chroma_decode("
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

# Match Exact's field-dependent source-line origin.  The pinned cuVHS source
# starts every format at physical line 2; Exact starts NTSC at line 1 and PAL
# at line 3/4 for first/second fields.  Keep the per-line heterodyne indexed
# from output line zero: only the RF source coordinates move here.
set(CUVHS_CHROMA_HOST_LEVEL_ORIGIN [=[        const float* field_linelocs = h_linelocs + (size_t)field * lines_per_frame;
        const int lineoffset = 1;
        std::vector<float> wow((size_t)output_field_lines + 1, 1.0);]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_HOST_LEVEL_ORIGIN}"
    CUVHS_CHROMA_HOST_LEVEL_ORIGIN_INDEX)
if(CUVHS_CHROMA_HOST_LEVEL_ORIGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected host level origin")
endif()
string(REPLACE
    "${CUVHS_CHROMA_HOST_LEVEL_ORIGIN}"
    [=[        const float* field_linelocs = h_linelocs + (size_t)field * lines_per_frame;
        const int lineoffset = lines_per_frame == 625
            ? (h_is_first_field != nullptr && h_is_first_field[field] == 0 ? 2 : 1)
            : 1;
        std::vector<float> wow((size_t)output_field_lines + 1, 1.0);]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_HOST_SOURCE_ORIGIN [=[        const float* linelocs = h_linelocs + static_cast<size_t>(field) * fmt.lines_per_frame;
        const int lineoffset = 1;
        const int outline_offset = (lineoffset + 1) * outwidth;]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_HOST_SOURCE_ORIGIN}"
    CUVHS_CHROMA_HOST_SOURCE_ORIGIN_INDEX)
if(CUVHS_CHROMA_HOST_SOURCE_ORIGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected host source origin")
endif()
string(REPLACE
    "${CUVHS_CHROMA_HOST_SOURCE_ORIGIN}"
    [=[        const float* linelocs = h_linelocs + static_cast<size_t>(field) * fmt.lines_per_frame;
        const int output_first_line = fmt.lines_per_frame == 625
            ? (h_is_first_field != nullptr && h_is_first_field[field] == 0 ? 4 : 3)
            : 1;
        const int outline_offset = output_first_line * outwidth;]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_PROBE_SOURCE_ORIGIN [=[    const int lineoffset = 1;
    int ll_line = out_line + lineoffset + 1;]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_PROBE_SOURCE_ORIGIN}"
    CUVHS_CHROMA_PROBE_SOURCE_ORIGIN_INDEX)
if(CUVHS_CHROMA_PROBE_SOURCE_ORIGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected probe source origin")
endif()
string(REPLACE
    "${CUVHS_CHROMA_PROBE_SOURCE_ORIGIN}"
    [=[    const int output_first_line = lines_per_frame == 625
        ? (is_first_field != nullptr && is_first_field[field] == 0 ? 4 : 3)
        : 1;
    int ll_line = out_line + output_first_line;]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_FILTER_BUILD [=[    float final_lower = (fmt.chroma_under / 1e6) * 0.9;
    float final_upper = (fmt.chroma_under / 1e6) * 0.75;
    float final_lo_hz = fmt.fsc - final_lower * 1e6;
    float final_hi_hz = fmt.fsc + final_upper * 1e6;
    auto final_bpf_resp = zpk_freqz_local(
        butter_digital_bandpass_zpk_local(4, final_lo_hz, final_hi_hz, fmt.output_rate),
        static_cast<std::size_t>(field_fft_size),
        true);
    auto* h_final_bpf = new float[field_freq_bins];
    for (int i = 0; i < field_freq_bins; i++) {
        h_final_bpf[i] = std::norm(final_bpf_resp[(size_t)i]) * field_fft_norm;
    }

    float* d_final_bpf_filter = nullptr;
    cudaMalloc(&d_final_bpf_filter, (size_t)field_freq_bins * sizeof(float));
    cudaMemcpy(d_final_bpf_filter, h_final_bpf,
               (size_t)field_freq_bins * sizeof(float), cudaMemcpyHostToDevice);
    delete[] h_final_bpf;]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_FILTER_BUILD}"
    CUVHS_CHROMA_FILTER_BUILD_INDEX)
if(CUVHS_CHROMA_FILTER_BUILD_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS chroma source no longer has the expected final-filter build")
endif()
string(REPLACE
    "${CUVHS_CHROMA_FILTER_BUILD}"
    [=[    std::vector<float> local_final_bpf;
    std::vector<float>& h_final_bpf = state != nullptr
        ? state->final_bpf_response
        : local_final_bpf;
    if (state == nullptr || state->final_bpf_size != field_fft_size ||
        h_final_bpf.size() != static_cast<size_t>(field_freq_bins)) {
        const float final_lower = (fmt.chroma_under / 1e6) * 0.9;
        const float final_upper = (fmt.chroma_under / 1e6) * 0.75;
        const float final_lo_hz = fmt.fsc - final_lower * 1e6;
        const float final_hi_hz = fmt.fsc + final_upper * 1e6;
        const auto final_bpf_resp = zpk_freqz_local(
            butter_digital_bandpass_zpk_local(4, final_lo_hz, final_hi_hz, fmt.output_rate),
            static_cast<std::size_t>(field_fft_size),
            true);
        h_final_bpf.resize(static_cast<size_t>(field_freq_bins));
        for (int i = 0; i < field_freq_bins; ++i) {
            h_final_bpf[static_cast<size_t>(i)] =
                std::norm(final_bpf_resp[static_cast<size_t>(i)]) * field_fft_norm;
        }
        if (state != nullptr) state->final_bpf_size = field_fft_size;
    }

    float* d_final_bpf_filter = nullptr;
    cudaMalloc(&d_final_bpf_filter, (size_t)field_freq_bins * sizeof(float));
    cudaMemcpy(d_final_bpf_filter, h_final_bpf.data(),
               (size_t)field_freq_bins * sizeof(float), cudaMemcpyHostToDevice);]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

string(REPLACE
    [=[#include "pipeline/chroma_decode.h"]=]
    [=[#include "pipeline/chroma_decode.h"
#include "cuda_fast_geometry.h"
#include <chrono>]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

string(REPLACE
    [=[// ============================================================================
// Host entry point
// ============================================================================]=]
    [=[struct CudaFastChromaWorkspace {
    int fft_size = 0;
    int field_fft_size = 0;
    int output_field_lines = 0;
    int chunk_lines = 0;
    int fields_per_chunk = 0;
    float* d_het_buf = nullptr;
    float* d_comb_buf = nullptr;
    float* d_metrics = nullptr;
    int* d_first_bad = nullptr;
    float* d_phase_i = nullptr;
    float* d_phase_q = nullptr;
    float* d_phase_adjust = nullptr;
    float* d_field_buf = nullptr;
    cufftComplex* d_field_fft = nullptr;
    cufftHandle plan_field_r2c = 0;
    cufftHandle plan_field_c2r = 0;

    void release() {
        if (plan_field_r2c) cufftDestroy(plan_field_r2c);
        if (plan_field_c2r) cufftDestroy(plan_field_c2r);
        if (d_het_buf) cudaFree(d_het_buf);
        if (d_comb_buf) cudaFree(d_comb_buf);
        if (d_metrics) cudaFree(d_metrics);
        if (d_first_bad) cudaFree(d_first_bad);
        if (d_phase_i) cudaFree(d_phase_i);
        if (d_phase_q) cudaFree(d_phase_q);
        if (d_phase_adjust) cudaFree(d_phase_adjust);
        if (d_field_buf) cudaFree(d_field_buf);
        if (d_field_fft) cudaFree(d_field_fft);
        *this = {};
    }

    bool compatible(int line_fft, int full_field_fft, int field_lines) const {
        return fft_size == line_fft &&
               field_fft_size == full_field_fft &&
               output_field_lines == field_lines &&
               chunk_lines >= field_lines &&
               d_het_buf && d_comb_buf && d_metrics && d_first_bad &&
               d_phase_i && d_phase_q && d_phase_adjust && d_field_buf && d_field_fft &&
               plan_field_r2c && plan_field_c2r;
    }
};

void chroma_state_release(ChromaState* state) {
    if (state == nullptr || state->workspace == nullptr) return;
    auto* workspace = static_cast<CudaFastChromaWorkspace*>(state->workspace);
    workspace->release();
    delete workspace;
    state->workspace = nullptr;
}

static bool ensure_chroma_workspace(
    CudaFastChromaWorkspace& workspace,
    int num_fields,
    int output_field_lines,
    int fft_size,
    int field_fft_size,
    int field_freq_bins)
{
    if (env_flag_enabled_local("CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE")) {
        return false;
    }
    if (workspace.compatible(fft_size, field_fft_size, output_field_lines)) {
        return true;
    }

    workspace.release();
    size_t free_mem = 0;
    size_t total_mem = 0;
    if (cudaMemGetInfo(&free_mem, &total_mem) != cudaSuccess) return false;
    const size_t bytes_per_line =
        2 * static_cast<size_t>(fft_size) * sizeof(float) +
        (static_cast<size_t>(field_fft_size) * sizeof(float) +
         static_cast<size_t>(field_freq_bins) * sizeof(cufftComplex)) /
            static_cast<size_t>(output_field_lines);
    const size_t usable = static_cast<size_t>(free_mem * 0.8);
    int max_lines = static_cast<int>(usable / bytes_per_line);
    if (max_lines < output_field_lines) max_lines = output_field_lines;

    int max_chunk_lines = (4096 / output_field_lines) * output_field_lines;
    if (max_chunk_lines < output_field_lines) max_chunk_lines = output_field_lines;
    max_lines = std::min(max_lines, max_chunk_lines);
    int total_lines = std::max(num_fields, 1) * output_field_lines;
    int candidate_lines = std::min(max_lines, total_lines);
    candidate_lines = (candidate_lines / output_field_lines) * output_field_lines;
    if (candidate_lines < output_field_lines) candidate_lines = output_field_lines;

    int field_n[] = { field_fft_size };
    while (candidate_lines >= output_field_lines) {
        const int candidate_fields = candidate_lines / output_field_lines;
        const size_t het_bytes =
            static_cast<size_t>(candidate_lines) * fft_size * sizeof(float);
        cudaError_t status = cudaMalloc(&workspace.d_het_buf, het_bytes);
        if (status == cudaSuccess) status = cudaMalloc(&workspace.d_comb_buf, het_bytes);
        if (status == cudaSuccess) {
            status = cudaMalloc(&workspace.d_metrics, candidate_fields * sizeof(float));
        }
        if (status == cudaSuccess) status = cudaMalloc(&workspace.d_first_bad, sizeof(int));
        if (status == cudaSuccess) {
            status = cudaMalloc(&workspace.d_phase_i, candidate_fields * sizeof(float));
        }
        if (status == cudaSuccess) {
            status = cudaMalloc(&workspace.d_phase_q, candidate_fields * sizeof(float));
        }
        if (status == cudaSuccess) {
            status = cudaMalloc(&workspace.d_phase_adjust, candidate_fields * sizeof(float));
        }
        if (status == cudaSuccess) {
            status = cudaMalloc(
                &workspace.d_field_buf,
                static_cast<size_t>(candidate_fields) * field_fft_size * sizeof(float));
        }
        if (status == cudaSuccess) {
            status = cudaMalloc(
                &workspace.d_field_fft,
                static_cast<size_t>(candidate_fields) * field_freq_bins * sizeof(cufftComplex));
        }

        bool plans_ok = status == cudaSuccess &&
            cufftPlanMany(&workspace.plan_field_r2c, 1, field_n,
                          nullptr, 1, field_fft_size, nullptr, 1, field_freq_bins,
                          CUFFT_R2C, candidate_fields) == CUFFT_SUCCESS &&
            cufftPlanMany(&workspace.plan_field_c2r, 1, field_n,
                          nullptr, 1, field_freq_bins, nullptr, 1, field_fft_size,
                          CUFFT_C2R, candidate_fields) == CUFFT_SUCCESS;
        if (plans_ok) {
            workspace.fft_size = fft_size;
            workspace.field_fft_size = field_fft_size;
            workspace.output_field_lines = output_field_lines;
            workspace.chunk_lines = candidate_lines;
            workspace.fields_per_chunk = candidate_fields;
            return true;
        }

        workspace.release();
        const int next_fields = std::max(1, candidate_fields / 2);
        const int next_lines = next_fields * output_field_lines;
        if (next_lines == candidate_lines) break;
        candidate_lines = next_lines;
    }

    return false;
}

// ============================================================================
// Host entry point
// ============================================================================]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_ENTRY [=[                   ChromaState* state,
                   size_t raw_offset)
{]=])
string(FIND "${CUVHS_CHROMA_SOURCE}" "${CUVHS_CHROMA_ENTRY}" CUVHS_CHROMA_ENTRY_INDEX)
if(CUVHS_CHROMA_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS chroma source no longer has the expected entry timing point")
endif()
string(REPLACE
    "${CUVHS_CHROMA_ENTRY}"
    [=[                   ChromaState* state,
                   size_t raw_offset)
{
    const bool stage_timing = env_flag_enabled_local("CUVHS_STAGE_TIMING");
    auto stage_last = std::chrono::steady_clock::now();
    auto stage_mark = [&](const char* name) {
        if (!stage_timing) return;
        cudaDeviceSynchronize();
        const auto now = std::chrono::steady_clock::now();
        const double milliseconds =
            std::chrono::duration<double, std::milli>(now - stage_last).count();
        std::fprintf(stderr, "  [cuda-fast stage] chroma/%s %.3f ms\n", name, milliseconds);
        stage_last = now;
    };]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_GEOMETRY_HOST_BLOCK [=[    {
        std::vector<float> h_linelocs((size_t)num_fields * fmt.lines_per_frame);
        std::vector<int> h_is_first(num_fields);
        cudaMemcpy(h_linelocs.data(), d_linelocs,
                   (size_t)num_fields * fmt.lines_per_frame * sizeof(float),
                   cudaMemcpyDeviceToHost);
        cudaMemcpy(h_is_first.data(), d_is_first_field,
                   (size_t)num_fields * sizeof(int),
                   cudaMemcpyDeviceToHost);
        std::vector<float> h_level_adjust;
        compute_chroma_level_adjust_local(h_linelocs.data(), h_is_first.data(), num_fields,
                                          fmt.lines_per_frame, output_field_lines,
                                          fmt.samples_per_line, &h_level_adjust);
        cudaMemcpy(d_level_adjust_pre, h_level_adjust.data(),
                   (size_t)num_fields * output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_level_adjust_post, h_level_adjust.data(),
                   (size_t)num_fields * output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        std::vector<float> h_source_coords;
        std::vector<float> h_source_level_adjust;
        compute_chroma_scale_field_params_local(h_linelocs.data(), h_is_first.data(), num_fields, fmt,
                                                &h_source_coords, &h_source_level_adjust);
        cudaMemcpy(d_source_coords_post, h_source_coords.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_source_level_adjust_post, h_source_level_adjust.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
    }]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_GEOMETRY_HOST_BLOCK}"
    CUVHS_CHROMA_GEOMETRY_HOST_BLOCK_INDEX)
if(CUVHS_CHROMA_GEOMETRY_HOST_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected initial host geometry block")
endif()
string(REPLACE
    "${CUVHS_CHROMA_GEOMETRY_HOST_BLOCK}"
    [=[    if (env_flag_enabled_local("CUVHS_FORCE_HOST_GEOMETRY") ||
        !cuda_fast_compute_chroma_geometry(
            d_linelocs,
            d_is_first_field,
            d_level_adjust_pre,
            d_level_adjust_post,
            d_source_coords_post,
            d_source_level_adjust_post,
            num_fields,
            fmt)) {
        // Preserve a correctness fallback for CUDA launch failures. The normal
        // cuda-fast path never downloads these geometry arrays.
        std::vector<float> h_linelocs((size_t)num_fields * fmt.lines_per_frame);
        std::vector<int> h_is_first(num_fields);
        cudaMemcpy(h_linelocs.data(), d_linelocs,
                   (size_t)num_fields * fmt.lines_per_frame * sizeof(float),
                   cudaMemcpyDeviceToHost);
        cudaMemcpy(h_is_first.data(), d_is_first_field,
                   (size_t)num_fields * sizeof(int),
                   cudaMemcpyDeviceToHost);
        std::vector<float> h_level_adjust;
        compute_chroma_level_adjust_local(h_linelocs.data(), h_is_first.data(), num_fields,
                                          fmt.lines_per_frame, output_field_lines,
                                          fmt.samples_per_line, &h_level_adjust);
        cudaMemcpy(d_level_adjust_pre, h_level_adjust.data(),
                   (size_t)num_fields * output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_level_adjust_post, h_level_adjust.data(),
                   (size_t)num_fields * output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        std::vector<float> h_source_coords;
        std::vector<float> h_source_level_adjust;
        compute_chroma_scale_field_params_local(h_linelocs.data(), h_is_first.data(), num_fields, fmt,
                                                &h_source_coords, &h_source_level_adjust);
        cudaMemcpy(d_source_coords_post, h_source_coords.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_source_level_adjust_post, h_source_level_adjust.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
    }]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

set(CUVHS_CHROMA_POST_GEOMETRY_HOST_BLOCK [=[        std::vector<float> h_level_adjust_post;
        std::vector<int> h_is_first(num_fields);
        cudaMemcpy(h_is_first.data(), d_is_first_field,
                   (size_t)num_fields * sizeof(int),
                   cudaMemcpyDeviceToHost);
        compute_chroma_level_adjust_local(h_linelocs.data(), h_is_first.data(), num_fields,
                                          fmt.lines_per_frame, output_field_lines,
                                          fmt.samples_per_line, &h_level_adjust_post);
        std::vector<float> h_source_coords_post;
        std::vector<float> h_source_level_adjust_post;
        compute_chroma_scale_field_params_local(h_linelocs.data(), h_is_first.data(), num_fields, fmt,
                                                &h_source_coords_post, &h_source_level_adjust_post);
        cudaMemcpy(d_level_adjust_post, h_level_adjust_post.data(),
                   (size_t)num_fields * output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_source_coords_post, h_source_coords_post.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_source_level_adjust_post, h_source_level_adjust_post.data(),
                   (size_t)num_fields * field_samples * sizeof(float),
                   cudaMemcpyHostToDevice);
        cudaMemcpy(d_linelocs, h_linelocs.data(),
                   (size_t)num_fields * fmt.lines_per_frame * sizeof(float),
                   cudaMemcpyHostToDevice);]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_POST_GEOMETRY_HOST_BLOCK}"
    CUVHS_CHROMA_POST_GEOMETRY_HOST_BLOCK_INDEX)
if(CUVHS_CHROMA_POST_GEOMETRY_HOST_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected post-sync host geometry block")
endif()
string(REPLACE
    "${CUVHS_CHROMA_POST_GEOMETRY_HOST_BLOCK}"
    [=[        cudaMemcpy(d_linelocs, h_linelocs.data(),
                   (size_t)num_fields * fmt.lines_per_frame * sizeof(float),
                   cudaMemcpyHostToDevice);
        if (env_flag_enabled_local("CUVHS_FORCE_HOST_GEOMETRY") ||
            !cuda_fast_compute_chroma_geometry(
                d_linelocs,
                d_is_first_field,
                d_level_adjust_post,
                nullptr,
                d_source_coords_post,
                d_source_level_adjust_post,
                num_fields,
                fmt)) {
            std::vector<float> h_level_adjust_post;
            std::vector<int> h_is_first(num_fields);
            cudaMemcpy(h_is_first.data(), d_is_first_field,
                       (size_t)num_fields * sizeof(int),
                       cudaMemcpyDeviceToHost);
            compute_chroma_level_adjust_local(h_linelocs.data(), h_is_first.data(), num_fields,
                                              fmt.lines_per_frame, output_field_lines,
                                              fmt.samples_per_line, &h_level_adjust_post);
            std::vector<float> h_source_coords_post;
            std::vector<float> h_source_level_adjust_post;
            compute_chroma_scale_field_params_local(h_linelocs.data(), h_is_first.data(), num_fields, fmt,
                                                    &h_source_coords_post, &h_source_level_adjust_post);
            cudaMemcpy(d_level_adjust_post, h_level_adjust_post.data(),
                       (size_t)num_fields * output_field_lines * sizeof(float),
                       cudaMemcpyHostToDevice);
            cudaMemcpy(d_source_coords_post, h_source_coords_post.data(),
                       (size_t)num_fields * field_samples * sizeof(float),
                       cudaMemcpyHostToDevice);
            cudaMemcpy(d_source_level_adjust_post, h_source_level_adjust_post.data(),
                       (size_t)num_fields * field_samples * sizeof(float),
                       cudaMemcpyHostToDevice);
        }]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

# Keep the large chroma scratch buffers and cuFFT plans alive for the lifetime
# of a Pipeline.  The original source rebuilt them for every field batch, even
# when the format and transform sizes were unchanged.  A local workspace keeps
# the standalone/null-state contract intact and provides an A/B diagnostic.
set(CUVHS_CHROMA_WORKSPACE_BEGIN
    [=[    // Determine chunk size from available VRAM]=])
set(CUVHS_CHROMA_WORKSPACE_END
    [=[    const char* dump_k4_dir = env_string_local("CUVHS_K4_DUMP_DIR");]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_WORKSPACE_BEGIN}"
    CUVHS_CHROMA_WORKSPACE_BEGIN_INDEX)
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_WORKSPACE_END}"
    CUVHS_CHROMA_WORKSPACE_END_INDEX)
if(CUVHS_CHROMA_WORKSPACE_BEGIN_INDEX EQUAL -1 OR
   CUVHS_CHROMA_WORKSPACE_END_INDEX EQUAL -1 OR
   CUVHS_CHROMA_WORKSPACE_END_INDEX LESS CUVHS_CHROMA_WORKSPACE_BEGIN_INDEX)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected workspace block")
endif()
string(SUBSTRING
    "${CUVHS_CHROMA_SOURCE}"
    0
    ${CUVHS_CHROMA_WORKSPACE_BEGIN_INDEX}
    CUVHS_CHROMA_WORKSPACE_PREFIX)
string(SUBSTRING
    "${CUVHS_CHROMA_SOURCE}"
    ${CUVHS_CHROMA_WORKSPACE_END_INDEX}
    -1
    CUVHS_CHROMA_WORKSPACE_SUFFIX)
set(CUVHS_CHROMA_WORKSPACE_REPLACEMENT [=[    const bool persistent_workspace =
        state != nullptr &&
        !env_flag_enabled_local("CUVHS_DISABLE_PERSISTENT_CHROMA_WORKSPACE");
    CudaFastChromaWorkspace local_workspace;
    CudaFastChromaWorkspace* workspace = &local_workspace;
    if (persistent_workspace) {
        if (state->workspace == nullptr) {
            state->workspace = new CudaFastChromaWorkspace();
        }
        workspace = static_cast<CudaFastChromaWorkspace*>(state->workspace);
    }

    if (!ensure_chroma_workspace(
            *workspace,
            num_fields,
            output_field_lines,
            fft_size,
            field_fft_size,
            field_freq_bins)) {
        fprintf(stderr,
                "  [chroma] Failed to allocate persistent temp buffers/plans even at one-field chunk size\n");
        if (!persistent_workspace) local_workspace.release();
        cudaFree(d_field_track);
        cudaFree(d_field_phase_offset);
        cudaFree(d_level_adjust_pre);
        cudaFree(d_level_adjust_post);
        cudaFree(d_source_coords_post);
        cudaFree(d_source_level_adjust_post);
        cudaFree(d_final_bpf_filter);
        return false;
    }

    const int fields_per_chunk = workspace->fields_per_chunk;
    float* d_het_buf = workspace->d_het_buf;
    float* d_comb_buf = workspace->d_comb_buf;
    float* d_metrics = workspace->d_metrics;
    int* d_first_bad = workspace->d_first_bad;
    float* d_phase_i = workspace->d_phase_i;
    float* d_phase_q = workspace->d_phase_q;
    float* d_phase_adjust = workspace->d_phase_adjust;
    float* d_field_buf = workspace->d_field_buf;
    cufftComplex* d_field_fft = workspace->d_field_fft;
    const cufftHandle plan_field_r2c = workspace->plan_field_r2c;
    const cufftHandle plan_field_c2r = workspace->plan_field_c2r;

]=])
set(CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_WORKSPACE_PREFIX}${CUVHS_CHROMA_WORKSPACE_REPLACEMENT}${CUVHS_CHROMA_WORKSPACE_SUFFIX}")

# A cuFFT plan has a fixed batch count.  Running its full capacity for the last
# partial chunk is safe because transforms are independent and only the valid
# fields are consumed.  It avoids destroying the persistent plans for every
# short final chunk.
set(CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN
    [=[        if (lines_this < chunk_lines) {]=])
set(CUVHS_CHROMA_PARTIAL_REPLAN_END
    [=[        // Retry loop: process chunk, check for track flips, retry if needed]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN}"
    CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN_INDEX)
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_PARTIAL_REPLAN_END}"
    CUVHS_CHROMA_PARTIAL_REPLAN_END_INDEX)
if(CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN_INDEX EQUAL -1 OR
   CUVHS_CHROMA_PARTIAL_REPLAN_END_INDEX EQUAL -1 OR
   CUVHS_CHROMA_PARTIAL_REPLAN_END_INDEX LESS CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN_INDEX)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected partial-chunk replan block")
endif()
string(SUBSTRING
    "${CUVHS_CHROMA_SOURCE}"
    0
    ${CUVHS_CHROMA_PARTIAL_REPLAN_BEGIN_INDEX}
    CUVHS_CHROMA_PARTIAL_REPLAN_PREFIX)
string(SUBSTRING
    "${CUVHS_CHROMA_SOURCE}"
    ${CUVHS_CHROMA_PARTIAL_REPLAN_END_INDEX}
    -1
    CUVHS_CHROMA_PARTIAL_REPLAN_SUFFIX)
set(CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_PARTIAL_REPLAN_PREFIX}${CUVHS_CHROMA_PARTIAL_REPLAN_SUFFIX}")

# Workspace-owned allocations are released by the Pipeline destructor.  The
# null-state/diagnostic path continues to release its local workspace here.
set(CUVHS_CHROMA_WORKSPACE_CLEANUP [=[    cufftDestroy(plan_r2c);
    cufftDestroy(plan_c2r);
    cufftDestroy(plan_field_r2c);
    cufftDestroy(plan_field_c2r);
    cudaFree(d_het_buf);
    cudaFree(d_comb_buf);
    cudaFree(d_fft_buf);
    cudaFree(d_field_buf);
    cudaFree(d_field_fft);
    cudaFree(d_metrics);
    cudaFree(d_first_bad);
    cudaFree(d_phase_adjust);
    cudaFree(d_phase_q);
    cudaFree(d_phase_i);]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_WORKSPACE_CLEANUP}"
    CUVHS_CHROMA_WORKSPACE_CLEANUP_INDEX)
if(CUVHS_CHROMA_WORKSPACE_CLEANUP_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected workspace cleanup block")
endif()
string(REPLACE
    "${CUVHS_CHROMA_WORKSPACE_CLEANUP}"
    [=[    stage_mark("processing");
    if (!persistent_workspace) local_workspace.release();]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")

string(REPLACE
    [=[    // Free detection buffers (only needed for initial auto-detect above)]=]
    [=[    stage_mark("setup-and-detect");

    // Free detection buffers (only needed for initial auto-detect above)]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")
string(REPLACE
    [=[    const char* dump_k4_dir = env_string_local("CUVHS_K4_DUMP_DIR");]=]
    [=[    stage_mark("workspace-and-plans");

    const char* dump_k4_dir = env_string_local("CUVHS_K4_DUMP_DIR");]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")
set(CUVHS_CHROMA_SUCCESS_TAIL [=[        state->cycle_start = uses_ntsc_family_phase
            ? (cycle_start + num_fields) % 8
            : 0;
    }
}]=])
string(FIND
    "${CUVHS_CHROMA_SOURCE}"
    "${CUVHS_CHROMA_SUCCESS_TAIL}"
    CUVHS_CHROMA_SUCCESS_TAIL_INDEX)
if(CUVHS_CHROMA_SUCCESS_TAIL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS chroma source no longer has the expected success tail")
endif()
string(REPLACE
    "${CUVHS_CHROMA_SUCCESS_TAIL}"
    [=[        state->cycle_start = uses_ntsc_family_phase
            ? (cycle_start + num_fields) % 8
            : 0;
    }
    return true;
}]=]
    CUVHS_CHROMA_SOURCE
    "${CUVHS_CHROMA_SOURCE}")
file(WRITE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/chroma_decode.cu"
    "${CUVHS_CHROMA_SOURCE}")

# Exact records dropout metadata but leaves TBC samples untouched; correction
# is a separate export-time operation. The maintained overlay follows Exact's
# sample-wise hysteresis and field geometry while retaining the FP32 data plane.
configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay/pipeline/dropout_detect.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/dropout_detect.cu"
    COPYONLY)

# The upstream fidelity pipeline retains an optional host-side K4 source
# reconstruction pass.  cuda-fast is deliberately the CUDA-full route, so
# compile that fallback out instead of relying on mutable process environment.
# Also keep short decodes from reserving a batch much larger than their input,
# and propagate metadata finalization failures through Pipeline::run().
file(READ "${cuvhs_SOURCE_DIR}/src/pipeline/pipeline.cu" CUVHS_PIPELINE_SOURCE)

set(CUVHS_PIPELINE_CONSTRUCTOR [=[Pipeline::Pipeline(const GPUDevice& gpu_, const VideoFormat& fmt_,
                   RawReader& reader_, TBCWriter& writer_)
    : gpu(gpu_), fmt(fmt_), reader(reader_), writer(writer_) {}]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_CONSTRUCTOR}"
    CUVHS_PIPELINE_CONSTRUCTOR_INDEX)
if(CUVHS_PIPELINE_CONSTRUCTOR_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected constructor implementation")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_CONSTRUCTOR}"
    [=[Pipeline::Pipeline(const GPUDevice& gpu_, const VideoFormat& fmt_,
                   RawReader& reader_, TBCWriter& writer_,
                   unsigned int maximum_output_fields_)
    : gpu(gpu_), fmt(fmt_), reader(reader_), writer(writer_),
      maximum_output_fields(maximum_output_fields_) {}]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# Upstream keeps final writer parity in a function-static variable. That leaks
# cadence across two decodes in the same process and flips every isFirstField
# value on alternating runs. Use pipeline-owned state so chunks remain
# continuous while each public ABI invocation starts from a clean cadence.
set(CUVHS_STATIC_WRITER_PARITY [=[    static bool last_parity = false;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_STATIC_WRITER_PARITY}"
    CUVHS_STATIC_WRITER_PARITY_INDEX)
if(CUVHS_STATIC_WRITER_PARITY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected writer parity state")
endif()
string(REPLACE
    "${CUVHS_STATIC_WRITER_PARITY}"
    ""
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
string(REPLACE
    "last_parity"
    "output_last_parity"
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# A precise start_fileloc can land just before a second field.  Keep the
# VSYNC-derived parity instead of relabelling that field as first, and omit the
# incomplete leading field so every emitted TBC starts on a complete frame.
set(CUVHS_WRITER_LOOP_START [=[    for (int i = 0; i < fields_loaded; i++) {
        // Set fileLoc for JSON output (for debugging field position alignment)]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_WRITER_LOOP_START}"
    CUVHS_WRITER_LOOP_START_INDEX)
if(CUVHS_WRITER_LOOP_START_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected writer loop")
endif()
string(REPLACE
    "${CUVHS_WRITER_LOOP_START}"
    [=[    for (int i = 0; i < fields_loaded; i++) {
        if (h_is_first[i] < 0
            || (!output_have_parity && h_is_first[i] == 0)) {
            continue;
        }
        if (maximum_output_fields > 0
            && writer.fields_written() >= maximum_output_fields) {
            break;
        }
        output_have_parity = true;

        // Set fileLoc for JSON output (for debugging field position alignment)]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# Preserve whether K2b obtained its anchor from a complete VBlank pulse train
# or merely projected it from the preceding field.  A complete observed
# VBlank is stronger evidence than nominal cross-chunk cadence: damaged VHS
# can legitimately jump by roughly half a field at an edit/servo break.
set(CUVHS_K2B_ANCHOR_STRUCT [=[struct HostK2bAnchor {
    bool valid = false;
    double line0loc = 0.0;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K2B_ANCHOR_STRUCT}"
    CUVHS_K2B_ANCHOR_STRUCT_INDEX)
if(CUVHS_K2B_ANCHOR_STRUCT_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K2b anchor structure")
endif()
string(REPLACE
    "${CUVHS_K2B_ANCHOR_STRUCT}"
    [=[struct HostK2bAnchor {
    bool valid = false;
    bool observed_vblank = false;
    double line0loc = 0.0;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K2B_ANCHOR_RESULT [=[    out.valid = true;
    out.line0loc = first_hsync_loc - meanlinelen * hsync_start_line;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K2B_ANCHOR_RESULT}"
    CUVHS_K2B_ANCHOR_RESULT_INDEX)
if(CUVHS_K2B_ANCHOR_RESULT_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K2b anchor result")
endif()
string(REPLACE
    "${CUVHS_K2B_ANCHOR_RESULT}"
    [=[    out.valid = true;
    out.observed_vblank = !used_estimated;
    out.line0loc = first_hsync_loc - meanlinelen * hsync_start_line;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_FIELD_DISCOVERY_EMPTY_MARKER [=[    if (chunk_field_offsets.empty()) {]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_FIELD_DISCOVERY_EMPTY_MARKER}"
    CUVHS_FIELD_DISCOVERY_EMPTY_MARKER_INDEX)
if(CUVHS_FIELD_DISCOVERY_EMPTY_MARKER_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected field-discovery fallback")
endif()
string(REPLACE
    "${CUVHS_FIELD_DISCOVERY_EMPTY_MARKER}"
    [=[    // A weak/damaged vertical interval can make the pre-scan miss one or
    // more candidates even though the RF for those fields is present. Do not
    // let a sparse candidate list turn into skipped output fields or a large
    // jump in the next chunk. Estimate local cadence only from adjacent
    // one-field gaps, then restore missing decode windows between anchors. The
    // downstream pulse state machine and hsync refinement still determine the
    // actual geometry; this does not duplicate an already-decoded field.
    if (!chunk_field_offsets.empty()
        && !env_flag_enabled("CUVHS_DISABLE_VSYNC_GAP_FILL")) {
        size_t estimated_spf = spf;
        std::vector<size_t> one_field_gaps;
        one_field_gaps.reserve(chunk_field_offsets.size());
        for (size_t i = 1; i < chunk_field_offsets.size(); ++i) {
            size_t gap = chunk_field_offsets[i] - chunk_field_offsets[i - 1];
            if (gap >= (spf * 3) / 4 && gap <= (spf * 5) / 4) {
                one_field_gaps.push_back(gap);
            }
        }
        if (!one_field_gaps.empty()) {
            const size_t middle = one_field_gaps.size() / 2;
            std::nth_element(
                one_field_gaps.begin(),
                one_field_gaps.begin() + middle,
                one_field_gaps.end());
            estimated_spf = one_field_gaps[middle];
        }

        std::vector<size_t> dense_offsets;
        dense_offsets.reserve((size_t)num_fields + 1U);
        int restored_fields = 0;

        if (raw_offset != 0 && estimated_spf > 0) {
            // Every stitched chunk starts ten lines before the predicted next
            // field. Build a cadence lattice from that known boundary, then
            // snap a window only to a nearby candidate. This rejects the
            // half-field false candidates that otherwise shift a whole batch.
            const size_t expected_first = 10U * (size_t)fmt.samples_per_line;
            const size_t snap_tolerance = estimated_spf / 4U;
            const size_t last_complete_offset =
                (size_t)total_chunk_samples > spf_padded
                    ? (size_t)total_chunk_samples - spf_padded
                    : 0U;
            size_t candidate_cursor = 0;
            for (int field = 0; field < num_fields; ++field) {
                const size_t expected =
                    expected_first + (size_t)field * estimated_spf;
                if (expected > last_complete_offset) break;

                while (candidate_cursor < chunk_field_offsets.size()
                    && chunk_field_offsets[candidate_cursor] + snap_tolerance < expected) {
                    candidate_cursor++;
                }

                size_t selected = expected;
                size_t best_candidate = chunk_field_offsets.size();
                size_t best_distance = snap_tolerance + 1U;
                for (size_t candidate = candidate_cursor;
                     candidate < chunk_field_offsets.size();
                     ++candidate) {
                    const size_t offset = chunk_field_offsets[candidate];
                    if (offset > expected + snap_tolerance) break;
                    const size_t distance = offset > expected
                        ? offset - expected
                        : expected - offset;
                    if (distance < best_distance) {
                        best_distance = distance;
                        best_candidate = candidate;
                    }
                }

                if (best_candidate < chunk_field_offsets.size()) {
                    selected = chunk_field_offsets[best_candidate];
                    candidate_cursor = best_candidate + 1U;
                } else {
                    restored_fields++;
                }
                dense_offsets.push_back(selected);
            }
        } else {
            // The first chunk has no cross-chunk cadence anchor. Preserve its
            // detected phase and fill only unambiguous internal whole-field
            // gaps before the existing startup-context trim runs.
            dense_offsets.push_back(chunk_field_offsets.front());
            for (size_t i = 1; i < chunk_field_offsets.size(); ++i) {
                const size_t previous = chunk_field_offsets[i - 1];
                const size_t current = chunk_field_offsets[i];
                const size_t gap = current - previous;
                size_t intervals = estimated_spf > 0
                    ? (size_t)std::llround((double)gap / (double)estimated_spf)
                    : 1U;
                if (intervals > 1U) {
                    for (size_t slot = 1; slot < intervals; ++slot) {
                        size_t restored = previous + (gap * slot) / intervals;
                        if (restored > dense_offsets.back() && restored < current) {
                            dense_offsets.push_back(restored);
                            restored_fields++;
                        }
                    }
                }
                dense_offsets.push_back(current);
            }
        }

        if (env_flag_enabled("CUVHS_VSYNC_GAP_FILL_LOG")) {
            fprintf(stderr,
                    "  [VSYNC gap fill] raw=%zu detected=%zu dense=%zu first=%zu->%zu restored=%d cadence=%zu\n",
                    raw_offset,
                    chunk_field_offsets.size(),
                    dense_offsets.size(),
                    chunk_field_offsets.front(),
                    dense_offsets.empty() ? size_t{0} : dense_offsets.front(),
                    restored_fields,
                    estimated_spf);
        }
        chunk_field_offsets = std::move(dense_offsets);
    }

    if (chunk_field_offsets.empty()) {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K2B_LOCAL_PULSE_BLOCK [=[            std::vector<HostValidPulse> validpulses = host_refine_pulses_exactish(
                h_pulse_starts_k2b + (size_t)field * MAX_PULSES,
                h_pulse_lengths_k2b + (size_t)field * MAX_PULSES,
                before,
                fmt);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K2B_LOCAL_PULSE_BLOCK}"
    CUVHS_K2B_LOCAL_PULSE_BLOCK_INDEX)
if(CUVHS_K2B_LOCAL_PULSE_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K2b pulse-refinement block")
endif()
string(REPLACE
    "${CUVHS_K2B_LOCAL_PULSE_BLOCK}"
    [=[            // sync_pulses() reports positions in the contiguous chunk,
            // while the copied vhs-decode sync history is defined in the
            // current field window. Keep control calculations local, then add
            // the chunk field offset exactly once at the GPU handoff.
            const size_t field_offset = chunk_field_offsets[(size_t)field];
            std::vector<int> local_pulse_starts((size_t)before);
            for (int pulse = 0; pulse < before; ++pulse) {
                local_pulse_starts[(size_t)pulse] =
                    h_pulse_starts_k2b[(size_t)field * MAX_PULSES + pulse]
                    - (int)field_offset;
            }
            std::vector<HostValidPulse> validpulses = host_refine_pulses_exactish(
                local_pulse_starts.data(),
                h_pulse_lengths_k2b + (size_t)field * MAX_PULSES,
                before,
                fmt);]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K2B_ABSOLUTE_OUTPUT_PULSE
    [=[h_out_starts_k2b[(size_t)field * MAX_PULSES + i] = validpulses[(size_t)i].pulse.start;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K2B_ABSOLUTE_OUTPUT_PULSE}"
    CUVHS_K2B_ABSOLUTE_OUTPUT_PULSE_INDEX)
if(CUVHS_K2B_ABSOLUTE_OUTPUT_PULSE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K2b pulse output assignment")
endif()
string(REPLACE
    "${CUVHS_K2B_ABSOLUTE_OUTPUT_PULSE}"
    [=[h_out_starts_k2b[(size_t)field * MAX_PULSES + i] =
                    validpulses[(size_t)i].pulse.start + (int)field_offset;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# Keep the small host-side sync-control pass deterministic and continuity
# aware.  The pinned prototype snaps every line independently to the nearest
# pulse and falls back to the first raw pulse when a VBlank is weak.  On real
# VHS damage that can lock to a half-line fragment and shear an otherwise
# usable field.  cuda-fast instead projects a missing/outlying field anchor
# from the preceding field, then uses the existing bounded beam tracker for
# the line lattice.  Only pulse summaries cross the PCIe boundary; RF samples
# and all image data stay resident on the GPU.
set(CUVHS_K2B_LINE_TRACK_BLOCK [=[            std::vector<double> locs;
            if (anchor.valid) {
                locs = host_valid_pulses_to_linelocs_exactish(
                    validpulses,
                    anchor.first_hsync_loc,
                    anchor.first_hsync_line,
                    meanlinelen,
                    fmt.lines_per_frame);
                h_out_is_first_k2b[field] = anchor.first_field;
                k2b_prev_first_field = anchor.first_field;
                k2b_prev_progressive_field = anchor.progressive_field;
                k2b_prev_first_hsync_readloc = (long long)(raw_offset + chunk_field_offsets[field]);
                k2b_prev_first_hsync_loc = anchor.first_hsync_loc;
                k2b_prev_first_hsync_diff = anchor.prev_hsync_diff;
            } else {
                locs.resize((size_t)fmt.lines_per_frame);
                double ref = before > 0 ? (double)h_pulse_starts_k2b[(size_t)field * MAX_PULSES] : 0.0;
                for (int line = 0; line < fmt.lines_per_frame; line++) {
                    locs[(size_t)line] = ref + meanlinelen * (double)line;
                }
                h_out_is_first_k2b[field] = (k2b_prev_first_field >= 0) ? (1 - k2b_prev_first_field) : 1;
            }]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K2B_LINE_TRACK_BLOCK}"
    CUVHS_K2B_LINE_TRACK_BLOCK_INDEX)
if(CUVHS_K2B_LINE_TRACK_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K2b line-location block")
endif()
string(REPLACE
    "${CUVHS_K2B_LINE_TRACK_BLOCK}"
    [=[            std::vector<double> locs;
            bool projected_anchor = false;
            const int usable_hsync_count = static_cast<int>(std::count_if(
                validpulses.begin(),
                validpulses.end(),
                [](const HostValidPulse& pulse) {
                    return pulse.good && pulse.type == PULSE_HSYNC;
                }));
            if (k2b_prev_first_hsync_readloc != -1 && meanlinelen > 0.0) {
                const int projected_first_field = k2b_prev_first_field >= 0
                    ? 1 - k2b_prev_first_field
                    : (anchor.first_field >= 0 ? anchor.first_field : 1);
                const double current_field_lines = projected_first_field
                    ? (double)fmt.field_lines_first
                    : (double)fmt.field_lines_second;
                const double previous_field_lines = projected_first_field
                    ? (double)fmt.field_lines_second
                    : (double)fmt.field_lines_first;
                const double projected_field_lines = fmt.system == VideoSystem::NTSC
                    ? previous_field_lines
                    : current_field_lines;
                double projected_first_hsync = std::round(
                    (prev_offset_lines
                        + projected_field_lines
                        + (k2b_prev_first_hsync_loc / meanlinelen))
                    * meanlinelen);
                if (k2b_prev_first_hsync_diff >= -0.5
                    && k2b_prev_first_hsync_diff <= 0.5) {
                    projected_first_hsync += meanlinelen * k2b_prev_first_hsync_diff;
                }

                // Project weak/missing anchors, but never overwrite a complete
                // observed VBlank.  The latter is the only trustworthy way to
                // recover phase after a VHS edit or servo break, where the
                // nominal cadence can be wrong by roughly half a field.
                if (!anchor.valid
                    || (!anchor.observed_vblank
                        && std::abs(anchor.first_hsync_loc - projected_first_hsync)
                            > meanlinelen * 0.45)) {
                    const double first_boundary = fmt.system == VideoSystem::NTSC
                        ? (projected_first_field ? 1.0 : 0.5)
                        : (projected_first_field ? 0.5 : 1.0);
                    const double second_boundary = fmt.system == VideoSystem::NTSC
                        ? (projected_first_field ? 0.5 : 1.0)
                        : (projected_first_field ? 0.5 : 1.0);
                    const double first_hsync_line = first_boundary
                        + ((double)fmt.num_eq_pulses * 1.5)
                        - 0.5
                        + second_boundary;
                    anchor.valid = true;
                    anchor.observed_vblank = false;
                    anchor.first_field = projected_first_field;
                    anchor.progressive_field = 0;
                    anchor.first_hsync_loc = projected_first_hsync;
                    anchor.first_hsync_line = first_hsync_line;
                    anchor.line0loc = projected_first_hsync
                        - meanlinelen * first_hsync_line;
                    anchor.prev_hsync_diff = k2b_prev_first_hsync_diff;
                    projected_anchor = true;
                }
            }

            if (anchor.valid) {
                if (!env_flag_enabled("CUVHS_DISABLE_STABLE_LINE_TRACK")) {
                    K3Debug stable_debug{};
                    const HostLineTimings stable_timings = host_get_line_timings(
                        h_pulse_lengths_k2b + (size_t)field * MAX_PULSES,
                        before,
                        fmt);
                    stable_debug.ref_position = anchor.first_hsync_loc;
                    stable_debug.ref_line = anchor.first_hsync_line;
                    stable_debug.meanlinelen = meanlinelen;
                    stable_debug.hsync_offset = stable_timings.hsync_nominal - fmt.hsync_width;
                    std::vector<int> stable_starts((size_t)after);
                    std::vector<int> stable_lengths((size_t)after);
                    std::vector<int> stable_types((size_t)after);
                    for (int pulse = 0; pulse < after; ++pulse) {
                        stable_starts[(size_t)pulse] = validpulses[(size_t)pulse].pulse.start;
                        stable_lengths[(size_t)pulse] = validpulses[(size_t)pulse].pulse.len;
                        stable_types[(size_t)pulse] = validpulses[(size_t)pulse].type;
                    }
                    HostRefTrackResult stable = host_track_linelocs_reference(
                        stable_debug,
                        stable_starts.data(),
                        stable_lengths.data(),
                        stable_types.data(),
                        after,
                        fmt,
                        HOST_MARKERS_BASE);
                    if (stable.valid
                        && stable.locs.size() >= (size_t)fmt.lines_per_frame) {
                        locs = std::move(stable.locs);
                    }
                }
                if (locs.empty()) {
                    locs = host_valid_pulses_to_linelocs_exactish(
                        validpulses,
                        anchor.first_hsync_loc,
                        anchor.first_hsync_line,
                        meanlinelen,
                        fmt.lines_per_frame);
                }
                h_out_is_first_k2b[field] = anchor.first_field;
                k2b_prev_first_field = anchor.first_field;
                k2b_prev_progressive_field = anchor.progressive_field;
                k2b_prev_first_hsync_readloc = (long long)(raw_offset + chunk_field_offsets[field]);
                k2b_prev_first_hsync_loc = anchor.first_hsync_loc;
                k2b_prev_first_hsync_diff = anchor.prev_hsync_diff;
            } else {
                // A complete horizontal lattice is still a usable picture
                // field when a damaged or synthetic VBlank cannot satisfy the
                // stricter field-order state machine. In contrast, leader
                // noise contains only a small scattering of sync-like pulses;
                // keep scanning instead of fabricating a field from it.
                const bool has_usable_hsync_lattice = usable_hsync_count
                    >= std::max(32, fmt.lines_per_frame / 2);
                k2b_startup_hsync_streak = has_usable_hsync_lattice
                    ? k2b_startup_hsync_streak + 1
                    : 0;
                const bool seed_hsync_cadence = has_usable_hsync_lattice
                    && k2b_startup_hsync_streak >= 2;
                const int first_field = k2b_prev_first_field >= 0
                    ? 1 - k2b_prev_first_field
                    : (seed_hsync_cadence ? 0 : 1);
                const double first_boundary = fmt.system == VideoSystem::NTSC
                    ? (first_field ? 1.0 : 0.5)
                    : (first_field ? 0.5 : 1.0);
                const double second_boundary = fmt.system == VideoSystem::NTSC
                    ? (first_field ? 0.5 : 1.0)
                    : (first_field ? 0.5 : 1.0);
                const double first_hsync_line = first_boundary
                    + ((double)fmt.num_eq_pulses * 1.5)
                    - 0.5
                    + second_boundary;
                double ref = meanlinelen * first_hsync_line;
                locs.resize((size_t)fmt.lines_per_frame);
                for (int line = 0; line < fmt.lines_per_frame; line++) {
                    locs[(size_t)line] = ref
                        + meanlinelen * ((double)line - first_hsync_line);
                }
                // The second consecutive lattice establishes this field as a
                // second field, but it is not emitted without its preceding
                // first field. The next projected field starts a complete
                // frame and retains the correct NTSC colour-frame phase.
                h_out_is_first_k2b[field] = -1;
                if (seed_hsync_cadence) {
                    k2b_prev_first_field = first_field;
                    k2b_prev_progressive_field = 0;
                    k2b_prev_first_hsync_readloc =
                        (long long)(raw_offset + chunk_field_offsets[field]);
                    k2b_prev_first_hsync_loc = ref;
                    k2b_prev_first_hsync_diff = 0.0;
                }
            }
            if (anchor.valid
                && (!writer.accepts_device_fields()
                    || env_flag_enabled("CUVHS_FORCE_PREVIEW_LINE_PHASE_GUARD"))
                && !env_flag_enabled("CUVHS_DISABLE_LINE_PHASE_GUARD")) {
                // A real VHS time-base error moves gradually. A sudden
                // quarter/half-line phase switch is a false sync train in the
                // picture, even when its spacing looks nominal. Bound both the
                // absolute correction and its line-to-line slew; K4 still
                // performs the final sub-microsecond edge refinement on GPU.
                double previous_correction = 0.0;
                for (int line = 0; line < fmt.lines_per_frame; ++line) {
                    const double expected = anchor.first_hsync_loc
                        + meanlinelen * ((double)line - anchor.first_hsync_line);
                    double correction = locs[(size_t)line] - expected;
                    correction = std::clamp(
                        correction,
                        -meanlinelen * 0.18f,
                        meanlinelen * 0.18f);
                    if (line > 0
                        && std::abs(correction - previous_correction)
                            > meanlinelen * 0.12f) {
                        correction = previous_correction;
                    }
                    locs[(size_t)line] = expected + correction;
                    previous_correction = correction;
                }
            }
            for (double& loc : locs) {
                loc += (double)field_offset;
            }
            if (log_k2b_host && projected_anchor) {
                fprintf(stderr,
                        "  [K2b continuity] chunk raw_offset=%zu field=%d projected anchor=%.1f mean=%.2f\n",
                        raw_offset,
                        field,
                        anchor.first_hsync_loc,
                        meanlinelen);
            }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_DESTRUCTOR [=[Pipeline::~Pipeline() {
    free_buffers();
}]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_DESTRUCTOR}"
    CUVHS_PIPELINE_DESTRUCTOR_INDEX)
if(CUVHS_PIPELINE_DESTRUCTOR_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected destructor")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_DESTRUCTOR}"
    [=[Pipeline::~Pipeline() {
    chroma_state_release(&chroma_state);
    free_buffers();
}]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_REMAINING_FIELDS [=[            fields_this_chunk = std::min(batch_size, remaining_fields);
        }

        size_t next_raw_offset;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_REMAINING_FIELDS}"
    CUVHS_PIPELINE_REMAINING_FIELDS_INDEX)
if(CUVHS_PIPELINE_REMAINING_FIELDS_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected remaining-field block")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_REMAINING_FIELDS}"
    [=[            fields_this_chunk = std::min(batch_size, remaining_fields);
        }

        if (maximum_output_fields > 0) {
            const unsigned int written = static_cast<unsigned int>(writer.fields_written());
            if (written >= maximum_output_fields) break;
            const unsigned int needed = maximum_output_fields - written;
            fields_this_chunk = std::min(
                fields_this_chunk,
                static_cast<int>(needed + 4U));
        }

        size_t next_raw_offset;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# A bounded managed decode deliberately exposes the remaining input so the
# native pipeline can scan past leader/no-signal regions. Report progress
# against the requested output contract, not the whole remaining recording.
set(CUVHS_PIPELINE_PROGRESS_START [=[    if (streaming) {
        fprintf(stderr, "Starting decode: streaming mode, chunks of %d fields\n", batch_size);
    } else {
        fprintf(stderr, "Starting decode: ~%d fields in chunks of %d\n",
                total_fields_est, batch_size);
    }]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_PROGRESS_START}"
    CUVHS_PIPELINE_PROGRESS_START_INDEX)
if(CUVHS_PIPELINE_PROGRESS_START_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress header")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_PROGRESS_START}"
    [=[    if (!writer.accepts_device_fields()) {
        if (maximum_output_fields > 0) {
            fprintf(stderr, "Starting decode: target %u output fields in chunks of %d\n",
                    maximum_output_fields, batch_size);
        } else if (streaming) {
            fprintf(stderr, "Starting decode: streaming mode, chunks of %d fields\n", batch_size);
        } else {
            fprintf(stderr, "Starting decode: ~%d fields in chunks of %d\n",
                    total_fields_est, batch_size);
        }
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_PROGRESS_BODY [=[        if (streaming) {
            fprintf(stderr, "  %d fields | %.1f FPS | %.0fs elapsed (streaming)\n\033[K\n",
                    total_fields, fps, elapsed);
        } else {
            double pct = 100.0 * total_fields / total_fields_est;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_PROGRESS_BODY}"
    CUVHS_PIPELINE_PROGRESS_BODY_INDEX)
if(CUVHS_PIPELINE_PROGRESS_BODY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress body")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_PROGRESS_BODY}"
    [=[        if (maximum_output_fields > 0) {
            const unsigned int written = static_cast<unsigned int>(writer.fields_written());
            const float pct = 100.0f * (float)written / (float)maximum_output_fields;
            const float output_fps = (written / 2.0f) / elapsed;
            const float eta = written > 0
                ? elapsed * (float)(maximum_output_fields - written) / (float)written
                : 0.0f;
            int bar_fill = (int)(pct * 40.0f / 100.0f);
            if (bar_fill > 40) bar_fill = 40;
            char bar[42];
            for (int i = 0; i < 40; i++) bar[i] = (i < bar_fill) ? '#' : '-';
            bar[40] = '\0';
            if (written > 0) {
                fprintf(stderr,
                        "  %u/%u output fields (%.1f%%) | %d scanned | %.1f FPS | ETA %d:%02d\n",
                        written,
                        maximum_output_fields,
                        pct,
                        total_fields,
                        output_fps,
                        (int)(eta / 60.0f),
                        (int)eta % 60);
            } else {
                fprintf(stderr,
                        "  0/%u output fields | %d scanned | awaiting stable sync\n",
                        maximum_output_fields,
                        total_fields);
            }
            fprintf(stderr, "\033[K  [%s]\n", bar);
        } else if (streaming) {
            fprintf(stderr, "  %d fields | %.1f FPS | %.0fs elapsed (streaming)\n\033[K\n",
                    total_fields, fps, elapsed);
        } else {
            float pct = 100.0 * total_fields / total_fields_est;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_PROGRESS_END [=[    fprintf(stderr, "Decode complete: %d fields in %.1f seconds (%.1f FPS)\n\033[K\n",
            total_fields, total_time, (total_fields / 2.0) / total_time);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_PROGRESS_END}"
    CUVHS_PIPELINE_PROGRESS_END_INDEX)
if(CUVHS_PIPELINE_PROGRESS_END_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress summary")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_PROGRESS_END}"
    [=[    if (maximum_output_fields > 0) {
        const size_t written = writer.fields_written();
        fprintf(stderr,
                "Decode complete: %zu output fields (%d scanned) in %.1f seconds (%.1f FPS)\n\033[K\n",
                written,
                total_fields,
                total_time,
                (written / 2.0) / total_time);
    } else {
        fprintf(stderr, "Decode complete: %d fields in %.1f seconds (%.1f FPS)\n\033[K\n",
                total_fields, total_time, (total_fields / 2.0) / total_time);
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PREVIEW_PROGRESS_PRIMER [=[    // Progress display: print two lines initially so \033[A can move up
    fprintf(stderr, "\n\n");
    fflush(stderr);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PREVIEW_PROGRESS_PRIMER}"
    CUVHS_PREVIEW_PROGRESS_PRIMER_INDEX)
if(CUVHS_PREVIEW_PROGRESS_PRIMER_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress primer")
endif()
string(REPLACE
    "${CUVHS_PREVIEW_PROGRESS_PRIMER}"
    [=[    if (!writer.accepts_device_fields()) {
        // Progress display: print two lines initially so \033[A can move up
        fprintf(stderr, "\n\n");
        fflush(stderr);
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PREVIEW_PROGRESS_BODY_BEGIN
    "        // Progress dashboard (two lines, rewritten in-place)")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PREVIEW_PROGRESS_BODY_BEGIN}"
    CUVHS_PREVIEW_PROGRESS_BODY_BEGIN_INDEX)
if(CUVHS_PREVIEW_PROGRESS_BODY_BEGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress-dashboard start")
endif()
string(REPLACE
    "${CUVHS_PREVIEW_PROGRESS_BODY_BEGIN}"
    [=[        if (!writer.accepts_device_fields()) {
        // Progress dashboard (two lines, rewritten in-place)]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PREVIEW_PROGRESS_BODY_END [=[        fflush(stderr);
    }

    auto t_end = std::chrono::steady_clock::now();]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PREVIEW_PROGRESS_BODY_END}"
    CUVHS_PREVIEW_PROGRESS_BODY_END_INDEX)
if(CUVHS_PREVIEW_PROGRESS_BODY_END_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress-dashboard end")
endif()
string(REPLACE
    "${CUVHS_PREVIEW_PROGRESS_BODY_END}"
    [=[        fflush(stderr);
        }
    }

    auto t_end = std::chrono::steady_clock::now();]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PREVIEW_PROGRESS_SUMMARY_BEGIN
    "    // Final summary (overwrite the progress lines)")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PREVIEW_PROGRESS_SUMMARY_BEGIN}"
    CUVHS_PREVIEW_PROGRESS_SUMMARY_BEGIN_INDEX)
if(CUVHS_PREVIEW_PROGRESS_SUMMARY_BEGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress-summary start")
endif()
string(REPLACE
    "${CUVHS_PREVIEW_PROGRESS_SUMMARY_BEGIN}"
    [=[    if (!writer.accepts_device_fields()) {
    // Final summary (overwrite the progress lines)]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PREVIEW_PROGRESS_SUMMARY_END [=[    fflush(stderr);

    writer.finalize();
    return true;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PREVIEW_PROGRESS_SUMMARY_END}"
    CUVHS_PREVIEW_PROGRESS_SUMMARY_END_INDEX)
if(CUVHS_PREVIEW_PROGRESS_SUMMARY_END_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected progress-summary end")
endif()
string(REPLACE
    "${CUVHS_PREVIEW_PROGRESS_SUMMARY_END}"
    [=[    fflush(stderr);
    }

    writer.finalize();
    return true;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_CPU_REBUILD_GUARD
    "if (!env_flag_enabled(\"CUVHS_DISABLE_K4_SOURCE_REBUILD\")) {")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_CPU_REBUILD_GUARD}"
    CUVHS_CPU_REBUILD_GUARD_INDEX)
if(CUVHS_CPU_REBUILD_GUARD_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected K4 CPU rebuild guard")
endif()
string(REPLACE
    "${CUVHS_CPU_REBUILD_GUARD}"
    "if constexpr (false) {  // vhs-decode-dotnet cuda-fast: GPU source only"
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_BATCH_SELECTION
    "        batch_size = batch_override > 0 ? batch_override : gpu.max_batch_size(bytes_per_field_est, fraction);")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_BATCH_SELECTION}"
    CUVHS_BATCH_SELECTION_INDEX)
if(CUVHS_BATCH_SELECTION_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected batch selection")
endif()
set(CUVHS_BATCH_CAP [=[

        // Preview windows run faster at 12 fields on the validated PAL GPU
        // workload while using less host CPU. Full CUDA-fast decode retains
        // the validated 16-field cap. Larger batches also let the opposite
        // VBlank train influence one geometry solve and can produce chroma
        // discontinuities. Keep an explicit diagnostic override available.
        if (batch_override <= 0) {
            const int automatic_batch_cap = writer.accepts_device_fields()
                    && reader.device_decimation_factor() == 2
                ? 12
                : 16;
            batch_size = std::min(batch_size, automatic_batch_cap);
        }

        // A very short managed window does not need a full-device allocation.
        // Four extra fields retain cuVHS look-ahead/context without changing
        // the normal long-decode batch size.
        if (!reader.is_stream() && fmt.samples_per_field > 0) {
            const size_t input_fields =
                reader.total_samples() / static_cast<size_t>(fmt.samples_per_field)
                + ((reader.total_samples() % static_cast<size_t>(fmt.samples_per_field)) != 0 ? 1 : 0);
            if (input_fields < static_cast<size_t>(batch_size)) {
                batch_size = static_cast<int>(std::min(
                    static_cast<size_t>(batch_size),
                    input_fields + 4));
            }
        }]=])
string(REPLACE
    "${CUVHS_BATCH_SELECTION}"
    "${CUVHS_BATCH_SELECTION}${CUVHS_BATCH_CAP}"
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_FINALIZE_SEQUENCE
    "    writer.finalize();\n    return true;")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_FINALIZE_SEQUENCE}"
    CUVHS_FINALIZE_SEQUENCE_INDEX)
if(CUVHS_FINALIZE_SEQUENCE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected writer finalization sequence")
endif()
string(REPLACE
    "${CUVHS_FINALIZE_SEQUENCE}"
    "    return writer.finalize();"
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_DROPOUT_CALL [=[    dropout_detect(static_cast<double*>(d_envelope),
                   static_cast<double*>(d_linelocs),
                   static_cast<uint16_t*>(d_tbc_luma),
                   static_cast<uint16_t*>(d_tbc_chroma),
                   static_cast<int*>(d_do_lines),
                   static_cast<int*>(d_do_starts),
                   static_cast<int*>(d_do_ends),
                   static_cast<int*>(d_do_count),
                   fields_loaded, spf_padded, fmt);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_DROPOUT_CALL}"
    CUVHS_DROPOUT_CALL_INDEX)
if(CUVHS_DROPOUT_CALL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected dropout call")
endif()
string(REPLACE
    "${CUVHS_DROPOUT_CALL}"
    [=[    if (!dropout_detect(static_cast<double*>(d_envelope),
                   static_cast<double*>(d_linelocs),
                   d_field_offsets,
                   static_cast<int*>(d_is_first_field),
                   static_cast<int*>(d_do_lines),
                   static_cast<int*>(d_do_starts),
                   static_cast<int*>(d_do_ends),
                   static_cast<int*>(d_do_count),
                   fields_loaded,
                   spf_padded,
                   static_cast<size_t>(demod_fields_loaded) * spf_padded,
                   fmt)) {
        delete[] h_k3_debug;
        if (d_k3_debug) cudaFree(d_k3_debug);
        cudaFree(d_field_offsets);
        return -1;
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PROCESS_RESULT
    "        if (processed == 0) break;  // EOF or error")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PROCESS_RESULT}"
    CUVHS_PROCESS_RESULT_INDEX)
if(CUVHS_PROCESS_RESULT_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer contains the expected chunk result check")
endif()
string(REPLACE
    "${CUVHS_PROCESS_RESULT}"
    [=[        if (processed < 0) {
            free_buffers();
            return false;
        }
        if (processed == 0) break;  // EOF]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
vhsdecode_cuda_fast_convert_pipeline_to_fp32(CUVHS_PIPELINE_SOURCE)

set(CUVHS_CHROMA_DECODE_CALL [=[    chroma_decode(static_cast<float*>(d_demod_burst),
                  static_cast<float*>(d_linelocs),
                  static_cast<int*>(d_is_first_field),
                  static_cast<uint16_t*>(d_tbc_chroma),
                  fields_loaded,
                  demod_fields_loaded * (int)spf_padded,
                  fmt,
                  field_phase_ids,
                  &chroma_state,
                  raw_offset);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_CHROMA_DECODE_CALL}"
    CUVHS_CHROMA_DECODE_CALL_INDEX)
if(CUVHS_CHROMA_DECODE_CALL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected chroma decode call")
endif()
string(REPLACE
    "${CUVHS_CHROMA_DECODE_CALL}"
    [=[    if (!chroma_decode(static_cast<float*>(d_demod_burst),
                  static_cast<float*>(d_linelocs),
                  static_cast<int*>(d_is_first_field),
                  static_cast<uint16_t*>(d_tbc_chroma),
                  fields_loaded,
                  demod_fields_loaded * (int)spf_padded,
                  fmt,
                  field_phase_ids,
                  &chroma_state,
                  raw_offset)) {
        fprintf(stderr, "CUDA-fast chroma decode failed\n");
        delete[] h_k3_debug;
        if (d_k3_debug) cudaFree(d_k3_debug);
        cudaFree(d_field_offsets);
        return -1;
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# The managed FLAC reader can now return libsndfile's native PCM16 samples.
# Upload those two-byte values directly and perform the exact int16-to-FP32
# conversion on the default CUDA stream, avoiding a CPU conversion pass and
# halving the input transfer. The FLOAT32 callback remains an A/B fallback.
set(CUVHS_RAW_ESTIMATE_LINE
    "    size_t raw = (spf_padded + FM_DEMOD_OVERLAP_SAMPLES) * sizeof(float);  // input + lead context")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_ESTIMATE_LINE}"
    CUVHS_RAW_ESTIMATE_LINE_INDEX)
if(CUVHS_RAW_ESTIMATE_LINE_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected raw VRAM estimate")
endif()
string(REPLACE
    "${CUVHS_RAW_ESTIMATE_LINE}"
    [=[    size_t raw = (spf_padded + FM_DEMOD_OVERLAP_SAMPLES) * sizeof(float);  // FP32 input + lead context
    const size_t source_staging_samples = reader.device_decimation_factor() == 2
        ? 2 * (spf_padded + FM_DEMOD_OVERLAP_SAMPLES) + 30
        : spf_padded + FM_DEMOD_OVERLAP_SAMPLES;
    size_t raw_s16 = reader.callback_returns_int16()
        ? source_staging_samples * sizeof(int16_t)
        : 0;
    size_t raw_source_f32 = !reader.callback_returns_int16()
            && reader.device_decimation_factor() == 2
        ? source_staging_samples * sizeof(float)
        : 0;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
set(CUVHS_RAW_ESTIMATED_TOTAL
    "        raw + demod + demod_05 + demod + envelope + pulses + linelocs + tbc + chroma +")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_ESTIMATED_TOTAL}"
    CUVHS_RAW_ESTIMATED_TOTAL_INDEX)
if(CUVHS_RAW_ESTIMATED_TOTAL_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected VRAM total")
endif()
string(REPLACE
    "${CUVHS_RAW_ESTIMATED_TOTAL}"
    "        raw + raw_s16 + raw_source_f32 + demod + demod_05 + demod + envelope + pulses + linelocs + tbc + chroma +"
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_DEVICE_ALLOCATION
    "        alloc(&d_raw,        (n * spf_padded + 2 * FM_DEMOD_OVERLAP_SAMPLES) * sizeof(float));")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_DEVICE_ALLOCATION}"
    CUVHS_RAW_DEVICE_ALLOCATION_INDEX)
if(CUVHS_RAW_DEVICE_ALLOCATION_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected raw allocation")
endif()
string(REPLACE
    "${CUVHS_RAW_DEVICE_ALLOCATION}"
    [=[        alloc(&d_raw,        (n * spf_padded + 2 * FM_DEMOD_OVERLAP_SAMPLES) * sizeof(float));
        const size_t raw_logical_capacity =
            n * spf_padded + 2 * FM_DEMOD_OVERLAP_SAMPLES;
        const size_t raw_source_capacity = reader.device_decimation_factor() == 2
            ? 2 * raw_logical_capacity + 30
            : raw_logical_capacity;
        if (reader.callback_returns_int16()) {
            alloc(&d_raw_s16, raw_source_capacity * sizeof(int16_t));
        } else if (reader.device_decimation_factor() == 2) {
            alloc(&d_raw_source_f32, raw_source_capacity * sizeof(float));
        }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
set(CUVHS_RAW_DEVICE_FREE "    safe_free(&d_raw);")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_DEVICE_FREE}"
    CUVHS_RAW_DEVICE_FREE_INDEX)
if(CUVHS_RAW_DEVICE_FREE_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected raw release")
endif()
string(REPLACE
    "${CUVHS_RAW_DEVICE_FREE}"
    [=[    stop_raw_prefetch();
    delete[] h_prefetch_raw_s16;
    h_prefetch_raw_s16 = nullptr;
    h_prefetch_capacity_samples = 0;
    safe_free(&d_raw);
    safe_free(&d_raw_s16);
    safe_free(&d_raw_source_f32);
    h_half_rate_raw_s16.clear();
    h_half_rate_raw_f32.clear();]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_HOST_UPLOAD [=[    // Read contiguous raw: num_fields * spf_padded (each field needs spf_padded samples)
    size_t total_samples = (size_t)num_fields * spf_padded;
    auto* h_raw = new float[lead_samples + total_samples + tail_samples];

    if (lead_samples > 0) {
        reader.read_at(h_raw, raw_offset - lead_samples, lead_samples);
    }
    size_t n_read = reader.read_at(h_raw + lead_samples, raw_offset, total_samples);
    if (n_read < spf) {
        delete[] h_raw;
        return 0;
    }
    if (n_read < total_samples) {
        memset(h_raw + lead_samples + n_read, 0, (total_samples - n_read) * sizeof(float));
    }
    size_t tail_read = reader.read_at(h_raw + lead_samples + total_samples, raw_offset + total_samples, tail_samples);
    if (tail_read < tail_samples) {
        memset(h_raw + lead_samples + total_samples + tail_read, 0, (tail_samples - tail_read) * sizeof(float));
    }

    int fields_loaded = (int)(n_read / spf);
    if (fields_loaded > num_fields) fields_loaded = num_fields;
    if (fields_loaded == 0) {
        delete[] h_raw;
        return 0;
    }
    const int demod_fields_loaded = fields_loaded;

    // Upload contiguous raw to GPU
    cudaMemcpy(d_raw, h_raw, (lead_samples + total_samples + tail_samples) * sizeof(float), cudaMemcpyHostToDevice);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_HOST_UPLOAD}"
    CUVHS_RAW_HOST_UPLOAD_INDEX)
if(CUVHS_RAW_HOST_UPLOAD_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected host raw upload block")
endif()
string(REPLACE
    "${CUVHS_RAW_HOST_UPLOAD}"
    [=[    // Read contiguous raw: num_fields * spf_padded (each field needs spf_padded samples)
    size_t total_samples = (size_t)num_fields * spf_padded;
    const size_t raw_sample_count = lead_samples + total_samples + tail_samples;
    float* h_raw = nullptr;
    int16_t* h_raw_s16 = nullptr;
    size_t n_read = 0;
    size_t tail_read = 0;

    if (reader.callback_returns_int16()) {
        h_raw_s16 = new int16_t[raw_sample_count];
        if (lead_samples > 0) {
            reader.read_raw_at(h_raw_s16, raw_offset - lead_samples, lead_samples);
        }
        n_read = reader.read_raw_at(h_raw_s16 + lead_samples, raw_offset, total_samples);
        if (n_read < total_samples) {
            memset(h_raw_s16 + lead_samples + n_read, 0,
                   (total_samples - n_read) * sizeof(int16_t));
        }
        tail_read = reader.read_raw_at(
            h_raw_s16 + lead_samples + total_samples,
            raw_offset + total_samples,
            tail_samples);
        if (tail_read < tail_samples) {
            memset(h_raw_s16 + lead_samples + total_samples + tail_read, 0,
                   (tail_samples - tail_read) * sizeof(int16_t));
        }
    } else {
        h_raw = new float[raw_sample_count];
        if (lead_samples > 0) {
            reader.read_at(h_raw, raw_offset - lead_samples, lead_samples);
        }
        n_read = reader.read_at(h_raw + lead_samples, raw_offset, total_samples);
        if (n_read < total_samples) {
            memset(h_raw + lead_samples + n_read, 0,
                   (total_samples - n_read) * sizeof(float));
        }
        tail_read = reader.read_at(
            h_raw + lead_samples + total_samples,
            raw_offset + total_samples,
            tail_samples);
        if (tail_read < tail_samples) {
            memset(h_raw + lead_samples + total_samples + tail_read, 0,
                   (tail_samples - tail_read) * sizeof(float));
        }
    }

    if (n_read < spf) {
        delete[] h_raw_s16;
        delete[] h_raw;
        return 0;
    }
    int fields_loaded = (int)(n_read / spf);
    if (fields_loaded > num_fields) fields_loaded = num_fields;
    if (fields_loaded == 0) {
        delete[] h_raw_s16;
        delete[] h_raw;
        return 0;
    }
    const int demod_fields_loaded = fields_loaded;

    // Upload contiguous raw to GPU. PCM16 uses half the PCIe payload, then an
    // exact integer-to-FP32 kernel feeds the same d_raw signal buffer.
    cudaError_t raw_upload_status;
    if (h_raw_s16 != nullptr) {
        raw_upload_status = cudaMemcpy(
            d_raw_s16, h_raw_s16, raw_sample_count * sizeof(int16_t), cudaMemcpyHostToDevice);
        if (raw_upload_status == cudaSuccess
            && !cuda_fast_convert_s16_to_float(
                static_cast<const int16_t*>(d_raw_s16),
                static_cast<float*>(d_raw),
                raw_sample_count)) {
            raw_upload_status = cudaPeekAtLastError();
            if (raw_upload_status == cudaSuccess) raw_upload_status = cudaErrorInvalidValue;
        }
        delete[] h_raw_s16;
        h_raw_s16 = nullptr;
    } else {
        raw_upload_status = cudaMemcpy(
            d_raw, h_raw, raw_sample_count * sizeof(float), cudaMemcpyHostToDevice);
    }
    if (raw_upload_status != cudaSuccess) {
        std::fprintf(stderr, "CUDA-fast raw upload/conversion failed: %s\n",
                     cudaGetErrorString(raw_upload_status));
        delete[] h_raw;
        return -1;
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# The managed callback reads seekable PCM16 input in bounded chunks. Overlap
# the next chunk's disk/decompression work with the current chunk's GPU tail.
# CUVHS_DISABLE_RF_PREFETCH provides an internal same-binary A/B control.
set(CUVHS_RAW_PREFETCH_DECLS [=[    int16_t* h_raw_s16 = nullptr;
    size_t n_read = 0;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_PREFETCH_DECLS}"
    CUVHS_RAW_PREFETCH_DECLS_INDEX)
if(CUVHS_RAW_PREFETCH_DECLS_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected PCM16 host declarations")
endif()
string(REPLACE
    "${CUVHS_RAW_PREFETCH_DECLS}"
    [=[    int16_t* h_raw_s16 = nullptr;
    bool h_raw_s16_is_prefetched = false;
    size_t n_read = 0;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_DIRECT_READ [=[    if (reader.callback_returns_int16()) {
        h_raw_s16 = new int16_t[raw_sample_count];
        if (lead_samples > 0) {
            reader.read_raw_at(h_raw_s16, raw_offset - lead_samples, lead_samples);
        }
        n_read = reader.read_raw_at(h_raw_s16 + lead_samples, raw_offset, total_samples);
        if (n_read < total_samples) {
            memset(h_raw_s16 + lead_samples + n_read, 0,
                   (total_samples - n_read) * sizeof(int16_t));
        }
        tail_read = reader.read_raw_at(
            h_raw_s16 + lead_samples + total_samples,
            raw_offset + total_samples,
            tail_samples);
        if (tail_read < tail_samples) {
            memset(h_raw_s16 + lead_samples + total_samples + tail_read, 0,
                   (tail_samples - tail_read) * sizeof(int16_t));
        }
    } else {]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_DIRECT_READ}"
    CUVHS_RAW_DIRECT_READ_INDEX)
if(CUVHS_RAW_DIRECT_READ_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected PCM16 direct-read block")
endif()
string(REPLACE
    "${CUVHS_RAW_DIRECT_READ}"
    [=[    if (reader.callback_returns_int16()) {
        if (prefetch_valid
            && prefetch_raw_offset == raw_offset
            && prefetch_num_fields >= num_fields
            && prefetch_lead_samples == lead_samples
            && prefetch_total_samples >= total_samples
            && prefetch_tail_samples == tail_samples) {
            if (raw_prefetch_thread.joinable()) raw_prefetch_thread.join();
            h_raw_s16 = h_prefetch_raw_s16;
            h_raw_s16_is_prefetched = true;
            n_read = std::min(prefetch_n_read, total_samples);
            if (prefetch_total_samples == total_samples) {
                tail_read = prefetch_tail_read;
            } else if (prefetch_n_read > total_samples) {
                tail_read = std::min(
                    prefetch_n_read - total_samples,
                    tail_samples);
            }
            prefetch_valid = false;
        } else {
            stop_raw_prefetch();
            h_raw_s16 = new int16_t[raw_sample_count];
            if (lead_samples > 0) {
                reader.read_raw_at(h_raw_s16, raw_offset - lead_samples, lead_samples);
            }
            n_read = reader.read_raw_at(h_raw_s16 + lead_samples, raw_offset, total_samples);
            if (n_read < total_samples) {
                memset(h_raw_s16 + lead_samples + n_read, 0,
                       (total_samples - n_read) * sizeof(int16_t));
            }
            tail_read = reader.read_raw_at(
                h_raw_s16 + lead_samples + total_samples,
                raw_offset + total_samples,
                tail_samples);
            if (tail_read < tail_samples) {
                memset(h_raw_s16 + lead_samples + total_samples + tail_read, 0,
                       (tail_samples - tail_read) * sizeof(int16_t));
            }
        }
    } else {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_DELETE_ON_EARLY_RETURN [=[        delete[] h_raw_s16;
        delete[] h_raw;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_DELETE_ON_EARLY_RETURN}"
    CUVHS_RAW_DELETE_ON_EARLY_RETURN_INDEX)
if(CUVHS_RAW_DELETE_ON_EARLY_RETURN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected early raw-buffer cleanup")
endif()
string(REPLACE
    "${CUVHS_RAW_DELETE_ON_EARLY_RETURN}"
    [=[        if (!h_raw_s16_is_prefetched) delete[] h_raw_s16;
        delete[] h_raw;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_DELETE_AFTER_UPLOAD [=[        delete[] h_raw_s16;
        h_raw_s16 = nullptr;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_DELETE_AFTER_UPLOAD}"
    CUVHS_RAW_DELETE_AFTER_UPLOAD_INDEX)
if(CUVHS_RAW_DELETE_AFTER_UPLOAD_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected uploaded raw-buffer cleanup")
endif()
string(REPLACE
    "${CUVHS_RAW_DELETE_AFTER_UPLOAD}"
    [=[        if (!h_raw_s16_is_prefetched) delete[] h_raw_s16;
        h_raw_s16 = nullptr;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_HALF_RATE_READ_ENTRY [=[    size_t tail_read = 0;

    if (reader.callback_returns_int16()) {]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_HALF_RATE_READ_ENTRY}"
    CUVHS_HALF_RATE_READ_ENTRY_INDEX)
if(CUVHS_HALF_RATE_READ_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected GPU half-rate read insertion point")
endif()
string(REPLACE
    "${CUVHS_HALF_RATE_READ_ENTRY}"
    [=[    size_t tail_read = 0;
    const bool gpu_half_rate_input = reader.device_decimation_factor() == 2;

    if (gpu_half_rate_input) {
        size_t logical_samples_read = 0;
        const bool use_half_rate_prefetch = prefetch_valid
            && prefetch_half_rate
            && prefetch_raw_offset == raw_offset
            && prefetch_num_fields >= num_fields
            && prefetch_lead_samples == lead_samples
            && prefetch_total_samples >= total_samples
            && prefetch_tail_samples == tail_samples;
        if (use_half_rate_prefetch) {
            if (raw_prefetch_thread.joinable()) raw_prefetch_thread.join();
            if (!prefetch_half_rate_read_ok
                || !cuda_fast_upload_half_rate_s16(
                    h_half_rate_raw_s16,
                    raw_sample_count,
                    static_cast<int16_t*>(d_raw_s16),
                    static_cast<float*>(d_raw))) {
                return -1;
            }
            logical_samples_read = std::min(
                prefetch_half_rate_logical_samples_read,
                raw_sample_count);
            prefetch_valid = false;
            prefetch_half_rate = false;
            prefetch_half_rate_read_ok = false;
        } else {
            stop_raw_prefetch();
            if (!cuda_fast_read_upload_half_rate(
                    reader,
                    raw_offset - lead_samples,
                    raw_sample_count,
                    static_cast<int16_t*>(d_raw_s16),
                    static_cast<float*>(d_raw_source_f32),
                    static_cast<float*>(d_raw),
                    h_half_rate_raw_s16,
                    h_half_rate_raw_f32,
                    &logical_samples_read)) {
                return -1;
            }
        }
        const size_t available = raw_offset < reader.total_samples()
            ? reader.total_samples() - raw_offset
            : 0;
        n_read = std::min(total_samples, available);
        tail_read = logical_samples_read > lead_samples + n_read
            ? std::min(tail_samples, logical_samples_read - lead_samples - n_read)
            : 0;
    } else if (reader.callback_returns_int16()) {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_HALF_RATE_UPLOAD_ENTRY [=[    cudaError_t raw_upload_status;
    if (h_raw_s16 != nullptr) {]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_HALF_RATE_UPLOAD_ENTRY}"
    CUVHS_HALF_RATE_UPLOAD_ENTRY_INDEX)
if(CUVHS_HALF_RATE_UPLOAD_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected GPU half-rate upload insertion point")
endif()
string(REPLACE
    "${CUVHS_HALF_RATE_UPLOAD_ENTRY}"
    [=[    cudaError_t raw_upload_status = cudaSuccess;
    if (gpu_half_rate_input) {
        // The native-rate RF was uploaded and decimated directly into d_raw.
    } else if (h_raw_s16 != nullptr) {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PROCESS_CHUNK_ENTRY
    "int Pipeline::process_chunk(size_t raw_offset, int num_fields, size_t& next_raw_offset) {")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PROCESS_CHUNK_ENTRY}"
    CUVHS_RAW_PREFETCH_ENTRY_INDEX)
if(CUVHS_RAW_PREFETCH_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected prefetch insertion point")
endif()
string(REPLACE
    "${CUVHS_PROCESS_CHUNK_ENTRY}"
    [=[void Pipeline::stop_raw_prefetch() {
    if (raw_prefetch_thread.joinable()) raw_prefetch_thread.join();
    prefetch_half_rate = false;
    prefetch_half_rate_read_ok = false;
    prefetch_valid = false;
}

void Pipeline::start_raw_prefetch(size_t raw_offset, int num_fields) {
    stop_raw_prefetch();
    if (!reader.callback_returns_int16()
        || num_fields <= 0
        || env_flag_enabled("CUVHS_DISABLE_RF_PREFETCH")) {
        return;
    }

    const size_t spf = fmt.samples_per_field;
    const size_t spf_padded = spf + FIELD_MARGIN_LINES * fmt.samples_per_line;
    const size_t lead_samples = std::min(
        raw_offset,
        static_cast<size_t>(FM_DEMOD_OVERLAP_SAMPLES));
    const size_t total_samples = static_cast<size_t>(num_fields) * spf_padded;
    const size_t tail_samples = static_cast<size_t>(FM_DEMOD_OVERLAP_SAMPLES);
    const size_t raw_sample_count = lead_samples + total_samples + tail_samples;

    prefetch_raw_offset = raw_offset;
    prefetch_num_fields = num_fields;
    prefetch_lead_samples = lead_samples;
    prefetch_total_samples = total_samples;
    prefetch_tail_samples = tail_samples;
    prefetch_n_read = 0;
    prefetch_tail_read = 0;
    prefetch_half_rate_logical_samples_read = 0;
    prefetch_half_rate = reader.device_decimation_factor() == 2;
    prefetch_half_rate_read_ok = false;
    prefetch_valid = true;
    if (prefetch_half_rate) {
        raw_prefetch_thread = std::thread([this, raw_sample_count]() {
            prefetch_half_rate_read_ok = cuda_fast_read_half_rate_s16(
                reader,
                prefetch_raw_offset - prefetch_lead_samples,
                raw_sample_count,
                h_half_rate_raw_s16,
                &prefetch_half_rate_logical_samples_read);
        });
        return;
    }

    if (h_prefetch_capacity_samples < raw_sample_count) {
        delete[] h_prefetch_raw_s16;
        h_prefetch_raw_s16 = new int16_t[raw_sample_count];
        h_prefetch_capacity_samples = raw_sample_count;
    }
    raw_prefetch_thread = std::thread([this]() {
        if (prefetch_lead_samples > 0) {
            reader.read_raw_at(
                h_prefetch_raw_s16,
                prefetch_raw_offset - prefetch_lead_samples,
                prefetch_lead_samples);
        }
        prefetch_n_read = reader.read_raw_at(
            h_prefetch_raw_s16 + prefetch_lead_samples,
            prefetch_raw_offset,
            prefetch_total_samples);
        if (prefetch_n_read < prefetch_total_samples) {
            memset(
                h_prefetch_raw_s16 + prefetch_lead_samples + prefetch_n_read,
                0,
                (prefetch_total_samples - prefetch_n_read) * sizeof(int16_t));
        }
        prefetch_tail_read = reader.read_raw_at(
            h_prefetch_raw_s16 + prefetch_lead_samples + prefetch_total_samples,
            prefetch_raw_offset + prefetch_total_samples,
            prefetch_tail_samples);
        if (prefetch_tail_read < prefetch_tail_samples) {
            memset(
                h_prefetch_raw_s16 + prefetch_lead_samples + prefetch_total_samples
                    + prefetch_tail_read,
                0,
                (prefetch_tail_samples - prefetch_tail_read) * sizeof(int16_t));
        }
    });
}

int Pipeline::process_chunk(size_t raw_offset, int num_fields, size_t& next_raw_offset) {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_RAW_READ_COMPLETE "    if (n_read < spf) {")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_RAW_READ_COMPLETE}"
    CUVHS_RAW_READ_COMPLETE_INDEX)
if(CUVHS_RAW_READ_COMPLETE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected raw-read completion point")
endif()
string(REPLACE
    "${CUVHS_RAW_READ_COMPLETE}"
    [=[    stage_mark("raw-read");
    if (n_read < spf) {]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_NEXT_RAW_OFFSET_BLOCK [=[    } else {
        next_raw_offset = raw_offset + predicted_next_field; // Fallback
    }

    // --- REMOVED delete[] h_k3_debug; ---]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_NEXT_RAW_OFFSET_BLOCK}"
    CUVHS_NEXT_RAW_OFFSET_BLOCK_INDEX)
if(CUVHS_NEXT_RAW_OFFSET_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected next-offset block")
endif()
string(REPLACE
    "${CUVHS_NEXT_RAW_OFFSET_BLOCK}"
    [=[    } else {
        next_raw_offset = raw_offset + predicted_next_field; // Fallback
    }

    const bool output_likely_complete = maximum_output_fields > 0
        && static_cast<unsigned int>(writer.fields_written() + fields_loaded)
            >= maximum_output_fields;
    if (!output_likely_complete && num_fields == batch_size) {
        start_raw_prefetch(next_raw_offset, batch_size);
    }

    // --- REMOVED delete[] h_k3_debug; ---]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_RUN_ENTRY [=[bool Pipeline::run() {
    if (!allocate_buffers())
        return false;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_RUN_ENTRY}"
    CUVHS_PIPELINE_RUN_ENTRY_INDEX)
if(CUVHS_PIPELINE_RUN_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected run entry")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_RUN_ENTRY}"
    [=[bool Pipeline::run() {
    stop_raw_prefetch();
    field_offsets.clear();
    k2b_prev_first_field = -1;
    k2b_startup_hsync_streak = 0;
    output_have_parity = false;
    output_last_parity = false;
    k2b_prev_progressive_field = -1;
    k2b_prev_first_hsync_readloc = -1;
    k2b_prev_first_hsync_loc = -1.0f;
    k2b_prev_first_hsync_diff = -1.0f;
    chroma_state.valid = false;
    chroma_state.current_track = 0;
    chroma_state.good_metric_threshold = 0.0f;
    chroma_state.cycle_start = 0;
    chroma_state.prev_quadrant = -1;

    // The first managed FLAC read and CUDA/cuFFT initialization are both
    // substantial one-time costs. Start the bounded read first so device
    // allocation and plan construction can overlap it. The normal batch is
    // capped at 16; an explicit diagnostic override is honored as well.
    int initial_prefetch_fields = 16;
    const char* initial_batch_override = env_string("CUVHS_BATCH_SIZE");
    if (initial_batch_override != nullptr) {
        initial_prefetch_fields = std::max(1, atoi(initial_batch_override));
    }
    start_raw_prefetch(0, initial_prefetch_fields);
    if (d_raw == nullptr && !allocate_buffers()) {
        stop_raw_prefetch();
        return false;
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_RUN_LOOP [=[    while (true) {
        int fields_this_chunk = batch_size;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_RUN_LOOP}"
    CUVHS_PIPELINE_RUN_LOOP_INDEX)
if(CUVHS_PIPELINE_RUN_LOOP_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected output-completion loop")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_RUN_LOOP}"
    [=[    while (true) {
        if (writer.output_complete()) break;
        int fields_this_chunk = batch_size;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_PROCESS_FAILURE [=[        if (processed < 0) {
            free_buffers();
            return false;
        }]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_PROCESS_FAILURE}"
    CUVHS_PIPELINE_PROCESS_FAILURE_INDEX)
if(CUVHS_PIPELINE_PROCESS_FAILURE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected process failure cleanup")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_PROCESS_FAILURE}"
    [=[        if (processed < 0) {
            stop_raw_prefetch();
            demod_state.destroy();
            chroma_state_release(&chroma_state);
            free_buffers();
            return false;
        }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_FINALIZE [=[    return writer.finalize();]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_FINALIZE}"
    CUVHS_PIPELINE_FINALIZE_INDEX)
if(CUVHS_PIPELINE_FINALIZE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected finalization return")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_FINALIZE}"
    [=[    stop_raw_prefetch();
    return writer.finalize();]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_CHECKPOINT_STATE [=[    auto t_start = std::chrono::steady_clock::now();
    int total_fields = 0;
    size_t raw_offset = 0;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_CHECKPOINT_STATE}"
    CUVHS_PIPELINE_CHECKPOINT_STATE_INDEX)
if(CUVHS_PIPELINE_CHECKPOINT_STATE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected checkpoint state")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_CHECKPOINT_STATE}"
    [=[    auto t_start = std::chrono::steady_clock::now();
    auto last_json_checkpoint = t_start;
    int total_fields = 0;
    size_t raw_offset = 0;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PIPELINE_JSON_CHECKPOINT [=[        // Write JSON after each chunk so partial results are usable if we crash/get killed
        writer.write_json();]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PIPELINE_JSON_CHECKPOINT}"
    CUVHS_PIPELINE_JSON_CHECKPOINT_INDEX)
if(CUVHS_PIPELINE_JSON_CHECKPOINT_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected JSON checkpoint")
endif()
string(REPLACE
    "${CUVHS_PIPELINE_JSON_CHECKPOINT}"
    [=[        // Keep partial metadata usable without rewriting a growing JSON
        // document every ~0.1-second GPU batch. A crash can lose roughly one
        // second of metadata, and finalize() always writes the complete file.
        const auto checkpoint_now = std::chrono::steady_clock::now();
        if (env_flag_enabled("CUVHS_FORCE_JSON_EVERY_CHUNK")
            || checkpoint_now - last_json_checkpoint >= std::chrono::seconds(1)) {
            writer.write_json();
            last_json_checkpoint = checkpoint_now;
        }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PROCESS_CHUNK_ENTRY
    "int Pipeline::process_chunk(size_t raw_offset, int num_fields, size_t& next_raw_offset) {")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PROCESS_CHUNK_ENTRY}"
    CUVHS_PROCESS_CHUNK_ENTRY_INDEX)
if(CUVHS_PROCESS_CHUNK_ENTRY_INDEX EQUAL -1)
    message(FATAL_ERROR "Pinned cuVHS pipeline no longer has the expected stage timing entry")
endif()
string(REPLACE
    "${CUVHS_PROCESS_CHUNK_ENTRY}"
    [=[int Pipeline::process_chunk(size_t raw_offset, int num_fields, size_t& next_raw_offset) {
    const bool stage_timing = env_flag_enabled("CUVHS_STAGE_TIMING");
    auto stage_last = std::chrono::steady_clock::now();
    auto stage_mark = [&](const char* name) {
        if (!stage_timing) return;
        cudaDeviceSynchronize();
        const auto now = std::chrono::steady_clock::now();
        const double milliseconds =
            std::chrono::duration<double, std::milli>(now - stage_last).count();
        std::fprintf(stderr, "  [cuda-fast stage] pipeline/%s %.3f ms\n", name, milliseconds);
        stage_last = now;
    };]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

string(REPLACE
    [=[    // ================================================================
    // NEW: Global Pulse Discovery (Replaces uniform-stride K2/K3)]=]
    [=[    stage_mark("upload-and-fm");

    // ================================================================
    // NEW: Global Pulse Discovery (Replaces uniform-stride K2/K3)]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
string(REPLACE
    [=[    // K4: Refine Line Locations via Hsync Zero-Crossing]=]
    [=[    stage_mark("field-discovery-and-k2b");

    // K4: Refine Line Locations via Hsync Zero-Crossing]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
string(REPLACE
    [=[    // K5: TBC Resample]=]
    [=[    stage_mark("hsync-and-chroma");

    // K5: TBC Resample]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
string(REPLACE
    [=[    if (!dropout_detect(static_cast<float*>(d_envelope),]=]
    [=[    stage_mark("luma-resample");
    if (!dropout_detect(static_cast<float*>(d_envelope),]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
string(REPLACE
    [=[    // Download TBC results + dropout metadata and write to disk]=]
    [=[    stage_mark("dropout");

    if (writer.accepts_device_fields()) {
        std::vector<int> h_preview_is_first(static_cast<size_t>(fields_loaded));
        const cudaError_t parity_status = cudaMemcpy(
            h_preview_is_first.data(),
            d_is_first_field,
            static_cast<size_t>(fields_loaded) * sizeof(int),
            cudaMemcpyDeviceToHost);
        const int* h_preview_phase_ids =
            field_phase_ids.size() >= static_cast<size_t>(fields_loaded)
            ? field_phase_ids.data()
            : nullptr;
        const bool output_ok = parity_status == cudaSuccess
            && writer.write_preview_device_fields(
                static_cast<const uint16_t*>(d_tbc_luma),
                static_cast<const uint16_t*>(d_tbc_chroma),
                static_cast<const int*>(d_do_lines),
                static_cast<const int*>(d_do_starts),
                static_cast<const int*>(d_do_ends),
                static_cast<const int*>(d_do_count),
                h_preview_is_first.data(),
                chunk_field_offsets.data(),
                h_preview_phase_ids,
                raw_offset,
                fields_loaded);
        delete[] h_k3_debug;
        if (d_k3_debug) cudaFree(d_k3_debug);
        cudaFree(d_field_offsets);
        stage_mark("gpu-preview-render-and-encode");
        return output_ok ? fields_loaded : -1;
    }

    // Download TBC results + dropout metadata and write to disk]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_PROCESS_CHUNK_RETURN [=[    if (d_k3_debug) cudaFree(d_k3_debug);
    cudaFree(d_field_offsets);

    return fields_loaded;]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_PROCESS_CHUNK_RETURN}"
    CUVHS_PROCESS_CHUNK_RETURN_INDEX)
if(CUVHS_PROCESS_CHUNK_RETURN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected chunk return")
endif()
string(REPLACE
    "${CUVHS_PROCESS_CHUNK_RETURN}"
    [=[    if (d_k3_debug) cudaFree(d_k3_debug);
    cudaFree(d_field_offsets);
    stage_mark("download-and-write");

    return fields_loaded;]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

string(REPLACE
    [=[#include "pipeline/pipeline.h"]=]
    [=[#include "pipeline/pipeline.h"
#include "cuda_fast_decimator.h"
#include "cuda_fast_geometry.h"]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K5_HOST_GEOMETRY_BLOCK [=[    auto* h_k5_linelocs = new float[(size_t)fields_loaded * fmt.lines_per_frame];
    auto* h_k5_is_first = new int[fields_loaded];
    std::vector<float> h_k5_level_adjust;
    cudaMemcpy(h_k5_linelocs, k5_linelocs,
               (size_t)fields_loaded * fmt.lines_per_frame * sizeof(float),
               cudaMemcpyDeviceToHost);
    cudaMemcpy(h_k5_is_first, d_is_first_field,
               (size_t)fields_loaded * sizeof(int),
               cudaMemcpyDeviceToHost);
    compute_k5_level_adjust(h_k5_linelocs, h_k5_is_first, fields_loaded, fmt, &h_k5_level_adjust);
    cudaMemcpy(d_k5_level_adjust, h_k5_level_adjust.data(),
               (size_t)fields_loaded * fmt.output_field_lines * sizeof(float),
               cudaMemcpyHostToDevice);]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K5_HOST_GEOMETRY_BLOCK}"
    CUVHS_K5_HOST_GEOMETRY_BLOCK_INDEX)
if(CUVHS_K5_HOST_GEOMETRY_BLOCK_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K5 host geometry block")
endif()
string(REPLACE
    "${CUVHS_K5_HOST_GEOMETRY_BLOCK}"
    [=[    if (env_flag_enabled("CUVHS_FORCE_HOST_GEOMETRY") ||
        !cuda_fast_compute_k5_level_adjust(
            k5_linelocs,
            static_cast<int*>(d_is_first_field),
            static_cast<float*>(d_k5_level_adjust),
            fields_loaded,
            fmt)) {
        auto* h_k5_linelocs = new float[(size_t)fields_loaded * fmt.lines_per_frame];
        auto* h_k5_is_first = new int[fields_loaded];
        std::vector<float> h_k5_level_adjust;
        cudaMemcpy(h_k5_linelocs, k5_linelocs,
                   (size_t)fields_loaded * fmt.lines_per_frame * sizeof(float),
                   cudaMemcpyDeviceToHost);
        cudaMemcpy(h_k5_is_first, d_is_first_field,
                   (size_t)fields_loaded * sizeof(int),
                   cudaMemcpyDeviceToHost);
        compute_k5_level_adjust(h_k5_linelocs, h_k5_is_first, fields_loaded, fmt, &h_k5_level_adjust);
        cudaMemcpy(d_k5_level_adjust, h_k5_level_adjust.data(),
                   (size_t)fields_loaded * fmt.output_field_lines * sizeof(float),
                   cudaMemcpyHostToDevice);
        delete[] h_k5_linelocs;
        delete[] h_k5_is_first;
    }]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

# The old unconditional delete statements belonged to the host geometry path;
# the fallback above owns its temporary arrays locally now.
string(REPLACE
    [=[    delete[] h_k5_linelocs;
    delete[] h_k5_is_first;]=]
    ""
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K5_PAL_LEVEL_ORIGIN
    "        const int lineoffset = (fmt.lines_per_frame == 525) ? (is_first ? 2 : 3) : 1;")
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K5_PAL_LEVEL_ORIGIN}"
    CUVHS_K5_PAL_LEVEL_ORIGIN_INDEX)
if(CUVHS_K5_PAL_LEVEL_ORIGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected K5 level origin")
endif()
string(REPLACE
    "${CUVHS_K5_PAL_LEVEL_ORIGIN}"
    [=[        const int lineoffset = (fmt.lines_per_frame == 625)
            ? (is_first ? 1 : 2)
            : ((fmt.lines_per_frame == 525) ? (is_first ? 2 : 3) : 1);]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K5_REFINED_CALL [=[    tbc_resample(static_cast<float*>(d_demod),
                 k5_linelocs,
                 static_cast<float*>(d_k5_level_adjust),]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K5_REFINED_CALL}"
    CUVHS_K5_REFINED_CALL_INDEX)
if(CUVHS_K5_REFINED_CALL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected refined K5 call")
endif()
string(REPLACE
    "${CUVHS_K5_REFINED_CALL}"
    [=[    tbc_resample(static_cast<float*>(d_demod),
                 k5_linelocs,
                 static_cast<int*>(d_is_first_field),
                 static_cast<float*>(d_k5_level_adjust),]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")

set(CUVHS_K5_COARSE_CALL [=[        tbc_resample(static_cast<float*>(d_demod),
                     static_cast<float*>(d_linelocs_coarse),
                     static_cast<float*>(d_k5_level_adjust),]=])
string(FIND
    "${CUVHS_PIPELINE_SOURCE}"
    "${CUVHS_K5_COARSE_CALL}"
    CUVHS_K5_COARSE_CALL_INDEX)
if(CUVHS_K5_COARSE_CALL_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS pipeline no longer has the expected coarse K5 call")
endif()
string(REPLACE
    "${CUVHS_K5_COARSE_CALL}"
    [=[        tbc_resample(static_cast<float*>(d_demod),
                     static_cast<float*>(d_linelocs_coarse),
                     static_cast<int*>(d_is_first_field),
                     static_cast<float*>(d_k5_level_adjust),]=]
    CUVHS_PIPELINE_SOURCE
    "${CUVHS_PIPELINE_SOURCE}")
file(WRITE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/pipeline.cu"
    "${CUVHS_PIPELINE_SOURCE}")

vhsdecode_cuda_fast_generate_fp32_source(
    "${CMAKE_CURRENT_BINARY_DIR}/generated/vsync_discover.cu"
    "${cuvhs_SOURCE_DIR}/src/pipeline/vsync_discover.cu")
vhsdecode_cuda_fast_generate_fp32_source(
    "${CMAKE_CURRENT_BINARY_DIR}/generated/line_locs.cu"
    "${cuvhs_SOURCE_DIR}/src/pipeline/line_locs.cu")
vhsdecode_cuda_fast_generate_fp32_source(
    "${CMAKE_CURRENT_BINARY_DIR}/generated/hsync_refine.cu"
    "${cuvhs_SOURCE_DIR}/src/pipeline/hsync_refine.cu")
vhsdecode_cuda_fast_generate_fp32_source(
    "${CMAKE_CURRENT_BINARY_DIR}/generated/tbc_resample.cu"
    "${cuvhs_SOURCE_DIR}/src/pipeline/tbc_resample.cu")

set(CUVHS_TBC_SOURCE_PATH
    "${CMAKE_CURRENT_BINARY_DIR}/generated/tbc_resample.cu")
file(READ "${CUVHS_TBC_SOURCE_PATH}" CUVHS_TBC_SOURCE)

set(CUVHS_TBC_KERNEL_GEOMETRY [=[    const float* __restrict__ linelocs,    // [num_fields x lines_per_frame]
    const float* __restrict__ level_adjust,// [num_fields x output_field_lines]]=])
string(FIND
    "${CUVHS_TBC_SOURCE}"
    "${CUVHS_TBC_KERNEL_GEOMETRY}"
    CUVHS_TBC_KERNEL_GEOMETRY_INDEX)
if(CUVHS_TBC_KERNEL_GEOMETRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC kernel no longer has the expected geometry parameters")
endif()
string(REPLACE
    "${CUVHS_TBC_KERNEL_GEOMETRY}"
    [=[    const float* __restrict__ linelocs,    // [num_fields x lines_per_frame]
    const int* __restrict__ is_first_field, // [num_fields], 1=first, 0=second
    const float* __restrict__ level_adjust,// [num_fields x output_field_lines]]=]
    CUVHS_TBC_SOURCE
    "${CUVHS_TBC_SOURCE}")

set(CUVHS_TBC_FIXED_LINE_ORIGIN [=[    int ll_line = out_line + 1;
    int ll_next = ll_line + 1;]=])
string(FIND
    "${CUVHS_TBC_SOURCE}"
    "${CUVHS_TBC_FIXED_LINE_ORIGIN}"
    CUVHS_TBC_FIXED_LINE_ORIGIN_INDEX)
if(CUVHS_TBC_FIXED_LINE_ORIGIN_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC kernel no longer has the expected fixed line origin")
endif()
string(REPLACE
    "${CUVHS_TBC_FIXED_LINE_ORIGIN}"
    [=[    int first_output_line = 1;
    if (lines_per_frame == 625 && is_first_field != nullptr) {
        first_output_line = is_first_field[field] == 1 ? 3 : 4;
    }
    int ll_line = out_line + first_output_line;
    int ll_next = ll_line + 1;]=]
    CUVHS_TBC_SOURCE
    "${CUVHS_TBC_SOURCE}")

set(CUVHS_TBC_SAMPLE_PHASE
    "    float coord = line_start + frac * line_len;")
string(FIND
    "${CUVHS_TBC_SOURCE}"
    "${CUVHS_TBC_SAMPLE_PHASE}"
    CUVHS_TBC_SAMPLE_PHASE_INDEX)
if(CUVHS_TBC_SAMPLE_PHASE_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC kernel no longer has the expected sample phase")
endif()
string(REPLACE
    "${CUVHS_TBC_SAMPLE_PHASE}"
    [=[    float coord = line_start + frac * line_len;
    // Match the Exact path's observed fractional luma phase without adding
    // another device/host resampling pass to the FP32 preview-oriented path.
    coord += 0.375f * line_len / (float)output_line_len;]=]
    CUVHS_TBC_SOURCE
    "${CUVHS_TBC_SOURCE}")

set(CUVHS_TBC_HOST_GEOMETRY [=[void tbc_resample(const float* d_demod,
                  const float* d_linelocs,
                  const float* d_level_adjust,]=])
string(FIND
    "${CUVHS_TBC_SOURCE}"
    "${CUVHS_TBC_HOST_GEOMETRY}"
    CUVHS_TBC_HOST_GEOMETRY_INDEX)
if(CUVHS_TBC_HOST_GEOMETRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC host entry no longer has the expected geometry parameters")
endif()
string(REPLACE
    "${CUVHS_TBC_HOST_GEOMETRY}"
    [=[void tbc_resample(const float* d_demod,
                  const float* d_linelocs,
                  const int* d_is_first_field,
                  const float* d_level_adjust,]=]
    CUVHS_TBC_SOURCE
    "${CUVHS_TBC_SOURCE}")

set(CUVHS_TBC_KERNEL_CALL_GEOMETRY [=[        d_demod,
        d_linelocs,
        d_level_adjust,]=])
string(FIND
    "${CUVHS_TBC_SOURCE}"
    "${CUVHS_TBC_KERNEL_CALL_GEOMETRY}"
    CUVHS_TBC_KERNEL_CALL_GEOMETRY_INDEX)
if(CUVHS_TBC_KERNEL_CALL_GEOMETRY_INDEX EQUAL -1)
    message(FATAL_ERROR
        "Pinned cuVHS TBC host entry no longer has the expected kernel call")
endif()
string(REPLACE
    "${CUVHS_TBC_KERNEL_CALL_GEOMETRY}"
    [=[        d_demod,
        d_linelocs,
        d_is_first_field,
        d_level_adjust,]=]
    CUVHS_TBC_SOURCE
    "${CUVHS_TBC_SOURCE}")

file(WRITE "${CUVHS_TBC_SOURCE_PATH}" "${CUVHS_TBC_SOURCE}")

set(CUVHS_SOURCES
    "${CMAKE_CURRENT_SOURCE_DIR}/src/cuda_fast_geometry.cu"
    "${cuvhs_SOURCE_DIR}/src/gpu/device.cu"
    "${cuvhs_SOURCE_DIR}/src/format/video_format.cpp"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/pipeline.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/fm_demod.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/vsync_discover.cu"
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay/pipeline/sync_pulses.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/line_locs.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/hsync_refine.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/tbc_resample.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/chroma_decode.cu"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/dropout_detect.cu")

add_library(vhsdecode_cuda_fast SHARED
    src/vhsdecode_cuda_fast.cpp
    src/cuda_fast_decimator.cu
    src/cuda_preview_output.cu
    src/raw_reader.cpp
    src/tbc_writer.cpp
    ${CUVHS_SOURCES})

target_compile_definitions(vhsdecode_cuda_fast PRIVATE
    VHSDECODE_CUDA_FAST_EXPORTS
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
    _USE_MATH_DEFINES)
target_include_directories(vhsdecode_cuda_fast PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay"
    "${cuvhs_SOURCE_DIR}/src"
    "${nv_codec_headers_SOURCE_DIR}/include")
target_link_libraries(
    vhsdecode_cuda_fast
    PRIVATE CUDA::cudart CUDA::cuda_driver CUDA::cufft)

if(MSVC)
    set_property(TARGET vhsdecode_cuda_fast PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(vhsdecode_cuda_fast PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/O2 /W4 /permissive- /utf-8>
        $<$<COMPILE_LANGUAGE:CUDA>:-O3 -Xcompiler=/O2 -Xcompiler=/utf-8>)
else()
    target_compile_options(vhsdecode_cuda_fast PRIVATE -O3)
endif()

set_target_properties(vhsdecode_cuda_fast PROPERTIES
    OUTPUT_NAME "vhsdecode_cuda_fast"
    CUDA_SEPARABLE_COMPILATION ON
    POSITION_INDEPENDENT_CODE ON)

add_executable(vhsdecode_cuda_fast_smoke tests/smoke.cpp)
target_include_directories(vhsdecode_cuda_fast_smoke PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(vhsdecode_cuda_fast_smoke PRIVATE vhsdecode_cuda_fast)

add_executable(
    vhsdecode_cuda_fast_cancellation_test
    tests/cancellation_latch_test.cpp)
target_include_directories(vhsdecode_cuda_fast_cancellation_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src")
if(MSVC)
    target_compile_options(vhsdecode_cuda_fast_cancellation_test PRIVATE
        /O2 /W4 /permissive- /utf-8)
else()
    target_compile_options(vhsdecode_cuda_fast_cancellation_test PRIVATE -O3)
endif()

add_executable(vhsdecode_cuda_fast_sync_pulses_test
    tests/sync_pulses_test.cu
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay/pipeline/sync_pulses.cu"
    "${cuvhs_SOURCE_DIR}/src/format/video_format.cpp")
target_include_directories(vhsdecode_cuda_fast_sync_pulses_test PRIVATE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay"
    "${cuvhs_SOURCE_DIR}/src")
target_link_libraries(vhsdecode_cuda_fast_sync_pulses_test PRIVATE CUDA::cudart)
if(MSVC)
    target_compile_options(vhsdecode_cuda_fast_sync_pulses_test PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/O2 /W4 /permissive- /utf-8>
        $<$<COMPILE_LANGUAGE:CUDA>:-O3 -Xcompiler=/O2 -Xcompiler=/utf-8>)
else()
    target_compile_options(vhsdecode_cuda_fast_sync_pulses_test PRIVATE -O3)
endif()
set_target_properties(vhsdecode_cuda_fast_sync_pulses_test PROPERTIES
    CUDA_SEPARABLE_COMPILATION ON)

add_executable(vhsdecode_cuda_fast_dropout_test
    tests/dropout_detect_test.cu
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay/pipeline/dropout_detect.cu"
    "${cuvhs_SOURCE_DIR}/src/format/video_format.cpp")
target_include_directories(vhsdecode_cuda_fast_dropout_test PRIVATE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/overlay"
    "${cuvhs_SOURCE_DIR}/src")
target_link_libraries(vhsdecode_cuda_fast_dropout_test PRIVATE CUDA::cudart)
if(MSVC)
    target_compile_options(vhsdecode_cuda_fast_dropout_test PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/O2 /W4 /permissive- /utf-8>
        $<$<COMPILE_LANGUAGE:CUDA>:-O3 -Xcompiler=/O2 -Xcompiler=/utf-8>)
else()
    target_compile_options(vhsdecode_cuda_fast_dropout_test PRIVATE -O3)
endif()
set_target_properties(vhsdecode_cuda_fast_dropout_test PROPERTIES
    CUDA_SEPARABLE_COMPILATION ON)

add_executable(
    vhsdecode_cuda_fast_synthetic_ntsc_test
    tests/synthetic_ntsc_test.cpp)
target_include_directories(vhsdecode_cuda_fast_synthetic_ntsc_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(
    vhsdecode_cuda_fast_synthetic_ntsc_test
    PRIVATE vhsdecode_cuda_fast)

enable_testing()
add_test(NAME cuda_fast_probe COMMAND vhsdecode_cuda_fast_smoke)
add_test(
    NAME cuda_fast_parallel_preview_cancellation
    COMMAND vhsdecode_cuda_fast_cancellation_test)
add_test(
    NAME cuda_fast_parallel_sync_pulses
    COMMAND vhsdecode_cuda_fast_sync_pulses_test)
add_test(
    NAME cuda_fast_exact_style_dropouts
    COMMAND vhsdecode_cuda_fast_dropout_test)
add_test(
    NAME cuda_fast_synthetic_ntsc
    COMMAND vhsdecode_cuda_fast_synthetic_ntsc_test)

install(TARGETS vhsdecode_cuda_fast vhsdecode_cuda_fast_smoke
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .)
install(FILES
    "${CMAKE_CURRENT_SOURCE_DIR}/THIRD-PARTY-NOTICES-CUDA-FAST.md"
    DESTINATION .)
