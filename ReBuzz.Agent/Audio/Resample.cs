#nullable disable
// Agent server (docs/specs/ux-overhaul.md P3j): export_audio at another sample rate. A windowed-sinc (Blackman) polyphase
// resampler with 32 input samples either side, low-passed at the lower of the two Nyquist frequencies; every phase's
// taps sum to 1 so a constant level stays a constant level. Pure double arithmetic in a fixed order, so the same audio
// gives the same samples each time (exports stay bit-identical).

using System;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Audio
{
    public static class Resample
    {
        public const int MinRate = 8000, MaxRate = 192000;
        const int Lobes = 32;

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        /// <summary>The audio at <paramref name="rate"/> samples a second (the same audio when it is already there).</summary>
        public static StereoAudio To(StereoAudio a, int rate)
        {
            if (rate < MinRate || rate > MaxRate) throw new ToolError($"sample_rate: {MinRate} to {MaxRate} Hz.");
            if (rate == a.Rate) return a;
            return new StereoAudio { Left = Channel(a.Left, a.Rate, rate), Right = a.Right != null ? Channel(a.Right, a.Rate, rate) : null, Rate = rate };
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        static float[] Channel(float[] x, int from, int to)
        {
            int g = Gcd(from, to);
            int up = to / g, down = from / g;
            double fc = Math.Min(1.0, (double)up / down);            // cut-off against the input's Nyquist
            int half = (int)Math.Ceiling(Lobes / fc);
            int taps = 2 * half;
            if ((long)up * taps > 8_000_000) throw new ToolError($"sample_rate: {from} to {to} Hz is too awkward a ratio; use 44100, 48000, 88200 or 96000.");
            var table = new double[up * taps];
            for (int p = 0; p < up; p++)
            {
                double frac = p / (double)up, sum = 0;
                for (int k = 0; k < taps; k++)
                {
                    double d = (k - half + 1) - frac;                // input offset of this tap from the output instant
                    double arg = Math.PI * fc * d;
                    double sinc = Math.Abs(arg) < 1e-12 ? 1 : Math.Sin(arg) / arg;
                    double w = 0.42 + 0.5 * Math.Cos(Math.PI * d / half) + 0.08 * Math.Cos(2 * Math.PI * d / half);
                    if (Math.Abs(d) >= half) w = 0;
                    double h = fc * sinc * w;
                    table[p * taps + k] = h;
                    sum += h;
                }
                for (int k = 0; k < taps; k++) table[p * taps + k] /= sum;
            }
            int n = (int)Math.Round(x.Length * (double)up / down);
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                long pos = (long)i * down;
                int i0 = (int)(pos / up), p = (int)(pos % up);
                int first = i0 - half + 1;
                int o = p * taps;
                double acc = 0;
                int k0 = Math.Max(0, -first), k1 = Math.Min(taps, x.Length - first);
                for (int k = k0; k < k1; k++) acc += table[o + k] * x[first + k];
                y[i] = (float)acc;
            }
            return y;
        }

        /// <summary>The gain that puts the master's highest sample at <paramref name="peakDb"/> dBFS (1 when it is silent).</summary>
        public static double NormaliseGain(StereoAudio master, double peakDb)
        {
            float peak = 0;
            foreach (float s in master.Left) peak = Math.Max(peak, Math.Abs(s));
            if (master.Right != null) foreach (float s in master.Right) peak = Math.Max(peak, Math.Abs(s));
            return peak <= 0 ? 1 : Math.Pow(10, peakDb / 20) / peak;
        }

        /// <summary>The audio times <paramref name="gain"/>.</summary>
        public static StereoAudio Scale(StereoAudio a, double gain)
        {
            if (gain == 1) return a;
            var l = new float[a.Frames];
            for (int i = 0; i < l.Length; i++) l[i] = (float)(a.Left[i] * gain);
            float[] r = null;
            if (a.Right != null) { r = new float[a.Frames]; for (int i = 0; i < r.Length; i++) r[i] = (float)(a.Right[i] * gain); }
            return new StereoAudio { Left = l, Right = r, Rate = a.Rate };
        }
    }
}
