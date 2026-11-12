#nullable disable
// Neo Imager (docs/specs/mastering-tools.md §5.7): the input goes through four zero-latency Linkwitz–Riley bands
// (LrBands); in each band the mid m = (l + r) / 2 and the side s = (l − r) / 2 are changed and put back.
//  - Width w (0 … 2): s' = w·s, so the side against the mid in the band is exactly w times what it was.
//  - Stereoize k (0 … 1): the side gets k times a decorrelated copy of the mid (the mid through the 90 degree
//    pair's second branch); the mid itself isn't touched, so l + r is the input's, sample for sample.
//  - Recover sides: the side narrowing takes away comes back into the mid, ρ = √(1 − w²) times the side through the
//    pair's second branch (a different phase at every frequency, so it adds to the mid's level and doesn't comb with it);
//    with w' = w the energy l² + r² stays what it was (m² + w²s² + ρ²s² = m² + s²).
//  - Exactly neutral (every width 100 %, every Stereoize 0) the output is the input, bit for bit, no latency: the
//    imager's result is blended with the input by a 20 ms ramp that rests at 0.
//  - Delta (DeltaListen) is the output minus the input, before Output; the gain-match reading compares the output with the input.
// Readings: per band, over the last 3 s at the output, the side against the mid (dB of energy) and the correlation.
// Allocation-free after construction; the portable maths and double arithmetic, fixed order.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Imager
{
    internal sealed class ImagerEngine
    {
        public const int MaxBlock = 256;
        const double GlideSeconds = 0.02;
        const int Steps = 30;

        readonly ParamValues values;
        float sampleRate = 48000;
        LrBands bands;
        readonly QuadratureAllpass[] qm = new QuadratureAllpass[4], qs = new QuadratureAllpass[4];
        readonly float[][] bandL = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[][] bandR = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        readonly float[] outL = new float[MaxBlock], outR = new float[MaxBlock];
        readonly SmoothRamp[] width = new SmoothRamp[4], stereoize = new SmoothRamp[4];
        SmoothRamp output;
        LinearRamp live;
        bool recover;
        int count;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // Readings: per step (100 ms) and band, the sums of l², r², l·r, m², s² at the output.
        readonly double[] sums = new double[Steps * 4 * 5];
        readonly double[] now = new double[4 * 5];
        int stepPos, stepLeft, stepLength, stepsDone;
        readonly double[] pubWidth = new double[4], pubCorr = new double[4];
        double pubMatch = double.NaN;

        public ImagerEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(16, 48000);
            Match = new GainMatch(48000);
        }

        public int Latency => 0;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            for (int k = 0; k < 4; k++)
            {
                width[k].Setup(GlideSeconds, sr);
                stereoize[k].Setup(GlideSeconds, sr);
                qm[k] = new QuadratureAllpass(sr);
                qs[k] = new QuadratureAllpass(sr);
            }
            output.Setup(GlideSeconds, sr);
            live.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            bands = new LrBands(sr, values.Value(P.Xo1), values.Value(P.Xo2), values.Value(P.Xo3));
            stepLength = Math.Max(1, (int)Math.Round(sr * 0.1));
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            bands.Reset();
            for (int k = 0; k < 4; k++) { qm[k].Reset(); qs[k].Reset(); }
            Delta.Reset();
            Match.Reset();
            Array.Clear(sums);
            Array.Clear(now);
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
            bool any = false;
            for (int k = 0; k < 4; k++)
            {
                float w = (float)(values.Value(P.Width1 + k) * 0.01);
                float s = values.F(P.Stereo1 + k);
                if (w != 1 || s != 0) any = true;
                Target(ref width[k], w, force);
                Target(ref stereoize[k], s, force);
            }
            recover = values.On(P.Recover);
            if (force) live.Reset(any ? 1 : 0);
            else live.SetTarget(any ? 1 : 0);
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
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            bands.Process(left, right, n, 4, bandL, bandR);
            Shape(n);
            if (live.At(0))
            {
                for (int i = 0; i < n; i++) live.Next();
            }
            else
                for (int i = 0; i < n; i++)
                {
                    float u = live.Next();
                    left[i] = dryL[i] + u * (outL[i] - dryL[i]);
                    right[i] = dryR[i] + u * (outR[i] - dryR[i]);
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
            stepLeft -= n;
            if (stepLeft <= 0)
            {
                Array.Copy(now, 0, sums, stepPos * 20, 20);
                Array.Clear(now);
                stepPos = (stepPos + 1) % Steps;
                if (stepsDone < Steps) stepsDone++;
                stepLeft += stepLength;
                Publish();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Shape(int n)
        {
            for (int i = 0; i < n; i++)
            {
                if ((++count & 63) == 0)
                    for (int b = 0; b < 4; b++) { qm[b].Flush(); qs[b].Flush(); }
                double sumL = 0, sumR = 0;
                for (int b = 0; b < 4; b++)
                {
                    double l = bandL[b][i], r = bandR[b][i];
                    double m = 0.5 * (l + r), s = 0.5 * (l - r);
                    double w = width[b].Next(), k = stereoize[b].Next();
                    double side = w * s, mid = m;
                    qm[b].Process(m, out _, out double mb);      // always run: the state doesn't depend on the settings' history
                    qs[b].Process(s, out _, out double sb);
                    if (k != 0) side += k * mb;
                    if (recover && w < 1) mid += Math.Sqrt(1 - w * w) * sb;
                    double ol = mid + side, orr = mid - side;
                    sumL += ol;
                    sumR += orr;
                    int a = b * 5;
                    now[a] += ol * ol;
                    now[a + 1] += orr * orr;
                    now[a + 2] += ol * orr;
                    now[a + 3] += mid * mid;
                    now[a + 4] += side * side;
                }
                outL[i] = (float)sumL;
                outR[i] = (float)sumR;
            }
        }

        void Publish()
        {
            for (int b = 0; b < 4; b++)
            {
                double ll = 0, rr = 0, lr = 0, mm = 0, ss = 0;
                for (int k = 0; k < stepsDone; k++)
                {
                    int a = k * 20 + b * 5;
                    ll += sums[a]; rr += sums[a + 1]; lr += sums[a + 2]; mm += sums[a + 3]; ss += sums[a + 4];
                }
                Volatile.Write(ref pubWidth[b], ss > 1e-20 && mm > 1e-20 ? 10 * PortableMath.Log10(ss / mm) : (ss > 1e-20 ? 100 : -100));
                Volatile.Write(ref pubCorr[b], ll > 1e-20 && rr > 1e-20 ? lr / Math.Sqrt(ll * rr) : 1);
            }
            Volatile.Write(ref pubMatch, Match.Db);
        }

        /// <summary>The gain-match reading, dB (NaN until 3 s of sound): for the editor's Match.</summary>
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        public double WidthDb(int band) => Volatile.Read(ref pubWidth[band]);
        public double Correlation(int band) => Volatile.Read(ref pubCorr[band]);

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>();
            for (int b = 0; b < 4; b++)
            {
                d[$"width_db_{b + 1}"] = Math.Round(WidthDb(b), 2);
                d[$"correlation_{b + 1}"] = Math.Round(Correlation(b), 3);
            }
            d["latency_samples"] = 0;
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
