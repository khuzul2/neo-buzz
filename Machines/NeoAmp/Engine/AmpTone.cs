#nullable disable
// Neo Amp (docs/specs/neo-amp.md §4): the passive three-knob tone stack. Its transfer function is worked out from the network itself,
// not from a formula copied from somewhere: the network is a list of resistors and capacitors between nodes (ToneNetwork.Parts); the
// nodal equations (admittance matrix Y(s) = G + sC of the nodes the signal does not drive) are solved for the two nodes the treble
// control picks between by Cramer's rule with polynomial determinants (the entries are polynomials of degree 1 in s), which gives
// H(s) = N(s) / D(s) of third order (three capacitors). The treble pot is an unloaded divider between the two nodes, so it only mixes
// them: Vout = t · Va + (1 − t) · Vb. The polynomials are turned into a digital filter by the bilinear transform
// (s = k (1 − z⁻¹)/(1 + z⁻¹), k pre-warped to match the analog response at 1.5 kHz), run in double precision as a transposed
// direct form II. A separate solver (Gaussian elimination of the same nodal equations at jω) is the reference the tests and the
// editor's picture compare with: ToneNetwork.Response.
//
// The Bass pot is a log-taper pot (a resistor to ground from the stack's middle node), Mid is the pot in series with the slope
// resistor, Treble is the wiper. The knobs are fractions of the pot's rotation, 0 … 1.
//
// Literature: Yeh and Smith (DAFx-06, Montreal, 2006; the title names an amplifier, so it is paraphrased here: discretising a classic passive
// guitar-amplifier tone stack) show this analysis (nodal equations of the RC network, the bilinear transform); the values
// and the network here are our own. No allocation after construction; portable maths only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Amp
{
    /// <summary>The network: nodes 0 ground, 1 the input, 2 a (top of the treble pot), 3 b (its bottom), 4 c, 5 d.</summary>
    internal static class ToneNetwork
    {
        public const int Unknowns = 4;      // nodes 2 … 5
        public const double Omega0 = 2 * Math.PI * 1000.0;   // the polynomials are in s' = s / Omega0
        public const double Rbase = 10000.0;                 // admittances are in units of 1 / Rbase

        public enum Role { Fixed, MidSeries, Bass }

        public readonly struct Part
        {
            public readonly bool Cap;
            public readonly int A, B;
            public readonly double Value;
            public readonly Role Role;
            public readonly int CapIndex;   // which of the three capacitors (0 treble, 1 bass-side, 2 mid-side) a cap scales with
            public Part(bool cap, int a, int b, double value, Role role = Role.Fixed, int capIndex = -1) { Cap = cap; A = a; B = b; Value = value; Role = role; CapIndex = capIndex; }
        }

        // Component values (farads, ohms), found by a numeric search for a stack whose three knobs each move their own band by 8 to 25 dB
        // with little effect on the others and a scooped middle at the centre setting (docs/specs/neo-amp.md §4).
        public const double C1 = 1.2e-9, C2 = 470e-12, C3 = 150e-9, R1 = 7.5e3, R2 = 62e3, R3 = 68e3, R4 = 43e3, Rs = 33e3;

        public static readonly Part[] Parts =
        {
            new Part(true, 1, 2, C1, Role.Fixed, 0),          // treble capacitor: input to the top of the treble pot
            new Part(false, 2, 3, R1),                        // the treble pot, whole (the wiper is unloaded)
            new Part(false, 1, 5, Rs),                        // the slope resistor
            new Part(false, 5, 3, R3, Role.MidSeries),        // the mid pot, in series: (1 - mid) · R3
            new Part(false, 3, 0, R2, Role.Bass),             // the bass pot, a resistor from the stack's middle to ground
            new Part(true, 3, 0, C3, Role.Fixed, 2),          // the mid capacitor
            new Part(false, 3, 4, R4),                        // the bass-side resistor
            new Part(true, 4, 0, C2, Role.Fixed, 1),          // the bass capacitor
        };

        /// <summary>Pot fractions from knob positions: bass is a log-taper pot, mid and treble linear. Never exactly 0 or 1 (a real pot has a track).</summary>
        public static void Pots(double bass, double mid, double treble, out double l, out double m, out double t)
        {
            bass = Math.Clamp(bass, 0, 1);
            l = (PortableMath.Pow(100.0, bass) - 1.0) / 99.0;
            l = Math.Clamp(l, 0.06, 1.0);
            m = Math.Clamp(mid, 0.02, 0.98);
            t = Math.Clamp(treble, 0, 1);
        }

        /// <summary>The value of a part for the pot fractions, with capacitor scale factors (a bass amp's larger caps).</summary>
        public static double ValueOf(in Part p, double l, double m, double[] capScale)
        {
            double v = p.Value;
            if (p.Cap) return p.CapIndex >= 0 && capScale != null ? v * capScale[p.CapIndex] : v;
            switch (p.Role)
            {
                case Role.Bass: return Math.Max(v * l, 100.0);
                case Role.MidSeries: return Math.Max(v * (1.0 - m), 1.0);
                default: return v;
            }
        }

        /// <summary>
        /// The reference: the network's response at <paramref name="hz"/> by solving the nodal equations with complex Gaussian elimination
        /// (a different solver from the one the filter's polynomials come from).
        /// </summary>
        public static (double Re, double Im) Response(double hz, double bass, double mid, double treble, double[] capScale = null)
        {
            Pots(bass, mid, treble, out double l, out double m, out double t);
            double w = 2 * Math.PI * hz;
            var yr = new double[Unknowns, Unknowns];
            var yi = new double[Unknowns, Unknowns];
            var br = new double[Unknowns];
            var bi = new double[Unknowns];
            foreach (var p in Parts)
            {
                double v = ValueOf(p, l, m, capScale);
                double gr = p.Cap ? 0 : 1.0 / v, gi = p.Cap ? w * v : 0;
                int i = p.A - 2, j = p.B - 2;
                if (i >= 0) { yr[i, i] += gr; yi[i, i] += gi; }
                if (j >= 0) { yr[j, j] += gr; yi[j, j] += gi; }
                if (i >= 0 && j >= 0) { yr[i, j] -= gr; yi[i, j] -= gi; yr[j, i] -= gr; yi[j, i] -= gi; }
                if (p.A == 1 && j >= 0) { br[j] += gr; bi[j] += gi; }
                if (p.B == 1 && i >= 0) { br[i] += gr; bi[i] += gi; }
            }
            // Gaussian elimination with partial pivoting on complex numbers.
            int n = Unknowns;
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                double best = yr[c, c] * yr[c, c] + yi[c, c] * yi[c, c];
                for (int r = c + 1; r < n; r++)
                {
                    double mag = yr[r, c] * yr[r, c] + yi[r, c] * yi[r, c];
                    if (mag > best) { best = mag; piv = r; }
                }
                if (piv != c)
                {
                    for (int k = 0; k < n; k++)
                    {
                        (yr[c, k], yr[piv, k]) = (yr[piv, k], yr[c, k]);
                        (yi[c, k], yi[piv, k]) = (yi[piv, k], yi[c, k]);
                    }
                    (br[c], br[piv]) = (br[piv], br[c]);
                    (bi[c], bi[piv]) = (bi[piv], bi[c]);
                }
                double dr = yr[c, c], di = yi[c, c], dd = dr * dr + di * di;
                for (int r = c + 1; r < n; r++)
                {
                    // factor = Y[r,c] / Y[c,c]
                    double fr = (yr[r, c] * dr + yi[r, c] * di) / dd, fi = (yi[r, c] * dr - yr[r, c] * di) / dd;
                    for (int k = c; k < n; k++)
                    {
                        yr[r, k] -= fr * yr[c, k] - fi * yi[c, k];
                        yi[r, k] -= fr * yi[c, k] + fi * yr[c, k];
                    }
                    br[r] -= fr * br[c] - fi * bi[c];
                    bi[r] -= fr * bi[c] + fi * br[c];
                }
            }
            var xr = new double[n];
            var xi = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double sr = br[r], si = bi[r];
                for (int k = r + 1; k < n; k++)
                {
                    sr -= yr[r, k] * xr[k] - yi[r, k] * xi[k];
                    si -= yr[r, k] * xi[k] + yi[r, k] * xr[k];
                }
                double dr = yr[r, r], di = yi[r, r], dd = dr * dr + di * di;
                xr[r] = (sr * dr + si * di) / dd;
                xi[r] = (si * dr - sr * di) / dd;
            }
            // Vout = t Va + (1 - t) Vb; Va = x[0] (node 2), Vb = x[1] (node 3).
            return (t * xr[0] + (1 - t) * xr[1], t * xi[0] + (1 - t) * xi[1]);
        }

        public static double ResponseDb(double hz, double bass, double mid, double treble, double[] capScale = null)
        {
            var (re, im) = Response(hz, bass, mid, treble, capScale);
            return DspMathDb(Math.Sqrt(re * re + im * im));
        }

        static double DspMathDb(double g) => g > 1e-12 ? 20.0 * PortableMath.Log10(g) : -240.0;
    }

    /// <summary>The tone stack as a digital filter: coefficients from the knob positions, then run per sample.</summary>
    internal struct ToneFilter
    {
        double b0, b1, b2, b3, a1, a2, a3;
        double s1, s2, s3;
        public double Gain;     // the level makeup applied by Process (1 = none)


        static readonly int[][] Perms = MakePerms();
        static readonly int[] Signs = MakeSigns();

        static int[][] MakePerms()
        {
            var list = new System.Collections.Generic.List<int[]>();
            var a = new[] { 0, 1, 2, 3 };
            Permute(a, 0, list);
            return list.ToArray();
        }

        static void Permute(int[] a, int k, System.Collections.Generic.List<int[]> list)
        {
            if (k == a.Length) { list.Add((int[])a.Clone()); return; }
            for (int i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                Permute(a, k + 1, list);
                (a[k], a[i]) = (a[i], a[k]);
            }
        }

        static int[] MakeSigns()
        {
            var s = new int[Perms.Length];
            for (int p = 0; p < Perms.Length; p++)
            {
                int inv = 0;
                for (int i = 0; i < 4; i++)
                    for (int j = i + 1; j < 4; j++)
                        if (Perms[p][i] > Perms[p][j]) inv++;
                s[p] = (inv & 1) == 0 ? 1 : -1;
            }
            return s;
        }

        public void Reset() { s1 = s2 = s3 = 0; }

        /// <summary>The polynomials N(s') and D(s') (coefficients in ascending powers, 5 each) of the stack at the knob positions, s' = s / 2π·1000.</summary>
        public static void Polynomials(double bass, double mid, double treble, double[] capScale, double[] num, double[] den)
        {
            ToneNetwork.Pots(bass, mid, treble, out double l, out double m, out double t);
            // The admittance matrix as polynomials: [i, j] → (g, c).
            Span<double> g = stackalloc double[16], c = stackalloc double[16];
            Span<double> rg = stackalloc double[4], rc = stackalloc double[4];
            g.Clear(); c.Clear(); rg.Clear(); rc.Clear();
            foreach (var p in ToneNetwork.Parts)
            {
                double v = ToneNetwork.ValueOf(p, l, m, capScale);
                double yg = p.Cap ? 0 : ToneNetwork.Rbase / v;
                double yc = p.Cap ? v * ToneNetwork.Omega0 * ToneNetwork.Rbase : 0;
                int i = p.A - 2, j = p.B - 2;
                if (i >= 0) { g[i * 4 + i] += yg; c[i * 4 + i] += yc; }
                if (j >= 0) { g[j * 4 + j] += yg; c[j * 4 + j] += yc; }
                if (i >= 0 && j >= 0)
                {
                    g[i * 4 + j] -= yg; c[i * 4 + j] -= yc;
                    g[j * 4 + i] -= yg; c[j * 4 + i] -= yc;
                }
                if (p.A == 1 && j >= 0) { rg[j] += yg; rc[j] += yc; }
                if (p.B == 1 && i >= 0) { rg[i] += yg; rc[i] += yc; }
            }
            Span<double> da = stackalloc double[5], na = stackalloc double[5], nb = stackalloc double[5];
            Det(g, c, rg, rc, -1, da);
            Det(g, c, rg, rc, 0, na);
            Det(g, c, rg, rc, 1, nb);
            for (int k = 0; k < 5; k++)
            {
                den[k] = da[k];
                num[k] = t * na[k] + (1 - t) * nb[k];
            }
        }

        /// <summary>The determinant (a polynomial in s') of the matrix with column <paramref name="col"/> replaced by the right-hand side (−1: none).</summary>
        static void Det(ReadOnlySpan<double> g, ReadOnlySpan<double> c, ReadOnlySpan<double> rg, ReadOnlySpan<double> rc, int col, Span<double> result)
        {
            result.Clear();
            Span<double> poly = stackalloc double[5];
            for (int p = 0; p < Perms.Length; p++)
            {
                var perm = Perms[p];
                poly.Clear();
                poly[0] = 1;
                int degree = 0;
                for (int r = 0; r < 4; r++)
                {
                    int k = perm[r];
                    double e0, e1;
                    if (k == col) { e0 = rg[r]; e1 = rc[r]; }
                    else { e0 = g[r * 4 + k]; e1 = c[r * 4 + k]; }
                    // poly *= (e0 + e1 s)
                    for (int d = degree + 1; d >= 0; d--)
                    {
                        double v = poly[d] * e0;
                        if (d > 0) v += poly[d - 1] * e1;
                        poly[d] = v;
                    }
                    degree++;
                }
                double sign = Signs[p];
                for (int d = 0; d < 5; d++) result[d] += sign * poly[d];
            }
        }

        /// <summary>The digital coefficients from the polynomials at sample rate <paramref name="rate"/> (bilinear, matched at 1.5 kHz).</summary>
        public void Set(double rate, double[] num, double[] den)
        {
            // k such that s' = k (1 - z^-1)/(1 + z^-1) matches at 1.5 kHz: s = ω_ref / tan(ω_ref T / 2) · (1 - z^-1)/(1 + z^-1).
            double wref = 2 * Math.PI * 1500.0;
            double k = wref / PortableMath.Tan(wref / (2 * rate)) / ToneNetwork.Omega0;
            const int M = 3;
            Span<double> bn = stackalloc double[M + 1], bd = stackalloc double[M + 1];
            Convert(num, k, bn);
            Convert(den, k, bd);
            double a0 = bd[0];
            b0 = bn[0] / a0; b1 = bn[1] / a0; b2 = bn[2] / a0; b3 = bn[3] / a0;
            a1 = bd[1] / a0; a2 = bd[2] / a0; a3 = bd[3] / a0;
        }

        // Σ p_k k^k (1 − z⁻¹)^k (1 + z⁻¹)^(M−k): the numerator polynomial in z⁻¹ (degree M).
        static void Convert(double[] p, double k, Span<double> outp)
        {
            const int M = 3;
            outp.Clear();
            Span<double> term = stackalloc double[M + 1];
            double kk = 1;
            for (int i = 0; i <= M; i++)
            {
                term.Clear();
                term[0] = p[i] * kk;
                int deg = 0;
                for (int f = 0; f < i; f++) { MulFactor(term, deg, -1); deg++; }
                for (int f = 0; f < M - i; f++) { MulFactor(term, deg, +1); deg++; }
                for (int d = 0; d <= M; d++) outp[d] += term[d];
                kk *= k;
            }
        }

        static void MulFactor(Span<double> poly, int degree, double sign)
        {
            // poly *= (1 + sign z^-1)
            for (int d = degree + 1; d >= 1; d--) poly[d] += sign * poly[d - 1];
        }

        public double Process(double x)
        {
            double y = b0 * x + s1;
            s1 = b1 * x - a1 * y + s2;
            s2 = b2 * x - a2 * y + s3;
            s3 = b3 * x - a3 * y;
            return y;
        }

        /// <summary>Flushes tiny state values (no denormals in the tail).</summary>
        public void Flush()
        {
            if (s1 > -1e-30 && s1 < 1e-30) s1 = 0;
            if (s2 > -1e-30 && s2 < 1e-30) s2 = 0;
            if (s3 > -1e-30 && s3 < 1e-30) s3 = 0;
        }

        /// <summary>The digital filter's magnitude (not including <see cref="Gain"/>) at <paramref name="hz"/> for sample rate <paramref name="rate"/>.</summary>
        public double Magnitude(double hz, double rate)
        {
            double w = 2 * Math.PI * hz / rate;
            double c1 = PortableMath.Cos(w), s1_ = PortableMath.Sin(w), c2 = PortableMath.Cos(2 * w), s2_ = PortableMath.Sin(2 * w), c3 = PortableMath.Cos(3 * w), s3_ = PortableMath.Sin(3 * w);
            double nr = b0 + b1 * c1 + b2 * c2 + b3 * c3, ni = -(b1 * s1_ + b2 * s2_ + b3 * s3_);
            double dr = 1 + a1 * c1 + a2 * c2 + a3 * c3, di = -(a1 * s1_ + a2 * s2_ + a3 * s3_);
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }

        public double MagnitudeDb(double hz, double rate)
        {
            double g = Magnitude(hz, rate);
            return g > 1e-12 ? 20.0 * PortableMath.Log10(g) : -240.0;
        }
    }
}
