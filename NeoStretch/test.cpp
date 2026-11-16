// The library's own test (ctest): the ABI's edges, no allocation in the audio-thread calls, clean output, and alignment.
#include "neostretch.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <new>
#include <vector>

static long g_news = 0;
static bool g_count = false;
void* operator new(std::size_t n) {
    if (g_count) ++g_news;
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](std::size_t n) {
    if (g_count) ++g_news;
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }

static int g_failed = 0;
#define CHECK(cond, ...) do { if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s  ", __FILE__, __LINE__, #cond); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)

static const int kRate = 48000, kBlock = 5760, kInterval = 1440;

// A run like the managed voice's: a seek at the start of `in`, then chunks of `chunk` output samples, the input following
// at `rate` input samples per output sample with the lookahead the latencies ask for.
static std::vector<float> Run(void* h, const std::vector<float>& in, double rate, int nOut, int chunk, double factor) {
    int inLat = ns_input_latency(h), outLat = ns_output_latency(h);
    ns_set_transpose(h, factor, 0);
    auto target = [&](long t) { return (long)std::llround(rate * (t + outLat)) + inLat; };
    auto at = [&](long i) { return i >= 0 && i < (long)in.size() ? in[i] : 0.0f; };
    long start = target(0);
    std::vector<float> seek(start);
    for (long i = 0; i < start; ++i) seek[i] = at(i);
    CHECK(ns_output_seek(h, seek.data(), seek.data(), (int)start) == 0, "seek");
    std::vector<float> out(nOut), tmp, scratch;
    long inPos = start;
    for (int t = 0; t < nOut; t += chunk) {
        int n = std::min(chunk, nOut - t);
        long end = target(t + n);
        int nIn = (int)std::max<long>(0, end - inPos);
        scratch.assign(nIn, 0.0f);
        for (int i = 0; i < nIn; ++i) scratch[i] = at(inPos + i);
        CHECK(ns_process(h, scratch.data(), scratch.data(), nIn, out.data() + t, out.data() + t, n) == 0, "process");
        inPos += nIn;
    }
    return out;
}

int main() {
    CHECK(ns_abi_version() == 1, "abi");
    CHECK(ns_create(0, kBlock, kInterval, 0) == nullptr, "0 channels");
    CHECK(ns_create(3, kBlock, kInterval, 0) == nullptr, "3 channels");
    CHECK(ns_create(1, 10, 5, 0) == nullptr, "tiny block");
    CHECK(ns_create(1, kBlock, kBlock + 1, 0) == nullptr, "interval over block");
    CHECK(ns_process(nullptr, nullptr, nullptr, 0, nullptr, nullptr, 0) < 0, "null handle");

    void* h = ns_create(2, kBlock, kInterval, 1);
    CHECK(h != nullptr, "create");
    if (!h) return 1;
    int inLat = ns_input_latency(h), outLat = ns_output_latency(h);
    std::printf("latency: input %d, output %d samples at %d Hz (block %d, interval %d, split)\n", inLat, outLat, kRate, kBlock, kInterval);
    CHECK(ns_output_seek(h, nullptr, nullptr, 10) < 0, "seek without audio");
    std::vector<float> small(10);
    CHECK(ns_output_seek(h, small.data(), small.data(), 10) < 0, "seek shorter than the latency");

    // Noise, silence, a loud click train, at several ratios and pitches, in chunks of several sizes: nothing allocates, nothing is
    // NaN, infinite or subnormal.
    std::vector<float> noise(kRate * 6);
    unsigned s = 1;
    for (auto& x : noise) { s = s * 1664525u + 1013904223u; x = ((s >> 8) / 8388608.0f - 1.0f) * 0.5f; }
    for (size_t i = 0; i < noise.size(); i += 12000) for (size_t j = 0; j < 300 && i + j < noise.size(); ++j) noise[i + j] *= 20.0f;   // loud bursts
    for (size_t i = kRate * 3; i < (size_t)kRate * 4; ++i) noise[i] = 0.0f;                                                           // a second of silence
    long bad = 0;
    struct Case { double rate, factor; int chunk; };
    const Case cases[] = {{1, 1, 64}, {0.8, 1, 100}, {1.25, 1, 256}, {0.5, 1.5, 17}, {2, 0.5, 512}, {8, 1, 64}, {0.1, 2, 300}, {1, 1.4983, 1}};
    for (const auto& c : cases) {
        std::vector<float> out;
        g_news = 0;
        // Run() allocates its own vectors, so the count is taken in a dedicated loop below; here only the output is checked.
        out = Run(h, noise, c.rate, kRate * 2, c.chunk, c.factor);
        for (float v : out) if (!std::isfinite(v) || std::fpclassify(v) == FP_SUBNORMAL) ++bad;
    }
    CHECK(bad == 0, "%ld non-finite or subnormal output samples", bad);

    // No allocation: the calls only.
    {
        std::vector<float> in(4096 * 8), l(512), r(512);
        for (size_t i = 0; i < in.size(); ++i) in[i] = std::sin(i * 0.05f) * 0.3f;
        g_news = 0;
        g_count = true;
        long calls = 0;
        for (int round = 0; round < 40; ++round) {
            ns_set_transpose(h, 1.0 + 0.05 * (round % 7), 8000.0 / kRate);
            int seekLen = inLat + 3000;
            if (round % 5 == 0) ns_output_seek(h, in.data(), in.data(), seekLen), ++calls;
            for (int k = 0; k < 20; ++k) {
                int nOut = 1 + (round * 37 + k * 91) % 512, nIn = (round * 13 + k * 29) % 900 + 1;
                ns_process(h, in.data(), in.data() + 1, nIn, l.data(), r.data(), nOut);
                ++calls;
            }
        }
        g_count = false;
        std::printf("%ld calls, %ld allocations\n", calls, g_news);
        CHECK(g_news == 0, "the audio-thread calls allocated %ld times", g_news);
    }

    // Alignment: an impulse train (a decaying noise burst, like a hit) at rate 1 and at other rates comes out where it belongs.
    {
        int hitAt = 20000;
        std::vector<float> in(kRate * 3, 0.0f);
        unsigned q = 7;
        for (int i = 0; i < 2400; ++i) { q = q * 1664525u + 1013904223u; in[hitAt + i] = ((q >> 8) / 8388608.0f - 1.0f) * std::exp(-i / 300.0f); }
        for (double rate : {1.0, 0.5, 2.0}) {
            int nOut = (int)(kRate * 1.5);
            auto out = Run(h, in, rate, nOut, 128, 1.0);
            double expect = hitAt / rate;
            // the start of the hit: the first sample over a tenth of the peak
            float peak = 0;
            for (float v : out) peak = std::max(peak, std::fabs(v));
            long first = -1;
            for (long i = 0; i < nOut; ++i) if (std::fabs(out[i]) > peak * 0.1f) { first = i; break; }
            std::printf("rate %.2f: the hit starts at %ld, expected %.0f (%.2f ms off), peak %.2f\n", rate, first, expect, (first - expect) * 1000.0 / kRate, peak);
            // Unstretched, to the sample; stretched, a hit smears over a window of the phase vocoder, so its start is found a few ms early.
            double tol = rate == 1.0 ? kRate / 1000.0 * 0.5 : kRate / 1000.0 * 10;
            CHECK(first >= 0 && std::fabs(first - expect) <= tol, "hit at %ld, expected %.0f", first, expect);
        }
    }

    ns_destroy(h);
    std::printf(g_failed ? "FAILED\n" : "neostretch-test: ok\n");
    return g_failed ? 1 : 0;
}
