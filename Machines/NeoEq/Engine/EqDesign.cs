#nullable disable
// Neo EQ (docs/specs/machines.md §5): the bands' design, the one function the engine runs (EqEngine),
// the editor draws, the tests measure against and the agent's eq_fit fits Neo EQ bands with
// (docs/specs/mastering-tools.md §6; the agent links this file). Each band is one TPT state-variable
// section in double precision (the RBJ cookbook biquad's response: the bilinear transform of the
// analog prototype, prewarped at the band frequency), or a cascade of them for the steeper cuts.
// Moved from EqEngine.cs unchanged.
// v2 (docs/specs/mastering-tools.md §5.3, M3): a shape per band (Standard is v1's, operation for operation):
//  - Proportional Q (bells): Q × 10^(|gain|/40), about twice as narrow for every 12 dB.
//  - Baxandall (shelves): first order, (s + √V)/(s + 1/√V) for a low shelf of gain V (V(s + 1/√V)/(s + √V) high),
//    half the dB gain at the frequency, 6 dB/oct; one SVF section whose numerator shares the pole at −√V.
//  - Vintage (shelves): the shelf, then a bell of half its gain the other way, Q 1, an octave above a low shelf's
//    frequency or below a high shelf's (Pultec's boost-and-cut dip).
//  - All-pass (any type): (s² − ks + 1)/(s² + ks + 1), k = 1/Q: the phase turns, the magnitude stays 1.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    internal enum BandType { Bell, LowShelf, HighShelf, LowCut, HighCut, Notch }

    /// <summary>v2: how a band's type is drawn (Standard: as v1).</summary>
    internal enum BandShape { Standard, ProportionalQ, Baxandall, Vintage, AllPass }

    /// <summary>v2: the channel a band works on.</summary>
    internal enum BandChannel { Stereo, Left, Right, Mid, Side }

    /// <summary>One band's settings in physical units.</summary>
    internal struct BandSettings
    {
        public BandType Type;
        public double Hz, GainDb, Q;
        /// <summary>0, 1, 2: 12, 24, 48 dB/oct (cuts).</summary>
        public int Slope;
        /// <summary>v2 (default Standard: v1's band).</summary>
        public BandShape Shape;

        public bool HasGain => (Type is BandType.Bell or BandType.LowShelf or BandType.HighShelf) && Shape != BandShape.AllPass;
        public bool IsShelf => Type is BandType.LowShelf or BandType.HighShelf;
    }

    /// <summary>The bands' filters: what the engine runs and the editor draws.</summary>
    internal static class EqDesign
    {
        public const int Bands = 8;
        public const int MaxSections = 4;
        /// <summary>Parameters per band, in Params.def order: on, type, freq, gain, Q, slope.</summary>
        public const int PerBand = 6;

        public static int On(int band) => band * PerBand;
        public static int TypeOf(int band) => band * PerBand + 1;
        public static int Freq(int band) => band * PerBand + 2;
        public static int Gain(int band) => band * PerBand + 3;
        public static int Q(int band) => band * PerBand + 4;
        public static int Slope(int band) => band * PerBand + 5;

        /// <summary>v2's parameters per band, in Params.def order after the graphic sliders.</summary>
        public const int PerBandV2 = 10;
        public static int Channel(int band) => P.B1Chan + band * PerBandV2;
        public static int ShapeOf(int band) => P.B1Chan + band * PerBandV2 + 1;
        public static int Dynamic(int band) => P.B1Chan + band * PerBandV2 + 2;
        public static int Threshold(int band) => P.B1Chan + band * PerBandV2 + 3;
        public static int Ratio(int band) => P.B1Chan + band * PerBandV2 + 4;
        public static int Knee(int band) => P.B1Chan + band * PerBandV2 + 5;
        public static int Range(int band) => P.B1Chan + band * PerBandV2 + 6;
        public static int Attack(int band) => P.B1Chan + band * PerBandV2 + 7;
        public static int Release(int band) => P.B1Chan + band * PerBandV2 + 8;
        public static int Detect(int band) => P.B1Chan + band * PerBandV2 + 9;

        /// <summary>A band's settings from parameter values.</summary>
        public static BandSettings Settings(ParamValues v, int band) => new BandSettings
        {
            Type = (BandType)v.Choice(TypeOf(band)),
            Hz = v.Value(Freq(band)),
            GainDb = v.Value(Gain(band)),
            Q = v.Value(Q(band)),
            Slope = v.Choice(Slope(band)),
            Shape = (BandShape)v.Choice(ShapeOf(band)),
        };

        /// <summary>Sections a band needs.</summary>
        public static int SectionCount(BandType type, int slope) =>
            type is BandType.LowCut or BandType.HighCut ? Butterworth.ForSlope(slope).Length : 1;

        /// <summary>Sections a band needs, with its shape (v2: a Vintage shelf takes two, an all-pass one).</summary>
        public static int SectionCount(in BandSettings b) =>
            b.Shape == BandShape.AllPass ? 1 : b.Shape == BandShape.Vintage && b.IsShelf ? 2 : SectionCount(b.Type, b.Slope);

        /// <summary>The gain a Vintage shelf's dip takes (half the shelf's, the other way), and where (an octave inside the shelf).</summary>
        public static double VintageDipDb(double shelfDb) => -0.5 * shelfDb;
        public static double VintageDipHz(BandType type, double hz) => type == BandType.LowShelf ? 2 * hz : 0.5 * hz;

        /// <summary>Sets the coefficients of a band's sections (keeping their state); returns how many it uses.</summary>
        public static int Design(in BandSettings b, double sampleRate, SvfSection[] s)
        {
            double g = SvfSection.Prewarp(b.Hz, sampleRate);
            double q = Math.Max(0.01, b.Q);
            double A = PortableMath.Pow(10, b.GainDb / 40);
            if (b.Shape != BandShape.Standard)
            {
                int n = DesignV2(b, sampleRate, s, g, q, A);
                if (n > 0) return n;
            }
            switch (b.Type)
            {
                case BandType.Bell:
                {
                    double k = 1 / (q * A);
                    s[0].Set(g, k, 1, k * (A * A - 1), 0);
                    return 1;
                }
                case BandType.LowShelf:
                {
                    double k = 1 / q;
                    s[0].Set(g / Math.Sqrt(A), k, 1, k * (A - 1), A * A - 1);
                    return 1;
                }
                case BandType.HighShelf:
                {
                    double k = 1 / q;
                    s[0].Set(g * Math.Sqrt(A), k, A * A, k * (1 - A) * A, 1 - A * A);
                    return 1;
                }
                case BandType.Notch:
                {
                    double k = 1 / q;
                    s[0].Set(g, k, 1, -k, 0);
                    return 1;
                }
                default:
                {
                    // Butterworth sections; Q scales the resonance of the sharpest one (0.71: flat).
                    var qs = Butterworth.ForSlope(b.Slope);
                    for (int i = 0; i < qs.Length; i++)
                    {
                        double qi = i == qs.Length - 1 ? qs[i] * q / Math.Sqrt(0.5) : qs[i];
                        if (b.Type == BandType.LowCut) s[i].SetHighPass(g, qi);
                        else s[i].SetLowPass(g, qi);
                    }
                    return qs.Length;
                }
            }
        }

        /// <summary>v2's shapes; 0 where the shape doesn't apply to the type (it runs as Standard).</summary>
        static int DesignV2(in BandSettings b, double sampleRate, SvfSection[] s, double g, double q, double A)
        {
            switch (b.Shape)
            {
                case BandShape.AllPass:
                {
                    double k = 1 / q;
                    s[0].Set(g, k, 1, -2 * k, 0);
                    return 1;
                }
                case BandShape.ProportionalQ when b.Type == BandType.Bell:
                {
                    double qp = q * PortableMath.Pow(10, Math.Abs(b.GainDb) / 40);
                    double k = 1 / (qp * A);
                    s[0].Set(g, k, 1, k * (A * A - 1), 0);
                    return 1;
                }
                case BandShape.Baxandall when b.IsShelf:
                {
                    // One SVF at the frequency, its poles at −A and −1/A (k = A + 1/A); the numerator's double zero shares −A
                    // (low) or −1/A (high), leaving a first-order shelf. A = √V.
                    double k = A + 1 / A;
                    if (b.Type == BandType.LowShelf) s[0].Set(g, k, 1, A - 1 / A, A * A - 1);
                    else s[0].Set(g, k, A * A, A * (1 - A * A), 1 - A * A);
                    return 1;
                }
                case BandShape.Vintage when b.IsShelf:
                {
                    var shelf = b;
                    shelf.Shape = BandShape.Standard;
                    Design(shelf, sampleRate, s);
                    double gd = SvfSection.Prewarp(VintageDipHz(b.Type, b.Hz), sampleRate);
                    double Ad = PortableMath.Pow(10, VintageDipDb(b.GainDb) / 40);
                    double kd = 1 / Ad;
                    s[1].Set(gd, kd, 1, kd * (Ad * Ad - 1), 0);
                    return 2;
                }
                default:
                    return 0;
            }
        }

        [ThreadStatic] static SvfSection[] scratch;

        /// <summary>A band's magnitude at <paramref name="hz"/> (linear).</summary>
        public static double Magnitude(in BandSettings b, double hz, double sampleRate)
        {
            var s = scratch ??= new SvfSection[MaxSections];
            int n = Design(b, sampleRate, s);
            double m = 1;
            for (int i = 0; i < n; i++) m *= s[i].Magnitude(hz, sampleRate);
            return m;
        }

        /// <summary>The whole EQ's magnitude at <paramref name="hz"/> in dB: every band that is on, and the output gain.</summary>
        public static double ResponseDb(ParamValues v, double hz, double sampleRate)
        {
            double db = v.Value(P.Output);
            for (int b = 0; b < Bands; b++)
                if (v.On(On(b))) db += 20 * PortableMath.Log10(Math.Max(1e-12, Magnitude(Settings(v, b), hz, sampleRate)));
            return db;
        }
    }
}
