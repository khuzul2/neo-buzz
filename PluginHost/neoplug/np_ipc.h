// neoplug sandbox (docs/specs/plugin-host.md H2): the OS pieces of the shared-memory protocol between the host and a
// neoplug-server process. A lane is a request word and a response word in shared memory; the side that waits spins briefly
// (Linux and Windows, never indefinitely) and then sleeps on the OS primitive: a futex on the word, a Windows event, a Mach
// semaphore. macOS never spins: the Mach semaphores are handed to the server in a Mach message through its bootstrap port (H0 §8.4).
#pragma once

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace np { namespace ipc {

/// One direction of a lane: a sequence number the waiter sleeps on, and whether it sleeps.
struct Word
{
    alignas(64) std::atomic<uint32_t> seq;
    alignas(64) std::atomic<uint32_t> sleeping;
};

/// What an endpoint sleeps on and is woken by (per Word and per process; Linux needs nothing: the futex is the word).
struct Wake
{
#if defined(_WIN32)
    void* ev = nullptr;
#elif defined(__APPLE__)
    uint32_t sem = 0;   // semaphore_t
#endif
};

enum { WaitOk = 0, WaitTimeout = 1, WaitAborted = 2 };

/// Publishes <paramref name="value"/> in the word and wakes the waiter if it sleeps.
void post(Word& w, uint32_t value, Wake& wake);
/// Waits until w.seq == want. Spins up to spinNs (0 = not at all), then sleeps; timeoutMs < 0 waits for ever. abort (if given) ends the wait when it is non-zero.
int wait(Word& w, uint32_t want, Wake& wake, int64_t spinNs, int timeoutMs, const std::atomic<int>* abort);
/// Wakes a sleeper without changing the word (it checks its abort flag).
void wakeOnly(Word& w, Wake& wake);

/// A named block of shared memory.
struct Shm
{
    void* ptr = nullptr;
    size_t size = 0;
    std::string name;
    bool owner = false;
#if defined(_WIN32)
    void* handle = nullptr;
#else
    int fd = -1;
#endif
    bool create(const std::string& name, size_t size);
    bool open(const std::string& name, size_t size);
    void close();
};

/// A fresh unique name for a shared block, a lane or a server.
std::string uniqueName(const char* kind);

#if defined(_WIN32)
bool makeEvent(Wake& wake, const std::string& name, bool create);
void closeEvent(Wake& wake);
#endif

/// A server process the host started.
struct Child
{
#if defined(_WIN32)
    void* process = nullptr;
    unsigned long pid = 0;
#else
    int pid = -1;
#endif
    long id() const { return (long)pid; }
    long code = -1;      // how it ended: the exit code (Windows: an NTSTATUS such as 0xC0000005 for an access violation)
    int signal = 0;      // POSIX: the signal that killed it
};

/// The path of neoplug-server: NEOPLUG_SERVER, else beside the library. Empty when it isn't there.
std::string serverPath();
/// Starts the server: <exe> <ctrl name> <parent pid>. On macOS the child's bootstrap port is a rendezvous port of ours (see machHost*).
bool spawn(const std::string& exe, const std::vector<std::string>& args, Child& out, std::string& err);
/// Blocks until the process has ended.
void waitExit(Child& c);
bool isAlive(Child& c);
void kill(Child& c);
void closeChild(Child& c);

/// The calling thread gets the priority the OS gives audio threads (best effort).
void audioThread(double periodSec);

/// The server's side of a spin: what to do between polls.
inline void cpuRelax()
{
#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__) || defined(_M_IX86)
#if defined(_MSC_VER)
    _mm_pause();
#else
    __builtin_ia32_pause();
#endif
#elif defined(__aarch64__) || defined(_M_ARM64)
#if defined(_MSC_VER)
    __yield();
#else
    __asm__ __volatile__("yield");
#endif
#endif
}

#if defined(__APPLE__)
// --- macOS: Mach semaphores and the handover --------------------------------------------------------------
// Port rights don't survive exec, and mach_ports_register no longer carries them. The host spawns the server with a receive port of
// its own as the child's bootstrap port; the server asks it there, sends it a send right to its own request port, and the host then
// sends every semaphore (and the real bootstrap port) to that port in Mach messages. Control requests ring the server's request port.
struct MachHost
{
    uint32_t rendezvous = 0;      // receive right: where the server says hello
    uint32_t serverPort = 0;      // send right to the server's request port
    uint32_t ctrlRespSem = 0;
    bool handshake(Child& c, uint32_t ctrlRespSemaphore, std::string& err);
    /// Rings the server's request port; with semaphores (copy-send rights) and/or a workgroup port attached.
    bool ring(uint32_t id, const uint32_t* ports, int nports);
};
uint32_t newSemaphore();
void freeSemaphore(uint32_t sem);

struct MachServer
{
    uint32_t requestPort = 0;     // receive right
    uint32_t ctrlRespSem = 0;
    /// Called first in the server: says hello on the bootstrap port, receives the setup message (control semaphore, real bootstrap port).
    bool hello(std::string& err);
    /// Waits for a doorbell; timeoutMs < 0 for ever. Returns 0 on a doorbell (ports = the semaphores that came with it), 1 on timeout, -1 on error.
    int waitDoorbell(int timeoutMs, uint32_t& id, uint32_t* ports, int& nports);
};
/// Joins the calling thread to the audio device's workgroup (a port the host sent); best effort.
bool joinWorkgroup(uint32_t port, double periodSec);
#endif

}} // namespace np::ipc
