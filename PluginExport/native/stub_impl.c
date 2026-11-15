/* One plug-in per module: the NativeAOT library lists every Neo plug-in in its factory, this module (built once per plug-in with
 * -DNEO_PLUGIN_ID="org.neobuzz.neo-filter") shows only its own, so the .clap, the .vst3 and the .component are each one plug-in.
 * The CLAP entry's three functions, forwarded to the C# exports `neo_clap_init`, `neo_clap_deinit` and `neo_clap_get_factory`
 * in the NativeAOT library next to the module this is linked into (the .clap, or clap-wrapper's VST3 or AU module). Static
 * library; clap_entry.c next to it holds the `clap_entry` data symbol, which a C# export cannot be (NativeAOT exports functions).
 * clap-wrapper's "clap first" helper links both into every format. */
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <clap/clap.h>
#include "neo_entry.h"
#include <stdio.h>
#include <string.h>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define DSP_FILE "neo_dsp.dll"
#define SEP '\\'
#else
#include <dlfcn.h>
#define SEP '/'
#ifdef __APPLE__
#define DSP_FILE "neo_dsp.dylib"
#else
#define DSP_FILE "neo_dsp.so"
#endif
#endif

static unsigned char (*p_init)(const char*);
static void (*p_deinit)(void);
static const void* (*p_factory)(const char*);
static int g_loaded;

static void module_dir(char* dir, size_t cap)
{
    dir[0] = 0;
#ifdef _WIN32
    HMODULE m = NULL;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&module_dir, &m))
        GetModuleFileNameA(m, dir, (DWORD)cap);
#else
    Dl_info i;
    if (dladdr((void*)&module_dir, &i) && i.dli_fname) { strncpy(dir, i.dli_fname, cap - 1); dir[cap - 1] = 0; }
#endif
    char* s = strrchr(dir, SEP);
    if (s) *s = 0;
}

bool neo_entry_init(const char* plugin_path)
{
    (void)plugin_path;
    char dir[2048], lib[2200];
    if (!g_loaded) {
        module_dir(dir, sizeof dir);
        snprintf(lib, sizeof lib, "%s%c%s", dir, SEP, DSP_FILE);
#ifdef _WIN32
        HMODULE h = LoadLibraryA(lib);
        if (!h) return false;
        p_init = (unsigned char (*)(const char*))GetProcAddress(h, "neo_clap_init");
        p_deinit = (void (*)(void))GetProcAddress(h, "neo_clap_deinit");
        p_factory = (const void* (*)(const char*))GetProcAddress(h, "neo_clap_get_factory");
#else
        void* h = dlopen(lib, RTLD_NOW | RTLD_LOCAL);
        if (!h) { fprintf(stderr, "neo clap: %s\n", dlerror()); return false; }
        p_init = (unsigned char (*)(const char*))dlsym(h, "neo_clap_init");
        p_deinit = (void (*)(void))dlsym(h, "neo_clap_deinit");
        p_factory = (const void* (*)(const char*))dlsym(h, "neo_clap_get_factory");
#endif
        if (!p_init || !p_deinit || !p_factory) return false;
        g_loaded = 1;
    }
    module_dir(dir, sizeof dir);
    return p_init(dir) != 0;
}

void neo_entry_deinit(void) { if (p_deinit) p_deinit(); }   /* the NativeAOT library stays loaded: a runtime cannot be unloaded */

#ifdef NEO_PLUGIN_ID
static const clap_plugin_factory_t* g_real;
static const clap_plugin_descriptor_t* g_desc;
static uint32_t f_count(const clap_plugin_factory_t* f) { (void)f; return g_desc ? 1u : 0u; }
static const clap_plugin_descriptor_t* f_desc(const clap_plugin_factory_t* f, uint32_t i) { (void)f; return i == 0 ? g_desc : NULL; }
static const clap_plugin_t* f_create(const clap_plugin_factory_t* f, const clap_host_t* host, const char* id)
{
    (void)f;
    return g_desc && id && strcmp(id, g_desc->id) == 0 ? g_real->create_plugin(g_real, host, id) : NULL;
}
static const clap_plugin_factory_t g_filtered = { f_count, f_desc, f_create };

const void* neo_entry_get_factory(const char* id)
{
    if (!p_factory) return NULL;
    const void* real = p_factory(id);
    if (!real || strcmp(id, CLAP_PLUGIN_FACTORY_ID) != 0) return real;
    if (!g_real) {
        g_real = (const clap_plugin_factory_t*)real;
        for (uint32_t i = 0, n = g_real->get_plugin_count(g_real); i < n; i++) {
            const clap_plugin_descriptor_t* d = g_real->get_plugin_descriptor(g_real, i);
            if (d && strcmp(d->id, NEO_PLUGIN_ID) == 0) { g_desc = d; break; }
        }
    }
    return &g_filtered;
}
#else
const void* neo_entry_get_factory(const char* id) { return p_factory ? p_factory(id) : NULL; }
#endif

