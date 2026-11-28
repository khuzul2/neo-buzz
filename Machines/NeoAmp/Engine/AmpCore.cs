#nullable disable
// Neo Amp (docs/specs/neo-amp.md §3): the amp's signal chain, at the oversampled rate, for one voicing:
//
//   in ─► [up ×F] ─► stage 1 ─► … ─► stage N ─► tone stack ─► power stage ─► Presence, Resonance ─► DC block ─► [down ×F] ─► out
//
// A stage is a triode-like gain stage: a coupling high-pass, a gain, a static asymmetric curve with a bias, a polarity flip and two
// one-pole low-passes (the stage's loading). The curve is an algebraic sigmoid, f(u) = u / sqrt(1 + (u/k)²) for u < 0 and
// u / sqrt(1 + u²) for u ≥ 0 (k > 1: the negative half swings further, which adds even harmonics), with a closed-form antiderivative
// F(u) = k² (sqrt(1 + (u/k)²) − 1) and sqrt(1 + u²) − 1, so each stage and the power stage run first-order antiderivative
// anti-aliasing (ADAA: y[n] = (F(u[n]) − F(u[n−1])) / (u[n] − u[n−1])) in double precision. The Gain knob adds its dB in front of one
// stage (the voicing says which) and a bright filter lifts the highs while the knob is low. The tone stack (AmpTone.cs) is followed
// by a level makeup that keeps its loss from moving the level as the knobs turn. The power stage has Sag, a slow follower of its own
// output that lowers the supply (the curve's scale), which compresses a sustained chord and lets it bloom back. Presence and
// Resonance are the feedback shelves (a high and a low shelf after the curve).
//
// Literature: Pakarinen and Yeh, "A review of digital techniques for modeling vacuum-tube guitar amplifiers", Computer Music Journal
// 33(2), 2009 (the stage chain, the tone stack, the power stage and its supply); Parker, Zavalishin and Le Bivic, "Reducing the
// aliasing of nonlinear waveshaping using continuous-time convolution", DAFx-16, Brno, 2016 and Bilbao, Esqueda, Parker and Välimäki,
// "Antiderivative antialiasing for memoryless nonlinearities", IEEE Signal Processing Letters 24(7), 2017 (ADAA).
//
// Allocation-free after construction; portable maths only; the sums in a fixed order.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Amp
{
    /// <summary>The slow controls of one block, 0 … 1 (after Amount).</summary>
    internal struct AmpSettings
    {
        public double Bass, Mid, Treble, Sag, Presence, Resonance;
    }

    internal sealed class AmpCore
    {
        public const int MaxBlock = 256;
        const int Chunk = 32;

        struct Stage
        {
            public double PrevU, PrevF;
            public bool Primed;
            public double Hp, Lp1, Lp2, Lp3;
            public double HpA, LpA;
            public double Gain, K, Bias, FBias, InvK;
        }

        readonly FastOversampler os = new FastOversampler(MaxBlock);
        readonly Stage[] st = new Stage[4];
        readonly double[] num = new double[5], den = new double[5];
        ToneFilter tone;
        Biquad pres, res;
        DcBlocker dc;
        Voicing v;
        int voicingIndex = -1;
        int factor = 4;
        double rate = 48000, osRate = 192000;

        // The bright filter (a one-pole high-pass added back).
        double brLp, brA;
        // The tone stack's smoothed knobs and level makeup; the power stage's.
        double sBass = 0.5, sMid = 0.5, sTreble = 0.5, sSag, sPres, sRes;
        double tBass = double.NaN, tMid, tTreble;
        double toneComp = 1;
        bool toneDirty = true;
        double smoothK;
        double pPrevU, pPrevF;
        bool pPrimed;
        double pK = 1, pInvK = 1;
        double sagEnv, sagAtt, sagRel;
        double pl1, pl2, plA;
        double presApplied = double.NaN, resApplied = double.NaN;
        double levelLin = 1;
        // The power stage's drive at Master 60 %: its level is made up by the square root of the drive's distance from this, so Master moves the
        // character more than the loudness.
        double powerRef = 1;
        readonly double[] pcomp = new double[Chunk];
        double lastBass = double.NaN, lastMid = double.NaN, lastTreble = double.NaN;
        bool primed;

        public AmpCore() { Configure(0, 4, 48000); }

        public int VoicingIndex => voicingIndex;
        public int Factor => factor;
        public Voicing Definition => v;

        /// <summary>Latency of the up-process-down path at the base rate (padded to a constant, FastOversampler's, by the engine).</summary>
        public int Latency => os.Latency;

        /// <summary>Sets the voicing and the oversampling factor and clears every state: the sound starts from rest.</summary>
        public void Configure(int voicing, int oversampling, double sampleRate)
        {
            voicingIndex = Math.Clamp(voicing, 0, Voicing.All.Length - 1);
            v = Voicing.All[voicingIndex];
            factor = oversampling >= 8 ? 8 : oversampling >= 4 ? 4 : 2;
            rate = sampleRate;
            osRate = sampleRate * factor;
            os.SetFactor(factor);
            os.Reset();
            for (int k = 0; k < 4; k++)
            {
                var s = default(Stage);
                if (k < v.Stages)
                {
                    s.Gain = DspMath.DbToGain(v.StageDb[k]);
                    s.K = v.Asym[k];
                    s.InvK = 1.0 / s.K;
                    s.Bias = v.Bias[k];
                    s.FBias = Curve(s.Bias, s.K, s.InvK);
                    s.HpA = 1.0 - PortableMath.Exp(-2 * Math.PI * Math.Max(1.0, v.HpHz[k]) / osRate);
                    s.LpA = 1.0 - PortableMath.Exp(-2 * Math.PI * Math.Min(v.LpHz[k], osRate * 0.45) / osRate);
                }
                st[k] = s;
            }
            brA = 1.0 - PortableMath.Exp(-2 * Math.PI * v.BrightHz / osRate);
            brLp = 0;
            pK = v.PowerAsym;
            pInvK = 1.0 / pK;
            sagAtt = 1.0 - PortableMath.Exp(-1.0 / (0.04 * osRate));
            sagRel = 1.0 - PortableMath.Exp(-1.0 / (0.25 * osRate));
            plA = 1.0 - PortableMath.Exp(-2 * Math.PI * Math.Min(v.PowerLpHz, osRate * 0.45) / osRate);
            pl1 = pl2 = 0;
            sagEnv = 0;
            pPrevU = pPrevF = 0; pPrimed = false;
            tone.Reset();
            dc.Set(osRate, 8);
            dc.Reset();
            pres.Reset(); res.Reset();
            presApplied = resApplied = double.NaN;
            lastBass = lastMid = lastTreble = double.NaN;
            toneDirty = true;
            smoothK = 1.0 - PortableMath.Exp(-(double)Chunk / (0.03 * rate));
            levelLin = DspMath.DbToGain(v.LevelDb);
            powerRef = DspMath.DbToGain(v.PowerMinDb + (v.PowerMaxDb - v.PowerMinDb) * 0.6);
            primed = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Curve(double u, double k, double invK)
        {
            if (u >= 0) return u / Math.Sqrt(1.0 + u * u);
            double w = u * invK;
            return u / Math.Sqrt(1.0 + w * w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double F(double u, double k, double invK)
        {
            if (u >= 0) return Math.Sqrt(1.0 + u * u) - 1.0;
            double w = u * invK;
            return k * k * (Math.Sqrt(1.0 + w * w) - 1.0);
        }

        /// <summary>The stage curve as drawn by the editor and tested: output for an input in the curve's own units.</summary>
        public static double StageCurve(double u, double k) => Curve(u, k, 1.0 / k);

        /// <summary>The tone stack's current level makeup, linear (for tests).</summary>
        public double ToneMakeup => toneComp;

        public ToneFilter ToneFilter => tone;

        public void Reset()
        {
            os.Reset();
            for (int k = 0; k < 4; k++)
            {
                st[k].PrevU = st[k].PrevF = 0; st[k].Primed = false; st[k].Hp = st[k].Lp1 = st[k].Lp2 = st[k].Lp3 = 0;
            }
            brLp = 0; sagEnv = 0; pPrimed = false; pPrevU = pPrevF = 0;
            tone.Reset(); dc.Reset(); pres.Reset(); res.Reset();
        }

        /// <summary>
        /// One block (at most <see cref="MaxBlock"/> samples, in order): <paramref name="input"/> → <paramref name="output"/> (not the same array).
        /// <paramref name="gain"/> is the Gain knob's linear gain per sample, <paramref name="power"/> the power stage's drive per sample.
        /// </summary>
        public void Process(float[] input, float[] output, int n, in AmpSettings s, float[] gain, float[] power, double gainNorm)
        {
            if (!primed)
            {
                sBass = s.Bass; sMid = s.Mid; sTreble = s.Treble; sSag = s.Sag; sPres = s.Presence; sRes = s.Resonance;
                primed = true;
                toneDirty = true;
            }
            for (int c0 = 0; c0 < n; c0 += Chunk)
            {
                int m = Math.Min(Chunk, n - c0);
                // Slow controls: smoothed once per chunk.
                sBass += (s.Bass - sBass) * smoothK;
                sMid += (s.Mid - sMid) * smoothK;
                sTreble += (s.Treble - sTreble) * smoothK;
                sSag += (s.Sag - sSag) * smoothK;
                sPres += (s.Presence - sPres) * smoothK;
                sRes += (s.Resonance - sRes) * smoothK;
                if (Math.Abs(s.Bass - sBass) < 1e-7) sBass = s.Bass;
                if (Math.Abs(s.Mid - sMid) < 1e-7) sMid = s.Mid;
                if (Math.Abs(s.Treble - sTreble) < 1e-7) sTreble = s.Treble;
                if (Math.Abs(s.Sag - sSag) < 1e-7) sSag = s.Sag;
                if (Math.Abs(s.Presence - sPres) < 1e-7) sPres = s.Presence;
                if (Math.Abs(s.Resonance - sRes) < 1e-7) sRes = s.Resonance;
                UpdateTone();
                UpdateShelves();

                float[] buf = os.Up(input, c0, m);
                int total = m * factor;
                double brightK = v.BrightDb > 0 ? (DspMath.DbToGain(v.BrightDb) - 1.0) * (1.0 - gainNorm) : 0;
                double sagAmt = sSag * v.SagDepth;
                int gs = v.GainStage;
                int stages = v.Stages;
                double comp = toneComp;
                int f = factor;
                for (int i = 0; i < m; i++) pcomp[i] = 1.0 / Math.Sqrt(power[c0 + i] / powerRef);
                for (int i = 0; i < total; i++)
                {
                    int j = c0 + i / f;
                    double x = buf[i];
                    for (int k = 0; k < stages; k++)
                    {
                        ref Stage q = ref st[k];
                        // coupling high-pass in front of the stage
                        q.Hp += q.HpA * (x - q.Hp);
                        x -= q.Hp;
                        double g = q.Gain;
                        if (k == gs) g *= gain[j];
                        double u = x * g + q.Bias;
                        double Fu = F(u, q.K, q.InvK);
                        double y;
                        if (!q.Primed) { y = Curve(u, q.K, q.InvK); q.Primed = true; }
                        else
                        {
                            double d = u - q.PrevU;
                            if (d > 1e-7 || d < -1e-7) y = (Fu - q.PrevF) / d;
                            else y = Curve(0.5 * (u + q.PrevU), q.K, q.InvK);
                        }
                        q.PrevU = u; q.PrevF = Fu;
                        y = q.FBias - y;   // polarity flip, bias removed
                        q.Lp1 += q.LpA * (y - q.Lp1);
                        q.Lp2 += q.LpA * (q.Lp1 - q.Lp2);
                        q.Lp3 += q.LpA * (q.Lp2 - q.Lp3);
                        x = q.Lp3;
                        if (k == gs - 1 && brightK > 0)
                        {
                            brLp += brA * (x - brLp);
                            x += (x - brLp) * brightK;
                        }
                    }
                    // tone stack and its makeup
                    double t = tone.Process(x) * comp;
                    pl1 += plA * (t - pl1);
                    pl2 += plA * (pl1 - pl2);
                    t = pl2;
                    // power stage with sag
                    double supply = 1.0 - sagAmt * (sagEnv / (sagEnv + 0.6));
                    double up = t * power[j] / supply;
                    double Fp = F(up, pK, pInvK);
                    double yp;
                    if (!pPrimed) { yp = Curve(up, pK, pInvK); pPrimed = true; }
                    else
                    {
                        double d = up - pPrevU;
                        if (d > 1e-7 || d < -1e-7) yp = (Fp - pPrevF) / d;
                        else yp = Curve(0.5 * (up + pPrevU), pK, pInvK);
                    }
                    pPrevU = up; pPrevF = Fp;
                    yp *= supply * pcomp[i / f];
                    double ay = yp < 0 ? -yp : yp;
                    sagEnv += (ay - sagEnv) * (ay > sagEnv ? sagAtt : sagRel);
                    float o = pres.Process((float)yp);
                    o = res.Process(o);
                    buf[i] = dc.Process(o);
                }
                os.Down(output, c0, m);
                Flush();
            }
            double lv = levelLin;
            for (int i = 0; i < n; i++) output[i] = (float)(output[i] * lv);
        }

        void Flush()
        {
            for (int k = 0; k < 4; k++)
            {
                ref Stage q = ref st[k];
                q.Hp = DspMath.Flush(q.Hp); q.Lp1 = DspMath.Flush(q.Lp1); q.Lp2 = DspMath.Flush(q.Lp2); q.Lp3 = DspMath.Flush(q.Lp3);
                pl1 = DspMath.Flush(pl1); pl2 = DspMath.Flush(pl2);
            }
            brLp = DspMath.Flush(brLp);
            sagEnv = DspMath.Flush(sagEnv);
            tone.Flush();
            pres.Flush(); res.Flush();
        }

        void UpdateTone()
        {
            if (!toneDirty && Math.Abs(sBass - lastBass) < 1e-5 && Math.Abs(sMid - lastMid) < 1e-5 && Math.Abs(sTreble - lastTreble) < 1e-5) return;
            lastBass = sBass; lastMid = sMid; lastTreble = sTreble;
            toneDirty = false;
            ToneFilter.Polynomials(sBass, sMid, sTreble, v.CapScale, num, den);
            tone.Set(osRate, num, den);
            // The level makeup: the stack's mean power over the guitar's band, so its loss does not move the level as the knobs turn.
            double acc = 0;
            for (int i = 0; i < MakeupFreqs.Length; i++)
            {
                double mag = tone.Magnitude(MakeupFreqs[i], osRate);
                acc += mag * mag;
            }
            double rms = Math.Sqrt(acc / MakeupFreqs.Length);
            toneComp = Math.Min(100.0, 1.0 / Math.Max(rms, 1e-9));
        }

        static readonly double[] MakeupFreqs = { 120, 240, 480, 960, 1920, 3840 };

        void UpdateShelves()
        {
            if (Math.Abs(sPres - presApplied) > 1e-4 || double.IsNaN(presApplied))
            {
                presApplied = sPres;
                pres.Set(BiquadType.HighShelf, v.PresHz, osRate, 0.7, v.PresDb * sPres);
            }
            if (Math.Abs(sRes - resApplied) > 1e-4 || double.IsNaN(resApplied))
            {
                resApplied = sRes;
                res.Set(BiquadType.LowShelf, v.ResHz, osRate, 0.7, v.ResDb * sRes);
            }
        }
    }
}
