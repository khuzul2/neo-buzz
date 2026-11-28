#nullable disable
// Neo Split (docs/specs/neo-split.md, SPL1): one stereo input, four stereo outputs that add up to it.
//  - Every output is a mix of "sources": the four bands of the 24 dB/oct split (LrBands), the four of the 48 dB/oct split (LrBands48),
//    the transient and the body, the mid and the side. The mode, the band count and the slope choose which sources go to which
//    output with a gain of 1 (the matrix below); a change of any of them moves every gain linearly over 20 ms, so a change while
//    the song plays does not click, and the outputs summed stay at the input's level all along (each source's gains sum to 1).
//  - Bands: the cascade of LrBands, its bands summing to the allpasses of the crossovers in a row (flat in level). Two bands are
//    the first source and the sum of the other three, three bands the first, the second and the sum of the last two, so the
//    crossovers that are not in use only turn the phase of every output alike.
//    The crossovers are kept in order, a third of an octave apart (as the engine uses them; the parameters keep what the user set).
//  - Transient and Body: TransientSplit's weight t (the louder channel's onset share, doubled and capped at 1: the split's weight
//    peaks near 0.8) comes from the input now and is applied to the input a lookahead later (2 ms, the latency reported to the host).
//    Transient = delayed × t, Body = delayed − Transient, so the two add up to the delayed input exactly. Sensitivity sets the
//    detector's deadband (11 dB at 0 %, 6 dB at 50 %, 1 dB at 100 %), Transient length its time constants, Body smoothing how slowly t falls.
//  - Mid and Side: output 1 = (M, M), output 2 = (S, −S) with M = (L + R)/2 and S = (L − R)/2: they add up to (L, R).
//  - A source nobody needs is idle (reset, not run); one that is used is always run, whether or not its outputs are wired, so a wire
//    added while the song plays hears the same as one that was always there. The host writes only the wired outputs.
// Allocation-free after construction; the portable maths; sums in a fixed order.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Split
{
    internal sealed class SplitEngine
    {
        public const int MaxBlock = 256;
        public const int Outputs = 4;

        /// <summary>The sources: 0 … 3 the bands of the 24 dB/oct split, 4 … 7 of the 48 dB/oct split, 8 transient, 9 body, 10 mid, 11 side.</summary>
        public const int Sources = 12;
        const int Paths = 4;                    // 24, 48, transient and body, mid and side
        const double FadeSeconds = 0.02;
        /// <summary>The lookahead of Transient and Body, ms.</summary>
        public const double LookaheadMs = 2.0;
        /// <summary>The closest two crossovers come: a third of an octave.</summary>
        public const double MinRatio = 1.2599210498948732;
        /// <summary>The onset weight is the split's × this, within 0 … 1 (Neo Transient's).</summary>
        public const double WeightScale = 2.0;
        const double FastRelease = 0.5, SlowRelease = 2;     // × the transient length: the detector's followers (fast release, slow attack = 1×, slow release)
        const int Ring = 8192;                  // the lookahead's most (2 ms at 384 kHz = 768) and a good deal over

        public enum Mode { Bands, TransientBody, MidSide }

        readonly ParamValues values;
        float sampleRate = 48000;
        int lookahead;
        volatile int latency;

        LrBands lr24;
        LrBands48 lr48;
        TransientSplit detector = new TransientSplit(48000);
        readonly float[] ringL = new float[Ring], ringR = new float[Ring];
        int ringPos;
        readonly float[] weight = new float[MaxBlock];
        double held, releaseCoef;

        readonly float[][] srcL = new float[Sources][], srcR = new float[Sources][];
        readonly float[][] b24L, b24R, b48L, b48R;
        /// <summary>What each output carries now, in the host's scale of ±1 (valid after Process, for every output, wired or not).</summary>
        public readonly float[][] OutL = new float[Outputs][], OutR = new float[Outputs][];
        LinearRamp[] gain = new LinearRamp[Outputs * Sources];
        readonly float[] want = new float[Outputs * Sources];
        readonly bool[] pathOn = new bool[Paths];

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter[] OutMeter = { new LevelMeter(), new LevelMeter(), new LevelMeter(), new LevelMeter() };

        Mode mode;
        int bands = 4, slope;
        readonly double[] crossover = new double[3];

        public SplitEngine(ParamValues values)
        {
            this.values = values;
            for (int s = 0; s < Sources; s++)
            {
                srcL[s] = new float[MaxBlock];
                srcR[s] = new float[MaxBlock];
            }
            for (int o = 0; o < Outputs; o++)
            {
                OutL[o] = new float[MaxBlock];
                OutR[o] = new float[MaxBlock];
            }
            b24L = new[] { srcL[0], srcL[1], srcL[2], srcL[3] };
            b24R = new[] { srcR[0], srcR[1], srcR[2], srcR[3] };
            b48L = new[] { srcL[4], srcL[5], srcL[6], srcL[7] };
            b48R = new[] { srcR[4], srcR[5], srcR[6], srcR[7] };
            SetSampleRate(48000);
        }

        /// <summary>Samples of delay on every output: 0 in Bands and Mid and Side, the lookahead in Transient and Body.</summary>
        public int Latency => latency;

        public float SampleRate => sampleRate;

        /// <summary>Whether an output carries anything in the current mode (the editor greys the others).</summary>
        public bool InUse(int output)
        {
            for (int s = 0; s < Sources; s++) if (want[output * Sources + s] != 0) return true;
            return false;
        }

        /// <summary>Takes the parameters' changes now (the machine calls this at rest too, so the latency it reports follows the mode).</summary>
        public void Poll() => Update(false);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            for (int e = 0; e < gain.Length; e++) gain[e].Setup(FadeSeconds, sr);
            InMeter.SetSampleRate(sr);
            foreach (var m in OutMeter) m.SetSampleRate(sr);
            detector = new TransientSplit(sr);
            lr24 = new LrBands(sr, 120, 1000, 6000);
            lr48 = new LrBands48(sr, 120, 1000, 6000);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            lr24.Reset();
            lr48.Reset();
            detector.Reset();
            Array.Clear(ringL);
            Array.Clear(ringR);
            ringPos = 0;
            held = 0;
            for (int p = 0; p < Paths; p++) pathOn[p] = false;
            for (int o = 0; o < Outputs; o++)
            {
                Array.Clear(OutL[o]);
                Array.Clear(OutR[o]);
                OutMeter[o].Set(0);
            }
            InMeter.Set(0);
            Update(true);
            for (int e = 0; e < gain.Length; e++) gain[e].Reset(want[e]);
            for (int s = 0; s < Sources; s++)
            {
                Array.Clear(srcL[s]);
                Array.Clear(srcR[s]);
            }
        }

        static int PathOf(int source) => source < 4 ? 0 : source < 8 ? 1 : source < 10 ? 2 : 3;

        /// <summary>The crossovers the engine uses for the parameters' (Hz): in order, a third of an octave apart, inside the audible band.</summary>
        public static void EffectiveCrossovers(double f1, double f2, double f3, int bands, double sampleRate, double[] result)
        {
            double top = Math.Min(20000, 0.45 * sampleRate);
            double[] x = { f1, f2, f3 };
            int active = Math.Max(1, Math.Min(3, bands - 1));
            for (int i = 1; i < active; i++) x[i] = Math.Max(x[i], x[i - 1] * MinRatio);
            for (int i = active - 1; i >= 0; i--)
            {
                double ceiling = i == active - 1 ? top : x[i + 1] / MinRatio;
                x[i] = Math.Min(Math.Max(x[i], 20), ceiling);
            }
            for (int i = active; i < 3; i++) x[i] = Math.Min(top, Math.Max(x[i], x[i - 1]));
            for (int i = 0; i < 3; i++) result[i] = x[i];
        }

        /// <summary>Which sources reach which output for a mode, a band count and a slope (1 where it does).</summary>
        public static void Matrix(Mode mode, int bands, int slope, float[] result)
        {
            Array.Clear(result);
            void Put(int output, int source) => result[output * Sources + source] = 1;
            switch (mode)
            {
                case Mode.Bands:
                    int b = slope == 0 ? 0 : 4;
                    if (bands <= 2)
                    {
                        Put(0, b);
                        Put(1, b + 1);
                        Put(1, b + 2);
                        Put(1, b + 3);
                    }
                    else if (bands == 3)
                    {
                        Put(0, b);
                        Put(1, b + 1);
                        Put(2, b + 2);
                        Put(2, b + 3);
                    }
                    else for (int o = 0; o < 4; o++) Put(o, b + o);
                    break;
                case Mode.TransientBody:
                    Put(0, 8);
                    Put(1, 9);
                    break;
                default:
                    Put(0, 10);
                    Put(1, 11);
                    break;
            }
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            mode = (Mode)Math.Clamp(values.Choice(P.Mode), 0, 2);
            bands = Math.Clamp((int)Math.Round(values.Value(P.Bands)), 2, 4);
            slope = values.Choice(P.Slope) == 0 ? 0 : 1;
            EffectiveCrossovers(values.Value(P.Xo1), values.Value(P.Xo2), values.Value(P.Xo3), bands, sampleRate, crossover);
            lr24.SetTarget(crossover[0], crossover[1], crossover[2]);
            lr48.SetTarget(crossover[0], crossover[1], crossover[2]);
            if (force)
            {
                lr24.Settle();
                lr48.Settle();
            }
            Matrix(mode, bands, slope, want);
            for (int e = 0; e < gain.Length; e++)
            {
                if (force) gain[e].Reset(want[e]);
                else gain[e].SetTarget(want[e]);
            }

            lookahead = Math.Min(Ring - 4, (int)Math.Round(LookaheadMs * 0.001 * sampleRate));
            latency = mode == Mode.TransientBody ? lookahead : 0;

            double sens = values.Value(P.Sensitivity);
            double deadbandDb = 2 + 10 * (1 - sens);
            double length = values.Value(P.Length);
            detector.Set(0.3, FastRelease * length, length, SlowRelease * length, deadbandDb);
            double smooth = values.Value(P.Smoothing);
            releaseCoef = smooth <= 0 ? 0 : PortableMath.Exp(-1.0 / (smooth * 0.001 * sampleRate));
            values.Clean();
        }

        bool Needed(int path)
        {
            for (int o = 0; o < Outputs; o++)
                for (int s = 0; s < Sources; s++)
                    if (PathOf(s) == path && (gain[o * Sources + s].Value != 0 || gain[o * Sources + s].Target != 0)) return true;
            return false;
        }

        void StartPath(int path)
        {
            switch (path)
            {
                case 0: lr24.Reset(); lr24.Settle(); break;
                case 1: lr48.Reset(); lr48.Settle(); break;
                case 2:
                    detector.Reset();
                    Array.Clear(ringL);
                    Array.Clear(ringR);
                    held = 0;
                    break;
            }
        }

        /// <summary>Splits a stereo block; the outputs are in <see cref="OutL"/> and <see cref="OutR"/> (all four, wired or not).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            for (int p = 0; p < Paths; p++)
            {
                bool need = Needed(p);
                if (need && !pathOn[p]) StartPath(p);
                pathOn[p] = need;
            }
            if (pathOn[0]) lr24.Process(left, right, n, 4, b24L, b24R);
            if (pathOn[1]) lr48.Process(left, right, n, 4, b48L, b48R);
            if (pathOn[2]) TransientBody(left, right, n);
            if (pathOn[3])
            {
                var m = srcL[10];
                var mr = srcR[10];
                var s = srcL[11];
                var sr = srcR[11];
                for (int i = 0; i < n; i++)
                {
                    float l = left[i], r = right[i];
                    float mid = (l + r) * 0.5f, side = (l - r) * 0.5f;
                    m[i] = mid;
                    mr[i] = mid;
                    s[i] = side;
                    sr[i] = 0f - side;
                }
            }
            for (int o = 0; o < Outputs; o++)
            {
                var ol = OutL[o];
                var or = OutR[o];
                Array.Clear(ol, 0, n);
                Array.Clear(or, 0, n);
                for (int s = 0; s < Sources; s++)
                {
                    ref var g = ref gain[o * Sources + s];
                    if (g.Settled)
                    {
                        float v = g.Value;
                        if (v == 0) continue;
                        var xl = srcL[s];
                        var xr = srcR[s];
                        if (v == 1f)
                            for (int i = 0; i < n; i++)
                            {
                                ol[i] += xl[i];
                                or[i] += xr[i];
                            }
                        else
                            for (int i = 0; i < n; i++)
                            {
                                ol[i] += v * xl[i];
                                or[i] += v * xr[i];
                            }
                    }
                    else
                    {
                        var xl = srcL[s];
                        var xr = srcR[s];
                        for (int i = 0; i < n; i++)
                        {
                            float v = g.Next();
                            ol[i] += v * xl[i];
                            or[i] += v * xr[i];
                        }
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    ol[i] = DspMath.Flush(ol[i]);
                    or[i] = DspMath.Flush(or[i]);
                }
                OutMeter[o].Feed(ol, or, n);
            }
        }

        // Transient and Body: the weight from the input now, the audio a lookahead later.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void TransientBody(float[] left, float[] right, int n)
        {
            detector.Process(left, right, 0, n, weight);
            var tl = srcL[8];
            var tr = srcR[8];
            var bl = srcL[9];
            var br = srcR[9];
            int mask = Ring - 1;
            double h = held;
            double c = releaseCoef;
            for (int i = 0; i < n; i++)
            {
                ringL[ringPos & mask] = left[i];
                ringR[ringPos & mask] = right[i];
                float dl = ringL[(ringPos - lookahead) & mask];
                float dr = ringR[(ringPos - lookahead) & mask];
                ringPos++;
                double t = weight[i] * WeightScale;
                if (t > 1) t = 1;
                h = t >= h ? t : t + (h - t) * c;
                if (h < 1e-12) h = 0;
                float w = (float)h;
                float xl = dl * w, xr = dr * w;
                tl[i] = xl;
                tr[i] = xr;
                bl[i] = dl - xl;
                br[i] = dr - xr;
            }
            held = h;
        }

        /// <summary>Nothing came in and the last sound has played out: the outputs are silent and the gains are at their targets (the host rests the machine).</summary>
        public void Rest(int n)
        {
            for (int e = 0; e < gain.Length; e++) gain[e].Reset(want[e]);
            for (int o = 0; o < Outputs; o++)
            {
                Array.Clear(OutL[o], 0, n);
                Array.Clear(OutR[o], 0, n);
            }
        }

        /// <summary>Samples of sound still to play out after the input stops (the crossovers' ringing, the lookahead): Work runs this long on silence.</summary>
        public int TailSamples => (int)(0.5 * sampleRate);

        /// <summary>The highest sample seen in any output, in dB (the engine's own meters), for agents.</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var r = new Dictionary<string, double>(5);
            for (int o = 0; o < Outputs; o++) r["out" + (o + 1) + "_db"] = Math.Max(-120.0, Math.Round(DspMath.GainToDb((double)OutMeter[o].Peak), 1));
            r["latency_samples"] = latency;
            return r;
        }

        public bool HoldsSubnormals() => lr24.HoldsSubnormals() || lr48.HoldsSubnormals();
    }
}
