#nullable disable
// Neo Clipper (docs/specs/mastering-tools.md §5.1): drive → (mid/side) → (bands, each with its drive) → per band:
// ÷ ceiling → up (FirOversampler) → the curve with first-order ADAA → down → × ceiling → (the bands summed;
// mid/side decoded) → (with bands or mid/side: the curve again on left and right, oversampled the same way) →
// the ceiling guard → mix with the dry signal → Delta → output. Pure: tested by NeoBuzz.MachineTests.
//  - The ceiling holds exactly: the curves never pass it, but the downsampling filter rings past it after hard
//    clipping, so a 0.5 ms lookahead gain (CeilingGuard) takes those samples under it without aliasing (at Mix
//    100 %; below, the dry signal can pass it).
//  - The latency is the same at every setting (two oversampled stages at 8× and the guard; the lower factors and
//    the unused second stage padded with plain delay), so changing a setting never moves the audio.
//  - 1× runs the curve as it is (no ADAA): ADAA's two-sample mean would cost 7.6 dB at 20 kHz there.
//  - Delta (DeltaListen) hears the mixed output minus the dry input, before Output; the gain-match reading
//    (GainMatch) compares the output with the input. Readings over the last 3 s for machine_readings.
// Allocation-free after construction; the curves and filters in double with the portable maths.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Clipper
{
    internal sealed class ClipperEngine
    {
        public const int MaxBlock = 256;
        const double GlideSeconds = 0.02;
        /// <summary>Samples of delay at a rate, the same at every setting: two oversampled stages at the largest factor and the guard.</summary>
        public static int LatencyAt(double sampleRate) => 2 * FirOversampler.MaxLatency + CeilingGuard.HoldFor(sampleRate) - 1;

        /// <summary>Samples of delay at the rate set.</summary>
        public int Latency => LatencyAt(sampleRate);

        const int MaxHold = 1920;                                    // the guard's hold up to 384 kHz
        static readonly int Ring = 2 * FirOversampler.MaxLatency + MaxHold + 2;

        const int Steps = 30;               // readings: 3 s of 100 ms steps

        /// <summary>One oversampled clip stage on one stream.</summary>
        sealed class Stage
        {
            public readonly FirOversampler Os = new FirOversampler(MaxBlock);
            public readonly DelayLineF Pad = new DelayLineF(FirOversampler.MaxLatency + 1);
            public ClipAdaa Adaa;

            public void Reset()
            {
                Os.Reset();
                Pad.Reset();
                Adaa.Reset();
            }
        }

        readonly ParamValues values;
        float sampleRate = 48000;
        // [channel][band], and the second stage per channel.
        readonly Stage[][] bandStage = { new[] { new Stage(), new Stage(), new Stage() }, new[] { new Stage(), new Stage(), new Stage() } };
        readonly Stage[] finalStage = { new Stage(), new Stage() };
        readonly DelayLineF[] dryRing = { new DelayLineF(Ring), new DelayLineF(Ring) };
        readonly DelayLineF ceilRing = new DelayLineF(Ring);
        readonly CeilingGuard guard = new CeilingGuard(MaxHold);
        readonly float[][] bandL = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[][] bandR = { new float[MaxBlock], new float[MaxBlock], new float[MaxBlock] };
        readonly float[] inL = new float[MaxBlock], inR = new float[MaxBlock];
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        readonly float[] sumL = new float[MaxBlock], sumR = new float[MaxBlock];
        readonly float[] dryOutL = new float[MaxBlock], dryOutR = new float[MaxBlock];
        readonly float[] ceil1 = new float[MaxBlock], ceil2 = new float[MaxBlock], invCeil = new float[MaxBlock];
        BandSplit split;

        ClipCurve curve;
        int factor = 4, bands = 1;
        bool midSide;
        LinearRamp drive, ceiling, mix, output, lowDrive, midDrive, highDrive;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;

        // Readings and meters.
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        readonly float[] stepClip = new float[Steps];
        readonly int[] stepBent = new int[Steps], stepCount = new int[Steps];
        int stepPos, stepLeft, stepLength, stepsDone;
        float clipNow;
        int bentNow, countNow;
        volatile float clipDbNow;

        public ClipperEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(Ring - 2, 48000);
            Match = new GainMatch(48000);
            SetSampleRate(48000);
        }

        public int Factor => factor;
        public int Bands => bands;
        public ClipCurve Curve => curve;

        /// <summary>How far the driven signal went over the ceiling in the last block, dB (≥ 0), for the editor.</summary>
        public float ClipDb => clipDbNow;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            drive.Setup(GlideSeconds, sr);
            ceiling.Setup(GlideSeconds, sr);
            mix.Setup(GlideSeconds, sr);
            output.Setup(GlideSeconds, sr);
            lowDrive.Setup(GlideSeconds, sr);
            midDrive.Setup(GlideSeconds, sr);
            highDrive.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            split = new BandSplit(sr, values.Value(P.Xo1), values.Value(P.Xo2));
            stepLength = Math.Max(1, (int)Math.Round(sr * 0.1));
            Delta.SetSampleRate(sr);
            guard.SetHold(CeilingGuard.HoldFor(sr));
            Delta.Latency = Latency;
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            for (int c = 0; c < 2; c++)
            {
                foreach (var s in bandStage[c]) s.Reset();
                finalStage[c].Reset();
                dryRing[c].Reset();
            }
            ceilRing.Reset();
            // The ceiling rings start full of the ceiling (a stage divides by what they hold).
            for (int k = 0; k < Ring; k++) ceilRing.Write(ceiling.Value);
            guard.Reset(ceiling.Value);
            split.Reset();
            Delta.Reset();
            Match.Reset();
            Array.Clear(stepClip);
            Array.Clear(stepBent);
            Array.Clear(stepCount);
            stepPos = stepsDone = 0;
            stepLeft = stepLength;
            clipNow = 0;
            bentNow = countNow = 0;
            clipDbNow = 0;
        }

        static void Target(ref LinearRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            curve = ClipCurve.Make((ClipShape)values.Choice(P.Shape), values.Value(P.Knee));
            int f = 1 << values.Choice(P.Oversample);
            if (f != factor || force)
            {
                factor = f;
                for (int c = 0; c < 2; c++)
                {
                    foreach (var s in bandStage[c]) { s.Os.SetFactor(factor); s.Adaa.Reset(); }
                    finalStage[c].Os.SetFactor(factor);
                    finalStage[c].Adaa.Reset();
                }
            }
            int b = values.Choice(P.Bands) + 1;
            if (b != bands)
            {
                bands = b;
                split.Reset();
            }
            midSide = values.Choice(P.Stereo) == 1;
            split.SetTarget(values.Value(P.Xo1), values.Value(P.Xo2));
            if (force) split.Settle();
            Target(ref drive, (float)DspMath.DbToGain(values.Value(P.Drive)), force);
            Target(ref ceiling, (float)DspMath.DbToGain(values.Value(P.Ceiling)), force);
            Target(ref mix, values.F(P.Mix), force);
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            Target(ref lowDrive, (float)DspMath.DbToGain(values.Value(P.LowDrive)), force);
            Target(ref midDrive, (float)DspMath.DbToGain(values.Value(P.MidDrive)), force);
            Target(ref highDrive, (float)DspMath.DbToGain(values.Value(P.HighDrive)), force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>
        /// One stage on one stream: <paramref name="x"/> (already divided by the ceiling) up, the curve, down, into
        /// <paramref name="y"/>, padded to the largest factor's latency.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Clip(Stage s, float[] x, float[] y, int n)
        {
            var hi = s.Os.Up(x, 0, n);
            int m = n * factor;
            var c = curve;
            if (factor == 1)
                for (int i = 0; i < m; i++) hi[i] = (float)c.F(hi[i]);
            else
                for (int i = 0; i < m; i++) hi[i] = (float)s.Adaa.Process(in c, hi[i]);
            s.Os.Down(y, 0, n);
            int pad = FirOversampler.MaxLatency - s.Os.Latency;
            if (pad > 0)
                for (int i = 0; i < n; i++)
                {
                    s.Pad.Write(y[i]);
                    y[i] = s.Pad.Read(pad);
                }
        }

        static void Delay(DelayLineF d, float[] x, int n, int by)
        {
            for (int i = 0; i < n; i++)
            {
                d.Write(x[i]);
                x[i] = d.Read(by);
            }
        }

        /// <summary>Processes a stereo block in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            int L1 = FirOversampler.MaxLatency, L2 = 2 * L1, latency = Latency;
            double bendsAbove = curve.BendsAbove;
            float blockClip = 1;

            // Drive (and mid/side); the ceiling now, one stage back and two stages back; the dry input, delayed in full.
            for (int i = 0; i < n; i++)
            {
                float g = drive.Next(), c = ceiling.Next();
                ceilRing.Write(c);
                ceil1[i] = ceilRing.Read(L1);
                ceil2[i] = ceilRing.Read(L2);
                float inv = 1f / c;
                invCeil[i] = inv;
                float x0 = left[i], x1 = right[i];
                dryL[i] = x0;
                dryR[i] = x1;
                dryRing[0].Write(x0);
                dryRing[1].Write(x1);
                dryOutL[i] = dryRing[0].Read(latency);
                dryOutR[i] = dryRing[1].Read(latency);
                float l = x0 * g, r = x1 * g;
                if (midSide)
                {
                    float m = (float)(0.5 * ((double)l + r)), s = (float)(0.5 * ((double)l - r));
                    l = m;
                    r = s;
                }
                inL[i] = l;
                inR[i] = r;
                float al = MathF.Abs(l) * inv, ar = MathF.Abs(r) * inv;
                float over = MathF.Max(al, ar);
                if (over > blockClip) blockClip = over;
                countNow += 2;
                if (al > bendsAbove) bentNow++;
                if (ar > bendsAbove) bentNow++;
            }

            // The first stage: one band, or each band with its drive, clipped and summed.
            if (bands == 1)
            {
                for (int i = 0; i < n; i++)
                {
                    inL[i] *= invCeil[i];
                    inR[i] *= invCeil[i];
                }
                Clip(bandStage[0][0], inL, sumL, n);
                Clip(bandStage[1][0], inR, sumR, n);
            }
            else
            {
                split.Process(inL, inR, n, bands, bandL, bandR);
                Array.Clear(sumL, 0, n);
                Array.Clear(sumR, 0, n);
                for (int b = 0; b < bands; b++)
                {
                    var bl = bandL[b];
                    var br = bandR[b];
                    for (int i = 0; i < n; i++)
                    {
                        float bd = b == 0 ? lowDrive.Next() : b == bands - 1 ? highDrive.Next() : midDrive.Next();
                        float k = bd * invCeil[i];
                        bl[i] *= k;
                        br[i] *= k;
                    }
                    Clip(bandStage[0][b], bl, bl, n);
                    Clip(bandStage[1][b], br, br, n);
                    for (int i = 0; i < n; i++)
                    {
                        sumL[i] += bl[i];
                        sumR[i] += br[i];
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                sumL[i] *= ceil1[i];
                sumR[i] *= ceil1[i];
            }
            if (midSide)
                for (int i = 0; i < n; i++)
                {
                    float m = sumL[i], s = sumR[i];
                    sumL[i] = (float)((double)m + s);
                    sumR[i] = (float)((double)m - s);
                }

            // The second stage (bands or mid/side: left and right clipped again), else the same delay; then the clamp.
            if (bands > 1 || midSide)
            {
                for (int i = 0; i < n; i++)
                {
                    float inv = 1f / ceil1[i];
                    sumL[i] *= inv;
                    sumR[i] *= inv;
                }
                Clip(finalStage[0], sumL, sumL, n);
                Clip(finalStage[1], sumR, sumR, n);
                for (int i = 0; i < n; i++)
                {
                    sumL[i] *= ceil2[i];
                    sumR[i] *= ceil2[i];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    finalStage[0].Pad.Write(sumL[i]);
                    finalStage[1].Pad.Write(sumR[i]);
                    sumL[i] = finalStage[0].Pad.Read(L1);
                    sumR[i] = finalStage[1].Pad.Read(L1);
                }
            }

            // The guard, the mix with the dry input (delayed by the latency), Delta, the output.
            guard.Process(sumL, sumR, ceil2, n);
            if (mix.At(1))
            {
                // Wet only: exactly the guarded signal (dl + 1·(y − dl) isn't y in floats).
                Array.Copy(sumL, left, n);
                Array.Copy(sumR, right, n);
            }
            else
                for (int i = 0; i < n; i++)
                {
                    float mx = mix.Next();
                    float dl = dryOutL[i], dr = dryOutR[i];
                    left[i] = dl + mx * (sumL[i] - dl);
                    right[i] = dr + mx * (sumR[i] - dr);
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

            float clipDb = blockClip > 1 ? (float)DspMath.GainToDb((double)blockClip) : 0;
            clipDbNow = clipDb;
            if (clipDb > clipNow) clipNow = clipDb;
            stepLeft -= n;
            if (stepLeft <= 0)
            {
                stepClip[stepPos] = clipNow;
                stepBent[stepPos] = bentNow;
                stepCount[stepPos] = countNow;
                stepPos = (stepPos + 1) % Steps;
                if (stepsDone < Steps) stepsDone++;
                clipNow = 0;
                bentNow = countNow = 0;
                stepLeft += stepLength;
                PublishReadings();
            }
        }

        // ---- readings (machine_readings), from any thread: values published every 100 ms --------------------------

        double pubClip, pubBent, pubMatch = double.NaN;

        void PublishReadings()
        {
            float clip = 0;
            long bent = 0, count = 0;
            for (int k = 0; k < stepsDone; k++)
            {
                clip = MathF.Max(clip, stepClip[k]);
                bent += stepBent[k];
                count += stepCount[k];
            }
            Volatile.Write(ref pubClip, clip);
            Volatile.Write(ref pubBent, count > 0 ? 100.0 * bent / count : 0);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        /// <summary>The gain-match reading, dB (NaN until 3 s of sound): for the editor's Match.</summary>
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        /// <summary>The readings now (gain_match_db only once it is known: 3 s of sound).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["clip_db"] = Math.Round(Volatile.Read(ref pubClip), 2),
                ["clipped_percent"] = Math.Round(Volatile.Read(ref pubBent), 2),
                ["latency_samples"] = Latency,
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}