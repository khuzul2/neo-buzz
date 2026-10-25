// Voice.cs — per-voice state for Pedal M1.
//
// Each voice carries:
//   - Two oscillator slots (OSC1, OSC2), each playing a PCM snapshot at
//     its own phase. PCM looping handled in Render via Pedal Tracker §4.
//   - ADSR amp envelope and ADSR filter envelope.
//   - Lowpass filter (FilterLp2).
//   - Two per-voice LFOs (LFO1, LFO2). Per-voice rather than shared so
//     each voice has its own phase, restarted at note-on.
//   - Anti-click target-gain ramp (CurrentGain → TargetGain) handles
//     buffer-edge glitches. Used in stop-fade and voice steal/retrigger.
//
// Voice is a plain data holder; the machine drives all DSP from Work().

using System;

namespace WDE.PedalM1
{
    public sealed class Voice
    {
        // ── Oscillator slot ────────────────────────────────────────────────
        public sealed class Osc
        {
            public WaveSnapshot Snap;
            public double       Pos;            // sample-domain position into snapshot
            public double       Increment;      // frames per audio-sample
            public bool         Active;
            public bool         BackwardsBidi;  // for bidirectional loops
        }

        public readonly Osc Osc1 = new Osc();
        public readonly Osc Osc2 = new Osc();

        // Pitch (continuous, for portamento).
        public double CurrentMidi  = 60.0;
        public double TargetMidi   = 60.0;

        // Velocity in [0..1].
        public float Velocity = 1f;

        // Envelopes.
        public readonly Envelope AmpEnv    = new Envelope();
        public readonly Envelope FilterEnv = new Envelope();

        // Filter (one per voice — state per voice).
        public readonly FilterLp2 Filter = new FilterLp2();

        // Per-voice LFOs.
        public readonly Lfo Lfo1 = new Lfo();
        public readonly Lfo Lfo2 = new Lfo();

        // Anti-click envelope ramp (Pedal Tracker §4 pattern).
        public float CurrentGain = 0f;
        public float TargetGain  = 0f;
        public const float FADE_STEP = 1f / 64f;   // ~1.3 ms at 48 kHz
        public const float NEAR_ZERO = 1e-4f;

        // Pending pattern events (drained at Work() top, like SH101 §6.3).
        public bool HasNoteOn;
        public byte PendingBuzzNote;     // raw Buzz-byte
        public bool HasNoteOff;
        public bool HasNewVelocity;
        public byte PendingVolumeRaw;    // 0..127 from track Volume column

        // Deferred-trigger machinery (Pedal Tracker §4). When a retrigger
        // arrives on a still-sounding voice, stage it here and fade out;
        // fire when CurrentGain reaches near-zero.
        public bool HasPendingTrigger;
        public byte PendingTriggerNote;

        public bool IsActive
            => AmpEnv.IsActive || CurrentGain > NEAR_ZERO || TargetGain > 0f
            || HasPendingTrigger;

        public void Reset()
        {
            Osc1.Snap = null; Osc1.Active = false; Osc1.Pos = 0; Osc1.BackwardsBidi = false;
            Osc2.Snap = null; Osc2.Active = false; Osc2.Pos = 0; Osc2.BackwardsBidi = false;
            AmpEnv.HardReset();
            FilterEnv.HardReset();
            Filter.Reset();
            CurrentGain = 0f;
            TargetGain  = 0f;
        }

        // Advance one oscillator slot, with PCM loop wrap. Returns the
        // sample value (mono mix-down: L + R if stereo).
        public float TickOsc(Osc o)
        {
            if (!o.Active || o.Snap == null) return 0f;
            var s = o.Snap;

            // Read sample (cubic).
            float vL = Interpolation.Cubic(s.DataL, o.Pos);
            float vR = s.Stereo ? Interpolation.Cubic(s.DataR, o.Pos) : vL;
            float v  = (vL + vR) * 0.5f;

            // Advance.
            if (o.BackwardsBidi)
            {
                o.Pos -= o.Increment;
                if (o.Pos < Interpolation.MAX_PAD + s.LoopStart)
                {
                    o.Pos = Interpolation.MAX_PAD + s.LoopStart
                          + ((Interpolation.MAX_PAD + s.LoopStart) - o.Pos);
                    o.BackwardsBidi = false;
                }
            }
            else
            {
                o.Pos += o.Increment;
                int loopEnd = Interpolation.MAX_PAD + s.LoopEnd;
                if (s.Loop && o.Pos >= loopEnd)
                {
                    if (s.BidiLoop)
                    {
                        o.Pos = loopEnd - (o.Pos - loopEnd);
                        o.BackwardsBidi = true;
                    }
                    else
                    {
                        double span = s.LoopEnd - s.LoopStart;
                        if (span < 1) span = 1;
                        while (o.Pos >= loopEnd) o.Pos -= span;
                    }
                }
                else if (!s.Loop && o.Pos >= Interpolation.MAX_PAD + s.Length)
                {
                    o.Active = false;
                    return v;
                }
            }
            return v;
        }
    }
}
