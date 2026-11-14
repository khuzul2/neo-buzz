#nullable disable
// Neo Spectral (docs/specs/mastering-tools.md §5.16): a short-time FFT resonance suppressor. Measured as a prototype first
// (the gate, "M7 as measured"); the mask is applied by multiplying the frame's spectrum (the FIR option failed the gate).
//  - Frames: Stft (N = 2048 at 44.1 / 48 kHz, 4096 at 88.2 / 96 and above, hop N/4, √Hann windows). Stereo is analysed as
//    one packed transform and one real mask scales both channels (linked): the power of a bin is the sum of both channels'.
//  - Detector: the bin power smoothed in time (one pole per hop) and over 3 bins, in dB (own float log2).
//    Suppress: the reference is the mean over ±Selectivity octaves of that level (prefix sums), taken twice, the second time
//    with the first's peaks (and, with Lift, its dips) clipped to 3 dB from it so a narrow resonance doesn't raise its own reference.
//    Balance: that smoothed level against a pink slope (−3 dB per octave) fitted to its own level in 200 Hz – 8 kHz, plus Tilt dB
//    per octave above 300 Hz. Stabilize: the level (smoothed over ±0.08 octave) against its own 4 s average.
//  - Mask: excess = level − reference. Above the reference: cut = Depth · excess · x⁶/(1 + x⁶), x = excess/Knee (a soft knee, nearly
//    nothing under Knee/2, nearly all over 1.5 · Knee), at most 24 dB; Balance has no knee. Below it (a dip): lift of the same shape,
//    at most Lift dB (0: cut only). Outside Low … High nothing changes. The mask is widened over ±Smoothing bins (the deeper cut is
//    kept), then smoothed per bin in dB with Attack (more reduction) and Release (less), both faster at high frequency
//    (τ / √(1 + f/1 kHz)). A bin whose mask is within 0.005 dB of nothing is exactly 1.0, so with nothing to do the output is the
//    delayed input within the transform's round-off (−138 dB); Mix 0 returns the delayed input bit for bit.
//  - Latency N − 1, reported; Delta delays the dry by it.
// Allocation-free after construction (SetSampleRate builds a new transform off the audio thread); the portable maths only (own float
// log2/exp2 from bit tricks and + − × ÷).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Spectral
{
    internal sealed class SpectralEngine
    {
        public const int MaxBlock = 256;
        public const int Bands = 96;                 // the editor's curve: log-spaced from 30 Hz to 20 kHz
        const int Chunk = 4096;

        readonly ParamValues values;
        double rate = 48000;
        Core core;
        SmoothRamp output;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        /// <summary>The mask now in dB per band (30 Hz – 20 kHz, 96 log bands): the deepest cut, or the lift where there is none. For the editor.</summary>
        public readonly float[] Curve = new float[Bands];
        double hold, pubRed, pubMean, pubMatch = double.NaN;
        int pubLatency;

        public SpectralEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(4096 + 64, 48000);
            Match = new GainMatch(48000);
            Build(48000);
        }

        public int Latency => core.Latency;
        /// <summary>Mask cells (bin × hop) seen, and those cut by more than 1 dB (diagnostics for the tests).</summary>
        public (long Seen, long Cut) Cells => (core.CellsSeen, core.CellsCut);
        public double ReductionDb => Volatile.Read(ref pubRed);
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        public void SetSampleRate(float sr)
        {
            Build(sr);
            output.Setup(0.02, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        void Build(double sr)
        {
            rate = sr;
            core = new Core(sr, Curve);
        }

        public void Reset()
        {
            core.Reset();
            Delta.Reset();
            Match.Reset();
            hold = 0;
            Array.Clear(Curve);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            core.Mode = values.Choice(P.Mode);
            core.Depth = (float)values.Value(P.Depth);
            core.KneeDb = (float)values.Value(P.Knee);
            core.Selectivity = (float)values.Value(P.Selectivity);
            core.Smoothing = (int)Math.Round(values.Value(P.Smoothing));
            core.TiltDb = (float)values.Value(P.Tilt);
            core.LiftDb = (float)values.Value(P.Lift);
            core.AttackMs = (float)(values.Value(P.Attack) * 1000);
            core.ReleaseMs = (float)(values.Value(P.Release) * 1000);
            core.LowHz = (float)values.Value(P.Low);
            core.HighHz = (float)values.Value(P.High);
            core.Mix = (float)values.Value(P.Mix);
            core.Configure();
            Delta.Latency = Latency;
            pubLatency = Latency;
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            core.Process(left, right, 0, n);
            Delta.Process(dryL, dryR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            OutMeter.Feed(left, right, n);
            hold = Math.Max(core.Deepest, hold - 12.0 * n / rate);
            Volatile.Write(ref pubRed, hold);
            Volatile.Write(ref pubMean, core.Mean);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["reduction_db"] = Math.Round(ReductionDb, 2),
                ["mean_reduction_db"] = Math.Round(Volatile.Read(ref pubMean), 2),
                ["latency_samples"] = Volatile.Read(ref pubLatency),
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }

        /// <summary>The transform, the mask and the dry path at one sample rate.</summary>
        internal sealed class Core : ISpectralProcessor
        {
            public readonly int N, Hop;
            public readonly double Rate;
            public int Mode, Smoothing;
            public float Depth = 0.5f, KneeDb = 4f, Selectivity = 0.4f, TiltDb, LiftDb, AttackMs = 20f, ReleaseMs = 150f, LowHz = 100f, HighHz = 16000f, Mix = 1f;
            /// <summary>Per block: the deepest cut now, and the mean cut over the working region, dB (0 or more).</summary>
            public double Deepest, Mean;
            public long CellsSeen, CellsCut;

            readonly NeoBuzz.Dsp.Stft stft;
            readonly int half;
            readonly float[] dryL, dryR, scratchL, scratchR;
            readonly float[] ps, lp, env, clip, cs, gdB, gsm, gs, gain, aAtt, aRel, hz, avg, sm, speed;
            readonly int[] bandOf;
            readonly float[] curve, bandMin, bandMax;
            float timeA, pinkA, longA;
            bool avgInit, pinkInit;
            int kLo, rLo, rHi, top;
            long count;

            public Core(double rate, float[] curve)
            {
                Rate = rate;
                N = rate > 66000 ? 4096 : 2048;
                stft = new NeoBuzz.Dsp.Stft(N);
                Hop = stft.Hop;
                half = N / 2;
                this.curve = curve;
                int ring = 8192;
                dryL = new float[ring]; dryR = new float[ring];
                scratchL = new float[Chunk]; scratchR = new float[Chunk];
                int m = half + 2;
                ps = new float[m]; lp = new float[m]; env = new float[m]; clip = new float[m]; sm = new float[m]; avg = new float[m];
                cs = new float[m + 1]; gdB = new float[m]; gsm = new float[m]; gs = new float[m]; gain = new float[m];
                aAtt = new float[m]; aRel = new float[m]; hz = new float[m]; speed = new float[m];
                bandOf = new int[m];
                bandMin = new float[Bands]; bandMax = new float[Bands];
                for (int k = 0; k < m; k++)
                {
                    hz[k] = (float)(k * rate / N);
                    speed[k] = (float)(1.0 / Math.Sqrt(1.0 + hz[k] / 1000.0));
                    gain[k] = 1f;
                    int b = (int)(PortableMath.Log(Math.Max(hz[k], 1.0) / 30.0) / PortableMath.Log(20000.0 / 30.0) * Bands);
                    bandOf[k] = Math.Clamp(b, 0, Bands - 1);
                }
                timeA = 0.35f;
                pinkA = (float)(1.0 - PortableMath.Exp(-Hop / Rate / 0.5));
                longA = (float)(1.0 - PortableMath.Exp(-Hop / Rate / 4.0));
                Configure();
            }

            public int Latency => N - 1;

            public void Reset()
            {
                stft.Reset();
                Array.Clear(dryL); Array.Clear(dryR);
                Array.Clear(ps); Array.Clear(gs); Array.Clear(avg);
                for (int k = 0; k < gain.Length; k++) gain[k] = 1f;
                avgInit = pinkInit = false;
                count = 0;
                Deepest = Mean = 0;
            }

            /// <summary>Recomputes the coefficients from the public fields. The per-bin time constants are the dear part (a few
            /// exponentials at every 8th bin, interpolated between), and only when Attack or Release moved.</summary>
            public void Configure()
            {
                double hop = Hop / Rate;
                if (AttackMs != doneAttack || ReleaseMs != doneRelease)
                {
                    doneAttack = AttackMs;
                    doneRelease = ReleaseMs;
                    double ta = Math.Max(AttackMs, 0.1) * 1e-3, tr = Math.Max(ReleaseMs, 0.1) * 1e-3;
                    const int Stride = 8;
                    float pa = 0, pr = 0;
                    for (int k = 0; k <= half + Stride; k += Stride)
                    {
                        int kk = Math.Min(k, half);
                        float na = FastExp2((float)(-hop / (ta * speed[kk]) * 1.44269504)), nr = FastExp2((float)(-hop / (tr * speed[kk]) * 1.44269504));
                        if (k > 0)
                            for (int j = k - Stride + 1; j <= k && j <= half; j++)
                            {
                                float u = (j - (k - Stride)) / (float)Stride;
                                aAtt[j] = pa + (na - pa) * u;
                                aRel[j] = pr + (nr - pr) * u;
                            }
                        else { aAtt[0] = na; aRel[0] = nr; }
                        pa = na; pr = nr;
                    }
                }
                kLo = Math.Max(2, (int)(20.0 * N / Rate));
                top = half - 2;
                rLo = Math.Max(kLo, (int)(LowHz * N / Rate));
                rHi = Math.Min(top, (int)(HighHz * N / Rate));
            }

            float doneAttack = -1f, doneRelease = -1f;

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Process(float[] l, float[] r, int off, int n)
            {
                Deepest = 0;
                double meanSum = 0;
                int means = 0;
                for (int pos = 0; pos < n; pos += Chunk)
                {
                    int len = Math.Min(Chunk, n - pos);
                    Chunk1(l, r, off + pos, len);
                    meanSum += lastMean;
                    means++;
                }
                Mean = means > 0 ? meanSum / means : 0;
            }

            double lastMean;

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            void Chunk1(float[] l, float[] r, int off, int n)
            {
                int lat = Latency, ringMask = dryL.Length - 1;
                for (int i = 0; i < n; i++)
                {
                    long w = count + i;
                    long rd = w - lat;
                    scratchL[i] = rd >= 0 ? dryL[(int)(rd & ringMask)] : 0f;
                    scratchR[i] = rd >= 0 ? dryR[(int)(rd & ringMask)] : 0f;
                    dryL[(int)(w & ringMask)] = l[off + i];
                    dryR[(int)(w & ringMask)] = r[off + i];
                }
                // The analysis always runs, so a later Mix change is in step.
                stft.Process(l, r, off, n, this);
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

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Process(float[] re, float[] im, int n)
            {
                const float Db = 3.0103f;   // dB per log2 of power
                for (int k = 1; k < half; k++)
                {
                    float a = re[k], b = im[k], c = re[n - k], d = im[n - k];
                    float pw = 0.5f * (a * a + b * b + c * c + d * d);
                    ps[k] += timeA * (pw - ps[k]);
                }
                for (int k = kLo; k <= top; k++)
                {
                    float p = 0.25f * ps[k - 1] + 0.5f * ps[k] + 0.25f * ps[k + 1];
                    lp[k] = Db * FastLog2(p + 1e-18f);
                }
                float up = (float)PortableMath.Pow(2.0, Selectivity), dn = 1f / up;
                float knee = KneeDb < 0.5f ? 0.5f : KneeDb;
                float depth = Depth, lift = LiftDb;
                bool balance = Mode == 1;
                for (int k = 0; k <= half; k++) gdB[k] = 0f;

                // The reference: env[k] is the level the bin is compared with; ex = level − reference.
                if (Mode == 2)
                {
                    float s2 = 0.08f;
                    Envelope(lp, sm, kLo, top, (float)PortableMath.Pow(2.0, s2), (float)PortableMath.Pow(2.0, -s2));
                    if (!avgInit) { for (int k = kLo; k <= top; k++) avg[k] = sm[k]; avgInit = true; }
                    for (int k = kLo; k <= top; k++) { env[k] = avg[k]; avg[k] += longA * (sm[k] - avg[k]); }
                    // env holds the average before this frame's update; the level compared is the smoothed one.
                }
                else if (balance)
                {
                    // Balance compares the broad level, not the peaks.
                    Envelope(lp, sm, kLo, top, up, dn);
                }
                else
                {
                    Envelope(lp, env, kLo, top, up, dn);
                    for (int k = kLo; k <= top; k++) clip[k] = Math.Min(lift > 0f ? Math.Max(lp[k], env[k] - 3f) : lp[k], env[k] + 3f);
                    Envelope(clip, env, kLo, top, up, dn);
                }
                float pinkOff = 0f;
                if (balance)
                {
                    int a0 = Math.Max(kLo, (int)(200.0 * N / Rate)), a1 = Math.Min(top, (int)(8000.0 * N / Rate));
                    double acc = 0;
                    for (int k = a0; k <= a1; k++) acc += sm[k] + Log10Hz(hz[k]);
                    float fit = (float)(acc / Math.Max(1, a1 - a0 + 1));
                    if (!pinkInit) { pinkOff = fit; pinkA2 = fit; pinkInit = true; }
                    else { pinkA2 += pinkA * (fit - pinkA2); pinkOff = pinkA2; }
                }
                bool any = false;
                float tilt = TiltDb;
                for (int k = rLo; k <= rHi; k++)
                {
                    float ex;
                    if (balance)
                    {
                        float f = hz[k];
                        float reference = pinkOff - Log10Hz(f) + (f > 300f ? tilt * FastLog2(f / 300f) : 0f);
                        ex = sm[k] - reference;
                        float g;
                        if (ex > 0.05f) { g = -depth * ex; if (g < -24f) g = -24f; }
                        else if (ex < -0.05f && lift > 0f) { g = depth * -ex; if (g > lift) g = lift; }
                        else continue;
                        gdB[k] = g;
                        any = true;
                        continue;
                    }
                    float level = Mode == 2 ? sm[k] : lp[k];
                    ex = level - env[k];
                    if (ex > 0.05f)
                    {
                        float x = ex / knee, x2 = x * x, x6 = x2 * x2 * x2;
                        float cut = depth * ex * x6 / (1f + x6);
                        gdB[k] = -(cut > 24f ? 24f : cut);
                        any = true;
                    }
                    else if (ex < -0.05f && lift > 0f)
                    {
                        float x = -ex / knee, x2 = x * x, x6 = x2 * x2 * x2;
                        float up2 = depth * -ex * x6 / (1f + x6);
                        gdB[k] = up2 > lift ? lift : up2;
                        any = true;
                    }
                }
                // Widening over ±Smoothing bins (box); a deeper cut is kept as it is.
                int w = Smoothing;
                if (any && w > 0)
                {
                    cs[0] = 0f;
                    for (int k = 0; k <= half; k++) cs[k + 1] = cs[k] + gdB[k];
                    for (int k = 0; k <= half; k++)
                    {
                        int lo = k - w < 0 ? 0 : k - w, hi = k + w > half ? half : k + w;
                        gsm[k] = (cs[hi + 1] - cs[lo]) / (hi - lo + 1);
                    }
                    for (int k = 0; k <= half; k++) if (gdB[k] < gsm[k]) gsm[k] = gdB[k];
                }
                else Array.Copy(gdB, gsm, half + 1);
                // Attack (more reduction) and release per bin, in dB; a mask that has come back to nothing is exactly unity.
                for (int b = 0; b < Bands; b++) { bandMin[b] = 0f; bandMax[b] = 0f; }
                double deepest = 0, sum = 0;
                int cnt = 0;
                for (int k = 0; k <= half; k++)
                {
                    float t = gsm[k], g = gs[k];
                    float a = t < g ? aAtt[k] : aRel[k];
                    g = t + (g - t) * a;
                    if (t == 0f && g > -0.005f && g < 0.005f) g = 0f;
                    gs[k] = g;
                    if (g == 0f) gain[k] = 1f;
                    else gain[k] = FastExp2(g * 0.16609640f);
                    CellsSeen++;
                    if (g < -1f) CellsCut++;
                    int bi = bandOf[k];
                    if (g < bandMin[bi]) bandMin[bi] = g;
                    if (g > bandMax[bi]) bandMax[bi] = g;
                    if (k >= rLo && k <= rHi)
                    {
                        if (-g > deepest) deepest = -g;
                        sum += g < 0f ? -g : 0f;
                        cnt++;
                    }
                }
                for (int b = 0; b < Bands; b++) curve[b] = bandMin[b] < 0f ? bandMin[b] : bandMax[b];
                if (deepest > Deepest) Deepest = deepest;
                lastMean = cnt > 0 ? sum / cnt : 0;
                NeoBuzz.Dsp.Stft.ApplyGains(re, im, n, gain);
            }

            float pinkA2;

            /// <summary>10·log10(f / 1 kHz) in dB (a power ratio), by the float log2.</summary>
            static float Log10Hz(float f) => 3.0103f * FastLog2(f * 0.001f);

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
            float fl = MathF.Floor(x);
            int i = (int)fl;
            float u = (x - fl) * 0.693147181f;
            float p = 1f + u * (1f + u * (0.5f + u * (1f / 6f + u * (1f / 24f + u * (1f / 120f + u * (1f / 720f + u * (1f / 5040f)))))));
            return p * BitConverter.Int32BitsToSingle((i + 127) << 23);
        }
    }
}
