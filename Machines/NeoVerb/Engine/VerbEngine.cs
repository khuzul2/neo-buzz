#nullable disable
// Neo Verb (docs/specs/machines.md §5): pre-delay → input diffusion (allpasses) → 16-line feedback
// delay network (Hadamard mix, modulated delays, per-line low/high shelves set from the decay
// times so T60 is exact in the mid band) plus early reflections → width → tone → mix. Freeze
// holds the tail at unity gain; shimmer feeds the tail back an octave up.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Verb
{
    internal enum VerbMode { Hall, Room, Chamber, Plate }

    internal sealed class VerbEngine
    {
        public const int MaxBlock = 256;
        const int N = 16;
        const double MaxPreDelay = 0.25;
        const double MaxSize = 1.6, MinSize = 0.4;
        const double LowCrossover = 250;
        const double HighDecayRatio = 0.25;
        const double MaxModDepth = 0.0012;   // seconds either side
        const float OutputScale = 0.42f;

        // Delay ranges (ms at size 1) and diffusion per mode.
        static readonly (double Min, double Max, int Stages, double DiffusionBase, double ErMin, double ErMax, int ErTaps)[] Modes =
        {
            (31, 97, 4, 0.50, 12, 90, 8),     // Hall
            (9, 31, 4, 0.50, 3, 40, 12),      // Room
            (17, 59, 5, 0.55, 6, 60, 10),     // Chamber
            (11, 41, 6, 0.60, 1, 9, 6),       // Plate
        };
        static readonly double[] DiffuserMs = { 1.31, 2.07, 3.43, 5.51, 7.71, 9.13 };

        readonly ParamValues values;
        float sampleRate = 48000;
        VerbMode mode;

        // Network.
        readonly FracDelay[] lines = new FracDelay[N];
        readonly double[] lengths = new double[N];       // target length, samples
        readonly double[] curLengths = new double[N];    // glides to the target
        readonly float[] lineGain = new float[N];
        readonly float[] shelfLowA = new float[N], shelfHighA = new float[N];
        readonly float[] lowState = new float[N], highState = new float[N];
        float lowG, highG;                               // one-pole coefficients (TPT G)
        readonly float[] v = new float[N], u = new float[N];
        readonly double[] modCos = new double[N], modSin = new double[N], modStepCos = new double[N], modStepSin = new double[N];
        readonly float[] modDepth = new float[N];
        static readonly float[] SignL = MakeSigns(0x5A3C96E1u), SignR = MakeSigns(0x93A5C3F1u);

        // Input side.
        FracDelay preL, preR;
        readonly AllpassDiffuser[] diffL = new AllpassDiffuser[6], diffR = new AllpassDiffuser[6];
        int stages;
        Biquad inLowCutL, inLowCutR, outHighCutL, outHighCutR, outLowCutL, outLowCutR;

        // Early reflections: taps on the pre-delay lines.
        readonly float[] erTimeL = new float[16], erTimeR = new float[16], erGainL = new float[16], erGainR = new float[16];
        int erTaps;

        // Shimmer: a two-tap pitch shifter (+12 semitones) on the tail.
        FracDelay shimmerLine;
        double shimmerPhase;
        float shimmerLp;
        int shimmerWindow;

        Smoother preDelay, early, late, width, mix, inputGain, shimmer;
        bool freeze;

        public VerbEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        static float[] MakeSigns(uint seed)
        {
            var rng = new Rng(seed);
            var s = new float[N];
            int plus = 0;
            for (int i = 0; i < N; i++)
            {
                s[i] = (rng.NextUInt() & 1) == 0 ? 1f : -1f;
                if (s[i] > 0) plus++;
            }
            return s;
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int maxLine = (int)Math.Ceiling((97 * MaxSize / 1000 + MaxModDepth) * sr) + 8;
            for (int i = 0; i < N; i++) lines[i] = new FracDelay(maxLine);
            int maxPre = (int)Math.Ceiling((MaxPreDelay + 0.09 * MaxSize + 0.01) * sr) + 8;
            preL = new FracDelay(maxPre);
            preR = new FracDelay(maxPre);
            int maxDiff = (int)Math.Ceiling(9.2 * 1.13 * MaxSize / 1000 * sr) + 4;
            for (int i = 0; i < 6; i++)
            {
                diffL[i] = new AllpassDiffuser(maxDiff);
                diffR[i] = new AllpassDiffuser(maxDiff);
            }
            shimmerWindow = (int)(0.05 * sr);
            shimmerLine = new FracDelay(shimmerWindow * 2 + 8);
            preDelay.Setup(0.05, sr);
            early.Setup(0.02, sr);
            late.Setup(0.02, sr);
            width.Setup(0.02, sr);
            mix.Setup(0.02, sr);
            inputGain.Setup(0.03, sr);
            shimmer.Setup(0.05, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
            Array.Copy(lengths, curLengths, N);
        }

        public void Reset()
        {
            foreach (var l in lines) l.Reset();
            preL.Reset();
            preR.Reset();
            foreach (var d in diffL) d.Reset();
            foreach (var d in diffR) d.Reset();
            shimmerLine.Reset();
            Array.Clear(lowState, 0, N);
            Array.Clear(highState, 0, N);
            inLowCutL.Reset(); inLowCutR.Reset(); outHighCutL.Reset(); outHighCutR.Reset(); outLowCutL.Reset(); outLowCutR.Reset();
        }

        static bool IsPrime(int n)
        {
            if (n < 2) return false;
            for (int d = 2; d * d <= n; d++) if (n % d == 0) return false;
            return true;
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            mode = (VerbMode)values.Choice(P.Mode);
            var m = Modes[(int)mode];
            double size = MinSize + (MaxSize - MinSize) * values.Value(P.Size);

            // Line lengths: geometric spread, nudged to distinct primes.
            int prev = 0;
            for (int i = 0; i < N; i++)
            {
                double ms = m.Min * PortableMath.Pow(m.Max / m.Min, i / (double)(N - 1));
                int len = Math.Max(prev + 1, (int)(ms * size * 0.001 * sampleRate));
                while (!IsPrime(len)) len++;
                lengths[i] = len;
                prev = len;
            }

            // Decay: per-line gains for exact T60 at 1 kHz; shelves for the low and high bands.
            double t60 = values.Value(P.Decay);
            double t60Low = t60 * values.Value(P.LowDecay);
            double t60High = t60 * HighDecayRatio;
            double damp = Math.Min(values.Value(P.Damping), sampleRate * 0.45);
            lowG = TptG(LowCrossover);
            highG = TptG(damp);
            freeze = values.On(P.Freeze);
            for (int i = 0; i < N; i++)
            {
                double d = lengths[i];
                double gMid = PortableMath.Pow(10, -3 * d / (sampleRate * t60));
                double gLow = PortableMath.Pow(10, -3 * d / (sampleRate * t60Low));
                double gHigh = PortableMath.Pow(10, -3 * d / (sampleRate * t60High));
                double aLow = gLow / gMid, aHigh = gHigh / gMid;
                // The shelves' gain at 1 kHz, divided out so the mid band is exact there.
                double at1k = ShelfLowMag(aLow, LowCrossover, 1000) * ShelfHighMag(aHigh, damp, 1000);
                if (freeze)
                {
                    lineGain[i] = 1f;
                    shelfLowA[i] = 1f;
                    shelfHighA[i] = 1f;
                }
                else
                {
                    lineGain[i] = (float)(gMid / at1k);
                    shelfLowA[i] = (float)aLow;
                    shelfHighA[i] = (float)aHigh;
                }
            }

            // Modulation: per-line rates spread ±30 %, depths in samples.
            double rate = values.Value(P.ModRate);
            double depth = values.Value(P.ModDepth) * MaxModDepth * sampleRate;
            var rng = new Rng(0x1234567u);
            for (int i = 0; i < N; i++)
            {
                double r = rate * (0.7 + 0.6 * rng.NextUnipolar());
                double w = 2 * Math.PI * r / sampleRate;
                modStepCos[i] = PortableMath.Cos(w);
                modStepSin[i] = PortableMath.Sin(w);
                if (force)
                {
                    double ph = 2 * Math.PI * rng.NextUnipolar();
                    modCos[i] = PortableMath.Cos(ph);
                    modSin[i] = PortableMath.Sin(ph);
                }
                modDepth[i] = (float)(depth * (0.6 + 0.4 * rng.NextUnipolar()));
            }

            // Diffusers.
            stages = m.Stages;
            double g = Math.Min(0.78, m.DiffusionBase + 0.25 * values.Value(P.Diffusion));
            for (int i = 0; i < 6; i++)
            {
                diffL[i].Delay = (float)(DiffuserMs[i] * size * 0.001 * sampleRate);
                diffR[i].Delay = (float)(DiffuserMs[i] * 1.13 * size * 0.001 * sampleRate);
                diffL[i].Gain = diffR[i].Gain = (float)(i < stages ? g : 0);
            }

            // Early reflections: a seeded pattern per mode, scaled by size.
            erTaps = m.ErTaps;
            var er = new Rng(0xBEEF00u + (uint)mode);
            for (int k = 0; k < erTaps; k++)
            {
                double t = m.ErMin + (m.ErMax - m.ErMin) * (k + 0.5 * er.NextUnipolar()) / erTaps;
                double t2 = m.ErMin + (m.ErMax - m.ErMin) * (k + 0.5 * er.NextUnipolar()) / erTaps;
                double decay = PortableMath.Exp(-2.2 * (t - m.ErMin) / (m.ErMax - m.ErMin));
                erTimeL[k] = (float)(t * size * 0.001 * sampleRate);
                erTimeR[k] = (float)(t2 * size * 0.001 * sampleRate);
                erGainL[k] = (float)(decay * (0.8 + 0.4 * er.NextUnipolar()) * ((er.NextUInt() & 1) == 0 ? 1 : -1) / Math.Sqrt(erTaps / 4.0));
                erGainR[k] = (float)(decay * (0.8 + 0.4 * er.NextUnipolar()) * ((er.NextUInt() & 1) == 0 ? 1 : -1) / Math.Sqrt(erTaps / 4.0));
            }

            double lowCut = values.Value(P.LowCut), highCut = Math.Min(values.Value(P.HighCut), sampleRate * 0.45);
            if (lowCut <= 10.5) { outLowCutL.SetIdentity(); outLowCutR.SetIdentity(); inLowCutL.SetIdentity(); inLowCutR.SetIdentity(); }
            else
            {
                outLowCutL.Set(BiquadType.HighPass, lowCut, sampleRate, 0.7071);
                outLowCutR.Set(BiquadType.HighPass, lowCut, sampleRate, 0.7071);
                inLowCutL.Set(BiquadType.HighPass, lowCut, sampleRate, 0.5);
                inLowCutR.Set(BiquadType.HighPass, lowCut, sampleRate, 0.5);
            }
            if (values[P.HighCut] >= values.Table[P.HighCut].Max) { outHighCutL.SetIdentity(); outHighCutR.SetIdentity(); }
            else
            {
                outHighCutL.Set(BiquadType.LowPass, highCut, sampleRate, 0.7071);
                outHighCutR.Set(BiquadType.LowPass, highCut, sampleRate, 0.7071);
            }

            preDelay.Target = (float)(values.Value(P.PreDelay) * 0.001 * sampleRate);
            early.Target = Level(values.Value(P.Early));
            late.Target = Level(values.Value(P.Late));
            width.Target = (float)(values.Value(P.Width) / 100);
            mix.Target = values.F(P.Mix);
            inputGain.Target = freeze ? 0f : 1f;
            shimmer.Target = freeze ? 0f : values.F(P.Shimmer);
            if (force)
            {
                preDelay.Reset(preDelay.Target);
                early.Reset(early.Target);
                late.Reset(late.Target);
                width.Reset(width.Target);
                mix.Reset(mix.Target);
                inputGain.Reset(inputGain.Target);
                shimmer.Reset(shimmer.Target);
            }
            values.Clean();
        }

        static float Level(double db) => double.IsNegativeInfinity(db) ? 0f : (float)DspMath.DbToGain(db);

        float TptG(double hz)
        {
            double w = PortableMath.Tan(Math.PI * Math.Clamp(hz, 1, sampleRate * 0.49) / sampleRate);
            return (float)(w / (1 + w));
        }

        // First-order shelves: low = A·LP + HP, high = LP + A·HP (one-pole crossovers).
        double ShelfLowMag(double a, double fc, double f)
        {
            double r = f / fc;
            // |LP| = 1/√(1+r²), with phase; LP + HP = 1 → low shelf = 1 + (A − 1)·LP.
            double re = 1 / (1 + r * r), im = -r / (1 + r * r);
            double sr = 1 + (a - 1) * re, si = (a - 1) * im;
            return Math.Sqrt(sr * sr + si * si);
        }

        double ShelfHighMag(double a, double fc, double f)
        {
            double r = f / fc;
            double re = 1 / (1 + r * r), im = -r / (1 + r * r);
            // high shelf = LP + A·(1 − LP) = A + (1 − A)·LP.
            double sr = a + (1 - a) * re, si = (1 - a) * im;
            return Math.Sqrt(sr * sr + si * si);
        }

        /// <summary>The decay time (T60, s) of the tail at a frequency, as set (for the editor).</summary>
        public static double DecayAt(ParamValues values, double hz)
        {
            double t60 = values.Value(P.Decay);
            if (hz <= LowCrossover) return t60 * values.Value(P.LowDecay);
            if (hz >= values.Value(P.Damping)) return t60 * HighDecayRatio;
            return t60;
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            for (int i = 0; i < n; i++)
            {
                float inL = left[i], inR = right[i];

                // Pre-delay (taps for the early reflections ride on the same lines).
                float ig = inputGain.Next();
                preL.Write(inLowCutL.Process(inL) * ig);
                preR.Write(inLowCutR.Process(inR) * ig);
                float pd = preDelay.Next();
                float dl = preL.ReadLinear(pd + 1);
                float dr = preR.ReadLinear(pd + 1);

                float erL = 0, erR = 0;
                for (int k = 0; k < erTaps; k++)
                {
                    erL += erGainL[k] * preL.ReadLinear(pd + 1 + erTimeL[k]);
                    erR += erGainR[k] * preR.ReadLinear(pd + 1 + erTimeR[k]);
                }

                // Input diffusion.
                for (int s = 0; s < stages; s++)
                {
                    dl = diffL[s].Process(dl);
                    dr = diffR[s].Process(dr);
                }

                // Shimmer: the tail's mono sum, an octave up, back into the input.
                float sh = shimmer.Next();
                if (sh > 1e-4f)
                {
                    float up = ShimmerRead();
                    dl += up * sh * 0.55f;
                    dr += up * sh * 0.55f;
                }

                // Read the lines (modulated), damp them.
                float outL = 0, outR = 0;
                for (int j = 0; j < N; j++)
                {
                    double c = modCos[j], s = modSin[j];
                    double nc = c * modStepCos[j] - s * modStepSin[j];
                    modSin[j] = c * modStepSin[j] + s * modStepCos[j];
                    modCos[j] = nc;
                    curLengths[j] += (lengths[j] - curLengths[j]) * 0.0005;
                    float x = lines[j].ReadCubic((float)(curLengths[j] + modDepth[j] * s));
                    // Low shelf (A·LP + HP) then high shelf (LP + A·HP), TPT one-poles.
                    float vv = (x - lowState[j]) * lowG;
                    float lp = vv + lowState[j];
                    lowState[j] = lp + vv;
                    x = shelfLowA[j] * lp + (x - lp);
                    vv = (x - highState[j]) * highG;
                    lp = vv + highState[j];
                    highState[j] = lp + vv;
                    x = lp + shelfHighA[j] * (x - lp);
                    x *= lineGain[j];
                    v[j] = x;
                    outL += SignL[j] * x;
                    outR += SignR[j] * x;
                }

                // Hadamard mix (orthonormal), inject the input, write back.
                for (int j = 0; j < N; j++) u[j] = v[j];
                for (int h = 1; h < N; h <<= 1)
                    for (int a = 0; a < N; a += 2 * h)
                        for (int b = a; b < a + h; b++)
                        {
                            float p = u[b], q = u[b + h];
                            u[b] = p + q;
                            u[b + h] = p - q;
                        }
                for (int j = 0; j < N; j++)
                {
                    float inj = (j & 1) == 0 ? dl : dr;
                    float w = u[j] * 0.25f + inj * ((j & 2) == 0 ? 0.5f : -0.5f);
                    lines[j].Write(DspMath.Flush(w));
                }

                outL *= OutputScale;
                outR *= OutputScale;
                if (sh > 1e-4f) ShimmerWrite(0.5f * (outL + outR));

                float el = early.Next(), lt = late.Next();
                float wetL = el * erL * 0.5f + lt * outL;
                float wetR = el * erR * 0.5f + lt * outR;
                float wd = width.Next();
                float mid = 0.5f * (wetL + wetR), side = 0.5f * (wetL - wetR) * wd;
                wetL = outHighCutL.Process(outLowCutL.Process(mid + side));
                wetR = outHighCutR.Process(outLowCutR.Process(mid - side));
                float mx = mix.Next();
                left[i] = inL + (wetL - inL) * mx;
                right[i] = inR + (wetR - inR) * mx;
            }
            // Keep the oscillators on the unit circle.
            for (int j = 0; j < N; j++)
            {
                double r = 1 / Math.Sqrt(modCos[j] * modCos[j] + modSin[j] * modSin[j]);
                modCos[j] *= r;
                modSin[j] *= r;
                lowState[j] = DspMath.Flush(lowState[j]);
                highState[j] = DspMath.Flush(highState[j]);
            }
            inLowCutL.Flush(); inLowCutR.Flush(); outHighCutL.Flush(); outHighCutR.Flush(); outLowCutL.Flush(); outLowCutR.Flush();
            OutMeter.Feed(left, right, n);
        }

        void ShimmerWrite(float x)
        {
            // Darken a little each pass so the octaves fade upwards.
            shimmerLp += (x - shimmerLp) * 0.35f;
            shimmerLine.Write(shimmerLp);
        }

        float ShimmerRead()
        {
            // Two taps sweeping through a window at twice the speed (+12 semitones), crossfaded.
            shimmerPhase += 1.0 / shimmerWindow;
            if (shimmerPhase >= 1) shimmerPhase -= 1;
            double p1 = shimmerPhase, p2 = shimmerPhase + 0.5;
            if (p2 >= 1) p2 -= 1;
            float d1 = (float)((1 - p1) * shimmerWindow) + 2;
            float d2 = (float)((1 - p2) * shimmerWindow) + 2;
            float g1 = (float)PortableMath.Sin(Math.PI * p1), g2 = (float)PortableMath.Sin(Math.PI * p2);
            return shimmerLine.ReadLinear(d1) * g1 + shimmerLine.ReadLinear(d2) * g2;
        }
    }
}
