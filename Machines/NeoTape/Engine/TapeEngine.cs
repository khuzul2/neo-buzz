#nullable disable
// Neo Tape (docs/specs/mastering-tools.md §5.11): per channel, in this order:
//  - record pre-emphasis: a high shelf (2.5 kHz, +Emphasis dB), double-precision biquad;
//  - the tape: Neo Drive's tape curve (tanh) with a first-order ADAA shaper, in a 4× oversampler (High) or at the base
//    rate (Live). y = (f(D·u + b) − f(b)) / (D·f'(b)): the small-signal gain is 1 whatever the Saturation D and the
//    Bias b (the bias moves the working point: the asymmetry makes even harmonics, its DC is subtracted); D and b
//    glide (20 ms). The even-harmonic electronics stage, y += k·y² (k = 0.25·Electronics), follows the curve in the same
//    oversampled domain; a 2 Hz DC blocker takes its DC out;
//  - playback: the matching de-emphasis (a high shelf of −Emphasis dB, the exact inverse of the pre-emphasis), the head
//    bump (a low shelf, Q 1.1, +Bump dB at 35 / 70 / 140 Hz for 7.5 / 15 / 30 ips) and the high-frequency loss (a high
//    shelf of −HF loss dB at 6 / 9.5 / 14 kHz);
//  - High only: a 3-tap droop compensation at the base rate. The first-order ADAA averages two oversampled samples,
//    a low-pass of cos(ω/2) at the oversampled rate (−0.30 dB at 16 kHz, 48 kHz base); [−a, 1 + 2a, −a] with a = 0.0118
//    lifts it back, within 0.04 dB up to 16 kHz;
//  - hiss: a first-difference of white noise (per channel, seeded), at the Hiss level +3 / 0 / −3 dB for 7.5 / 15 / 30 ips;
//  - wow and flutter: one modulated read (cubic) of a delay line, the same for both channels: the delay is 5.2 ms plus
//    Aw·sin(2π·rate·t) for wow and two sines (8.1 and 13.7 Hz, weights 0.7 and 0.3) for flutter, each amplitude A =
//    cents / (1731.2 · 2π · f), so the pitch deviation (the delay's derivative) peaks at the setting in cents. With both
//    off the line isn't used and adds nothing.
// Latency (reported, constant at a setting): High 4× oversampler + 1; plus the 5.2 ms with Wow or Flutter above 0; Live 0.
// Mix 0 gives the input delayed by the latency, bit for bit. Allocation-free after construction; the portable maths.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Drive;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tape
{
    internal sealed class TapeEngine
    {
        public const int MaxBlock = 256;
        public const int HighFactor = 4;
        const double GlideSeconds = 0.02;
        const double CompA = 0.0118;
        const double WfBaseSeconds = 0.0052;
        const double FlutterHz1 = 8.1, FlutterHz2 = 13.7;
        const double CentsPerRatio = 1731.234;       // 1200 / ln 2

        public static readonly double[] BumpHz = { 35, 70, 140 };
        public static readonly double[] LossHz = { 6000, 9500, 14000 };
        static readonly double[] HissDb = { 3, 0, -3 };
        public const double EmphasisHz = 2500;
        public const double BumpQ = 1.1;

        public static int OversampleLatency(bool high) => high ? FastOversampler.LatencyFor(HighFactor) + 1 : 0;

        readonly ParamValues values;
        float sampleRate = 48000;
        readonly FastOversampler[] os = new FastOversampler[2];
        AdaaShaper[] adaa = new AdaaShaper[2];
        Shaper shaper = new Shaper { Mode = DriveMode.Tape };
        Biquad[] pre = new Biquad[2], de = new Biquad[2], bump = new Biquad[2], loss = new Biquad[2];
        DcBlocker[] dc = new DcBlocker[2];
        readonly float[] c1 = new float[2], c2 = new float[2];        // the droop compensation's two previous samples
        readonly FracDelay[] wf = new FracDelay[2];
        Rng[] rng = new Rng[2];
        readonly float[] hissPrev = new float[2];
        DelayLineF dryRingL, dryRingR;
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        readonly float[][] x = { new float[MaxBlock], new float[MaxBlock] };
        readonly float[] dArr = new float[MaxBlock], invArr = new float[MaxBlock];
        readonly double[] bArr = new double[MaxBlock], fbArr = new double[MaxBlock], modArr = new double[MaxBlock];
        readonly float[] hissGain = new float[MaxBlock];
        readonly float[] mixArr = new float[MaxBlock];
        SmoothRamp driveRamp, biasRamp, wowRamp, flutterRamp, noiseRamp, mixRamp, output;
        double pw, pf1, pf2;
        bool high, wfOn, hissOn;
        int speed, latency, wfBase;
        double electronics, wowHz;
        double pAdd, pIn, powCoef;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        double pubAdded = -100, pubMatch = double.NaN;
        int pubLatency;

        public TapeEngine(ParamValues values)
        {
            this.values = values;
            for (int c = 0; c < 2; c++) os[c] = new FastOversampler(MaxBlock);
            Delta = new DeltaListen(FastOversampler.LatencyFor(HighFactor) + 1 + (int)Math.Ceiling(WfBaseSeconds * 192000) + 8, 48000);
            Match = new GainMatch(48000);
        }

        /// <summary>Samples the audio is delayed by.</summary>
        public int Latency => latency;

        /// <summary>Whether the hiss is on: the machine then makes sound with no input.</summary>
        public bool HissOn => hissOn;

        public double AddedDb => Volatile.Read(ref pubAdded);
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        /// <summary>The design's small-signal response in dB at <paramref name="hz"/> (the head bump and the high-frequency loss; the emphasis pair cancels).</summary>
        public static double DesignDb(int speed, double bumpDb, double lossDb, double hz, double sampleRate)
        {
            var b = new Biquad();
            var l = new Biquad();
            b.Set(BiquadType.LowShelf, BumpHz[speed], sampleRate, BumpQ, bumpDb);
            l.Set(BiquadType.HighShelf, LossHz[speed], sampleRate, 0.7071067811865476, -lossDb);
            return 20 * PortableMath.Log10(b.Magnitude(hz, sampleRate) * l.Magnitude(hz, sampleRate));
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = (int)Math.Ceiling(WfBaseSeconds * sr) * 2 + 16;
            for (int c = 0; c < 2; c++) wf[c] = new FracDelay(max);
            int ring = FastOversampler.LatencyFor(HighFactor) + 1 + max;
            dryRingL = new DelayLineF(ring);
            dryRingR = new DelayLineF(ring);
            driveRamp.Setup(GlideSeconds, sr);
            biasRamp.Setup(GlideSeconds, sr);
            wowRamp.Setup(GlideSeconds, sr);
            flutterRamp.Setup(GlideSeconds, sr);
            noiseRamp.Setup(GlideSeconds, sr);
            mixRamp.Setup(GlideSeconds, sr);
            output.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            for (int c = 0; c < 2; c++) dc[c].Set(sr, 2);
            powCoef = 1 - Detector.Coef(3.0, sr);
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            wfBase = (int)Math.Round(WfBaseSeconds * sr);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            for (int c = 0; c < 2; c++)
            {
                os[c].Reset();
                adaa[c].Reset();
                pre[c].Reset(); de[c].Reset(); bump[c].Reset(); loss[c].Reset();
                dc[c].Reset();
                c1[c] = c2[c] = 0;
                wf[c].Reset();
                rng[c] = new Rng(c == 0 ? 0x1234567u : 0x7654321u);
                hissPrev[c] = 0;
            }
            dryRingL.Reset();
            dryRingR.Reset();
            pw = pf1 = pf2 = 0;
            pAdd = pIn = 0;
            Delta.Reset();
            Match.Reset();
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            speed = values.Choice(P.Speed);
            bool nowHigh = values.Choice(P.Quality) == 1;
            if (nowHigh != high || force)
            {
                high = nowHigh;
                for (int c = 0; c < 2; c++) os[c].SetFactor(high ? HighFactor : 1);
            }
            double emph = values.Value(P.Emphasis);
            for (int c = 0; c < 2; c++)
            {
                if (emph < 0.001) { pre[c].SetIdentity(); de[c].SetIdentity(); }
                else
                {
                    pre[c].Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071067811865476, emph);
                    de[c].Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071067811865476, -emph);
                }
                bump[c].Set(BiquadType.LowShelf, BumpHz[speed], sampleRate, BumpQ, values.Value(P.Bump));
                loss[c].Set(BiquadType.HighShelf, Math.Min(LossHz[speed], 0.45 * sampleRate), sampleRate, 0.7071067811865476, -values.Value(P.Loss));
            }
            electronics = 0.25 * values.F(P.Electronics);
            double wow = values.Value(P.Wow), flutter = values.Value(P.Flutter);
            wfOn = wow > 0 || flutter > 0;
            wowHz = values.Value(P.WowRate);
            double noiseDb = values.Value(P.Noise);
            hissOn = !double.IsNegativeInfinity(noiseDb);
            Target(ref driveRamp, (float)DspMath.DbToGain(values.Value(P.Drive)), force);
            Target(ref biasRamp, (float)(0.6 * values.Value(P.Bias)), force);
            Target(ref wowRamp, (float)wow, force);
            Target(ref flutterRamp, (float)flutter, force);
            Target(ref noiseRamp, hissOn ? (float)(DspMath.DbToGain(noiseDb + HissDb[speed]) / 0.8165) : 0f, force);
            Target(ref mixRamp, values.F(P.Mix), force);
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            latency = OversampleLatency(high) + (wfOn ? wfBase : 0);
            pubLatency = latency;
            Delta.Latency = latency;
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            Array.Copy(left, x[0], n);
            Array.Copy(right, x[1], n);
            Controls(n);
            Wet(0, n);
            Wet(1, n);
            Finish(left, right, n);
        }

        /// <summary>The glides, once per sample for both channels: drive, bias (and the curve's value at the bias), modulation, hiss level, mix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Controls(int n)
        {
            double k = sampleRate / (CentsPerRatio * 2 * Math.PI);
            double w1 = 2 * Math.PI * wowHz / sampleRate, w2 = 2 * Math.PI * FlutterHz1 / sampleRate, w3 = 2 * Math.PI * FlutterHz2 / sampleRate;
            for (int i = 0; i < n; i++)
            {
                double d = driveRamp.Next();
                double b = biasRamp.Next();
                double fb = shaper.F(b);
                dArr[i] = (float)d;
                invArr[i] = (float)(1 / (d * (1 - fb * fb)));
                bArr[i] = b;
                fbArr[i] = fb;
                double wowC = wowRamp.Next(), flutC = flutterRamp.Next();
                pw += w1; if (pw > 2 * Math.PI) pw -= 2 * Math.PI;
                pf1 += w2; if (pf1 > 2 * Math.PI) pf1 -= 2 * Math.PI;
                pf2 += w3; if (pf2 > 2 * Math.PI) pf2 -= 2 * Math.PI;
                modArr[i] = wowC * k / wowHz * PortableMath.Sin(pw)
                          + flutC * k * (0.7 / FlutterHz1 * PortableMath.Sin(pf1) + 0.3 / FlutterHz2 * PortableMath.Sin(pf2 + 1.1));
                hissGain[i] = noiseRamp.Next();
                mixArr[i] = mixRamp.Next();
            }
        }

        /// <summary>One channel through the whole chain, in place in <c>x[c]</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Wet(int c, int n)
        {
            var s = x[c];
            int factor = high ? HighFactor : 1;
            for (int i = 0; i < n; i++) s[i] = pre[c].Process(s[i]);
            var up = os[c].Up(s, 0, n);
            int k = 0;
            double e = electronics;
            for (int i = 0; i < n; i++)
            {
                double d = dArr[i], inv = invArr[i], b = bArr[i], fb = fbArr[i];
                for (int j = 0; j < factor; j++, k++)
                {
                    double y = adaa[c].Process(ref shaper, up[k] * d, b, fb) * inv;
                    up[k] = (float)(y + e * y * y);
                }
            }
            os[c].Down(s, 0, n);
            double a = high ? CompA : 0;
            var ring = wf[c];
            var rg = rng[c];
            float hp = hissPrev[c];
            float z1 = c1[c], z2 = c2[c];
            for (int i = 0; i < n; i++)
            {
                float v = dc[c].Process(s[i]);
                v = de[c].Process(v);
                v = bump[c].Process(v);
                v = loss[c].Process(v);
                if (high)
                {
                    // [−a, 1 + 2a, −a] centred one sample back.
                    float cur = v;
                    v = (float)((1 + 2 * a) * z1 - a * (cur + z2));
                    z2 = z1;
                    z1 = cur;
                }
                if (hissOn)
                {
                    float w = rg.NextBipolar();
                    v += (w - hp) * hissGain[i];
                    hp = w;
                }
                if (wfOn)
                {
                    ring.Write(v);
                    v = ring.ReadCubic((float)(wfBase + modArr[i]));
                }
                s[i] = v;
            }
            rng[c] = rg;
            hissPrev[c] = hp;
            c1[c] = z1;
            c2[c] = z2;
        }

        void Finish(float[] left, float[] right, int n)
        {
            int la = latency;
            for (int i = 0; i < n; i++)
            {
                dryRingL.Write(dryL[i]);
                dryRingR.Write(dryR[i]);
                float dl = dryRingL.Read(la), dr = dryRingR.Read(la);
                float m = mixArr[i];
                float yl = dl + (x[0][i] - dl) * m, yr = dr + (x[1][i] - dr) * m;
                double al = (double)yl - dl, ar = (double)yr - dr;
                pAdd += (al * al + ar * ar - pAdd) * powCoef;
                pIn += ((double)dl * dl + (double)dr * dr - pIn) * powCoef;
                left[i] = yl;
                right[i] = yr;
            }
            Delta.Process(dryL, dryR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            OutMeter.Feed(left, right, n);
            Volatile.Write(ref pubAdded, pAdd > 1e-20 && pIn > 1e-20 ? 10 * PortableMath.Log10(pAdd / pIn) : -100);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["added_db"] = Math.Round(AddedDb, 2),
                ["latency_samples"] = Volatile.Read(ref pubLatency),
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
