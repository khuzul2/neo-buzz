#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md): the engine. Mono audio in, MIDI events out; no ReBuzz types.
//  - Pitch: PitchTracker (Voice range, Low latency or Quality) -> the nearest note allowed by the key (Set, Song, Suggested; muted notes out)
//    -> a note after Settle ms of the same candidate, changed only past Stickiness cents of advantage held for Settle ms, off after the voice
//    has been silent 25 ms and the note has lasted Min note length; velocity from the voice level; pitch bend free or anchored; legato.
//  - Triggers: OnsetDetector (Sensitivity, Min gap) -> VoiceFeatures (Fast 18 ms or Accurate 30 ms) -> VoiceClassifier (trained sounds) ->
//    a note of that sound with its velocity, off after the trigger length. Learning one sound: every onset's features go to a queue the
//    machine files as examples. A hit taken for a trigger does not also play a pitch note (Overlap).
//  - Chords: on each pitch note, the diatonic chord (ChordBuilder) on its own channel; suggestions from the notes sung lately.
//  - Expression: the voice level and the vowels aa, oo, ee as controllers with min/max, range and channel.
// Allocation-free per block after construction (Train, the learning drain and the readings are off the audio thread).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.VoiceToMidi
{
    internal enum MidiKind : byte { NoteOn, NoteOff, Controller, PitchBend }
    internal enum MidiStream : byte { Pitch, Chord, Trigger, Expression }

    /// <summary>One MIDI event. NoteOn: Data1 note, Data2 velocity. NoteOff: Data1 note. Controller: Data1 number, Data2 value. PitchBend: Data2 0 ... 16383 (8192 centre).</summary>
    internal struct MidiEvent
    {
        public int Offset;
        public MidiKind Kind;
        public MidiStream Stream;
        public byte Channel;
        public byte Data1;
        public int Data2;
    }

    internal sealed class VoiceEngine
    {
        public const int MaxBlock = 256, MaxEvents = 128, Sounds = 8, LearnSlots = 64;
        public readonly ParamValues Values;
        readonly VoiceClassifier classifier = new VoiceClassifier(Sounds);
        public VoiceClassifier Classifier => classifier;

        int rate = 48000;
        PitchTracker trackerQ, trackerL, tracker;
        PitchEstimate[] frames;
        OnsetDetector onsets;
        OnsetEvent[] onsetBuf;
        VoiceFeatures featFast, featAccurate;
        VowelEstimator vowels;
        float[] ring;
        int ringMask;
        long total;
        long blockStart;
        MidiEvent[] ev;
        int evCount;

        // Settings read once per block.
        bool pitchOn, triggersOn, chordsOn, bendOn, legato, fixedVel, accurate, triggersWin, anchored, sevenths, melody;
        int lockMode, keyRoot, scale, custom, muted, octave, bendRange, pitchCh, trigCh, chordCh, chordInv, chordSpread, chordOct;
        float stickSemis, anchorSemis, thresholdLin, velSoftDb, velLoudDb;
        int fixedVelocity, velMin, trigLenSamples, settleSamples, minNoteSamples, releaseHold, gapSamples;
        float chordVelShare;
        int activeMask = Scales.All;

        // Key.
        readonly KeyFinder finder = new KeyFinder();
        readonly ChordSuggester suggester = new ChordSuggester();
        public KeyFinder Finder => finder;
        public ChordSuggester Suggester => suggester;
        volatile string songKeyText = "";
        int songRoot = -1, songScale;
        string songKeyParsed = null;
        double keyClock, suggestClock;
        public int ActiveRoot { get; private set; }
        public int ActiveScale { get; private set; }
        public int ActiveMask => activeMask;

        // Pitch state.
        int curNote = -1;                 // the quantised note sounding (before the octave shift), -1 none
        int sentNote = -1;                // the note number sent (after the shift)
        long curOnAt;
        int pendNote = -1; long pendSince; float pendPeak;
        int switchNote = -1; long switchSince;
        long lastVoicedAt;
        bool pendingOff; long pendDue;
        bool bendOut;                      // anchored: out of the anchor
        int lastBend = 8192; long lastBendAt;
        int noteChannel = 0;
        public bool NoteSounding => curNote >= 0;
        public int CurrentNote => curNote;
        /// <summary>Something is still sounding or waiting (a note, a chord, a trigger's note off, an onset being classified): the machine keeps running.</summary>
        public bool Busy => curNote >= 0 || chordCount > 0 || offCount > 0 || queued > 0 || pendingOff;
        double lastSung;                   // the last voiced fractional MIDI note
        bool lastVoiced;
        float lastLevel;                   // linear RMS
        bool suppressPitch; long onsetPendingUntil;

        // Chords.
        readonly ChordBuilder chordBuilder = new ChordBuilder();
        readonly int[] chordNotes = new int[ChordBuilder.MaxNotes], chordTmp = new int[ChordBuilder.MaxNotes];
        int chordCount, chordChannel;
        public int ChordRoot => chordBuilder.RootPc;
        public int ChordQuality => chordBuilder.Quality;
        public bool ChordSounding => chordCount > 0;

        // Triggers.
        readonly OnsetEvent[] queue = new OnsetEvent[16];
        int queued;
        readonly float[] feat = new float[VoiceFeatures.Dimension];
        readonly int[] offNote = new int[32], offCh = new int[32];
        readonly long[] offDue = new long[32];
        int offCount;
        public int LastTrigger { get; private set; }
        public int TriggerSerial { get; private set; }
        /// <summary>The sung pitch (fractional MIDI, 0 unvoiced) every 5 ms, newest at <see cref="TrailHead"/> - 1, for the editor's piano roll.</summary>
        public const int TrailLength = 1024;
        public readonly float[] Trail = new float[TrailLength];
        public volatile int TrailHead;
        int trailAcc;
        /// <summary>Samples processed (the clock of <see cref="LitAt"/>).</summary>
        public long Total => Volatile.Read(ref total);
        public readonly bool[] Lit = new bool[Sounds];
        public readonly long[] LitAt = new long[Sounds];

        // Learning (audio thread produces, the machine's pump consumes).
        public volatile int Learning = -1;
        public int LearnTarget = 20;
        readonly float[][] learnF = new float[LearnSlots][];
        readonly int[] learnC = new int[LearnSlots];
        int learnWrite, learnRead;
        int learnedThisPass;
        public volatile bool LearnDone;
        public int LearnedThisPass => Volatile.Read(ref learnedThisPass);

        // Expression.
        readonly float[] laneLast = new float[4];
        readonly int[] laneSent = { -1, -1, -1, -1 };
        readonly long[] laneAt = new long[4];
        float lvl;                         // smoothed linear level
        int vowelAcc;
        VowelResult vowel;

        // For readings (any thread).
        public volatile float LevelDb = -120, SungMidi;
        public volatile bool Voiced;
        public float VowelAa, VowelOo, VowelEe;
        public long EventsSent;

        public VoiceEngine(ParamValues values)
        {
            Values = values;
            for (int i = 0; i < LearnSlots; i++) learnF[i] = new float[VoiceFeatures.Dimension];
            ev = new MidiEvent[MaxEvents];
            SetSampleRate(48000);
        }

        public void SetSongKey(string text) => songKeyText = text ?? "";

        public void SetSampleRate(int sampleRate)
        {
            rate = Math.Clamp(sampleRate, 8000, 192000);
            trackerQ = new PitchTracker(rate, PitchRange.Voice, PitchQuality.Quality);
            trackerL = new PitchTracker(rate, PitchRange.Voice, PitchQuality.LowLatency);
            tracker = trackerQ;
            frames = new PitchEstimate[Math.Max(trackerQ.MaxFrames(MaxBlock), trackerL.MaxFrames(MaxBlock)) + 4];
            onsets = new OnsetDetector(rate);
            onsetBuf = new OnsetEvent[onsets.MaxEvents(MaxBlock) + 4];
            featFast = new VoiceFeatures(rate, VoiceFeatures.FastSeconds);
            featAccurate = new VoiceFeatures(rate, VoiceFeatures.AccurateSeconds);
            vowels = new VowelEstimator(rate);
            int size = 1;
            while (size < rate / 4) size <<= 1;
            ring = new float[Math.Max(16384, size)];
            ringMask = ring.Length - 1;
            total = 0;
            Reset();
        }

        public void Reset()
        {
            trackerQ.Reset(); trackerL.Reset(); onsets.Reset(); vowels.Reset();
            Array.Clear(ring, 0, ring.Length);
            total = 0; lastVoicedAt = 0; curOnAt = 0; onsetPendingUntil = 0;   // the tracker's and the detector's sample counts restart with the ring's
            curNote = -1; sentNote = -1; pendNote = -1; switchNote = -1; pendingOff = false; bendOut = false; lastBend = 8192;
            queued = 0; offCount = 0; chordCount = 0; suppressPitch = false; onsetPendingUntil = 0;
            vowelAcc = 0; lvl = 0;
            for (int i = 0; i < 4; i++) laneSent[i] = -1;
            chordBuilder.Reset();
        }

        /// <summary>Samples from the start of a note's voice to its note on at best: the pitch window plus Settle (for the readings).</summary>
        public double NoteLatencySeconds(double hz = 220) => (tracker ?? trackerQ).DetectionSeconds(hz) + settleSamples / (double)rate;
        public int Rate => rate;
        public PitchTracker CurrentTracker => tracker;

        // ---------------------------------------------------------------------------------------------------------- settings

        float V(int i) => (float)Values.Table[i].Value(Values[i]);
        int I(int i) => (int)Math.Round(Values.Table[i].Value(Values[i]));
        bool B(int i) => Values.Table[i].Value(Values[i]) > 0;

        void ReadSettings()
        {
            pitchOn = B(P.Pitch);
            pitchEmit = pitchOn;
            lockMode = I(P.Lock);
            keyRoot = I(P.Root);
            scale = I(P.Scale);
            custom = Values[P.Custom];
            muted = Values[P.Muted];
            octave = I(P.Octave);
            stickSemis = V(P.Stick) / 100f;
            settleSamples = (int)(V(P.Settle) * 0.001 * rate);
            bendRange = I(P.Bend);
            bendOn = bendRange > 0;
            anchored = I(P.BendMode) == 1;
            anchorSemis = V(P.Anchor) / 100f;
            fixedVel = I(P.VelMode) == 1;
            fixedVelocity = I(P.Fixed);
            velMin = Math.Min(I(P.VelMin), fixedVelocity);
            velSoftDb = V(P.Soft);
            velLoudDb = Math.Max(V(P.Loud), velSoftDb + 1f);
            minNoteSamples = (int)(V(P.MinNote) * 0.001 * rate);
            legato = B(P.Legato);
            pitchCh = I(P.PitchCh) - 1;
            thresholdLin = (float)Math.Pow(10.0, V(P.Level) / 20.0);
            releaseHold = (int)(0.025 * rate);
            triggersOn = B(P.Triggers);
            accurate = I(P.Window) == 1;
            triggersWin = I(P.Overlap) == 0;
            trigLenSamples = (int)(V(P.Length) * 0.001 * rate);
            trigCh = I(P.TrigCh) - 1;
            gapSamples = (int)(V(P.Gap) * 0.001 * rate);
            chordsOn = B(P.Chords);
            melody = I(P.ChordFollow) == 1;
            sevenths = I(P.ChordType) == 1;
            chordInv = I(P.Inversion);
            chordSpread = I(P.Spread);
            chordOct = I(P.ChordOct);
            chordCh = I(P.ChordCh) - 1;
            chordVelShare = V(P.ChordVel);
            var want = I(P.Range) == 0 ? trackerL : trackerQ;
            if (!ReferenceEquals(want, tracker)) { tracker = want; want.Reset(); EndNote(0); }
            onsets.Threshold = 8f - 6f * V(P.Sensitivity);
            onsets.MinIntervalSeconds = Math.Max(0.02, gapSamples / (double)rate);
            activeMask = ComputeMask();
        }

        int ComputeMask()
        {
            int mask, root = keyRoot, sc = scale;
            switch (lockMode)
            {
                case 1: mask = Scales.KeyMask(keyRoot, scale, custom); break;
                case 2:
                    {
                        string text = songKeyText;
                        if (!ReferenceEquals(text, songKeyParsed))
                        {
                            songKeyParsed = text;
                            songRoot = -1;
                            if (Scales.TryParseKey(text, out int r, out int s)) { songRoot = r; songScale = s; }
                        }
                        if (songRoot >= 0) { root = songRoot; sc = songScale; mask = Scales.KeyMask(root, sc, custom); }
                        else mask = Scales.KeyMask(keyRoot, scale, custom);
                        break;
                    }
                case 3:
                    if (finder.Known) { root = finder.Root; sc = finder.Scale; mask = Scales.KeyMask(root, sc, custom); }
                    else mask = Scales.All;
                    break;
                default: mask = Scales.All; break;
            }
            ActiveRoot = root; ActiveScale = sc;
            int allowed = mask & ~muted & Scales.All;
            if (allowed == 0) allowed = Scales.All & ~muted;
            if (allowed == 0) allowed = Scales.All;
            return allowed;
        }

        /// <summary>The key's notes, before muting (for the editor: the chords, the piano roll's highlights).</summary>
        public int KeyNotes
        {
            get
            {
                switch (lockMode)
                {
                    case 1: return Scales.KeyMask(keyRoot, scale, custom);
                    case 2: return songRoot >= 0 ? Scales.KeyMask(songRoot, songScale, custom) : Scales.KeyMask(keyRoot, scale, custom);
                    case 3: return finder.Known ? Scales.KeyMask(finder.Root, finder.Scale, custom) : Scales.All;
                    default: return Scales.All;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------- events

        void Emit(MidiKind kind, MidiStream stream, int channel, int d1, int d2, long at)
        {
            if (evCount >= ev.Length) return;
            if (stream == MidiStream.Pitch && !pitchEmit && kind != MidiKind.NoteOff) return;
            int off = (int)Math.Clamp(at - blockStart, 0, Math.Max(0, blockLen - 1));
            ref var e = ref ev[evCount++];
            e.Offset = off; e.Kind = kind; e.Stream = stream; e.Channel = (byte)(channel & 15); e.Data1 = (byte)Math.Clamp(d1, 0, 127); e.Data2 = d2;
            EventsSent++;
        }

        int blockLen;
        // Pitch notes are sent while Pitch is on; the note machine also runs for the chords alone (a note on while Pitch is off is not sent, its note off never is).
        bool pitchEmit, noteSent;

        // ---------------------------------------------------------------------------------------------------------- the block

        /// <summary>Processes <paramref name="n"/> mono samples (at most <see cref="MaxBlock"/>) and returns the events (sorted by Offset) in <see cref="Events"/>.</summary>
        public int Process(float[] x, int n)
        {
            if (n > MaxBlock) n = MaxBlock;
            evCount = 0;
            blockStart = total; blockLen = n;
            ReadSettings();
            for (int i = 0; i < n; i++) ring[(int)((total + i) & ringMask)] = x[i];
            long end = total + n;

            // Level.
            double ss = 0;
            for (int i = 0; i < n; i++) ss += (double)x[i] * x[i];
            float rms = (float)Math.Sqrt(ss / Math.Max(1, n));
            float ka = (float)(1.0 - Math.Exp(-n / (0.005 * rate))), kr = (float)(1.0 - Math.Exp(-n / (0.06 * rate)));
            lvl += (rms > lvl ? ka : kr) * (rms - lvl);
            LevelDb = lvl > 1e-6f ? 20f * MathF.Log10(lvl) : -120f;

            // Pitch.
            int got = tracker.Process(x, 0, n, frames, 0);
            for (int i = 0; i < got; i++) Estimate(in frames[i]);
            // A note whose off is due.
            CheckOff(end);

            // Triggers.
            int found = onsets.Process(x, 0, n, onsetBuf, 0);
            for (int i = 0; i < found && queued < queue.Length; i++)
            {
                queue[queued++] = onsetBuf[i];
                var f = accurate ? featAccurate : featFast;
                onsetPendingUntil = Math.Max(onsetPendingUntil, onsetBuf[i].Sample - f.Lead + f.Length + (long)(0.002 * rate));
                // Overlap: an onset is a sound to name before the voice may start a note; a new onset ends the last one's suppression.
                if (Learning < 0 && !triggersWin) onsetPendingUntil = 0;
            }
            ServiceQueue(end);
            ServiceOffs(end);

            // Chords' suggestions and the key.
            double dt = n / (double)rate;
            suggester.Decay(dt);
            keyClock += dt; suggestClock += dt;
            if (keyClock >= 1.0) { keyClock = 0; finder.Update(); }
            if (suggestClock >= 0.25) { suggestClock = 0; suggester.Update(ActiveRoot, KeyNotes, sevenths); }

            Expression(x, n, end);

            // The trail: one pitch value every 5 ms.
            trailAcc += n;
            int step = Math.Max(1, rate / 200);
            if (trailAcc >= step)
            {
                float v = lastVoiced && lastLevel >= thresholdLin ? SungMidi : 0f;
                int head = TrailHead;
                while (trailAcc >= step) { Trail[head % TrailLength] = v; head++; trailAcc -= step; }
                TrailHead = head % (TrailLength * 1024);
            }

            Volatile.Write(ref total, end);
            // Sort by offset (stable insertion sort: a few events).
            for (int i = 1; i < evCount; i++)
            {
                var t = ev[i]; int j = i - 1;
                while (j >= 0 && ev[j].Offset > t.Offset) { ev[j + 1] = ev[j]; j--; }
                ev[j + 1] = t;
            }
            return evCount;
        }

        public MidiEvent[] Events => ev;

        // ---------------------------------------------------------------------------------------------------------- pitch

        void Estimate(in PitchEstimate e)
        {
            long t = e.EndSample;
            bool vo = e.Voiced && e.Rms >= thresholdLin;
            Voiced = e.Voiced;
            if (e.Voiced) { lastSung = e.Midi; SungMidi = (float)e.Midi; } else SungMidi = 0;
            lastVoiced = e.Voiced; lastLevel = e.Rms;
            if (!e.Voiced) suppressPitch = false;
            if ((!pitchOn && !chordsOn) || Learning >= 0) { if (curNote >= 0) EndNote(t); return; }
            double hop = tracker.Hop / (double)rate;
            if (vo && !suppressPitch)
            {
                lastVoicedAt = t;
                int cand = Scales.Nearest(e.Midi, activeMask);
                int sungPc = (int)Math.Round(e.Midi);
                finder.Add(sungPc, hop); suggester.Add(sungPc, hop);
                if (curNote < 0)
                {
                    if (cand != pendNote) { pendNote = cand; pendSince = t; pendPeak = e.Rms; }
                    else pendPeak = Math.Max(pendPeak, e.Rms);
                    bool held = triggersOn && triggersWin && onsetPendingUntil > t && Learning < 0;
                    if (t - pendSince >= settleSamples && !held) StartNote(cand, t, pendPeak, e.Midi);
                }
                else
                {
                    pendingOff = false;
                    pendPeak = Math.Max(pendPeak * 0.999f, e.Rms);
                    if (cand != curNote)
                    {
                        double advantage = Math.Abs(e.Midi - curNote) - Math.Abs(e.Midi - cand);
                        if (advantage >= stickSemis)
                        {
                            if (cand != switchNote) { switchNote = cand; switchSince = t; pendPeak = e.Rms; }
                            else pendPeak = Math.Max(pendPeak, e.Rms);
                            if (t - switchSince >= settleSamples) ChangeNote(cand, t, pendPeak, e.Midi);
                        }
                        else switchNote = -1;
                    }
                    else switchNote = -1;
                    if (curNote >= 0) Bend(e.Midi - curNote, t);
                }
            }
            else
            {
                pendNote = -1; switchNote = -1;
                if (curNote >= 0 && t - lastVoicedAt >= releaseHold)
                {
                    long due = Math.Max(t, curOnAt + minNoteSamples);
                    if (due <= t) EndNote(t);
                    else { pendingOff = true; pendDue = due; }
                }
            }
        }

        void CheckOff(long now)
        {
            if (pendingOff && curNote >= 0 && now >= pendDue) EndNote(pendDue);
        }

        int Velocity(float peak)
        {
            if (fixedVel) return Math.Clamp(fixedVelocity, 1, 127);
            double db = peak > 1e-6f ? 20.0 * Math.Log10(peak) : -120;
            double t = Math.Clamp((db - velSoftDb) / (velLoudDb - velSoftDb), 0.0, 1.0);
            return Math.Clamp((int)Math.Round(velMin + t * (fixedVelocity - velMin)), 1, 127);
        }

        int Shift(int note) => Math.Clamp(note + 12 * octave, 0, 127);

        void StartNote(int note, long t, float peak, double sung)
        {
            curNote = note; curOnAt = t; pendNote = -1; switchNote = -1; pendingOff = false; bendOut = false;
            int vel = Velocity(peak);
            if (bendOn && lastBend != 8192) SendBend(8192, t, true);
            sentNote = Shift(note); noteChannel = pitchCh;
            Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t);
            noteSent = pitchEmit;
            PlayChord(note, vel, t);
            LastNoteVelocity = vel;
        }

        public int LastNoteVelocity { get; private set; }

        void ChangeNote(int note, long t, float peak, double sung)
        {
            int old = sentNote, oldCh = noteChannel;
            bool oldSent = noteSent;
            int vel = Velocity(peak);
            curNote = note; curOnAt = t; switchNote = -1; bendOut = false;
            if (bendOn && lastBend != 8192) SendBend(8192, t, true);
            sentNote = Shift(note); noteChannel = pitchCh;
            if (legato)
            {
                Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t);
                if (oldSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, oldCh, old, 0, t);
            }
            else
            {
                if (oldSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, oldCh, old, 0, t);
                Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t);
            }
            noteSent = pitchEmit;
            StopChord(t);
            PlayChord(note, vel, t);
            LastNoteVelocity = vel;
        }

        void EndNote(long t)
        {
            if (curNote < 0) return;
            if (noteSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, noteChannel, sentNote, 0, t);
            noteSent = false;
            StopChord(t);
            curNote = -1; sentNote = -1; pendingOff = false; pendNote = -1; switchNote = -1;
            if (bendOn && lastBend != 8192) SendBend(8192, t, true);
        }

        void PlayChord(int note, int vel, long t)
        {
            if (!chordsOn) return;
            int count = chordBuilder.Build(note, ActiveRoot, KeyNotes, melody, sevenths, chordInv, chordSpread, chordOct, chordTmp);
            int cv = Math.Clamp((int)Math.Round(vel * chordVelShare), 1, 127);
            chordChannel = chordCh;
            for (int i = 0; i < count; i++) { chordNotes[i] = chordTmp[i]; Emit(MidiKind.NoteOn, MidiStream.Chord, chordChannel, chordNotes[i], cv, t); }
            chordCount = count;
        }

        void StopChord(long t)
        {
            for (int i = 0; i < chordCount; i++) Emit(MidiKind.NoteOff, MidiStream.Chord, chordChannel, chordNotes[i], 0, t);
            chordCount = 0;
        }

        void Bend(double dev, long t)
        {
            if (!bendOn) return;
            double d = dev;
            if (anchored)
            {
                double a = anchorSemis;
                double ad = Math.Abs(d);
                if (!bendOut) { if (ad > a) bendOut = true; }
                else if (ad < a * 0.5) bendOut = false;
                d = bendOut ? Math.Sign(d) * Math.Max(0, ad - a * 0.5) : 0;
            }
            int v = (int)Math.Round(8192 + Math.Clamp(d / bendRange, -1.0, 1.0) * 8191);
            SendBend(v, t, false);
        }

        void SendBend(int v, long t, bool force)
        {
            if (!force && (Math.Abs(v - lastBend) < 24 || t - lastBendAt < (long)(0.004 * rate))) return;
            lastBend = v; lastBendAt = t;
            Emit(MidiKind.PitchBend, MidiStream.Pitch, noteChannel, 0, Math.Clamp(v, 0, 16383), t);
        }

        // ---------------------------------------------------------------------------------------------------------- triggers

        void ServiceQueue(long now)
        {
            if (queued == 0) return;
            var f = accurate ? featAccurate : featFast;
            int keep = 0;
            for (int i = 0; i < queued; i++)
            {
                long ready = queue[i].Sample - f.Lead + f.Length;
                if (ready > now) { queue[keep++] = queue[i]; continue; }
                if (!triggersOn && Learning < 0) continue;
                f.Extract(ring, ringMask, queue[i].Sample, feat);
                int learn = Learning;
                if (learn >= 0)
                {
                    int w = learnWrite;
                    Array.Copy(feat, learnF[w % LearnSlots], VoiceFeatures.Dimension);
                    learnC[w % LearnSlots] = learn;
                    Volatile.Write(ref learnWrite, w + 1);
                    int c = Interlocked.Increment(ref learnedThisPass);
                    LitAt[learn] = total; Lit[learn] = true;
                    if (c >= LearnTarget) { Learning = -1; LearnDone = true; }
                    continue;
                }
                var r = classifier.Classify(feat);
                if (r.Class < 0) { onsetPendingUntil = 0; continue; }
                float sens = V(P.T1Sens + 3 * r.Class);
                float need = 0.6f - 0.5f * sens;
                if (r.Confidence < need) { onsetPendingUntil = 0; continue; }
                Trigger(r.Class, queue[i], Math.Max(ready, queue[i].ReportedAt));
            }
            queued = keep;
        }

        void Trigger(int cls, in OnsetEvent o, long at)
        {
            int note = I(P.T1Note + 3 * cls);
            int fixedV = I(P.T1Vel + 3 * cls);
            int vel;
            if (fixedV > 0) vel = fixedV;
            else
            {
                double t = Math.Clamp((o.LevelDb - velSoftDb) / (velLoudDb - velSoftDb), 0.0, 1.0);
                vel = Math.Clamp((int)Math.Round(velMin + t * (127 - velMin)), 1, 127);
            }
            // The same note still sounding: off first.
            for (int i = 0; i < offCount; i++)
                if (offNote[i] == note && offCh[i] == trigCh) { Emit(MidiKind.NoteOff, MidiStream.Trigger, trigCh, note, 0, at); offDue[i] = long.MaxValue; }
            Emit(MidiKind.NoteOn, MidiStream.Trigger, trigCh, note, vel, at);
            if (offCount < offNote.Length) { offNote[offCount] = note; offCh[offCount] = trigCh; offDue[offCount] = at + Math.Max(trigLenSamples, 1); offCount++; }
            LastTrigger = cls + 1; TriggerSerial++;
            Lit[cls] = true; LitAt[cls] = at;
            // A voiced sound taken for a trigger: no pitch note from it.
            if (triggersWin)
            {
                suppressPitch = true;
                if (curNote >= 0 && curOnAt >= o.Sample - (long)(0.005 * rate)) EndNote(at);
                pendNote = -1;
            }
            onsetPendingUntil = 0;
        }

        void ServiceOffs(long now)
        {
            int keep = 0;
            for (int i = 0; i < offCount; i++)
            {
                if (offDue[i] == long.MaxValue) continue;
                if (offDue[i] <= now) { Emit(MidiKind.NoteOff, MidiStream.Trigger, offCh[i], offNote[i], 0, offDue[i]); continue; }
                offNote[keep] = offNote[i]; offCh[keep] = offCh[i]; offDue[keep] = offDue[i]; keep++;
            }
            offCount = keep;
        }

        // ---------------------------------------------------------------------------------------------------------- learning

        /// <summary>Consumer (the machine's pump): the next learned example, or false.</summary>
        public bool TakeLearned(out int cls, float[] features)
        {
            cls = -1;
            int w = Volatile.Read(ref learnWrite);
            if (learnRead >= w) return false;
            if (w - learnRead > LearnSlots) learnRead = w - LearnSlots;
            int slot = learnRead % LearnSlots;
            cls = learnC[slot];
            Array.Copy(learnF[slot], features, VoiceFeatures.Dimension);
            learnRead++;
            return true;
        }

        /// <summary>Starts a pass for one sound (0 ... 7): onsets in it are filed as that sound's examples until <see cref="LearnTarget"/> or <see cref="StopLearning"/>.</summary>
        public void StartLearning(int sound)
        {
            Volatile.Write(ref learnedThisPass, 0);
            LearnDone = false;
            Learning = sound;
        }

        public void StopLearning() { Learning = -1; }

        // ---------------------------------------------------------------------------------------------------------- expression

        void Expression(float[] x, int n, long now)
        {
            vowels.Push(x, 0, n);
            vowelAcc += n;
            if (vowelAcc >= (int)(0.01 * rate))
            {
                vowelAcc = 0;
                vowel = vowels.Update(lastVoiced);
                VowelAa = vowel.Aa; VowelOo = vowel.Oo; VowelEe = vowel.Ee;
            }
            float levelNorm = Math.Clamp((LevelDb + 60f) / 60f, 0f, 1f);
            Lane(0, P.LvlOn, levelNorm, now);
            Lane(1, P.AaOn, vowel.Aa, now);
            Lane(2, P.OoOn, vowel.Oo, now);
            Lane(3, P.EeOn, vowel.Ee, now);
        }

        void Lane(int lane, int first, float x, long now)
        {
            if (!B(first + 0)) return;
            float min = V(first + 2), max = V(first + 3);
            float t = max > min + 1e-4f ? Math.Clamp((x - min) / (max - min), 0f, 1f) : (x >= min ? 1f : 0f);
            int lo = I(first + 4), hi = I(first + 5);
            int value = Math.Clamp((int)Math.Round(lo + t * (hi - lo)), 0, 127);
            if (value == laneSent[lane] || now - laneAt[lane] < (long)(0.01 * rate)) return;
            laneSent[lane] = value; laneAt[lane] = now;
            Emit(MidiKind.Controller, MidiStream.Expression, I(first + 6) - 1, I(first + 1), value, now);
        }

        // ---------------------------------------------------------------------------------------------------------- flush

        /// <summary>Ends everything sounding (transport stop, the machine going quiet, a new target): the events are in <see cref="Events"/>.</summary>
        public int Flush()
        {
            evCount = 0; blockStart = total; blockLen = 1;
            ReadSettings();
            EndNote(total);
            for (int i = 0; i < offCount; i++) if (offDue[i] != long.MaxValue) Emit(MidiKind.NoteOff, MidiStream.Trigger, offCh[i], offNote[i], 0, total);
            offCount = 0;
            for (int i = 0; i < 4; i++) laneSent[i] = -1;
            return evCount;
        }

        // ---------------------------------------------------------------------------------------------------------- readings

        public IReadOnlyDictionary<string, double> Readings()
        {
            int trained = 0;
            for (int c = 0; c < Sounds; c++) if (classifier.ExamplesOf(c) > 0) trained++;
            return new Dictionary<string, double>
            {
                ["note"] = sentNote >= 0 ? sentNote : 0,
                ["sung_midi"] = SungMidi,
                ["voiced"] = Voiced ? 1 : 0,
                ["key_root"] = lockMode == 0 ? -1 : ActiveRoot,
                ["key_minor"] = ActiveScale == 1 || ActiveScale == 7 || ActiveScale == 8 ? 1 : 0,
                ["level_db"] = LevelDb,
                ["vowel_aa"] = VowelAa,
                ["vowel_oo"] = VowelOo,
                ["vowel_ee"] = VowelEe,
                ["last_trigger"] = LastTrigger,
                ["trained_sounds"] = trained,
                ["note_latency_ms"] = NoteLatencySeconds() * 1000.0,
            };
        }
    }
}
