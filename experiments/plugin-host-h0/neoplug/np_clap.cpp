// neoplug (H0 spike): CLAP hosting (MIT headers, nothing else needed).
#include "np_internal.h"

#include <clap/clap.h>

#include <algorithm>
#include <cmath>
#include <map>
#include <memory>
#include <mutex>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#elif defined(__APPLE__)
#include <CoreFoundation/CoreFoundation.h>
#else
#include <dlfcn.h>
#endif

namespace np {

// --- Modules: loaded once per path, init/deinit around their users -----------------------------

struct ClapModule
{
    std::string path;
    const clap_plugin_entry_t* entry = nullptr;
#if defined(_WIN32)
    HMODULE handle = nullptr;
#elif defined(__APPLE__)
    CFBundleRef bundle = nullptr;
#else
    void* handle = nullptr;
#endif
    ~ClapModule()
    {
        if (entry) entry->deinit();
#if defined(_WIN32)
        if (handle) FreeLibrary(handle);
#elif defined(__APPLE__)
        if (bundle) CFRelease(bundle);
#else
        if (handle) dlclose(handle);
#endif
    }
};

static std::mutex modulesLock;
static std::map<std::string, std::weak_ptr<ClapModule>> modules;

static std::shared_ptr<ClapModule> loadModule(const std::string& path, std::string& err)
{
    std::lock_guard<std::mutex> g(modulesLock);
    if (auto m = modules[path].lock()) return m;
    auto m = std::make_shared<ClapModule>();
    m->path = path;
#if defined(_WIN32)
    int n = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, w.data(), n);
    m->handle = LoadLibraryW(w.c_str());
    if (!m->handle) { err = "LoadLibrary failed (" + std::to_string(GetLastError()) + ")"; return nullptr; }
    m->entry = (const clap_plugin_entry_t*)GetProcAddress(m->handle, "clap_entry");
#elif defined(__APPLE__)
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(nullptr, (const UInt8*)path.c_str(), (CFIndex)path.size(), true);
    m->bundle = url ? CFBundleCreate(nullptr, url) : nullptr;
    if (url) CFRelease(url);
    if (!m->bundle) { err = "not a bundle"; return nullptr; }
    CFErrorRef cfErr = nullptr;
    if (!CFBundleLoadExecutableAndReturnError(m->bundle, &cfErr))
    {
        err = "the bundle's executable did not load";
        if (cfErr) CFRelease(cfErr);
        return nullptr;
    }
    m->entry = (const clap_plugin_entry_t*)CFBundleGetDataPointerForName(m->bundle, CFSTR("clap_entry"));
#else
    m->handle = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!m->handle) { err = std::string("dlopen: ") + dlerror(); return nullptr; }
    m->entry = (const clap_plugin_entry_t*)dlsym(m->handle, "clap_entry");
#endif
    if (!m->entry) { err = "no clap_entry"; return nullptr; }
    if (!clap_version_is_compatible(m->entry->clap_version)) { m->entry = nullptr; err = "incompatible CLAP version"; return nullptr; }
    if (!m->entry->init(path.c_str())) { m->entry = nullptr; err = "clap_entry.init failed"; return nullptr; }
    modules[path] = m;
    return m;
}

static void fillInfo(const clap_plugin_descriptor_t* d, np_class_info& out)
{
    out = np_class_info{};
    out.format = NP_FORMAT_CLAP;
    copyStr(out.id, sizeof out.id, d->id ? d->id : "");
    copyStr(out.name, sizeof out.name, d->name ? d->name : "");
    copyStr(out.vendor, sizeof out.vendor, d->vendor ? d->vendor : "");
    copyStr(out.version, sizeof out.version, d->version ? d->version : "");
    std::string features;
    for (auto f = d->features; f && *f; f++)
    {
        if (!features.empty()) features += "|";
        features += *f;
        if (std::string(*f) == CLAP_PLUGIN_FEATURE_INSTRUMENT) out.is_instrument = 1;
    }
    copyStr(out.category, sizeof out.category, features);
}

int clapList(const std::string& path, std::vector<np_class_info>& out, std::string& err)
{
    auto m = loadModule(path, err);
    if (!m) return -1;
    auto f = (const clap_plugin_factory_t*)m->entry->get_factory(CLAP_PLUGIN_FACTORY_ID);
    if (!f) { err = "no plugin factory"; return -1; }
    uint32_t n = f->get_plugin_count(f);
    for (uint32_t i = 0; i < n; i++)
    {
        np_class_info info;
        fillInfo(f->get_plugin_descriptor(f, i), info);
        out.push_back(info);
    }
    return (int)out.size();
}

// --- The plugin ---------------------------------------------------------------------------------

static thread_local bool onAudioThread = false;

union AnyEvent
{
    clap_event_header_t header;
    clap_event_note_t note;
    clap_event_param_value_t param;
    clap_event_midi_t midi;
};

class ClapPlugin : public Plugin
{
public:
    std::shared_ptr<ClapModule> module;
    clap_host_t host{};
    const clap_plugin_t* plugin = nullptr;
    const clap_plugin_params_t* params = nullptr;
    const clap_plugin_state_t* state = nullptr;
    const clap_plugin_audio_ports_t* audioPorts = nullptr;
    const clap_plugin_note_ports_t* notePorts = nullptr;
    const clap_plugin_latency_t* latencyExt = nullptr;
    const clap_plugin_tail_t* tailExt = nullptr;
    const clap_plugin_gui_t* gui = nullptr;
    const clap_plugin_render_t* render = nullptr;

    bool active = false, processing = false, guiCreated = false;
    std::atomic<bool> callbackRequested{false}, restartRequested{false};
    bool useMidiDialect = false;
    int64_t steadyTime = 0;
    int maxFrames = 0;
    double sampleRate = 48000;

    struct Port { uint32_t channels; bool main; std::vector<float> storage; std::vector<float*> ptrs; };
    std::vector<Port> inPorts, outPorts;
    std::vector<clap_audio_buffer_t> inBufs, outBufs;
    std::vector<AnyEvent> evStore;
    uint32_t evCount = 0;
    ParamRing toProcessor;

    ~ClapPlugin() override
    {
        if (!plugin) return;
        guiDetach();
        deactivate();
        plugin->destroy(plugin);
    }

    // Host callbacks.
    static ClapPlugin* self(const clap_host_t* h) { return (ClapPlugin*)h->host_data; }

    static const void* getExtension(const clap_host_t* h, const char* id);
    static void requestRestart(const clap_host_t* h) { self(h)->restartRequested = true; }
    static void requestProcess(const clap_host_t*) {}
    static void requestCallback(const clap_host_t* h) { self(h)->callbackRequested = true; }

    bool open(const std::string& path, const std::string& id, std::string& err)
    {
        module = loadModule(path, err);
        if (!module) return false;
        auto f = (const clap_plugin_factory_t*)module->entry->get_factory(CLAP_PLUGIN_FACTORY_ID);
        if (!f) { err = "no plugin factory"; return false; }
        const clap_plugin_descriptor_t* desc = nullptr;
        for (uint32_t i = 0; i < f->get_plugin_count(f); i++)
        {
            auto d = f->get_plugin_descriptor(f, i);
            if (id.empty() || id == d->id || id == d->name) { desc = d; break; }
        }
        if (!desc) { err = "plugin not found: " + id; return false; }
        fillInfo(desc, info);
        host.clap_version = CLAP_VERSION;
        host.host_data = this;
        host.name = "neo-buzz (neoplug H0)";
        host.vendor = "neo-buzz";
        host.url = "https://github.com/khuzul2/neo-buzz";
        host.version = "0.1";
        host.get_extension = getExtension;
        host.request_restart = requestRestart;
        host.request_process = requestProcess;
        host.request_callback = requestCallback;
        plugin = f->create_plugin(f, &host, desc->id);
        if (!plugin) { err = "create_plugin failed"; return false; }
        if (!plugin->init(plugin))
        {
            plugin->destroy(plugin);
            plugin = nullptr;
            err = "init failed";
            return false;
        }
        params = (const clap_plugin_params_t*)plugin->get_extension(plugin, CLAP_EXT_PARAMS);
        state = (const clap_plugin_state_t*)plugin->get_extension(plugin, CLAP_EXT_STATE);
        audioPorts = (const clap_plugin_audio_ports_t*)plugin->get_extension(plugin, CLAP_EXT_AUDIO_PORTS);
        notePorts = (const clap_plugin_note_ports_t*)plugin->get_extension(plugin, CLAP_EXT_NOTE_PORTS);
        latencyExt = (const clap_plugin_latency_t*)plugin->get_extension(plugin, CLAP_EXT_LATENCY);
        tailExt = (const clap_plugin_tail_t*)plugin->get_extension(plugin, CLAP_EXT_TAIL);
        gui = (const clap_plugin_gui_t*)plugin->get_extension(plugin, CLAP_EXT_GUI);
        render = (const clap_plugin_render_t*)plugin->get_extension(plugin, CLAP_EXT_RENDER);
        if (notePorts && notePorts->count(plugin, true) > 0)
        {
            clap_note_port_info_t ni{};
            if (notePorts->get(plugin, 0, true, &ni))
                useMidiDialect = !(ni.supported_dialects & CLAP_NOTE_DIALECT_CLAP) && (ni.supported_dialects & CLAP_NOTE_DIALECT_MIDI);
        }
        evStore.resize(2048);
        return true;
    }

    void scanPorts(bool input, std::vector<Port>& ports)
    {
        ports.clear();
        uint32_t n = audioPorts ? audioPorts->count(plugin, input) : 0;
        for (uint32_t i = 0; i < n; i++)
        {
            clap_audio_port_info_t pi{};
            if (!audioPorts->get(plugin, i, input, &pi)) continue;
            Port p;
            p.channels = pi.channel_count;
            p.main = (pi.flags & CLAP_AUDIO_PORT_IS_MAIN) != 0;
            p.storage.assign((size_t)pi.channel_count * (size_t)maxFrames, 0.f);
            for (uint32_t c = 0; c < pi.channel_count; c++) p.ptrs.push_back(p.storage.data() + (size_t)c * maxFrames);
            ports.push_back(std::move(p));
        }
    }

    bool activate(double rate, int frames, bool offline, std::string& err) override
    {
        deactivate();
        maxFrames = frames;
        sampleRate = rate;
        scanPorts(true, inPorts);
        scanPorts(false, outPorts);
        inBufs.assign(inPorts.size(), clap_audio_buffer_t{});
        outBufs.assign(outPorts.size(), clap_audio_buffer_t{});
        for (size_t i = 0; i < inPorts.size(); i++) { inBufs[i].data32 = inPorts[i].ptrs.data(); inBufs[i].channel_count = inPorts[i].channels; }
        for (size_t i = 0; i < outPorts.size(); i++) { outBufs[i].data32 = outPorts[i].ptrs.data(); outBufs[i].channel_count = outPorts[i].channels; }
        if (render) render->set(plugin, offline ? CLAP_RENDER_OFFLINE : CLAP_RENDER_REALTIME);
        if (!plugin->activate(plugin, rate, 1, (uint32_t)frames)) { err = "activate failed"; return false; }
        active = true;
        processing = false;
        return true;
    }

    void deactivate() override
    {
        if (!active) return;
        if (processing)
        {
            // stop_processing belongs to the audio thread; the spike's caller has stopped calling process.
            bool was = onAudioThread;
            onAudioThread = true;
            plugin->stop_processing(plugin);
            onAudioThread = was;
        }
        processing = false;
        plugin->deactivate(plugin);
        active = false;
    }

    int channels(bool input) override
    {
        auto& ports = input ? inPorts : outPorts;
        if (ports.empty() && audioPorts)
        {
            uint32_t n = audioPorts->count(plugin, input);
            for (uint32_t i = 0; i < n; i++)
            {
                clap_audio_port_info_t pi{};
                if (audioPorts->get(plugin, i, input, &pi) && (i == 0 || (pi.flags & CLAP_AUDIO_PORT_IS_MAIN))) return (int)pi.channel_count;
            }
            return 0;
        }
        for (auto& p : ports) if (p.main) return (int)p.channels;
        return ports.empty() ? 0 : (int)ports[0].channels;
    }

    int paramCount() override { return params ? (int)params->count(plugin) : 0; }

    bool paramInfo(int index, np_param_info& out) override
    {
        if (!params) return false;
        clap_param_info_t pi{};
        if (!params->get_info(plugin, (uint32_t)index, &pi)) return false;
        out.id = pi.id;
        out.min = pi.min_value;
        out.max = pi.max_value;
        out.def = pi.default_value;
        out.flags = 0;
        if (pi.flags & CLAP_PARAM_IS_AUTOMATABLE) out.flags |= NP_PARAM_AUTOMATABLE;
        if (pi.flags & CLAP_PARAM_IS_STEPPED)
        {
            out.flags |= NP_PARAM_STEPPED;
            out.steps = (int)std::lround(pi.max_value - pi.min_value);
        }
        if (pi.flags & CLAP_PARAM_IS_READONLY) out.flags |= NP_PARAM_READONLY;
        if (pi.flags & CLAP_PARAM_IS_HIDDEN) out.flags |= NP_PARAM_HIDDEN;
        if (pi.flags & CLAP_PARAM_IS_BYPASS) out.flags |= NP_PARAM_BYPASS;
        copyStr(out.name, sizeof out.name, pi.name);
        copyStr(out.module, sizeof out.module, pi.module);
        return true;
    }

    double paramGet(uint32_t id) override
    {
        double v = 0;
        if (params) params->get_value(plugin, id, &v);
        return v;
    }

    bool paramText(uint32_t id, double v, std::string& out) override
    {
        char buf[256] = {};
        if (!params || !params->value_to_text(plugin, id, v, buf, sizeof buf)) return false;
        out = buf;
        return true;
    }

    bool paramFromText(uint32_t id, const char* text, double& out) override
    {
        return params && params->text_to_value(plugin, id, text, &out);
    }

    // Input event list over evStore[0..evCount).
    static uint32_t evSize(const clap_input_events_t* l) { return ((ClapPlugin*)l->ctx)->evCount; }
    static const clap_event_header_t* evGet(const clap_input_events_t* l, uint32_t i) { return &((ClapPlugin*)l->ctx)->evStore[i].header; }
    static bool outPush(const clap_output_events_t*, const clap_event_header_t*) { return true; }

    void pushParam(uint32_t time, uint32_t id, double v)
    {
        if (evCount >= evStore.size()) return;
        auto& e = evStore[evCount++].param;
        e = clap_event_param_value_t{};
        e.header = {sizeof(clap_event_param_value_t), time, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_PARAM_VALUE, 0};
        e.param_id = id;
        e.cookie = nullptr;
        e.note_id = -1;
        e.port_index = -1;
        e.channel = -1;
        e.key = -1;
        e.value = v;
    }

    bool paramSet(uint32_t id, double v) override
    {
        if (!params) return false;
        if (active) return toProcessor.push(id, v);
        evCount = 0;
        pushParam(0, id, v);
        clap_input_events_t in{this, evSize, evGet};
        clap_output_events_t out{this, outPush};
        params->flush(plugin, &in, &out);
        evCount = 0;
        return true;
    }

    int process(const float* const* in, int inCh, float* const* out, int outCh, int frames, const np_event* ev, int nev,
                double tempo, bool playing) override
    {
        struct AudioScope { AudioScope() { onAudioThread = true; } ~AudioScope() { onAudioThread = false; } } scope;
        if (!active || frames > maxFrames)
        {
            for (int c = 0; c < outCh; c++) std::memset(out[c], 0, sizeof(float) * frames);
            return active ? -1 : 0;
        }
        if (!processing)
        {
            if (!plugin->start_processing(plugin))
            {
                for (int c = 0; c < outCh; c++) std::memset(out[c], 0, sizeof(float) * frames);
                return -1;
            }
            processing = true;
        }
        bool mainIn = false;
        for (auto& p : inPorts)
        {
            for (uint32_t c = 0; c < p.channels; c++)
            {
                float* dst = p.ptrs[c];
                if (!mainIn && p.main && inCh > 0 && in) std::memcpy(dst, in[std::min<int>((int)c, inCh - 1)], sizeof(float) * frames);
                else std::memset(dst, 0, sizeof(float) * frames);
            }
            if (p.main) mainIn = true;
        }
        evCount = 0;
        ParamRing::Item it;
        while (toProcessor.pop(it)) pushParam(0, it.id, it.value);
        for (int i = 0; i < nev && evCount < evStore.size(); i++)
        {
            const np_event& e = ev[i];
            uint32_t t = std::min<uint32_t>(e.frame, (uint32_t)frames - 1);
            if (e.type == NP_EV_PARAM) { pushParam(t, e.param_id, e.value); continue; }
            bool on = e.type == NP_EV_NOTE_ON;
            if (useMidiDialect)
            {
                auto& m = evStore[evCount++].midi;
                m.header = {sizeof(clap_event_midi_t), t, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_MIDI, 0};
                m.port_index = 0;
                m.data[0] = (uint8_t)((on ? 0x90 : 0x80) | (e.channel & 15));
                m.data[1] = (uint8_t)(e.key & 127);
                m.data[2] = (uint8_t)std::clamp((int)std::lround(e.value * 127.0), on ? 1 : 0, 127);
            }
            else
            {
                auto& n = evStore[evCount++].note;
                n.header = {sizeof(clap_event_note_t), t, CLAP_CORE_EVENT_SPACE_ID, (uint16_t)(on ? CLAP_EVENT_NOTE_ON : CLAP_EVENT_NOTE_OFF), 0};
                n.note_id = e.note_id;
                n.port_index = 0;
                n.channel = (int16_t)e.channel;
                n.key = (int16_t)e.key;
                n.velocity = e.value;
            }
        }
        clap_event_transport_t transport{};
        transport.header = {sizeof(clap_event_transport_t), 0, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_TRANSPORT, 0};
        transport.flags = CLAP_TRANSPORT_HAS_TEMPO | (playing ? CLAP_TRANSPORT_IS_PLAYING : 0);
        transport.tempo = tempo > 0 ? tempo : 120;
        transport.tsig_num = 4;
        transport.tsig_denom = 4;
        clap_input_events_t inEv{this, evSize, evGet};
        clap_output_events_t outEv{this, outPush};
        clap_process_t pr{};
        pr.steady_time = steadyTime;
        pr.frames_count = (uint32_t)frames;
        pr.transport = &transport;
        pr.audio_inputs = inBufs.empty() ? nullptr : inBufs.data();
        pr.audio_inputs_count = (uint32_t)inBufs.size();
        pr.audio_outputs = outBufs.empty() ? nullptr : outBufs.data();
        pr.audio_outputs_count = (uint32_t)outBufs.size();
        pr.in_events = &inEv;
        pr.out_events = &outEv;
        for (auto& b : outBufs) b.constant_mask = 0;
        clap_process_status st = plugin->process(plugin, &pr);
        steadyTime += frames;
        evCount = 0;
        const Port* mainOut = nullptr;
        for (auto& p : outPorts) if (p.main) { mainOut = &p; break; }
        if (!mainOut && !outPorts.empty()) mainOut = &outPorts[0];
        float peak = 0;
        for (int c = 0; c < outCh; c++)
        {
            if (mainOut && mainOut->channels > 0 && st != CLAP_PROCESS_ERROR)
            {
                const float* src = mainOut->ptrs[std::min<uint32_t>((uint32_t)c, mainOut->channels - 1)];
                std::memcpy(out[c], src, sizeof(float) * frames);
                for (int i = 0; i < frames; i++) peak = std::max(peak, std::fabs(src[i]));
            }
            else
                std::memset(out[c], 0, sizeof(float) * frames);
        }
        if (st == CLAP_PROCESS_ERROR) return -1;
        return peak > 0 ? 1 : 0;
    }

    static int64_t writeCb(const clap_ostream_t* s, const void* buf, uint64_t size)
    {
        auto* v = (std::vector<uint8_t>*)s->ctx;
        v->insert(v->end(), (const uint8_t*)buf, (const uint8_t*)buf + size);
        return (int64_t)size;
    }

    struct Reader { const uint8_t* p; const uint8_t* end; };
    static int64_t readCb(const clap_istream_t* s, void* buf, uint64_t size)
    {
        auto* r = (Reader*)s->ctx;
        uint64_t n = std::min<uint64_t>(size, (uint64_t)(r->end - r->p));
        std::memcpy(buf, r->p, n);
        r->p += n;
        return (int64_t)n;
    }

    bool saveState(std::vector<uint8_t>& out) override
    {
        if (!state) return false;
        std::vector<uint8_t> blob;
        clap_ostream_t os{&blob, writeCb};
        if (!state->save(plugin, &os)) return false;
        out.clear();
        out.insert(out.end(), {'N', 'P', 'S', 'T'});
        putU32(out, NP_FORMAT_CLAP);
        putU32(out, (uint32_t)blob.size());
        out.insert(out.end(), blob.begin(), blob.end());
        return true;
    }

    bool loadState(const uint8_t* d, size_t size, std::string& err) override
    {
        if (!state) { err = "no state extension"; return false; }
        const uint8_t* p = d;
        const uint8_t* end = d + size;
        uint32_t fmt = 0, len = 0;
        if (size < 8 || std::memcmp(d, "NPST", 4) != 0) { err = "not a neoplug state"; return false; }
        p += 4;
        if (!getU32(p, end, fmt) || fmt != NP_FORMAT_CLAP) { err = "not a CLAP state"; return false; }
        if (!getU32(p, end, len) || (size_t)(end - p) < len) { err = "truncated"; return false; }
        Reader r{p, p + len};
        clap_istream_t is{&r, readCb};
        if (!state->load(plugin, &is)) { err = "the plugin refused its state"; return false; }
        return true;
    }

    int latency() override { return latencyExt ? (int)latencyExt->get(plugin) : 0; }

    int tail() override
    {
        if (!tailExt) return 0;
        uint32_t t = tailExt->get(plugin);
        return t >= (uint32_t)INT32_MAX ? -1 : (int)t;
    }

    void idle() override
    {
        if (callbackRequested.exchange(false)) plugin->on_main_thread(plugin);
    }

    static const char* clapApi(const char* api)
    {
        std::string a = api ? api : "";
        if (a == "nsview") return CLAP_WINDOW_API_COCOA;
        if (a == "hwnd") return CLAP_WINDOW_API_WIN32;
        if (a == "x11") return CLAP_WINDOW_API_X11;
        return nullptr;
    }

    int guiSupport(const char* api) override
    {
        const char* a = clapApi(api);
        if (!gui || !a) return 0;
        return (gui->is_api_supported(plugin, a, false) ? 1 : 0) | (gui->is_api_supported(plugin, a, true) ? 2 : 0);
    }

    bool guiAttach(void* parent, const char* api, int& w, int& h, std::string& err) override
    {
        const char* a = clapApi(api);
        if (!gui || !a) { err = "no GUI extension"; return false; }
        if (!gui->is_api_supported(plugin, a, false)) { err = "embedding not supported"; return false; }
        if (!gui->create(plugin, a, false)) { err = "gui.create failed"; return false; }
        guiCreated = true;
#if defined(_WIN32)
        gui->set_scale(plugin, 1.0);
#endif
        uint32_t cw = 0, ch = 0;
        gui->get_size(plugin, &cw, &ch);
        clap_window_t win{};
        win.api = a;
        win.ptr = parent;
        if (!gui->set_parent(plugin, &win)) { err = "gui.set_parent failed"; return false; }
        gui->show(plugin);
        w = (int)cw;
        h = (int)ch;
        return true;
    }

    void guiDetach() override
    {
        if (!guiCreated) return;
        gui->hide(plugin);
        gui->destroy(plugin);
        guiCreated = false;
    }
};

// --- Host extensions ----------------------------------------------------------------------------

static void hLog(const clap_host_t*, clap_log_severity sev, const char* msg)
{
    if (sev >= CLAP_LOG_WARNING) fprintf(stderr, "[clap] %s\n", msg);
}
static const clap_host_log_t hostLog{hLog};

static bool hIsMain(const clap_host_t* h) { return std::this_thread::get_id() == ClapPlugin::self(h)->mainThread; }
static bool hIsAudio(const clap_host_t*) { return onAudioThread; }
static const clap_host_thread_check_t hostThreadCheck{hIsMain, hIsAudio};

static void hParamsRescan(const clap_host_t*, clap_param_rescan_flags) {}
static void hParamsClear(const clap_host_t*, clap_id, clap_param_clear_flags) {}
static void hParamsFlush(const clap_host_t*) {}
static const clap_host_params_t hostParams{hParamsRescan, hParamsClear, hParamsFlush};

static void hMarkDirty(const clap_host_t*) {}
static const clap_host_state_t hostState{hMarkDirty};

static void hLatencyChanged(const clap_host_t*) {}
static const clap_host_latency_t hostLatency{hLatencyChanged};

static void hTailChanged(const clap_host_t*) {}
static const clap_host_tail_t hostTail{hTailChanged};

static bool hPortsRescanSupported(const clap_host_t*, uint32_t) { return true; }
static void hPortsRescan(const clap_host_t* h, uint32_t) { ClapPlugin::self(h)->restartRequested = true; }
static const clap_host_audio_ports_t hostAudioPorts{hPortsRescanSupported, hPortsRescan};

static uint32_t hNoteDialects(const clap_host_t*) { return CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI; }
static void hNoteRescan(const clap_host_t*, uint32_t) {}
static const clap_host_note_ports_t hostNotePorts{hNoteDialects, hNoteRescan};

static void hGuiHints(const clap_host_t*) {}
static bool hGuiResize(const clap_host_t*, uint32_t, uint32_t) { return true; }
static bool hGuiShow(const clap_host_t*) { return true; }
static bool hGuiHide(const clap_host_t*) { return true; }
static void hGuiClosed(const clap_host_t*, bool) {}
static const clap_host_gui_t hostGui{hGuiHints, hGuiResize, hGuiShow, hGuiHide, hGuiClosed};

const void* ClapPlugin::getExtension(const clap_host_t*, const char* id)
{
    std::string e = id ? id : "";
    if (e == CLAP_EXT_LOG) return &hostLog;
    if (e == CLAP_EXT_THREAD_CHECK) return &hostThreadCheck;
    if (e == CLAP_EXT_PARAMS) return &hostParams;
    if (e == CLAP_EXT_STATE) return &hostState;
    if (e == CLAP_EXT_LATENCY) return &hostLatency;
    if (e == CLAP_EXT_TAIL) return &hostTail;
    if (e == CLAP_EXT_AUDIO_PORTS) return &hostAudioPorts;
    if (e == CLAP_EXT_NOTE_PORTS) return &hostNotePorts;
    if (e == CLAP_EXT_GUI) return &hostGui;
    return nullptr;
}

Plugin* clapOpen(const std::string& path, const std::string& id, std::string& err)
{
    auto* p = new ClapPlugin();
    if (!p->open(path, id, err))
    {
        delete p;
        return nullptr;
    }
    return p;
}

} // namespace np
