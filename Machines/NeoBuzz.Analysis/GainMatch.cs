#nullable disable
// Neo machines shared measurement (docs/specs/mastering-tools.md §2, the gain-match convention, decided in
// M1): the reading `gain_match_db` the mixing and mastering machines from M2 on show, and their editors'
// "Match" action, which sets `Output` by it as one undoable change (never automatic gain riding).
//  - The short-term loudness (BS.1770 K-weighting, the same filter as Loudness and LoudnessMeter, a 3 s window
//    in 100 ms steps) of the machine's output minus that of its input, in dB (LU): positive, the machine
//    made it louder. Both meters step together, fed by one call per block.
//  - Unknown (NaN) until 3 s have passed, and while either side is below −70 LUFS (the absolute gate), so a
//    silent input or a muted output never suggests a jump.
//  - The output lags the input by the machine's latency: over a 3 s window that is a few hundredths of a dB
//    on music, under the 0.1 dB step Output moves in.
// Allocation-free after construction (two K-weighting pairs and two rings of 30 steps), for the audio thread;
// the portable maths throughout, so the reading is the same on every platform.

using System;

namespace NeoBuzz.Analysis
{
    internal sealed class GainMatch
    {
        const int Steps = 30;            // 3 s of 100 ms steps (short-term loudness)
        const double Gate = -70;         // LUFS

        Loudness.KWeighting inL, inR, outL, outR;
        int hop;
        readonly double[] inSteps = new double[Steps], outSteps = new double[Steps];
        double inSum, outSum;
        int inStep, stepPos;
        long total;

        public GainMatch(int rate)
        {
            Rate = rate;
            hop = (int)Math.Round(rate * 0.1);
            inL = Loudness.KWeighting.For(rate);
            inR = Loudness.KWeighting.For(rate);
            outL = Loudness.KWeighting.For(rate);
            outR = Loudness.KWeighting.For(rate);
            Db = double.NaN;
            InputLufs = OutputLufs = double.NegativeInfinity;
        }

        public int Rate { get; private set; }

        /// <summary>A new rate: the filters and the step follow it, and measuring starts again.</summary>
        public void SetRate(int rate)
        {
            Rate = rate;
            hop = (int)Math.Round(rate * 0.1);
            Reset();
        }

        /// <summary>Output − input short-term loudness, dB; NaN while unknown.</summary>
        public double Db { get; private set; }

        /// <summary>The two short-term loudnesses, LUFS (−∞ until 3 s have passed).</summary>
        public double InputLufs { get; private set; }
        public double OutputLufs { get; private set; }

        public void Reset()
        {
            var rate = Rate;
            inL = Loudness.KWeighting.For(rate);
            inR = Loudness.KWeighting.For(rate);
            outL = Loudness.KWeighting.For(rate);
            outR = Loudness.KWeighting.For(rate);
            Array.Clear(inSteps, 0, Steps);
            Array.Clear(outSteps, 0, Steps);
            inSum = outSum = 0;
            inStep = stepPos = 0;
            total = 0;
            Db = double.NaN;
            InputLufs = OutputLufs = double.NegativeInfinity;
        }

        /// <summary>A block: the machine's input and output (right null: mono, counted as both channels).</summary>
        public void Process(float[] inLeft, float[] inRight, float[] outLeft, float[] outRight, int offset, int n)
        {
            while (n > 0)
            {
                int m = Math.Min(n, hop - inStep);
                inSum += inL.SumOfSquares(inLeft, offset, m) * (inRight == null ? 2 : 1);
                if (inRight != null) inSum += inR.SumOfSquares(inRight, offset, m);
                outSum += outL.SumOfSquares(outLeft, offset, m) * (outRight == null ? 2 : 1);
                if (outRight != null) outSum += outR.SumOfSquares(outRight, offset, m);
                offset += m;
                n -= m;
                inStep += m;
                if (inStep == hop) Step();
            }
        }

        void Step()
        {
            inSteps[stepPos] = inSum / hop;
            outSteps[stepPos] = outSum / hop;
            inSum = outSum = 0;
            inStep = 0;
            stepPos = stepPos + 1 == Steps ? 0 : stepPos + 1;
            if (++total < Steps) return;
            double a = 0, b = 0;
            for (int i = 0; i < Steps; i++)
            {
                a += inSteps[i];
                b += outSteps[i];
            }
            // With the portable maths: a machine may take the reading into its audio (Neo Limiter's Unity listen).
            InputLufs = Lufs(a / Steps);
            OutputLufs = Lufs(b / Steps);
            Db = InputLufs > Gate && OutputLufs > Gate ? 10 * NeoBuzz.Dsp.PortableMath.Log10(b / a) : double.NaN;
        }

        static double Lufs(double meanSquare) => meanSquare > 0 ? -0.691 + 10 * NeoBuzz.Dsp.PortableMath.Log10(meanSquare) : double.NegativeInfinity;

        /// <summary>
        /// The Output setting that matches the loudness: <paramref name="outputDb"/> − <paramref name="db"/>, on Output's
        /// 0.1 dB steps, within its range; the setting unchanged while the reading is unknown.
        /// </summary>
        public static double Match(double outputDb, double db, double minDb, double maxDb) =>
            double.IsFinite(db) ? Math.Clamp(Math.Round((outputDb - db) * 10) / 10, minDb, maxDb) : outputDb;
    }
}
