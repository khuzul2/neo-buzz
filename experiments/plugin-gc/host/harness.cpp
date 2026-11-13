// Native host harness, no .NET in it. Loads a plugin library with the np_* C ABI (NativeAOT plugin or the native
// control), runs a real-time-like audio thread (highest priority available, one callback per block paced to the
// wall clock, 16 instances with parameter automation every block) and records every callback.
//
//   harness --plugin <lib> --block 64 --seconds 300 --load 0..3 [--latency 1] [--label text] [--warmup 10]
//
// Output: "RESULT key=value ..." (one line), "GCCFG ..." and "WORST ..." lines; tools/summarize.py makes tables from them.
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#include "np_abi.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <avrt.h>
#include <mmsystem.h>
#else
#include <dlfcn.h>
#include <pthread.h>
#include <sched.h>
#include <time.h>
#endif
#ifdef __APPLE__
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <mach/thread_policy.h>
#endif

static const double kRate = 48000.0;

// ---------------------------------------------------------------- clock and waiting
#ifdef _WIN32
static double qpcScale;
static int64_t now_ns() { LARGE_INTEGER c; QueryPerformanceCounter(&c); return (int64_t)((double)c.QuadPart * qpcScale); }
static HANDLE gTimer;
static void wait_until(int64_t target)
{
    int64_t rem = target - now_ns();
    if (rem > 700000 && gTimer) {                       // sleep most of it on a high-resolution timer, spin the rest
        LARGE_INTEGER due; due.QuadPart = -((rem - 400000) / 100);
        SetWaitableTimer(gTimer, &due, 0, nullptr, nullptr, FALSE);
        WaitForSingleObject(gTimer, 100);
    }
    while (now_ns() < target) YieldProcessor();
}
#elif defined(__APPLE__)
static mach_timebase_info_data_t tb;
static int64_t now_ns() { return (int64_t)(mach_absolute_time() * tb.numer / tb.denom); }
static void wait_until(int64_t target)
{
    if (target > now_ns()) mach_wait_until((uint64_t)(target * tb.denom / tb.numer));
}
#else
static int64_t now_ns() { timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (int64_t)t.tv_sec * 1000000000LL + t.tv_nsec; }
static void wait_until(int64_t target)
{
    timespec t{ (time_t)(target / 1000000000LL), (long)(target % 1000000000LL) };
    clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &t, nullptr);
}
#endif

// ---------------------------------------------------------------- plugin loading
struct Api {
    np_create_t create; np_param_t param; np_note_on_t noteOn; np_note_off_t noteOff; np_process_t process;
    np_gc_count_t gcCount; np_gc_count_gen_t gcCountGen; np_gc_pause_ns_t gcPause; np_gc_max_pause_ns_t gcMaxPause;
    np_gc_max_pause_reset_t gcMaxPauseReset; np_alloc_bytes_t allocBytes; np_load_start_t loadStart; np_load_stop_t loadStop;
    np_set_latency_t setLatency; np_gc_config_t gcConfig;
};
static Api api;
static int zeroCount() { return 0; }

static void* sym(void* lib, const char* name)
{
#ifdef _WIN32
    return (void*)GetProcAddress((HMODULE)lib, name);
#else
    return dlsym(lib, name);
#endif
}
static bool loadPlugin(const char* path)
{
#ifdef _WIN32
    void* lib = (void*)LoadLibraryA(path);
#else
    void* lib = dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
    if (!lib) { fprintf(stderr, "cannot load %s\n", path); return false; }
#define REQ(f, n) api.f = (decltype(api.f))sym(lib, n); if (!api.f) { fprintf(stderr, "missing export %s\n", n); return false; }
    REQ(create, "np_create") REQ(param, "np_param") REQ(noteOn, "np_note_on") REQ(noteOff, "np_note_off") REQ(process, "np_process")
    api.gcCount = (np_gc_count_t)sym(lib, "np_gc_count");
    api.gcCountGen = (np_gc_count_gen_t)sym(lib, "np_gc_count_gen");
    api.gcPause = (np_gc_pause_ns_t)sym(lib, "np_gc_pause_ns");
    api.gcMaxPause = (np_gc_max_pause_ns_t)sym(lib, "np_gc_max_pause_ns");
    api.gcMaxPauseReset = (np_gc_max_pause_reset_t)sym(lib, "np_gc_max_pause_reset");
    api.allocBytes = (np_alloc_bytes_t)sym(lib, "np_alloc_bytes");
    api.loadStart = (np_load_start_t)sym(lib, "np_load_start");
    api.loadStop = (np_load_stop_t)sym(lib, "np_load_stop");
    api.setLatency = (np_set_latency_t)sym(lib, "np_set_latency");
    api.gcConfig = (np_gc_config_t)sym(lib, "np_gc_config");
    return true;
}
static inline int gcCount() { return api.gcCount ? api.gcCount() : 0; }

// ---------------------------------------------------------------- real-time priority
static const char* makeRealtime(int64_t periodNs)
{
#ifdef _WIN32
    static char msg[160];
    bool tc = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL) != 0;
    DWORD idx = 0;
    HANDLE mm = AvSetMmThreadCharacteristicsW(L"Pro Audio", &idx);
    if (mm) AvSetMmThreadPriority(mm, AVRT_PRIORITY_CRITICAL);
    snprintf(msg, sizeof msg, "time_critical=%d mmcss_pro_audio=%d", tc, mm != nullptr);
    return msg;
#elif defined(__APPLE__)
    static char msg[160];
    mach_timebase_info_data_t t; mach_timebase_info(&t);
    auto toAbs = [&](int64_t ns) { return (uint32_t)(ns * t.denom / t.numer); };
    thread_time_constraint_policy_data_t p;
    p.period = toAbs(periodNs); p.computation = toAbs(periodNs * 8 / 10); p.constraint = toAbs(periodNs); p.preemptible = 1;
    kern_return_t kr = thread_policy_set(mach_thread_self(), THREAD_TIME_CONSTRAINT_POLICY, (thread_policy_t)&p, THREAD_TIME_CONSTRAINT_POLICY_COUNT);
    snprintf(msg, sizeof msg, "time_constraint_policy=%s", kr == KERN_SUCCESS ? "ok" : "failed");
    return msg;
#else
    static char msg[160];
    sched_param sp{}; sp.sched_priority = sched_get_priority_max(SCHED_FIFO);
    int r = pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp);
    snprintf(msg, sizeof msg, "sched_fifo=%s", r == 0 ? "ok" : "denied (normal priority)");
    return msg;
#endif
}

// ---------------------------------------------------------------- the run
struct Config { std::string plugin, label = "run"; int block = 64; double seconds = 300, warmup = 10; int load = 0, latency = 0, instances = 16, driveQuality = 0, driveAutogain = 0; };

int main(int argc, char** argv)
{
    Config c;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        auto val = [&] { return i + 1 < argc ? argv[++i] : (char*)""; };
        if (a == "--plugin") c.plugin = val(); else if (a == "--block") c.block = atoi(val()); else if (a == "--seconds") c.seconds = atof(val());
        else if (a == "--warmup") c.warmup = atof(val()); else if (a == "--load") c.load = atoi(val()); else if (a == "--latency") c.latency = atoi(val());
        else if (a == "--label") c.label = val(); else if (a == "--instances") c.instances = atoi(val()); else if (a == "--drive-quality") c.driveQuality = atoi(val()); else if (a == "--drive-autogain") c.driveAutogain = atoi(val());
    }
    if (c.plugin.empty() || c.block < 1 || c.block > 256) { fprintf(stderr, "usage: harness --plugin lib --block 64 --seconds 300 --load 0..3\n"); return 2; }
#ifdef _WIN32
    LARGE_INTEGER f; QueryPerformanceFrequency(&f); qpcScale = 1e9 / (double)f.QuadPart;
    timeBeginPeriod(1);
    gTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!gTimer) gTimer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
#elif defined(__APPLE__)
    mach_timebase_info(&tb);
#endif
    if (!loadPlugin(c.plugin.c_str())) return 3;

    const int N = c.instances;
    std::vector<int> h(N);
    for (int i = 0; i < N; i++) h[i] = api.create(i < N / 2 ? 0 : 1, (float)kRate);   // first half Poly, second half Drive
    for (int i = N / 2; i < N; i++) api.param(h[i], DRIVE_QUALITY, c.driveQuality);
    // Drive re-runs a 4096-sample auto-gain probe on every parameter change; with automation every block that costs more than
    // the rest of the machine, so it is off by default here (it is a CPU finding, not a GC one; see the report).
    for (int i = N / 2; i < N; i++) api.param(h[i], DRIVE_AUTOGAIN, c.driveAutogain);
    // 0 = 2x, 1 = 4x, 2 = 8x oversampling
    if (api.setLatency) api.setLatency(c.latency);
    if (api.loadStart) api.loadStart(c.load);

    char cfg[512] = "";
    if (api.gcConfig) api.gcConfig(cfg, sizeof cfg);
    printf("GCCFG label=%s %s\n", c.label.c_str(), cfg);

    // Drive input: a few seconds of saw + noise, shared by all Drive instances.
    const int InLen = 1 << 16;
    std::vector<float> in(InLen + 256);
    uint32_t rnd = 1;
    for (int i = 0; i < (int)in.size(); i++) { rnd = rnd * 1664525u + 1013904223u; in[i] = 0.4f * (float)(((i * 220) % 480) / 240.0 - 1.0) + 0.1f * ((int)(rnd >> 9) / 4194304.0f - 1.0f); }

    const int64_t period = (int64_t)((double)c.block * 1e9 / kRate);
    const int64_t warmBlocks = (int64_t)(c.warmup * kRate / c.block), recBlocks = (int64_t)(c.seconds * kRate / c.block);
    std::vector<uint32_t> dur(recBlocks), late(recBlocks);
    std::vector<uint8_t> gcFlag(recBlocks);
    std::atomic<bool> done{false};
    int64_t resyncCount = 0;
    std::string rtMsg;
    int gc0Start = 0, gc1Start = 0, gc2Start = 0; int64_t pauseStart = 0, allocStart = 0, allocEnd = 0, startedNs = 0;
    int gcEnd0 = 0, gcEnd1 = 0, gcEnd2 = 0; int64_t pauseEnd = 0, maxPause = 0;

    std::thread audio([&] {
        rtMsg = makeRealtime(period);
        std::vector<float> outL(256), outR(256);
        const int chords[4][4] = { { 48, 55, 58, 63 }, { 46, 53, 58, 62 }, { 44, 51, 56, 60 }, { 43, 50, 55, 59 } };
        const int blocksPerSec = (int)(kRate / c.block);
        int64_t t0 = now_ns() + 20000000;
        int64_t resyncs = 0;
        float peak = 0;
        for (int64_t k = -warmBlocks; k < recBlocks; k++) {
            int64_t sched = t0 + (k + warmBlocks) * period;
            wait_until(sched);
            if (now_ns() - sched > 50 * period) { t0 += now_ns() - sched; sched = now_ns(); resyncs++; }   // hopelessly behind (a host would have dropped out): restart the schedule
            if (k == 0) {                                    // recording starts: baseline the counters
                gc0Start = gcCount(); gc1Start = api.gcCountGen ? api.gcCountGen(1) : 0; gc2Start = api.gcCountGen ? api.gcCountGen(2) : 0;
                pauseStart = api.gcPause ? api.gcPause() : 0; allocStart = api.allocBytes ? api.allocBytes() : 0;
                if (api.gcMaxPauseReset) api.gcMaxPauseReset();
                startedNs = now_ns();
            }
            int g0 = gcCount();
            int64_t t = now_ns();
            int64_t blk = k + warmBlocks;
            if (blk % blocksPerSec == 0) {                   // a new chord every second of audio time
                int ci = (int)((blk / blocksPerSec) & 3), pi = (int)(((blk / blocksPerSec) + 3) & 3);
                for (int i = 0; i < N / 2; i++) {
                    if (blk > 0) for (int n = 0; n < 4; n++) api.noteOff(h[i], chords[pi][n]);
                    for (int n = 0; n < 4; n++) api.noteOn(h[i], chords[ci][n] + (i & 1) * 12, 0.8f);
                }
            }
            int tri = (int)(blk % 400); tri = tri < 200 ? tri : 400 - tri;           // triangle 0..200
            int off = (int)((blk * c.block) & (InLen - 1));
            for (int i = 0; i < N; i++) {
                const bool poly = i < N / 2;
                api.param(h[i], poly ? POLY_F1CUTOFF : DRIVE_DRIVE, poly ? 400 + tri * 2 : 150 + tri * 3);
                api.param(h[i], poly ? POLY_UNIDETUNE : DRIVE_TONE, 300 + tri * 2);
                api.process(h[i], &in[off], &in[off + 17], outL.data(), outR.data(), c.block);
                peak += outL[0];
            }
            int64_t t1 = now_ns();
            int g1 = gcCount();
            if (k >= 0) { dur[k] = (uint32_t)std::min<int64_t>(t1 - t, 0xFFFFFFFFLL); late[k] = (uint32_t)std::max<int64_t>(0, std::min<int64_t>(t - sched, 0xFFFFFFFFLL)); gcFlag[k] = g1 != g0; }
        }
        gcEnd0 = gcCount(); gcEnd1 = api.gcCountGen ? api.gcCountGen(1) : 0; gcEnd2 = api.gcCountGen ? api.gcCountGen(2) : 0;
        pauseEnd = api.gcPause ? api.gcPause() : 0; allocEnd = api.allocBytes ? api.allocBytes() : 0;
        maxPause = api.gcMaxPause ? api.gcMaxPause() : 0;
        resyncCount = resyncs;
        if (peak == 12345.678f) printf("?\n");               // keep the work observable
        done = true;
    });
    audio.join();
    double wall = (now_ns() - startedNs) / 1e9;
    if (api.loadStop) api.loadStop();

    // ---- statistics
    auto pct = [&](std::vector<uint32_t>& v, double p) { size_t i = (size_t)std::min<double>(v.size() - 1, std::ceil(p / 100.0 * v.size()) - 1); return v[i]; };
    std::vector<uint32_t> sorted(dur), sortedLate(late);
    std::sort(sorted.begin(), sorted.end()); std::sort(sortedLate.begin(), sortedLate.end());
    double sum = 0; for (uint32_t d : dur) sum += d;
    uint32_t median = sorted[sorted.size() / 2];
    int64_t misses = 0, missesGc = 0, gcCbs = 0, stalls500 = 0, stalls500All = 0; uint32_t worstGc = 0, worstNoGc = 0;
    for (int64_t i = 0; i < recBlocks; i++) {
        bool miss = dur[i] > period;
        misses += miss; missesGc += miss && gcFlag[i]; gcCbs += gcFlag[i];
        if (dur[i] > median + 500000) { stalls500All++; if (gcFlag[i]) stalls500++; }
        if (gcFlag[i]) worstGc = std::max(worstGc, dur[i]); else worstNoGc = std::max(worstNoGc, dur[i]);
    }
    // Non-GC callbacks set the baseline (engine cost bursts and runner noise live there); a GC callback is an
    // "attributable stall" when it exceeds that baseline's 99.9th percentile by more than 0.5 ms.
    std::vector<uint32_t> nonGc; nonGc.reserve(recBlocks);
    for (int64_t i = 0; i < recBlocks; i++) if (!gcFlag[i]) nonGc.push_back(dur[i]);
    std::sort(nonGc.begin(), nonGc.end());
    uint32_t base999 = nonGc.empty() ? 0 : pct(nonGc, 99.9), base9999 = nonGc.empty() ? 0 : pct(nonGc, 99.99);
    int64_t attrib = 0; uint32_t maxGcCb = 0;
    for (int64_t i = 0; i < recBlocks; i++) if (gcFlag[i]) { maxGcCb = std::max(maxGcCb, dur[i]); if (dur[i] > base999 + 500000u) attrib++; }
    double gcExcess = maxGcCb > base999 ? (double)(maxGcCb - base999) : 0.0;
    auto us = [](double ns) { return ns / 1000.0; };
    printf("RT label=%s %s\n", c.label.c_str(), rtMsg.c_str());
    printf("RESULT label=%s block=%d period_us=%.1f load=%d latency=%d seconds=%.1f callbacks=%lld mean_us=%.1f median_us=%.1f p99_us=%.1f p999_us=%.1f p9999_us=%.1f worst_us=%.1f "
           "misses=%lld misses_gc=%lld gc_callbacks=%lld stalls500_all=%lld stalls500_gc=%lld worst_gc_cb_us=%.1f worst_nongc_cb_us=%.1f late_p999_us=%.1f late_max_us=%.1f "
           "gc_count=%d gc_gen1=%d gc_gen2=%d pause_total_ms=%.2f pause_max_ms=%.3f alloc_mb=%.1f resyncs=%lld nongc_p999_us=%.1f nongc_p9999_us=%.1f gc_attrib_stalls=%lld gc_max_excess_us=%.1f\n",
           c.label.c_str(), c.block, us((double)period), c.load, c.latency, wall, (long long)recBlocks, us(sum / recBlocks), us(median), us(pct(sorted, 99)), us(pct(sorted, 99.9)), us(pct(sorted, 99.99)), us(sorted.back()),
           (long long)misses, (long long)missesGc, (long long)gcCbs, (long long)stalls500All, (long long)stalls500, us(worstGc), us(worstNoGc), us(pct(sortedLate, 99.9)), us(sortedLate.back()),
           gcEnd0 - gc0Start, gcEnd1 - gc1Start, gcEnd2 - gc2Start, (pauseEnd - pauseStart) / 1e6, maxPause / 1e6, (allocEnd - allocStart) / 1048576.0, (long long)resyncCount, us(base999), us(base9999), (long long)attrib, us(gcExcess));
    // The ten worst callbacks, to see what they coincide with.
    std::vector<int64_t> idx(recBlocks); for (int64_t i = 0; i < recBlocks; i++) idx[i] = i;
    std::partial_sort(idx.begin(), idx.begin() + std::min<int64_t>(10, recBlocks), idx.end(), [&](int64_t a, int64_t b) { return dur[a] > dur[b]; });
    for (int i = 0; i < std::min<int64_t>(10, recBlocks); i++) printf("WORST label=%s rank=%d at_s=%.2f dur_us=%.1f gc=%d late_us=%.1f\n", c.label.c_str(), i + 1, idx[i] * c.block / kRate, us(dur[idx[i]]), gcFlag[idx[i]], us(late[idx[i]]));
    fflush(stdout);
    return 0;
}
