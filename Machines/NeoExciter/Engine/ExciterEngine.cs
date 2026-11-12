#nullable disable
// Neo Exciter (docs/specs/mastering-tools.md §5.8): the successor of Oomek Exciter.
//  - The input is split into four zero-latency Linkwitz–Riley bands (LrBands). Each band, per channel, goes through a
//    4× oversampler (FastOversampler) and a first-order ADAA shaper; what it adds is the harmonic residual
//    r = f(D·u) / D − u, the shaper's output (brought back by the drive D) minus the audio (the audio taken half a
//    sample late, which is the ADAA's own delay, so the linear part cancels and only the new content is left).
//    Modes: Tape, Tube, Transistor, Diode (Neo Drive's Shapers.cs) and Warm, an even function (u² / (1 + u²), whose
//    harmonics of a sine are all even).
//  - Each band's residual goes down to the base rate and is scaled by its Amount (a ramp), summed, high-passed at
//    20 Hz (Warm has a DC term), shelved, and added to the input delayed by the oversampling's latency: the audio
//    itself never passes through a filter, so every Amount at 0 gives the input delayed, bit for bit; the latency
//    is constant (the 4× oversampler's) whatever the settings.
//  - Auto gain: the output's power and the input's (delayed) in 300 ms one-poles; gain √(in / out) within ±6 dB;
//    exactly 1 when nothing is added.
//  - Left/right, or mid/side (the bands run on mid and side; the residuals come back through the matrix).
//  - Delta (DeltaListen): the output minus the input before Output; the gain-match reading compares output with input.
// Allocation-free after construction; the portable maths and double arithmetic, fixed order.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Drive;
using NeoBuzz.Dsp;

namespace NeoBuzz.Exciter
{
    internal sealed class ExciterEngine
    {
        public const int MaxBlock = 256;
        public const int Factor = 4;
        const double GlideSeconds = 0.02;
        const int Steps = 30;

        public static int Latency => FastOversampler.LatencyFor(Factor);

        readonly ParamValues values;
        float sampleRate = 48000;
        LrBands bands;
        readonly FastOversampler[] os = new FastOversampler[8];       // [channel * 4 + band]
        AdaaShaper[] adaa = new AdaaShaper[8];
        readonly double[] warmU = new double[8], warmF = new double[8];
        readonly bool[] warmPrimed = new bool[8];
        readonly double[] lastU = new double[8];
        readonly float[][] bandA = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[][] bandB = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[] inA = new float[MaxBlock], inB = new float[MaxBlock];     // the channels the bands split (L/R or M/S)
        readonly float[] res = new float[MaxBlock];
        readonly float[][] sum = { new float[MaxBlock], new float[MaxBlock] };     // the residual sums per channel
        readonly float[][] amt = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];   // the input, now
        readonly float[] delL = new float[MaxBlock], delR = new float[MaxBlock];   // the input, Latency back
        readonly DelayLineF ringL = new DelayLineF(2048), ringR = new DelayLineF(2048);
        readonly SmoothRamp[] amount = new SmoothRamp[4];
        SmoothRamp output;
        Svf[] shelf = new Svf[2];
        DcBlocker[] dc = new DcBlocker[2];
        Shaper shaper;
        int mode;
        double drive = 4, invDrive = 0.25;
        bool midSide, auto;
        double powIn, powOut, autoCoef;
        int count;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        readonly double[] stepAdded = new double[Steps], stepIn = new double[Steps];
        double addedNow, inNow;
        int stepPos, stepLeft, stepLength, stepsDone;
        double pubAdded = -100, pubMatch = double.NaN;

        public ExciterEngine(ParamValues values)
        {
            this.values = values;
            for (int k = 0; k < os.Length; k++)
            {
                os[k] = new FastOversampler(MaxBlock);
                os[k].SetFactor(Factor);
            }
            Delta = new DeltaListen(Latency + 2, 48000);
            Match = new GainMatch(48000);
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            for (int k = 0; k < 4; k++) amount[k].Setup(GlideSeconds, sr);
            output.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            bands = new LrBands(sr, values.Value(P.Xo1), values.Value(P.Xo2), values.Value(P.Xo3));
            autoCoef = 1 - Detector.Coef(0.3, sr);
            for (int c = 0; c < 2; c++)
            {
                shelf[c].Mode = SvfMode.HighShelf;
                dc[c].Set(sr, 20);
            }
            stepLength = Math.Max(1, (int)Math.Round(sr * 0.1));
            Delta.SetSampleRate(sr);
            Delta.Latency = Latency;
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            bands.Reset();
            for (int k = 0; k < 8; k++)
            {
                os[k].Reset();
                adaa[k].Reset();
                warmPrimed[k] = false;
                lastU[k] = 0;
            }
            ringL.Reset();
            ringR.Reset();
            for (int c = 0; c < 2; c++) { shelf[c].Reset(); dc[c].Reset(); }
            Delta.Reset();
            Match.Reset();
            powIn = powOut = 0;
            Array.Clear(stepAdded);
            Array.Clear(stepIn);
            addedNow = inNow = 0;
            stepPos = stepsDone = 0;
            stepLeft = stepLength;
            count = 0;
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            double top = Math.Min(20000, 0.45 * sampleRate);
            double x1 = Math.Min(values.Value(P.Xo1), top);
            double x2 = Math.Min(Math.Max(values.Value(P.Xo2), x1), top);
            double x3 = Math.Min(Math.Max(values.Value(P.Xo3), x2), top);
            bands.SetTarget(x1, x2, x3);
            if (force) bands.Settle();
            for (int k = 0; k < 4; k++) Target(ref amount[k], values.F(P.Amount1 + k), force);
            mode = values.Choice(P.Mode);
            drive = DspMath.DbToGain(values.Value(P.Drive));
            invDrive = 1 / drive;
            shaper = new Shaper
            {
                Mode = mode == 1 ? DriveMode.Tube : mode == 2 ? DriveMode.Transistor : mode == 3 ? DriveMode.Diode : DriveMode.Tape,
                Hardness = mode == 2 ? 0.7 : 0,
                NegLevel = mode == 1 ? 0.8 : mode == 3 ? 0.6 : 1,
            };
            for (int c = 0; c < 2; c++) shelf[c].Set(Math.Min(values.Value(P.ShelfFreq), 0.45 * sampleRate), sampleRate, 0.7071, values.Value(P.ShelfGain));
            midSide = values.Choice(P.Stereo) == 1;
            auto = values.On(P.Auto);
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            int la = Latency;
            for (int i = 0; i < n; i++)
            {
                dryL[i] = left[i];
                dryR[i] = right[i];
                ringL.Write(left[i]);
                ringR.Write(right[i]);
                delL[i] = ringL.Read(la);
                delR[i] = ringR.Read(la);
            }
            if (midSide)
                for (int i = 0; i < n; i++)
                {
                    inA[i] = (float)(0.5 * ((double)dryL[i] + dryR[i]));
                    inB[i] = (float)(0.5 * ((double)dryL[i] - dryR[i]));
                }
            else
            {
                Array.Copy(dryL, inA, n);
                Array.Copy(dryR, inB, n);
            }
            bands.Process(inA, inB, n, 4, bandA, bandB);
            for (int b = 0; b < 4; b++)
                for (int i = 0; i < n; i++) amt[b][i] = amount[b].Next();
            Array.Clear(sum[0], 0, n);
            Array.Clear(sum[1], 0, n);
            for (int c = 0; c < 2; c++)
                for (int b = 0; b < 4; b++)
                {
                    Excite(c * 4 + b, c == 0 ? bandA[b] : bandB[b], n);
                    var s = sum[c];
                    var a = amt[b];
                    for (int i = 0; i < n; i++) s[i] += res[i] * a[i];
                }
            for (int c = 0; c < 2; c++)
            {
                var s = sum[c];
                for (int i = 0; i < n; i++)
                {
                    if ((++count & 63) == 0) { shelf[c].Flush(); }
                    s[i] = shelf[c].Process(dc[c].Process(s[i]));
                }
            }
            if (count > 1 << 30) count = 0;
            double addedPow = 0, inPow = 0;
            for (int i = 0; i < n; i++)
            {
                double ad0 = sum[0][i], ad1 = sum[1][i];
                double al, ar;
                if (midSide) { al = ad0 + ad1; ar = ad0 - ad1; }
                else { al = ad0; ar = ad1; }
                float yl = (float)(delL[i] + al), yr = (float)(delR[i] + ar);
                addedPow += al * al + ar * ar;
                inPow += (double)delL[i] * delL[i] + (double)delR[i] * delR[i];
                if (auto)
                {
                    powIn += ((double)delL[i] * delL[i] + (double)delR[i] * delR[i] - powIn) * autoCoef;
                    powOut += ((double)yl * yl + (double)yr * yr - powOut) * autoCoef;
                    double g = Math.Sqrt((powIn + 1e-12) / (powOut + 1e-12));
                    g = g > 2 ? 2 : g < 0.5 ? 0.5 : g;
                    yl = (float)(yl * g);
                    yr = (float)(yr * g);
                }
                left[i] = yl;
                right[i] = yr;
            }
            Delta.Process(dryL, dryR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            OutMeter.Feed(left, right, n);
            addedNow += addedPow;
            inNow += inPow;
            stepLeft -= n;
            if (stepLeft <= 0)
            {
                stepAdded[stepPos] = addedNow;
                stepIn[stepPos] = inNow;
                addedNow = inNow = 0;
                stepPos = (stepPos + 1) % Steps;
                if (stepsDone < Steps) stepsDone++;
                stepLeft += stepLength;
                double a = 0, ip = 0;
                for (int k = 0; k < stepsDone; k++) { a += stepAdded[k]; ip += stepIn[k]; }
                Volatile.Write(ref pubAdded, a > 1e-20 && ip > 1e-20 ? 10 * PortableMath.Log10(a / ip) : -100);
                Volatile.Write(ref pubMatch, Match.Db);
            }
        }

        /// <summary>The harmonic residual of one band and channel (filter slot <paramref name="slot"/>) into <see cref="res"/>, at the base rate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Excite(int slot, float[] x, int n)
        {
            var o = os[slot];
            var up = o.Up(x, 0, n);
            int m = n * Factor;
            double d = drive, id = invDrive;
            double prev = lastU[slot];
            if (mode == 4)
            {
                double u1 = warmU[slot], f1 = warmF[slot];
                bool primed = warmPrimed[slot];
                for (int k = 0; k < m; k++)
                {
                    double u = up[k] * d;
                    double F = u - PortableMath.Atan(u);
                    double y;
                    if (!primed) { primed = true; y = u * u / (1 + u * u); }
                    else
                    {
                        double dx = u - u1;
                        if (Math.Abs(dx) > 1e-6) y = (F - f1) / dx;
                        else { double um = 0.5 * (u + u1); y = um * um / (1 + um * um); }
                    }
                    u1 = u;
                    f1 = F;
                    up[k] = (float)(y * id);
                }
                warmU[slot] = u1;
                warmF[slot] = f1;
                warmPrimed[slot] = primed;
            }
            else
            {
                for (int k = 0; k < m; k++)
                {
                    double u = up[k];
                    double y = adaa[slot].Process(ref shaper, u * d, 0, 0) * id;
                    up[k] = (float)(y - 0.5 * (u + prev));
                    prev = u;
                }
                lastU[slot] = prev;
            }
            o.Down(res, 0, n);
        }

        public double GainMatchDb => Volatile.Read(ref pubMatch);
        public double AddedDb => Volatile.Read(ref pubAdded);

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["added_db"] = Math.Round(AddedDb, 2),
                ["latency_samples"] = Latency,
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
