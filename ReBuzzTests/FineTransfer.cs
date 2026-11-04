using System;
using System.Globalization;
using System.Linq;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §12.10, machine-safety.md §4): an effect's magnitude response measured from
    /// its input and output on noise, finer than third octaves (a closed EQ's band shapes): Welch's H1 estimate,
    /// |Σ X̄·Y| / Σ |X|² over Hann windows, each frequency's DFT term computed directly.
    /// </summary>
    internal static class FineTransfer
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary><paramref name="perOctave"/> log-spaced frequencies from <paramref name="lo"/> to <paramref name="hi"/>.</summary>
        public static double[] Frequencies(double lo = 20, double hi = 20000, int perOctave = 6)
        {
            int n = (int)Math.Round(Math.Log2(hi / lo) * perOctave);
            return Enumerable.Range(0, n + 1).Select(i => lo * Math.Pow(2, i / (double)perOctave)).ToArray();
        }

        /// <summary>The output's level over the input's at each frequency, dB.</summary>
        public static double[] Db(float[] input, float[] output, int rate, double[] hz, int size = 16384, double fromSeconds = 0.5)
        {
            int n = Math.Min(input.Length, output.Length);
            var window = new double[size];
            for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
            var result = new double[hz.Length];
            int start = (int)(fromSeconds * rate);
            for (int k = 0; k < hz.Length; k++)
            {
                double w = 2 * Math.PI * hz[k] / rate, cr = Math.Cos(w), ci = -Math.Sin(w);
                double sxyR = 0, sxyI = 0, sxx = 0;
                for (int s = start; s + size <= n; s += size / 2)
                {
                    double xr = 0, xi = 0, yr = 0, yi = 0, zr = 1, zi = 0;
                    for (int i = 0; i < size; i++)
                    {
                        double a = window[i];
                        xr += a * input[s + i] * zr;
                        xi += a * input[s + i] * zi;
                        yr += a * output[s + i] * zr;
                        yi += a * output[s + i] * zi;
                        double t = zr * cr - zi * ci;
                        zi = zr * ci + zi * cr;
                        zr = t;
                    }
                    // conj(X)·Y
                    sxyR += xr * yr + xi * yi;
                    sxyI += xr * yi - xi * yr;
                    sxx += xr * xr + xi * xi;
                }
                result[k] = 20 * Math.Log10(Math.Sqrt(sxyR * sxyR + sxyI * sxyI) / Math.Max(sxx, 1e-300) + 1e-30);
            }
            return result;
        }

        /// <summary>"20:0.0 22.4:0.1 …".</summary>
        public static string Text(double[] hz, double[] db) =>
            string.Join(" ", hz.Select((f, i) => f.ToString(f < 100 ? "0.#" : "0", C) + ":" + db[i].ToString("0.00", C)));
    }
}
