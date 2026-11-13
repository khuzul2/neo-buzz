#nullable disable
// Neo Step's engine (docs/specs/neo-patch.md §3): a value sequencer. One step per update; the position follows the song's beats while
// it plays and runs free at the tempo when it doesn't. Output 0 … 1. Basic IEEE operations and PortableMath only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Step
{
    internal sealed class StepEngine
    {
        /// <summary>Step lengths in beats for the Rate choices: 1/2 … 1/32, then the dotted ones.</summary>
        public static readonly double[] RateBeats = { 2, 1, 0.5, 0.25, 0.125, 3, 1.5, 0.75, 0.375 };

        readonly ParamValues v;
        Rng rng = new Rng(0x51ED2701u);
        double freeBeats;
        long lastStep = long.MinValue;
        float randomLevel;
        double y;
        bool primed;

        public StepEngine(ParamValues values)
        {
            v = values;
        }

        public int Steps => Math.Clamp((int)v.Value(P.Steps), 1, 32);

        /// <summary>The index (0-based) of the step the last update played.</summary>
        public int Current { get; private set; }

        /// <summary>The level of step <paramref name="i"/> (0-based), 0 … 1.</summary>
        public float Level(int i) => v.F(P.S1 + i);

        /// <summary>The step a position (in steps, fractional) falls in and how far into it (0 … 1), with swing: odd steps start late.</summary>
        public static (long Index, double Frac) Locate(double pos, double swing)
        {
            long pair = (long)Math.Floor(pos / 2);
            double p = pos - pair * 2;                 // 0 … 2 within the pair
            double firstLen = 1 + swing;               // the first step lasts longer, the second shorter
            if (p < firstLen) return (pair * 2, p / firstLen);
            return (pair * 2 + 1, (p - firstLen) / (1 - swing));
        }

        public float Step(double dt, double beats, bool playing, double bpm)
        {
            double stepBeats = RateBeats[Math.Clamp(v.Choice(P.Rate), 0, RateBeats.Length - 1)];
            freeBeats += dt * bpm / 60.0;
            double b = playing ? beats : freeBeats;
            double swing = Math.Clamp(v.Value(P.Swing), 0, 0.75);
            var (index, frac) = Locate(b / stepBeats, swing);
            int n = Steps;
            int slot = (int)(((index % n) + n) % n);
            Current = slot;
            if (index != lastStep)
            {
                lastStep = index;
                randomLevel = rng.NextUnipolar();
            }
            double amount = v.Value(P.Random);
            double target = Level(slot) + (randomLevel - Level(slot)) * amount;

            // Glide: the first part of each step slews from the previous level to this one (a fraction of the step).
            double glide = v.Value(P.Glide);
            if (!primed) { y = target; primed = true; }
            if (glide <= 0) y = target;
            else
            {
                // Over the glide's share of the step the level moves from where it was; later it sits.
                double k = frac >= glide ? 1.0 : frac / glide;
                if (k >= 1) y = target;
                else y += (target - y) * Math.Min(1.0, dt / Math.Max(1e-4, (glide - frac) * stepBeats * 60.0 / bpm) );
            }
            return (float)Math.Clamp(y, 0, 1);
        }

        public double WriteGlide => v.Value(P.WriteGlide);
    }
}
