#nullable disable
// Neo Mallet (docs/specs/neo-mallet.md): the mode tables. A model is a list of resonant modes, each with a frequency ratio to the played
// note, a base gain and a shape number that the strike position reads. The ratios are written here from published acoustics (clean room):
//   - free-free bar: the Euler-Bernoulli bending modes, frequency proportional to (beta_n L)^2 with beta_n L = 4.7300, 7.8532, 10.9956,
//     14.1372 and (n + 1/2) pi above (Fletcher and Rossing, The Physics of Musical Instruments, 2nd ed., ch. 2 and 19):
//     1 : 2.756 : 5.404 : 8.933 : 13.34 ...
//   - tuned (undercut) bars: the maker tunes the second and third modes to 4 and 10 times the first (marimba, vibraphone) or 3 and 6
//     times (xylophone) (Fletcher and Rossing ch. 19; Rossing, Science of Percussion Instruments, ch. 3 and 4); above the last tuned
//     mode the spacing of the free bar continues.
//   - bell: the partials of a church bell, hum, prime, minor third (tierce), fifth (quint), nominal and above, as measured (Rossing,
//     Science of Percussion Instruments, ch. 8; Fletcher and Rossing ch. 21): 0.5, 1, 1.183, 1.506, 2, 2.514, 2.662, 3.011, 4.166,
//     5.433, 6.796, 8.215, with the further partials spread about evenly and weaker.
//   - tine (clamped-free bar): beta_n L = 1.8751, 4.6941, 7.8548, 10.9955, then (n - 1/2) pi: 1 : 6.267 : 17.55 : 34.39 : 56.84 ...
//   - stiff string: f_n = n f_1 sqrt(1 + B n^2) (Fletcher and Rossing ch. 12), B the inharmonicity coefficient.
//   - open tube (a struck or blown air column): harmonics n f_1, the higher ones flattened by the frequency-dependent end
//     correction, about 0.4 % per harmonic (Fletcher and Rossing ch. 8).
//   - glass (a bowl or goblet): the ring modes of a thin shell, f_n proportional to n (n^2 - 1) / sqrt(n^2 + 1), n = 2, 3, ... (Rayleigh;
//     Rossing, Science of Percussion Instruments, ch. 9), each a doublet whose members the strike angle weighs by cos and sin of n times it.
//   - plate: a thin rectangular plate with sides in the ratio 1 : 1.3, frequency proportional to m^2 + (n / 1.3)^2 (Leissa, Vibration of
//     Plates; Fletcher and Rossing ch. 13), the first 32 sorted by frequency.
// Pure and allocation-free after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Mallet
{
    internal enum MalletModel { SoftBar, HardBar, MetalBar, BrightBar, Tube, Bell, Tine, Pluck, Glass, Plate, Custom }

    /// <summary>The modes of one model at the current settings.</summary>
    internal sealed class ModeSet
    {
        public const int Max = 32;
        public readonly double[] Ratio = new double[Max];
        public readonly float[] BaseGain = new float[Max];
        /// <summary>The strike-position shape number of each mode (a mode is weighed |sin(m pi x)|).</summary>
        public readonly float[] Shape = new float[Max];
        /// <summary>Glass: the mode is the second member of a doublet (weighed |cos| instead of |sin| of the strike angle).</summary>
        public readonly bool[] Partner = new bool[Max];
        public int Count;
        /// <summary>The mode that sounds the played note (ratio 1).</summary>
        public int Ref;
        /// <summary>1 for a model whose high modes die as the damping tilt says, lower for a model that rings on in the highs.</summary>
        public float TiltScale = 1f;
        /// <summary>Strings and tines are plucked by a short pick, the rest struck.</summary>
        public bool Plucked;
        /// <summary>The model's level trim: chords of every model sound about as loud (measured, docs/specs/neo-mallet.md).</summary>
        public float Level = 1f;
    }

    internal static class MalletModels
    {
        /// <summary>The weight floor of the strike position: a strike at a node still sounds a little (-30 dB).</summary>
        public const float PositionFloor = 0.03f;

        static readonly float[] Trim = { 1.25f, 0.94f, 1.05f, 0.90f, 0.70f, 0.30f, 1.03f, 0.66f, 0.79f, 0.78f, 0.79f };

        static readonly double[] FreeBeta = { 4.730040745, 7.853204624, 10.99560784, 14.13716549 };
        static readonly double[] ClampedBeta = { 1.875104069, 4.694091133, 7.854757438, 10.99554073 };

        /// <summary>Free-free bar ratio of mode n (1 based): 1, 2.756, 5.404, 8.933, 13.344 ...</summary>
        public static double FreeBar(int n)
        {
            double b = n <= FreeBeta.Length ? FreeBeta[n - 1] : (n + 0.5) * Math.PI;
            double r = b / FreeBeta[0];
            return r * r;
        }

        /// <summary>Clamped-free bar (a tine, a reed) ratio of mode n (1 based): 1, 6.267, 17.55, 34.39 ...</summary>
        public static double Clamped(int n)
        {
            double b = n <= ClampedBeta.Length ? ClampedBeta[n - 1] : (n - 0.5) * Math.PI;
            double r = b / ClampedBeta[0];
            return r * r;
        }

        static readonly double[] BellPartials = { 0.5, 1.0, 1.183, 1.506, 2.0, 2.514, 2.662, 3.011, 4.166, 5.433, 6.796, 8.215 };

        /// <summary>Stiff-string inharmonicity coefficient B for the Inharmonicity setting (0 ... 1): 0.02 at 100 %.</summary>
        public static double StiffnessB(double inharmonicity) => 0.02 * inharmonicity * inharmonicity;

        /// <summary>The signed weight of mode <paramref name="k"/> for a strike at <paramref name="x"/> (0 ... 1 along the bar, round the ring, across the plate).</summary>
        public static float Weight(ModeSet set, int k, double x)
        {
            double m = set.Shape[k];
            double a = m * Math.PI * x;
            double sh = set.Partner[k] ? PortableMath.Cos(a) : PortableMath.Sin(a);
            double w = PositionFloor + (1.0 - PositionFloor) * Math.Abs(sh);
            // The sign is the mode shape's: neighbouring modes move in opposite senses at a strike, so the strike does not start them all in phase
            // (which would add up to one sharp pulse).
            return (float)(sh < 0 ? -w : w);
        }

        /// <summary>Fills <paramref name="s"/> with the modes of <paramref name="model"/>, at most <paramref name="modes"/> of them.</summary>
        public static void Fill(ModeSet s, MalletModel model, int modes, double stretchCents, double inharmonicity)
        {
            modes = Math.Clamp(modes, 1, ModeSet.Max);
            int n = 0;
            double rolloff;
            s.TiltScale = 1f;
            s.Plucked = false;
            s.Level = Trim[(int)model];
            s.Ref = 0;
            Array.Clear(s.Partner, 0, ModeSet.Max);
            switch (model)
            {
                case MalletModel.SoftBar: Tuned(s, modes, 4, 10, out rolloff); s.TiltScale = 1f; rolloff = 0.4; break;
                case MalletModel.HardBar: Tuned(s, modes, 3, 6, out rolloff); s.TiltScale = 0.8f; rolloff = 0.2; break;
                case MalletModel.MetalBar: Tuned(s, modes, 4, 10, out rolloff); s.TiltScale = 0.35f; rolloff = 0.15; break;
                case MalletModel.BrightBar:
                    for (int i = 0; i < modes; i++) { s.Ratio[i] = FreeBar(i + 1); s.Shape[i] = i + 1; }
                    rolloff = 0.1; s.TiltScale = 0.5f; break;
                case MalletModel.Tube:
                    for (int i = 0; i < modes; i++) { int h = i + 1; s.Ratio[i] = h * (1.0 - 0.004 * (h - 1)); s.Shape[i] = h; }
                    rolloff = 0.5; s.TiltScale = 0.6f; break;
                case MalletModel.Bell:
                    for (int i = 0; i < modes; i++)
                    {
                        s.Ratio[i] = i < BellPartials.Length ? BellPartials[i] : BellPartials[BellPartials.Length - 1] + 1.45 * (i - BellPartials.Length + 1);
                        s.Shape[i] = (float)(2.0 * s.Ratio[i]);       // a bell's own shape: the strike position reads each partial at its own wavelength
                    }
                    s.Ref = 1;
                    rolloff = 0.25; s.TiltScale = 0.4f; break;
                case MalletModel.Tine:
                    for (int i = 0; i < modes; i++) { s.Ratio[i] = Clamped(i + 1); s.Shape[i] = i + 1; }
                    rolloff = 0.35; s.TiltScale = 0.7f; s.Plucked = true; break;
                case MalletModel.Pluck:
                {
                    double b = StiffnessB(inharmonicity);
                    for (int i = 0; i < modes; i++) { int h = i + 1; s.Ratio[i] = h * Math.Sqrt(1.0 + b * h * h); s.Shape[i] = h; }
                    rolloff = 1.1; s.TiltScale = 0.8f; s.Plucked = true; break;
                }
                case MalletModel.Glass:
                {
                    int ring = 2;
                    double f2 = Ring(2);
                    for (; n < modes; ring++)
                    {
                        double r = Ring(ring) / f2;
                        s.Ratio[n] = r; s.Shape[n] = ring; s.Partner[n] = false; n++;
                        if (n < modes) { s.Ratio[n] = r * 1.0035; s.Shape[n] = ring; s.Partner[n] = true; n++; }
                    }
                    rolloff = 0.5; s.TiltScale = 0.3f; break;
                }
                case MalletModel.Plate:
                    PlateModes(s, modes);
                    rolloff = 0.4; s.TiltScale = 0.6f; break;
                default:
                {
                    double b = StiffnessB(inharmonicity);
                    for (int i = 0; i < modes; i++)
                    {
                        int h = i + 1;
                        s.Ratio[i] = h * PortableMath.Pow(2.0, (h - 1) * stretchCents / 1200.0) * Math.Sqrt(1.0 + b * h * h);
                        s.Shape[i] = h;
                    }
                    rolloff = 0.7; break;
                }
            }
            s.Count = modes;
            if (s.Ref >= modes) s.Ref = 0;
            // The base gain of a mode falls with its ratio (the mallet's own spectrum is the pulse, the rest is the body's response).
            for (int i = 0; i < modes; i++)
            {
                double r = Math.Max(0.05, s.Ratio[i]);
                s.BaseGain[i] = (float)PortableMath.Pow(r / Math.Max(0.05, s.Ratio[s.Ref]), -rolloff);
            }
            if (model == MalletModel.Bell) { s.BaseGain[0] *= 0.8f; for (int i = 12; i < modes; i++) s.BaseGain[i] *= 0.5f; }
            if (model == MalletModel.Glass) for (int i = 0; i < modes; i++) if (s.Partner[i]) s.BaseGain[i] *= 0.8f;
            if (model == MalletModel.Plate)
                for (int i = 0; i < modes; i++) s.BaseGain[i] *= PlateY[i];
        }

        static void Tuned(ModeSet s, int modes, double second, double third, out double rolloff)
        {
            rolloff = 0;
            for (int i = 0; i < modes; i++)
            {
                int n = i + 1;
                s.Ratio[i] = n == 1 ? 1.0 : n == 2 ? second : n == 3 ? third : third * FreeBar(n) / FreeBar(3);
                s.Shape[i] = n;
            }
        }

        static double Ring(int n) => n * (n * n - 1.0) / Math.Sqrt(n * (double)n + 1.0);

        // Plate modes (m, n): sorted once, shared.
        static readonly double[] PlateRatio = new double[ModeSet.Max];
        static readonly float[] PlateM = new float[ModeSet.Max];
        static readonly float[] PlateY = new float[ModeSet.Max];

        static MalletModels()
        {
            var list = new System.Collections.Generic.List<(double f, int m, int n)>();
            const double aspect = 1.3;
            for (int m = 1; m <= 8; m++)
                for (int n = 1; n <= 8; n++)
                    list.Add((m * (double)m + (n / aspect) * (n / aspect), m, n));
            list.Sort((a, b) => a.f.CompareTo(b.f));
            double f0 = list[0].f;
            for (int i = 0; i < ModeSet.Max; i++)
            {
                PlateRatio[i] = list[i].f / f0;
                PlateM[i] = list[i].m;
                PlateY[i] = (float)(0.15 + 0.85 * Math.Abs(PortableMath.Sin(list[i].n * Math.PI * 0.37)));
            }
        }

        static void PlateModes(ModeSet s, int modes)
        {
            for (int i = 0; i < modes; i++) { s.Ratio[i] = PlateRatio[i]; s.Shape[i] = PlateM[i]; }
        }
    }
}
