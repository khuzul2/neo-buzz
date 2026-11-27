#nullable disable
// Neo Prism's spectral processor (docs/specs/neo-prism.md): what runs on every frame of the shared Stft (a frame of N samples, a hop of N/4,
// √Hann windows). Each channel's spectrum goes through five sections in this order, each blended in by a weight that ramps over at least
// 30 ms (so switching a section never clicks):
//   1. Spectral pitch   peaks and their regions of influence move by the pitch ratio, phase-locked (Laroche and Dolson, 1999): the
//                       peak's frequency is read from the phase advance between frames, its main lobe is laid down again at the new
//                       frequency (the sine window's lobe cos(πd)/(1 − 4d²)), the rest of its region is rotated and moved with it.
//   2. Scramble         bins swap inside bands by a seeded shuffle; the moved bin keeps its own frequency offset, so its phase runs on.
//                       A peak guard follows: the frame's time-domain peak out is held to the peak in (a gain that falls fast and rises slowly).
//   3. Freeze           the spectrum is held: magnitudes kept, every peak's region turns at the peak's own frequency.
//   4. Blur             magnitudes smoothed over time (one pole per bin) and across frequency (a box).
//   5. Spectral delay   bins are delayed by whole frames, the time tilting from low to high frequencies, with feedback and damping.
// Everything is allocated up front (the delay's ring is the big part); a frame allocates nothing. Portable maths only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Prism
{
    /// <summary>What the engine tells the processor, once per block (read by every frame of the block).</summary>
    internal sealed class PrismSettings
    {
        public bool PitchOn, FormantKeep, ScrOn, BlurOn, DelayOn;
        public double Semitones;
        public float ScrAmount;
        public int ScrWidth, ScrSeed;
        /// <summary>Free: the shuffles per second; otherwise 0 and <see cref="ScrBeats"/> is the length of one shuffle in beats.</summary>
        public double ScrHz, ScrBeats;
        /// <summary>The freeze is wanted now, and the serial counts the catches (a new catch while held takes a new snapshot).</summary>
        public bool FreezeReq;
        public int FreezeSerial;
        public float Hold;
        public double BlurSeconds;
        public int Smear;
        public double DelLowS, DelHighS;
        public float Feedback, Damping;
        /// <summary>The song's position in beats at the start of the block, and the samples a beat takes.</summary>
        public double Beat, SamplesPerBeat = 24000;
    }

    /// <summary>One snapshot for the editor: input and output levels per band in dB, the sections that are on.</summary>
    internal sealed class PrismView
    {
        public const int Bands = 160;
        public readonly float[] In = new float[Bands], Out = new float[Bands];
        public int Lights;
        public int Size;

        public PrismView()
        {
            Array.Fill(In, -120f);      // nothing has played yet
            Array.Fill(Out, -120f);
        }

        public void CopyTo(PrismView o)
        {
            Array.Copy(In, o.In, Bands);
            Array.Copy(Out, o.Out, Bands);
            o.Lights = Lights;
            o.Size = Size;
        }
    }

    internal sealed class PrismProcessor : ISpectralProcessor
    {
        public const double HalfPi = Math.PI / 2, TwoPi = Math.PI * 2;
        const double FreqSmooth = 0.2;
        const int LobeHalf = 4;
        public const int Sections = 5;

        public readonly int N, Hop, Nb;
        readonly double rate;
        readonly int maxLag;
        public PrismSettings S;
        public TripleBuffer<PrismView> View;

        sealed class Chan
        {
            // pitch
            public readonly float[] PrevPh, PrevYRe, PrevYIm;
            /// <summary>Each peak's frequency in bins from the frames before, by its bin (0: no peak there): a partial's frequency is steady, so it is smoothed.</summary>
            public float[] FreqNow, FreqNext;
            // scramble
            public readonly float[] ScrPrevPh, ScrOutPh;
            /// <summary>The peak guard's smoothed gain (1: the shuffle's frame comes out as it is).</summary>
            public float ScrGain = 1f;
            // freeze
            public readonly float[] PrevZRe, PrevZIm, FrozenM;
            public readonly double[] FrozenPh, FrozenAdv;
            // blur
            public readonly float[] Mb, BPrevPh, BAdv, BSyn;
            // delay
            public readonly float[] DRe, DIm;
            public bool PitchStale = true, ScrStale = true, BlurStale = true;

            public Chan(int nb, int ring)
            {
                PrevPh = new float[nb]; PrevYRe = new float[nb]; PrevYIm = new float[nb]; FreqNow = new float[nb]; FreqNext = new float[nb];
                ScrPrevPh = new float[nb]; ScrOutPh = new float[nb];
                PrevZRe = new float[nb]; PrevZIm = new float[nb]; FrozenM = new float[nb];
                FrozenPh = new double[nb]; FrozenAdv = new double[nb];
                Mb = new float[nb]; BPrevPh = new float[nb]; BAdv = new float[nb]; BSyn = new float[nb];
                DRe = new float[ring * nb]; DIm = new float[ring * nb];
            }

            public void Reset()
            {
                Array.Clear(PrevPh); Array.Clear(PrevYRe); Array.Clear(PrevYIm); Array.Clear(FreqNow); Array.Clear(FreqNext);
                Array.Clear(ScrPrevPh); Array.Clear(ScrOutPh);
                Array.Clear(PrevZRe); Array.Clear(PrevZIm); Array.Clear(FrozenM);
                Array.Clear(FrozenPh); Array.Clear(FrozenAdv);
                Array.Clear(Mb); Array.Clear(BPrevPh); Array.Clear(BAdv); Array.Clear(BSyn);
                ScrGain = 1f;
                PitchStale = ScrStale = BlurStale = true;
            }
        }

        readonly Chan[] ch = new Chan[2];
        readonly float[] lRe, lIm, rRe, rIm;
        // scratch shared by the channels (they run one after the other)
        readonly float[] mag, ph, yRe, yIm, tRe, tIm, env, prefix;
        readonly int[] pk, lo, hi;
        readonly bool[] lobeMark;
        // delay
        readonly int[] lag;
        readonly float[] fbGain, pos;
        int ringSize, slot, valid;
        // scramble
        readonly int[] perm, bandOf, selScratch, valScratch;
        readonly ComplexFft guardFft;
        readonly float[] gRe, gIm;
        long scrEpoch = long.MinValue;
        double scrPhase;
        float permAmount = -1;
        int permWidth = -1, permSeed = -1;
        // freeze
        bool frozen;
        int lastSerial;
        // weights, one per section, and the smoothed settings
        readonly float[] weight = new float[Sections];
        readonly float[] target = new float[Sections];
        double semiS;
        float holdS, fbS, dampS;
        double lowS, highS;
        double blurS;
        readonly float rampStep;
        readonly float smoothA;
        bool first = true;
        long frameCount;
        readonly float[] bandIn = new float[PrismView.Bands], bandOut = new float[PrismView.Bands];

        public PrismProcessor(int size, double sampleRate)
        {
            N = size;
            Hop = size / 4;
            Nb = size / 2 + 1;
            rate = sampleRate;
            maxLag = (int)Math.Ceiling(2.0 * sampleRate / Hop) + 2;
            ringSize = maxLag + 1;
            ch[0] = new Chan(Nb, ringSize);
            ch[1] = new Chan(Nb, ringSize);
            lRe = new float[Nb]; lIm = new float[Nb]; rRe = new float[Nb]; rIm = new float[Nb];
            mag = new float[Nb]; ph = new float[Nb]; yRe = new float[Nb]; yIm = new float[Nb];
            tRe = new float[Nb]; tIm = new float[Nb]; env = new float[Nb]; prefix = new float[Nb + 1];
            pk = new int[Nb]; lo = new int[Nb]; hi = new int[Nb]; lobeMark = new bool[Nb];
            lag = new int[Nb]; fbGain = new float[Nb]; pos = new float[Nb];
            guardFft = new ComplexFft(size);
            gRe = new float[size]; gIm = new float[size];
            perm = new int[Nb]; bandOf = new int[Nb]; selScratch = new int[Nb]; valScratch = new int[Nb];
            for (int k = 0; k < Nb; k++)
            {
                double f = k * sampleRate / N;
                pos[k] = (float)Math.Clamp(PortableMath.Log2(Math.Max(f, 20.0) / 20.0) / PortableMath.Log2(1000.0), 0.0, 1.0);
                // the editor's bands: 160 of them, log-spaced from 30 Hz to 20 kHz
                double b = PortableMath.Log2(Math.Max(f, 30.0) / 30.0) / PortableMath.Log2(20000.0 / 30.0) * PrismView.Bands;
                bandOf[k] = Math.Clamp((int)b, 0, PrismView.Bands - 1);
                perm[k] = k;
            }
            // Ramp: at least 30 ms (one frame when a frame is longer: the window already spreads it).
            rampStep = (float)Math.Min(1.0, Hop / (0.03 * sampleRate));
            smoothA = (float)(1.0 - PortableMath.Exp(-Hop / (0.03 * sampleRate)));
        }

        /// <summary>Samples from an input to the output: the frame minus one.</summary>
        public int Latency => N - 1;

        public void Reset()
        {
            ch[0].Reset();
            ch[1].Reset();
            Array.Clear(weight);
            Array.Clear(target);
            frozen = false;
            first = true;
            valid = 0;
            slot = 0;
            scrEpoch = long.MinValue;
            scrPhase = 0;
            permAmount = -1;
            frameCount = 0;
            lastSerial = S?.FreezeSerial ?? 0;
        }

        // ------------------------------------------------------------------------------------------------ the frame

        public void Process(float[] re, float[] im, int n)
        {
            var s = S;
            Stft.Unpack(re, im, n, lRe, lIm, rRe, rIm);
            frameCount++;

            if (first)
            {
                semiS = s.Semitones; holdS = s.Hold; fbS = s.Feedback; dampS = s.Damping;
                lowS = s.DelLowS; highS = s.DelHighS; blurS = s.BlurSeconds;
                first = false;
            }
            else
            {
                double a = smoothA;
                semiS += (s.Semitones - semiS) * a;
                holdS += (float)((s.Hold - holdS) * a);
                fbS += (float)((s.Feedback - fbS) * a);
                dampS += (float)((s.Damping - dampS) * a);
                lowS += (s.DelLowS - lowS) * a;
                highS += (s.DelHighS - highS) * a;
                blurS += (s.BlurSeconds - blurS) * a;
            }

            // Which sections are wanted, and where their weights stand.
            bool capture = false;
            if (s.FreezeReq)
            {
                if (!frozen || s.FreezeSerial != lastSerial) capture = true;
                frozen = true;
            }
            else frozen = false;
            lastSerial = s.FreezeSerial;
            target[0] = s.PitchOn ? 1f : 0f;
            target[1] = s.ScrOn ? 1f : 0f;
            target[2] = frozen ? 1f : 0f;
            target[3] = s.BlurOn ? 1f : 0f;
            target[4] = s.DelayOn ? 1f : 0f;
            for (int i = 0; i < Sections; i++)
            {
                float d = target[i] - weight[i];
                weight[i] += d > rampStep ? rampStep : d < -rampStep ? -rampStep : d;
            }

            bool anyIn = View != null;
            if (anyIn) Bands(bandIn);

            if (weight[1] > 0 || target[1] > 0) UpdateScramble(s);
            if (weight[4] > 0 || target[4] > 0) UpdateDelay(s);
            else valid = 0;

            RunChannel(ch[0], lRe, lIm, capture);
            RunChannel(ch[1], rRe, rIm, capture);

            if (anyIn)
            {
                Bands(bandOut);
                var v = View.Back;
                Array.Copy(bandIn, v.In, PrismView.Bands);
                Array.Copy(bandOut, v.Out, PrismView.Bands);
                int lights = 0;
                for (int i = 0; i < Sections; i++) if (target[i] > 0) lights |= 1 << i;
                v.Lights = lights;
                v.Size = N;
                View.Publish();
            }

            lIm[0] = 0; lIm[Nb - 1] = 0; rIm[0] = 0; rIm[Nb - 1] = 0;
            Stft.Pack(lRe, lIm, rRe, rIm, n, re, im);
        }

        /// <summary>The sections that are on now, as bits (pitch, scramble, freeze, blur, delay).</summary>
        public int Lights
        {
            get
            {
                int l = 0;
                for (int i = 0; i < Sections; i++) if (target[i] > 0) l |= 1 << i;
                return l;
            }
        }

        /// <summary>Peak level per band of the two channels' spectra, in dB (full scale reads about 0 dB).</summary>
        void Bands(float[] into)
        {
            Array.Fill(into, 0f);
            for (int k = 1; k < Nb - 1; k++)
            {
                float m = MathF.Sqrt(lRe[k] * lRe[k] + lIm[k] * lIm[k]) + MathF.Sqrt(rRe[k] * rRe[k] + rIm[k] * rIm[k]);
                int band = bandOf[k];
                if (m > into[band]) into[band] = m;
            }
            // A full-scale sine on a bin reads 0.3183·N through the √Hann window (the mean of the two channels' magnitudes).
            float scale = 0.5f / (0.3183f * N);
            for (int i = 0; i < into.Length; i++)
                into[i] = into[i] > 1e-12f ? 20f * PortableMathF.Log10(into[i] * scale) : -140f;
        }

        void RunChannel(Chan c, float[] re, float[] im, bool capture)
        {
            var s = S;
            // 1. pitch
            if (weight[0] > 0 || target[0] > 0)
            {
                Pitch(c, re, im, tRe, tIm);
                Blend(re, im, tRe, tIm, weight[0]);
                c.PitchStale = false;
            }
            else c.PitchStale = true;
            // 2. scramble
            if (weight[1] > 0 || target[1] > 0)
            {
                Scramble(c, re, im, tRe, tIm);
                PeakGuard(c, re, im, tRe, tIm);
                Blend(re, im, tRe, tIm, weight[1]);
                c.ScrStale = false;
            }
            else c.ScrStale = true;
            // 3. freeze (its weight is the freeze's on/off ramp times the Hold blend)
            if (capture) Capture(c, re, im);
            Array.Copy(re, 0, c.PrevZRe, 0, Nb);
            Array.Copy(im, 0, c.PrevZIm, 0, Nb);
            if (weight[2] > 0)
            {
                FrozenOut(c, tRe, tIm);
                Blend(re, im, tRe, tIm, weight[2] * holdS);
            }
            // 4. blur
            if (weight[3] > 0 || target[3] > 0)
            {
                Blur(c, re, im, tRe, tIm);
                Blend(re, im, tRe, tIm, weight[3]);
                c.BlurStale = false;
            }
            else c.BlurStale = true;
            // 5. spectral delay
            if (weight[4] > 0 || target[4] > 0)
            {
                Delay(c, re, im, tRe, tIm);
                Blend(re, im, tRe, tIm, weight[4]);
            }
        }

        static void Blend(float[] re, float[] im, float[] oRe, float[] oIm, float w)
        {
            if (w >= 1f)
            {
                Array.Copy(oRe, re, re.Length);
                Array.Copy(oIm, im, im.Length);
            }
            else if (w > 0f)
            {
                for (int k = 0; k < re.Length; k++)
                {
                    re[k] += (oRe[k] - re[k]) * w;
                    im[k] += (oIm[k] - im[k]) * w;
                }
            }
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

        // ------------------------------------------------------------------------------------------------ peaks

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

        /// <summary>The sine window's main lobe at a distance <paramref name="d"/> bins: cos(πd)/(1 − 4d²).</summary>
        static double Lobe(double d)
        {
            double den = 1 - 4 * d * d;
            if (Math.Abs(den) < 1e-6) return Math.PI / 4;
            return PortableMath.Cos(Math.PI * d) / den;
        }

        // ------------------------------------------------------------------------------------------------ 1. pitch

        void Pitch(Chan c, float[] re, float[] im, float[] oRe, float[] oIm)
        {
            double ratio = PortableMath.Pow(2.0, semiS / 12.0);
            Polar(re, im);
            int count = FindPeaks();
            Array.Clear(yRe);
            Array.Clear(yIm);
            // A local maximum whose frequency (from the phase advance) is more than 0.8 bin from its bin is a sidelobe of a sine
            // somewhere else: its region joins the neighbour's instead of moving as a peak of its own.
            int valid = 0;
            int pendingLo = -1;
            for (int i = 0; i < count; i++)
            {
                int kq = pk[i];
                double dq = c.PitchStale ? 0.0 : Wrap(ph[kq] - c.PrevPh[kq] - (kq & 3) * HalfPi) * (2.0 / Math.PI);
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
                double dphi = c.PitchStale ? 0.0 : Wrap(ph[kp] - c.PrevPh[kp] - (kp & 3) * HalfPi);
                double delta = dphi * (2.0 / Math.PI);                  // the peak's true offset from bin kp, in bins
                // A partial's frequency is steady while a neighbour's beat (aliased by the hop) jitters the estimate: follow it with a one pole.
                double fRaw = kp + delta, fSm = fRaw;
                if (!c.PitchStale)
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
                double dEst = delta;                                    // (within 0.8 bin: the lobe is positive and smooth there)
                double g = ratio * (kp + delta);                        // where it goes, in bins
                int jp = (int)Math.Floor(g + 0.5);
                if (jp < -6 || jp > Nb + 6) continue;
                double dp = g - jp;
                float s = (float)(Lobe(dp) / Lobe(dEst));
                // The phase at the new peak bin: what it was a frame ago plus the advance of the new frequency.
                double psi;
                int jc = Math.Clamp(jp, 0, Nb - 1);
                double prevMag = Math.Sqrt((double)c.PrevYRe[jc] * c.PrevYRe[jc] + (double)c.PrevYIm[jc] * c.PrevYIm[jc]);
                if (!c.PitchStale && prevMag >= 0.1 * mag[kp] * s)
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
            // Formant keep: the envelope of the input over the shifted spectrum's: each bin takes the gain that restores it.
            if (S.FormantKeep && Math.Abs(ratio - 1.0) > 1e-4)
            {
                int w = Math.Max(2, (int)(250.0 * N / rate));
                prefix[0] = 0;
                for (int k = 0; k < Nb; k++) prefix[k + 1] = prefix[k] + mag[k];
                for (int k = 0; k < Nb; k++)
                {
                    int a = Math.Max(0, k - w), b = Math.Min(Nb, k + w + 1);
                    env[k] = (prefix[b] - prefix[a]) / (b - a) + 1e-12f;
                }
                for (int j = 0; j < Nb; j++)
                {
                    int src = (int)(j / ratio + 0.5);
                    if (src >= Nb) continue;
                    float gain = env[j] / env[src];
                    gain = gain < 0.25f ? 0.25f : gain > 4f ? 4f : gain;
                    // ...applied to what is stored for the next frame's phase too: it is a real gain, phases are untouched.
                    yRe[j] *= gain;
                    yIm[j] *= gain;
                }
            }
            yIm[0] = 0;
            yIm[Nb - 1] = 0;
            for (int k = 0; k < Nb; k++)
            {
                oRe[k] = yRe[k];
                oIm[k] = yIm[k];
            }
            Array.Copy(yRe, c.PrevYRe, Nb);
            Array.Copy(yIm, c.PrevYIm, Nb);
        }

        // ------------------------------------------------------------------------------------------------ 2. scramble

        static uint Hash(uint a, uint b, uint c)
        {
            uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu ^ (c + 0x165667B1u) * 0xC2B2AE35u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }

        void UpdateScramble(PrismSettings s)
        {
            long epoch;
            if (s.ScrBeats > 0) epoch = (long)Math.Floor(s.Beat / s.ScrBeats);
            else
            {
                scrPhase += Hop * s.ScrHz / rate;
                epoch = (long)Math.Floor(scrPhase);
            }
            if (epoch == scrEpoch && permAmount == s.ScrAmount && permWidth == s.ScrWidth && permSeed == s.ScrSeed) return;
            scrEpoch = epoch;
            permAmount = s.ScrAmount;
            permWidth = s.ScrWidth;
            permSeed = s.ScrSeed;
            for (int k = 0; k < Nb; k++) perm[k] = k;
            int width = Math.Max(2, s.ScrWidth);
            uint seed = (uint)s.ScrSeed * 7919u + (uint)(epoch & 0x7FFFFFFF) * 104729u;
            uint threshold = (uint)(Math.Clamp(s.ScrAmount, 0f, 1f) * 4294967295.0);
            uint band = 0;
            for (int b0 = 1; b0 < Nb - 1; b0 += width, band++)
            {
                int b1 = Math.Min(b0 + width, Nb - 1);
                int cnt = 0;
                for (int k = b0; k < b1; k++)
                    if (Hash(seed, (uint)k, 17u) <= threshold) selScratch[cnt++] = k;
                for (int i = 0; i < cnt; i++) valScratch[i] = selScratch[i];
                for (int i = cnt - 1; i > 0; i--)
                {
                    int j = (int)(Hash(seed, band, (uint)i) % (uint)(i + 1));
                    (valScratch[i], valScratch[j]) = (valScratch[j], valScratch[i]);
                }
                for (int i = 0; i < cnt; i++) perm[selScratch[i]] = valScratch[i];
            }
        }

        void Scramble(Chan c, float[] re, float[] im, float[] oRe, float[] oIm)
        {
            Polar(re, im);
            oRe[0] = re[0]; oIm[0] = 0;
            oRe[Nb - 1] = re[Nb - 1]; oIm[Nb - 1] = 0;
            for (int j = 1; j < Nb - 1; j++)
            {
                int k = perm[j];
                if (k == j)
                {
                    oRe[j] = re[j]; oIm[j] = im[j];
                    c.ScrOutPh[j] = ph[j];
                }
                else
                {
                    // The source bin's own frequency offset from its centre goes with its content; the phase runs on from this bin's last.
                    double dphi = c.ScrStale ? 0.0 : Wrap(ph[k] - c.ScrPrevPh[k] - (k & 3) * HalfPi);
                    double psi = Wrap(c.ScrOutPh[j] + (j & 3) * HalfPi + dphi);
                    c.ScrOutPh[j] = (float)psi;
                    oRe[j] = mag[k] * (float)PortableMath.Cos(psi);
                    oIm[j] = mag[k] * (float)PortableMath.Sin(psi);
                }
            }
            Array.Copy(ph, c.ScrPrevPh, Nb);
        }

        /// <summary>
        /// Keeps the shuffle from raising the peak: the frame's input and its shuffled output go back to the time domain together (one
        /// complex transform, the input as its real part and the output as its imaginary), and when the output's peak is above the input's
        /// the output is scaled down by the ratio (10 % tolerance). The gain falls by 70 % of the gap per frame and rises by 15 %, so it never pumps from frame
        /// to frame (the 4x overlap of the windows crossfades it) and never boosts. A shuffle that does not raise the peak is left as it is.
        /// </summary>
        void PeakGuard(Chan c, float[] inRe, float[] inIm, float[] oRe, float[] oIm)
        {
            int n = N;
            for (int k = 0; k < Nb; k++)
            {
                float ar = inRe[k], ai = inIm[k], br = oRe[k], bi = oIm[k];
                gRe[k] = ar - bi; gIm[k] = ai + br;
                if (k > 0 && k < n - k) { gRe[n - k] = ar + bi; gIm[n - k] = br - ai; }
            }
            guardFft.Transform(gRe, gIm, true);
            float pa = 0f, pb = 0f;
            for (int i = 0; i < n; i++)
            {
                float a = Math.Abs(gRe[i]), b = Math.Abs(gIm[i]);
                if (a > pa) pa = a;
                if (b > pb) pb = b;
            }
            float want = pb > pa * 1.1f && pb > 1e-12f ? pa * 1.1f / pb : 1f;
            if (c.ScrStale) c.ScrGain = want;
            else if (want < c.ScrGain) c.ScrGain += (want - c.ScrGain) * 0.7f;
            else c.ScrGain += (want - c.ScrGain) * 0.15f;
            float g = c.ScrGain;
            if (g < 1f)
                for (int k = 0; k < Nb; k++) { oRe[k] *= g; oIm[k] *= g; }
        }

        // ------------------------------------------------------------------------------------------------ 3. freeze

        /// <summary>Takes the snapshot: the magnitudes, the phases, and for each region the advance per frame of its peak's frequency.</summary>
        void Capture(Chan c, float[] re, float[] im)
        {
            Polar(re, im);
            int count = FindPeaks();
            for (int k = 0; k < Nb; k++)
            {
                c.FrozenM[k] = mag[k];
                c.FrozenPh[k] = ph[k];
                c.FrozenAdv[k] = 0;
            }
            for (int i = 0; i < count; i++)
            {
                int kp = pk[i];
                double prevPhase = PortableMath.Atan2(c.PrevZIm[kp], c.PrevZRe[kp]);
                double dphi = Wrap(ph[kp] - prevPhase - (kp & 3) * HalfPi);
                double adv = Wrap((kp & 3) * HalfPi + dphi);
                for (int k = lo[i]; k <= hi[i]; k++) c.FrozenAdv[k] = adv;
            }
        }

        void FrozenOut(Chan c, float[] oRe, float[] oIm)
        {
            for (int k = 0; k < Nb; k++)
            {
                double p = Wrap(c.FrozenPh[k] + c.FrozenAdv[k]);
                c.FrozenPh[k] = p;
                float m = c.FrozenM[k];
                oRe[k] = m * (float)PortableMath.Cos(p);
                oIm[k] = m * (float)PortableMath.Sin(p);
            }
            oIm[0] = 0;
            oIm[Nb - 1] = 0;
        }

        // ------------------------------------------------------------------------------------------------ 4. blur

        void Blur(Chan c, float[] re, float[] im, float[] oRe, float[] oIm)
        {
            double tau = blurS;
            float a = tau > 1e-4 ? (float)PortableMath.Exp(-Hop / (tau * rate)) : 0f;
            for (int k = 0; k < Nb; k++)
            {
                float m = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                mag[k] = m;
                float mb = a * c.Mb[k] + (1f - a) * m;
                c.Mb[k] = mb < 1e-20f ? 0f : mb;
            }
            // Smear: a box over the blurred magnitudes (the state keeps the unsmeared ones).
            int sm = S.Smear;
            if (sm > 0)
            {
                prefix[0] = 0;
                for (int k = 0; k < Nb; k++) prefix[k + 1] = prefix[k] + c.Mb[k];
                for (int k = 0; k < Nb; k++)
                {
                    int lo0 = Math.Max(0, k - sm), hi0 = Math.Min(Nb, k + sm + 1);
                    env[k] = (prefix[hi0] - prefix[lo0]) / (hi0 - lo0);
                }
            }
            else Array.Copy(c.Mb, env, Nb);
            for (int k = 0; k < Nb; k++)
            {
                float m = mag[k], target = env[k];
                float p = m > 1e-20f ? (float)PortableMath.Atan2(im[k], re[k]) : 0f;
                float live = p;
                bool strong = m >= 0.5f * target;
                if (strong)
                {
                    if (!c.BlurStale && m > 1e-12f)
                    {
                        double dphi = Wrap(p - c.BPrevPh[k] - (k & 3) * HalfPi);
                        c.BAdv[k] = (float)Wrap((k & 3) * HalfPi + dphi);
                    }
                    c.BSyn[k] = p;
                }
                else
                {
                    c.BSyn[k] = (float)Wrap(c.BSyn[k] + c.BAdv[k]);
                    p = c.BSyn[k];
                }
                if (m > 1e-20f) c.BPrevPh[k] = live;
                oRe[k] = target * (float)PortableMath.Cos(p);
                oIm[k] = target * (float)PortableMath.Sin(p);
            }
            oIm[0] = 0;
            oIm[Nb - 1] = 0;
        }

        // ------------------------------------------------------------------------------------------------ 5. spectral delay

        void UpdateDelay(PrismSettings s)
        {
            double perFrame = rate / Hop;
            float fb = Math.Min(fbS, 0.99f);
            for (int k = 0; k < Nb; k++)
            {
                double t = lowS + (highS - lowS) * pos[k];
                int l = (int)(t * perFrame + 0.5);
                lag[k] = l < 0 ? 0 : l > maxLag ? maxLag : l;
                fbGain[k] = fb * (1f - dampS * pos[k]);
            }
        }

        // The slot is advanced once per frame (after both channels), valid counts frames since the section woke.
        int delayChannelsDone;

        void Delay(Chan c, float[] re, float[] im, float[] oRe, float[] oIm)
        {
            int nb = Nb;
            int cur = slot;
            for (int k = 0; k < nb; k++)
            {
                int l = lag[k];
                float yr, yi;
                if (l == 0) { yr = re[k]; yi = im[k]; }
                else if (l > valid) { yr = 0; yi = 0; }
                else
                {
                    int src = cur - l;
                    if (src < 0) src += ringSize;
                    yr = c.DRe[src * nb + k];
                    yi = c.DIm[src * nb + k];
                }
                float g = l == 0 ? 0f : fbGain[k];
                float wr = re[k] + g * yr, wi = im[k] + g * yi;
                c.DRe[cur * nb + k] = wr > -1e-20f && wr < 1e-20f ? 0f : wr;
                c.DIm[cur * nb + k] = wi > -1e-20f && wi < 1e-20f ? 0f : wi;
                oRe[k] = yr;
                oIm[k] = yi;
            }
            oIm[0] = 0;
            oIm[nb - 1] = 0;
            if (++delayChannelsDone == 2)
            {
                delayChannelsDone = 0;
                slot = slot + 1 == ringSize ? 0 : slot + 1;
                if (valid < ringSize - 1) valid++;
            }
        }
    }
}
