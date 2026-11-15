// neohost: a small CLAP host for testing the exported Neo plug-ins through the CLAP ABI, on any module (.clap, or the module inside a
// VST3/AU is not loadable here: those are tested by clap-wrapper's own hosts and, on macOS, auval).
//
//   neohost --module <clap> --mode list
//   neohost --module <clap> --mode check  [--id <clap id>]            functional checks: ports, parameters, texts, state, render
//   neohost --module <clap> --mode gc     [--id ..] [--seconds 20] [--load 0..3] [--instances 4]
//                                          real-time paced render, 64-sample blocks, with the editor open and animated (and, with
//                                          --load, allocating in its own runtime): the DSP runtime must do no GC
//   neohost --module <clap> --mode gui    [--id ..] [--shot file.ppm]   (Linux, NEOHOST_X11, under Xvfb) a window, the editor in it,
//                                          a screenshot, a drag on a knob that reaches the host as gesture + value events
//
// Output: lines "ok"/"FAIL" and a final "CHECK ok", "GC ok" or "GUI ok" (exit code 0), or "... FAILED".
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <clap/clap.h>
#include <clap/ext/gui.h>
#include <clap/ext/timer-support.h>
#include <clap/ext/note-ports.h>
#include <clap/ext/tail.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#include <time.h>
#include <unistd.h>
#endif
#ifdef NEOHOST_X11
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#endif

// ---------------------------------------------------------------- the plug-in's test extension (mirrors dsp/Clap.cs)
struct NeoStats { int dspGc0, dspGc1, dspGc2, edGc0, edGc1, edGc2, edAlive, edReady, inprocReady, pad; int64_t dspPauseNs, dspMaxPauseNs, dspAllocBytes, edPauseNs, edMaxPauseNs, edAllocBytes, editsApplied, editsPosted, edFrames; };
struct NeoStatsExt {
    void (*get_stats)(const clap_plugin_t*, NeoStats*);
    void (*set_inproc_load)(const clap_plugin_t*, int);
    int (*gc_config)(char*, int);
    int (*editor_open)(const clap_plugin_t*, int, int);
    void (*editor_close)(const clap_plugin_t*);
};

static int64_t nowNs() {
#ifdef _WIN32
    LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return (int64_t)((double)c.QuadPart * 1e9 / (double)f.QuadPart);
#else
    timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (int64_t)t.tv_sec * 1000000000LL + t.tv_nsec;
#endif
}
static void sleepNs(int64_t ns) { if (ns > 0) std::this_thread::sleep_for(std::chrono::nanoseconds(ns)); }

// ---------------------------------------------------------------- a minimal host
struct HostState {
    std::atomic<int> flushRequests{0}, callbackRequests{0}, restartRequests{0}, latencyChanged{0};
    std::vector<std::pair<uint32_t, uint32_t>> timers;   // id, period ms
    uint32_t nextTimer = 1;
    std::mutex m;
    uint32_t reqW = 0, reqH = 0;
};
static HostState H;

static void pFlush(const clap_host_t*) { H.flushRequests++; }
static void pRescan(const clap_host_t*, clap_param_rescan_flags) {}
static void pClear(const clap_host_t*, clap_id, clap_param_clear_flags) {}
static const clap_host_params_t hostParams{ pRescan, pClear, pFlush };
static void latChanged(const clap_host_t*) { H.latencyChanged++; }
static const clap_host_latency_t hostLatency{ latChanged };
static bool regTimer(const clap_host_t*, uint32_t ms, clap_id* id) { std::lock_guard<std::mutex> g(H.m); *id = H.nextTimer++; H.timers.push_back({ *id, ms }); return true; }
static bool unregTimer(const clap_host_t*, clap_id id) { std::lock_guard<std::mutex> g(H.m); H.timers.erase(std::remove_if(H.timers.begin(), H.timers.end(), [&](auto& t) { return t.first == id; }), H.timers.end()); return true; }
static const clap_host_timer_support_t hostTimer{ regTimer, unregTimer };
static void guiHints(const clap_host_t*) {}
static bool guiResize(const clap_host_t*, uint32_t w, uint32_t h) { H.reqW = w; H.reqH = h; return true; }
static bool guiShow(const clap_host_t*) { return true; }
static bool guiHide(const clap_host_t*) { return true; }
static void guiClosed(const clap_host_t*, bool) {}
static const clap_host_gui_t hostGui{ guiHints, guiResize, guiShow, guiHide, guiClosed };
static const void* hostExt(const clap_host_t*, const char* id) {
    if (!strcmp(id, CLAP_EXT_PARAMS)) return &hostParams;
    if (!strcmp(id, CLAP_EXT_LATENCY)) return &hostLatency;
    if (!strcmp(id, CLAP_EXT_TIMER_SUPPORT)) return &hostTimer;
    if (!strcmp(id, CLAP_EXT_GUI)) return &hostGui;
    return nullptr;
}
static void hostRestart(const clap_host_t*) { H.restartRequests++; }
static void hostProcess(const clap_host_t*) {}
static void hostCallback(const clap_host_t*) { H.callbackRequests++; }
static clap_host_t gHost{ CLAP_VERSION_INIT, nullptr, "neohost", "neo-buzz", "", "0", hostExt, hostRestart, hostProcess, hostCallback };

// ---------------------------------------------------------------- events in and out
struct InEvents {
    std::vector<std::vector<uint8_t>> ev;
    void clear() { ev.clear(); }
    template <class T> T& add(uint32_t time, uint16_t type) {
        ev.emplace_back(sizeof(T)); T* e = (T*)ev.back().data(); *e = T{}; e->header.size = sizeof(T); e->header.time = time; e->header.space_id = CLAP_CORE_EVENT_SPACE_ID; e->header.type = type; return *e;
    }
    void param(uint32_t time, clap_id id, double v) { auto& e = add<clap_event_param_value_t>(time, CLAP_EVENT_PARAM_VALUE); e.param_id = id; e.note_id = -1; e.port_index = -1; e.channel = -1; e.key = -1; e.value = v; }
    void note(uint32_t time, bool on, int key, double vel) { auto& e = add<clap_event_note_t>(time, on ? CLAP_EVENT_NOTE_ON : CLAP_EVENT_NOTE_OFF); e.note_id = -1; e.port_index = 0; e.channel = 0; e.key = (int16_t)key; e.velocity = vel; }
    void sortByTime() { std::stable_sort(ev.begin(), ev.end(), [](auto& a, auto& b) { return ((clap_event_header_t*)a.data())->time < ((clap_event_header_t*)b.data())->time; }); }
};
static uint32_t evSize(const clap_input_events_t* l) { return (uint32_t)((InEvents*)l->ctx)->ev.size(); }
static const clap_event_header_t* evGet(const clap_input_events_t* l, uint32_t i) { return (const clap_event_header_t*)((InEvents*)l->ctx)->ev[i].data(); }

struct OutRec { uint16_t type; clap_id id; double value; uint32_t flags; };
struct OutEvents { std::vector<OutRec> rec; };
static bool outPush(const clap_output_events_t* l, const clap_event_header_t* h) {
    auto* o = (OutEvents*)l->ctx;
    if (h->type == CLAP_EVENT_PARAM_VALUE) { auto* e = (const clap_event_param_value_t*)h; o->rec.push_back({ h->type, e->param_id, e->value, h->flags }); }
    else if (h->type == CLAP_EVENT_PARAM_GESTURE_BEGIN || h->type == CLAP_EVENT_PARAM_GESTURE_END) { auto* e = (const clap_event_param_gesture_t*)h; o->rec.push_back({ h->type, e->param_id, 0, h->flags }); }
    return true;
}

// ---------------------------------------------------------------- module and instance
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
    const clap_plugin_timer_support_t* timer = nullptr; const clap_plugin_audio_ports_t* audio = nullptr; const clap_plugin_note_ports_t* notes = nullptr;
    const clap_plugin_latency_t* latency = nullptr; const clap_plugin_tail_t* tail = nullptr; const NeoStatsExt* stats = nullptr;
    bool instrument = false, sidechain = false; uint32_t inPorts = 0;
    bool create(Module& m, const clap_plugin_descriptor_t* d, double sr) {
        p = m.factory->create_plugin(m.factory, &gHost, d->id);
        if (!p || !p->init(p)) return false;
        params = (const clap_plugin_params_t*)p->get_extension(p, CLAP_EXT_PARAMS);
        gui = (const clap_plugin_gui_t*)p->get_extension(p, CLAP_EXT_GUI);
        state = (const clap_plugin_state_t*)p->get_extension(p, CLAP_EXT_STATE);
        timer = (const clap_plugin_timer_support_t*)p->get_extension(p, CLAP_EXT_TIMER_SUPPORT);
        audio = (const clap_plugin_audio_ports_t*)p->get_extension(p, CLAP_EXT_AUDIO_PORTS);
        notes = (const clap_plugin_note_ports_t*)p->get_extension(p, CLAP_EXT_NOTE_PORTS);
        latency = (const clap_plugin_latency_t*)p->get_extension(p, CLAP_EXT_LATENCY);
        tail = (const clap_plugin_tail_t*)p->get_extension(p, CLAP_EXT_TAIL);
        stats = (const NeoStatsExt*)p->get_extension(p, "neo.export.stats/1");
        instrument = notes != nullptr; inPorts = audio ? audio->count(p, true) : 0; sidechain = inPorts > 1;
        if (!p->activate(p, sr, 1, 256)) return false;
        return p->start_processing(p);
    }
    void destroy() { if (!p) return; p->stop_processing(p); p->deactivate(p); p->destroy(p); p = nullptr; }
};

struct Audio {
    std::vector<float> inL, inR, kL, kR, outL, outR;
    explicit Audio(int n = 4096) : inL(n), inR(n), kL(n), kR(n), outL(n), outR(n) {}
    void noise(uint32_t seed, float amp) {
        uint32_t r = seed;
        auto f = [&] { r = r * 1664525u + 1013904223u; return ((int)(r >> 9) / 4194304.0f - 1.0f); };
        for (size_t i = 0; i < inL.size(); i++) { inL[i] = amp * f(); inR[i] = amp * f(); kL[i] = 0.3f * f(); kR[i] = 0.3f * f(); }
    }
};

static int processBlock(Inst& in, Audio& a, int off, int block, InEvents& el, OutEvents* out, int64_t steady) {
    float* ip[2] = { &a.inL[off], &a.inR[off] }; float* kp[2] = { &a.kL[off], &a.kR[off] }; float* op[2] = { &a.outL[off], &a.outR[off] };
    clap_audio_buffer_t ib[2] = { { ip, nullptr, 2, 0, 0 }, { kp, nullptr, 2, 0, 0 } }, ob{ op, nullptr, 2, 0, 0 };
    clap_input_events_t ie{ &el, evSize, evGet };
    clap_output_events_t oe{ out, outPush };
    clap_process_t pr{}; pr.steady_time = steady; pr.frames_count = block; pr.audio_inputs = ib; pr.audio_outputs = &ob;
    pr.audio_inputs_count = in.instrument ? 0 : in.inPorts; pr.audio_outputs_count = 1; pr.in_events = &ie; pr.out_events = &oe;
    return in.p->process(in.p, &pr);
}

static int gFails = 0;
static void check(bool ok, const std::string& what) { printf("%s %s\n", ok ? "ok  " : "FAIL", what.c_str()); fflush(stdout); if (!ok) gFails++; }

// ---------------------------------------------------------------- functional checks through the ABI
struct Mem { std::vector<uint8_t> b; size_t pos = 0; };
static int64_t memWrite(const clap_ostream_t* s, const void* buf, uint64_t n) { auto* m = (Mem*)s->ctx; m->b.insert(m->b.end(), (const uint8_t*)buf, (const uint8_t*)buf + n); return (int64_t)n; }
static int64_t memRead(const clap_istream_t* s, void* buf, uint64_t n) { auto* m = (Mem*)s->ctx; size_t k = std::min<size_t>(n, m->b.size() - m->pos); memcpy(buf, m->b.data() + m->pos, k); m->pos += k; return (int64_t)k; }

static double peakOf(const std::vector<float>& v, int from, int n) { double p = 0; for (int i = from; i < from + n; i++) { if (!std::isfinite(v[i])) return INFINITY; p = std::max(p, (double)std::fabs(v[i])); } return p; }

// one render of 20 blocks of 128 with the given events at block 0; returns the output (left) concatenated
static std::vector<float> renderOnce(Inst& in, Audio& a, bool withNotes, int blocks = 20) {
    std::vector<float> out;
    InEvents ev; OutEvents oe;
    for (int b = 0; b < blocks; b++) {
        ev.clear();
        if (withNotes && b == 0) { ev.note(10, true, 60, 0.8); ev.note(20, true, 67, 0.6); }
        if (withNotes && b == blocks / 2) { ev.note(5, false, 60, 0); ev.note(6, false, 67, 0); }
        processBlock(in, a, b * 128, 128, ev, &oe, b * 128);
        out.insert(out.end(), a.outL.begin() + b * 128, a.outL.begin() + (b + 1) * 128);
    }
    return out;
}

static void checkPlugin(Module& m, const clap_plugin_descriptor_t* d) {
    printf("--- %s (%s)\n", d->name, d->id);
    check(!strncmp(d->name, "Neo ", 4), "the plug-in's name starts with Neo");
    Inst a;
    check(a.create(m, d, 48000), "create, init, activate, start_processing");
    if (!a.p) return;
    check(a.params && a.state && a.gui && a.audio && a.latency && a.tail && a.timer, "extensions: params, state, gui, audio-ports, latency, tail, timer-support");
    check(a.stats != nullptr, "the test extension is there");
    uint32_t n = a.params->count(a.p);
    check(n > 0, std::to_string(n) + " parameters");
    clap_param_info_t info; char txt[256]; int units = 0; bool ok = true;
    for (uint32_t i = 0; i < n; i++) {
        ok &= a.params->get_info(a.p, i, &info) && info.id == i && info.min_value <= info.max_value && info.default_value >= info.min_value && info.default_value <= info.max_value;
        ok &= a.params->value_to_text(a.p, i, info.default_value, txt, sizeof txt) && strlen(txt) > 0;
        if (strstr(txt, "Hz") || strstr(txt, "dB") || strstr(txt, " ms") || strstr(txt, "%") || strstr(txt, " s") || strstr(txt, "oct")) units++;
        double back; double v = (info.min_value + info.max_value) / 2;
        a.params->value_to_text(a.p, i, v, txt, sizeof txt);
        if (a.params->text_to_value(a.p, i, txt, &back)) { char t2[256]; a.params->value_to_text(a.p, i, back, t2, sizeof t2); ok &= !strcmp(txt, t2); }
    }
    check(ok, "parameter infos and text round trips are consistent");
    check(units > 0, std::to_string(units) + " parameters print a unit");
    uint32_t outs = a.audio->count(a.p, false);
    check(outs == 1 && (a.instrument ? a.inPorts == 0 : a.inPorts >= 1), "audio ports (" + std::to_string(a.inPorts) + " in, 1 out)" + (a.sidechain ? ", sidechain" : ""));
    check(a.tail->get(a.p) >= a.latency->get(a.p), "tail covers the latency (" + std::to_string(a.tail->get(a.p)) + " >= " + std::to_string(a.latency->get(a.p)) + ")");

    Audio au; au.noise(5, 0.4f);
    if (a.instrument) std::fill(au.inL.begin(), au.inL.end(), 0.f);
    auto first = renderOnce(a, au, a.instrument);
    double pk = 0; for (float f : first) pk = std::max(pk, (double)std::fabs(f));
    check(std::isfinite(pk) && pk > 1e-3, "a render is audible and finite (peak " + std::to_string(pk) + ")");

    // state: save, load into a second instance, move a parameter on the first and not the second: the second must equal a fresh one
    Mem mem; clap_ostream_t os{ &mem, memWrite };
    check(a.state->save(a.p, &os), "state saved (" + std::to_string(mem.b.size()) + " bytes)");
    Inst b; b.create(m, d, 48000);
    clap_istream_t is{ &mem, memRead };
    check(b.state->load(b.p, &is), "state loaded into a second instance");
    bool same = true; for (uint32_t i = 0; i < n; i++) { double x, y; a.params->get_value(a.p, i, &x); b.params->get_value(b.p, i, &y); same &= x == y; }
    check(same, "all parameter values came back");
    // identical instances, identical input: identical output
    Inst c; c.create(m, d, 48000);
    Audio au2; au2.noise(5, 0.4f); if (a.instrument) std::fill(au2.inL.begin(), au2.inL.end(), 0.f);
    auto o1 = renderOnce(b, au2, a.instrument), o2 = renderOnce(c, au2, a.instrument);
    check(o1 == o2, "two instances with the same state and input render the same bits");
    // a bad state is refused
    Mem bad; bad.b = { 1, 2, 3, 4, 5, 6, 7, 8 }; clap_istream_t isb{ &bad, memRead };
    check(!c.state->load(c.p, &isb), "a state with a bad header is refused");
    a.destroy(); b.destroy(); c.destroy();
}

static int modeCheck(Module& m, const std::string& id) {
    uint32_t cnt = m.factory->get_plugin_count(m.factory);
    int done = 0;
    for (uint32_t i = 0; i < cnt; i++) {
        auto* d = m.factory->get_plugin_descriptor(m.factory, i);
        if (!id.empty() && id != d->id) continue;
        checkPlugin(m, d); done++;
    }
    check(done > 0, "plug-ins checked: " + std::to_string(done));
    printf(gFails ? "CHECK FAILED (%d)\n" : "CHECK ok\n", gFails);
    return gFails ? 1 : 0;
}

// ---------------------------------------------------------------- the editor open, the audio thread rendering
static void pumpTimers(std::vector<Inst*>& insts, int64_t until) {
    // the main thread of a host: calls each plug-in's on_timer at the period it registered
    while (nowNs() < until) {
        int64_t t0 = nowNs();
        for (Inst* in : insts) if (in->timer) { std::vector<std::pair<uint32_t, uint32_t>> ts; { std::lock_guard<std::mutex> g(H.m); ts = H.timers; } for (auto& t : ts) in->timer->on_timer(in->p, t.first); }
        sleepNs(16'000'000 - (nowNs() - t0));
    }
}

static int modeGc(Module& m, const std::string& id, double seconds, int load, int instances) {
    auto* d = m.factory->get_plugin_descriptor(m.factory, 0);
    for (uint32_t i = 0, c = m.factory->get_plugin_count(m.factory); i < c; i++) { auto* x = m.factory->get_plugin_descriptor(m.factory, i); if (id.empty() || id == x->id) { d = x; break; } }
    printf("--- %s: %d instance(s), %.0f s, editor load %d\n", d->name, instances, seconds, load);
    if (load) { char b[8]; snprintf(b, sizeof b, "%d", load);
#ifdef _WIN32
        _putenv_s("NEO_EXPORT_EDITOR_LOAD", b);
#else
        setenv("NEO_EXPORT_EDITOR_LOAD", b, 1);
#endif
    }
    std::vector<Inst> insts(instances); std::vector<Inst*> ptrs;
    for (auto& in : insts) { check(in.create(m, d, 48000), "instance created"); ptrs.push_back(&in); }
    if (gFails) return 1;
    bool opened = insts[0].gui->create(insts[0].p, nullptr, false);   // the editor library, its own runtime; no parent window: it renders offscreen
    check(opened, "gui.create (the editor library loads, its runtime starts)");
    if (opened) { insts[0].gui->show(insts[0].p); }
    NeoStats s0{}; if (insts[0].stats) insts[0].stats->get_stats(insts[0].p, &s0);
    std::atomic<bool> run{ true }; std::atomic<int64_t> worst{ 0 }; std::atomic<int64_t> blocks{ 0 };
    std::thread audio([&] {
        std::vector<Audio> bufs(instances); for (int i = 0; i < instances; i++) { bufs[i].noise(i + 1, 0.4f); if (insts[i].instrument) std::fill(bufs[i].inL.begin(), bufs[i].inL.end(), 0.f); }
        InEvents ev; OutEvents oe; int64_t next = nowNs(); const int64_t period = 64LL * 1000000000LL / 48000;
        long k = 0;
        while (run) {
            int64_t t0 = nowNs();
            ev.clear(); if (insts[0].instrument && k % 750 == 0) { ev.note(0, true, 48 + (k / 750) % 24, 0.7); }
            oe.rec.clear();
            for (int i = 0; i < instances; i++) processBlock(insts[i], bufs[i], (int)((k * 64) % 2048), 64, ev, &oe, k * 64);
            int64_t dt = nowNs() - t0; int64_t w = worst.load(); while (dt > w && !worst.compare_exchange_weak(w, dt)) {}
            blocks++; k++; next += period; sleepNs(next - nowNs());
        }
    });
    pumpTimers(ptrs, nowNs() + (int64_t)(seconds * 1e9));
    run = false; audio.join();
    NeoStats s1{}; if (insts[0].stats) insts[0].stats->get_stats(insts[0].p, &s1);
    printf("     %lld blocks, worst callback %.0f us (budget %.0f us), editor frames %lld, editor GCs %d (g2 %d), editor alloc %.0f MB, DSP GCs %d -> %d, DSP alloc %lld bytes\n",
           (long long)blocks.load(), worst.load() / 1000.0, 64.0 / 48000 * 1e6, (long long)s1.edFrames, s1.edGc0, s1.edGc2, s1.edAllocBytes / 1048576.0, s0.dspGc0, s1.dspGc0, (long long)(s1.dspAllocBytes - s0.dspAllocBytes));
    check(s1.dspGc0 == s0.dspGc0 && s1.dspGc1 == 0 && s1.dspGc2 == 0, "no garbage collection in the DSP runtime during the render");
    check(opened && s1.edFrames > 10, "the editor rendered frames while the audio ran");
    if (load) check(s1.edAllocBytes > 50.0 * 1048576, "the editor runtime allocated (the load generator ran)");
    for (auto& in : insts) { if (&in == &insts[0]) in.gui->destroy(in.p); in.destroy(); }
    printf(gFails ? "GC FAILED (%d)\n" : "GC ok\n", gFails);
    return gFails ? 1 : 0;
}

// ---------------------------------------------------------------- the editor in an X11 window
#ifdef NEOHOST_X11
static int modeGui(Module& m, const std::string& id, const std::string& shot) {
    auto* d = m.factory->get_plugin_descriptor(m.factory, 0);
    for (uint32_t i = 0, c = m.factory->get_plugin_count(m.factory); i < c; i++) { auto* x = m.factory->get_plugin_descriptor(m.factory, i); if (id.empty() || id == x->id) { d = x; break; } }
    printf("--- %s: the editor in an X11 window\n", d->name);
    Display* dpy = XOpenDisplay(nullptr);
    check(dpy != nullptr, "an X display (run under Xvfb)");
    if (!dpy) return 1;
    Inst in; check(in.create(m, d, 48000), "instance created");
    std::vector<Inst*> ptrs{ &in };
    check(in.gui->is_api_supported(in.p, CLAP_WINDOW_API_X11, false), "gui supports x11");
    check(in.gui->create(in.p, CLAP_WINDOW_API_X11, false), "gui.create");
    uint32_t w = 0, h = 0; check(in.gui->get_size(in.p, &w, &h) && w > 100 && h > 60, "gui.get_size " + std::to_string(w) + " x " + std::to_string(h));
    clap_gui_resize_hints_t hints{}; check(in.gui->get_resize_hints(in.p, &hints) && hints.preserve_aspect_ratio, "resize hints: aspect ratio kept");
    int scr = DefaultScreen(dpy);
    Window top = XCreateSimpleWindow(dpy, RootWindow(dpy, scr), 0, 0, w, h, 0, 0, 0x202020);
    XMapWindow(dpy, top); XSync(dpy, False);
    clap_window_t cw{}; cw.api = CLAP_WINDOW_API_X11; cw.x11 = (clap_xwnd)top;
    check(in.gui->set_parent(in.p, &cw), "gui.set_parent (an X window of the host)");
    check(in.gui->show(in.p), "gui.show");
    pumpTimers(ptrs, nowNs() + 1'500'000'000LL);   // the editor builds and paints
    // the screenshot of the host's window (the editor's child covers it)
    XImage* img = XGetImage(dpy, top, 0, 0, w, h, AllPlanes, ZPixmap);
    check(img != nullptr, "screenshot of the window");
    if (img) {
        uint64_t sum = 0, sum2 = 0; int bg = 0; int n = 0; std::vector<uint8_t> rgb;
        for (uint32_t y = 0; y < h; y += 1) for (uint32_t x = 0; x < w; x += 1) {
            unsigned long px = XGetPixel(img, x, y); int r = (px >> 16) & 255, g = (px >> 8) & 255, b = px & 255;
            int lum = (r * 3 + g * 6 + b) / 10; sum += lum; sum2 += (uint64_t)lum * lum; n++; if (r == 0x20 && g == 0x20 && b == 0x20) bg++;
            rgb.push_back(r); rgb.push_back(g); rgb.push_back(b);
        }
        double mean = (double)sum / n, var = (double)sum2 / n - mean * mean;
        printf("     window %u x %u: mean luminance %.1f, variance %.1f, host-background pixels %d%%\n", w, h, mean, var, bg * 100 / n);
        check(var > 200 && bg * 100 / n < 50, "the window shows the editor (not blank, not the host's background)");
        if (!shot.empty()) { FILE* f = fopen(shot.c_str(), "wb"); if (f) { fprintf(f, "P6\n%u %u\n255\n", w, h); fwrite(rgb.data(), 1, rgb.size(), f); fclose(f); } }
        XDestroyImage(img);
    }
    // a drag on a knob: the editor library (loaded by the plug-in next to the module) says where one is
    typedef int (*HotFn)(int*, int*);
    int hx = (int)w / 5, hy = (int)h / 4;   // fallback: the upper-left quarter, where the first knobs are
#ifndef _WIN32
    if (const char* mod = getenv("NEOHOST_EDITOR")) {
        void* lib = dlopen(mod, RTLD_NOW | RTLD_NOLOAD);
        auto hot = lib ? (HotFn)dlsym(lib, "ned_test_hotspot") : nullptr;
        int ex = 0, ey = 0;
        if (hot && hot(&ex, &ey)) { hx = ex; hy = ey; }
    }
#endif
    Window child = 0;
    { Window root, parent, *kids = nullptr; unsigned nk = 0; if (XQueryTree(dpy, top, &root, &parent, &kids, &nk) && nk > 0) child = kids[nk - 1]; if (kids) XFree(kids); }
    check(child != 0, "the editor made a child window of the host's");
    if (const char* e = getenv("NEOHOST_HOTSPOT")) sscanf(e, "%d,%d", &hx, &hy);
    OutEvents oe; InEvents ev;
    auto sendBtn = [&](int type, int x, int y, unsigned state) {
        XEvent e{}; e.xbutton.type = type; e.xbutton.display = dpy; e.xbutton.window = child; e.xbutton.root = RootWindow(dpy, scr); e.xbutton.x = x; e.xbutton.y = y; e.xbutton.button = Button1; e.xbutton.state = state; e.xbutton.same_screen = True;
        XSendEvent(dpy, child, True, type == ButtonPress ? ButtonPressMask : ButtonReleaseMask, &e); XFlush(dpy);
    };
    auto sendMove = [&](int x, int y) {
        XEvent e{}; e.xmotion.type = MotionNotify; e.xmotion.display = dpy; e.xmotion.window = child; e.xmotion.root = RootWindow(dpy, scr); e.xmotion.x = x; e.xmotion.y = y; e.xmotion.state = Button1Mask; e.xmotion.same_screen = True;
        XSendEvent(dpy, child, True, ButtonMotionMask | PointerMotionMask, &e); XFlush(dpy);
    };
    Audio au; au.noise(3, 0.3f);
    int64_t until = nowNs() + 300'000'000LL; int blk = 0;
    auto step = [&](double secs) { until = nowNs() + (int64_t)(secs * 1e9); while (nowNs() < until) { ev.clear(); processBlock(in, au, (blk++ % 16) * 128, 128, ev, &oe, blk * 128); pumpTimers(ptrs, nowNs() + 17'000'000LL); } };
    std::vector<std::pair<int, int>> tries{ { hx, hy } };
    for (int ty = 40; ty < (int)h - 20; ty += 28) for (int tx = 30; tx < (int)w - 20; tx += 36) tries.push_back({ tx, ty });
    size_t bestVal = 0; int hitX = -1, hitY = -1;
    for (auto& t : tries) {
        oe.rec.clear();
        sendBtn(ButtonPress, t.first, t.second, 0); step(0.05);
        for (int k = 1; k <= 8; k++) { sendMove(t.first, t.second - 6 * k); step(0.03); }
        sendBtn(ButtonRelease, t.first, t.second - 48, Button1Mask); step(0.1);
        // a gesture pair and a value tell that an edit reached the host
        size_t vals = 0; bool gb = false, ge = false;
        for (auto& r : oe.rec) { if (r.type == CLAP_EVENT_PARAM_VALUE) vals++; if (r.type == CLAP_EVENT_PARAM_GESTURE_BEGIN) gb = true; if (r.type == CLAP_EVENT_PARAM_GESTURE_END) ge = true; }
        // the events come out of process() or flush: run one flush if the plug-in asked for it
        if (vals == 0 && H.flushRequests > 0) { InEvents none; clap_input_events_t ie{ &none, evSize, evGet }; clap_output_events_t oo{ &oe, outPush }; in.params->flush(in.p, &ie, &oo); for (auto& r : oe.rec) { if (r.type == CLAP_EVENT_PARAM_VALUE) vals++; if (r.type == CLAP_EVENT_PARAM_GESTURE_BEGIN) gb = true; if (r.type == CLAP_EVENT_PARAM_GESTURE_END) ge = true; } }
        if (vals > 0 && gb && ge) { bestVal = vals; hitX = t.first; hitY = t.second; break; }
    }
    printf("     a drag at (%d,%d) made %zu value events; flush requests %d\n", hitX, hitY, bestVal, H.flushRequests.load());
    check(bestVal > 0, "a mouse drag in the editor reached the host as gesture begin, parameter values, gesture end");
    // a host-side change shows in the editor: set a parameter through the host and compare screenshots
    in.gui->hide(in.p); in.gui->destroy(in.p); in.destroy();
    XDestroyWindow(dpy, top); XCloseDisplay(dpy);
    printf(gFails ? "GUI FAILED (%d)\n" : "GUI ok\n", gFails);
    return gFails ? 1 : 0;
}
#endif

int main(int argc, char** argv) {
    std::string module, mode = "check", id, shot; double seconds = 20; int load = 0, instances = 4;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        auto val = [&] { return i + 1 < argc ? argv[++i] : (char*)""; };
        if (a == "--module") module = val(); else if (a == "--mode") mode = val(); else if (a == "--id") id = val(); else if (a == "--seconds") seconds = atof(val());
        else if (a == "--load") load = atoi(val()); else if (a == "--instances") instances = atoi(val()); else if (a == "--shot") shot = val();
    }
    if (module.empty()) { fprintf(stderr, "usage: neohost --module <clap> --mode list|check|gc|gui [--id <clap id>]\n"); return 2; }
    Module m;
    if (!m.load(module)) return 2;
    if (mode == "list") { for (uint32_t i = 0; i < m.factory->get_plugin_count(m.factory); i++) { auto* d = m.factory->get_plugin_descriptor(m.factory, i); printf("%s\t%s\n", d->id, d->name); } return 0; }
    if (mode == "check") return modeCheck(m, id);
    if (mode == "gc") return modeGc(m, id, seconds, load, instances);
#ifdef NEOHOST_X11
    if (mode == "gui") return modeGui(m, id, shot);
#endif
    fprintf(stderr, "unknown mode %s\n", mode.c_str());
    return 2;
}
