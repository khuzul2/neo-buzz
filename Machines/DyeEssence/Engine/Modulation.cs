#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.5): modulation. Two DAHDSR envelopes with a curve per
// segment, four LFOs (per note or one free), four macros and sixteen routes, each a source, a
// destination and an amount, evaluated per note on the control grid (every 16 samples, the
// destinations ramping across the tick as every control value does). Envelopes and LFOs advance in
// closed form per tick from portable maths, so the same bits everywhere and any block size.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>A route's source (append only).</summary>
    internal enum RouteSource { Off, ModEnv1, ModEnv2, Lfo1, Lfo2, Lfo3, Lfo4, AmpEnv, FilterEnv, Velocity, Key, ModWheel, Aftertouch, Macro1, Macro2, Macro3, Macro4, Random, Count }

    /// <summary>A route's destination (append only).</summary>
    internal enum RouteDest
    {
        Off, Pitch, APitch, BPitch, CPitch, APosition, BPosition, CPosition, AWarp1, AWarp2, BWarp1, BWarp2, CWarp1, CWarp2,
        ALevel, BLevel, CLevel, SubLevel, NoiseLevel, F1Cutoff, F2Cutoff, F1Reso, F2Reso, Amp, Lfo1Rate, Lfo2Rate, Lfo3Rate, Lfo4Rate, Count,
    }

    internal enum WaveShape { Sine, Triangle, SawUp, SawDown, Square, SampleHold, Smooth }

    internal sealed class ModEnvSettings
    {
        /// <summary>Seconds (beats when <see cref="Sync"/>); sustain 0 … 1; curves −1 … 1 (0 straight).</summary>
        public float Delay, A = 0.01f, Hold, D = 0.3f, S, R = 0.3f, ACurve, DCurve = -0.6f, RCurve = -0.6f;
        public bool Sync;
    }

    internal sealed class LfoSettings
    {
        public WaveShape Shape;
        /// <summary>Hz; 0: synced by <see cref="Beats"/>.</summary>
        public float Rate = 1;
        /// <summary>The synced period in beats (0: free).</summary>
        public float Beats;
        public bool Retrig = true;
        /// <summary>Start phase in cycles; fade-in seconds.</summary>
        public float Phase, Fade;
    }

    internal sealed class RouteSettings
    {
        public RouteSource Source;
        public RouteDest Dest;
        public float Amount;
    }

    internal static class ModEnvelope
    {
        public const int Idle = 0, Delay = 1, Attack = 2, Hold = 3, Decay = 4, Sustain = 5, Release = 6;

        /// <summary>A segment's shape: 0 → 0, 1 → 1; curve 0 straight, below 0 a fast start, above a slow one.</summary>
        public static double Shape(double x, float curve)
        {
            x = Math.Clamp(x, 0.0, 1.0);
            if (curve > -0.001f && curve < 0.001f) return x;
            double k = 6.0 * curve;
            return PortableMath.Expm1(k * x) / PortableMath.Expm1(k);
        }

        /// <summary>
        /// Advances a note's envelope by <paramref name="dt"/> seconds (<paramref name="beat"/>: seconds
        /// a beat, for a synced one); returns its level. <paramref name="time"/> is the time in the
        /// stage; <paramref name="from"/> the level the release started at.
        /// </summary>
        public static float Advance(ref int stage, ref double time, ref float level, ref float from, bool gate, ModEnvSettings e, double dt, double beat)
        {
            double scale = e.Sync ? beat : 1.0;
            if (!gate && stage != Idle && stage != Release)
            {
                stage = Release;
                time = 0;
                from = level;
            }
            time += dt;
            while (true)
            {
                double length = stage switch
                {
                    Delay => e.Delay * scale,
                    Attack => e.A * scale,
                    Hold => e.Hold * scale,
                    Decay => e.D * scale,
                    Release => e.R * scale,
                    _ => double.PositiveInfinity,
                };
                if (time < length) break;
                if (stage == Idle || stage == Sustain) break;
                time -= length;
                if (stage == Release)
                {
                    stage = Idle;
                    time = 0;
                    break;
                }
                stage++;
            }
            double s = Math.Clamp(e.S, 0f, 1f), l;
            switch (stage)
            {
                case Delay: l = 0; break;
                case Attack: l = Shape(time / Math.Max(1e-9, e.A * scale), e.ACurve); break;
                case Hold: l = 1; break;
                case Decay: l = 1 + (s - 1) * Shape(time / Math.Max(1e-9, e.D * scale), e.DCurve); break;
                case Sustain: l = s; break;
                case Release: l = from * (1 - Shape(time / Math.Max(1e-9, e.R * scale), e.RCurve)); break;
                default: l = 0; break;
            }
            level = (float)l;
            return level;
        }
    }

    internal static class LfoWave
    {
        /// <summary>
        /// The value (−1 … 1) at <paramref name="phase"/> (cycles, 0 … 1) for the shapes without memory;
        /// S&amp;H and Smooth use <paramref name="held"/> and <paramref name="next"/> (the random values
        /// either side of this cycle).
        /// </summary>
        public static float Value(WaveShape shape, double phase, float held, float next)
        {
            switch (shape)
            {
                case WaveShape.Sine: return (float)PortableMath.Sin(2 * Math.PI * phase);
                case WaveShape.Triangle: return (float)(phase < 0.25 ? 4 * phase : phase < 0.75 ? 2 - 4 * phase : 4 * phase - 4);
                case WaveShape.SawUp: return (float)(2 * phase - 1);
                case WaveShape.SawDown: return (float)(1 - 2 * phase);
                case WaveShape.Square: return phase < 0.5 ? 1f : -1f;
                case WaveShape.SampleHold: return held;
                default:
                {
                    // A raised-cosine glide from one random value to the next across the cycle.
                    double w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * phase);
                    return (float)(held + (next - held) * w);
                }
            }
        }
    }
}
