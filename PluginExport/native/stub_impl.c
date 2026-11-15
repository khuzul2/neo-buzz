/* The CLAP entry's three functions, forwarded to the C# exports `neo_clap_init`, `neo_clap_deinit` and `neo_clap_get_factory`
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
#define DSP_FILE "neofilter_dsp.dll"
#define SEP '\\'
#else
#include <dlfcn.h>
#define SEP '/'
#ifdef __APPLE__
#define DSP_FILE "neofilter_dsp.dylib"
#else
#define DSP_FILE "neofilter_dsp.so"
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
const void* neo_entry_get_factory(const char* id) { return p_factory ? p_factory(id) : NULL; }

