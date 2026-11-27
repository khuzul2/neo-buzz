#nullable disable
// Neo Filter (docs/specs/machines.md §5): a stereo filter effect on Neo Poly's filters, with an
// envelope follower and an LFO moving the cutoff.
//  - Per channel and sample: up to four times the rate (two IIR halfbands), the drive's saturator
//    (antiderivative anti-aliased), the filter (Neo Poly's ladder, or a double-precision TPT state-
//    variable filter with a resonance limiter, one section or two), and back down.
//  - Control values live on a grid of 16-sample segments that runs across blocks: at each segment's
//    start the smoothers (20 ms, in log frequency and log Q), the envelope follower's level and the
//    LFO give each channel's cutoff, and the coefficients move in straight lines to it over the
//    segment, sample by sample, so modulation never zips and the output doesn't depend on how the
//    host splits it.
//  - The envelope follower rides the louder channel's peak (attack and release as one-pole time
//    constants); the LFO runs free, follows the song's position while it plays (Song), or restarts on
//    the envelope (Env); the right channel's LFO runs LFO spread ahead.
//  - Bounded at every setting: the ladder's loop saturates (its tanh), the SVF's damping grows with
//    its band's amplitude past full scale (and past a quarter of it at self-oscillation), and the
//    input is limited to ±1000 (NaN and ±∞ count as silence).
// At Mix 0 % and Output 0 dB the input passes untouched, bit for bit. Zero latency (the halfbands'
// phase is part of the filter's). No allocation after SetSampleRate.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Filter
{
    /// <summary>A state-variable section in double precision (TPT, after A. Simper) with a resonance limiter: past a threshold of band amplitude its damping grows with the excess squared.</summary>
    internal struct SvfCore
    {
        public double Ic1, Ic2;

        /// <summary>One sample: <paramref name="low"/> and <paramref name="band"/> (the band's peak is Q), and the damping it ran with.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(double v0, double g, double k, double threshold, out double low, out double band, out double kEff)
        {
            double e = Math.Abs(Ic1) - threshold;
            double kk = e > 0 ? k + FilterDesign.LimitGain * e * e : k;
            double a1 = 1.0 / (1.0 + g * (g + kk));
            double a2 = g * a1, a3 = g * a2;
            double v3 = v0 - Ic2;
            double v1 = a1 * Ic1 + a2 * v3;
            double v2 = Ic2 + a2 * Ic1 + a3 * v3;
            Ic1 = 2 * v1 - Ic1;
            Ic2 = 2 * v2 - Ic2;
            low = v2;
            band = v1;
            kEff = kk;
        }

        public void Reset() { Ic1 = Ic2 = 0; }

        public void Flush()
        {
            Ic1 = DspMath.Flush(Ic1);
            Ic2 = DspMath.Flush(Ic2);
        }
    }

    internal sealed class FilterEngine
    {
        public const int MaxBlock = 256;
        internal const int Segment = 16;
        internal const int Os = FilterDesign.Oversampling;
        const double SmoothSeconds = 0.02, SwitchSeconds = 0.005;
        /// <summary>The input is limited to this (+60 dBFS): every state stays finite whatever comes in.</summary>
        const float InputLimit = 1000f;

        sealed class Channel
        {
            public IirHalfband Up1, Up2, Down1, Down2;
            public Ladder Ladder;
            public SvfCore Svf1, Svf2, Svf3;
            // The drive's antiderivative anti-aliasing: the last input, its scaled value and √(1 + u²).
            public double XPrev, UPrev, FPrev = 1;
            // The filter's prewarped frequency and damping (or loop gain), moving in straight lines over a segment.
            public double G, GStep, K, KStep;
            public bool LadderSet;     // the ladder's coefficients follow G and K
            // Vowel type: the three formants' prewarped frequency, damping and signed gain, moving in straight lines over a segment.
            public readonly double[] FG = new double[3], FGStep = new double[3], FK = new double[3], FKStep = new double[3], FA = new double[3], FAStep = new double[3];
            public double VowelPos;    // 0 … 1 now (the modulation's included), for the editor
            public double VowelShift;  // octaves now, for the editor
            public double Cutoff;      // Hz, for the editor
            public float Lfo;          // −1 … 1, for the editor

            public void Reset()
            {
                Up1.Reset(); Up2.Reset(); Down1.Reset(); Down2.Reset();
                Ladder.Reset();
                Svf1.Reset();
                Svf2.Reset();
                Svf3.Reset();
                XPrev = UPrev = 0;
                FPrev = 1;
                LadderSet = false;
            }

            public void Flush()
            {
                Up1.Flush(); Up2.Flush(); Down1.Flush(); Down2.Flush();
                Ladder.Flush();
                Svf1.Flush();
                Svf2.Flush();
                Svf3.Flush();
                XPrev = DspMath.Flush(XPrev);
                UPrev = DspMath.Flush(UPrev);
            }
        }

        readonly ParamValues values;
        readonly FxStage fx;
        readonly Channel[] channels = { new Channel(), new Channel() };
        double sampleRate = 48000;
        IirHalfbandDesign[] stages;

        /// <summary>Beats per minute, for LFO sync (the machine sets it every block).</summary>
        public double Tempo = 120;
        double tempoApplied = -1;

        /// <summary>Where the song is at the block's start (the machine sets it every block; Song retrig).</summary>
        public FilterTransport Transport;

        FilterSetting s;
        // The filter in use and the one asked for: a change fades the filter out over 5 ms, switches and fades it back in.
        FilterType activeType;
        bool activeSteep;
        float fade = 1, fadeStep;
        LinearRamp mix, gain;
        Smoother cutLog, qLog, osc, driveDb, envOct, envSens, lfoOct, vowelPos, voiceSm, vowelMod;
        // The vowel design's scratch (control rate only): frequencies, dampings and gains of the three formants.
        readonly double[] vf = new double[3], vk = new double[3], va = new double[3];
        /// <summary>The Vowel type's SVF sections never limit: a formant is linear (its input is limited to ±1000, the damping to 1/250).</summary>
        const double VowelThreshold = 1e9;
        double segC;
        int segmentLeft;
        float threshold;
        double driveD = 1, driveShare;
        // The envelope follower (the louder channel's peak), its coefficients and its onset detector (Env retrig).
        float env, envAttack, envRelease;
        double envLevel;
        bool armed = true;
        // The LFO: whole cycles and the phase within one.
        long cycle;
        double frac;
        int quietSamples;

        /// <summary>What Amount scales (docs/specs/machines.md, "Mix and Amount").</summary>
        public static readonly AmountRule[] AmountRules =
        {
            // Low-pass types open towards 20 kHz and high-pass types towards 20 Hz as Amount falls (octaves scale); band types keep their cutoff.
            AmountRule.Fun(P.Cutoff, (raw, a) =>
            {
                double c = raw.Value(P.Cutoff);
                int type = raw.Choice(P.Type);
                if (type == 0 || type == 1 || type == 2) return 20000 * PortableMath.Pow(c / 20000, a);
                if (type == 4) return 20 * PortableMath.Pow(c / 20, a);
                if (type == 7) return VowelDesign.ReferenceHz * PortableMath.Pow(c / VowelDesign.ReferenceHz, a);   // the formants' shift in octaves
                return c;
            }),
            // Resonance of the band, notch and peak types scales in octaves from the lowest Q.
            AmountRule.Fun(P.Resonance, (raw, a) =>
            {
                double q = raw.Value(P.Resonance);
                int type = raw.Choice(P.Type);
                if (type == 7) return VowelDesign.ReferenceQ * PortableMath.Pow(q / VowelDesign.ReferenceQ, a);    // the formants' bandwidth in octaves from the table's
                return type == 3 || type == 6 ? 0.5 * PortableMath.Pow(q / 0.5, a) : q;
            }),
            AmountRule.Lin(P.Drive, 0), AmountRule.Lin(P.EnvAmount, 0), AmountRule.Lin(P.LfoDepth, 0),
        };

        public FilterEngine(ParamValues values)
        {
            fx = new FxStage(values, P.Amount, -1, AmountRules);
            this.values = fx.Effective;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>No latency: the dry signal is never delayed (the halfbands' little phase belongs to the filter's).</summary>
        public int Latency => 0;

        public double SampleRate => sampleRate;

        /// <summary>The resampling stages the engine runs (the design's response includes them).</summary>
        internal IirHalfbandDesign[] Stages => stages;

        public void SetSampleRate(float sr)
        {
            fx.SetSampleRate(sr);
            sampleRate = sr;
            stages = FilterDesign.Halfbands(sr);
            foreach (var c in channels)
            {
                c.Up1 = new IirHalfband(stages[0]);
                c.Down1 = new IirHalfband(stages[0]);
                c.Up2 = new IirHalfband(stages[1]);
                c.Down2 = new IirHalfband(stages[1]);
            }
            mix.Setup(SmoothSeconds, sr);
            gain.Setup(SmoothSeconds, sr);
            segC = 1 - PortableMath.Exp(-Segment / (SmoothSeconds * sr));
            fadeStep = (float)(1 / (SwitchSeconds * sr));
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            fx.Reset();
            foreach (var c in channels) c.Reset();
            env = 0;
            envLevel = 0;
            armed = true;
            cycle = 0;
            frac = s.LfoPhase;
            quietSamples = 0;
            segmentLeft = 0;
            Settle();
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty && Tempo == tempoApplied) return;
            tempoApplied = Tempo;
            s = FilterDesign.From(values, Tempo);
            Ramp(ref mix, (float)s.Mix, force);
            Ramp(ref gain, (float)s.Gain, force);
            cutLog.Target = (float)PortableMath.Log2(s.CutoffHz);
            qLog.Target = (float)PortableMath.Log2(s.Q);
            osc.Target = s.SelfOsc ? 1f : 0f;
            driveDb.Target = (float)s.DriveDb;
            envOct.Target = (float)s.EnvOctaves;
            envSens.Target = (float)s.EnvSensitivityDb;
            lfoOct.Target = (float)s.LfoOctaves;
            vowelPos.Target = (float)s.VowelPos;
            voiceSm.Target = (float)s.Voice;
            vowelMod.Target = (float)s.VowelMod;
            envAttack = (float)(1 - PortableMath.Exp(-1 / (Math.Max(1e-5, s.EnvAttack) * sampleRate)));
            envRelease = (float)(1 - PortableMath.Exp(-1 / (Math.Max(1e-5, s.EnvRelease) * sampleRate)));
            if (force)
            {
                activeType = s.Type;
                activeSteep = s.Steep;
                fade = 1;
                Settle();
            }
            values.Clean();
        }

        static void Ramp(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        // The smoothers at their targets and the coefficients where they lead (a start, or a new sample rate).
        void Settle()
        {
            cutLog.Reset(cutLog.Target);
            qLog.Reset(qLog.Target);
            osc.Reset(osc.Target);
            driveDb.Reset(driveDb.Target);
            envOct.Reset(envOct.Target);
            envSens.Reset(envSens.Target);
            lfoOct.Reset(lfoOct.Target);
            vowelPos.Reset(vowelPos.Target);
            voiceSm.Reset(voiceSm.Target);
            vowelMod.Reset(vowelMod.Target);
            segmentLeft = 0;
            Controls(0, settle: true);
        }

        static void Step(ref Smoother m, double c)
        {
            m.Value += (m.Target - m.Value) * (float)c;
            if (m.Settled) m.Value = m.Target;
        }

        // The start of a segment: the smoothers one segment on, the envelope and the LFO now, each channel's cutoff and the
        // coefficients the segment moves to.
        void NextSegment(int offset)
        {
            Step(ref cutLog, segC);
            Step(ref qLog, segC);
            Step(ref osc, segC);
            Step(ref driveDb, segC);
            Step(ref envOct, segC);
            Step(ref envSens, segC);
            Step(ref lfoOct, segC);
            Step(ref vowelPos, segC);
            Step(ref voiceSm, segC);
            Step(ref vowelMod, segC);
            Controls(offset, settle: false);
        }

        void Controls(int offset, bool settle)
        {
            // The envelope, 0 … 1: full when the peak reaches −Sensitivity dBFS.
            envLevel = Math.Min(1.0, env * DspMath.DbToGain((double)envSens.Value));
            Lfo(offset, settle);
            double q = PortableMath.Exp2(qLog.Value);
            double w = osc.Value;
            bool ladder = FilterDesign.IsLadder(activeType);
            double k = ladder ? FilterDesign.LadderLoop(q, w) : FilterDesign.SvfDamping(q, w);
            threshold = (float)(FilterDesign.LimitThreshold + (FilterDesign.OscThreshold - FilterDesign.LimitThreshold) * w);
            double d = DspMath.DbToGain((double)driveDb.Value);
            double share = FilterDesign.DriveShare(driveDb.Value);
            for (int c = 0; c < 2; c++)
            {
                var ch = channels[c];
                double hz = PortableMath.Exp2(cutLog.Value + envOct.Value * envLevel + lfoOct.Value * ch.Lfo);
                hz = FilterDesign.Reach(hz, sampleRate);
                ch.Cutoff = hz;
                if (activeType == FilterType.Vowel) VowelControls(ch, envOct.Value * envLevel + lfoOct.Value * ch.Lfo, settle);
                else
                {
                    double g = FilterDesign.Prewarp(hz, sampleRate);
                    if (settle)
                    {
                        ch.G = g;
                        ch.K = k;
                        ch.GStep = ch.KStep = 0;
                    }
                    else
                    {
                        ch.GStep = (g - ch.G) / (Segment * Os);
                        ch.KStep = (k - ch.K) / (Segment * Os);
                        if (ch.GStep == 0) ch.G = g;
                        if (ch.KStep == 0) ch.K = k;
                    }
                }
                // The drive's history rescaled to the new drive, so its anti-aliasing stays exact across the change.
                if (d != driveD || settle)
                {
                    ch.UPrev = d * ch.XPrev;
                    ch.FPrev = Math.Sqrt(1 + ch.UPrev * ch.UPrev);
                }
            }
            driveD = d;
            driveShare = share;
        }

        // The Vowel type's control values for a channel: the vowel (the smoothed control plus the modulation's share of it), the shift of every
        // formant in octaves (Cutoff against 1 kHz, within ±2, plus the rest of the modulation), the bandwidth scale from Resonance; each
        // formant's frequency, damping and gain, and the straight lines the coefficients take to them over the segment.
        void VowelControls(Channel ch, double modOct, bool settle)
        {
            double vm = vowelMod.Value;
            double shift = Math.Clamp(Math.Clamp(cutLog.Value - VowelDesign.ReferenceLog2, -2.0, 2.0) + modOct * (1 - vm), -4.0, 4.0);
            double pos = Math.Clamp(vowelPos.Value + 0.25 * modOct * vm, 0.0, 1.0);
            ch.VowelPos = pos;
            ch.VowelShift = shift;
            ch.Cutoff = VowelDesign.ReferenceHz * PortableMath.Exp2(shift);
            VowelDesign.Design(pos, voiceSm.Value, shift, VowelDesign.BandwidthScale(PortableMath.Exp2(qLog.Value)), sampleRate, vf, vk, va);
            for (int i = 0; i < VowelDesign.Formants; i++)
            {
                double g = FilterDesign.Prewarp(vf[i], sampleRate);
                if (settle)
                {
                    ch.FG[i] = g; ch.FK[i] = vk[i]; ch.FA[i] = va[i];
                    ch.FGStep[i] = ch.FKStep[i] = ch.FAStep[i] = 0;
                }
                else
                {
                    double n = Segment * Os;
                    ch.FGStep[i] = (g - ch.FG[i]) / n;
                    ch.FKStep[i] = (vk[i] - ch.FK[i]) / n;
                    ch.FAStep[i] = (va[i] - ch.FA[i]) / n;
                    if (ch.FGStep[i] == 0) ch.FG[i] = g;
                    if (ch.FKStep[i] == 0) ch.FK[i] = vk[i];
                    if (ch.FAStep[i] == 0) ch.FA[i] = va[i];
                }
            }
        }

        // The LFO at this segment: its phase (free, from the song's position, or restarted by the envelope) and each channel's value.
        void Lfo(int offset, bool settle)
        {
            double rate = s.LfoHz;
            if (s.Retrig == LfoRetrig.Song && Transport.Playing && Transport.SamplesPerTick > 0)
            {
                double beats = Transport.Beats(offset);
                double cycles = s.LfoSynced ? beats / s.LfoBeats : beats * 60.0 / Math.Max(1, Tempo) * rate;
                cycles += s.LfoPhase;
                double whole = Math.Floor(cycles);
                cycle = (long)whole;
                frac = cycles - whole;
            }
            else if (!settle)
            {
                frac += rate * Segment / sampleRate;
                if (frac >= 1)
                {
                    double whole = Math.Floor(frac);
                    cycle += (long)whole;
                    frac -= whole;
                }
            }
            if (s.Retrig == LfoRetrig.Env)
            {
                if (armed && envLevel > 0.5)
                {
                    armed = false;
                    cycle++;
                    frac = s.LfoPhase;
                }
                else if (envLevel < 0.25) armed = true;
            }
            for (int c = 0; c < 2; c++)
            {
                double f = frac + (c == 1 ? s.LfoSpread : 0);
                long n = cycle;
                if (f >= 1)
                {
                    f -= 1;
                    n++;
                }
                channels[c].Lfo = (float)FilterDesign.Wave(s.LfoShape, n, f);
            }
        }

        /// <summary>Processes a stereo block in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            fx.Begin(left, right, n);
            ProcessCore(left, right, n);
            fx.End(left, right, n, Latency);
        }

        void ProcessCore(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            bool dryOnly = mix.At(0f) && gain.At(1f);
            var L = channels[0];
            var R = channels[1];
            float loudest = 0, input = 0;
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    NextSegment(i);
                    segmentLeft = Segment;
                }
                segmentLeft--;
                float xl = left[i], xr = right[i];
                float cl = Clean(xl), cr = Clean(xr);

                // The envelope follower: the louder channel's peak.
                float a = MathF.Max(MathF.Abs(cl), MathF.Abs(cr));
                env += (a - env) * (a > env ? envAttack : envRelease);
                if (a > input) input = a;

                // A change of filter: fade out, switch, fade back in.
                if (s.Type != activeType || s.Steep != activeSteep)
                {
                    fade -= fadeStep;
                    if (fade <= 0)
                    {
                        fade = 0;
                        Switch();
                    }
                }
                else if (fade < 1) fade = MathF.Min(1, fade + fadeStep);

                float yl = Run(L, cl), yr = Run(R, cr);
                float wl = yl * fade, wr = yr * fade;
                float p = MathF.Max(MathF.Abs(wl), MathF.Abs(wr));
                if (p > loudest) loudest = p;
                float m = mix.Next(), g = gain.Next();
                if (dryOnly) continue;
                left[i] = (xl + (wl - xl) * m) * g;
                right[i] = (xr + (wr - xr) * m) * g;
            }
            L.Flush();
            R.Flush();
            env = DspMath.Flush(env);
            quietSamples = input < 1e-7f && loudest < 1e-7f ? Math.Min(quietSamples + n, int.MaxValue / 2) : 0;
            OutMeter.Feed(left, right, n);
        }

        /// <summary>NaN and ±∞ as silence, and the input limited to ±1000, so every state stays finite.</summary>
        static float Clean(float x) => float.IsNaN(x) ? 0f : Math.Clamp(x, -InputLimit, InputLimit);

        // The new filter: its states start clean (the one the setting left keeps its own; the SVF's first section is shared).
        void Switch()
        {
            bool wasLadder = FilterDesign.IsLadder(activeType);
            bool ladder = FilterDesign.IsLadder(s.Type);
            bool wasVowel = activeType == FilterType.Vowel, vowel = s.Type == FilterType.Vowel;
            foreach (var c in channels)
            {
                // To or from the formants: all three sections start clean.
                if (wasVowel != vowel)
                {
                    c.Svf1.Reset();
                    c.Svf2.Reset();
                    c.Svf3.Reset();
                }
                c.LadderSet = false;
                if (ladder && !wasLadder) c.Ladder.Reset();
                if (!ladder && wasLadder) c.Svf1.Reset();
                if (!ladder && s.Steep && !(activeSteep && !wasLadder)) c.Svf2.Reset();
            }
            activeType = s.Type;
            activeSteep = s.Steep;
            // The loop gain or damping for the new family at once (they mean different things).
            double q = PortableMath.Exp2(qLog.Value);
            double k = ladder ? FilterDesign.LadderLoop(q, osc.Value) : FilterDesign.SvfDamping(q, osc.Value);
            foreach (var c in channels)
            {
                c.K = k;
                c.KStep = 0;
            }
            // The formants for the new type at once (the segment's lines start from them).
            if (vowel)
                foreach (var c in channels) VowelControls(c, envOct.Value * envLevel + lfoOct.Value * c.Lfo, true);
        }

        // One base-rate sample of a channel: up four times, drive and filter each, back down.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Run(Channel c, float x)
        {
            c.Up1.Up(x, out float a, out float b);
            c.Up2.Up(a, out float a0, out float a1);
            c.Up2.Up(b, out float b0, out float b1);
            float y0 = Core(c, a0), y1 = Core(c, a1), y2 = Core(c, b0), y3 = Core(c, b1);
            float ya = c.Down2.Down(y0, y1), yb = c.Down2.Down(y2, y3);
            return c.Down1.Down(ya, yb);
        }

        // One sample at four times the rate: the drive's saturator (anti-aliased), the coefficients one step on, the filter.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Core(Channel c, float input)
        {
            double x = input;
            if (driveShare > 0)
            {
                double d = driveD;
                double u = d * x;
                double f = Math.Sqrt(1 + u * u);
                double sat = (u + c.UPrev) / (d * (f + c.FPrev));
                double avg = 0.5 * (x + c.XPrev);
                c.XPrev = x;
                c.UPrev = u;
                c.FPrev = f;
                // The saturator's anti-aliased output stands in for the mean of the last two samples; the linear rest
                // passes as it is, so small signals see the filter alone at any drive.
                x += driveShare * (sat - avg);
            }
            else c.XPrev = x;

            c.G += c.GStep;
            c.K += c.KStep;
            switch (activeType)
            {
                case FilterType.Ladder24:
                case FilterType.Ladder12:
                    if (c.GStep != 0 || c.KStep != 0 || !c.LadderSet)
                    {
                        c.Ladder.Set((float)c.G, (float)(c.K / FilterDesign.LadderSelfOscK));
                        c.LadderSet = true;
                    }
                    return c.Ladder.Process((float)x, 1f, activeType == FilterType.Ladder12);
                case FilterType.Vowel:
                {
                    // Three band-passes in parallel at the formants, each with its unity-peak scaling (damping times the band) and signed gain.
                    for (int i = 0; i < VowelDesign.Formants; i++)
                    {
                        c.FG[i] += c.FGStep[i];
                        c.FK[i] += c.FKStep[i];
                        c.FA[i] += c.FAStep[i];
                    }
                    c.Svf1.Step(x, c.FG[0], c.FK[0], VowelThreshold, out _, out double b1, out _);
                    c.Svf2.Step(x, c.FG[1], c.FK[1], VowelThreshold, out _, out double b2, out _);
                    c.Svf3.Step(x, c.FG[2], c.FK[2], VowelThreshold, out _, out double b3, out _);
                    return (float)(c.FA[0] * c.FK[0] * b1 + c.FA[1] * c.FK[1] * b2 + c.FA[2] * c.FK[2] * b3);
                }
                default:
                {
                    double y = Section(ref c.Svf1, x, c.G, c.K);
                    if (activeSteep) y = Section(ref c.Svf2, y, c.G, c.K);
                    return (float)y;
                }
            }
        }

        // One SVF section in the type in use.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Section(ref SvfCore svf, double x, double g, double k)
        {
            svf.Step(x, g, k, threshold, out double low, out double band, out double kk);
            switch (activeType)
            {
                case FilterType.LowPass: return low;
                case FilterType.BandPass: return band;
                case FilterType.HighPass: return x - kk * band - low;
                case FilterType.Notch: return x - kk * band;
                default: return x + (2 - kk) * band;
            }
        }

        // ---- for the machine, the editor and the tests ------------------------------------------

        /// <summary>Whether input and output have been silent for a quarter second (the filter's tail is gone; self-oscillation never is).</summary>
        public bool Idle => quietSamples > 0.25 * sampleRate;

        /// <summary>The setting as the parameters ask for it now.</summary>
        internal FilterSetting Setting => s;

        /// <summary>A channel's cutoff now (Hz, the modulation's included), for the editor.</summary>
        public double Cutoff(int channel) => channels[channel].Cutoff;

        /// <summary>A channel's vowel now (0 … 1, the modulation's included) and the formants' shift in octaves, for the editor (Vowel type).</summary>
        public double VowelPosition(int channel) => channels[channel].VowelPos;
        public double VowelShiftNow(int channel) => channels[channel].VowelShift;

        /// <summary>A channel's LFO now (−1 … 1).</summary>
        public float LfoValue(int channel) => channels[channel].Lfo;

        /// <summary>The envelope now, 0 … 1 (as it moves the cutoff), and the follower's raw level (linear peak).</summary>
        public double EnvelopeLevel => envLevel;
        internal float Follower => env;

        /// <summary>The LFO's phase (cycles), its whole cycles, and the Q and self-oscillation share the filter runs with now.</summary>
        internal double LfoPhase => frac;
        internal long LfoCycle => cycle;
        public double QNow => PortableMath.Exp2(qLog.Value);
        public double OscNow => osc.Value;

        /// <summary>The filter type running now (it lags a change by the 5 ms fade).</summary>
        internal FilterType ActiveType => activeType;

        /// <summary>Whether a state holds a subnormal number.</summary>
        internal bool HoldsSubnormals()
        {
            foreach (var c in channels)
                if (double.IsSubnormal(c.Svf1.Ic1) || double.IsSubnormal(c.Svf1.Ic2) || double.IsSubnormal(c.Svf2.Ic1) || double.IsSubnormal(c.Svf2.Ic2)
                    || double.IsSubnormal(c.Svf3.Ic1) || double.IsSubnormal(c.Svf3.Ic2)
                    || double.IsSubnormal(c.XPrev) || double.IsSubnormal(c.UPrev)) return true;
            return float.IsSubnormal(env);
        }

        /// <summary>Whether any state is NaN or infinite.</summary>
        internal bool HoldsNonFinite()
        {
            foreach (var c in channels)
                if (!double.IsFinite(c.Svf1.Ic1) || !double.IsFinite(c.Svf1.Ic2) || !double.IsFinite(c.Svf2.Ic1) || !double.IsFinite(c.Svf2.Ic2)
                    || !double.IsFinite(c.Svf3.Ic1) || !double.IsFinite(c.Svf3.Ic2)
                    || !double.IsFinite(c.G) || !double.IsFinite(c.K) || !double.IsFinite(c.UPrev)) return true;
            return !float.IsFinite(env);
        }
    }
}
