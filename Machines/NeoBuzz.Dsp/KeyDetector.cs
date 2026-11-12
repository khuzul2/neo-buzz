#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §5.12, §5.13): the detector Neo De-esser and Neo Gate share.
// A key signal goes through an optional high-pass and low-pass (second order each: a band-pass when both are set, which
// is the de-esser's sibilance band), and a linked stereo level follower reads it in dB: a peak follower (attack and
// release one-poles on max(|l|, |r|), Pedal Gate's detector) or an RMS one (a sliding rectangular window over the mean
// square of both channels, read on the peak scale, which equals Pedal Gate's stereo power sum: its sqrt((l² + r²) / N)). Allocation-free; the portable maths.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class KeyDetector
    {
        Biquad hpL, hpR, lpL, lpR;
        bool hpOn, lpOn, rms;
        double attackA, releaseA;
        double env, sum;
        const int MaxWindow = 4096;
        readonly double[] ring = new double[MaxWindow];
        int window = 1, ringPos;

        /// <summary>The level below which a signal counts as silence (dB), what <see cref="Level"/> returns for an empty key.</summary>
        public const double Floor = -300;

        /// <summary>The filters: 0 (or less) leaves one off. Second-order Butterworth each (Q 0.7071).</summary>
        public void SetBand(double highPassHz, double lowPassHz, double sampleRate)
        {
            hpOn = highPassHz > 0 && highPassHz < 0.49 * sampleRate;
            lpOn = lowPassHz > 0 && lowPassHz < 0.49 * sampleRate;
            // Only the coefficients change: the filters' states carry on, so a moved band doesn't click.
            if (hpOn) { hpL.Set(BiquadType.HighPass, highPassHz, sampleRate, 0.7071067811865476); hpR.Set(BiquadType.HighPass, highPassHz, sampleRate, 0.7071067811865476); }
            if (lpOn) { lpL.Set(BiquadType.LowPass, lowPassHz, sampleRate, 0.7071067811865476); lpR.Set(BiquadType.LowPass, lowPassHz, sampleRate, 0.7071067811865476); }
        }

        /// <summary>Peak: attack and release follow the key's level. RMS: <paramref name="attackSeconds"/> is the window (at most 4096 samples).</summary>
        public void SetDetector(bool useRms, double attackSeconds, double releaseSeconds, double sampleRate)
        {
            rms = useRms;
            attackA = Detector.Coef(attackSeconds, sampleRate);
            releaseA = Detector.Coef(releaseSeconds, sampleRate);
            int w = Math.Clamp((int)(attackSeconds * sampleRate), 1, MaxWindow);
            if (useRms && w != window)
            {
                window = w;
                sum = 0;
                for (int k = 0; k < window; k++) sum += ring[(ringPos - k) & (MaxWindow - 1)];
            }
        }

        public bool Filtered => hpOn || lpOn;

        /// <summary>The key through the filters (the band the detector hears).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Filter(float l, float r, out float kl, out float kr)
        {
            if (hpOn) { l = hpL.Process(l); r = hpR.Process(r); }
            if (lpOn) { l = lpL.Process(l); r = lpR.Process(r); }
            kl = l;
            kr = r;
        }

        /// <summary>One sample of the linked level follower on a (filtered) key, in dB on the peak scale; <see cref="Floor"/> when silent.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Level(float kl, float kr)
        {
            if (rms)
            {
                double sq = 0.5 * ((double)kl * kl + (double)kr * kr);
                ringPos = (ringPos + 1) & (MaxWindow - 1);
                sum += sq - ring[(ringPos - window) & (MaxWindow - 1)];
                ring[ringPos] = sq;
                if (sum < 0) sum = 0;
                double ms = sum / window;
                if (ms < 1e-30) return Floor;
                return Detector.RmsDb(ms);
            }
            double p = Math.Max(Math.Abs((double)kl), Math.Abs((double)kr));
            env = p > env ? p + (env - p) * attackA : p + (env - p) * releaseA;
            if (env < 1e-15) { env = 0; return Floor; }
            return Detector.PeakDb(env);
        }

        public void Reset()
        {
            hpL.Reset(); hpR.Reset(); lpL.Reset(); lpR.Reset();
            env = sum = 0;
            Array.Clear(ring);
            ringPos = 0;
        }
    }
}
