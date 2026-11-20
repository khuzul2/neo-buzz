// neoplug-sandbox-test (docs/specs/plugin-host.md H2): what the sandbox has to do, through the C ABI alone, for CI on Linux, Windows and macOS.
//
//   neoplug-sandbox-test smoke   <plugin> [--id ID] [--placement N]          load, play, automation, state round trip, latency, offline render, against in process
//   neoplug-sandbox-test crash   <crasher.clap>                              a server that crashes, hangs or crashes while saving; the others and a reload
//   neoplug-sandbox-test latency <plugin> [--id ID] [--placement N] [--block 128] [--blocks 20000]   paced round trip, p50 / p99
//   neoplug-sandbox-test chain   <plugin>[#id] ... [--placement N] [--block 128] [--seconds S] [--baseline S]   the hour test, bounded
//
// Prints "ok" / "FAIL" lines and, for the measuring commands, one JSON object. Exit code 0 when every check passes.
#include "neoplug.h"
#include "np_ipc.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#if defined(_WIN32)
#include <mmsystem.h>
#else
#include <unistd.h>
#include <signal.h>
#endif

namespace {

using Clock = std::chrono::steady_clock;
int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what.c_str());
    std::fflush(stdout);
    if (!ok) failures++;
}

std::string opt(int argc, char** argv, const char* name)
{
    for (int i = 0; i + 1 < argc; i++) if (!std::strcmp(argv[i], name)) return argv[i + 1];
    return "";
}

np_plugin* open(const std::string& path, const std::string& id, int placement, std::string* why = nullptr)
{
    char err[1024] = {0};
    np_plugin* p = np_open_ex(path.c_str(), id.empty() ? nullptr : id.c_str(), placement, err, sizeof err);
    if (!p && why) *why = err;
    return p;
}

uint32_t rng = 12345;
float noise() { rng = rng * 1664525u + 1013904223u; return ((rng >> 9) & 0xffff) / 32768.0f - 1.0f; }

struct Render
{
    std::vector<float> l, r;
};

// A fixed scenario: `blocks` blocks of `frames`; a note (instruments) or noise (effects) from the first block; param (if >= 0) set at
// block `autoAt` to `autoValue`. Returns every output sample, or fails (rc < 0) at the first block that does.
bool render(np_plugin* p, bool instrument, int blocks, int frames, long autoParam, double autoValue, int autoAt, Render& out, int* rcOut = nullptr)
{
    rng = 12345;
    std::vector<float> inL((size_t)frames), inR((size_t)frames), oL((size_t)frames), oR((size_t)frames);
    for (int b = 0; b < blocks; b++)
    {
        for (int i = 0; i < frames; i++) { inL[(size_t)i] = noise() * 0.25f; inR[(size_t)i] = noise() * 0.25f; }
        const float* ins[2] = {inL.data(), inR.data()};
        float* outs[2] = {oL.data(), oR.data()};
        np_event ev[4];
        int n = 0;
        if (b == 0 && instrument)
        {
            ev[n] = {}; ev[n].frame = 0; ev[n].type = NP_EV_NOTE_ON; ev[n].channel = 0; ev[n].key = 60; ev[n].note_id = 1; ev[n].value = 0.8; n++;
        }
        if (autoParam >= 0 && b == autoAt)
        {
            ev[n] = {}; ev[n].frame = 10; ev[n].type = NP_EV_PARAM; ev[n].param_id = (uint32_t)autoParam; ev[n].value = autoValue; n++;
        }
        np_event outEv[16];
        np_process_io io{};
        io.in = instrument ? nullptr : ins; io.in_ch = instrument ? 0 : 2;
        io.out = outs; io.out_ch = 2; io.frames = frames;
        io.events = ev; io.nevents = n; io.out_events = outEv; io.out_cap = 16;
        io.tempo = 120; io.playing = 1; io.beat_pos = b * frames * 120.0 / (60.0 * 48000); io.tsig_num = 4; io.tsig_den = 4;
        int rc = np_process_ex(p, &io);
        if (rcOut) *rcOut = rc;
        if (rc < 0) return false;
        out.l.insert(out.l.end(), oL.begin(), oL.end());
        out.r.insert(out.r.end(), oR.begin(), oR.end());
    }
    return true;
}

bool same(const Render& a, const Render& b)
{
    return a.l.size() == b.l.size() && std::memcmp(a.l.data(), b.l.data(), a.l.size() * sizeof(float)) == 0 &&
           std::memcmp(a.r.data(), b.r.data(), a.r.size() * sizeof(float)) == 0;
}

double rms(const Render& a)
{
    double s = 0;
    for (float v : a.l) s += (double)v * v;
    for (float v : a.r) s += (double)v * v;
    return std::sqrt(s / (double)std::max<size_t>(1, a.l.size() + a.r.size()));
}

bool exactPlugin = true;   // false for a plug-in that isn't deterministic (random phases): then the loudness is compared, as headless-test does

bool eq(const Render& a, const Render& b)
{
    if (exactPlugin) return same(a, b);
    double x = rms(a), y = rms(b);
    return a.l.size() == b.l.size() && x > 0 && y > 0 && y / x > 0.8 && y / x < 1.25;
}

double peak(const Render& a)
{
    double m = 0;
    for (float v : a.l) m = std::max(m, (double)std::fabs(v));
    for (float v : a.r) m = std::max(m, (double)std::fabs(v));
    return m;
}

bool fresh(np_plugin* p, double rate, int maxFrames, int offline)
{
    char err[512] = {0};
    return np_activate(p, rate, maxFrames, offline, err, sizeof err) == 0;
}

std::vector<uint8_t> save(np_plugin* p)
{
    int n = np_state_save(p, nullptr, 0);
    std::vector<uint8_t> s((size_t)std::max(n, 0));
    if (n > 0) np_state_save(p, s.data(), n);
    return s;
}

// A parameter event lands on its own sample (np_event.frame), in process and in a sandbox (docs/specs/plugin-host.md): the gain effect takes
// its gain when an input sample enters its delay line, so the edge of a gain change at frame `at` of a block is `at` + latency samples later.
void edge(np_plugin* p, const std::string& tag)
{
    const int frames = 128, blocks = 3, at = 70;
    char err[256] = {0};
    check(np_activate(p, 48000, 256, 0, err, sizeof err) == 0, tag + "edge: activates");
    np_reset(p);
    int lat = np_latency(p);
    const double g0 = 0.0, g1 = -6.0;
    std::vector<float> in((size_t)frames, 0.5f), oL((size_t)frames), oR((size_t)frames), out;
    for (int b = 0; b < blocks; b++)
    {
        const float* ins[2] = {in.data(), in.data()};
        float* outs[2] = {oL.data(), oR.data()};
        np_event ev{};
        ev.frame = (uint32_t)(b == 0 ? 0 : at); ev.type = NP_EV_PARAM; ev.param_id = 1; ev.value = b == 0 ? g0 : g1;
        np_process_io io{};
        io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = frames;
        io.events = b <= 1 ? &ev : nullptr; io.nevents = b <= 1 ? 1 : 0;
        io.tempo = 120; io.playing = 1; io.beat_pos = -1; io.tsig_num = 4; io.tsig_den = 4;
        if (np_process_ex(p, &io) < 0) { check(false, tag + "edge: plays"); return; }
        out.insert(out.end(), oL.begin(), oL.end());
    }
    size_t k = (size_t)(frames + at + lat);
    float oldv = (float)(0.5 * std::pow(10.0, g0 / 20.0)), newv = (float)(0.5 * std::pow(10.0, g1 / 20.0));
    bool ok = k < out.size() && std::fabs(out[k - 1] - oldv) < 1e-6 && std::fabs(out[k] - newv) < 1e-6 && std::fabs(oldv - newv) > 0.01;
    check(ok, tag + "a parameter event at frame " + std::to_string(at) + " changes the output exactly at sample " + std::to_string(k) + " (latency " + std::to_string(lat) +
          ": " + std::to_string(k < out.size() ? out[k - 1] : 0.f) + " then " + std::to_string(k < out.size() ? out[k] : 0.f) + ")");
    // np_flush (the plug-in's own window changed a value) crosses to the sandbox's server like the other calls: it answers and the plug-in plays on
    np_flush(p);
    np_process_io io{};
    const float* ins[2] = {in.data(), in.data()};
    float* outs[2] = {oL.data(), oR.data()};
    io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = frames; io.tempo = 120; io.playing = 1; io.beat_pos = -1; io.tsig_num = 4; io.tsig_den = 4;
    check(np_status(p) != 2 && np_process_ex(p, &io) >= 0, tag + "np_flush answers and the plug-in plays on");
}

// --- smoke ---------------------------------------------------------------------------------------------------------------

int smoke(const std::string& path, const std::string& id, int placement)
{
    const char* names[] = {"in process", "sandbox per plug-in", "sandbox per instance", "sandbox shared"};
    std::string tag = std::string(names[placement]) + ": ";
    std::string why;
    np_plugin* ref = open(path, id, 0, &why);
    check(ref != nullptr, tag + "reference loads in process " + why);
    if (!ref) return 1;
    np_plugin* sb = open(path, id, placement, &why);
    check(sb != nullptr, tag + "loads " + why);
    if (!sb) return 1;
    check(np_placement(sb) == placement && np_status(sb) == 1, tag + "placement and status");
    check(np_server_pid(sb) > 0, tag + "server pid " + std::to_string(np_server_pid(sb)));
    np_class_info a{}, b{};
    np_info(ref, &a); np_info(sb, &b);
    check(!std::strcmp(a.name, b.name) && !std::strcmp(a.id, b.id) && a.is_instrument == b.is_instrument, tag + "same plug-in info (" + std::string(b.name) + ")");
    bool inst = a.is_instrument != 0;
    check(np_param_count(ref) == np_param_count(sb), tag + "same parameter list (" + std::to_string(np_param_count(sb)) + ")");
    long autoId = -1; double autoVal = 0;
    for (int i = 0; i < np_param_count(sb); i++)
    {
        np_param_info x{}, y{};
        np_param_info_at(ref, i, &x); np_param_info_at(sb, i, &y);
        if (x.id != y.id || std::strcmp(x.name, y.name)) { check(false, tag + "parameter " + std::to_string(i) + " differs"); break; }
        if (autoId < 0 && (x.flags & NP_PARAM_AUTOMATABLE) && !(x.flags & (NP_PARAM_READONLY | NP_PARAM_BYPASS)) && x.steps == 0)
        { autoId = x.id; autoVal = x.min + (x.max - x.min) * 0.25; }
    }
    check(fresh(ref, 48000, 256, 0) && fresh(sb, 48000, 256, 0), tag + "activates");
    check(np_channels(ref, 0) == np_channels(sb, 0) && np_aux_channels(ref) == np_aux_channels(sb) && np_latency(ref) == np_latency(sb) && np_tail(ref) == np_tail(sb),
          tag + "channels, latency (" + std::to_string(np_latency(sb)) + ") and tail match");
    Render r0, r1;
    int rc0 = 0, rc1 = 0;
    bool ok0 = render(ref, inst, 12, 128, -1, 0, 0, r0, &rc0), ok1 = render(sb, inst, 12, 128, -1, 0, 0, r1, &rc1);
    check(ok0 && ok1 && peak(r1) > 0.0, tag + "plays (peak " + std::to_string(peak(r1)) + ")");
    {
        np_plugin* refB = open(path, id, 0);
        fresh(refB, 48000, 256, 0);
        Render rB;
        render(refB, inst, 12, 128, -1, 0, 0, rB);
        exactPlugin = same(r0, rB);
        np_close(refB);
        if (!exactPlugin) std::printf("note %s is not deterministic in process: loudness is compared instead of samples\n", tag.c_str());
    }
    check(eq(r0, r1), tag + (exactPlugin ? "null test: bit-identical to in process" : "same loudness as in process"));
    // automation
    np_reset(ref); np_reset(sb);
    fresh(ref, 48000, 256, 0); fresh(sb, 48000, 256, 0);
    Render q0, q1;
    render(ref, inst, 12, 128, autoId, autoVal, 4, q0);
    render(sb, inst, 12, 128, autoId, autoVal, 4, q1);
    check(autoId < 0 || eq(q0, q1), tag + (exactPlugin ? "automation: bit-identical to in process" : "automation: same loudness"));
    if (id == "neoplug.test.gain") { edge(ref, tag + "in process: "); edge(sb, tag); }
    // parameters set from the main thread, and the plug-in's own values
    if (autoId >= 0)
    {
        np_param_set(ref, (uint32_t)autoId, autoVal);
        np_param_set(sb, (uint32_t)autoId, autoVal);
        np_idle(sb);
        double a1 = np_param_get(ref, (uint32_t)autoId), a2 = np_param_get(sb, (uint32_t)autoId);
        check(std::fabs(a1 - a2) < 1e-9, tag + "parameter set and read back (" + std::to_string(a2) + ")");
        char t1[128] = {0}, t2[128] = {0};
        np_param_text(ref, (uint32_t)autoId, autoVal, t1, sizeof t1); np_param_text(sb, (uint32_t)autoId, autoVal, t2, sizeof t2);
        check(!std::strcmp(t1, t2), tag + std::string("parameter text \"") + t2 + "\"");
    }
    // state round trip: A's state into a fresh B; both render the same
    auto state = save(sb);
    check(!state.empty(), tag + "state saved (" + std::to_string(state.size()) + " bytes)");
    std::string why2;
    np_plugin* sb2 = open(path, id, placement, &why2);
    check(sb2 != nullptr, tag + "second instance loads " + why2);
    if (sb2)
    {
        char err[512] = {0};
        check(np_state_load(sb2, state.data(), (int)state.size(), err, sizeof err) == 0, tag + "state loads " + err);
        fresh(sb2, 48000, 256, 0);
        if (autoId >= 0) check(std::fabs(np_param_get(sb2, (uint32_t)autoId) - np_param_get(sb, (uint32_t)autoId)) < 1e-9, tag + "state restores the parameter");
        // the same state loaded into an in-process instance: the sandboxed one must render what it renders
        np_plugin* ref2 = open(path, id, 0);
        char err2[512] = {0};
        np_state_load(ref2, state.data(), (int)state.size(), err2, sizeof err2);
        fresh(ref2, 48000, 256, 0);
        Render s0, s1;
        render(ref2, inst, 6, 128, -1, 0, 0, s0);
        render(sb2, inst, 6, 128, -1, 0, 0, s1);
        check(eq(s0, s1) && peak(s1) > 0.0, tag + "state round trip renders the same as the state does in process");
        np_close(ref2);
        check(np_status(sb2) == 1 && (placement != 2 || np_server_pid(sb2) != np_server_pid(sb)), tag + "instances share a server as the placement says (pids " + std::to_string(np_server_pid(sb)) + ", " + std::to_string(np_server_pid(sb2)) + ")");
        np_close(sb2);
    }
    // offline render in big blocks
    np_deactivate(ref); np_deactivate(sb);
    np_reset(ref); np_reset(sb);
    check(fresh(ref, 44100, 1024, 1) && fresh(sb, 44100, 1024, 1), tag + "activates offline");
    Render o0, o1;
    render(ref, inst, 4, 1024, -1, 0, 0, o0);
    render(sb, inst, 4, 1024, -1, 0, 0, o1);
    check(eq(o0, o1) && peak(o1) > 0.0, tag + "offline render (1024-frame blocks) bit-identical to in process");
    // a block longer than the lane
    np_deactivate(ref); np_deactivate(sb);
    np_reset(ref); np_reset(sb);
    fresh(ref, 48000, 8192, 0); fresh(sb, 48000, 8192, 0);
    Render l0, l1;
    render(ref, inst, 2, 6000, -1, 0, 0, l0);
    render(sb, inst, 2, 6000, -1, 0, 0, l1);
    check(eq(l0, l1), tag + "a 6000-frame block is sent in pieces and comes back identical");
    np_idle(sb);
    check((np_poll(sb) & ~(NP_POLL_LATENCY | NP_POLL_PARAMS | NP_POLL_DIRTY)) == 0, tag + "idle and poll");
    int outs = np_out_count(sb);
    check(outs == np_out_count(ref) && outs >= 1, tag + "output buses (" + std::to_string(outs) + ")");
    np_close(sb);
    np_close(ref);
    return 0;
}

// --- crash ---------------------------------------------------------------------------------------------------------------

bool alive(long pid)
{
#if defined(_WIN32)
    HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, (DWORD)pid);
    if (!h) return false;
    bool a = WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
    CloseHandle(h);
    return a;
#else
    return ::kill((pid_t)pid, 0) == 0;
#endif
}

void waitGone(long pid, int ms)
{
    for (int i = 0; i < ms / 10 && alive(pid); i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
}

int processOne(np_plugin* p, int frames = 128)
{
    std::vector<float> in((size_t)frames * 2, 0.1f), out((size_t)frames * 2, 0.f);
    const float* ins[2] = {in.data(), in.data() + frames};
    float* outs[2] = {out.data(), out.data() + frames};
    np_process_io io{};
    io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = frames; io.tempo = 120; io.tsig_num = 4; io.tsig_den = 4; io.beat_pos = -1;
    return np_process_ex(p, &io);
}

int crash(const std::string& path)
{
    const std::string id = "neoplug.test.crasher";
    for (int placement : {1, 2, 3})
    {
        std::string tag = std::string("crash in ") + (placement == 1 ? "per-plug-in" : placement == 2 ? "per-instance" : "shared") + " sandbox: ";
        std::string why;
        np_plugin* a = open(path, id, placement, &why);
        np_plugin* b = open(path, id, placement, &why);
        np_plugin* other = open(path, "neoplug.test.gain", placement, &why);   // another plug-in, same placement (shared: same server)
        check(a && b && other, tag + "three instances load " + why);
        if (!a || !b || !other) return 1;
        fresh(a, 48000, 256, 0); fresh(b, 48000, 256, 0); fresh(other, 48000, 256, 0);
        check(processOne(a) >= 0 && processOne(b) >= 0 && processOne(other) >= 0, tag + "all play");
        // a state saved before: Crash is a trigger and never in it
        np_param_set(a, 1, -6.0);
        auto good = save(a);
        long pidA = np_server_pid(a);
        // the crash: the parameter reaches the plug-in at the next block, which dies
        np_param_set(a, 2, 1.0);
        auto t0 = Clock::now();
        np_event e{};
        e.type = NP_EV_PARAM; e.param_id = 2; e.value = 1.0;
        float inb[256] = {0}, outb[256] = {0};
        const float* ins[2] = {inb, inb + 128}; float* outs[2] = {outb, outb + 128};
        np_process_io io{}; io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = 128; io.events = &e; io.nevents = 1; io.tempo = 120; io.tsig_num = 4; io.tsig_den = 4; io.beat_pos = -1;
        int rc = np_process_ex(a, &io);
        double ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
        check(rc < 0 && ms < 400, tag + "the block that crashed returns an error at once (" + std::to_string(ms) + " ms)");
        for (int i = 0; i < 200 && np_status(a) != 2; i++) std::this_thread::sleep_for(std::chrono::milliseconds(5));
        char text[256] = {0};
        np_status_text(a, text, sizeof text);
        check(np_status(a) == 2, tag + "status says the server ended: " + text);
        check(std::strstr(text, "crash") != nullptr, tag + "the reason names a crash");
        waitGone(pidA, 3000);
        check(!alive(pidA), tag + "the server process is gone");
        check(processOne(a) < 0, tag + "a dead plug-in plays silence (error code) without waiting");
        if (placement == 2)
        {
            check(np_status(b) == 1 && processOne(b) >= 0 && np_status(other) == 1 && processOne(other) >= 0, tag + "the other instances play on (own server each)");
            check(np_server_pid(b) != pidA, tag + "they have servers of their own");
        }
        else
        {
            for (int i = 0; i < 200 && np_status(b) != 2; i++) std::this_thread::sleep_for(std::chrono::milliseconds(5));
            if (placement == 1) check(np_status(b) == 2 && np_status(other) == 1 && processOne(other) >= 0, tag + "the same plug-in's other instance goes with it; another plug-in's server plays on");
            else check(np_status(b) == 2 && np_status(other) == 2, tag + "everything in the shared server goes with it");
        }
        // reload from the last state
        np_close(a);
        np_plugin* a2 = open(path, id, placement, &why);
        check(a2 != nullptr, tag + "reopens " + why);
        if (a2)
        {
            char err[256] = {0};
            check(np_state_load(a2, good.data(), (int)good.size(), err, sizeof err) == 0, tag + "the last state loads");
            fresh(a2, 48000, 256, 0);
            check(std::fabs(np_param_get(a2, 1) + 6.0) < 1e-6 && np_param_get(a2, 2) == 0.0, tag + "the parameters are as saved (gain -6 dB, Crash off)");
            check(np_status(a2) == 1 && processOne(a2) >= 0, tag + "and it plays");
            np_close(a2);
        }
        np_close(b);
        np_close(other);
    }

    // a hang: the host gives up after the timeout and ends the server
    {
        std::string tag = "hang: ";
        std::string why;
        np_plugin* a = open(path, id, 2, &why);
        check(a != nullptr, tag + "loads " + why);
        if (!a) return 1;
        fresh(a, 48000, 256, 0);
        long pid = np_server_pid(a);
        np_event e{}; e.type = NP_EV_PARAM; e.param_id = 2; e.value = 2.0;
        float inb[256] = {0}, outb[256] = {0};
        const float* ins[2] = {inb, inb + 128}; float* outs[2] = {outb, outb + 128};
        np_process_io io{}; io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = 128; io.events = &e; io.nevents = 1; io.tempo = 120; io.tsig_num = 4; io.tsig_den = 4; io.beat_pos = -1;
        auto t0 = Clock::now();
        int rc = np_process_ex(a, &io);
        double ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
        const char* lim = std::getenv("NEOPLUG_PROCESS_TIMEOUT_MS");
        double limit = lim ? std::atof(lim) : 500;
        check(rc < 0 && ms >= limit * 0.8 && ms < limit + 1500, tag + "an error after the timeout (" + std::to_string(ms) + " ms, limit " + std::to_string(limit) + ")");
        char text[256] = {0};
        np_status_text(a, text, sizeof text);
        check(np_status(a) == 2 && std::strstr(text, "hung"), tag + std::string("status says it hung: ") + text);
        waitGone(pid, 3000);
        check(!alive(pid), tag + "the hung server was ended");
        np_close(a);
    }
    // a crash while saving the state
    {
        std::string tag = "crash while saving: ";
        std::string why;
        np_plugin* a = open(path, id, 2, &why);
        check(a != nullptr, tag + "loads " + why);
        if (!a) return 1;
        fresh(a, 48000, 256, 0);
        np_param_set(a, 2, 3.0);
        int n = np_state_save(a, nullptr, 0);
        check(n < 0, tag + "the save fails");
        for (int i = 0; i < 200 && np_status(a) != 2; i++) std::this_thread::sleep_for(std::chrono::milliseconds(5));
        check(np_status(a) == 2, tag + "and the plug-in shows as crashed");
        np_close(a);
    }
    // a killed server (from outside)
    {
        std::string tag = "killed from outside: ";
        std::string why;
        np_plugin* a = open(path, id, 1, &why);
        if (!a) { check(false, tag + "loads " + why); return 1; }
        fresh(a, 48000, 256, 0);
        long pid = np_server_pid(a);
#if defined(_WIN32)
        HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, (DWORD)pid);
        if (h) { TerminateProcess(h, 9); CloseHandle(h); }
#else
        ::kill((pid_t)pid, SIGKILL);
#endif
        for (int i = 0; i < 200 && np_status(a) != 2; i++) std::this_thread::sleep_for(std::chrono::milliseconds(5));
        check(np_status(a) == 2 && processOne(a) < 0, tag + "shows as crashed and plays silence");
        np_close(a);
    }
    return failures ? 1 : 0;
}

// --- latency, chain ------------------------------------------------------------------------------------------------------

struct Stats { double p50, p90, p99, p999, max, mean; };

Stats stats(std::vector<double> v)
{
    std::sort(v.begin(), v.end());
    auto at = [&](double q) { return v.empty() ? 0.0 : v[std::min(v.size() - 1, (size_t)(q * (double)v.size()))]; };
    double sum = 0; for (double x : v) sum += x;
    return {at(0.5), at(0.9), at(0.99), at(0.999), v.empty() ? 0.0 : v.back(), v.empty() ? 0.0 : sum / (double)v.size()};
}

void sleepUntil(Clock::time_point t)
{
    auto now = Clock::now();
    if (t - now > std::chrono::milliseconds(2)) std::this_thread::sleep_until(t - std::chrono::milliseconds(1));
    while (Clock::now() < t) np::ipc::cpuRelax();   // the last millisecond is spun: the OS's sleep is too coarse to pace a block
}

int latency(const std::string& path, const std::string& id, int placement, int block, int blocks)
{
    std::string why;
    np_plugin* p = open(path, id, placement, &why);
    if (!p) { check(false, "loads " + why); return 1; }
    fresh(p, 48000, block, 0);
    double period = block / 48000.0;
    np::ipc::audioThread(period);
    std::vector<float> in((size_t)block * 2, 0.1f), out((size_t)block * 2, 0.f);
    const float* ins[2] = {in.data(), in.data() + block};
    float* outs[2] = {out.data(), out.data() + block};
    np_process_io io{}; io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = block; io.tempo = 120; io.tsig_num = 4; io.tsig_den = 4; io.beat_pos = -1;
    std::vector<double> us;
    us.reserve((size_t)blocks);
    auto next = Clock::now() + std::chrono::milliseconds(50);
    int bad = 0;
    for (int i = 0; i < blocks + 200; i++)
    {
        sleepUntil(next);
        next += std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(period));
        auto t0 = Clock::now();
        int rc = np_process_ex(p, &io);
        auto t1 = Clock::now();
        if (rc < 0) bad++;
        if (i >= 200) us.push_back(std::chrono::duration<double, std::micro>(t1 - t0).count());   // (the first 200 warm up)
    }
    Stats s = stats(us);
    std::printf("{\"placement\":%d,\"block\":%d,\"blocks\":%d,\"errors\":%d,\"p50_us\":%.1f,\"p90_us\":%.1f,\"p99_us\":%.1f,\"p999_us\":%.1f,\"max_us\":%.1f,\"mean_us\":%.1f,\"deadline_us\":%.0f}\n",
                placement, block, blocks, bad, s.p50, s.p90, s.p99, s.p999, s.max, s.mean, period * 1e6);
    check(bad == 0, "no errors");
    np_close(p);
    return failures ? 1 : 0;
}

struct ChainResult { long blocks = 0, misses = 0, errors = 0; Stats chain{}; };

ChainResult runChain(const std::vector<std::pair<std::string, std::string>>& specs, int placement, int block, double seconds, bool progress, bool* allAlive)
{
    std::vector<np_plugin*> ps;
    for (auto& s : specs)
    {
        std::string why;
        np_plugin* p = open(s.first, s.second, placement, &why);
        if (!p) { check(false, "chain: " + s.first + " loads " + why); for (auto* q : ps) np_close(q); return {}; }
        fresh(p, 48000, block, 0);
        ps.push_back(p);
    }
    double period = block / 48000.0;
    np::ipc::audioThread(period);
    std::vector<float> a((size_t)block * 2, 0.f), b((size_t)block * 2, 0.f);
    rng = 99;
    for (auto& x : a) x = noise() * 0.1f;
    std::vector<double> us;
    long total = (long)(seconds / period);
    us.reserve((size_t)std::min<long>(total, 20000000));
    ChainResult res;
    auto next = Clock::now() + std::chrono::milliseconds(100);
    auto lastReport = Clock::now();
    auto dur = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(period));
    for (long i = 0; i < total + 300; i++)
    {
        sleepUntil(next);
        next += dur;
        auto t0 = Clock::now();
        for (size_t k = 0; k < ps.size(); k++)
        {
            const float* ins[2] = {a.data(), a.data() + block};
            float* outs[2] = {b.data(), b.data() + block};
            np_process_io io{}; io.in = ins; io.in_ch = 2; io.out = outs; io.out_ch = 2; io.frames = block; io.tempo = 120; io.tsig_num = 4; io.tsig_den = 4; io.beat_pos = -1;
            if (np_process_ex(ps[k], &io) < 0) res.errors++;
            std::swap(a, b);
        }
        double took = std::chrono::duration<double, std::micro>(Clock::now() - t0).count();
        if (i >= 300)
        {
            res.blocks++;
            us.push_back(took);
            if (took > period * 1e6) res.misses++;
        }
        if (progress && Clock::now() - lastReport > std::chrono::seconds(60))
        {
            lastReport = Clock::now();
            std::printf("  ... %ld blocks, %ld deadline misses, %ld errors\n", res.blocks, res.misses, res.errors);
            std::fflush(stdout);
        }
    }
    res.chain = stats(us);
    if (allAlive) { *allAlive = true; for (auto* p : ps) if (np_placement(p) != 0 && np_status(p) != 1) *allAlive = false; }
    for (auto* p : ps) np_close(p);
    return res;
}

int chain(int argc, char** argv)
{
    std::vector<std::pair<std::string, std::string>> specs;
    for (int i = 2; i < argc; i++)
    {
        if (argv[i][0] == '-') { i++; continue; }
        std::string s = argv[i];
        size_t h = s.find('#');
        specs.push_back({s.substr(0, h), h == std::string::npos ? "" : s.substr(h + 1)});
    }
    int placement = std::atoi(opt(argc, argv, "--placement").c_str());
    int block = opt(argc, argv, "--block").empty() ? 128 : std::atoi(opt(argc, argv, "--block").c_str());
    double seconds = opt(argc, argv, "--seconds").empty() ? 60 : std::atof(opt(argc, argv, "--seconds").c_str());
    double baseline = opt(argc, argv, "--baseline").empty() ? 60 : std::atof(opt(argc, argv, "--baseline").c_str());
    std::printf("baseline: %zu plug-ins in process, %d frames, %.0f s\n", specs.size(), block, baseline);
    std::fflush(stdout);
    ChainResult base = runChain(specs, 0, block, baseline, true, nullptr);
    std::printf("sandbox (placement %d): %zu plug-ins, %d frames, %.0f s\n", placement, specs.size(), block, seconds);
    std::fflush(stdout);
    bool alive = false;
    ChainResult sb = runChain(specs, placement, block, seconds, true, &alive);
    double deadline = block / 48000.0 * 1e6;
    auto json = [&](const char* name, const ChainResult& r) {
        std::printf("{\"%s\":{\"blocks\":%ld,\"deadline_misses\":%ld,\"errors\":%ld,\"miss_per_million\":%.1f,\"chain_p50_us\":%.1f,\"chain_p99_us\":%.1f,\"chain_p999_us\":%.1f,\"chain_max_us\":%.1f,\"deadline_us\":%.0f}}\n",
                    name, r.blocks, r.misses, r.errors, r.blocks ? 1e6 * (double)r.misses / (double)r.blocks : 0.0, r.chain.p50, r.chain.p99, r.chain.p999, r.chain.max, deadline);
    };
    json("baseline_in_process", base);
    json("sandboxed", sb);
    check(sb.blocks > 0, "the chain ran");
    check(sb.errors == 0 && alive, "no block failed and every server was still running at the end");
    // Misses are counted against the in-process baseline on the same runner: the sandbox may not add more than a small allowance.
    // Local and dedicated runs keep the strict allowance (0.1 point). A shared CI runner (GitHub Actions, the CI variable) is a VM whose scheduler
    // steals a few blocks now and then, so it reports the rate and fails only above CI_ALLOWANCE (NEOPLUG_CI_MISS_ALLOWANCE, in points), which a
    // real regression reaches and a flake does not: over the last 24 green runs of Linux and of Windows the sandbox added at most 0.053 points (4 blocks
    // of 7500); the two flakes seen were 0.79 points (Windows, VM1 and NT2 package runs); a sandbox that costs a block its deadline reaches whole points.
    // NEOPLUG_STRICT_RT=1 asserts the strict allowance anywhere (a dedicated runner, a real Mac); NEOPLUG_STRICT_RT=0 never does.
    // (plugin-host.md, H2 checks)
    const double CI_ALLOWANCE = 2.0;
    const char* strictEnv = std::getenv("NEOPLUG_STRICT_RT");
    const char* ciEnv = std::getenv("GITHUB_ACTIONS");
    const char* ciEnv2 = std::getenv("CI");
    bool onCi = (ciEnv && *ciEnv) || (ciEnv2 && *ciEnv2);
    std::string strictValue = strictEnv ? strictEnv : "";
    bool strict = strictValue == "1" || (strictValue != "0" && !onCi);
    double ciAllowance = CI_ALLOWANCE;
    if (const char* a = std::getenv("NEOPLUG_CI_MISS_ALLOWANCE")) { double v = std::atof(a); if (v > 0) ciAllowance = v; }
    double baseRate = base.blocks ? (double)base.misses / (double)base.blocks : 0.0;
    double sbRate = sb.blocks ? (double)sb.misses / (double)sb.blocks : 0.0;
    double over = (sbRate - baseRate) * 100;   // points of blocks the sandbox adds to the baseline
    std::string rates = "deadline misses: sandbox " + std::to_string(sbRate * 100) + " % of blocks (" + std::to_string(sb.misses) + " of " + std::to_string(sb.blocks) + "), in-process baseline " +
                        std::to_string(baseRate * 100) + " %";
#ifdef __APPLE__
    // macOS CI runs on a VM whose wake-up p99 is 3-4x Linux's (plugin-host.md §8.4): the miss rate there is reported, not asserted.
    // The gate for macOS is the hour test on a real Mac (a hand test); NEOPLUG_STRICT_RT=1 asserts it there.
    if (strictValue == "1") check(sbRate <= baseRate + 0.001, rates + " (allowance 0.1 point)");
    else std::printf("note %s, %.3f points over%s\n", rates.c_str(), over, over <= 0.1 ? "" : " -- over the strict allowance (0.1 point); reported only on the macOS VM");
#else
    if (strict) check(sbRate <= baseRate + 0.001, rates + " (allowance 0.1 point, strict)");
    else
    {
        check(over <= ciAllowance, rates + " (CI allowance " + std::to_string(ciAllowance) + " points)");
        if (over > 0.1) std::printf("note the sandbox added %.3f points of deadline misses: over the strict allowance (0.1 point), under the CI allowance; a shared runner's scheduler (plugin-host.md, H2 checks)\n", over);
    }
#endif
    return failures ? 1 : 0;
}

} // namespace

int main(int argc, char** argv)
{
#if defined(_WIN32)
    timeBeginPeriod(1);
#endif
    if (argc < 3)
    {
        std::fprintf(stderr, "usage: neoplug-sandbox-test smoke|crash|latency|chain <plugin> [--id ID] [--placement N] ...\n");
        return 2;
    }
    std::string cmd = argv[1], path = argv[2], id = opt(argc, argv, "--id");
    int placement = opt(argc, argv, "--placement").empty() ? 1 : std::atoi(opt(argc, argv, "--placement").c_str());
    if (!np_server_available()) { std::fprintf(stderr, "neoplug-server is not installed beside the library\n"); return 2; }
    if (cmd == "smoke")
    {
        if (opt(argc, argv, "--placement").empty()) { for (int pl : {1, 2, 3}) smoke(path, id, pl); }
        else smoke(path, id, placement);
        return failures ? 1 : 0;
    }
    if (cmd == "crash") return crash(path);
    if (cmd == "latency")
    {
        int block = opt(argc, argv, "--block").empty() ? 128 : std::atoi(opt(argc, argv, "--block").c_str());
        int blocks = opt(argc, argv, "--blocks").empty() ? 20000 : std::atoi(opt(argc, argv, "--blocks").c_str());
        return latency(path, id, placement, block, blocks);
    }
    if (cmd == "chain") return chain(argc, argv);
    std::fprintf(stderr, "unknown command %s\n", cmd.c_str());
    return 2;
}
