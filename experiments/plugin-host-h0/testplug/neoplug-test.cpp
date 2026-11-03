// neoplug-test.clap (H0 spike): a minimal CLAP module with two plugins, written for the host's tests.
//  - neoplug.test.synth: 8-voice sine/saw/square synth; params Gain (1), Wave (2, stepped), Detune (3).
//  - neoplug.test.gain:  stereo gain effect; param Gain dB (1); reports 64 samples of latency.
// Both save and load their parameters as state. The GUI (macOS NSView, Windows HWND) is a solid
// colour panel the GUI spike looks for in screenshots. MIT.
#include <clap/clap.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

extern "C" {
// testgui_*.mm / .cpp: a solid panel of the given colour inside the parent, or a no-op.
void* testgui_create(void* parent, int w, int h, int rgb);
void testgui_destroy(void* view);
int testgui_supported(void);
}

namespace {

constexpr double Pi = 3.14159265358979323846;

struct ParamDesc { clap_id id; const char* name; const char* module; double min, max, def; bool stepped; };

struct TestPlugin
{
    clap_plugin_t plugin{};
    const clap_host_t* host = nullptr;
    bool synth = true;
    std::vector<ParamDesc> descs;
    std::vector<double> values;       // main-thread view
    std::vector<double> audioValues;  // audio-thread view
    std::atomic<bool> dirtyToAudio{false};
    double rate = 48000;
    void* view = nullptr;
    int guiW = 360, guiH = 120;

    struct Voice { int key = -1; int32_t noteId = -1; double phase = 0, freq = 0, amp = 0, env = 0; bool releasing = false; };
    Voice voices[8];
    // the gain effect's latency line
    std::vector<float> delayL, delayR;
    size_t delayPos = 0;

    int indexOf(clap_id id) const
    {
        for (size_t i = 0; i < descs.size(); i++) if (descs[i].id == id) return (int)i;
        return -1;
    }
};

TestPlugin* self(const clap_plugin_t* p) { return (TestPlugin*)p->plugin_data; }

// --- params ------------------------------------------------------------------------------------

uint32_t paramsCount(const clap_plugin_t* p) { return (uint32_t)self(p)->descs.size(); }

bool paramsInfo(const clap_plugin_t* p, uint32_t index, clap_param_info_t* info)
{
    auto* s = self(p);
    if (index >= s->descs.size()) return false;
    const auto& d = s->descs[index];
    *info = clap_param_info_t{};
    info->id = d.id;
    info->flags = CLAP_PARAM_IS_AUTOMATABLE | (d.stepped ? CLAP_PARAM_IS_STEPPED : 0);
    snprintf(info->name, sizeof info->name, "%s", d.name);
    snprintf(info->module, sizeof info->module, "%s", d.module);
    info->min_value = d.min;
    info->max_value = d.max;
    info->default_value = d.def;
    return true;
}

bool paramsValue(const clap_plugin_t* p, clap_id id, double* out)
{
    auto* s = self(p);
    int i = s->indexOf(id);
    if (i < 0) return false;
    *out = s->values[i];
    return true;
}

bool paramsToText(const clap_plugin_t* p, clap_id id, double v, char* buf, uint32_t size)
{
    auto* s = self(p);
    int i = s->indexOf(id);
    if (i < 0) return false;
    if (s->synth && id == 2)
    {
        static const char* names[] = {"Sine", "Saw", "Square"};
        snprintf(buf, size, "%s", names[std::clamp((int)std::lround(v), 0, 2)]);
    }
    else if (s->synth && id == 3) snprintf(buf, size, "%.1f ct", v);
    else if (!s->synth) snprintf(buf, size, "%.2f dB", v);
    else snprintf(buf, size, "%.0f %%", v * 100);
    return true;
}

bool paramsFromText(const clap_plugin_t* p, clap_id id, const char* text, double* out)
{
    auto* s = self(p);
    if (s->indexOf(id) < 0) return false;
    if (s->synth && id == 2)
    {
        if (!strncmp(text, "Sine", 4)) { *out = 0; return true; }
        if (!strncmp(text, "Saw", 3)) { *out = 1; return true; }
        if (!strncmp(text, "Square", 6)) { *out = 2; return true; }
        return false;
    }
    double v = atof(text);
    *out = (s->synth && id == 1) ? v / 100.0 : v;
    return true;
}

void applyParamEvents(TestPlugin* s, const clap_input_events_t* in, uint32_t from, uint32_t to, uint32_t& next);

void paramsFlush(const clap_plugin_t* p, const clap_input_events_t* in, const clap_output_events_t*)
{
    auto* s = self(p);
    uint32_t n = in->size(in);
    for (uint32_t i = 0; i < n; i++)
    {
        auto* h = in->get(in, i);
        if (h->space_id == CLAP_CORE_EVENT_SPACE_ID && h->type == CLAP_EVENT_PARAM_VALUE)
        {
            auto* e = (const clap_event_param_value_t*)h;
            int k = s->indexOf(e->param_id);
            if (k >= 0) { s->values[k] = e->value; s->audioValues[k] = e->value; }
        }
    }
}

const clap_plugin_params_t paramsExt{paramsCount, paramsInfo, paramsValue, paramsToText, paramsFromText, paramsFlush};

// --- state -------------------------------------------------------------------------------------

bool stateSave(const clap_plugin_t* p, const clap_ostream_t* os)
{
    auto* s = self(p);
    const char magic[8] = {'N', 'P', 'T', 'E', 'S', 'T', '0', '1'};
    if (os->write(os, magic, 8) != 8) return false;
    uint32_t n = (uint32_t)s->values.size();
    if (os->write(os, &n, 4) != 4) return false;
    for (double v : s->values) if (os->write(os, &v, 8) != 8) return false;
    return true;
}

bool stateLoad(const clap_plugin_t* p, const clap_istream_t* is)
{
    auto* s = self(p);
    char magic[8];
    if (is->read(is, magic, 8) != 8 || memcmp(magic, "NPTEST01", 8) != 0) return false;
    uint32_t n = 0;
    if (is->read(is, &n, 4) != 4 || n != s->values.size()) return false;
    for (uint32_t i = 0; i < n; i++)
    {
        double v;
        if (is->read(is, &v, 8) != 8) return false;
        s->values[i] = v;
    }
    s->dirtyToAudio = true;
    return true;
}

const clap_plugin_state_t stateExt{stateSave, stateLoad};

// --- ports -------------------------------------------------------------------------------------

uint32_t audioCount(const clap_plugin_t* p, bool isInput) { return (isInput && self(p)->synth) ? 0 : 1; }

bool audioGet(const clap_plugin_t* p, uint32_t index, bool isInput, clap_audio_port_info_t* info)
{
    if (index != 0 || (isInput && self(p)->synth)) return false;
    *info = clap_audio_port_info_t{};
    info->id = isInput ? 0 : 1;
    snprintf(info->name, sizeof info->name, "%s", isInput ? "In" : "Out");
    info->flags = CLAP_AUDIO_PORT_IS_MAIN;
    info->channel_count = 2;
    info->port_type = CLAP_PORT_STEREO;
    info->in_place_pair = CLAP_INVALID_ID;
    return true;
}

const clap_plugin_audio_ports_t audioExt{audioCount, audioGet};

uint32_t noteCount(const clap_plugin_t* p, bool isInput) { return (isInput && self(p)->synth) ? 1 : 0; }

bool noteGet(const clap_plugin_t* p, uint32_t index, bool isInput, clap_note_port_info_t* info)
{
    if (index != 0 || !isInput || !self(p)->synth) return false;
    *info = clap_note_port_info_t{};
    info->id = 0;
    info->supported_dialects = CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI;
    info->preferred_dialect = CLAP_NOTE_DIALECT_CLAP;
    snprintf(info->name, sizeof info->name, "Notes");
    return true;
}

const clap_plugin_note_ports_t noteExt{noteCount, noteGet};

uint32_t latencyGet(const clap_plugin_t* p) { return self(p)->synth ? 0 : 64; }
const clap_plugin_latency_t latencyExt{latencyGet};

// --- gui ---------------------------------------------------------------------------------------

#if defined(__APPLE__)
const char* const nativeApi = CLAP_WINDOW_API_COCOA;
#elif defined(_WIN32)
const char* const nativeApi = CLAP_WINDOW_API_WIN32;
#else
const char* const nativeApi = "none";
#endif

bool guiApiSupported(const clap_plugin_t*, const char* api, bool floating)
{
    return testgui_supported() && !floating && api && !strcmp(api, nativeApi);
}
bool guiPreferred(const clap_plugin_t*, const char** api, bool* floating)
{
    *api = nativeApi;
    *floating = false;
    return testgui_supported() != 0;
}
bool guiCreate(const clap_plugin_t* p, const char* api, bool floating) { return guiApiSupported(p, api, floating); }
void guiDestroy(const clap_plugin_t* p)
{
    auto* s = self(p);
    if (s->view) testgui_destroy(s->view);
    s->view = nullptr;
}
bool guiScale(const clap_plugin_t*, double) { return true; }
bool guiSize(const clap_plugin_t* p, uint32_t* w, uint32_t* h)
{
    *w = (uint32_t)self(p)->guiW;
    *h = (uint32_t)self(p)->guiH;
    return true;
}
bool guiCanResize(const clap_plugin_t*) { return false; }
bool guiHints(const clap_plugin_t*, clap_gui_resize_hints_t*) { return false; }
bool guiAdjust(const clap_plugin_t*, uint32_t*, uint32_t*) { return false; }
bool guiSetSize(const clap_plugin_t*, uint32_t, uint32_t) { return false; }
bool guiSetParent(const clap_plugin_t* p, const clap_window_t* w)
{
    auto* s = self(p);
    // Synth: dodger blue (30,144,255); gain: orange (255,140,0). The spike looks for these.
    s->view = testgui_create(w->ptr, s->guiW, s->guiH, s->synth ? 0x1E90FF : 0xFF8C00);
    return s->view != nullptr;
}
bool guiTransient(const clap_plugin_t*, const clap_window_t*) { return false; }
void guiTitle(const clap_plugin_t*, const char*) {}
bool guiShow(const clap_plugin_t*) { return true; }
bool guiHide(const clap_plugin_t*) { return true; }

const clap_plugin_gui_t guiExt{guiApiSupported, guiPreferred, guiCreate, guiDestroy, guiScale, guiSize, guiCanResize,
                               guiHints, guiAdjust, guiSetSize, guiSetParent, guiTransient, guiTitle, guiShow, guiHide};

// --- plugin ------------------------------------------------------------------------------------

bool init(const clap_plugin_t*) { return true; }
void destroy(const clap_plugin_t* p)
{
    guiDestroy(p);
    delete self(p);
}
bool activate(const clap_plugin_t* p, double rate, uint32_t, uint32_t)
{
    auto* s = self(p);
    s->rate = rate;
    s->audioValues = s->values;
    s->delayL.assign(64, 0.f);
    s->delayR.assign(64, 0.f);
    s->delayPos = 0;
    return true;
}
void deactivate(const clap_plugin_t*) {}
bool startProcessing(const clap_plugin_t*) { return true; }
void stopProcessing(const clap_plugin_t*) {}
void reset(const clap_plugin_t* p)
{
    for (auto& v : self(p)->voices) v = TestPlugin::Voice{};
}

void noteOn(TestPlugin* s, int key, int32_t id, double vel)
{
    TestPlugin::Voice* v = nullptr;
    for (auto& x : s->voices) if (x.key < 0) { v = &x; break; }
    if (!v) v = &s->voices[0];
    v->key = key;
    v->noteId = id;
    v->phase = 0;
    v->freq = 440.0 * std::pow(2.0, (key - 69) / 12.0);
    v->amp = vel;
    v->env = 0;
    v->releasing = false;
}

void noteOff(TestPlugin* s, int key, int32_t id)
{
    for (auto& v : s->voices)
        if (v.key >= 0 && !v.releasing && (id >= 0 ? v.noteId == id : v.key == key)) v.releasing = true;
}

void handleEvent(TestPlugin* s, const clap_event_header_t* h)
{
    if (h->space_id != CLAP_CORE_EVENT_SPACE_ID) return;
    switch (h->type)
    {
    case CLAP_EVENT_NOTE_ON: { auto* e = (const clap_event_note_t*)h; noteOn(s, e->key, e->note_id, e->velocity); break; }
    case CLAP_EVENT_NOTE_OFF: { auto* e = (const clap_event_note_t*)h; noteOff(s, e->key, e->note_id); break; }
    case CLAP_EVENT_MIDI:
    {
        auto* e = (const clap_event_midi_t*)h;
        int st = e->data[0] & 0xF0;
        if (st == 0x90 && e->data[2] > 0) noteOn(s, e->data[1], -1, e->data[2] / 127.0);
        else if (st == 0x80 || st == 0x90) noteOff(s, e->data[1], -1);
        break;
    }
    case CLAP_EVENT_PARAM_VALUE:
    {
        auto* e = (const clap_event_param_value_t*)h;
        int k = s->indexOf(e->param_id);
        if (k >= 0) { s->audioValues[k] = e->value; s->values[k] = e->value; }
        break;
    }
    default: break;
    }
}

clap_process_status process(const clap_plugin_t* p, const clap_process_t* pr)
{
    auto* s = self(p);
    if (s->dirtyToAudio.exchange(false)) s->audioValues = s->values;
    uint32_t n = pr->frames_count;
    uint32_t nev = pr->in_events->size(pr->in_events);
    uint32_t ei = 0;
    float* L = pr->audio_outputs[0].data32[0];
    float* R = pr->audio_outputs[0].data32[1];
    bool any = false;
    for (uint32_t i = 0; i < n; i++)
    {
        while (ei < nev)
        {
            auto* h = pr->in_events->get(pr->in_events, ei);
            if (h->time > i) break;
            handleEvent(s, h);
            ei++;
        }
        if (s->synth)
        {
            double gain = s->audioValues[0];
            int wave = (int)std::lround(s->audioValues[1]);
            double detune = std::pow(2.0, s->audioValues[2] / 1200.0);
            double out = 0;
            for (auto& v : s->voices)
            {
                if (v.key < 0) continue;
                double target = v.releasing ? 0.0 : v.amp;
                v.env += (target - v.env) * (v.releasing ? 0.0015 : 0.01);
                if (v.releasing && v.env < 1e-4) { v.key = -1; continue; }
                double ph = v.phase;
                double x = wave == 0 ? std::sin(2 * Pi * ph) : wave == 1 ? 2 * ph - 1 : (ph < 0.5 ? 1 : -1);
                out += x * v.env * 0.25;
                v.phase += v.freq * detune / s->rate;
                v.phase -= std::floor(v.phase);
            }
            float y = (float)(out * gain);
            L[i] = y;
            R[i] = y;
            if (y != 0) any = true;
        }
        else
        {
            double g = std::pow(10.0, s->audioValues[0] / 20.0);
            const float* inL = pr->audio_inputs[0].data32[0];
            const float* inR = pr->audio_inputs[0].data32[1];
            float dl = s->delayL[s->delayPos], dr = s->delayR[s->delayPos];
            s->delayL[s->delayPos] = (float)(inL[i] * g);
            s->delayR[s->delayPos] = (float)(inR[i] * g);
            s->delayPos = (s->delayPos + 1) % s->delayL.size();
            L[i] = dl;
            R[i] = dr;
            if (dl != 0 || dr != 0 || inL[i] != 0 || inR[i] != 0) any = true;
        }
    }
    while (ei < nev) handleEvent(s, pr->in_events->get(pr->in_events, ei++));
    return any ? CLAP_PROCESS_CONTINUE : CLAP_PROCESS_SLEEP;
}

const void* getExtension(const clap_plugin_t*, const char* id)
{
    if (!strcmp(id, CLAP_EXT_PARAMS)) return &paramsExt;
    if (!strcmp(id, CLAP_EXT_STATE)) return &stateExt;
    if (!strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &audioExt;
    if (!strcmp(id, CLAP_EXT_NOTE_PORTS)) return &noteExt;
    if (!strcmp(id, CLAP_EXT_LATENCY)) return &latencyExt;
    if (!strcmp(id, CLAP_EXT_GUI)) return &guiExt;
    return nullptr;
}

void onMainThread(const clap_plugin_t*) {}

const char* synthFeatures[] = {CLAP_PLUGIN_FEATURE_INSTRUMENT, CLAP_PLUGIN_FEATURE_SYNTHESIZER, nullptr};
const char* gainFeatures[] = {CLAP_PLUGIN_FEATURE_AUDIO_EFFECT, CLAP_PLUGIN_FEATURE_UTILITY, nullptr};

const clap_plugin_descriptor_t synthDesc{CLAP_VERSION_INIT, "neoplug.test.synth", "NeoPlug Test Synth", "neo-buzz", "", "", "", "0.1.0",
                                         "Sine, saw and square voices for the host's tests", synthFeatures};
const clap_plugin_descriptor_t gainDesc{CLAP_VERSION_INIT, "neoplug.test.gain", "NeoPlug Test Gain", "neo-buzz", "", "", "", "0.1.0",
                                        "A gain with 64 samples of latency", gainFeatures};

const clap_plugin_t* create(const clap_host_t* host, const clap_plugin_descriptor_t* desc, bool synth)
{
    auto* s = new TestPlugin();
    s->host = host;
    s->synth = synth;
    if (synth)
        s->descs = {{1, "Gain", "Output", 0, 1, 0.5, false}, {2, "Wave", "Oscillator", 0, 2, 0, true}, {3, "Detune", "Oscillator", -50, 50, 0, false}};
    else
        s->descs = {{1, "Gain dB", "", -60, 12, 0, false}};
    for (auto& d : s->descs) s->values.push_back(d.def);
    s->audioValues = s->values;
    s->plugin.desc = desc;
    s->plugin.plugin_data = s;
    s->plugin.init = init;
    s->plugin.destroy = destroy;
    s->plugin.activate = activate;
    s->plugin.deactivate = deactivate;
    s->plugin.start_processing = startProcessing;
    s->plugin.stop_processing = stopProcessing;
    s->plugin.reset = reset;
    s->plugin.process = process;
    s->plugin.get_extension = getExtension;
    s->plugin.on_main_thread = onMainThread;
    return &s->plugin;
}

uint32_t factoryCount(const clap_plugin_factory_t*) { return 2; }
const clap_plugin_descriptor_t* factoryDesc(const clap_plugin_factory_t*, uint32_t i) { return i == 0 ? &synthDesc : i == 1 ? &gainDesc : nullptr; }
const clap_plugin_t* factoryCreate(const clap_plugin_factory_t*, const clap_host_t* host, const char* id)
{
    if (!clap_version_is_compatible(host->clap_version)) return nullptr;
    if (!strcmp(id, synthDesc.id)) return create(host, &synthDesc, true);
    if (!strcmp(id, gainDesc.id)) return create(host, &gainDesc, false);
    return nullptr;
}

const clap_plugin_factory_t factory{factoryCount, factoryDesc, factoryCreate};

bool entryInit(const char*) { return true; }
void entryDeinit() {}
const void* entryFactory(const char* id) { return !strcmp(id, CLAP_PLUGIN_FACTORY_ID) ? &factory : nullptr; }

} // namespace

extern "C" CLAP_EXPORT const clap_plugin_entry_t clap_entry{CLAP_VERSION_INIT, entryInit, entryDeinit, entryFactory};
