#nullable disable
// Neo Tune's two engines and the output (docs/specs/neo-tune.md section 11.2).
//  SPLICE: the output at time t is the input read at a position r that advances by the pitch ratio rho per output sample (rho = 2^(shift/12), the
//  shift read from the contour at r), through the 16-tap sinc. The offset e = r - t drifts by rho - 1 per sample; when it leaves
//  +-(half a local period) the reader jumps back or forward by exactly one period (the spacing of the analysis marks around r), so it lands at the
//  same phase of the cycle: a waveform repeated or dropped whole, joined by a short raised-cosine crossfade (amplitude-complementary, since the two
//  segments are the same waveform a period apart; the sum stays at 1). No grain overlap: no comb filter, no roughness. With rho = 1 and e = 0
//  the output is the input, sample for sample. In unvoiced stretches the ratio is 1 (the contour ramps it down) and the reader is brought back
//  to e = 0 with one crossfade, so the voice always comes back aligned. A shift of exactly 0 for a while eases e to 0 by 0.1 % in speed.
//  PSOLA: grains of two periods (at most the range's cap) around the analysis mark nearest the grain centre, a Hann window, laid at the TRUE
//  mark spacing divided by the ratio (so the cycle-to-cycle variation of the singer is kept, and at a ratio of 1 the grains are the input), added with
//  their weights. Formant control reads the grain at speed phi. Where no grain covers a sample the input passes.

using System;
using System.Collections.Generic;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal sealed partial class TuneEngine
    {
        // ---- PSOLA synthesis
        float[] num, den;
        int olaMask;
        bool synthActive;
        int synthRun;
        double sNext;
        readonly float[] hann;
        double hgMax;
        double pGate = 1, pStep = 0.001;

        // ---- splice
        const double EaseSpeed = 0.002;
        double rA, rB, rho = 1, spJ;
        bool fading, spInit;
        int fadeN, fadeLen, spCount, zeroRun;
        long spPtr;
        double[] scoreJ = new double[1];
        bool evBypass, evHold;

        void ResetSplice()
        {
            rA = rB = outPos; rho = 1; spJ = sr / 100; fading = false; fadeN = fadeLen = 0; spCount = 0; zeroRun = 0; spPtr = 0; spInit = true;
            pGate = 1; pStep = 1.0 / (0.005 * sr);
        }

        float Hann(double u)
        {
            double x = u * HannSize;
            int i = (int)x;
            if (i >= HannSize) return 0f;
            double f = x - i;
            return (float)(hann[i] + f * (hann[i + 1] - hann[i]));
        }

        // ------------------------------------------------------------------ splice

        /// <summary>The index of the newest mark at or before r and the spacing to use for a jump there (the gap to the previous mark; the last one's when r is past the newest).</summary>
        bool SpacingAt(double r, out double j)
        {
            j = 0;
            long floor = Math.Max(0, mCount - MarkSize + 4);
            for (long i = mCount - 1; i > floor; i--)
            {
                double t = mT[(int)(i & MarkMask)];
                if (t > r) continue;
                int run = mRun[(int)(i & MarkMask)];
                if (mRun[(int)((i - 1) & MarkMask)] != run) return false;
                j = t - mT[(int)((i - 1) & MarkMask)];
                // the reader must be inside this run (not long after its last mark)
                if (i == mCount - 1 && r - t > 2.5 * j) return false;
                if (i < mCount - 1 && mRun[(int)((i + 1) & MarkMask)] != run && r - t > 1.5 * j) return false;
                return j > 4;
            }
            return false;
        }

        void RefreshRatio(long next)
        {
            bool voiced = ShiftAt(rA, ref rPtr, out double shift);
            if (shift > 12) shift = 12; else if (shift < -12) shift = -12;
            double e = rA - next;
            if (evOn && (spCount & 255) == 1) Log(next, TuneEventKind.Trace, shift, e, fading ? 1 : 0);
            rho = shift == 0 ? 1.0 : PortableMath.Exp2(shift / 12.0);
            zeroRun = shift == 0 ? zeroRun + 8 : 0;
            if (!fading && Math.Abs(e) > 1e-3 && next > 0 && shift == 0)
            {
                if (!voiced)
                {
                    // Back to the nominal position in an unvoiced stretch (noise does not mind): one short crossfade, and the output is the input again.
                    rB = next; fading = true; fadeN = 0;
                    fadeLen = Math.Max(16, (int)(0.002 * sr));
                    if (evOn) Log((long)rA, TuneEventKind.SpliceReturn, e, fadeLen, SegmentMismatch(rA, rB, fadeLen), 0);
                }
                else if (zeroRun > (int)(0.03 * sr))
                {
                    // Inside a voiced note the offset is NOT joined by a crossfade (NT2 did, over about 1.5 periods, between waveforms a fraction of a period apart: the
                    // cycles cancelled in part and the join was heard as a click). The correction has been nil for 30 ms: the reader eases to the input's position at
                    // 0.2 % in speed (3.5 ct, below what a held note shows). The output is then the input again (the aligned null, a dry part mixed in).
                    rho = e > 0 ? 1.0 - EaseSpeed : 1.0 + EaseSpeed;
                    if (Math.Abs(e) < 2 * EaseSpeed * 8) { rA = next; rho = 1.0; if (evOn) Log(next, TuneEventKind.SpliceReturn, e, 0, 0, 1); }
                }
            }
        }

        float SpliceSample(long t)
        {
            long next = t + 1;
            if ((spCount++ & 7) == 0) RefreshRatio(t);
            float y = Interp(rA);
            if (fading)
            {
                float b = Interp(rB);
                float wa = Hann((double)fadeN / fadeLen);
                y = wa * y + (1f - wa) * b;
                rB += rho;
                fadeN++;
            }
            rA += rho;
            if (fading && fadeN >= fadeLen) { rA = rB; fading = false; }
            // the splice decision for the next sample
            double e = rA - next;
            if (!fading && (Math.Abs(e) > 0.4 * spJ || spJ < 8))
            {
                if (SpacingAt(rA, out double j)) spJ = j;
                else j = 0;
                if (j > 0)
                {
                    double lim = 0.55 * j;
                    if (e > lim || e < -lim)
                    {
                        // The splice is made where the next period matches the one a period away (correlation 0.85 or better), which a leap or a note change does not give for a few
                        // periods: the reader may then run on to 0.9 of a period from the input (checked every 16 samples) rather than splice a waveform that is changing.
                        bool late = Math.Abs(e) >= 0.9 * j;
                        if (!late && (spCount & 15) != 0) return y;
                        double corr = 1;
                        double jump = AlignedJump(rA, e > 0 ? -j : j, out corr);
                        if (!late && corr < 0.85) return y;
                        rB = rA + jump;
                        fading = true; fadeN = 0;
                        fadeLen = Math.Clamp((int)(0.5 * j), 24, Math.Max(24, (int)(0.006 * sr)));
                        if (evOn) Log(next, TuneEventKind.SpliceJump, e > 0 ? -j : j, e, SegmentMismatch(rA, rB, fadeLen), fadeLen);
                    }
                }
            }
            return y;
        }

        /// <summary>
        /// The jump (samples, negative = back) that joins the reader at <paramref name="r"/> to the waveform one period away: the analysis marks give the spacing, but a mark is
        /// placed on a period's correlation peak and the period itself changes from one cycle to the next (vibrato, a glide, a note change), so the spacing is only close. The
        /// normalised correlation of the next period of the input against the same period displaced by the jump is searched within +-12 % of it (whole samples, then a parabola),
        /// and the jump that makes the two segments equal is used: the join is then a splice of the same phase of the cycle, as the method needs.
        /// </summary>
        double AlignedJump(double r, double nominal, out double corr)
        {
            int j = (int)Math.Round(Math.Abs(nominal));
            int span = Math.Max(2, (int)(0.12 * j));
            int w = Math.Max(24, Math.Min(j, (int)(0.012 * sr)));
            long a0 = (long)Math.Floor(r) - w / 4;
            double sign = nominal < 0 ? -1 : 1;
            int n = 2 * span + 1;
            double best = double.MinValue; int bi = span;
            for (int di = 0; di < n; di++)
            {
                long off = (long)(sign * (j + di - span));
                double c = 0, ea = 0, eb = 0;
                for (int k = 0; k < w; k++)
                {
                    float a = X(a0 + k), b = X(a0 + k + off);
                    c += (double)a * b; ea += (double)a * a; eb += (double)b * b;
                }
                scoreJ[di] = c / Math.Sqrt(ea * eb + 1e-18);
                if (scoreJ[di] > best) { best = scoreJ[di]; bi = di; }
            }
            double frac = 0;
            if (bi > 0 && bi < n - 1)
            {
                double a = scoreJ[bi - 1], b = scoreJ[bi], c2 = scoreJ[bi + 1], d = a - 2 * b + c2;
                if (d < -1e-12) frac = Math.Clamp(0.5 * (a - c2) / d, -0.5, 0.5);
            }
            corr = best;
            // a weak correlation (a transient, noise): keep the marks' spacing
            if (best < 0.5) return nominal;
            return sign * (j + bi - span + frac);
        }

        // ------------------------------------------------------------------ PSOLA

        /// <summary>The nearest mark of run <paramref name="run"/> to s: its time and the spacing to its successor (the predecessor at the newest), or -1.</summary>
        double NearestMarkSpacing(double s, int run, out double spacing)
        {
            spacing = 0;
            double best = -1, bd = double.MaxValue;
            long bi = -1;
            long floor = Math.Max(0, mCount - MarkSize + 4);
            for (long i = mCount - 1; i >= floor; i--)
            {
                int ri = mRun[(int)(i & MarkMask)];
                if (ri > run) continue;
                if (ri < run) break;
                double t = mT[(int)(i & MarkMask)];
                double d = Math.Abs(t - s);
                if (d < bd) { bd = d; best = t; bi = i; }
                else if (t < s) break;
            }
            if (bi < 0) return -1;
            if (bi + 1 < mCount && mRun[(int)((bi + 1) & MarkMask)] == run) spacing = mT[(int)((bi + 1) & MarkMask)] - best;
            else if (bi - 1 >= floor && mRun[(int)((bi - 1) & MarkMask)] == run) spacing = best - mT[(int)((bi - 1) & MarkMask)];
            return best;
        }

        void PlaceGrains(long tEnd)
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                if (!synthActive)
                {
                    // The first mark of the next run.
                    long floor = Math.Max(0, mCount - MarkSize + 4);
                    long found = -1;
                    for (long i = mCount - 1; i >= floor; i--)
                    {
                        int ri = mRun[(int)(i & MarkMask)];
                        if (ri <= synthRun) break;
                        found = i;
                    }
                    if (found < 0) return;
                    synthRun = mRun[(int)(found & MarkMask)];
                    sNext = mT[(int)(found & MarkMask)];
                    sPtr = 0;
                    synthActive = true;
                }
                if (!ContourAt(sNext, ref sPtr, out double f, out double shiftSemis))
                {
                    // Unvoiced where the next grain would go: if the contour is that far, the run is over.
                    if (sPtr + 1 < cCount || sNext < inCount - latency - 2 * hgMax) { synthActive = false; continue; }
                    return;
                }
                double ratio = PortableMath.Exp2(shiftSemis / 12.0);
                double a = NearestMarkSpacing(sNext, synthRun, out double pin);
                if (a < 0) return;
                if (pin < 4) pin = sr / f;
                // the output period is the actual mark spacing over the ratio (the pitch of the note, unless it leaves the range)
                double pout = pin / ratio;
                double minP = sr / (sr * 0.25), maxP = sr / 20.0;
                if (pout < minP) { pout = minP; ratio = pin / pout; }
                else if (pout > maxP) { pout = maxP; ratio = pin / pout; }
                double phi = preserve ? fshift : ratio * fshift;
                phi = Math.Clamp(phi, 0.25, 4.0);
                double hs = Math.Min(pin, capSamples);
                double hg = Math.Min(Math.Max(hs / phi, 0.55 * pout), hgMax);
                if (sNext - hg >= tEnd) return;
                long kLo = (long)Math.Ceiling(sNext - hg), kHi = (long)Math.Floor(sNext + hg);
                if (kLo < outPos) kLo = outPos;
                double invHg = 1.0 / hg;
                for (long k = kLo; k <= kHi; k++)
                {
                    double u = k - sNext;
                    float w = Hann(Math.Abs(u) * invHg);
                    if (w <= 0f) continue;
                    float v = Interp(a + phi * u);
                    int oi = (int)(k & olaMask);
                    num[oi] += w * v;
                    den[oi] += w;
                }
                sNext += pout;
            }
        }

        // ------------------------------------------------------------------ output

        void Emit(float[] left, float[] right, int offset, int c, long tEnd)
        {
            float wet = (float)mix;
            for (int i = 0; i < c; i++)
            {
                long t = outPos + i;
                int di = (int)(t & ringMask);
                float dl = t >= 0 ? dryL[di] : 0f, dr = t >= 0 ? dryR[di] : 0f;
                float v;
                float shaped = 1f;
                if (useSplice) v = SpliceSample(t);
                else
                {
                    int oi = (int)(t & olaMask);
                    float nu = num[oi], de = den[oi];
                    num[oi] = 0f; den[oi] = 0f;
                    v = nu / Math.Max(de, 1e-3f);
                    shaped = Math.Min(1f, de * 6f);
                    // a correction of exactly nothing is the input, aligned (the grains are close to it already: this hides the residue)
                    bool bypass = ShiftAt(t, ref gPtr, out double sh) && sh == 0;
                    if (evOn && bypass != evBypass) { evBypass = bypass; Log(t, bypass ? TuneEventKind.PsolaBypassOn : TuneEventKind.PsolaBypassOff); }
                    double pt = bypass ? 0.0 : 1.0;
                    if (pGate < pt) pGate = Math.Min(pt, pGate + pStep); else if (pGate > pt) pGate = Math.Max(pt, pGate - pStep);
                    shaped *= (float)pGate;
                }
                bool dryHold = !useSplice && transients && t >= holdFrom && t < holdUntil;    // the splice engine keeps a transient as it is (no grains to smear it)
                if (evOn && dryHold != evHold) { evHold = dryHold; Log(t, dryHold ? TuneEventKind.TransientOn : TuneEventKind.TransientOff); }
                double tg = dryHold ? 0.0 : 1.0;
                if (gate < tg) gate = Math.Min(tg, gate + gateStep);
                else if (gate > tg) gate = Math.Max(tg, gate - gateStep);
                float w = (float)(wet * gate * shaped);
                if (w > 0f)
                {
                    left[offset + i] = dl + w * (v - dl);
                    right[offset + i] = dr + w * (v - dr);
                }
                else
                {
                    left[offset + i] = dl;
                    right[offset + i] = dr;
                }
            }
            outPos += c;
        }
    }
}
