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
        double rA, rB, rho = 1, spJ;
        bool fading, spInit;
        int fadeN, fadeLen, spCount, zeroRun;
        long spPtr;
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
            if (!fading && Math.Abs(e) > 1e-3 && next > 0 && shift == 0 && (!voiced || zeroRun > (int)(0.03 * sr)))
            {
                // Back to the nominal position: in an unvoiced stretch (noise does not mind) or after the correction has been nil for 30 ms: one crossfade
                // (a voiced note is joined over about a period and a half, the offset being less than half a period), and the output is the input again.
                rB = next; fading = true; fadeN = 0;
                fadeLen = voiced ? Math.Clamp((int)(1.5 * spJ), 32, (int)(0.012 * sr)) : Math.Max(16, (int)(0.002 * sr));
                if (evOn) Log((long)rA, TuneEventKind.SpliceReturn, e, fadeLen, SegmentMismatch(rA, rB, fadeLen), voiced ? 1 : 0);
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
                        rB = rA + (e > 0 ? -j : j);
                        fading = true; fadeN = 0;
                        fadeLen = Math.Clamp((int)(0.25 * j), 16, Math.Max(16, (int)(0.003 * sr)));
                        if (evOn) Log(next, TuneEventKind.SpliceJump, e > 0 ? -j : j, e, SegmentMismatch(rA, rB, fadeLen), fadeLen);
                    }
                }
            }
            return y;
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
