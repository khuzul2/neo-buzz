#nullable disable
// Neo Strip (docs/specs/mastering-tools.md §5.10): gate, EQ, compressor, drive, clipper and limiter in that order, each
// stage the engine of the machine of that name (Neo EQ's six bands for the low cut, shelves, two bells and high cut;
// Neo Comp; Neo Drive; Neo Clipper, hard at 4×; Neo Limiter), run with the strip's settings: the strip's controls are the
// same ParamDefs, converted value for value into each engine's own ParamValues, so a stage sounds as its machine does.
//  - A stage that is off passes its input untouched, delayed by that stage's latency, so the strip's latency (drive + clipper
//    + limiter) is the same whichever stages are on, and a stage switched on or off crossfades over 5 ms.
//  - Everything off: the output is the input delayed by the latency, bit for bit.
//  - Delta: the output minus the input delayed by the latency, before Output (DeltaListen); the gain match compares the
//    output with the input (GainMatch).
// No allocation after construction (one per sample rate).

using System;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Strip
{
    /// <summary>The gate: the louder channel's peak against a threshold with 3 dB hysteresis, a hold, and attack and release on the gain.</summary>
    internal sealed class StripGate
    {
        double thr = 0.003, rangeGain = 0.01, aOpen = 1, aClose = 0.001, envDecay = 0.99;
        int holdSamples = 2400;
        double env, g = 1;
        int holdLeft;
        bool open;

        /// <summary>The closed gate's gain, linear.</summary>
        public double Gain => g;

        public double GainDb => 20 * PortableMath.Log10(Math.Max(1e-6, g));

        public void Set(double sampleRate, double threshDb, double rangeDb, double attack, double hold, double release)
        {
            thr = PortableMath.Pow(10, threshDb / 20);
            rangeGain = PortableMath.Pow(10, -rangeDb / 20);
            aOpen = 1 - PortableMath.Exp(-1 / (Math.Max(1e-5, attack) * sampleRate));
            aClose = 1 - PortableMath.Exp(-1 / (Math.Max(1e-5, release) * sampleRate));
            envDecay = PortableMath.Exp(-1 / (0.005 * sampleRate));
            holdSamples = (int)Math.Round(hold * sampleRate);
        }

        public void Reset()
        {
            env = 0;
            g = 1;
            open = true;
            holdLeft = 0;
        }

        public void Process(float[] l, float[] r, int n)
        {
            double close = thr * 0.7079;
            for (int i = 0; i < n; i++)
            {
                double x = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
                env = x > env ? x : env * envDecay;
                if (env > thr)
                {
                    open = true;
                    holdLeft = holdSamples;
                }
                else if (open && env < close)
                {
                    if (holdLeft > 0) holdLeft--;
                    else open = false;
                }
                else if (open) holdLeft = holdSamples;
                double target = open ? 1 : rangeGain;
                g += (target - g) * (open ? aOpen : aClose);
                l[i] = (float)(l[i] * g);
                r[i] = (float)(r[i] * g);
            }
        }
    }

    internal sealed class StripEngine
    {
        public const int MaxBlock = 256;
        const double FadeSeconds = 0.005, GlideSeconds = 0.02;
        const int GateStage = 0, EqStage = 1, CompStage = 2, DriveStage = 3, ClipStage = 4, LimitStage = 5, Stages = 6;

        readonly ParamValues values;
        float sampleRate = 48000;

        // The engines, each with its own parameter values.
        readonly ParamValues eqV = new ParamValues(NeoBuzz.Eq.Params.Table), compV = new ParamValues(NeoBuzz.Comp.Params.Table),
            driveV = new ParamValues(NeoBuzz.Drive.Params.Table), clipV = new ParamValues(NeoBuzz.Clipper.Params.Table),
            limV = new ParamValues(NeoBuzz.Limiter.Params.Table);
        readonly NeoBuzz.Eq.EqEngine eq;
        readonly NeoBuzz.Comp.CompEngine comp;
        readonly NeoBuzz.Drive.DriveEngine drive;
        readonly NeoBuzz.Clipper.ClipperEngine clip;
        readonly NeoBuzz.Limiter.LimiterEngine lim;
        readonly StripGate gate = new StripGate();

        sealed class Stage
        {
            public LinearRamp Mix;
            public DelayLineF PadL = new DelayLineF(1 << 14), PadR = new DelayLineF(1 << 14);
            public int Latency;
            public bool WasOff = true;
            public readonly float[] WetL = new float[MaxBlock], WetR = new float[MaxBlock];
        }

        readonly Stage[] stage = new Stage[Stages];
        readonly Link[] links;
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        LinearRamp output;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        double pubMatch = double.NaN;
        int latency;

        readonly struct Link
        {
            public readonly int Src, Dst;
            public readonly ParamValues Values;
            public Link(int src, ParamValues values, int dst)
            {
                Src = src;
                Values = values;
                Dst = dst;
            }
        }

        public StripEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < Stages; i++) stage[i] = new Stage();
            eq = new NeoBuzz.Eq.EqEngine(eqV);
            comp = new NeoBuzz.Comp.CompEngine(compV);
            drive = new NeoBuzz.Drive.DriveEngine(driveV);
            clip = new NeoBuzz.Clipper.ClipperEngine(clipV);
            lim = new NeoBuzz.Limiter.LimiterEngine(limV);
            Delta = new DeltaListen(1 << 14, 48000);
            Match = new GainMatch(48000);
            // The six bands: types fixed, the rest linked.
            int[] types = { 3, 1, 0, 0, 2, 4 };
            for (int b = 0; b < types.Length; b++) eqV.Set(NeoBuzz.Eq.EqDesign.TypeOf(b), types[b]);
            // The cuts are Butterworth 12 dB/oct and the shelves and cuts sit at Q 0.7071 (flat); only the bells have a Q to set.
            foreach (int b in new[] { 0, 1, 4, 5 }) eqV.Set(NeoBuzz.Eq.EqDesign.Q(b), NeoBuzz.Eq.Params.Table[NeoBuzz.Eq.EqDesign.Q(b)].Raw(0.7071));
            eqV.Set(NeoBuzz.Eq.EqDesign.Slope(0), 0);
            eqV.Set(NeoBuzz.Eq.EqDesign.Slope(5), 0);
            // Neo Clipper: hard.
            clipV.Set(NeoBuzz.Clipper.P.Shape, 0);
            var list = new System.Collections.Generic.List<Link>();
            void L(int src, ParamValues v, int dst) => list.Add(new Link(src, v, dst));
            L(P.LoCutFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(0));
            L(P.LoShelfFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(1));
            L(P.LoShelfGain, eqV, NeoBuzz.Eq.EqDesign.Gain(1));
            L(P.LmFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(2));
            L(P.LmGain, eqV, NeoBuzz.Eq.EqDesign.Gain(2));
            L(P.LmQ, eqV, NeoBuzz.Eq.EqDesign.Q(2));
            L(P.HmFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(3));
            L(P.HmGain, eqV, NeoBuzz.Eq.EqDesign.Gain(3));
            L(P.HmQ, eqV, NeoBuzz.Eq.EqDesign.Q(3));
            L(P.HiShelfFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(4));
            L(P.HiShelfGain, eqV, NeoBuzz.Eq.EqDesign.Gain(4));
            L(P.HiCutFreq, eqV, NeoBuzz.Eq.EqDesign.Freq(5));
            L(P.CompMode, compV, NeoBuzz.Comp.P.Mode);
            L(P.CompThresh, compV, NeoBuzz.Comp.P.Threshold);
            L(P.CompRatio, compV, NeoBuzz.Comp.P.Ratio);
            L(P.CompAttack, compV, NeoBuzz.Comp.P.Attack);
            L(P.CompRelease, compV, NeoBuzz.Comp.P.Release);
            L(P.CompMakeup, compV, NeoBuzz.Comp.P.Makeup);
            L(P.DriveMode, driveV, NeoBuzz.Drive.P.Mode);
            L(P.DriveAmount, driveV, NeoBuzz.Drive.P.Drive);
            L(P.DriveMix, driveV, NeoBuzz.Drive.P.Mix);
            L(P.Ceiling, clipV, NeoBuzz.Clipper.P.Ceiling);
            L(P.Ceiling, limV, NeoBuzz.Limiter.P.Ceiling);
            L(P.LimRelease, limV, NeoBuzz.Limiter.P.Release);
            links = list.ToArray();
            SetSampleRate(48000);
        }

        /// <summary>Samples from the strip's input to its output: the drive's, the clipper's and the limiter's, whichever stages are on.</summary>
        public int Latency => latency;

        public float SampleRate => sampleRate;

        public float GainReductionDb => lim.GainReductionDb;
        public float CompReductionDb => comp.GainReductionDb;
        public double GateDb => gate.GainDb;
        public double GainMatchDb => System.Threading.Volatile.Read(ref pubMatch);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            // The engines take the strip's settings first, so each starts from them (not gliding from its defaults).
            Update(true);
            eq.SetSampleRate(sr);
            comp.SetSampleRate(sr);
            drive.SetSampleRate(sr);
            clip.SetSampleRate(sr);
            lim.SetSampleRate(sr);
            stage[DriveStage].Latency = NeoBuzz.Drive.DriveEngine.Latency;
            stage[ClipStage].Latency = clip.Latency;
            stage[LimitStage].Latency = lim.Latency;
            latency = stage[DriveStage].Latency + stage[ClipStage].Latency + stage[LimitStage].Latency;
            foreach (var s in stage) s.Mix.Setup(FadeSeconds, sr);
            output.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Delta.Latency = latency;
            Reset();
        }

        public void Reset()
        {
            eq.Reset();
            comp.Reset();
            drive.Reset();
            clip.Reset();
            lim.Reset();
            gate.Reset();
            foreach (var s in stage)
            {
                s.PadL.Reset();
                s.PadR.Reset();
                s.WasOff = true;
            }
            Delta.Reset();
            Match.Reset();
        }

        bool Want(int stageIndex) => stageIndex switch
        {
            GateStage => values.On(P.GateOn),
            EqStage => values.On(P.EqOn),
            CompStage => values.On(P.CompOn),
            DriveStage => values.On(P.DriveOn),
            ClipStage => values.On(P.ClipOn),
            _ => values.On(P.LimitOn),
        };

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            for (int i = 0; i < Stages; i++)
            {
                float t = Want(i) ? 1 : 0;
                if (force) stage[i].Mix.Reset(t);
                else stage[i].Mix.SetTarget(t);
            }
            gate.Set(sampleRate, values.Value(P.GateThresh), values.Value(P.GateRange), values.Value(P.GateAttack), values.Value(P.GateHold), values.Value(P.GateRelease));
            // The EQ's bands: those with a gain (or a cut on) are on.
            bool eqOn = true;
            SetBand(0, eqOn && values.On(P.LoCutOn));
            SetBand(1, eqOn && values.Value(P.LoShelfGain) != 0);
            SetBand(2, eqOn && values.Value(P.LmGain) != 0);
            SetBand(3, eqOn && values.Value(P.HmGain) != 0);
            SetBand(4, eqOn && values.Value(P.HiShelfGain) != 0);
            SetBand(5, eqOn && values.On(P.HiCutOn));
            SetBand(6, false);
            SetBand(7, false);
            foreach (var l in links)
            {
                int raw = l.Values.Table[l.Dst].Raw(values.Value(l.Src));
                if (l.Values[l.Dst] != raw) l.Values.Set(l.Dst, raw);
            }
            float og = (float)DspMath.DbToGain(values.Value(P.Output));
            if (force) output.Reset(og);
            else output.SetTarget(og);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            Delta.Latency = latency;
            values.Clean();
        }

        void SetBand(int b, bool on)
        {
            int idx = NeoBuzz.Eq.EqDesign.On(b);
            int raw = on ? 1 : 0;
            if (eqV[idx] != raw) eqV.Set(idx, raw);
        }

        /// <summary>Processes a stereo block in place (up to 256 samples).</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            Run(stage[GateStage], left, right, n);
            Run(stage[EqStage], left, right, n);
            Run(stage[CompStage], left, right, n);
            Run(stage[DriveStage], left, right, n);
            Run(stage[ClipStage], left, right, n);
            Run(stage[LimitStage], left, right, n);
            Delta.Process(dryL, dryR, left, right, n);
            if (!output.At(1))
                for (int i = 0; i < n; i++)
                {
                    float g = output.Next();
                    left[i] *= g;
                    right[i] *= g;
                }
            OutMeter.Feed(left, right, n);
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            System.Threading.Volatile.Write(ref pubMatch, Match.Db);
        }

        void Wet(int which, float[] l, float[] r, int n)
        {
            switch (which)
            {
                case GateStage: gate.Process(l, r, n); break;
                case EqStage: eq.Process(l, r, n); break;
                case CompStage: comp.Process(l, r, null, null, n); break;
                case DriveStage: drive.Process(l, r, n); break;
                case ClipStage: clip.Process(l, r, n); break;
                default: lim.Process(l, r, n); break;
            }
        }

        void Run(Stage s, float[] l, float[] r, int n)
        {
            int which = Array.IndexOf(stage, s);
            int lat = s.Latency;
            if (s.Mix.At(0))
            {
                // Off: the input, late by the stage's latency.
                if (lat > 0)
                    for (int i = 0; i < n; i++)
                    {
                        s.PadL.Write(l[i]);
                        s.PadR.Write(r[i]);
                        l[i] = s.PadL.Read(lat);
                        r[i] = s.PadR.Read(lat);
                    }
                s.WasOff = true;
                return;
            }
            if (s.WasOff)
            {
                ResetStage(which);
                s.WasOff = false;
            }
            Array.Copy(l, s.WetL, n);
            Array.Copy(r, s.WetR, n);
            Wet(which, s.WetL, s.WetR, n);
            bool full = s.Mix.At(1);
            for (int i = 0; i < n; i++)
            {
                float dl = l[i], dr = r[i];
                if (lat > 0)
                {
                    s.PadL.Write(dl);
                    s.PadR.Write(dr);
                    dl = s.PadL.Read(lat);
                    dr = s.PadR.Read(lat);
                }
                if (full)
                {
                    l[i] = s.WetL[i];
                    r[i] = s.WetR[i];
                }
                else
                {
                    float w = s.Mix.Next();
                    l[i] = w == 1 ? s.WetL[i] : dl + w * (s.WetL[i] - dl);
                    r[i] = w == 1 ? s.WetR[i] : dr + w * (s.WetR[i] - dr);
                }
            }
        }

        void ResetStage(int which)
        {
            switch (which)
            {
                case GateStage: gate.Reset(); break;
                case EqStage: eq.Reset(); break;
                case CompStage: comp.Reset(); break;
                case DriveStage: drive.Reset(); break;
                case ClipStage: clip.Reset(); break;
                default: lim.Reset(); break;
            }
        }
    }
}
