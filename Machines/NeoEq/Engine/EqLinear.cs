#nullable disable
// Neo EQ linear phase (docs/specs/mastering-tools.md §5.6, M5b). The eight bands' zero-phase magnitude, times the curve
// layer, as FIRs of 2D + 1 taps (D samples each side of the centre) on the shared library's partitioned convolution
// (PartitionedConvolver, SteppedFirDesign, LinearPhaseFilters, as Neo Low End's linear split): partitions of P samples,
// 16 of them, a complex FFT of 2P points carrying left + i·right.
//  - Four FIRs make the 2 × 2 matrix the channel bands need. A band on Left, Right, Mid or Side multiplies gL, gR, gM or
//    gS (a Stereo band all of gL and gR); out L = hLL·L + hLR·R, out R = hRL·L + hRR·R with
//    hLL = gL(gM + gS)/2, hLR = gR(gM − gS)/2, hRL = gL(gM − gS)/2, hRR = gR(gM + gS)/2: what the bands' mid/side
//    encoding does in minimum phase, since linear filters commute.
//  - The magnitude of each band is the design's (EqDesign.Design: the section's exact response at the grid frequency,
//    tan(πf/fs) = t, so no tangent); so the linear-phase response is the minimum-phase one's magnitude.
//  - The Q of a band is limited where its ringing would not fit the FIR (time constant Q/(π f) ≤ D/(5 fs)): the editor says so.
//  - A design that is the identity (every band flat, no curve: the matrix 1, 0, 0, 1 to 1e-6 at every frequency) passes the
//    input delayed by D untouched (a ring), bit for bit; the FIR runs beside it, and the partition where a design takes
//    over uses the FIR's crossfade.
//  - Dynamic bands are static here (their setting); Output is outside the FIR (it glides).
//  - A redesign starts when a parameter other than Output or Delta moved, at most every DesignEvery partitions, in
//    bounded steps taken at fixed samples of each partition, crossfading the FIR's output over a partition.
// Latency D + P. No allocation after construction (one per sample rate).

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    internal static class EqLinearDesign
    {
        public const int Partitions = 16;
        /// <summary>Curve layer points, log-spaced from 20 Hz to 20 kHz.</summary>
        public const int CurvePoints = 32;
        public const double CurveLowHz = 20, CurveHighHz = 20000;

        /// <summary>1 up to 50 kHz, 2 up to 100 kHz, then 4: the FIR keeps its length in time.</summary>
        public static int Scale(double sampleRate) => sampleRate <= 50000 ? 1 : sampleRate <= 100000 ? 2 : 4;
        public static int PartitionLength(double sampleRate) => 512 * Scale(sampleRate);
        public static int HalfLength(double sampleRate) => Partitions * PartitionLength(sampleRate) / 2 - 1;
        public static int LinearLatency(double sampleRate) => HalfLength(sampleRate) + PartitionLength(sampleRate);
        public static int MaxLatency => LinearLatency(384000);

        /// <summary>Point <paramref name="i"/> of the curve layer, Hz.</summary>
        public static double CurveHz(int i) => CurveLowHz * PortableMath.Pow(CurveHighHz / CurveLowHz, i / (double)(CurvePoints - 1));

        /// <summary>The longest time constant a band's ringing may have: it is 5 of them (−43 dB) over the FIR's half length.</summary>
        public static double TauMax(double sampleRate) => HalfLength(sampleRate) / (5.0 * sampleRate);

        /// <summary>The highest Q a band at <paramref name="hz"/> keeps: τ = Q/(π f) ≤ τmax (never under 0.1).</summary>
        public static double MaxQ(double hz, double sampleRate) => Math.Max(0.1, Math.PI * hz * TauMax(sampleRate));

        /// <summary>The band as linear phase runs it: its Q limited where it would ring past the FIR.</summary>
        public static BandSettings Limit(in BandSettings b, double sampleRate, out bool capped)
        {
            var r = b;
            capped = false;
            if (b.Shape == BandShape.Baxandall && b.IsShelf) return r;
            double factor = b.Shape == BandShape.ProportionalQ && b.Type == BandType.Bell ? PortableMath.Pow(10, Math.Abs(b.GainDb) / 40) : 1;
            double max = MaxQ(b.Hz, sampleRate) / factor;
            if (b.Q > max)
            {
                r.Q = max;
                capped = true;
            }
            return r;
        }

        /// <summary>The curve layer's gain in dB at point position <paramref name="x"/> (0 … 31): a Catmull–Rom spline through the points, flat beyond the ends.</summary>
        public static double Curve(double[] db, double x)
        {
            if (x <= 0) return db[0];
            if (x >= CurvePoints - 1) return db[CurvePoints - 1];
            int i = (int)x;
            double u = x - i;
            double p0 = db[Math.Max(0, i - 1)], p1 = db[i], p2 = db[i + 1], p3 = db[Math.Min(CurvePoints - 1, i + 2)];
            return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * (p1 - p2) + p3 - p0) * u * u * u);
        }

        static readonly double CurveScale = (CurvePoints - 1) / PortableMath.Log2(CurveHighHz / CurveLowHz);

        /// <summary>Curve position of <paramref name="hz"/>.</summary>
        public static double CurveX(double hz) => hz <= CurveLowHz ? 0 : PortableMath.Log2(hz / CurveLowHz) * CurveScale;

        /// <summary>The curve layer's gain at <paramref name="hz"/> in dB, from parameter values.</summary>
        public static double CurveDb(ParamValues v, double hz)
        {
            var db = new double[CurvePoints];
            for (int i = 0; i < CurvePoints; i++) db[i] = v.Value(P.Curve1 + i);
            return Curve(db, CurveX(hz));
        }
    }

    internal sealed class EqLinear
    {
        public const int DesignEvery = 4;
        public const int DesignStride = 16;

        public readonly int P, K, N, D;
        readonly LinearPhaseFilters filters;
        readonly Target target;
        readonly float[][] firL = new float[4][], firR = new float[4][];
        /// <summary>The partition just processed: the bands applied to its P input samples, delayed by D.</summary>
        public readonly float[] OutL, OutR;
        long wanted, designed;
        readonly float[] ringL, ringR, dryL, dryR;
        readonly int ringMask;
        long written;

        public EqLinear(double sampleRate, ParamValues values)
        {
            P = EqLinearDesign.PartitionLength(sampleRate);
            K = EqLinearDesign.Partitions;
            N = K * P;
            D = EqLinearDesign.HalfLength(sampleRate);
            OutL = new float[P];
            OutR = new float[P];
            for (int f = 0; f < 4; f++)
            {
                firL[f] = new float[P];
                firR[f] = new float[P];
            }
            int ring = 1;
            while (ring < D + 2 * P) ring <<= 1;
            ringL = new float[ring];
            ringR = new float[ring];
            ringMask = ring - 1;
            dryL = new float[P];
            dryR = new float[P];
            target = new Target(this, sampleRate, values);
            filters = new LinearPhaseFilters(P, K, 4, D, target, DesignEvery);
            target.MeasurePreRinging();
        }

        public int Latency => D + P;

        /// <summary>Bands whose Q the design limited, as of the design playing.</summary>
        public int CappedBands => target.Capped;

        /// <summary>A bit per band: its Q is limited (as of the parameters now).</summary>
        public int CappedMask(ParamValues v, double sampleRate)
        {
            int mask = 0;
            for (int b = 0; b < EqDesign.Bands; b++)
            {
                EqLinearDesign.Limit(EqDesign.Settings(v, b), sampleRate, out bool c);
                if (c && v.On(EqDesign.On(b))) mask |= 1 << b;
            }
            return mask;
        }

        /// <summary>Energy before the centre over all of the playing FIR's (left to left), dB; -300 when flat.</summary>
        public double PreRingingDb => target.PreRingingDb;

        /// <summary>The parameters changed: a design is wanted.</summary>
        public void Want() => wanted++;

        public void Reset()
        {
            filters.Reset();
            Array.Clear(ringL);
            Array.Clear(ringR);
            written = 0;
            Array.Clear(OutL);
            Array.Clear(OutR);
        }

        /// <summary>One partition: P input samples in <paramref name="inL"/>/<paramref name="inR"/>; fills OutL/OutR.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] inL, float[] inR)
        {
            for (int n = 0; n < P; n++)
            {
                int w = (int)((written + n) & ringMask);
                ringL[w] = inL[n];
                ringR[w] = inR[n];
            }
            written += P;
            long from = written - P - D;
            for (int n = 0; n < P; n++)
            {
                long t = from + n;
                int r = (int)(t & ringMask);
                dryL[n] = t >= 0 ? ringL[r] : 0;
                dryR[n] = t >= 0 ? ringR[r] : 0;
            }
            target.TookThis = false;
            filters.Process(inL, inR, 4, firL, firR);
            if (target.Identity && !target.TookThis)
            {
                Array.Copy(dryL, OutL, P);
                Array.Copy(dryR, OutR, P);
                return;
            }
            var a0 = firL[0];
            var a1 = firR[1];
            var a2 = firL[2];
            var a3 = firR[3];
            for (int n = 0; n < P; n++)
            {
                OutL[n] = a0[n] + a1[n];
                OutR[n] = a2[n] + a3[n];
            }
        }

        public void DesignStep() => filters.DesignStep();

        /// <summary>The design for the parameters now, whole and playing at once (a start or a phase switch).</summary>
        public void DesignNow() => filters.DesignNow();

        internal float[] Taps(int f) => filters.Design.Taps(f);

        public bool HoldsNonFinite() => filters.Convolver.HoldsNonFinite();

        public bool HoldsSubnormals() => filters.Convolver.HoldsSubnormals();

        sealed class Target : IFirTarget
        {
            readonly EqLinear owner;
            readonly ParamValues values;
            readonly double sampleRate;
            long pending;
            // The latched settings, and the sections designed from them at Begin.
            readonly BandSettings[] band = new BandSettings[EqDesign.Bands];
            readonly bool[] on = new bool[EqDesign.Bands];
            readonly BandChannel[] chan = new BandChannel[EqDesign.Bands];
            readonly double[] curve = new double[EqLinearDesign.CurvePoints];
            readonly SvfSection[][] sections = new SvfSection[EqDesign.Bands][];
            readonly int[] count = new int[EqDesign.Bands];
            bool anyCurve;
            int capped, cappedLatched;
            public int Capped => capped;
            /// <summary>The design playing is the identity (the input passes untouched), and whether one took over in this partition.</summary>
            public bool Identity, TookThis;
            bool idAcc = true;
            public double PreRingingDb { get; private set; } = -300;

            public Target(EqLinear owner, double sampleRate, ParamValues values)
            {
                this.owner = owner;
                this.sampleRate = sampleRate;
                this.values = values;
                for (int b = 0; b < sections.Length; b++) sections[b] = new SvfSection[EqDesign.MaxSections];
            }

            public bool Moved => owner.wanted != owner.designed;

            public void Latch()
            {
                pending = owner.wanted;
                int c = 0;
                anyCurve = false;
                for (int b = 0; b < EqDesign.Bands; b++)
                {
                    on[b] = values.On(EqDesign.On(b));
                    chan[b] = (BandChannel)values.Choice(EqDesign.Channel(b));
                    band[b] = EqLinearDesign.Limit(EqDesign.Settings(values, b), sampleRate, out bool cap);
                    if (cap && on[b]) c++;
                }
                for (int i = 0; i < curve.Length; i++)
                {
                    curve[i] = values.Value(Eq.P.Curve1 + i);
                    if (curve[i] != 0) anyCurve = true;
                }
                cappedLatched = c;
            }

            public void TookOver()
            {
                owner.designed = pending;
                Identity = idAcc;
                TookThis = true;
                capped = cappedLatched;
                if (owner.filters != null) MeasurePreRinging();
            }

            public void MeasurePreRinging()
            {
                // The energy of FIR 0's taps before the centre, over all of them (fixed order).
                var t = owner.filters.Design.Taps(0);
                double pre = 0, all = 0;
                for (int i = 0; i < owner.D; i++) pre += (double)t[i] * t[i];
                all = pre;
                all += (double)t[owner.D] * t[owner.D];
                for (int i = owner.D + 1; i <= 2 * owner.D; i++) all += (double)t[i] * t[i];
                PreRingingDb = pre > 1e-14 * all && all > 0 ? Math.Max(-300, 10 * PortableMath.Log10(pre / all)) : -300;
            }

            public void Begin()
            {
                idAcc = true;
                for (int b = 0; b < EqDesign.Bands; b++)
                    count[b] = on[b] ? EqDesign.Design(band[b], sampleRate, sections[b]) : 0;
            }

            public void At(int k, double t, float[] m)
            {
                double gL = 1, gR = 1, gM = 1, gS = 1;
                for (int b = 0; b < EqDesign.Bands; b++)
                {
                    int n = count[b];
                    if (n == 0) continue;
                    double mag = 1;
                    for (int s = 0; s < n; s++)
                    {
                        ref var sec = ref sections[b][s];
                        double w = double.IsPositiveInfinity(t) ? 1e12 : t / sec.G;
                        double w2 = w * w;
                        double n1 = sec.M0 * sec.K + sec.M1, n0 = sec.M0 + sec.M2;
                        double nr = n0 - sec.M0 * w2, ni = n1 * w;
                        double dr = 1 - w2, di = sec.K * w;
                        mag *= Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
                    }
                    switch (chan[b])
                    {
                        case BandChannel.Stereo: gL *= mag; gR *= mag; break;
                        case BandChannel.Left: gL *= mag; break;
                        case BandChannel.Right: gR *= mag; break;
                        case BandChannel.Mid: gM *= mag; break;
                        default: gS *= mag; break;
                    }
                }
                if (anyCurve)
                {
                    double hz = double.IsPositiveInfinity(t) ? 0.5 * sampleRate : PortableMath.Atan(t) * sampleRate / Math.PI;
                    double c = PortableMath.Pow(10, EqLinearDesign.Curve(curve, EqLinearDesign.CurveX(hz)) / 20);
                    gL *= c;
                    gR *= c;
                }
                m[0] = (float)(gL * (gM + gS) * 0.5);
                m[1] = (float)(gR * (gM - gS) * 0.5);
                m[2] = (float)(gL * (gM - gS) * 0.5);
                m[3] = (float)(gR * (gM + gS) * 0.5);
                if (idAcc && (Math.Abs(m[0] - 1) > 1e-6f || Math.Abs(m[3] - 1) > 1e-6f || Math.Abs(m[1]) > 1e-6f || Math.Abs(m[2]) > 1e-6f)) idAcc = false;
            }
        }
    }
}
