#nullable disable
// Neo Wave (docs/specs/machines.md §5): wavetable synth. Two wavetable oscillators (frame position,
// warp), a sub oscillator and noise into one filter; three envelopes, two LFOs, an 8-slot matrix.
// Voices run at twice the sample rate and an IIR halfband brings the mix down, so every mipmap
// keeps the full audible band without aliasing. Pure.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Wave
{
    internal enum WaveSource { Off, Lfo1, Lfo2, ModEnv, FilterEnv, AmpEnv, Velocity, Key, ModWheel, Aftertouch, Random, Unison }

    internal enum WaveDest { Off, Pitch, APitch, BPitch, APos, BPos, AWarp, BWarp, ALevel, BLevel, SubLevel, NoiseLevel, Cutoff, Reso, Amp, Pan, Lfo1Rate, Lfo2Rate, Count }

    internal enum Warp { None, BendUp, BendDown, Squeeze, Mirror, Asym, WindowSync, PhaseMod }

    internal sealed class WaveSettings
    {
        public VoiceMode Mode;
        public int Polyphony, Unison;
        public float UniDetune, UniSpread, BendRange, VelAmp, Volume, GlideTime;
        public bool GlideAlways, GlideLegato;
        public readonly Wavetable[] Table = new Wavetable[2];
        public readonly float[] Pos = new float[2], WarpAmt = new float[2], Transpose = new float[2], Level = new float[2];
        public readonly Warp[] WarpMode = new Warp[2];
        public bool SubSquare;
        public float SubOct, SubLevel, Noise;
        public int FType;
        public float Cutoff, Reso, Drive, FEnv, FKey, FVel;
        public readonly float[] AmpEnv = new float[4], FltEnv = new float[4], ModEnv = new float[4];
        public readonly LfoShape[] LfoShapes = new LfoShape[2];
        public readonly float[] LfoRates = new float[2], LfoFades = new float[2];
        public readonly bool[] LfoRetrigs = new bool[2];
        public readonly WaveSource[] Src = new WaveSource[8];
        public readonly WaveDest[] Dst = new WaveDest[8];
        public readonly float[] Amt = new float[8];
        public int Version;
    }

    internal sealed class WaveEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int VoiceCount = 32;
        public const int ControlRate = 16;   // at the base rate
        static readonly double[] SyncBeats = { 0, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 2, 4, 8, 16 };
        static readonly IirHalfbandDesign Decimator = new IirHalfbandDesign(100, (28.0 - 20.0) / 96);

        readonly ParamValues values;
        readonly WaveVoice[] voices = new WaveVoice[VoiceCount];
        readonly VoiceAllocator alloc;
        internal readonly WaveSettings S = new WaveSettings();
        readonly IirHalfband decL = new IirHalfband(Decimator), decR = new IirHalfband(Decimator);
        readonly float[] hiL = new float[2 * MaxBlock], hiR = new float[2 * MaxBlock];
        readonly Lfo globalLfo1 = new Lfo(31), globalLfo2 = new Lfo(37);
        float sampleRate = 48000;
        double bpm = 120;
        Smoother volume;
        int controlCounter;
        Wavetable userTable;

        public WaveEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < VoiceCount; i++) voices[i] = new WaveVoice(this, (uint)(0x2545F491u * (i + 3)));
            alloc = new VoiceAllocator(voices);
            SetSampleRate(48000);
        }

        public readonly LevelMeter OutMeter = new LevelMeter();
        public float SampleRate => sampleRate;
        internal float ModWheelValue, PitchBendSemis, AftertouchValue, GlobalLfo1, GlobalLfo2;
        public double Tempo { get => bpm; set => bpm = value > 0 ? value : 120; }

        volatile Wavetable requestedUser;   // the latest table asked for (any thread); null: none
        int userChanged;                    // 1: the audio thread hasn't picked up requestedUser yet
        bool tablesPending;   // a chosen built-in table is still being built: look again next block

        /// <summary>The table "User" plays (loaded by the machine from a file or its saved state); null when none.</summary>
        public Wavetable UserTable => requestedUser;

        /// <summary>Any thread: plays <paramref name="table"/> as "User" from the next block (null: no user table).</summary>
        public void SetUserTable(Wavetable table)
        {
            requestedUser = table;
            Volatile.Write(ref userChanged, 1);
        }

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

        internal WaveVoice Voice(int i) => voices[i];

        /// <summary>For editors: the modulated position and warp of the loudest sounding voice.</summary>
        internal bool Live(int osc, out float pos, out float warp)
        {
            WaveVoice best = null;
            float loudest = 0.001f;
            foreach (var v in voices)
                if (v.IsActive && v.Loudness > loudest)
                {
                    best = v;
                    loudest = v.Loudness;
                }
            pos = warp = 0;
            if (best == null) return false;
            (pos, warp) = best.Live(osc);
            return true;
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            volume.Setup(0.02, sr);
            OutMeter.SetSampleRate(sr);
            foreach (var v in voices) v.SetSampleRate(2 * sr);
            Update(true);
            volume.Reset(S.Volume);
        }

        /// <summary>The table an oscillator plays now (null while a built-in one is still being built).</summary>
        public Wavetable TableFor(int osc) => S.Table[osc];

        Wavetable Pick(int choice) => choice >= Tables.Names.Length ? (userTable ?? Tables.Get(0)) : Tables.Get(choice);

        void Update(bool force)
        {
            bool userNew = Interlocked.Exchange(ref userChanged, 0) == 1;
            if (!force && !values.AnyDirty && !userNew && !tablesPending) return;
            // Read after clearing the flag: a table asked for meanwhile sets the flag again and is picked up next block.
            if (userNew) userTable = requestedUser;
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
            double vol = values.Value(P.Volume);
            s.Volume = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            s.VelAmp = values.F(P.VelAmp);

            // Keep playing the previous table until a newly chosen one is built.
            var ta = Pick(values.Choice(P.ATable));
            var tb = Pick(values.Choice(P.BTable));
            tablesPending = ta == null || tb == null;
            s.Table[0] = ta ?? s.Table[0];
            s.Table[1] = tb ?? s.Table[1];
            s.Pos[0] = values.F(P.APos); s.Pos[1] = values.F(P.BPos);
            s.WarpMode[0] = (Warp)values.Choice(P.AWarp); s.WarpMode[1] = (Warp)values.Choice(P.BWarp);
            s.WarpAmt[0] = values.F(P.AWarpAmt); s.WarpAmt[1] = values.F(P.BWarpAmt);
            s.Transpose[0] = values.F(P.AOct) * 12 + values.F(P.ASemi) + values.F(P.AFine) / 100f;
            s.Transpose[1] = values.F(P.BOct) * 12 + values.F(P.BSemi) + values.F(P.BFine) / 100f;
            s.Level[0] = values.F(P.ALevel); s.Level[1] = values.F(P.BLevel);
            s.SubSquare = values.Choice(P.SubShape) == 1;
            s.SubOct = values.Choice(P.SubOct) == 0 ? -12 : -24;
            s.SubLevel = values.F(P.SubLevel);
            s.Noise = values.F(P.NoiseLevel);

            s.FType = values.Choice(P.FType);
            s.Cutoff = values.F(P.FCutoff);
            s.Reso = values.F(P.FReso);
            s.Drive = (float)DspMath.DbToGain(values.Value(P.FDrive));
            s.FEnv = values.F(P.FEnv) * 6;
            s.FKey = values.F(P.FKey);
            s.FVel = values.F(P.FVel) * 3;

            Env(s.AmpEnv, P.AmpA, P.AmpD, P.AmpS, P.AmpR);
            Env(s.FltEnv, P.FltA, P.FltD, P.FltS, P.FltR);
            Env(s.ModEnv, P.ModA, P.ModD, P.ModS, P.ModR);
            DecodeLfo(0, P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade);
            DecodeLfo(1, P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade);
            for (int m = 0; m < 8; m++)
            {
                int b = P.M1Src + 3 * m;
                s.Src[m] = (WaveSource)values.Choice(b);
                s.Dst[m] = (WaveDest)values.Choice(b + 1);
                s.Amt[m] = values.F(b + 2);
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
            e[0] = values.F(a); e[1] = values.F(d); e[2] = values.F(s); e[3] = values.F(r);
        }

        void DecodeLfo(int i, int shape, int rate, int sync, int retrig, int fade)
        {
            S.LfoShapes[i] = (LfoShape)values.Choice(shape);
            int sy = values.Choice(sync);
            S.LfoRates[i] = sy > 0 ? (float)(bpm / 60.0 / SyncBeats[sy]) : values.F(rate);
            S.LfoRetrigs[i] = values.On(retrig);
            S.LfoFades[i] = values.F(fade);
        }

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
            if (values[P.L1Sync] > 0 || values[P.L2Sync] > 0)
            {
                DecodeLfo(0, P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade);
                DecodeLfo(1, P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade);
            }
            Update(false);
            int m2 = 2 * n;
            Array.Clear(hiL, 0, m2);
            Array.Clear(hiR, 0, m2);
            bool any = false;
            if (S.Table[0] != null && S.Table[1] != null)
            {
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
                            v.Render(hiL, hiR, 2 * pos, 2 * m);
                            any = true;
                        }
                    controlCounter -= m;
                    pos += m;
                }
            }
            for (int i = 0; i < n; i++)
            {
                float g = volume.Next() * 0.5f;
                left[i] = decL.Down(hiL[2 * i], hiL[2 * i + 1]) * g;
                right[i] = decR.Down(hiR[2 * i], hiR[2 * i + 1]) * g;
            }
            decL.Flush();
            decR.Flush();
            OutMeter.Feed(left, right, n);
            return any || OutMeter.Peak > 1e-6f;
        }
    }

    /// <summary>One voice, running at twice the sample rate.</summary>
    internal sealed class WaveVoice : IVoice
    {
        readonly WaveEngine engine;
        readonly Adsr ampEnv = new Adsr(), fltEnv = new Adsr(), modEnv = new Adsr();
        readonly Lfo lfo1, lfo2;
        Ladder ladder;
        Svf svf;
        Rng rng;
        StealFade steal;
        float sr = 96000;          // the voice's (oversampled) rate
        int envVersion = -1;

        float note, velocity, randomValue, unisonPos;
        float pitch, pitchTarget, glideStep;
        bool pending, pendRelease;
        float pendNote, pendVel, pendGlideFrom;
        int pendUni, pendUniCount;

        double phA, phB, phSub;
        double incA, incB, incSub;
        float posA, posB, warpA, warpB, lvlA, lvlB, lvlSub, noise, panL = 1, panR = 1, ampMod = 1;
        int levelA, levelB, levelSub;
        readonly float[] dst = new float[(int)WaveDest.Count];

        public WaveVoice(WaveEngine engine, uint seed)
        {
            this.engine = engine;
            rng = new Rng(seed);
            lfo1 = new Lfo(seed ^ 0x1F1F1F1Fu);
            lfo2 = new Lfo(seed ^ 0xF1F1F1F1u);
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
            if ((ampEnv.IsActive && ampEnv.Level > 0.001f) || pending)
            {
                bool start = !pending;
                pending = true;
                pendRelease = false;
                pendNote = n; pendVel = vel; pendUni = unisonIndex; pendUniCount = unisonCount; pendGlideFrom = glideFrom;
                if (start) steal.Start(sr);
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
            // Unison voices start at spread phases; single voices at 0 (a consistent attack).
            phA = unisonCount > 1 ? rng.NextUnipolar() : 0;
            phB = unisonCount > 1 ? rng.NextUnipolar() : 0;
            phSub = 0;
            if (!ampEnv.IsActive)
            {
                ladder.Reset();
                svf.Reset();
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
            if (glide && s.GlideTime > 0.0005f) glideStep = Math.Abs(pitchTarget - pitch) / (s.GlideTime * sr);
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

        float Source(WaveSource src, float l1, float l2)
        {
            switch (src)
            {
                case WaveSource.Lfo1: return l1;
                case WaveSource.Lfo2: return l2;
                case WaveSource.ModEnv: return modEnv.Level;
                case WaveSource.FilterEnv: return fltEnv.Level;
                case WaveSource.AmpEnv: return ampEnv.Level;
                case WaveSource.Velocity: return velocity;
                case WaveSource.Key: return (note - 60f) / 60f;
                case WaveSource.ModWheel: return engine.ModWheelValue;
                case WaveSource.Aftertouch: return engine.AftertouchValue;
                case WaveSource.Random: return randomValue;
                case WaveSource.Unison: return unisonPos;
                default: return 0;
            }
        }

        /// <summary>How fast a warp can read through the cycle, relative to the plain phase (for the mip level).</summary>
        static float WarpSpeed(Warp w, float a)
        {
            switch (w)
            {
                case Warp.BendUp:
                case Warp.BendDown: return 1 + 3 * a;
                case Warp.Squeeze: return 1 / Math.Max(0.04f, 1 - 0.96f * a);
                case Warp.Mirror: return 1 + a;
                case Warp.Asym: return 1 + 0.6f * a;
                case Warp.WindowSync: return 1 + 7 * a;
                case Warp.PhaseMod: return 1 + 6 * a;
                default: return 1;
            }
        }

        /// <summary>What the editor draws: the modulated position and warp amount of an oscillator.</summary>
        internal (float Pos, float Warp) Live(int osc) => osc == 0 ? (posA, warpA) : (posB, warpB);

        internal static double WarpPhase(Warp w, float a, double p)
        {
            switch (w)
            {
                case Warp.BendUp: return Math.Pow(p, 1 + 3 * a);
                case Warp.BendDown: return 1 - Math.Pow(1 - p, 1 + 3 * a);
                case Warp.Squeeze:
                {
                    double width = 1 - 0.96 * a;
                    return p < width ? p / width : 0.999999;
                }
                case Warp.Mirror:
                {
                    double m = p < 0.5 ? 2 * p : 2 - 2 * p;
                    return (1 - a) * p + a * m * 0.5;
                }
                case Warp.Asym: return p + a * 0.15 * Math.Sin(2 * Math.PI * p);
                case Warp.WindowSync:
                {
                    double q = p * (1 + 7 * a);
                    return q - Math.Floor(q);
                }
                default: return p;
            }
        }

        void Control(int n)
        {
            var s = engine.S;
            ApplyEnvelopes(false);
            float l1, l2;
            if (s.LfoRetrigs[0])
            {
                lfo1.Shape = s.LfoShapes[0];
                lfo1.Rate = s.LfoRates[0] * MathF.Pow(2, dst[(int)WaveDest.Lfo1Rate] * 4);
                l1 = lfo1.Advance(n, sr);
            }
            else l1 = engine.GlobalLfo1;
            if (s.LfoRetrigs[1])
            {
                lfo2.Shape = s.LfoShapes[1];
                lfo2.Rate = s.LfoRates[1] * MathF.Pow(2, dst[(int)WaveDest.Lfo2Rate] * 4);
                l2 = lfo2.Advance(n, sr);
            }
            else l2 = engine.GlobalLfo2;
            float fenv = fltEnv.Advance(n);
            modEnv.Advance(n);

            Array.Clear(dst, 0, dst.Length);
            for (int m = 0; m < 8; m++)
            {
                if (s.Dst[m] == WaveDest.Off || s.Src[m] == WaveSource.Off) continue;
                dst[(int)s.Dst[m]] += s.Amt[m] * Source(s.Src[m], l1, l2);
            }

            if (glideStep > 0)
            {
                if (pitch < pitchTarget) pitch = Math.Min(pitchTarget, pitch + glideStep * n);
                else pitch = Math.Max(pitchTarget, pitch - glideStep * n);
                if (pitch == pitchTarget) glideStep = 0;
            }
            else pitch = pitchTarget;
            float basePitch = pitch + engine.PitchBendSemis + dst[(int)WaveDest.Pitch] * 24f + unisonPos * s.UniDetune * 0.5f;
            incA = Inc(basePitch + s.Transpose[0] + dst[(int)WaveDest.APitch] * 24f);
            incB = Inc(basePitch + s.Transpose[1] + dst[(int)WaveDest.BPitch] * 24f);
            incSub = Inc(basePitch + s.SubOct);
            posA = DspMath.Clamp(s.Pos[0] + dst[(int)WaveDest.APos], 0, 1);
            posB = DspMath.Clamp(s.Pos[1] + dst[(int)WaveDest.BPos], 0, 1);
            warpA = DspMath.Clamp(s.WarpAmt[0] + dst[(int)WaveDest.AWarp], 0, 1);
            warpB = DspMath.Clamp(s.WarpAmt[1] + dst[(int)WaveDest.BWarp], 0, 1);
            lvlA = DspMath.Clamp(s.Level[0] + dst[(int)WaveDest.ALevel], 0, 1);
            lvlB = DspMath.Clamp(s.Level[1] + dst[(int)WaveDest.BLevel], 0, 1);
            lvlSub = DspMath.Clamp(s.SubLevel + dst[(int)WaveDest.SubLevel], 0, 1);
            noise = DspMath.Clamp(s.Noise + dst[(int)WaveDest.NoiseLevel], 0, 1);

            // Mip levels: harmonics up to 44 kHz (below the oversampled Nyquist, so nothing
            // aliases; the decimator removes what's above 20 kHz), counting the warp's speed-up.
            double limit = Math.Min(44000.0, sr * 0.5 - 100) / sr;
            levelA = Wavetable.LevelFor(incA * WarpSpeed(s.WarpMode[0], warpA), limit);
            // "PM from B" only means something for osc A; on osc B it reads the cycle plainly.
            levelB = Wavetable.LevelFor(incB * (s.WarpMode[1] == Warp.PhaseMod ? 1f : WarpSpeed(s.WarpMode[1], warpB)), limit);
            levelSub = Wavetable.LevelFor(incSub, limit);

            float key = (note - 60f) / 12f;
            float oct = key * s.FKey + fenv * s.FEnv + s.FVel * (velocity - 1f) + dst[(int)WaveDest.Cutoff] * 6f;
            float cut = DspMath.Clamp(s.Cutoff * MathF.Pow(2, oct), 16f, 22000f);
            float reso = DspMath.Clamp(s.Reso + dst[(int)WaveDest.Reso], 0, 1);
            float g = Svf.Prewarp(cut, sr);
            if (s.FType <= 1) ladder.Set(g, reso);
            else if (s.FType < 6)
            {
                svf.Mode = s.FType == 2 ? SvfMode.LowPass : s.FType == 3 ? SvfMode.BandPass : s.FType == 4 ? SvfMode.HighPass : SvfMode.Notch;
                svf.SetFast(g, 2f - 1.96f * reso);
            }

            float velGain = 1f - s.VelAmp * (1f - velocity);
            ampMod = velGain * DspMath.Clamp(1f + dst[(int)WaveDest.Amp], 0, 2);
            float pan = DspMath.Clamp(unisonPos * s.UniSpread + dst[(int)WaveDest.Pan], -1, 1);
            DspMath.PanGains(pan, out panL, out panR);
        }

        double Inc(float semis) => Math.Min(DspMath.NoteToHz(semis) / sr, 0.49);

        /// <summary>Renders <paramref name="count"/> oversampled samples from <paramref name="offset"/>.</summary>
        public void Render(float[] left, float[] right, int offset, int count)
        {
            var s = engine.S;
            Control(count);
            var tA = s.Table[0];
            var tB = s.Table[1];
            var tSub = Tables.Get(0);
            var wA = s.WarpMode[0];
            var wB = s.WarpMode[1];
            float fA = posA * (tA.FrameCount - 1), fB = posB * (tB.FrameCount - 1);
            float subFrame = s.SubSquare && tSub != null ? tSub.FrameCount - 1 : 0;
            int ft = s.FType;
            bool lp12 = ft == 1;
            float drive = s.Drive;
            for (int i = 0; i < count; i++)
            {
                float b = 0;
                if (lvlB > 0 || wA == Warp.PhaseMod)
                {
                    b = tB.Read(fB, WarpPhase(wB, warpB, phB), levelB);
                    if (wB == Warp.WindowSync) b *= (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * phB));
                }
                double pa = wA == Warp.PhaseMod ? phA + warpA * 0.5 * b : WarpPhase(wA, warpA, phA);
                float a = lvlA > 0 ? tA.Read(fA, pa - Math.Floor(pa), levelA) : 0f;
                if (wA == Warp.WindowSync) a *= (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * phA));
                float x = a * lvlA + b * lvlB;
                if (lvlSub > 0 && tSub != null) x += tSub.Read(subFrame, phSub, levelSub) * lvlSub;
                if (noise > 0) x += rng.NextBipolar() * noise;
                phA += incA; if (phA >= 1) phA -= 1;
                phB += incB; if (phB >= 1) phB -= 1;
                phSub += incSub; if (phSub >= 1) phSub -= 1;

                float y;
                if (ft <= 1) y = ladder.Process(x, drive, lp12);
                else if (ft < 6) y = svf.Process(drive > 1.01f ? DspMath.FastTanh(x * drive) / MathF.Sqrt(drive) : x);
                else y = x;

                float gain = ampEnv.Next() * ampMod;
                if (steal.Fading)
                {
                    if (!steal.Next(out float sg))
                    {
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
                    gain *= sg;
                }
                float o = y * gain;
                left[offset + i] += o * panL;
                right[offset + i] += o * panR;
            }
            ladder.Flush();
            svf.Flush();
            if (!ampEnv.IsActive && !steal.Fading && !pending)
            {
                fltEnv.Reset();
                modEnv.Reset();
            }
        }
    }
}
