#nullable disable
// Neo Comp (docs/specs/machines.md §5): a log-domain compressor. Detector (peak, or RMS for Glue)
// on the sidechain (input, or input 2) through a low cut → stereo link → gain computer with soft
// knee → attack/release smoothing in dB (feed-forward; FET solves its feedback loop implicitly
// per sample) → gain on the audio, delayed by the lookahead → colour → mix → output. The gain
// computer and the detector's levels, link and smoothing are the shared ones (NeoBuzz.Dsp
// Dynamics.cs), which Neo Multiband uses too.

using System.Runtime.CompilerServices;
using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Comp
{
    internal enum CompMode { Clean, Glue, Fet, Opto }

    internal sealed class CompEngine
    {
        public const int MaxBlock = 256;
        public const double MaxLookahead = 0.005;
        const double RmsTime = 0.003;
        const double AutoMakeupLevel = -12;

        readonly ParamValues values;
        float sampleRate = 48000;

        CompMode mode;
        GainComputer gc;
        double releaseSeconds;
        double attackA, releaseA, releaseFastA, releaseSlowA, attackSlowA, optoFastA, memA, rmsA;
        bool autoRelease, external;
        float link, character;
        int lookahead;
        Smoother makeup, mix, output;

        readonly Channel[] ch = { new Channel(), new Channel() };
        readonly double[] level = new double[2];
        float grMeter;

        sealed class Channel
        {
            public Biquad ScLowCut;
            public double G, GFast, GSlow, Ms, Mem;
            public DelayLineF Audio;
            public DcBlocker Dc;
            public void Reset()
            {
                ScLowCut.Reset();
                G = GFast = GSlow = Ms = Mem = 0;
                Audio?.Reset();
                Dc.Reset();
            }
        }

        public CompEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>Gain reduction now, dB (≥ 0, held and falling), for the editor.</summary>
        public float GainReductionDb => grMeter;

        /// <summary>Samples the audio is delayed by (the lookahead).</summary>
        public int Latency => lookahead;

        public CompMode Mode => mode;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = (int)Math.Ceiling(MaxLookahead * sr) + 2;
            foreach (var c in ch)
            {
                c.Audio = new DelayLineF(max);
                c.Dc.Set(sr, 5);
            }
            makeup.Setup(0.02, sr);
            mix.Setup(0.02, sr);
            output.Setup(0.02, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
        }

        public void Reset()
        {
            foreach (var c in ch) c.Reset();
            grMeter = 0;
        }

        static double Coef(double seconds, double sr) => Detector.Coef(seconds, sr);

        /// <summary>The static curve from the parameters: threshold, ratio and soft knee, feedback in FET.</summary>
        static GainComputer From(ParamValues v) =>
            GainComputer.Make(v.Value(P.Threshold), v.Value(P.Ratio), v.Value(P.Knee), (CompMode)v.Choice(P.Mode) == CompMode.Fet);

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            mode = (CompMode)values.Choice(P.Mode);
            gc = From(values);
            double attack = values.Value(P.Attack);
            double release = values.Value(P.Release);
            releaseSeconds = release;
            attackA = Coef(attack, sampleRate);
            releaseA = Coef(release, sampleRate);
            releaseFastA = Coef(Math.Max(0.03, release * 0.3), sampleRate);
            releaseSlowA = Coef(release * 3, sampleRate);
            attackSlowA = Coef(Math.Max(0.05, attack * 10), sampleRate);
            optoFastA = Coef(0.06, sampleRate);
            memA = Coef(2.0, sampleRate);
            rmsA = Coef(RmsTime, sampleRate);
            autoRelease = values.On(P.AutoRelease);
            external = values.Choice(P.Sidechain) == 1;
            link = values.F(P.Link);
            character = mode == CompMode.Clean ? 0 : values.F(P.Character);
            int la = (int)Math.Round(values.Value(P.Lookahead) * 0.001 * sampleRate);
            int max = (int)Math.Ceiling(MaxLookahead * sampleRate);
            lookahead = Math.Clamp(la, 0, max);

            double lowCut = values.Value(P.ScLowCut);
            foreach (var c in ch)
            {
                if (lowCut <= 10.5) c.ScLowCut.SetIdentity();
                else c.ScLowCut.Set(BiquadType.HighPass, lowCut, sampleRate, 0.7071);
            }

            double mk = values.Value(P.Makeup);
            if (values.On(P.AutoMakeup)) mk += gc.Static(AutoMakeupLevel);
            makeup.Target = (float)mk;
            mix.Target = values.F(P.Mix);
            output.Target = (float)DspMath.DbToGain(values.Value(P.Output));
            if (force)
            {
                makeup.Reset(makeup.Target);
                mix.Reset(mix.Target);
                output.Reset(output.Target);
            }
            values.Clean();
        }

        /// <summary>The reduction a steady signal at <paramref name="levelDb"/> gets (the static curve).</summary>
        public double StaticReduction(double levelDb) => gc.Static(levelDb);

        /// <summary>One step of g = α g1 + (1 − α) c(x − g), solved for g (Newton).</summary>
        double SolveFeedback(double x, double g1, double alpha)
        {
            double g = g1;
            for (int it = 0; it < 6; it++)
            {
                double c = gc.Fb(x - g, out double dc);
                double h = g - alpha * g1 - (1 - alpha) * c;
                double hp = 1 + (1 - alpha) * dc;
                double step = h / hp;
                g -= step;
                if (Math.Abs(step) < 1e-9) break;
            }
            return g;
        }

        static float Saturate(float y, CompMode m)
        {
            switch (m)
            {
                case CompMode.Glue:
                    return PortableMathF.Tanh(1.6f * y) / 1.6f;
                case CompMode.Fet:
                    return y >= 0 ? PortableMathF.Tanh(1.8f * y) / 1.8f : 0.8f * PortableMathF.Tanh(1.8f * y / 0.8f) / 1.8f;
                default:
                    return y >= 0 ? PortableMathF.Tanh(1.3f * y) / 1.3f : 0.85f * PortableMathF.Tanh(1.3f * y / 0.85f) / 1.3f;
            }
        }

        /// <summary>
        /// Processes a stereo block in place. <paramref name="keyLeft"/>/<paramref name="keyRight"/>
        /// are input 2 (null when nothing is connected); used when the sidechain is external.
        /// </summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, float[] keyLeft, float[] keyRight, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            float blockGr = 0;
            bool useKey = external;
            float lk = link;
            var c0 = ch[0];
            var c1 = ch[1];
            // Opto: the slow release lengthens after long compression (per block is plenty).
            double slow0 = mode == CompMode.Opto ? Coef(releaseSeconds * (1 + 3 * c0.Mem), sampleRate) : 0;
            double slow1 = mode == CompMode.Opto ? Coef(releaseSeconds * (1 + 3 * c1.Mem), sampleRate) : 0;
            for (int i = 0; i < n; i++)
            {
                float x0 = left[i], x1 = right[i];
                float k0, k1;
                if (useKey)
                {
                    k0 = keyLeft != null ? keyLeft[i] : 0f;
                    k1 = keyRight != null ? keyRight[i] : 0f;
                }
                else
                {
                    k0 = x0;
                    k1 = x1;
                }
                k0 = c0.ScLowCut.Process(k0);
                k1 = c1.ScLowCut.Process(k1);
                if (mode == CompMode.Glue)
                {
                    c0.Ms = Detector.MeanSquare(c0.Ms, k0, rmsA);
                    c1.Ms = Detector.MeanSquare(c1.Ms, k1, rmsA);
                    level[0] = Detector.RmsDb(c0.Ms);
                    level[1] = Detector.RmsDb(c1.Ms);
                }
                else
                {
                    level[0] = Detector.PeakDb(k0);
                    level[1] = Detector.PeakDb(k1);
                }
                if (lk > 0) Detector.Link(ref level[0], ref level[1], lk);

                c0.Audio.Write(x0);
                c1.Audio.Write(x1);
                float d0 = c0.Audio.Read(lookahead);
                float d1 = c1.Audio.Read(lookahead);

                float mk = makeup.Next();
                float mx = mix.Next();
                float og = output.Next();
                for (int c = 0; c < 2; c++)
                {
                    var s = ch[c];
                    double x = level[c];
                    double g;
                    if (mode == CompMode.Fet && !useKey)
                    {
                        // Feedback: the detector hears the output (x − g).
                        double ga = SolveFeedback(x, s.G, attackA);
                        g = ga > s.G ? ga : Math.Max(0, SolveFeedback(x, s.G, autoRelease ? releaseFastA : releaseA));
                        s.G = g;
                    }
                    else
                    {
                        double target = gc.Ff(x);
                        if (mode == CompMode.Opto)
                        {
                            // Two-stage release: half recovers in ~60 ms, half slowly, slower still
                            // after long compression (the cell's memory).
                            double slowRel = c == 0 ? slow0 : slow1;
                            s.GFast = target > s.GFast ? target + (s.GFast - target) * attackA : target + (s.GFast - target) * optoFastA;
                            s.GSlow = target > s.GSlow ? target + (s.GSlow - target) * attackA : target + (s.GSlow - target) * slowRel;
                            g = 0.5 * (s.GFast + s.GSlow);
                            s.Mem += (Math.Min(1.0, g / 12) - s.Mem) * (1 - memA);
                        }
                        else if (autoRelease)
                        {
                            // Fast and slow paths: transients recover fast, sustained compression slowly.
                            s.GFast = target > s.GFast ? target + (s.GFast - target) * attackA : target + (s.GFast - target) * releaseFastA;
                            s.GSlow = target > s.GSlow ? target + (s.GSlow - target) * attackSlowA : target + (s.GSlow - target) * releaseSlowA;
                            g = Math.Max(s.GFast, s.GSlow);
                        }
                        else
                        {
                            g = Detector.Smooth(s.G, target, attackA, releaseA);
                        }
                        s.G = g;
                    }
                    if (g > blockGr) blockGr = (float)g;

                    float dry = c == 0 ? d0 : d1;
                    float y = dry * (float)PortableMath.Pow(10, (mk - g) / 20);
                    if (character > 0)
                    {
                        float sat = Saturate(y, mode);
                        float amount = mode == CompMode.Fet ? character * (float)Math.Min(1, 0.4 + g / 15) : character;
                        y += amount * (sat - y);
                        y = s.Dc.Process(y);
                    }
                    float o = (dry + (y - dry) * mx) * og;
                    if (c == 0) left[i] = o; else right[i] = o;
                }
            }
            c0.G = DspMath.Flush(c0.G); c1.G = DspMath.Flush(c1.G);
            c0.Ms = DspMath.Flush(c0.Ms); c1.Ms = DspMath.Flush(c1.Ms);
            c0.ScLowCut.Flush(); c1.ScLowCut.Flush();
            float fall = PortableMathF.Pow(10f, -20f / 20f * n / sampleRate * 1.5f);
            grMeter = Math.Max(blockGr, grMeter * fall);
            OutMeter.Feed(left, right, n);
        }

        /// <summary>The static curve for the editor: output level for an input level (dB), before makeup.</summary>
        public static double Curve(ParamValues v, double inDb) => inDb - From(v).Static(inDb);
    }
}
