# ReBuzzLink: Ableton Link for ReBuzz, in its own process (docs/specs/session-view.md 0.8).
# GPL-2.0-or-later, as Link is; ReBuzz (MIT) talks to it over stdin/stdout only.
#
#   git submodule update --init --recursive
#   cmake -S LinkHelper -B build/LinkHelper -A x64
#   cmake --build build/LinkHelper --config Release

cmake_minimum_required(VERSION 3.18)
project(ReBuzzLink CXX)

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
