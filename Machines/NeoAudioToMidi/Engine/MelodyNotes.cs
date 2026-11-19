#nullable disable
// Neo Audio to MIDI (docs/specs/audio-to-midi.md §3): the melody part. One note at a time from a live mono instrument.
//  - An onset (a pluck, a key, a pick) opens a note: the note's pitch is the first clean pitched estimate after it (Fast) or the pitch held for Settle
//    ms (Stable); the velocity is the attack's level. A new onset while a note sounds plays it again (a re-pluck, also of the same pitch), unless
//    Retrigger is off. Two detections of one pluck within 50 ms are one note.
//  - Without an onset (a swell, a soft attack, a bowed or wind note, a synth) a note starts when the pitch is clean and loud enough, as the
//    voice does in Neo Voice to MIDI; a note that ended is not restarted by its own decay: after a note off the level must rise 6 dB above the level
//    it ended at (or an onset must come).
//  - A pitch that moves to another note without an onset (a hammer-on, a pull-off, a slide) changes the note after Stickiness and Settle (a new
//    note on, Legato: before the old one's off); with Pitch bend on, a slide or a vibrato is sent as bend and the note changes only when the pitch
//    is a bend range away.
//  - Fast corrects a wrong first note: within 80 ms of the note, two agreeing estimates of another note retrigger it (or leave it to the bend).
//  - A note ends when the pitch is lost for 25 ms, or the level has been Release dB under the note's peak for 25 ms, after its minimum length.
// Allocation-free. Events go to a MidiBlock (MidiStream.Pitch).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.AudioToMidi
{
    internal sealed class MelodyNotes
    {
        // ---- settings (the engine sets them each block)
        public int Rate = 48000;
        public bool Fast = true, Retrigger = true, Legato;
        public int Octave, Channel;
        public float OnLevel = 0.003f;             // linear RMS under which nothing starts
        public float ReleaseDb = 20f;
        public float StickSemis = 0.25f;
        public int SettleSamples, MinNoteSamples;
        public int ActiveMask = Scales.All;
        public bool FixedVel; public int FixedVelocity = 100, VelMin = 25; public float SoftDb = -50f, LoudDb = -12f;
        public int Window = 912, Hop = 130;       // the tracker's, in samples
        public readonly PitchBendMap Bend = new PitchBendMap();

        // ---- constants (docs/specs/audio-to-midi.md §3.4)
        /// <summary>Fast: the clarity (0 to 1) that lets the first note play alone, or with two agreeing estimates at the lower one, and the longest wait after the first pitched estimate, in ms (Stable: two agreeing estimates at 0.8, over a whole window when a note still sounds).</summary>
        public const float FastConfidence = 0.80f, FastAgreeConfidence = 0.55f, StableConfidence = 0.80f;
        public const double FastWaitMs = 25, StableWaitMs = 40;
        /// <summary>How long a first note may still be corrected, ms.</summary>
        public const double CorrectionMs = 80;
        /// <summary>Fast, over a note that is still sounding: the share of the tracker's window that must be after the onset before an estimate counts (the rest is the old note).</summary>
        public const double FreshShare = 0.5;
        const double ArmTimeoutMs = 150, DoubleOnsetMs = 50, ReleaseHoldMs = 25, StillRingingMs = 120, AfterOffMs = 250, RestartGuardMs = 60;

        readonly MidiBlock block;

        // ---- state
        int cur = -1, sent = -1;                   // the note sounding (before the shift), the number sent
        long onAt, startAt, corrUntil;
        float peak;
        bool armed, armOver;
        long armAt; float armPeak; double prevM; bool prevOk;
        int cand = -1; long candSince, firstSince = -1; float candPeak;
        int switchNote = -1; long switchSince;
        long lastVoicedAt, lowSince = -1;
        bool pendingOff; long pendDue;
        long offAt = long.MinValue / 2; float offLevel;
        float lastRms;
        public int Corrections { get; private set; }
        public int LastVelocity { get; private set; }

        // The notes as played, for the editor's roll (start, end, note), newest at LogHead - 1.
        public const int LogLength = 64;
        public readonly long[] LogStart = new long[LogLength], LogEnd = new long[LogLength];
        public readonly int[] LogNote = new int[LogLength];
        public volatile int LogHead;

        // Tuning (static for the evaluation tool): a note must be within GateDb of the loudest recent one (a string's bleed, a ringing neighbour, are far under it);
        // a note started over one sounding must rise RiseRatio over the level it hears (a beating sustain is not a new pluck).
        public static float GateDb = -16f, RiseRatio = 1.2f;
        float refLevel, endPeak;

        public MelodyNotes(MidiBlock block) { this.block = block; }

        public bool Sounding => cur >= 0;
        public int CurrentNote => cur;
        public bool Busy => cur >= 0 || armed || pendingOff;

        public void Reset()
        {
            cur = -1; sent = -1; armed = false; cand = -1; firstSince = -1; switchNote = -1; pendingOff = false; lowSince = -1;
            offAt = long.MinValue / 2; peak = 0; Bend.ResetAnchor(); Bend.Centre(0, out _);
        }

        float Lin(float db) => (float)PortableMath.Pow(10.0, db / 20.0);

        // ------------------------------------------------------------------------------------------------------------ onsets

        public void Onset(in OnsetEvent o)
        {
            float lvl = Lin(o.LevelDb);
            if (lvl < OnLevel || lvl < refLevel * Lin(GateDb)) return;
            if (cur >= 0 && lvl < lastRms * RiseRatio) return;
            refLevel = Math.Max(refLevel, lvl);
            if (cur >= 0 && o.Sample - startAt < (long)(DoubleOnsetMs * 0.001 * Rate)) return;           // the same pluck, detected twice
            if (armed)
            {
                if (o.Sample - armAt > (long)(0.03 * Rate)) { armAt = o.Sample; armPeak = Math.Max(armPeak, lvl); }
                return;
            }
            if (cur >= 0 && !Retrigger) return;
            armed = true; armAt = o.Sample; armPeak = lvl; armOver = cur >= 0;
            cand = -1; firstSince = -1; prevOk = false;
        }

        // ------------------------------------------------------------------------------------------------------------ pitch estimates

        public void Estimate(in PitchEstimate e)
        {
            long t = e.EndSample;
            lastRms = e.Rms;
            refLevel *= (float)PortableMath.Exp(-Hop / (6.0 * Rate));
            bool onOk = e.Voiced && e.Rms >= OnLevel && e.Rms >= refLevel * Lin(GateDb);
            bool keepOk = e.Voiced && e.Rms >= OnLevel * 0.5f;
            if (cur >= 0) peak = Math.Max(peak * 0.9995f, e.Rms);

            if (armed)
            {
                if (t - armAt > (long)(ArmTimeoutMs * 0.001 * Rate)) armed = false;
                else { Armed(in e, onOk); return; }
            }
            if (cur < 0) Idle(in e, onOk, t);
            else Sounding_(in e, keepOk, t);
        }

        void Armed(in PitchEstimate e, bool onOk)
        {
            long t = e.EndSample;
            long since = t - armAt;
            // The estimate must have heard enough of the new sound: over a note still sounding, half the window or more (Fast), the whole window (Stable); from silence any.
            long need = Fast ? (armOver ? (long)(FreshShare * Window) : 0) : (armOver ? Window : 0);
            if (since < need) return;
            // The tracker's best candidate counts before it is called voiced: the pluck's first periods are clear well before the voicing decision settles.
            if (e.RawF0 <= 0 || e.Rms < OnLevel) { prevOk = false; return; }
            double m = 69 + 12 * PortableMath.Log2(e.RawF0 / 440.0);
            bool agree = prevOk && Math.Abs(m - prevM) < 0.5;
            prevM = m; prevOk = true;
            if (firstSince < 0) firstSince = t;
            long waited = t - firstSince;
            bool go;
            if (Fast) go = e.Confidence >= FastConfidence || (agree && e.Confidence >= FastAgreeConfidence) || (waited >= (long)(FastWaitMs * 0.001 * Rate) && e.Confidence >= 0.4f);
            else go = (agree && e.Confidence >= StableConfidence) || (waited >= (long)(StableWaitMs * 0.001 * Rate) && e.Confidence >= 0.5f);
            if (!go) return;
            Begin(Scales.Nearest(m, ActiveMask), t, armPeak);
            armed = false;
        }

        void Idle(in PitchEstimate e, bool onOk, long t)
        {
            if (!onOk) { cand = -1; firstSince = -1; candPeak = 0; return; }
            // A note's own decay does not start another: the level must have risen over where the last one ended.
            if (t - offAt < (long)(RestartGuardMs * 0.001 * Rate) || e.Rms < offLevel * 2f) return;
            int c = Scales.Nearest(e.Midi, ActiveMask);
            if (cand < 0) { firstSince = t; candSince = t; cand = c; candPeak = e.Rms; }
            candPeak = Math.Max(candPeak, e.Rms);
            if (Fast)
            {
                if (e.Confidence >= 0.93f || t - firstSince >= (long)(0.012 * Rate)) Begin(c, t, candPeak);
            }
            else
            {
                if (c != cand) { cand = c; candSince = t; }
                if (t - candSince >= SettleSamples) Begin(c, t, candPeak);
            }
        }

        void Sounding_(in PitchEstimate e, bool keepOk, long t)
        {
            long hold = (long)(ReleaseHoldMs * 0.001 * Rate);
            // The level has fallen far under the peak: the string has died away into noise.
            float floor = peak * Lin(-ReleaseDb);
            if (e.Rms < floor)
            {
                if (lowSince < 0) lowSince = t;
                if (t - lowSince >= hold) { RequestOff(t); return; }
            }
            else lowSince = -1;
            if (!keepOk)
            {
                // A string still ringing at its level while the voicing flickers (a low note in a short window) is not over.
                long wait = e.Rms >= peak * Lin(-8f) ? (long)(StillRingingMs * 0.001 * Rate) : hold;
                if (t - lastVoicedAt >= wait) RequestOff(t);
                return;
            }
            lastVoicedAt = t; pendingOff = false;
            int c = Scales.Nearest(e.Midi, ActiveMask);
            double dev = e.Midi - cur;
            bool bendOn = Bend.On;
            long hop = Hop;
            if (c != cur)
            {
                bool inCorrection = t < corrUntil;
                bool bendFixes = bendOn && Math.Abs(dev) <= Bend.Range - 0.5;
                if (inCorrection && Fast && !bendFixes)
                {
                    // The first estimate can be another note: two agreeing ones within the window correct it.
                    if (c != switchNote) { switchNote = c; switchSince = t; }
                    else if (t - switchSince >= 2 * hop) { long keep = startAt; Change(c, t, Math.Max(peak, e.Rms), true); startAt = keep; Corrections++; }
                }
                else if (bendOn)
                {
                    // A slide or a bend is the bend's: the note changes when the pitch is a bend range away, and holds there for Settle.
                    if (Math.Abs(dev) >= Bend.Range - 0.25)
                    {
                        if (c != switchNote) { switchNote = c; switchSince = t; }
                        else if (t - switchSince >= SettleSamples) Change(c, t, Math.Max(peak, e.Rms), false);
                    }
                    else switchNote = -1;
                }
                else
                {
                    double advantage = Math.Abs(e.Midi - cur) - Math.Abs(e.Midi - c);
                    if (advantage >= StickSemis)
                    {
                        if (c != switchNote) { switchNote = c; switchSince = t; }
                        else if (t - switchSince >= SettleSamples) Change(c, t, Math.Max(peak, e.Rms), false);
                    }
                    else switchNote = -1;
                }
            }
            else switchNote = -1;
            if (cur >= 0 && bendOn && Bend.Next(e.Midi - cur, t, Rate, out int v)) block.Emit(MidiKind.PitchBend, MidiStream.Pitch, Channel, 0, v, t);
        }

        // ------------------------------------------------------------------------------------------------------------ notes

        int Shift(int note) => Math.Clamp(note + 12 * Octave, 0, 127);
        int Velocity(float peakLin) => VelocityMap.FromPeak(peakLin, FixedVel, FixedVelocity, VelMin, SoftDb, LoudDb);

        void Centre(long t)
        {
            if (Bend.Centre(t, out int v)) block.Emit(MidiKind.PitchBend, MidiStream.Pitch, Channel, 0, v, t);
        }

        void Begin(int note, long t, float peakLin)
        {
            int old = sent;
            bool oldSounding = cur >= 0;
            Centre(t);
            int vel = Velocity(peakLin);
            int n = Shift(note);
            if (oldSounding)
            {
                if (Legato) { block.Emit(MidiKind.NoteOn, MidiStream.Pitch, Channel, n, vel, t); block.Emit(MidiKind.NoteOff, MidiStream.Pitch, Channel, old, 0, t); }
                else { block.Emit(MidiKind.NoteOff, MidiStream.Pitch, Channel, old, 0, t); block.Emit(MidiKind.NoteOn, MidiStream.Pitch, Channel, n, vel, t); }
                LogEndLast(t);
            }
            else block.Emit(MidiKind.NoteOn, MidiStream.Pitch, Channel, n, vel, t);
            cur = note; sent = n; onAt = t; startAt = t; corrUntil = t + (long)(CorrectionMs * 0.001 * Rate);
            peak = Math.Max(peakLin, lastRms); pendingOff = false; cand = -1; firstSince = -1; switchNote = -1; lowSince = -1; lastVoicedAt = t;
            Bend.ResetAnchor();
            LastVelocity = vel;
            int h = LogHead % LogLength;
            LogStart[h] = t; LogEnd[h] = long.MaxValue; LogNote[h] = n;
            LogHead = (LogHead + 1) % (LogLength * 1024);
        }

        void Change(int note, long t, float peakLin, bool correction)
        {
            Begin(note, t, peakLin);
            if (correction) peak = Math.Max(peak, peakLin);
        }

        void LogEndLast(long t)
        {
            int h = ((LogHead - 1) % LogLength + LogLength) % LogLength;
            if (LogEnd[h] == long.MaxValue) LogEnd[h] = t;
        }

        void RequestOff(long t)
        {
            long due = Math.Max(t, onAt + MinNoteSamples);
            if (due <= t) End(t);
            else { pendingOff = true; pendDue = due; }
        }

        /// <summary>A note whose off is due (called each block with the stream position at its end).</summary>
        public void CheckOff(long now)
        {
            if (pendingOff && cur >= 0 && now >= pendDue) End(pendDue);
        }

        public void End(long t)
        {
            if (cur < 0) return;
            block.Emit(MidiKind.NoteOff, MidiStream.Pitch, Channel, sent, 0, t);
            LogEndLast(t);
            offAt = t; offLevel = Math.Max(lastRms, 1e-6f); endPeak = peak;
            cur = -1; sent = -1; pendingOff = false; switchNote = -1; cand = -1; firstSince = -1; lowSince = -1;
            Centre(t);
        }
    }
}
