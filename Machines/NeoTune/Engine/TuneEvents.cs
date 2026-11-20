#nullable disable
// Neo Tune's evaluation mode (docs/specs/neo-tune.md section 12): the engine can log its own events (splices, method switches, target changes, ratio steps,
// transient pass-through, analysis-mark runs and outliers, voicing) so that a click found in the audio by the artefact detector (tools/PitchEval/TuneArtefacts.cs)
// can be tied to its cause. Off by default: the audio path then does one null test per event site and nothing else. The log is a fixed array
// allocated by EnableEventLog (never from the audio thread); when it is full the newest events are counted as dropped.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal enum TuneEventKind
    {
        SpliceJump,        // the reader jumped one period (A = jump in samples, B = offset before the jump, C = mismatch of the two segments in dB, D = crossfade length)
        SpliceReturn,      // the reader returned to the nominal position (A = offset, B = crossfade length, C = mismatch dB, D = 1 when voiced)
        MethodSwitch,      // Splice <-> PSOLA (A = 1 when splice is now in use)
        TargetChange,      // the note target changed (A = old note, B = new note, C = dwell s, D = centre cents from the new note)
        ShiftStep,         // the shift between two tracker estimates changed by more than 12 ct at once (A = before, B = after, semitones)
        TransientOn,       // the PSOLA dry hold began
        TransientOff,      // the PSOLA dry hold ended
        MarkRunStart,      // a new run of analysis marks
        MarkRunEnd,
        MarkOutlier,       // a mark spacing more than 12 % away from the one before (A = spacing, B = previous spacing)
        PsolaBypassOn,     // PSOLA output switched to the aligned input (shift exactly 0)
        PsolaBypassOff,
        VoicedStart,       // a run began (the tracker went voiced after an unvoiced stretch)
        VoicedEnd,         // the shift ramp to 1 began (a real unvoiced sound or silence)
        PitchHold,         // a short gap in voicing was bridged with the last pitch and shift
        Trace,             // every 256 output samples while the splice engine runs (A = shift in semitones, B = reader offset in samples, C = 1 when a crossfade is running)
    }

    internal struct TuneEvent
    {
        /// <summary>The input time (samples, the time of the audio the event concerns; the output is aligned to it).</summary>
        public long Sample;
        public TuneEventKind Kind;
        public double A, B, C, D;
    }

    internal sealed partial class TuneEngine
    {
        TuneEvent[] evLog;
        int evCount;
        long evDropped;
        bool evOn;

        /// <summary>Starts logging events into a fixed array of <paramref name="capacity"/> entries (not for the audio thread).</summary>
        public void EnableEventLog(int capacity = 1 << 16)
        {
            evLog = new TuneEvent[Math.Max(16, capacity)];
            evCount = 0; evDropped = 0; evOn = true;
        }

        public void DisableEventLog() { evOn = false; }

        /// <summary>The events logged so far, oldest first (a copy), and how many were dropped for want of room.</summary>
        public TuneEvent[] Events(out long dropped)
        {
            dropped = evDropped;
            if (evLog == null) return Array.Empty<TuneEvent>();
            var a = new TuneEvent[evCount];
            Array.Copy(evLog, a, evCount);
            return a;
        }

        void Log(long sample, TuneEventKind kind, double a = 0, double b = 0, double c = 0, double d = 0)
        {
            if (evCount >= evLog.Length) { evDropped++; return; }
            ref var e = ref evLog[evCount++];
            e.Sample = sample; e.Kind = kind; e.A = a; e.B = b; e.C = c; e.D = d;
        }

        /// <summary>The mismatch of two readings of the input, 10 log10 of the energy of their difference over the energy of the first, over len samples from two positions (logging only).</summary>
        double SegmentMismatch(double ra, double rb, int len)
        {
            double d = 0, e = 1e-12;
            for (int k = 0; k < len; k++)
            {
                float a = Interp(ra + k), b = Interp(rb + k);
                d += (double)(a - b) * (a - b);
                e += (double)a * a;
            }
            return 10 * PortableMath.Log10(d / e + 1e-12);
        }
    }
}
