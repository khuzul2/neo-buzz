#nullable disable
// Neo Amp (docs/specs/neo-amp.md §5): the cabinets, made by our own code and no recording. Each is a target magnitude response built from
// analog sections evaluated at jω on the FFT grid:
//   - the speaker's low resonance, a second-order high-pass (frequency, Q), plus the cabinet's own bump or ripple (peaks);
//   - the cone's high-frequency roll-off: a second-order low-pass and a first-order one;
//   - the cone-breakup peaks and notches of the mid and upper range (second-order peaking sections, gains in dB).
// The magnitude becomes a minimum-phase impulse response by the real cepstrum (take the log magnitude, fold the cepstrum onto the
// causal half, exponentiate: Oppenheim and Schafer, Discrete-Time Signal Processing, the chapter on the cepstrum and homomorphic
// systems), is cut to at most 50 ms with a raised-cosine fade, and is scaled so that its gain on a pink spectrum (equal energy per
// octave, 100 Hz to 5 kHz) is 1: the cabinets sit at the same level. A wave of the song becomes a cabinet the same way (mono, its
// lead of silence skipped, its DC removed, cut to 250 ms, the same level).
//
// Built off the audio thread; portable maths only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Amp
{
    internal static class AmpCabs
    {
        /// <summary>The Cabinet choices: 0 Off, 1 … 6 the built-in cabinets, 7 the song's wave.</summary>
        public const int Off = 0, FirstBuiltIn = 1, LastBuiltIn = 6, WaveCab = 7, Fallback = 3;

        public static readonly string[] Names = { "Off", "Open 1x12", "Closed 2x12", "Closed 4x12", "Bass 1x15", "Bass 4x10", "Bass 8x10", "Wave" };

        /// <summary>The longest a built-in cabinet's response is, seconds.</summary>
        public const double MaxSeconds = 0.05;

        /// <summary>The longest a wave used as the cabinet is read, seconds.</summary>
        public const double MaxWaveSeconds = 0.25;

        sealed class Spec
        {
            public double HpHz, HpQ;                // speaker resonance
            public double Lp2Hz, Lp2Q, Lp1Hz;       // cone roll-off
            public double[] Peaks;                  // (Hz, dB, Q) triples
            public double TiltDb;                   // dB per octave of an overall slope above 1 kHz (negative: darker), 0 for none
        }

        static readonly Spec[] Specs =
        {
            null,
            // Open-back 1x12: thin lows (a high resonance, no box), a wide bump in the upper mids, an airy top with little cut.
            new Spec { HpHz = 115, HpQ = 1.1, Lp2Hz = 5600, Lp2Q = 0.8, Lp1Hz = 9500,
                Peaks = new[] { 190.0, 2.5, 1.2,   900, -2.0, 1.0,   2100, 5.0, 1.6,   3300, 3.0, 2.5,   4900, -5.0, 3.0 } },
            // Closed 2x12: more low end from the box, a ripple in the low mids from two cones, firm upper mids.
            new Spec { HpHz = 98, HpQ = 1.0, Lp2Hz = 5200, Lp2Q = 0.8, Lp1Hz = 8500,
                Peaks = new[] { 120.0, 3.0, 1.4,   420, -2.5, 1.5,   1250, -3.0, 2.0,   2300, 5.5, 1.8,   3700, 3.5, 2.5,   5600, -6.0, 3.0 } },
            // Closed 4x12: deep and tight, a scooped low-mid, a hard peak in the upper mids, a steep roll-off.
            new Spec { HpHz = 84, HpQ = 1.15, Lp2Hz = 4700, Lp2Q = 0.9, Lp1Hz = 6500,
                Peaks = new[] { 105.0, 4.0, 1.3,   380, -3.0, 1.2,   800, -2.0, 1.5,   2500, 6.0, 1.9,   3800, 3.0, 2.4,   6200, -8.0, 3.5 } },
            // Bass 1x15: a deep, round bottom, a dark top, a broad mid hump.
            new Spec { HpHz = 46, HpQ = 0.9, Lp2Hz = 3300, Lp2Q = 0.8, Lp1Hz = 5000,
                Peaks = new[] { 72.0, 3.5, 1.2,   260, -2.0, 1.5,   900, 3.5, 1.2,   1700, 2.0, 2.0,   3500, -4.0, 3.0 } },
            // Bass 4x10: a firmer low end, a clear upper mid for definition and string noise, a higher cut-off.
            new Spec { HpHz = 55, HpQ = 1.0, Lp2Hz = 6200, Lp2Q = 0.8, Lp1Hz = 9000,
                Peaks = new[] { 95.0, 2.5, 1.3,   340, -3.0, 1.6,   1100, 2.5, 1.4,   3100, 4.5, 2.2,   7000, -5.0, 3.0 } },
            // Bass 8x10: a very deep, loud bottom, a scooped lower mid, a bright, wide top.
            new Spec { HpHz = 42, HpQ = 1.1, Lp2Hz = 5600, Lp2Q = 0.8, Lp1Hz = 8000,
                Peaks = new[] { 62.0, 4.5, 1.2,   310, -3.5, 1.4,   1500, 3.0, 1.6,   2700, 3.5, 2.2,   5200, -4.0, 3.0 } },
        };

        /// <summary>The built-in cabinet's magnitude response (linear, not levelled) at <paramref name="hz"/>.</summary>
        public static double Magnitude(int cab, double hz)
        {
            var s = Specs[cab];
            double w = hz;
            double g = HighPass2(w, s.HpHz, s.HpQ) * LowPass2(w, s.Lp2Hz, s.Lp2Q) * LowPass1(w, s.Lp1Hz);
            for (int i = 0; i < s.Peaks.Length; i += 3) g *= Peak(w, s.Peaks[i], s.Peaks[i + 1], s.Peaks[i + 2]);
            return g;
        }

        static double HighPass2(double f, double f0, double q)
        {
            double r = f / f0, r2 = r * r;
            double re = 1 - r2, im = r / q;   // |s² + s/Q + 1| in units of ω0 with s = jr: 1 − r² + j r/Q
            return r2 / Math.Sqrt(re * re + im * im);
        }

        static double LowPass2(double f, double f0, double q)
        {
            double r = f / f0, r2 = r * r;
            double re = 1 - r2, im = r / q;
            return 1.0 / Math.Sqrt(re * re + im * im);
        }

        static double LowPass1(double f, double f0) => 1.0 / Math.Sqrt(1 + (f / f0) * (f / f0));

        static double Peak(double f, double f0, double db, double q)
        {
            double a = PortableMath.Pow(10, db / 40);
            double r = f / f0, r2 = r * r;
            // H = (s² + s A/Q + 1) / (s² + s/(A Q) + 1), s = j r.
            double re = 1 - r2;
            double nim = r * a / q, dim = r / (a * q);
            return Math.Sqrt((re * re + nim * nim) / (re * re + dim * dim));
        }

        static int GridSize(double rate) => rate > 100000 ? 32768 : rate > 50000 ? 16384 : 8192;

        /// <summary>A built-in cabinet's impulse response at <paramref name="rate"/> (1 … 6), ≤ 50 ms, minimum phase, levelled.</summary>
        public static float[] Build(int cab, double rate)
        {
            int n = GridSize(rate);
            var mag = new double[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++)
            {
                double hz = Math.Max(1.0, k * rate / n);
                mag[k] = Math.Max(1e-6, Magnitude(cab, hz));
            }
            return Finish(MinimumPhase(mag, n), rate, AmpCabs.MaxSeconds);
        }

        /// <summary>A wave of the song as a cabinet (mono, at the host's rate), ≤ 250 ms, levelled.</summary>
        public static float[] FromWave(float[] left, float[] right, double waveRate, double rate)
        {
            int total = Math.Min(left.Length, right?.Length ?? left.Length);
            var mono = new float[total];
            double dc = 0;
            for (int i = 0; i < total; i++)
            {
                float v = right == null ? left[i] : 0.5f * (left[i] + right[i]);
                mono[i] = v;
                dc += v;
            }
            dc /= Math.Max(1, total);
            double peak = 0;
            for (int i = 0; i < total; i++) { mono[i] -= (float)dc; peak = Math.Max(peak, Math.Abs(mono[i])); }
            float[] x = Math.Abs(waveRate - rate) > 0.5 ? ImpulseResponses.Resample(mono, waveRate / rate) : mono;
            int start = 0;
            double floor = peak * 0.00316;
            while (start < x.Length && Math.Abs(x[start]) < floor) start++;
            start = Math.Max(0, start - (int)(0.0005 * rate));
            int len = Math.Min(x.Length - start, Math.Max(16, (int)(MaxWaveSeconds * rate)));
            var ir = new float[Math.Max(16, len)];
            Array.Copy(x, start, ir, 0, Math.Max(0, len));
            return Finish(ir, rate, MaxWaveSeconds, fromWave: true);
        }

        static double[] MinimumPhase(double[] mag, int n)
        {
            var re = new double[n];
            var im = new double[n];
            for (int k = 0; k <= n / 2; k++)
            {
                double l = PortableMath.Log(mag[k]);
                re[k] = l;
                if (k > 0 && k < n / 2) re[n - k] = l;
            }
            Fft.Transform(re, im, true);   // the real cepstrum
            var fr = new double[n];
            var fi = new double[n];
            fr[0] = re[0];
            for (int i = 1; i < n / 2; i++) fr[i] = 2 * re[i];
            fr[n / 2] = re[n / 2];
            Fft.Transform(fr, fi, false);
            for (int k = 0; k < n; k++)
            {
                double e = PortableMath.Exp(fr[k]);
                double a = fi[k];
                fr[k] = e * PortableMath.Cos(a);
                fi[k] = e * PortableMath.Sin(a);
            }
            Fft.Transform(fr, fi, true);
            return fr;
        }

        static float[] Finish(double[] h, double rate, double maxSeconds)
        {
            var f = new float[h.Length];
            for (int i = 0; i < h.Length; i++) f[i] = (float)h[i];
            return Finish(f, rate, maxSeconds);
        }

        static float[] Finish(float[] h, double rate, double maxSeconds, bool fromWave = false)
        {
            int len = Math.Min(h.Length, Math.Max(32, (int)Math.Round(maxSeconds * rate)));
            var ir = new float[len];
            Array.Copy(h, ir, len);
            // A raised-cosine fade over the last fifth (a wave: the last tenth, at least 5 ms).
            int fade = fromWave ? Math.Max((int)(0.005 * rate), len / 10) : len / 5;
            fade = Math.Min(fade, len / 2);
            for (int i = 0; i < fade; i++)
            {
                double w = 0.5 * (1 + PortableMath.Cos(Math.PI * (i + 1) / (fade + 1)));
                ir[len - fade + i] *= (float)w;
            }
            double norm = PinkGain(ir, rate);
            if (norm > 1e-12)
            {
                float g = (float)(1.0 / norm);
                for (int i = 0; i < len; i++) ir[i] *= g;
            }
            for (int i = 0; i < len; i++) if (Math.Abs(ir[i]) < 1e-20f) ir[i] = 0;
            return ir;
        }

        /// <summary>The response's gain on a pink spectrum between 100 Hz and 5 kHz: the root of the mean of |H|² weighted by 1/f.</summary>
        public static double PinkGain(float[] ir, double rate)
        {
            int n = 8192;
            while (n < ir.Length) n <<= 1;
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < ir.Length; i++) re[i] = ir[i];
            Fft.Transform(re, im, false);
            double num = 0, den = 0;
            for (int k = 1; k < n / 2; k++)
            {
                double hz = k * rate / n;
                if (hz < 100 || hz > 5000) continue;
                double w = 1.0 / hz;
                num += w * (re[k] * re[k] + im[k] * im[k]);
                den += w;
            }
            return den > 0 ? Math.Sqrt(num / den) : 0;
        }
    }
}
