#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.2): the note envelopes, advanced in closed form once per
// control tick instead of sample by sample. The shapes are NeoBuzz.Dsp.Adsr's: an RC attack aimed
// at 1.5 (so it reaches 1 in the attack time), an exponential decay to the sustain level and an
// exponential release, both 60 dB in their times, idle below −100 dB. A tick's level is where that
// curve is at the tick's end; the amp ramps to it across the tick. Portable maths (the same bits on
// every platform), one exp per stage per tick shared by every note.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class EnvSettings
    {
        /// <summary>Seconds; sustain 0 … 1.</summary>
        public float A = 0.002f, D = 0.3f, S = 1f, R = 0.2f;
    }

    internal static class NoteEnvelope
    {
        public const int Idle = 0, Attack = 1, Decay = 2, Release = 3;
        const double Target = 1.5;
        const float Instant = 0.0002f;
        const float IdleLevel = 1e-5f;
        static readonly double AttackLn = PortableMath.Log(Target / (Target - 1.0));
        static readonly double SixtyDb = PortableMath.Log(1000.0);

        /// <summary>The fraction of each stage's distance left after <paramref name="dt"/> seconds.</summary>
        public static void Factors(EnvSettings e, double dt, out double ka, out double kd, out double kr)
        {
            ka = Factor(e.A, AttackLn, dt);
            kd = Factor(e.D, SixtyDb, dt);
            kr = Factor(e.R, SixtyDb, dt);
        }

        static double Factor(float time, double ln, double dt) => time <= Instant ? 0.0 : PortableMath.Exp(-ln * dt / time);

        /// <summary>
        /// Advances one note's envelope by <paramref name="dt"/> seconds, given that span's
        /// <see cref="Factors"/>; returns its level at the end. A gate that has closed starts the release.
        /// </summary>
        public static float Advance(ref int stage, ref float level, bool gate, EnvSettings e, double dt, double ka, double kd, double kr)
        {
            if (!gate && (stage == Attack || stage == Decay)) stage = Release;
            double l = level, s = Math.Clamp(e.S, 0f, 1f);
            switch (stage)
            {
                case Attack:
                {
                    double rest = (Target - l) * ka;
                    if (Target - rest < 1.0)
                    {
                        l = Target - rest;
                        break;
                    }
                    // It reaches 1 inside the tick: the rest of the tick decays.
                    double ta = e.A <= Instant ? 0.0 : e.A / AttackLn * PortableMath.Log((Target - l) / (Target - 1.0));
                    double left = Math.Max(0.0, dt - ta);
                    stage = Decay;
                    l = s + (1.0 - s) * Factor(e.D, SixtyDb, left);
                    break;
                }
                case Decay:
                    l = s + (l - s) * kd;
                    break;
                case Release:
                    l *= kr;
                    if (l < IdleLevel)
                    {
                        l = 0;
                        stage = Idle;
                    }
                    break;
                default:
                    l = 0;
                    break;
            }
            level = (float)l;
            return level;
        }
    }
}
