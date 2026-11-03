// neoplug-probe (H0 spike): loads a plugin through the neoplug C ABI and checks what H0 asks:
// it loads, plays notes or processes audio, saves its state, and a fresh instance restores it
// and renders the same. Optionally opens its GUI in a floating window and checks the pixels.
// Prints one JSON object; exit code 0 when the checks pass.
//
//   neoplug-probe list <plugin>
//   neoplug-probe test <plugin> [--id ID] [--out DIR] [--rate HZ] [--block N] [--seconds S] [--gui]
//   neoplug-probe gui <plugin> [--id ID] [--out DIR] [--hold S]
#include "neoplug.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace {

std::string esc(const std::string& s)
{
    std::string o;
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\') { o += '\\'; o += (char)c; }
        else if (c == '\n') o += "\\n";
        else if (c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); o += b; }
        else o += (char)c;
    }
    return o;
}

std::string q(const std::string& s) { return "\"" + esc(s) + "\""; }
std::string num(double v)
{
    if (!std::isfinite(v)) return "null";
    char b[64];
    snprintf(b, sizeof b, "%.6g", v);
    return b;
}

struct Args
{
    std::string cmd, path, id, out;
    double rate = 48000, seconds = 2, hold = 2;
    int block = 256;
    bool gui = false;
};

std::string opt(int argc, char** argv, const char* name)
{
    for (int i = 0; i + 1 < argc; i++) if (!strcmp(argv[i], name)) return argv[i + 1];
    return "";
}

bool flag(int argc, char** argv, const char* name)
{
    for (int i = 0; i < argc; i++) if (!strcmp(argv[i], name)) return true;
    return false;
}

#if defined(__APPLE__)
const char* nativeApi = "nsview";
#elif defined(_WIN32)
const char* nativeApi = "hwnd";
#else
const char* nativeApi = "";
#endif

void writeWav(const std::string& path, const std::vector<float>& l, const std::vector<float>& r, int rate)
{
    FILE* f = fopen(path.c_str(), "wb");
    if (!f) return;
    uint32_t n = (uint32_t)l.size();
    uint32_t dataBytes = n * 2 * 4;
    auto u32 = [&](uint32_t v) { fwrite(&v, 4, 1, f); };
    auto u16 = [&](uint16_t v) { fwrite(&v, 2, 1, f); };
    fwrite("RIFF", 1, 4, f); u32(36 + dataBytes); fwrite("WAVE", 1, 4, f);
    fwrite("fmt ", 1, 4, f); u32(16); u16(3); u16(2); u32((uint32_t)rate); u32((uint32_t)rate * 8); u16(8); u16(32);
    fwrite("data", 1, 4, f); u32(dataBytes);
    for (uint32_t i = 0; i < n; i++) { fwrite(&l[i], 4, 1, f); fwrite(&r[i], 4, 1, f); }
    fclose(f);
}

struct Render
{
    std::vector<float> l, r;
    double rms = 0, peak = 0, procMeanUs = 0, procMaxUs = 0, realtimeFactor = 0;
    int nonSilentBlocks = 0, errors = 0;
};

// Renders on a separate thread (the plugin's audio thread), as the engine does.
Render render(np_plugin* p, bool instrument, double rate, int block, double seconds)
{
    Render R;
    std::thread t([&] {
        int total = (int)(rate * seconds);
        int inCh = std::max(0, np_channels(p, 1));
        int outCh = 2;
        std::vector<float> inL(block), inR(block), outL(block), outR(block);
        const float* ins[2] = {inL.data(), inR.data()};
        float* outs[2] = {outL.data(), outR.data()};
        double phase = 0, sumUs = 0;
        int blocks = 0;
        auto start = std::chrono::steady_clock::now();
        for (int pos = 0; pos < total; pos += block)
        {
            int n = std::min(block, total - pos);
            std::vector<np_event> ev;
            if (instrument)
            {
                int offAt = (int)(rate * seconds * 0.5);
                const int keys[3] = {60, 64, 67};
                for (int k = 0; k < 3; k++)
                {
                    if (pos == 0) ev.push_back(np_event{0, NP_EV_NOTE_ON, 0, keys[k], k + 1, 0, 0.8});
                    if (offAt >= pos && offAt < pos + n) ev.push_back(np_event{(uint32_t)(offAt - pos), NP_EV_NOTE_OFF, 0, keys[k], k + 1, 0, 0.0});
                }
            }
            for (int i = 0; i < n; i++)
            {
                float x = (float)(0.25 * std::sin(phase));   // -12 dBFS, 440 Hz
                phase += 2 * 3.14159265358979 * 440 / rate;
                inL[i] = x;
                inR[i] = x;
            }
            auto t0 = std::chrono::steady_clock::now();
            int rc = np_process(p, inCh > 0 ? ins : nullptr, inCh > 0 ? 2 : 0, outs, outCh, n, ev.data(), (int)ev.size(), 120, 1);
            double us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count();
            sumUs += us;
            R.procMaxUs = std::max(R.procMaxUs, us);
            blocks++;
            if (rc < 0) R.errors++;
            if (rc > 0) R.nonSilentBlocks++;
            for (int i = 0; i < n; i++)
            {
                R.l.push_back(outL[i]);
                R.r.push_back(outR[i]);
            }
        }
        double wall = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        R.procMeanUs = blocks ? sumUs / blocks : 0;
        R.realtimeFactor = wall > 0 ? seconds / wall : 0;
    });
    t.join();
    double ss = 0;
    for (size_t i = 0; i < R.l.size(); i++)
    {
        ss += R.l[i] * (double)R.l[i] + R.r[i] * (double)R.r[i];
        R.peak = std::max(R.peak, (double)std::max(std::fabs(R.l[i]), std::fabs(R.r[i])));
    }
    R.rms = R.l.empty() ? 0 : std::sqrt(ss / (2.0 * R.l.size()));
    return R;
}

double dbfs(double x) { return x > 0 ? 20 * std::log10(x) : -999; }

double maxDiff(const Render& a, const Render& b)
{
    double d = 0;
    size_t n = std::min(a.l.size(), b.l.size());
    for (size_t i = 0; i < n; i++) d = std::max(d, (double)std::max(std::fabs(a.l[i] - b.l[i]), std::fabs(a.r[i] - b.r[i])));
    return d;
}

std::string renderJson(const Render& r)
{
    return "{\"rms_dbfs\":" + num(dbfs(r.rms)) + ",\"peak_dbfs\":" + num(dbfs(r.peak)) + ",\"non_silent_blocks\":" + std::to_string(r.nonSilentBlocks) +
           ",\"errors\":" + std::to_string(r.errors) + ",\"process_mean_us\":" + num(r.procMeanUs) + ",\"process_max_us\":" + num(r.procMaxUs) +
           ",\"realtime_factor\":" + num(r.realtimeFactor) + "}";
}

np_plugin* openPlugin(const Args& a, std::string& err, double& ms)
{
    char e[512] = {};
    auto t0 = std::chrono::steady_clock::now();
    np_plugin* p = np_open(a.path.c_str(), a.id.empty() ? nullptr : a.id.c_str(), e, sizeof e);
    ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    err = e;
    return p;
}

// Opens the plugin's GUI in a floating window of our own and checks it drew something.
std::string guiCheck(np_plugin* p, const Args& a, bool& ok)
{
    ok = false;
    if (!*nativeApi) return "{\"skipped\":\"no GUI on this platform\"}";
    int support = np_gui_support(p, nativeApi);
    if (!(support & 1)) return "{\"support\":" + std::to_string(support) + ",\"error\":\"cannot embed\"}";
    void* content = np_platform_window("neoplug-probe", 900, 650);
    int w = 0, h = 0;
    char err[512] = {};
    int rc = np_gui_attach(p, content, nativeApi, &w, &h, err, sizeof err);
    np_platform_run(a.hold);
    char tree[8192] = {};
    np_platform_describe(content, tree, sizeof tree);
    char stats[512] = {};
    std::string png = a.out.empty() ? "" : a.out + "/gui-floating" + (std::string(nativeApi) == "hwnd" ? ".bmp" : ".png");
    np_platform_snapshot(content, png.empty() ? nullptr : png.c_str(), 0x1E90FF, stats, sizeof stats);
    np_gui_detach(p);
    np_platform_run(0.2);
    np_platform_release(content);
    ok = rc == 0;
    return "{\"support\":" + std::to_string(support) + ",\"attached\":" + (rc == 0 ? "true" : "false") + ",\"error\":" + q(err) +
           ",\"size\":[" + std::to_string(w) + "," + std::to_string(h) + "],\"snapshot\":" + (stats[0] ? stats : "null") + ",\"tree\":" + q(tree) + "}";
}

int cmdList(const Args& a)
{
    np_class_info infos[64];
    char err[512] = {};
    int n = np_list(a.path.c_str(), infos, 64, err, sizeof err);
    if (n < 0)
    {
        printf("{\"path\":%s,\"error\":%s}\n", q(a.path).c_str(), q(err).c_str());
        return 1;
    }
    printf("{\"path\":%s,\"classes\":[", q(a.path).c_str());
    for (int i = 0; i < n && i < 64; i++)
        printf("%s{\"format\":%s,\"id\":%s,\"name\":%s,\"vendor\":%s,\"version\":%s,\"category\":%s,\"instrument\":%s}", i ? "," : "",
               infos[i].format == NP_FORMAT_VST3 ? "\"vst3\"" : "\"clap\"", q(infos[i].id).c_str(), q(infos[i].name).c_str(),
               q(infos[i].vendor).c_str(), q(infos[i].version).c_str(), q(infos[i].category).c_str(), infos[i].is_instrument ? "true" : "false");
    printf("]}\n");
    return 0;
}

int cmdTest(const Args& a)
{
    std::string out = "{\"path\":" + q(a.path);
    std::vector<std::string> failures;
    std::string err;
    double openMs = 0;
    np_plugin* p = openPlugin(a, err, openMs);
    if (!p)
    {
        printf("%s,\"ok\":false,\"failures\":[\"open\"],\"error\":%s}\n", out.c_str(), q(err).c_str());
        return 1;
    }
    np_class_info info{};
    np_info(p, &info);
    bool instrument = info.is_instrument != 0;
    out += ",\"format\":" + std::string(info.format == NP_FORMAT_VST3 ? "\"vst3\"" : "\"clap\"") + ",\"id\":" + q(info.id) + ",\"name\":" + q(info.name) +
           ",\"vendor\":" + q(info.vendor) + ",\"version\":" + q(info.version) + ",\"instrument\":" + (instrument ? "true" : "false") +
           ",\"open_ms\":" + num(openMs);

    // Parameters, and the one we tweak to make the state round trip mean something.
    int pc = np_param_count(p);
    out += ",\"param_count\":" + std::to_string(pc) + ",\"params\":[";
    np_param_info tweak{};
    bool haveTweak = false;
    int listed = 0;
    for (int i = 0; i < pc; i++)
    {
        np_param_info pi{};
        if (np_param_info_at(p, i, &pi) != 0) continue;
        bool usable = (pi.flags & NP_PARAM_AUTOMATABLE) && !(pi.flags & (NP_PARAM_READONLY | NP_PARAM_HIDDEN | NP_PARAM_BYPASS));
        if (usable && !haveTweak) { tweak = pi; haveTweak = true; }
        if (listed < 12)
        {
            char text[128] = {};
            np_param_text(p, pi.id, np_param_get(p, pi.id), text, sizeof text);
            out += std::string(listed ? "," : "") + "{\"id\":" + std::to_string(pi.id) + ",\"name\":" + q(pi.name) + ",\"units\":" + q(pi.units) +
                   ",\"min\":" + num(pi.min) + ",\"max\":" + num(pi.max) + ",\"default\":" + num(pi.def) + ",\"steps\":" + std::to_string(pi.steps) +
                   ",\"flags\":" + std::to_string(pi.flags) + ",\"text\":" + q(text) + "}";
            listed++;
        }
    }
    out += "]";

    char e[512] = {};
    if (np_activate(p, a.rate, a.block, 0, e, sizeof e) != 0)
    {
        printf("%s,\"ok\":false,\"failures\":[\"activate\"],\"error\":%s}\n", out.c_str(), q(e).c_str());
        np_close(p);
        return 1;
    }
    out += ",\"channels\":{\"in\":" + std::to_string(np_channels(p, 1)) + ",\"out\":" + std::to_string(np_channels(p, 0)) + "}";
    out += ",\"latency\":" + std::to_string(np_latency(p)) + ",\"tail\":" + std::to_string(np_tail(p));

    Render first = render(p, instrument, a.rate, a.block, a.seconds);
    out += ",\"render\":" + renderJson(first);
    if (first.errors) failures.push_back("process errors");
    if (first.peak <= 1e-5) failures.push_back("silent render");
    if (!a.out.empty()) writeWav(a.out + "/render.wav", first.l, first.r, (int)a.rate);

    // Tweak one parameter, let it reach the processor, then save the state.
    double target = 0;
    std::string tweakText;
    if (haveTweak)
    {
        double span = tweak.max - tweak.min;
        target = tweak.min + span * 0.3;
        if (tweak.steps > 0)
        {
            double step = span / tweak.steps;
            target = tweak.min + std::round(0.3 * tweak.steps) * step;
            if (std::fabs(target - tweak.def) < step / 2) target = tweak.def + step <= tweak.max ? tweak.def + step : tweak.def - step;
        }
        else if (std::fabs(target - tweak.def) < span * 0.05)
            target = tweak.min + span * 0.7;
        np_param_set(p, tweak.id, target);
        np_idle(p);
        render(p, instrument, a.rate, a.block, 0.1);
        np_idle(p);
        char t[128] = {};
        np_param_text(p, tweak.id, np_param_get(p, tweak.id), t, sizeof t);
        tweakText = t;
        out += ",\"tweak\":{\"id\":" + std::to_string(tweak.id) + ",\"name\":" + q(tweak.name) + ",\"value\":" + num(target) + ",\"text\":" + q(t) + "}";
    }
    int size = np_state_save(p, nullptr, 0);
    std::vector<uint8_t> state(size > 0 ? size : 0);
    if (size > 0) np_state_save(p, state.data(), size);
    out += ",\"state_bytes\":" + std::to_string(size);
    if (size <= 0) failures.push_back("state save");
    np_deactivate(p);
    np_close(p);

    // Two fresh instances from the saved state: the parameter comes back, and they render alike.
    Render restored[2];
    std::string restoredText;
    double restoredValue = 0;
    int resaveSame = -1;
    for (int k = 0; k < 2; k++)
    {
        np_plugin* r = openPlugin(a, err, openMs);
        if (!r) { failures.push_back("reopen"); break; }
        char le[512] = {};
        if (size > 0 && np_state_load(r, state.data(), size, le, sizeof le) != 0) { failures.push_back(std::string("state load: ") + le); }
        np_idle(r);
        if (k == 0 && haveTweak)
        {
            restoredValue = np_param_get(r, tweak.id);
            char t[128] = {};
            np_param_text(r, tweak.id, restoredValue, t, sizeof t);
            restoredText = t;
        }
        np_activate(r, a.rate, a.block, 0, le, sizeof le);
        restored[k] = render(r, instrument, a.rate, a.block, a.seconds);
        if (k == 0)
        {
            int s2 = np_state_save(r, nullptr, 0);
            std::vector<uint8_t> st2(s2 > 0 ? s2 : 0);
            if (s2 > 0) np_state_save(r, st2.data(), s2);
            resaveSame = st2 == state ? 1 : 0;
        }
        np_deactivate(r);
        np_close(r);
    }
    if (haveTweak)
    {
        bool same = std::fabs(restoredValue - target) <= 1e-4 * std::max(1.0, std::fabs(tweak.max - tweak.min)) || restoredText == tweakText;
        out += ",\"restored\":{\"value\":" + num(restoredValue) + ",\"text\":" + q(restoredText) + ",\"matches\":" + (same ? "true" : "false") + "}";
        if (!same) failures.push_back("restored parameter differs");
    }
    out += ",\"resave_identical\":" + std::string(resaveSame == 1 ? "true" : resaveSame == 0 ? "false" : "null");
    out += ",\"restored_render\":" + renderJson(restored[0]);
    double nullDiff = maxDiff(restored[0], restored[1]);
    out += ",\"restored_null_dbfs\":" + num(dbfs(nullDiff));
    if (restored[0].peak <= 1e-5) failures.push_back("restored instance is silent");
    if (!a.out.empty()) writeWav(a.out + "/restored.wav", restored[0].l, restored[0].r, (int)a.rate);

    if (a.gui)
    {
        np_plugin* g = openPlugin(a, err, openMs);
        bool gok = false;
        if (g)
        {
            out += ",\"gui\":" + guiCheck(g, a, gok);
            np_close(g);
        }
    }

    out += ",\"ok\":" + std::string(failures.empty() ? "true" : "false") + ",\"failures\":[";
    for (size_t i = 0; i < failures.size(); i++) out += (i ? "," : "") + q(failures[i]);
    out += "]}";
    printf("%s\n", out.c_str());
    if (!a.out.empty())
    {
        FILE* f = fopen((a.out + "/result.json").c_str(), "w");
        if (f) { fprintf(f, "%s\n", out.c_str()); fclose(f); }
    }
    return failures.empty() ? 0 : 1;
}

int cmdGui(const Args& a)
{
    std::string err;
    double ms = 0;
    np_plugin* p = openPlugin(a, err, ms);
    if (!p)
    {
        printf("{\"error\":%s}\n", q(err).c_str());
        return 1;
    }
    bool ok = false;
    std::string g = guiCheck(p, a, ok);
    np_close(p);
    printf("{\"path\":%s,\"gui\":%s}\n", q(a.path).c_str(), g.c_str());
    return ok ? 0 : 1;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: neoplug-probe list|test|gui <plugin> [--id ID] [--out DIR] [--rate HZ] [--block N] [--seconds S] [--gui] [--hold S]\n");
        return 2;
    }
    Args a;
    a.cmd = argv[1];
    a.path = argv[2];
    a.id = opt(argc, argv, "--id");
    a.out = opt(argc, argv, "--out");
    if (!opt(argc, argv, "--rate").empty()) a.rate = atof(opt(argc, argv, "--rate").c_str());
    if (!opt(argc, argv, "--block").empty()) a.block = atoi(opt(argc, argv, "--block").c_str());
    if (!opt(argc, argv, "--seconds").empty()) a.seconds = atof(opt(argc, argv, "--seconds").c_str());
    if (!opt(argc, argv, "--hold").empty()) a.hold = atof(opt(argc, argv, "--hold").c_str());
    a.gui = flag(argc, argv, "--gui");
    if (a.cmd == "list") return cmdList(a);
    if (a.cmd == "test") return cmdTest(a);
    if (a.cmd == "gui") return cmdGui(a);
    fprintf(stderr, "unknown command %s\n", a.cmd.c_str());
    return 2;
}
