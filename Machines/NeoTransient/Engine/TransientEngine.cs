#nullable disable
// Neo Transient (docs/specs/mastering-tools.md §5.4): the audio goes into a ring, the lookahead's worth behind its
// input; two followers of the input's level (TransientSplit) say how much of each sample belongs to an onset, and
// the delayed audio gets g = 1 + (a − 1)·t + (b − 1)·s with Attack a, Sustain b, onset weight t and body weight
// s = 1 − t. Or, in Impact mode, the delayed audio is split into four bands (LrBands: zero latency, summing flat)
// and each band's microdynamics are moved by its own knob (MicroGain), with auto gain.
//  - The weight is the split's weight × 2 within 0 … 1: the split's weight peaks at about 0.8 a few milliseconds
//    into a hit, and the Attack gain is meant to be the gain of the hit's first peak, so it saturates there.
//  - Unity is exact: a = b = 1 gives g = 1 and every sample passes bit for bit (the audio delayed by the lookahead);
//    in mid/side the matrix is skipped while the gains are exactly 1. A steady sound has weight 0 (the split's
//    deadband), so Attack alone doesn't touch it.
//  - Left/right: one gain for both channels from the louder; mid/side: the matrix first (double), each of mid and
//    side its own detector and gain, and back.
//  - Mix is against the dry signal delayed by the lookahead; Delta (DeltaListen) hears the mixed output minus the
//    dry input, before Output; the gain-match reading (GainMatch) compares the output with the input.
//  - Mode switches crossfade over 20 ms (Impact settles to the plain delayed audio while all its knobs are at 0).
// Allocation-free after construction; the gains in double with the portable maths.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Transient
{
    internal sealed class TransientEngine
    {
        public const int MaxBlock = 256;
        const double GlideSeconds = 0.02;
        const double MaxLookaheadSeconds = 0.005;
        /// <summary>The onset weight is the split's × this, within 0 … 1.</summary>
        public const double WeightScale = 2.0;
        const int MaxRing = 2048;            // the lookahead's most (5 ms up to 384 kHz = 1920) and a little over
        const int Steps = 30;                // readings: 3 s of 100 ms steps
        const int HistoryLength = 160;       // the editor's gain trace: one point every 20 ms

        readonly ParamValues values;
        float sampleRate = 48000;
        int lookahead;

        readonly DelayLineF ringL = new DelayLineF(MaxRing), ringR = new DelayLineF(MaxRing);
        readonly TransientSplit[] split = new TransientSplit[2];
        readonly float[][] weight = { new float[MaxBlock], new float[MaxBlock] };
        LrBands bands;
        readonly float[][] bandL = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[][] bandR = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly MicroGain[] micro = new MicroGain[8];      // [band * 2 + channel]
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];     // the input, now
        readonly float[] delL = new float[MaxBlock], delR = new float[MaxBlock];     // the input, the lookahead back
        readonly float[] detA = new float[MaxBlock], detB = new float[MaxBlock];     // the detection signal (mid, side)
        readonly float[] xL = new float[MaxBlock], xR = new float[MaxBlock];         // what the bands split
        readonly float[] splitL = new float[MaxBlock], splitR = new float[MaxBlock]; // Attack/Sustain's output
        readonly float[] impactL = new float[MaxBlock], impactR = new float[MaxBlock];

        SmoothRamp attack, sustain, mix, output;
        LinearRamp mode, live;
        SmoothRamp[] impact = new SmoothRamp[4];
        bool midSide, auto;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;

        // Meters and readings.
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        readonly float[] stepMax = new float[Steps], stepMin = new float[Steps];
        readonly int[] stepOnset = new int[Steps], stepCount = new int[Steps];
        int stepPos, stepLeft, stepLength, stepsDone;
        float maxNow, minNow;
        int onsetNow, countNow;
        volatile float gainDbNow;
        readonly float[] history = new float[HistoryLength];
        int historyPos, historyLeft, historyLength;
        float historyMax;

        public TransientEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(MaxRing - 2, 48000);
            Match = new GainMatch(48000);
            split[0] = new TransientSplit(48000);
            split[1] = new TransientSplit(48000);
            SetSampleRate(48000);
        }

        /// <summary>Samples of delay: the lookahead.</summary>
        public int Latency => lookahead;

        /// <summary>The lookahead in samples for a setting in ms at a rate (what the machine reports).</summary>
        public static int LookaheadSamples(double ms, double sampleRate) => Math.Min(MaxRing - 4, (int)Math.Round(ms * 0.001 * sampleRate));

        /// <summary>The gain applied in the last block, dB (the largest of the bands' in Impact mode), for the editor's meter.</summary>
        public float GainDb => gainDbNow;

        /// <summary>The gain trace the editor draws (dB, one point every 20 ms, oldest after <see cref="HistoryPos"/>).</summary>
        public float[] History => history;
        public int HistoryPos => Volatile.Read(ref historyPos);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            attack.Setup(GlideSeconds, sr);
            sustain.Setup(GlideSeconds, sr);
            mix.Setup(GlideSeconds, sr);
            output.Setup(GlideSeconds, sr);
            mode.Setup(GlideSeconds, sr);
            live.Setup(GlideSeconds, sr);
            for (int k = 0; k < 4; k++) impact[k].Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            split[0] = new TransientSplit(sr);
            split[1] = new TransientSplit(sr);
            bands = new LrBands(sr, values.Value(P.Xo1), values.Value(P.Xo2), values.Value(P.Xo3));
            stepLength = Math.Max(1, (int)Math.Round(sr * 0.1));
            historyLength = Math.Max(1, (int)Math.Round(sr * 0.02));
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            ringL.Reset();
            ringR.Reset();
            split[0].Reset();
            split[1].Reset();
            bands.Reset();
            for (int k = 0; k < micro.Length; k++) micro[k].Reset();
            Delta.Reset();
            Match.Reset();
            Array.Clear(stepMax);
            Array.Clear(stepMin);
            Array.Clear(stepOnset);
            Array.Clear(stepCount);
            Array.Clear(history);
            stepPos = stepsDone = 0;
            stepLeft = stepLength;
            maxNow = minNow = 0;
            onsetNow = countNow = 0;
            gainDbNow = 0;
            historyPos = 0;
            historyLeft = historyLength;
            historyMax = 0;
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        static void Target(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        // Speed: the split's followers (fast attack, fast release, slow attack, slow release, ms) and the bands' scale.
        static readonly double[][] Speeds =
        {
            new[] { 0.1, 10, 10, 100 },
            new[] { 0.3, 20, 20, 150 },
            new[] { 1.0, 30, 50, 300 },
        };
        static readonly double[] BandSpeed = { 0.5, 1, 2 };

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            int speed = values.Choice(P.Speed);
            var sp = Speeds[speed];
            foreach (var s in split) s.Set(sp[0], sp[1], sp[2], sp[3], 0.5);
            lookahead = LookaheadSamples(values.Value(P.Lookahead), sampleRate);
            Delta.Latency = lookahead;
            double x1 = values.Value(P.Xo1), x2 = values.Value(P.Xo2), x3 = values.Value(P.Xo3);
            double top = Math.Min(20000, 0.45 * sampleRate);
            x1 = Math.Min(x1, top);
            x2 = Math.Min(Math.Max(x2, x1), top);
            x3 = Math.Min(Math.Max(x3, x2), top);
            bands.SetTarget(x1, x2, x3);
            if (force) bands.Settle();
            double[] centre = { Math.Sqrt(20 * x1), Math.Sqrt(x1 * x2), Math.Sqrt(x2 * x3), Math.Sqrt(x3 * top) };
            for (int b = 0; b < 4; b++)
            {
                MicroGain.Times(centre[b], BandSpeed[speed], out double f, out double sl);
                micro[2 * b].Setup(f, sl, sampleRate);
                micro[2 * b + 1].Setup(f, sl, sampleRate);
            }
            midSide = values.Choice(P.Stereo) == 1;
            auto = values.On(P.Auto);
            Target(ref attack, (float)DspMath.DbToGain(values.Value(P.Attack)), force);
            Target(ref sustain, (float)DspMath.DbToGain(values.Value(P.Sustain)), force);
            Target(ref mix, values.F(P.Mix), force);
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            Target(ref mode, values.Choice(P.Mode), force);
            bool any = false;
            for (int k = 0; k < 4; k++)
            {
                float v = values.F(P.Impact1 + k);
                if (v != 0) any = true;
                Target(ref impact[k], v, force);
            }
            Target(ref live, any ? 1 : 0, force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>Processes a stereo block in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            int la = lookahead;
            for (int i = 0; i < n; i++)
            {
                float x0 = left[i], x1 = right[i];
                dryL[i] = x0;
                dryR[i] = x1;
                ringL.Write(x0);
                ringR.Write(x1);
                delL[i] = ringL.Read(la);
                delR[i] = ringR.Read(la);
            }
            float gMax = float.NegativeInfinity, gMin = float.PositiveInfinity;
            int onsets = 0;

            bool needSplit = !mode.At(1), needImpact = !mode.At(0);
            if (needSplit) onsets = AttackSustain(n, ref gMax, ref gMin);
            if (needImpact) Impact(n, ref gMax, ref gMin);

            if (needSplit && needImpact)
                for (int i = 0; i < n; i++)
                {
                    float m = mode.Next();
                    left[i] = splitL[i] + m * (impactL[i] - splitL[i]);
                    right[i] = splitR[i] + m * (impactR[i] - splitR[i]);
                }
            else
            {
                var sl = needSplit ? splitL : impactL;
                var sr = needSplit ? splitR : impactR;
                Array.Copy(sl, left, n);
                Array.Copy(sr, right, n);
                for (int i = 0; i < n; i++) mode.Next();
            }

            // Mix with the dry signal (delayed by the lookahead), Delta, the output.
            if (!mix.At(1))
                for (int i = 0; i < n; i++)
                {
                    float mx = mix.Next();
                    float dl = delL[i], dr = delR[i];
                    left[i] = dl + mx * (left[i] - dl);
                    right[i] = dr + mx * (right[i] - dr);
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

            if (float.IsInfinity(gMax)) { gMax = 0; gMin = 0; }
            float maxDb = (float)DspMath.GainToDb((double)Math.Max(gMax, 1e-6f)), minDb = (float)DspMath.GainToDb((double)Math.Max(gMin, 1e-6f));
            if (gMax == 0 && gMin == 0) maxDb = minDb = 0;
            gainDbNow = Math.Abs(maxDb) >= Math.Abs(minDb) ? maxDb : minDb;
            if (maxDb > maxNow) maxNow = maxDb;
            if (minDb < minNow) minNow = minDb;
            onsetNow += onsets;
            countNow += n;
            if (Math.Abs(gainDbNow) > Math.Abs(historyMax)) historyMax = gainDbNow;
            historyLeft -= n;
            if (historyLeft <= 0)
            {
                history[historyPos % HistoryLength] = historyMax;
                Volatile.Write(ref historyPos, (historyPos + 1) % HistoryLength);
                historyMax = 0;
                historyLeft += historyLength;
            }
            stepLeft -= n;
            if (stepLeft <= 0)
            {
                stepMax[stepPos] = maxNow;
                stepMin[stepPos] = minNow;
                stepOnset[stepPos] = onsetNow;
                stepCount[stepPos] = countNow;
                stepPos = (stepPos + 1) % Steps;
                if (stepsDone < Steps) stepsDone++;
                maxNow = minNow = 0;
                onsetNow = countNow = 0;
                stepLeft += stepLength;
                PublishReadings();
            }
        }

        /// <summary>Attack/Sustain: the weights from the input now, the gains on the delayed audio; the count of samples in an onset.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int AttackSustain(int n, ref float gMax, ref float gMin)
        {
            int onsets = 0;
            bool unity = attack.At(1f) && sustain.At(1f);
            if (!midSide)
            {
                split[0].Process(dryL, dryR, 0, n, weight[0]);
                var w = weight[0];
                for (int i = 0; i < n; i++)
                {
                    double t = w[i] * WeightScale;
                    if (t > 1) t = 1;
                    if (t >= 0.5) onsets++;
                    double g = TransientSplit.Gain(attack.Next(), sustain.Next(), t);
                    splitL[i] = (float)(delL[i] * g);
                    splitR[i] = (float)(delR[i] * g);
                    if (g > gMax) gMax = (float)g;
                    if (g < gMin) gMin = (float)g;
                }
                return onsets;
            }
            for (int i = 0; i < n; i++)
            {
                detA[i] = (float)(0.5 * ((double)dryL[i] + dryR[i]));
                detB[i] = (float)(0.5 * ((double)dryL[i] - dryR[i]));
            }
            split[0].Process(detA, detA, 0, n, weight[0]);
            split[1].Process(detB, detB, 0, n, weight[1]);
            var w0 = weight[0];
            var w1 = weight[1];
            for (int i = 0; i < n; i++)
            {
                double ta = w0[i] * WeightScale, tb = w1[i] * WeightScale;
                if (ta > 1) ta = 1;
                if (tb > 1) tb = 1;
                if (ta >= 0.5 || tb >= 0.5) onsets++;
                float a = attack.Next(), b = sustain.Next();
                double ga = TransientSplit.Gain(a, b, ta), gb = TransientSplit.Gain(a, b, tb);
                if (unity)
                {
                    splitL[i] = delL[i];
                    splitR[i] = delR[i];
                }
                else
                {
                    double m = 0.5 * ((double)delL[i] + delR[i]) * ga, s = 0.5 * ((double)delL[i] - delR[i]) * gb;
                    splitL[i] = (float)(m + s);
                    splitR[i] = (float)(m - s);
                }
                float hi = (float)Math.Max(ga, gb), lo = (float)Math.Min(ga, gb);
                if (hi > gMax) gMax = hi;
                if (lo < gMin) gMin = lo;
            }
            return onsets;
        }

        /// <summary>Impact: the delayed audio in four bands, each band's microdynamics moved, summed; blended with the plain audio by how many knobs are off.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Impact(int n, ref float gMax, ref float gMin)
        {
            if (midSide)
                for (int i = 0; i < n; i++)
                {
                    xL[i] = (float)(0.5 * ((double)delL[i] + delR[i]));
                    xR[i] = (float)(0.5 * ((double)delL[i] - delR[i]));
                }
            else
            {
                Array.Copy(delL, xL, n);
                Array.Copy(delR, xR, n);
            }
            bands.Process(xL, xR, n, 4, bandL, bandR);
            Array.Clear(impactL, 0, n);
            Array.Clear(impactR, 0, n);
            for (int b = 0; b < 4; b++)
            {
                var bl = bandL[b];
                var br = bandR[b];
                ref var ra = ref impact[b];
                for (int i = 0; i < n; i++)
                {
                    float amt = ra.Next();
                    double l = bl[i], r = br[i];
                    double gl, gr;
                    if (midSide)
                    {
                        gl = micro[2 * b].Next(l * l, amt, auto);
                        gr = micro[2 * b + 1].Next(r * r, amt, auto);
                    }
                    else
                    {
                        gl = gr = micro[2 * b].Next(Math.Max(l * l, r * r), amt, auto);
                    }
                    impactL[i] += (float)(l * gl);
                    impactR[i] += (float)(r * gr);
                    float hi = (float)Math.Max(gl, gr), lo = (float)Math.Min(gl, gr);
                    if (hi > gMax) gMax = hi;
                    if (lo < gMin) gMin = lo;
                }
            }
            if (midSide)
                for (int i = 0; i < n; i++)
                {
                    double m = impactL[i], s = impactR[i];
                    impactL[i] = (float)(m + s);
                    impactR[i] = (float)(m - s);
                }
            // Settles to the plain delayed audio while every knob is at 0 (bit for bit at rest).
            if (live.At(0))
            {
                Array.Copy(delL, impactL, n);
                Array.Copy(delR, impactR, n);
                for (int i = 0; i < n; i++) live.Next();
            }
            else
                for (int i = 0; i < n; i++)
                {
                    float u = live.Next();
                    impactL[i] = delL[i] + u * (impactL[i] - delL[i]);
                    impactR[i] = delR[i] + u * (impactR[i] - delR[i]);
                }
        }

        // ---- readings (machine_readings), from any thread: values published every 100 ms --------------------------

        double pubMax, pubMin, pubOnset, pubMatch = double.NaN;

        void PublishReadings()
        {
            float max = 0, min = 0;
            long onsets = 0, count = 0;
            for (int k = 0; k < stepsDone; k++)
            {
                max = MathF.Max(max, stepMax[k]);
                min = MathF.Min(min, stepMin[k]);
                onsets += stepOnset[k];
                count += stepCount[k];
            }
            Volatile.Write(ref pubMax, max);
            Volatile.Write(ref pubMin, min);
            Volatile.Write(ref pubOnset, count > 0 ? 100.0 * onsets / count : 0);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        /// <summary>The gain-match reading, dB (NaN until 3 s of sound): for the editor's Match.</summary>
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        /// <summary>The readings now (gain_match_db only once it is known: 3 s of sound).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["gain_max_db"] = Math.Round(Volatile.Read(ref pubMax), 2),
                ["gain_min_db"] = Math.Round(Volatile.Read(ref pubMin), 2),
                ["transient_percent"] = Math.Round(Volatile.Read(ref pubOnset), 2),
                ["latency_samples"] = Latency,
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
