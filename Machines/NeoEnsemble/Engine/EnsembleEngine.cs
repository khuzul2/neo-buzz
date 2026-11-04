#nullable disable
// Neo Ensemble (docs/specs/machines.md §5): chorus, flanger and phaser in one. Each channel has its
// own loop; the right channel's LFO runs Spread ahead of the left's.
//  - Chorus, Flanger: 1–4 voices read a delay line at delays swept exponentially around Delay.
//    Read with a cubic (Catmull-Rom) and averaged, through the Tone one-pole, they are the wet
//    signal (times √voices, so the moving part keeps its power). Read with straight lines and
//    averaged, through Tone, the saturator and Feedback, they go back into the line. The input
//    enters the line through a 10 Hz high-pass, so positive feedback can't boom below the audible
//    range (a high-pass inside the loop would break its bound: see EnsembleDesign).
//  - Phaser: 2–12 first-order allpasses (normalised lattices) at a frequency swept exponentially
//    around Centre; their output is the wet signal and, one sample later, through Tone, a 10 Hz
//    high-pass, the saturator and Feedback, goes back into their input.
// The loop can't grow at any setting, however fast it is swept (EnsembleDesign says why), and the
// saturator caps what goes round. The LFO and the coefficients move in straight lines over segments of 8
// samples that run across blocks, so the output doesn't depend on how the host splits it. Mix,
// Output and Feedback ramp per sample; Mode, Voices (Chorus) and Stages (Phaser) fade the wet and
// the feedback out over 5 ms, switch and fade them back in; the line is always written, so a mode
// comes back in on the input as it is. At Mix 0 % and Output 0 dB the input passes untouched, bit
// for bit.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Ensemble
{
    internal sealed class EnsembleEngine
    {
        public const int MaxBlock = 256;
        public const int MaxVoices = 4, MaxStages = 12;
        internal const int Segment = 8;
        const double GlideSeconds = 0.02, SweepSeconds = 0.05, ShapeSeconds = 0.05, FadeSeconds = 0.005;

        sealed class Channel
        {
            public FracDelay Line;
            public float WetS, LoopS, HpS, InS, Fb;
            public readonly float[] Ap = new float[MaxStages];
            public readonly float[] Tau = new float[MaxVoices], TauStep = new float[MaxVoices];
            public float K, KStep;

            public void Clear()
            {
                Line.Reset();
                WetS = LoopS = HpS = InS = Fb = 0;
                ClearStages();
            }

            /// <summary>The phaser's own state: its stages, its loop high-pass and the feedback sample.</summary>
            public void ClearStages()
            {
                HpS = Fb = 0;
                Array.Clear(Ap, 0, Ap.Length);
            }

            public void Flush()
            {
                WetS = DspMath.Flush(WetS);
                LoopS = DspMath.Flush(LoopS);
                HpS = DspMath.Flush(HpS);
                InS = DspMath.Flush(InS);
                Fb = DspMath.Flush(Fb);
                for (int k = 0; k < Ap.Length; k++) Ap[k] = DspMath.Flush(Ap[k]);
            }
        }

        readonly ParamValues values;
        readonly Channel[] ch = { new Channel(), new Channel() };
        float sampleRate = 48000;

        /// <summary>Beats per minute, for Sync (the machine sets it every block).</summary>
        public double Tempo = 120;
        double tempoApplied = -1;

        // What runs; the discrete settings change at the bottom of a fade.
        EnsembleMode mode;
        int voices = 1, stages = 4;
        EnsembleMode wantMode;
        int wantVoices = 1, wantStages = 4;
        bool switching;
        LinearRamp fade;

        // The LFO (cycles; the left channel's first voice) and the segment it is in.
        double phase, rate;
        int segmentLeft;
        Smoother log2Delay, log2Centre, depth, spread, triangle, toneA, feedback;
        float segDelayC, segSweepC, segShapeC;          // per-segment one-pole coefficients
        float hpG, inG;
        LinearRamp mix, gain;
        float wetScale = 1, invVoices = 1;
        int depthMode = -1;

        public EnsembleEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>No latency: the dry signal is never delayed.</summary>
        public int Latency => 0;

        /// <summary>The LFO's phase, cycles (the left channel's first voice), for the editor.</summary>
        public double Phase => phase;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = (int)Math.Ceiling(EnsembleDesign.MaxDelaySeconds * sr) + 8;
            foreach (var c in ch) c.Line = new FracDelay(max);
            feedback.Setup(GlideSeconds, sr);
            mix.Setup(GlideSeconds, sr);
            gain.Setup(GlideSeconds, sr);
            fade.Setup(FadeSeconds, sr);
            segDelayC = SegmentCoeff(SweepSeconds, sr);
            segSweepC = SegmentCoeff(GlideSeconds, sr);
            segShapeC = SegmentCoeff(ShapeSeconds, sr);
            hpG = (float)EnsembleDesign.OnePoleG(EnsembleDesign.LoopHighPassHz, sr);
            inG = (float)EnsembleDesign.OnePoleG(EnsembleDesign.InputHighPassHz, sr);
            depthMode = -1;
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
        }

        static float SegmentCoeff(double seconds, double sr) => (float)(1 - PortableMath.Exp(-Segment / (seconds * sr)));

        public void Reset()
        {
            foreach (var c in ch) c.Clear();
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty && Tempo == tempoApplied) return;
            tempoApplied = Tempo;
            var s = EnsembleDesign.From(values, Tempo, sampleRate);
            rate = s.Rate;
            wantMode = s.Mode;
            wantVoices = s.Mode == EnsembleMode.Chorus ? s.Voices : 1;
            wantStages = s.Stages;
            log2Delay.Target = (float)s.Log2Delay;
            log2Centre.Target = (float)s.Log2Centre;
            spread.Target = (float)s.SpreadCycles;
            triangle.Target = (float)s.Triangle;
            toneA.Target = (float)s.ToneA;
            feedback.Target = (float)s.Feedback;
            Ramp(ref mix, (float)s.Mix, force);
            Ramp(ref gain, (float)s.Gain, force);
            if (force)
            {
                mode = wantMode;
                voices = wantVoices;
                stages = wantStages;
                switching = false;
                fade.Reset(1);
                depthMode = -1;
            }
            else if (wantMode != mode || (mode == EnsembleMode.Chorus && wantVoices != voices) || (mode == EnsembleMode.Phaser && wantStages != stages))
            {
                switching = true;
                fade.SetTarget(0);
            }
            else if (!switching) stages = wantStages;   // not heard in this mode: no fade
            SetDepthTarget();
            if (force)
            {
                log2Delay.Reset(log2Delay.Target);
                log2Centre.Reset(log2Centre.Target);
                depth.Reset(depth.Target);
                spread.Reset(spread.Target);
                triangle.Reset(triangle.Target);
                toneA.Reset(toneA.Target);
                feedback.Reset(feedback.Target);
                Apply();
                segmentLeft = 0;
                Targets(jump: true);
            }
            values.Clean();
        }

        static void Ramp(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        // Depth's span depends on the mode running (an octave, two or three either side).
        void SetDepthTarget()
        {
            depth.Target = (float)(values.Value(P.Depth) * EnsembleDesign.SpanOctaves[(int)mode]);
            if (depthMode != (int)mode)
            {
                depth.Reset(depth.Target);
                depthMode = (int)mode;
            }
        }

        // The discrete settings, applied (at the start, or at the bottom of a fade).
        void Apply()
        {
            mode = wantMode;
            voices = wantVoices;
            stages = wantStages;
            invVoices = 1f / voices;
            wetScale = (float)Math.Sqrt(voices);
            SetDepthTarget();
        }

        // The start of a segment: the smoothers and the LFO move one segment on, and every voice's
        // delay (or the stages' coefficient) ramps to its value at the segment's end.
        void Targets(bool jump)
        {
            if (!jump)
            {
                log2Delay.Value += (log2Delay.Target - log2Delay.Value) * segDelayC;
                log2Centre.Value += (log2Centre.Target - log2Centre.Value) * segDelayC;
                depth.Value += (depth.Target - depth.Value) * segDelayC;
                spread.Value += (spread.Target - spread.Value) * segDelayC;
                triangle.Value += (triangle.Target - triangle.Value) * segShapeC;
                toneA.Value += (toneA.Target - toneA.Value) * segSweepC;
                phase += rate * Segment / sampleRate;
                phase -= Math.Floor(phase);
            }
            for (int c = 0; c < 2; c++)
            {
                var C = ch[c];
                if (mode == EnsembleMode.Phaser)
                {
                    double lfo = EnsembleDesign.Wave(EnsembleDesign.VoicePhase(phase, c, 0, 1, spread.Value), triangle.Value);
                    float k = (float)EnsembleDesign.StageK(EnsembleDesign.StageHz(log2Centre.Value, depth.Value, lfo, sampleRate), sampleRate);
                    if (jump) { C.K = k; C.KStep = 0; }
                    else C.KStep = (k - C.K) * (1f / Segment);
                }
                else
                {
                    for (int v = 0; v < voices; v++)
                    {
                        double lfo = EnsembleDesign.Wave(EnsembleDesign.VoicePhase(phase, c, v, voices, spread.Value), triangle.Value);
                        float tau = (float)EnsembleDesign.DelaySamples(log2Delay.Value, depth.Value, lfo, sampleRate);
                        if (jump) { C.Tau[v] = tau; C.TauStep[v] = 0; }
                        else C.TauStep[v] = (tau - C.Tau[v]) * (1f / Segment);
                    }
                }
            }
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            bool dryOnly = mix.At(0f) && gain.At(1f);
            var L = ch[0];
            var R = ch[1];
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    if (switching && fade.At(0f))
                    {
                        // The bottom of the fade, the wet and the feedback silent: switch, fade back in.
                        // The phaser's stages start clean; the delay line has kept the input.
                        if (wantMode == EnsembleMode.Phaser && mode != EnsembleMode.Phaser)
                        {
                            L.ClearStages();
                            R.ClearStages();
                        }
                        Apply();
                        Targets(jump: true);
                        switching = false;
                        fade.SetTarget(1);
                    }
                    Targets(jump: false);
                    segmentLeft = Segment;
                }
                segmentLeft--;
                float fd = fade.Next();
                float fb = feedback.Next() * fd;
                float xl = left[i], xr = right[i];
                float wl, wr;
                float ta = toneA.Value;
                if (mode == EnsembleMode.Phaser)
                {
                    wl = Phaser(L, xl, fb, ta);
                    wr = Phaser(R, xr, fb, ta);
                }
                else
                {
                    wl = Delays(L, xl, fb, ta);
                    wr = Delays(R, xr, fb, ta);
                }
                wl *= fd;
                wr *= fd;
                float m = mix.Next(), g = gain.Next();
                if (dryOnly) continue;
                left[i] = (xl + (wl - xl) * m) * g;
                right[i] = (xr + (wr - xr) * m) * g;
            }
            L.Flush();
            R.Flush();
            OutMeter.Feed(left, right, n);
        }

        // One sample of a channel's delay loop (Chorus, Flanger): the wet signal.
        float Delays(Channel C, float x, float fb, float a)
        {
            float xin = x - OnePole(x, ref C.InS, inG);
            float wet = 0, loop = 0;
            var line = C.Line;
            for (int v = 0; v < voices; v++)
            {
                float tau = C.Tau[v] + C.TauStep[v];
                C.Tau[v] = tau;
                float back = tau - 1f;
                if (back < 1f) back = 1f;   // a ramp may round an ulp under the shortest delay
                wet += line.ReadCubic(back);
                loop += line.ReadLinear(back);
            }
            // The Tone one-pole y = x + a(y₁ − x): a convex combination, so the loop can't grow.
            float w = wet * invVoices;
            w = DspMath.Flush(w + a * (C.WetS - w));
            C.WetS = w;
            float l = loop * invVoices;
            l = DspMath.Flush(l + a * (C.LoopS - l));
            C.LoopS = l;
            line.Write(DspMath.Flush(xin + EnsembleDesign.Saturate(fb * l)));
            return w * wetScale;
        }

        // One sample of a channel's allpass chain (Phaser): the wet signal.
        float Phaser(Channel C, float x, float fb, float a)
        {
            // The delay line keeps the input, for a switch to Chorus or Flanger.
            C.Line.Write(DspMath.Flush(x - OnePole(x, ref C.InS, inG)));
            float k = C.K + C.KStep;
            C.K = k;
            // A normalised lattice: [y, s′] = [[k, c], [c, −k]]·[x, s], c = √(1 − k²), orthogonal, so
            // the stages can't add energy however fast k moves.
            float c = MathF.Sqrt(Math.Max(0f, 1f - k * k));
            float u = x + C.Fb;
            var ap = C.Ap;
            for (int i = 0; i < stages; i++)
            {
                float s = ap[i];
                ap[i] = c * u - k * s;
                u = k * u + c * s;
            }
            float t = DspMath.Flush(u + a * (C.LoopS - u));
            C.LoopS = t;
            float h = t - OnePole(t, ref C.HpS, hpG);
            C.Fb = EnsembleDesign.Saturate(fb * h);
            return u;
        }

        // A TPT one-pole low-pass (v = (x − s)·G, y = v + s, s ← y + v).
        static float OnePole(float x, ref float s, float g)
        {
            float v = (x - s) * g;
            float y = v + s;
            s = y + v;
            return y;
        }

        // ---- for the tests ---------------------------------------------------------------------

        internal EnsembleMode Mode => mode;
        internal int Voices => voices;
        internal int Stages => stages;

        /// <summary>A channel's voice delay (samples) now.</summary>
        internal float Delay(int channel, int voice) => ch[channel].Tau[voice];

        /// <summary>A channel's allpass coefficient now (Phaser).</summary>
        internal float StageK(int channel) => ch[channel].K;

        /// <summary>The loop's coefficients now: the feedback gain, the Tone one-pole's a and the high-pass's G.</summary>
        internal (float Feedback, float ToneA, float HpG) Loop => (feedback.Value * fade.Value, toneA.Value, hpG);

        /// <summary>Whether a delay line, or a filter or allpass state, holds a subnormal number.</summary>
        internal bool HoldsSubnormals()
        {
            foreach (var c in ch)
            {
                if (float.IsSubnormal(c.WetS) || float.IsSubnormal(c.LoopS) || float.IsSubnormal(c.HpS) || float.IsSubnormal(c.Fb)) return true;
                foreach (float a in c.Ap) if (float.IsSubnormal(a)) return true;
                for (int k = 0; k <= c.Line.MaxDelay; k++) if (float.IsSubnormal(c.Line.Read(k))) return true;
            }
            return false;
        }
    }
}
