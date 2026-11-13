// Control plugin: pure native, no GC, similar per-block cost to the managed machines. With --load it runs a native
// malloc/free churn thread at the same rates as the managed load generator, so the runner's own noise (CPU sharing,
// memory bandwidth) can be told apart from anything the .NET GC adds. Same C ABI as the NativeAOT plugin.
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>
#ifdef _WIN32
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
struct Inst { int kind; float s1[8]{}, s2[8]{}; float p0 = 0, p1 = 0; };
std::vector<Inst*> insts;
std::mutex mu;
std::atomic<bool> loadRun{false};
std::atomic<int> loadReady{1};
std::thread loadThread;
// Work per sample per instance: calibrated so a 16-instance block costs about what the managed machines do.
int workPerSample() { static int w = getenv("NP_CONTROL_WORK") ? atoi(getenv("NP_CONTROL_WORK")) : 60; return w; }
}

EXPORT int np_create(int kind, float) { std::lock_guard<std::mutex> g(mu); insts.push_back(new Inst{kind}); return (int)insts.size() - 1; }
EXPORT void np_param(int h, int index, int raw) { Inst* s = insts[h]; if (index & 1) s->p1 = raw * 0.001f; else s->p0 = raw * 0.001f; }
EXPORT void np_note_on(int, int, float) {}
EXPORT void np_note_off(int, int) {}
EXPORT void np_process(int h, const float* inL, const float* inR, float* outL, float* outR, int n)
{
    Inst* s = insts[h];
    int w = workPerSample();
    for (int i = 0; i < n; i++) {
        float x = s->kind ? inL[i] : 0.25f, y = s->kind ? inR[i] : 0.25f;
        for (int k = 0; k < w; k++) {                        // a chain of dependent one-pole filters and a soft clip
            int j = k & 7;
            s->s1[j] += (x - s->s1[j]) * (0.01f + 0.1f * s->p0);
            s->s2[j] += (y - s->s2[j]) * (0.01f + 0.1f * s->p1);
            x = s->s1[j] * 1.5f - s->s2[j] * 0.5f;
            y = s->s2[j] * 1.5f - s->s1[j] * 0.5f;
            x = x / (1.0f + fabsf(x)); y = y / (1.0f + fabsf(y));
        }
        outL[i] = x; outR[i] = y;
    }
}
EXPORT int np_gc_count(void) { return 0; }
EXPORT int np_gc_count_gen(int) { return 0; }
EXPORT int64_t np_gc_pause_ns(void) { return 0; }
EXPORT int64_t np_gc_max_pause_ns(void) { return 0; }
EXPORT void np_gc_max_pause_reset(void) {}
EXPORT int64_t np_alloc_bytes(void) { return 0; }
EXPORT void np_set_latency(int) {}
EXPORT int np_gc_config(char* buf, int cap) { return snprintf(buf, cap, "native control, no GC"); }

EXPORT int np_load_ready(void) { return loadReady; }
EXPORT void np_load_stop(void) { loadRun = false; if (loadThread.joinable()) loadThread.join(); }
EXPORT void np_load_start(int mode)
{
    np_load_stop();
    if (mode <= 0) return;
    loadRun = true; loadReady = 0;
    loadThread = std::thread([mode] {
        double bytesPerSec = mode == 1 ? 1e6 : 20e6;
        std::vector<void*> ring(2048, nullptr);
        std::vector<char*> live;
        if (mode == 3) { live.resize(1000000); for (auto& p : live) { p = (char*)malloc(232); memset(p, 1, 232); } }
        loadReady = 1;
        unsigned rnd = 12345;
        auto next = [&] { rnd = rnd * 1664525u + 1013904223u; return rnd >> 8; };
        auto t0 = std::chrono::steady_clock::now();
        double allocated = 0, churned = 0, lohAt = 0; size_t pos = 0; void* big = nullptr;
        while (loadRun) {
            double t = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
            double debt = t * bytesPerSec - allocated;
            while (debt > 0) {
                int size = 64 + (int)(next() % 2048);
                free(ring[pos]); ring[pos] = malloc(size); memset(ring[pos], 7, size); pos = (pos + 1) % ring.size();
                allocated += size + 24; debt -= size + 24;
            }
            if (!live.empty()) {
                double due = t * 1e6 - churned;
                while (due > 0) { size_t i = next() % live.size(); free(live[i]); live[i] = (char*)malloc(232); memset(live[i], 2, 232); churned += 256; due -= 256; }
                if (t >= lohAt) { free(big); big = malloc(2 << 20); memset(big, 3, 2 << 20); lohAt = t + 1; }
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        for (void* p : ring) free(p);
        for (char* p : live) free(p);
        free(big);
    });
}
