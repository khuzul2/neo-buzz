#nullable disable
// Neo Spectral PROTOTYPE (docs/specs/mastering-tools.md §5.16, M7 gate): a short-time FFT resonance suppressor.
//  - Detector: the packed spectrum's per-bin power (left + right, linked), smoothed in time and over 3 bins, as dB;
//    an envelope = the mean of that over ±Selectivity octaves (prefix sums), twice (the second pass with the first's
//    peaks clipped to 3 dB over, so a narrow resonance doesn't lift its own reference); excess = level − envelope; the mask is
//    −Depth·excess·x⁴/(1 + x⁴) with x = excess/Threshold (the knee; at most 24 dB), smoothed over ±MaskSmooth bins, then per bin attack/release
//    in dB (faster at high frequency). A mask that is exactly unity is exactly 1.0f (the null).
//  - Two ways of applying it, both measured: Multiply (the mask times the frame's spectrum, WOLA: Stft.ApplyGains) and
//    Fir (a 511-tap linear-phase kernel per hop from the mask, Hann-windowed, the outputs of the neighbouring hops'
//    kernels cross-faded; latency N − 1 + 255).
//  - Mix 0 returns the delayed input bit for bit.
// Allocation-free after construction; the portable maths only (own float log2/exp2, bit tricks and +,−,×,÷).

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Spectral
{
    internal sealed class SpectralEngine
    {
        public const int MaxBlock = 4096;
        public readonly int FirHalf;   // taps 0 … FirHalf − 1 on each side of the centre (2·FirHalf − 1 taps)
        const int Kernels = 16;

        public readonly int N, Hop, Mode;
        public readonly double Rate;
        public float Depth = 0.5f, ThresholdDb = 3.5f, Selectivity = 0.4f, MaskSmooth = 2f, AttackMs = 20f, ReleaseMs = 150f, Mix = 1f;

        readonly Stft stft;
        readonly Mask mask;
        // Dry delay and the Fir stage.
        readonly float[] dryL, dryR, scratchL, scratchR, ringL, ringR;
        readonly float[][] kernels;      // [frame % Kernels][FirHalf]
        readonly bool[] kernelDelta;
        readonly ComplexFft kfft;
        readonly float[] kre, kim, kwin;
        /// <summary>Diagnostics: mask cells (bin × hop) seen, and those cut by more than 1 dB.</summary>
        public long CellsSeen, CellsCut;
        long count;           // samples in
        long frames;          // frames computed

        public SpectralEngine(double rate, int mode, int firHalf = 256)
        {
            FirHalf = firHalf;
            Rate = rate;
            Mode = mode;
            N = rate > 66000 ? 4096 : 2048;
            stft = new Stft(N);
            Hop = stft.Hop;
            mask = new Mask(this);
            int ring = 8192;
            dryL = new float[ring];
            dryR = new float[ring];
            scratchL = new float[MaxBlock];
            scratchR = new float[MaxBlock];
            ringL = new float[4096];
            ringR = new float[4096];
            kernels = new float[Kernels][];
            kernelDelta = new bool[Kernels];
            for (int i = 0; i < Kernels; i++) { kernels[i] = new float[FirHalf]; kernelDelta[i] = true; kernels[i][0] = 1f; }
            kfft = new ComplexFft(N);
            kre = new float[N];
            kim = new float[N];
            kwin = new float[FirHalf];
            for (int j = 0; j < FirHalf; j++) kwin[j] = (float)(0.5 + 0.5 * PortableMath.Cos(Math.PI * j / FirHalf));
            Configure();
        }

        public int Latency => Mode == 0 ? N - 1 : N - 1 + (FirHalf - 1);

        /// <summary>Recomputes the coefficients from the public fields (not on the audio thread's hot path).</summary>
        public void Configure() => mask.Configure();

        public void Reset()
        {
            stft.Reset();
            mask.Reset();
            Array.Clear(dryL); Array.Clear(dryR); Array.Clear(ringL); Array.Clear(ringR);
            for (int i = 0; i < Kernels; i++) { Array.Clear(kernels[i]); kernels[i][0] = 1f; kernelDelta[i] = true; }
            count = 0; frames = 0;
        }

        public void Process(float[] l, float[] r, int n)
        {
            for (int pos = 0; pos < n; pos += MaxBlock) ProcessChunk(l, r, pos, Math.Min(MaxBlock, n - pos));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessChunk(float[] l, float[] r, int off, int n)
        {
            int lat = Latency, ringMask = dryL.Length - 1;
            // The dry, delayed by the latency (before the wet overwrites the buffer).
            for (int i = 0; i < n; i++)
            {
                long w = count + i;
                int rd = (int)((w - lat) & ringMask);
                scratchL[i] = w - lat >= 0 ? dryL[rd] : 0f;
                scratchR[i] = w - lat >= 0 ? dryR[rd] : 0f;
                dryL[(int)(w & ringMask)] = l[off + i];
                dryR[(int)(w & ringMask)] = r[off + i];
            }
            // The analysis (and the Fir stage) always run, so a later Mix change is in step.
            stft.Process(l, r, off, n, mask);
            if (Mode == 1) FirStage(l, r, off, n, count);
            count += n;
            float m = Mix;
            if (m <= 0f)
            {
                for (int i = 0; i < n; i++) { l[off + i] = scratchL[i]; r[off + i] = scratchR[i]; }
            }
            else if (m < 1f)
            {
                float d = 1f - m;
                for (int i = 0; i < n; i++)
                {
                    l[off + i] = scratchL[i] * d + l[off + i] * m;
                    r[off + i] = scratchR[i] * d + r[off + i] * m;
                }
            }
        }

        // The Fir stage runs on the STFT's output (the input N − 1 back): the sample of label L (input time) is the centre of
        // its kernel, so the stage lags the STFT by FirHalf − 1 more samples. Frame f ends at (f + 1)·Hop − 1 and its kernel is
        // centred on label (f + 1)·Hop − 1 − N/2; the kernels of frames f and f + 1 are cross-faded linearly between their centres.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void FirStage(float[] l, float[] r, int off, int n, long first)
        {
            int rm = ringL.Length - 1;
            for (int i = 0; i < n; i++)
            {
                long t = first + i;                 // this STFT output is label t − (N − 1)
                ringL[(int)(t & rm)] = l[off + i];
                ringR[(int)(t & rm)] = r[off + i];
                long outT = t - (FirHalf - 1);      // centre sample of the taps, in the STFT output's time
                long label = outT - (N - 1);
                long q = label + 1 + N / 2;
                float yl = 0, yr = 0;
                {
                    long f = q >= 0 ? q / Hop - 1 : -1;
                    float u = q >= 0 ? (float)(q % Hop) / Hop : 0f;
                    int kA = (int)(((f % Kernels) + Kernels) % Kernels), kB = (int)((((f + 1) % Kernels) + Kernels) % Kernels);
                    float wA = 1f - u, wB = u;
                    if (f < 0) { wA = 1f; wB = 0f; }
                    // centre sample
                    float cL = ringL[(int)(outT & rm)], cR = ringR[(int)(outT & rm)];
                    bool dA = f < 0 || f >= frames || kernelDelta[kA], dB = f + 1 >= frames || kernelDelta[kB];
                    if (dA && dB)
                    {
                        yl = cL; yr = cR;
                    }
                    else
                    {
                        float[] hA = kernels[kA], hB = kernels[kB];
                        float sAl = hA[0] * cL, sAr = hA[0] * cR, sBl = hB[0] * cL, sBr = hB[0] * cR;
                        for (int j = 1; j < FirHalf; j++)
                        {
                            int p = (int)((outT + j) & rm), m = (int)((outT - j) & rm);
                            float xl = ringL[p] + ringL[m], xr = ringR[p] + ringR[m];
                            sAl += hA[j] * xl; sAr += hA[j] * xr;
                            sBl += hB[j] * xl; sBr += hB[j] * xr;
                        }
                        if (dA) { sAl = cL; sAr = cR; }
                        if (dB) { sBl = cL; sBr = cR; }
                        yl = sAl * wA + sBl * wB;
                        yr = sAr * wA + sBr * wB;
                    }
                }
                l[off + i] = yl;
                r[off + i] = yr;
            }
        }

        /// <summary>A kernel of the half-taps from the mask's gains (called once per hop, Fir mode).</summary>
        void StoreKernel(float[] gains, bool unity)
        {
            int slot = (int)(frames % Kernels);
            frames++;
            if (unity)
            {
                kernelDelta[slot] = true;
                Array.Clear(kernels[slot]);
                kernels[slot][0] = 1f;
                return;
            }
            kernelDelta[slot] = false;
            int half = N / 2;
            for (int k = 0; k <= half; k++) { kre[k] = gains[k]; kim[k] = 0f; }
            for (int k = 1; k < half; k++) { kre[N - k] = gains[k]; kim[N - k] = 0f; }
            kfft.Transform(kre, kim, true);
            float s = 1f / N;
            float[] h = kernels[slot];
            for (int j = 0; j < FirHalf; j++) h[j] = kre[j] * s * kwin[j];
        }

        sealed class Mask : ISpectralProcessor
        {
            readonly SpectralEngine e;
            readonly int half;
            readonly float[] ps, pf, lp, env, clip, cs, gdB, gsm, gs, gain, aAtt, aRel, hz;
            float timeA;
            int kLo, ilo0;

            public Mask(SpectralEngine e)
            {
                this.e = e;
                half = e.N / 2;
                int m = half + 2;
                ps = new float[m]; pf = new float[m]; lp = new float[m]; env = new float[m]; clip = new float[m];
                cs = new float[m + 1]; gdB = new float[m]; gsm = new float[m]; gs = new float[m]; gain = new float[m];
                aAtt = new float[m]; aRel = new float[m]; hz = new float[m];
                for (int k = 0; k < m; k++) { hz[k] = (float)(k * e.Rate / e.N); gain[k] = 1f; }
            }

            public void Reset()
            {
                Array.Clear(ps); Array.Clear(gs);
                for (int k = 0; k < gain.Length; k++) gain[k] = 1f;
            }

            public void Configure()
            {
                double hop = e.Hop / e.Rate;
                for (int k = 0; k <= half; k++)
                {
                    double speed = 1.0 / Math.Sqrt(1.0 + hz[k] / 1000.0);
                    aAtt[k] = (float)PortableMath.Exp(-hop / (Math.Max(e.AttackMs, 0.1) * 1e-3 * speed));
                    aRel[k] = (float)PortableMath.Exp(-hop / (Math.Max(e.ReleaseMs, 0.1) * 1e-3 * speed));
                }
                timeA = 0.35f;
                kLo = Math.Max(2, (int)(40.0 * e.N / e.Rate));
            }

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Process(float[] re, float[] im, int n)
            {
                const float Db = 3.0103f;   // dB per log2 of power
                float depth = e.Depth, thr = e.ThresholdDb;
                for (int k = 1; k < half; k++)
                {
                    float a = re[k], b = im[k], c = re[n - k], d = im[n - k];
                    float pw = 0.5f * (a * a + b * b + c * c + d * d);
                    ps[k] += timeA * (pw - ps[k]);
                }
                for (int k = kLo; k < half - 1; k++)
                {
                    float p = 0.25f * ps[k - 1] + 0.5f * ps[k] + 0.25f * ps[k + 1];
                    lp[k] = Db * FastLog2(p + 1e-18f);
                }
                // Envelope, two passes.
                float s = e.Selectivity;
                float up = (float)PortableMath.Pow(2.0, s), dn = 1f / up;
                int top = half - 2;
                Envelope(lp, env, kLo, top, up, dn);
                float over = 3f;
                for (int k = kLo; k <= top; k++) clip[k] = Math.Min(lp[k], env[k] + over);
                Envelope(clip, env, kLo, top, up, dn);
                bool any = false;
                for (int k = 0; k <= half; k++) gdB[k] = 0f;
                for (int k = kLo; k <= top; k++)
                {
                    float ex = lp[k] - env[k];
                    float x = ex / thr, x2 = x * x, x4 = x2 * x2 * x2;
                    if (ex > 0.05f)
                    {
                        float cut = depth * ex * x4 / (1f + x4);
                        gdB[k] = -(cut > 24f ? 24f : cut);
                        any = true;
                    }
                }
                // Mask smoothing over ±MaskSmooth bins (box), only where there is something.
                int w = (int)(e.MaskSmooth + 0.5f);
                if (any && w > 0)
                {
                    cs[0] = 0f;
                    for (int k = 0; k <= half; k++) cs[k + 1] = cs[k] + gdB[k];
                    for (int k = 0; k <= half; k++)
                    {
                        int lo = k - w < 0 ? 0 : k - w, hi = k + w > half ? half : k + w;
                        gsm[k] = (cs[hi + 1] - cs[lo]) / (hi - lo + 1);
                    }
                    // Keep the deeper of the box and the raw mask, so a narrow cut is widened but not thinned out.
                    for (int k = 0; k <= half; k++) if (gdB[k] < gsm[k]) gsm[k] = gdB[k];
                }
                else Array.Copy(gdB, gsm, half + 1);
                // Attack (more reduction) / release per bin, in dB.
                bool unity = true;
                for (int k = 0; k <= half; k++)
                {
                    float t = gsm[k], g = gs[k];
                    float a = t < g ? aAtt[k] : aRel[k];
                    g = t + (g - t) * a;
                    if (g > -0.005f && t == 0f) g = 0f;
                    gs[k] = g;
                    e.CellsSeen++;
                    if (g < -1f) e.CellsCut++;
                    if (g == 0f) gain[k] = 1f;
                    else { gain[k] = FastExp2(g * 0.16609640f); unity = false; }
                }
                if (e.Mode == 0) Stft.ApplyGains(re, im, n, gain);
                e.StoreKernel(gain, unity);
            }

            // mean of v over [k/ratio, k·ratio], by prefix sums, into dst.
            void Envelope(float[] v, float[] dst, int lo0, int hi0, float up, float dn)
            {
                cs[lo0] = 0f;
                for (int k = lo0; k <= hi0; k++) cs[k + 1] = cs[k] + v[k];
                for (int k = lo0; k <= hi0; k++)
                {
                    int lo = (int)(k * dn + 0.5f), hi = (int)(k * up + 0.5f);
                    if (lo < lo0) lo = lo0;
                    if (hi > hi0) hi = hi0;
                    dst[k] = (cs[hi + 1] - cs[lo]) / (hi - lo + 1);
                }
            }
        }

        /// <summary>log2 for a positive normal float: the exponent plus the series of ln((1 + z)/(1 − z)) in z = (f − 1)/(f + 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FastLog2(float x)
        {
            int bits = BitConverter.SingleToInt32Bits(x);
            int e = ((bits >> 23) & 255) - 127;
            float f = BitConverter.Int32BitsToSingle((bits & 0x7FFFFF) | 0x3F800000);
            float z = (f - 1f) / (f + 1f), z2 = z * z;
            float ln = 2f * z * (1f + z2 * (1f / 3f + z2 * (0.2f + z2 * (1f / 7f + z2 * (1f / 9f)))));
            return e + ln * 1.44269504f;
        }

        /// <summary>2^x for −60 &lt; x &lt; 60.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FastExp2(float x)
        {
            float fl = (float)Math.Floor(x);
            int i = (int)fl;
            float u = (x - fl) * 0.693147181f;
            float p = 1f + u * (1f + u * (0.5f + u * (1f / 6f + u * (1f / 24f + u * (1f / 120f + u * (1f / 720f + u * (1f / 5040f)))))));
            return p * BitConverter.Int32BitsToSingle((i + 127) << 23);
        }
    }
}
