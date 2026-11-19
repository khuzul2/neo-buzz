#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md §4): the richer trigger features. Per onset: the 19 numbers of
// TriggerFeatures (band shares, centroid, flatness, zero crossings, attack, decay, 5 cepstral numbers of 16 bands) plus
//  - 13 mel-frequency cepstral coefficients (c1 ... c13 of 26 mel bands from 80 Hz to the top, the background taken off as there),
//  - the change over the window: the log energy of 8 coarse bands in the second half of the window less the first half, and
//    the same for the ZCR and the spectral centroid (what a "p" and a "ts" do inside 30 ms),
//  - the spectral roll-off (85 % of the energy, log2 of Hz re 1 kHz) and the spectral tilt (slope of the log band energies).
// 19 + 13 + 8 + 2 + 2 = 44 numbers. The window is 18 ms (Fast) or 30 ms (Accurate), from 3 ms before the onset. Allocation-free
// after construction.

using System;

namespace NeoBuzz.Dsp
{
    internal sealed class VoiceFeatures
    {
        public const int Mfcc = 13, Mels = 26, Coarse = 8;
        public const int Dimension = TriggerFeatures.Dimension + Mfcc + Coarse + 2 + 2;
        public const double FastSeconds = 0.018, AccurateSeconds = 0.030;

        public readonly double SampleRate, LengthSeconds;
        public readonly int Length, Lead;
        readonly TriggerFeatures baseFeatures;
        readonly ComplexFft fft, fftHalf;
        readonly float[] re, im, pre, preIm, hre, him;
        readonly float[] window, windowHalf;
        readonly int n, nh;
        readonly float[] feat19 = new float[TriggerFeatures.Dimension];
        readonly int[] melLo, melHi;
        readonly float[][] melW;
        readonly double[] mel = new double[Mels], melBg = new double[Mels], logMel = new double[Mels];
        readonly double[] dct = new double[Mfcc * Mels];
        readonly double[] coarseA = new double[Coarse], coarseB = new double[Coarse];
        readonly int[] coarseEdge = new int[Coarse + 1];

        public VoiceFeatures(double sampleRate, double lengthSeconds = FastSeconds)
        {
            SampleRate = sampleRate;
            LengthSeconds = lengthSeconds;
            baseFeatures = new TriggerFeatures(sampleRate, lengthSeconds);
            Length = baseFeatures.Length;
            Lead = baseFeatures.Lead;
            n = 64;
            while (n < Length) n <<= 1;
            nh = Math.Max(64, n / 2);
            fft = new ComplexFft(n);
            fftHalf = new ComplexFft(nh);
            re = new float[n]; im = new float[n]; pre = new float[n]; preIm = new float[n];
            hre = new float[nh]; him = new float[nh];
            window = new float[Length];
            int edge = Math.Max(1, Length / 5);
            for (int i = 0; i < Length; i++)
            {
                double w = 1;
                if (i < edge) w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * i / edge);
                else if (i >= Length - edge) w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * (Length - 1 - i) / edge);
                window[i] = (float)w;
            }
            int half = Length / 2;
            windowHalf = new float[half];
            for (int i = 0; i < half; i++) windowHalf[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * (i + 0.5) / half));
            // Mel filters: 26 triangles from 80 Hz to the top, on the bins of the main FFT.
            double top = Math.Min(16000.0, 0.45 * sampleRate);
            double mLo = Hz2Mel(80), mHi = Hz2Mel(top);
            melLo = new int[Mels]; melHi = new int[Mels]; melW = new float[Mels][];
            double binHz = sampleRate / n;
            for (int m = 0; m < Mels; m++)
            {
                double f0 = Mel2Hz(mLo + (mHi - mLo) * m / (Mels + 1)), f1 = Mel2Hz(mLo + (mHi - mLo) * (m + 1) / (Mels + 1)), f2 = Mel2Hz(mLo + (mHi - mLo) * (m + 2) / (Mels + 1));
                int a = Math.Max(1, (int)Math.Floor(f0 / binHz)), c = Math.Min(n / 2, (int)Math.Ceiling(f2 / binHz));
                if (c <= a) c = a + 1;
                melLo[m] = a; melHi[m] = c;
                melW[m] = new float[c - a + 1];
                for (int k = a; k <= c; k++)
                {
                    double f = k * binHz;
                    double w = f < f1 ? (f - f0) / Math.Max(1e-9, f1 - f0) : (f2 - f) / Math.Max(1e-9, f2 - f1);
                    melW[m][k - a] = (float)Math.Max(0, w);
                }
            }
            for (int c = 0; c < Mfcc; c++)
                for (int m = 0; m < Mels; m++) dct[c * Mels + m] = PortableMath.Cos(Math.PI * (c + 1) * (m + 0.5) / Mels);
            // Coarse bands for the change over the window (on the half-window FFT): edges 150 Hz ... top, log spaced.
            double hz0 = 150.0;
            for (int i = 0; i <= Coarse; i++) coarseEdge[i] = Math.Clamp((int)Math.Round(hz0 * PortableMath.Pow(top / hz0, i / (double)Coarse) / (sampleRate / nh)), 1, nh / 2);
            for (int i = 1; i <= Coarse; i++) if (coarseEdge[i] <= coarseEdge[i - 1]) coarseEdge[i] = Math.Min(nh / 2, coarseEdge[i - 1] + 1);
        }

        static double Hz2Mel(double hz) => 2595.0 * PortableMath.Log10(1.0 + hz / 700.0);
        static double Mel2Hz(double m) => 700.0 * (PortableMath.Pow(10.0, m / 2595.0) - 1.0);

        /// <summary>The features of the audio from <paramref name="onsetSample"/> less 3 ms, for the window (a ring: index = position &amp; mask).</summary>
        public void Extract(float[] ring, int mask, long onsetSample, float[] f)
        {
            baseFeatures.Extract(ring, mask, onsetSample, feat19);
            Array.Copy(feat19, f, TriggerFeatures.Dimension);
            long start = onsetSample - Lead;
            int len = Length;
            for (int i = 0; i < len; i++) { re[i] = ring[(int)((start + i) & mask)] * window[i]; im[i] = 0; pre[i] = ring[(int)((start - len + i) & mask)] * window[i]; preIm[i] = 0; }
            for (int i = len; i < n; i++) { re[i] = 0; im[i] = 0; pre[i] = 0; preIm[i] = 0; }
            fft.Transform(re, im, false);
            fft.Transform(pre, preIm, false);
            double totalMel = 1e-18;
            for (int m = 0; m < Mels; m++)
            {
                double e = 0, b = 0;
                var w = melW[m];
                for (int k = melLo[m]; k <= melHi[m]; k++)
                {
                    double wk = w[k - melLo[m]];
                    e += wk * ((double)re[k] * re[k] + (double)im[k] * im[k]);
                    b += wk * ((double)pre[k] * pre[k] + (double)preIm[k] * preIm[k]);
                }
                e = Math.Max(e - 0.9 * b, 0.05 * e) + 1e-18;
                mel[m] = e; totalMel += e;
            }
            for (int m = 0; m < Mels; m++) logMel[m] = PortableMath.Log(mel[m] / totalMel + 1e-9);
            int o = TriggerFeatures.Dimension;
            for (int c = 0; c < Mfcc; c++)
            {
                double s = 0;
                for (int m = 0; m < Mels; m++) s += logMel[m] * dct[c * Mels + m];
                f[o++] = (float)(s / 4.0);
            }
            // The change over the window: the first and the second half.
            int half = len / 2;
            HalfBands(ring, mask, start + Lead / 2, half, coarseA);
            HalfBands(ring, mask, start + half, half, coarseB);
            double ta = 1e-18, tb = 1e-18;
            for (int i = 0; i < Coarse; i++) { ta += coarseA[i]; tb += coarseB[i]; }
            for (int i = 0; i < Coarse; i++) f[o++] = (float)(10.0 * PortableMath.Log10((coarseB[i] / tb + 1e-6) / (coarseA[i] / ta + 1e-6)) / 10.0);
            // Centroid and zero crossings in the halves.
            double ca = 0, cb = 0;
            for (int i = 0; i < Coarse; i++) { double hz = Math.Sqrt(Edge(i) * Edge(i + 1)); ca += coarseA[i] * hz; cb += coarseB[i] * hz; }
            f[o++] = (float)PortableMath.Log2(Math.Max(150.0, cb / tb) / Math.Max(150.0, ca / ta));
            f[o++] = (float)(ZeroRate(ring, mask, start + half, half) - ZeroRate(ring, mask, start, half));
            // Roll-off and tilt of the whole window's mel spectrum.
            double acc = 0, target = 0.85 * totalMel;
            int roll = Mels - 1;
            for (int m = 0; m < Mels; m++) { acc += mel[m]; if (acc >= target) { roll = m; break; } }
            f[o++] = (float)roll / Mels;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int m = 0; m < Mels; m++) { double x = m, y = logMel[m]; sx += x; sy += y; sxx += x * x; sxy += x * y; }
            f[o++] = (float)((Mels * sxy - sx * sy) / (Mels * sxx - sx * sx));
        }

        double Edge(int i) => coarseEdge[i] * (SampleRate / nh);

        double ZeroRate(float[] ring, int mask, long from, int count)
        {
            int zc = 0; float prev = ring[(int)(from & mask)];
            for (int i = 1; i < count; i++) { float v = ring[(int)((from + i) & mask)]; if ((v >= 0) != (prev >= 0)) zc++; prev = v; }
            return (double)zc / count;
        }

        void HalfBands(float[] ring, int mask, long from, int count, double[] bands)
        {
            count = Math.Min(count, windowHalf.Length);
            for (int i = 0; i < count; i++) { hre[i] = ring[(int)((from + i) & mask)] * windowHalf[i]; him[i] = 0; }
            for (int i = count; i < nh; i++) { hre[i] = 0; him[i] = 0; }
            fftHalf.Transform(hre, him, false);
            for (int b = 0; b < Coarse; b++)
            {
                double e = 0;
                for (int k = coarseEdge[b]; k < coarseEdge[b + 1]; k++) e += (double)hre[k] * hre[k] + (double)him[k] * him[k];
                bands[b] = e + 1e-18;
            }
        }
    }
}
