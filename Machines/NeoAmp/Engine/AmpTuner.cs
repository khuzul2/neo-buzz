#nullable disable
// Neo Amp (docs/specs/neo-amp.md §6): the tuner. The DI (after Trim, before the gate) goes to the shared pitch tracker
// (NeoBuzz.Dsp/PitchTracker.cs, YIN) in its Full range. That range stops at 38 Hz, and a bass's low B is 30.9 Hz, so the tracker is told the
// sample rate is 1.4 times the real one: it then sees every pitch 1.4 times higher (low B reads 43 Hz, E6 reads 1847 Hz, both inside its range)
// and what it reports is divided by 1.4 (the period in samples, which is what matters, is untouched). The tracker's estimate is the starting
// point of a refinement over the DI itself: the normalised cross-correlation of the newest window with itself one period back, at fractional
// periods (4-point Lagrange interpolation), a coarse search then a fine one, each ended by a parabola. That lands within a few thousandths of a
// cent of the true period for a clean string, which the plain integer-lag interpolation of the tracker does not at the top of the range.
//
// The reading is for the editor and for agents: note, cents, frequency and level. It is published without a lock (a sequence number: odd while
// being written; the reader retries) and is held while the note decays (the tracker no longer sure but the level still above −70 dBFS), and
// cleared after 150 ms of silence. The tuner runs only while the Tuner switch is on.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Amp
{
    /// <summary>What the tuner shows.</summary>
    internal readonly struct TunerReading
    {
        public readonly bool Valid;
        /// <summary>Frequency of the note being played, Hz.</summary>
        public readonly double Hz;
        /// <summary>The nearest note's MIDI number (A4 = 69) and how far the pitch is from it, cents (−50 … +50).</summary>
        public readonly int Note;
        public readonly double Cents;
        /// <summary>Level of the DI, linear RMS.</summary>
        public readonly float Level;

        public TunerReading(bool valid, double hz, int note, double cents, float level) { Valid = valid; Hz = hz; Note = note; Cents = cents; Level = level; }

        static readonly string[] Letters = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>The note's name with its octave (E2, C#4); empty when there is no reading.</summary>
        public string NoteName => !Valid ? "" : Letters[((Note % 12) + 12) % 12] + (Note / 12 - 1);
    }

    internal sealed class AmpTuner
    {
        /// <summary>The factor the tracker's sample rate is told (see the file's comment).</summary>
        public const double Stretch = 1.4;
        const int RingSize = 16384;
        const int Mask = RingSize - 1;
        const float SilenceLevel = 0.0003f;       // −70 dBFS
        const double HoldSeconds = 2.0, ClearSeconds = 0.15;

        readonly double rate;
        readonly PitchTracker tracker;
        readonly float[] ring = new float[RingSize];
        readonly PitchEstimate[] frames;
        long total;
        long lastVoiced = long.MinValue / 2, quietSince = -1;
        double heldHz;
        bool haveHeld;

        // The published reading (sequence number: odd while being written).
        int seq;
        bool pValid;
        double pHz, pCents;
        int pNote;
        float pLevel;

        public AmpTuner(double sampleRate, int maxBlock = 256)
        {
            rate = sampleRate;
            tracker = new PitchTracker(sampleRate * Stretch, PitchRange.Full) { Gate = SilenceLevel };
            frames = new PitchEstimate[tracker.MaxFrames(maxBlock) + 2];
        }

        public double Rate => rate;

        /// <summary>A4 in Hz (the Reference parameter).</summary>
        public double Reference = 440.0;

        public void Reset()
        {
            tracker.Reset();
            Array.Clear(ring, 0, ring.Length);
            total = 0;
            lastVoiced = long.MinValue / 2;
            quietSince = -1;
            haveHeld = false;
            Publish(false, 0, 0, 0, 0);
        }

        /// <summary>Feeds mono DI (audio thread, at most the block size the constructor was told).</summary>
        public void Process(float[] x, int n)
        {
            for (int i = 0; i < n; i++) ring[(total + i) & Mask] = x[i];
            long start = total;
            total += n;
            int produced = tracker.Process(x, 0, n, frames, 0);
            for (int f = 0; f < produced; f++) Handle(frames[f]);
            if (produced == 0 && total - start > 0)
            {
                // No estimate in this block: the level alone can end the reading.
                double acc = 0;
                int m = Math.Min(n, 256);
                for (int i = 0; i < m; i++) acc += (double)x[n - 1 - i] * x[n - 1 - i];
                float rms = (float)Math.Sqrt(acc / Math.Max(1, m));
                if (rms < SilenceLevel) CheckSilence(rms);
            }
        }

        void Handle(in PitchEstimate e)
        {
            if (e.Voiced && e.F0 > 0)
            {
                double period = tracker.SampleRate / e.F0;
                double refined = Refine(period, e.EndSample);
                double hz = rate / refined;
                lastVoiced = total;
                quietSince = -1;
                heldHz = hz;
                haveHeld = true;
                PublishHz(hz, e.Rms);
                return;
            }
            if (e.Rms < SilenceLevel)
            {
                CheckSilence(e.Rms);
                return;
            }
            quietSince = -1;
            // Held while the note decays: the tracker is unsure, the level is still there.
            if (haveHeld && total - lastVoiced < HoldSeconds * rate) PublishHz(heldHz, e.Rms);
            else Publish(false, 0, 0, 0, e.Rms);
        }

        void CheckSilence(float rms)
        {
            if (quietSince < 0) quietSince = total;
            if (total - quietSince >= ClearSeconds * rate)
            {
                haveHeld = false;
                Publish(false, 0, 0, 0, rms);
            }
        }

        void PublishHz(double hz, float level)
        {
            double note = 69.0 + 12.0 * PortableMath.Log2(hz / Reference);
            int nearest = (int)Math.Floor(note + 0.5);
            Publish(true, hz, nearest, (note - nearest) * 100.0, level);
        }

        void Publish(bool valid, double hz, int note, double cents, float level)
        {
            Volatile.Write(ref seq, seq + 1);
            pValid = valid; pHz = hz; pNote = note; pCents = cents; pLevel = level;
            Volatile.Write(ref seq, seq + 1);
        }

        /// <summary>The reading (any thread, no lock).</summary>
        public TunerReading Read()
        {
            for (int tries = 0; tries < 64; tries++)
            {
                int a = Volatile.Read(ref seq);
                if ((a & 1) != 0) continue;
                var r = new TunerReading(pValid, pHz, pNote, pCents, pLevel);
                if (Volatile.Read(ref seq) == a) return r;
            }
            return new TunerReading(false, 0, 0, 0, 0);
        }

        // ---- the refinement -------------------------------------------------

        double Refine(double period, long endSample)
        {
            // The newest sample of the estimate's window in the ring: the sample before endSample.
            long end = endSample - 1;
            if (total - end > RingSize / 4) return period;
            int w = (int)Math.Min(3072, Math.Max(512, (long)(period * 4)));
            if (period + w + 8 > RingSize) return period;
            double centre = period;
            double step = 0.25;
            double best = double.NegativeInfinity, bestTau = centre;
            // Coarse: five points, walking the window while the best is at its edge.
            for (int walk = 0; walk < 4; walk++)
            {
                int bi = 0;
                best = double.NegativeInfinity;
                for (int k = 0; k < 5; k++)
                {
                    double tau = centre + (k - 2) * step;
                    double c = Ncc(tau, end, w);
                    if (c > best) { best = c; bi = k; bestTau = tau; }
                }
                if (bi == 0 || bi == 4) { centre = bestTau; continue; }
                centre = Parabola(centre, step, bi, end, w);
                break;
            }
            // Fine: five points a quarter of the coarse step apart.
            double fineStep = 0.06;
            double fbest = double.NegativeInfinity;
            int fi = 2;
            for (int k = 0; k < 5; k++)
            {
                double c = Ncc(centre + (k - 2) * fineStep, end, w);
                if (c > fbest) { fbest = c; fi = k; }
            }
            if (fi == 0 || fi == 4) return centre + (fi - 2) * fineStep;
            return Parabola(centre, fineStep, fi, end, w);
        }

        /// <summary>The vertex of the parabola through the three points around the best of five (centre + (k − 2) · step).</summary>
        double Parabola(double centre, double step, int best, long end, int w)
        {
            double t0 = centre + (best - 3) * step, t1 = centre + (best - 2) * step, t2 = centre + (best - 1) * step;
            double a = Ncc(t0, end, w), b = Ncc(t1, end, w), c = Ncc(t2, end, w);
            double den = a - 2 * b + c;
            if (den >= -1e-18) return t1;
            double d = 0.5 * (a - c) / den;
            return t1 + Math.Clamp(d, -1.0, 1.0) * step;
        }

        /// <summary>Normalised cross-correlation of the w samples ending at <paramref name="end"/> with themselves <paramref name="tau"/> samples earlier (fractional, cubic Lagrange).</summary>
        double Ncc(double tau, long end, int w)
        {
            int ti = (int)Math.Floor(tau);
            double f = tau - ti;
            // The delayed sample for a at a − tau lies between a − ti − 1 (s1) and a − ti (s2), at p = 1 − f from s1.
            double p = 1.0 - f;
            double c0 = -p * (p - 1) * (p - 2) / 6.0, c1 = (p + 1) * (p - 1) * (p - 2) / 2.0, c2 = -(p + 1) * p * (p - 2) / 2.0, c3 = (p + 1) * p * (p - 1) / 6.0;
            double sxy = 0, sxx = 0, syy = 0;
            for (int m = 0; m < w; m++)
            {
                long a = end - m;
                double x = ring[a & Mask];
                long b = a - ti;
                double y = c0 * ring[(b - 2) & Mask] + c1 * ring[(b - 1) & Mask] + c2 * ring[b & Mask] + c3 * ring[(b + 1) & Mask];
                sxy += x * y; sxx += x * x; syy += y * y;
            }
            double d = Math.Sqrt(sxx * syy);
            return d > 1e-30 ? sxy / d : 0;
        }
    }
}
