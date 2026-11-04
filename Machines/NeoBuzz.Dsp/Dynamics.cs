#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): the dynamics Neo Comp built and Neo Multiband
// shares: a log-domain gain computer (threshold, ratio, soft knee; feed-forward or feedback) and the
// detector around it (peak or RMS level in dB, stereo link, attack and release smoothing in dB).
// Moved here from Neo Comp unchanged, operation for operation, so Neo Comp renders as before, bit
// for bit (CompTests pins it).

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>
    /// The static curve: the gain change (dB, positive cuts) for a detector level. A ratio above 1
    /// compresses above the threshold, ∞ limits; a ratio below 1 (Neo Multiband's upward expansion)
    /// gives a negative slope, so the same curve and knee raise the level above the threshold.
    /// </summary>
    internal struct GainComputer
    {
        public double Threshold, Knee, SlopeFf, SlopeFb;
        public bool Feedback;

        /// <param name="ratio">n:1 (∞ for limiting; below 1 expands)</param>
        /// <param name="feedback">The detector hears the output (Neo Comp's FET)</param>
        public static GainComputer Make(double threshold, double ratio, double knee, bool feedback = false) => new GainComputer
        {
            Threshold = threshold,
            Knee = knee,
            SlopeFf = double.IsPositiveInfinity(ratio) ? 1 : 1 - 1 / ratio,
            SlopeFb = double.IsPositiveInfinity(ratio) ? 100 : Math.Min(100, ratio - 1),
            Feedback = feedback,
        };

        /// <summary>Feed-forward: reduction (dB; negative raises) for a detector level.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Ff(double x)
        {
            double over = x - Threshold;
            if (Knee > 1e-6)
            {
                if (2 * over < -Knee) return 0;
                if (2 * over <= Knee) return SlopeFf * (over + Knee / 2) * (over + Knee / 2) / (2 * Knee);
            }
            else if (over <= 0) return 0;
            return SlopeFf * over;
        }

        /// <summary>Feedback: reduction for an output level, and its derivative.</summary>
        public double Fb(double y, out double dc)
        {
            double over = y - Threshold;
            if (Knee > 1e-6)
            {
                if (2 * over < -Knee) { dc = 0; return 0; }
                if (2 * over <= Knee)
                {
                    double t = over + Knee / 2;
                    dc = SlopeFb * t / Knee;
                    return SlopeFb * t * t / (2 * Knee);
                }
            }
            else if (over <= 0) { dc = 0; return 0; }
            dc = SlopeFb;
            return SlopeFb * over;
        }

        /// <summary>The reduction a steady signal at <paramref name="levelDb"/> gets.</summary>
        public double Static(double levelDb)
        {
            if (!Feedback) return Ff(levelDb);
            double g = 0;
            for (int i = 0; i < 40; i++)
            {
                double c = Fb(levelDb - g, out double dc);
                double step = (g - c) / (1 + dc);
                g -= step;
                if (Math.Abs(step) < 1e-10) break;
            }
            return Math.Max(0, g);
        }
    }

    /// <summary>The detector around the gain computer: levels in dB, the stereo link, and the smoothing in dB.</summary>
    internal static class Detector
    {
        /// <summary>RMS reads a sine 3 dB under its peak; +3 dB keeps the threshold on the peak scale.</summary>
        public const double RmsOffsetDb = 3.0103;

        /// <summary>A one-pole coefficient for a time constant: exp(−1 / (t · fs)); 0 for no time.</summary>
        public static double Coef(double seconds, double sampleRate) => seconds <= 0 ? 0 : PortableMath.Exp(-1.0 / (seconds * sampleRate));

        /// <summary>The peak detector: a sample's level in dB.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PeakDb(float x) => DspMath.DbPerNeper * PortableMath.Log(Math.Abs(x) + 1e-15);

        /// <summary>The peak detector in double precision (bands computed in double).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PeakDb(double x) => DspMath.DbPerNeper * PortableMath.Log(Math.Abs(x) + 1e-15);

        /// <summary>The RMS detector's mean square, one sample on (coefficient <paramref name="a"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double MeanSquare(double ms, float x, double a) => x * x + (ms - x * x) * a;

        /// <summary>The RMS detector's level in dB, on the peak scale.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double RmsDb(double ms) => 0.5 * DspMath.DbPerNeper * PortableMath.Log(ms + 1e-30) + RmsOffsetDb;

        /// <summary>Stereo link: each level moves <paramref name="link"/> (0 … 1) of the way to the louder one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Link(ref double a, ref double b, float link)
        {
            double m = Math.Max(a, b);
            a += link * (m - a);
            b += link * (m - b);
        }

        /// <summary>
        /// One step of the smoothing in dB: towards <paramref name="target"/> with the attack
        /// coefficient while the reduction grows, with the release coefficient while it falls.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Smooth(double g, double target, double attackA, double releaseA) =>
            target > g ? target + (g - target) * attackA : target + (g - target) * releaseA;
    }
}
