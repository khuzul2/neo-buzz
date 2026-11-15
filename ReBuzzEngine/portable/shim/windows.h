// Portable build of the native machine host (docs/specs/cross-platform.md R1): just enough of the Windows
// types and calls that ReBuzzEngine's sources use, so that rebe/Callbacks.cpp, rebe/mdkimp.cpp and ipc/Message.h
// compile unchanged on Linux and macOS (LP64: pointers go over the wire as 64-bit, like ReBuzzEngine64).
#pragma once
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <assert.h>
#include <unistd.h>
#include <pthread.h>
#include <mutex>
#include <algorithm>

typedef uint32_t DWORD;
typedef uint16_t WORD;
typedef int64_t LONGLONG;
typedef int BOOL;
typedef void* HANDLE;
typedef void* HINSTANCE;
typedef void* HWND;
typedef void* LPVOID;
typedef char* LPTSTR;
typedef unsigned char BYTE;
#ifndef TRUE
#define TRUE 1
#define FALSE 0
#endif
#define __cdecl
#define __stdcall
#define __int64 int64_t
#define __FUNCTION__ __func__
#define _WIN64 1
#define NOMINMAX
#define WINAPI
#define APIENTRY
#define CALLBACK
#define _In_
#define _In_opt_

using std::min;
using std::max;

#define sprintf_s(buf, size, ...) snprintf((buf), (size), __VA_ARGS__)
template <size_t N> inline int vsnprintf_s(char (&buf)[N], size_t count, const char* fmt, va_list ap) { return vsnprintf(buf, N, fmt, ap); }
inline int vsnprintf_s(char* buf, size_t size, size_t, const char* fmt, va_list ap) { return vsnprintf(buf, size, fmt, ap); }
inline void* _aligned_malloc(size_t size, size_t align) { void* p = nullptr; if (posix_memalign(&p, align < sizeof(void*) ? sizeof(void*) : align, size)) return nullptr; return p; }
inline void _aligned_free(void* p) { free(p); }
inline int _getpid() { return (int)getpid(); }

// CCriticalSection of MFC (Machine.h)
class CCriticalSection
{
public:
	void Lock() { m.lock(); }
	void Unlock() { m.unlock(); }
private:
	std::recursive_mutex m;
};
