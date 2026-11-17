#nullable disable
// Neo machines shared DSP (docs/specs/pitch.md §2): the real-time monophonic pitch tracker Neo Tune, Neo Voice to MIDI and
// Neo Audio to MIDI share.
//  - YIN (de Cheveigné and Kawahara) on the newest W samples against the same W samples τ back, for every lag τ up to the
//    lowest pitch: d(τ) = Σ (x[m] − x[m−τ])² = E_new + E_lagged(τ) − 2 r(τ), the correlation r from one pair of FFTs (the
//    newest block and the whole span packed as the real and imaginary parts of one transform; the product's inverse
//    transform is r), the energies from running sums. Then the cumulative-mean-normalised difference d′(τ), the candidate
//    dips, parabolic interpolation, and a voiced decision with hysteresis. Aligning the window to the newest sample (not
//    the oldest) makes every estimate use the freshest W samples whatever the pitch: the first estimate after a note starts
//    comes W + one period after it.
//  - Octave guards: the candidates are the local minima of d′ within a margin of the deepest one; the shortest period among
//    them wins (the usual YIN preference, which stops the octave-down error of a missed first dip), unless a candidate lies
//    within 0.35 octave of the previous voiced pitch and the shortest does not: then the continuous one wins (which stops an
//    octave jump either way when the spectrum favours a harmonic for a few frames). The deepest dip at the edge of the lag
//    range is no pitch.
//  - A range sets the lowest and highest pitch, the window W and the hop (docs/specs/pitch.md §2.3); Low latency shortens
//    the Voice window to a third and the hop to half, at a lowest pitch of 100 Hz.
// Allocation-free after construction; the portable maths only (no libm); the transforms ComplexFft's. One estimate per hop of
// the tracker's own sample count, so the output doesn't depend on how the host splits the audio.

using System;

namespace NeoBuzz.Dsp
{
    /// <summary>What the tracker listens for: the pitch range sets the lowest and highest pitch, the window and the hop.</summary>
    internal enum PitchRange { Voice, Guitar, Bass, Full }

    /// <summary>Quality: the longest window, the most robust. Low latency: Voice only (the other ranges ignore it).</summary>
    internal enum PitchQuality { Quality, LowLatency }

    /// <summary>One estimate, every hop.</summary>
    internal struct PitchEstimate
    {
        /// <summary>The stream position just after the newest sample the estimate heard (the count of samples so far).</summary>
        public long EndSample;
        /// <summary>Hz; 0 when not voiced.</summary>
        public double F0;
        /// <summary>The best candidate whether voiced or not (0 when none); for displays and for offline analysis.</summary>
        public double RawF0;
        /// <summary>Fractional MIDI note (69 = A4 = 440 Hz); 0 when not voiced.</summary>
        public double Midi;
        /// <summary>Cents from the nearest semitone (−50 … +50); 0 when not voiced.</summary>
        public double Cents;
        /// <summary>1 − d′ at the chosen dip, 0 … 1: near 1 for a clean periodic tone.</summary>
        public float Confidence;
        public bool Voiced;
        /// <summary>RMS of the newest window (linear, full scale 1).</summary>
        public float Rms;
    }

    internal sealed class PitchTracker
    {
        public readonly double SampleRate;
        public readonly PitchRange Range;
        public readonly PitchQuality Quality;
        public readonly double MinHz, MaxHz;
        /// <summary>Samples in the newest window, the lag range's top, the analysed span, the hop, and the transform's size.</summary>
        public readonly int Window, TauMin, TauMax, Span, Hop, FftSize;

        /// <summary>Below this RMS (linear) nothing is voiced. −62 dBFS.</summary>
        public float Gate = 0.0008f;
        /// <summary>Unvoiced becomes voiced at this confidence or more, voiced becomes unvoiced below <see cref="OffConfidence"/>.</summary>
        public float OnConfidence = 0.8f, OffConfidence = 0.55f;
        /// <summary>A dip within this much (in d′) of the deepest is a candidate.</summary>
        public float CandidateMargin = 0.10f;
        /// <summary>No candidate above this d′.</summary>
        public float CandidateCeiling = 0.45f;
        /// <summary>The first candidate gives way to one near the last pitch only if its dip is this much (in d′) shallower.</summary>
        public float ContinuityMargin = 0.04f;

        readonly ComplexFft fft;
        readonly float[] re, im, cre, cim;
        readonly float[] hist;
        readonly double[] cs;
        readonly float[] dif, nd;
        readonly int[] cand;
        int pos, sinceHop;
        long total;
        readonly double dcR;
        double dcX, dcY;
        bool voiced;
        double prevF0;

        // Range tables (docs/specs/pitch.md §2.3): lowest and highest Hz, the window and the hop in seconds.
        static void Table(PitchRange range, PitchQuality quality, out double lo, out double hi, out double win, out double hop)
        {
            switch (range)
            {
                case PitchRange.Voice:
                    if (quality == PitchQuality.LowLatency) { lo = 100; hi = 1100; win = 0.005; hop = 0.00135; }
                    else { lo = 70; hi = 1100; win = 0.021; hop = 0.0027; }
                    break;
                case PitchRange.Guitar: lo = 80; hi = 1200; win = 0.019; hop = 0.0027; break;
                case PitchRange.Bass: lo = 38; hi = 400; win = 0.040; hop = 0.0053; break;
                default: lo = 38; hi = 2000; win = 0.040; hop = 0.0053; break;
            }
        }

        public PitchTracker(double sampleRate, PitchRange range, PitchQuality quality = PitchQuality.Quality)
        {
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            Range = range;
            Quality = range == PitchRange.Voice ? quality : PitchQuality.Quality;
            Table(Range, Quality, out MinHz, out MaxHz, out double win, out double hop);
            Window = (int)Math.Round(win * sampleRate);
            TauMin = Math.Max(2, (int)Math.Floor(sampleRate / MaxHz));
            TauMax = (int)Math.Ceiling(sampleRate / MinHz);
            // τ + 1 for the interpolation's neighbour.
            Span = Window + TauMax + 2;
            Hop = Math.Max(16, (int)Math.Round(hop * sampleRate));
            int n = 8;
            while (n < Span) n <<= 1;
            FftSize = n;
            fft = new ComplexFft(n);
            re = new float[n]; im = new float[n]; cre = new float[n]; cim = new float[n];
            hist = new float[Span];
            cs = new double[Span + 1];
            dif = new float[TauMax + 3];
            nd = new float[TauMax + 3];
            cand = new int[TauMax / 2 + 2];
            dcR = 1.0 - 2 * Math.PI * 15.0 / sampleRate;
        }

        /// <summary>The audio the estimate describes lies this many samples before the stream's end, on average: half the span.</summary>
        public int Latency => Span / 2;

        /// <summary>How long after a note starts its first estimate can be right, for a note of <paramref name="hz"/>: a window and a period.</summary>
        public double DetectionSeconds(double hz) => (Window + SampleRate / hz) / SampleRate;

        public void Reset()
        {
            Array.Clear(hist, 0, hist.Length);
            pos = 0; sinceHop = 0; total = 0; dcX = dcY = 0; voiced = false; prevF0 = 0;
        }

        /// <summary>The most estimates <see cref="Process"/> can return for <paramref name="count"/> samples.</summary>
        public int MaxFrames(int count) => count / Hop + 1;

        /// <summary>Feeds mono audio; writes the estimates that fall in it to <paramref name="frames"/> (at least <see cref="MaxFrames"/> long from the offset) and returns how many.</summary>
        public int Process(float[] x, int offset, int count, PitchEstimate[] frames, int frameOffset)
        {
            if (frames.Length - frameOffset < MaxFrames(count)) throw new ArgumentException("frames is shorter than MaxFrames(count)", nameof(frames));
            int produced = 0;
            for (int i = 0; i < count; i++)
            {
                double s = x[offset + i];
                double y = s - dcX + dcR * dcY;
                dcX = s; dcY = y;
                hist[pos] = (float)y;
                if (++pos == Span) pos = 0;
                total++;
                if (++sinceHop >= Hop)
                {
                    sinceHop = 0;
                    if (total >= Span) frames[frameOffset + produced++] = Analyse();
                }
            }
            return produced;
        }

        PitchEstimate Analyse()
        {
            int n = FftSize, span = Span, w = Window;
            // The span in time order: z = a + i·b, a the newest window (the rest 0), b the whole span.
            int p = pos;
            double acc = 0;
            cs[0] = 0;
            for (int j = 0; j < span; j++)
            {
                float v = hist[p];
                if (++p == span) p = 0;
                im[j] = v;
                re[j] = j >= span - w ? v : 0f;
                acc += (double)v * v;
                cs[j + 1] = acc;
            }
            for (int j = span; j < n; j++) { re[j] = 0; im[j] = 0; }
            double eNew = cs[span] - cs[span - w];
            float rms = (float)Math.Sqrt(eNew / w);

            var est = new PitchEstimate { EndSample = total, Rms = rms };
            if (rms < Gate * 0.5f)
            {
                voiced = false;
                return est;
            }

            fft.Transform(re, im, false);
            // A = (Z[k] + conj Z[N−k]) / 2, B = (Z[k] − conj Z[N−k]) / 2i; C = A · conj B: r(τ) = Σ a[m] b[m−τ] = IFFT(C).
            int mask = n - 1;
            for (int k = 0; k < n; k++)
            {
                int q = (n - k) & mask;
                float zr = re[k], zi = im[k], yr = re[q], yi = im[q];
                float ar = 0.5f * (zr + yr), ai = 0.5f * (zi - yi);
                // (zr + i zi) − (yr − i yi) = (zr − yr) + i (zi + yi); divided by 2i: ((zi + yi) − i (zr − yr)) / 2.
                float br = 0.5f * (zi + yi), bi = -0.5f * (zr - yr);
                cre[k] = ar * br + ai * bi;
                cim[k] = ai * br - ar * bi;
            }
            fft.Transform(cre, cim, true);
            float inv = 1f / n;

            // d(τ), then d′(τ) = d(τ) τ / Σ_{j ≤ τ} d(j).
            int tauMax = TauMax + 1;
            double run = 0;
            nd[0] = 1f;
            dif[0] = 0f;
            for (int tau = 1; tau <= tauMax; tau++)
            {
                double eLag = cs[span - tau] - cs[span - w - tau];
                double d = eNew + eLag - 2.0 * cre[tau] * inv;
                if (d < 0) d = 0;
                dif[tau] = (float)d;
                run += d;
                nd[tau] = run > 1e-20 ? (float)(d * tau / run) : 1f;
            }

            // The deepest dip in the lag range, and the candidates within the margin of it. A dip is ranked by the value of the
            // parabola through it and its neighbours (its interpolated minimum): a period that isn't a whole number of samples
            // leaves the dip at the nearest lag higher than it is (more for a bright voice), and a multiple of the period that
            // happens to fall on a whole sample would otherwise win by it.
            int lo = TauMin, hi = TauMax;
            float best = float.MaxValue;
            int bestTau = 0;
            for (int tau = lo; tau <= hi; tau++)
            {
                float v = nd[tau];
                if (v < nd[tau - 1] && v <= nd[tau + 1])
                {
                    v = Vertex(tau);
                    if (v < best) { best = v; bestTau = tau; }
                }
            }
            if (bestTau == 0)
            {
                voiced = false;
                return est;
            }
            int nc = 0;
            float limit = Math.Min(best + CandidateMargin, CandidateCeiling);
            for (int tau = lo; tau <= hi; tau++)
            {
                float v = nd[tau];
                if (v < nd[tau - 1] && v <= nd[tau + 1] && Vertex(tau) <= limit) cand[nc++] = tau;
            }
            // No dip under the ceiling: nothing periodic (noise); the deepest dip still goes out as the raw candidate.
            int chosen = nc > 0 ? cand[0] : bestTau;
            if (nc > 0 && voiced && prevF0 > 0)
            {
                // Continuity: the first candidate stands unless it is more than 0.35 octave from the last pitch, another is
                // within it, and the first is clearly the worse dip (by ContinuityMargin): a tie goes to the shortest
                // period, so a wrong octave taken at a note's start doesn't hold for the whole note.
                double prevTau = SampleRate / prevF0;
                int first = chosen;
                if (Math.Abs(OctavesBetween(first, prevTau)) > 0.35)
                {
                    double closest = double.MaxValue;
                    int near = 0;
                    for (int c = 0; c < nc; c++)
                    {
                        double dist = Math.Abs(OctavesBetween(cand[c], prevTau));
                        if (dist < closest) { closest = dist; near = cand[c]; }
                    }
                    if (closest <= 0.35 && Vertex(first) > Vertex(near) + ContinuityMargin) chosen = near;
                }
            }
            // The deepest dip at the edge of the lag range is no pitch.
            if (chosen >= TauMax - 1 && best > 0.1f)
            {
                voiced = false;
                return est;
            }

            // Parabolic interpolation on d′.
            double a = nd[chosen - 1], b = nd[chosen], c2 = nd[chosen + 1];
            double den = a - 2 * b + c2;
            double shift = den > 1e-12 ? 0.5 * (a - c2) / den : 0;
            if (shift > 1) shift = 1; else if (shift < -1) shift = -1;
            double period = chosen + shift;
            double f0 = SampleRate / period;
            float conf = Math.Clamp(1f - Vertex(chosen), 0f, 1f);

            est.RawF0 = f0;
            est.Confidence = conf;
            if (!voiced) voiced = conf >= OnConfidence && rms >= Gate;
            else if (conf < OffConfidence || rms < Gate * 0.5f) voiced = false;
            if (voiced)
            {
                prevF0 = f0;
                est.Voiced = true;
                est.F0 = f0;
                double midi = 69.0 + 12.0 * PortableMath.Log2(f0 / 440.0);
                est.Midi = midi;
                est.Cents = 100.0 * (midi - Math.Round(midi));
            }
            return est;
        }

        /// <summary>d′ at the minimum of the parabola through the dip at <paramref name="tau"/> and its neighbours.</summary>
        float Vertex(int tau)
        {
            float a = nd[tau - 1], b = nd[tau], c = nd[tau + 1];
            float den = a - 2 * b + c;
            if (den <= 1e-9f) return b;
            float v = b - (a - c) * (a - c) / (8 * den);
            return v < 0 ? 0 : v;
        }

        double OctavesBetween(int tau, double prevTau) => PortableMath.Log2(prevTau / tau);
    }
}
