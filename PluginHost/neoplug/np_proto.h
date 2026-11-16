// neoplug sandbox protocol (docs/specs/plugin-host.md H2): what the host (np_remote.cpp) and neoplug-server (np_serve.cpp) put in shared memory.
// One server process has a control lane (every call but the audio block, answered on the server's main thread); each instance in it has
// an audio lane of its own (one block per round trip, answered on an audio thread of the instance).
#pragma once

#include "neoplug.h"
#include "np_ipc.h"

#include <string>
#include <vector>

namespace np { namespace proto {

constexpr int MaxFrames = 4096;       // frames in one audio message (a longer block is sent in pieces)
constexpr int MaxEvents = 1024;       // events into the plug-in in one message; more are dropped
constexpr int MaxOutEvents = 256;
constexpr int MaxTable = 4096;        // parameter values the server publishes
constexpr size_t CtrlData = 256 * 1024;

enum Op : uint32_t
{
    OpOpen = 1, OpClose, OpActivate, OpDeactivate, OpChannels, OpParamCount, OpParamInfo, OpParamGet, OpParamText, OpParamFromText,
    OpParamSet, OpParamPlain, OpAuxChannels, OpReset, OpStateSave, OpStateGet, OpStatePut, OpStateLoad, OpLatency, OpTail,
    OpGuiSupport, OpGuiAttach, OpGuiDetach, OpGuiSize, OpGuiCanResize, OpGuiSetSize, OpGuiSetScale, OpGuiResizeDone,
    OpGuiOpenWindow, OpGuiCloseWindow, OpOutCount, OpOutInfo, OpQuit, OpPing, OpCrash, OpFlush
};

// Mach doorbell ids (macOS)
enum MachId : uint32_t { MachHello = 1, MachSetup = 2, MachRing = 3, MachSemaphores = 4, MachWorkgroup = 5 };

// np_poll bits the server adds: the GUI's
enum { GuiResize = 1, GuiClosed = 2 };

struct Ctrl
{
    ipc::Word req, resp;
    alignas(64) std::atomic<uint32_t> ready;     // 1: the server is up; 2: it could not start
    std::atomic<uint32_t> quit;
    std::atomic<uint32_t> serverPid;
    uint32_t op;
    int32_t inst;
    uint32_t len;
    int32_t rc;
    uint32_t rlen;
    alignas(64) uint8_t data[CtrlData];
    alignas(64) uint8_t rdata[CtrlData];
};

struct Audio
{
    ipc::Word req, resp;
    alignas(64) std::atomic<int32_t> outSel;
    std::atomic<uint32_t> pollFlags;             // np_poll bits the server collected, for the host
    std::atomic<uint32_t> guiFlags;              // GuiResize / GuiClosed
    std::atomic<int32_t> guiW, guiH;
    std::atomic<uint32_t> quit;
    std::atomic<uint32_t> tableGen;              // changes when the server's list of parameters did
    std::atomic<uint32_t> nvalues;
    // the request
    alignas(64) uint32_t frames;
    int32_t inCh, auxCh, outCh, nev, outCap, playing, tsn, tsd;
    double tempo, beat;
    // the response
    int32_t rc, outCount;
    alignas(64) double values[MaxTable];
    np_event events[MaxEvents];
    np_event outEvents[MaxOutEvents];
    alignas(64) float in[2][MaxFrames];
    float aux[2][MaxFrames];
    float out[2][MaxFrames];
};

/// Writes a request or a response.
struct Wr
{
    uint8_t* p; size_t cap, n = 0;
    bool ok = true;
    Wr(uint8_t* buf, size_t capacity) : p(buf), cap(capacity) {}
    void raw(const void* d, size_t k) { if (n + k > cap) { ok = false; return; } std::memcpy(p + n, d, k); n += k; }
    void u32(uint32_t v) { raw(&v, 4); }
    void i32(int32_t v) { raw(&v, 4); }
    void u64(uint64_t v) { raw(&v, 8); }
    void f64(double v) { raw(&v, 8); }
    void str(const std::string& s) { u32((uint32_t)s.size()); raw(s.data(), s.size()); }
    void bytes(const void* d, size_t k) { u32((uint32_t)k); raw(d, k); }
};

struct Rd
{
    const uint8_t* p; size_t n, i = 0;
    bool ok = true;
    Rd(const uint8_t* buf, size_t size) : p(buf), n(size) {}
    bool raw(void* d, size_t k) { if (i + k > n) { ok = false; return false; } std::memcpy(d, p + i, k); i += k; return true; }
    uint32_t u32() { uint32_t v = 0; raw(&v, 4); return v; }
    int32_t i32() { int32_t v = 0; raw(&v, 4); return v; }
    uint64_t u64() { uint64_t v = 0; raw(&v, 8); return v; }
    double f64() { double v = 0; raw(&v, 8); return v; }
    std::string str() { uint32_t k = u32(); if (!ok || i + k > n) { ok = false; return {}; } std::string s((const char*)p + i, k); i += k; return s; }
    const uint8_t* bytes(size_t& k) { k = u32(); if (!ok || i + k > n) { ok = false; k = 0; return nullptr; } const uint8_t* r = p + i; i += k; return r; }
};

/// The server's loop (np_serve.cpp); the executable's main calls it. Returns the exit code.
int serve(int argc, char** argv);

}} // namespace np::proto
