#nullable disable
// Neo Lo-Fi (docs/specs/neo-lofi.md): zero latency. Clean room: textbook quantisation and sample-and-hold, our own filters,
// noise and delay line; no code or table from any other machine.
//
//   input → motion (wow, flutter, drift: one modulated read of a short delay line, linear interpolation)
//         → anti-alias (12th-order Butterworth low-pass at 0.40 of the hold rate, a crossfade in and out)
//         → sample-and-hold (a hold clock of fractional period, optional jitter, the same instants for both channels)
//         → bit reduction (mid-tread, 2^n levels: the codes −2^(n−1) … 2^(n−1)−1 of step 2^(1−n); fractional n by the step; optional TPDF dither)
//         → hiss (white, tilted a little, RMS in dBFS; optionally following the input and gated) and crackle (Poisson pops)
//         → tone (a low-pass to the left of centre, a high shelf to the right) → mix with the dry input → output.
//
// The motion's read delay is d = Σ A·(1 − cos φ) ≥ 0, so the read never looks into the future: there is no baseline delay and the
// machine reports no latency. The price: while wow, flutter or drift is on, the wet signal runs behind the dry by between 0 and 2·ΣA
// (a few ms for light settings), which is the effect (a gentle chorus when mixed with the dry), not a delay to compensate.
// A stage that is off (Bits at 24, Rate at or above the host's, Hiss, Crackle, Wow, Flutter and Drift at 0, Tone in the middle)
// is skipped, and with every stage off, or Mix at 0, at Output 0 dB the buffer is not touched: the input passes bit for bit.
// Every control glides (SmoothRamp); the motion's depth glides for 0.4 s, since any change of a delay bends the pitch.
// Allocation-free after SetSampleRate; basic maths from PortableMath only.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.LoFi
{
    internal sealed class LoFiEngine
    {
        public const int MaxBlock = 256;

        /// <summary>Samples of delay the host compensates: none, at every setting.</summary>
        public const int Latency = 0;

        const int Sub = 16;                       // coefficients and powers are refreshed every Sub samples
        const double GlideSeconds = 0.03;
        const double MotionGlideSeconds = 0.4;
        const double Ln2 = 0.6931471805599453;
        const int AaSections = 6;                 // 12th order
        const double AaCutoff = 0.40;             // of the hold rate
        const double WowMinHz = 0.3;
        const double Log2Min = 7.643856189774724;   // log2 of 200 Hz, the lowest hold rate
        const double FlutterHz1 = 7.3, FlutterHz2 = 11.9, FlutterW1 = 0.65, FlutterW2 = 0.35;
        static readonly double[] DriftHz = { 0.12, 0.19, 0.31 };
        static readonly double[] DriftW = { 0.5, 0.3, 0.2 };
        const double HissColour = 0.6;            // one-pole low-pass coefficient of the hiss: a gentle tilt
        const double GateOpen = 3.1623e-4, GateClose = 1.9e-4;   // -70 and -74.4 dBFS
        const double FollowRef = 0.1;             // the hiss is at its setting when the input is at -20 dBFS or above

        readonly ParamValues values;
        float sampleRate = 48000;
        float offLog2 = 15.55f;                   // log2 of the host's rate: Rate at or above it is off

        SmoothRamp bitsR, rateR, aaR, jitR, hissR, dens, crackLev, wowR, wowRateR, flutR, driftR, toneR, mixR, outR;
        bool dither, aaOn, follow;

        sealed class Channel
        {
            public readonly Biquad[] Aa = new Biquad[AaSections];
            public Biquad ToneLp, ToneHs;
            public FracDelay Delay;
            public float Held, HissLp, Pop, Env;
            public Rng Dith, Hiss, Crack;
            public float Gate;      // 0 … 1, smoothed
            public bool GateIsOpen;
        }

        readonly Channel[] ch = { new Channel(), new Channel() };

        // Per-sample controls for the block, shared by both channels.
        readonly float[] aaW = new float[MaxBlock];
        readonly bool[] tick = new bool[MaxBlock];
        readonly float[] delayArr = new float[MaxBlock];
        readonly float[] stepArr = new float[MaxBlock], invArr = new float[MaxBlock], kMinArr = new float[MaxBlock], kMaxArr = new float[MaxBlock];
        readonly float[] hissArr = new float[MaxBlock], popP = new float[MaxBlock], popAmp = new float[MaxBlock];
        readonly float[] toneW = new float[MaxBlock], mixArr = new float[MaxBlock], outArr = new float[MaxBlock];

        // Hold clock.
        double counter, period = 1, jitterScale;
        Rng clockRng = new Rng(0xC10C4);
        float rateLog;                            // the ramp's current value, for the filter's coefficient refresh
        float lastAaLog = float.NaN;

        // Motion phases (radians).
        double pWow, pF1, pF2;
        readonly double[] pDrift = new double[3];

        // Tone.
        float lastToneT = float.NaN;
        float toneT;
        double popDecay = 0.95;
        double hissNorm = 1;

        bool idle = true;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>The smoothed word length now (for tests: a stepped parameter changes it smoothly).</summary>
        public float CurrentBits { get; private set; } = 24;

        /// <summary>The smoothed hold rate now, in Hz.</summary>
        public float CurrentRate { get; private set; }

        public LoFiEngine(ParamValues values)
        {
            this.values = values;
            ch[0].Dith = new Rng(0xD17E01); ch[1].Dith = new Rng(0xD17E02);
            ch[0].Hiss = new Rng(0x4155A1); ch[1].Hiss = new Rng(0x4155B2);
            ch[0].Crack = new Rng(0xC4AC01); ch[1].Crack = new Rng(0xC4AC02);
            SetSampleRate(48000);
        }

        /// <summary>True while the machine passes the buffer untouched (every stage off, or Mix 0, and Output 0 dB).</summary>
        public bool Idle => idle;

        /// <summary>Whether the hiss is on: the machine then makes sound with no input.</summary>
        public bool MakesSound => !hissR.At(0) || !dens.At(0);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            offLog2 = (float)(PortableMath.Log(sr) / Ln2);
            bitsR.Setup(GlideSeconds, sr); rateR.Setup(GlideSeconds, sr); aaR.Setup(0.03, sr); jitR.Setup(GlideSeconds, sr);
            hissR.Setup(GlideSeconds, sr); dens.Setup(GlideSeconds, sr); crackLev.Setup(GlideSeconds, sr);
            wowR.Setup(MotionGlideSeconds, sr); wowRateR.Setup(MotionGlideSeconds, sr);
            flutR.Setup(MotionGlideSeconds, sr); driftR.Setup(MotionGlideSeconds, sr);
            toneR.Setup(GlideSeconds, sr); mixR.Setup(GlideSeconds, sr); outR.Setup(GlideSeconds, sr);
            int max = (int)Math.Ceiling(0.15 * sr) + 8;
            for (int c = 0; c < 2; c++) ch[c].Delay = new FracDelay(max);
            popDecay = PortableMath.Exp(-1.0 / (0.0004 * sr));
            hissNorm = 1.0 / Math.Sqrt(HissColour / (2 - HissColour));
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            for (int c = 0; c < 2; c++)
            {
                var x = ch[c];
                for (int k = 0; k < AaSections; k++) x.Aa[k].Reset();
                x.ToneLp.Reset(); x.ToneHs.Reset();
                x.Delay.Reset();
                x.Held = x.HissLp = x.Pop = x.Env = 0;
                x.Gate = 0; x.GateIsOpen = false;
            }
            counter = 0;
            lastAaLog = float.NaN;
            lastToneT = float.NaN;
        }

        // ---------------------------------------------------------------- parameters

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            double bits = values.Value(P.Bits);
            double rate = values.Value(P.Rate);
            double hiss = values.Value(P.Hiss);
            double a = values.Value(P.Amount) / 100.0;       // the effect strength, 0 … 2
            bool unity = Math.Abs(a - 1) < 1e-9;             // 100 %: the settings exactly as they stand
            bool rateTop = values[P.Rate] >= values.Table[P.Rate].Max;   // the top of the range is off whatever the host's rate

            // Amount scales each active stage on a perceptual scale (docs/specs/neo-lofi.md section 2a): 0 is every stage off.
            double bitsEff = unity ? bits : ScaledBits(bits, a);
            double rateOct = rateTop ? offLog2 : PortableMath.Log(Math.Min(rate, sampleRate)) / Ln2;   // log2 of the hold rate
            double rateEffLog = unity || rateTop ? rateOct : ScaledRateLog2(rateOct, offLog2, a);
            double jitter = values.F(P.Jitter);
            double hissGain = double.IsNegativeInfinity(hiss) ? 0.0 : DspMath.DbToGain(hiss);
            double density = values.Value(P.Crackle);
            double popDb = values.Value(P.CrackleLevel);
            double wow = values.Value(P.Wow), flutter = values.Value(P.Flutter), drift = values.Value(P.Drift);
            double tone = values.Value(P.Tone);
            if (!unity)
            {
                jitter = Math.Min(1.0, jitter * a);
                if (!double.IsNegativeInfinity(hiss))
                {
                    // dB above the floor of the control (-90): linear in dB, capped at -12 dBFS however far it is pushed
                    double db = a <= 0 ? double.NegativeInfinity : Math.Min(-90 + a * (hiss + 90), Math.Max(hiss, -12.0));
                    hissGain = double.IsNegativeInfinity(db) ? 0.0 : DspMath.DbToGain(db);
                }
                density = Math.Min(density * a, 200.0);
                popDb = Math.Min(-60 + a * (popDb + 60), Math.Max(popDb, -6.0));
                wow = Math.Min(wow * a, 100.0);
                flutter = Math.Min(flutter * a, 100.0);
                drift = Math.Min(drift * a, 50.0);
                tone = Math.Min(1.34, Math.Max(-1.25, tone * a));   // the cut in octaves, the lift in dB, both scaled
            }
            Target(ref bitsR, (float)bitsEff, force);
            Target(ref rateR, (float)rateEffLog, force);
            dither = values.On(P.Dither);
            aaOn = values.On(P.AntiAlias);
            follow = values.On(P.HissFollow);
            Target(ref jitR, (float)jitter, force);
            Target(ref hissR, (float)hissGain, force);
            Target(ref dens, (float)density, force);
            Target(ref crackLev, (float)DspMath.DbToGain(popDb), force);
            Target(ref wowR, (float)wow, force);
            Target(ref wowRateR, (float)values.Value(P.WowRate), force);
            Target(ref flutR, (float)flutter, force);
            Target(ref driftR, (float)drift, force);
            Target(ref toneR, (float)tone, force);
            Target(ref mixR, values.F(P.Mix), force);
            Target(ref outR, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            values.Clean();
        }

        /// <summary>The word length at an Amount (<paramref name="a"/>, 1 = 100 %): the bits removed from 24 scale with it, and above 100 % each extra 100 % removes up to 6 more; never below 2 bits (or the setting, when it is lower).</summary>
        public static double ScaledBits(double bits, double a)
        {
            if (bits >= 24) return bits;
            double removed = 24 - bits;
            double scaled = a <= 1 ? removed * a : removed + (a - 1) * Math.Min(removed, 6.0);
            return Math.Max(Math.Min(bits, 2.0), 24 - scaled);
        }

        /// <summary>log2 of the hold rate at an Amount: the octaves below the host's rate (<paramref name="offLog2"/>) scale with it, down to 200 Hz at most.</summary>
        public static double ScaledRateLog2(double rateLog2, double offLog2, double a) =>
            Math.Max(Math.Min(rateLog2, Log2Min), offLog2 - a * (offLog2 - rateLog2));

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v); else r.SetTarget(v);
        }

        bool RateOff => rateR.At(offLog2);
        bool StagesOff => bitsR.At(24f) && RateOff && hissR.At(0f) && dens.At(0f) && Math.Abs(ch[0].Pop) + Math.Abs(ch[1].Pop) == 0
            && wowR.At(0f) && flutR.At(0f) && driftR.At(0f) && toneR.At(0f);

        // ---------------------------------------------------------------- processing

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            bool nothing = outR.At(1f) && (mixR.At(0f) || StagesOff);
            if (nothing)
            {
                idle = true;
                OutMeter.Feed(left, right, n);
                return;
            }
            if (idle)
            {
                // Leaving rest: the stages start from silence, the way they were left.
                Reset();
                idle = false;
            }
            for (int pos = 0; pos < n; pos += Sub)
            {
                int m = Math.Min(Sub, n - pos);
                bool motion = !(wowR.At(0f) && flutR.At(0f) && driftR.At(0f));
                if (motion && !wasMotion) { ch[0].Delay.Reset(); ch[1].Delay.Reset(); }
                wasMotion = motion;
                bool bits = !bitsR.At(24f);
                bool hold = !RateOff;
                Controls(m, hold);
                RefreshFilters(hold);
                Run(0, left, pos, m, motion, bits, hold);
                Run(1, right, pos, m, motion, bits, hold);
            }
            OutMeter.Feed(left, right, n);
        }

        bool wasMotion;


        /// <summary>The control glides for <paramref name="m"/> (at most Sub) samples, once per sample for both channels.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Controls(int m, bool hold)
        {
            double fs = sampleRate;
            double kw = fs * Ln2 / 1200.0;        // samples·rad/s per cent: A = cents·kw/ω
            for (int i = 0; i < m; i++)
            {
                // ---- crush: bits and rate
                float b = bitsR.Next();
                float rl = rateR.Next();
                aaR.SetTarget(aaOn && hold ? 1f : 0f);
                float aw = aaR.Next();
                float jit = jitR.Next();
                if (i == 0)
                {
                    CurrentBits = b;
                    double N = b == MathF.Floor(b) ? Math.ScaleB(1.0, (int)b - 1) : PortableMath.Exp((b - 1) * Ln2);   // 2^(b-1) = 1 / step
                    cachedInv = (float)N;
                    cachedStep = (float)(1.0 / N);
                    cachedKMax = (float)(Math.Ceiling(N) - 1);
                    cachedKMin = (float)(-Math.Ceiling(N));
                    rateLog = rl;
                    CurrentRate = (float)PortableMath.Exp(rl * Ln2);
                    period = Math.Max(1.0, fs / CurrentRate);
                    jitterScale = 0.25 * Math.Min(1.0, period - 1.0);
                }
                stepArr[i] = cachedStep; invArr[i] = cachedInv; kMinArr[i] = cachedKMin; kMaxArr[i] = cachedKMax;
                aaW[i] = aw;
                // The hold clock: a sample is taken whenever the counter has run out. Period 1 takes every sample (the identity).
                counter -= 1;
                if (counter <= 0)
                {
                    tick[i] = true;
                    double p = period;
                    if (p > 1.0000001) p *= 1 + jit * jitterScale * clockRng.NextBipolar();
                    counter += Math.Max(1.0, p);
                }
                else tick[i] = false;

                // ---- noise
                hissArr[i] = hissR.Next();
                popP[i] = dens.Next() / (float)fs;
                popAmp[i] = crackLev.Next();

                // ---- tone and output
                float t = toneR.Next();
                toneT = t;
                toneW[i] = Math.Min(1f, Math.Abs(t) * 10f);
                mixArr[i] = mixR.Next();
                outArr[i] = outR.Next();

                // ---- motion: d = Σ A (1 − cos φ) ≥ 0, A = peak cents · kw / ω
                float wc = wowR.Next(), fc = flutR.Next(), dc = driftR.Next(), wr = wowRateR.Next();
                if (wc == 0f && fc == 0f && dc == 0f && wowR.Target == 0f && flutR.Target == 0f && driftR.Target == 0f)
                {
                    delayArr[i] = 0f;
                    continue;
                }
                double ww = 2 * Math.PI * wr;
                pWow += ww / fs; if (pWow > 2 * Math.PI) pWow -= 2 * Math.PI;
                pF1 += 2 * Math.PI * FlutterHz1 / fs; if (pF1 > 2 * Math.PI) pF1 -= 2 * Math.PI;
                pF2 += 2 * Math.PI * FlutterHz2 / fs; if (pF2 > 2 * Math.PI) pF2 -= 2 * Math.PI;
                double dly = wc * kw / ww * (1 - PortableMath.Cos(pWow))
                           + fc * kw * (FlutterW1 / (2 * Math.PI * FlutterHz1) * (1 - PortableMath.Cos(pF1))
                                      + FlutterW2 / (2 * Math.PI * FlutterHz2) * (1 - PortableMath.Cos(pF2)));
                for (int k = 0; k < 3; k++)
                {
                    double wk = 2 * Math.PI * DriftHz[k];
                    pDrift[k] += wk / fs; if (pDrift[k] > 2 * Math.PI) pDrift[k] -= 2 * Math.PI;
                    dly += dc * DriftW[k] * kw / wk * (1 - PortableMath.Cos(pDrift[k]));
                }
                delayArr[i] = (float)dly;
            }
        }

        float cachedStep = 1, cachedInv = 1, cachedKMin, cachedKMax;

        /// <summary>The anti-alias and tone coefficients, when what they depend on moved.</summary>
        void RefreshFilters(bool hold)
        {
            if (aaOn && hold && (float.IsNaN(lastAaLog) || Math.Abs(rateLog - lastAaLog) > 1e-4f))
            {
                lastAaLog = rateLog;
                double hz = Math.Min(AaCutoff * CurrentRate, 0.45 * sampleRate);
                for (int c = 0; c < 2; c++)
                    for (int k = 0; k < AaSections; k++)
                        ch[c].Aa[k].Set(BiquadType.LowPass, hz, sampleRate, ButterworthQ(k));
            }
            if (float.IsNaN(lastToneT) || Math.Abs(toneT - lastToneT) > 1e-5f)
            {
                lastToneT = toneT;
                double t = toneT;
                double fc = Math.Min(20000.0 * PortableMath.Pow(600.0 / 20000.0, Math.Min(1.25, Math.Max(0.0, -t))), 0.45 * sampleRate);
                for (int c = 0; c < 2; c++)
                {
                    ch[c].ToneLp.Set(BiquadType.LowPass, fc, sampleRate, 0.7071067811865476);
                    ch[c].ToneHs.Set(BiquadType.HighShelf, 3000, sampleRate, 0.7071067811865476, 9.0 * Math.Max(0.0, t));
                }
            }
        }

        /// <summary>Q of section k of a 12th-order Butterworth.</summary>
        static double ButterworthQ(int k) => 1.0 / (2 * PortableMath.Sin((2 * k + 1) * Math.PI / (4.0 * AaSections)));

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Run(int c, float[] s, int pos, int m, bool motion, bool bits, bool hold)
        {
            var x = ch[c];
            double decay = popDecay;
            bool hissOn = hissArr[0] != 0f || hissArr[m - 1] != 0f;
            bool crackOn = popP[0] > 0f || popP[m - 1] > 0f || x.Pop != 0f;
            bool toneOn = toneW[0] != 0f || toneW[m - 1] != 0f;
            var rngD = x.Dith; var rngH = x.Hiss; var rngC = x.Crack;
            float held = x.Held, hp = x.HissLp, pop = x.Pop, env = x.Env, gate = x.Gate;
            bool gateOpen = x.GateIsOpen;
            float norm = (float)hissNorm * 1.7320508f;
            for (int j = 0; j < m; j++)
            {
                float dry = s[pos + j];
                float y = dry;

                // ---- the hiss's envelope follower runs on the input (gate with hysteresis, about 10 ms)
                if (hissOn && follow)
                {
                    float a = Math.Abs(dry);
                    env = a > env ? a : env * 0.9998f;
                    if (env < 1e-20f) env = 0f;
                    if (env > GateOpen) gateOpen = true; else if (env < GateClose) gateOpen = false;
                    gate += ((gateOpen ? 1f : 0f) - gate) * 0.002f;
                    if (gate < 1e-20f) gate = 0f;
                }

                // ---- motion
                if (motion)
                {
                    x.Delay.Write(y);
                    y = x.Delay.ReadLinear(delayArr[j]);
                }

                // ---- anti-alias, sample-and-hold
                if (hold)
                {
                    float w = aaW[j];
                    if (aaOn || w > 0f)
                    {
                        float f = y;
                        var aa = x.Aa;
                        for (int k = 0; k < AaSections; k++) f = aa[k].Process(f);
                        y += (DspMath.Flush(f) - y) * w;
                    }
                    if (tick[j]) held = y;
                    y = held;
                }

                // ---- bit reduction
                if (bits)
                {
                    double v = y * (double)invArr[j] + 0.5;
                    if (dither) v += rngD.NextUnipolar() - rngD.NextUnipolar();
                    double k = Math.Floor(v);
                    if (k < kMinArr[j]) k = kMinArr[j]; else if (k > kMaxArr[j]) k = kMaxArr[j];
                    double q = k * stepArr[j];
                    y = (float)(q < -1 ? -1 : q > 1 ? 1 : q);
                }

                // ---- hiss and crackle
                if (hissOn)
                {
                    hp += (float)HissColour * (rngH.NextBipolar() - hp);
                    float amp = hissArr[j];
                    if (follow) amp *= gate * Math.Min(1f, env * (1f / (float)FollowRef));
                    y += hp * norm * amp;
                }
                if (crackOn)
                {
                    if (rngC.NextUnipolar() < popP[j])
                    {
                        float amp = popAmp[j] * (0.4f + 0.6f * rngC.NextUnipolar());
                        pop += rngC.NextBipolar() < 0 ? -amp : amp;
                    }
                    y += pop;
                    pop = (float)(pop * decay);
                    if (Math.Abs(pop) < 1e-9f) pop = 0f;
                }

                // ---- tone
                if (toneOn)
                {
                    float lp = DspMath.Flush(x.ToneLp.Process(y)), hs = DspMath.Flush(x.ToneHs.Process(y));
                    y = toneT < 0 ? y + (lp - y) * toneW[j] : hs;
                }

                // ---- mix, output
                float mx = mixArr[j];
                s[pos + j] = (mx >= 1f ? y : dry + (y - dry) * mx) * outArr[j];
            }
            for (int k = 0; k < AaSections; k++) x.Aa[k].Flush();
            x.ToneLp.Flush(); x.ToneHs.Flush();
            x.Held = held; x.HissLp = hp; x.Pop = pop; x.Env = env; x.Gate = gate; x.GateIsOpen = gateOpen;
            x.Dith = rngD; x.Hiss = rngH; x.Crack = rngC;
        }

        /// <summary>The bit reduction alone, at <paramref name="bits"/> (no dither): for the editor's picture and tests.</summary>
        public static double Quantize(double x, double bits)
        {
            double n = bits == Math.Floor(bits) ? Math.ScaleB(1.0, (int)bits - 1) : PortableMath.Exp((bits - 1) * Ln2);
            double k = Math.Floor(x * n + 0.5);
            k = Math.Min(Math.Max(k, -Math.Ceiling(n)), Math.Ceiling(n) - 1);
            double q = k / n;
            return Math.Min(1, Math.Max(-1, q));
        }
    }
}
