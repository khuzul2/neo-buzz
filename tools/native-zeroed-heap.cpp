// neo-buzz (docs/specs/mcp-server.md §12.14): linked into the unfixed builds of the natives with a source fix
// (tools/build-natives.ps1 -Unfixed, which also defines NEOBUZZ_UNFIXED to leave each fix out). Every C++
// allocation in the machine's DLL comes back zeroed, the machine object (`new mi`) among them, so the state
// upstream's code never initialises starts at 0 in every instance: upstream's code as it plays from a fresh,
// zeroed allocation. That is the start the fixes give, so a fixed build must render the same bits as this one
// in every case (tools/native-compare.py --unfixed). The DLL's static C runtime (/MT) takes these in place of
// its own operator new and delete; nothing outside the DLL sees them.
#include <cstdlib>
#include <new>

void *operator new(std::size_t n)
{
    void *p = std::calloc(1, n ? n : 1);
    if (!p)
        std::abort();
    return p;
}

void *operator new[](std::size_t n) { return operator new(n); }
void operator delete(void *p) noexcept { std::free(p); }
void operator delete[](void *p) noexcept { std::free(p); }
void operator delete(void *p, std::size_t) noexcept { std::free(p); }
void operator delete[](void *p, std::size_t) noexcept { std::free(p); }
