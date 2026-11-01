#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): the spectrum. A Welch-averaged FFT (Hann,
// 50% overlap) scaled so the bins sum to the signal's mean square, then band powers on that one
// scale: ISO third-octave bands (20 Hz – 20 kHz), the named mixing bands (sub … air), and
// centroid, 85% roll-off, flatness and tilt. Levels are plain RMS dB (a full-scale square wave
// reads 0 dB, a full-scale sine −3 dB), so bands add up to the total and pink noise, which has
// equal power in every third octave, reads flat.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Analysis
{
    /// <summary>A Welch power spectrum: per bin (0 … Size/2), on a scale where the bins sum to the mean square.</summary>
    internal sealed class Psd
    {
        public int Rate;
        public int Size;
        public int Segments;
        public double BinHz => Rate / (double)Size;
        public double Nyquist => Rate / 2.0;
        public double[] Left;
        /// <summary>Null for mono.</summary>
        public double[] Right;
        /// <summary>Re(L·conj R) per bin (null for mono): the in-phase part both channels share.</summary>
        public double[] Cross;
        /// <summary>The channels' mean power per bin.</summary>
        public double[] Mean;
        public bool Stereo => Right != null;

        /// <summary>The power of <paramref name="p"/> between <paramref name="lo"/> and <paramref name="hi"/> Hz (bins split at the edges).</summary>
        public double Band(double[] p, double lo, double hi) => Spectrum.Integrate(p, BinHz, lo, hi);
    }

    internal static class Welch
    {
        /// <summary>About 0.68 s: 32768 at 44.1 and 48 kHz (1.5 Hz bins, three in the 20 Hz third octave).</summary>
        public static int DefaultSize(int rate) => Fft.Nearest(0.68 * rate);

        /// <summary>Welch-averaged power spectra of both channels and their cross spectrum.</summary>
        public static Psd Measure(float[] left, float[] right, int rate, int size = 0)
        {
            int n = left.Length;
            bool stereo = right != null && !ReferenceEquals(right, left);
            if (size <= 0) size = DefaultSize(rate);
            while (size > 64 && size > n) size >>= 1;
            var fft = new Fft(size);
            var w = Fft.Hann(size);
            double w2 = 0;
            foreach (double v in w) w2 += v * v;
            int half = size / 2, hop = size / 2;
            var pl = new double[half + 1];
            var pr = stereo ? new double[half + 1] : null;
            var px = stereo ? new double[half + 1] : null;
            var re = new double[size];
            var im = new double[size];
            int segments = 0;
            for (int start = 0; segments == 0 || start + size <= n; start += hop)
            {
                for (int i = 0; i < size; i++)
                {
                    int j = start + i;
                    re[i] = j < n ? left[j] * w[i] : 0;
                    im[i] = stereo && j < n ? right[j] * w[i] : 0;
                }
                fft.Transform(re, im);
                for (int k = 0; k <= half; k++)
                {
                    if (stereo)
                    {
                        Fft.Split(re, im, k, out double lr, out double li, out double rr, out double ri);
                        pl[k] += lr * lr + li * li;
                        pr[k] += rr * rr + ri * ri;
                        px[k] += lr * rr + li * ri;
                    }
                    else pl[k] += re[k] * re[k] + im[k] * im[k];
                }
                segments++;
            }
            var mean = new double[half + 1];
            for (int k = 0; k <= half; k++)
            {
                double c = (k == 0 || k == half ? 1.0 : 2.0) / (size * w2 * segments);
                pl[k] *= c;
                if (stereo)
                {
                    pr[k] *= c;
                    px[k] *= c;
                    mean[k] = 0.5 * (pl[k] + pr[k]);
                }
                else mean[k] = pl[k];
            }
            return new Psd { Rate = rate, Size = size, Segments = segments, Left = pl, Right = pr, Cross = px, Mean = mean };
        }
    }

    internal readonly struct NamedBand
    {
        public NamedBand(string name, double lo, double hi) { Name = name; Lo = lo; Hi = hi; }
        public string Name { get; }
        public double Lo { get; }
        public double Hi { get; }
        public double Octaves => Math.Log2(Hi / Lo);
    }

    internal static class Bands
    {
        /// <summary>ISO 266 nominal third-octave centres, 20 Hz – 20 kHz.</summary>
        public static readonly double[] ThirdOctaveNominal =
        {
            20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
            1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
        };

        /// <summary>The exact (base-ten) centre of third octave <paramref name="i"/>: 1 kHz × 10^((i − 17)/10).</summary>
        public static double ThirdOctaveCentre(int i) => 1000 * Math.Pow(10, (i - 17) / 10.0);

        public static (double Lo, double Hi) ThirdOctaveEdges(int i)
        {
            double c = ThirdOctaveCentre(i);
            return (c * Math.Pow(10, -0.05), c * Math.Pow(10, 0.05));
        }

        /// <summary>The mixing bands, by their usual names.</summary>
        public static readonly NamedBand[] Named =
        {
            new("sub", 20, 60),
            new("low", 60, 250),
            new("low_mid", 250, 500),
            new("mid", 500, 2000),
            new("high_mid", 2000, 4000),
            new("presence", 4000, 6000),
            new("air", 6000, 20000),
        };

        /// <summary>Zwicker's critical bands (Bark), from 20 Hz, with a last band to 20 kHz.</summary>
        public static readonly double[] BarkEdges =
        {
            20, 100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320, 2700,
            3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 20000,
        };
    }

    internal sealed class SpectrumResult
    {
        public Psd Psd;
        /// <summary>Nominal centres and band levels (dB RMS) of the 31 third octaves.</summary>
        public double[] ThirdOctaveHz, ThirdOctaveDb;
        /// <summary>Levels of <see cref="Bands.Named"/>, dB RMS.</summary>
        public double[] NamedDb;
        /// <summary>Level of everything from 20 Hz to 20 kHz (or Nyquist), dB RMS.</summary>
        public double TotalDb;
        /// <summary>Amplitude-weighted mean frequency, Hz.</summary>
        public double CentroidHz;
        /// <summary>Where 85% of the amplitude spectrum lies below, Hz.</summary>
        public double RolloffHz;
        /// <summary>Geometric over arithmetic mean of the power spectrum: 1 for white noise, near 0 for a tone.</summary>
        public double Flatness;
        /// <summary>Least-squares slope of the third-octave levels over 50 Hz – 16 kHz, dB per octave (pink noise 0, white +3).</summary>
        public double TiltDbPerOctave;
        /// <summary>The fitted level at 1 kHz (dB per third octave), for reading bands against the slope.</summary>
        public double TiltAt1k;
    }

    internal static class Spectrum
    {
        /// <summary>Power below this (−200 dB) is silence.</summary>
        const double Floor = 1e-20;

        public static double Db(double power) => power > Floor ? 10 * Math.Log10(power) : double.NegativeInfinity;

        public static SpectrumResult Measure(float[] left, float[] right, int rate, int size = 0) => FromPsd(Welch.Measure(left, right, rate, size));

        public static SpectrumResult FromPsd(Psd psd)
        {
            var p = psd.Mean;
            double df = psd.BinHz, top = Math.Min(20000, psd.Nyquist);
            var r = new SpectrumResult { Psd = psd };
            int bands = Bands.ThirdOctaveNominal.Length;
            r.ThirdOctaveHz = (double[])Bands.ThirdOctaveNominal.Clone();
            r.ThirdOctaveDb = new double[bands];
            for (int i = 0; i < bands; i++)
            {
                var (lo, hi) = Bands.ThirdOctaveEdges(i);
                r.ThirdOctaveDb[i] = lo >= psd.Nyquist ? double.NegativeInfinity : Db(Integrate(p, df, lo, hi));
            }
            r.NamedDb = new double[Bands.Named.Length];
            for (int i = 0; i < Bands.Named.Length; i++) r.NamedDb[i] = Db(Integrate(p, df, Bands.Named[i].Lo, Bands.Named[i].Hi));
            r.TotalDb = Db(Integrate(p, df, 20, top));

            int k0 = (int)Math.Ceiling(20 / df), k1 = Math.Min(p.Length - 1, (int)Math.Floor(top / df));
            double amp = 0, weighted = 0, sum = 0, logSum = 0;
            int count = 0;
            for (int k = k0; k <= k1; k++)
            {
                double a = Math.Sqrt(p[k]);
                amp += a;
                weighted += a * k * df;
                sum += p[k];
                logSum += Math.Log(Math.Max(p[k], Floor));
                count++;
            }
            r.CentroidHz = amp > 0 ? weighted / amp : 0;
            r.RolloffHz = 0;
            if (amp > 0)
            {
                double acc = 0;
                for (int k = k0; k <= k1; k++)
                {
                    acc += Math.Sqrt(p[k]);
                    if (acc >= 0.85 * amp) { r.RolloffHz = k * df; break; }
                }
            }
            r.Flatness = count > 0 && sum > Floor * count ? Math.Exp(logSum / count) / (sum / count) : 0;
            (r.TiltDbPerOctave, r.TiltAt1k) = Tilt(r.ThirdOctaveDb, psd.Nyquist);
            return r;
        }

        /// <summary>The slope (dB/octave) and 1 kHz level of a least-squares line through the third octaves 50 Hz – 16 kHz.</summary>
        public static (double Slope, double At1k) Tilt(double[] thirdOctaveDb, double nyquist)
        {
            double max = double.NegativeInfinity;
            foreach (double d in thirdOctaveDb) if (d > max) max = d;
            if (double.IsNegativeInfinity(max)) return (0, double.NegativeInfinity);
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < thirdOctaveDb.Length; i++)
            {
                double nominal = Bands.ThirdOctaveNominal[i];
                if (nominal < 50 || nominal > 16000 || Bands.ThirdOctaveEdges(i).Hi > nyquist) continue;
                xs.Add(Math.Log2(Bands.ThirdOctaveCentre(i) / 1000));
                // Bands far below the loudest count at 100 dB down, so silence above a cutoff reads dark, not infinite.
                ys.Add(Math.Max(thirdOctaveDb[i], max - 100));
            }
            if (xs.Count < 3) return (0, max);
            double mx = 0, my = 0;
            for (int i = 0; i < xs.Count; i++) { mx += xs[i]; my += ys[i]; }
            mx /= xs.Count;
            my /= xs.Count;
            double sxy = 0, sxx = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                sxy += (xs[i] - mx) * (ys[i] - my);
                sxx += (xs[i] - mx) * (xs[i] - mx);
            }
            double slope = sxx > 0 ? sxy / sxx : 0;
            return (slope, my - slope * mx);
        }

        /// <summary>
        /// The power of the bins between <paramref name="lo"/> and <paramref name="hi"/> Hz. Bin k
        /// covers (k ± ½)·<paramref name="binHz"/>; one straddling an edge counts in proportion.
        /// </summary>
        public static double Integrate(double[] p, double binHz, double lo, double hi)
        {
            int last = p.Length - 1;
            double a = lo / binHz, b = Math.Min(hi / binHz, last + 0.5);
            if (b <= a) return 0;
            int k0 = Math.Max(0, (int)Math.Floor(a + 0.5)), k1 = Math.Min(last, (int)Math.Floor(b + 0.5));
            double sum = 0;
            for (int k = k0; k <= k1; k++)
            {
                double overlap = Math.Min(b, k + 0.5) - Math.Max(a, k - 0.5);
                if (overlap > 0) sum += p[k] * Math.Min(1, overlap);
            }
            return sum;
        }

        /// <summary>
        /// A smooth curve for plots: the third-octave band level centred on each of
        /// <paramref name="perOctave"/> points per octave from <paramref name="fromHz"/> to
        /// <paramref name="toHz"/>, so it passes through the third-octave readings and pink noise draws flat.
        /// </summary>
        public static (double[] Hz, double[] Db) Curve(Psd psd, double[] p = null, double fromHz = 20, double toHz = 20000, int perOctave = 24)
        {
            p ??= psd.Mean;
            toHz = Math.Min(toHz, psd.Nyquist / Math.Pow(10, 0.05));
            int count = Math.Max(2, (int)Math.Floor(Math.Log2(toHz / fromHz) * perOctave) + 1);
            var hz = new double[count];
            var db = new double[count];
            double edge = Math.Pow(10, 0.05);
            for (int i = 0; i < count; i++)
            {
                double f = fromHz * Math.Pow(2, i / (double)perOctave);
                hz[i] = f;
                db[i] = Db(Integrate(p, psd.BinHz, f / edge, f * edge));
            }
            return (hz, db);
        }
    }
}
