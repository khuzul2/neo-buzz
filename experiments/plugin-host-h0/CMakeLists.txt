# H0 spike (docs/specs/plugin-host.md §6.1): neoplug, a small C ABI over VST3 and CLAP hosting, its
# probe, a test CLAP module, the SDK's AGain and Note Expression Synth, and the IPC benchmark.
# Throwaway code: nothing in the product builds this.
#
#   cmake -S experiments/plugin-host-h0 -B build/h0 -DCMAKE_BUILD_TYPE=Release
#         [-DNEOPLUG_VST3_SDK=<vst3sdk>] [-DNEOPLUG_CLAP=<clap>]   (fetched from GitHub when empty)
#   cmake --build build/h0 --config Release
#
# Outputs: build/h0/bin (neoplug library, neoplug-probe, neoplug-ipc-bench, neoplug-test.clap) and
# build/h0/VST3/Release (again.vst3, note_expression_synth.vst3).
cmake_minimum_required(VERSION 3.25)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "13.0" CACHE STRING "" FORCE)
endif()

project(neoplug_h0 LANGUAGES C CXX)
if(APPLE)
    enable_language(OBJCXX)
endif()
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(NEOPLUG_VST3_SDK "" CACHE PATH "VST3 SDK checkout (3.8.x with base, pluginterfaces, public.sdk, cmake, vstgui4)")
set(NEOPLUG_CLAP "" CACHE PATH "CLAP checkout (headers)")

include(FetchContent)
if(NOT NEOPLUG_CLAP)
    FetchContent_Declare(clap GIT_REPOSITORY https://github.com/free-audio/clap GIT_TAG 1.2.10 GIT_SHALLOW TRUE SOURCE_SUBDIR none)
    FetchContent_MakeAvailable(clap)
    set(NEOPLUG_CLAP ${clap_SOURCE_DIR})
endif()
if(NOT NEOPLUG_VST3_SDK)
    FetchContent_Declare(vst3sdk GIT_REPOSITORY https://github.com/steinbergmedia/vst3sdk GIT_TAG v3.8.1_build_84 GIT_SHALLOW TRUE
                         GIT_SUBMODULES base pluginterfaces public.sdk cmake vstgui4 SOURCE_SUBDIR none)
    FetchContent_MakeAvailable(vst3sdk)
    set(NEOPLUG_VST3_SDK ${vst3sdk_SOURCE_DIR})
endif()

# The SDK, for its hosting library and its example plugins (built only when asked for, below).
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES ON CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT ON CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_BUILD_UNIVERSAL_BINARY OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_AUV2_BUILDS OFF CACHE BOOL "" FORCE)
set(SMTG_DISABLE_CODE_SIGNING ON CACHE BOOL "" FORCE)
add_subdirectory(${NEOPLUG_VST3_SDK} vst3sdk EXCLUDE_FROM_ALL)

set(NP_OUT ${CMAKE_BINARY_DIR}/bin)
function(np_output target)
    set_target_properties(${target} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY ${NP_OUT} RUNTIME_OUTPUT_DIRECTORY_RELEASE ${NP_OUT} RUNTIME_OUTPUT_DIRECTORY_DEBUG ${NP_OUT}
        LIBRARY_OUTPUT_DIRECTORY ${NP_OUT} LIBRARY_OUTPUT_DIRECTORY_RELEASE ${NP_OUT} LIBRARY_OUTPUT_DIRECTORY_DEBUG ${NP_OUT})
endfunction()

# --- neoplug: the hosting library behind a C ABI (P/Invoke from the managed machine) ----------------
add_library(neoplug SHARED
    neoplug/neoplug.cpp
    neoplug/np_vst3.cpp
    neoplug/np_clap.cpp
    # sdk_hosting leaves these to each host:
    ${NEOPLUG_VST3_SDK}/public.sdk/source/common/memorystream.cpp
    ${NEOPLUG_VST3_SDK}/public.sdk/source/vst/hosting/plugprovider.cpp)
target_include_directories(neoplug PUBLIC neoplug PRIVATE ${NEOPLUG_VST3_SDK} ${NEOPLUG_CLAP}/include)
target_link_libraries(neoplug PRIVATE sdk_hosting)
set_target_properties(neoplug PROPERTIES CXX_VISIBILITY_PRESET hidden OBJCXX_VISIBILITY_PRESET hidden)
if(APPLE)
    target_sources(neoplug PRIVATE neoplug/np_platform_mac.mm ${NEOPLUG_VST3_SDK}/public.sdk/source/vst/hosting/module_mac.mm)
    set_source_files_properties(neoplug/np_platform_mac.mm ${NEOPLUG_VST3_SDK}/public.sdk/source/vst/hosting/module_mac.mm PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    target_link_libraries(neoplug PRIVATE "-framework Cocoa" "-framework CoreFoundation")
    set_target_properties(neoplug PROPERTIES INSTALL_RPATH "@loader_path" BUILD_WITH_INSTALL_RPATH ON)
elseif(WIN32)
    target_sources(neoplug PRIVATE neoplug/np_platform_win.cpp ${NEOPLUG_VST3_SDK}/public.sdk/source/vst/hosting/module_win32.cpp)
    target_link_libraries(neoplug PRIVATE user32 gdi32 ole32 shell32)
    target_compile_definitions(neoplug PRIVATE UNICODE _UNICODE)
else()
    target_sources(neoplug PRIVATE ${NEOPLUG_VST3_SDK}/public.sdk/source/vst/hosting/module_linux.cpp)
    target_link_libraries(neoplug PRIVATE dl pthread)
endif()
np_output(neoplug)

# --- neoplug-probe ---------------------------------------------------------------------------------
add_executable(neoplug-probe probe/neoplug-probe.cpp)
target_link_libraries(neoplug-probe PRIVATE neoplug)
if(APPLE)
    set_target_properties(neoplug-probe PROPERTIES INSTALL_RPATH "@executable_path" BUILD_WITH_INSTALL_RPATH ON)
elseif(UNIX)
    set_target_properties(neoplug-probe PROPERTIES INSTALL_RPATH "$ORIGIN" BUILD_WITH_INSTALL_RPATH ON)
    target_link_libraries(neoplug-probe PRIVATE pthread)
endif()
np_output(neoplug-probe)

# --- neoplug-test.clap: a CLAP module for the tests (synth + gain, with a native GUI panel) ----------
add_library(neoplug-test MODULE testplug/neoplug-test.cpp)
target_include_directories(neoplug-test PRIVATE ${NEOPLUG_CLAP}/include)
set_target_properties(neoplug-test PROPERTIES PREFIX "" CXX_VISIBILITY_PRESET hidden)
if(APPLE)
    target_sources(neoplug-test PRIVATE testplug/testgui_mac.mm)
    set_source_files_properties(testplug/testgui_mac.mm PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    target_link_libraries(neoplug-test PRIVATE "-framework Cocoa")
    set_target_properties(neoplug-test PROPERTIES BUNDLE TRUE BUNDLE_EXTENSION clap MACOSX_BUNDLE_GUI_IDENTIFIER org.neo-buzz.neoplug-test)
else()
    target_sources(neoplug-test PRIVATE testplug/testgui_other.cpp)
    set_target_properties(neoplug-test PROPERTIES SUFFIX ".clap")
    if(WIN32)
        target_link_libraries(neoplug-test PRIVATE user32 gdi32)
    endif()
endif()
np_output(neoplug-test)

# --- neoplug-ipc-bench: the sandbox round trip ------------------------------------------------------
add_executable(neoplug-ipc-bench ipcbench/neoplug-ipc-bench.cpp)
if(WIN32)
    target_link_libraries(neoplug-ipc-bench PRIVATE avrt)
elseif(UNIX AND NOT APPLE)
    target_link_libraries(neoplug-ipc-bench PRIVATE pthread)
endif()
np_output(neoplug-ipc-bench)

# --- The SDK's example plugins, as test fixtures ------------------------------------------------------
add_custom_target(h0-plugins ALL)
add_dependencies(h0-plugins again note-expression-synth)
