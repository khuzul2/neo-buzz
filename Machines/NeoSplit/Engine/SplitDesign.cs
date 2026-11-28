#nullable disable
// Neo Split's bands as the editor draws them and the tests check them (docs/specs/neo-split.md section 3): the magnitude of each
// band of the Linkwitz-Riley cascade at any frequency, from the crossovers the engine uses. A crossover of order N (4 for
// 24 dB/oct, 8 for 48 dB/oct) is a low-pass 1/(1 + x^N) and a high-pass x^N/(1 + x^N) with x = f/fc: each 6 dB down at fc, the two
// summing (with the allpass the cascade adds) to 1. No libm: powers by multiplication.

using System;

namespace NeoBuzz.Split
{
    internal static class SplitDesign
    {
        /// <summary>The order of a crossover for a slope setting (0: 24 dB/oct, 1: 48 dB/oct).</summary>
        public static int Order(int slope) => slope == 0 ? 4 : 8;

        static double Power(double x, int n)
        {
            double p = 1;
            for (int i = 0; i < n; i++) p *= x;
            return p;
        }

        /// <summary>The low-pass magnitude of a crossover at <paramref name="hz"/>.</summary>
        public static double Low(double hz, double crossover, int slope)
        {
            double p = Power(hz / crossover, Order(slope));
            return 1.0 / (1.0 + p);
        }

        /// <summary>The high-pass magnitude of a crossover at <paramref name="hz"/>.</summary>
        public static double High(double hz, double crossover, int slope)
        {
            double p = Power(hz / crossover, Order(slope));
            return p / (1.0 + p);
        }

        /// <summary>
        /// Band <paramref name="band"/> (0 …) of <paramref name="bands"/> at <paramref name="hz"/>, linear: the high-passes of the
        /// crossovers below it and the low-pass of the one above (none for the last band).
        /// </summary>
        public static double Band(int band, int bands, double hz, double[] crossover, int slope)
        {
            double m = 1;
            for (int j = 0; j < band && j < bands - 1; j++) m *= High(hz, crossover[j], slope);
            if (band < bands - 1) m *= Low(hz, crossover[band], slope);
            return m;
        }

        /// <summary>What each output carries in a mode, for the editor's labels and the guide ("" for an output that is silent).</summary>
        public static string[] OutputNames(SplitEngine.Mode mode, int bands)
        {
            switch (mode)
            {
                case SplitEngine.Mode.TransientBody: return new[] { "Transient", "Body", "", "" };
                case SplitEngine.Mode.MidSide: return new[] { "Mid", "Side", "", "" };
                default:
                    if (bands <= 2) return new[] { "Low", "High", "", "" };
                    if (bands == 3) return new[] { "Low", "Mid", "High", "" };
                    return new[] { "Low", "Low Mid", "High Mid", "High" };
            }
        }
    }
}
