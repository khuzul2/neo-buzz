#nullable disable
// Neo Random's engine (docs/specs/neo-random.md): four bounded modulation sources (Random, Sample and hold,
// 1/f-like Drift, and Lorenz Chaos) with tempo sync, probability, raised-cosine smoothing, quantisation,
// depth, signed bias, unipolar/bipolar range, deterministic seeding and beat/bar/note retriggering.
// Pure: basic IEEE operations and PortableMath only, 0 bytes allocated per update, bit-exact across platforms.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.RandomMachine
{
    internal enum RandomMode
    {
        Random = 0,
        SampleAndHold = 1,
        Drift = 2,
        Chaos = 3,
    }

    internal sealed class RandomEngine
    {
        /// <summary>Synced step lengths in beats (four to the bar): Free, 8 bars … 1/64.</summary>
        public static readonly double[] SyncBeats = TempoSync.BeatsTable(Params.Table[P.Sync].Names);

        // 5-stage geometric Ornstein-Uhlenbeck bank (3x corner spacing, gamma = 0.25 -> ~-1.5 dec/dec PSD slope).
        const int DriftStages = 5;
        static readonly double[] DriftFreqRatios = { 0.06, 0.18, 0.54, 1.62, 4.86 };
        static readonly double[] DriftWeights = ComputeNormalizedDriftWeights();

        static double[] ComputeNormalizedDriftWeights()
        {
            double[] raw = { 1.0, 0.7598356856515925, 0.5773502691896257, 0.4386913376508308, 0.3333333333333333 };
            double sumSq = 0.0;
            for (int i = 0; i < raw.Length; i++) sumSq += raw[i] * raw[i];
            double inv = 1.0 / Math.Sqrt(sumSq);
            for (int i = 0; i < raw.Length; i++) raw[i] *= inv;
            return raw;
        }

        // PCG32 state (64-bit state, odd increment): passes BigCrush, zero startup bias, bit-exact everywhere.
        ulong rngState;
        ulong rngInc;

        // Monotonic auto-seed counter so Seed = 0 differs across instances and runs.
        static long autoSeedSeq;

        readonly ParamValues v;
        readonly double[] driftS = new double[DriftStages];

        int lastSeedParam = int.MinValue;
        double phase;
        long cycle;
        bool wasPlaying;
        double lastBeats = double.NaN;
        bool noteRestart;

        // Random & Sample-and-hold state.
        double prevRand, curRand;
        double prevSh, curSh;

        // Drift state.
        double driftSmooth;
        bool driftPrimed;

        // Chaos (Lorenz + calm periodic blend) state.
        double lx, ly, lz;
        double orbitPhase;
        double prevChaos, curChaos;

        public RandomEngine(ParamValues values)
        {
            v = values;
            int seed = Math.Clamp(v.Choice(P.Seed), 0, 9999);
            lastSeedParam = seed;
            Reseed(seed, v.Value(P.Chaos));
        }

        /// <summary>The step length in beats for Sync and Feel (0 when Sync is Free).</summary>
        public double CycleBeats()
        {
            double b = SyncBeats[Math.Clamp(v.Choice(P.Sync), 0, SyncBeats.Length - 1)];
            if (b <= 0) return 0;
            return TempoSync.Beats(b, v.Choice(P.Feel));
        }

        /// <summary>Seconds a target write may take to cross its whole range.</summary>
        public double Glide => v.Value(P.Glide);

        /// <summary>A note on the listened machine (Retrigger: Note).</summary>
        public void Note() => noteRestart = true;

        /// <summary>Explicitly resets phase and re-seeds from the current Seed parameter.</summary>
        public void Reset()
        {
            int seed = Math.Clamp(v.Choice(P.Seed), 0, 9999);
            lastSeedParam = seed;
            wasPlaying = false;
            lastBeats = double.NaN;
            noteRestart = false;
            Reseed(seed, v.Value(P.Chaos));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong SplitMix64(ref ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint NextUInt()
        {
            ulong old = rngState;
            rngState = unchecked(old * 6364136223846793005UL + rngInc);
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>Uniform double in [0, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double NextUnipolar() => NextUInt() * (1.0 / 4294967296.0);

        /// <summary>Zero-mean, unit-variance bounded pseudo-Gaussian draw: 2*(u1 + u2 + u3 - 1.5).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double NextUnitNoise() => 2.0 * (NextUnipolar() + NextUnipolar() + NextUnipolar() - 1.5);

        void SeedRng(int seedParam)
        {
            ulong s;
            if (seedParam <= 0)
            {
                long seq = Interlocked.Increment(ref autoSeedSeq);
                s = unchecked((ulong)Environment.TickCount64 ^ ((ulong)seq * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL);
            }
            else
            {
                s = unchecked((ulong)(uint)seedParam * 0x9E3779B97F4A7C15UL ^ 0xA0761D6478BD642FUL);
            }
            ulong initState = SplitMix64(ref s);
            ulong initSeq = SplitMix64(ref s);
            rngState = 0UL;
            rngInc = (initSeq << 1) | 1UL;
            NextUInt();
            rngState = unchecked(rngState + initState);
            NextUInt();
        }

        void Reseed(int seedParam, double chaosAmt)
        {
            SeedRng(seedParam);
            phase = 0.0;
            cycle = 0;

            double r0 = NextUnipolar();
            prevRand = curRand = r0;
            double s0 = NextUnipolar();
            prevSh = curSh = s0;

            for (int k = 0; k < DriftStages; k++) driftS[k] = NextUnitNoise();
            driftSmooth = EvalDriftOutput();
            driftPrimed = true;

            // Deterministic Lorenz start off the invariant z-axis (x = y = 0) + seeded periodic orbitPhase.
            orbitPhase = NextUnipolar();
            double sx = NextUnipolar() < 0.5 ? -1.0 : 1.0;
            double sy = NextUnipolar() < 0.5 ? -1.0 : 1.0;
            lx = sx * (2.0 + 6.0 * NextUnipolar());
            ly = sy * (2.0 + 6.0 * NextUnipolar());
            lz = 18.0 + 12.0 * NextUnipolar();

            // Short warm-up (1.0 Lorenz time unit) settles transients onto the strange attractor.
            double rho = 26.0 + 14.0 * DspMath.Clamp(chaosAmt, 0.0, 1.0);
            for (int i = 0; i < 200; i++) LorenzStepRk4(0.005, rho);

            double c0 = EvalChaosOutput(chaosAmt);
            prevChaos = curChaos = c0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double EvalDriftOutput()
        {
            double sum = 0.0;
            for (int k = 0; k < DriftStages; k++) sum += DriftWeights[k] * driftS[k];
            return 0.5 + 0.5 * PortableMath.Tanh(0.85 * sum);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double EvalChaosOutput(double chaosAmt)
        {
            double c = DspMath.Clamp(chaosAmt, 0.0, 1.0);
            double calm = 0.5 + 0.5 * PortableMath.Sin(DspMath.TwoPiD * orbitPhase);
            double lorenz = 0.5 + 0.5 * PortableMath.Tanh(lx / 18.0);
            return (1.0 - c) * calm + c * lorenz;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void LorenzStepRk4(double h, double rho)
        {
            const double Sigma = 10.0;
            const double Beta = 8.0 / 3.0;

            double x = lx, y = ly, z = lz;

            double k1x = Sigma * (y - x);
            double k1y = x * (rho - z) - y;
            double k1z = x * y - Beta * z;

            double x2 = x + 0.5 * h * k1x, y2 = y + 0.5 * h * k1y, z2 = z + 0.5 * h * k1z;
            double k2x = Sigma * (y2 - x2);
            double k2y = x2 * (rho - z2) - y2;
            double k2z = x2 * y2 - Beta * z2;

            double x3 = x + 0.5 * h * k2x, y3 = y + 0.5 * h * k2y, z3 = z + 0.5 * h * k2z;
            double k3x = Sigma * (y3 - x3);
            double k3y = x3 * (rho - z3) - y3;
            double k3z = x3 * y3 - Beta * z3;

            double x4 = x + h * k3x, y4 = y + h * k3y, z4 = z + h * k3z;
            double k4x = Sigma * (y4 - x4);
            double k4y = x4 * (rho - z4) - y4;
            double k4z = x4 * y4 - Beta * z4;

            x += (h / 6.0) * (k1x + 2.0 * k2x + 2.0 * k3x + k4x);
            y += (h / 6.0) * (k1y + 2.0 * k2y + 2.0 * k3y + k4y);
            z += (h / 6.0) * (k1z + 2.0 * k2z + 2.0 * k3z + k4z);

            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                lx = 3.0; ly = 4.0; lz = 24.0;
                return;
            }
            lx = DspMath.Flush(DspMath.Clamp(x, -100.0, 100.0));
            ly = DspMath.Flush(DspMath.Clamp(y, -100.0, 100.0));
            lz = DspMath.Flush(DspMath.Clamp(z, -100.0, 100.0));
        }

        void AdvanceDrift(double dt, double effRate)
        {
            if (dt <= 0.0) return;
            double maxFreq = 0.45 / dt;
            for (int k = 0; k < DriftStages; k++)
            {
                double fk = effRate * DriftFreqRatios[k];
                if (fk > maxFreq) fk = maxFreq;
                double alpha = PortableMath.Exp(-DspMath.TwoPiD * fk * dt);
                double sigma = Math.Sqrt(Math.Max(0.0, 1.0 - alpha * alpha));
                driftS[k] = DspMath.Flush(alpha * driftS[k] + sigma * NextUnitNoise());
            }
        }

        void AdvanceChaos(double dt, double effRate, double chaosAmt)
        {
            if (dt <= 0.0) return;
            orbitPhase += dt * effRate;
            if (orbitPhase >= 1.0) orbitPhase -= Math.Floor(orbitPhase);

            double rho = 26.0 + 14.0 * DspMath.Clamp(chaosAmt, 0.0, 1.0);
            double totalH = dt * effRate * 1.5;
            // Bounded sub-stepping keeps RK4 strictly stable at any dt and any Rate (up to 40 Hz).
            int sub = Math.Clamp((int)Math.Ceiling(totalH / 0.005), 1, 64);
            double h = Math.Min(0.005, totalH / sub);
            for (int i = 0; i < sub; i++) LorenzStepRk4(h, rho);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void FireStep(double prob, double chaosAmt)
        {
            prevRand = curRand;
            curRand = NextUnipolar();

            double coin = NextUnipolar();
            double sample = NextUnipolar();
            if (coin < prob)
            {
                prevSh = curSh;
                curSh = sample;
            }

            prevChaos = curChaos;
            curChaos = EvalChaosOutput(chaosAmt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double GlideStep(double prev, double cur, double p, double smooth)
        {
            if (smooth <= 0.0) return cur;
            double t = p >= smooth ? 1.0 : p / smooth;
            double w = 0.5 - 0.5 * PortableMath.Cos(Math.PI * t);
            return prev + (cur - prev) * w;
        }

        /// <summary>
        /// Advances <paramref name="dt"/> seconds (song at <paramref name="beats"/> beats and <paramref name="bpm"/>),
        /// returning the shaped modulation output in [0, 1].
        /// </summary>
        public float Step(double dt, double beats, bool playing, double bpm)
        {
            if (!(dt > 0.0)) dt = 0.0;
            if (!(bpm > 1.0)) bpm = 120.0;

            var mode = (RandomMode)Math.Clamp(v.Choice(P.Mode), 0, 3);
            double cyc = CycleBeats();
            double rate = v.Value(P.Rate);
            double effRate = cyc > 0.0 ? (bpm / 60.0) / cyc : rate;
            double prob = v.Value(P.Prob);
            double chaosAmt = v.Value(P.Chaos);
            double smooth = v.Value(P.Smooth);
            double depth = v.Value(P.Depth);
            double bias = v.Value(P.Bias);       // signed [-1, +1]
            bool bipolar = v.Choice(P.Range) != 0;
            int quantise = Math.Clamp(v.Choice(P.Quantise), 0, 32);
            int seed = Math.Clamp(v.Choice(P.Seed), 0, 9999);
            int retrigger = v.Choice(P.Retrigger);

            // Re-seed on Seed parameter change, transport start (!wasPlaying && playing), or loop rewind.
            bool transportStart = !wasPlaying && playing;
            bool rewind = playing && !double.IsNaN(lastBeats) && beats < lastBeats - 1e-6;
            if (seed != lastSeedParam)
            {
                lastSeedParam = seed;
                Reseed(seed, chaosAmt);
            }
            else if (seed > 0 && (transportStart || rewind))
            {
                Reseed(seed, chaosAmt);
            }

            // Explicit Beat / Bar / Note retrigger handling across all 4 modes.
            bool retriggered = false;
            if (playing && !double.IsNaN(lastBeats) && !rewind)
            {
                if (retrigger == 1)
                    retriggered = Math.Floor(beats + 1e-12) != Math.Floor(lastBeats + 1e-12);
                else if (retrigger == 2)
                    retriggered = Math.Floor(beats / 4.0 + 1e-12) != Math.Floor(lastBeats / 4.0 + 1e-12);
            }
            if (noteRestart && retrigger == 3)
                retriggered = true;
            noteRestart = false;

            wasPlaying = playing;
            lastBeats = playing ? beats : double.NaN;

            if (retriggered)
            {
                if (seed > 0)
                {
                    Reseed(seed, chaosAmt);
                }
                else
                {
                    phase = 0.0;
                    cycle = 0;
                    // Force an immediate fresh transition on every mode even when cycle was already 0.
                    AdvanceDrift(Math.Max(dt, 0.02 / Math.Max(0.02, effRate)), effRate);
                    AdvanceChaos(Math.Max(dt, 0.05 / Math.Max(0.02, effRate)), effRate, chaosAmt);
                    FireStep(prob, chaosAmt);
                }
            }

            // Advance continuous generators (Drift and Chaos) at effective rate.
            if (mode == RandomMode.Drift)
                AdvanceDrift(dt, effRate);
            else if (mode == RandomMode.Chaos)
                AdvanceChaos(dt, effRate, chaosAmt);

            // Advance step clock (song-locked when synced + playing + Retrigger Free, else double integration).
            if (cyc > 0.0 && playing && retrigger == 0)
                phase = Math.Max(0.0, beats / cyc);
            else
                phase += dt * effRate;

            long c = (long)Math.Floor(phase + 1e-12);
            if (c != cycle)
            {
                long delta = c - cycle;
                int steps = delta > 0 ? (int)Math.Min(delta, 64) : 1;
                for (int s = 0; s < steps; s++) FireStep(prob, chaosAmt);
                cycle = c;
            }
            double p = DspMath.Clamp(phase - c, 0.0, 1.0);

            // Raw bounded [0, 1] mode output.
            double m;
            switch (mode)
            {
                case RandomMode.Random:
                    m = GlideStep(prevRand, curRand, p, smooth);
                    break;
                case RandomMode.SampleAndHold:
                    m = GlideStep(prevSh, curSh, p, smooth);
                    break;
                case RandomMode.Drift:
                {
                    double raw = EvalDriftOutput();
                    if (!driftPrimed || smooth <= 0.0)
                    {
                        driftSmooth = raw;
                        driftPrimed = true;
                    }
                    else
                    {
                        double tau = smooth * 0.25 / Math.Max(0.05, effRate);
                        double a = 1.0 - PortableMath.Exp(-dt / Math.Max(1e-6, tau));
                        driftSmooth = DspMath.Flush(driftSmooth + a * (raw - driftSmooth));
                    }
                    m = driftSmooth;
                    break;
                }
                default: // Chaos
                {
                    if (smooth >= 0.999)
                        m = EvalChaosOutput(chaosAmt);
                    else
                        m = GlideStep(prevChaos, curChaos, p, smooth);
                    break;
                }
            }

            // Quantise: 0 = Off, 1 = 0.5 (1 distinct level), 2..32 = N evenly spaced levels.
            double q;
            if (quantise <= 0)
                q = m;
            else if (quantise == 1)
                q = 0.5;
            else
            {
                int k = Math.Clamp((int)Math.Floor(m * quantise), 0, quantise - 1);
                q = k / (double)(quantise - 1);
            }

            // Range, Depth and signed Bias -> [0, 1].
            double y = bipolar
                ? 0.5 + depth * (q - 0.5) + 0.5 * bias
                : depth * q + bias;

            return (float)DspMath.Clamp(y, 0.0, 1.0);
        }
    }
}
