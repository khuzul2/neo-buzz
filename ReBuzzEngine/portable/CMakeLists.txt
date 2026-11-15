# The portable native machine host (docs/specs/cross-platform.md R1): ReBuzzEngine's sources without MFC and Win32,
# for Linux and macOS. It is the same protocol and the same host code as ReBuzzEngine64.exe (rebe/, ipc/); portable/shim/
# gives the few Windows types they use and replaces ipc/ServerProcess.h's Windows transport with FIFOs.
#
#   cmake -S ReBuzzEngine/portable -B build/native-host && cmake --build build/native-host
#
# produces build/native-host/rebuzz-native-host, which ReBuzz starts for the classics' .dylib/.so files.
cmake_minimum_required(VERSION 3.16)
project(rebuzz-native-host CXX)

set(CMAKE_CXX_STANDARD 14)            # the sources do `using namespace std` and typedef byte: C++17's std::byte clashes
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(ENGINE ${CMAKE_CURRENT_SOURCE_DIR}/..)
set(OSC ${CMAKE_CURRENT_BINARY_DIR}/osctbl_data.cpp)
add_custom_command(OUTPUT ${OSC}
  COMMAND ${CMAKE_COMMAND} -DIN=${ENGINE}/rebe/res/osctbl.raw -DOUT=${OSC} -P ${CMAKE_CURRENT_SOURCE_DIR}/embed.cmake
  DEPENDS ${ENGINE}/rebe/res/osctbl.raw ${CMAKE_CURRENT_SOURCE_DIR}/embed.cmake)

add_executable(rebuzz-native-host
  ${ENGINE}/rebe/ReBuzzEngine.cpp
  ${ENGINE}/rebe/Callbacks.cpp
  ${ENGINE}/rebe/mdkimp.cpp
  ${ENGINE}/rebe/Wavetable.cpp
  ${ENGINE}/rebe/osctbl.cpp
  ${OSC})
# "../buzz/X.h" and "../IPC/X.h" (Windows is case-blind) resolve against shim/a/..
target_include_directories(rebuzz-native-host PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/shim/a ${CMAKE_CURRENT_SOURCE_DIR}/shim ${ENGINE}/rebe ${ENGINE}/ipc)
# no fused multiply-adds: the mixing code in mdkimp.cpp must round as the Windows build does
target_compile_options(rebuzz-native-host PRIVATE -ffp-contract=off -fpermissive -w)
target_link_libraries(rebuzz-native-host PRIVATE pthread ${CMAKE_DL_LIBS})
