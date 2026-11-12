#nullable disable
// Neo Low End (docs/specs/mastering-tools.md §5.5): two domains. The detection domain works on the input as it
// arrives: LrBands' first crossover splits it (a Linkwitz–Riley 24 low-pass at the crossover, 20–300 Hz, and the rest),
// and from the low band one gain per sample is worked out, in this order:
//  1. Punch and Sustain: TransientSplit's weight of the low band (linked over both channels) × 3 within 0 … 1,
//     g = 1 + (a − 1)·t + (b − 1)·(1 − t); exactly 1 at 0 dB and 0 dB.
//  2. Contrast: MicroGain on the low band (fast 10 ms, slow 300 ms, auto gain): the gain in dB is the amount times
//     how far the level is above (+) or below its slow level, so +100 % keeps the strongest parts and pulls the rest.
//  3. Balance: the low band's mono power and the mid band's (the rest, mono, through a 2 kHz Butterworth low-pass)
//     over 1 s, so no feedback; every 32 samples the gain that would move their ratio toward Target by the amount,
//     within ±9 dB, followed by a 0.3 s one-pole.
// The apply domain works on the audio the Lookahead later, split the same way: minimum phase delays the low band, the
// rest and the dry signal by the lookahead (the latency); linear phase takes the FIR's low band, the delayed input
// minus it as the rest (latency D + P: the gain is read the lookahead ahead of the sample it acts on, for free). Only
// the low band is touched, after the gain, in this order:
//  4. Mono: the low band's side through a Linkwitz–Riley 24 high-pass at the mono frequency (so the side below it
//     is gone, −6 dB at the frequency), the mid untouched; switched over 20 ms.
//  5. Peak control: ClipCurve's Knee (6 dB) with first-order ADAA on each channel of the low band alone, at the Peak
//     level; switched over 20 ms. The low band holds little above 300 Hz, so no oversampling is needed.
// The low band and the rest then sum; while every control is at rest the output is the dry signal bit for bit, and
// moving any of them blends over 20 ms. Delta (DeltaListen) hears the output minus the dry input before Output; the
// gain-match reading (GainMatch) compares the output with the input. A phase switch resets the filters and fades the
// output in over 20 ms (set it before playing).
// Allocation-free after construction; the shaping in double with the portable maths.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.LowEnd
{
    internal sealed class LowEndEngine
    {
        public const int MaxBlock = 256;
        /// <summary>The longest stretch the shaping takes at once: a linear-phase partition at 384 kHz.</summary>
        const int MaxChunk = 2048;
        const int MaxLookahead = 4096;       // 10 ms at 384 kHz is 3840
        const double GlideSeconds = 0.02;
        public const double WeightScale = 3.0;
        const int Steps = 30;                // readings: 3 s of 100 ms steps

        struct Lp2
        {
            public double S1, S2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double Tick(double x, double a1, double a2, double a3)
            {
                double v3 = x - S2;
                double v1 = a1 * S1 + a2 * v3;
                double v2 = S2 + a2 * S1 + a3 * v3;
                S1 = 2 * v1 - S1;
                S2 = 2 * v2 - S2;
                return v2;
            }

            /// <summary>The high-pass output of the same section (the low-pass is <see cref="Tick"/>'s), the state moved on.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double TickHigh(double x, double a1, double a2, double a3)
            {
                double v3 = x - S2;
                double v1 = a1 * S1 + a2 * v3;
                double v2 = S2 + a2 * S1 + a3 * v3;
                S1 = 2 * v1 - S1;
                S2 = 2 * v2 - S2;
                return x - Sqrt2 * v1 - v2;
            }

            public void Flush()
            {
                S1 = DspMath.Flush(S1);
                S2 = DspMath.Flush(S2);
            }
        }

        const double Sqrt2 = 1.4142135623730951;

        readonly ParamValues values;
        float sampleRate = 48000;
        int phase;                                  // 0 minimum, 1 linear
        double crossover = 120;

        LrBands bands;
        LowLinear linear;
        int linPos;
        readonly float[] linInL = new float[MaxChunk], linInR = new float[MaxChunk], linOutL = new float[MaxChunk], linOutR = new float[MaxChunk];
        readonly float[][] bandL = { new float[MaxBlock], new float[MaxBlock] }, bandR = { new float[MaxBlock], new float[MaxBlock] };
        readonly float[] inL = new float[MaxBlock], inR = new float[MaxBlock];
        readonly float[] highL = new float[MaxChunk], highR = new float[MaxChunk];
        readonly float[] weight = new float[MaxChunk];
        readonly float[] shapedL = new float[MaxChunk], shapedR = new float[MaxChunk];
        readonly float[] gain = new float[MaxBlock], gainApply = new float[MaxChunk];
        // Minimum phase: the low band, the rest and the input delayed by the lookahead.
        readonly DelayLineF[] lowDelay = { new DelayLineF(MaxLookahead), new DelayLineF(MaxLookahead) }, highDelay = { new DelayLineF(MaxLookahead), new DelayLineF(MaxLookahead) }, dryDelay = { new DelayLineF(MaxLookahead), new DelayLineF(MaxLookahead) };
        readonly float[] aLowL = new float[MaxBlock], aLowR = new float[MaxBlock], aHighL = new float[MaxBlock], aHighR = new float[MaxBlock], aDryL = new float[MaxBlock], aDryR = new float[MaxBlock];
        // Linear phase: the gains of the last D + P + lookahead samples.
        float[] gainRing;
        int gainMask;
        long gainWritten;
        int lookahead;

        readonly TransientSplit split;
        MicroGain contrastGain;
        SmoothRamp punch, sustain, contrast, balance, output, peakLevel;
        LinearRamp live, monoMix, peakMix, fade;
        bool peakOn, monoOn;
        ClipCurve curve = ClipCurve.Make(ClipShape.Knee, 6);
        ClipAdaa adaaL, adaaR;
        double monoA1, monoA2, monoA3, midA1, midA2, midA3;
        Lp2 monoLpL1, monoLpL2, midLp;
        double pLow, pMid, balanceGain = 1, balanceTarget = 1, measureCoef, balanceCoef;
        int counter, monoCount;
        double targetDb;

        public readonly DeltaListen Delta;
        public readonly GainMatch Match;

        // Meters and readings.
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        readonly float[] stepPeakIn = new float[Steps], stepPeakOut = new float[Steps], stepClip = new float[Steps];
        readonly double[] stepPowIn = new double[Steps], stepPowOut = new double[Steps];
        readonly int[] stepCount = new int[Steps];
        int stepPos, stepLeft, stepLength, stepsDone;
        float peakInNow, peakOutNow, clipNow;
        double powInNow, powOutNow;
        int countNow;
        volatile float lowDbNow;
        volatile float balanceGainDbNow;
        double balanceDbNow;

        public LowEndEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen(LowEndDesign.MaxLatency + 2, 48000);
            Match = new GainMatch(48000);
            split = new TransientSplit(48000);
            SetSampleRate(48000);
        }

        /// <summary>The phase mode now: 0 minimum, 1 linear.</summary>
        public int Phase => phase;

        public double Crossover => crossover;

        /// <summary>Samples of delay: the lookahead in minimum phase, the FIR's D + P in linear phase.</summary>
        public int Latency => phase == 1 ? LowEndDesign.LinearLatency(sampleRate) : lookahead;

        /// <summary>The low band's level in the last block, dBFS (peak, after shaping), for the editor.</summary>
        public float LowDb => lowDbNow;

        /// <summary>The gain Balance applies to the low band now, dB.</summary>
        public float BalanceGainDb => balanceGainDbNow;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            punch.Setup(GlideSeconds, sr);
            sustain.Setup(GlideSeconds, sr);
            contrast.Setup(GlideSeconds, sr);
            balance.Setup(GlideSeconds, sr);
            output.Setup(GlideSeconds, sr);
            peakLevel.Setup(GlideSeconds, sr);
            live.Setup(GlideSeconds, sr);
            monoMix.Setup(GlideSeconds, sr);
            peakMix.Setup(GlideSeconds, sr);
            fade.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            measureCoef = 1 - Detector.Coef(1.0, sr);
            balanceCoef = 1 - Detector.Coef(0.3, sr);
            double g = PortableMath.Tan(Math.PI * Math.Min(2000, 0.45 * sr) / sr);
            midA1 = 1 / (1 + g * (g + Sqrt2));
            midA2 = g * midA1;
            midA3 = g * midA2;
            stepLength = Math.Max(1, (int)Math.Round(sr * 0.1));
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            phase = values.Choice(P.Phase);
            crossover = Math.Clamp(values.Value(P.Xo), LowEndDesign.MinHz, LowEndDesign.MaxHz);
            bands = new LrBands(sr, crossover, crossover, crossover);
            linear = new LowLinear(sr, crossover);
            int ring = 1;
            while (ring < LowEndDesign.LinearLatency(sr) + 2 * MaxChunk + MaxLookahead) ring <<= 1;
            gainRing = new float[ring];
            gainMask = ring - 1;
            contrastGain.Setup(0.01, 0.3, sr);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            bands.Reset();
            linear.Reset();
            linPos = 0;
            Array.Fill(gainRing, 1f);
            gainWritten = 0;
            foreach (var d in lowDelay) d.Reset();
            foreach (var d in highDelay) d.Reset();
            foreach (var d in dryDelay) d.Reset();
            Array.Clear(linInL);
            Array.Clear(linInR);
            Array.Clear(linOutL);
            Array.Clear(linOutR);
            split.Reset();
            contrastGain.Reset();
            monoLpL1 = monoLpL2 = midLp = default;
            adaaL = adaaR = default;
            pLow = pMid = 0;
            balanceGain = balanceTarget = 1;
            counter = 0;
            Delta.Reset();
            Match.Reset();
            Array.Clear(stepPeakIn);
            Array.Clear(stepPeakOut);
            Array.Clear(stepClip);
            Array.Clear(stepPowIn);
            Array.Clear(stepPowOut);
            Array.Clear(stepCount);
            stepPos = stepsDone = 0;
            stepLeft = stepLength;
            peakInNow = peakOutNow = clipNow = 0;
            powInNow = powOutNow = 0;
            countNow = 0;
            lowDbNow = -120;
            balanceGainDbNow = 0;
            balanceDbNow = double.NaN;
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

        static readonly double[][] Speeds =
        {
            new[] { 0.1, 10, 10, 100 },
            new[] { 0.3, 20, 20, 150 },
            new[] { 1.0, 30, 50, 300 },
        };

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            int ph = values.Choice(P.Phase);
            if (force) fade.Reset(1);
            else if (ph != phase)
            {
                phase = ph;
                Reset();
                fade.Reset(0);
                fade.SetTarget(1);
            }
            crossover = Math.Clamp(values.Value(P.Xo), LowEndDesign.MinHz, LowEndDesign.MaxHz);
            bands.SetTarget(crossover, crossover, crossover);
            if (force) bands.Settle();
            var sp = Speeds[values.Choice(P.Speed)];
            split.Set(sp[0], sp[1], sp[2], sp[3], 0.5);
            lookahead = Math.Min(MaxLookahead - 4, (int)Math.Round(values.Value(P.Lookahead) * 0.001 * sampleRate));
            Delta.Latency = Latency;
            double punchDb = values.Value(P.Punch), sustainDb = values.Value(P.Sustain), contrastAmt = values.Value(P.Contrast), bal = values.Value(P.Balance);
            Target(ref punch, (float)DspMath.DbToGain(punchDb), force);
            Target(ref sustain, (float)DspMath.DbToGain(sustainDb), force);
            Target(ref contrast, (float)contrastAmt, force);
            Target(ref balance, (float)bal, force);
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            Target(ref peakLevel, (float)DspMath.DbToGain(values.Value(P.PeakLevel)), force);
            targetDb = values.Value(P.Target);
            peakOn = values.On(P.Peak);
            monoOn = values.On(P.Mono);
            Target(ref peakMix, peakOn ? 1 : 0, force);
            Target(ref monoMix, monoOn ? 1 : 0, force);
            double mg = PortableMath.Tan(Math.PI * Math.Clamp(values.Value(P.MonoFreq), 20, 0.45 * sampleRate) / sampleRate);
            monoA1 = 1 / (1 + mg * (mg + Sqrt2));
            monoA2 = mg * monoA1;
            monoA3 = mg * monoA2;
            bool rest = punchDb == 0 && sustainDb == 0 && contrastAmt == 0 && bal == 0 && !peakOn && !monoOn;
            Target(ref live, rest ? 0 : 1, force);
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
            for (int i = 0; i < n; i++)
            {
                float x0 = left[i], x1 = right[i];
                inL[i] = float.IsFinite(x0) ? x0 : 0f;
                inR[i] = float.IsFinite(x1) ? x1 : 0f;
            }
            InMeter.Feed(inL, inR, n);

            bands.Process(inL, inR, n, 2, bandL, bandR);
            Gains(bandL[0], bandR[0], bandL[1], bandR[1], n);
            if (phase == 0)
            {
                int la = lookahead;
                for (int i = 0; i < n; i++)
                {
                    lowDelay[0].Write(bandL[0][i]);
                    lowDelay[1].Write(bandR[0][i]);
                    highDelay[0].Write(bandL[1][i]);
                    highDelay[1].Write(bandR[1][i]);
                    dryDelay[0].Write(inL[i]);
                    dryDelay[1].Write(inR[i]);
                    aLowL[i] = lowDelay[0].Read(la);
                    aLowR[i] = lowDelay[1].Read(la);
                    aHighL[i] = highDelay[0].Read(la);
                    aHighR[i] = highDelay[1].Read(la);
                    aDryL[i] = dryDelay[0].Read(la);
                    aDryR[i] = dryDelay[1].Read(la);
                }
                Apply(aLowL, aLowR, aHighL, aHighR, aDryL, aDryR, gain, n, left, right);
            }
            else
            {
                int p = linear.P;
                for (int i = 0; i < n; i++)
                {
                    gainRing[(int)(gainWritten++ & gainMask)] = gain[i];
                    linInL[linPos] = inL[i];
                    linInR[linPos] = inR[i];
                    left[i] = linOutL[linPos];
                    right[i] = linOutR[linPos];
                    if (++linPos == p)
                    {
                        linPos = 0;
                        Partition();
                    }
                    else if ((linPos & (LowLinear.DesignStride - 1)) == 0) linear.DesignStep();
                }
            }

            if (!fade.At(1))
                for (int i = 0; i < n; i++)
                {
                    float f = fade.Next();
                    left[i] *= f;
                    right[i] *= f;
                }
            Delta.Process(inL, inR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(inL, inR, left, right, 0, n);
            OutMeter.Feed(left, right, n);

            stepLeft -= n;
            if (stepLeft <= 0)
            {
                stepPeakIn[stepPos] = peakInNow;
                stepPeakOut[stepPos] = peakOutNow;
                stepClip[stepPos] = clipNow;
                stepPowIn[stepPos] = powInNow;
                stepPowOut[stepPos] = powOutNow;
                stepCount[stepPos] = countNow;
                stepPos = (stepPos + 1) % Steps;
                if (stepsDone < Steps) stepsDone++;
                peakInNow = peakOutNow = clipNow = 0;
                powInNow = powOutNow = 0;
                countNow = 0;
                stepLeft += stepLength;
                PublishReadings();
            }
        }

        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Partition()
        {
            int p = linear.P;
            linear.Process(linInL, linInR, crossover);
            var lowL = linear.LowL;
            var lowR = linear.LowR;
            var dl = linear.DryL;
            var dr = linear.DryR;
            long first = gainWritten - p - linear.D + lookahead;
            for (int i = 0; i < p; i++)
            {
                highL[i] = dl[i] - lowL[i];
                highR[i] = dr[i] - lowR[i];
                long at = first + i;
                gainApply[i] = at >= 0 ? gainRing[(int)(at & gainMask)] : 1f;
            }
            Apply(lowL, lowR, highL, highR, dl, dr, gainApply, p, linOutL, linOutR);
        }

        /// <summary>
        /// The detection domain: one gain per sample from the low band as it arrives (<paramref name="lowL"/>, <paramref name="lowR"/>)
        /// and the rest (<paramref name="hiL"/>, <paramref name="hiR"/>) for Balance's mid measurement, into <see cref="gain"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Gains(float[] lowL, float[] lowR, float[] hiL, float[] hiR, int n)
        {
            split.Process(lowL, lowR, 0, n, weight);
            float peakInBlock = 0;
            double powIn = 0;
            for (int i = 0; i < n; i++)
            {
                double l = lowL[i], r = lowR[i];

                // Balance's measurement: the low band's mono power and the mid band's, over 1 s.
                double ml = 0.5 * (l + r);
                double md = midLp.Tick(0.5 * ((double)hiL[i] + hiR[i]), midA1, midA2, midA3);
                pLow += (ml * ml - pLow) * measureCoef;
                pMid += (md * md - pMid) * measureCoef;
                double balAmt = balance.Next();
                if ((++counter & 31) == 0)
                {
                    if ((counter & 63) == 32)
                    {
                        pLow = DspMath.Flush(pLow);
                        pMid = DspMath.Flush(pMid);
                        midLp.Flush();
                    }
                    double want = 1;
                    if (pLow > 1e-12 && pMid > 1e-12)
                    {
                        double ratioDb = 10 * PortableMath.Log10(pLow / pMid);
                        balanceDbNow = ratioDb;
                        if (balAmt > 0)
                        {
                            double c = Math.Clamp(targetDb - ratioDb, -9, 9) * balAmt;
                            want = DspMath.DbToGain(c);
                        }
                    }
                    balanceTarget = want;
                }
                if (balanceGain != balanceTarget)
                {
                    balanceGain += (balanceTarget - balanceGain) * balanceCoef;
                    if (Math.Abs(balanceTarget - balanceGain) < 1e-9) balanceGain = balanceTarget;
                }

                // Punch and Sustain, Contrast, Balance: one gain on the low band.
                double t = weight[i] * WeightScale;
                if (t > 1) t = 1;
                double g = TransientSplit.Gain(punch.Next(), sustain.Next(), t);
                g *= contrastGain.Next(Math.Max(l * l, r * r), contrast.Next(), true);
                g *= balanceGain;
                gain[i] = (float)g;

                float al = MathF.Max(MathF.Abs(lowL[i]), MathF.Abs(lowR[i]));
                if (al > peakInBlock) peakInBlock = al;
                powIn += l * l + r * r;
            }
            if (peakInBlock > peakInNow) peakInNow = peakInBlock;
            powInNow += powIn;
            countNow += 2 * n;
            balanceGainDbNow = (float)DspMath.GainToDb(balanceGain);
        }

        /// <summary>
        /// The apply domain: the low band (aligned with the rest and the dry signal) × the gain, mono and peak control, added to
        /// the rest, blended with the dry signal by the rest state: <paramref name="n"/> samples into <paramref name="outL"/>/<paramref name="outR"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Apply(float[] lowL, float[] lowR, float[] hiL, float[] hiR, float[] dryL, float[] dryR, float[] gains, int n, float[] outL, float[] outR)
        {
            bool peakActive = !peakMix.At(0);
            if (!peakActive) { adaaL = default; adaaR = default; }
            bool monoActive = !monoMix.At(0);
            if (!monoActive) { monoLpL1 = default; monoLpL2 = default; }
            float peakOutBlock = 0, clipBlock = 0;
            double powOut = 0;
            for (int i = 0; i < n; i++)
            {
                double g = gains[i];
                double ol = lowL[i] * g, or = lowR[i] * g;

                // Mono below the frequency: the side without its own low-pass.
                if (monoActive)
                {
                    double mm = monoMix.Next();
                    double mid = 0.5 * (ol + or), side = 0.5 * (ol - or);
                    double hp = monoLpL2.TickHigh(monoLpL1.TickHigh(side, monoA1, monoA2, monoA3), monoA1, monoA2, monoA3);
                    side += mm * (hp - side);
                    ol = mid + side;
                    or = mid - side;
                    if ((++monoCount & 63) == 0) { monoLpL1.Flush(); monoLpL2.Flush(); }
                }

                // Peak control: the soft clipper on the low band alone.
                if (peakActive)
                {
                    double cg = peakLevel.Next();
                    double pm = peakMix.Next();
                    float over = (float)(Math.Max(Math.Abs(ol), Math.Abs(or)) / cg);
                    if (over > clipBlock) clipBlock = over;
                    double yl = cg * adaaL.Process(curve, ol / cg), yr = cg * adaaR.Process(curve, or / cg);
                    ol += pm * (yl - ol);
                    or += pm * (yr - or);
                }

                float fl = (float)ol, fr = (float)or;
                float ao = MathF.Max(MathF.Abs(fl), MathF.Abs(fr));
                if (ao > peakOutBlock) peakOutBlock = ao;
                powOut += (double)fl * fl + (double)fr * fr;
                shapedL[i] = fl + hiL[i];
                shapedR[i] = fr + hiR[i];
            }

            // Blend with the dry signal: at rest the dry signal exactly.
            if (live.At(0))
            {
                Array.Copy(dryL, outL, n);
                Array.Copy(dryR, outR, n);
                for (int i = 0; i < n; i++) live.Next();
            }
            else
                for (int i = 0; i < n; i++)
                {
                    float u = live.Next();
                    outL[i] = dryL[i] + u * (shapedL[i] - dryL[i]);
                    outR[i] = dryR[i] + u * (shapedR[i] - dryR[i]);
                }

            lowDbNow = (float)DspMath.GainToDb((double)peakOutBlock);
            if (peakOutBlock > peakOutNow) peakOutNow = peakOutBlock;
            if (clipBlock > clipNow) clipNow = clipBlock;
            powOutNow += powOut;
        }

        // ---- readings (machine_readings), from any thread: values published every 100 ms --------------------------

        double pubCrestIn, pubCrestOut, pubBalance = double.NaN, pubBalanceGain, pubClip, pubMatch = double.NaN;

        static double Crest(float peak, double power, long count) =>
            count > 0 && power > 1e-20 && peak > 0 ? 20 * Math.Log10(peak / Math.Sqrt(power / count)) : double.NaN;

        void PublishReadings()
        {
            float pIn = 0, pOut = 0, clip = 0;
            double sIn = 0, sOut = 0;
            long count = 0;
            for (int k = 0; k < stepsDone; k++)
            {
                pIn = MathF.Max(pIn, stepPeakIn[k]);
                pOut = MathF.Max(pOut, stepPeakOut[k]);
                clip = MathF.Max(clip, stepClip[k]);
                sIn += stepPowIn[k];
                sOut += stepPowOut[k];
                count += stepCount[k];
            }
            Volatile.Write(ref pubCrestIn, Crest(pIn, sIn, count));
            Volatile.Write(ref pubCrestOut, Crest(pOut, sOut, count));
            Volatile.Write(ref pubBalance, balanceDbNow);
            Volatile.Write(ref pubBalanceGain, balanceGainDbNow);
            Volatile.Write(ref pubClip, clip > 1 ? DspMath.GainToDb((double)clip) : 0);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        /// <summary>The gain-match reading, dB (NaN until 3 s of sound): for the editor's Match.</summary>
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        /// <summary>The low band's crest going in and out over the last 3 s, dB (NaN while the band is silent).</summary>
        public double CrestInDb => Volatile.Read(ref pubCrestIn);
        public double CrestOutDb => Volatile.Read(ref pubCrestOut);

        /// <summary>The readings now (the ones that are known: crest and balance once the low band has sound, gain_match_db after 3 s).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["balance_gain_db"] = Math.Round(Volatile.Read(ref pubBalanceGain), 2),
                ["peak_clip_db"] = Math.Round(Volatile.Read(ref pubClip), 2),
                ["latency_samples"] = Latency,
            };
            double ci = CrestInDb, co = CrestOutDb, b = Volatile.Read(ref pubBalance), m = GainMatchDb;
            if (double.IsFinite(ci)) d["low_crest_in_db"] = Math.Round(ci, 2);
            if (double.IsFinite(co)) d["low_crest_out_db"] = Math.Round(co, 2);
            if (double.IsFinite(b)) d["balance_db"] = Math.Round(b, 2);
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
