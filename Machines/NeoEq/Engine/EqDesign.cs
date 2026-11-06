#nullable disable
// Neo EQ (docs/specs/machines.md §5): the bands' design, the one function the engine runs (EqEngine),
// the editor draws, the tests measure against and the agent's eq_fit fits Neo EQ bands with
// (docs/specs/mastering-tools.md §6; the agent links this file). Each band is one TPT state-variable
// section in double precision (the RBJ cookbook biquad's response: the bilinear transform of the
// analog prototype, prewarped at the band frequency), or a cascade of them for the steeper cuts.
// Moved from EqEngine.cs unchanged.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    internal enum BandType { Bell, LowShelf, HighShelf, LowCut, HighCut, Notch }

    /// <summary>One band's settings in physical units.</summary>
    internal struct BandSettings
    {
        public BandType Type;
        public double Hz, GainDb, Q;
        /// <summary>0, 1, 2: 12, 24, 48 dB/oct (cuts).</summary>
        public int Slope;

        public bool HasGain => Type is BandType.Bell or BandType.LowShelf or BandType.HighShelf;
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

        /// <summary>A band's settings from parameter values.</summary>
        public static BandSettings Settings(ParamValues v, int band) => new BandSettings
        {
            Type = (BandType)v.Choice(TypeOf(band)),
            Hz = v.Value(Freq(band)),
            GainDb = v.Value(Gain(band)),
            Q = v.Value(Q(band)),
            Slope = v.Choice(Slope(band)),
        };

        /// <summary>Sections a band needs.</summary>
        public static int SectionCount(BandType type, int slope) =>
            type is BandType.LowCut or BandType.HighCut ? Butterworth.ForSlope(slope).Length : 1;

        /// <summary>Sets the coefficients of a band's sections (keeping their state); returns how many it uses.</summary>
        public static int Design(in BandSettings b, double sampleRate, SvfSection[] s)
        {
            double g = SvfSection.Prewarp(b.Hz, sampleRate);
            double q = Math.Max(0.01, b.Q);
            double A = PortableMath.Pow(10, b.GainDb / 40);
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
