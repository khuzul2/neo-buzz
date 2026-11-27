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
        const double OverdriveNegLevel = 0.7, FuzzNegLevel = 0.55;
        // Overdrive: a peaking hump before the clip (+10 dB), partly taken back after it (−4 dB).
        const double HumpPreDb = 10, HumpPostDb = -4, HumpQ = 0.8;
        // Fuzz: the bias is pushed towards the rail by FuzzStarve · k · (3 + jitter) while the input envelope k = 1 / (1 + 6 env) is small.
        const double FuzzOffset = 3.0, FuzzJitter = 0.8, FuzzEnvK = 6.0, FuzzEnvSeconds = 0.02, FuzzJitterHz = 45;
        // Transformer: the flux (low-passed input) is boosted TfGain times before the curve, so the lows saturate first; the memory is a
        // slow low-pass of how far the flux is compressed, taken off the flux again (TfHysteresis).
        const double TfGain = 3, TfHysteresis = 0.25, TfMemory = 0.7, TfHighSat = 2.0, TfHighFloor = 0.6;

        readonly ParamValues values;
        readonly FxStage fx;
        readonly Channel[] channels = { new Channel(), new Channel() };
        float sampleRate = 48000;
        int factor = 4;

        Shaper shaper;
        Smoother drive, bias, output, mix;
        float sag;                // tube sag depth
        double emphasisDb;        // tape
        double humpHz;            // overdrive: the mid hump's centre, 0 = not Overdrive
        double starve;            // fuzz: the bias starvation depth, 0 … 1
        double tfHz;              // transformer: the low corner, 0 = not Transformer
        DriveMode activeMode;     // the Mode being played (it follows the Mode parameter through a crossfade)
        int fadeState;            // 0 steady, 1 fading out to the dry signal, 2 fading back in
        float fadeGain = 1;       // the wet signal's share during a Mode change
        float fadeStep;           // per sample
        bool recalc;              // run Update after a Mode change took effect
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
        double keyHard, keyNeg, keyEmph, keyDrive, keyBias, keyAux;
        float outputGain = 1;                     // the Output parameter's gain (before auto-gain)

        // Deterministic pink noise (RMS 1, Gaussian) for the auto-gain estimate.
        static readonly float[] Probe = MakeProbe();

        /// <summary>What Amount scales (docs/specs/machines.md, "Mix and Amount").</summary>
        public static readonly AmountRule[] AmountRules =
        {
            AmountRule.Lin(P.Drive, 0), AmountRule.Lin(P.Bias, 0),
        };

        const uint FuzzSeed = 0x2545F491;

        public DriveEngine(ParamValues values)
        {
            fx = new FxStage(values, P.Amount, -1, AmountRules);
            this.values = fx.Effective;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>Constant at every quality, so ReBuzz can compensate it once.</summary>
        public static int Latency => FirOversampler.MaxLatency;

        public int Factor => factor;

        /// <summary>True while a Mode change is crossfading (the wet share fading out, the Mode switching, fading back in).</summary>
        public bool Fading => fadeState != 0;

        public void SetSampleRate(float sr)
        {
            fx.SetSampleRate(sr);
            sampleRate = sr;
            fadeStep = (float)(1.0 / (0.005 * sr));   // 5 ms out and 5 ms in
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
            fx.Reset();
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
            public TransformerCore Tf;    // transformer: flux and compression memory
            public Starver Fz;            // fuzz: the envelope and the sputter's noise

            /// <summary>The state of the shaper stage only (a Mode change): the filters after it carry on.</summary>
            public void ResetShaper()
            {
                PreEmphasis.Reset(); DeEmphasis.Reset(); Adaa.Reset(); Dc.Reset(); Pad.Reset();
                SagEnv = 0; Tf.Reset(); Fz.Reset();
            }

            public void Reset()
            {
                Tf.Reset(); Fz.Reset();
                LowCut.Reset(); PreEmphasis.Reset(); DeEmphasis.Reset();
                TiltLow.Reset(); TiltHigh.Reset(); HighCut.Reset();
                Os.Reset(); Adaa.Reset(); Dc.Reset(); Dry.Reset(); Pad.Reset();
                SagEnv = 0;
            }
        }

        /// <summary>The curve's own settings for a Mode and a Character (0 … 1): shared by the audio, the probe and the editor's drawing.</summary>
        static void Configure(ref Shaper s, DriveMode mode, float character)
        {
            s.Mode = mode;
            s.Hardness = mode == DriveMode.Transistor || mode == DriveMode.Fold || mode == DriveMode.HardClip || mode == DriveMode.Rectify ? character : 0;
            s.NegLevel = mode == DriveMode.Tube ? TubeNegLevel : mode == DriveMode.Diode ? 1 - 0.8 * character
                : mode == DriveMode.Overdrive ? OverdriveNegLevel : mode == DriveMode.Fuzz ? FuzzNegLevel : 1;
        }

        void Update(bool force)
        {
            if (!force && !recalc && !values.AnyDirty) return;
            recalc = false;
            var wanted = (DriveMode)values.Choice(P.Mode);
            if (force) { activeMode = wanted; fadeState = 0; fadeGain = 1; }
            else if (wanted != activeMode && fadeState != 1) fadeState = 1;   // the Mode changed: fade the wet signal out, then switch
            var mode = activeMode;
            float character = values.F(P.Character);
            Configure(ref shaper, mode, character);
            sag = mode == DriveMode.Tube ? character : 0;
            emphasisDb = mode == DriveMode.Tape ? 12 * character : 0;
            humpHz = mode == DriveMode.Overdrive ? 500 * PortableMath.Exp(character * 1.3862943611198906) : 0;   // 500 Hz … 2 kHz
            starve = mode == DriveMode.Fuzz ? character : 0;
            tfHz = mode == DriveMode.Transformer ? 40 * PortableMath.Exp(character * 2.0149030205422647) : 0;     // 40 Hz … 300 Hz

            int newFactor = 2 << values.Choice(P.Quality);
            if (newFactor != factor || force)
            {
                factor = newFactor;
                foreach (var c in channels) c.Os.SetFactor(factor);
            }

            foreach (var c in channels)
            {
                if (mode == DriveMode.Fuzz) c.Fz.Configure(sampleRate * (double)factor);
                if (mode == DriveMode.Transformer) c.Tf.Configure(tfHz, sampleRate * (double)factor);
            }

            double lowCut = values.Value(P.LowCut);
            double highCut = Math.Min(values.Value(P.HighCut), sampleRate * 0.49);
            double tilt = values.Value(P.Tone);
            foreach (var c in channels)
            {
                if (lowCut <= 10.5) c.LowCut.SetIdentity();
                else c.LowCut.Set(BiquadType.HighPass, lowCut, sampleRate, 0.7071);
                if (humpHz > 0)
                {
                    c.PreEmphasis.Set(BiquadType.Peak, humpHz, sampleRate, HumpQ, HumpPreDb);
                    c.DeEmphasis.Set(BiquadType.Peak, humpHz, sampleRate, HumpQ, HumpPostDb);
                }
                else if (emphasisDb > 0.01)
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
                && keyEmph == emphasisDb && keyDrive == driveGain && keyBias == b && keyMode == shaper.Mode && keyAux == humpHz + starve + tfHz)
                return;                                // Mix, Output, filters, Quality: nothing the probe depends on
            keyAuto = true; keyMode = shaper.Mode; keyHard = shaper.Hardness; keyNeg = shaper.NegLevel;
            keyEmph = emphasisDb; keyDrive = driveGain; keyBias = b; keyAux = humpHz + starve + tfHz;
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
            if (humpHz > 0)
            {
                pre.Set(BiquadType.Peak, humpHz, sampleRate, HumpQ, HumpPreDb);
                post.Set(BiquadType.Peak, humpHz, sampleRate, HumpQ, HumpPostDb);
            }
            else if (emphasisDb > 0.01)
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
            if (s.Mode == DriveMode.Fuzz)
            {
                // The same starved bias as the audio (at the base rate), so the gating counts in the level.
                var st = new Starver(sampleRate);
                for (int i = 0; i < Probe.Length; i++)
                {
                    double u = pre.Process((float)(Probe[i] * level)) * driveGain;
                    double bb = bias + st.Offset(u, starve);
                    double y = post.Process((float)(s.F(u + bb) - s.F(bb)));
                    probeOut[i] = y;
                    mean += y;
                }
            }
            else if (s.Mode == DriveMode.Transformer)
            {
                var tf = new TransformerCore(tfHz, sampleRate);
                var adaa = new AdaaShaper();
                for (int i = 0; i < Probe.Length; i++)
                {
                    double u = pre.Process((float)(Probe[i] * level)) * driveGain;
                    double y = post.Process((float)tf.Step(u, bias, ref s, ref adaa, false, fb));
                    probeOut[i] = y;
                    mean += y;
                }
            }
            else
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
            var s = new Shaper();
            Configure(ref s, mode, character);
            double g = DspMath.DbToGain(v.Value(P.Drive));
            double b = v.F(P.Bias) * 0.6;
            return s.F(x * g + b) - s.F(b);
        }

        /// <summary>
        /// Fuzz: the bias starvation. The input's envelope (20 ms) sets how far the bias is pushed towards the rail: the weaker the signal,
        /// the further, until a decaying note is gated; slow noise (45 Hz) on that offset makes the gate sputter where it opens and closes.
        /// Used by the audio loop and by the level probe, so the two agree. Deterministic, no allocation.
        /// </summary>
        internal struct Starver
        {
            float env, noise;
            uint rng;
            float envCoef, noiseCoef, noiseGain;

            public Starver(double rate) : this()
            {
                Configure(rate);
                Reset();
            }

            /// <summary>The coefficients for a (possibly oversampled) rate; the state stays.</summary>
            public void Configure(double rate)
            {
                envCoef = 1f - PortableMathF.Exp(-1f / (float)(FuzzEnvSeconds * rate));
                noiseCoef = 1f - PortableMathF.Exp((float)(-2 * Math.PI * FuzzJitterHz / rate));
                // The low-passed uniform noise (variance 1/3) has variance a / (2 − a) / 3; scale it to 1.
                noiseGain = (float)Math.Sqrt(3 * (2 - noiseCoef) / noiseCoef);
            }

            public void Reset() { env = 0; noise = 0; rng = FuzzSeed; }

            /// <summary>The bias offset for one input sample (after drive) at a starvation depth 0 … 1.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double Offset(double x, double depth)
            {
                env += ((float)Math.Abs(x) - env) * envCoef;
                env = DspMath.Flush(env);
                rng = rng * 1664525u + 1013904223u;
                float r = (rng >> 8) * (1f / 8388608f) - 1f;
                noise += (r - noise) * noiseCoef;
                if (depth <= 0) return 0;
                double k = 1.0 / (1.0 + FuzzEnvK * Math.Min(env, 8f));
                // The sputter is the bias's jitter: strong where the envelope is small (a decaying note), gone while a note is loud (k²) and
                // in silence (no envelope: the gate stays shut), so a held note has no noise skirts and silence stays silent.
                double jitter = FuzzJitter * noise * noiseGain * k * Math.Min(1.0, env * 200.0);
                return depth * k * (FuzzOffset + jitter);
            }
        }

        /// <summary>
        /// Transformer: the lows saturate first. The flux is the input low-passed at the corner (one pole); it is boosted TfGain times and
        /// run through the tanh, so the lows reach the curve earlier than the highs; what remains above the corner passes through a soft
        /// saturation of its own (TfHighSat, much later), scaled down a little while the flux is compressed. A slow memory of how far the
        /// flux is compressed is taken off the flux, so the curve differs on the way up and the way down (a gentle hysteresis loop). With
        /// the flux and the memory at rest the output is the input, so small signals pass unchanged.
        /// </summary>
        internal struct TransformerCore
        {
            double l, z;
            double aL, aZ;

            public TransformerCore(double hz, double rate) : this()
            {
                Configure(hz, rate);
            }

            /// <summary>The corner at a (possibly oversampled) rate; the state stays.</summary>
            public void Configure(double hz, double rate)
            {
                aL = 1 - PortableMath.Exp(-2 * Math.PI * hz / rate);
                aZ = aL * TfMemory;
            }

            public void Reset() { l = z = 0; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double Step(double u, double bias, ref Shaper s, ref AdaaShaper adaa, bool useAdaa, double fb)
            {
                l += aL * (u - l);
                double arg = TfGain * l - TfHysteresis * z;
                double ym = useAdaa ? adaa.Process(ref s, arg, bias, fb) : s.F(arg + bias) - fb;
                z += aZ * ((arg - ym) - z);
                double t = ym + fb;                               // tanh(arg + bias)
                double slope = Math.Max(0, 1 - t * t);
                double h = u - l;
                double hs = TfHighSat * PortableMath.Tanh(h / TfHighSat);
                l = DspMath.Flush(l);
                z = DspMath.Flush(z);
                return ym / TfGain + hs * (TfHighFloor + (1 - TfHighFloor) * slope);
            }
        }

        /// <summary>Processes a stereo block in place (±1 floats).</summary>
        public void Process(float[] left, float[] right, int n)
        {
            fx.Begin(left, right, n);
            ProcessCore(left, right, n);
            fx.End(left, right, n, Latency);
        }

        void ProcessCore(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            if (fadeState == 1 && fadeGain <= 0)
            {
                // The wet signal is out: switch the Mode (the dry signal carried the sound meanwhile) and fade back in.
                var wanted = (DriveMode)values.Choice(P.Mode);
                if (wanted != activeMode)
                {
                    activeMode = wanted;
                    foreach (var c in channels) c.ResetShaper();
                    probeValid = false;
                    probePending = false;
                    recalc = true;
                }
                fadeState = 2;
            }
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
            if (fadeState != 0)
            {
                // A Mode change: the wet share of the mix fades out over 5 ms, the Mode switches, and it fades back in over 5 ms.
                for (int i = 0; i < n; i++)
                {
                    if (fadeState == 1) fadeGain = Math.Max(0f, fadeGain - fadeStep);
                    else if (fadeState == 2)
                    {
                        fadeGain += fadeStep;
                        if (fadeGain >= 1f) { fadeGain = 1f; fadeState = 0; }
                    }
                    mg[i] *= fadeGain;
                }
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
                if (shaper.Mode == DriveMode.Fuzz)
                {
                    // Hard asymmetric clip with a starved bias: the offset follows the envelope and sputters when it is small.
                    for (int i = 0; i < m; i++)
                    {
                        double bb = b + c.Fz.Offset(hi[i], starve);
                        hi[i] = (float)c.Adaa.Process(ref shaper, hi[i], bb, shaper.F(bb));
                    }
                }
                else if (shaper.Mode == DriveMode.Transformer)
                {
                    for (int i = 0; i < m; i++)
                        hi[i] = (float)c.Tf.Step(hi[i], b, ref shaper, ref c.Adaa, true, fb);
                }
                else
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
