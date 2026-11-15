# PluginExport (docs/specs/plugin-export.md): per Neo plug-in the CLAP stub and the VST3 and (macOS) AU wrappers around it
# (clap-wrapper, MIT), and the test host (neohost).
#
#   cmake -S . -B _build -DCLAP_SDK_ROOT=<clap> -DVST3_SDK_ROOT=<vst3sdk> [-DAUDIOUNIT_SDK_ROOT=<AudioUnitSDK>] -DCLAP_WRAPPER_DIR=<clap-wrapper>
#   cmake --build _build --config Release
# Outputs in _build/assets (CLAP, VST3, AUV2 where the platform has them) and the host in _build.
# The NativeAOT libraries (neo_dsp, neo_editor) are copied next to each module by ci/stage.py.
cmake_minimum_required(VERSION 3.25)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
  set(CMAKE_OSX_DEPLOYMENT_TARGET "13.0" CACHE STRING "" FORCE)
endif()
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # as clap-wrapper: one runtime in every module
project(neo_export VERSION 0.1.0 LANGUAGES C CXX)
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

include(${CMAKE_CURRENT_LIST_DIR}/cmake/plugins.cmake)   # NEO_PLUGINS: "name|AU subtype|AU type|CLAP id" per plug-in (tools/gen-plugin-export.py)

set(NEO_FORMATS CLAP)
if(NEO_WRAP)
  list(APPEND NEO_FORMATS VST3)
  if(APPLE)
    list(APPEND NEO_FORMATS AUV2)
  endif()
endif()

foreach(entry IN LISTS NEO_PLUGINS)
  string(REPLACE "|" ";" entry "${entry}")
  list(GET entry 0 pname)
  list(GET entry 1 pau)
  list(GET entry 2 pautype)
  list(GET entry 3 pid)
  string(TOLOWER "${pname}" pslug)
  string(REGEX REPLACE "[^a-z0-9]+" "_" pslug "${pslug}")
  # the stub, built once per plug-in: it shows only this plug-in of neo_dsp's factory to the module it is linked into
  add_library(impl_${pslug} STATIC native/stub_impl.c)
  target_include_directories(impl_${pslug} PUBLIC native)
  target_link_libraries(impl_${pslug} PUBLIC clap)
  target_compile_definitions(impl_${pslug} PRIVATE "NEO_PLUGIN_ID=\"${pid}\"")
  if(UNIX AND NOT APPLE)
    target_link_libraries(impl_${pslug} PUBLIC ${CMAKE_DL_LIBS})
  endif()
  set_target_properties(impl_${pslug} PROPERTIES POSITION_INDEPENDENT_CODE ON)
  if(NEO_WRAP)
    make_clapfirst_plugins(
      TARGET_NAME ${pslug}
      IMPL_TARGET impl_${pslug}
      OUTPUT_NAME "${pname}"
      ENTRY_SOURCE native/clap_entry.cpp
      BUNDLE_IDENTIFIER "org.neobuzz.${pslug}"
      BUNDLE_VERSION ${PROJECT_VERSION}
      COPY_AFTER_BUILD FALSE
      WINDOWS_FOLDER_VST3 FALSE
      PLUGIN_FORMATS ${NEO_FORMATS}
      ASSET_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/assets
      AUV2_MANUFACTURER_NAME "NeoBuzz Live"
      AUV2_MANUFACTURER_CODE "NBuz"
      AUV2_SUBTYPE_CODE "${pau}"
      AUV2_INSTRUMENT_TYPE "${pautype}")
  else()
    # Only the CLAP: the same module, without clap-wrapper
    add_library(${pslug}_clap MODULE native/clap_entry.cpp)
    target_link_libraries(${pslug}_clap PRIVATE impl_${pslug})
    set_target_properties(${pslug}_clap PROPERTIES PREFIX "" OUTPUT_NAME "${pname}" SUFFIX ".clap" LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/assets)
  endif()
endforeach()

# the test host: loads a module and drives it through the CLAP ABI (functional checks, GUI on X11 under Xvfb, GC figures)
add_executable(neohost host/neohost.cpp)
target_link_libraries(neohost PRIVATE clap)
find_package(Threads REQUIRED)
if(WIN32)
  target_link_libraries(neohost PRIVATE avrt winmm user32 gdi32 Threads::Threads)
else()
  target_link_libraries(neohost PRIVATE ${CMAKE_DL_LIBS} Threads::Threads)
endif()
if(UNIX AND NOT APPLE)
  find_package(X11)
  if(X11_FOUND)
    target_compile_definitions(neohost PRIVATE NEOHOST_X11=1)
    target_include_directories(neohost PRIVATE ${X11_INCLUDE_DIR})
    target_link_libraries(neohost PRIVATE ${X11_LIBRARIES})
  endif()
endif()
if(MSVC)
  target_compile_options(neohost PRIVATE /O2)
endif()
