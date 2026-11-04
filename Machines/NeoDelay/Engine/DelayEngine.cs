#nullable disable
// Neo Delay (docs/specs/machines.md §5): a stereo delay with two lines, routed as Stereo (each line
// its own side), Ping-pong (the input, mono, into the left line; each line's echo into the other) or
// Cross (each side's input into its own line, each line's echo into the other).
//  - Each sample a line is read at its delay (linear interpolation), the echo passes the loop's
//    filters (the high cut, two convex one-poles; the low cut, a Butterworth high-pass, blended in),
//    and what enters the line, the input plus Feedback times the echo the routing gives it, passes
//    the tape saturator. The echoes are the wet signal: Width, then ducking by the input's envelope,
//    then Mix against the dry signal, then Output.
//  - A line's delay settles on a whole sample, so a settled line reads exactly. A change glides (a
//    tape-like bend: at most 20 % slower or faster, smoothed in and out) when it is small, and
//    crossfades between the old and the new delay over 40 ms when it is larger than 50 ms.
//  - The wow moves the read either way (the right line a quarter cycle on), in straight lines over
//    16-sample segments that run across blocks, as the ducking gain and the coefficients do, so the
//    output doesn't depend on how the host splits it.
//  - Bounded at every setting, and decaying with no input (DelayDesign says why).
// At Mix 0 % and Output 0 dB the input passes untouched, bit for bit. No allocation after
// SetSampleRate.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Delay
{
    /// <summary>A power-of-two ring of floats with a fractional read in double precision (long lines keep their fraction exact).</summary>
    internal sealed class DelayLine
    {
        readonly float[] data;
        readonly int mask;
        int pos;

        public DelayLine(int maxDelaySamples)
        {
            int size = 4;
            while (size < maxDelaySamples + 4) size <<= 1;
            data = new float[size];
            mask = size - 1;
        }

        public int Length => data.Length;

        public void Write(float x)
        {
            pos = (pos + 1) & mask;
            data[pos] = x;
        }

        /// <summary>The sample <paramref name="delay"/> samples before the next one written (delay ≥ 1): linear between its two neighbours.</summary>
        public float Read(double delay)
        {
            double back = delay - 1;
            int i = (int)back;
            float f = (float)(back - i);
            float a = data[(pos - i) & mask];
            if (f == 0) return a;
            float b = data[(pos - i - 1) & mask];
            return a + (b - a) * f;
        }

        public float At(int index) => data[index & mask];

        public void Reset()
        {
            Array.Clear(data, 0, data.Length);
            pos = 0;
        }
    }

    internal sealed class DelayEngine
    {
        public const int MaxBlock = 256;
        internal const int Segment = 16;
        /// <summary>Glides: the time constant towards the new delay, the bend's smoothing, the fastest bend (20 % of speed either way).</summary>
        internal const double GlideSeconds = 0.2, BendSeconds = 0.03, MaxBend = 0.2;
        /// <summary>A change larger than this crossfades (over FadeSeconds) instead of gliding.</summary>
        internal const double JumpSeconds = 0.05, FadeSeconds = 0.04;
        const double SmoothSeconds = 0.02, DepthSeconds = 0.05, SwitchSeconds = 0.01, DuckSeconds = 0.003;

        sealed class Line
        {
            public DelayLine Data;
            public double Tau, Target, TauB, Velocity;   // samples
            public bool Fading;
            public float Fade;                            // tap B's share
            public float Lp1, Lp2;                        // the high cut's one-poles
            public double S1, S2;                         // the low cut's state-variable states
            public float Mod, ModStep, ModTarget;         // the wow (samples), ramped over a segment

            public void Clear()
            {
                Data.Reset();
                Lp1 = Lp2 = Mod = ModStep = ModTarget = 0;
                S1 = S2 = 0;
                Velocity = 0;
                Fading = false;
                Fade = 0;
                Tau = TauB = Target;
            }

            public void Flush()
            {
                Lp1 = DspMath.Flush(Lp1);
                Lp2 = DspMath.Flush(Lp2);
                S1 = DspMath.Flush(S1);
                S2 = DspMath.Flush(S2);
            }
        }

        readonly ParamValues values;
        readonly Line[] lines = { new Line(), new Line() };
        float sampleRate = 48000;

        /// <summary>Beats per minute, for Sync (the machine sets it every block).</summary>
        public double Tempo = 120;
        double tempoApplied = -1;

        DelayMode mode;
        double jumpSamples, glideK, bendC, fadeStep;
        LinearRamp feedback, mix, gain, width, lowCutMix, duckGain;
        Smoother highCutA, lowCutLogG, threshold, modDepth, duckSmooth;
        float segSmoothC, segDepthC, segDuckC;
        double modRate, phase;
        int segmentLeft;
        float hpG, hpH, hpK;
        // Ducking: the input's envelope (peak), its coefficients, the depth and where it starts.
        float env, envAttack, envRelease;
        double duckDb, duckThresholdDb;
        float lastPeak;
        int quietSamples;

        public DelayEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        /// <summary>The ducking's gain reduction now (linear, 1 − gain), for the editor's meter.</summary>
        public readonly LevelMeter DuckMeter = new LevelMeter();

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int Latency => 0;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = DelayDesign.LineSamples(sr);
            foreach (var l in lines) l.Data = new DelayLine(max);
            feedback.Setup(SmoothSeconds, sr);
            mix.Setup(SmoothSeconds, sr);
            gain.Setup(SmoothSeconds, sr);
            width.Setup(SmoothSeconds, sr);
            lowCutMix.Setup(SwitchSeconds, sr);
            duckGain.Setup(Segment / (double)sr, sr);
            segSmoothC = SegmentCoeff(SmoothSeconds, sr);
            segDepthC = SegmentCoeff(DepthSeconds, sr);
            segDuckC = SegmentCoeff(DuckSeconds, sr);
            jumpSamples = JumpSeconds * sr;
            glideK = 1 / (GlideSeconds * sr);
            bendC = 1 - PortableMath.Exp(-1 / (BendSeconds * sr));
            fadeStep = 1 / (FadeSeconds * sr);
            envAttack = (float)(1 - PortableMath.Exp(-1 / (DelayDesign.DuckAttack * sr)));
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            DuckMeter.SetSampleRate(sr);
            Update(true);
            Reset();
        }

        static float SegmentCoeff(double seconds, double sr) => (float)(1 - PortableMath.Exp(-Segment / (seconds * sr)));

        public void Reset()
        {
            foreach (var l in lines) l.Clear();
            env = 0;
            quietSamples = 0;
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty && Tempo == tempoApplied) return;
            tempoApplied = Tempo;
            var s = DelayDesign.From(values, Tempo, sampleRate);
            mode = s.Mode;
            Retarget(lines[0], DelayDesign.Samples(s.SecondsL, sampleRate), force);
            Retarget(lines[1], DelayDesign.Samples(s.SecondsR, sampleRate), force);
            Ramp(ref feedback, (float)s.Feedback, force);
            Ramp(ref mix, (float)s.Mix, force);
            Ramp(ref gain, (float)s.Gain, force);
            Ramp(ref width, (float)s.Width, force);
            Ramp(ref lowCutMix, s.LowCutOn ? 1f : 0f, force);
            highCutA.Target = (float)s.HighCutA;
            lowCutLogG.Target = (float)PortableMath.Log(DelayDesign.LowCutG(s.LowCutHz, sampleRate));
            threshold.Target = (float)s.Threshold;
            modDepth.Target = (float)(s.ModSeconds * sampleRate);
            modRate = s.ModRate;
            duckDb = s.DuckDb;
            duckThresholdDb = s.DuckThresholdDb;
            envRelease = (float)(1 - PortableMath.Exp(-1 / (Math.Max(1e-4, s.DuckRelease) * sampleRate)));
            if (force)
            {
                highCutA.Reset(highCutA.Target);
                lowCutLogG.Reset(lowCutLogG.Target);
                threshold.Reset(threshold.Target);
                modDepth.Reset(modDepth.Target);
                duckGain.Reset(1);
                duckSmooth.Reset(1);
                segmentLeft = 0;
                Coefficients();
            }
            values.Clean();
        }

        static void Ramp(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        // A new delay for a line: at once (the start), or by a glide or a crossfade.
        void Retarget(Line l, double target, bool force)
        {
            if (force)
            {
                l.Target = l.Tau = l.TauB = target;
                l.Velocity = 0;
                l.Fading = false;
                return;
            }
            if (target == l.Target) return;
            l.Target = target;
            if (!l.Fading) Move(l);
        }

        // Starts the move to the line's target: a crossfade when it is far, else a glide (run per sample).
        void Move(Line l)
        {
            if (Math.Abs(l.Target - l.Tau) <= jumpSamples) return;
            l.TauB = l.Target;
            l.Velocity = 0;
            l.Fade = 0;
            l.Fading = true;
        }

        // The coefficients the segment runs with (the smoothers moved one segment on).
        void Coefficients()
        {
            hpG = (float)PortableMath.Exp(lowCutLogG.Value);
            hpK = (float)(1 / DelayDesign.LowCutQ);
            hpH = 1f / (1f + hpG * (hpK + hpG));
        }

        // The start of a segment: the smoothers, the wow and the ducking gain move one segment on.
        void NextSegment()
        {
            highCutA.Value += (highCutA.Target - highCutA.Value) * segSmoothC;
            lowCutLogG.Value += (lowCutLogG.Target - lowCutLogG.Value) * segSmoothC;
            threshold.Value += (threshold.Target - threshold.Value) * segSmoothC;
            modDepth.Value += (modDepth.Target - modDepth.Value) * segDepthC;
            if (highCutA.Settled) highCutA.Value = highCutA.Target;
            if (threshold.Settled) threshold.Value = threshold.Target;
            if (modDepth.Settled) modDepth.Value = modDepth.Target;
            Coefficients();
            phase += modRate * Segment / sampleRate;
            phase -= Math.Floor(phase);
            for (int c = 0; c < 2; c++)
            {
                var l = lines[c];
                float depth = Math.Min(modDepth.Value, (float)(0.5 * Math.Min(l.Tau, l.Fading ? l.TauB : l.Tau)));
                float target = depth <= 0 ? 0f : depth * (float)PortableMath.Sin(2 * Math.PI * (phase + 0.25 * c));
                l.Mod = l.ModTarget;   // the last segment's ramp, landed exactly
                l.ModTarget = target;
                l.ModStep = (target - l.Mod) * (1f / Segment);
            }
            // Ducking: the envelope now against the threshold, over a 12 dB knee below it, the gain smoothed (3 ms)
            // so that ducking never clicks.
            float g = 1;
            if (duckDb > 0)
            {
                double level = env > 1e-9f ? 20 * PortableMath.Log10(env) : -180;
                double k = Math.Clamp((level - duckThresholdDb) / DelayDesign.DuckKneeDb + 1, 0, 1);
                if (k > 0) g = (float)DspMath.DbToGain(-duckDb * k);
            }
            duckSmooth.Target = g;
            duckSmooth.Value += (g - duckSmooth.Value) * segDuckC;
            if (duckSmooth.Settled) duckSmooth.Value = g;
            duckGain.SetTarget(duckSmooth.Value);
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            bool dryOnly = mix.At(0f) && gain.At(1f);
            var L = lines[0];
            var R = lines[1];
            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    NextSegment();
                    segmentLeft = Segment;
                }
                segmentLeft--;
                float xl = left[i], xr = right[i];

                // The input's envelope (peak, NaN and infinity ignored), for ducking.
                float a = MathF.Max(MathF.Abs(xl), MathF.Abs(xr));
                if (!(a <= 1e3f)) a = float.IsNaN(a) ? 0 : 1e3f;
                env += (a - env) * (a > env ? envAttack : envRelease);

                Advance(L);
                Advance(R);
                float hc = lowCutMix.Next();
                float yl = Filter(L, Read(L), hc);
                float yr = Filter(R, Read(R), hc);
                float fb = feedback.Next();
                float t = threshold.Value;
                float ul, ur;
                switch (mode)
                {
                    case DelayMode.PingPong:
                        ul = 0.5f * (xl + xr) + fb * yr;
                        ur = fb * yl;
                        break;
                    case DelayMode.Cross:
                        ul = xl + fb * yr;
                        ur = xr + fb * yl;
                        break;
                    default:
                        ul = xl + fb * yl;
                        ur = xr + fb * yr;
                        break;
                }
                float wl = DspMath.Flush(DelayDesign.Saturate(ul, t));
                float wr = DspMath.Flush(DelayDesign.Saturate(ur, t));
                L.Data.Write(wl);
                R.Data.Write(wr);
                float p = MathF.Max(MathF.Max(MathF.Abs(wl), MathF.Abs(wr)), MathF.Max(MathF.Abs(yl), MathF.Abs(yr)));
                if (p > peak) peak = p;

                // The wet signal: width, then ducking.
                float wd = width.Next();
                float el = yl, er = yr;
                if (wd != 1f)
                {
                    float mid = 0.5f * (yl + yr), side = 0.5f * (yl - yr) * wd;
                    el = mid + side;
                    er = mid - side;
                }
                float dg = duckGain.Next();
                el *= dg;
                er *= dg;
                float m = mix.Next(), g = gain.Next();
                if (dryOnly) continue;
                left[i] = (xl + (el - xl) * m) * g;
                right[i] = (xr + (er - xr) * m) * g;
            }
            L.Flush();
            R.Flush();
            env = DspMath.Flush(env);
            lastPeak = peak;
            quietSamples = peak < 1e-7f ? Math.Min(quietSamples + n, int.MaxValue / 2) : 0;
            DuckMeter.FeedValue(1 - duckGain.Value, n);
            OutMeter.Feed(left, right, n);
        }

        // A line's delay one sample on: the crossfade, or the glide.
        void Advance(Line l)
        {
            if (l.Fading)
            {
                l.Fade += (float)fadeStep;
                if (l.Fade >= 1f)
                {
                    l.Tau = l.TauB;
                    l.Fading = false;
                    l.Fade = 0;
                    if (l.Target != l.Tau) Move(l);
                }
            }
            else if (l.Tau != l.Target || l.Velocity != 0)
            {
                // Towards the target, the bend at most MaxBend and smoothed in and out.
                double want = Math.Clamp((l.Target - l.Tau) * glideK, -MaxBend, MaxBend);
                l.Velocity += (want - l.Velocity) * bendC;
                l.Tau += l.Velocity;
                if (Math.Abs(l.Target - l.Tau) < 1e-3 && Math.Abs(l.Velocity) < 1e-5)
                {
                    l.Tau = l.Target;
                    l.Velocity = 0;
                }
            }
            l.Mod += l.ModStep;
        }

        // A line read at its delay (and the wow), or crossfaded between its two delays: a convex combination of the line's samples.
        static float Read(Line l)
        {
            float a = l.Data.Read(l.Tau + l.Mod);
            if (!l.Fading) return a;
            float b = l.Data.Read(l.TauB + l.Mod);
            return a + (b - a) * l.Fade;
        }

        // The loop's filters: the high cut (two one-poles y = x + a(y₁ − x), convex), then the low cut (a TPT
        // state-variable high-pass, Butterworth), blended in over 10 ms when it is switched on or off.
        float Filter(Line l, float r, float c)
        {
            float a = highCutA.Value;
            float y = DspMath.Flush(r + a * (l.Lp1 - r));
            l.Lp1 = y;
            y = DspMath.Flush(y + a * (l.Lp2 - y));
            l.Lp2 = y;
            double g = hpG;
            double hp = (y - (hpK + g) * l.S1 - l.S2) * hpH;
            double bp = g * hp + l.S1;
            double lp = g * bp + l.S2;
            l.S1 = g * hp + bp;
            l.S2 = g * bp + lp;
            if (c == 0f) return y;
            return y + ((float)hp - y) * c;
        }

        // ---- for the machine, the editor and the tests ------------------------------------------

        /// <summary>Whether the lines have been silent for longer than the longest delay: nothing more can come out until the input plays.</summary>
        public bool Idle
        {
            get
            {
                double longest = 0;
                foreach (var l in lines) longest = Math.Max(longest, Math.Max(l.Tau, l.TauB) + Math.Abs(l.Mod));
                return quietSamples > longest + 0.1 * sampleRate;
            }
        }

        internal DelayMode Mode => mode;

        /// <summary>A line's delay now (samples; while crossfading, the one it leaves).</summary>
        internal double Delay(int line) => lines[line].Tau;

        /// <summary>A line's delay it is moving to, and whether it is crossfading.</summary>
        internal (double Target, bool Fading, double Velocity) Motion(int line) => (lines[line].Target, lines[line].Fading, lines[line].Velocity);

        /// <summary>The loop's coefficients now: the feedback, the high cut's a, the low cut's g and share, the saturator's threshold.</summary>
        internal (float Feedback, float HighCutA, float LowCutG, float LowCutMix, float Threshold) Loop =>
            (feedback.Value, highCutA.Value, hpG, lowCutMix.Value, threshold.Value);

        /// <summary>The largest |sample| written to the lines or echoed in the last block.</summary>
        internal float LastPeak => lastPeak;

        /// <summary>The ducking gain now (1: none).</summary>
        internal float DuckGain => duckGain.Value;

        /// <summary>The wow's phase (cycles), for the editor.</summary>
        internal double Phase => phase;

        /// <summary>The largest |sample| the lines hold.</summary>
        internal float LinePeak()
        {
            float m = 0;
            foreach (var l in lines)
                for (int k = 0; k < l.Data.Length; k++) m = MathF.Max(m, MathF.Abs(l.Data.At(k)));
            return m;
        }

        /// <summary>Whether a line or a filter state holds a subnormal number.</summary>
        internal bool HoldsSubnormals()
        {
            foreach (var l in lines)
            {
                if (float.IsSubnormal(l.Lp1) || float.IsSubnormal(l.Lp2) || double.IsSubnormal(l.S1) || double.IsSubnormal(l.S2)) return true;
                for (int k = 0; k < l.Data.Length; k++) if (float.IsSubnormal(l.Data.At(k))) return true;
            }
            return float.IsSubnormal(env);
        }

        /// <summary>Whether any state is NaN or infinite.</summary>
        internal bool HoldsNonFinite()
        {
            foreach (var l in lines)
            {
                if (!float.IsFinite(l.Lp1) || !float.IsFinite(l.Lp2) || !double.IsFinite(l.S1) || !double.IsFinite(l.S2)) return true;
                for (int k = 0; k < l.Data.Length; k++) if (!float.IsFinite(l.Data.At(k))) return true;
            }
            return !float.IsFinite(env);
        }
    }
}
