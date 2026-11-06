// Per-track voice state for Pedal Tracker.
//
// One Voice == one tracker pattern column.  Mono per track, polyphony
// achieved by stacking columns – exactly the Matilde model.
//
// All state is kept inside arrays of fixed length (see PedalTracker.MaxVoices)
// so the audio thread never allocates.

using System.Runtime.CompilerServices;

namespace WDE.PedalTracker
{
    internal sealed class Voice
    {
        // ── Pending parameters delivered by setters, consumed by Work() ──────
        // Cmd / Arg arrays are length 2 — one slot per effect column.
        // Slot 0 is the "first" effect column, slot 1 is the second.
        public const int CMD_SLOTS = 2;

        public byte PendingNote;       // Buzz note byte; 0 = none, 255 = off
        public bool HasNewNote;        // a SetNote was called this tick
        public byte PendingVolume;     // 0..0xFE; 0xFF = no change
        public bool HasNewVolume;
        public byte PendingWave;       // 0..max wave; 0 = no change
        public bool HasNewWave;
        public readonly byte[] PendingCmd = new byte[CMD_SLOTS];
        public readonly byte[] PendingArg = new byte[CMD_SLOTS];
        public readonly bool[] HasNewCmd = new bool[CMD_SLOTS];

        // ── Active playback state ────────────────────────────────────────────
        public WaveSnapshot Snap;      // currently playing wave (null = silent)
        public int   ActiveWaveSlot;   // last triggered slot

        public double Pos;             // sample frame position (sub-sample)
        public double Increment;       // frames per output sample (positive)

        public bool   Active;          // voice is currently producing audio
        public bool   InLoop;          // we've reached the loop region at least once
        public int    PingPongDir;     // +1 forward, -1 reverse (ping-pong)

        public int    LastNoteMidi;    // last triggered MIDI note (for portamento etc)
        public float  Volume;          // 0..1 (combined wave + track volume)

        // ── Effect-command runtime state ─────────────────────────────────────
        // ActiveCmd[0] / ActiveArg[0] = first column, ActiveCmd[1] / ActiveArg[1] = second.
        // Both slots are processed every tick by RunPerTickEffects, so two
        // continuous effects (e.g. vibrato + volume slide) compose naturally.
        public readonly byte[] ActiveCmd = new byte[CMD_SLOTS];
        public readonly byte[] ActiveArg = new byte[CMD_SLOTS];
        public int    EffectSamplesAccum;   // for slides / vibrato

        // ── 04 vibrato state ─────────────────────────────────────────────────
        // BaseIncrement is the un-modulated playback increment.  Each tick the
        // vibrato LFO advances by VibratoSpeed and modulates Increment by
        // ±2^(depth_in_cents/1200).  When 04 stops, Increment is restored to
        // BaseIncrement.
        public double VibratoPhase;     // 0..1, advanced each row
        public double VibratoSpeed;     // phase units per row
        public double VibratoDepthCents; // peak swing in cents
        public double BaseIncrement;    // un-modulated pitch — restored when vibrato ends
        public bool   VibratoActive;

        // ── 03 tone portamento state ─────────────────────────────────────────
        // PortaTargetMidi is the MIDI note we're sliding TOWARD; the audible
        // pitch (CurrentMidi) advances by PortaSpeed semitones per row until
        // it reaches PortaTargetMidi.
        public int    PortaTargetMidi;
        public double CurrentMidi;      // audible pitch in MIDI (sub-semitone OK)
        public double PortaSpeedSemis;  // semitones per row (positive)
        public bool   PortaActive;

        // ── 0E sub-tick scheduling ───────────────────────────────────────────
        // ReBuzz divides each pattern row into MasterInfo.TPB ticks, and each
        // tick into MasterInfo.SamplesPerTick output samples.  These three
        // counters track sample-accurate scheduling for delayed/retriggered
        // events.  Each is decremented every Work() by the buffer length and
        // fires when it reaches zero.
        public int  NoteDelaySamples;     // 0EDx — fire pending trigger when this hits 0
        public int  NoteCutSamples;       // 0ECx — set TargetGain=0 when this hits 0
        public int  RetrigPeriodSamples;  // 0E9x — period in samples
        public int  RetrigCountdown;      // counts down to next retrig fire
        public bool RetrigActive;

        // ── Anti-click envelope ───────────────────────────────────────────────
        // CurrentGain ramps toward TargetGain at FADE_STEP per output sample.
        // Note-on  → TargetGain = 1
        // Note-off → TargetGain = 0  (Active stays true until CurrentGain
        //                              reaches zero, then Render() deactivates)
        public float  CurrentGain;
        public float  TargetGain;

        // ── Deferred retrigger state ─────────────────────────────────────────
        // When a retrigger arrives while the voice is still sounding we
        // capture all the trigger info here, set TargetGain = 0, and let
        // Render() fire the trigger inline once the fade-out reaches zero.
        public bool         HasPendingTrigger;
        public WaveSnapshot PendingSnap;
        public int          PendingMidi;
        public int          PendingPos;     // start-of-playback frame

        public void Reset()
        {
            PendingNote = 0;     HasNewNote   = false;
            PendingVolume = 0xFF; HasNewVolume = false;
            PendingWave = 0;     HasNewWave   = false;
            for (int s = 0; s < CMD_SLOTS; s++)
            {
                PendingCmd[s] = 0; PendingArg[s] = 0; HasNewCmd[s] = false;
                ActiveCmd[s]  = 0; ActiveArg[s]  = 0;
            }

            Snap = null;
            ActiveWaveSlot = 0;
            Pos = 0; Increment = 0;
            Active = false; InLoop = false;
            PingPongDir = 1;
            LastNoteMidi = -1;
            Volume = 1f;

            EffectSamplesAccum = 0;

            VibratoPhase = 0; VibratoSpeed = 0; VibratoDepthCents = 0;
            BaseIncrement = 0; VibratoActive = false;

            PortaTargetMidi = 0; CurrentMidi = 0;
            PortaSpeedSemis = 0; PortaActive = false;

            NoteDelaySamples = 0; NoteCutSamples = 0;
            RetrigPeriodSamples = 0; RetrigCountdown = 0;
            RetrigActive = false;

            CurrentGain = 0f; TargetGain = 0f;
            HasPendingTrigger = false;
            PendingSnap = null;
            PendingMidi = 0; PendingPos = 0;
        }
    }

    /// <summary>
    /// The per-sample render loop for a single voice.  Static so the JIT
    /// can inline it cleanly; takes the engine sample rate so the same
    /// kernel works at 44.1 / 48 / 88.2 / 96 / 192 kHz transparently.
    /// </summary>
    internal static class VoiceRender
    {
        /// <summary>
        /// Render `n` stereo samples from voice `v`, mixing into outL/outR.
        /// outL/outR are the L and R fields of the host's Sample[] buffer
        /// flattened to two parallel arrays for inner-loop simplicity; the
        /// caller copies them back into Sample[] after all voices are mixed.
        ///
        /// Anti-click envelope:
        ///   - CurrentGain ramps toward TargetGain at FADE_STEP per sample.
        ///   - When CurrentGain hits zero with TargetGain == 0:
        ///       * if HasPendingTrigger → fire it inline (swap snapshot,
        ///         reset position, set TargetGain back to 1, fade in).
        ///       * otherwise → deactivate the voice.
        /// </summary>
        const float FADE_STEP = 1f / 64f;     // ~1.5 ms at 44.1 kHz, ~0.7 ms at 96 kHz
        const float NEAR_ZERO = 1e-4f;

        // Optional trace hook for debugging pitch-effect / deferred-trigger
        // interactions.  Set by PedalTrackerMachine at construction time;
        // null-safe so it's free when not enabled.
        public static System.Action<string> Trace;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fork (neo-buzz): never compiled on the audio thread
        public static void Render(Voice v, float[] outL, float[] outR, int n,
                                  Interp interp, double engineSampleRate, float masterGain)
        {
            if (!v.Active || v.Snap == null) return;
            if (v.Increment <= 0) return;

            // ── Local copies of voice state (hot-loop locals) ─────────────────
            var snap = v.Snap;
            float[] sL = snap.L;
            float[] sR = snap.R;
            int padHead   = snap.PadHead;
            int loopStart = snap.LoopStart + padHead;
            int loopEnd   = snap.LoopEnd   + padHead;
            int trackEnd  = snap.Length    + padHead;
            bool loop     = snap.Looped && loopEnd > loopStart;
            bool ping     = loop && snap.PingPong;

            double pos = v.Pos + padHead;
            double inc = v.Increment;
            int    dir = v.PingPongDir;
            float  amp = v.Volume * snap.Volume * masterGain;

            float curGain = v.CurrentGain;
            float tgtGain = v.TargetGain;

            // ── Interpolation bypass when sample-rate-conversion is identity ──
            // When inc == 1.0 (the wave is being played at exactly its native
            // rate), the read position advances by exactly 1 per sample, frac
            // stays 0, and EVERY interpolator (Linear / Cubic / B-Spline /
            // Sinc16 / Sinc32) reduces mathematically to src[i0].  The strict
            // eligibility check (no active slides, integer Pos) guarantees
            // this for the whole block — once integer pos starts integer-stepping
            // by 1, it stays integer.  Forcing the local switch dispatch to
            // Interp.None for this block elides 16-32 multiply-adds per output
            // sample on Sinc paths with bit-identical output.  Cost is zero
            // when bypass doesn't apply: one comparison, one assignment.
            Interp effectiveInterp =
                (inc == 1.0
                 && !v.PortaActive
                 && !v.VibratoActive
                 && pos == (int)pos)
                ? Interp.None
                : interp;

            for (int i = 0; i < n; i++)
            {
                // ── Envelope ramp ─────────────────────────────────────────────
                if (curGain != tgtGain)
                {
                    if (curGain < tgtGain)
                    {
                        curGain += FADE_STEP;
                        if (curGain > tgtGain) curGain = tgtGain;
                    }
                    else
                    {
                        curGain -= FADE_STEP;
                        if (curGain < tgtGain) curGain = tgtGain;
                    }
                }

                // ── Fade-out reached zero?  Fire deferred trigger or stop ────
                if (curGain <= NEAR_ZERO && tgtGain == 0f)
                {
                    if (v.HasPendingTrigger && v.PendingSnap != null)
                    {
                        Trace?.Invoke($"FIREdef midi {v.CurrentMidi:F2}->{v.PendingMidi:F2} (was active gain ramp ended)");
                        // Inline-fire the deferred trigger.
                        v.Snap          = v.PendingSnap;
                        v.LastNoteMidi  = v.PendingMidi;
                        v.CurrentMidi   = v.PendingMidi;       // re-seat for porta/vibrato
                        v.Pos           = v.PendingPos;
                        v.PingPongDir   = 1;
                        v.InLoop        = false;
                        v.Increment     = IncrementFor(v.PendingMidi, v.PendingSnap, engineSampleRate);
                        v.BaseIncrement = v.Increment;          // un-modulated base for vibrato

                        v.HasPendingTrigger = false;
                        v.PendingSnap       = null;
                        v.PortaActive       = false;
                        v.VibratoActive     = false;

                        // Refresh local hot vars from new snapshot.
                        snap = v.Snap;
                        sL = snap.L; sR = snap.R;
                        padHead   = snap.PadHead;
                        loopStart = snap.LoopStart + padHead;
                        loopEnd   = snap.LoopEnd   + padHead;
                        trackEnd  = snap.Length    + padHead;
                        loop      = snap.Looped && loopEnd > loopStart;
                        ping      = loop && snap.PingPong;
                        pos       = v.Pos + padHead;
                        inc       = v.Increment;
                        dir       = v.PingPongDir;
                        amp       = v.Volume * snap.Volume * masterGain;

                        // Re-evaluate bypass for the new wave/note.  The
                        // PortaActive/VibratoActive flags were just cleared
                        // above, so we only need to recheck inc and pos.
                        effectiveInterp =
                            (inc == 1.0 && pos == (int)pos)
                            ? Interp.None
                            : interp;

                        curGain = 0f;
                        tgtGain = 1f;
                    }
                    else
                    {
                        // Genuine note-off / end-of-sample → deactivate.
                        v.Active = false;
                        curGain  = 0f;
                        break;
                    }
                }

                // ── End-of-sample / loop handling ─────────────────────────────
                if (!loop)
                {
                    if (pos >= trackEnd)
                    {
                        // Wave ended without note-off – start fading out.
                        // The next iteration's "fade-out reached zero" branch
                        // either fires a pending trigger or deactivates.
                        if (tgtGain != 0f) tgtGain = 0f;
                        // Hold position so we don't read out of bounds.
                        pos = trackEnd - 1;
                    }
                }
                else if (!ping)
                {
                    while (pos >= loopEnd) pos -= (loopEnd - loopStart);
                    v.InLoop = true;
                }
                else
                {
                    if (dir > 0 && pos >= loopEnd)
                    {
                        pos = loopEnd - (pos - loopEnd);
                        dir = -1;
                    }
                    else if (dir < 0 && pos <= loopStart)
                    {
                        pos = loopStart + (loopStart - pos);
                        dir = 1;
                    }
                    v.InLoop = true;
                }

                // ── Interpolation ─────────────────────────────────────────────
                int    i0   = (int)pos;
                float  frac = (float)(pos - i0);

                float l, r;
                switch (effectiveInterp)
                {
                    case Interp.None:
                        l = Interpolation.None(sL, i0);
                        r = Interpolation.None(sR, i0);
                        break;
                    case Interp.Linear:
                        l = Interpolation.Linear(sL, i0, frac);
                        r = Interpolation.Linear(sR, i0, frac);
                        break;
                    case Interp.Cubic:
                        l = Interpolation.Cubic(sL, i0, frac);
                        r = Interpolation.Cubic(sR, i0, frac);
                        break;
                    case Interp.BSpline:
                        l = Interpolation.BSpline(sL, i0, frac);
                        r = Interpolation.BSpline(sR, i0, frac);
                        break;
                    case Interp.Sinc16:
                        l = Interpolation.Sinc16(sL, i0, frac);
                        r = Interpolation.Sinc16(sR, i0, frac);
                        break;
                    default: // Sinc32
                        l = Interpolation.Sinc32(sL, i0, frac);
                        r = Interpolation.Sinc32(sR, i0, frac);
                        break;
                }

                outL[i] += l * amp * curGain;
                outR[i] += r * amp * curGain;

                pos += inc * dir;
            }

            // ── Write back state ─────────────────────────────────────────────
            v.Pos         = pos - padHead;
            v.PingPongDir = dir;
            v.CurrentGain = curGain;
            v.TargetGain  = tgtGain;
        }

        /// <summary>
        /// Compute the per-output-sample increment for a given MIDI note,
        /// taking into account the wave's root note, source rate and the
        /// engine's current rate.  All three rates can differ – e.g. a
        /// 96 kHz source playing at C-5 (one octave up) on a 48 kHz engine
        /// gives an increment of (96000 / 48000) × 2 = 4.0.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double IncrementFor(int midiNote, WaveSnapshot snap, double engineRate)
        {
            // Ratio of pitches: 2^((note - root) / 12)
            int    noteDelta = midiNote - snap.RootNote;
            double pitchRatio = System.Math.Pow(2.0, noteDelta / 12.0);

            // Sample-rate ratio: source frames per output frame
            double srRatio = (double)snap.SampleRate / engineRate;

            return pitchRatio * srRatio;
        }

        /// <summary>
        /// Sub-semitone variant — used by portamento (which slides through
        /// fractional MIDI) and vibrato (which modulates around an integer).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double IncrementFor(double midiNote, WaveSnapshot snap, double engineRate)
        {
            double noteDelta  = midiNote - snap.RootNote;
            double pitchRatio = System.Math.Pow(2.0, noteDelta / 12.0);
            double srRatio    = (double)snap.SampleRate / engineRate;
            return pitchRatio * srRatio;
        }
    }
}
