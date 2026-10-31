#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): Complex warp mode, a phase vocoder.
// Pure (ReBuzz.SessionTests links this file); no allocation after construction.
//
// Each output frame (2048 samples, a new one every 512) reads the audio around where the clip's
// warp map puts that moment, measures each bin's frequency from two analysis frames a hop
// apart, and advances its own phase by that much: the sound keeps its pitch at any speed.
// Phases are locked to the nearest spectral peak (Laroche and Dolson's identity phase
// locking), which keeps chords and transients clean; both channels follow the phase of their
// sum, so the stereo image holds. Transposing (or another sample rate) moves each peak, with the
// bins round it, to its frequency times the ratio.

using System;

namespace ReBuzz.Core.Clips
{
    /// <summary>Where the audio is for a moment of the output (a clip voice says, from the beat it plays).</summary>
    internal interface ISourceClock
    {
        /// <summary>The audio's sample position heard at this output time (samples since the voice started, may be negative).</summary>
        double SourceAt(double outputTime);
    }

    internal sealed class PhaseVocoder
    {
        public const int Size = 2048;
        public const int Hop = 512;
        const int Bins = Size / 2 + 1;
        const int Ring = 4 * Size;
        const double Overlap = 1.5;            // the sum of a periodic Hann window squared at 4x overlap

        readonly Fft fft = new Fft(Size);
        readonly double[] window = new double[Size];
        readonly double[] reM = new double[Size], imM = new double[Size];
        readonly double[] reP = new double[Size], imP = new double[Size];
        readonly double[] reL = new double[Size], imL = new double[Size];
        readonly double[] reR = new double[Size], imR = new double[Size];
        readonly double[] magL = new double[Bins], magR = new double[Bins], magM = new double[Bins];
        readonly double[] phaseM = new double[Bins], offsetL = new double[Bins], offsetR = new double[Bins];
        readonly double[] advance = new double[Bins];
        readonly double[] synth = new double[Bins];
        readonly double[] outMagL = new double[Bins], outMagR = new double[Bins];
        readonly double[] outPhase = new double[Bins], outOffL = new double[Bins], outOffR = new double[Bins];
        readonly double[] peakPhase = new double[Bins], outWeight = new double[Bins];
        readonly bool[] peak = new bool[Bins];
        readonly int[] nearest = new int[Bins], owner = new int[Bins], lastOwner = new int[Bins];
        readonly float[] olaL = new float[Ring], olaR = new float[Ring];
        long time;                 // the next output sample
        long nextFrame;            // the next frame to make (it starts at nextFrame * Hop)
        bool fresh = true;

        public PhaseVocoder()
        {
            for (int i = 0; i < Size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size);
            Reset();
        }

        /// <summary>From scratch: output time 0 is where the next render starts, phases from the audio.</summary>
        public void Reset()
        {
            Array.Clear(olaL);
            Array.Clear(olaR);
            time = 0;
            nextFrame = -(Size / Hop - 1);
            fresh = true;
            Array.Fill(lastOwner, -1);
        }

        /// <summary>Output samples since the reset.</summary>
        public long Time => time;

        /// <summary>
        /// Renders <paramref name="count"/> samples into the buffers (added, times <paramref name="gain"/>);
        /// <paramref name="pitchRatio"/> scales every frequency (transposition times the audio's
        /// sample rate over the output's).
        /// </summary>
        public void Render(AudioSource source, ISourceClock clock, double pitchRatio, float[] left, float[] right, int offset, int count, float gain)
        {
            for (int i = 0; i < count; i++)
            {
                while (nextFrame * Hop <= time)
                {
                    Frame(source, clock, pitchRatio);
                    nextFrame++;
                }
                int slot = (int)(time % Ring);
                left[offset + i] += gain * (float)(olaL[slot] / Overlap);
                right[offset + i] += gain * (float)(olaR[slot] / Overlap);
                olaL[slot] = 0;
                olaR[slot] = 0;
                time++;
            }
        }

        void Frame(AudioSource source, ISourceClock clock, double ratio)
        {
            long start = nextFrame * Hop;
            double center = clock.SourceAt(start + Size / 2);
            long at = (long)Math.Round(center) - Size / 2;           // the analysis window's first sample
            for (int n = 0; n < Size; n++)
            {
                double w = window[n];
                float l = source.L(at + n), r = source.R(at + n);
                reL[n] = l * w; imL[n] = 0;
                reR[n] = r * w; imR[n] = 0;
                reM[n] = 0.5 * (l + r) * w; imM[n] = 0;
                reP[n] = 0.5 * (source.L(at - Hop + n) + source.R(at - Hop + n)) * w; imP[n] = 0;
            }
            fft.Transform(reL, imL);
            fft.Transform(reR, imR);
            fft.Transform(reM, imM);
            fft.Transform(reP, imP);

            for (int k = 0; k < Bins; k++)
            {
                magL[k] = Math.Sqrt(reL[k] * reL[k] + imL[k] * imL[k]);
                magR[k] = Math.Sqrt(reR[k] * reR[k] + imR[k] * imR[k]);
                magM[k] = Math.Sqrt(reM[k] * reM[k] + imM[k] * imM[k]);
                double pm = Math.Atan2(imM[k], reM[k]);
                double pp = Math.Atan2(imP[k], reP[k]);
                phaseM[k] = pm;
                offsetL[k] = Wrap(Math.Atan2(imL[k], reL[k]) - pm);
                offsetR[k] = Wrap(Math.Atan2(imR[k], reR[k]) - pm);
                double expected = 2 * Math.PI * k * Hop / Size;
                advance[k] = expected + Wrap(pm - pp - expected);       // the bin's true phase advance over a hop
            }

            LockedPhases(ratio);
            fresh = false;

            // Each channel: the sum's phase plus its own offset from it.
            for (int k = 0; k < Bins; k++)
            {
                double pl = outPhase[k] + outOffL[k], pr = outPhase[k] + outOffR[k];
                reL[k] = outMagL[k] * Math.Cos(pl); imL[k] = outMagL[k] * Math.Sin(pl);
                reR[k] = outMagR[k] * Math.Cos(pr); imR[k] = outMagR[k] * Math.Sin(pr);
                if (k > 0 && k < Size / 2)
                {
                    reL[Size - k] = reL[k]; imL[Size - k] = -imL[k];
                    reR[Size - k] = reR[k]; imR[Size - k] = -imR[k];
                }
            }
            imL[0] = imR[0] = 0;
            imL[Size / 2] = imR[Size / 2] = 0;
            fft.Transform(reL, imL, inverse: true);
            fft.Transform(reR, imR, inverse: true);
            for (int n = 0; n < Size; n++)
            {
                long t = start + n;
                if (t < time) continue;                                  // already out (frames before the reset)
                int slot = (int)(((t % Ring) + Ring) % Ring);
                olaL[slot] += (float)(reL[n] * window[n]);
                olaR[slot] += (float)(reR[n] * window[n]);
            }
        }

        /// <summary>
        /// Identity phase locking, transposed by the ratio (Laroche and Dolson): each spectral
        /// peak moves to its frequency times the ratio with the bins round it (its region, the
        /// bins nearer it than any other peak), keeping their shape. A peak's phase goes on from
        /// the peak whose region it was in last frame, advanced by its own frequency times the
        /// ratio; the bins of its region keep their phase offset to it.
        /// </summary>
        void LockedPhases(double ratio)
        {
            int peaks = 0;
            for (int k = 0; k < Bins; k++)
            {
                double m = magM[k];
                peak[k] = k >= 2 && k < Bins - 2 && m > magM[k - 1] && m > magM[k - 2] && m >= magM[k + 1] && m >= magM[k + 2] && m > 1e-9;
                if (peak[k]) peaks++;
            }
            if (peaks == 0)
                for (int k = 0; k < Bins; k++) peak[k] = true;          // nothing stands out: each bin its own
            // Each bin's nearest peak.
            int last = -1;
            for (int k = 0; k < Bins; k++)
            {
                if (peak[k]) last = k;
                nearest[k] = last;
            }
            int next = -1;
            for (int k = Bins - 1; k >= 0; k--)
            {
                if (peak[k]) { next = k; continue; }
                if (next >= 0 && (nearest[k] < 0 || next - k < k - nearest[k])) nearest[k] = next;
            }
            // The peaks, moved: their phases go on from last frame's.
            for (int k = 0; k < Bins; k++)
            {
                if (!peak[k]) continue;
                int to = (int)Math.Round(k * ratio);
                if (to < 0 || to >= Bins) continue;
                if (fresh) peakPhase[to] = phaseM[k];
                else
                {
                    int from = lastOwner[to] >= 0 ? lastOwner[to] : to;
                    peakPhase[to] = Wrap(synth[from] + advance[k] * ratio);
                }
            }
            Array.Clear(outMagL);
            Array.Clear(outMagR);
            Array.Clear(outWeight);
            Array.Clear(outPhase);
            Array.Clear(outOffL);
            Array.Clear(outOffR);
            for (int k = 0; k < Bins; k++) owner[k] = -1;
            // Each region round its moved peak.
            for (int k = 0; k < Bins; k++)
            {
                int p = nearest[k];
                if (p < 0) continue;
                int to = (int)Math.Round(p * ratio);
                int t = k + to - p;
                if (to < 0 || to >= Bins || t < 0 || t >= Bins) continue;
                if (owner[t] >= 0 && outWeight[t] >= magM[k]) continue;   // two regions on a bin: the louder
                outMagL[t] = magL[k];
                outMagR[t] = magR[k];
                outWeight[t] = magM[k];
                outPhase[t] = peakPhase[to] + (phaseM[k] - phaseM[p]);
                outOffL[t] = offsetL[k];
                outOffR[t] = offsetR[k];
                owner[t] = to;
            }
            for (int t = 0; t < Bins; t++)
            {
                synth[t] = Wrap(outPhase[t]);
                lastOwner[t] = owner[t];
            }
        }

        static double Wrap(double phase)
        {
            phase %= 2 * Math.PI;
            if (phase > Math.PI) phase -= 2 * Math.PI;
            else if (phase < -Math.PI) phase += 2 * Math.PI;
            return phase;
        }
    }
}
