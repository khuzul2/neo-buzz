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
            public SvfCore Svf1, Svf2;
            // The drive's antiderivative anti-aliasing: the last input, its scaled value and √(1 + u²).
            public double XPrev, UPrev, FPrev = 1;
            // The filter's prewarped frequency and damping (or loop gain), moving in straight lines over a segment.
            public double G, GStep, K, KStep;
            public bool LadderSet;     // the ladder's coefficients follow G and K
            public double Cutoff;      // Hz, for the editor
            public float Lfo;          // −1 … 1, for the editor

            public void Reset()
            {
                Up1.Reset(); Up2.Reset(); Down1.Reset(); Down2.Reset();
                Ladder.Reset();
                Svf1.Reset();
                Svf2.Reset();
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
                XPrev = DspMath.Flush(XPrev);
                UPrev = DspMath.Flush(UPrev);
            }
        }

        readonly ParamValues values;
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
        Smoother cutLog, qLog, osc, driveDb, envOct, envSens, lfoOct;
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

        public FilterEngine(ParamValues values)
        {
            this.values = values;
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
            foreach (var c in channels)
            {
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
                    || double.IsSubnormal(c.XPrev) || double.IsSubnormal(c.UPrev)) return true;
            return float.IsSubnormal(env);
        }

        /// <summary>Whether any state is NaN or infinite.</summary>
        internal bool HoldsNonFinite()
        {
            foreach (var c in channels)
                if (!double.IsFinite(c.Svf1.Ic1) || !double.IsFinite(c.Svf1.Ic2) || !double.IsFinite(c.Svf2.Ic1) || !double.IsFinite(c.Svf2.Ic2)
                    || !double.IsFinite(c.G) || !double.IsFinite(c.K) || !double.IsFinite(c.UPrev)) return true;
            return !float.IsFinite(env);
        }
    }
}
