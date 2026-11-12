#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2): the transient/sustain split the mixing and
// mastering machines share (Neo Transient, Neo Low End, Neo Limiter v2's transient emphasis, the clipper's
// and imager's Transient/Sustain modes), so the words mean the same everywhere.
//  - The level is the signal's envelope from a 90° allpass pair (QuadratureAllpass: √(a² + b²)), the louder
//    channel's: a steady sine's envelope is flat at any frequency from 20 Hz, where a rectifier's ripples
//    by the half period over the release (4 dB at 50 Hz with a 20 ms release), which a shaper would turn
//    into distortion on bass.
//  - Two followers on it, one-pole in the linear level: fast (by default attack 0.3 ms, release 20 ms) and
//    slow (attack 20 ms, release 60 ms: never shorter than the fast one's). On an onset the fast one leads;
//    once the level holds, the slow one catches up; on a decay the fast one falls first, so a decay is all
//    sustain. On 200 Hz hits decaying with 60 ms the weight rises within 1 ms, peaks at about 0.8 by 3 ms
//    and falls to 0 by 60 ms.
//  - The transient weight t = 1 − k·slow/fast while that is positive, else 0, with k = 10^(deadband/20):
//    the share of the level above the sustained part, less a deadband (0.5 dB by default) that keeps a
//    steady signal's weight at exactly 0. The sustain weight is s = 1 − t.
//  - Consumers apply g = 1 + (a − 1)·t + (b − 1)·s for a transient gain a and a sustain gain b
//    (TransientSplit.Gain): with a = b = 1 every factor is 0, so g is exactly 1 and the signal passes bit
//    for bit, whatever t.
// The weights lag the audio by the pair's group delay (a few ms at 50 Hz, under 0.1 ms above 2 kHz):
// consumers that want the gain ahead of an onset delay the audio (a lookahead, its latency reported).
// State under 1e-30 is flushed to 0 every 64 samples of the split's own count, so the output doesn't depend
// on how the host splits the audio. Allocation-free after construction; IEEE basic operations and Sqrt only
// (the coefficients from the portable maths), so the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class TransientSplit
    {
        readonly double sampleRate;
        readonly QuadratureAllpass left, right;
        double fastAttack, fastRelease, slowAttack, slowRelease, deadband;
        double fast, slow;
        int count;

        /// <summary>Default detector: fast 0.3 ms / 20 ms, slow 20 ms / 60 ms, deadband 0.5 dB.</summary>
        public TransientSplit(double sampleRate)
        {
            this.sampleRate = sampleRate;
            var c = QuadratureAllpass.Coefficients(sampleRate);
            left = new QuadratureAllpass(c);
            right = new QuadratureAllpass(c);
            Set(0.3, 20, 20, 60, 0.5);
        }

        /// <summary>The followers' times in ms (the slow release no shorter than the fast one) and the deadband in dB.</summary>
        public void Set(double fastAttackMs, double fastReleaseMs, double slowAttackMs, double slowReleaseMs, double deadbandDb)
        {
            fastAttack = Detector.Coef(fastAttackMs / 1000, sampleRate);
            fastRelease = Detector.Coef(fastReleaseMs / 1000, sampleRate);
            slowAttack = Detector.Coef(slowAttackMs / 1000, sampleRate);
            slowRelease = Detector.Coef(Math.Max(slowReleaseMs, fastReleaseMs) / 1000, sampleRate);
            deadband = DspMath.DbToGain(Math.Max(0, deadbandDb));
        }

        public void Reset()
        {
            left.Reset();
            right.Reset();
            fast = slow = 0;
            count = 0;
        }

        /// <summary>The followers now (linear level), for readings and tests.</summary>
        public double Fast => fast;
        public double Slow => slow;

        /// <summary>The gain for a transient gain <paramref name="a"/> and a sustain gain <paramref name="b"/> at weight <paramref name="t"/>: exactly 1 when both are 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Gain(double a, double b, double t) => 1 + (a - 1) * t + (b - 1) * (1 - t);

        /// <summary>
        /// The transient weights (0 … 1) of <paramref name="n"/> samples of <paramref name="l"/> and <paramref name="r"/> from
        /// <paramref name="offset"/> into <paramref name="weights"/> from 0; the audio is only read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int offset, int n, float[] weights)
        {
            for (int done = 0; done < n; done += Chunk)
                Process(l, r, offset + done, Math.Min(Chunk, n - done), weights, done);
        }

        // M3: the pair a block at a time (QuadratureAllpass.Block), then the followers; the same bits as one sample at a
        // time (each sample's pair outputs, then its followers, the state flushed after every 64th sample of the count).
        const int Chunk = 256;
        readonly double[] pairA = new double[2 * Chunk], pairB = new double[2 * Chunk];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Process(float[] l, float[] r, int offset, int n, float[] weights, int at)
        {
            var pa = pairA;
            var pb = pairB;
            for (int i = 0; i < n; i++)
            {
                pa[2 * i] = l[offset + i];
                pa[2 * i + 1] = r[offset + i];
            }
            // The first sample of this chunk after which the count reaches a multiple of 64.
            int first = 63 - (count & 63);
            QuadratureAllpass.Block(left, right, pa, pb, n, first);
            double f = fast, s = slow;
            int next = first;
            for (int i = 0; i < n; i++)
            {
                double la = pa[2 * i], ra = pa[2 * i + 1], lb = pb[2 * i], rb = pb[2 * i + 1];
                double level = Math.Sqrt(Math.Max(la * la + lb * lb, ra * ra + rb * rb));
                f = level + (f - level) * (level > f ? fastAttack : fastRelease);
                s = level + (s - level) * (level > s ? slowAttack : slowRelease);
                double t = f > 1e-12 ? 1 - deadband * s / f : 0;
                weights[at + i] = t > 0 ? (float)t : 0f;
                if (i == next)
                {
                    f = DspMath.Flush(f);
                    s = DspMath.Flush(s);
                    next += 64;
                }
            }
            count += n;
            fast = f;
            slow = s;
        }
    }
}
