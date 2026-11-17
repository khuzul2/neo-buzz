// NeoStretch (docs/specs/neo-audio.md section 11): the C ABI of the "Pro" warp mode. One instance is one stretcher of
// one or two channels. Everything that allocates (ns_create, ns_destroy) belongs off the audio thread; the others never
// allocate, lock or throw, and are safe on it. Functions return 0 or a value >= 0 on success, negative on a bad argument.
#ifndef NEOSTRETCH_H
#define NEOSTRETCH_H

#if defined(_WIN32) && !defined(NS_STATIC)
#  define NS_API extern "C" __declspec(dllexport)
#elif !defined(NS_STATIC)
#  define NS_API extern "C" __attribute__((visibility("default")))
#else
#  define NS_API extern "C"
#endif

/// The ABI's version (1). The managed side refuses a library with another.
NS_API int ns_abi_version(void);
/// A static text naming what the library is built from.
NS_API const char* ns_version(void);

/// A stretcher with an STFT block of `block` samples and a hop of `interval` samples (`split`: one more interval of
/// latency for an even load). NULL when the arguments are out of range or memory is out.
NS_API void* ns_create(int channels, int block, int interval, int split);
NS_API void ns_destroy(void* h);
/// The state of a fresh instance again (what ns_output_seek does first).
NS_API void ns_reset(void* h);

NS_API int ns_input_latency(void* h);
NS_API int ns_output_latency(void* h);

/// The pitch factor (frequency multiplier) and the tonality limit (a multiple of the sample rate; 0 for none), for the
/// next samples. A non-finite factor is 1; the factor is held within 1/32 to 32.
NS_API int ns_set_transpose(void* h, double factor, double tonality_limit);

/// A start at the first sample of `in` (right is ignored for one channel; it may equal left): the state is reset and the
/// next ns_process continues at `in + inputLength`, its first output aligned to the first input sample. `inputLength`
/// is at least ns_input_latency(); the rate is what the stretcher gets from it (see the library's outputSeek).
NS_API int ns_output_seek(void* h, const float* left, const float* right, int input_length);

/// `input_samples` in, `output_samples` out: the time-stretch is their ratio (any, but not zero input forever).
NS_API int ns_process(void* h, const float* in_left, const float* in_right, int input_samples,
                      float* out_left, float* out_right, int output_samples);

#endif
