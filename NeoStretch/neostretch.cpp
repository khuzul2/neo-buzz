// NeoStretch: see neostretch.h. A thin, exception-proof shell over Signalsmith Stretch (MIT, third_party/).
#include "neostretch.h"

#include <signalsmith-stretch/signalsmith-stretch.h>

#include <cmath>
#include <new>

#if defined(__SSE__) || defined(_M_X64) || defined(__x86_64__)
#  include <xmmintrin.h>
#  define NS_X86 1
#elif defined(__aarch64__)
#  include <cstdint>
#  define NS_ARM64 1
#endif

namespace {

// Subnormals as zero for the length of a call (they cost 100 times a normal number, and they come from the decay of a
// filter-like tail): the control register is put back as it was.
struct FlushDenormals {
#if defined(NS_X86)
    unsigned int saved;
    FlushDenormals() : saved(_mm_getcsr()) { _mm_setcsr(saved | 0x8040u); }       // FTZ | DAZ
    ~FlushDenormals() { _mm_setcsr(saved); }
#elif defined(NS_ARM64) && (defined(__GNUC__) || defined(__clang__))
    std::uint64_t saved;
    FlushDenormals() {
        asm volatile("mrs %0, fpcr" : "=r"(saved));
        std::uint64_t fz = saved | (1ull << 24);                                  // FZ
        asm volatile("msr fpcr, %0" : : "r"(fz));
    }
    ~FlushDenormals() { asm volatile("msr fpcr, %0" : : "r"(saved)); }
#else
    FlushDenormals() {}
#endif
};

constexpr int kMaxChannels = 2;

struct Instance {
    signalsmith::stretch::SignalsmithStretch<float> stretch{12345L};          // seeded: the same input gives the same output
    int channels = 1;
};

struct In {
    const float* c[kMaxChannels];
    const float* operator[](int i) const { return c[i]; }
};
struct Out {
    float* c[kMaxChannels];
    float* operator[](int i) const { return c[i]; }
};

Instance* Cast(void* h) { return static_cast<Instance*>(h); }

}  // namespace

NS_API int ns_abi_version(void) { return 1; }

NS_API const char* ns_version(void) { return "neostretch 1; signalsmith-stretch 1.3.2, signalsmith-linear 0.6.6 (MIT)"; }

NS_API void* ns_create(int channels, int block, int interval, int split) {
    if (channels < 1 || channels > kMaxChannels || block < 64 || block > (1 << 20) || interval < 16 || interval > block) return nullptr;
    try {
        auto* p = new (std::nothrow) Instance();
        if (!p) return nullptr;
        p->channels = channels;
        p->stretch.configure(channels, block, interval, split != 0);
        p->stretch.reset();
        return p;
    } catch (...) {
        return nullptr;
    }
}

NS_API void ns_destroy(void* h) {
    try {
        delete Cast(h);
    } catch (...) {
    }
}

NS_API void ns_reset(void* h) {
    if (!h) return;
    try {
        FlushDenormals ftz;
        Cast(h)->stretch.reset();
    } catch (...) {
    }
}

NS_API int ns_input_latency(void* h) { return h ? Cast(h)->stretch.inputLatency() : -1; }
NS_API int ns_output_latency(void* h) { return h ? Cast(h)->stretch.outputLatency() : -1; }

NS_API int ns_set_transpose(void* h, double factor, double tonality_limit) {
    if (!h) return -1;
    if (!std::isfinite(factor) || factor <= 0) factor = 1;
    if (factor < 1.0 / 32) factor = 1.0 / 32;
    if (factor > 32) factor = 32;
    if (!std::isfinite(tonality_limit) || tonality_limit < 0 || tonality_limit > 0.5) tonality_limit = 0;
    try {
        Cast(h)->stretch.setTransposeFactor(static_cast<float>(factor), static_cast<float>(tonality_limit));
        return 0;
    } catch (...) {
        return -2;
    }
}

NS_API int ns_output_seek(void* h, const float* left, const float* right, int input_length) {
    if (!h || !left || input_length < 0) return -1;
    auto* p = Cast(h);
    if (p->channels > 1 && !right) return -1;
    if (input_length < p->stretch.inputLatency()) return -1;
    try {
        FlushDenormals ftz;
        In in{{left, right ? right : left}};
        p->stretch.outputSeek(in, input_length);
        return 0;
    } catch (...) {
        return -2;
    }
}

NS_API int ns_process(void* h, const float* in_left, const float* in_right, int input_samples,
                      float* out_left, float* out_right, int output_samples) {
    if (!h || !out_left || input_samples < 0 || output_samples < 0) return -1;
    auto* p = Cast(h);
    if (p->channels > 1 && !out_right) return -1;
    if (input_samples > 0 && (!in_left || (p->channels > 1 && !in_right))) return -1;
    if (output_samples == 0) return 0;
    try {
        FlushDenormals ftz;
        static const float kZero[1] = {0.0f};
        In in{{in_left ? in_left : kZero, in_right ? in_right : (in_left ? in_left : kZero)}};
        Out out{{out_left, out_right ? out_right : out_left}};
        p->stretch.process(in, input_samples, out, output_samples);
        return 0;
    } catch (...) {
        return -2;
    }
}
