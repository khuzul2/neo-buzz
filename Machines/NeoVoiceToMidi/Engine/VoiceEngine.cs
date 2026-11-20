#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md): the engine. Mono audio in, MIDI events out; no ReBuzz types.
//  - Pitch: PitchTracker (Voice range, Low latency or Quality) -> the nearest note allowed by the key (Set, Song, Suggested; muted notes out)
//    -> Fast response: a note at the first pitched estimate (corrected within 80 ms if the nearest note was another); Stable: after Settle ms of
//    the same candidate. A running note changes only past Stickiness cents of advantage held for Settle ms; off after the voice
//    has been silent 25 ms and the note has lasted Min note length; velocity from the voice level; pitch bend free or anchored; legato.
//  - Triggers: OnsetDetector (Sensitivity, Min gap) -> VoiceFeatures (Fast 18 ms or Accurate 30 ms) -> VoiceClassifier (trained sounds) ->
//    a note of that sound with its velocity, off after the trigger length. Learning one sound: every onset's features go to a queue the
//    machine files as examples. A hit taken for a trigger does not also play a pitch note (Overlap).
//  - Chords: on each pitch note, the diatonic chord (ChordBuilder) on its own channel; suggestions from the notes sung lately.
//  - Expression: the voice level and the vowels aa, oo, ee as controllers with min/max, range and channel.
// Allocation-free per block after construction (Train, the learning drain and the readings are off the audio thread).
// The parts Neo Audio to MIDI shares (events, key lock, velocity, bend, note-off queue, training pass, waiting hits) are in Machines/NeoBuzz.Dsp/Midi.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.VoiceToMidi
{
    internal sealed class VoiceEngine
    {
        public const int MaxBlock = 256, MaxEvents = 128, Sounds = 8, LearnSlots = 64;
        public readonly ParamValues Values;
        readonly VoiceClassifier classifier = new VoiceClassifier(Sounds);
        readonly MidiBlock block = new MidiBlock();
        readonly KeyLock keyLock = new KeyLock();
        readonly PitchBendMap bend = new PitchBendMap();
        readonly NoteOffQueue offs = new NoteOffQueue(32);
        readonly OnsetTrainer trainer = new OnsetTrainer(VoiceFeatures.Dimension);
        readonly HitQueue pending = new HitQueue(16);
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

        // Settings read once per block.
        bool fastResponse, pitchOn, triggersOn, chordsOn, bendOn, legato, fixedVel, accurate, triggersWin, sevenths, melody;
        int lockMode, keyRoot, scale, custom, muted, octave, bendRange, pitchCh, trigCh, chordCh, chordInv, chordSpread, chordOct;
        float stickSemis, thresholdLin, velSoftDb, velLoudDb;
        int fixedVelocity, velMin, trigLenSamples, settleSamples, correctSamples, fastWaitSamples, minNoteSamples, releaseHold, gapSamples;
        float chordVelShare;
        int activeMask = Scales.All;

        // Key.
        readonly ChordSuggester suggester = new ChordSuggester();
        public KeyFinder Finder => keyLock.Finder;
        public ChordSuggester Suggester => suggester;
        double keyClock, suggestClock;
        public int ActiveRoot => keyLock.ActiveRoot;
        public int ActiveScale => keyLock.ActiveScale;
        public int ActiveMask => activeMask;

        // Pitch state.
        int curNote = -1;                 // the quantised note sounding (before the octave shift), -1 none
        int sentNote = -1;                // the note number sent (after the shift)
        long curOnAt;
        long noteStartAt;                 // when the voice's first note of this run played (the correction window counts from here)
        int pendNote = -1; long pendSince, fastSince; float pendPeak;
        int switchNote = -1; long switchSince;
        long lastVoicedAt;
        bool pendingOff; long pendDue;
        int noteChannel = 0;
        public bool NoteSounding => curNote >= 0;
        public int CurrentNote => curNote;
        /// <summary>Something is still sounding or waiting (a note, a chord, a trigger's note off, an onset being classified): the machine keeps running.</summary>
        public bool Busy => curNote >= 0 || chordCount > 0 || offs.Count > 0 || pending.Count > 0 || pendingOff;
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
        readonly float[] feat = new float[VoiceFeatures.Dimension];
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
        public int Learning => trainer.Learning;
        public int LearnTarget { get => trainer.Target; set => trainer.Target = value; }
        public bool LearnDone => trainer.Done;
        public int LearnedThisPass => trainer.Learned;

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
        public long EventsSent => block.EventsSent;

        public VoiceEngine(ParamValues values)
        {
            Values = values;
            SetSampleRate(48000);
        }

        public void SetSongKey(string text) => keyLock.SetSongKey(text);

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
            curNote = -1; sentNote = -1; pendNote = -1; switchNote = -1; pendingOff = false; bend.ResetAnchor(); bend.Centre(0, out _);
            pending.Clear(); offs.Clear(); chordCount = 0; suppressPitch = false; onsetPendingUntil = 0;
            vowelAcc = 0; lvl = 0;
            for (int i = 0; i < 4; i++) laneSent[i] = -1;
            chordBuilder.Reset();
        }

        /// <summary>Samples from the start of a note's voice to its note on at best: the pitch window plus Settle (for the readings).</summary>
        public double NoteLatencySeconds(double hz = 220) => (tracker ?? trackerQ).DetectionSeconds(hz) + (fastResponse ? FastWaitMs * 0.001 : settleSamples / (double)rate);
        /// <summary>How long the first note may still be corrected after it played (Fast response), in ms.</summary>
        public const double CorrectionMs = 80;
        /// <summary>Fast response: the clarity (0 to 1) that lets the first note play, and the longest wait for it after the first pitched estimate, in ms.</summary>
        public const float FastConfidence = 0.93f;
        public const double FastWaitMs = 12;
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
            fastResponse = I(P.Response) == 0;
            correctSamples = (int)(CorrectionMs * 0.001 * rate);
            fastWaitSamples = (int)(FastWaitMs * 0.001 * rate);
            bendRange = I(P.Bend);
            bendOn = bendRange > 0;
            bend.Range = bendRange;
            bend.Anchored = I(P.BendMode) == 1;
            bend.AnchorSemis = V(P.Anchor) / 100f;
            fixedVel = I(P.VelMode) == 1;
            fixedVelocity = I(P.Fixed);
            velMin = Math.Min(I(P.VelMin), fixedVelocity);
            velSoftDb = V(P.Soft);
            velLoudDb = Math.Max(V(P.Loud), velSoftDb + 1f);
            minNoteSamples = (int)(V(P.MinNote) * 0.001 * rate);
            legato = B(P.Legato);
            pitchCh = I(P.PitchCh) - 1;
            thresholdLin = (float)PortableMath.Pow(10.0, V(P.Level) / 20.0);
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

        int ComputeMask() => keyLock.Compute(lockMode, keyRoot, scale, custom, muted);

        /// <summary>The key's notes, before muting (for the editor: the chords, the piano roll's highlights).</summary>
        public int KeyNotes => keyLock.KeyNotes;

        // ---------------------------------------------------------------------------------------------------------- events

        void Emit(MidiKind kind, MidiStream stream, int channel, int d1, int d2, long at) => Emit(kind, stream, channel, d1, d2, at, at);

        void Emit(MidiKind kind, MidiStream stream, int channel, int d1, int d2, long at, long attack)
        {
            if (stream == MidiStream.Pitch && !pitchEmit && kind != MidiKind.NoteOff) return;
            block.Emit(kind, stream, channel, d1, d2, at, attack);
        }

        /// <summary>The stream position the sound of the note began at, as far as the engine knows: the first voiced estimate's window centre (a recording track places the note there: Record at attack).</summary>
        long AttackOf(long since, long t) => Math.Max(Math.Min(since - tracker.Window / 2, t), curNote >= 0 ? curAttack + rate / 1000 : lastOffAt);
        long curAttack, lastOffAt = long.MinValue / 2;

        // Pitch notes are sent while Pitch is on; the note machine also runs for the chords alone (a note on while Pitch is off is not sent, its note off never is).
        bool pitchEmit, noteSent;

        // ---------------------------------------------------------------------------------------------------------- the block

        /// <summary>Processes <paramref name="n"/> mono samples (at most <see cref="MaxBlock"/>) and returns the events (sorted by Offset) in <see cref="Events"/>.</summary>
        public int Process(float[] x, int n)
        {
            if (n > MaxBlock) n = MaxBlock;
            block.Begin(total, n);
            ReadSettings();
            for (int i = 0; i < n; i++) ring[(int)((total + i) & ringMask)] = x[i];
            long end = total + n;

            // Level.
            double ss = 0;
            for (int i = 0; i < n; i++) ss += (double)x[i] * x[i];
            float rms = (float)Math.Sqrt(ss / Math.Max(1, n));
            float ka = (float)(1.0 - PortableMath.Exp(-n / (0.005 * rate))), kr = (float)(1.0 - PortableMath.Exp(-n / (0.06 * rate)));
            lvl += (rms > lvl ? ka : kr) * (rms - lvl);
            LevelDb = lvl > 1e-6f ? 20f * PortableMathF.Log10(lvl) : -120f;

            // Onsets first: a percussive hit is known before the pitch of its first frames can start a note (Fast response plays at once).
            int found = onsets.Process(x, 0, n, onsetBuf, 0);
            for (int i = 0; i < found; i++)
            {
                if (!pending.Add(onsetBuf[i])) break;
                var f = accurate ? featAccurate : featFast;
                onsetPendingUntil = Math.Max(onsetPendingUntil, onsetBuf[i].Sample - f.Lead + f.Length + (long)(0.002 * rate));
                // Overlap: an onset is a sound to name before the voice may start a note; a new onset ends the last one's suppression.
                if (Learning < 0 && !triggersWin) onsetPendingUntil = 0;
            }
            // Pitch.
            int got = tracker.Process(x, 0, n, frames, 0);
            for (int i = 0; i < got; i++) Estimate(in frames[i]);
            // A note whose off is due.
            CheckOff(end);

            // Triggers.
            ServiceQueue(end);
            ServiceOffs(end);

            // Chords' suggestions and the key.
            double dt = n / (double)rate;
            suggester.Decay(dt);
            keyClock += dt; suggestClock += dt;
            if (keyClock >= 1.0) { keyClock = 0; keyLock.Finder.Update(); }
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
            block.SortByOffset();
            return block.Count;
        }

        public MidiEvent[] Events => block.Events;

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
                keyLock.Finder.Add(sungPc, hop); suggester.Add(sungPc, hop);
                if (curNote < 0)
                {
                    if (pendNote < 0) fastSince = t;
                    if (cand != pendNote) { pendNote = cand; pendSince = t; pendPeak = e.Rms; }
                    else pendPeak = Math.Max(pendPeak, e.Rms);
                    // Stable holds a note back while an onset is being named; Fast plays at once and a hit named a trigger cuts it (Trigger).
                    bool held = !fastResponse && triggersOn && triggersWin && onsetPendingUntil > t && Learning < 0;
                    // Fast: the first pitched estimate plays at once (Settle and Stickiness only filter what comes after); Stable waits Settle ms.
                    // The first estimates of an onset can be an octave out: Fast waits for a clean one (Confidence), at most FastWaitMs.
                    bool ready = fastResponse ? e.Confidence >= FastConfidence || t - fastSince >= fastWaitSamples : t - pendSince >= settleSamples;
                    if (ready && !held) StartNote(cand, t, pendPeak, e.Midi, fastSince);
                }
                else
                {
                    pendingOff = false;
                    pendPeak = Math.Max(pendPeak * 0.999f, e.Rms);
                    if (fastResponse && cand != curNote && t - noteStartAt < correctSamples && !bendOn)
                    {
                        // The first estimate can be a note off: within the correction window the nearest note wins as soon as two estimates agree
                        // (with Pitch bend on, the bend already follows the voice, so the note is left alone).
                        if (cand != switchNote) { switchNote = cand; switchSince = t; pendPeak = Math.Max(pendPeak, e.Rms); }
                        else if (t - switchSince >= 2 * hop * rate) { long keep = noteStartAt; ChangeNote(cand, t, pendPeak, e.Midi, cand % 12 == curNote % 12); noteStartAt = keep; Corrections++; }
                    }
                    else if (cand != curNote)
                    {
                        double advantage = Math.Abs(e.Midi - curNote) - Math.Abs(e.Midi - cand);
                        if (advantage >= stickSemis)
                        {
                            if (cand != switchNote) { switchNote = cand; switchSince = t; pendPeak = e.Rms; }
                            else pendPeak = Math.Max(pendPeak, e.Rms);
                            if (t - switchSince >= settleSamples) ChangeNote(cand, t, pendPeak, e.Midi, false, switchSince);
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

        int Velocity(float peak) => VelocityMap.FromPeak(peak, fixedVel, fixedVelocity, velMin, velSoftDb, velLoudDb);

        int Shift(int note) => Math.Clamp(note + 12 * octave, 0, 127);

        void StartNote(int note, long t, float peak, double sung, long since)
        {
            long attack = AttackOf(since, t);
            curNote = note; curOnAt = t; noteStartAt = t; pendNote = -1; switchNote = -1; pendingOff = false; bend.ResetAnchor();
            int vel = Velocity(peak);
            CentreBend(t);
            sentNote = Shift(note); noteChannel = pitchCh;
            Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t, attack);
            curAttack = attack;
            noteSent = pitchEmit;
            PlayChord(note, vel, t, attack);
            LastNoteVelocity = vel;
        }

        public int LastNoteVelocity { get; private set; }
        /// <summary>How many first notes were corrected within the window (Fast response).</summary>
        public int Corrections { get; private set; }

        void ChangeNote(int note, long t, float peak, double sung, bool keepChord = false, long since = long.MaxValue)
        {
            long attack = since == long.MaxValue ? curAttack : AttackOf(since, t);
            int old = sentNote, oldCh = noteChannel;
            bool oldSent = noteSent;
            int vel = Velocity(peak);
            curNote = note; curOnAt = t; switchNote = -1; bend.ResetAnchor();
            CentreBend(t);
            sentNote = Shift(note); noteChannel = pitchCh;
            if (since == long.MaxValue) attack = Math.Min(t, curAttack + rate / 1000);         // a correction: the first note's attack, a millisecond on
            if (legato)
            {
                Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t, attack);
                if (oldSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, oldCh, old, 0, t, attack);
            }
            else
            {
                if (oldSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, oldCh, old, 0, t, attack);
                Emit(MidiKind.NoteOn, MidiStream.Pitch, noteChannel, sentNote, vel, t, attack);
            }
            curAttack = attack;
            noteSent = pitchEmit;
            if (!keepChord) { StopChord(t); PlayChord(note, vel, t, attack); }
            LastNoteVelocity = vel;
        }

        void EndNote(long t)
        {
            if (curNote < 0) return;
            if (noteSent) Emit(MidiKind.NoteOff, MidiStream.Pitch, noteChannel, sentNote, 0, t);
            noteSent = false; lastOffAt = t;
            StopChord(t);
            curNote = -1; sentNote = -1; pendingOff = false; pendNote = -1; switchNote = -1;
            CentreBend(t);
        }

        void PlayChord(int note, int vel, long t, long attack)
        {
            if (!chordsOn) return;
            int count = chordBuilder.Build(note, ActiveRoot, KeyNotes, melody, sevenths, chordInv, chordSpread, chordOct, chordTmp);
            int cv = Math.Clamp((int)Math.Round(vel * chordVelShare), 1, 127);
            chordChannel = chordCh;
            for (int i = 0; i < count; i++) { chordNotes[i] = chordTmp[i]; Emit(MidiKind.NoteOn, MidiStream.Chord, chordChannel, chordNotes[i], cv, t, attack); }
            chordCount = count;
        }

        void StopChord(long t)
        {
            for (int i = 0; i < chordCount; i++) Emit(MidiKind.NoteOff, MidiStream.Chord, chordChannel, chordNotes[i], 0, t);
            chordCount = 0;
        }

        void Bend(double dev, long t)
        {
            if (bend.Next(dev, t, rate, out int v)) Emit(MidiKind.PitchBend, MidiStream.Pitch, noteChannel, 0, v, t);
        }

        void CentreBend(long t)
        {
            if (bend.Centre(t, out int v)) Emit(MidiKind.PitchBend, MidiStream.Pitch, noteChannel, 0, v, t);
        }

        // ---------------------------------------------------------------------------------------------------------- triggers

        void ServiceQueue(long now)
        {
            if (pending.Count == 0) return;
            var f = accurate ? featAccurate : featFast;
            while (pending.TakeReady(now, f.Lead, f.Length, out var o, out long ready))
            {
                if (!triggersOn && Learning < 0) continue;
                f.Extract(ring, ringMask, o.Sample, feat);
                int learn = Learning;
                if (learn >= 0)
                {
                    LitAt[learn] = total; Lit[learn] = true;
                    trainer.Offer(learn, feat);
                    continue;
                }
                var r = classifier.Classify(feat);
                if (r.Class < 0) { onsetPendingUntil = 0; continue; }
                float sens = V(P.T1Sens + 3 * r.Class);
                float need = 0.6f - 0.5f * sens;
                if (r.Confidence < need) { onsetPendingUntil = 0; continue; }
                Trigger(r.Class, o, Math.Max(ready, o.ReportedAt));
            }
        }

        void Trigger(int cls, in OnsetEvent o, long at)
        {
            int note = I(P.T1Note + 3 * cls);
            int fixedV = I(P.T1Vel + 3 * cls);
            int vel = fixedV > 0 ? fixedV : VelocityMap.FromHit(o.LevelDb, velMin, velSoftDb, velLoudDb);
            // The same note still sounding: off first.
            offs.Play(block, MidiStream.Trigger, trigCh, note, vel, at, trigLenSamples, Math.Min(at, o.Sample));
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

        void ServiceOffs(long now) => offs.Service(block, MidiStream.Trigger, now);

        // ---------------------------------------------------------------------------------------------------------- learning

        /// <summary>Consumer (the machine's pump): the next learned example, or false.</summary>
        public bool TakeLearned(out int cls, float[] features) => trainer.Take(out cls, features);

        /// <summary>Starts a pass for one sound (0 ... 7): onsets in it are filed as that sound's examples until <see cref="LearnTarget"/> or <see cref="StopLearning"/>.</summary>
        public void StartLearning(int sound) => trainer.Start(sound);

        public void StopLearning() => trainer.Stop();

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
            block.Begin(total, 1);
            ReadSettings();
            EndNote(total);
            offs.Flush(block, MidiStream.Trigger, total);
            for (int i = 0; i < 4; i++) laneSent[i] = -1;
            return block.Count;
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
