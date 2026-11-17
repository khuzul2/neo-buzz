#nullable disable
// Neo machines shared DSP (docs/specs/pitch.md §5): the vowel estimator Neo Voice to MIDI's expression section reads.
//  - The audio is low-passed (a 4th-order Butterworth) and decimated to about 10 kHz (every fifth sample at 48 kHz: the
//    formants lie under 4 kHz), pre-emphasised (0.97), and every update the newest 28 ms are Hann-windowed and analysed by
//    linear prediction: the autocorrelation with a 100 Hz Gaussian lag window and a white-noise floor (−50 dB), the
//    Levinson-Durbin recursion at order fs/1000 + 2 (12 at 9.6 kHz).
//  - Two readings of that envelope: the formants F1 and F2 (the first peak in 200 ... 1100 Hz, the next one in 700 ... 3400 Hz at
//    least 250 Hz above it; for a display and for the offline tools), and what the weights come from, the shape of the envelope:
//    its highest point in three regions, Low (180-500 Hz), Mid (500-1400) and High (1700-3400), in dB. The three vowels differ in
//    which regions stand out, and a pair of formants too close to be two peaks (the first two of "aa", of "oo") still shows
//    in which regions are high. Measured on VocalSet vowels (8 singers; L-M, H-M, L-H in dB): aa has Mid well above Low (-10 ... -16)
//    and High not far under Mid; oo has Low above High (+7 ... +21) and High far under Mid (under -5); ee has High at or above
//    Mid (above 0) and Low not far under Mid.
//  - The weights are smooth (logistic) scores of those dB differences, each 0 ... 1; they need not add to 1: a vowel between
//    two shows two middling weights, silence none. Smoothed with a one-pole (50 ms); unvoiced input lets them fall.
// Limits (the spec's): LPC finds a harmonic and not a formant above about 300 Hz of f0, a whisper has no voiced frame, and the
// three prototypes are an average adult's: a very deep or a child's voice shifts all three.
// Allocation-free after construction; the portable maths only.

using System;

namespace NeoBuzz.Dsp
{
    internal struct VowelResult
    {
        /// <summary>False when the last update found no voiced frame or no formant.</summary>
        public bool Valid;
        public float F1, F2;
        /// <summary>Smoothed closeness to each vowel, 0 … 1.</summary>
        public float Aa, Oo, Ee;
    }

    internal sealed class VowelEstimator
    {
        public readonly double SampleRate;
        public readonly int Decimation, Order, WindowSamples;
        public readonly double EffectiveRate;

        /// <summary>Seconds for the weights to follow a change (63 %).</summary>
        public double SmoothingSeconds = 0.05;

        const int Grid = 512;
        Biquad lpA, lpB;
        readonly float[] ring;
        readonly int ringMask;
        int phase;
        long written;
        float preX;
        readonly double[] frame, ac, lag, a, tmp, env;
        readonly float[] re, im;
        readonly ComplexFft fft;
        readonly double[] hann;
        VowelResult state;
        long lastUpdate;

        public VowelEstimator(double sampleRate)
        {
            SampleRate = sampleRate;
            Decimation = Math.Max(1, (int)Math.Round(sampleRate / 10000.0));
            EffectiveRate = sampleRate / Decimation;
            Order = (int)Math.Round(EffectiveRate / 1000.0) + 2;
            WindowSamples = (int)Math.Round(0.028 * EffectiveRate);
            lpA.Set(BiquadType.LowPass, 0.43 * EffectiveRate, sampleRate, 0.5411961);
            lpB.Set(BiquadType.LowPass, 0.43 * EffectiveRate, sampleRate, 1.3065630);
            int r = 1;
            while (r < WindowSamples * 2) r <<= 1;
            ring = new float[r];
            ringMask = r - 1;
            frame = new double[WindowSamples];
            ac = new double[Order + 1];
            lag = new double[Order + 1];
            a = new double[Order + 1];
            tmp = new double[Order + 1];
            env = new double[Grid / 2 + 1];
            re = new float[Grid]; im = new float[Grid];
            fft = new ComplexFft(Grid);
            hann = new double[WindowSamples];
            for (int i = 0; i < WindowSamples; i++) hann[i] = 0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * (i + 0.5) / WindowSamples);
            // Gaussian lag window: 100 Hz.
            for (int k = 0; k <= Order; k++)
            {
                double w = 2 * Math.PI * 100.0 / EffectiveRate * k;
                lag[k] = PortableMath.Exp(-0.5 * w * w);
            }
        }

        public void Reset()
        {
            lpA.Reset(); lpB.Reset();
            Array.Clear(ring, 0, ring.Length);
            phase = 0; written = 0; preX = 0; state = default; lastUpdate = 0;
        }

        /// <summary>Feeds mono audio (every sample: the filter runs at the full rate).</summary>
        public void Push(float[] x, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float y = lpB.Process(lpA.Process(x[offset + i]));
                if (++phase >= Decimation)
                {
                    phase = 0;
                    float e = y - 0.97f * preX;
                    preX = y;
                    ring[(int)(written & ringMask)] = e;
                    written++;
                }
            }
            streamPos += count;
        }

        long streamPos;

        /// <summary>
        /// Analyses the newest 28 ms. <paramref name="voiced"/> from the pitch tracker: false lets the weights fall without
        /// analysing. Call it at the hop the display or the CCs need (every 10 ms is plenty); the smoothing follows the time
        /// between calls.
        /// </summary>
        public VowelResult Update(bool voiced)
        {
            double dt = (streamPos - lastUpdate) / SampleRate;
            lastUpdate = streamPos;
            float k = (float)(1.0 - PortableMath.Exp(-Math.Max(dt, 1e-4) / Math.Max(SmoothingSeconds, 1e-3)));
            float ta = 0, to = 0, te = 0;
            bool ok = false;
            if (voiced && written >= WindowSamples)
            {
                ok = Analyse(out double f1, out double f2);
                if (ok)
                {
                    state.F1 = (float)f1; state.F2 = (float)f2;
                    double lm = lowDb - midDb, hm = highDb - midDb, lh = lowDb - highDb;
                    ta = Logistic((-lm - 8) / 2.0) * Logistic((14 - lh) / 3.0);
                    to = Logistic((lh - 6) / 3.0) * Logistic((lm + 8) / 3.0) * Logistic((-hm - 4) / 3.0);
                    te = Logistic((hm + 1) / 3.0) * Logistic((lm + 8) / 2.5);
                }
            }
            state.Valid = ok;
            state.Aa += (ta - state.Aa) * k;
            state.Oo += (to - state.Oo) * k;
            state.Ee += (te - state.Ee) * k;
            return state;
        }

        double lowDb, midDb, highDb;

        /// <summary>The envelope's highest point in each region at the last analysis, dB (for the offline tools and the spec's tuning).</summary>
        public (double Low, double Mid, double High) Regions => (lowDb, midDb, highDb);

        bool Analyse(out double f1, out double f2)
        {
            f1 = f2 = 0;
            int n = WindowSamples;
            long start = written - n;
            for (int i = 0; i < n; i++) frame[i] = ring[(int)((start + i) & ringMask)] * hann[i];
            for (int l = 0; l <= Order; l++)
            {
                double s = 0;
                for (int i = l; i < n; i++) s += frame[i] * frame[i - l];
                ac[l] = s * lag[l];
            }
            if (ac[0] < 1e-12) return false;
            ac[0] *= 1.00001;
            // Levinson-Durbin.
            Array.Clear(a, 0, a.Length);
            a[0] = 1;
            double err = ac[0];
            for (int i = 1; i <= Order; i++)
            {
                double acc = ac[i];
                for (int j = 1; j < i; j++) acc += a[j] * ac[i - j];
                double kk = -acc / err;
                for (int j = 1; j < i; j++) tmp[j] = a[j] + kk * a[i - j];
                for (int j = 1; j < i; j++) a[j] = tmp[j];
                a[i] = kk;
                err *= 1 - kk * kk;
                if (err <= 1e-12 * ac[0]) break;
            }
            // The envelope on the grid.
            for (int i = 0; i < Grid; i++) { re[i] = i <= Order ? (float)a[i] : 0f; im[i] = 0f; }
            fft.Transform(re, im, false);
            for (int i = 0; i <= Grid / 2; i++) env[i] = 1.0 / (re[i] * (double)re[i] + im[i] * (double)im[i] + 1e-12);
            double binHz = EffectiveRate / Grid;
            lowDb = RegionMax(180, 500, binHz); midDb = RegionMax(500, 1400, binHz); highDb = RegionMax(1700, 3400, binHz);
            double pf1 = 0, pf2 = 0;
            int lo1 = (int)(200 / binHz), hi1 = (int)(1100 / binHz);
            for (int i = Math.Max(1, lo1); i <= hi1 && i < Grid / 2; i++)
                if (env[i] > env[i - 1] && env[i] >= env[i + 1]) { pf1 = Peak(i) * binHz; break; }
            if (pf1 <= 0) return false;
            int lo2 = (int)(Math.Max(700, pf1 + 250) / binHz), hi2 = (int)(3400 / binHz);
            for (int i = Math.Max(1, lo2); i <= hi2 && i < Grid / 2; i++)
                if (env[i] > env[i - 1] && env[i] >= env[i + 1]) { pf2 = Peak(i) * binHz; break; }
            if (pf2 <= 0) pf2 = pf1;
            f1 = pf1; f2 = pf2;
            return true;
        }

        double RegionMax(double lo, double hi, double binHz)
        {
            double m = 0;
            int a = Math.Max(1, (int)(lo / binHz)), b = Math.Min(Grid / 2 - 1, (int)(hi / binHz));
            for (int i = a; i <= b; i++) if (env[i] > m) m = env[i];
            return 10.0 * PortableMath.Log10(m + 1e-30);
        }

        static float Logistic(double x) => (float)(1.0 / (1.0 + PortableMath.Exp(-x)));

        double Peak(int i)
        {
            double l = env[i - 1], c = env[i], r = env[i + 1];
            double den = l - 2 * c + r;
            double sh = den < -1e-30 ? 0.5 * (l - r) / den : 0;
            return i + Math.Clamp(sh, -1, 1);
        }
    }
}
