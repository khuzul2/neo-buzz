#nullable disable
// Neo Multiband (docs/specs/machines.md §5): a multiband compressor and upward expander.
//  - The crossovers split the input into 3 or 4 bands (LinearSplit: linear phase, D + P samples of
//    latency, reported to ReBuzz; MinimumSplit: minimum phase, none). The bands sum to the dry signal:
//    in linear phase the input delayed by the latency, in minimum phase the input through the
//    crossovers' allpasses (so Mix never combs).
//  - Each band has Neo Comp's detector and gain computer (NeoBuzz.Dsp Dynamics.cs): the peak level in
//    dB, the stereo link (left and right, or mid and side), the static curve with its soft knee (a
//    ratio below 1:1 gives a negative slope: upward expansion above the threshold), clamped to ±Range,
//    then attack and release in dB (attack while the gain change grows, either way), makeup, solo and
//    bypass.
//  - The output is the dry signal plus each band's change, (factor − 1) · band, times Mix, then Output.
//    A band whose factor is exactly 1 adds nothing, so with every band at 1:1 and 0 dB the output is
//    the dry signal bit for bit, and at Mix 0 % too.
//  - Three bands are the four-band split with bands 3 and 4 merged (H1·H2·(L3 + H3) = H1·H2): band 4's
//    factor follows band 3's, and band 3's detector hears both; switching the count crossfades that
//    over 10 ms. Phase changes fade the output out over 10 ms, switch and fade back in; the latency
//    changes at the switch and ReBuzz realigns its other paths.
// No allocation after SetSampleRate. Parameters latch at block starts; the linear path applies them
// at its partition boundaries and designs its FIRs in steps at every 16th sample of a partition
// (LinearSplit.DesignStep), which fall on fixed samples, so the output doesn't depend on how the
// host splits it or how fast the machine runs.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Multiband
{
    internal sealed class MultibandEngine
    {
        public const int MaxBlock = 256;
        public const int Bands = MultibandDesign.MaxBands;
        public const int ScopeLength = 4096;
        const double MakeupSeconds = 0.02, SwitchSeconds = 0.01, MixSeconds = 0.02, FadeSeconds = 0.01, TimeGlideSeconds = 0.02;
        const int Segment = 16;
        const double Ln10Over20 = 0.11512925464970229;
        /// <summary>The readings' window: 30 steps of 100 ms.</summary>
        const int Steps = 30;

        sealed class Band
        {
            public GainComputer Gc;
            public double Range, AttackA, ReleaseA;
            // Attack and release glide in log time (a jump from 200 ms to 0.05 ms would let a lagging gain snap).
            public double AttackLog, AttackTarget, ReleaseLog, ReleaseTarget;
            public bool Expands;
            public double G0, G1;
            public LinearRamp Makeup, Solo, Bypass;
            // This block's, for the meters; this step's, for the readings.
            public double BlockMax, BlockMin, BlockLevel;
            public double StepMax, StepMin, StepLevel;
            public readonly double[] RingMax = new double[Steps], RingMin = new double[Steps], RingLevel = new double[Steps];
            public volatile float Reduction, Boost, Level;
        }

        sealed class Snapshot
        {
            public readonly double[] Now = new double[Bands], Peak = new double[Bands], LevelDb = new double[Bands];
            public int Count, Latency;
            public bool Any;

            public void CopyTo(Snapshot o)
            {
                Array.Copy(Now, o.Now, Bands);
                Array.Copy(Peak, o.Peak, Bands);
                Array.Copy(LevelDb, o.LevelDb, Bands);
                o.Count = Count;
                o.Latency = Latency;
                o.Any = Any;
            }
        }

        readonly ParamValues values;
        float sampleRate = 48000;
        LinearSplit linear;
        MinimumSplit minimum;
        PhaseMode phase, wanted;
        int bandCount = 4;
        StereoMode stereo;
        float link = 1;
        Xover target;
        readonly Band[] band = { new Band(), new Band(), new Band(), new Band() };
        LinearRamp mix, output, merge;

        // The linear path's partition: its input as it arrives, its output as it leaves.
        float[] inL, inR, outL, outR;
        int pos;
        // The dynamics' working arrays (a partition or a block).
        double[][] bandL, bandR;
        double[] dryL, dryR;
        readonly float[] tmpL = new float[MaxBlock], tmpR = new float[MaxBlock];
        readonly double[] f0 = new double[Bands], f1 = new double[Bands], s0 = new double[Bands], s1 = new double[Bands];

        // The Phase switch: a fade out, the switch, a hold while the new path fills (linear: its latency), a fade in.
        float fade = 1, fadeStep;
        int fadeLeft, fadeHold;
        bool fadingOut;

        long quiet;
        int stepLength, stepFill, ringAt, segmentLeft;
        double timeGlide;
        readonly TripleBuffer<Snapshot> snapshots = new TripleBuffer<Snapshot>(() => new Snapshot(), (a, b) => a.CopyTo(b));
        readonly float[] scope = new float[ScopeLength];
        volatile int scopePos;

        public MultibandEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        public float SampleRate => sampleRate;

        /// <summary>Samples ReBuzz compensates: linear phase's D + P (4607 at 44.1 and 48 kHz), 0 in minimum phase.</summary>
        public int Latency => phase == PhaseMode.Linear ? linear.Latency : 0;

        /// <summary>The phase the engine runs now (Phase, once a switch has faded through).</summary>
        public PhaseMode Phase => phase;

        public int BandCount => bandCount;

        /// <summary>Band <paramref name="b"/>'s gain reduction now (dB, held and falling): positive cuts, negative boosts (expansion).</summary>
        public float GainChangeDb(int b) => band[b].Reduction >= band[b].Boost ? band[b].Reduction : -band[b].Boost;

        /// <summary>Band <paramref name="b"/>'s reduction (≥ 0 dB) and boost (≥ 0 dB) now, held and falling, for the meters.</summary>
        public float ReductionDb(int b) => band[b].Reduction;
        public float BoostDb(int b) => band[b].Boost;

        /// <summary>Band <paramref name="b"/>'s peak level (dBFS, as its detector reads it), held and falling.</summary>
        public float LevelDb(int b) => band[b].Level;

        /// <summary>The linear split (tests).</summary>
        internal LinearSplit Linear => linear;

        /// <summary>The crossovers the bands are split at now (the linear FIRs' design, or the minimum sections' glide).</summary>
        public Xover Crossovers => phase == PhaseMode.Linear ? linear.Designed : minimum.Current;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            var v = values;
            target = MultibandDesign.Effective(v, sr);
            linear = new LinearSplit(sr, target);
            minimum = new MinimumSplit(sr, target);
            int p = linear.P;
            inL = new float[p];
            inR = new float[p];
            outL = new float[p];
            outR = new float[p];
            int work = Math.Max(p, MaxBlock);
            bandL = new double[Bands][];
            bandR = new double[Bands][];
            for (int b = 0; b < Bands; b++)
            {
                bandL[b] = new double[work];
                bandR[b] = new double[work];
            }
            dryL = new double[work];
            dryR = new double[work];
            foreach (var b in band)
            {
                b.Makeup.Setup(MakeupSeconds, sr);
                b.Solo.Setup(SwitchSeconds, sr);
                b.Bypass.Setup(SwitchSeconds, sr);
            }
            mix.Setup(MixSeconds, sr);
            output.Setup(MakeupSeconds, sr);
            merge.Setup(SwitchSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            stepLength = Math.Max(1, (int)Math.Round(sr / 10));
            timeGlide = 1 - PortableMath.Exp(-Segment / (TimeGlideSeconds * sr));
            phase = wanted = (PhaseMode)v.Choice(P.Phase);
            Update(true);
            Reset();
        }

        public void Reset()
        {
            linear.Reset();
            minimum.Reset();
            Array.Clear(inL);
            Array.Clear(inR);
            Array.Clear(outL);
            Array.Clear(outR);
            pos = 0;
            foreach (var b in band)
            {
                b.G0 = b.G1 = 0;
                b.Reduction = b.Boost = 0;
                b.Level = -120;
                Array.Fill(b.RingMax, 0.0);
                Array.Fill(b.RingMin, 0.0);
                Array.Fill(b.RingLevel, double.NegativeInfinity);
                b.StepMax = b.StepMin = 0;
                b.StepLevel = double.NegativeInfinity;
            }
            stepFill = ringAt = segmentLeft = 0;
            fade = 1;
            fadeLeft = fadeHold = 0;
            fadingOut = false;
            quiet = 0;
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var v = values;
            wanted = (PhaseMode)v.Choice(P.Phase);
            bandCount = v.Choice(P.Bands) == 0 ? 3 : 4;
            stereo = (StereoMode)v.Choice(P.Stereo);
            link = v.F(P.Link);
            target = MultibandDesign.Effective(v, sampleRate);
            minimum.SetTarget(target);
            if (force) minimum.Settle();
            bool anySolo = false;
            for (int b = 0; b < bandCount; b++) anySolo |= v.On(Param(b, BandParam.Solo));
            for (int b = 0; b < Bands; b++)
            {
                var s = band[b];
                double ratio = MultibandDesign.Ratio(v.Value(Param(b, BandParam.Ratio)));
                s.Gc = GainComputer.Make(v.Value(Param(b, BandParam.Threshold)), ratio, v.Value(Param(b, BandParam.Knee)));
                s.Expands = s.Gc.SlopeFf < 0;
                s.Range = v.Value(Param(b, BandParam.Range));
                s.AttackTarget = PortableMath.Log(v.Value(Param(b, BandParam.Attack)));
                s.ReleaseTarget = PortableMath.Log(v.Value(Param(b, BandParam.Release)));
                s.Makeup.SetTarget((float)v.Value(Param(b, BandParam.Makeup)));
                s.Bypass.SetTarget(v.On(Param(b, BandParam.Bypass)) ? 1f : 0f);
                int own = b < bandCount ? b : bandCount - 1;
                s.Solo.SetTarget(!anySolo || v.On(Param(own, BandParam.Solo)) ? 1f : 0f);
                if (force)
                {
                    s.AttackLog = s.AttackTarget;
                    s.ReleaseLog = s.ReleaseTarget;
                    s.AttackA = Detector.Coef(v.Value(Param(b, BandParam.Attack)), sampleRate);
                    s.ReleaseA = Detector.Coef(v.Value(Param(b, BandParam.Release)), sampleRate);
                    s.Makeup.Reset(s.Makeup.Target);
                    s.Bypass.Reset(s.Bypass.Target);
                    s.Solo.Reset(s.Solo.Target);
                }
            }
            mix.SetTarget(anySolo ? 1f : v.F(P.Mix));
            output.SetTarget((float)DspMath.DbToGain(v.Value(P.Output)));
            merge.SetTarget(bandCount == 4 ? 1f : 0f);
            if (force)
            {
                mix.Reset(mix.Target);
                output.Reset(output.Target);
                merge.Reset(merge.Target);
            }
            v.Clean();
        }

        internal enum BandParam { Threshold, Ratio, Attack, Release, Knee, Range, Makeup, Solo, Bypass }

        /// <summary>Band <paramref name="b"/>'s parameter: nine per band, from B1 threshold on.</summary>
        internal static int Param(int b, BandParam what) => P.B1Threshold + 9 * b + (int)what;

        /// <summary>Processes a stereo block in place (n ≤ MaxBlock).</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            bool silent = true;
            for (int i = 0; i < n && silent; i++) silent = left[i] == 0 && right[i] == 0;
            quiet = silent ? quiet + n : 0;
            InMeter.Feed(left, right, n);
            int sp = scopePos;
            for (int i = 0; i < n; i++)
            {
                scope[sp] = 0.5f * (left[i] + right[i]);
                sp = (sp + 1) & (ScopeLength - 1);
            }
            scopePos = sp;

            int fadeLength = Math.Max(1, (int)Math.Round(FadeSeconds * sampleRate));
            if (wanted != phase && !fadingOut)
            {
                fadingOut = true;
                fadeHold = 0;
                fadeLeft = fadeLength;
                fadeStep = -fade / fadeLeft;
            }
            else if (wanted == phase && fadingOut)
            {
                // Changed back before the switch: fade back in from here.
                fadingOut = false;
                fadeLeft = Math.Max(1, (int)Math.Round(fadeLength * (1 - fade)));
                fadeStep = (1 - fade) / fadeLeft;
            }

            int done = 0;
            while (done < n)
            {
                int count = n - done;
                if (fadingOut) count = Math.Min(count, Math.Max(1, fadeLeft));
                if (phase == PhaseMode.Linear) LinearBlock(left, right, done, count);
                else MinimumBlock(left, right, done, count);
                for (int i = done; i < done + count; i++)
                {
                    float g = output.Next();
                    if (fadeHold > 0)
                    {
                        if (--fadeHold == 0)
                        {
                            fadeLeft = fadeLength;
                            fadeStep = 1f / fadeLength;
                        }
                    }
                    else if (fadeLeft > 0)
                    {
                        fadeLeft--;
                        fade = fadeLeft == 0 ? (fadingOut ? 0f : 1f) : fade + fadeStep;
                    }
                    g *= fade;
                    left[i] *= g;
                    right[i] *= g;
                }
                done += count;
                if (fadingOut && fadeLeft == 0)
                {
                    // Silent now: switch, start the other path afresh, and fade in.
                    phase = wanted;
                    linear.Reset();
                    minimum.Reset();
                    Array.Clear(inL);
                    Array.Clear(inR);
                    Array.Clear(outL);
                    Array.Clear(outR);
                    pos = 0;
                    foreach (var b in band) b.G0 = b.G1 = 0;
                    fadingOut = false;
                    fade = 0;
                    if (phase == PhaseMode.Linear)
                    {
                        // Silent until the first input reaches the output, then the fade in.
                        fadeHold = linear.Latency;
                        fadeLeft = 0;
                    }
                    else
                    {
                        fadeLeft = fadeLength;
                        fadeStep = 1f / fadeLength;
                    }
                }
            }
            OutMeter.Feed(left, right, n);
            Meters(n);
        }

        static float Clean(float x) => float.IsFinite(x) ? x : 0f;

        void LinearBlock(float[] left, float[] right, int offset, int count)
        {
            int p = linear.P;
            for (int i = offset; i < offset + count; i++)
            {
                inL[pos] = Clean(left[i]);
                inR[pos] = Clean(right[i]);
                left[i] = outL[pos];
                right[i] = outR[pos];
                if (++pos == p)
                {
                    pos = 0;
                    Partition();
                }
                else if ((pos & (LinearSplit.DesignStride - 1)) == 0) linear.DesignStep();
            }
        }

        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Partition()
        {
            int p = linear.P;
            // Band 3's FIR runs with four bands, and while the merge into three is still fading.
            bool third = bandCount == 4 || !merge.At(0f);
            linear.Process(inL, inR, third, target);
            for (int b = 0; b < Bands; b++)
            {
                var sl = linear.BandL[b];
                var sr = linear.BandR[b];
                var dl = bandL[b];
                var dr = bandR[b];
                for (int i = 0; i < p; i++)
                {
                    dl[i] = sl[i];
                    dr[i] = sr[i];
                }
            }
            for (int i = 0; i < p; i++)
            {
                dryL[i] = linear.DryL[i];
                dryR[i] = linear.DryR[i];
            }
            Dynamics(p, outL, outR, 0);
        }

        void MinimumBlock(float[] left, float[] right, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                tmpL[i] = Clean(left[offset + i]);
                tmpR[i] = Clean(right[offset + i]);
            }
            minimum.Process(tmpL, tmpR, count, bandL, bandR);
            for (int i = 0; i < count; i++)
            {
                dryL[i] = bandL[0][i] + bandL[1][i] + bandL[2][i] + bandL[3][i];
                dryR[i] = bandR[0][i] + bandR[1][i] + bandR[2][i] + bandR[3][i];
            }
            Dynamics(count, left, right, offset);
        }

        /// <summary>One step of a band's gain change (dB) towards its curve's, with attack while the change grows either way.</summary>
        static double Step(Band s, double g, double level)
        {
            double t = s.Gc.Ff(level);
            if (t > s.Range) t = s.Range;
            else if (t < -s.Range) t = -s.Range;
            return s.Expands ? -Detector.Smooth(-g, -t, s.AttackA, s.ReleaseA) : Detector.Smooth(g, t, s.AttackA, s.ReleaseA);
        }

        static double Factor(double g, float makeup, float solo, float bypass)
        {
            double e = makeup - g;
            double gain = e == 0 ? 1 : PortableMath.Exp(e * Ln10Over20);
            return solo * (gain + bypass * (1 - gain));
        }

        /// <summary>The bands' dynamics over <paramref name="n"/> samples of bandL/bandR and dryL/dryR, into <paramref name="yl"/>, <paramref name="yr"/> from <paramref name="at"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Dynamics(int n, float[] yl, float[] yr, int at)
        {
            bool ms = stereo == StereoMode.MidSide;
            bool linked = link >= 1f;
            float lk = link;
            foreach (var s in band)
            {
                s.BlockMax = 0;
                s.BlockMin = 0;
                s.BlockLevel = double.NegativeInfinity;
            }
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    segmentLeft = Segment;
                    foreach (var s in band)
                    {
                        if (s.AttackLog != s.AttackTarget)
                        {
                            double d = s.AttackTarget - s.AttackLog;
                            s.AttackLog = Math.Abs(d) < 1e-6 ? s.AttackTarget : s.AttackLog + d * timeGlide;
                            s.AttackA = Detector.Coef(PortableMath.Exp(s.AttackLog), sampleRate);
                        }
                        if (s.ReleaseLog != s.ReleaseTarget)
                        {
                            double d = s.ReleaseTarget - s.ReleaseLog;
                            s.ReleaseLog = Math.Abs(d) < 1e-6 ? s.ReleaseTarget : s.ReleaseLog + d * timeGlide;
                            s.ReleaseA = Detector.Coef(PortableMath.Exp(s.ReleaseLog), sampleRate);
                        }
                    }
                }
                segmentLeft--;
                float u = merge.Next();
                float m = mix.Next();
                for (int b = 0; b < Bands; b++)
                {
                    var s = band[b];
                    double l = bandL[b][i], r = bandR[b][i];
                    double xl = l, xr = r;
                    if (b == 2 && u < 1)
                    {
                        // Three bands: band 3's detector hears band 4 too.
                        xl += (1 - u) * bandL[3][i];
                        xr += (1 - u) * bandR[3][i];
                    }
                    double a0 = l, a1 = r, x0 = xl, x1 = xr;
                    if (ms)
                    {
                        a0 = 0.5 * (l + r);
                        a1 = 0.5 * (l - r);
                        x0 = 0.5 * (xl + xr);
                        x1 = 0.5 * (xl - xr);
                    }
                    double lv0, lv1;
                    if (linked) lv0 = lv1 = Detector.PeakDb(Math.Max(Math.Abs(x0), Math.Abs(x1)));
                    else
                    {
                        lv0 = Detector.PeakDb(x0);
                        lv1 = Detector.PeakDb(x1);
                        if (lk > 0) Detector.Link(ref lv0, ref lv1, lk);
                    }
                    s.G0 = Step(s, s.G0, lv0);
                    s.G1 = linked ? s.G0 : Step(s, s.G1, lv1);
                    float mk = s.Makeup.Next(), solo = s.Solo.Next(), byp = s.Bypass.Next();
                    f0[b] = Factor(s.G0, mk, solo, byp);
                    f1[b] = linked ? f0[b] : Factor(s.G1, mk, solo, byp);
                    s0[b] = a0;
                    s1[b] = a1;
                    double gmax = Math.Max(s.G0, s.G1), gmin = Math.Min(s.G0, s.G1), lv = Math.Max(lv0, lv1);
                    if (gmax > s.BlockMax) s.BlockMax = gmax;
                    if (gmin < s.BlockMin) s.BlockMin = gmin;
                    if (lv > s.BlockLevel) s.BlockLevel = lv;
                    if (gmax > s.StepMax) s.StepMax = gmax;
                    if (gmin < s.StepMin) s.StepMin = gmin;
                    if (lv > s.StepLevel) s.StepLevel = lv;
                }
                if (u < 1)
                {
                    // Three bands: band 4 takes band 3's factor.
                    f0[3] = f0[2] + u * (f0[3] - f0[2]);
                    f1[3] = f1[2] + u * (f1[3] - f1[2]);
                }
                double wl = 0, wr = 0;
                for (int b = 0; b < Bands; b++)
                {
                    double d0 = (f0[b] - 1) * s0[b], d1 = (f1[b] - 1) * s1[b];
                    if (ms)
                    {
                        wl += d0 + d1;
                        wr += d0 - d1;
                    }
                    else
                    {
                        wl += d0;
                        wr += d1;
                    }
                }
                double dl = dryL[i], dr = dryR[i];
                yl[at + i] = wl == 0 ? (float)dl : (float)(dl + m * wl);
                yr[at + i] = wr == 0 ? (float)dr : (float)(dr + m * wr);
                if (++stepFill == stepLength) PushStep();
            }
            foreach (var s in band)
            {
                s.G0 = DspMath.Flush(s.G0);
                s.G1 = DspMath.Flush(s.G1);
            }
        }

        /// <summary>A 100 ms step of the readings: this step's most reduction, boost and level into the window, and a snapshot.</summary>
        void PushStep()
        {
            stepFill = 0;
            var snap = snapshots.Back;
            for (int b = 0; b < Bands; b++)
            {
                var s = band[b];
                s.RingMax[ringAt] = s.StepMax;
                s.RingMin[ringAt] = s.StepMin;
                s.RingLevel[ringAt] = s.StepLevel;
                s.StepMax = s.StepMin = 0;
                s.StepLevel = double.NegativeInfinity;
                double most = 0, boost = 0, level = double.NegativeInfinity;
                for (int k = 0; k < Steps; k++)
                {
                    most = Math.Max(most, s.RingMax[k]);
                    boost = Math.Min(boost, s.RingMin[k]);
                    level = Math.Max(level, s.RingLevel[k]);
                }
                snap.Now[b] = GainChangeDb(b);
                snap.Peak[b] = most >= -boost ? most : boost;
                snap.LevelDb[b] = level;
            }
            ringAt = (ringAt + 1) % Steps;
            snap.Count = bandCount;
            snap.Latency = Latency;
            snap.Any = true;
            snapshots.Publish();
        }

        void Meters(int n)
        {
            float fall = PortableMathF.Pow(10f, -1.5f * n / sampleRate);
            float levelFall = 20f * n / sampleRate;
            foreach (var s in band)
            {
                s.Reduction = (float)Math.Max(s.BlockMax, s.Reduction * fall);
                s.Boost = (float)Math.Max(-s.BlockMin, s.Boost * fall);
                float lv = (float)Math.Max(-120, s.BlockLevel);
                s.Level = Math.Max(lv, s.Level - levelFall);
            }
        }

        /// <summary>
        /// Readings for agents (machine_readings, docs/specs/mcp-server.md §3.5), from any thread: per band its
        /// gain change now, its largest over the last 3 s (dB; positive cuts, negative boosts) and its peak
        /// level over the last 3 s (dBFS); the bands and the latency in samples.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var reading = new Snapshot();
            snapshots.Read(reading);
            var d = new Dictionary<string, double>();
            int count = reading.Any ? reading.Count : bandCount;
            for (int b = 0; b < count; b++)
            {
                d[$"b{b + 1}_gr_db"] = reading.Any ? Math.Round(reading.Now[b], 2) : 0;
                d[$"b{b + 1}_gr_peak_db"] = reading.Any ? Math.Round(reading.Peak[b], 2) : 0;
                d[$"b{b + 1}_level_db"] = reading.Any ? reading.LevelDb[b] : double.NegativeInfinity;
            }
            d["bands"] = count;
            d["latency_samples"] = reading.Any ? reading.Latency : Latency;
            return d;
        }

        /// <summary>The last <see cref="ScopeLength"/> input samples (mono), oldest first, for the editor's spectrum.</summary>
        public void Scope(float[] into)
        {
            int p = scopePos;
            for (int i = 0; i < ScopeLength; i++) into[i] = scope[(p + i) & (ScopeLength - 1)];
        }

        /// <summary>
        /// True once the input has been silent long enough for everything in flight to have left (linear: the
        /// delay line's 16 partitions; minimum: the sections at rest), every band's gain has come back, and no
        /// switch is under way: the machine can stop working.
        /// </summary>
        public bool Idle
        {
            get
            {
                if (fadeLeft > 0 || fadeHold > 0 || fadingOut) return false;
                if (phase == PhaseMode.Linear ? quiet < (long)(linear.K + 2) * linear.P : quiet < MaxBlock || !minimum.AtRest) return false;
                foreach (var s in band) if (Math.Abs(s.G0) > 1e-4 || Math.Abs(s.G1) > 1e-4) return false;
                return true;
            }
        }

        public bool HoldsSubnormals()
        {
            if (linear.HoldsSubnormals() || minimum.HoldsSubnormals()) return true;
            foreach (var s in band) if (double.IsSubnormal(s.G0) || double.IsSubnormal(s.G1)) return true;
            foreach (var x in outL) if (float.IsSubnormal(x)) return true;
            foreach (var x in outR) if (float.IsSubnormal(x)) return true;
            return false;
        }

        public bool HoldsNonFinite()
        {
            if (linear.HoldsNonFinite() || minimum.HoldsNonFinite()) return true;
            foreach (var s in band) if (!double.IsFinite(s.G0) || !double.IsFinite(s.G1)) return true;
            foreach (var x in outL) if (!float.IsFinite(x)) return true;
            foreach (var x in outR) if (!float.IsFinite(x)) return true;
            return false;
        }
    }
}
