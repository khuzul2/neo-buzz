// CLAP bench: loads a module exporting clap_entry (.clap), runs a real-time-like audio thread (highest priority available,
// one callback per block paced to the wall clock, N instances, parameter automation every block) and records every callback,
// as experiments/plugin-gc/host/harness.cpp did for a bare C ABI. The editor library is opened through the plug-in's own
// spike extension ("neo.spike.stats/1"), which also reports the GC figures of both runtimes.
//
//   clapbench --clap <module> --block 64 --seconds 300 --mode none|editor|inproc [--load 0..3] [--label text] [--warmup 10]
//     mode none:   no editor, nothing else running in the plug-in's runtime
//     mode editor: the editor library (its own runtime) runs load N
//     mode inproc: the same load N in the DSP library's own runtime (the plugin-gc situation: one runtime)
//   clapbench --clap <module> --selftest     functional checks through the CLAP ABI (params, state, gui, editor ring)
//
// Output: "RESULT key=value ..." (one line), "GCCFG ...", "RT ..." and "WORST ..." lines; ci/summarize.py makes tables.
#include <algorithm>
#include <atomic>
#include <functional>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#include <clap/clap.h>
#include <clap/ext/gui.h>

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


// ---------------------------------------------------------------- the spike's extension (mirrors dsp/Clap.cs)
struct NeoStats { int dspGc0, dspGc1, dspGc2, edGc0, edGc1, edGc2, edAlive, edReady, inprocReady, pad; int64_t dspPauseNs, dspMaxPauseNs, dspAllocBytes, edPauseNs, edMaxPauseNs, edAllocBytes, editsApplied, editsPosted, edFrames; };
struct NeoSpikeExt {
    void (*getStats)(const clap_plugin_t*, NeoStats*);
    void (*setInprocLoad)(const clap_plugin_t*, int);
    int (*gcConfig)(char*, int);
    int (*editorOpen)(const clap_plugin_t*, int load, int edits);
    void (*editorClose)(const clap_plugin_t*);
};

// ---------------------------------------------------------------- a minimal host
static const void* hostExt(const clap_host_t*, const char*) { return nullptr; }
static void hostNop(const clap_host_t*) {}
static clap_host_t gHost{ CLAP_VERSION_INIT, nullptr, "clapbench", "neo-buzz", "", "0", hostExt, hostNop, hostNop, hostNop };

struct EventList {
    clap_event_param_value_t ev[4]; uint32_t n = 0;
    void add(uint32_t time, clap_id id, double v) {
        clap_event_param_value_t& e = ev[n++];
        e = {}; e.header.size = sizeof e; e.header.time = time; e.header.space_id = CLAP_CORE_EVENT_SPACE_ID; e.header.type = CLAP_EVENT_PARAM_VALUE;
        e.param_id = id; e.note_id = -1; e.port_index = -1; e.channel = -1; e.key = -1; e.value = v;
    }
};
static uint32_t evSize(const clap_input_events_t* l) { return ((EventList*)l->ctx)->n; }
static const clap_event_header_t* evGet(const clap_input_events_t* l, uint32_t i) { return &((EventList*)l->ctx)->ev[i].header; }

struct Module {
    void* lib = nullptr; const clap_plugin_entry_t* entry = nullptr; const clap_plugin_factory_t* factory = nullptr;
    bool load(const std::string& path) {
#ifdef _WIN32
        HMODULE h = LoadLibraryA(path.c_str()); lib = h;
        if (h) entry = (const clap_plugin_entry_t*)GetProcAddress(h, "clap_entry");
#else
        lib = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
        if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return false; }
        entry = (const clap_plugin_entry_t*)dlsym(lib, "clap_entry");
#endif
        if (!entry) { fprintf(stderr, "no clap_entry in %s\n", path.c_str()); return false; }
        if (!entry->init(path.c_str())) { fprintf(stderr, "clap_entry.init failed\n"); return false; }
        factory = (const clap_plugin_factory_t*)entry->get_factory(CLAP_PLUGIN_FACTORY_ID);
        return factory && factory->get_plugin_count(factory) > 0;
    }
};

struct Inst {
    const clap_plugin_t* p = nullptr; const clap_plugin_params_t* params = nullptr; const clap_plugin_gui_t* gui = nullptr; const clap_plugin_state_t* state = nullptr;
    const NeoSpikeExt* spike = nullptr;
    bool create(Module& m, double sr) {
        const clap_plugin_descriptor_t* d = m.factory->get_plugin_descriptor(m.factory, 0);
        p = m.factory->create_plugin(m.factory, &gHost, d->id);
        if (!p || !p->init(p)) return false;
        params = (const clap_plugin_params_t*)p->get_extension(p, CLAP_EXT_PARAMS);
        gui = (const clap_plugin_gui_t*)p->get_extension(p, CLAP_EXT_GUI);
        state = (const clap_plugin_state_t*)p->get_extension(p, CLAP_EXT_STATE);
        spike = (const NeoSpikeExt*)p->get_extension(p, "neo.spike.stats/1");
        if (!p->activate(p, sr, 1, 256)) return false;
        return p->start_processing(p);
    }
};

struct Bufs {
    std::vector<float> in, outL, outR;
    Bufs() : in((1 << 16) + 256), outL(256), outR(256) {
        uint32_t rnd = 1;
        for (int i = 0; i < (int)in.size(); i++) { rnd = rnd * 1664525u + 1013904223u; in[i] = 0.4f * (float)(((i * 220) % 480) / 240.0 - 1.0) + 0.1f * ((int)(rnd >> 9) / 4194304.0f - 1.0f); }
    }
};

static int processBlock(const clap_plugin_t* p, Bufs& b, int off, int block, EventList& el, int64_t steady)
{
    float* inp[2] = { &b.in[off], &b.in[off + 17] };
    float* outp[2] = { b.outL.data(), b.outR.data() };
    clap_audio_buffer_t ib{ inp, nullptr, 2, 0, 0 }, ob{ outp, nullptr, 2, 0, 0 };
    clap_input_events_t ie{ &el, evSize, evGet };
    clap_process_t pr{}; pr.steady_time = steady; pr.frames_count = block; pr.audio_inputs = &ib; pr.audio_outputs = &ob; pr.audio_inputs_count = 1; pr.audio_outputs_count = 1; pr.in_events = &ie;
    return p->process(p, &pr);
}

// ---------------------------------------------------------------- functional checks
static int selftest(Module& m)
{
    int fails = 0;
    auto check = [&](bool ok, const char* what) { printf("%s %s\n", ok ? "ok  " : "FAIL", what); if (!ok) fails++; };
    Inst a;
    check(a.create(m, kRate), "create, init, activate, start_processing");
    check(a.params && a.params->count(a.p) == 18, "18 parameters");
    clap_param_info_t info; char txt[128];
    a.params->get_info(a.p, 2, &info); a.params->value_to_text(a.p, 2, info.default_value, txt, sizeof txt);
    printf("     param 2: %s (module \"%s\"), default \"%s\", range %g..%g\n", info.name, info.module, txt, info.min_value, info.max_value);
    check(!strcmp(info.name, "Cutoff") && strstr(txt, "Hz"), "Cutoff has its name and a unit in its text");
    // state round trip through the host's stream
    struct Mem { std::vector<uint8_t> b; size_t pos = 0; } mem;
    clap_ostream_t os{ &mem, [](const clap_ostream_t* s, const void* buf, uint64_t n) -> int64_t { auto* m = (Mem*)s->ctx; m->b.insert(m->b.end(), (const uint8_t*)buf, (const uint8_t*)buf + n); return (int64_t)n; } };
    clap_istream_t is{ &mem, [](const clap_istream_t* s, void* buf, uint64_t n) -> int64_t { auto* m = (Mem*)s->ctx; size_t k = std::min<size_t>(n, m->b.size() - m->pos); memcpy(buf, m->b.data() + m->pos, k); m->pos += k; return (int64_t)k; } };
    Bufs bufs; EventList el; el.add(0, 2, 4321);
    processBlock(a.p, bufs, 0, 64, el, 0);
    check(a.state && a.state->save(a.p, &os) && mem.b.size() == 80, "state saved (80 bytes)");
    Inst b; check(b.create(m, kRate), "second instance");
    check(b.state->load(b.p, &is), "state loaded");
    double v = 0; b.params->get_value(b.p, 2, &v);
    check(v == 4321, "Cutoff came back through the state");
    // the gui extension opens the editor library; its edits come back through the ring
    const char* api = CLAP_WINDOW_API_X11;
#ifdef _WIN32
    api = CLAP_WINDOW_API_WIN32;
#elif defined(__APPLE__)
    api = CLAP_WINDOW_API_COCOA;
#endif
    check(a.gui && a.gui->is_api_supported(a.p, api, false), "gui: native window API supported");
    check(a.gui->create(a.p, api, false), "gui: create loads the editor library");
    uint32_t w = 0, h = 0; a.gui->get_size(a.p, &w, &h); check(w > 0 && h > 0, "gui: size");
    int64_t steady = 0; NeoStats st{}, st0{}; a.spike->getStats(a.p, &st0);
    int64_t t0 = now_ns();
    while (now_ns() - t0 < 3500000000LL) {          // 3.5 s of audio in real time
        EventList none; processBlock(a.p, bufs, (int)(steady * 7 & 0xFFFF), 64, none, steady); steady += 64;
        wait_until(t0 + steady * 1000000000LL / (int64_t)kRate);
    }
    a.spike->getStats(a.p, &st);
    printf("     editor: alive=%d frames=%lld edits posted=%lld applied=%lld, editor GCs=%d, DSP-runtime GCs=%d\n", st.edAlive, (long long)st.edFrames, (long long)st.editsPosted, (long long)st.editsApplied, st.edGc0, st.dspGc0 - st0.dspGc0);
    check(st.edAlive == 1 && st.edFrames > 50, "editor loop runs (60 Hz)");
    check(st.editsPosted >= 2 && st.editsApplied >= st.editsPosted - 1 && st.editsApplied >= 2, "editor edits reach the audio thread through the ring");
    a.params->get_value(a.p, 17, &v);
    check(v >= 239 && v <= 243, "the edit is the parameter's value");
    check(st.dspGc0 == st0.dspGc0, "the DSP runtime did no GC meanwhile");
    a.gui->destroy(a.p);
    a.spike->getStats(a.p, &st);
    check(st.edAlive == 0, "gui: destroy stops the editor");
    a.p->stop_processing(a.p); a.p->destroy(a.p); b.p->destroy(b.p);
    printf(fails ? "SELFTEST FAILED (%d)\n" : "SELFTEST ok\n", fails);
    return fails ? 1 : 0;
}

struct Config { std::string clap, label = "run", mode = "none"; int block = 64; double seconds = 300, warmup = 10; int load = 0, instances = 16; bool selftest = false; };

int main(int argc, char** argv)
{
    Config c;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        auto val = [&] { return i + 1 < argc ? argv[++i] : (char*)""; };
        if (a == "--clap") c.clap = val(); else if (a == "--block") c.block = atoi(val()); else if (a == "--seconds") c.seconds = atof(val());
        else if (a == "--warmup") c.warmup = atof(val()); else if (a == "--load") c.load = atoi(val()); else if (a == "--mode") c.mode = val();
        else if (a == "--label") c.label = val(); else if (a == "--instances") c.instances = atoi(val()); else if (a == "--selftest") c.selftest = true;
    }
    if (c.clap.empty() || c.block < 1 || c.block > 256) { fprintf(stderr, "usage: clapbench --clap module --block 64 --seconds 300 --mode none|editor|inproc --load 0..3\n"); return 2; }
#ifdef _WIN32
    LARGE_INTEGER f; QueryPerformanceFrequency(&f); qpcScale = 1e9 / (double)f.QuadPart;
    timeBeginPeriod(1);
    gTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!gTimer) gTimer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
#elif defined(__APPLE__)
    mach_timebase_info(&tb);
#endif
    if (c.selftest) {
#ifdef _WIN32
        _putenv_s("NEO_EXPORT_EDITOR_EDITS", "1");
#else
        setenv("NEO_EXPORT_EDITOR_EDITS", "1", 1);
#endif
    }
    Module mod;
    if (!mod.load(c.clap)) return 3;
    if (c.selftest) return selftest(mod);

    const int N = c.instances;
    std::vector<Inst> inst(N);
    for (int i = 0; i < N; i++) if (!inst[i].create(mod, kRate)) { fprintf(stderr, "instance %d failed\n", i); return 4; }
    const NeoSpikeExt* sp = inst[0].spike;
    if (!sp) { fprintf(stderr, "the module has no neo.spike.stats extension\n"); return 5; }
    char cfg[512] = "";
    if (c.mode == "editor" && c.load >= 0) { if (!sp->editorOpen(inst[0].p, c.load, 0)) { fprintf(stderr, "editor library did not open\n"); return 6; } }
    else if (c.mode == "inproc") sp->setInprocLoad(inst[0].p, c.load);
    for (int i = 0; i < 1200; i++) {                        // wait for the load's live heap
        NeoStats s{}; sp->getStats(inst[0].p, &s);
        if (c.mode == "none" || (c.mode == "editor" ? s.edReady : s.inprocReady)) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    sp->gcConfig(cfg, sizeof cfg);
    printf("GCCFG label=%s mode=%s %s\n", c.label.c_str(), c.mode.c_str(), cfg);

    Bufs bufs;
    const int InLen = 1 << 16;
    const int64_t period = (int64_t)((double)c.block * 1e9 / kRate);
    const int64_t warmBlocks = (int64_t)(c.warmup * kRate / c.block), recBlocks = (int64_t)(c.seconds * kRate / c.block);
    std::vector<uint32_t> dur(recBlocks), late(recBlocks);
    std::vector<uint8_t> gcFlag(recBlocks), edFlag(recBlocks);
    int64_t resyncCount = 0;
    std::string rtMsg;
    NeoStats st0{}, st1{};
    int64_t startedNs = 0;

    std::thread audio([&] {
        rtMsg = makeRealtime(period);
        int64_t t0 = now_ns() + 20000000;
        int64_t resyncs = 0;
        float peak = 0;
        NeoStats sa{}, sb{};
        for (int64_t k = -warmBlocks; k < recBlocks; k++) {
            int64_t sched = t0 + (k + warmBlocks) * period;
            wait_until(sched);
            if (now_ns() - sched > 50 * period) { t0 += now_ns() - sched; sched = now_ns(); resyncs++; }   // hopelessly behind (a host would have dropped out): restart the schedule
            if (k == 0) { sp->getStats(inst[0].p, &st0); startedNs = now_ns(); }
            sp->getStats(inst[0].p, &sa);
            int64_t t = now_ns();
            int64_t blk = k + warmBlocks;
            int tri = (int)(blk % 400); tri = tri < 200 ? tri : 400 - tri;           // triangle 0..200
            int off = (int)((blk * c.block) & (InLen - 1));
            for (int i = 0; i < N; i++) {
                EventList el;
                el.add(0, 2, 3500 + tri * 10 + (i & 3) * 100);                          // Cutoff, every block
                el.add(c.block / 2, 3, 200 + tri * 2);                                  // Resonance, mid-block
                processBlock(inst[i].p, bufs, off, c.block, el, blk * c.block);
                peak += bufs.outL[0];
            }
            int64_t t1 = now_ns();
            sp->getStats(inst[0].p, &sb);
            if (k >= 0) {
                dur[k] = (uint32_t)std::min<int64_t>(t1 - t, 0xFFFFFFFFLL); late[k] = (uint32_t)std::max<int64_t>(0, std::min<int64_t>(t - sched, 0xFFFFFFFFLL));
                gcFlag[k] = sb.dspGc0 != sa.dspGc0; edFlag[k] = sb.edGc0 != sa.edGc0;
            }
        }
        sp->getStats(inst[0].p, &st1);
        resyncCount = resyncs;
        if (peak == 12345.678f) printf("?\n");
    });
    audio.join();
    double wall = (now_ns() - startedNs) / 1e9;
    if (c.mode == "editor") sp->editorClose(inst[0].p); else if (c.mode == "inproc") sp->setInprocLoad(inst[0].p, 0);

    // ---- statistics
    std::vector<uint8_t> anyFlag(recBlocks);
    for (int64_t i = 0; i < recBlocks; i++) anyFlag[i] = gcFlag[i] || edFlag[i];
    auto pct = [&](std::vector<uint32_t>& v, double p) { size_t i = (size_t)std::min<double>(v.size() - 1, std::ceil(p / 100.0 * v.size()) - 1); return v[i]; };
    std::vector<uint32_t> sorted(dur), sortedLate(late);
    std::sort(sorted.begin(), sorted.end()); std::sort(sortedLate.begin(), sortedLate.end());
    double sum = 0; for (uint32_t d : dur) sum += d;
    uint32_t median = sorted[sorted.size() / 2];
    int64_t misses = 0, missesDspGc = 0, dspGcCbs = 0, edGcCbs = 0, stalls500 = 0, stalls500All = 0; uint32_t worstGc = 0;
    for (int64_t i = 0; i < recBlocks; i++) {
        bool miss = dur[i] > period;
        misses += miss; missesDspGc += miss && gcFlag[i]; dspGcCbs += gcFlag[i]; edGcCbs += edFlag[i];
        if (dur[i] > median + 500000) { stalls500All++; if (anyFlag[i]) stalls500++; }
        if (anyFlag[i]) worstGc = std::max(worstGc, dur[i]);
    }
    // Callbacks without a GC in either runtime set the baseline; a GC callback is an "attributable stall" when it exceeds that
    // baseline's 99.9th percentile by more than 0.5 ms (a lower bound: the editor's GC flag has the 1 ms resolution of its sampler).
    std::vector<uint32_t> base; base.reserve(recBlocks);
    for (int64_t i = 0; i < recBlocks; i++) if (!anyFlag[i]) base.push_back(dur[i]);
    std::sort(base.begin(), base.end());
    uint32_t base999 = base.empty() ? 0 : pct(base, 99.9), base9999 = base.empty() ? 0 : pct(base, 99.99);
    int64_t attrib = 0;
    for (int64_t i = 0; i < recBlocks; i++) if (anyFlag[i] && dur[i] > base999 + 500000u) attrib++;
    auto us = [](double ns) { return ns / 1000.0; };
    printf("RT label=%s %s\n", c.label.c_str(), rtMsg.c_str());
    printf("RESULT label=%s mode=%s block=%d period_us=%.1f load=%d seconds=%.1f instances=%d callbacks=%lld mean_us=%.1f median_us=%.1f p99_us=%.1f p999_us=%.1f p9999_us=%.1f worst_us=%.1f "
           "misses=%lld misses_dspgc=%lld stalls500_all=%lld stalls500_gc=%lld worst_gc_cb_us=%.1f late_p999_us=%.1f late_max_us=%.1f resyncs=%lld base_p999_us=%.1f base_p9999_us=%.1f gc_attrib_stalls=%lld "
           "dsp_gc=%d dsp_gc1=%d dsp_gc2=%d dsp_gc_cbs=%lld dsp_pause_total_ms=%.2f dsp_pause_max_ms=%.3f dsp_alloc_mb=%.1f "
           "ed_gc=%d ed_gc1=%d ed_gc2=%d ed_gc_cbs=%lld ed_pause_total_ms=%.2f ed_pause_max_ms=%.3f ed_alloc_mb=%.1f\n",
           c.label.c_str(), c.mode.c_str(), c.block, us((double)period), c.load, wall, N, (long long)recBlocks, us(sum / recBlocks), us(median), us(pct(sorted, 99)), us(pct(sorted, 99.9)), us(pct(sorted, 99.99)), us(sorted.back()),
           (long long)misses, (long long)missesDspGc, (long long)stalls500All, (long long)stalls500, us(worstGc), us(pct(sortedLate, 99.9)), us(sortedLate.back()), (long long)resyncCount, us(base999), us(base9999), (long long)attrib,
           st1.dspGc0 - st0.dspGc0, st1.dspGc1 - st0.dspGc1, st1.dspGc2 - st0.dspGc2, (long long)dspGcCbs, (st1.dspPauseNs - st0.dspPauseNs) / 1e6, st1.dspMaxPauseNs / 1e6, (st1.dspAllocBytes - st0.dspAllocBytes) / 1048576.0,
           st1.edGc0 - st0.edGc0, st1.edGc1 - st0.edGc1, st1.edGc2 - st0.edGc2, (long long)edGcCbs, (st1.edPauseNs - st0.edPauseNs) / 1e6, st1.edMaxPauseNs / 1e6, (st1.edAllocBytes - st0.edAllocBytes) / 1048576.0);
    std::vector<int64_t> idx(recBlocks); for (int64_t i = 0; i < recBlocks; i++) idx[i] = i;
    std::partial_sort(idx.begin(), idx.begin() + std::min<int64_t>(10, recBlocks), idx.end(), [&](int64_t a, int64_t b) { return dur[a] > dur[b]; });
    for (int i = 0; i < std::min<int64_t>(10, recBlocks); i++) printf("WORST label=%s rank=%d at_s=%.2f dur_us=%.1f dspgc=%d edgc=%d late_us=%.1f\n", c.label.c_str(), i + 1, idx[i] * c.block / kRate, us(dur[idx[i]]), gcFlag[idx[i]], edFlag[idx[i]], us(late[idx[i]]));
    for (auto& i : inst) { i.p->stop_processing(i.p); i.p->destroy(i.p); }
    fflush(stdout);
    return 0;
}
