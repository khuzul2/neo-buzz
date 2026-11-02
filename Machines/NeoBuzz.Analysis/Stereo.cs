#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): the stereo image. Correlation (overall and
// in 200 ms windows), width as the side-to-mid energy ratio (0 mono, 1 for unrelated channels,
// above 1 when the channels oppose), and mono fold-down loss: the level of (L + R)/2 against the
// channels' mean, overall and per named band (from the Welch cross spectrum).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class StereoBand
    {
        public string Name;
        public double Lo, Hi;
        /// <summary>Side over mid energy (0 mono, 1 unrelated channels, ∞ fully opposed).</summary>
        public double Width;
        public double Correlation;
        /// <summary>Level of (L + R)/2 against the channels' mean level, dB (0 mono, −3 unrelated, −∞ opposed).</summary>
        public double MonoLossDb;
        /// <summary>The band's level (channels' mean), dB RMS: a near-silent band's width means little.</summary>
        public double LevelDb;
    }

    internal sealed class StereoResult
    {
        /// <summary>No second channel, or one identical to the first.</summary>
        public bool Mono;
        public double Correlation;
        public double WindowSeconds;
        /// <summary>Correlation per window; NaN where both channels are below −70 dBFS.</summary>
        public double[] CorrelationOverTime;
        public double CorrelationMin;
        /// <summary>Share of the sounding windows with negative correlation, 0 … 1.</summary>
        public double NegativeShare;
        public double Width;
        public double SideToMidDb;
        public double MonoLossDb;
        /// <summary>Left level over right level, dB.</summary>
        public double BalanceDb;
        public StereoBand[] Bands;
    }

    internal static class Stereo
    {
        public static StereoResult Measure(float[] left, float[] right, int rate, Psd psd = null, double window = 0.2)
        {
            int n = left.Length;
            bool mono = right == null || ReferenceEquals(left, right);
            if (!mono)
            {
                mono = true;
                for (int i = 0; i < n && mono; i++) if (left[i] != right[i]) mono = false;
            }
            var r = new StereoResult { Mono = mono, WindowSeconds = window };
            if (mono)
            {
                psd ??= Welch.Measure(left, null, rate);
                r.Correlation = 1;
                r.CorrelationMin = 1;
                r.SideToMidDb = double.NegativeInfinity;
                int windows = n / Math.Max(1, (int)Math.Round(window * rate));
                r.CorrelationOverTime = Enumerable.Repeat(1.0, windows).ToArray();
                r.Bands = Bands.Named.Select(b => new StereoBand { Name = b.Name, Lo = b.Lo, Hi = b.Hi, Correlation = 1, LevelDb = Spectrum.Db(psd.Band(psd.Mean, b.Lo, b.Hi)) }).ToArray();
                return r;
            }

            double ll = 0, rr = 0, lr = 0;
            for (int i = 0; i < n; i++)
            {
                ll += left[i] * (double)left[i];
                rr += right[i] * (double)right[i];
                lr += left[i] * (double)right[i];
            }
            (r.Correlation, r.Width, r.MonoLossDb) = Measures(ll, rr, lr);
            r.SideToMidDb = Spectrum.Db(r.Width);
            r.BalanceDb = rr > 0 && ll > 0 ? 10 * Math.Log10(ll / rr) : 0;

            int size = Math.Max(1, (int)Math.Round(window * rate));
            int count = n / size;
            r.CorrelationOverTime = new double[count];
            double gate = size * 1e-7 * 2;   // −70 dBFS mean square over both channels
            int sounding = 0, negative = 0;
            r.CorrelationMin = 1;
            for (int b = 0; b < count; b++)
            {
                double a = 0, c = 0, x = 0;
                for (int i = b * size, end = i + size; i < end; i++)
                {
                    a += left[i] * (double)left[i];
                    c += right[i] * (double)right[i];
                    x += left[i] * (double)right[i];
                }
                if (a + c < gate) { r.CorrelationOverTime[b] = double.NaN; continue; }
                double corr = a > 0 && c > 0 ? x / Math.Sqrt(a * c) : 0;
                r.CorrelationOverTime[b] = corr;
                sounding++;
                if (corr < 0) negative++;
                if (corr < r.CorrelationMin) r.CorrelationMin = corr;
            }
            r.NegativeShare = sounding > 0 ? negative / (double)sounding : 0;

            psd ??= Welch.Measure(left, right, rate);
            r.Bands = new StereoBand[Bands.Named.Length];
            for (int i = 0; i < Bands.Named.Length; i++)
            {
                var band = Bands.Named[i];
                double pl = psd.Band(psd.Left, band.Lo, band.Hi), pr = psd.Band(psd.Right, band.Lo, band.Hi), px = psd.Band(psd.Cross, band.Lo, band.Hi);
                var (corr, width, loss) = Measures(pl, pr, px);
                r.Bands[i] = new StereoBand { Name = band.Name, Lo = band.Lo, Hi = band.Hi, Correlation = corr, Width = width, MonoLossDb = loss, LevelDb = Spectrum.Db(0.5 * (pl + pr)) };
            }
            return r;
        }

        /// <summary>Correlation, side/mid energy and mono loss from the channels' energies and their in-phase product.</summary>
        internal static (double Correlation, double Width, double MonoLossDb) Measures(double ll, double rr, double lr)
        {
            double mid = ll + rr + 2 * lr, side = ll + rr - 2 * lr;
            if (ll + rr <= 0) return (1, 0, 0);
            double corr = ll > 0 && rr > 0 ? lr / Math.Sqrt(ll * rr) : 0;
            double width = mid > 1e-12 * (ll + rr) ? Math.Max(0, side) / mid : double.PositiveInfinity;
            // (L + R)/2 has energy mid/4; the channels' mean is (ll + rr)/2.
            double loss = mid > 0 ? 10 * Math.Log10(mid / (2 * (ll + rr))) : double.NegativeInfinity;
            return (Math.Clamp(corr, -1, 1), width, loss);
        }
    }
}
