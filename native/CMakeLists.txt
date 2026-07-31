# SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds `stingray_cmp`, a small shared library containing AMD Compressonator's
# CMP_Core block codecs plus the C shim in stingray_cmp.cpp.
#
# Point COMPRESSONATOR_SOURCE at a checkout of
# https://github.com/GPUOpen-Tools/compressonator (only cmp_core/ and
# applications/_libs/cmp_math/ are needed):
#
#   cmake -S native -B build/native -DCOMPRESSONATOR_SOURCE=/path/to/compressonator
#   cmake --build build/native --config Release
#
# The result is ~500 KB, versus the ~137 MB Compressonator CLI, which drags in
# Qt, OpenCV and DirectX/Vulkan plugins it does not need for block compression.

cmake_minimum_required(VERSION 3.16)
project(stingray_cmp CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED COMPRESSONATOR_SOURCE)
  message(FATAL_ERROR "Set -DCOMPRESSONATOR_SOURCE=<path to a compressonator checkout>")
endif()

set(CORE "${COMPRESSONATOR_SOURCE}/cmp_core")
set(CMPMATH "${COMPRESSONATOR_SOURCE}/applications/_libs/cmp_math")

if(NOT EXISTS "${CORE}/source/cmp_core.cpp")
  message(FATAL_ERROR "cmp_core not found under ${COMPRESSONATOR_SOURCE}")
endif()

# Each SIMD kernel is compiled for its own instruction set and selected at run
# time by cpu_extensions.cpp. That dispatch is what Compressonator's CLI calls
# its "HPC" path.
add_library(cmp_simd_sse    STATIC "${CORE}/source/core_simd_sse.cpp")
add_library(cmp_simd_avx    STATIC "${CORE}/source/core_simd_avx.cpp")
add_library(cmp_simd_avx512 STATIC "${CORE}/source/core_simd_avx512.cpp")

foreach(t cmp_simd_sse cmp_simd_avx cmp_simd_avx512)
  target_include_directories(${t} PRIVATE "${CORE}/source" "${CORE}/shaders")
  if(UNIX)
    target_compile_definitions(${t} PRIVATE _LINUX ASPM_GPU)
  endif()
endforeach()

if(MSVC)
  target_compile_options(cmp_simd_avx    PRIVATE /arch:AVX2)
  target_compile_options(cmp_simd_avx512 PRIVATE /arch:AVX512)
else()
  target_compile_options(cmp_simd_sse PRIVATE -march=nehalem)
  target_compile_options(cmp_simd_avx PRIVATE -march=haswell)
  # Explicit feature flags rather than -march=knl: newer GCC dropped that target.
  target_compile_options(cmp_simd_avx512 PRIVATE -mavx512f -mavx512bw -mavx512dq -mavx512vl)
endif()

add_library(stingray_cmp SHARED
  stingray_cmp.cpp
  "${CORE}/shaders/bc1_encode_kernel.cpp"
  "${CORE}/shaders/bc2_encode_kernel.cpp"
  "${CORE}/shaders/bc3_encode_kernel.cpp"
  "${CORE}/shaders/bc4_encode_kernel.cpp"
  "${CORE}/shaders/bc5_encode_kernel.cpp"
  "${CORE}/shaders/bc6_encode_kernel.cpp"
  "${CORE}/shaders/bc7_encode_kernel.cpp"
  "${CORE}/source/cmp_core.cpp"
  "${CMPMATH}/cpu_extensions.cpp"
  "${CMPMATH}/cmp_math_common.cpp"
)

target_include_directories(stingray_cmp PRIVATE
  "${CORE}/shaders" "${CORE}/source" "${CMPMATH}")

if(UNIX)
  target_compile_definitions(stingray_cmp PRIVATE _LINUX ASPM_GPU)
endif()

target_link_libraries(stingray_cmp PRIVATE cmp_simd_sse cmp_simd_avx cmp_simd_avx512)

find_package(Threads REQUIRED)
target_link_libraries(stingray_cmp PRIVATE Threads::Threads)

set_target_properties(stingray_cmp PROPERTIES PREFIX "" OUTPUT_NAME "stingray_cmp")
