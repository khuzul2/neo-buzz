# NeoStretch: the "Pro" warp mode of Neo Audio (docs/specs/neo-audio.md section 11). A small C ABI library over
# Signalsmith Stretch (MIT; the two headers it needs are vendored in third_party/, see README.md), loaded by
# ReBuzz/Core/Clips/NeoStretch.cs through ReBuzz/Platform/NativeLibraries.cs.
#
#   Windows:       cmake -S NeoStretch -B build/NeoStretch -A x64 && cmake --build build/NeoStretch --config Release
#                  -> build/NeoStretch/Release/neostretch.dll
#   macOS, Linux:  cmake -S NeoStretch -B build/NeoStretch && cmake --build build/NeoStretch
#                  -> build/NeoStretch/libneostretch.dylib | libneostretch.so
#   Tests:         ctest --test-dir build/NeoStretch -C Release   (neostretch-test: allocation, NaN, latency, the ABI)
#
# No dependencies beyond the C++ runtime, which is linked statically where the platform allows it, so the library
# loads from the app's folder with nothing else installed.

cmake_minimum_required(VERSION 3.18)
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
  set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Oldest macOS neostretch runs on")
endif()
project(neostretch CXX)
if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(MSVC)
  # One runtime inside the DLL: nothing to install beside it.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# The library, and next to it (so the loader finds it) the test.
set(NS_OUT ${CMAKE_BINARY_DIR})
add_library(neostretch SHARED neostretch.cpp)
target_include_directories(neostretch PRIVATE third_party)
set_target_properties(neostretch PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${NS_OUT} RUNTIME_OUTPUT_DIRECTORY_RELEASE ${NS_OUT} RUNTIME_OUTPUT_DIRECTORY_DEBUG ${NS_OUT}
  LIBRARY_OUTPUT_DIRECTORY ${NS_OUT} LIBRARY_OUTPUT_DIRECTORY_RELEASE ${NS_OUT} LIBRARY_OUTPUT_DIRECTORY_DEBUG ${NS_OUT})
if(MSVC)
  target_compile_options(neostretch PRIVATE /W3 /O2 /bigobj)
  target_compile_definitions(neostretch PRIVATE _USE_MATH_DEFINES NOMINMAX)
else()
  target_compile_options(neostretch PRIVATE -Wall -O2)
  if(NOT APPLE)
    target_link_options(neostretch PRIVATE -static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL)
  endif()
endif()

add_executable(neostretch-test test.cpp neostretch.cpp)
target_include_directories(neostretch-test PRIVATE third_party)
target_compile_definitions(neostretch-test PRIVATE NS_STATIC)
set_target_properties(neostretch-test PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${NS_OUT} RUNTIME_OUTPUT_DIRECTORY_RELEASE ${NS_OUT} RUNTIME_OUTPUT_DIRECTORY_DEBUG ${NS_OUT})
if(MSVC)
  target_compile_options(neostretch-test PRIVATE /W3 /O2 /bigobj)
  target_compile_definitions(neostretch-test PRIVATE _USE_MATH_DEFINES NOMINMAX)
else()
  target_compile_options(neostretch-test PRIVATE -Wall -O2)
endif()

enable_testing()
add_test(NAME neostretch-test COMMAND neostretch-test)
