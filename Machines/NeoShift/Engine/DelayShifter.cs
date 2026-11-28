#nullable disable
// Neo Shift's Fast pitch shifter (docs/specs/neo-shift.md): the two-tap crossfaded delay-line kind (Bogdanowicz and Belcher, 1981; Dattorro, 2002),
// one channel, written for this machine so that the phase runs in double precision (the pitch is exact to well under a cent) and the window can
// glide.
//  - Two read points sweep through a short delay line at (1 - ratio) samples a sample and are crossfaded with complementary Hann weights, a
//    window long (10 ... 100 ms). Each tap alone is the input played at the ratio, so the output's pitch is the input's times the ratio.
//  - The delay of the output swings between 0 and a window and is half a window on average: at ratio 1 the phase rests where one tap carries
//    the whole signal and the sound is the input delayed by half a window, exactly: that is the latency the machine reports.
//  - The ratio moves linearly across each block (from the value the last block ended on), so a changing shift glides without a click.
//  - Waveform alignment (the SOLA idea of Roucos and Wilgus, 1985): every time a tap starts a new sweep (its weight is 0 then) its delay is shifted by
//    the lag, within a quarter of the window, that makes it most like the other tap (the normalised cross-correlation of the signal just heard). A
//    steady tone, whose two taps would otherwise cancel each other at some pitches and tones, comes out with a steady amplitude and its exact pitch;
//    a chord or a voice has its two taps in step as far as they can be.
//  - At a ratio of exactly 1 the phase would stay wherever a sweep left it (a static comb of two taps): it is eased to the nearest rest point
//    at a rate worth about 2 cents of pitch, so a shift of 0 settles to a clean delay.
// Allocation-free after construction; IEEE basic operations and PortableMath only.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Shift
{
    internal sealed class DelayShifter
    {
        public const double MinWindowMs = 10, MaxWindowMs = 100;
        /// <summary>The pitch the rest point is approached with at a ratio of exactly 1 (as 1 - ratio).</summary>
        const double HomingSlope = 0.0011;

        const int MaxCorrelation = 1024;

        readonly FracDelay line;
        readonly double maxGrain, sr;
        double phase;
        double grain;
        double lagA, lagB;
        bool primed;

        public DelayShifter(double sampleRate)
        {
            sr = sampleRate;
            maxGrain = Math.Ceiling(sampleRate * MaxWindowMs * 0.001) + 8;
            line = new FracDelay((int)maxGrain + (int)(0.012 * sampleRate) + MaxCorrelation + 64);
            grain = 0;
        }

        /// <summary>The window in samples as the machine uses it: even, so that half of it (the latency) is a whole number.</summary>
        public static int WindowSamples(double windowMs, double sampleRate)
        {
            int g = (int)Math.Round(Math.Clamp(windowMs, MinWindowMs, MaxWindowMs) * 0.001 * sampleRate);
            return g & ~1;
        }

        public void Reset()
        {
            line.Reset();
            phase = 0;
            lagA = lagB = 0;
            primed = false;
        }

        /// <summary>
        /// Shifts <paramref name="n"/> samples of <paramref name="x"/> (from <paramref name="offset"/>) in place: the ratio goes linearly from
        /// <paramref name="ratio0"/> to <paramref name="ratio1"/>, the window from where it was to <paramref name="windowSamples"/> (a quick glide).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] x, int n, double ratio0, double ratio1, double windowSamples)
        {
            if (!primed) { grain = windowSamples; primed = true; }
            double gStep = 0;
            double gTarget = windowSamples;
            if (grain != gTarget)
            {
                // a glide of the window: at most 1/2000 of itself per sample, so a change takes about 1/4 s
                gStep = Math.Max(0.05, gTarget * 0.0005);
            }
            double dr = (ratio1 - ratio0) / n;
            double r = ratio0;
            double g = grain;
            double ph = phase;
            for (int i = 0; i < n; i++)
            {
                r += dr;
                if (g != gTarget)
                {
                    g += g < gTarget ? gStep : -gStep;
                    if (Math.Abs(g - gTarget) <= gStep) g = gTarget;
                }
                double slope = 1.0 - r;
                double next;
                if (slope == 0.0)
                {
                    // at rest: ease to the nearest of the two points where one tap carries the sound (0 and 1/2), and the lags to 0
                    double nearest = Math.Floor(ph * 2 + 0.5) * 0.5;
                    double d = nearest - ph;
                    double step = HomingSlope / g;
                    next = ph + (d > step ? step : d < -step ? -step : d);
                    lagA += Math.Clamp(-lagA, -HomingSlope, HomingSlope);
                    lagB += Math.Clamp(-lagB, -HomingSlope, HomingSlope);
                }
                else next = ph + slope / g;
                bool wrapA = next >= 1.0 || next < 0.0;
                next -= Math.Floor(next);
                bool crossB = (ph < 0.5) != (next < 0.5);
                ph = next;
                double p2 = ph + 0.5;
                if (p2 >= 1.0) p2 -= 1.0;
                line.Write(x[i]);
                if (wrapA) lagA = Align(ph * g, lagB + p2 * g, g, lagA);
                else if (crossB) lagB = Align(p2 * g, lagA + ph * g, g, lagB);
                float wa = (float)(0.5 - 0.5 * Cos(ph));
                float a = line.ReadCubic((float)Math.Max(2.0, ph * g + lagA));
                float b = line.ReadCubic((float)Math.Max(2.0, p2 * g + lagB));
                x[i] = a * wa + b * (1f - wa);
            }
            grain = g;
            phase = ph;
        }

        /// <summary>
        /// The lag, within a quarter of the window of 0 (and of 12 ms), that puts the tap that starts a sweep (its delay <paramref name="startDelay"/>
        /// plus the lag) most in step with the other (at <paramref name="otherDelay"/>): the best normalised correlation of the last stretch of
        /// the signal at the two delays, coarse then fine, with a small pull toward 0. <paramref name="previous"/> is kept when nothing correlates.
        /// </summary>
        double Align(double startDelay, double otherDelay, double window, double previous)
        {
            int span = (int)Math.Min(window * 0.25, 0.012 * sr);
            int len = (int)Math.Min(window, MaxCorrelation);
            if (span < 4 || len < 16) return previous;
            int dy = (int)Math.Round(otherDelay);
            int dx0 = (int)Math.Round(startDelay);
            double ey = Energy(dy, len, 2);
            if (ey < 1e-12) return previous;
            int lo = Math.Max(0, 2 - dx0), hi = span;
            if (lo > hi) return previous;
            int bestE = 0;
            double best = double.NegativeInfinity;
            int coarse = Math.Max(2, span / 48);
            for (int e = lo; e <= hi; e += coarse)
            {
                double sc = Score(dx0 + e, dy, len, 4, ey, e, span);
                if (sc > best) { best = sc; bestE = e; }
            }
            int from = Math.Max(lo, bestE - coarse), to = Math.Min(hi, bestE + coarse);
            for (int e = from; e <= to; e++)
            {
                double sc = Score(dx0 + e, dy, len, 2, ey, e, span);
                if (sc > best) { best = sc; bestE = e; }
            }
            return best <= -1e9 ? previous : bestE;
        }

        double Energy(int delay, int len, int dec)
        {
            double sum = 0;
            for (int m = 0; m < len; m += dec) { float v = line.Read(delay + m); sum += v * v; }
            return sum;
        }

        // The normalised correlation of the signal at two delays over the last len samples, less a pull toward a lag of 0.
        double Score(int dx, int dy, int len, int dec, double ey, int e, int span)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int m = 0; m < len; m += dec)
            {
                float a = line.Read(dx + m), b = line.Read(dy + m);
                xy += a * b;
                xx += a * a;
                yy += b * b;
            }
            if (xx < 1e-12 || yy < 1e-12) return -1e9 + 1;
            return xy / Math.Sqrt(xx * yy) - 0.03 * Math.Abs(e) / span;
        }

        // cos(2 pi p), p in 0 ... 1: a parabolic sine of the phase a quarter ahead with the usual correction (error under 0.1 %; exactly 1 at 0 and -1 at 1/2).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Cos(double p)
        {
            double x = p + 0.25;
            x -= Math.Floor(x + 0.5);
            double y = 8 * x * (1 - 2 * Math.Abs(x));
            return 0.225 * (y * Math.Abs(y) - y) + y;
        }
    }
}
