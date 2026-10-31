#nullable disable
// Neo Poly (docs/specs/machines.md §5): polyphonic virtual analog. Per voice: three BLEP
// oscillators (shape morph, PWM, sync, through-zero FM, ring), noise, two filters (ladder or SVF,
// then SVF; serial or parallel), amp/filter/mod envelopes, two LFOs, an 8-slot mod matrix and
// analog drift. Modulation runs at control rate (every 16 samples), audio per sample. Pure.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Poly
{
    internal enum ModSource { Off, Lfo1, Lfo2, ModEnv, FilterEnv, AmpEnv, Velocity, Key, ModWheel, Aftertouch, Random, Unison }

    internal enum ModDest
    {
        Off, Pitch, Osc1Pitch, Osc2Pitch, Osc3Pitch, Osc1Shape, Osc2Shape, Osc3Shape, Osc1Pw, Osc2Pw, Fm,
        Osc1Level, Osc2Level, Osc3Level, NoiseLevel, RingLevel, F1Cutoff, F1Reso, F2Cutoff, F2Reso, Amp, Pan, Lfo1Rate, Lfo2Rate,
        Count,
    }

    /// <summary>The decoded parameters voices read (updated once per block when something changed).</summary>
    internal sealed class PolySettings
    {
        public VoiceMode Mode;
        public int Polyphony, Unison;
        public float UniDetune, UniSpread, BendRange, Drift, VelAmp, Volume;
        public bool GlideAlways, GlideLegato;
        public float GlideTime;
        public float[] Shape = new float[3], Pw = new float[3], Transpose = new float[3], Level = new float[3];
        public bool Sync;
        public float Fm, Ring, Noise, NoiseColor;
        public int F1Type, F2Type;
        public float F1Cutoff, F1Reso, F1Drive, F1Env, F1Key, F1Vel, F2Cutoff, F2Reso, F2Env;
        public bool Parallel;
        public float[] AmpEnv = new float[4], FltEnv = new float[4], ModEnv = new float[4];
        public LfoShape[] LfoShapes = new LfoShape[2];
        public float[] LfoRates = new float[2], LfoFades = new float[2];
        public bool[] LfoRetrigs = new bool[2];
        public ModSource[] MatrixSrc = new ModSource[8];
        public ModDest[] MatrixDst = new ModDest[8];
        public float[] MatrixAmt = new float[8];
        public int Version;
    }

    internal sealed class PolyEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int VoiceCount = 32;
        public const int ControlRate = 16;
        static readonly double[] SyncBeats = { 0, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 2, 4, 8, 16 };

        readonly ParamValues values;
        readonly PolyVoice[] voices = new PolyVoice[VoiceCount];
        readonly VoiceAllocator alloc;
        internal readonly PolySettings S = new PolySettings();
        float sampleRate = 48000;
        double bpm = 120;
        Smoother volume;
        readonly Lfo globalLfo1 = new Lfo(11), globalLfo2 = new Lfo(23);
        int controlCounter;

        public PolyEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < VoiceCount; i++) voices[i] = new PolyVoice(this, (uint)(0x51ED270Bu * (i + 1)));
            alloc = new VoiceAllocator(voices);
            SetSampleRate(48000);
        }

        public readonly LevelMeter OutMeter = new LevelMeter();

        public float SampleRate => sampleRate;
        internal float ModWheelValue, PitchBendSemis, AftertouchValue;
        internal float GlobalLfo1, GlobalLfo2;

        /// <summary>Beats per minute, for tempo-synced LFOs.</summary>
        public double Tempo { get => bpm; set => bpm = value > 0 ? value : 120; }

        public bool IsActive
        {
            get
            {
                foreach (var v in voices) if (v.IsActive) return true;
                return false;
            }
        }

        public int ActiveVoices
        {
            get
            {
                int n = 0;
                foreach (var v in voices) if (v.IsActive) n++;
                return n;
            }
        }

        internal PolyVoice Voice(int i) => voices[i];

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            volume.Setup(0.02, sr);
            OutMeter.SetSampleRate(sr);
            foreach (var v in voices) v.SetSampleRate(sr);
            Update(true);
            volume.Reset(S.Volume);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var s = S;
            s.Mode = (VoiceMode)values.Choice(P.VoiceMode);
            s.Polyphony = (int)values.Value(P.Polyphony);
            s.Unison = (int)values.Value(P.Unison);
            s.UniDetune = values.F(P.UniDetune);
            s.UniSpread = values.F(P.UniSpread);
            int glide = values.Choice(P.GlideMode);
            s.GlideAlways = glide == 1;
            s.GlideLegato = glide == 2;
            s.GlideTime = values.F(P.Glide);
            s.BendRange = values.F(P.BendRange);
            s.Drift = values.F(P.Drift);
            double vol = values.Value(P.Volume);
            s.Volume = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            s.VelAmp = values.F(P.VelAmp);

            s.Shape[0] = values.F(P.O1Shape); s.Pw[0] = values.F(P.O1PW) / 100f;
            s.Transpose[0] = values.F(P.O1Oct) * 12 + values.F(P.O1Semi) + values.F(P.O1Fine) / 100f;
            s.Level[0] = values.F(P.O1Level);
            s.Shape[1] = values.F(P.O2Shape); s.Pw[1] = values.F(P.O2PW) / 100f;
            s.Transpose[1] = values.F(P.O2Oct) * 12 + values.F(P.O2Semi) + values.F(P.O2Fine) / 100f;
            s.Level[1] = values.F(P.O2Level);
            s.Sync = values.On(P.O2Sync);
            s.Fm = values.F(P.O2Fm);
            s.Shape[2] = values.F(P.O3Shape); s.Pw[2] = 0.5f;
            s.Transpose[2] = values.F(P.O3Oct) * 12 + values.F(P.O3Semi) + values.F(P.O3Fine) / 100f;
            s.Level[2] = values.F(P.O3Level);
            s.Ring = values.F(P.RingLevel);
            s.Noise = values.F(P.NoiseLevel);
            s.NoiseColor = values.F(P.NoiseColor);

            s.F1Type = values.Choice(P.F1Type);
            s.F1Cutoff = values.F(P.F1Cutoff);
            s.F1Reso = values.F(P.F1Reso);
            s.F1Drive = (float)DspMath.DbToGain(values.Value(P.F1Drive));
            s.F1Env = values.F(P.F1Env) * 6;
            s.F1Key = values.F(P.F1Key);
            s.F1Vel = values.F(P.F1Vel) * 3;
            s.F2Type = values.Choice(P.F2Type);
            s.F2Cutoff = values.F(P.F2Cutoff);
            s.F2Reso = values.F(P.F2Reso);
            s.F2Env = values.F(P.F2Env) * 6;
            s.Parallel = values.Choice(P.Routing) == 1;

            Env(s.AmpEnv, P.AmpA, P.AmpD, P.AmpS, P.AmpR);
            Env(s.FltEnv, P.FltA, P.FltD, P.FltS, P.FltR);
            Env(s.ModEnv, P.ModA, P.ModD, P.ModS, P.ModR);

            DecodeLfo(0, P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade);
            DecodeLfo(1, P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade);
            for (int m = 0; m < 8; m++)
            {
                int b = P.M1Src + 3 * m;
                s.MatrixSrc[m] = (ModSource)values.Choice(b);
                s.MatrixDst[m] = (ModDest)values.Choice(b + 1);
                s.MatrixAmt[m] = values.F(b + 2);
            }

            alloc.Mode = s.Mode;
            alloc.Polyphony = s.Polyphony;
            alloc.Unison = s.Unison;
            alloc.GlideAlways = s.GlideAlways;
            alloc.GlideLegatoOnly = s.GlideLegato;
            volume.Target = s.Volume;
            s.Version++;
            values.Clean();
        }

        void Env(float[] e, int a, int d, int s, int r)
        {
            e[0] = values.F(a);
            e[1] = values.F(d);
            e[2] = values.F(s);
            e[3] = values.F(r);
        }

        void DecodeLfo(int i, int shape, int rate, int sync, int retrig, int fade)
        {
            S.LfoShapes[i] = (LfoShape)values.Choice(shape);
            int sy = values.Choice(sync);
            S.LfoRates[i] = sy > 0 ? (float)(bpm / 60.0 / SyncBeats[sy]) : values.F(rate);
            S.LfoRetrigs[i] = values.On(retrig);
            S.LfoFades[i] = values.F(fade);
        }

        // ── notes ───────────────────────────────────────────────────────────

        public void NoteOn(int key, int note, float velocity) { Update(false); alloc.NoteOn(key, note, velocity); }
        public void NoteOff(int key) => alloc.NoteOff(key);
        public void ModWheel(float value) => ModWheelValue = value;
        public void PitchBend(float value) => PitchBendSemis = value * S.BendRange;
        public void Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) => AftertouchValue = value;
        public void AllNotesOff(bool hard) => alloc.AllNotesOff(hard);

        /// <summary>Renders a block (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            // Tempo can change without a parameter change: synced rates follow it.
            if (values[P.L1Sync] > 0 || values[P.L2Sync] > 0)
            {
                DecodeLfo(0, P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade);
                DecodeLfo(1, P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade);
            }
            Update(false);
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
            bool any = false;
            int pos = 0;
            while (pos < n)
            {
                if (controlCounter <= 0)
                {
                    globalLfo1.Shape = S.LfoShapes[0];
                    globalLfo1.Rate = S.LfoRates[0];
                    globalLfo2.Shape = S.LfoShapes[1];
                    globalLfo2.Rate = S.LfoRates[1];
                    GlobalLfo1 = globalLfo1.Advance(ControlRate, sampleRate);
                    GlobalLfo2 = globalLfo2.Advance(ControlRate, sampleRate);
                    controlCounter = ControlRate;
                }
                int m = Math.Min(controlCounter, n - pos);
                foreach (var v in voices)
                    if (v.IsActive || v.Pending)
                    {
                        v.Render(left, right, pos, m);
                        any = true;
                    }
                controlCounter -= m;
                pos += m;
            }
            for (int i = 0; i < n; i++)
            {
                float g = volume.Next() * 0.5f;
                left[i] *= g;
                right[i] *= g;
            }
            OutMeter.Feed(left, right, n);
            return any;
        }
    }

    /// <summary>One voice.</summary>
    internal sealed class PolyVoice : IVoice
    {
        readonly PolyEngine engine;
        readonly BlepOscillator[] osc = { new BlepOscillator(), new BlepOscillator(), new BlepOscillator() };
        readonly Adsr ampEnv = new Adsr(), fltEnv = new Adsr(), modEnv = new Adsr();
        readonly Lfo lfo1, lfo2;
        Ladder ladder;
        Svf svf1, svf2;
        Rng rng;
        PinkNoise pink;
        StealFade steal;
        float sr = 48000;
        int envVersion = -1;

        // Note state.
        float note, velocity, randomValue, unisonPos;
        float pitch, pitchTarget, glideStep;
        bool pending, pendRelease;
        float pendNote, pendVel, pendGlideFrom;
        int pendUni, pendUniCount;

        // Drift (slow random walk).
        float driftPitch, driftCut, driftPitchTarget, driftCutTarget;
        int driftCounter;

        // Control-rate values used per sample.
        double inc0, inc1, inc2;
        float lvl0, lvl1, lvl2, ring, noise, noiseColor, fm, panL = 1, panR = 1, ampMod = 1;
        bool sync;
        readonly float[] dst = new float[(int)ModDest.Count];

        public PolyVoice(PolyEngine engine, uint seed)
        {
            this.engine = engine;
            rng = new Rng(seed);
            lfo1 = new Lfo(seed ^ 0xA5A5A5A5u);
            lfo2 = new Lfo(seed ^ 0x5A5A5A5Au);
            for (int i = 0; i < 3; i++) osc[i].Reset(rng.NextUnipolar());
        }

        public void SetSampleRate(float rate)
        {
            sr = rate;
            ampEnv.SetSampleRate(rate);
            fltEnv.SetSampleRate(rate);
            modEnv.SetSampleRate(rate);
            envVersion = -1;
        }

        public bool IsActive => ampEnv.IsActive || steal.Fading;
        public bool Pending => pending;
        public float Loudness => ampEnv.Level;
        public float Pitch => pitch;

        public void NoteOn(float n, float vel, int unisonIndex, int unisonCount, float glideFrom)
        {
            if (ampEnv.IsActive && ampEnv.Level > 0.001f && !pending)
            {
                // Steal: fade the old note out, then start this one.
                pending = true;
                pendRelease = false;
                pendNote = n; pendVel = vel; pendUni = unisonIndex; pendUniCount = unisonCount; pendGlideFrom = glideFrom;
                steal.Start(sr);
                return;
            }
            if (pending)
            {
                pendNote = n; pendVel = vel; pendUni = unisonIndex; pendUniCount = unisonCount; pendGlideFrom = glideFrom;
                return;
            }
            Start(n, vel, unisonIndex, unisonCount, glideFrom);
        }

        void Start(float n, float vel, int unisonIndex, int unisonCount, float glideFrom)
        {
            var s = engine.S;
            note = n;
            velocity = vel;
            unisonPos = unisonCount > 1 ? 2f * unisonIndex / (unisonCount - 1) - 1f : 0f;
            randomValue = rng.NextBipolar();
            pitchTarget = n;
            if (!float.IsNaN(glideFrom) && s.GlideTime > 0.0005f)
            {
                pitch = glideFrom;
                glideStep = Math.Abs(pitchTarget - pitch) / (s.GlideTime * sr);
            }
            else
            {
                pitch = n;
                glideStep = 0;
            }
            if (unisonCount > 1)
                for (int i = 0; i < 3; i++) osc[i].Reset(rng.NextUnipolar());
            if (!ampEnv.IsActive)
            {
                ladder.Reset();
                svf1.Reset();
                svf2.Reset();
            }
            ApplyEnvelopes(true);
            ampEnv.Gate(true);
            fltEnv.Gate(true);
            modEnv.Gate(true);
            if (s.LfoRetrigs[0]) lfo1.Retrigger(0, 0, s.LfoFades[0], sr);
            if (s.LfoRetrigs[1]) lfo2.Retrigger(0, 0, s.LfoFades[1], sr);
        }

        public void Legato(float n, bool glide)
        {
            var s = engine.S;
            note = n;
            pitchTarget = n;
            if (glide && s.GlideTime > 0.0005f)
                glideStep = Math.Abs(pitchTarget - pitch) / (s.GlideTime * sr);
            else
            {
                pitch = n;
                glideStep = 0;
            }
        }

        public void NoteOff()
        {
            if (pending)
            {
                // Released before the steal finished: the pending note starts when the fade ends and releases at once.
                pendRelease = true;
                return;
            }
            ampEnv.Gate(false);
            fltEnv.Gate(false);
            modEnv.Gate(false);
        }

        public void Kill()
        {
            pending = false;
            pendRelease = false;
            if (ampEnv.IsActive) steal.Start(sr, 0.004f);
            ampEnv.Gate(false);
            fltEnv.Gate(false);
            modEnv.Gate(false);
        }

        void ApplyEnvelopes(bool force)
        {
            var s = engine.S;
            if (!force && envVersion == s.Version) return;
            envVersion = s.Version;
            ampEnv.Set(s.AmpEnv[0], s.AmpEnv[1], s.AmpEnv[2], s.AmpEnv[3]);
            fltEnv.Set(s.FltEnv[0], s.FltEnv[1], s.FltEnv[2], s.FltEnv[3]);
            modEnv.Set(s.ModEnv[0], s.ModEnv[1], s.ModEnv[2], s.ModEnv[3]);
        }

        float Source(ModSource src, float l1, float l2)
        {
            switch (src)
            {
                case ModSource.Lfo1: return l1;
                case ModSource.Lfo2: return l2;
                case ModSource.ModEnv: return modEnv.Level;
                case ModSource.FilterEnv: return fltEnv.Level;
                case ModSource.AmpEnv: return ampEnv.Level;
                case ModSource.Velocity: return velocity;
                case ModSource.Key: return (note - 60f) / 60f;
                case ModSource.ModWheel: return engine.ModWheelValue;
                case ModSource.Aftertouch: return engine.AftertouchValue;
                case ModSource.Random: return randomValue;
                case ModSource.Unison: return unisonPos;
                default: return 0;
            }
        }

        /// <summary>Control-rate update for the next <paramref name="n"/> samples: modulation, pitch, filter coefficients.</summary>
        void Control(int n)
        {
            var s = engine.S;
            ApplyEnvelopes(false);

            // LFOs first (their rates can be modulated by last block's matrix).
            float l1, l2;
            if (s.LfoRetrigs[0])
            {
                lfo1.Shape = s.LfoShapes[0];
                lfo1.Rate = s.LfoRates[0] * MathF.Pow(2, dst[(int)ModDest.Lfo1Rate] * 4);
                l1 = lfo1.Advance(n, sr);
            }
            else l1 = engine.GlobalLfo1;
            if (s.LfoRetrigs[1])
            {
                lfo2.Shape = s.LfoShapes[1];
                lfo2.Rate = s.LfoRates[1] * MathF.Pow(2, dst[(int)ModDest.Lfo2Rate] * 4);
                l2 = lfo2.Advance(n, sr);
            }
            else l2 = engine.GlobalLfo2;
            float fenv = fltEnv.Advance(n);
            modEnv.Advance(n);

            Array.Clear(dst, 0, dst.Length);
            for (int m = 0; m < 8; m++)
            {
                var d = s.MatrixDst[m];
                if (d == ModDest.Off || s.MatrixSrc[m] == ModSource.Off) continue;
                dst[(int)d] += s.MatrixAmt[m] * Source(s.MatrixSrc[m], l1, l2);
            }

            // Drift: a new random target every ~0.4 s, approached smoothly.
            if (--driftCounter <= 0)
            {
                driftCounter = (int)(0.4f * sr / PolyEngine.ControlRate);
                driftPitchTarget = rng.NextBipolar() * 8f * s.Drift;     // cents
                driftCutTarget = rng.NextBipolar() * 0.15f * s.Drift;    // octaves
            }
            float dk = 0.02f * n / PolyEngine.ControlRate;
            driftPitch += (driftPitchTarget - driftPitch) * dk;
            driftCut += (driftCutTarget - driftCut) * dk;

            // Pitch with glide.
            if (glideStep > 0)
            {
                if (pitch < pitchTarget) pitch = Math.Min(pitchTarget, pitch + glideStep * n);
                else pitch = Math.Max(pitchTarget, pitch - glideStep * n);
                if (pitch == pitchTarget) glideStep = 0;
            }
            else pitch = pitchTarget;
            float uniCents = unisonPos * s.UniDetune * 50f;
            float basePitch = pitch + engine.PitchBendSemis + dst[(int)ModDest.Pitch] * 24f + (driftPitch + uniCents) / 100f;
            inc0 = Inc(basePitch + s.Transpose[0] + dst[(int)ModDest.Osc1Pitch] * 24f);
            inc1 = Inc(basePitch + s.Transpose[1] + dst[(int)ModDest.Osc2Pitch] * 24f);
            inc2 = Inc(basePitch + s.Transpose[2] + dst[(int)ModDest.Osc3Pitch] * 24f);
            osc[0].SetShape(s.Shape[0] + dst[(int)ModDest.Osc1Shape] * 3f, s.Pw[0] + dst[(int)ModDest.Osc1Pw] * 0.48f);
            osc[1].SetShape(s.Shape[1] + dst[(int)ModDest.Osc2Shape] * 3f, s.Pw[1] + dst[(int)ModDest.Osc2Pw] * 0.48f);
            osc[2].SetShape(s.Shape[2] + dst[(int)ModDest.Osc3Shape] * 3f, 0.5f);
            lvl0 = DspMath.Clamp(s.Level[0] + dst[(int)ModDest.Osc1Level], 0, 1);
            lvl1 = DspMath.Clamp(s.Level[1] + dst[(int)ModDest.Osc2Level], 0, 1);
            lvl2 = DspMath.Clamp(s.Level[2] + dst[(int)ModDest.Osc3Level], 0, 1);
            ring = DspMath.Clamp(s.Ring + dst[(int)ModDest.RingLevel], 0, 1);
            noise = DspMath.Clamp(s.Noise + dst[(int)ModDest.NoiseLevel], 0, 1);
            noiseColor = s.NoiseColor;
            fm = Math.Max(0, s.Fm + dst[(int)ModDest.Fm] * 4f);
            sync = s.Sync;

            // Filters: cutoff in octaves from the knob, key, envelope, velocity, matrix, drift.
            float key = (note - 60f) / 12f;
            float velOct = s.F1Vel * (velocity - 1f);
            float oct1 = key * s.F1Key + fenv * s.F1Env + velOct + dst[(int)ModDest.F1Cutoff] * 6f + driftCut;
            float cut1 = DspMath.Clamp(s.F1Cutoff * MathF.Pow(2, oct1), 16f, sr * 0.48f);
            float reso1 = DspMath.Clamp(s.F1Reso + dst[(int)ModDest.F1Reso], 0, 1);
            float g1 = Svf.Prewarp(cut1, sr);
            if (s.F1Type <= 1) ladder.Set(g1, reso1);
            else
            {
                svf1.Mode = s.F1Type == 2 ? SvfMode.LowPass : s.F1Type == 3 ? SvfMode.BandPass : s.F1Type == 4 ? SvfMode.HighPass : SvfMode.Notch;
                svf1.SetFast(g1, 2f - 1.96f * reso1);
            }
            if (s.F2Type > 0)
            {
                float oct2 = key * s.F1Key + fenv * s.F2Env + dst[(int)ModDest.F2Cutoff] * 6f + driftCut;
                float cut2 = DspMath.Clamp(s.F2Cutoff * MathF.Pow(2, oct2), 16f, sr * 0.48f);
                float reso2 = DspMath.Clamp(s.F2Reso + dst[(int)ModDest.F2Reso], 0, 1);
                svf2.Mode = s.F2Type == 1 ? SvfMode.LowPass : s.F2Type == 2 ? SvfMode.BandPass : s.F2Type == 3 ? SvfMode.HighPass : SvfMode.Notch;
                svf2.SetFast(Svf.Prewarp(cut2, sr), 2f - 1.96f * reso2);
            }

            // Amp: velocity, matrix; pan: unison spread, matrix.
            float velGain = 1f - s.VelAmp * (1f - velocity);
            ampMod = velGain * DspMath.Clamp(1f + dst[(int)ModDest.Amp], 0, 2);
            float pan = DspMath.Clamp(unisonPos * s.UniSpread + dst[(int)ModDest.Pan], -1, 1);
            DspMath.PanGains(pan, out panL, out panR);
        }

        double Inc(float semis)
        {
            double hz = DspMath.NoteToHz(semis);
            return Math.Min(hz / sr, 0.49);
        }

        public void Render(float[] left, float[] right, int offset, int count)
        {
            var s = engine.S;
            // The engine calls once per segment of its 16-sample control grid.
            Control(count);
            int f1 = s.F1Type, f2 = s.F2Type;
            bool lp12 = f1 == 1;
            bool parallel = s.Parallel && f2 > 0;
            float drive = s.F1Drive;
            float l0 = lvl0, l1 = lvl1, l2 = lvl2, rg = ring, nz = noise, nc = noiseColor, fmAmt = fm;
            for (int i = 0; i < count; i++)
            {
                float a = osc[0].Next(inc0, out bool wrapped, out double ago);
                double i1 = inc1;
                if (fmAmt > 0) i1 *= 1 + fmAmt * osc[0].NaiveValue;
                float b = sync ? osc[1].NextSynced(i1, wrapped, ago) : osc[1].Next(i1, out _, out _);
                float c = l2 > 0 ? osc[2].Next(inc2, out _, out _) : 0f;
                float x = a * l0 + b * l1 + c * l2;
                if (rg > 0) x += a * b * rg;
                if (nz > 0)
                {
                    float w = rng.NextBipolar();
                    x += nz * (w + (pink.Next(w) * 3f - w) * nc);
                }

                float y1;
                if (f1 <= 1) y1 = ladder.Process(x, drive, lp12);
                else
                {
                    float xd = drive > 1.01f ? DspMath.FastTanh(x * drive) / MathF.Sqrt(drive) : x;
                    y1 = svf1.Process(xd);
                }
                float y;
                if (f2 == 0) y = y1;
                else if (parallel) y = 0.5f * (y1 + svf2.Process(x));
                else y = svf2.Process(y1);

                float env = ampEnv.Next();
                float g = env * ampMod;
                if (steal.Fading)
                {
                    if (!steal.Next(out float sg))
                    {
                        // The old note is gone: start the pending one.
                        ampEnv.Reset();
                        if (pending)
                        {
                            pending = false;
                            Start(pendNote, pendVel, pendUni, pendUniCount, pendGlideFrom);
                            if (pendRelease)
                            {
                                pendRelease = false;
                                NoteOff();
                            }
                            Control(Math.Max(1, count - i));   // the rest of this block plays the new note's settings
                        }
                        sg = 0;
                    }
                    g *= sg;
                }
                float o = y * g;
                left[offset + i] += o * panL;
                right[offset + i] += o * panR;
            }
            ladder.Flush();
            svf1.Flush();
            svf2.Flush();
            if (!ampEnv.IsActive && !steal.Fading && !pending)
            {
                fltEnv.Reset();
                modEnv.Reset();
            }
        }
    }
}
