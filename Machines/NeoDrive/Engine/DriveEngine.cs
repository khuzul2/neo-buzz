#nullable disable
// Neo Drive (docs/specs/machines.md §5): low cut → pre-emphasis → drive → oversampled shaper with
// ADAA → de-emphasis → DC block → tilt → high cut → output, mixed with the dry signal delayed by
// the same latency. Pure: tested by NeoBuzz.MachineTests.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Drive
{
    internal sealed class DriveEngine
    {
        public const int MaxBlock = 256;
        const double EmphasisHz = 2500;
        const double TubeNegLevel = 0.72;

        readonly ParamValues values;
        readonly Channel[] channels = { new Channel(), new Channel() };
        float sampleRate = 48000;
        int factor = 4;

        Shaper shaper;
        Smoother drive, bias, output, mix;
        float sag;                // tube sag depth
        double emphasisDb;        // tape
        float autoGain = 1;

        // The auto-gain probe (4096 samples of shaper) costs ~50x a block, so it runs only when an input it depends on changed
        // (drive gain, bias, curve, emphasis), never for Mix / Output / filters. An isolated change probes at once (static
        // renders and one-off edits are exactly as before). A change that follows another within ProbeInterval samples is
        // motion (automation, a modulator writing every sub-block): probes then run at most once per ProbeInterval samples of
        // audio, counted in samples so offline renders stay deterministic, the output glide covers the step, and the last
        // value is probed as soon as the interval ends. No clock, no allocation.
        const long ProbeInterval = 1024;
        long sampleCount;                         // samples processed
        long lastProbeAt = long.MinValue / 2;     // sampleCount at the last probe
        long lastChangeAt = long.MinValue / 2;    // sampleCount at the last change of a probe input
        bool probeValid, probePending;
        bool keyAuto;
        DriveMode keyMode;
        double keyHard, keyNeg, keyEmph, keyDrive, keyBias;
        float outputGain = 1;                     // the Output parameter's gain (before auto-gain)

        // Deterministic pink noise (RMS 1, Gaussian) for the auto-gain estimate.
        static readonly float[] Probe = MakeProbe();

        public DriveEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>Constant at every quality, so ReBuzz can compensate it once.</summary>
        public static int Latency => FirOversampler.MaxLatency;

        public int Factor => factor;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            drive.Setup(0.02, sr);
            bias.Setup(0.02, sr);
            output.Setup(0.02, sr);
            mix.Setup(0.02, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            for (int i = 0; i < channels.Length; i++) channels[i].Dc.Set(sr, 5);
            Update(true);
        }

        public void Reset()
        {
            foreach (var c in channels) c.Reset();
        }

        sealed class Channel
        {
            public Biquad LowCut, PreEmphasis, DeEmphasis, TiltLow, TiltHigh, HighCut;
            public readonly FirOversampler Os = new FirOversampler(MaxBlock);
            public AdaaShaper Adaa;
            public DcBlocker Dc;
            public readonly DelayLineF Dry = new DelayLineF(FirOversampler.MaxLatency + 1);
            public readonly DelayLineF Pad = new DelayLineF(FirOversampler.MaxLatency + 1);
            public readonly float[] Buf = new float[MaxBlock];
            public readonly float[] DryBuf = new float[MaxBlock];
            public float SagEnv;

            public void Reset()
            {
                LowCut.Reset(); PreEmphasis.Reset(); DeEmphasis.Reset();
                TiltLow.Reset(); TiltHigh.Reset(); HighCut.Reset();
                Os.Reset(); Adaa.Reset(); Dc.Reset(); Dry.Reset(); Pad.Reset();
                SagEnv = 0;
            }
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var mode = (DriveMode)values.Choice(P.Mode);
            float character = values.F(P.Character);
            shaper.Mode = mode;
            shaper.Hardness = mode == DriveMode.Transistor || mode == DriveMode.Fold ? character : 0;
            shaper.NegLevel = mode == DriveMode.Tube ? TubeNegLevel : mode == DriveMode.Diode ? 1 - 0.8 * character : 1;
            sag = mode == DriveMode.Tube ? character : 0;
            emphasisDb = mode == DriveMode.Tape ? 12 * character : 0;

            int newFactor = 2 << values.Choice(P.Quality);
            if (newFactor != factor || force)
            {
                factor = newFactor;
                foreach (var c in channels) c.Os.SetFactor(factor);
            }

            double lowCut = values.Value(P.LowCut);
            double highCut = Math.Min(values.Value(P.HighCut), sampleRate * 0.49);
            double tilt = values.Value(P.Tone);
            foreach (var c in channels)
            {
                if (lowCut <= 10.5) c.LowCut.SetIdentity();
                else c.LowCut.Set(BiquadType.HighPass, lowCut, sampleRate, 0.7071);
                if (emphasisDb > 0.01)
                {
                    c.PreEmphasis.Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071, emphasisDb);
                    c.DeEmphasis.Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071, -emphasisDb);
                }
                else
                {
                    c.PreEmphasis.SetIdentity();
                    c.DeEmphasis.SetIdentity();
                }
                c.TiltLow.Set(BiquadType.LowShelf, 300, sampleRate, 0.6, -5 * tilt);
                c.TiltHigh.Set(BiquadType.HighShelf, 3000, sampleRate, 0.6, 5 * tilt);
                if (highCut >= sampleRate * 0.45 || values[P.HighCut] >= values.Table[P.HighCut].Max) c.HighCut.SetIdentity();
                else c.HighCut.Set(BiquadType.LowPass, highCut, sampleRate, 0.7071);
            }

            float driveGain = (float)DspMath.DbToGain(values.Value(P.Drive));
            float b = values.F(P.Bias) * 0.6f;
            outputGain = (float)DspMath.DbToGain(values.Value(P.Output));
            UpdateCompensation(driveGain, b, force);
            drive.Target = driveGain;
            bias.Target = b;
            output.Target = outputGain * autoGain;
            mix.Target = values.F(P.Mix);
            if (force)
            {
                drive.Reset(drive.Target);
                bias.Reset(bias.Target);
                output.Reset(output.Target);
                mix.Reset(mix.Target);
            }
            values.Clean();
        }

        /// <summary>
        /// Brings autoGain up to date for the probe's inputs (see ProbeInterval): unchanged inputs keep the last answer; a
        /// change in motion that falls inside the interval is left pending (ResolvePending runs it).
        /// </summary>
        void UpdateCompensation(float driveGain, float b, bool force)
        {
            bool on = values.On(P.AutoGain);
            if (!on)
            {
                autoGain = 1f;
                probeValid = false;
                probePending = false;
                return;
            }
            if (probeValid && !force && keyAuto && keyHard == shaper.Hardness && keyNeg == shaper.NegLevel
                && keyEmph == emphasisDb && keyDrive == driveGain && keyBias == b && keyMode == shaper.Mode)
                return;                                // Mix, Output, filters, Quality: nothing the probe depends on
            keyAuto = true; keyMode = shaper.Mode; keyHard = shaper.Hardness; keyNeg = shaper.NegLevel;
            keyEmph = emphasisDb; keyDrive = driveGain; keyBias = b;
            bool inMotion = sampleCount - lastChangeAt < ProbeInterval;
            lastChangeAt = sampleCount;
            if (force || !probeValid || !inMotion || sampleCount - lastProbeAt >= ProbeInterval)
            {
                autoGain = EstimateCompensation(shaper, driveGain, b);
                lastProbeAt = sampleCount;
                probeValid = true;
                probePending = false;
            }
            else probePending = true;
        }

        /// <summary>Runs the probe a motion left pending, once ProbeInterval samples have passed since the last one.</summary>
        void ResolvePending()
        {
            if (!probePending || sampleCount - lastProbeAt < ProbeInterval) return;
            autoGain = EstimateCompensation(shaper, (float)keyDrive, (float)keyBias);
            lastProbeAt = sampleCount;
            probePending = false;
            output.Target = outputGain * autoGain;
        }

        static float[] MakeProbe()
        {
            var rng = new Rng(0xC0FFEE);
            var pink = new PinkNoise();
            var p = new float[4096];
            for (int i = 0; i < 1024; i++) pink.Next(rng.NextBipolar());
            for (int i = 0; i < p.Length; i++) p[i] = pink.Next(rng.NextBipolar());
            double sum = 0;
            foreach (var x in p) sum += x * x;
            float g = (float)(1 / Math.Sqrt(sum / p.Length));
            for (int i = 0; i < p.Length; i++) p[i] *= g;
            return p;
        }

        readonly double[] probeOut = new double[4096];

        /// <summary>
        /// The output gain that keeps a −18 dBFS RMS pink-noise level through emphasis, drive,
        /// curve and de-emphasis (run on a probe whenever those change; no allocation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        float EstimateCompensation(Shaper s, float driveGain, float bias)
        {
            const double level = 0.125;
            double fb = s.F(bias);
            var pre = new Biquad();
            var post = new Biquad();
            if (emphasisDb > 0.01)
            {
                pre.Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071, emphasisDb);
                post.Set(BiquadType.HighShelf, EmphasisHz, sampleRate, 0.7071, -emphasisDb);
            }
            else
            {
                pre.SetIdentity();
                post.SetIdentity();
            }
            double mean = 0;
            for (int i = 0; i < Probe.Length; i++)
            {
                double u = pre.Process((float)(Probe[i] * level)) * driveGain;
                double y = post.Process((float)(s.F(u + bias) - fb));
                probeOut[i] = y;
                mean += y;
            }
            mean /= Probe.Length;
            double sum = 0;
            for (int i = Probe.Length / 4; i < Probe.Length; i++) sum += (probeOut[i] - mean) * (probeOut[i] - mean);
            double rms = Math.Sqrt(sum / (Probe.Length - Probe.Length / 4));
            return rms > 1e-9 ? (float)Math.Clamp(level / rms, 0.01, 100) : 1f;
        }

        /// <summary>The curve as the editor draws it: output for an input level (before output gain).</summary>
        public static double Transfer(ParamValues v, double x)
        {
            var mode = (DriveMode)v.Choice(P.Mode);
            float character = v.F(P.Character);
            var s = new Shaper
            {
                Mode = mode,
                Hardness = mode == DriveMode.Transistor || mode == DriveMode.Fold ? character : 0,
                NegLevel = mode == DriveMode.Tube ? TubeNegLevel : mode == DriveMode.Diode ? 1 - 0.8 * character : 1,
            };
            double g = DspMath.DbToGain(v.Value(P.Drive));
            double b = v.F(P.Bias) * 0.6;
            return s.F(x * g + b) - s.F(b);
        }

        /// <summary>Processes a stereo block in place (±1 floats).</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            ResolvePending();
            InMeter.Feed(left, right, n);
            int pad = Latency - FirOversampler.LatencyFor(factor);

            // Smoothed values per sample, shared by both channels.
            Span<float> dg = stackalloc float[n];
            Span<float> og = stackalloc float[n];
            Span<float> mg = stackalloc float[n];
            for (int i = 0; i < n; i++)
            {
                dg[i] = drive.Next();
                og[i] = output.Next();
                mg[i] = mix.Next();
            }
            float b = bias.Advance(n);
            double fb = shaper.F(b);
            float sagCoeff = 1f - PortableMathF.Exp(-1f / (0.015f * sampleRate * factor));

            for (int ch = 0; ch < 2; ch++)
            {
                var c = channels[ch];
                var x = ch == 0 ? left : right;
                var buf = c.Buf;
                for (int i = 0; i < n; i++)
                {
                    c.Dry.Write(x[i]);
                    c.DryBuf[i] = c.Dry.Read(Latency);
                    float v = c.LowCut.Process(x[i]);
                    v = c.PreEmphasis.Process(v);
                    buf[i] = v * dg[i];
                }
                var hi = c.Os.Up(buf, 0, n);
                int m = n * factor;
                float env = c.SagEnv;
                for (int i = 0; i < m; i++)
                {
                    double bb = b;
                    if (sag > 0)
                    {
                        env += (MathF.Abs(hi[i]) - env) * sagCoeff;
                        bb = b - sag * 0.5 * Math.Min(env, 2f);
                    }
                    hi[i] = (float)c.Adaa.Process(ref shaper, hi[i], bb, sag > 0 ? shaper.F(bb) : fb);
                }
                c.SagEnv = DspMath.Flush(env);
                c.Os.Down(buf, 0, n);
                for (int i = 0; i < n; i++)
                {
                    float v = buf[i];
                    if (pad > 0)
                    {
                        c.Pad.Write(v);
                        v = c.Pad.Read(pad);
                    }
                    v = c.DeEmphasis.Process(v);
                    v = c.Dc.Process(v);
                    v = c.TiltLow.Process(v);
                    v = c.TiltHigh.Process(v);
                    v = c.HighCut.Process(v);
                    float dry = c.DryBuf[i];
                    x[i] = dry + (v * og[i] - dry) * mg[i];
                }
                c.LowCut.Flush(); c.PreEmphasis.Flush(); c.DeEmphasis.Flush();
                c.TiltLow.Flush(); c.TiltHigh.Flush(); c.HighCut.Flush();
            }
            sampleCount += n;
            OutMeter.Feed(left, right, n);
        }
    }
}
