#nullable disable
// Neo Mallet (docs/specs/neo-mallet.md): a modal synth. Each voice is a bank of up to 32 modes, each a decaying complex oscillator
//     z[n+1] = z[n] * r e^{j w} + g x[n],   output = Re z less half the input (the mode's velocity),
// which is a 2-pole resonator with its own frequency, gain and T60 (Adrien 1991; Cook, Real Sound Synthesis). x is a
// raised-cosine force pulse (area 1) whose contact time follows Hardness and velocity, plus a little noise. Coefficients follow the settings with a short slew; a released
// voice is damped by Release damping, a stolen or killed one fades out in 5 ms (a raised cosine, so the fade has no corner). 16 voices. Pure; no allocation after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Mallet
{
    internal sealed class MalletEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int Voices = 16;
        public const int Modes = ModeSet.Max;
        public const int Tick = 16;
        /// <summary>The highest mode frequency, as a fraction of the sample rate (modes above it are dropped).</summary>
        public const double TopFraction = 0.45;
        /// <summary>The length of a steal's or a kill's fade, seconds (at least 3 ms).</summary>
        public const double FadeSeconds = 0.005;
        const float StrikeGain = 0.65f;
        const double Ln10x3 = -3.0 * 2.302585092994046;      // r = 10^(-3 / (T60 fs)) = exp(this / (T60 fs))
        const float LimitKnee = 0.7f, LimitCeiling = 0.89f;
        const float NoiseGain = 4f;
        const double SlewPerTick = 0.012;

        const int Free = 0, Held = 1, Released = 2, Fading = 3;
        const int HannSize = 512;
        const int MidiKeyBase = 1000;

        readonly ParamValues values;
        double fs = 48000;

        readonly ModeSet set = new ModeSet();

        // Per voice.
        readonly int[] state = new int[Voices];
        readonly int[] key = new int[Voices];
        readonly float[] note = new float[Voices];
        readonly float[] velocity = new float[Voices];
        readonly long[] age = new long[Voices];
        readonly float[] damper = new float[Voices];
        readonly float[] fadeFrom = new float[Voices], fadeAt = new float[Voices];
        readonly bool[] sustained = new bool[Voices];
        readonly bool[] retarget = new bool[Voices];
        readonly bool[] slewing = new bool[Voices];
        readonly int[] modeCount = new int[Voices];
        readonly int[] pulseLen = new int[Voices], pulsePos = new int[Voices];
        readonly float[] pulseAmp = new float[Voices], pulseNoise = new float[Voices];
        readonly Rng[] noise = new Rng[Voices];
        readonly bool[] hasPending = new bool[Voices];
        readonly int[] pendKey = new int[Voices];
        readonly float[] pendNote = new float[Voices], pendVel = new float[Voices];
        readonly float[] panL = new float[Voices], panR = new float[Voices];
        long clock;
        bool sustainDown;
        float releaseCoef = 0.999f;

        // Per voice per mode (voice * Modes + mode): the state, the coefficients the loop reads (float), and the double ones they follow.
        readonly float[] zr = new float[Voices * Modes], zi = new float[Voices * Modes];
        readonly float[] cf = new float[Voices * Modes], sf = new float[Voices * Modes], gf = new float[Voices * Modes], gp = new float[Voices * Modes];   // gp: the drive gain the last chunk ended on
        readonly double[] cd = new double[Voices * Modes], sd = new double[Voices * Modes], gd = new double[Voices * Modes];
        readonly double[] tc = new double[Voices * Modes], ts = new double[Voices * Modes], tg = new double[Voices * Modes];
        readonly double[] gainScratch = new double[Modes];
        // The mode table is refilled only when its own settings change (a moving Decay or Position must not redo it), with the natural log of each ratio to the carrier's.
        readonly double[] lnRel = new double[Modes];
        int tableModel = -1, tableModes = -1; double tableStretch = double.NaN, tableInharm = double.NaN;

        // Block buffers.
        readonly float[] outL = new float[MaxBlock], outR = new float[MaxBlock];
        readonly float[] drive = new float[Tick], acc = new float[Tick], damp = new float[Tick];
        readonly float[] hann = new float[HannSize + 2];

        int tickLeft;
        SmoothRamp volume, tremDepth, tremRate, spread;
        double tremPhase;
        float spreadNow;

        public readonly LevelMeter OutMeter = new LevelMeter();

        public MalletEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i <= HannSize + 1; i++) hann[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2.0 * Math.PI * Math.Min(i, HannSize) / HannSize));
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
            volume.Setup(0.02, rate); tremDepth.Setup(0.03, rate); tremRate.Setup(0.05, rate); spread.Setup(0.05, rate / Tick);
            OutMeter.SetSampleRate(rate);
            AllNotesOff(true);
            KillAll();
            Update(true);
            volume.Reset(volume.Target); tremDepth.Reset(tremDepth.Target); tremRate.Reset(tremRate.Target); spread.Reset(spread.Target); spreadNow = spread.Target;
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
            int b = v * Modes;
            Array.Clear(zr, b, Modes); Array.Clear(zi, b, Modes);
            Array.Clear(gf, b, Modes); Array.Clear(gp, b, Modes); Array.Clear(gd, b, Modes); Array.Clear(tg, b, Modes);
        }

        // ── settings ────────────────────────────────────────────────────────

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var model = (MalletModel)values.Choice(P.Model);
            int nModes = (int)values.Value(P.Modes); double stretch = values.Value(P.Stretch), inharm = values.Value(P.Inharm);
            if ((int)model != tableModel || nModes != tableModes || stretch != tableStretch || inharm != tableInharm)
            {
                MalletModels.Fill(set, model, nModes, stretch, inharm);
                tableModel = (int)model; tableModes = nModes; tableStretch = stretch; tableInharm = inharm;
                double refRatio0 = set.Ratio[set.Ref];
                for (int m = 0; m < set.Count; m++) lnRel[m] = set.Ratio[m] > 0 ? PortableMath.Log(set.Ratio[m] / refRatio0) : 0.0;
            }
            double rel = values.Value(P.Release) * 0.001;
            releaseCoef = (float)PortableMath.Pow(10.0, -3.0 / (rel * fs));
            double vol = values.Value(P.Volume);
            volume.SetTarget(double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol));
            tremDepth.SetTarget(values.F(P.TremDepth));
            tremRate.SetTarget(values.F(P.TremRate));
            spread.SetTarget(values.F(P.Spread));
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
            key[v] = k; note[v] = n; velocity[v] = vel;
            age[v] = ++clock;
            sustained[v] = false;
            hasPending[v] = false;
            modeCount[v] = set.Count;
            retarget[v] = false;
            Targets(v);
            for (int m = 0; m < Modes; m++)
            {
                int i = b + m;
                cd[i] = tc[i]; sd[i] = ts[i]; gd[i] = tg[i];
                cf[i] = (float)cd[i]; sf[i] = (float)sd[i]; gf[i] = (float)gd[i]; gp[i] = gf[i];
            }
            slewing[v] = false;
            damper[v] = 1f;
            double contact = values.Value(P.Hardness);
            double vh = values.Value(P.VelHard);
            contact *= PortableMath.Pow(8.0, -(2.0 * vel - 1.0) * vh);
            if (set.Plucked) contact *= 0.3;
            contact = Math.Clamp(contact, 0.08, 30.0);
            int len = Math.Max(2, (int)Math.Round(contact * 0.001 * fs));
            pulseLen[v] = len; pulsePos[v] = 0;
            pulseAmp[v] = StrikeGain * vel;
            pulseNoise[v] = (float)(values.Value(P.Noise) * NoiseGain * (set.Plucked ? 0.5 : 1.0));
            noise[v] = new Rng(0x9E3779B1u + (uint)(age[v] * 2654435761u));
            panL[v] = panR[v] = -1f;                   // set at the first chunk
        }

        /// <summary>The targets of every mode of voice <paramref name="v"/> for the settings now.</summary>
        void Targets(int v)
        {
            int b = v * Modes;
            double f0 = DspMath.NoteToHz(note[v]);
            double decay = values.Value(P.Decay);
            double tilt = values.Value(P.Tilt) * set.TiltScale;
            double bright = values.Value(P.Bright);
            double pos = values.Value(P.Position);
            double top = TopFraction * fs;
            double refRatio = set.Ratio[set.Ref];
            double sum2 = 0;
            for (int m = 0; m < set.Count; m++)
            {
                double ratio = set.Ratio[m] / refRatio;       // the table's ratio to the mode that carries the note: a stiff string's fundamental sits at the note too
                double f = f0 * ratio;
                int i = b + m;
                if (f >= top || f <= 0.5)
                {
                    tc[i] = 0; ts[i] = 0; gainScratch[m] = 0; tg[i] = 0;
                    continue;
                }
                double t60 = decay * Math.Clamp(PortableMath.Exp(-2.0 * tilt * lnRel[m]), 0.001, 3.0);
                t60 = Math.Clamp(t60, 0.008, 80.0);
                double r = PortableMath.Exp(Ln10x3 / (t60 * fs));
                double w = 2.0 * Math.PI * f / fs;
                tc[i] = r * PortableMath.Cos(w);
                ts[i] = r * PortableMath.Sin(w);
                double g = set.BaseGain[m] * MalletModels.Weight(set, m, pos) * PortableMath.Exp(bright * lnRel[m]);
                gainScratch[m] = g;
                sum2 += g * g;
                tg[i] = g * set.Level;
            }
            for (int m = set.Count; m < Modes; m++) tg[b + m] = 0;
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
            spreadNow = spread.Next();
            for (int v = 0; v < Voices; v++)
            {
                if (state[v] == Free) continue;
                if (retarget[v])
                {
                    retarget[v] = false;
                    if (state[v] != Fading)
                    {
                        if (set.Count > modeCount[v]) modeCount[v] = set.Count;
                        Targets(v);
                        slewing[v] = true;
                    }
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
                double dc = tc[i] - cd[i], ds = ts[i] - sd[i], dg = tg[i] - gd[i];
                if (dc == 0 && ds == 0 && dg == 0) continue;
                if (Math.Abs(dc) < 1e-13 && Math.Abs(ds) < 1e-13 && Math.Abs(dg) < 1e-9) { cd[i] = tc[i]; sd[i] = ts[i]; gd[i] = tg[i]; }
                else { cd[i] += dc * SlewPerTick; sd[i] += ds * SlewPerTick; gd[i] += dg * SlewPerTick; moving = true; }
                cf[i] = (float)cd[i]; sf[i] = (float)sd[i]; gf[i] = (float)gd[i];
            }
            slewing[v] = moving;
        }

        /// <summary>Renders <paramref name="m"/> samples (one tick or less) at <paramref name="pos"/> of the block.</summary>
        void Chunk(int pos, int m)
        {
            for (int i = 0; i < m; i++) { outL[pos + i] = 0f; outR[pos + i] = 0f; }
            double top = TopFraction * fs;
            float sp = spreadNow;
            for (int v = 0; v < Voices; v++)
            {
                int st = state[v];
                if (st == Free) continue;
                int b = v * Modes;
                // The damper: 1 while held, the release's decay, the fade's ramp.
                float d = damper[v];
                float fadeT = fadeAt[v];
                float fadeD = (float)(1.0 / (FadeSeconds * fs));
                for (int i = 0; i < m; i++)
                {
                    if (st == Held) { }
                    else if (st == Released) d *= releaseCoef;
                    else { fadeT += fadeD; d = fadeT >= 1f ? 0f : fadeFrom[v] * (0.5f + 0.5f * PortableMathF.Cos((float)(Math.PI * fadeT))); }
                    damp[i] = d;
                }
                damper[v] = d;
                fadeAt[v] = fadeT;

                // The drive: the mallet's force pulse.
                {
                    int pp = pulsePos[v], pl = pulseLen[v];
                    for (int i = 0; i < m; i++)
                    {
                        if (pp < pl)
                        {
                            float t = (pp + 0.5f) / pl * HannSize;
                            int q = (int)t;
                            float e = hann[q] + (hann[q + 1] - hann[q]) * (t - q);
                            e *= 2f / pl * pulseAmp[v];
                            drive[i] = e * (1f + pulseNoise[v] * noise[v].NextBipolar());
                            pp++;
                        }
                        else drive[i] = 0f;
                    }
                    pulsePos[v] = pp;
                }

                for (int i = 0; i < m; i++) acc[i] = 0f;
                int count = modeCount[v];
                for (int k = 0; k < count; k++)
                {
                    int ix = b + k;
                    float c = cf[ix], s = sf[ix], g = gf[ix];
                    if (c == 0f && s == 0f) { zr[ix] = 0f; zi[ix] = 0f; gp[ix] = g; continue; }
                    float r = zr[ix], q = zi[ix];
                    // The drive gain glides from where the last chunk left it to its new value over this chunk, so a change of Position,
                    // Brightness, Decay makes no step in what the mode is fed.
                    float gi = gp[ix], dgi = (g - gi) / m;
                    gp[ix] = g;
                    for (int i = 0; i < m; i++)
                    {
                        gi += dgi;
                        float nr = r * c - q * s + gi * drive[i];
                        q = r * s + q * c;
                        r = nr;
                        // The mode's velocity (the cosine phase), less the half sample of the input that the one-step update puts into it: a slow force
                        // (the mallet's own pulse) then leaves nothing in the output but what it sets ringing, and a strike starts without a thump.
                        acc[i] += r - 0.5f * gi * drive[i];
                    }
                    zr[ix] = r; zi[ix] = q;
                }

                // The pan, moving over the chunk.
                float pan = Math.Clamp((note[v] - 60f) / 30f, -1f, 1f) * sp;
                DspMath.PanGains(pan, out float gl, out float gr);
                gl *= 0.70710678f; gr *= 0.70710678f;
                if (panL[v] < 0f) { panL[v] = gl; panR[v] = gr; }
                float dl = (gl - panL[v]) / m, dr = (gr - panR[v]) / m;
                float pl2 = panL[v], pr2 = panR[v];
                for (int i = 0; i < m; i++)
                {
                    pl2 += dl; pr2 += dr;
                    float o = acc[i] * damp[i];
                    outL[pos + i] += o * pl2;
                    outR[pos + i] += o * pr2;
                }
                panL[v] = gl; panR[v] = gr;

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
            // Master: tremolo, volume, a soft limit.
            for (int i = 0; i < m; i++)
            {
                float vol = volume.Next();
                float depth = tremDepth.Next();
                float rate = tremRate.Next();
                float gain = 1f;
                if (depth > 0f)
                {
                    gain = 1f - depth * (0.5f - 0.5f * PortableMathF.Cos((float)tremPhase));
                }
                tremPhase += 2.0 * Math.PI * rate / fs;
                if (tremPhase > 2.0 * Math.PI) tremPhase -= 2.0 * Math.PI;
                float g = vol * gain;
                outL[pos + i] = DspMath.Flush(SoftLimit(outL[pos + i] * g));
                outR[pos + i] = DspMath.Flush(SoftLimit(outR[pos + i] * g));
            }
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
            if (st == Released && damper[v] < 1e-5f) { Clear(v); return; }
            if (pulsePos[v] >= pulseLen[v] && level < 1e-7) Clear(v);
        }

        /// <summary>Transparent below 0.7 (-3.1 dBFS); above it a smooth knee (slope 1 at the knee) to a ceiling of 0.89 (-1 dBFS), which it never reaches.</summary>
        static float SoftLimit(float x)
        {
            float a = Math.Abs(x);
            if (a <= LimitKnee) return x;
            float y = LimitKnee + (LimitCeiling - LimitKnee) * DspMath.FastTanh((a - LimitKnee) * (1f / (LimitCeiling - LimitKnee)));   // a strike's coherent peak is rounded, not clipped
            return x < 0 ? -y : y;
        }

        // ── for the editor and the tests ────────────────────────────────────

        /// <summary>The modes of the voice playing <paramref name="k"/> as the loop has them: frequency (Hz), T60 (s) and drive gain; the count.</summary>
        public int Snapshot(int k, double[] hz, double[] t60, double[] gain)
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
                    gain[m] = gf[b + m];
                }
                return count;
            }
            return 0;
        }

        /// <summary>The mode table the engine rings now (a copy for the editor is made by <see cref="MalletModels.Fill"/>).</summary>
        public ModeSet CurrentModes => set;
    }
}
