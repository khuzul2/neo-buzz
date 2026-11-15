/* neoplug (docs/specs/plugin-host.md §6, phase H1): a small C ABI over VST3 and CLAP hosting.
 * Neo Plug, Neo Plug FX, the probe and the scanner call only this. Threads: np_process_ex on the
 * audio thread (no allocation, no locks); everything else on the thread that opened the plugin
 * (the "main" thread). MIT. */
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

enum {
    NP_EV_NOTE_ON = 1,          /* channel, key, note_id (-1 = none), value = velocity 0..1 */
    NP_EV_NOTE_OFF = 2,         /* channel, key, note_id, value = release velocity 0..1 */
    NP_EV_PARAM = 3,            /* param_id, value (the parameter's own range) */
    NP_EV_CC = 4,               /* channel, key = controller 0..127, value 0..1 */
    NP_EV_PITCH_BEND = 5,       /* channel, value -1..1 */
    NP_EV_CHANNEL_PRESSURE = 6, /* channel, value 0..1 */
    NP_EV_POLY_PRESSURE = 7,    /* channel, key, value 0..1 */
    NP_EV_NOTE_EXPR = 8,        /* a playing note's expression: note_id, channel, key, param_id = NP_EXPR_*, value */
    NP_EV_ALL_NOTES_OFF = 9     /* note-offs for every note the library has sent and not ended */
};

/* Note expressions (the numbers of CLAP's; VST3's Volume, Pan, Tuning, Vibrato, Expression and Brightness match). */
enum {
    NP_EXPR_VOLUME = 0,         /* a gain, 0..4 (1 = unchanged) */
    NP_EXPR_PAN = 1,            /* 0..1, 0.5 = centre */
    NP_EXPR_TUNING = 2,         /* semitones */
    NP_EXPR_VIBRATO = 3,        /* 0..1 */
    NP_EXPR_EXPRESSION = 4,     /* 0..1 */
    NP_EXPR_BRIGHTNESS = 5,     /* 0..1 */
    NP_EXPR_PRESSURE = 6        /* 0..1 */
};

typedef struct np_event {
    uint32_t frame;           /* offset in the block */
    int32_t type;             /* NP_EV_* */
    int32_t channel;          /* 0..15 */
    int32_t key;              /* 0..127 */
    int32_t note_id;          /* -1 = none */
    uint32_t param_id;
    double value;             /* velocity 0..1, or the parameter value */
} np_event;

/* One block. in/out are planar float channels; the main bus is folded to the channels given (a mono
 * plugin gets the mean of in[0] and in[1] and its output is copied to both outputs; a bus of more than
 * two channels gives its first two). aux is the sidechain (the plugin's second audio input); NULL or
 * aux_ch 0 feeds it silence. events must be sorted by frame. out_events (may be NULL) receives the
 * plugin's note and MIDI output; out_count is set to how many were written (at most out_cap).
 * beat_pos is the song position in quarter notes (< 0: unknown). */
typedef struct np_process_io {
    const float* const* in; int32_t in_ch;
    const float* const* aux; int32_t aux_ch;
    float* const* out; int32_t out_ch;
    int32_t frames;
    const np_event* events; int32_t nevents;
    np_event* out_events; int32_t out_cap; int32_t out_count;
    double tempo; int32_t playing;
    double beat_pos;
    int32_t tsig_num, tsig_den;
} np_process_io;

enum {
    NP_POLL_LATENCY = 1,      /* the latency changed: read np_latency */
    NP_POLL_PARAMS = 2,       /* parameter names, ranges or the list changed: rescan */
    NP_POLL_RESTART = 4,      /* the plugin asks to be deactivated and activated again (ports changed) */
    NP_POLL_DIRTY = 8         /* the plugin's state changed */
};

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

/* Audio thread. Returns 1 if the output may be non-silent, 0 if silent, <0 on error. */
NP_API int np_process_ex(np_plugin* p, np_process_io* io);
NP_API int np_process(np_plugin* p, const float* const* in, int in_ch, float* const* out, int out_ch,
                      int frames, const np_event* events, int nevents, double tempo, int playing);

/* Main thread. The sidechain's channel count (the plugin's second audio input), 0 when it has none. */
NP_API int np_aux_channels(np_plugin* p);
/* Main thread. What the plugin asked since the last call (NP_POLL_*). */
NP_API int np_poll(np_plugin* p);
/* The plain value of a parameter (VST3: its normalized value converted; CLAP: the value itself). */
NP_API double np_param_plain(np_plugin* p, uint32_t id, double value);
/* Main thread. Asks the plugin to forget its voices (a deactivate and activate would do too). */
NP_API void np_reset(np_plugin* p);

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

/* GUI, ABI 3 (docs/specs/plugin-host.md H3). Every np_gui_* call comes from the thread that shows the GUI (Avalonia's UI thread);
 * the library counts that thread as the plugin's main thread while a GUI is attached. Sizes are physical pixels on Windows and points
 * on macOS. The plugin's own resize requests (VST3 IPlugFrame::resizeView, CLAP request_resize) come to resize_cb on that thread:
 * the host sizes its container and window to (width, height) there; the library tells the plugin when the callback returns. */
typedef void (*np_gui_resize_cb)(void* user, int width, int height);
typedef void (*np_gui_closed_cb)(void* user);   /* the plugin asks for its window to close (CLAP gui.closed) */
NP_API void np_gui_set_callbacks(np_plugin* p, np_gui_resize_cb resize_cb, np_gui_closed_cb closed_cb, void* user);
NP_API int np_gui_size(np_plugin* p, int* width, int* height);          /* the view's current size; 0 = ok */
NP_API int np_gui_can_resize(np_plugin* p);                              /* 1 if the user may resize the view */
/* Host-initiated resize: in = wanted size, out = the size the plugin took (it may adjust to its constraints); 0 = ok. */
NP_API int np_gui_set_size(np_plugin* p, int* width, int* height);
/* The display scale (1.0 = 96 dpi); call before np_gui_attach. Windows only: macOS views are in points. */
NP_API int np_gui_set_scale(np_plugin* p, double scale);

/* Audio outputs, ABI 3: a plugin's output buses (VST3) or ports (CLAP). np_process_ex's out takes the selected one (default 0, the main). */
NP_API int np_out_count(np_plugin* p);
NP_API int np_out_info(np_plugin* p, int index, char* name, int len, int* channels);
NP_API void np_out_select(np_plugin* p, int index);   /* any thread; takes effect at the next block */

/* Platform helpers for the spike (macOS: NSView / NSWindow; Windows: HWND). */
NP_API void* np_platform_container(void* parent, int width, int height); /* a child view to host a plugin view */
NP_API void np_platform_resize(void* view, int width, int height);         /* sets a container's size */
NP_API void* np_platform_window(const char* title, int width, int height); /* a floating window's content view */
NP_API int np_platform_describe(void* view, char* buf, int len);           /* the view tree under it, for checks */
/* Renders the view (and, on macOS, its window) into an image at path (PNG on macOS, BMP on
 * Windows; NULL = no file) and writes stats as JSON: size, distinct colours, permille of pixels
 * within 24 of rgb. Returns 0 on success. */
NP_API int np_platform_snapshot(void* view, const char* path, int rgb, char* stats, int len);
NP_API void np_platform_run(double seconds);                               /* runs the platform event loop */
NP_API void np_platform_release(void* handle);
/* Floating windows (from np_platform_window): sets the window's client size; and a callback for its close button, which then only
 * reports (the window stays until np_platform_release). */
NP_API void np_platform_window_size(void* content, int width, int height);
NP_API void np_platform_on_close(void* content, void (*cb)(void*), void* user);

NP_API const char* np_version(void);
/* The library's ABI number (managed callers refuse a different one). */
#define NP_ABI 3
NP_API int np_abi(void);

#ifdef __cplusplus
}
#endif
#endif
