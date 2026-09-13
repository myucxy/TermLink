cmake_minimum_required(VERSION 3.22)
project(termlink_direct_jni LANGUAGES C CXX)

if(NOT ANDROID)
    message(FATAL_ERROR "The TermLink direct JNI library must be cross-compiled with the Android NDK")
endif()
if(NOT DEFINED TERMLINK_DIRECT_CORE_ROOT OR NOT DEFINED TERMLINK_CARGO)
    message(FATAL_ERROR "TERMLINK_DIRECT_CORE_ROOT and TERMLINK_CARGO are required")
endif()
file(TO_CMAKE_PATH "${TERMLINK_DIRECT_CORE_ROOT}" TERMLINK_DIRECT_CORE_ROOT)
file(TO_CMAKE_PATH "${TERMLINK_CARGO}" TERMLINK_CARGO)
if(DEFINED TERMLINK_JNI_OUTPUT_DIR)
    file(TO_CMAKE_PATH "${TERMLINK_JNI_OUTPUT_DIR}" TERMLINK_JNI_OUTPUT_DIR)
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(ANDROID_ABI STREQUAL "arm64-v8a")
    set(RUST_TARGET "aarch64-linux-android")
    set(RUST_ENV_TARGET "AARCH64_LINUX_ANDROID")
    set(RUST_LINKER_NAME "aarch64-linux-android26-clang.cmd")
elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
    set(RUST_TARGET "armv7-linux-androideabi")
    set(RUST_ENV_TARGET "ARMV7_LINUX_ANDROIDEABI")
    set(RUST_LINKER_NAME "armv7a-linux-androideabi26-clang.cmd")
elseif(ANDROID_ABI STREQUAL "x86")
    set(RUST_TARGET "i686-linux-android")
    set(RUST_ENV_TARGET "I686_LINUX_ANDROID")
    set(RUST_LINKER_NAME "i686-linux-android26-clang.cmd")
elseif(ANDROID_ABI STREQUAL "x86_64")
    set(RUST_TARGET "x86_64-linux-android")
    set(RUST_ENV_TARGET "X86_64_LINUX_ANDROID")
    set(RUST_LINKER_NAME "x86_64-linux-android26-clang.cmd")
else()
    message(FATAL_ERROR "Unsupported Android ABI: ${ANDROID_ABI}")
endif()

get_filename_component(NDK_TOOLCHAIN_BIN "${CMAKE_C_COMPILER}" DIRECTORY)
set(RUST_LINKER "${NDK_TOOLCHAIN_BIN}/${RUST_LINKER_NAME}")
set(RUST_ARCHIVER "${NDK_TOOLCHAIN_BIN}/llvm-ar.exe")
get_filename_component(CARGO_BIN_DIR "${TERMLINK_CARGO}" DIRECTORY)
set(RUSTC "${CARGO_BIN_DIR}/rustc.exe")
set(RUST_TARGET_DIR "${CMAKE_CURRENT_BINARY_DIR}/rust-target")
set(RUST_LIBRARY "${RUST_TARGET_DIR}/${RUST_TARGET}/release/libtermlink_direct_ffi.a")

file(GLOB_RECURSE RUST_SOURCES CONFIGURE_DEPENDS
    "${TERMLINK_DIRECT_CORE_ROOT}/crates/*.rs"
    "${TERMLINK_DIRECT_CORE_ROOT}/crates/*/Cargo.toml")
add_custom_command(
    OUTPUT "${RUST_LIBRARY}"
    COMMAND "${CMAKE_COMMAND}" -E env
        "CARGO_TARGET_DIR=${RUST_TARGET_DIR}"
        "CARGO_TARGET_${RUST_ENV_TARGET}_LINKER=${RUST_LINKER}"
        "CC_${RUST_TARGET}=${RUST_LINKER}"
        "AR_${RUST_TARGET}=${RUST_ARCHIVER}"
        "RUSTC=${RUSTC}"
        "${TERMLINK_CARGO}" build
            --manifest-path "${TERMLINK_DIRECT_CORE_ROOT}/Cargo.toml"
            --package termlink-direct-ffi
            --target "${RUST_TARGET}"
            --release
            --locked
    DEPENDS
        ${RUST_SOURCES}
        "${TERMLINK_DIRECT_CORE_ROOT}/Cargo.toml"
        "${TERMLINK_DIRECT_CORE_ROOT}/Cargo.lock"
        "${TERMLINK_DIRECT_CORE_ROOT}/include/termlink_direct_ffi_v2.h"
        "${TERMLINK_DIRECT_CORE_ROOT}/include/termlink_direct_ffi_v3.h"
        "${TERMLINK_DIRECT_CORE_ROOT}/include/termlink_direct_ffi_v4.h"
        "${TERMLINK_DIRECT_CORE_ROOT}/include/termlink_direct_ffi_v5.h"
    COMMENT "Building TermLink direct Rust static library for ${ANDROID_ABI}"
    VERBATIM)

add_custom_target(termlink_direct_rust DEPENDS "${RUST_LIBRARY}")
add_library(termlink_direct_ffi STATIC IMPORTED GLOBAL)
set_target_properties(termlink_direct_ffi PROPERTIES IMPORTED_LOCATION "${RUST_LIBRARY}")

add_library(termlink_direct_jni SHARED
    termlink_direct_jni.cpp
    termlink_direct_jni_v4.cpp
    termlink_direct_jni_v5.cpp)
add_dependencies(termlink_direct_jni termlink_direct_rust)
target_include_directories(termlink_direct_jni PRIVATE "${TERMLINK_DIRECT_CORE_ROOT}/include")
target_compile_options(termlink_direct_jni PRIVATE
    -Wall -Wextra -Wconversion -Wsign-conversion -Werror
    -fvisibility=hidden -fvisibility-inlines-hidden)
target_link_options(termlink_direct_jni PRIVATE
    -Wl,--no-undefined
    -Wl,--exclude-libs,ALL
    -Wl,-z,max-page-size=16384
    -Wl,--strip-all)
target_link_libraries(termlink_direct_jni PRIVATE termlink_direct_ffi android log dl m)

if(DEFINED TERMLINK_JNI_OUTPUT_DIR)
    set_target_properties(termlink_direct_jni PROPERTIES
        LIBRARY_OUTPUT_DIRECTORY "${TERMLINK_JNI_OUTPUT_DIR}")
endif()
