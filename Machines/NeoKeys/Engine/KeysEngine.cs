#nullable disable
// Neo Keys (docs/specs/neo-keys.md): electric keys by physical modelling. Each voice is a bank of up to 13 decaying modes,
//     z[n+1] = z[n] * r e^{j w} + x[n],   displacement = Im z,
// a 2-pole resonator with its own frequency and T60 (Adrien 1991; Cook, Real Sound Synthesis). x is the hammer's force pulse: a Gaussian with a Hann
// taper (no spectral nulls, so the level rises monotonically with the hammer speed and the contact time only tilts the spectrum), scaled so that the note's own
// mode is excited by exactly the hammer speed, whatever the contact. The displacements of the modes, weighed by where the pickup reads them, are summed and
// go through the pickup's curve (KeysModels.Flux), differenced (the coil's voltage is the rate of change of the flux; the difference of the flux is also its
// first-order antiderivative form, so the curve's harmonics do not fold back), and so the bark is a function of how far the tine swings and fades as the note
// dies. Mechanical thumps, the preamp drive (ADAA), the tone tilt, the tremolo with auto-pan, the volume and a soft limit are the master. 16 voices; a stolen or
// killed voice fades out in 5 ms (a raised cosine). Pure; no allocation after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Keys
{
    internal sealed class KeysEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int Voices = 16;
        public const int Modes = KeysModels.MaxModes;
        public const int Tick = 16;
        public const int PulseMax = 4096;
        /// <summary>The highest mode frequency, as a fraction of the sample rate (modes above it are dropped).</summary>
        public const double TopFraction = 0.45;
        /// <summary>The length of a steal's or a kill's fade, seconds (at least 3 ms).</summary>
        public const double FadeSeconds = 0.005;
        const double Ln10x3 = -3.0 * 2.302585092994046;      // r = 10^(-3 / (T60 fs)) = exp(this / (T60 fs))
        const float LimitKnee = 0.7f, LimitCeiling = 0.89f;
        const double SlewPerTick = 0.012;
        /// <summary>A voice's output gain: the level of a note at the hammer speed 1 before the master.</summary>
        const float VoiceGain = 0.085f;
        const float ThumpGain = 0.9f;
        /// <summary>The knock's resonance (a Tine's and a Reed's, Hz; Q about 2.5 with the thump's decay) and the levels of its two parts (re <see cref="ThumpGain"/>).</summary>
        static readonly double[] KnockHz = { 120.0, 180.0, 0.0 };
        static readonly float[] KnockRes = { 0.24f, 0.29f, 0f }, KnockNoise = { 0.4f, 0.5f, 0f };

        const int Free = 0, Held = 1, Released = 2, Fading = 3;

        readonly ParamValues values;
        double fs = 48000;

        // Per voice.
        readonly int[] state = new int[Voices];
        readonly int[] key = new int[Voices];
        readonly int[] vmodel = new int[Voices];
        readonly float[] note = new float[Voices];
        readonly float[] speed = new float[Voices];           // the shaped hammer speed, 0 to 1
        readonly long[] age = new long[Voices];
        readonly float[] damper = new float[Voices];
        readonly float[] fadeFrom = new float[Voices], fadeAt = new float[Voices];
        readonly bool[] sustained = new bool[Voices];
        readonly bool[] retarget = new bool[Voices];
        readonly bool[] slewing = new bool[Voices];
        readonly int[] modeCount = new int[Voices];
        readonly int[] pulseLen = new int[Voices], pulsePos = new int[Voices];
        readonly float[] invOmega = new float[Voices];
        readonly float[] prevA = new float[Voices], prevB = new float[Voices];   // the flux of the last sample at each pickup
        readonly bool[] hasPending = new bool[Voices];
        readonly int[] pendKey = new int[Voices];
        readonly float[] pendNote = new float[Voices], pendVel = new float[Voices];
        // The thump: two one-poles in a row (an alpha function, so it starts smoothly). A String's is noise through one low-pass. A Tine's and a Reed's is a knock:
        // a short decaying low resonance (thZr, thZi, smoothed by thLp) plus a little noise through a 4th-order low-pass (four one-poles, thN) under the same alpha envelope, so no hiss.
        readonly float[] th1 = new float[Voices], th2 = new float[Voices], thIn = new float[Voices], thLp = new float[Voices], thAmp = new float[Voices];
        readonly float[] thZr = new float[Voices], thZi = new float[Voices], thN = new float[Voices * 4];
        readonly bool[] thOn = new bool[Voices];
        readonly Rng[] noise = new Rng[Voices];
        long clock;
        bool sustainDown;
        float releaseCoef = 0.999f;
        readonly float[] thRho = new float[3], thLpK = new float[3], thNorm = new float[3];
        readonly float[] thRc = new float[3], thRs = new float[3], thNzK = new float[3];

        // Per voice per mode (voice * Modes + mode): the state and the coefficients the loop reads (float), and the double ones they follow.
        readonly float[] zr = new float[Voices * Modes], zi = new float[Voices * Modes];
        readonly float[] cf = new float[Voices * Modes], sf = new float[Voices * Modes];
        readonly double[] cd = new double[Voices * Modes], sd = new double[Voices * Modes];
        readonly double[] tc = new double[Voices * Modes], ts = new double[Voices * Modes];
        readonly float[] pulse = new float[Voices * PulseMax];

        // Block buffers.
        readonly float[] mix = new float[MaxBlock];
        readonly float[] drive = new float[Tick], ya = new float[Tick], yb = new float[Tick], damp = new float[Tick], tmul = new float[Tick];
        readonly float[] aArr = new float[Tick], wNArr = new float[Tick], wBArr = new float[Tick], barArr = new float[Tick];
        readonly float[] outL = new float[MaxBlock], outR = new float[MaxBlock];

        int tickLeft;
        SmoothRamp volume, tremDepth, tremRate, stereo, driveGain, driveComp, driveMix, toneLow, toneHigh, pickup, bar, selN, selB;
        double tremPhase;
        float toneState;
        float toneK;
        // The drive's ADAA state (the previous input and its antiderivative).
        float adaaX;
        double adaaF;

        public readonly LevelMeter OutMeter = new LevelMeter();

        public KeysEngine(ParamValues values)
        {
            this.values = values;
            for (int v = 0; v < Voices; v++) noise[v] = new Rng(0x9E3779B1u + (uint)v * 7919u);
            SetSampleRate(48000);
        }

        public float SampleRate => (float)fs;
        public bool IsActive
        {
            get
            {
                for (int v = 0; v < Voices; v++) if (state[v] != Free) return true;
                return false;
            }
        }

        /// <summary>Notes held now (gate on), for the note contract tests (docs/specs/notes.md).</summary>
        public int HeldNotes { get { int n = 0; for (int v = 0; v < Voices; v++) if (state[v] == Held) n++; return n; } }

        /// <summary>Voices sounding now.</summary>
        public int ActiveVoices
        {
            get { int n = 0; for (int v = 0; v < Voices; v++) if (state[v] != Free) n++; return n; }
        }

        public void SetSampleRate(float rate)
        {
            fs = rate;
            volume.Setup(0.02, rate); tremDepth.Setup(0.03, rate); tremRate.Setup(0.05, rate); stereo.Setup(0.03, rate);
            driveGain.Setup(0.03, rate); driveComp.Setup(0.03, rate); driveMix.Setup(0.03, rate);
            toneLow.Setup(0.03, rate); toneHigh.Setup(0.03, rate); pickup.Setup(0.03, rate); bar.Setup(0.03, rate); selN.Setup(0.03, rate); selB.Setup(0.03, rate);
            toneK = (float)(1.0 - PortableMath.Exp(-2.0 * Math.PI * 1000.0 / rate));
            double[] tau = { 0.007, 0.004, 0.003 }, cutoff = { 600.0, 900.0, 3000.0 };
            for (int m = 0; m < 3; m++)
            {
                thRho[m] = (float)PortableMath.Exp(-1.0 / (tau[m] * rate));
                thLpK[m] = (float)(1.0 - PortableMath.Exp(-2.0 * Math.PI * cutoff[m] / rate));
                thNorm[m] = (float)(Math.E / (tau[m] * rate));      // a peak of 1 for the alpha function n rho^n
                double kw = 2.0 * Math.PI * KnockHz[m] / rate, rr = PortableMath.Exp(-1.0 / (tau[m] * rate));
                thRc[m] = (float)(rr * PortableMath.Cos(kw)); thRs[m] = (float)(rr * PortableMath.Sin(kw));
                thNzK[m] = thLpK[m];                                // the knock's noise: four of these in a row, at the model's cutoff
            }
            OutMeter.SetSampleRate(rate);
            AllNotesOff(true);
            KillAll();
            Update(true);
            volume.Reset(volume.Target); tremDepth.Reset(tremDepth.Target); tremRate.Reset(tremRate.Target); stereo.Reset(stereo.Target);
            driveGain.Reset(driveGain.Target); driveComp.Reset(driveComp.Target); driveMix.Reset(driveMix.Target);
            toneLow.Reset(toneLow.Target); toneHigh.Reset(toneHigh.Target); pickup.Reset(pickup.Target); bar.Reset(bar.Target); selN.Reset(selN.Target); selB.Reset(selB.Target);
            toneState = 0f; adaaX = 0f; adaaF = 0.0;
        }

        void KillAll()
        {
            for (int v = 0; v < Voices; v++) Clear(v);
        }

        void Clear(int v)
        {
            state[v] = Free;
            hasPending[v] = false;
            sustained[v] = false;
            damper[v] = 0f;
            thOn[v] = false; th1[v] = th2[v] = thIn[v] = thLp[v] = 0f;
            prevA[v] = prevB[v] = 0f;
            int b = v * Modes;
            Array.Clear(zr, b, Modes); Array.Clear(zi, b, Modes);
        }

        // ── settings ────────────────────────────────────────────────────────

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            double rel = values.Value(P.Release) * 0.001;
            releaseCoef = (float)PortableMath.Pow(10.0, -3.0 / (rel * fs));
            double vol = values.Value(P.Volume);
            volume.SetTarget(double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol));
            double driveDb = values.Value(P.Drive);
            driveGain.SetTarget((float)DspMath.DbToGain(driveDb));
            driveComp.SetTarget((float)PortableMath.Pow(DspMath.DbToGain(driveDb), -0.5));
            driveMix.SetTarget((float)Math.Min(1.0, driveDb / 6.0));
            double tone = values.Value(P.Tone);
            toneLow.SetTarget((float)DspMath.DbToGain(-6.0 * tone));
            toneHigh.SetTarget((float)DspMath.DbToGain(6.0 * tone));
            tremDepth.SetTarget(values.F(P.TremDepth));
            tremRate.SetTarget(values.F(P.TremRate));
            stereo.SetTarget(values.F(P.Stereo));
            pickup.SetTarget(values.F(P.Pickup));
            bar.SetTarget(values.F(P.Bar));
            int sel = values.Choice(P.PickupSel);
            selN.SetTarget(sel == 1 ? 0f : sel == 2 ? 0.62f : 1f);
            selB.SetTarget(sel == 0 ? 0f : sel == 2 ? 0.62f : 1f);
            for (int v = 0; v < Voices; v++) if (state[v] != Free) retarget[v] = true;
            values.Clean();
        }

        // ── notes ───────────────────────────────────────────────────────────

        public void NoteOn(int k, int n, float vel)
        {
            Update(false);
            int slot = -1;
            for (int v = 0; v < Voices; v++) if (state[v] == Free) { slot = v; break; }
            if (slot >= 0)
            {
                Begin(slot, k, n, vel);
                return;
            }
            // Steal: the quietest released voice, else the oldest held one; one already fading is taken last (its pending note is replaced).
            int best = -1; double bestScore = double.MaxValue;
            for (int v = 0; v < Voices; v++)
            {
                double score = state[v] == Fading ? 1e12 + age[v] : state[v] == Released ? damper[v] : 1e6 + age[v];
                if (score < bestScore) { bestScore = score; best = v; }
            }
            if (state[best] != Fading) StartFade(best);
            hasPending[best] = true;
            pendKey[best] = k; pendNote[best] = n; pendVel[best] = vel;
        }

        public void NoteOff(int k)
        {
            for (int v = 0; v < Voices; v++)
            {
                if (state[v] != Held || key[v] != k) continue;
                if (sustainDown) sustained[v] = true;
                else Release(v);
            }
        }

        void Release(int v)
        {
            sustained[v] = false;
            state[v] = Released;
            // The damper's thump.
            thIn[v] += 0.6f * thAmp[v] * thNorm[vmodel[v]];
            if (vmodel[v] != (int)KeysModel.String) thZr[v] += 0.6f * thAmp[v] * KnockRes[vmodel[v]];
            thOn[v] = thAmp[v] > 0f;
        }

        void StartFade(int v)
        {
            state[v] = Fading;
            sustained[v] = false;
            fadeFrom[v] = damper[v];
            fadeAt[v] = 0f;
        }

        public void ModWheel(float value) { }
        public void PitchBend(float value) { }
        public void Aftertouch(float value) { }

        public void Sustain(bool down)
        {
            sustainDown = down;
            if (down) return;
            for (int v = 0; v < Voices; v++) if (sustained[v] && state[v] == Held) Release(v);
        }

        public void AllNotesOff(bool hard)
        {
            for (int v = 0; v < Voices; v++)
            {
                hasPending[v] = false;
                if (state[v] == Free) continue;
                if (hard) { if (state[v] != Fading) StartFade(v); }
                else if (state[v] == Held) Release(v);
            }
            if (hard) sustainDown = false;
        }

        // ── a voice ─────────────────────────────────────────────────────────

        void Begin(int v, int k, float n, float vel)
        {
            int b = v * Modes;
            Array.Clear(zr, b, Modes); Array.Clear(zi, b, Modes);
            state[v] = Held;
            key[v] = k; note[v] = n;
            age[v] = ++clock;
            sustained[v] = false;
            hasPending[v] = false;
            retarget[v] = false;
            int model = values.Choice(P.Model);
            vmodel[v] = model;
            var table = KeysModels.Tables[model];
            modeCount[v] = table.Count;
            Targets(v);
            for (int m = 0; m < Modes; m++) { int i = b + m; cd[i] = tc[i]; sd[i] = ts[i]; cf[i] = (float)cd[i]; sf[i] = (float)sd[i]; }
            slewing[v] = false;
            damper[v] = 1f;
            prevA[v] = prevB[v] = 0f;

            // The hammer: its speed from the velocity through the curve, its contact time from Hardness and Touch.
            double f0 = DspMath.NoteToHz(n);
            double sp = PortableMath.Pow(Math.Clamp(vel, 0f, 1f), PortableMath.Pow(2.0, values.Value(P.VelCurve)));
            speed[v] = (float)sp;
            double contact = values.Value(P.Hardness) * PortableMath.Pow(6.0, -(2.0 * sp - 1.0) * values.Value(P.Touch));
            double sigma = Math.Clamp(contact * 0.001 / 5.0, 0.00002, 0.55 / f0);        // seconds; f0 sigma at most 0.55, so the note's own mode is still excited
            double sn = Math.Max(sigma * fs, 0.4);
            int half = Math.Min((int)Math.Ceiling(4.0 * sn), (PulseMax - 1) / 2);
            int len = 2 * half + 1;
            double w0 = 2.0 * Math.PI * f0 / fs;
            double norm = 0;
            int pb = v * PulseMax;
            for (int i = 0; i < len; i++)
            {
                double t = i - half;
                double win = Math.Abs(t) >= 4.0 * sn ? 0.0 : 0.5 + 0.5 * PortableMath.Cos(Math.PI * t / (4.0 * sn));
                double g = PortableMath.Exp(-0.5 * t * t / (sn * sn)) * win;
                pulse[pb + i] = (float)g;
                norm += g * PortableMath.Cos(w0 * t);
            }
            float scale = (float)(PortableMath.Pow(sp, KeysModels.SpeedToLevel) / Math.Max(norm, 1e-3));
            for (int i = 0; i < len; i++) pulse[pb + i] *= scale;
            pulseLen[v] = len; pulsePos[v] = 0;
            invOmega[v] = (float)(VoiceGain * KeysModels.ModelGain[model] / w0);

            // The thumps.
            noise[v] = new Rng(0x9E3779B1u + (uint)(age[v] * 2654435761u));
            float kn = values.F(P.KeyNoise);
            thAmp[v] = kn * ThumpGain * (float)PortableMath.Pow(sp, 1.2);
            th1[v] = th2[v] = thLp[v] = 0f;
            thZr[v] = thZi[v] = 0f;
            for (int j = 0; j < 4; j++) thN[v * 4 + j] = 0f;
            if (model != (int)KeysModel.String) thZr[v] = thAmp[v] * KnockRes[model];
            thIn[v] = thAmp[v] * thNorm[model];
            thOn[v] = thAmp[v] > 0f;
        }

        /// <summary>The targets of every mode of voice <paramref name="v"/> for the settings now.</summary>
        void Targets(int v)
        {
            int b = v * Modes;
            var table = KeysModels.Tables[vmodel[v]];
            double f0 = DspMath.NoteToHz(note[v]);
            double decay = values.Value(P.Decay);
            bool muted = vmodel[v] == (int)KeysModel.String && values.On(P.Mute);
            double top = TopFraction * fs;
            double[] rel = muted ? table.T60Muted : table.T60;
            for (int m = 0; m < Modes; m++)
            {
                int i = b + m;
                if (m >= table.Count) { tc[i] = 0; ts[i] = 0; continue; }
                double f = f0 * table.Ratio[m];
                if (f >= top || f <= 0.5) { tc[i] = 0; ts[i] = 0; continue; }
                double t60 = Math.Clamp(decay * rel[m], 0.006, 200.0);
                double r = PortableMath.Exp(Ln10x3 / (t60 * fs));
                double w = 2.0 * Math.PI * f / fs;
                tc[i] = r * PortableMath.Cos(w);
                ts[i] = r * PortableMath.Sin(w);
            }
        }

        // ── a block ─────────────────────────────────────────────────────────

        /// <summary>One stereo block: the sound replaces whatever is in the buffers.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            bool sounding = Core(left, right, n);
            OutMeter.Feed(left, right, n);
            return sounding;
        }

        bool Core(float[] left, float[] right, int n)
        {
            Update(false);
            bool any = false;
            for (int v = 0; v < Voices; v++) if (state[v] != Free) { any = true; break; }
            int pos = 0;
            while (pos < n)
            {
                if (tickLeft <= 0) StartTick();
                int m = Math.Min(tickLeft, n - pos);
                Chunk(pos, m);
                tickLeft -= m;
                pos += m;
            }
            for (int i = 0; i < n; i++) { left[i] = outL[i]; right[i] = outR[i]; }
            return any || IsActive;
        }

        void StartTick()
        {
            tickLeft = Tick;
            for (int v = 0; v < Voices; v++)
            {
                if (state[v] == Free) continue;
                if (retarget[v])
                {
                    retarget[v] = false;
                    if (state[v] != Fading) { Targets(v); slewing[v] = true; }
                }
                if (slewing[v]) Slew(v);
            }
        }

        void Slew(int v)
        {
            int b = v * Modes;
            bool moving = false;
            int count = modeCount[v];
            for (int m = 0; m < count; m++)
            {
                int i = b + m;
                double dc = tc[i] - cd[i], ds = ts[i] - sd[i];
                if (dc == 0 && ds == 0) continue;
                if (Math.Abs(dc) < 1e-13 && Math.Abs(ds) < 1e-13) { cd[i] = tc[i]; sd[i] = ts[i]; }
                else { cd[i] += dc * SlewPerTick; sd[i] += ds * SlewPerTick; moving = true; }
                cf[i] = (float)cd[i]; sf[i] = (float)sd[i];
            }
            slewing[v] = moving;
        }

        /// <summary>Renders <paramref name="m"/> samples (one tick or less) at <paramref name="pos"/> of the block.</summary>
        void Chunk(int pos, int m)
        {
            for (int i = 0; i < m; i++)
            {
                mix[i] = 0f;
                aArr[i] = pickup.Next();      // the closeness 0 to 1; each model scales it below
                wNArr[i] = selN.Next();
                wBArr[i] = selB.Next();
                barArr[i] = bar.Next();
            }
            for (int v = 0; v < Voices; v++)
            {
                int st = state[v];
                if (st == Free) continue;
                int b = v * Modes;
                var table = KeysModels.Tables[vmodel[v]];
                bool isString = vmodel[v] == (int)KeysModel.String;
                bool isReed = vmodel[v] == (int)KeysModel.Reed;
                float maxA = (float)KeysModels.MaxA[vmodel[v]];
                // The damper: 1 while held, the release's decay, the fade's ramp.
                float d = damper[v];
                float fadeT = fadeAt[v];
                float fadeD = (float)(1.0 / (FadeSeconds * fs));
                for (int i = 0; i < m; i++)
                {
                    if (st == Held) { tmul[i] = 1f; }
                    else if (st == Released) { d *= releaseCoef; tmul[i] = 1f; }
                    else { fadeT += fadeD; d = fadeT >= 1f ? 0f : fadeFrom[v] * (0.5f + 0.5f * PortableMathF.Cos((float)(Math.PI * fadeT))); tmul[i] = d; }
                    damp[i] = d;
                }
                damper[v] = d;
                fadeAt[v] = fadeT;

                // The drive: the hammer's force pulse.
                int pp = pulsePos[v], pl = pulseLen[v], pb = v * PulseMax;
                bool driven = pp < pl;
                if (driven)
                {
                    for (int i = 0; i < m; i++)
                    {
                        drive[i] = pp < pl ? pulse[pb + pp] : 0f;
                        pp++;
                    }
                    pulsePos[v] = Math.Min(pp, pl);
                }

                for (int i = 0; i < m; i++) { ya[i] = 0f; yb[i] = 0f; }
                int count = modeCount[v];
                for (int k = 0; k < count; k++)
                {
                    int ix = b + k;
                    float c = cf[ix], s = sf[ix];
                    if (c == 0f && s == 0f) { zr[ix] = 0f; zi[ix] = 0f; continue; }
                    float r = zr[ix], q = zi[ix];
                    float wa = table.WeightA[k], wb = table.WeightB[k];
                    if (k == 0)
                    {
                        // The slow partial: its level is Bar and body, gliding per sample.
                        for (int i = 0; i < m; i++)
                        {
                            float nr = r * c - q * s + (driven ? drive[i] : 0f);
                            q = r * s + q * c;
                            r = nr;
                            float qb = barArr[i] * q;
                            ya[i] += wa * qb;
                            if (isString) yb[i] += wb * qb;
                        }
                    }
                    else if (isString)
                    {
                        for (int i = 0; i < m; i++)
                        {
                            float nr = r * c - q * s + (driven ? drive[i] : 0f);
                            q = r * s + q * c;
                            r = nr;
                            ya[i] += wa * q;
                            yb[i] += wb * q;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < m; i++)
                        {
                            float nr = r * c - q * s + (driven ? drive[i] : 0f);
                            q = r * s + q * c;
                            r = nr;
                            ya[i] += wa * q;
                        }
                    }
                    zr[ix] = r; zi[ix] = q;
                }

                // The pickup: the displacement through the flux curve, differenced.
                float inv = invOmega[v], pin = KeysModels.PickupIn[vmodel[v]], dgain = VoiceGain * KeysModels.ModelGain[vmodel[v]];
                float pa = prevA[v], pbv = prevB[v];
                bool thump = thOn[v];
                float rho = thRho[vmodel[v]], lpk = thLpK[vmodel[v]];
                float t1 = th1[v], t2 = th2[v], tl = thLp[v];
                float kr = thZr[v], ki = thZi[v], rc = thRc[vmodel[v]], rs = thRs[vmodel[v]], nk = thNzK[vmodel[v]], ng = KnockNoise[vmodel[v]];
                float n1 = thN[v * 4], n2 = thN[v * 4 + 1], n3 = thN[v * 4 + 2], n4 = thN[v * 4 + 3];
                if (thump) { t1 += thIn[v]; thIn[v] = 0f; }
                var rng = noise[v];
                for (int i = 0; i < m; i++)
                {
                    float a = maxA * aArr[i];
                    float o;
                    if (isString)
                    {
                        float fa = KeysModels.Flux(KeysModel.String, ya[i] * damp[i] * pin, a);
                        float fb = KeysModels.Flux(KeysModel.String, yb[i] * damp[i] * pin, a);
                        o = (wNArr[i] * (fa - pa) + wBArr[i] * (fb - pbv)) * inv;
                        pa = fa; pbv = fb;
                    }
                    else
                    {
                        float fa = KeysModels.Flux(isReed ? KeysModel.Reed : KeysModel.Tine, ya[i] * damp[i] * pin, a);
                        if (isReed) o = 0.7f * fa * dgain + 0.3f * (fa - pa) * inv;
                        else o = (fa - pa) * inv;
                        pa = fa;
                    }
                    if (thump)
                    {
                        t1 *= rho;
                        t2 = t2 * rho + t1;
                        if (isString)
                        {
                            tl += lpk * (rng.NextBipolar() - tl);
                            o += tl * t2 * tmul[i];
                        }
                        else
                        {
                            // The knock: the resonance rings down; the noise is under the alpha envelope, through four one-poles.
                            float nkr = rc * kr - rs * ki;
                            ki = rs * kr + rc * ki; kr = nkr;
                            n1 += nk * (rng.NextBipolar() - n1);
                            n2 += nk * (n1 - n2);
                            n3 += nk * (n2 - n3);
                            n4 += nk * (n3 - n4);
                            tl += lpk * (ki - tl);                    // a smoother start for the resonance
                            o += (tl + ng * n4 * t2) * tmul[i];
                        }
                    }
                    mix[i] += o;
                }
                prevA[v] = pa; prevB[v] = pbv;
                noise[v] = rng;
                if (thump)
                {
                    th1[v] = t1; th2[v] = t2; thLp[v] = tl;
                    thZr[v] = kr; thZi[v] = ki; thN[v * 4] = n1; thN[v * 4 + 1] = n2; thN[v * 4 + 2] = n3; thN[v * 4 + 3] = n4;
                    if (t1 < 1e-6f && t2 < 1e-6f && thIn[v] == 0f && Math.Abs(kr) < 1e-6f && Math.Abs(ki) < 1e-6f)
                    {
                        thOn[v] = false; th1[v] = th2[v] = 0f; thZr[v] = thZi[v] = 0f;
                        for (int j = 0; j < 4; j++) thN[v * 4 + j] = 0f;
                    }
                }

                // The end of the chunk: a fade that has landed starts the pending note or frees the voice; quiet voices are freed.
                if (st == Fading && d <= 0f)
                {
                    if (hasPending[v])
                    {
                        int pk = pendKey[v]; float pn = pendNote[v], pv = pendVel[v];
                        Clear(v);
                        Begin(v, pk, pn, pv);
                    }
                    else Clear(v);
                    continue;
                }
                Quiet(v);
            }

            // Master: the drive, the tone tilt, the tremolo with auto-pan, the volume and a soft limit.
            for (int i = 0; i < m; i++)
            {
                float x = mix[i];
                float g = driveGain.Next(), comp = driveComp.Next(), dmix = driveMix.Next();
                if (dmix > 0f)
                {
                    float xi = x * g;
                    float y = AdaaTanh(xi) * comp;
                    x += dmix * (y - x);
                }
                else { adaaX = 0f; adaaF = 0.0; }
                float lo = toneLow.Next(), hi = toneHigh.Next();
                toneState += toneK * (x - toneState);
                toneState = DspMath.Flush(toneState);
                x = toneState * lo + (x - toneState) * hi;
                float vol = volume.Next();
                float depth = tremDepth.Next();
                float rate = tremRate.Next();
                float pan = stereo.Next();
                float s = 0.5f - 0.5f * PortableMathF.Cos((float)tremPhase);
                float gl = 1f - depth * s;
                float gr = 1f - depth * (s * (1f - pan) + (1f - s) * pan);
                tremPhase += 2.0 * Math.PI * rate / fs;
                if (tremPhase > 2.0 * Math.PI) tremPhase -= 2.0 * Math.PI;
                outL[pos + i] = DspMath.Flush(SoftLimit(x * vol * gl));
                outR[pos + i] = DspMath.Flush(SoftLimit(x * vol * gr));
            }
        }

        /// <summary>First-order antiderivative anti-aliased tanh: (F(x1) - F(x0)) / (x1 - x0), F = ln cosh.</summary>
        float AdaaTanh(float x)
        {
            double ax = Math.Abs((double)x);
            double f = ax + PortableMath.Log1p(PortableMath.Exp(-2.0 * ax)) - 0.6931471805599453;
            float y;
            float dx = x - adaaX;
            if (Math.Abs(dx) < 1e-4f) y = DspMath.FastTanh(0.5f * (x + adaaX));
            else y = (float)((f - adaaF) / dx);
            adaaX = x; adaaF = f;
            return y;
        }

        /// <summary>Flushes tiny state to zero and frees a voice that has died away (or a damped one).</summary>
        void Quiet(int v)
        {
            int b = v * Modes;
            double level = 0;
            int count = modeCount[v];
            for (int k = 0; k < count; k++)
            {
                int i = b + k;
                float a = zr[i], q = zi[i];
                if (a < 1e-18f && a > -1e-18f) zr[i] = a = 0f;
                if (q < 1e-18f && q > -1e-18f) zi[i] = q = 0f;
                level += Math.Abs(a) + Math.Abs(q);
            }
            int st = state[v];
            if (st == Released && damper[v] < 1e-5f && !thOn[v]) { Clear(v); return; }
            if (pulsePos[v] >= pulseLen[v] && level < 1e-7 && !thOn[v]) Clear(v);
        }

        /// <summary>Transparent below 0.7 (-3.1 dBFS); above it a smooth knee (slope 1 at the knee) to a ceiling of 0.89 (-1 dBFS), which it never reaches.</summary>
        static float SoftLimit(float x)
        {
            float a = Math.Abs(x);
            if (a <= LimitKnee) return x;
            float y = LimitKnee + (LimitCeiling - LimitKnee) * DspMath.FastTanh((a - LimitKnee) * (1f / (LimitCeiling - LimitKnee)));
            return x < 0 ? -y : y;
        }

        // ── for the editor and the tests ────────────────────────────────────

        /// <summary>The modes of the voice playing <paramref name="k"/> as the loop has them: frequency (Hz) and T60 (s); the count.</summary>
        public int Snapshot(int k, double[] hz, double[] t60)
        {
            for (int v = 0; v < Voices; v++)
            {
                if (state[v] == Free || key[v] != k) continue;
                int b = v * Modes;
                int count = Math.Min(modeCount[v], hz.Length);
                for (int m = 0; m < count; m++)
                {
                    double c = cf[b + m], s = sf[b + m];
                    double r = Math.Sqrt(c * c + s * s);
                    hz[m] = r > 0 ? PortableMath.Atan2(s, c) * fs / (2.0 * Math.PI) : 0;
                    t60[m] = r > 0 && r < 1 ? -3.0 / (fs * PortableMath.Log10(r)) : 0;
                }
                return count;
            }
            return 0;
        }
    }
}
