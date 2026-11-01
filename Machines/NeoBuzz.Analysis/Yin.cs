#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): pitch of one voice (de Cheveigné and
// Kawahara's YIN), on the audio low-passed and decimated to about six times the highest pitch
// (12 kHz at 48 kHz; the pitch needs no more). Per 10 ms frame: the difference function d(τ), here weighted by a Hann window
// over two periods of the lowest pitch so the frame's edges don't bias it; its cumulative-mean
// normalisation; the first dip under the threshold; then the dip's exact position from the
// band-limited d(τ) (Newton steps on its Fourier series), which keeps high notes within a cent.
// Confidence is 1 − d'(τ). Frames join into notes while the pitch stays within 0.6 semitones.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class PitchFrame
    {
        /// <summary>The analysis window's centre, seconds.</summary>
        public double Time;
        public double Hz;
        /// <summary>Fractional MIDI number (69 = A4 = 440 Hz).</summary>
        public double Midi;
        /// <summary>Cents from the nearest equal-tempered note.</summary>
        public double Cents => 100 * (Midi - Math.Round(Midi));
        /// <summary>1 − d'(τ): near 1 for a clean periodic tone.</summary>
        public double Confidence;
        public bool Voiced;
    }

    internal sealed class PitchNote
    {
        public double Start, End;
        public int Midi;
        /// <summary>Median frequency over the note, Hz.</summary>
        public double Hz;
        /// <summary>Median cents from <see cref="Midi"/>'s equal-tempered pitch.</summary>
        public double Cents;
        public double Confidence;
        public int Frames;
    }

    internal sealed class PitchTrack
    {
        public double HopSeconds;
        public PitchFrame[] Frames;
        public List<PitchNote> Notes;
        /// <summary>Share of frames with a pitch, 0 … 1.</summary>
        public double VoicedShare;
        /// <summary>Median over the voiced frames (NaN when none are).</summary>
        public double MedianHz = double.NaN, MedianMidi = double.NaN;
    }

    internal static class Yin
    {
        public static PitchTrack Track(float[] audio, int sampleRate, double minHz = 40, double maxHz = 2000, double hopSeconds = 0.01,
                                       double threshold = 0.15, IReadOnlyList<double> onsets = null, double gateDb = -60)
        {
            int factor = Math.Max(1, (int)Math.Floor(sampleRate / (6.0 * maxHz)));
            var x = factor > 1 ? Decimate(audio, factor) : audio;
            double rate = sampleRate / (double)factor;
            int tauMin = Math.Max(2, (int)Math.Floor(rate / maxHz));
            int tauMax = (int)Math.Ceiling(rate / minHz);
            int w = 2 * tauMax;                  // the weighted window: two periods of the lowest pitch
            int len = w + tauMax + 2;            // the window and every lag after it
            int m = Fft.AtLeast(len);
            var fft = new Fft(m);
            var h = new double[w];
            double hSum = 0;
            for (int j = 0; j < w; j++)
            {
                h[j] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (j + 0.5) / w);
                hSum += h[j];
            }
            var hRe = new double[m];
            var hIm = new double[m];
            Array.Copy(h, hRe, w);
            fft.Transform(hRe, hIm);

            int hop = Math.Max(1, (int)Math.Round(hopSeconds * rate));
            int n = x.Length;
            int count = n >= w ? (n - w) / hop + 1 : 0;
            var frames = new PitchFrame[count];
            double gate = Math.Pow(10, gateDb / 10);
            var re1 = new double[m]; var im1 = new double[m];
            var re2 = new double[m]; var im2 = new double[m];
            var cr = new double[m]; var ci = new double[m];
            var gr = new double[m / 2 + 1]; var gi = new double[m / 2 + 1];
            var d = new double[tauMax + 2];
            var dn = new double[tauMax + 2];
            for (int f = 0; f < count; f++)
            {
                int t = f * hop;
                var frame = new PitchFrame { Time = (t + w / 2.0) / rate, Hz = double.NaN, Midi = double.NaN };
                frames[f] = frame;
                double energy = 0;
                for (int i = 0; i < m; i++)
                {
                    double b = i < len && t + i < n ? x[t + i] : 0;
                    re1[i] = i < w ? h[i] * b : 0;
                    im1[i] = b;
                    re2[i] = b * b;
                    im2[i] = 0;
                    if (i < w) energy += h[i] * b * b;
                }
                if (energy / hSum < gate) continue;
                fft.Transform(re1, im1);
                fft.Transform(re2, im2);
                for (int k = 0; k < m; k++)
                {
                    Fft.Split(re1, im1, k, out double ar, out double ai, out double br, out double bi);
                    // c1 = h·a ⋆ b, c2 = h ⋆ b²: conj(first)·second.
                    double g1r = ar * br + ai * bi, g1i = ar * bi - ai * br;
                    double g2r = hRe[k] * re2[k] + hIm[k] * im2[k], g2i = hRe[k] * im2[k] - hIm[k] * re2[k];
                    cr[k] = g1r - g2i;
                    ci[k] = g1i + g2r;
                    if (k <= m / 2)
                    {
                        gr[k] = g2r - 2 * g1r;
                        gi[k] = g2i - 2 * g1i;
                    }
                }
                fft.Transform(cr, ci, inverse: true);
                double run = 0;
                dn[0] = 1;
                for (int tau = 0; tau <= tauMax + 1; tau++)
                {
                    d[tau] = Math.Max(0, energy + ci[tau] - 2 * cr[tau]);
                    if (tau == 0) continue;
                    run += d[tau];
                    dn[tau] = run > 0 ? d[tau] * tau / run : 1;
                }
                int best = -1;
                for (int tau = tauMin; tau <= tauMax; tau++)
                {
                    if (dn[tau] >= threshold) continue;
                    while (tau + 1 <= tauMax && dn[tau + 1] < dn[tau]) tau++;
                    best = tau;
                    break;
                }
                frame.Voiced = best >= 0;
                if (best < 0)
                {
                    best = tauMin;
                    for (int tau = tauMin + 1; tau <= tauMax; tau++) if (dn[tau] < dn[best]) best = tau;
                }
                frame.Confidence = Math.Clamp(1 - dn[best], 0, 1);
                double period = Refine(d, best, tauMin, tauMax, gr, gi, m);
                frame.Hz = rate / period;
                frame.Midi = 69 + 12 * Math.Log2(frame.Hz / 440);
            }

            var track = new PitchTrack { HopSeconds = hop / (double)rate, Frames = frames };
            var voiced = frames.Where(fr => fr.Voiced).ToList();
            track.VoicedShare = count > 0 ? voiced.Count / (double)count : 0;
            if (voiced.Count > 0)
            {
                track.MedianHz = Median(voiced.Select(v => v.Hz));
                track.MedianMidi = 69 + 12 * Math.Log2(track.MedianHz / 440);
            }
            track.Notes = Notes(frames, track.HopSeconds, onsets);
            return track;
        }

        /// <summary>Every <paramref name="factor"/>th sample of <paramref name="x"/> after a windowed-sinc low-pass at 0.4 of the new rate.</summary>
        static float[] Decimate(float[] x, int factor)
        {
            int half = 8 * factor;
            double cutoff = 0.4 / factor;
            var h = new double[2 * half + 1];
            double sum = 0;
            for (int k = 0; k < h.Length; k++)
            {
                double t = k - half;
                double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / (h.Length - 1)) + 0.08 * Math.Cos(4 * Math.PI * k / (h.Length - 1));
                h[k] = sinc * w;
                sum += h[k];
            }
            var y = new float[x.Length / factor];
            for (int m = 0; m < y.Length; m++)
            {
                int centre = m * factor, from = Math.Max(0, half - centre), to = Math.Min(h.Length, x.Length - centre + half);
                double acc = 0;
                for (int k = from; k < to; k++) acc += h[k] * x[centre + k - half];
                y[m] = (float)(acc / sum);
            }
            return y;
        }

        /// <summary>
        /// The dip's position between samples: a parabola through d at the three lags around it,
        /// then Newton steps on d(τ) = E + (1/m)·Σ Re(G_k e^{2πikτ/m}), its exact band-limited form.
        /// </summary>
        static double Refine(double[] d, int best, int tauMin, int tauMax, double[] gr, double[] gi, int m)
        {
            double tau = best;
            if (best > tauMin && best < tauMax + 1)
            {
                double a = d[best - 1], b = d[best], c = d[best + 1], den = a - 2 * b + c;
                if (den > 0) tau = best + Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
            }
            double start = tau, omega = 2 * Math.PI / m;
            for (int iter = 0; iter < 6; iter++)
            {
                double c1 = Math.Cos(omega * tau), s1 = Math.Sin(omega * tau);
                double zr = 1, zi = 0, d1 = 0, d2 = 0;
                for (int k = 1; k < m / 2; k++)
                {
                    double nr = zr * c1 - zi * s1;
                    zi = zr * s1 + zi * c1;
                    zr = nr;
                    double wk = omega * k;
                    double pr = gr[k] * zr - gi[k] * zi, pi = gr[k] * zi + gi[k] * zr;
                    d1 -= wk * pi;
                    d2 -= wk * wk * pr;
                }
                if (d2 <= 0) break;
                double step = Math.Clamp(-d1 / d2, -0.5, 0.5);
                tau += step;
                if (Math.Abs(step) < 1e-6) break;
            }
            return Math.Abs(tau - best) <= 1 ? tau : start;
        }

        /// <summary>
        /// Voiced frames joined into notes: a note ends at a gap of more than 20 ms, a pitch more
        /// than 0.6 semitones from its median so far, or an onset. Notes shorter than 40 ms are dropped.
        /// </summary>
        static List<PitchNote> Notes(PitchFrame[] frames, double hop, IReadOnlyList<double> onsets)
        {
            var notes = new List<PitchNote>();
            int i = 0;
            while (i < frames.Length)
            {
                if (!frames[i].Voiced) { i++; continue; }
                var members = new List<PitchFrame> { frames[i] };
                double median = frames[i].Midi;
                int last = i, j = i + 1;
                for (; j < frames.Length; j++)
                {
                    if (j - last > 2) break;
                    if (onsets != null && members.Count >= 3 && onsets.Any(o => o > frames[j - 1].Time && o <= frames[j].Time)) break;
                    if (!frames[j].Voiced) continue;
                    if (Math.Abs(frames[j].Midi - median) > 0.6) break;
                    members.Add(frames[j]);
                    last = j;
                    if (members.Count <= 5 || members.Count % 4 == 0) median = Median(members.Select(fr => fr.Midi));
                }
                double start = frames[i].Time - hop / 2, end = frames[last].Time + hop / 2;
                if (end - start >= 0.04)
                {
                    double hz = Median(members.Select(fr => fr.Hz));
                    double midi = 69 + 12 * Math.Log2(hz / 440);
                    int note = (int)Math.Round(midi);
                    notes.Add(new PitchNote
                    {
                        Start = start,
                        End = end,
                        Midi = note,
                        Hz = hz,
                        Cents = 100 * (midi - note),
                        Confidence = Median(members.Select(fr => fr.Confidence)),
                        Frames = members.Count,
                    });
                }
                i = Math.Max(last + 1, i + 1);
            }
            return notes;
        }

        static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            if (v.Length == 0) return double.NaN;
            return v.Length % 2 == 1 ? v[v.Length / 2] : 0.5 * (v[v.Length / 2 - 1] + v[v.Length / 2]);
        }
    }
}
