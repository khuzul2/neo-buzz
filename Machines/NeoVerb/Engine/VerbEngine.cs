#nullable disable
// Neo Verb (docs/specs/machines.md §5): pre-delay → input diffusion (allpasses) → 16-line feedback
// delay network (Hadamard mix, modulated delays, per-line low/high shelves set from the decay
// times so T60 is exact in the mid band) plus early reflections → width → tone → mix. Freeze
// holds the tail at unity gain; shimmer feeds the tail back an octave up. Every line's loop gain
// stays below 1 at every frequency, whatever the settings (see Bounded), and the shimmer loop is
// held just inside its edge where it would grow (see the shimmer guard).

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
        internal const double MaxOvershoot = 3;  // a band may ring this many times longer than set (see Bounded)
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
        float tapD1, tapD2, tapG1, tapG2;                // this sample's shifter taps (ShimmerRead)

        // Shimmer guard (fork, docs/specs/machines.md Neo Verb): a shadow of the network and the
        // shimmer loop with no input, the same lines, shelves, gains, modulation and shifter taps, so
        // it follows exactly the tail's own dynamics. Seeded with noise and renormalised, it measures
        // how fast the loop decays. The rule is the one Bounded applies to the network: nothing rings
        // more than MaxOvershoot times longer than the slowest set decay (Decay, or Low decay where
        // that is longer). Only when the loop breaks it, decaying slower than that edge for
        // TriggerSeconds (on its rate averaged over 2 s, which starts from the network's own), or
        // growing by itself (its level, averaged over 2 s, rises TriggerDb above
        // its lowest; or, for a fast runaway, 15 dB at once, or 6 dB at more than 12 dB/s), is the
        // shimmer's gain lowered: at once by the excess it measured, then continuously (in
        // proportion to the excess relative to the target, so long and short decays settle alike),
        // until the loop decays as if it rang HoldOvershoot times longer than set, inside the edge (where
        // the network alone rings longer than that, close to the bound already, the shimmer is held
        // down as far as MinLnScale). A setting whose loop rings inside the edge never triggers it,
        // so it renders exactly as before. Freeze fades the shimmer out, which stops the guard;
        // plain Freeze never runs it.
        const double TriggerDb = 6, FastTriggerDb = 15, RunawayDbPerSecond = 12;
        const double HoldOvershoot = 0.8 * MaxOvershoot;
        const double TriggerSeconds = 1.5;
        const double MinLnScale = -6.9;                  // −60 dB of shimmer at most
        const double GuardGain = 0.07;                   // ln(scale) per s, per (excess / |target|)
        const double MinTargetDbPerSecond = 0.1;         // the excess is relative to at least this
        const double FirstStep = 1 / 30.0;               // ln(scale) per dB/s, when it triggers
        const double SettleSeconds = 0.15;
        const int SeedSamples = 2048;
        readonly FracDelay[] shadowLines = new FracDelay[N];
        readonly float[] shadowLow = new float[N], shadowHigh = new float[N], shadowV = new float[N], shadowU = new float[N];
        readonly float[] readPos = new float[N];
        FracDelay shadowShimmerLine;
        float shadowShimmerLp;
        bool shadowOn, limiting;
        int seedLeft;
        Rng seedRng;
        double shadowEnergy, shadowOffsetDb, shadowLevel, shadowPrev, shadowMin, shadowSlow, shadowSlowMin, shadowGrowth, shadowGrowthSlow, settle, lnScale, release, overEdge;
        float shimmerScale = 1f;
        double networkSlowest;                           // dB/s, the slowest line's decay without shimmer (≤ 0)
        double edgeRate, holdRate;                       // dB/s: decaying MaxOvershoot, HoldOvershoot times slower than set (0 frozen)

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
            for (int i = 0; i < N; i++) shadowLines[i] = new FracDelay(maxLine);
            shadowShimmerLine = new FracDelay(shimmerWindow * 2 + 8);
            shadowOn = false;
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
            StopGuard();
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
            double slowestT60 = Math.Max(t60, t60Low);
            edgeRate = freeze ? 0 : -60 / (MaxOvershoot * slowestT60);
            holdRate = freeze ? 0 : -60 / (HoldOvershoot * slowestT60);
            networkSlowest = double.NegativeInfinity;
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
                    networkSlowest = 0;
                }
                else
                {
                    shelfLowA[i] = (float)aLow;
                    shelfHighA[i] = (float)aHigh;
                    lineGain[i] = Bounded((float)(gMid / at1k), shelfLowA[i], shelfHighA[i], Math.Max(gLow, gMid), out double peak);
                    // The slowest this line lets anything decay, dB/s (the network decays no slower).
                    networkSlowest = Math.Max(networkSlowest, 20 * PortableMath.Log10(lineGain[i] * peak) * sampleRate / d);
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

        // Fork (neo-buzz, docs/specs/machine-safety.md §1): dividing out the shelves' gain at 1 kHz
        // is exact only while 1 kHz is in the flat mid band. With the damping corner low (below about
        // 1.4 kHz at the defaults, up to 2.9 kHz with Low decay ×4) the high shelf already cuts at
        // 1 kHz, and dividing that out lifted the slowest band (the lows, or the mid band below the
        // corner) over unity: the tail grew without bound (+347 dBFS). Now each line's peak loop gain
        // over frequency, computed exactly from its shelves, is checked against the slowest band's
        // set gain. Where that band would ring more than MaxOvershoot times longer than set, or never
        // die, the gain is lowered so it rings as set (the mid band at the set Decay, or the lows at
        // the set Low decay), blended in continuously from that edge. Anything that rings less than
        // that is untouched, bit for bit (the defaults and every factory preset). So no line's gain
        // reaches gSlowest^(1/MaxOvershoot) < 1 at any frequency, and with the orthonormal mix the
        // network decays, for every setting (Freeze holds it at unity by design).
        float Bounded(float gain, float aLow, float aHigh, double gSlowest, out double peak)
        {
            peak = ShelfPeak(aLow, aHigh, lowG, highG);
            // The loss per pass relative to the design: 1 as set, 1/3 three times longer, ≤ 0 growing.
            double loss = PortableMath.Log(gain * peak) / PortableMath.Log(gSlowest);
            const double edge = 1 / MaxOvershoot;
            if (loss >= edge) return gain;
            double target = loss <= 0 ? 1 : edge + (1 - edge) * (1 - loss / edge);
            return (float)(PortableMath.Pow(gSlowest, target) / peak);
        }

        /// <summary>
        /// The peak over frequency of |low shelf · high shelf| as the loop runs them. A TPT one-pole
        /// with coefficient G is exact at every frequency with its prewarped corner c = G/(1 − G):
        /// with x = tan²(ω/2), |H|² = N(x)/D(x), a ratio of quadratics, so the peak is at DC, at
        /// Nyquist, or where N′D − ND′ = 0 (a quadratic).
        /// </summary>
        static double ShelfPeak(double aLow, double aHigh, double lowG, double highG)
        {
            double cl = lowG / (1 - lowG), ch = highG / (1 - highG);
            double p = 1 / (cl * cl), q = 1 / (ch * ch);
            // N = (aLow² + p·x)(1 + aHigh²·q·x), D = (1 + p·x)(1 + q·x).
            double n0 = aLow * aLow, n1 = p + n0 * aHigh * aHigh * q, n2 = p * q * aHigh * aHigh;
            double d1 = p + q, d2 = p * q;
            double best = Math.Max(n0, aHigh * aHigh);
            double a = n2 * d1 - n1 * d2, b = 2 * (n2 - n0 * d2), c = n1 - n0 * d1;
            double disc = Math.Max(0, b * b - 4 * a * c);
            double h = -0.5 * (b + (b < 0 ? -Math.Sqrt(disc) : Math.Sqrt(disc)));
            if (a != 0) Consider(h / a);
            if (h != 0) Consider(c / h);
            return Math.Sqrt(best);

            void Consider(double x)
            {
                if (!(x > 0) || double.IsInfinity(x)) return;
                double f = (n0 + x * (n1 + x * n2)) / (1 + x * (d1 + x * d2));
                if (f > best) best = f;
            }
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

        /// <summary>For the tests: line <paramref name="i"/>'s length (samples), gain and shelves, as the loop runs them.</summary>
        internal (double Length, float Gain, float LowA, float HighA, float LowG, float HighG) Line(int i) =>
            (lengths[i], lineGain[i], shelfLowA[i], shelfHighA[i], lowG, highG);

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
                bool shimmering = sh > 1e-4f;
                float shUse = sh;
                if (shimmering)
                {
                    if (!shadowOn) StartGuard();
                    if (limiting) shUse = sh * shimmerScale;
                    float up = ShimmerRead();
                    dl += up * shUse * 0.55f;
                    dr += up * shUse * 0.55f;
                }
                else if (shadowOn) StopGuard();

                // Read the lines (modulated), damp them.
                float outL = 0, outR = 0;
                for (int j = 0; j < N; j++)
                {
                    double c = modCos[j], s = modSin[j];
                    double nc = c * modStepCos[j] - s * modStepSin[j];
                    modSin[j] = c * modStepSin[j] + s * modStepCos[j];
                    modCos[j] = nc;
                    curLengths[j] += (lengths[j] - curLengths[j]) * 0.0005;
                    float rp = (float)(curLengths[j] + modDepth[j] * s);
                    readPos[j] = rp;
                    float x = lines[j].ReadCubic(rp);
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
                if (shimmering)
                {
                    ShimmerWrite(0.5f * (outL + outR));
                    ShadowStep(shUse);
                }

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
            if (shadowOn) GuardBlock(n);
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
            tapD1 = d1; tapD2 = d2; tapG1 = g1; tapG2 = g2;
            return shimmerLine.ReadLinear(d1) * g1 + shimmerLine.ReadLinear(d2) * g2;
        }

        // The shimmer guard's shadow: one sample of the network and the shimmer loop without input,
        // with this sample's read positions, shelves, gains and shifter taps (seeded with noise at the start).
        void ShadowStep(float shUse)
        {
            float up = shadowShimmerLine.ReadLinear(tapD1) * tapG1 + shadowShimmerLine.ReadLinear(tapD2) * tapG2;
            float inj = up * shUse * 0.55f;
            float outL = 0, outR = 0;
            double e = 0;
            for (int j = 0; j < N; j++)
            {
                float x = shadowLines[j].ReadCubic(readPos[j]);
                float vv = (x - shadowLow[j]) * lowG;
                float lp = vv + shadowLow[j];
                shadowLow[j] = lp + vv;
                x = shelfLowA[j] * lp + (x - lp);
                vv = (x - shadowHigh[j]) * highG;
                lp = vv + shadowHigh[j];
                shadowHigh[j] = lp + vv;
                x = lp + shelfHighA[j] * (x - lp);
                x *= lineGain[j];
                shadowV[j] = x;
                shadowU[j] = x;
                outL += SignL[j] * x;
                outR += SignR[j] * x;
                e += (double)x * x;
            }
            shadowEnergy += e;
            for (int h = 1; h < N; h <<= 1)
                for (int a = 0; a < N; a += 2 * h)
                    for (int b = a; b < a + h; b++)
                    {
                        float p = shadowU[b], q = shadowU[b + h];
                        shadowU[b] = p + q;
                        shadowU[b + h] = p - q;
                    }
            bool seed = seedLeft > 0;
            if (seed) seedLeft--;
            for (int j = 0; j < N; j++)
            {
                float w = shadowU[j] * 0.25f + inj * ((j & 2) == 0 ? 0.5f : -0.5f);
                if (seed) w += 0.1f * seedRng.NextBipolar();
                shadowLines[j].Write(DspMath.Flush(w));
            }
            shadowShimmerLp += (0.5f * (outL + outR) * OutputScale - shadowShimmerLp) * 0.35f;
            shadowShimmerLine.Write(shadowShimmerLp);
        }

        void StartGuard()
        {
            foreach (var l in shadowLines) l.Reset();
            shadowShimmerLine.Reset();
            Array.Clear(shadowLow, 0, N);
            Array.Clear(shadowHigh, 0, N);
            shadowShimmerLp = 0;
            seedRng = new Rng(0x5EED5u);
            seedLeft = SeedSamples;
            shadowEnergy = 0;
            shadowOffsetDb = 0;
            settle = SettleSeconds;
            // The loop decays no faster than the network alone: start from there, so the rate's
            // averages come at the loop's from below and never cross the edge on the way.
            shadowGrowth = shadowGrowthSlow = networkSlowest;
            lnScale = 0;
            release = 0;
            overEdge = 0;
            limiting = false;
            shimmerScale = 1f;
            shadowOn = true;
        }

        void StopGuard()
        {
            shadowOn = false;
            limiting = false;
            shimmerScale = 1f;
        }

        // Once a block: the shadow's level (renormalised to stay near 1), whether it grows, and the
        // shimmer's gain while it does.
        void GuardBlock(int n)
        {
            double meanSquare = shadowEnergy / (n * (double)N);
            shadowEnergy = 0;
            double dt = n / (double)sampleRate;
            if (!(meanSquare > 1e-30))
            {
                // Nothing out of the lines yet (the seed is still on its way), or died out: seed again.
                if (seedLeft == 0) { if (settle > 0) settle -= dt; else StartGuard(); }
                return;
            }
            double level = shadowOffsetDb + 10 * PortableMath.Log10(meanSquare);
            if (meanSquare > 1e4 || meanSquare < 1e-4)
            {
                float f = (float)(1 / Math.Sqrt(meanSquare));
                foreach (var l in shadowLines) l.Scale(f);
                shadowShimmerLine.Scale(f);
                for (int j = 0; j < N; j++)
                {
                    shadowLow[j] *= f;
                    shadowHigh[j] *= f;
                }
                shadowShimmerLp *= f;
                shadowOffsetDb += 10 * PortableMath.Log10(meanSquare);
            }
            if (seedLeft > 0 || settle > 0)
            {
                if (seedLeft == 0) settle -= dt;
                shadowLevel = shadowSlow = level;
                shadowPrev = shadowMin = shadowSlowMin = level;
                return;
            }
            shadowLevel += (level - shadowLevel) * (1 - PortableMath.Exp(-dt / 0.1));
            shadowSlow += (level - shadowSlow) * (1 - PortableMath.Exp(-dt / 2.0));
            shadowGrowth += ((shadowLevel - shadowPrev) / dt - shadowGrowth) * (1 - PortableMath.Exp(-dt / 0.5));
            shadowPrev = shadowLevel;
            shadowGrowthSlow += (shadowGrowth - shadowGrowthSlow) * (1 - PortableMath.Exp(-dt / 2.0));
            if (shadowLevel < shadowMin) shadowMin = shadowLevel;
            if (shadowSlow < shadowSlowMin) shadowSlowMin = shadowSlow;
            // Aim just inside the edge: ringing HoldOvershoot times longer than set.
            double target = GuardTarget;
            if (!limiting)
            {
                double rise = shadowLevel - shadowMin;
                overEdge = shadowGrowthSlow > edgeRate ? overEdge + dt : 0;
                if (overEdge < TriggerSeconds && shadowSlow - shadowSlowMin <= TriggerDb && rise <= FastTriggerDb && !(rise > TriggerDb && shadowGrowth > RunawayDbPerSecond)) return;
                limiting = true;
                lnScale = -Math.Max(0, shadowGrowth - target) * FirstStep;
                release = 0;
            }
            lnScale -= GuardGain * (shadowGrowth - target) / Math.Max(-target, MinTargetDbPerSecond) * dt;
            // The shimmer can't make the loop decay faster than the network without it: where that
            // is slower than the target, it is held down this far, and the network's bound holds.
            if (lnScale < MinLnScale) lnScale = MinLnScale;
            if (lnScale >= 0)
            {
                lnScale = 0;
                release = shadowGrowth < target ? release + dt : 0;
                if (release > 2)
                {
                    // Decays at the full setting again: hand the shimmer back, untouched.
                    limiting = false;
                    shimmerScale = 1f;
                    shadowMin = shadowLevel;
                    shadowSlowMin = shadowSlow;
                    overEdge = 0;
                    return;
                }
            }
            shimmerScale = (float)PortableMath.Exp(lnScale);
        }

        double GuardTarget => holdRate;

        /// <summary>For the tests: whether the shimmer guard holds the shimmer down, by how much, how fast the
        /// shadow grows (dB/s), the decay it aims at and the edge it keeps the loop inside (dB/s).</summary>
        internal (bool Limiting, float Scale, double Growth, double Target, double Edge) ShimmerGuard => (limiting, shimmerScale, shadowGrowth, GuardTarget, edgeRate);
    }
}
