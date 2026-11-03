#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): band-limited steps and ramps. A naive
// waveform's discontinuities (value jumps, slope changes) are corrected by adding the difference
// between a band-limited step (BLEP) or ramp (BLAMP) and the naive one, read from tables made
// from a windowed sinc. The correction spans Z samples either side of the discontinuity, so the
// output runs Z samples late (linear phase).

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class BlepTable
    {
        /// <summary>Zero crossings either side: the correction spans 2·Z samples, the latency is Z.</summary>
        public const int Z = 24;
        /// <summary>Sub-sample phases tabulated (interpolated linearly between).</summary>
        public const int Os = 256;
        /// <summary>Samples a correction touches.</summary>
        public const int Span = 2 * Z;

        public static readonly BlepTable Shared = new BlepTable(0.46);

        /// <summary>
        /// Polyphase residuals: row j (0 … Os), column i (0 … Span−1) holds r(i − Z + j/Os), where
        /// r is the band-limited step minus the naive one (BLEP) or the band-limited ramp minus the
        /// naive one (BLAMP). Rows are interpolated within a column, so the step's jump at t = 0
        /// (between column Z−1 at row Os and column Z at row 0) is never interpolated across.
        /// </summary>
        public readonly float[] Step;
        public readonly float[] Ramp;

        /// <param name="cutoff">Sinc cutoff as a fraction of the sample rate (0.5 = Nyquist).</param>
        public BlepTable(double cutoff)
        {
            int n = Span * Os + 1;   // grid t = −Z + k/Os
            var impulse = new double[n];
            for (int k = 0; k < n; k++)
            {
                double t = -Z + (double)k / Os;
                double x = 2 * cutoff * t;
                double sinc = Math.Abs(x) < 1e-12 ? 1 : PortableMath.Sin(Math.PI * x) / (Math.PI * x);
                // 4-term Blackman–Harris over [−Z, Z].
                double w = (t + Z) / (2.0 * Z);
                double win = 0.35875 - 0.48829 * PortableMath.Cos(2 * Math.PI * w) + 0.14128 * PortableMath.Cos(4 * Math.PI * w)
                             - 0.01168 * PortableMath.Cos(6 * Math.PI * w);
                impulse[k] = 2 * cutoff * sinc * win;
            }
            // Integrate (Simpson-corrected trapezoid) to the step; normalise its end to exactly 1.
            var step = new double[n];
            double acc = 0;
            for (int k = 1; k < n; k++)
            {
                acc += 0.5 * (impulse[k - 1] + impulse[k]) / Os;
                step[k] = acc;
            }
            double total = step[n - 1];
            for (int k = 0; k < n; k++) step[k] /= total;
            var ramp = new double[n];
            acc = 0;
            for (int k = 1; k < n; k++)
            {
                acc += 0.5 * (step[k - 1] + step[k]) / Os;
                ramp[k] = acc;
            }
            double shift = ramp[n - 1] - Z;
            Step = new float[(Os + 1) * Span];
            Ramp = new float[(Os + 1) * Span];
            int zeroK = Z * Os;
            for (int j = 0; j <= Os; j++)
            {
                for (int i = 0; i < Span; i++)
                {
                    int k = i * Os + j;
                    double t = -Z + (double)k / Os;
                    // Column Z−1 at row Os is t = 0 approached from below: the jump hasn't happened.
                    bool after = k > zeroK || (k == zeroK && i >= Z);
                    Step[j * Span + i] = (float)(step[k] - (after ? 1 : 0));
                    Ramp[j * Span + i] = (float)(ramp[k] - shift - Math.Max(t, 0));
                }
            }
        }
    }

    /// <summary>
    /// The accumulator an oscillator writes into: naive samples plus BLEP/BLAMP corrections,
    /// read out Z samples later.
    /// </summary>
    internal struct BlepBuffer
    {
        float[] acc;
        int pos;
        const int Span = BlepTable.Span;
        const int Len = 64;                // ring size: a power of two ≥ Span
        const int Mask = Len - 1;

        public void Init()
        {
            acc = new float[Len];
            pos = 0;
            // Fork (audio hygiene): the shared tables are made here, where the oscillator is (with
            // its machine, off the audio thread), not on the first note: the audio thread built
            // them there (about 200 KB, and the loop's compilation).
            _ = BlepTable.Shared;
        }

        public void Reset()
        {
            if (acc != null) Array.Clear(acc, 0, Len);
        }

        /// <summary>
        /// A jump of <paramref name="height"/> that happened <paramref name="ago"/> samples
        /// (0 … 1) before the sample about to be added.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddStep(float height, float ago)
        {
            // Correction at sample m = current − Z + i (i = 0 … Span−1) is r(i − Z + ago).
            Add(BlepTable.Shared.Step, height, ago);
        }

        /// <summary>A slope change of <paramref name="slopeDelta"/> per sample, <paramref name="ago"/> samples before the current one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRamp(float slopeDelta, float ago)
        {
            Add(BlepTable.Shared.Ramp, slopeDelta, ago);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Add(float[] table, float scale, float ago)
        {
            float fpos = ago * BlepTable.Os;
            int j = (int)fpos;
            if (j >= BlepTable.Os) j = BlepTable.Os - 1;
            float frac = fpos - j;
            int r0 = j * Span, r1 = r0 + Span;
            float a = scale * (1f - frac), b = scale * frac;
            for (int i = 0; i < Span; i++)
                acc[(pos + i) & Mask] += a * table[r0 + i] + b * table[r1 + i];
        }

        /// <summary>Adds the current naive sample and returns the corrected sample Z samples ago.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next(float naive)
        {
            // Slot pos is the oldest (m = current − Z); slot pos + Z is the current sample.
            acc[(pos + BlepTable.Z) & Mask] += naive;
            float y = acc[pos];
            acc[pos] = 0;
            pos = (pos + 1) & Mask;
            return y;
        }
    }
}
