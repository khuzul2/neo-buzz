// neoplug: the C ABI, dispatching to the VST3 and CLAP backends.
#include "np_internal.h"
#include "np_ipc.h"

#include <algorithm>
#include <cctype>

using namespace np;

#if defined(_WIN32)
namespace np { void ensureOle(); }   // np_platform_win.cpp
#define NP_UI_THREAD() np::ensureOle()
#else
#define NP_UI_THREAD() ((void)0)
#endif

struct np_plugin
{
    Plugin* impl;
};

namespace np {

void putU32(std::vector<uint8_t>& v, uint32_t x)
{
    for (int i = 0; i < 4; i++) v.push_back((uint8_t)(x >> (8 * i)));
}

bool getU32(const uint8_t*& p, const uint8_t* end, uint32_t& x)
{
    if (end - p < 4) return false;
    x = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    p += 4;
    return true;
}

} // namespace np

static bool isClap(const std::string& path)
{
    std::string p = path;
    while (!p.empty() && (p.back() == '/' || p.back() == '\\')) p.pop_back();
    if (p.size() < 5) return false;
    std::string ext = p.substr(p.size() - 5);
    std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char c) { return (char)std::tolower(c); });
    return ext == ".clap";
}

extern "C" {

NP_API const char* np_version(void) { return "neoplug 1.1 (H2)"; }
NP_API int np_abi(void) { return NP_ABI; }

NP_API int np_list(const char* path, np_class_info* out, int cap, char* err, int errlen)
{
    std::vector<np_class_info> list;
    std::string e;
    int r = isClap(path) ? clapList(path, list, e) : vst3List(path, list, e);
    if (r < 0)
    {
        setErr(err, errlen, e);
        return -1;
    }
    for (int i = 0; i < (int)list.size() && i < cap; i++) out[i] = list[i];
    return (int)list.size();
}

NP_API np_plugin* np_open(const char* path, const char* id, char* err, int errlen)
{
    NP_UI_THREAD();   // plugins and their editors expect COM/OLE on the main thread, as in every DAW
    std::string e;
    std::string sid = id ? id : "";
    Plugin* impl = isClap(path) ? clapOpen(path, sid, e) : vst3Open(path, sid, e);
    if (!impl)
    {
        setErr(err, errlen, e);
        return nullptr;
    }
    return new np_plugin{impl};
}

NP_API np_plugin* np_open_ex(const char* path, const char* id, int placement, char* err, int errlen)
{
    if (placement <= 0) return np_open(path, id, err, errlen);
    std::string e;
    Plugin* impl = remoteOpen(placement > 3 ? 3 : placement, path ? path : "", id ? id : "", e);
    if (!impl)
    {
        setErr(err, errlen, e);
        return nullptr;
    }
    return new np_plugin{impl};
}

NP_API int np_server_available(void) { return ipc::serverPath().empty() ? 0 : 1; }
NP_API int np_placement(np_plugin* p) { return p->impl->placement(); }
NP_API int np_status(np_plugin* p) { return p->impl->status(); }
NP_API int np_server_pid(np_plugin* p) { return (int)p->impl->serverPid(); }

NP_API int np_status_text(np_plugin* p, char* buf, int len)
{
    std::string s = p->impl->statusText();
    copyStr(buf, (size_t)(len > 0 ? len : 0), s);
    return (int)s.size();
}

NP_API int np_gui_open_window(np_plugin* p, const char* title, int* width, int* height, char* err, int errlen)
{
    std::string e;
    int w = 0, h = 0;
    if (!p->impl->guiOpenWindow(title, w, h, e))
    {
        setErr(err, errlen, e);
        return -1;
    }
    if (width) *width = w;
    if (height) *height = h;
    return 0;
}

NP_API void np_gui_close_window(np_plugin* p) { p->impl->guiCloseWindow(); }
NP_API void np_set_workgroup(void* wg, double period) { remoteSetWorkgroup(wg, period); }

NP_API void np_close(np_plugin* p)
{
    if (!p) return;
    delete p->impl;
    delete p;
}

NP_API int np_info(np_plugin* p, np_class_info* out)
{
    if (!p || !out) return -1;
    *out = p->impl->info;
    return 0;
}

NP_API int np_activate(np_plugin* p, double rate, int max_frames, int offline, char* err, int errlen)
{
    std::string e;
    if (!p->impl->activate(rate, max_frames, offline != 0, e))
    {
        setErr(err, errlen, e);
        return -1;
    }
    return 0;
}

NP_API void np_deactivate(np_plugin* p) { p->impl->deactivate(); }
NP_API int np_channels(np_plugin* p, int input) { return p->impl->channels(input != 0); }
NP_API int np_param_count(np_plugin* p) { return p->impl->paramCount(); }

NP_API int np_param_info_at(np_plugin* p, int index, np_param_info* out)
{
    *out = np_param_info{};
    return p->impl->paramInfo(index, *out) ? 0 : -1;
}

NP_API double np_param_get(np_plugin* p, uint32_t id) { return p->impl->paramGet(id); }

NP_API int np_param_text(np_plugin* p, uint32_t id, double value, char* buf, int len)
{
    std::string s;
    if (!p->impl->paramText(id, value, s)) return -1;
    copyStr(buf, (size_t)len, s);
    return (int)s.size();
}

NP_API int np_param_from_text(np_plugin* p, uint32_t id, const char* text, double* value)
{
    return p->impl->paramFromText(id, text, *value) ? 0 : -1;
}

NP_API int np_param_set(np_plugin* p, uint32_t id, double value) { return p->impl->paramSet(id, value) ? 0 : -1; }

NP_API int np_process_ex(np_plugin* p, np_process_io* io)
{
    io->out_count = 0;
    return p->impl->process(*io);
}

NP_API int np_process(np_plugin* p, const float* const* in, int in_ch, float* const* out, int out_ch, int frames,
                      const np_event* events, int nevents, double tempo, int playing)
{
    np_process_io io{};
    io.in = in; io.in_ch = in_ch; io.out = out; io.out_ch = out_ch; io.frames = frames;
    io.events = events; io.nevents = nevents; io.tempo = tempo; io.playing = playing; io.beat_pos = -1;
    io.tsig_num = 4; io.tsig_den = 4;
    return p->impl->process(io);
}

NP_API int np_aux_channels(np_plugin* p) { return p->impl->auxChannels(); }
NP_API int np_poll(np_plugin* p) { return p->impl->poll(); }
NP_API void np_flush(np_plugin* p) { p->impl->flushIdle(); }
NP_API double np_param_plain(np_plugin* p, uint32_t id, double value) { return p->impl->paramPlain(id, value); }
NP_API void np_reset(np_plugin* p) { p->impl->reset(); }

NP_API int np_state_save(np_plugin* p, uint8_t* buf, int cap)
{
    std::vector<uint8_t> s;
    if (!p->impl->saveState(s)) return -1;
    if (buf && cap >= (int)s.size()) std::memcpy(buf, s.data(), s.size());
    return (int)s.size();
}

NP_API int np_state_load(np_plugin* p, const uint8_t* buf, int size, char* err, int errlen)
{
    std::string e;
    if (!p->impl->loadState(buf, (size_t)size, e))
    {
        setErr(err, errlen, e);
        return -1;
    }
    return 0;
}

NP_API int np_latency(np_plugin* p) { return p->impl->latency(); }
NP_API int np_tail(np_plugin* p) { return p->impl->tail(); }
NP_API void np_idle(np_plugin* p) { p->impl->idle(); }
NP_API int np_gui_support(np_plugin* p, const char* api)
{
    NP_UI_THREAD();
    return p->impl->guiSupport(api);
}

NP_API int np_gui_attach(np_plugin* p, void* parent, const char* api, int* width, int* height, char* err, int errlen)
{
    NP_UI_THREAD();
    std::string e;
    int w = 0, h = 0;
    p->impl->guiThreadHash.store(Plugin::hashOf(std::this_thread::get_id()));
    if (!p->impl->guiAttach(parent, api, w, h, e))
    {
        p->impl->guiThreadHash.store(0);
        setErr(err, errlen, e);
        return -1;
    }
    if (width) *width = w;
    if (height) *height = h;
    return 0;
}

NP_API void np_gui_detach(np_plugin* p)
{
    p->impl->guiDetach();
    p->impl->guiThreadHash.store(0);
}

NP_API void np_gui_set_callbacks(np_plugin* p, np_gui_resize_cb resize_cb, np_gui_closed_cb closed_cb, void* user)
{
    p->impl->guiResizeCb = resize_cb;
    p->impl->guiClosedCb = closed_cb;
    p->impl->guiUser = user;
}

NP_API int np_gui_size(np_plugin* p, int* width, int* height)
{
    int w = 0, h = 0;
    if (!p->impl->guiSize(w, h)) return -1;
    if (width) *width = w;
    if (height) *height = h;
    return 0;
}

NP_API int np_gui_can_resize(np_plugin* p) { return p->impl->guiCanResize() ? 1 : 0; }

NP_API int np_gui_set_size(np_plugin* p, int* width, int* height)
{
    int w = width ? *width : 0, h = height ? *height : 0;
    if (!p->impl->guiSetSize(w, h)) return -1;
    if (width) *width = w;
    if (height) *height = h;
    return 0;
}

NP_API int np_gui_set_scale(np_plugin* p, double scale)
{
    p->impl->guiSetScale(scale);
    return 0;
}

NP_API int np_out_count(np_plugin* p) { return p->impl->outCount(); }

NP_API int np_out_info(np_plugin* p, int index, char* name, int len, int* channels)
{
    std::string n;
    int ch = 0;
    if (!p->impl->outInfo(index, n, ch)) return -1;
    copyStr(name, (size_t)(len > 0 ? len : 0), n);
    if (channels) *channels = ch;
    return 0;
}

NP_API void np_out_select(np_plugin* p, int index) { p->impl->outSel.store(index < 0 ? 0 : index); }

} // extern "C"

#if !defined(__APPLE__) && !defined(_WIN32)
// Linux: no GUI in the spike (CI has no display); the helpers report that.
extern "C" {
NP_API void* np_platform_container(void*, int, int) { return nullptr; }
NP_API void np_platform_resize(void*, int, int) {}
NP_API void* np_platform_window(const char*, int, int) { return nullptr; }
NP_API int np_platform_describe(void*, char* buf, int len)
{
    copyStr(buf, (size_t)len, "no GUI on this platform");
    return -1;
}
NP_API int np_platform_snapshot(void*, const char*, int, char* stats, int len)
{
    copyStr(stats, (size_t)len, "{}");
    return -1;
}
NP_API void np_platform_run(double) {}
NP_API void np_platform_release(void*) {}
NP_API void np_platform_window_size(void*, int, int) {}
NP_API void np_platform_on_close(void*, void (*)(void*), void*) {}
}
#endif
