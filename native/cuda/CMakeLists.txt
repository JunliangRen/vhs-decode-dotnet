cmake_minimum_required(VERSION 3.26)

project(vhsdecode_cuda LANGUAGES CXX)

option(VHSDECODE_CUDA_ENABLE "Build the optional CUDA RF compute sidecar" ON)
option(VHSDECODE_CUDA_BUILD_TESTS "Build the native CUDA smoke test" ON)

if(NOT VHSDECODE_CUDA_ENABLE)
  message(STATUS "vhsdecode CUDA sidecar disabled (VHSDECODE_CUDA_ENABLE=OFF)")
  return()
endif()

include(CheckLanguage)
check_language(CUDA)
if(NOT CMAKE_CUDA_COMPILER)
  message(STATUS "CUDA compiler not found; the optional vhsdecode CUDA sidecar will not be built")
  return()
endif()

enable_language(CUDA)
find_package(CUDAToolkit 13.0 REQUIRED)

# CUDA 13.0 Update 2 identifies its nvcc as 13.0.88. Keep the native ABI tied to
# that toolchain until a newer toolkit is deliberately qualified.
if(CMAKE_CUDA_COMPILER_VERSION VERSION_LESS 13.0.88 OR
   NOT CMAKE_CUDA_COMPILER_VERSION VERSION_LESS 13.1)
  message(FATAL_ERROR
    "vhsdecode_cuda requires CUDA 13.0 Update 2 (nvcc 13.0.88); found ${CMAKE_CUDA_COMPILER_VERSION}")
endif()

add_library(vhsdecode_cuda SHARED
  include/vhsdecode_cuda.h
  src/vhsdecode_cuda.cu)

target_compile_features(vhsdecode_cuda PRIVATE cxx_std_20)
target_compile_definitions(vhsdecode_cuda PRIVATE VHSDECODE_CUDA_BUILD_DLL=1)
target_include_directories(vhsdecode_cuda
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>)
target_link_libraries(vhsdecode_cuda PRIVATE CUDA::cudart CUDA::cufft)

# Ship native code for the explicitly supported GPUs and retain compute_89 PTX
# as a forward-compatible fallback for later NVIDIA architectures.
set_target_properties(vhsdecode_cuda PROPERTIES
  CUDA_ARCHITECTURES "75-real;86-real;89"
  CUDA_RUNTIME_LIBRARY Shared
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
  OUTPUT_NAME "vhsdecode_cuda"
  VERSION 1.0.0
  SOVERSION 1
  WINDOWS_EXPORT_ALL_SYMBOLS OFF)

if(MSVC)
  target_compile_options(vhsdecode_cuda PRIVATE
    $<$<COMPILE_LANGUAGE:CXX>:/W4 /permissive->
    $<$<COMPILE_LANGUAGE:CUDA>:-Xcompiler=/W4>)
else()
  target_compile_options(vhsdecode_cuda PRIVATE
    $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra -Wpedantic>
    $<$<COMPILE_LANGUAGE:CUDA>:-Xcompiler=-Wall,-Wextra>)
endif()

install(TARGETS vhsdecode_cuda
  RUNTIME DESTINATION bin
  LIBRARY DESTINATION bin
  ARCHIVE DESTINATION lib)
install(FILES include/vhsdecode_cuda.h DESTINATION include)
install(FILES README.md THIRD-PARTY-NOTICES-CUDA.md DESTINATION share/vhsdecode-cuda)

# CUDA 13 places Windows runtime DLLs under bin/x64 while FindCUDAToolkit's
# imported targets point at their import libraries. Copy only the two EULA-
# redistributable runtime families into an install image; never into source.
if(WIN32)
  file(GLOB _vhsdecode_cudart_dll CONFIGURE_DEPENDS
    "${CUDAToolkit_BIN_DIR}/x64/cudart64_*.dll")
  file(GLOB _vhsdecode_cufft_dll CONFIGURE_DEPENDS
    "${CUDAToolkit_BIN_DIR}/x64/cufft64_*.dll")
  if(NOT _vhsdecode_cudart_dll OR NOT _vhsdecode_cufft_dll)
    message(FATAL_ERROR "CUDA Runtime or cuFFT redistributable DLL was not found under ${CUDAToolkit_BIN_DIR}/x64")
  endif()
  install(FILES ${_vhsdecode_cudart_dll} ${_vhsdecode_cufft_dll}
    DESTINATION bin)
  foreach(_vhsdecode_runtime_dll IN LISTS
      _vhsdecode_cudart_dll _vhsdecode_cufft_dll)
    add_custom_command(TARGET vhsdecode_cuda POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${_vhsdecode_runtime_dll}" "$<TARGET_FILE_DIR:vhsdecode_cuda>"
      VERBATIM)
  endforeach()
  get_filename_component(_vhsdecode_cuda_root "${CUDAToolkit_BIN_DIR}" DIRECTORY)
  install(FILES "${_vhsdecode_cuda_root}/LICENSE"
    DESTINATION share/vhsdecode-cuda
    RENAME NVIDIA-CUDA-LICENSE.txt)
endif()

if(VHSDECODE_CUDA_BUILD_TESTS)
  include(CTest)
  add_executable(vhsdecode_cuda_smoke tests/smoke.cpp)
  target_compile_features(vhsdecode_cuda_smoke PRIVATE cxx_std_20)
  set_property(TARGET vhsdecode_cuda_smoke PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_link_libraries(vhsdecode_cuda_smoke PRIVATE vhsdecode_cuda)
  add_test(NAME vhsdecode_cuda_smoke COMMAND vhsdecode_cuda_smoke)
  set_tests_properties(vhsdecode_cuda_smoke PROPERTIES SKIP_RETURN_CODE 77)
endif()
