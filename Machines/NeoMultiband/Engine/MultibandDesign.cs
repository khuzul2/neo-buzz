#nullable disable
// Neo Multiband (docs/specs/machines.md §5): the crossovers' design, which the engine runs, the editor
// draws and the tests measure against.
//  - Each crossover is a digital Linkwitz–Riley (24 dB/oct, −6 dB at its frequency): its low share is
//    L = 1 / (1 + w⁴), its high share H = w⁴ / (1 + w⁴), w = tan(πf/fs) / tan(πfc/fs) (the bilinear
//    transform's warp), so L + H = 1.
//  - The bands are a cascade: band 1 = L1, band 2 = H1·L2, band 3 = H1·H2·L3 (with 3 bands: H1·H2),
//    band 4 = H1·H2·H3. They sum to 1 at every frequency, in either phase mode.
//  - Minimum phase runs it as two cascaded Butterworth sections per share, with allpasses at the
//    higher crossovers on the lower bands; every band then has the same phase (the three allpasses'),
//    so any band gains give the magnitude Σ gain · |band| in both modes.
//  - Linear phase runs the same magnitudes as FIRs of 2D + 1 taps (D samples each side of the centre),
//    designed on a grid twice their length and cut at ±D, the last band the delayed input minus the
//    others, so the bands sum to the delayed input exactly (to float rounding), whatever the FIRs'
//    truncation.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Multiband
{
    internal enum PhaseMode { Linear, Minimum }

    internal enum StereoMode { LeftRight, MidSide }

    /// <summary>The three crossover frequencies as the engine uses them (Hz): ordered, inside 20 Hz … 0.45 fs.</summary>
    internal struct Xover
    {
        public double F1, F2, F3;

        public double this[int i] => i == 0 ? F1 : i == 1 ? F2 : F3;

        public bool Equals(in Xover o) => F1 == o.F1 && F2 == o.F2 && F3 == o.F3;
    }

    internal static class MultibandDesign
    {
        public const int MaxBands = 4, Crossovers = 3;
        public const double MinHz = 20, MaxHz = 20000;

        /// <summary>The linear-phase FIRs: this many partitions of the convolution.</summary>
        public const int Partitions = 16;

        /// <summary>1 up to 50 kHz, 2 up to 100 kHz, then 4: the FIRs keep their length in time.</summary>
        public static int Scale(double sampleRate) => sampleRate <= 50000 ? 1 : sampleRate <= 100000 ? 2 : 4;

        /// <summary>The convolution's partition (and block) length, samples.</summary>
        public static int PartitionLength(double sampleRate) => 512 * Scale(sampleRate);

        /// <summary>D: the FIRs' taps each side of the centre (2D + 1 taps in Partitions · P).</summary>
        public static int HalfLength(double sampleRate) => Partitions * PartitionLength(sampleRate) / 2 - 1;

        /// <summary>Linear phase's latency: the FIRs' centre and one partition of buffering.</summary>
        public static int LinearLatency(double sampleRate) => HalfLength(sampleRate) + PartitionLength(sampleRate);

        /// <summary>The highest crossover, 0.45 fs (20 kHz up to 44.4 kHz).</summary>
        public static double Top(double sampleRate) => Math.Min(MaxHz, 0.45 * sampleRate);

        /// <summary>The crossovers as set, ordered (none below the one before) and inside 20 Hz … 0.45 fs.</summary>
        public static Xover Effective(double x1, double x2, double x3, double sampleRate)
        {
            double top = Top(sampleRate);
            double f1 = Math.Clamp(x1, MinHz, top);
            double f2 = Math.Clamp(Math.Max(x2, f1), MinHz, top);
            double f3 = Math.Clamp(Math.Max(x3, f2), MinHz, top);
            return new Xover { F1 = f1, F2 = f2, F3 = f3 };
        }

        public static Xover Effective(ParamValues v, double sampleRate) =>
            Effective(v.Value(P.Xo1), v.Value(P.Xo2), v.Value(P.Xo3), sampleRate);

        /// <summary>The bilinear warp, tan(πf/fs), below Nyquist.</summary>
        public static double Warp(double hz, double sampleRate) =>
            PortableMath.Tan(Math.PI * Math.Clamp(hz, 0.0, 0.49999 * sampleRate) / sampleRate);

        /// <summary>The low share of a crossover at warped frequency <paramref name="tc"/>, for warped frequency <paramref name="t"/>: 1/(1 + (t/tc)⁴).</summary>
        public static double Low(double t, double tc)
        {
            double r = t / tc;
            r *= r;
            r *= r;
            return 1 / (1 + r);
        }

        /// <summary>The high share, (t/tc)⁴/(1 + (t/tc)⁴) (0 at DC, 1 at Nyquist, where t is ∞).</summary>
        public static double High(double t, double tc)
        {
            double r = t / tc;
            r *= r;
            r *= r;
            return double.IsPositiveInfinity(r) ? 1 : r / (1 + r);
        }

        /// <summary>Band <paramref name="band"/>'s magnitude (0 … 1) at warped frequency <paramref name="t"/>, crossovers warped to t1 … t3.</summary>
        public static double BandAt(int band, int bands, double t, double t1, double t2, double t3)
        {
            switch (band)
            {
                case 0: return Low(t, t1);
                case 1: return High(t, t1) * Low(t, t2);
                case 2: return bands == 4 ? High(t, t1) * High(t, t2) * Low(t, t3) : High(t, t1) * High(t, t2);
                default: return bands == 4 ? High(t, t1) * High(t, t2) * High(t, t3) : 0;
            }
        }

        /// <summary>Band <paramref name="band"/>'s magnitude (0 … 1) at <paramref name="hz"/>, in either phase mode.</summary>
        public static double Band(int band, int bands, double hz, in Xover x, double sampleRate)
        {
            double t = hz >= 0.5 * sampleRate ? double.PositiveInfinity : Warp(hz, sampleRate);
            return BandAt(band, bands, t, Warp(x.F1, sampleRate), Warp(x.F2, sampleRate), Warp(x.F3, sampleRate));
        }

        /// <summary>The machine's magnitude for band gains (linear): Σ gain · |band|, the same in both modes (the bands share one phase).</summary>
        public static double Response(int bands, double hz, in Xover x, double sampleRate, double g0, double g1, double g2, double g3)
        {
            double t = hz >= 0.5 * sampleRate ? double.PositiveInfinity : Warp(hz, sampleRate);
            double t1 = Warp(x.F1, sampleRate), t2 = Warp(x.F2, sampleRate), t3 = Warp(x.F3, sampleRate);
            double r = g0 * BandAt(0, bands, t, t1, t2, t3) + g1 * BandAt(1, bands, t, t1, t2, t3) + g2 * BandAt(2, bands, t, t1, t2, t3);
            if (bands == 4) r += g3 * BandAt(3, bands, t, t1, t2, t3);
            return r;
        }

        /// <summary>The band a frequency falls in (between which crossovers), 0 … bands − 1.</summary>
        public static int BandOf(double hz, int bands, in Xover x)
        {
            if (hz < x.F1) return 0;
            if (hz < x.F2) return 1;
            if (bands == 3 || hz < x.F3) return 2;
            return 3;
        }

        /// <summary>The ratio as the engine takes it: within 1e-9 of 1:1 is exactly 1:1 (the raw value's maths lands a few ulps off).</summary>
        public static double Ratio(double raw) => Math.Abs(raw - 1) < 1e-9 ? 1 : raw;
    }
}
