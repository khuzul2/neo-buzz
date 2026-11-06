#nullable disable
// Neo analysis library (docs/specs/mastering-tools.md §6): the long-term tonal curve and its difference
// from a target, what Ozone's Tonal Balance and Match EQ compare. The curve is the Welch spectrum summed
// in bands a sixth of an octave wide, at six points an octave from 20 Hz to 20 kHz, so pink noise reads
// flat. Against a target (a reference's curve, or a style target averaged from several), the two are
// level-matched first: the target is moved by the mean of its difference from 100 Hz to 10 kHz, so what
// is left is tone, not loudness. The difference is target − source: positive where the source has less
// than the target (a boost would bring it closer).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class TonalCurve
    {
        public double[] Hz, Db;
    }

    internal sealed class TonalDifference
    {
        /// <summary>The points (the source's curve's), target − source after level matching, dB; NaN where either is silent.</summary>
        public double[] Hz, Db;
        /// <summary>What the target was moved by to match the source's level, dB.</summary>
        public double OffsetDb;
        /// <summary>RMS of the difference from 50 Hz to 16 kHz, dB: 0 is a perfect match.</summary>
        public double RmsDb;
        /// <summary>The difference smoothed over a third of an octave (what an EQ can follow).</summary>
        public double[] SmoothedDb;
    }

    internal static class Tonal
    {
        public const int PerOctave = 6;
        public const double MatchLo = 100, MatchHi = 10000, RmsLo = 50, RmsHi = 16000;

        /// <summary>The long-term curve of a measured spectrum (left and right's mean).</summary>
        public static TonalCurve Measure(Psd psd)
        {
            var (hz, db) = Spectrum.Curve(psd, null, 20, 20000, PerOctave, 1.0 / PerOctave);
            return new TonalCurve { Hz = hz, Db = db };
        }

        /// <summary>The curve's level at <paramref name="hz"/>, read between its points in log frequency.</summary>
        public static double At(TonalCurve c, double hz)
        {
            if (c.Hz.Length == 0) return double.NaN;
            if (hz <= c.Hz[0]) return c.Db[0];
            if (hz >= c.Hz[^1]) return c.Db[^1];
            int i = 0;
            while (i < c.Hz.Length - 2 && c.Hz[i + 1] < hz) i++;
            double t = Math.Log(hz / c.Hz[i]) / Math.Log(c.Hz[i + 1] / c.Hz[i]);
            return c.Db[i] + (c.Db[i + 1] - c.Db[i]) * t;
        }

        /// <summary>Target − source at the source's points, level-matched (see the file's comment).</summary>
        public static TonalDifference Difference(TonalCurve source, TonalCurve target)
        {
            int n = source.Hz.Length;
            var raw = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = source.Db[i], t = At(target, source.Hz[i]);
                raw[i] = double.IsFinite(s) && double.IsFinite(t) ? t - s : double.NaN;
            }
            var matched = Enumerable.Range(0, n).Where(i => source.Hz[i] >= MatchLo && source.Hz[i] <= MatchHi && !double.IsNaN(raw[i])).Select(i => raw[i]).ToList();
            double offset = matched.Count > 0 ? matched.Average() : 0;
            var diff = raw.Select(d => double.IsNaN(d) ? double.NaN : d - offset).ToArray();
            var inRange = Enumerable.Range(0, n).Where(i => source.Hz[i] >= RmsLo && source.Hz[i] <= RmsHi && !double.IsNaN(diff[i])).Select(i => diff[i]).ToList();
            double rms = inRange.Count > 0 ? Math.Sqrt(inRange.Average(d => d * d)) : double.NaN;
            return new TonalDifference { Hz = source.Hz, Db = diff, OffsetDb = -offset, RmsDb = rms, SmoothedDb = Smooth(source.Hz, diff, 1.0 / 3) };
        }

        /// <summary>A moving average over <paramref name="octaves"/> in log frequency (NaN points left out).</summary>
        public static double[] Smooth(double[] hz, double[] db, double octaves)
        {
            var y = new double[hz.Length];
            double half = octaves / 2;
            for (int i = 0; i < hz.Length; i++)
            {
                double s = 0;
                int count = 0;
                for (int j = 0; j < hz.Length; j++)
                {
                    if (double.IsNaN(db[j]) || Math.Abs(Math.Log2(hz[j] / hz[i])) > half + 1e-9) continue;
                    s += db[j];
                    count++;
                }
                y[i] = count > 0 ? s / count : double.NaN;
            }
            return y;
        }

        /// <summary>The mean of several curves (each first moved to 0 dB mean from 100 Hz to 10 kHz), at the first's points.</summary>
        public static TonalCurve Average(IReadOnlyList<TonalCurve> curves)
        {
            var hz = curves[0].Hz;
            var db = new double[hz.Length];
            var normalised = curves.Select(Normalise).ToList();
            for (int i = 0; i < hz.Length; i++)
            {
                var values = normalised.Select(c => At(c, hz[i])).Where(double.IsFinite).ToList();
                db[i] = values.Count > 0 ? values.Average() : double.NegativeInfinity;
            }
            return new TonalCurve { Hz = hz, Db = db };
        }

        /// <summary>The curve moved to a mean of 0 dB from 100 Hz to 10 kHz.</summary>
        public static TonalCurve Normalise(TonalCurve c)
        {
            var mid = Enumerable.Range(0, c.Hz.Length).Where(i => c.Hz[i] >= MatchLo && c.Hz[i] <= MatchHi && double.IsFinite(c.Db[i])).Select(i => c.Db[i]).ToList();
            double mean = mid.Count > 0 ? mid.Average() : 0;
            return new TonalCurve { Hz = c.Hz, Db = c.Db.Select(d => d - mean).ToArray() };
        }
    }
}
