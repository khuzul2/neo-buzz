// The plugin ABI shared by the NativeAOT plugin (plugin/Exports.cs) and the control plugin (control.cpp).
// Every function is a plain C export. np_gc_* and np_load_* are optional: the host treats missing ones as 0.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
// kind: 0 = Neo Poly (synth, ignores inputs), 1 = Neo Drive (effect). Returns an instance handle (>= 0).
typedef int (*np_create_t)(int kind, float sampleRate);
typedef void (*np_param_t)(int h, int index, int raw);            // raw parameter value (engine units)
typedef void (*np_note_on_t)(int h, int note, float velocity);
typedef void (*np_note_off_t)(int h, int note);
typedef void (*np_process_t)(int h, const float* inL, const float* inR, float* outL, float* outR, int n);
typedef int (*np_gc_count_t)(void);                                // GC.CollectionCount(0): every GC
typedef int (*np_gc_count_gen_t)(int gen);
typedef int64_t (*np_gc_pause_ns_t)(void);                         // GC.GetTotalPauseDuration
typedef int64_t (*np_gc_max_pause_ns_t)(void);                     // longest pause a 1 ms sampler saw
typedef void (*np_gc_max_pause_reset_t)(void);
typedef int64_t (*np_alloc_bytes_t)(void);
typedef void (*np_load_start_t)(int mode);                         // 0 none, 1 = 1 MB/s, 2 = 20 MB/s, 3 = 20 MB/s + 256 MB live gen2 churn
typedef void (*np_load_stop_t)(void);
typedef void (*np_set_latency_t)(int sustained);
typedef int (*np_gc_config_t)(char* buf, int cap);

// Parameter indices the host automates every block.
enum { POLY_UNIDETUNE = 3, POLY_F1CUTOFF = 34, DRIVE_DRIVE = 1, DRIVE_TONE = 5, DRIVE_QUALITY = 10 };
#ifdef __cplusplus
}
#endif
