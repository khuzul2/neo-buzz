# experiments/plugin-export: the CLAP stub, the VST3 and (macOS) AU wrappers around it (clap-wrapper, MIT), and the CLAP bench.
#
#   cmake -S . -B _build -DCLAP_SDK_ROOT=<clap> -DVST3_SDK_ROOT=<vst3sdk> [-DAUDIOUNIT_SDK_ROOT=<AudioUnitSDK>] -DCLAP_WRAPPER_DIR=<clap-wrapper>
#   cmake --build _build --config Release
# Outputs in _build/neofilter_assets (CLAP, VST3, AUV2 where the platform has them) and the bench in _build.
# The NativeAOT libraries (dsp, editor) are copied next to each module by ci/stage.py.
cmake_minimum_required(VERSION 3.25)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
  set(CMAKE_OSX_DEPLOYMENT_TARGET "13.0" CACHE STRING "" FORCE)
endif()
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # as clap-wrapper: one runtime in every module
project(neo_export VERSION 0.0.1 LANGUAGES C CXX)
if(APPLE)
  enable_language(OBJC OBJCXX)
endif()
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)   # the SDK and wrapper libraries are linked into shared modules
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CLAP_WRAPPER_DIR "" CACHE PATH "clap-wrapper checkout")
set(NEO_WRAP ON CACHE BOOL "Build the VST3 / AU wrappers (needs the VST3 SDK)")
if(NEO_WRAP)
  add_subdirectory(${CLAP_WRAPPER_DIR} clap-wrapper EXCLUDE_FROM_ALL)
else()
  add_subdirectory(${CLAP_SDK_ROOT} base-sdk-clap EXCLUDE_FROM_ALL)
endif()

add_library(neo_export_impl STATIC native/stub_impl.c)
target_include_directories(neo_export_impl PUBLIC native)
target_link_libraries(neo_export_impl PUBLIC clap)
if(UNIX AND NOT APPLE)
  target_link_libraries(neo_export_impl PUBLIC ${CMAKE_DL_LIBS})
endif()
set_target_properties(neo_export_impl PROPERTIES POSITION_INDEPENDENT_CODE ON)

set(NEO_FORMATS CLAP)
if(NEO_WRAP)
  list(APPEND NEO_FORMATS VST3)
  if(APPLE)
    list(APPEND NEO_FORMATS AUV2)
  endif()
  make_clapfirst_plugins(
    TARGET_NAME neofilter
    IMPL_TARGET neo_export_impl
    OUTPUT_NAME "Neo Filter"
    ENTRY_SOURCE native/clap_entry.cpp
    BUNDLE_IDENTIFIER "org.neobuzz.neofilter"
    BUNDLE_VERSION ${PROJECT_VERSION}
    COPY_AFTER_BUILD FALSE
    WINDOWS_FOLDER_VST3 FALSE
    PLUGIN_FORMATS ${NEO_FORMATS}
    ASSET_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/neofilter_assets
    AUV2_MANUFACTURER_NAME "neo-buzz"
    AUV2_MANUFACTURER_CODE "NBuz"
    AUV2_SUBTYPE_CODE "NFlt"
    AUV2_INSTRUMENT_TYPE "aufx")
else()
  # Only the CLAP: the same module, without clap-wrapper
  add_library(neofilter_clap MODULE native/clap_entry.cpp)
  target_link_libraries(neofilter_clap PRIVATE neo_export_impl)
  set_target_properties(neofilter_clap PROPERTIES PREFIX "" OUTPUT_NAME "Neo Filter" SUFFIX ".clap" LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/neofilter_assets)
endif()

add_executable(clapbench bench/clapbench.cpp)
target_link_libraries(clapbench PRIVATE clap)
find_package(Threads REQUIRED)
if(WIN32)
  target_link_libraries(clapbench PRIVATE avrt winmm Threads::Threads)
else()
  target_link_libraries(clapbench PRIVATE ${CMAKE_DL_LIBS} Threads::Threads)
endif()
if(MSVC)
  target_compile_options(clapbench PRIVATE /O2)
endif()
