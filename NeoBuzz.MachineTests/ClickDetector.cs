namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// A headless click detector (AC2): finds the isolated discontinuities a listener hears as clicks and pops, in a rendered signal.
    /// The signal is first smoothed by [1 2 1] / 4, which takes out what lies near the Nyquist frequency (a resonant filter ringing at 20 kHz
    /// is not a click, and the second difference counts it four times over) and leaves a step a quarter of its size in the second difference.
    /// A click is a sample whose second difference of the smoothed signal, times 4 (so a step of size s reads s), is
    ///   1. at least <see cref="Floor"/> (absolute: a faint tick in a quiet tail does not count),
    ///   2. at least <see cref="K"/> times the mean |second difference| over the 10 ms around it, with ±2 ms left out (a click is far above its surroundings), and
    ///   3. impulsive: at least <see cref="Impulse"/> times the largest value 3 to 8 samples either side of it (a click is two or three
    ///      samples wide; a filter ringing at 6 kHz keeps going, and is a tone, not a click), and
    ///   4. at least <see cref="Isolation"/> times the largest |second difference| within ±40 ms outside ±2 ms of it.
    /// The third condition is what keeps a clean saw's own edges from being flagged: an edge repeats every period, so its
    /// neighbours one period away are as large, while a click occurs once. Clicks within 2 ms of each other count as one.
    /// An opening filter can leave one sharp edge alone (the first of a note), and that is still the oscillator's own edge: the
    /// optional <c>edges</c> mask (from <see cref="OscillatorEdges"/>, the same notes rendered with the filter wide open) excludes
    /// the clicks that sit on one of them. The counts without the mask are reported next to the gated ones.
    /// </summary>
    internal static class ClickDetector
    {
        public const double K = 12;
        public const double Floor = 0.02;
        public const double Isolation = 3;
        public const double Impulse = 2.5;
        const int Window = 480, Guard = 96, Reach = 1920, Merge = 96;

        /// <summary>Sample positions of the clicks in the first <paramref name="n"/> samples of <paramref name="x"/> (48 kHz).</summary>
        public static List<int> Find(float[] x, int n = -1, double floor = Floor, double k = K, double isolation = Isolation, bool[] edges = null)
        {
            if (n < 0 || n > x.Length) n = x.Length;
            var a = Second(x, n);
            var c = new double[n + 1];
            for (int i = 0; i < n; i++) c[i + 1] = c[i] + a[i];
            var found = new List<int>();
            for (int i = 8; i < n - 8; i++)
            {
                if (a[i] < floor) continue;
                int lo = Math.Max(0, i - Window / 2), hi = Math.Min(n, i + Window / 2);
                int glo = Math.Max(0, i - Guard), ghi = Math.Min(n, i + Guard + 1);
                double mean = ((c[hi] - c[lo]) - (c[ghi] - c[glo])) / Math.Max(1, (hi - lo) - (ghi - glo));
                if (a[i] < k * mean) continue;
                float near = 0;
                for (int j = Math.Max(0, i - Reach); j < Math.Max(0, i - Guard); j++) near = Math.Max(near, a[j]);
                for (int j = Math.Min(n, i + Guard + 1); j < Math.Min(n, i + Reach + 1); j++) near = Math.Max(near, a[j]);
                if (a[i] < isolation * near) continue;
                float shoulder = 0;
                for (int j = 3; j <= 8; j++) shoulder = Math.Max(shoulder, Math.Max(a[i - j], a[Math.Min(n - 1, i + j)]));
                if (a[i] < Impulse * shoulder) continue;
                if (edges != null && edges[i]) continue;
                if (found.Count == 0 || i - found[found.Count - 1] > Merge) found.Add(i);
            }
            return found;
        }

        /// <summary>|second difference| of the [1 2 1] / 4 smoothed signal, times 4.</summary>
        static float[] Second(float[] x, int n)
        {
            var y = new float[n];
            for (int i = 1; i < n - 1; i++) y[i] = 0.25f * (x[i - 1] + 2 * x[i] + x[i + 1]);
            var a = new float[n];
            for (int i = 2; i < n - 2; i++) a[i] = 4 * Math.Abs(y[i + 1] - 2 * y[i] + y[i - 1]);
            return a;
        }

        /// <summary>
        /// Where the oscillator's own edges are: a mask over the samples from 10 before to 40 after (a filter delays an edge) a sharp edge of <paramref name="reference"/> (the same notes
        /// with the filter wide open and no resonance, so the saw's wraps and the pulse's edges are all that is sharp in it).
        /// </summary>
        public static bool[] OscillatorEdges(float[] reference)
        {
            int n = reference.Length;
            var a = Second(reference, n);
            var c = new double[n + 1];
            for (int i = 0; i < n; i++) c[i + 1] = c[i] + a[i];
            var mask = new bool[n];
            for (int i = 1; i < n - 1; i++)
            {
                if (a[i] < 0.002f) continue;
                int lo = Math.Max(0, i - Window / 2), hi = Math.Min(n, i + Window / 2);
                if (a[i] < 8 * (c[hi] - c[lo]) / Math.Max(1, hi - lo)) continue;
                for (int j = Math.Max(0, i - 10); j < Math.Min(n, i + 41); j++) mask[j] = true;
            }
            return mask;
        }

        /// <summary>The size of the click at <paramref name="at"/>: the largest second difference within 1 ms of it.</summary>
        public static double Size(float[] x, int at)
        {
            double m = 0;
            var a = Second(x, x.Length);
            for (int i = Math.Max(0, at - 48); i < Math.Min(x.Length, at + 48); i++) m = Math.Max(m, a[i]);
            return m;
        }

        /// <summary>The clicks of both channels together (a click in either counts, one within 2 ms of another is the same).</summary>
        public static List<int> Find(float[] left, float[] right, int n = -1, double floor = Floor, bool[] edges = null)
        {
            var all = Find(left, n, floor, edges: edges);
            all.AddRange(Find(right, n, floor, edges: edges));
            all.Sort();
            var merged = new List<int>();
            foreach (var p in all) if (merged.Count == 0 || p - merged[merged.Count - 1] > Merge) merged.Add(p);
            return merged;
        }
    }
}
