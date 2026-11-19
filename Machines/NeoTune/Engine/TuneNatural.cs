#nullable disable
// The "Natural" control (docs/specs/neo-tune.md section 11.4): one number, 0 to 1, that sets the six fine controls that decide how natural the
// correction sounds. The machine does not read Natural itself; the editor (and a preset, a test, the evaluation) writes these values into the fine controls,
// which can then be edited by hand. 0 is the hard effect, 1 a transparent correction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal static class TuneNatural
    {
        /// <summary>The fine controls Natural writes, as parameter indices.</summary>
        public static readonly int[] Controls = { P.Retune, P.Zone, P.VibKeep, P.Humanize, P.Glide, P.Stick };

        /// <summary>The value (in the parameter's own unit: ms, cents, fractions) control <paramref name="i"/> of <see cref="Controls"/> gets at Natural <paramref name="n"/> (0 to 1).</summary>
        public static double Value(int i, double n)
        {
            n = Math.Clamp(n, 0.0, 1.0);
            switch (i)
            {
                case 0: return 100.0 * PortableMath.Pow(n, 2.2);   // Retune speed, ms: 0 at 0 %, 13 ms at 40 %, 53 ms at 75 %, 100 ms at 100 % (a perceptual curve)
                case 1: return 100.0 - 40.0 * n;                    // Correction zone, cents: off (100) at 0 %, 60 at 100 %
                case 2: return Math.Min(1.0, n / 0.6);              // Keep vibrato (expression): none at 0 %, all from 60 %
                case 3: return n;                                   // Humanize: a sustained note is retuned more slowly the more natural
                case 4: return n;                                   // Transition: snap at 0 %, the singer's glide at 100 %
                default: return 0.15 + 0.85 * n;                    // Note stickiness: low (quick to follow) at 0 %, high at 100 %
            }
        }

        /// <summary>All six values at Natural <paramref name="n"/>.</summary>
        public static double[] Values(double n)
        {
            var a = new double[Controls.Length];
            for (int i = 0; i < a.Length; i++) a[i] = Value(i, n);
            return a;
        }

        /// <summary>Writes Natural <paramref name="n"/> (0 to 1) into the six fine controls of <paramref name="v"/> (and the Natural control itself).</summary>
        public static void Apply(ParamValues v, double n)
        {
            for (int i = 0; i < Controls.Length; i++) v.Set(Controls[i], Params.Table[Controls[i]].Raw(Value(i, n)));
            v.Set(P.Natural, Params.Table[P.Natural].Raw(Math.Clamp(n, 0.0, 1.0)));
        }
    }
}
