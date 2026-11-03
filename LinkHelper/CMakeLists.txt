# ReBuzzLink: Ableton Link for ReBuzz, in its own process (docs/specs/session-view.md 0.8).
# GPL-2.0-or-later, as Link is; ReBuzz (MIT) talks to it over stdin/stdout only.
#
#   git submodule update --init --recursive LinkHelper/link
#   Windows:       cmake -S LinkHelper -B build/LinkHelper -A x64
#                  cmake --build build/LinkHelper --config Release    -> build/LinkHelper/Release/ReBuzzLink.exe
#   macOS, Linux:  cmake -S LinkHelper -B build/LinkHelper
#                  cmake --build build/LinkHelper                     -> build/LinkHelper/ReBuzzLink
#
# rebuzz-headless's build copies it from there beside the app (docs/specs/mac-engine.md M6).

cmake_minimum_required(VERSION 3.18)
# macOS: the oldest system it runs on (before project(), which reads it).
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
  set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Oldest macOS ReBuzzLink runs on")
endif()
project(ReBuzzLink CXX)
# Single-configuration generators (Makefiles, Ninja): Release unless asked otherwise.
if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(MSVC)
  # One runtime inside the exe: nothing to install beside it.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

include(${CMAKE_CURRENT_SOURCE_DIR}/link/AbletonLinkConfig.cmake)

add_executable(ReBuzzLink main.cpp)
target_link_libraries(ReBuzzLink Ableton::Link)
if(WIN32)
  target_compile_definitions(ReBuzzLink PRIVATE _WIN32_WINNT=0x0601 NOMINMAX _CRT_SECURE_NO_WARNINGS)
endif()
