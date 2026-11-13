#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.12): the Spectral engine's analysis, off the audio thread, run when a
// sample is loaded. Textbook methods, our own code: a short-time Fourier transform (Hann, 2048 points at 48 kHz,
// a hop of a quarter of it, both scaled up with the sample rate), peak picking with the Hann window's own
// interpolation of frequency and amplitude, McAulay-Quatieri tracking (a peak continues the nearest track within a
// frequency limit; births and deaths between frames), at most 64 partials a frame, and the residual: what is left
// of each frame's power once the partials' own window kernels are taken off, as a level in 24 Bark-like bands.
// Only IEEE basic operations and PortableMath, and total orders in every sort, so the model of a sample is the same
// on every platform.

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal static class SpectralAnalysis
    {
        /// <summary>Peaks below this level under the sample's peak are not partials; a peak must also stand this far (power) over its neighbourhood.</summary>
        const double FloorDb = -72.0, TonalDb = 15.0;

        /// <summary>The transform's size for a sample rate: 2048 at 48 kHz, scaled up with the rate (a power of two, as an FFT needs).</summary>
        public static int SizeFor(int rate) => rate < 72000 ? 2048 : rate < 144000 ? 4096 : 8192;

        /// <summary>
        /// The model of <paramref name="x"/> (mono, at <paramref name="rate"/>). <paramref name="progress"/> gets 0 … 1 as it goes;
        /// <paramref name="cancel"/> stops it (null is returned). Not for the audio thread.
        /// </summary>
        public static SpectralModel Analyse(float[] x, int rate, Action<float> progress = null, CancellationToken cancel = default, string name = "")
        {
            int n = SizeFor(rate), hop = n / 4, half = n / 2;
            int len = x.Length;
            int frames = (len + hop - 1) / hop + 1;
            double binHz = rate / (double)n;
            var win = new double[n];
            double sumW = 0, sumW2 = 0;
            for (int i = 0; i < n; i++)
            {
                win[i] = 0.5 - 0.5 * PortableMath.Cos(2.0 * Math.PI * i / n);
                sumW += win[i];
                sumW2 += win[i] * win[i];
            }
            double peak = 0;
            for (int i = 0; i < len; i++) peak = Math.Max(peak, Math.Abs((double)x[i]));
            double floorAmp = Math.Max(peak * PortableMath.Exp10(FloorDb / 20.0), 1e-5);
            double tonal = PortableMath.Exp10(TonalDb / 10.0);

            var re = new double[n];
            var im = new double[n];
            var mag2 = new double[half + 1];
            var prefix = new double[half + 2];
            var resid = new double[half + 1];
            var near = new double[18];
            var excl = new bool[half + 1];

            // Bands of the residual: the bin where each starts.
            var bandStart = new int[SpectralModel.Bands + 1];
            for (int b = 0; b <= SpectralModel.Bands; b++) bandStart[b] = (int)Math.Min(half + 1, Math.Ceiling(SpectralModel.BandEdges[b] / binHz));
            bandStart[SpectralModel.Bands] = half + 1;

            var start = new int[frames + 1];
            var slots = new List<byte>(frames * 16);
            var freqQ = new List<ushort>(frames * 16);
            var ampQ = new List<ushort>(frames * 16);
            var f0q = new ushort[frames];
            var noiseQ = new byte[SpectralModel.Bands * frames];

            // This frame's candidates and tracks.
            var cf = new double[half]; var ca = new double[half]; var ck = new double[half];
            var order = new int[half];
            var prevSlot = new int[SpectralModel.MaxPartials]; var prevFreq = new double[SpectralModel.MaxPartials]; int prevN = 0;
            var curSlot = new int[SpectralModel.MaxPartials]; var curFreq = new double[SpectralModel.MaxPartials]; var curAmp = new double[SpectralModel.MaxPartials]; var curBin = new double[SpectralModel.MaxPartials];
            var usedPrev = new bool[SpectralModel.MaxPartials]; var usedNow = new bool[SpectralModel.MaxPartials];
            var matchedPrev = new bool[SpectralModel.MaxPartials];
            var pairD = new double[SpectralModel.MaxPartials * SpectralModel.MaxPartials];
            var pairI = new int[SpectralModel.MaxPartials * SpectralModel.MaxPartials];
            var pairJ = new int[SpectralModel.MaxPartials * SpectralModel.MaxPartials];
            var pairOrder = new int[SpectralModel.MaxPartials * SpectralModel.MaxPartials];
            var sortF = new int[SpectralModel.MaxPartials];

            for (int f = 0; f < frames; f++)
            {
                if ((f & 15) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Invoke(f / (float)frames);
                }
                int c0 = f * hop - half;
                for (int i = 0; i < n; i++)
                {
                    int s = c0 + i;
                    re[i] = (uint)s < (uint)len ? x[s] * win[i] : 0.0;
                    im[i] = 0.0;
                }
                Fft.Transform(re, im, false);
                prefix[0] = 0;
                for (int k = 0; k <= half; k++)
                {
                    mag2[k] = re[k] * re[k] + im[k] * im[k];
                    prefix[k + 1] = prefix[k] + mag2[k];
                }

                // Peaks: a local maximum over the floor and over its neighbourhood (the quieter side's mean, 4 … 12 bins out).
                int nc = 0;
                double ampScale = 2.0 / sumW;
                for (int k = 2; k < half - 1; k++)
                {
                    double m = mag2[k];
                    if (!(m > mag2[k - 1] && m >= mag2[k + 1])) continue;
                    double mag = Math.Sqrt(m);
                    if (mag * ampScale < floorAmp) continue;
                    // The neighbourhood's floor: the fifth lowest power of the bins 4 … 12 away on either side (the valleys between partials, not their lobes).
                    double floor = Floor(mag2, k, half, near);
                    if (floor >= 0 && m < tonal * floor) continue;
                    // The Hann window's own interpolation: from the neighbour's ratio to the peak, the offset in bins (exact for a steady sinusoid).
                    double lo = Math.Sqrt(mag2[k - 1]), hi = Math.Sqrt(mag2[k + 1]);
                    int sign = hi >= lo ? 1 : -1;
                    double r = (sign > 0 ? hi : lo) / mag;
                    double d = Math.Clamp((2.0 * r - 1.0) / (r + 1.0), 0.0, 0.5) * sign;
                    double kb = k + d;
                    double wn = Kernel(d);
                    cf[nc] = kb * binHz;
                    ca[nc] = mag * ampScale / Math.Max(0.5, wn);
                    ck[nc] = kb;
                    nc++;
                }
                // The strongest 64 (a total order: amplitude, then frequency).
                for (int i = 0; i < nc; i++) order[i] = i;
                Array.Sort(order, 0, nc, Comparer<int>.Create((a, b) => ca[a] > ca[b] ? -1 : ca[a] < ca[b] ? 1 : cf[a] < cf[b] ? -1 : cf[a] > cf[b] ? 1 : 0));
                int keep = Math.Min(nc, SpectralModel.MaxPartials);

                // Tracks: the closest pairs of an earlier partial and a peak within the frequency limit continue; the rest are born and die.
                int np = 0;
                Array.Clear(matchedPrev, 0, matchedPrev.Length);
                Array.Clear(usedNow, 0, usedNow.Length);
                int pairs = 0;
                for (int a = 0; a < keep; a++)
                {
                    int ci = order[a];
                    for (int j = 0; j < prevN; j++)
                    {
                        double dist = Math.Abs(cf[ci] - prevFreq[j]);
                        double limit = Math.Max(1.5 * binHz, 0.03 * prevFreq[j]);
                        if (dist > limit) continue;
                        pairD[pairs] = dist; pairI[pairs] = a; pairJ[pairs] = j;
                        pairs++;
                    }
                }
                for (int p = 0; p < pairs; p++) pairOrder[p] = p;
                Array.Sort(pairOrder, 0, pairs, Comparer<int>.Create((a, b) => pairD[a] < pairD[b] ? -1 : pairD[a] > pairD[b] ? 1 : pairI[a] != pairI[b] ? pairI[a].CompareTo(pairI[b]) : pairJ[a].CompareTo(pairJ[b])));
                var assigned = new int[keep];
                for (int a = 0; a < keep; a++) assigned[a] = -1;
                for (int pp = 0; pp < pairs; pp++)
                {
                    int p = pairOrder[pp];
                    int a = pairI[p], j = pairJ[p];
                    if (assigned[a] >= 0 || matchedPrev[j]) continue;
                    assigned[a] = prevSlot[j];
                    matchedPrev[j] = true;
                    usedNow[prevSlot[j]] = true;
                }
                Array.Clear(usedPrev, 0, usedPrev.Length);
                for (int j = 0; j < prevN; j++) usedPrev[prevSlot[j]] = true;
                for (int a = 0; a < keep; a++)
                {
                    if (assigned[a] < 0)
                    {
                        // A birth takes the lowest slot neither the last frame nor this one uses (a slot freed by a death rests a frame).
                        for (int s = 0; s < SpectralModel.MaxPartials; s++)
                            if (!usedPrev[s] && !usedNow[s]) { assigned[a] = s; usedNow[s] = true; break; }
                    }
                    if (assigned[a] < 0) continue;
                    int ci = order[a];
                    curSlot[np] = assigned[a]; curFreq[np] = cf[ci]; curAmp[np] = ca[ci]; curBin[np] = ck[ci];
                    np++;
                }
                // Sorted by frequency.
                for (int i = 0; i < np; i++) sortF[i] = i;
                Array.Sort(sortF, 0, np, Comparer<int>.Create((a, b) => curFreq[a] < curFreq[b] ? -1 : curFreq[a] > curFreq[b] ? 1 : a.CompareTo(b)));
                prevN = np;
                for (int i = 0; i < np; i++)
                {
                    int e = sortF[i];
                    slots.Add((byte)curSlot[e]);
                    freqQ.Add(SpectralModel.QuantiseFreq(curFreq[e]));
                    ampQ.Add(SpectralModel.QuantiseAmp(curAmp[e]));
                    prevSlot[i] = curSlot[e];
                    prevFreq[i] = curFreq[e];
                }
                start[f + 1] = start[f] + np;

                f0q[f] = SpectralModel.QuantiseFreq(Fundamental(curFreq, curAmp, np, sortF));

                // The residual: the frame's power less the partials' kernels, in bands.
                Array.Copy(mag2, resid, half + 1);
                for (int i = 0; i < np; i++)
                {
                    double kb = curBin[i], a = curAmp[i] * sumW * 0.5;
                    int k0 = Math.Max(1, (int)Math.Floor(kb) - 6), k1 = Math.Min(half, (int)Math.Floor(kb) + 7);
                    double s0 = PortableMath.Sin(Math.PI * kb);
                    for (int k = k0; k <= k1; k++)
                    {
                        double d = k - kb;
                        double w = Math.Abs(d) < 1e-9 ? 1.0 : Math.Abs(Math.Abs(d) - 1.0) < 1e-9 ? 0.5 : Math.Abs((((k & 1) == 0 ? -s0 : s0)) / (Math.PI * d * (1.0 - d * d)));
                        resid[k] -= a * a * w * w;
                    }
                }
                // The main lobes of the partials are theirs (a mismatch there is the model's, not noise): the bands' noise is read from the bins outside them.
                Array.Clear(excl, 0, excl.Length);
                for (int i = 0; i < np; i++)
                {
                    int k0 = Math.Max(0, (int)Math.Ceiling(curBin[i] - 2.5)), k1 = Math.Min(half, (int)Math.Floor(curBin[i] + 2.5));
                    for (int k = k0; k <= k1; k++) excl[k] = true;
                }
                double scale = 2.0 / (n * sumW2);
                for (int b = 0; b < SpectralModel.Bands; b++)
                {
                    double sum = 0;
                    int total = bandStart[b + 1] - bandStart[b], used = 0;
                    for (int k = bandStart[b]; k < bandStart[b + 1]; k++)
                        if (!excl[k]) { sum += Math.Max(0.0, resid[k]); used++; }
                    // The mean over the bins outside the lobes, for the whole band (when most of it is lobes, the rest is taken as empty).
                    double v = total > 0 ? sum * total / Math.Max(used, Math.Max(1, total / 4)) : 0.0;
                    noiseQ[b * frames + f] = SpectralModel.QuantiseLevel(Math.Sqrt(v * scale));
                }
            }
            progress?.Invoke(1f);

            var model = new SpectralModel
            {
                Name = name ?? "", Rate = rate, Length = len, Size = n, Hop = hop, Frames = frames,
                Start = start, Slot = slots.ToArray(), FreqQ = freqQ.ToArray(), AmpQ = ampQ.ToArray(), F0Q = f0q, NoiseQ = noiseQ,
            };
            model.Finish();
            return model;
        }

        /// <summary>The Hann window's transform at an offset of <paramref name="d"/> bins from its peak, relative to the peak (sin πd / (πd (1 − d²))).</summary>
        static double Kernel(double d)
        {
            d = Math.Abs(d);
            if (d < 1e-9) return 1.0;
            if (Math.Abs(d - 1.0) < 1e-9) return 0.5;
            return PortableMath.Sin(Math.PI * d) / (Math.PI * d * (1.0 - d * d));
        }

        /// <summary>The fifth lowest power among the bins 4 … 12 either side of <paramref name="k"/> (those inside 1 … half − 1); −1 when fewer than five.</summary>
        static double Floor(double[] mag2, int k, int half, double[] near)
        {
            int n = 0;
            for (int d = 4; d <= 12; d++)
            {
                if (k - d >= 1) near[n++] = mag2[k - d];
                if (k + d <= half - 1) near[n++] = mag2[k + d];
            }
            if (n < 5) return -1.0;
            for (int i = 1; i < n; i++)
            {
                double v = near[i];
                int j = i - 1;
                while (j >= 0 && near[j] > v) { near[j + 1] = near[j]; j--; }
                near[j + 1] = v;
            }
            return near[4];
        }

        /// <summary>
        /// A frame's fundamental: the candidate (a strong partial over a small whole number) whose multiples explain the most of the
        /// partials' power within 3 %, refined by a least-squares fit over those it explains; 0 when none explains 60 %.
        /// </summary>
        static double Fundamental(double[] freq, double[] amp, int n, int[] order)
        {
            if (n < 2) return 0.0;
            double total = 0;
            for (int i = 0; i < n; i++) total += amp[i] * amp[i];
            if (!(total > 0)) return 0.0;
            // The strongest eight partials as the base, over 1 … 6.
            var strong = new int[Math.Min(8, n)];
            for (int s = 0; s < strong.Length; s++)
            {
                int pick = -1;
                for (int i = 0; i < n; i++)
                {
                    bool taken = false;
                    for (int t = 0; t < s; t++) if (strong[t] == i) taken = true;
                    if (!taken && (pick < 0 || amp[i] > amp[pick])) pick = i;
                }
                strong[s] = pick;
            }
            var candF = new double[strong.Length * 6];
            var candS = new double[strong.Length * 6];
            int nc = 0;
            double bestScore = 0;
            foreach (int e in strong)
                for (int h = 1; h <= 6; h++)
                {
                    double f0 = freq[e] / h;
                    if (f0 < 30.0) continue;
                    double score = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double r = Math.Round(freq[i] / f0);
                        if (r < 1) continue;
                        double dev = Math.Abs(freq[i] / (r * f0) - 1.0);
                        if (dev < 0.03) score += amp[i] * amp[i] * (1.0 - dev / 0.03);
                    }
                    score /= total;
                    candF[nc] = f0; candS[nc] = score; nc++;
                    if (score > bestScore) bestScore = score;
                }
            // Among the candidates nearly as good as the best, the highest wins: the octave below explains the same partials.
            double best = 0;
            for (int c = 0; c < nc; c++)
                if (candS[c] >= bestScore - 0.02 && candF[c] > best) best = candF[c];
            if (bestScore < 0.6) return 0.0;
            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                double r = Math.Round(freq[i] / best);
                if (r < 1 || Math.Abs(freq[i] / (r * best) - 1.0) >= 0.03) continue;
                double w = amp[i] * amp[i];
                num += w * r * freq[i];
                den += w * r * r;
            }
            return den > 0 ? num / den : best;
        }
    }
}
