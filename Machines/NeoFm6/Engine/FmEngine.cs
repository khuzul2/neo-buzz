#nullable disable
// Neo FM6 (docs/specs/machines.md §5): the engine. Six operators per voice wired by one of the
// 32 DX7 algorithms, with the DX7's envelopes, scaling and tuning (Dx7.cs); one LFO for all voices
// (restarted by notes when key sync is on) faded in per voice; the pitch envelope; glide and
// unison. Voices run at twice the sample rate ("Clean", decimated by a 100 dB half-band) or at
// the sample rate ("Raw", aliasing like the original).

using System;
using System.Numerics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Fm6
{
    internal sealed class OpSettings
    {
        public bool On = true, Fixed;
        public int Level, Coarse = 1, Fine, Detune = 7, Wave, Break = 39, LDepth, RDepth, LCurve, RCurve, RateScale, AmpSens, VelSens;
        public readonly int[] Rates = { 99, 99, 99, 99 }, Levels = { 99, 99, 99, 0 };
    }

    internal sealed class FmSettings
    {
        public Algorithm Algorithm = Algorithms.Get(1);
        public int Feedback, Transpose;
        public bool OscSync = true, LfoSync = true;
        public readonly int[] PegRates = { 99, 99, 99, 99 }, PegLevels = { 50, 50, 50, 50 };
        public int LfoWave, LfoSpeed = 35, LfoDelay, LfoPmd, LfoAmd, PitchSens = 3;
        public double LfoWait, LfoFade;
        public int WheelPitch, WheelAmp, AtPitch, AtAmp;
        public VoiceMode Mode;
        public int Polyphony = 8, Unison = 1;
        public float UniDetune, UniSpread, GlideTime, BendRange = 2, Volume = 1, Tone = 20000;
        public bool GlideAlways, GlideLegato;
        public int Oversample = 2;
        public readonly OpSettings[] Ops = { new(), new(), new(), new(), new(), new() };
        public int Version;
    }

    internal sealed class FmEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int VoiceCount = 32;
        public const int ControlRate = 16;     // output samples between envelope / LFO updates
        public const int TableBits = 12, TableSize = 1 << TableBits;
        public const float OutputScale = 0.125f;

        // Parameter offsets within an operator's block (Params.def order).
        public const int OpStride = P.O2On - P.O1On;
        const int OOn = 0, OLevel = P.O1Level - P.O1On, OMode = P.O1Mode - P.O1On, OCoarse = P.O1Coarse - P.O1On,
            OFine = P.O1Fine - P.O1On, ODetune = P.O1Detune - P.O1On, OWave = P.O1Wave - P.O1On,
            OR1 = P.O1R1 - P.O1On, OL1 = P.O1L1 - P.O1On, OBreak = P.O1Break - P.O1On, OLDepth = P.O1LDepth - P.O1On,
            ORDepth = P.O1RDepth - P.O1On, OLCurve = P.O1LCurve - P.O1On, ORCurve = P.O1RCurve - P.O1On,
            ORateSc = P.O1RateSc - P.O1On, OAmpSens = P.O1AmpSens - P.O1On, OVelSens = P.O1VelSens - P.O1On;

        /// <summary>Parameter index of an operator's (0 … 5) parameter at <paramref name="offset"/>.</summary>
        public static int OpParam(int op, int offset) => P.O1On + op * OpStride + offset;

        static readonly IirHalfbandDesign Decimator = new IirHalfbandDesign(100, (28.0 - 20.0) / 96);

        /// <summary>Operator waveforms: sine, half sine, abs sine, pulse sine (one cycle, two guard samples).</summary>
        internal static readonly float[][] Waves = BuildWaves();

        readonly ParamValues values;
        readonly FmVoice[] voices = new FmVoice[VoiceCount];
        readonly VoiceAllocator alloc;
        internal readonly FmSettings S = new FmSettings();
        readonly IirHalfband decL = new IirHalfband(Decimator), decR = new IirHalfband(Decimator);
        readonly float[] hiL = new float[2 * MaxBlock], hiR = new float[2 * MaxBlock];
        OnePole toneL1, toneL2, toneR1, toneR2;
        DcBlocker dcL, dcR;
        float sampleRate = 48000;
        Smoother volume;
        int controlCounter;
        double lfoPhase;
        Rng lfoRng = new Rng(0x5EED);
        float sampleHold = 0.5f;

        static FmEngine() => FmText.Install();

        public FmEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < VoiceCount; i++) voices[i] = new FmVoice(this, (uint)(0x9E3779B1u * (i + 7)));
            alloc = new VoiceAllocator(voices);
            SetSampleRate(48000);
        }

        public readonly LevelMeter OutMeter = new LevelMeter();
        public float SampleRate => sampleRate;
        internal float ModWheelValue, PitchBendSemis, AftertouchValue;
        /// <summary>The LFO now, 0 … 1.</summary>
        internal double LfoValue { get; private set; } = 0.5;

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

        /// <summary>For editors: an operator's amplitude in the loudest sounding voice (0 when silent).</summary>
        internal float OpLevel(int op)
        {
            FmVoice best = null;
            float loudest = 1e-4f;
            foreach (var v in voices)
                if (v.IsActive && v.Loudness > loudest)
                {
                    best = v;
                    loudest = v.Loudness;
                }
            return best?.OpGain(op) ?? 0f;
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            volume.Setup(0.02, sr);
            OutMeter.SetSampleRate(sr);
            dcL.Set(sr);
            dcR.Set(sr);
            Update(true);
            foreach (var v in voices) v.SetSampleRate(S.Oversample * sr);
            volume.Reset(S.Volume);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var s = S;
            s.Algorithm = Algorithms.Get((int)values.Value(P.Algorithm));
            s.Feedback = (int)values.Value(P.Feedback);
            s.OscSync = values.On(P.OscSync);
            s.Transpose = (int)values.Value(P.Transpose);
            for (int i = 0; i < 4; i++)
            {
                s.PegRates[i] = values[P.PegR1 + i];
                s.PegLevels[i] = values[P.PegL1 + i];
            }
            s.LfoWave = values.Choice(P.LfoWave);
            s.LfoSpeed = values[P.LfoSpeed];
            s.LfoDelay = values[P.LfoDelay];
            (s.LfoWait, s.LfoFade) = Dx7.LfoDelay(s.LfoDelay);
            s.LfoPmd = values[P.LfoPmd];
            s.LfoAmd = values[P.LfoAmd];
            s.LfoSync = values.On(P.LfoSync);
            s.PitchSens = values[P.PitchSens];
            s.WheelPitch = values[P.WheelPitch];
            s.WheelAmp = values[P.WheelAmp];
            s.AtPitch = values[P.AtPitch];
            s.AtAmp = values[P.AtAmp];

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
            s.Tone = values.F(P.Tone);
            toneL1.Set(Math.Min(s.Tone, sampleRate * 0.45), sampleRate);
            toneL2.Set(Math.Min(s.Tone, sampleRate * 0.45), sampleRate);
            toneR1.Set(Math.Min(s.Tone, sampleRate * 0.45), sampleRate);
            toneR2.Set(Math.Min(s.Tone, sampleRate * 0.45), sampleRate);

            int os = values.Choice(P.EngineMode) == 0 ? 2 : 1;
            if (os != s.Oversample)
            {
                s.Oversample = os;
                foreach (var v in voices) v.SetSampleRate(os * sampleRate);
            }

            for (int k = 0; k < 6; k++)
            {
                var o = s.Ops[k];
                int b = OpParam(k, 0);
                o.On = values.On(b + OOn);
                o.Level = values[b + OLevel];
                o.Fixed = values.Choice(b + OMode) == 1;
                o.Coarse = values[b + OCoarse];
                o.Fine = values[b + OFine];
                o.Detune = values[b + ODetune];
                o.Wave = values.Choice(b + OWave);
                for (int i = 0; i < 4; i++)
                {
                    o.Rates[i] = values[b + OR1 + i];
                    o.Levels[i] = values[b + OL1 + i];
                }
                o.Break = values[b + OBreak];
                o.LDepth = values[b + OLDepth];
                o.RDepth = values[b + ORDepth];
                o.LCurve = values.Choice(b + OLCurve);
                o.RCurve = values.Choice(b + ORCurve);
                o.RateScale = values[b + ORateSc];
                o.AmpSens = values[b + OAmpSens];
                o.VelSens = values[b + OVelSens];
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

        public void NoteOn(int key, int note, float velocity)
        {
            Update(false);
            if (S.LfoSync)
            {
                lfoPhase = 0.5 - 1e-9;
                LfoValue = LfoShape(S.LfoWave, lfoPhase);
            }
            alloc.NoteOn(key, note, velocity);
        }

        public void NoteOff(int key) => alloc.NoteOff(key);
        public void ModWheel(float value) => ModWheelValue = value;
        public void PitchBend(float value) => PitchBendSemis = value * S.BendRange;
        public void Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) => AftertouchValue = value;
        public void AllNotesOff(bool hard) => alloc.AllNotesOff(hard);

        double LfoShape(int wave, double p)
        {
            switch (wave)
            {
                case 0: return p < 0.5 ? 2 * p : 2 - 2 * p;                  // triangle
                case 1: { double v = 1.5 - p; return v >= 1 ? v - 1 : v; }    // saw down
                case 2: { double v = p + 0.5; return v >= 1 ? v - 1 : v; }    // saw up
                case 3: return p < 0.5 ? 1 : 0;                              // square
                case 4: return 0.5 + 0.5 * Math.Sin(2 * Math.PI * p);        // sine
                default: return sampleHold;                                  // sample and hold
            }
        }

        void AdvanceLfo(int n)
        {
            lfoPhase += Dx7.LfoHz(S.LfoSpeed) * n / sampleRate;
            if (lfoPhase >= 1)
            {
                lfoPhase -= Math.Floor(lfoPhase);
                sampleHold = lfoRng.NextUnipolar();
            }
            LfoValue = LfoShape(S.LfoWave, lfoPhase);
        }

        /// <summary>Renders a block (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            int os = S.Oversample;
            Array.Clear(hiL, 0, os * n);
            Array.Clear(hiR, 0, os * n);
            bool any = false;
            int pos = 0;
            while (pos < n)
            {
                if (controlCounter <= 0)
                {
                    AdvanceLfo(ControlRate);
                    controlCounter = ControlRate;
                }
                int m = Math.Min(controlCounter, n - pos);
                foreach (var v in voices)
                    if (v.IsActive || v.Pending)
                    {
                        v.Render(hiL, hiR, os * pos, os * m);
                        any = true;
                    }
                controlCounter -= m;
                pos += m;
            }
            bool tone = S.Tone < 19500;
            for (int i = 0; i < n; i++)
            {
                float l, r;
                if (os == 2)
                {
                    l = decL.Down(hiL[2 * i], hiL[2 * i + 1]);
                    r = decR.Down(hiR[2 * i], hiR[2 * i + 1]);
                }
                else
                {
                    l = hiL[i];
                    r = hiR[i];
                }
                l = dcL.Process(l);
                r = dcR.Process(r);
                if (tone)
                {
                    l = toneL2.LowPass(toneL1.LowPass(l));
                    r = toneR2.LowPass(toneR1.LowPass(r));
                }
                float g = volume.Next() * OutputScale;
                left[i] = l * g;
                right[i] = r * g;
            }
            if (os == 2)
            {
                decL.Flush();
                decR.Flush();
            }
            toneL1.Flush(); toneL2.Flush(); toneR1.Flush(); toneR2.Flush();
            OutMeter.Feed(left, right, n);
            return any || OutMeter.Peak > 1e-6f;
        }

        static float[][] BuildWaves()
        {
            var w = new float[4][];
            for (int k = 0; k < 4; k++)
            {
                w[k] = new float[TableSize + 2];
                for (int i = 0; i < TableSize + 2; i++)
                {
                    double p = (double)(i % TableSize) / TableSize;
                    double s = Math.Sin(2 * Math.PI * p);
                    w[k][i] = (float)(k switch
                    {
                        0 => s,
                        1 => p < 0.5 ? s : 0,
                        2 => Math.Abs(s),
                        _ => (p % 0.5) < 0.25 ? Math.Abs(s) : 0,
                    });
                }
            }
            return w;
        }
    }

    /// <summary>One six-operator voice.</summary>
    internal sealed class FmVoice : IVoice
    {
        static readonly float[] CarrierNorm = { 1f, 1f, 0.7071f, 0.5774f, 0.5f, 0.4472f, 0.4082f };
        const float SilentGain = 1120f / (1 << 24);   // msfa's kLevelThresh: below it an operator outputs nothing

        readonly FmEngine engine;
        readonly Dx7Envelope[] env = { new(), new(), new(), new(), new(), new() };
        readonly Dx7PitchEnvelope peg = new Dx7PitchEnvelope();
        // Phases are 32-bit fixed point (2^32 = one cycle): they wrap by themselves and index the table directly.
        readonly uint[] phase = new uint[6], inc = new uint[6];
        readonly double[] ratio = new double[6], fixedHz = new double[6];
        const double Cycle = 4294967296.0;
        const uint FracMask = (1u << (32 - FmEngine.TableBits)) - 1;
        const float FracScale = 1f / (1 << (32 - FmEngine.TableBits));
        readonly float[] gain = new float[6], gainStep = new float[6], outs = new float[6];
        readonly float[][] tables = new float[6][];
        Rng rng;
        StealFade steal;
        float sr = 96000;
        int settingsVersion = -1;

        int midiNote, velocity;
        float note, pitch, pitchTarget, glideStep, unisonPos, panL = 1, panR = 1, loudness;
        double sinceNoteOn;
        float fb1, fb2;
        bool keyDown, finished = true;
        bool pending, pendRelease;
        float pendNote, pendVel, pendGlideFrom;
        int pendUni, pendUniCount;

        public FmVoice(FmEngine engine, uint seed)
        {
            this.engine = engine;
            rng = new Rng(seed);
            for (int k = 0; k < 6; k++) tables[k] = FmEngine.Waves[0];
        }

        public void SetSampleRate(float rate)
        {
            sr = rate;
            foreach (var e in env) e.SetRate(rate);
            settingsVersion = -1;
        }

        public bool IsActive => !finished || steal.Fading;
        public bool Pending => pending;
        public float Loudness => loudness;
        public float OpGain(int op) => finished ? 0f : gain[op];

        public void NoteOn(float n, float vel, int unisonIndex, int unisonCount, float glideFrom)
        {
            if ((!finished && loudness > 0.001f) || pending)
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
            velocity = Math.Clamp((int)Math.Round(vel * 127), 0, 127);
            unisonPos = unisonCount > 1 ? 2f * unisonIndex / (unisonCount - 1) - 1f : 0f;
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
            midiNote = Math.Clamp((int)Math.Round(n) + s.Transpose, 0, 127);
            for (int k = 0; k < 6; k++)
            {
                var o = s.Ops[k];
                env[k].Start(o.Rates, o.Levels, OutLevel(o), Dx7.ScaleRate(midiNote, o.RateScale));
                if (unisonCount > 1) phase[k] = rng.NextUInt();
                else if (s.OscSync || finished) phase[k] = 0;
                gain[k] = 0;
                gainStep[k] = 0;
            }
            peg.Start(s.PegRates, s.PegLevels);
            Tune();
            fb1 = fb2 = 0;
            sinceNoteOn = 0;
            keyDown = true;
            finished = false;
            loudness = 1;
            settingsVersion = s.Version;
            DspMath.PanGains(DspMath.Clamp(unisonPos * s.UniSpread, -1, 1), out panL, out panR);
        }

        int OutLevel(OpSettings o) =>
            Dx7.OutLevel(o.Level, midiNote, velocity, o.Break, o.LDepth, o.RDepth, o.LCurve, o.RCurve, o.VelSens);

        // Ratio-mode multipliers (with detune at the note's pitch) and fixed frequencies.
        void Tune()
        {
            var s = engine.S;
            double noteHz = DspMath.NoteToHz(midiNote);
            for (int k = 0; k < 6; k++)
            {
                var o = s.Ops[k];
                ratio[k] = Dx7.Ratio(o.Coarse, o.Fine) * Math.Pow(2, Dx7.DetuneOctaves(noteHz, o.Detune));
                fixedHz[k] = Dx7.FixedHz(o.Coarse, o.Fine) * Math.Pow(2, Dx7.FixedDetuneOctaves(o.Detune));
                tables[k] = FmEngine.Waves[Math.Clamp(o.Wave, 0, 3)];
            }
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
            keyDown = false;
            foreach (var e in env) e.KeyDown(false);
            peg.KeyDown(false);
        }

        public void Kill()
        {
            pending = false;
            pendRelease = false;
            if (!finished) steal.Start(sr, 0.004f);
            keyDown = false;
            foreach (var e in env) e.KeyDown(false);
        }

        void ApplySettings()
        {
            var s = engine.S;
            settingsVersion = s.Version;
            for (int k = 0; k < 6; k++)
            {
                var o = s.Ops[k];
                env[k].Update(o.Rates, o.Levels, OutLevel(o), Dx7.ScaleRate(midiNote, o.RateScale));
            }
            peg.Update(s.PegRates, s.PegLevels);
            Tune();
        }

        void Control(int count)
        {
            var s = engine.S;
            if (settingsVersion != s.Version) ApplySettings();
            double dt = count / (double)sr;
            sinceNoteOn += dt;

            if (glideStep > 0)
            {
                if (pitch < pitchTarget) pitch = Math.Min(pitchTarget, pitch + glideStep * count);
                else pitch = Math.Max(pitchTarget, pitch - glideStep * count);
                if (pitch == pitchTarget) glideStep = 0;
            }
            else pitch = pitchTarget;

            // LFO: fades in after the delay; the wheel and aftertouch add depth (as the DX7's controllers).
            double lfo = engine.LfoValue;
            double fade = 1;
            if (s.LfoDelay > 0)
                fade = sinceNoteOn < s.LfoWait ? 0 : s.LfoFade <= 0 ? 1 : Math.Min(1, (sinceNoteOn - s.LfoWait) / s.LfoFade);
            double pmdLfo = Dx7.Depth(s.LfoPmd) * fade;
            double pmdCtl = Math.Max(Dx7.Depth(s.WheelPitch) * engine.ModWheelValue, Dx7.Depth(s.AtPitch) * engine.AftertouchValue);
            double pitchMod = Math.Max(Math.Abs(Dx7.PitchMod(pmdLfo, s.PitchSens, lfo)), Math.Abs(Dx7.PitchMod(pmdCtl, s.PitchSens, lfo)));
            if (lfo < 0.5) pitchMod = -pitchMod;
            // Amplitude modulation attenuates by the inverted LFO, as the DX7 (a key-synced triangle starts unattenuated).
            double amd = Math.Max(Dx7.Depth(s.LfoAmd) / 255.0 * fade,
                                  Math.Max(Dx7.Depth(s.WheelAmp) / 255.0 * engine.ModWheelValue, Dx7.Depth(s.AtAmp) / 255.0 * engine.AftertouchValue)) * (1 - lfo);

            double modOct = peg.Next(dt) + pitchMod;
            double noteHz = DspMath.NoteToHz(pitch + s.Transpose + engine.PitchBendSemis + unisonPos * s.UniDetune * 0.25f);
            double modMul = Math.Pow(2, modOct);
            // Fixed-frequency operators follow pitch bend only (as Dexed: no pitch envelope, no vibrato).
            double fixedMul = Math.Pow(2, engine.PitchBendSemis / 12.0);
            float loud = 0;
            bool carriersDone = true;
            var alg = s.Algorithm;
            for (int k = 0; k < 6; k++)
            {
                var o = s.Ops[k];
                double hz = o.Fixed ? fixedHz[k] * fixedMul : noteHz * ratio[k] * modMul;
                // Above the Nyquist frequency an operator folds back, as on the DX7 (the phase wraps).
                double cycles = hz / sr;
                inc[k] = (uint)((cycles - Math.Floor(cycles)) * Cycle);
                double level = env[k].Next(count);
                if (o.AmpSens > 0 && amd > 0) level -= 256 * Dx7.AmpModDoublings(amd, o.AmpSens);
                float target = o.On ? (float)Dx7Envelope.Amplitude(level) : 0f;
                if (target < SilentGain) target = 0;   // as the DX7: an operator this quiet is silent
                gainStep[k] = (target - gain[k]) / count;
                if (alg.IsCarrier(k) && o.On)
                {
                    loud = Math.Max(loud, target);
                    if (!env[k].Done || target > 2e-4f) carriersDone = false;
                }
            }
            loudness = loud;
            if (!keyDown && carriersDone && !pending) finished = true;
        }

        /// <summary>Renders <paramref name="count"/> samples (at the voice's rate) from <paramref name="offset"/>.</summary>
        public void Render(float[] left, float[] right, int offset, int count)
        {
            if (finished && !pending && !steal.Fading) return;
            var s = engine.S;
            Control(count);
            var alg = s.Algorithm;
            var mods = alg.Modulators;
            int carriers = alg.Carriers;
            int fbFrom = alg.FeedbackFrom, fbTo = alg.FeedbackTo;
            float fbScale = s.Feedback == 0 ? 0f : MathF.Pow(2f, s.Feedback - 8) * 0.5f;
            float norm = CarrierNorm[alg.CarrierCount];
            for (int i = 0; i < count; i++)
            {
                float sum = 0;
                for (int k = 5; k >= 0; k--)
                {
                    float g = gain[k];
                    float y = 0;
                    if (g != 0 || gainStep[k] != 0)
                    {
                        float mod = 0;
                        int m = mods[k];
                        while (m != 0)
                        {
                            mod += outs[BitOperations.TrailingZeroCount(m)];
                            m &= m - 1;
                        }
                        if (k == fbTo) mod += fbScale * (fb1 + fb2);
                        uint p = phase[k] + (uint)(long)(mod * Cycle);
                        int idx = (int)(p >> (32 - FmEngine.TableBits));
                        float fr = (p & FracMask) * FracScale;
                        var t = tables[k];
                        y = (t[idx] + fr * (t[idx + 1] - t[idx])) * g;
                        gain[k] = g + gainStep[k];
                    }
                    outs[k] = y;
                    if (((carriers >> k) & 1) != 0) sum += y;
                    phase[k] += inc[k];
                }
                fb2 = fb1;
                fb1 = outs[fbFrom];

                float gainOut = norm;
                if (steal.Fading)
                {
                    if (!steal.Next(out float sg))
                    {
                        foreach (var e in env) e.Reset();
                        for (int k = 0; k < 6; k++) gain[k] = gainStep[k] = outs[k] = 0;
                        finished = true;
                        if (pending)
                        {
                            pending = false;
                            Start(pendNote, pendVel, pendUni, pendUniCount, pendGlideFrom);
                            if (pendRelease)
                            {
                                pendRelease = false;
                                NoteOff();
                            }
                            Control(Math.Max(1, count - i));
                        }
                        sg = 0;
                    }
                    gainOut *= sg;
                }
                float o = sum * gainOut;
                left[offset + i] += o * panL;
                right[offset + i] += o * panR;
            }
            if (Math.Abs(fb1) < 1e-20f) fb1 = 0;
            if (Math.Abs(fb2) < 1e-20f) fb2 = 0;
        }
    }
}
