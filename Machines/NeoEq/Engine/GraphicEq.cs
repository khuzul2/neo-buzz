#nullable disable
// Neo EQ's graphic mode (docs/specs/machines.md §5, machine-safety.md §4: H2, the successor of
// Joachims Hyperion): 31 bells at the ISO third-octave centres, 20 Hz – 20 kHz.
//
// - Constant-Q: each bell is the analog prototype H(s) = (s² + (A/Q)s + 1) / (s² + s/(AQ) + 1), whose
//   half-gain points (half the gain in dB) are 2/3 octave apart whatever its gain, cuts the mirror of
//   boosts.
// - No cramping near Nyquist: the digital bell matches the prototype at DC, at its centre, at its lower
//   half-gain point and at Nyquist (after Orfanidis' prescribed-Nyquist-gain design), so the 16 and
//   20 kHz bands keep their shape at 44.1 and 48 kHz; a cut is the boost's inverse.
// - The response follows the sliders: the bands overlap, so their gains are solved for (Gauss–Newton
//   on a weighted least-squares fit at the centres and two points between each pair, after Liski and
//   Välimäki's interaction-matrix design), only when a slider moves. The sliders' median is a plain
//   gain, so all sliders at +X is +X exactly.
// - The bands run as TPT state-variable sections in double precision (as state-space updates of the
//   SVF's own states, for a short chain per sample); their gains glide (a 20 ms
//   one-pole in dB) in 16-sample segments that run across blocks, the sections' coefficients moving
//   in straight lines within each, so fast automation stays smooth and stable and the output doesn't
//   depend on how the host splits it. No allocation after construction.
// Pure: tested by NeoBuzz.MachineTests (EqGraphicTests).

using System.Runtime.CompilerServices;
using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    /// <summary>One bell's constants at a sample rate: what its design needs besides the gain.</summary>
    internal struct BellShape
    {
        /// <summary>tan(π f0 / fs): the centre on the bilinear transform's frequency axis.</summary>
        public double W0;
        /// <summary>W0².</summary>
        public double X;
        /// <summary>tan²(π f0 2^(−1/3) / fs): the prototype's lower half-gain point, squared, on the same axis.</summary>
        public double Y;
        /// <summary>Q² u, u = (1 − ν²)² / ν², ν = (fs / 2) / f0: how far Nyquist is from the centre, for the prototype's gain there.</summary>
        public double QQU;
    }

    /// <summary>The graphic mode's bands: centres, labels and the bell design the engine runs, the editor draws and the tests measure.</summary>
    internal static class GraphicDesign
    {
        public const int Bands = 31;
        public const double MinDb = -18, MaxDb = 18;
        /// <summary>Octaves between a bell's half-gain points, at every gain.</summary>
        public const double Bandwidth = 2.0 / 3;
        /// <summary>Bands at or above this share of the sample rate are left out (none at 44.1 kHz and above).</summary>
        public const double MaxShare = 0.46;
        /// <summary>The largest gain the solver gives a bell (zigzag sliders at ±18 dB need about 54).</summary>
        public const double MaxFilterDb = 60;

        /// <summary>The prototype's Q: 1 / (2^(B/2) − 2^(−B/2)), B the bandwidth in octaves.</summary>
        public static readonly double Q = 1 / (PortableMath.Pow(2, Bandwidth / 2) - PortableMath.Pow(2, -Bandwidth / 2));

        /// <summary>The lower half-gain point over the centre: 2^(−B/2).</summary>
        static readonly double LowerEdge = PortableMath.Pow(2, -Bandwidth / 2);

        /// <summary>The exact ISO 266 centres, 1000 · 10^(k/10) Hz (k = −17 … 13).</summary>
        public static readonly double[] Centres = MakeCentres();

        /// <summary>The nominal centres, as ISO 266 names them.</summary>
        public static readonly string[] Labels =
        {
            "20 Hz", "25 Hz", "31.5 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz", "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz",
            "315 Hz", "400 Hz", "500 Hz", "630 Hz", "800 Hz", "1 kHz", "1.25 kHz", "1.6 kHz", "2 kHz", "2.5 kHz", "3.15 kHz",
            "4 kHz", "5 kHz", "6.3 kHz", "8 kHz", "10 kHz", "12.5 kHz", "16 kHz", "20 kHz",
        };

        static double[] MakeCentres()
        {
            var c = new double[Bands];
            for (int k = 0; k < Bands; k++) c[k] = 1000 * PortableMath.Pow(10, (k - 17) / 10.0);
            return c;
        }

        /// <summary>The slider of band <paramref name="k"/> (parameter index).</summary>
        public static int Slider(int k) => P.G20 + k;

        /// <summary>Bands that run at <paramref name="sampleRate"/>: those below 0.46 fs (all 31 at 44.1 kHz and above).</summary>
        public static int ActiveBands(double sampleRate)
        {
            int n = 0;
            while (n < Bands && Centres[n] < MaxShare * sampleRate) n++;
            return n;
        }

        public static BellShape Shape(int band, double sampleRate)
        {
            double f0 = Centres[band];
            double w0 = PortableMath.Tan(Math.PI * f0 / sampleRate);
            double w1 = PortableMath.Tan(Math.PI * f0 * LowerEdge / sampleRate);
            double nu = 0.5 * sampleRate / f0;
            double d = 1 - nu * nu;
            return new BellShape { W0 = w0, X = w0 * w0, Y = w1 * w1, QQU = Q * Q * d * d / (nu * nu) };
        }

        /// <summary>
        /// The boost of |<paramref name="db"/>| as H(s) = (G1 s² + B s + X) / (s² + A s + X), s = (1 − z⁻¹) / (1 + z⁻¹): the
        /// gain G at the centre and 1 at DC; at the lower half-gain point √G and at Nyquist G1, the prototype's own there.
        /// Written so that nothing cancels as the gain goes to 0, where it is exactly 1 (A = B, G1 = 1).
        /// </summary>
        public static void Boost(in BellShape s, double db, out double g1, out double a, out double b)
        {
            double G = PortableMath.Pow(10, Math.Abs(db) / 20);
            double xy = s.X - s.Y;
            double t = G * s.QQU + 1;
            double a2 = xy / (s.Y * G) * (xy + s.Y * (G + 1) / t);
            double g1m1 = (G - 1) * (G + 1) / t;    // G1² − 1
            g1 = Math.Sqrt(1 + g1m1);
            double e = g1m1 / (1 + g1);              // G1 − 1
            double b2 = G * G * a2 - e * e * s.X;
            a = Math.Sqrt(a2);
            b = Math.Sqrt(Math.Max(0, b2));
        }

        /// <summary>The bell at <paramref name="db"/> as a TPT section's coefficients (<see cref="SvfSection.Set"/>); a cut inverts the boost.</summary>
        public static void Section(in BellShape s, double db, out double g, out double k, out double m0, out double m1, out double m2)
        {
            Boost(s, db, out double g1, out double a, out double b);
            if (db >= 0)
            {
                g = s.W0;
                k = a / s.W0;
                m0 = g1;
                m1 = (b - g1 * a) / s.W0;
                m2 = 1 - g1;
            }
            else
            {
                double r = Math.Sqrt(g1);
                g = s.W0 / r;
                k = b / (s.W0 * r);
                m0 = 1 / g1;
                m1 = (a * g1 - b) / (g1 * s.W0 * r);
                m2 = 1 - 1 / g1;
            }
        }

        /// <summary>|H|² of the bell at <paramref name="db"/> at W² = tan²(π f / fs).</summary>
        public static double Magnitude2(in BellShape s, double db, double w2)
        {
            Boost(s, db, out double g1, out double a, out double b);
            return Magnitude2(s.X, g1, a, b, db >= 0, w2);
        }

        /// <summary>|H|² from <see cref="Boost"/>'s coefficients (a cut: <paramref name="boost"/> false).</summary>
        public static double Magnitude2(double x, double g1, double a, double b, bool boost, double w2)
        {
            double nr = x - g1 * w2, dr = x - w2;
            double num = nr * nr + b * b * w2, den = dr * dr + a * a * w2;
            return boost ? num / den : den / num;
        }

        /// <summary>tan²(π f / fs), f just under Nyquist at most.</summary>
        public static double W2(double hz, double sampleRate)
        {
            double t = PortableMath.Tan(Math.PI * Math.Clamp(hz, 0, sampleRate * 0.49999) / sampleRate);
            return t * t;
        }
    }

    /// <summary>
    /// Solves the bands' gains for the sliders. The sliders' median becomes a plain gain (the offset); the
    /// bands make up the rest, fitted at the active centres (weight 1) and at two points between each
    /// pair (weight 0.15, their targets on straight lines in dB between the sliders): a first estimate
    /// from the bands' interaction at 12 dB (precomputed per sample rate), then three Gauss–Newton
    /// steps on the exact response. Allocates only when constructed.
    /// </summary>
    internal sealed class GraphicSolver
    {
        const int N = GraphicDesign.Bands;
        const int Between = 2;
        const int MaxPoints = N + (N - 1) * Between;
        const double MidWeight = 0.15;
        const double PrototypeDb = 12;
        const int Steps = 3;
        const double Delta = 1e-3;
        const double DbPerLn = 4.3429448190325182765;   // 10 / ln 10

        public readonly BellShape[] Shapes = new BellShape[N];
        /// <summary>The bands' gains in dB (0 for bands left out at low sample rates).</summary>
        public readonly double[] Gains = new double[N];
        /// <summary>The plain gain in dB: the sliders' median.</summary>
        public double Offset;
        public int Active { get; private set; }
        public double SampleRate { get; private set; }

        readonly double[] w2 = new double[MaxPoints], weight = new double[MaxPoints];
        readonly int[] pointBand = new int[MaxPoints];
        readonly double[] pointFrac = new double[MaxPoints];
        readonly double[] first = new double[N * MaxPoints];        // the first estimate: Gains = first · targets
        readonly double[] jac = new double[MaxPoints * N];
        readonly double[] normal = new double[N * N];
        readonly double[] rhs = new double[N], step = new double[N];
        readonly double[] product = new double[MaxPoints], target = new double[MaxPoints];
        readonly double[] relative = new double[N], sorted = new double[N];
        // The solved bands' coefficients (Boost), for ResponseDb.
        readonly double[] cg1 = new double[N], ca = new double[N], cb = new double[N];
        int points;

        public GraphicSolver(double sampleRate = 48000) => SetSampleRate(sampleRate);

        public void SetSampleRate(double sampleRate)
        {
            SampleRate = sampleRate;
            Active = GraphicDesign.ActiveBands(sampleRate);
            for (int k = 0; k < N; k++) Shapes[k] = GraphicDesign.Shape(k, sampleRate);
            points = 0;
            for (int k = 0; k < Active; k++)
                for (int j = 0; j <= Between; j++)
                {
                    if (j > 0 && k == Active - 1) break;
                    double hz = GraphicDesign.Centres[k] * PortableMath.Pow(10, j / (10.0 * (Between + 1)));
                    w2[points] = GraphicDesign.W2(hz, sampleRate);
                    weight[points] = j == 0 ? 1 : MidWeight;
                    pointBand[points] = k;
                    pointFrac[points] = j / (double)(Between + 1);
                    points++;
                }
            int n = Active;
            if (n == 0) return;
            // The interaction at the prototype gain, each column per dB: jac[m, k] = w_m · dB_k(f_m; 12) / 12.
            for (int k = 0; k < n; k++)
            {
                GraphicDesign.Boost(Shapes[k], PrototypeDb, out double g1, out double a, out double b);
                for (int m = 0; m < points; m++)
                    jac[m * n + k] = weight[m] * 10 * PortableMath.Log10(GraphicDesign.Magnitude2(Shapes[k].X, g1, a, b, true, w2[m])) / PrototypeDb;
            }
            Normal(n);
            Cholesky(n);
            // first = (JᵀJ)⁻¹ Jᵀ W: one solve per point.
            for (int m = 0; m < points; m++)
            {
                for (int k = 0; k < n; k++) rhs[k] = jac[m * n + k] * weight[m];
                Substitute(n);
                for (int k = 0; k < n; k++) first[k * MaxPoints + m] = step[k];
            }
            Solve(null);
        }

        /// <summary>Solves for the sliders' values in dB (<paramref name="sliders"/>[k] for band k; null: all 0).</summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Solve(double[] sliders)
        {
            int n = Active;
            Array.Clear(Gains, 0, N);
            Offset = 0;
            for (int k = 0; k < N; k++) GraphicDesign.Boost(Shapes[k], 0, out cg1[k], out ca[k], out cb[k]);
            if (n == 0) return;
            for (int k = 0; k < n; k++) sorted[k] = sliders?[k] ?? 0;
            // The median (insertion sort: 31 values).
            for (int i = 1; i < n; i++)
            {
                double v = sorted[i];
                int j = i - 1;
                while (j >= 0 && sorted[j] > v)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = v;
            }
            Offset = (n & 1) == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
            for (int k = 0; k < n; k++) relative[k] = (sliders?[k] ?? 0) - Offset;
            for (int m = 0; m < points; m++)
            {
                int k = pointBand[m];
                double f = pointFrac[m];
                target[m] = f == 0 ? relative[k] : relative[k] + (relative[k + 1] - relative[k]) * f;
            }
            for (int k = 0; k < n; k++)
            {
                double s = 0;
                for (int m = 0; m < points; m++) s += first[k * MaxPoints + m] * target[m];
                Gains[k] = s;
            }
            for (int it = 0; it < Steps; it++)
            {
                // The response now (one log per point: the bands' |H|² multiplied) and its derivative per band
                // (forward differences of 0.001 dB), each row times its weight.
                for (int m = 0; m < points; m++) product[m] = 1;
                for (int k = 0; k < n; k++)
                {
                    ref readonly var s = ref Shapes[k];
                    double g = Gains[k], gd = g + Delta;
                    GraphicDesign.Boost(s, g, out double g1, out double a, out double b);
                    GraphicDesign.Boost(s, gd, out double g1d, out double ad, out double bd);
                    bool up = g >= 0, upd = gd >= 0;
                    for (int m = 0; m < points; m++)
                    {
                        double h = GraphicDesign.Magnitude2(s.X, g1, a, b, up, w2[m]);
                        double hd = GraphicDesign.Magnitude2(s.X, g1d, ad, bd, upd, w2[m]);
                        product[m] *= h;
                        jac[m * n + k] = weight[m] * DbPerLn * (hd / h - 1) / Delta;
                    }
                }
                Normal(n);
                for (int k = 0; k < n; k++) rhs[k] = 0;
                for (int m = 0; m < points; m++)
                {
                    double e = weight[m] * (target[m] - 10 * PortableMath.Log10(product[m]));
                    for (int k = 0; k < n; k++) rhs[k] += jac[m * n + k] * e;
                }
                Cholesky(n);
                Substitute(n);
                for (int k = 0; k < n; k++)
                    Gains[k] = Math.Clamp(Gains[k] + step[k], -GraphicDesign.MaxFilterDb, GraphicDesign.MaxFilterDb);
            }
            for (int k = 0; k < n; k++) GraphicDesign.Boost(Shapes[k], Gains[k], out cg1[k], out ca[k], out cb[k]);
        }

        /// <summary>The response in dB at <paramref name="hz"/>: the offset and every band (no output gain).</summary>
        public double ResponseDb(double hz)
        {
            double w = GraphicDesign.W2(hz, SampleRate), p = 1;
            for (int k = 0; k < Active; k++) p *= GraphicDesign.Magnitude2(Shapes[k].X, cg1[k], ca[k], cb[k], Gains[k] >= 0, w);
            return Offset + 10 * PortableMath.Log10(p);
        }

        // normal = JᵀJ (lower triangle used).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Normal(int n)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double s = 0;
                    for (int m = 0; m < points; m++) s += jac[m * n + i] * jac[m * n + j];
                    normal[i * N + j] = s;
                }
        }

        // In place: normal's lower triangle becomes L, normal = L Lᵀ.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Cholesky(int n)
        {
            for (int j = 0; j < n; j++)
            {
                double d = normal[j * N + j];
                for (int k = 0; k < j; k++) d -= normal[j * N + k] * normal[j * N + k];
                d = Math.Sqrt(Math.Max(d, 1e-300));
                normal[j * N + j] = d;
                for (int i = j + 1; i < n; i++)
                {
                    double s = normal[i * N + j];
                    for (int k = 0; k < j; k++) s -= normal[i * N + k] * normal[j * N + k];
                    normal[i * N + j] = s / d;
                }
            }
        }

        // step = (L Lᵀ)⁻¹ rhs.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Substitute(int n)
        {
            for (int i = 0; i < n; i++)
            {
                double s = rhs[i];
                for (int k = 0; k < i; k++) s -= normal[i * N + k] * step[k];
                step[i] = s / normal[i * N + i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = step[i];
                for (int k = i + 1; k < n; k++) s -= normal[k * N + i] * step[k];
                step[i] = s / normal[i * N + i];
            }
        }
    }

    /// <summary>
    /// One band for both channels: the TPT state-variable section (<see cref="SvfSection"/>, whose
    /// coefficients it takes) written as its state-space update, so each step's chain through the
    /// state is one multiply and one add: s′ = C s + B x, y = E·(x, s). The states are the SVF's own
    /// (its integrators), so it behaves as the SVF does when its coefficients move.
    /// </summary>
    internal struct StereoBell
    {
        double c11, c12, c21, c22, b1, b2, e0, e1, e2;
        double l1, l2, r1, r2;

        /// <summary>The section's coefficients as <see cref="SvfSection.Set"/> takes them.</summary>
        public void Set(double g, double k, double m0, double m1, double m2)
        {
            double a1 = 1.0 / (1.0 + g * (g + k)), a2 = g * a1, a3 = g * a2;
            c11 = 2 * a1 - 1;
            c12 = -2 * a2;
            b1 = 2 * a2;
            c21 = 2 * a2;
            c22 = 1 - 2 * a3;
            b2 = 2 * a3;
            e0 = m0 + m1 * a2 + m2 * a3;
            e1 = m1 * a1 + m2 * a2;
            e2 = m2 * (1 - a3) - m1 * a2;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Process(ref double left, ref double right)
        {
            double x = left, s1 = l1, s2 = l2;
            left = e0 * x + e1 * s1 + e2 * s2;
            l1 = b1 * x + c12 * s2 + c11 * s1;
            l2 = b2 * x + c21 * s1 + c22 * s2;
            x = right;
            s1 = r1;
            s2 = r2;
            right = e0 * x + e1 * s1 + e2 * s2;
            r1 = b1 * x + c12 * s2 + c11 * s1;
            r2 = b2 * x + c21 * s1 + c22 * s2;
        }

        /// <summary>Updates the state without the output (a band at exactly 0 dB, which passes its input untouched).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Track(double left, double right)
        {
            double s1 = l1, s2 = l2;
            l1 = b1 * left + c12 * s2 + c11 * s1;
            l2 = b2 * left + c21 * s1 + c22 * s2;
            s1 = r1;
            s2 = r2;
            r1 = b1 * right + c12 * s2 + c11 * s1;
            r2 = b2 * right + c21 * s1 + c22 * s2;
        }

        public void Reset() { l1 = l2 = r1 = r2 = 0; }

        /// <summary>Flushes tiny state (once per block), as <see cref="SvfSection.Flush"/> does.</summary>
        public void Flush()
        {
            l1 = DspMath.Flush(l1);
            l2 = DspMath.Flush(l2);
            r1 = DspMath.Flush(r1);
            r2 = DspMath.Flush(r2);
        }
    }

    /// <summary>
    /// The graphic mode's bands as the engine runs them: the solver's gains, gliding in 16-sample
    /// segments, through a TPT section per band (both channels), then the offset gain.
    /// </summary>
    internal sealed class GraphicBank
    {
        const int N = GraphicDesign.Bands;
        /// <summary>Samples per glide segment.</summary>
        public const int Segment = 16;
        const double GlideSeconds = 0.02;
        const double Snap = 1e-4;

        public readonly GraphicSolver Solver = new GraphicSolver();
        readonly StereoBell[] bells = new StereoBell[N];
        readonly double[] now = new double[N], want = new double[N];
        // Coefficients now, at the segment's end and per sample: 5 per band (g, k, m0, m1, m2).
        readonly double[] cur = new double[N * 5], end = new double[N * 5], inc = new double[N * 5];
        readonly int[] rampLeft = new int[N];
        readonly double[] sliders = new double[N];
        double offNow, offWant, gainNow, gainEnd, gainInc;
        int gainLeft;
        int segPos;
        double segA;

        /// <summary>True until <see cref="Restart"/>: the sections hold nothing worth keeping (the mode was off).</summary>
        public bool Cold { get; set; } = true;

        public void SetSampleRate(double sampleRate)
        {
            Solver.SetSampleRate(sampleRate);
            segA = 1 - PortableMath.Exp(-Segment / (GlideSeconds * sampleRate));
            Cold = true;
        }

        /// <summary>Solves for the sliders' values (from the parameters); the bands glide to the result.</summary>
        public void SetSliders(ParamValues v)
        {
            for (int k = 0; k < N; k++) sliders[k] = v.Value(GraphicDesign.Slider(k));
            Solver.Solve(sliders);
            for (int k = 0; k < N; k++) want[k] = Solver.Gains[k];
            offWant = Solver.Offset;
        }

        /// <summary>Starts from clean sections at the solved gains, with no glide.</summary>
        public void Restart()
        {
            for (int k = 0; k < N; k++)
            {
                now[k] = want[k];
                rampLeft[k] = 0;
                Design(k, now[k], cur);
                Apply(k);
                bells[k].Reset();
            }
            offNow = offWant;
            gainNow = DspMath.DbToGain(offNow);
            gainLeft = 0;
            Cold = false;
        }

        /// <summary>True while any band or the offset is still gliding.</summary>
        public bool Moving
        {
            get
            {
                if (offNow != offWant || gainLeft > 0) return true;
                for (int k = 0; k < N; k++) if (now[k] != want[k] || rampLeft[k] > 0) return true;
                return false;
            }
        }

        /// <summary>Every band settled at exactly 0 dB and the offset at 0 dB: the bank passes its input untouched.</summary>
        public bool Identity
        {
            get
            {
                if (gainNow != 1 || offNow != offWant || gainLeft > 0) return false;
                for (int k = 0; k < Solver.Active; k++) if (now[k] != 0 || want[k] != 0 || rampLeft[k] > 0) return false;
                return true;
            }
        }

        /// <summary>A band's gain in dB now (gliding).</summary>
        public double GainNow(int band) => now[band];

        void Design(int k, double db, double[] into)
        {
            GraphicDesign.Section(Solver.Shapes[k], db, out into[k * 5], out into[k * 5 + 1], out into[k * 5 + 2], out into[k * 5 + 3], out into[k * 5 + 4]);
        }

        void Apply(int k)
        {
            int o = k * 5;
            bells[k].Set(cur[o], cur[o + 1], cur[o + 2], cur[o + 3], cur[o + 4]);
        }

        /// <summary>Processes a stereo block in place (double precision).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(double[] left, double[] right, int n)
        {
            int active = Solver.Active;
            for (int k = 0; k < active; k++)
            {
                ref var bell = ref bells[k];
                if (now[k] == want[k] && rampLeft[k] == 0)
                {
                    // Settled: its coefficients stay as they are for the whole block. At exactly 0 dB it passes the
                    // signal untouched, its state still running so that it moves off cleanly.
                    if (now[k] == 0)
                        for (int i = 0; i < n; i++) bell.Track(left[i], right[i]);
                    else
                        for (int i = 0; i < n; i++) bell.Process(ref left[i], ref right[i]);
                    bell.Flush();
                    continue;
                }
                int o = k * 5;
                int pos = segPos;
                for (int i = 0; i < n; i++)
                {
                    if (pos == 0 && now[k] != want[k])
                    {
                        // A segment starts: glide one segment's worth, then move the coefficients there in a straight line.
                        double g = now[k] + (want[k] - now[k]) * segA;
                        if (Math.Abs(want[k] - g) <= Snap) g = want[k];
                        now[k] = g;
                        Design(k, g, end);
                        for (int c = 0; c < 5; c++) inc[o + c] = (end[o + c] - cur[o + c]) / Segment;
                        rampLeft[k] = Segment;
                    }
                    if (rampLeft[k] > 0)
                    {
                        if (--rampLeft[k] == 0)
                            for (int c = 0; c < 5; c++) cur[o + c] = end[o + c];
                        else
                            for (int c = 0; c < 5; c++) cur[o + c] += inc[o + c];
                        bell.Set(cur[o], cur[o + 1], cur[o + 2], cur[o + 3], cur[o + 4]);
                    }
                    bell.Process(ref left[i], ref right[i]);
                    if (++pos == Segment) pos = 0;
                }
                bell.Flush();
            }
            // The offset: a plain gain, gliding as the bands do.
            {
                int pos = segPos;
                for (int i = 0; i < n; i++)
                {
                    if (pos == 0 && offNow != offWant)
                    {
                        double g = offNow + (offWant - offNow) * segA;
                        if (Math.Abs(offWant - g) <= Snap) g = offWant;
                        offNow = g;
                        gainEnd = DspMath.DbToGain(g);
                        gainInc = (gainEnd - gainNow) / Segment;
                        gainLeft = Segment;
                    }
                    if (gainLeft > 0)
                    {
                        if (--gainLeft == 0) gainNow = gainEnd;
                        else gainNow += gainInc;
                    }
                    if (gainNow != 1)
                    {
                        left[i] *= gainNow;
                        right[i] *= gainNow;
                    }
                    if (++pos == Segment) pos = 0;
                }
            }
            segPos = (segPos + n) % Segment;
        }
    }
}
