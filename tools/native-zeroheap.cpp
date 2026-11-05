// neo-buzz (docs/specs/mcp-server.md §12.14): ZEROHEAP.dll, which the copies of upstream's binaries that
// tools/native-zeroheap.py makes import in place of KERNEL32.dll, for the package job's comparison
// (tools/native-compare.py --zeroed). Its exports (the .def that script writes) forward every function to kernel32
// but HeapAlloc, which comes here and returns the block zeroed: those binaries' C runtime allocates everything,
// the machine object among it, through HeapAlloc, so upstream's code starts its unset state at 0 in every instance,
// as a fresh allocation did. Built with no C runtime of its own (/NOENTRY, kernel32 only).
#include <windows.h>

extern "C" LPVOID WINAPI NbZeroHeapAlloc(HANDLE heap, DWORD flags, SIZE_T bytes)
{
    return HeapAlloc(heap, flags | HEAP_ZERO_MEMORY, bytes);
}
