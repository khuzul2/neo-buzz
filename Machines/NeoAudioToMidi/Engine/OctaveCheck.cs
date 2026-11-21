#nullable disable
// Neo Audio to MIDI (docs/specs/audio-to-midi.md §3.5): the octave check. A difference-function tracker (YIN) now and then answers an octave under the string's
// pitch (the pluck's first periods repeat at twice the period too) or, less often, over it. The check scores three pitches (the tracker's, half and double) on the
// spectrum of the newest slice of audio by subharmonic summation (Hermes 1988): the sum of the square-root magnitudes at the first eight harmonics with a
// weight that falls by 0.84 a harmonic. A pitch that is half the true one has every other harmonic empty and scores less than the true one, since its
// even harmonics weigh less; a pitch that is double has half the harmonics missing. The best-scoring pitch replaces the tracker's only when it scores
// at least Margin times as much. Magnitudes are Goertzel sums over a Hann-windowed slice, at the harmonic and a third of a percent either side of it.
// Allocation-free.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.AudioToMidi
{
    internal sealed class OctaveCheck
    {
        const int Harmonics = 8, MaxSlice = 4096;
        const double Decay = 0.84;
        readonly float[] slice = new float[MaxSlice];
        readonly float[] hann = new float[MaxSlice + 1];
        int hannFor;

        /// <summary>The last scores of the three pitches (half, same, double), for the tests and the evaluation.</summary>
        public readonly double[] Scores = new double[3];

        void BuildWindow(int n)
        {
            if (hannFor == n) return;
            for (int i = 0; i < n; i++) hann[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * (i + 0.5) / n));
            hannFor = n;
        }

        double Magnitude(int n, double hz, int rate)
        {
            double w = 2 * Math.PI * hz / rate;
            double coeff = 2 * PortableMath.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double s0 = slice[i] + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }
            double p = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Sqrt(Math.Max(0, p)) / n;
        }

        double Score(int n, double g, int rate, double maxHz)
        {
            double sum = 0, wgt = 1;
            for (int h = 1; h <= Harmonics; h++, wgt *= Decay)
            {
                double f = g * h;
                if (f >= maxHz) break;
                double m = Magnitude(n, f, rate);
                m = Math.Max(m, Math.Max(Magnitude(n, f * 0.997, rate), Magnitude(n, f * 1.003, rate)));
                sum += wgt * Math.Sqrt(m);
            }
            return sum;
        }

        /// <summary>
        /// The pitch (Hz) the audio before <paramref name="end"/> supports: <paramref name="hz"/>, or half or double of it. The slice is up to 40 ms long and starts no earlier than
        /// <paramref name="notBefore"/> (the pluck: what rang before it is not of this note); with fewer than 2.5 periods of <paramref name="hz"/> in it, <paramref name="hz"/> is returned.
        /// </summary>
        public double Fix(float[] ring, int mask, long end, long notBefore, double hz, double minHz, double maxHz, int rate, double margin)
        {
            Scores[0] = Scores[1] = Scores[2] = 0;
            int n = (int)Math.Min(MaxSlice, 0.040 * rate);
            long start = Math.Max(end - n, Math.Max(notBefore, 0));
            n = (int)(end - start);
            if (n < 2.5 * rate / hz || n < 64) return hz;
            BuildWindow(n);
            for (int i = 0; i < n; i++) slice[i] = ring[(int)((start + i) & mask)] * hann[i];
            double nyq = rate * 0.45;
            double best = hz, bestScore;
            double same = Score(n, hz, rate, nyq);
            Scores[1] = same; bestScore = same;
            double low = hz * 0.5, high = hz * 2;
            double sLow = 0, sHigh = 0;
            if (low >= minHz * 0.92 && n >= 2.5 * rate / low) sLow = Score(n, low, rate, nyq);
            if (high <= maxHz * 1.08) sHigh = Score(n, high, rate, nyq);
            Scores[0] = sLow; Scores[2] = sHigh;
            if (sLow > bestScore * margin) { best = low; bestScore = sLow; }
            if (sHigh > bestScore * margin) { best = high; }
            return best;
        }
    }
}
