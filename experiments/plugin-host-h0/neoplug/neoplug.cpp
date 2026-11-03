// neoplug (H0 spike): the C ABI, dispatching to the VST3 and CLAP backends.
#include "np_internal.h"

#include <algorithm>
#include <cctype>

using namespace np;

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

NP_API const char* np_version(void) { return "neoplug-h0 0.1"; }

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

NP_API int np_process(np_plugin* p, const float* const* in, int in_ch, float* const* out, int out_ch, int frames,
                      const np_event* events, int nevents, double tempo, int playing)
{
    return p->impl->process(in, in_ch, out, out_ch, frames, events, nevents, tempo, playing != 0);
}

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
NP_API int np_gui_support(np_plugin* p, const char* api) { return p->impl->guiSupport(api); }

NP_API int np_gui_attach(np_plugin* p, void* parent, const char* api, int* width, int* height, char* err, int errlen)
{
    std::string e;
    int w = 0, h = 0;
    if (!p->impl->guiAttach(parent, api, w, h, e))
    {
        setErr(err, errlen, e);
        return -1;
    }
    if (width) *width = w;
    if (height) *height = h;
    return 0;
}

NP_API void np_gui_detach(np_plugin* p) { p->impl->guiDetach(); }

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
}
#endif
