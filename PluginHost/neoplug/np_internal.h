// neoplug internals: one backend class per format behind the C ABI.
#pragma once

#include "neoplug.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace np {

inline void copyStr(char* dst, size_t cap, const std::string& s)
{
    if (!dst || cap == 0) return;
    size_t n = s.size() < cap - 1 ? s.size() : cap - 1;
    std::memcpy(dst, s.data(), n);
    dst[n] = 0;
}

inline void setErr(char* err, int len, const std::string& s)
{
    if (err && len > 0) copyStr(err, (size_t)len, s);
}

/// Single-producer single-consumer ring for parameter changes between threads.
struct ParamRing
{
    struct Item { uint32_t id; double value; };
    static constexpr uint32_t Size = 1024;
    Item items[Size];
    std::atomic<uint32_t> head{0}, tail{0};

    bool push(uint32_t id, double v)
    {
        uint32_t h = head.load(std::memory_order_relaxed);
        uint32_t n = (h + 1) % Size;
        if (n == tail.load(std::memory_order_acquire)) return false;
        items[h] = {id, v};
        head.store(n, std::memory_order_release);
        return true;
    }
    bool pop(Item& out)
    {
        uint32_t t = tail.load(std::memory_order_relaxed);
        if (t == head.load(std::memory_order_acquire)) return false;
        out = items[t];
        tail.store((t + 1) % Size, std::memory_order_release);
        return true;
    }
};

/// Which notes the library has started and not ended, so a stop can end them. Audio thread only.
struct NoteTracker
{
    int32_t ids[16][128];
    uint8_t on[16][128];
    NoteTracker() { clear(); }
    void clear() { std::memset(on, 0, sizeof on); for (auto& r : ids) for (auto& i : r) i = -1; }
    void noteOn(int ch, int key, int id) { ch &= 15; key &= 127; on[ch][key] = 1; ids[ch][key] = id; }
    void noteOff(int ch, int key) { ch &= 15; key &= 127; on[ch][key] = 0; }
    int idOf(int ch, int key) const { return ids[ch & 15][key & 127]; }
    /// Calls f(channel, key, id) for every note that is on, and forgets them.
    template <class F> void endAll(F f)
    {
        for (int c = 0; c < 16; c++)
            for (int k = 0; k < 128; k++)
                if (on[c][k]) { on[c][k] = 0; f(c, k, ids[c][k]); }
    }
};

/// The main bus the plugin has (planes) from the caller's channels: a mono bus gets the mean, a wider
/// one the first two channels and silence in the others. n is the plane count of the bus.
inline void foldIn(float* const* dst, int n, const float* const* in, int inCh, int frames)
{
    for (int c = 0; c < n; c++)
    {
        if (!in || inCh <= 0 || c >= 2) std::memset(dst[c], 0, sizeof(float) * (size_t)frames);
        else if (n == 1 && inCh >= 2)
            for (int i = 0; i < frames; i++) dst[0][i] = 0.5f * (in[0][i] + in[1][i]);
        else std::memcpy(dst[c], in[c < inCh ? c : inCh - 1], sizeof(float) * (size_t)frames);
    }
}

/// The plugin's main output (src planes) into the caller's channels: a mono bus is copied to every
/// channel, a wider one gives its first two (channel c takes plane c, or the last when it has fewer).
inline void foldOut(float* const* out, int outCh, const float* const* src, int n, int frames)
{
    for (int c = 0; c < outCh; c++)
    {
        if (n <= 0) std::memset(out[c], 0, sizeof(float) * (size_t)frames);
        else std::memcpy(out[c], src[c < n ? c : n - 1], sizeof(float) * (size_t)frames);
    }
}

struct Plugin
{
    virtual ~Plugin() = default;
    np_class_info info{};
    std::thread::id mainThread = std::this_thread::get_id();

    virtual bool activate(double rate, int maxFrames, bool offline, std::string& err) = 0;
    virtual void deactivate() = 0;
    virtual int channels(bool input) = 0;
    virtual int paramCount() = 0;
    virtual bool paramInfo(int index, np_param_info& out) = 0;
    virtual double paramGet(uint32_t id) = 0;
    virtual bool paramText(uint32_t id, double v, std::string& out) = 0;
    virtual bool paramFromText(uint32_t id, const char* text, double& out) = 0;
    virtual bool paramSet(uint32_t id, double v) = 0;
    virtual int process(np_process_io& io) = 0;
    virtual int auxChannels() = 0;
    virtual int poll() = 0;
    virtual double paramPlain(uint32_t id, double v) = 0;
    virtual void reset() = 0;
    virtual bool saveState(std::vector<uint8_t>& out) = 0;
    virtual bool loadState(const uint8_t* data, size_t size, std::string& err) = 0;
    virtual int latency() = 0;
    virtual int tail() = 0;
    virtual void idle() = 0;
    virtual int guiSupport(const char* api) = 0;
    virtual bool guiAttach(void* parent, const char* api, int& w, int& h, std::string& err) = 0;
    virtual void guiDetach() = 0;
};

// Backends.
int vst3List(const std::string& path, std::vector<np_class_info>& out, std::string& err);
Plugin* vst3Open(const std::string& path, const std::string& id, std::string& err);
int clapList(const std::string& path, std::vector<np_class_info>& out, std::string& err);
Plugin* clapOpen(const std::string& path, const std::string& id, std::string& err);

// State envelope: "NPST", format, then length-prefixed chunks.
void putU32(std::vector<uint8_t>& v, uint32_t x);
bool getU32(const uint8_t*& p, const uint8_t* end, uint32_t& x);

} // namespace np
