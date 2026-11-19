#nullable disable
// Neo Tune's interpolator (docs/specs/neo-tune.md section 11.3): a 16-tap Kaiser-windowed sinc on a table of 256 fractional phases with
// a linear blend between neighbouring phases, every row normalised to a gain of exactly 1 at DC. The cubic it replaces lost about
// 1 dB above 5 kHz (the lead's listening measurements of NT1); this one is flat to within 0.05 dB up to 0.40 of the sample rate
// (measured in NeoBuzz.MachineTests, TuneTests). At a whole-sample position the row is a unit impulse: the sample as it is.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal static class TuneSinc
    {
        public const int Taps = 16, Half = 8, Phases = 256;
        /// <summary>(Phases + 2) rows of <see cref="Taps"/> coefficients; row p is the kernel for a position p / Phases past the sample; tap k reads the sample k - 7 from it.</summary>
        public static readonly float[] Table = Build();

        static double I0(double x)
        {
            double sum = 1, term = 1, q = x * x * 0.25;
            for (int k = 1; k < 40; k++)
            {
                term *= q / ((double)k * k);
                sum += term;
                if (term < 1e-14 * sum) break;
            }
            return sum;
        }

        static float[] Build()
        {
            var t = new float[(Phases + 2) * Taps];
            const double beta = 5.0;
            double i0b = I0(beta);
            var row = new double[Taps];
            for (int p = 0; p < Phases + 2; p++)
            {
                double frac = p / (double)Phases;
                double sum = 0;
                for (int k = 0; k < Taps; k++)
                {
                    double x = (k - (Half - 1)) - frac;
                    double sinc = Math.Abs(x) < 1e-12 ? 1.0 : PortableMath.Sin(Math.PI * x) / (Math.PI * x);
                    double r = x / Half;
                    double w = Math.Abs(r) >= 1 ? 0.0 : I0(beta * Math.Sqrt(1 - r * r)) / i0b;
                    row[k] = sinc * w;
                    sum += row[k];
                }
                for (int k = 0; k < Taps; k++) t[p * Taps + k] = (float)(row[k] / sum);
            }
            return t;
        }
    }
}
