// AW2 augmentation (docs/specs/neo-audio.md, AW2 as built): warps mono audio along a tempo curve with Signalsmith Stretch (MIT), the library behind the app's Pro
// warp (NeoStretch/), and transposes it. The labels of the audio are mapped through the same curve by the caller from the map this writes.
//
//   aw2warp IN.f32 OUT.f32 CURVE.txt MAP.txt RATE SEMITONES
//
// IN and OUT are raw little-endian float32, mono. CURVE.txt: lines "t r": at input time t (seconds) the stretch factor is r (output seconds per input second: 2 is
// half speed), linear between lines, held after the last. MAP.txt gets "t_in t_out" (seconds) at every block: the audio at input time t_in is at output time t_out.
// Pure and offline: nothing here is in the app. Build: g++ -O2 -std=c++17 -I NeoStretch/third_party warp.cpp -o aw2warp
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <vector>
#include <algorithm>
#include "signalsmith-stretch/signalsmith-stretch.h"

struct Ptr { const float *p; const float *operator[](int) const { return p; } };
struct OPtr { float *p; float *operator[](int) { return p; } };

int main(int argc, char **argv) {
	if (argc < 7) { fprintf(stderr, "usage: aw2warp IN OUT CURVE MAP RATE SEMITONES\n"); return 2; }
	int rate = atoi(argv[5]);
	double semi = atof(argv[6]);
	FILE *f = fopen(argv[1], "rb");
	if (!f) return 3;
	fseek(f, 0, SEEK_END); long bytes = ftell(f); fseek(f, 0, SEEK_SET);
	std::vector<float> in(bytes / 4);
	if (fread(in.data(), 4, in.size(), f) != in.size()) return 3;
	fclose(f);
	std::vector<double> ct, cr;
	FILE *c = fopen(argv[3], "r");
	if (!c) return 3;
	double a, b;
	while (fscanf(c, "%lf %lf", &a, &b) == 2) { ct.push_back(a); cr.push_back(b); }
	fclose(c);
	if (ct.empty()) { ct.push_back(0); cr.push_back(1); }
	auto factor = [&](double t) {
		if (t <= ct[0]) return cr[0];
		for (size_t i = 1; i < ct.size(); i++)
			if (t <= ct[i]) return cr[i - 1] + (cr[i] - cr[i - 1]) * (t - ct[i - 1]) / std::max(1e-9, ct[i] - ct[i - 1]);
		return cr.back();
	};
	signalsmith::stretch::SignalsmithStretch<float> st(12345);
	st.presetDefault(1, (float)rate);
	st.setTransposeSemitones((float)semi);
	const int B = 512;
	long N = (long)in.size();
	double q0 = 1.0 / factor(0);
	int L = st.outputSeekLength(q0);
	std::vector<float> pad(std::max<long>(L, 1), 0.f);
	for (int i = 0; i < L && i < N; i++) pad[i] = in[i];
	Ptr pin{pad.data()};
	st.outputSeek(pin, L);
	double pos = (double)L;              // input samples consumed
	double aligned = 0;                  // the input position the next output sample is
	std::vector<float> out;
	out.reserve((size_t)(N * 2));
	FILE *m = fopen(argv[4], "w");
	std::vector<float> ob(B);
	std::vector<float> ib;
	double acc = 0;
	while (pos < N) {
		double r = factor(aligned / rate);
		double q = 1.0 / r;                              // input samples per output sample
		acc += q * B;
		int n = (int)std::floor(acc);
		acc -= n;
		ib.assign(std::max(n, 1), 0.f);
		for (int i = 0; i < n; i++) { long k = (long)pos + i; ib[i] = k < N ? in[k] : 0.f; }
		fprintf(m, "%.6f %.6f\n", aligned / rate, (double)out.size() / rate);
		Ptr pi{ib.data()}; OPtr po{ob.data()};
		st.process(pi, n, po, B);
		out.insert(out.end(), ob.begin(), ob.end());
		pos += n; aligned += q * B;
	}
	fprintf(m, "%.6f %.6f\n", aligned / rate, (double)out.size() / rate);
	fclose(m);
	FILE *o = fopen(argv[2], "wb");
	fwrite(out.data(), 4, out.size(), o);
	fclose(o);
	return 0;
}
