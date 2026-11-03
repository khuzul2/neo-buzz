/* neoplug (H0 spike, docs/specs/plugin-host.md §6): a small C ABI over VST3 and CLAP hosting.
 * The managed machine, the probe and the GUI spike call only this. Threads: np_process on the
 * audio thread; everything else on the thread that opened the plugin (the "main" thread).
 * Throwaway code: H1 replaces it with the real library and protocol. MIT. */
#ifndef NEOPLUG_H
#define NEOPLUG_H

#include <stdint.h>

#if defined(_WIN32)
#define NP_API __declspec(dllexport)
#else
#define NP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum { NP_FORMAT_VST3 = 1, NP_FORMAT_CLAP = 2 };

typedef struct np_class_info {
    int32_t format;           /* NP_FORMAT_* */
    int32_t is_instrument;    /* takes notes and has no main audio input */
    char id[128];             /* CLAP id, or the VST3 class UID as 32 hex digits */
    char name[128];
    char vendor[128];
    char version[64];
    char category[192];       /* VST3 subcategories, or CLAP features joined with '|' */
} np_class_info;

enum {
    NP_PARAM_AUTOMATABLE = 1,
    NP_PARAM_STEPPED = 2,
    NP_PARAM_READONLY = 4,
    NP_PARAM_HIDDEN = 8,
    NP_PARAM_BYPASS = 16
};

typedef struct np_param_info {
    uint32_t id;              /* VST3 ParamID or CLAP clap_id: stable across versions */
    int32_t flags;            /* NP_PARAM_* */
    int32_t steps;            /* 0 = continuous */
    double min, max, def;     /* VST3: 0..1 normalized; CLAP: plain values */
    char name[128];
    char units[32];
    char module[128];         /* CLAP module path or VST3 unit id */
} np_param_info;

enum { NP_EV_NOTE_ON = 1, NP_EV_NOTE_OFF = 2, NP_EV_PARAM = 3 };

typedef struct np_event {
    uint32_t frame;           /* offset in the block */
    int32_t type;             /* NP_EV_* */
    int32_t channel;          /* 0..15 */
    int32_t key;              /* 0..127 */
    int32_t note_id;          /* -1 = none */
    uint32_t param_id;
    double value;             /* velocity 0..1, or the parameter value */
} np_event;

typedef struct np_plugin np_plugin;

/* Lists the classes of a module (.vst3 bundle or .clap). Returns the count (may exceed cap), or -1. */
NP_API int np_list(const char* path, np_class_info* out, int cap, char* err, int errlen);

/* Loads one class (id NULL or "" = the first one). NULL on failure, with err filled. */
NP_API np_plugin* np_open(const char* path, const char* id, char* err, int errlen);
NP_API void np_close(np_plugin* p);
NP_API int np_info(np_plugin* p, np_class_info* out);

/* Activates for a rate and a maximum block. offline != 0 asks for offline rendering. */
NP_API int np_activate(np_plugin* p, double rate, int max_frames, int offline, char* err, int errlen);
NP_API void np_deactivate(np_plugin* p);
NP_API int np_channels(np_plugin* p, int input); /* main bus channels (0 if none) */

NP_API int np_param_count(np_plugin* p);
NP_API int np_param_info_at(np_plugin* p, int index, np_param_info* out);
NP_API double np_param_get(np_plugin* p, uint32_t id);
NP_API int np_param_text(np_plugin* p, uint32_t id, double value, char* buf, int len);
NP_API int np_param_from_text(np_plugin* p, uint32_t id, const char* text, double* value);
/* Main thread: sets a parameter; reaches the processor at the next np_process (or now when inactive). */
NP_API int np_param_set(np_plugin* p, uint32_t id, double value);

/* Audio thread. in/out: planar float channels. Returns 1 if the output may be non-silent, 0 if
 * silent, <0 on error. Events must be sorted by frame. */
NP_API int np_process(np_plugin* p, const float* const* in, int in_ch, float* const* out, int out_ch,
                      int frames, const np_event* events, int nevents, double tempo, int playing);

/* State: the plugin's own blobs in a small envelope. np_state_save returns the size needed; it
 * writes only when cap is large enough. */
NP_API int np_state_save(np_plugin* p, uint8_t* buf, int cap);
NP_API int np_state_load(np_plugin* p, const uint8_t* buf, int size, char* err, int errlen);

NP_API int np_latency(np_plugin* p);
NP_API int np_tail(np_plugin* p);   /* samples; -1 = infinite */
NP_API void np_idle(np_plugin* p);  /* main thread: plugin callbacks, parameter echo to the controller */

/* GUI. np_gui_support: 1 = can embed in the given API ("nsview", "hwnd", "x11"), 2 = can float. */
NP_API int np_gui_support(np_plugin* p, const char* api);
NP_API int np_gui_attach(np_plugin* p, void* parent, const char* api, int* width, int* height, char* err, int errlen);
NP_API void np_gui_detach(np_plugin* p);

/* Platform helpers for the spike (macOS: NSView / NSWindow; Windows: HWND). */
NP_API void* np_platform_container(void* parent, int width, int height); /* a child view to host a plugin view */
NP_API void* np_platform_window(const char* title, int width, int height); /* a floating window's content view */
NP_API int np_platform_describe(void* view, char* buf, int len);           /* the view tree under it, for checks */
/* Renders the view (and, on macOS, its window) into an image at path (PNG on macOS, BMP on
 * Windows; NULL = no file) and writes stats as JSON: size, distinct colours, permille of pixels
 * within 24 of rgb. Returns 0 on success. */
NP_API int np_platform_snapshot(void* view, const char* path, int rgb, char* stats, int len);
NP_API void np_platform_run(double seconds);                               /* runs the platform event loop */
NP_API void np_platform_release(void* handle);

NP_API const char* np_version(void);

#ifdef __cplusplus
}
#endif
#endif
