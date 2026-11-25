#nullable disable
// Neo Chop (docs/specs/neo-chop.md): cuts the audio on a track up in time with the song. Zero latency. Clean room: textbook
// delay-line reads, loop crossfades and a state-variable filter; no code or table from any other machine.
//
//   input ──► ring of the last 8 s ──► the four cutting modes (one voice each at a time, a higher one takes over from a lower one:
//   Tape > Stutter > Repeat > Retrigger), each a reader of the ring, crossfaded with the live signal ──► step gate ──► auto-pan.
//
// Every cut is a crossfade that ENDS on its grid time: a voice starts F samples before its slice's first sample (reading the audio
// one slice earlier, so the blend arrives full at the grid time), each repeat boundary blends over the last F samples of the repeat
// into the start of the next, and a voice ends by blending back to the live signal over the last F samples before its end. So the
// first sample of every slice, repeat, jump and gate step is on its grid time to the sample, and nothing is ever cut hard. Notes
// can't be looked ahead of: a note-started voice starts its blend on the note's sample. F is the Smoothness knob (2 to 20 ms).
//
// Time is in the song's beats (4 per bar). Each block the host says where the song is (the beat at the block's first sample and the
// samples per beat); a grid point of beat g is on sample ceil((g - b0) / bps) of the block, so the schedule does not depend on the
// block size, follows a tempo change at once and starts again from the new position after a loop jump. Chances and variation are
// seeded hashes of the period's number: the same on every play.
//
// Mix and Amount (docs/specs/machines.md, "Mix and Amount"): the shared FxStage; Amount scales the stutter's pitch, decay and
// filter, the repeat chance and the gate and pan depths. Allocation-free after SetSampleRate; basic maths from PortableMath only.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Chop
{
    internal sealed class ChopEngine
    {
        public const int MaxBlock = 256;

        /// <summary>Samples of delay the host compensates: none.</summary>
        public const int Latency = 0;

        /// <summary>How much of the past the ring keeps, in seconds.</summary>
        public const double RingSeconds = 8.0;

        const int Slots = 3;                       // voices per mode (the one playing, the ones fading out)
        const uint Seed = 0xC40B5EED;
        const double Eps = 1e-6;                   // samples: how close to a whole sample a grid point counts as on it
        const double TapeFadeBelow = 0.25;         // the tape's level fades out below this speed
        const double MaxHoldSeconds = 20.0;

        readonly ParamValues values;
        readonly FxStage fx;

        float sampleRate = 48000;
        float[] ringL = Array.Empty<float>(), ringR = Array.Empty<float>();
        int mask;
        long now;                                  // stream time of the next sample
        int maxLag;                                // the farthest back a read goes

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // ---- transport, set by the machine before every block
        bool tPlaying;
        double tBeat, tSpb = 24000;
        // ---- what the block works with
        double b0, bps = 1.0 / 24000, spb = 24000;
        bool playing;
        long t0;
        int F;                                     // crossfade samples
        double invF;

        // ---- continuity of the transport
        bool havePos;
        double expectBeat;
        bool wasPlaying;

        // ---- notes
        readonly int[] noteOffset = new int[256];
        readonly int[] noteKey = new int[256];     // key, or -1 - key for a note off
        int noteCount;

        // ---- modes
        sealed class Mode
        {
            public bool On, Sync, Note;
            public bool HasNext;
            public double NextStart, NextBoundary;
            public double Signature = double.NaN;
            public int Fire = int.MaxValue;        // sample of this block the next voice starts on
            public double Period, Lead, Chance;
        }

        readonly Mode[] modes = { new Mode(), new Mode(), new Mode(), new Mode() };

        sealed class Voice
        {
            public bool Active;
            public ChopMode Mode;
            public int Key = -1;
            public long Serial;
            // envelope: in over inLen from InStart, out over outLen from OutStart
            public long InStart, OutStart = long.MaxValue;
            public double InvIn, InvOut;
            // end of the window
            public double EndBeat = double.PositiveInfinity;
            public long EndAbs = long.MaxValue;
            public bool Hold;                      // ends on note off (Hold stutter, tape)
            public bool SyncStart;
            // loop (stutter, repeat)
            public long A;
            public int S0;
            public long CurStart, NextRepAbs;
            public double RepBeat, NextRepBeat, SliceBeats;
            public int K;
            public double Rate, RateNext, Gain, GainNext;
            public int Xf;                         // crossfade samples of the current repeat
            public bool WillWrap;
            public bool IsRepeat;
            public double Variation;
            public bool FilterOn, FilterHp;
            public double FilLog, FilTarget;
            public ChopSvf FilL, FilR;
            public int FilCounter;
            public double FilG, FilK = 1.4142135623730951;
            // retrigger
            public double Grid, Unit;
            public long DCur, DNext;
            public long NextStepAbs;
            public double NextStepBeat;
            public int Step;
            // tape
            public int TState;                     // 0 stopping, 1 held, 2 starting, 3 resuming
            public long TStart, TBegin, TResume;
            public double Ts, Tu, Exp;
            public double TBoundaryBeat;
            public bool TSynced, NoteOffSeen;
            public long TBoundaryAbs;
        }

        readonly Voice[] voices = new Voice[4 * Slots];
        long voiceSerial;

        // ---- gate
        // The gate's "open" value o moves between 0 (an off step), 1 (on) and 2 (accent) along a smoothstep that lasts the attack (rising) or
        // the release (falling): no corner at either end of a transition. The gain is 1 - depth (1 - o) up to on, then up to the accent level.
        SmoothRamp gateDepthR, gateOnR;
        bool gateInit;
        double gateBeats = 0.25;
        double gateNextBeat;
        long gateNextAbs = long.MaxValue;
        int gateStep;
        double gateFreePos;
        double gateAttackSamples = 100, gateReleaseSamples = 1000;
        double gateO, gateFrom, gateTo, gateU = 1, gateInv;
        float gateAccent = 1.5f;
        float gateGain = 1f;

        // ---- pan
        SmoothRamp panDepthR;
        double panPhaseFree;
        bool panOn, panSynced;
        double panCycleBeats = 1, panRate = 1, panPhaseOffset;
        int panShape;

        // ---- parameters of the block
        ParamValues v;
        bool notePick;

        // ---- events (for the timing tests)
        public ChopEvent[] Log;
        public int LogCount;

        public ChopEngine(ParamValues values)
        {
            this.values = values;
            fx = new FxStage(values, P.Amount, P.Mix, Rules(), maxLatency: Latency, maxBlock: MaxBlock);
            for (int i = 0; i < voices.Length; i++) voices[i] = new Voice();
            SetSampleRate(48000);
        }

        static AmountRule[] Rules() => new[]
        {
            AmountRule.Lin(P.StutterPitch, 0),
            AmountRule.Lin(P.StutterDecay, 0),
            AmountRule.Lin(P.StutterFilter, 0),
            AmountRule.Lin(P.RepeatChance, 0),
            AmountRule.Lin(P.GateDepth, 0),
            AmountRule.Lin(P.PanDepth, 0),
        };

        /// <summary>The effective parameter values of the last block (Amount applied), for the editor's pictures.</summary>
        public ParamValues Effective => fx.Effective;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            fx.SetSampleRate(sr);
            int size = 1;
            long need = (long)(RingSeconds * sr) + (long)(0.1 * sr) + 64;
            while (size < need) size <<= 1;
            if (ringL.Length != size)
            {
                ringL = new float[size];
                ringR = new float[size];
            }
            mask = size - 1;
            maxLag = (int)((RingSeconds - 0.05) * sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            gateDepthR.Setup(0.02, sr); gateOnR.Setup(0.02, sr); panDepthR.Setup(0.02, sr);
            Reset();
        }

        /// <summary>Back to the start: the ring is emptied and everything stands down.</summary>
        public void Reset()
        {
            Array.Clear(ringL, 0, ringL.Length);
            Array.Clear(ringR, 0, ringR.Length);
            fx.Reset();
            now = 0;
            silentSamples = 0;
            for (int i = 0; i < voices.Length; i++) voices[i].Active = false;
            for (int m = 0; m < 4; m++) { modes[m].HasNext = false; modes[m].Signature = double.NaN; }
            havePos = false;
            wasPlaying = false;
            gateInit = false;
            gateGain = 1f; gateO = gateFrom = gateTo = 1; gateU = 1;
            gateDepthR.Reset(0f); gateOnR.Reset(0f); panDepthR.Reset(0f);
            noteCount = 0;
            LogCount = 0;
        }

        long silentSamples;

        /// <summary>How many samples in a row have been silent (the machine rests once the ring holds only silence).</summary>
        public long SilentSamples => silentSamples;

        /// <summary>The size of the ring, in samples.</summary>
        public int RingLength => ringL.Length;

        /// <summary>Forgets the notes queued for the next block.</summary>
        public void ClearNotes() { noteCount = 0; }

        /// <summary>The machine did not run for a while (its input was silent): positions are not continuous any more.</summary>
        public void Skipped()
        {
            havePos = false;
            for (int i = 0; i < voices.Length; i++) voices[i].Active = false;
            gateInit = false;
        }

        /// <summary>Whether a mode's voice is sounding (for the machine's rest, and tests).</summary>
        public bool AnyVoice
        {
            get
            {
                for (int i = 0; i < voices.Length; i++) if (voices[i].Active) return true;
                return false;
            }
        }

        /// <summary>The gate or the pan is doing something to the sound.</summary>
        public bool Shaping => gateOnR.Value > 0f || gateGain != 1f || panDepthR.Value > 0f || panDepthR.Target > 0f;

        /// <summary>Mix at 0 or Amount at 0: the stage passes the input untouched.</summary>
        public bool Idle => !AnyVoice && !Shaping;

        /// <summary>Where the song is for the next block: whether it plays, the beat of the first sample, and the samples per beat.</summary>
        public void SetTransport(bool playing, double beat, double samplesPerBeat)
        {
            tPlaying = playing;
            tBeat = beat;
            tSpb = samplesPerBeat > 1 ? samplesPerBeat : 24000;
        }

        /// <summary>A note for the next block at sample <paramref name="offset"/> (call in order of offset): on, or off.</summary>
        public void QueueNote(int offset, bool on, int key)
        {
            if (noteCount >= noteOffset.Length) return;
            noteOffset[noteCount] = Math.Max(0, offset);
            noteKey[noteCount] = on ? Math.Clamp(key, 0, 127) : -1 - Math.Clamp(key, 0, 127);
            noteCount++;
        }

        // ------------------------------------------------------------------------------------------------ parameters

        int Ch(int p) => (int)v.Value(p);

        void ReadParams()
        {
            double ms = v.Value(P.Smoothness);
            F = (int)Math.Max(2.0 * sampleRate / 1000.0 + 0.5, Math.Round(ms * sampleRate / 1000.0));
            invF = 1.0 / F;
            notePick = v.On(P.NotePick);
            modes[(int)ChopMode.Stutter].On = v.On(P.StutterOn);
            modes[(int)ChopMode.Repeat].On = v.On(P.RepeatOn);
            modes[(int)ChopMode.Retrigger].On = v.On(P.RetrigOn);
            modes[(int)ChopMode.Tape].On = v.On(P.TapeOn);
            SetTrigger(ChopMode.Stutter, Ch(P.StutterTrig));
            SetTrigger(ChopMode.Repeat, Ch(P.RepeatTrig));
            SetTrigger(ChopMode.Retrigger, Ch(P.RetrigTrig));
            SetTrigger(ChopMode.Tape, Ch(P.TapeTrig));
        }

        void SetTrigger(ChopMode m, int trig)
        {
            var s = modes[(int)m];
            s.Sync = s.On && playing && trig != 1;
            s.Note = s.On && playing && trig != 0;
        }

        double StutterSlice(int key) => notePick && key >= 0 ? ChopGrid.KeySlice[key % 12] : ChopGrid.SliceBeats[Ch(P.Slice)] * ChopGrid.Feel(Ch(P.SliceFeel));

        double RepeatSlice(int key) => notePick && key >= 0 ? ChopGrid.KeySlice[key % 12] : ChopGrid.SliceBeats[Ch(P.RepeatGrid)] * ChopGrid.Feel(Ch(P.RepeatFeel));

        double RetrigSlice(int key) => notePick && key >= 0 ? Math.Max(ChopGrid.KeySlice[key % 12], 0.125) : ChopGrid.SliceBeats[Ch(P.RetrigGrid)] * ChopGrid.Feel(Ch(P.RetrigFeel));

        double TapeStopSamples() => Ch(P.StopSync) == 0 ? v.Value(P.StopTime) * sampleRate / 1000.0 : ChopGrid.TimeBeats[Ch(P.StopSync)] * spb;

        double TapeStartSamples() => Ch(P.StartSync) == 0 ? v.Value(P.StartTime) * sampleRate / 1000.0 : ChopGrid.TimeBeats[Ch(P.StartSync)] * spb;

        /// <summary>The period of a synced mode, how long before its end its window starts, and the chance that it happens.</summary>
        void Window(ChopMode m, Mode s)
        {
            s.Chance = 1;
            switch (m)
            {
                case ChopMode.Stutter:
                {
                    double p = ChopGrid.EveryBeats[Ch(P.StutterEvery)];
                    int reps = Ch(P.Repeats) + 1;
                    double slice = StutterSlice(-1);
                    s.Period = p;
                    s.Lead = reps > 16 ? p * 0.5 : Math.Min(reps * slice, p);
                    break;
                }
                case ChopMode.Repeat:
                {
                    double p = ChopGrid.BeatsPerBar * (Ch(P.RepeatInterval) + 1);
                    s.Period = p;
                    s.Lead = Math.Min(ChopGrid.LengthBeats[Ch(P.RepeatLength)], p);
                    s.Chance = v.Value(P.RepeatChance);
                    break;
                }
                case ChopMode.Retrigger:
                {
                    double p = ChopGrid.EveryBeats[Ch(P.RetrigEvery)];
                    s.Period = p;
                    s.Lead = Math.Min(ChopGrid.LengthBeats[Ch(P.RetrigLength)], p);
                    break;
                }
                default:
                {
                    double p = ChopGrid.EveryBeats[Ch(P.TapeEvery)];
                    s.Period = p;
                    s.Lead = Math.Min(TapeStopSamples() / spb, p);
                    break;
                }
            }
        }

        /// <summary>The first window of a mode that starts at or after <paramref name="from"/> (beats).</summary>
        static bool NextWindow(Mode s, int mode, double from)
        {
            double p = s.Period, lead = s.Lead;
            long j = Math.Max(1, (long)Math.Ceiling((from + lead) / p - 1e-9));
            for (int tries = 0; tries < 64; tries++, j++)
            {
                if (s.Chance >= 1 || ChopGrid.Random01(Seed, j, mode) < s.Chance)
                {
                    s.NextStart = j * p - lead;
                    s.NextBoundary = j * p;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Plans each mode's next window from the parameters, and where in this block its voice starts.</summary>
        void PlanWindows(bool force)
        {
            for (int mi = 0; mi < 4; mi++)
            {
                var s = modes[mi];
                if (!s.Sync) { s.HasNext = false; s.Fire = int.MaxValue; s.Signature = double.NaN; continue; }
                Window((ChopMode)mi, s);
                double sig = s.Period * 1000003.0 + s.Lead * 7919.0 + s.Chance * 131.0;
                if (force || sig != s.Signature || !s.HasNext)
                {
                    s.Signature = sig;
                    s.HasNext = NextWindow(s, mi, b0 - 1e-9);
                }
                FindFire(s, mi);
            }
        }

        void FindFire(Mode s, int mi)
        {
            s.Fire = int.MaxValue;
            for (int guard = 0; s.HasNext && guard < 8; guard++)
            {
                double w = Math.Ceiling((s.NextStart - b0) / bps - Eps);
                if (w < 0)
                {
                    // Missed (the window began before this block): the next one.
                    s.HasNext = NextWindow(s, mi, s.NextStart + 1e-7);
                    continue;
                }
                if (w > MaxBlock + F + 8) return;                    // not before the block after the next
                int fire = (int)w - F;
                s.Fire = fire < 0 ? 0 : fire;
                return;
            }
        }

        // ------------------------------------------------------------------------------------------------ the block

        public void Process(float[] l, float[] r, int n)
        {
            if (n > MaxBlock) throw new InvalidOperationException("block larger than 256");
            fx.Begin(l, r, n);
            InMeter.Feed(l, r, n);
            v = fx.Effective;

            // Transport.
            playing = tPlaying;
            b0 = tBeat;
            spb = tSpb;
            bps = 1.0 / spb;
            t0 = now;
            ReadParams();
            if (playing)
            {
                bool jump = !havePos || !wasPlaying || Math.Abs(b0 - expectBeat) > 3 * bps;
                if (jump) OnJump();
                expectBeat = b0 + n * bps;
                havePos = true;
            }
            else
            {
                if (wasPlaying || havePos) StandDown();
                havePos = false;
            }
            wasPlaying = playing;

            // The ring takes the block (readers only look at samples up to the one being made).
            for (int i = 0; i < n; i++)
            {
                int k = (int)((t0 + i) & mask);
                ringL[k] = l[i];
                ringR[k] = r[i];
            }
            bool loud = false;
            for (int i = 0; i < n && !loud; i++) loud = MathF.Abs(l[i]) > 1e-7f || MathF.Abs(r[i]) > 1e-7f;
            silentSamples = loud ? 0 : silentSamples + n;

            PlanWindows(false);
            RefreshVoices(n);
            SetupGate();
            SetupPan();

            int ni = 0;
            for (int i = 0; i < n; i++)
            {
                long t = t0 + i;

                while (ni < noteCount && noteOffset[ni] <= i)
                {
                    HandleNote(i, noteKey[ni]);
                    ni++;
                }
                for (int mi = 0; mi < 4; mi++)
                {
                    var s = modes[mi];
                    while (s.Fire <= i) { FireWindow((ChopMode)mi, s, i); }
                }

                float xl = ringL[(int)(t & mask)], xr = ringR[(int)(t & mask)];
                float ol = xl, or = xr;
                if (AnyVoice) Mix(t, xl, xr, ref ol, ref or);

                if (gateOnR.Target > 0f || gateOnR.Value > 0f || gateGain != 1f) GateSample(i, t, ref ol, ref or);
                if (panDepthR.Target > 0f || panDepthR.Value > 0f) PanSample(i, ref ol, ref or);
                l[i] = ol;
                r[i] = or;
            }
            noteCount = 0;
            now = t0 + n;
            OutMeter.Feed(l, r, n);
            fx.End(l, r, n, Latency);
        }

        void OnJump()
        {
            // The song moved (it started, stopped and started, looped or was moved): what was running is faded out and the schedule starts from here.
            ReleaseAll(0);
            for (int m = 0; m < 4; m++) { modes[m].Signature = double.NaN; modes[m].HasNext = false; }
            gateInit = false;
        }

        void StandDown()
        {
            ReleaseAll(0);
            for (int m = 0; m < 4; m++) { modes[m].HasNext = false; modes[m].Signature = double.NaN; }
            gateInit = false;
        }

        void ReleaseAll(int at)
        {
            for (int i = 0; i < voices.Length; i++)
                if (voices[i].Active) Release(voices[i], t0 + at, F);
        }

        static void Release(Voice vo, long at, int len)
        {
            if (vo.OutStart <= at) return;
            vo.OutStart = at;
            vo.InvOut = 1.0 / Math.Max(1, len);
        }

        // ------------------------------------------------------------------------------------------------ notes and windows

        void HandleNote(int i, int key)
        {
            if (!playing) return;
            if (key >= 0)
            {
                if (Log != null) AddLog(ChopEventKind.NoteStart, t0 + i, b0 + i * bps, key);
                for (int mi = 0; mi < 4; mi++)
                {
                    var s = modes[mi];
                    if (!s.Note) continue;
                    StartVoice((ChopMode)mi, i, b0 + i * bps, double.NaN, key, false);
                }
            }
            else
            {
                int note = -1 - key;
                for (int vi = 0; vi < voices.Length; vi++)
                {
                    var vo = voices[vi];
                    if (!vo.Active || vo.Key != note) continue;
                    if (vo.Mode == ChopMode.Tape) vo.NoteOffSeen = true;
                    else if (vo.Hold) { Release(vo, t0 + i, F); }
                }
            }
        }

        void FireWindow(ChopMode m, Mode s, int i)
        {
            double start = s.NextStart, boundary = s.NextBoundary;
            s.HasNext = NextWindow(s, (int)m, start + 1e-7);
            StartVoice(m, i, start, boundary, -1, true);
            FindFire(s, (int)m);
            if (s.Fire <= i) s.Fire = i + 1;   // never twice on the same sample
        }

        bool Blocked(ChopMode m)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                var vo = voices[i];
                if (vo.Active && vo.Mode > m && vo.OutStart == long.MaxValue) return true;
            }
            return false;
        }

        /// <summary>Starts a voice: its blend begins on sample <paramref name="i"/> (a synced one ahead of its grid time, so the cut lands on it).</summary>
        void StartVoice(ChopMode m, int i, double startBeat, double boundary, int key, bool synced)
        {
            if (Blocked(m)) return;
            // Where the cut's first sample is: for a synced one its grid time, for a note the note's own sample.
            long w = synced ? t0 + (long)Math.Max(i, Math.Ceiling((startBeat - b0) / bps - Eps)) : t0 + i;
            var releaseAt = t0 + i;
            for (int k = 0; k < voices.Length; k++)
            {
                var o = voices[k];
                if (o.Active && o.Mode <= m) Release(o, releaseAt, (int)Math.Max(1, Math.Min(F, w - releaseAt + (synced ? 0 : F))));
            }
            var vo = TakeSlot(m);
            vo.Active = true;
            vo.Mode = m;
            vo.Key = key;
            vo.Serial = ++voiceSerial;
            vo.SyncStart = synced;
            vo.InStart = t0 + i;
            int inLen = synced ? (int)Math.Max(1, Math.Min(F, w - (t0 + i))) : F;
            vo.InvIn = 1.0 / inLen;
            vo.OutStart = long.MaxValue;
            vo.InvOut = invF;
            vo.Hold = false;
            vo.EndBeat = double.PositiveInfinity;
            vo.EndAbs = long.MaxValue;
            switch (m)
            {
                case ChopMode.Stutter: StartLoop(vo, false, w, startBeat, boundary, key, synced); break;
                case ChopMode.Repeat: StartLoop(vo, true, w, startBeat, boundary, key, synced); break;
                case ChopMode.Retrigger: StartRetrig(vo, w, startBeat, boundary, key, synced); break;
                default: StartTape(vo, w, startBeat, boundary, key, synced); break;
            }
            vo.EndAbs = long.MaxValue;
            if (!double.IsPositiveInfinity(vo.EndBeat)) UpdateEnd(vo);
        }

        Voice TakeSlot(ChopMode m)
        {
            int baseIdx = (int)m * Slots;
            Voice oldest = null;
            for (int k = 0; k < Slots; k++)
            {
                var vo = voices[baseIdx + k];
                if (!vo.Active) return vo;
                if (oldest == null || vo.Serial < oldest.Serial) oldest = vo;
            }
            return oldest;
        }

        void UpdateEnd(Voice vo)
        {
            if (double.IsPositiveInfinity(vo.EndBeat)) { vo.EndAbs = long.MaxValue; return; }
            double e = Math.Ceiling((vo.EndBeat - b0) / bps - Eps);
            vo.EndAbs = t0 + (long)e;
            long os = vo.EndAbs - F;
            if (vo.OutStart == long.MaxValue || vo.OutStart > os)
            {
                vo.OutStart = Math.Max(os, vo.InStart);
                vo.InvOut = invF;
            }
        }

        void RefreshVoices(int n)
        {
            for (int k = 0; k < voices.Length; k++)
            {
                var vo = voices[k];
                if (!vo.Active) continue;
                double ahead;
                switch (vo.Mode)
                {
                    case ChopMode.Stutter:
                    case ChopMode.Repeat:
                        if (vo.K == 0 && t0 < vo.CurStart && vo.SyncStart)
                        {
                            // Still ahead of its first sample: the grid time follows the tempo.
                            ahead = Math.Ceiling((vo.RepBeat - b0) / bps - Eps);
                            vo.CurStart = t0 + (long)ahead;
                            vo.A = vo.CurStart - vo.S0;
                        }
                        vo.NextRepAbs = t0 + (long)Math.Ceiling((vo.NextRepBeat - b0) / bps - Eps);
                        if (!double.IsPositiveInfinity(vo.EndBeat) && vo.OutStart > t0 - 1) UpdateEnd(vo);
                        break;
                    case ChopMode.Retrigger:
                        vo.NextStepAbs = t0 + (long)Math.Ceiling((vo.NextStepBeat - b0) / bps - Eps);
                        if (!double.IsPositiveInfinity(vo.EndBeat)) UpdateEnd(vo);
                        break;
                    default:
                        if (vo.TSynced && vo.TState <= 1) vo.TBoundaryAbs = t0 + (long)Math.Ceiling((vo.TBoundaryBeat - b0) / bps - Eps);
                        break;
                }
            }
        }

        void AddLog(ChopEventKind kind, long sample, double beat, int index)
        {
            if (Log == null || LogCount >= Log.Length) return;
            Log[LogCount].Kind = kind;
            Log[LogCount].Sample = sample;
            Log[LogCount].Beat = beat;
            Log[LogCount].Index = index;
            LogCount++;
        }

        // ------------------------------------------------------------------------------------------------ loop voices (stutter, repeat)

        double RepeatLen(Voice vo, int k)
        {
            if (!vo.IsRepeat || vo.Variation <= 0) return vo.SliceBeats;
            long sal = vo.Serial * 977 + k;
            if (ChopGrid.Random01(Seed, sal, 11) >= vo.Variation) return vo.SliceBeats;
            return vo.SliceBeats * (ChopGrid.Random01(Seed, sal, 13) < 0.5 ? 0.5 : 0.25);
        }

        double RateFor(Voice vo, int k)
        {
            if (vo.IsRepeat) return 1.0;
            double st = v.Value(P.StutterPitch) * k;
            if (st == 0) return 1.0;
            return Math.Clamp(PortableMath.Exp2(st / 12.0), 0.125, 8.0);
        }

        double GainFor(Voice vo, int k)
        {
            if (vo.IsRepeat) return 1.0;
            double db = -v.Value(P.StutterDecay) * k;
            return db == 0 ? 1.0 : PortableMath.Exp10(db / 20.0);
        }

        void SetFilterTarget(Voice vo, int k, bool snap)
        {
            if (vo.IsRepeat) { vo.FilterOn = false; return; }
            double oct = v.Value(P.StutterFilter);
            if (Math.Abs(oct) < 0.005) { vo.FilterOn = false; return; }
            double fc;
            if (oct < 0) { fc = 20000.0 * PortableMath.Exp2(oct * k); fc = Math.Max(fc, 60.0); vo.FilterHp = false; }
            else { fc = 20.0 * PortableMath.Exp2(oct * k); fc = Math.Min(fc, 10000.0); vo.FilterHp = true; }
            fc = Math.Min(fc, sampleRate * 0.45);
            vo.FilTarget = PortableMath.Log2(fc);
            if (snap || !vo.FilterOn) vo.FilLog = vo.FilTarget;
            vo.FilterOn = true;
        }

        void StartLoop(Voice vo, bool isRepeat, long w, double startBeat, double boundary, int key, bool synced)
        {
            vo.IsRepeat = isRepeat;
            vo.Variation = isRepeat ? v.Value(P.Variation) : 0;
            double slice = isRepeat ? RepeatSlice(key) : StutterSlice(key);
            vo.SliceBeats = slice;
            int s0 = (int)Math.Round(slice / bps);
            s0 = Math.Clamp(s0, 16, maxLag - 2 * F);
            vo.S0 = s0;
            vo.CurStart = w;
            vo.A = w - s0;
            vo.K = 0;
            vo.RepBeat = startBeat;
            vo.NextRepBeat = startBeat + RepeatLen(vo, 0);
            vo.NextRepAbs = t0 + (long)Math.Ceiling((vo.NextRepBeat - b0) / bps - Eps);
            vo.Rate = RateFor(vo, 0);
            vo.Gain = GainFor(vo, 0);
            vo.RateNext = RateFor(vo, 1);
            vo.GainNext = GainFor(vo, 1);
            vo.Xf = XfFor(vo, w);
            vo.WillWrap = vo.Rate * (vo.NextRepAbs - w) > vo.S0;
            vo.FilL.Reset(); vo.FilR.Reset();
            vo.FilCounter = 0;
            vo.FilterOn = false;
            SetFilterTarget(vo, 0, true);
            // The end.
            if (isRepeat)
            {
                vo.EndBeat = synced ? boundary : startBeat + ChopGrid.LengthBeats[Ch(P.RepeatLength)];
            }
            else
            {
                int reps = Ch(P.Repeats) + 1;
                if (synced) vo.EndBeat = boundary;
                else if (reps > 16) { vo.EndBeat = double.PositiveInfinity; vo.Hold = true; }
                else vo.EndBeat = startBeat + reps * slice;
            }
            if (Log != null) AddLog(isRepeat ? ChopEventKind.RepeatStart : ChopEventKind.StutterStart, w, startBeat, 0);
        }

        int XfFor(Voice vo, long curStart)
        {
            long len = vo.NextRepAbs - curStart;
            return (int)Math.Max(4, Math.Min(F, len / 2));
        }

        void AdvanceRepeat(Voice vo, long t)
        {
            vo.K++;
            vo.CurStart = vo.NextRepAbs;
            vo.Rate = vo.RateNext;
            vo.Gain = vo.GainNext;
            vo.RepBeat = vo.NextRepBeat;
            if (Log != null) AddLog(vo.IsRepeat ? ChopEventKind.RepeatRepeat : ChopEventKind.StutterRepeat, vo.CurStart, vo.RepBeat, vo.K);
            vo.NextRepBeat = vo.RepBeat + RepeatLen(vo, vo.K);
            vo.NextRepAbs = t0 + (long)Math.Ceiling((vo.NextRepBeat - b0) / bps - Eps);
            if (vo.NextRepAbs <= vo.CurStart) vo.NextRepAbs = vo.CurStart + 1;
            vo.RateNext = RateFor(vo, vo.K + 1);
            vo.GainNext = GainFor(vo, vo.K + 1);
            vo.Xf = XfFor(vo, vo.CurStart);
            vo.WillWrap = vo.Rate * (vo.NextRepAbs - vo.CurStart) > vo.S0;
            SetFilterTarget(vo, vo.K, false);
        }

        /// <summary>The slice at <paramref name="rs"/> samples (of the slice's own time) from its start; before the start it is the audio before the slice.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReadSlice(Voice vo, double rs, out float l, out float r)
        {
            if (rs < 0 || !vo.WillWrap && rs < vo.S0)
            {
                ReadAt(vo.A + rs, out l, out r);
                return;
            }
            double w = rs - vo.S0 * Math.Floor(rs / vo.S0);
            ReadAt(vo.A + w, out l, out r);
            double zone = vo.S0 - F;
            if (w > zone)
            {
                double c = (w - zone) * invF;
                c = c * c * (3 - 2 * c);
                ReadAt(vo.A + w - vo.S0, out float l2, out float r2);
                l = (float)(l + (l2 - l) * c);
                r = (float)(r + (r2 - r) * c);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReadAt(double pos, out float l, out float r)
        {
            double fl = Math.Floor(pos);
            long i = (long)fl;
            double f = pos - fl;
            if (f == 0)
            {
                int k = (int)(i & mask);
                l = ringL[k];
                r = ringR[k];
                return;
            }
            int a = (int)((i - 1) & mask), b = (int)(i & mask), c = (int)((i + 1) & mask), d = (int)((i + 2) & mask);
            l = Hermite(ringL[a], ringL[b], ringL[c], ringL[d], (float)f);
            r = Hermite(ringR[a], ringR[b], ringR[c], ringR[d], (float)f);
        }

        static float Hermite(float y0, float y1, float y2, float y3, float f)
        {
            float c0 = y1, c1 = 0.5f * (y2 - y0), c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3, c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * f + c2) * f + c1) * f + c0;
        }

        void LoopSample(Voice vo, long t, out float yl, out float yr)
        {
            if (t >= vo.NextRepAbs && t >= vo.CurStart) AdvanceRepeat(vo, t);
            ReadSlice(vo, (t - vo.CurStart) * vo.Rate, out yl, out yr);
            float g = (float)vo.Gain;
            long d = vo.NextRepAbs - t;
            if (d <= vo.Xf)
            {
                double c = 1.0 - (double)d / vo.Xf;
                c = c * c * (3 - 2 * c);
                ReadSlice(vo, (t - vo.NextRepAbs) * vo.RateNext, out float l2, out float r2);
                float gn = (float)vo.GainNext;
                yl = (float)(yl * g * (1 - c) + l2 * gn * c);
                yr = (float)(yr * g * (1 - c) + r2 * gn * c);
            }
            else if (g != 1f)
            {
                yl *= g;
                yr *= g;
            }
            if (vo.FilterOn)
            {
                if (vo.FilCounter-- <= 0)
                {
                    vo.FilCounter = 7;
                    double diff = vo.FilTarget - vo.FilLog;
                    vo.FilLog += diff * 0.12;                 // about 8 updates of 8 samples: a glide over a few ms
                    double fc = PortableMath.Exp2(vo.FilLog);
                    vo.FilG = PortableMath.Tan(Math.PI * Math.Min(fc, sampleRate * 0.45) / sampleRate);
                }
                yl = vo.FilL.Process(yl, vo.FilG, vo.FilK, vo.FilterHp);
                yr = vo.FilR.Process(yr, vo.FilG, vo.FilK, vo.FilterHp);
            }
        }

        // ------------------------------------------------------------------------------------------------ retrigger

        long DelayFor(Voice vo, double beat)
        {
            double back = beat - Math.Floor(beat / vo.Unit + 1e-9) * vo.Unit;
            if (back < 0) back = 0;
            long d = (long)Math.Round(back / bps);
            return Math.Min(d, maxLag - 2 * F);
        }

        void StartRetrig(Voice vo, long w, double startBeat, double boundary, int key, bool synced)
        {
            double grid = RetrigSlice(key);
            vo.Grid = grid;
            vo.Unit = Ch(P.RetrigBack) == 0 ? 1.0 : ChopGrid.BeatsPerBar;
            double first = startBeat;
            if (synced) first = Math.Ceiling(startBeat / grid - 1e-9) * grid;   // the first jump is on the grid
            vo.Step = 0;
            vo.DCur = DelayFor(vo, first);
            vo.NextStepBeat = first + grid;
            vo.DNext = DelayFor(vo, vo.NextStepBeat);
            vo.NextStepAbs = t0 + (long)Math.Ceiling((vo.NextStepBeat - b0) / bps - Eps);
            vo.CurStart = w;
            vo.RepBeat = first;
            vo.Xf = (int)Math.Max(4, Math.Min(F, (long)(grid * spb) / 2));
            vo.EndBeat = synced ? boundary : startBeat + ChopGrid.LengthBeats[Ch(P.RetrigLength)];
            if (Log != null) AddLog(ChopEventKind.RetrigStart, w, first, 0);
        }

        void RetrigSample(Voice vo, long t, out float yl, out float yr)
        {
            if (t >= vo.NextStepAbs && t >= vo.CurStart)
            {
                vo.Step++;
                vo.DCur = vo.DNext;
                vo.RepBeat = vo.NextStepBeat;
                if (Log != null) AddLog(ChopEventKind.RetrigStep, vo.NextStepAbs, vo.RepBeat, vo.Step);
                vo.NextStepBeat = vo.RepBeat + vo.Grid;
                vo.DNext = DelayFor(vo, vo.NextStepBeat);
                vo.NextStepAbs = t0 + (long)Math.Ceiling((vo.NextStepBeat - b0) / bps - Eps);
                if (vo.NextStepAbs <= t) vo.NextStepAbs = t + 1;
                vo.Xf = (int)Math.Max(4, Math.Min(F, (vo.NextStepAbs - t) / 2));
            }
            ReadAt(t - vo.DCur, out yl, out yr);
            long d = vo.NextStepAbs - t;
            if (d <= vo.Xf && vo.DNext != vo.DCur)
            {
                double c = 1.0 - (double)d / vo.Xf;
                c = c * c * (3 - 2 * c);
                ReadAt(t - vo.DNext, out float l2, out float r2);
                yl = (float)(yl + (l2 - yl) * c);
                yr = (float)(yr + (r2 - yr) * c);
            }
        }

        // ------------------------------------------------------------------------------------------------ tape

        void StartTape(Voice vo, long w, double startBeat, double boundary, int key, bool synced)
        {
            double ts = Math.Max(1, TapeStopSamples());
            ts = Math.Min(ts, maxLag * 0.5);
            double tu = Math.Max(0, TapeStartSamples());
            tu = Math.Min(tu, maxLag * 0.5);
            double curve = v.Value(P.TapeCurve);
            vo.Ts = ts;
            vo.Tu = tu;
            vo.Exp = PortableMath.Exp2(-curve * 1.5);
            vo.TState = 0;
            vo.TStart = w;
            vo.TSynced = synced;
            vo.NoteOffSeen = false;
            vo.TBoundaryBeat = boundary;
            vo.TBoundaryAbs = t0 + (long)Math.Ceiling((boundary - b0) / bps - Eps);
            vo.Hold = !synced;
            if (Log != null) AddLog(ChopEventKind.TapeStop, w, startBeat, 0);
        }

        void TapeSample(Voice vo, long t, float xl, float xr, out float yl, out float yr)
        {
            yl = xl; yr = xr;
            if (t < vo.TStart) return;
            switch (vo.TState)
            {
                case 0:
                {
                    double u = (t - vo.TStart) / vo.Ts;
                    if (u >= 1.0)
                    {
                        vo.TState = 1;
                        vo.TBegin = long.MaxValue;
                        goto case 1;
                    }
                    double e = vo.Exp;
                    double rem = 1.0 - u;
                    double s = PortableMath.Pow(rem, e);
                    double adv = vo.Ts * (1.0 - rem * s) / (e + 1.0);
                    ReadAt(vo.TStart + adv, out yl, out yr);
                    float g = (float)Math.Min(1.0, s / TapeFadeBelow);
                    yl *= g; yr *= g;
                    return;
                }
                case 1:
                {
                    yl = 0; yr = 0;
                    bool go;
                    if (vo.TSynced) go = t >= vo.TBoundaryAbs;
                    else go = vo.NoteOffSeen;
                    if (!go && t - (vo.TStart + (long)vo.Ts) > (long)(MaxHoldSeconds * sampleRate)) go = true;
                    if (!go) return;
                    vo.TBegin = t;
                    vo.TState = 2;
                    if (Log != null) AddLog(ChopEventKind.TapeStart, t, vo.TSynced ? vo.TBoundaryBeat : b0 + (t - t0) * bps, 0);
                    goto case 2;
                }
                case 2:
                {
                    if (vo.Tu < 1)
                    {
                        vo.TState = 3;
                        vo.TResume = t;
                        goto case 3;
                    }
                    double u = (t - vo.TBegin) / vo.Tu;
                    if (u >= 1.0)
                    {
                        vo.TState = 3;
                        vo.TResume = t;
                        goto case 3;
                    }
                    double e = vo.Exp;
                    double s = PortableMath.Pow(u, e);
                    double adv = vo.Tu * u * s / (e + 1.0);
                    ReadAt(vo.TBegin + adv, out yl, out yr);
                    float g = (float)Math.Min(1.0, s / TapeFadeBelow);
                    yl *= g; yr *= g;
                    return;
                }
                case 3:
                {
                    double lag = vo.Tu < 1 ? 0 : vo.Tu - vo.Tu / (vo.Exp + 1.0);
                    double c = (t - vo.TResume) * invF;
                    if (c >= 1.0)
                    {
                        vo.Active = false;
                        return;
                    }
                    c = c * c * (3 - 2 * c);
                    if (vo.Tu < 1) { yl = (float)(xl * c); yr = (float)(xr * c); return; }
                    ReadAt(t - lag, out float al, out float ar);
                    yl = (float)(al * (1 - c) + xl * c);
                    yr = (float)(ar * (1 - c) + xr * c);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ mixing the voices

        void Mix(long t, float xl, float xr, ref float ol, ref float or)
        {
            double sumL = 0, sumR = 0, a = 0;
            for (int k = 0; k < voices.Length; k++)
            {
                var vo = voices[k];
                if (!vo.Active) continue;
                double e = (t - vo.InStart) * vo.InvIn;
                if (e > 1) e = 1;
                if (e < 0) e = 0;
                if (t >= vo.OutStart)
                {
                    double o = 1.0 - (t - vo.OutStart) * vo.InvOut;
                    if (o <= 0) { vo.Active = false; continue; }
                    if (o < e) e = o;
                }
                float yl, yr;
                switch (vo.Mode)
                {
                    case ChopMode.Stutter:
                    case ChopMode.Repeat: LoopSample(vo, t, out yl, out yr); break;
                    case ChopMode.Retrigger: RetrigSample(vo, t, out yl, out yr); break;
                    default: TapeSample(vo, t, xl, xr, out yl, out yr); break;
                }
                if (!vo.Active) continue;
                e = e * e * (3 - 2 * e);
                sumL += e * yl;
                sumR += e * yr;
                a += e;
            }
            if (a <= 0) return;
            if (a > 1) { sumL /= a; sumR /= a; a = 1; }
            ol = (float)((1 - a) * xl + sumL);
            or = (float)((1 - a) * xr + sumR);
        }

        // ------------------------------------------------------------------------------------------------ gate

        void SetupGate()
        {
            bool on = v.On(P.GateOn);
            bool song = Ch(P.GateLock) == 0;
            bool active = on && (!song || playing);
            gateOnR.SetTarget(active ? 1f : 0f);
            gateDepthR.SetTarget((float)v.Value(P.GateDepth));
            gateBeats = ChopGrid.SliceBeats[Ch(P.GateRate)];
            gateAttackSamples = Math.Max(2.0 * sampleRate / 1000.0, v.Value(P.GateAttack) * sampleRate / 1000.0);
            gateReleaseSamples = Math.Max(2.0 * sampleRate / 1000.0, v.Value(P.GateRelease) * sampleRate / 1000.0);
            gateAccent = (float)PortableMath.Exp10(v.Value(P.GateAccent) / 20.0);
            if (!active) { gateNextAbs = long.MaxValue; return; }
            if (song)
            {
                if (!gateInit)
                {
                    long idx = (long)Math.Floor(b0 / gateBeats + 1e-9);
                    gateStep = (int)(idx & 15);
                    gateNextBeat = (idx + 1) * gateBeats;
                    gateInit = true;
                    SnapGate(gateStep);
                }
                else if (gateNextBeat - b0 > gateBeats * 1.0001 || gateNextBeat <= b0 - 1e-9)
                {
                    // The rate changed: the step the song is in now.
                    long idx = (long)Math.Floor(b0 / gateBeats + 1e-9);
                    gateStep = (int)(idx & 15);
                    gateNextBeat = (idx + 1) * gateBeats;
                    GoGate(gateStep);
                }
                gateNextAbs = t0 + (long)Math.Ceiling((gateNextBeat - b0) / bps - Eps);
            }
            else
            {
                if (!gateInit) { gateInit = true; gateStep = 0; gateFreePos = 0; SnapGate(0); }
                gateNextAbs = long.MaxValue;
            }
        }

        double StepTarget(int step)
        {
            int st = Ch(P.Step1 + (step & 15));
            return st == 0 ? 0.0 : st == 1 ? 1.0 : 2.0;
        }

        void SnapGate(int step)
        {
            gateO = gateFrom = gateTo = StepTarget(step);
            gateU = 1;
        }

        /// <summary>A step begins: the open value starts its smooth move to the step's level.</summary>
        void GoGate(int step)
        {
            double to = StepTarget(step);
            if (to == gateTo && gateU >= 1) return;
            gateFrom = gateO;
            gateTo = to;
            gateU = 0;
            gateInv = 1.0 / (to > gateFrom ? gateAttackSamples : gateReleaseSamples);
        }

        void GateSample(int i, long t, ref float ol, ref float or)
        {
            bool song = Ch(P.GateLock) == 0;
            if (song)
            {
                if (t >= gateNextAbs)
                {
                    long idx = (long)Math.Round(gateNextBeat / gateBeats);
                    gateStep = (int)(idx & 15);
                    if (Log != null) AddLog(ChopEventKind.GateStep, gateNextAbs, gateNextBeat, gateStep);
                    GoGate(gateStep);
                    gateNextBeat += gateBeats;
                    gateNextAbs = t0 + (long)Math.Ceiling((gateNextBeat - b0) / bps - Eps);
                    if (gateNextAbs <= t) gateNextAbs = t + 1;
                }
            }
            else
            {
                gateFreePos += 1.0;
                double len = gateBeats * spb;
                if (gateFreePos >= len)
                {
                    gateFreePos -= len;
                    gateStep = (gateStep + 1) & 15;
                    GoGate(gateStep);
                }
            }
            if (gateU < 1)
            {
                gateU += gateInv;
                if (gateU > 1) gateU = 1;
                double s = gateU * gateU * (3 - 2 * gateU);
                gateO = gateFrom + (gateTo - gateFrom) * s;
            }
            float w = gateOnR.Next();
            float depth = gateDepthR.Next();
            double g = gateO <= 1.0 ? 1.0 - depth * (1.0 - gateO) : 1.0 + (gateAccent - 1.0) * (gateO - 1.0);
            gateGain = (float)(1.0 + (g - 1.0) * w);
            if (gateGain != 1f) { ol *= gateGain; or *= gateGain; }
        }

        // ------------------------------------------------------------------------------------------------ pan

        void SetupPan()
        {
            panOn = v.On(P.PanOn);
            int sync = Ch(P.PanSync);
            panSynced = sync != 0;
            panCycleBeats = ChopGrid.PanBeats[sync];
            panRate = v.Value(P.PanRate);
            panPhaseOffset = v.Value(P.PanPhase) / 360.0;
            panShape = Ch(P.PanShape);
            bool active = panOn && (!panSynced || playing);
            panDepthR.SetTarget(active ? (float)v.Value(P.PanDepth) : 0f);
        }

        void PanSample(int i, ref float ol, ref float or)
        {
            float depth = panDepthR.Next();
            double cycles;
            if (panSynced && playing) cycles = (b0 + i * bps) / panCycleBeats;
            else { panPhaseFree += panRate / sampleRate; if (panPhaseFree >= 1e6) panPhaseFree -= 1e6; cycles = panPhaseFree; }
            cycles += panPhaseOffset;
            double ph = cycles - Math.Floor(cycles);
            double p;
            switch (panShape)
            {
                case 1:
                    p = ph < 0.5 ? 4 * ph - 1 : 3 - 4 * ph;
                    break;
                case 2:
                {
                    // A square whose edges take the Smoothness time: sin(phase) scaled up and clamped.
                    double edge = Math.Max(1e-4, 2 * Math.PI * (panSynced ? 1.0 / (panCycleBeats * spb) : panRate / sampleRate) * F);
                    double k = Math.Max(1.0, 2.0 / edge);
                    double y = Math.Clamp(PortableMath.Sin(2 * Math.PI * ph) * k, -1.0, 1.0);
                    p = 1.5 * y - 0.5 * y * y * y;
                    break;
                }
                default:
                    p = PortableMath.Sin(2 * Math.PI * ph);
                    break;
            }
            p *= depth;
            if (p == 0) return;
            // A balance law: the near channel stays, the far one falls as the cosine of the way over.
            double a = Math.Abs(p) * (Math.PI / 2);
            float far = (float)PortableMath.Cos(a);
            if (p > 0) ol *= far; else or *= far;
        }
    }
}
