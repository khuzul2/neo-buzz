#nullable disable
// Neo LFO's engine (docs/specs/neo-patch.md §3): the wave, from the parameters, one step per update. Pure: the machine gives it the
// elapsed time, the song position and whether the transport plays. Output 0 … 1 (the wave's −1 … 1 mapped up). Basic IEEE
// operations and PortableMath only, so every platform writes the same values.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.LfoMachine
{
    internal sealed class LfoEngine
    {
        /// <summary>The synced lengths in beats (four to the bar): 8 bars … 1/64.</summary>
        public static readonly double[] SyncBeats = { 0, 32, 16, 8, 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };

        readonly ParamValues v;
        Rng rng = new Rng(0x1F2E3D4Cu);
        double phase;
        double lastBeats = double.NaN;
        long cycle = long.MinValue;
        float prevHeld, held, nextHeld;
        double smoothed;
        bool primed;
        bool noteRestart;

        public LfoEngine(ParamValues values)
        {
            v = values;
            held = rng.NextBipolar();
            nextHeld = rng.NextBipolar();
        }

        /// <summary>The cycle length in beats for the Sync and Feel settings (0: Free).</summary>
        public double CycleBeats()
        {
            double b = SyncBeats[Math.Clamp(v.Choice(P.Sync), 0, SyncBeats.Length - 1)];
            if (b <= 0) return 0;
            int feel = v.Choice(P.Feel);
            return feel == 1 ? b * 1.5 : feel == 2 ? b * (2.0 / 3.0) : b;
        }

        /// <summary>A note on the listened machine (Retrigger: Note).</summary>
        public void Note() => noteRestart = true;

        /// <summary>The wave's value at a phase (0 … 1) of a cycle, −1 … 1.</summary>
        public float Shape(int shape, double p)
        {
            float x = (float)p;
            switch (shape)
            {
                case 0: return PortableMathF.Sin(DspMath.TwoPi * x);
                case 1: return x < 0.25f ? 4 * x : x < 0.75f ? 2 - 4 * x : 4 * x - 4;
                case 2: return 2 * x - 1;
                case 3: return 1 - 2 * x;
                case 4: return x < 0.5f ? 1 : -1;
                case 5:
                {
                    float t = 0.5f - 0.5f * PortableMathF.Cos(DspMath.Pi * x);
                    return prevHeld + (held - prevHeld) * t;
                }
                default: return held;
            }
        }

        /// <summary>Advances <paramref name="dt"/> seconds (the song at <paramref name="beats"/> beats, <paramref name="bpm"/>), returning 0 … 1.</summary>
        public float Step(double dt, double beats, bool playing, double bpm)
        {
            double cyc = CycleBeats();
            int retrigger = v.Choice(P.Retrigger);
            double offset = v.F(P.Phase) / 360.0;
            double rate = v.Value(P.Rate);

            if (playing && !double.IsNaN(lastBeats))
            {
                // The transport's own beats and bars restart a Beat or Bar retriggered wave.
                double bar = 4;
                bool crossed = retrigger == 1 ? Math.Floor(beats) != Math.Floor(lastBeats) : retrigger == 2 && Math.Floor(beats / bar) != Math.Floor(lastBeats / bar);
                if (crossed) { phase = 0; }
            }
            if (noteRestart && retrigger == 3) phase = 0;
            noteRestart = false;
            lastBeats = playing ? beats : double.NaN;

            if (cyc > 0 && playing && retrigger == 0)
                phase = beats / cyc;                              // locked to the song
            else if (cyc > 0)
                phase += dt * (bpm / 60.0) / cyc;                 // synced, running free of the song
            else
                phase += dt * rate;

            double total = phase + offset;
            long c = (long)Math.Floor(total);
            if (c != cycle)
            {
                if (cycle != long.MinValue)
                {
                    prevHeld = held;
                    held = nextHeld;
                    nextHeld = rng.NextBipolar();
                }
                cycle = c;
            }
            double p = total - c;
            float value = Shape(v.Choice(P.Shape), p);
            double u = (value + 1.0) * 0.5;

            double smooth = v.Value(P.Smooth);
            if (!primed) { smoothed = u; primed = true; }
            if (smooth > 0)
            {
                double tau = smooth * 0.5;
                smoothed += (u - smoothed) * (1.0 - PortableMath.Exp(-dt / tau));
            }
            else smoothed = u;
            return (float)smoothed;
        }

        /// <summary>Seconds a write may take to cross a whole range.</summary>
        public double Glide => v.Value(P.Glide);
    }
}
