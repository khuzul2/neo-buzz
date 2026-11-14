// Fork (docs/specs/ux-overhaul.md P6b): what a tap delivers, kept for a display. A tap (a connection's, or the master's) calls Add on the
// engine's thread with a block of interleaved samples (full scale = 32768); Add copies it into rings made up front, so it allocates
// nothing and takes no lock, and the UI thread reads the latest frames when it draws (a torn block at the seam is only a pixel of
// scope). The spectrum is made on the UI thread from those frames: an FFT with the chosen window, in dB below full scale.

using System;
using System.Threading;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class SignalTap
    {
        public const float FullScale = 32768f;

        readonly float[] left, right;
        readonly int mask;
        long written;

        /// <param name="frames">The ring's length in frames; a power of two.</param>
        public SignalTap(int frames = 16384)
        {
            if (frames < 2 || (frames & (frames - 1)) != 0) throw new ArgumentException("a power of two", nameof(frames));
            left = new float[frames];
            right = new float[frames];
            mask = frames - 1;
        }

        public int Capacity => left.Length;

        /// <summary>Frames delivered so far.</summary>
        public long Written => Volatile.Read(ref written);

        /// <summary>The tap's thread: a block of samples, interleaved L R when <paramref name="stereo"/>. Allocates nothing.</summary>
        public void Add(float[] samples, bool stereo)
        {
            if (samples == null) return;
            long w = written;
            if (stereo)
            {
                for (int i = 0; i + 1 < samples.Length; i += 2, w++)
                {
                    left[(int)w & mask] = samples[i];
                    right[(int)w & mask] = samples[i + 1];
                }
            }
            else
            {
                for (int i = 0; i < samples.Length; i++, w++)
                    left[(int)w & mask] = right[(int)w & mask] = samples[i];
            }
            Volatile.Write(ref written, w);
        }

        /// <summary>The latest frames (the oldest first), as fractions of full scale, into arrays <paramref name="l"/> and <paramref name="r"/>; returns how many were there.</summary>
        public int CopyLatest(double[] l, double[] r)
        {
            long end = Written;
            int n = (int)Math.Min(Math.Min(l.Length, r.Length), Math.Min(end, Capacity));
            int pad = l.Length - n;
            for (int i = 0; i < pad; i++) l[i] = 0;
            for (int i = 0; i < pad && i < r.Length; i++) r[i] = 0;
            for (int i = 0; i < n; i++)
            {
                int at = (int)(end - n + i) & mask;
                l[pad + i] = left[at] / FullScale;
                r[pad + i] = right[at] / FullScale;
            }
            return n;
        }
    }

    /// <summary>A radix-2 FFT with the windows ReBuzz's Signal Analysis offers; arrays are the caller's, nothing allocates per call.</summary>
    internal sealed class Spectrum
    {
        public static readonly string[] Windows = { "Hanning", "Hamming", "Blackman", "Blackman-Harris", "Flat top", "Bartlett", "Cosine", "Rectangular" };

        readonly int size;
        readonly double[] re, im, sin, cos;
        readonly int[] reverse;
        double[] window;
        double windowSum;
        string windowName;

        public Spectrum(int size = 2048)
        {
            if (size < 4 || (size & (size - 1)) != 0) throw new ArgumentException("a power of two", nameof(size));
            this.size = size;
            re = new double[size];
            im = new double[size];
            sin = new double[size / 2];
            cos = new double[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                sin[i] = Math.Sin(-2 * Math.PI * i / size);
                cos[i] = Math.Cos(-2 * Math.PI * i / size);
            }
            reverse = new int[size];
            int bits = (int)Math.Log2(size);
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                reverse[i] = r;
            }
            SetWindow("Hanning");
        }

        public int Size => size;
        public int Bins => size / 2;

        public void SetWindow(string name)
        {
            if (name == windowName) return;
            windowName = name;
            window = new double[size];
            double n1 = size - 1;
            for (int i = 0; i < size; i++)
            {
                double x = 2 * Math.PI * i / n1;
                window[i] = name switch
                {
                    "Hamming" => 0.54 - 0.46 * Math.Cos(x),
                    "Blackman" => 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x),
                    "Blackman-Harris" => 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x),
                    "Flat top" => 0.21557895 - 0.41663158 * Math.Cos(x) + 0.277263158 * Math.Cos(2 * x) - 0.083578947 * Math.Cos(3 * x) + 0.006947368 * Math.Cos(4 * x),
                    "Bartlett" => 1 - Math.Abs(2 * i / n1 - 1),
                    "Cosine" => Math.Sin(Math.PI * i / n1),
                    "Rectangular" => 1,
                    _ => 0.5 - 0.5 * Math.Cos(x),
                };
            }
            windowSum = 0;
            foreach (var w in window) windowSum += w;
        }

        /// <summary>
        /// The magnitude of the last <see cref="Size"/> values of <paramref name="x"/> (fractions of full scale) in dB below full scale
        /// (a sine of full scale on a bin reads 0), into <paramref name="db"/> (<see cref="Bins"/> long). -120 for silence.
        /// </summary>
        public void Magnitudes(double[] x, double[] db)
        {
            int offset = x.Length - size;
            for (int i = 0; i < size; i++)
            {
                re[reverse[i]] = (offset + i >= 0 ? x[offset + i] : 0) * window[i];
                im[reverse[i]] = 0;
            }
            for (int len = 2; len <= size; len <<= 1)
            {
                int half = len / 2, step = size / len;
                for (int i = 0; i < size; i += len)
                    for (int j = 0; j < half; j++)
                    {
                        double c = cos[j * step], s = sin[j * step];
                        int a = i + j, b = a + half;
                        double tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
            double scale = 2.0 / windowSum;
            for (int k = 0; k < size / 2 && k < db.Length; k++)
            {
                double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * scale;
                db[k] = m <= 1e-6 ? -120 : 20 * Math.Log10(m);
            }
        }
    }
}
