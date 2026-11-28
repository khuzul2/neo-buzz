#nullable disable
// Neo Shift's Smooth pitch shifter (docs/specs/neo-shift.md): a phase vocoder with phase locking on the shared Stft (frames of 2048, a hop of 512,
// square-root Hann windows; latency 2047 samples).
//  - Each frame's spectrum goes through one pass: the peaks of the magnitude (local maxima) are found, each with the region up to the quietest bin
//    between it and the next peak. Each peak's frequency is read from the phase advance between frames (Flanagan and Golden, 1966; Portnoff,
//    1976), the peak's main lobe is laid down again at the new frequency (the sine window's lobe cos(pi d)/(1 - 4 d^2)) with the phase it had a
//    frame ago plus the advance of the new frequency, and the rest of its region is rotated by the same angle and moved with it (the identity phase
//    locking of Laroche and Dolson, 1999). A chord keeps its partials' phase relations, so it does not smear.
//  - Each channel has its own ratio (the stereo detune); the ratio is smoothed once per frame (30 ms), so a changing pitch glides.
//  - A ratio of exactly 1 passes the frame as it is.
// Everything is allocated up front; a frame allocates nothing. Portable maths only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Shift
{
    internal sealed class PhaseVocoderShifter : ISpectralProcessor
    {
        public const int Size = 2048;
        const double HalfPi = Math.PI / 2, TwoPi = Math.PI * 2;
        const double FreqSmooth = 0.2;
        const int LobeHalf = 4;

        public readonly int N = Size, Nb = Size / 2 + 1;
        readonly double rate;
        readonly float smoothA;

        /// <summary>The ratio each channel is shifted by, set by the engine for the frames to come.</summary>
        public double TargetSemitonesL, TargetSemitonesR;

        sealed class Chan
        {
            public readonly float[] PrevPh, PrevYRe, PrevYIm;
            public float[] FreqNow, FreqNext;
            public double Semi;
            public bool Stale = true;

            public Chan(int nb)
            {
                PrevPh = new float[nb]; PrevYRe = new float[nb]; PrevYIm = new float[nb]; FreqNow = new float[nb]; FreqNext = new float[nb];
            }

            public void Reset()
            {
                Array.Clear(PrevPh); Array.Clear(PrevYRe); Array.Clear(PrevYIm); Array.Clear(FreqNow); Array.Clear(FreqNext);
                Stale = true;
            }
        }

        readonly Chan[] ch = { null, null };
        readonly float[] lRe, lIm, rRe, rIm;
        readonly float[] mag, ph, yRe, yIm;
        readonly int[] pk, lo, hi;
        readonly bool[] lobeMark;
        bool first = true;

        public PhaseVocoderShifter(double sampleRate)
        {
            rate = sampleRate;
            ch[0] = new Chan(Nb);
            ch[1] = new Chan(Nb);
            lRe = new float[Nb]; lIm = new float[Nb]; rRe = new float[Nb]; rIm = new float[Nb];
            mag = new float[Nb]; ph = new float[Nb]; yRe = new float[Nb]; yIm = new float[Nb];
            pk = new int[Nb]; lo = new int[Nb]; hi = new int[Nb]; lobeMark = new bool[Nb];
            smoothA = (float)(1.0 - PortableMath.Exp(-(Size / 4) / (0.03 * sampleRate)));
        }

        public int Latency => N - 1;

        public void Reset()
        {
            ch[0].Reset();
            ch[1].Reset();
            first = true;
        }

        public void Process(float[] re, float[] im, int n)
        {
            Stft.Unpack(re, im, n, lRe, lIm, rRe, rIm);
            if (first)
            {
                ch[0].Semi = TargetSemitonesL;
                ch[1].Semi = TargetSemitonesR;
                first = false;
            }
            else
            {
                Glide(ch[0], TargetSemitonesL);
                Glide(ch[1], TargetSemitonesR);
            }
            Run(ch[0], lRe, lIm);
            Run(ch[1], rRe, rIm);
            lIm[0] = 0; lIm[Nb - 1] = 0; rIm[0] = 0; rIm[Nb - 1] = 0;
            Stft.Pack(lRe, lIm, rRe, rIm, n, re, im);
        }

        void Glide(Chan c, double target)
        {
            double d = target - c.Semi;
            c.Semi = Math.Abs(d) < 1e-7 ? target : c.Semi + d * smoothA;
        }

        void Run(Chan c, float[] re, float[] im)
        {
            if (c.Semi == 0.0)
            {
                // nothing to shift: the frame goes through as it is (the next frame starts the phase chains afresh)
                c.Stale = true;
                return;
            }
            Pitch(c, re, im);
            c.Stale = false;
        }

        static double Wrap(double x)
        {
            x -= TwoPi * Math.Floor(x / TwoPi + 0.5);
            return x;
        }

        void Polar(float[] re, float[] im)
        {
            for (int k = 0; k < Nb; k++)
            {
                float a = re[k], b = im[k];
                mag[k] = MathF.Sqrt(a * a + b * b);
                ph[k] = mag[k] > 1e-20f ? (float)PortableMath.Atan2(b, a) : 0f;
            }
        }

        /// <summary>The peaks of <see cref="mag"/> (local maxima 80 dB or less below the loudest), and each one's region: up to the quietest bin between it and the next.</summary>
        int FindPeaks()
        {
            float max = 0;
            for (int k = 0; k < Nb; k++) if (mag[k] > max) max = mag[k];
            if (max < 1e-9f) return 0;
            float thr = max * 1e-4f;
            int count = 0;
            for (int k = 1; k < Nb - 1; k++)
            {
                float m = mag[k];
                if (m <= thr || m < mag[k - 1] || m <= mag[k + 1]) continue;
                if (k >= 2 && m < mag[k - 2]) continue;
                if (k + 2 < Nb && m < mag[k + 2]) continue;
                pk[count++] = k;
            }
            if (count == 0) return 0;
            lo[0] = 0;
            for (int i = 0; i + 1 < count; i++)
            {
                int a = pk[i], b = pk[i + 1], at = a + 1;
                float least = mag[at];
                for (int k = a + 1; k < b; k++) if (mag[k] < least) { least = mag[k]; at = k; }
                hi[i] = at;
                lo[i + 1] = at + 1;
            }
            hi[count - 1] = Nb - 1;
            return count;
        }

        /// <summary>The sine window's main lobe at a distance <paramref name="d"/> bins: cos(pi d)/(1 - 4 d^2).</summary>
        static double Lobe(double d)
        {
            double den = 1 - 4 * d * d;
            if (Math.Abs(den) < 1e-6) return Math.PI / 4;
            return PortableMath.Cos(Math.PI * d) / den;
        }

        void Pitch(Chan c, float[] re, float[] im)
        {
            double ratio = PortableMath.Exp2(c.Semi / 12.0);
            Polar(re, im);
            int count = FindPeaks();
            Array.Clear(yRe);
            Array.Clear(yIm);
            // A local maximum whose frequency (from the phase advance) is more than 0.8 bin from its bin is a sidelobe of a sine somewhere else:
            // its region joins the neighbour's instead of moving as a peak of its own.
            int valid = 0;
            int pendingLo = -1;
            for (int i = 0; i < count; i++)
            {
                int kq = pk[i];
                double dq = c.Stale ? 0.0 : Wrap(ph[kq] - c.PrevPh[kq] - (kq & 3) * HalfPi) * (2.0 / Math.PI);
                if (Math.Abs(dq) <= 0.8)
                {
                    pk[valid] = kq;
                    lo[valid] = pendingLo >= 0 ? pendingLo : lo[i];
                    hi[valid] = hi[i];
                    pendingLo = -1;
                    valid++;
                }
                else if (valid > 0) hi[valid - 1] = hi[i];
                else if (pendingLo < 0) pendingLo = lo[i];
            }
            count = valid;
            // The main lobe of every peak is laid down whole (four bins either side) whatever the regions say; the rest is copied.
            Array.Clear(lobeMark);
            for (int i = 0; i < count; i++)
                for (int k = Math.Max(0, pk[i] - LobeHalf); k <= Math.Min(Nb - 1, pk[i] + LobeHalf); k++) lobeMark[k] = true;
            for (int i = 0; i < count; i++)
            {
                int kp = pk[i];
                double dphi = c.Stale ? 0.0 : Wrap(ph[kp] - c.PrevPh[kp] - (kp & 3) * HalfPi);
                double delta = dphi * (2.0 / Math.PI);                  // the peak's true offset from bin kp, in bins
                // A partial's frequency is steady while a neighbour's beat (aliased by the hop) jitters the estimate: follow it with a one pole.
                double fRaw = kp + delta, fSm = fRaw;
                if (!c.Stale)
                {
                    double best = 0.6;
                    for (int q = -1; q <= 1; q++)
                    {
                        int kq = kp + q;
                        if (kq < 0 || kq >= Nb || c.FreqNow[kq] <= 0) continue;
                        double dist = Math.Abs(c.FreqNow[kq] - fRaw);
                        if (dist < best) { best = dist; fSm = c.FreqNow[kq] + FreqSmooth * (fRaw - c.FreqNow[kq]); }
                    }
                }
                c.FreqNext[kp] = (float)fSm;
                delta = fSm - kp;
                double dEst = delta;
                double g = ratio * (kp + delta);                        // where it goes, in bins
                int jp = (int)Math.Floor(g + 0.5);
                if (jp < -6 || jp > Nb + 6) continue;
                double dp = g - jp;
                float s = (float)(Lobe(dp) / Lobe(dEst));
                // The phase at the new peak bin: what it was a frame ago plus the advance of the new frequency.
                double psi;
                int jc = Math.Clamp(jp, 0, Nb - 1);
                double prevMag = Math.Sqrt((double)c.PrevYRe[jc] * c.PrevYRe[jc] + (double)c.PrevYIm[jc] * c.PrevYIm[jc]);
                if (!c.Stale && prevMag >= 0.1 * mag[kp] * s)
                    psi = Wrap(PortableMath.Atan2(c.PrevYIm[jc], c.PrevYRe[jc]) + Wrap(g * HalfPi));
                else psi = ph[kp];
                double rot = psi - ph[kp];
                float cr = (float)PortableMath.Cos(rot), ci = (float)PortableMath.Sin(rot);
                float cp = (float)PortableMath.Cos(psi), sp = (float)PortableMath.Sin(psi);
                float lobeDen = (float)Lobe(dp);
                float amp0 = mag[kp] * s;
                for (int m = -LobeHalf; m <= LobeHalf; m++)
                {
                    int j = jp + m;
                    if (j < 0 || j >= Nb) continue;
                    float q = (float)(Lobe(dp - m) / lobeDen);
                    if ((m & 1) != 0) q = -q;
                    float amp = amp0 * q;
                    yRe[j] += amp * cp;
                    yIm[j] += amp * sp;
                }
                for (int k = lo[i]; k <= hi[i]; k++)
                {
                    if (lobeMark[k]) continue;
                    int j = jp + (k - kp);
                    if (j < 0 || j >= Nb) continue;
                    float xr = re[k], xi = im[k];
                    yRe[j] += (xr * cr - xi * ci) * s;
                    yIm[j] += (xr * ci + xi * cr) * s;
                }
            }
            Array.Copy(ph, c.PrevPh, Nb);
            Array.Clear(c.FreqNow);
            (c.FreqNow, c.FreqNext) = (c.FreqNext, c.FreqNow);
            yIm[0] = 0;
            yIm[Nb - 1] = 0;
            for (int k = 0; k < Nb; k++)
            {
                re[k] = yRe[k];
                im[k] = yIm[k];
            }
            Array.Copy(yRe, c.PrevYRe, Nb);
            Array.Copy(yIm, c.PrevYIm, Nb);
        }
    }
}
