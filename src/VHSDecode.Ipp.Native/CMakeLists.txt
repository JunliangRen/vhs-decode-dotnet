cmake_minimum_required(VERSION 3.20)
project(VHSDecodeIppNative LANGUAGES CXX)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux" OR
   NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64)$" OR
   NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "This build targets glibc Linux x64. Use the vcxproj for Windows.")
endif()

set(IPP_ROOT "" CACHE PATH "Root of the verified Intel IPP Linux development package")
foreach(path include/ipp/ippcore.h include/ipp/ipps.h lib/libipps.a lib/libippvm.a lib/libippcore.a)
    if(NOT EXISTS "${IPP_ROOT}/${path}")
        message(FATAL_ERROR "Missing ${IPP_ROOT}/${path}; run tools/build-ipp-native-linux.ps1.")
    endif()
endforeach()

find_package(Threads REQUIRED)
add_library(vhsdecode_ipp SHARED src/vhsdecode_ipp.cpp)
target_compile_features(vhsdecode_ipp PRIVATE cxx_std_20)
target_compile_definitions(vhsdecode_ipp PRIVATE VHSDECODE_IPP_EXPORTS)
target_include_directories(vhsdecode_ipp PUBLIC include PRIVATE "${IPP_ROOT}/include")
target_compile_options(vhsdecode_ipp PRIVATE -Wall -Wextra -Wpedantic -Werror -fno-gnu-unique)
set_target_properties(vhsdecode_ipp PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    POSITION_INDEPENDENT_CODE YES
    BUILD_WITH_INSTALL_RPATH YES
    INSTALL_RPATH "")
# Use the PIC sequential archives, never lib/nonpic or the OpenMP/TBB layers.
target_link_libraries(vhsdecode_ipp PRIVATE
    "-Wl,--start-group"
    "${IPP_ROOT}/lib/libipps.a"
    "${IPP_ROOT}/lib/libippvm.a"
    "${IPP_ROOT}/lib/libippcore.a"
    "-Wl,--end-group"
    Threads::Threads m)
target_link_options(vhsdecode_ipp PRIVATE
    "-Wl,--no-undefined,--exclude-libs,ALL,-z,relro,-z,now"
    "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/vhsdecode_ipp.map")
set_property(TARGET vhsdecode_ipp APPEND PROPERTY LINK_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/vhsdecode_ipp.map")

include(CTest)
if(BUILD_TESTING)
    add_executable(vhsdecode_ipp_smoke tests/smoke.cpp)
    target_compile_features(vhsdecode_ipp_smoke PRIVATE cxx_std_20)
    target_compile_options(vhsdecode_ipp_smoke PRIVATE -Wall -Wextra -Wpedantic -Werror)
    target_link_libraries(vhsdecode_ipp_smoke PRIVATE vhsdecode_ipp Threads::Threads)
    set_target_properties(vhsdecode_ipp_smoke PROPERTIES BUILD_RPATH "$ORIGIN")
    add_test(NAME ipp_native_smoke COMMAND vhsdecode_ipp_smoke)
    set_tests_properties(ipp_native_smoke PROPERTIES TIMEOUT 120)

    # Export presence is tested separately from transform execution. Generate
    # inert exports from the public header so new ABI entries cannot silently
    # turn every loader fixture into an unrelated missing-export failure.
    file(READ "${CMAKE_CURRENT_SOURCE_DIR}/include/vhsdecode_ipp.h" ABI_HEADER)
    string(REGEX MATCHALL "vhsdecode_ipp_[a-z0-9_]+[ \t\r\n]*\\(" ABI_FUNCTIONS "${ABI_HEADER}")
    set(FIXTURE_EXPORTS "")
    foreach(declaration IN LISTS ABI_FUNCTIONS)
        string(REGEX REPLACE "[ \t\r\n]*\\($" "" name "${declaration}")
        if(NOT name MATCHES "^vhsdecode_ipp_(get_abi_version|get_runtime_info|status_string)$")
            if(name STREQUAL "vhsdecode_ipp_fft32_create")
                string(APPEND FIXTURE_EXPORTS "#ifndef FIXTURE_MISSING_EXPORT\n")
            endif()
            string(APPEND FIXTURE_EXPORTS "extern \"C\" __attribute__((visibility(\"default\"))) void ${name}() {}\n")
            if(name STREQUAL "vhsdecode_ipp_fft32_create")
                string(APPEND FIXTURE_EXPORTS "#endif\n")
            endif()
        endif()
    endforeach()
    configure_file(tests/probe_fixture.cpp.in probe_fixture.cpp @ONLY)
    foreach(kind valid missing_export wrong_abi wrong_info_abi wrong_size runtime_rejected init_rejected warning)
        set(target "ipp_probe_${kind}")
        add_library(${target} SHARED "${CMAKE_CURRENT_BINARY_DIR}/probe_fixture.cpp")
        target_compile_definitions(${target} PRIVATE
            FIXTURE_ABI=$<IF:$<STREQUAL:${kind},wrong_abi>,0x00010005,0x00010004>
            FIXTURE_INFO_ABI=$<IF:$<STREQUAL:${kind},wrong_info_abi>,0x00010005,0x00010004>
            FIXTURE_SIZE=$<IF:$<STREQUAL:${kind},wrong_size>,239,240>
            FIXTURE_STATUS=$<IF:$<STREQUAL:${kind},runtime_rejected>,-10006,$<IF:$<STREQUAL:${kind},warning>,1,0>>
            FIXTURE_INIT_STATUS=$<IF:$<STREQUAL:${kind},init_rejected>,-10006,$<IF:$<STREQUAL:${kind},warning>,1,0>>
            $<$<STREQUAL:${kind},missing_export>:FIXTURE_MISSING_EXPORT>)
        set_target_properties(${target} PROPERTIES
            CXX_VISIBILITY_PRESET hidden
            LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/probe-fixtures")
    endforeach()
endif()
