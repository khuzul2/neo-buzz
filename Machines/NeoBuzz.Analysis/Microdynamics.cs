#nullable disable
// Neo analysis library (docs/specs/mastering-tools.md §6): microdynamics, after the LDR Ozone's Impact
// and Master Assistant read: how far the level rises above its own sustained level, in four bands split
// at 120 Hz, 2 kHz and 10 kHz (Ozone's width bands too). Each band (Linkwitz–Riley 24 dB/oct crossovers
// on the mono sum) gives a short level (the RMS of 20 ms frames) and a long one (the frames' mean
// square over the second around each); a frame's LDR is the short over the long in dB. The band's LDR is
// the 95th percentile over the frames that sound (the long level within 30 dB of the band's loudest and
// above −90 dBFS), so a quiet section after a loud one doesn't change it, and its median is reported too.
// Punchy, transient material reads high; limiting and heavy compression bring it down. The values are
// relative: compare a master with its reference or with a style target measured the same way.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class MicrodynamicsBand
    {
        /// <summary>below_120, 120_2k, 2k_10k, above_10k.</summary>
        public string Name;
        public double Lo, Hi;
        /// <summary>The band's long-term level, dB RMS (its frames' mean square).</summary>
        public double LevelDb;
        /// <summary>The 95th percentile of the short level over the long one, dB; NaN when the band never sounds.</summary>
        public double LdrDb;
        /// <summary>The median of the same, dB.</summary>
        public double MedianDb;
    }

    internal sealed class MicrodynamicsResult
    {
        public MicrodynamicsBand[] Bands;
        /// <summary>The same over the whole band.</summary>
        public double LdrDb, MedianDb;
        public double ShortSeconds, LongSeconds;
    }

    internal static class Microdynamics
    {
        /// <summary>The four bands (Ozone's): split at 120 Hz, 2 kHz and 10 kHz.</summary>
        public static (string Name, double Lo, double Hi)[] Bands => NeoBuzz.Analysis.Bands.Mastering;

        public const double ShortSeconds = 0.02, LongSeconds = 1.0, Percentile = 0.95, GateDb = 30;

        public static MicrodynamicsResult Measure(float[] mono, int rate)
        {
            var result = new MicrodynamicsResult { ShortSeconds = ShortSeconds, LongSeconds = LongSeconds, Bands = new MicrodynamicsBand[Bands.Length] };
            for (int b = 0; b < Bands.Length; b++)
            {
                var (name, lo, hi) = Bands[b];
                var band = Filter(mono, rate, lo, hi);
                var (level, ldr, median) = Ldr(band, rate);
                result.Bands[b] = new MicrodynamicsBand { Name = name, Lo = lo, Hi = hi, LevelDb = level, LdrDb = ldr, MedianDb = median };
            }
            var all = new double[mono.Length];
            for (int i = 0; i < mono.Length; i++) all[i] = mono[i];
            (_, result.LdrDb, result.MedianDb) = Ldr(all, rate);
            return result;
        }

        /// <summary>The band between <paramref name="lo"/> and <paramref name="hi"/> Hz (0 and ∞: open): Linkwitz–Riley 24 dB/oct edges.</summary>
        internal static double[] Filter(float[] x, int rate, double lo, double hi)
        {
            var y = new double[x.Length];
            for (int i = 0; i < x.Length; i++) y[i] = x[i];
            if (lo > 0 && lo < 0.45 * rate)
                for (int k = 0; k < 2; k++) Biquad(y, rate, lo, highPass: true);
            if (!double.IsInfinity(hi) && hi < 0.45 * rate)
                for (int k = 0; k < 2; k++) Biquad(y, rate, hi, highPass: false);
            return y;
        }

        /// <summary>A Butterworth (Q 0.7071) section in place, RBJ cookbook, direct form II transposed.</summary>
        static void Biquad(double[] y, int rate, double hz, bool highPass)
        {
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            double a0 = 1 + alpha;
            double b0 = highPass ? (1 + cos) / 2 : (1 - cos) / 2, b1 = highPass ? -(1 + cos) : 1 - cos, b2 = b0;
            double a1 = -2 * cos, a2 = 1 - alpha;
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
            double z1 = 0, z2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double x = y[i], o = b0 * x + z1;
                z1 = b1 * x - a1 * o + z2;
                z2 = b2 * x - a2 * o;
                y[i] = o;
            }
        }

        /// <summary>(long-term level dB, LDR P95 dB, LDR median dB) of a signal.</summary>
        internal static (double LevelDb, double Ldr, double Median) Ldr(double[] x, int rate)
        {
            int frame = Math.Max(1, (int)Math.Round(ShortSeconds * rate));
            int frames = x.Length / frame;
            if (frames == 0) return (double.NegativeInfinity, double.NaN, double.NaN);
            var ms = new double[frames];
            double total = 0;
            for (int f = 0; f < frames; f++)
            {
                double s = 0;
                for (int i = f * frame, end = i + frame; i < end; i++) s += x[i] * x[i];
                ms[f] = s / frame;
                total += ms[f];
            }
            double level = total > 0 ? 10 * Math.Log10(total / frames) : double.NegativeInfinity;
            // The long level: the frames' mean square over the second around each (shorter at the ends).
            int half = Math.Max(1, (int)Math.Round(LongSeconds / ShortSeconds / 2));
            var prefix = new double[frames + 1];
            for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + ms[f];
            var longMs = new double[frames];
            double loudest = 0;
            for (int f = 0; f < frames; f++)
            {
                int a = Math.Max(0, f - half), b = Math.Min(frames, f + half + 1);
                longMs[f] = (prefix[b] - prefix[a]) / (b - a);
                loudest = Math.Max(loudest, longMs[f]);
            }
            double gate = Math.Max(1e-9, loudest * Math.Pow(10, -GateDb / 10));
            var ldr = new List<double>();
            for (int f = 0; f < frames; f++)
                if (longMs[f] >= gate && ms[f] > 0) ldr.Add(10 * Math.Log10(ms[f] / longMs[f]));
            if (ldr.Count == 0) return (level, double.NaN, double.NaN);
            ldr.Sort();
            return (level, Quantile(ldr, Percentile), Quantile(ldr, 0.5));
        }

        static double Quantile(List<double> sorted, double q)
        {
            double at = q * (sorted.Count - 1);
            int i = (int)Math.Floor(at);
            if (i >= sorted.Count - 1) return sorted[^1];
            return sorted[i] + (sorted[i + 1] - sorted[i]) * (at - i);
        }
    }
}
