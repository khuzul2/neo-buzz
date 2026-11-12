#nullable disable
// Neo EQ (docs/specs/machines.md §5): eight bands in series, then the output gain. Each band is
// one TPT state-variable section in double precision (the RBJ cookbook biquad's response: the
// bilinear transform of the analog prototype, prewarped at the band frequency), or a cascade of
// them for the steeper cuts. Frequency, gain and Q glide (the sections recomputed every sample
// while they move); type, slope and on/off changes crossfade the band out and in. The design
// (EqDesign, EqDesign.cs) is the one function the engine runs, the editor draws, the tests measure
// against and the agent's eq_fit fits with.
// Mode Graphic (machine-safety.md §4, H2) rests the eight bands and runs the 31-band graphic EQ
// instead (GraphicEq.cs), the two crossfading over 5 ms when the mode changes; in Parametric mode,
// the default, the engine runs exactly as before graphic mode was added.
// v2 (docs/specs/mastering-tools.md §5.3, M3), each feature off at its parameters' defaults, so v1's bands run the
// code they ran (EqClassicTests pins their renders):
//  - Channel: a band on Left or Right alone, or on Mid or Side (encoded as (L+R)/2 and (L−R)/2 in double around the
//    band, decoded after it; the other half untouched). Only bands set so run these chains.
//  - Shape (EqDesign): proportional-Q bells, Baxandall and Vintage shelves, all-pass.
//  - Dynamic bands (bells and shelves): the detector hears the band's input (or the sidechain, input 2) in its channel
//    through the band's own shape (a band-pass at its frequency and Q for a bell, a low- or high-pass for a shelf),
//    its envelope exact for any sound from 20 Hz (the shared 90° pair: √(a² + b²)); its level through the shared gain
//    computer (threshold, ratio, knee), at most Range, smoothed by attack and release in dB (Detector.Smooth), every
//    sample. The band's gain is its setting moved down or up by that; it is designed on Neo Filter's 16-sample grid
//    of the engine's own count and its sections' coefficients move in straight lines to the new design over the
//    segment, sample by sample: no zipper, and the output doesn't depend on how the host splits the audio.
//  - Delta (DeltaListen, before Output) and the gain-match reading (GainMatch).
// Minimum phase, zero latency throughout.
// Pure: tested by NeoBuzz.MachineTests.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Eq
{
    internal sealed class EqEngine
    {
        public const int MaxBlock = 256;
        const double GlideSeconds = 0.02;
        const double FadeSeconds = 0.005;
        /// <summary>Samples of output kept for the editor's spectrum.</summary>
        public const int ScopeLength = 4096;

        readonly ParamValues values;
        float sampleRate = 48000;
        double glideA;
        LinearRamp output;
        readonly Band[] bands = new Band[EqDesign.Bands];
        readonly float[] scope = new float[ScopeLength];
        volatile int scopePos;
        // Graphic mode: the bank, its fade in and out, whether its sliders changed while it rested.
        readonly GraphicBank graphic = new GraphicBank();
        LinearRamp graphicMix;
        bool graphicWanted, graphicStale = true;
        readonly double[] gl = new double[MaxBlock], gr = new double[MaxBlock];
        // v2: the engine's own sample count (the 16-sample grid, the flushes), scratch for the dynamic bands, Delta and
        // the gain match.
        /// <summary>The dynamic bands' design grid, samples (a power of two; the tests' reference designs every sample).</summary>
        internal int Grid = 16;
        long count;
        readonly SvfSection[] design = new SvfSection[EqDesign.MaxSections];
        readonly double[] pairA = new double[2 * MaxBlock], pairB = new double[2 * MaxBlock], changes = new double[MaxBlock];
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;

        sealed class Band
        {
            public readonly SvfSection[] L = new SvfSection[EqDesign.MaxSections];
            public readonly SvfSection[] R = new SvfSection[EqDesign.MaxSections];
            public int Count;
            // What runs now (type, slope) and the glide state (log2 Hz, dB, log2 Q) towards the targets.
            public BandSettings Now;
            public double Lf, Gdb, Lq, TLf, TGdb, TLq;
            public bool Moving;
            public LinearRamp Mix;
            public bool WantOn;
            public BandType WantType;
            public int WantSlope;
            // v2: what runs now and what is wanted (a change fades the band out and in, as a type change).
            public BandChannel Chan, WantChan;
            public BandShape WantShape;
            public int Dyn, WantDyn;            // 0 off, 1 down, 2 up
            // The dynamic band: its curve, smoothing and detector; the change now (dB ≥ 0, after the last sample).
            public GainComputer Gc;
            public double Range, AttackA, ReleaseA, Floor2;
            public bool Sidechain;
            public double DynDb;
            public float ReadingDb;
            public readonly RampSection[] DL = new RampSection[2], DR = new RampSection[2];
            public int DCount;
            public SvfSection DetL, DetR;
            public QuadratureAllpass QL, QR;

            /// <summary>Runs v1's code: a static stereo band of v1's shapes (its state flushed per block, as v1).</summary>
            public bool Plain => Chan == BandChannel.Stereo && !Dynamic && Now.Shape == BandShape.Standard;

            /// <summary>Runs the dynamic code: a dynamic setting on a band with a gain.</summary>
            public bool Dynamic => Dyn != 0 && Now.HasGain;

            public void Reset()
            {
                for (int i = 0; i < EqDesign.MaxSections; i++)
                {
                    L[i].Reset();
                    R[i].Reset();
                }
                for (int i = 0; i < DL.Length; i++)
                {
                    DL[i].Reset();
                    DR[i].Reset();
                }
                DetL.Reset();
                DetR.Reset();
                QL?.Reset();
                QR?.Reset();
                DynDb = 0;
                ReadingDb = 0;
            }
        }

        public EqEngine(ParamValues values)
        {
            this.values = values;
            for (int b = 0; b < bands.Length; b++) bands[b] = new Band();
            Delta = new DeltaListen(1, 48000);
            Match = new GainMatch(48000);
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>Minimum phase: no latency.</summary>
        public int Latency => 0;

        public float SampleRate => sampleRate;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            glideA = 1 - PortableMath.Exp(-1 / (GlideSeconds * sr));
            output.Setup(GlideSeconds, sr);
            foreach (var b in bands) b.Mix.Setup(FadeSeconds, sr);
            graphicMix.Setup(FadeSeconds, sr);
            graphic.SetSampleRate(sr);
            graphicStale = true;
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            // The dynamic bands' 90° pairs for this rate (their design follows it).
            var qc = QuadratureAllpass.Coefficients(sr);
            foreach (var b in bands)
            {
                b.QL = new QuadratureAllpass(qc);
                b.QR = new QuadratureAllpass(qc);
            }
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            count = 0;
            Update(true);
        }

        public void Reset()
        {
            foreach (var b in bands)
            {
                b.Reset();
                if (b.Dynamic) Snap(b);
            }
            graphic.Cold = true;
            Delta.Reset();
            Match.Reset();
        }

        /// <summary>The graphic mode's bands and solver (the editor and tests read them).</summary>
        internal GraphicBank Graphic => graphic;

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            graphicWanted = values.Choice(P.Mode) == 1;
            for (int k = 0; k < GraphicDesign.Bands && !graphicStale; k++) graphicStale = values.IsDirty(GraphicDesign.Slider(k));
            if (graphicWanted && graphicStale)
            {
                // Off the per-sample path: only when a slider moved (or the mode came on since).
                graphic.SetSliders(values);
                graphicStale = false;
            }
            if (force) graphicMix.Reset(graphicWanted ? 1 : 0);
            else graphicMix.SetTarget(graphicWanted ? 1 : 0);
            if (force) graphic.Cold = true;
            float og = (float)DspMath.DbToGain(values.Value(P.Output));
            if (force) output.Reset(og);
            else output.SetTarget(og);
            for (int i = 0; i < bands.Length; i++)
            {
                var b = bands[i];
                var s = EqDesign.Settings(values, i);
                b.WantOn = values.On(EqDesign.On(i)) && !graphicWanted;
                b.WantType = s.Type;
                b.WantSlope = s.Slope;
                b.TLf = PortableMath.Log2(s.Hz);
                b.TGdb = s.GainDb;
                b.TLq = PortableMath.Log2(s.Q);
                UpdateV2(b, i, s, force);
                if (force)
                {
                    b.Now = s;
                    b.Chan = b.WantChan;
                    b.Dyn = b.WantDyn;
                    b.Lf = b.TLf;
                    b.Gdb = b.TGdb;
                    b.Lq = b.TLq;
                    b.Moving = false;
                    b.Reset();
                    Apply(b);
                    if (b.Dynamic) Snap(b);
                    b.Mix.Reset(b.WantOn ? 1 : 0);
                }
                else
                {
                    b.Moving = b.Lf != b.TLf || b.Gdb != b.TGdb || b.Lq != b.TLq;
                    // A cut's slope counts only for the cuts (v2's fix: a live cut ignored a new slope until it was
                    // switched off and on, as SectionCount(cut, 12 dB/oct) is 1 like every other type's).
                    bool sameShape = b.Now.Type == b.WantType && (!(b.Now.Type is BandType.LowCut or BandType.HighCut) || b.Now.Slope == b.WantSlope)
                        && b.Chan == b.WantChan && b.Now.Shape == b.WantShape && b.Dyn == b.WantDyn;
                    // Fade out for a new shape or switching off; back in once it has switched.
                    b.Mix.SetTarget(b.WantOn && sameShape ? 1 : 0);
                }
            }
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>v2's settings of band <paramref name="i"/>: the channel, shape and dynamic wanted, the curve and the detector.</summary>
        void UpdateV2(Band b, int i, in BandSettings s, bool force)
        {
            b.WantChan = (BandChannel)values.Choice(EqDesign.Channel(i));
            b.WantShape = s.Shape;
            b.WantDyn = values.Choice(EqDesign.Dynamic(i));
            double threshold = values.Value(EqDesign.Threshold(i)), knee = values.Value(EqDesign.Knee(i));
            b.Gc = GainComputer.Make(threshold, values.Value(EqDesign.Ratio(i)), knee);
            // Under this envelope² the curve gives nothing (the knee's foot): no logarithm needed.
            b.Floor2 = PortableMath.Pow(10, (knee > 1e-6 ? threshold - knee / 2 : threshold) / 10);
            b.Range = values.Value(EqDesign.Range(i));
            b.AttackA = Detector.Coef(values.Value(EqDesign.Attack(i)), sampleRate);
            b.ReleaseA = Detector.Coef(values.Value(EqDesign.Release(i)), sampleRate);
            b.Sidechain = values.Choice(EqDesign.Detect(i)) == 1;
            // The detector's own shape, at the band's set frequency and Q (not gliding: it only listens).
            double g = SvfSection.Prewarp(s.Hz, sampleRate);
            if (s.Type == BandType.LowShelf)
            {
                b.DetL.SetLowPass(g, Math.Sqrt(0.5));
                b.DetR.SetLowPass(g, Math.Sqrt(0.5));
            }
            else if (s.Type == BandType.HighShelf)
            {
                b.DetL.SetHighPass(g, Math.Sqrt(0.5));
                b.DetR.SetHighPass(g, Math.Sqrt(0.5));
            }
            else
            {
                // A band-pass of unity peak: k·(band output).
                double k = 1 / Math.Max(0.01, s.Q);
                b.DetL.Set(g, k, 0, k, 0);
                b.DetR.Set(g, k, 0, k, 0);
            }
        }

        /// <summary>A dynamic band's gain, its setting moved by the change, stays within ±30 dB.</summary>
        public const double MaxDynamicDb = 30;

        /// <summary>A dynamic band's sections set at once to its design now, at its change now (a fresh start or a reset).</summary>
        void Snap(Band b)
        {
            DesignDynamic(b, b.DynDb);
            for (int k = 0; k < b.DCount; k++)
            {
                b.DL[k].Snap(design[k]);
                b.DR[k].Snap(design[k]);
            }
        }

        /// <summary>The band designed at its glide state with its gain moved by the change <paramref name="d"/> (dB ≥ 0), into <see cref="design"/>.</summary>
        void DesignDynamic(Band b, double d)
        {
            var s = b.Now;
            s.Hz = PortableMath.Pow(2, b.Lf);
            s.Q = PortableMath.Pow(2, b.Lq);
            s.GainDb = Math.Clamp(b.Gdb + (b.Dyn == 1 ? -d : d), -MaxDynamicDb, MaxDynamicDb);
            b.DCount = Math.Min(2, EqDesign.Design(s, sampleRate, design));
        }

        /// <summary>Recomputes a band's sections from its glide state.</summary>
        void Apply(Band b)
        {
            b.Now.Hz = PortableMath.Pow(2, b.Lf);
            b.Now.GainDb = b.Gdb;
            b.Now.Q = PortableMath.Pow(2, b.Lq);
            b.Count = EqDesign.Design(b.Now, sampleRate, b.L);
            for (int k = 0; k < b.Count; k++)
            {
                ref var r = ref b.R[k];
                var l = b.L[k];
                r.Set(l.G, l.K, l.M0, l.M1, l.M2);
            }
        }

        static double Glide(double now, double target, double a, double snap) =>
            Math.Abs(target - now) <= snap ? target : now + (target - now) * a;

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n) => Process(left, right, null, null, n);

        /// <summary>Processes a stereo block in place; <paramref name="keyLeft"/>, <paramref name="keyRight"/>: the sidechain (input 2), or null.</summary>
        public void Process(float[] left, float[] right, float[] keyLeft, float[] keyRight, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            for (int bi = 0; bi < bands.Length; bi++)
            {
                var b = bands[bi];
                if (b.Mix.Value == 0)
                {
                    // Silent band (off, or faded out for a new shape): take what is wanted now, from
                    // a clean state, so it fades in with its new shape.
                    if (b.Now.Type != b.WantType || b.Now.Slope != b.WantSlope || b.Moving
                        || b.Chan != b.WantChan || b.Now.Shape != b.WantShape || b.Dyn != b.WantDyn)
                    {
                        b.Now.Type = b.WantType;
                        b.Now.Slope = b.WantSlope;
                        b.Now.Shape = b.WantShape;
                        b.Chan = b.WantChan;
                        b.Dyn = b.WantDyn;
                        b.Lf = b.TLf;
                        b.Gdb = b.TGdb;
                        b.Lq = b.TLq;
                        b.Moving = false;
                        Apply(b);
                    }
                    b.Reset();
                    if (b.Dynamic) Snap(b);
                    if (b.WantOn) b.Mix.SetTarget(1);
                    if (b.Mix.At(0)) continue;
                }
                if (b.Plain) ProcessBand(b, left, right, n);
                else if (b.Dynamic) ProcessDynamic(b, left, right, keyLeft, keyRight, n);
                else ProcessChannel(b, left, right, n);
            }
            if (graphicMix.Value != 0 || graphicMix.Target != 0) ProcessGraphic(left, right, n);
            Delta.Process(dryL, dryR, left, right, n);
            if (!output.At(1))
            {
                for (int i = 0; i < n; i++)
                {
                    float g = output.Next();
                    left[i] *= g;
                    right[i] *= g;
                }
            }
            int p = scopePos;
            for (int i = 0; i < n; i++)
            {
                scope[p] = 0.5f * (left[i] + right[i]);
                p = (p + 1) & (ScopeLength - 1);
            }
            scopePos = p;
            OutMeter.Feed(left, right, n);
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            Volatile.Write(ref pubMatch, Match.Db);
            count += n;
        }

        // ---- v2: a band on one channel, and the dynamic bands ------------------------------------------------------

        /// <summary>
        /// A static band of v2: on Left, Right, Mid or Side (the other channel or half untouched), or on both with a v2 shape;
        /// its state flushed on the grid of the engine's count.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessChannel(Band b, float[] left, float[] right, int n)
        {
            var sl = b.L;
            var sr = b.R;
            var chan = b.Chan;
            for (int i = 0; i < n; i++)
            {
                if (b.Moving)
                {
                    b.Lf = Glide(b.Lf, b.TLf, glideA, 1e-7);
                    b.Gdb = Glide(b.Gdb, b.TGdb, glideA, 1e-6);
                    b.Lq = Glide(b.Lq, b.TLq, glideA, 1e-7);
                    Apply(b);
                    b.Moving = b.Lf != b.TLf || b.Gdb != b.TGdb || b.Lq != b.TLq;
                }
                float w = b.Mix.Next();
                if (chan == BandChannel.Stereo)
                {
                    double x0 = left[i], x1 = right[i], y0 = x0, y1 = x1;
                    for (int k = 0; k < b.Count; k++)
                    {
                        y0 = sl[k].Process(y0);
                        y1 = sr[k].Process(y1);
                    }
                    if (w == 1)
                    {
                        left[i] = (float)y0;
                        right[i] = (float)y1;
                    }
                    else
                    {
                        left[i] = (float)(x0 + w * (y0 - x0));
                        right[i] = (float)(x1 + w * (y1 - x1));
                    }
                }
                else if (chan == BandChannel.Left || chan == BandChannel.Right)
                {
                    var x = chan == BandChannel.Left ? left : right;
                    var sec = chan == BandChannel.Left ? sl : sr;
                    double x0 = x[i], y0 = x0;
                    for (int k = 0; k < b.Count; k++) y0 = sec[k].Process(y0);
                    x[i] = w == 1 ? (float)y0 : (float)(x0 + w * (y0 - x0));
                }
                else
                {
                    double l = left[i], r = right[i];
                    double m = 0.5 * (l + r), s = 0.5 * (l - r);
                    double x0 = chan == BandChannel.Mid ? m : s, y0 = x0;
                    for (int k = 0; k < b.Count; k++) y0 = sl[k].Process(y0);
                    if (w != 1) y0 = x0 + w * (y0 - x0);
                    if (chan == BandChannel.Mid) m = y0;
                    else s = y0;
                    left[i] = (float)(m + s);
                    right[i] = (float)(m - s);
                }
                if (((count + i) & 15) == 15)
                    for (int k = 0; k < b.Count; k++)
                    {
                        sl[k].Flush();
                        sr[k].Flush();
                    }
            }
        }

        /// <summary>
        /// A dynamic band: the detector over the block (its change after each sample into <see cref="changes"/>), then the
        /// band, designed at every grid point with the change after the sample before it, its coefficients ramped there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessDynamic(Band b, float[] left, float[] right, float[] keyLeft, float[] keyRight, int n)
        {
            var chan = b.Chan;
            bool stereo = chan == BandChannel.Stereo;
            // The detector: its input in the band's channel, through the band's shape, then the 90° pair.
            float[] s0 = b.Sidechain ? keyLeft : left, s1 = b.Sidechain ? keyRight : right;
            var pa = pairA;
            for (int i = 0; i < n; i++)
            {
                double x0 = 0, x1 = 0;
                if (s0 != null)
                {
                    double l = s0[i], r = s1 != null ? s1[i] : l;
                    switch (chan)
                    {
                        case BandChannel.Stereo: x0 = l; x1 = r; break;
                        case BandChannel.Left: x0 = l; break;
                        case BandChannel.Right: x0 = r; break;
                        case BandChannel.Mid: x0 = 0.5 * (l + r); break;
                        default: x0 = 0.5 * (l - r); break;
                    }
                }
                pa[2 * i] = b.DetL.Process(x0);
                pa[2 * i + 1] = stereo ? b.DetR.Process(x1) : 0;
                if (((count + i + 1) & 63) == 0)
                {
                    b.DetL.Flush();
                    b.DetR.Flush();
                }
            }
            int first = 63 - (int)(count & 63);
            QuadratureAllpass.Block(b.QL, b.QR, pa, pairB, n, first);
            var pb = pairB;
            double before = b.DynDb, d = before;
            for (int i = 0; i < n; i++)
            {
                double e2 = pa[2 * i] * pa[2 * i] + pb[2 * i] * pb[2 * i];
                if (stereo) e2 = Math.Max(e2, pa[2 * i + 1] * pa[2 * i + 1] + pb[2 * i + 1] * pb[2 * i + 1]);
                double t = 0;
                if (e2 > b.Floor2)
                {
                    t = b.Gc.Ff(0.5 * DspMath.DbPerNeper * PortableMath.Log(e2));
                    if (t > b.Range) t = b.Range;
                }
                d = Detector.Smooth(d, t, b.AttackA, b.ReleaseA);
                if (((count + i + 1) & 63) == 0) d = DspMath.Flush(d);
                changes[i] = d;
            }
            b.DynDb = d;

            // The band.
            var dl = b.DL;
            var dr = b.DR;
            float sign = b.Dyn == 1 ? -1 : 1;
            for (int i = 0; i < n; i++)
            {
                if (b.Moving)
                {
                    b.Lf = Glide(b.Lf, b.TLf, glideA, 1e-7);
                    b.Gdb = Glide(b.Gdb, b.TGdb, glideA, 1e-6);
                    b.Lq = Glide(b.Lq, b.TLq, glideA, 1e-7);
                    b.Moving = b.Lf != b.TLf || b.Gdb != b.TGdb || b.Lq != b.TLq;
                }
                if (((count + i) & (Grid - 1)) == 0)
                {
                    double now = i == 0 ? before : changes[i - 1];
                    int was = b.DCount;
                    DesignDynamic(b, now);
                    for (int k = 0; k < b.DCount; k++)
                    {
                        if (k < was)
                        {
                            dl[k].RampTo(design[k], Grid);
                            dr[k].RampTo(design[k], Grid);
                        }
                        else
                        {
                            dl[k].Snap(design[k]);
                            dr[k].Snap(design[k]);
                        }
                        dl[k].Flush();
                        dr[k].Flush();
                    }
                    b.ReadingDb = (float)(sign * now);
                }
                float w = b.Mix.Next();
                int count2 = b.DCount;
                if (stereo)
                {
                    double x0 = left[i], x1 = right[i], y0 = x0, y1 = x1;
                    for (int k = 0; k < count2; k++)
                    {
                        y0 = dl[k].Process(y0);
                        y1 = dr[k].Process(y1);
                    }
                    if (w == 1)
                    {
                        left[i] = (float)y0;
                        right[i] = (float)y1;
                    }
                    else
                    {
                        left[i] = (float)(x0 + w * (y0 - x0));
                        right[i] = (float)(x1 + w * (y1 - x1));
                    }
                }
                else if (chan == BandChannel.Left || chan == BandChannel.Right)
                {
                    var x = chan == BandChannel.Left ? left : right;
                    double x0 = x[i], y0 = x0;
                    for (int k = 0; k < count2; k++) y0 = dl[k].Process(y0);
                    x[i] = w == 1 ? (float)y0 : (float)(x0 + w * (y0 - x0));
                }
                else
                {
                    double l = left[i], r = right[i];
                    double m = 0.5 * (l + r), s = 0.5 * (l - r);
                    double x0 = chan == BandChannel.Mid ? m : s, y0 = x0;
                    for (int k = 0; k < count2; k++) y0 = dl[k].Process(y0);
                    if (w != 1) y0 = x0 + w * (y0 - x0);
                    if (chan == BandChannel.Mid) m = y0;
                    else s = y0;
                    left[i] = (float)(m + s);
                    right[i] = (float)(m - s);
                }
            }
        }

        // ---- readings (machine_readings) -------------------------------------------------------------------------

        double pubMatch = double.NaN;

        /// <summary>The gain-match reading, dB (NaN until 3 s of sound): for the editor's Match.</summary>
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        /// <summary>A band's dynamic gain change now, dB (negative: down; 0 while static), for the editor.</summary>
        public float DynamicDb(int band) => bands[band].Dynamic ? Volatile.Read(ref bands[band].ReadingDb) : 0;

        /// <summary>The readings now (gain_match_db only once it is known: 3 s of sound).</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>();
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            for (int b = 0; b < bands.Length; b++) d[$"b{b + 1}_dynamic_db"] = Math.Round(DynamicDb(b), 2);
            return d;
        }

        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ProcessBand(Band b, float[] left, float[] right, int n)
        {
            var sl = b.L;
            var sr = b.R;
            for (int i = 0; i < n; i++)
            {
                if (b.Moving)
                {
                    b.Lf = Glide(b.Lf, b.TLf, glideA, 1e-7);
                    b.Gdb = Glide(b.Gdb, b.TGdb, glideA, 1e-6);
                    b.Lq = Glide(b.Lq, b.TLq, glideA, 1e-7);
                    Apply(b);
                    b.Moving = b.Lf != b.TLf || b.Gdb != b.TGdb || b.Lq != b.TLq;
                }
                double x0 = left[i], x1 = right[i];
                double y0 = x0, y1 = x1;
                for (int k = 0; k < b.Count; k++)
                {
                    y0 = sl[k].Process(y0);
                    y1 = sr[k].Process(y1);
                }
                float w = b.Mix.Next();
                if (w == 1)
                {
                    left[i] = (float)y0;
                    right[i] = (float)y1;
                }
                else
                {
                    left[i] = (float)(x0 + w * (y0 - x0));
                    right[i] = (float)(x1 + w * (y1 - x1));
                }
            }
            for (int k = 0; k < b.Count; k++)
            {
                sl[k].Flush();
                sr[k].Flush();
            }
        }

        void ProcessGraphic(float[] left, float[] right, int n)
        {
            if (graphic.Cold) graphic.Restart();
            // Every band at exactly 0 dB and no offset: the sections run (to move off cleanly), the audio passes bit for bit.
            bool untouched = graphicMix.At(1) && graphic.Identity;
            for (int i = 0; i < n; i++)
            {
                gl[i] = left[i];
                gr[i] = right[i];
            }
            graphic.Process(gl, gr, n);
            if (untouched) return;
            if (graphicMix.At(1))
            {
                for (int i = 0; i < n; i++)
                {
                    left[i] = (float)Clean(gl[i]);
                    right[i] = (float)Clean(gr[i]);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    double w = graphicMix.Next();
                    left[i] = (float)Clean(left[i] + w * (gl[i] - left[i]));
                    right[i] = (float)Clean(right[i] + w * (gr[i] - right[i]));
                }
                if (graphicMix.At(0)) graphic.Cold = true;
            }
        }

        /// <summary>A value below 1e-30 as 0, so a dying tail leaves no subnormal floats.</summary>
        static double Clean(double x) => x > -1e-30 && x < 1e-30 ? 0 : x;

        /// <summary>The last <see cref="ScopeLength"/> output samples (mono), oldest first, for the editor's spectrum.</summary>
        public void Scope(float[] into)
        {
            int p = scopePos;
            for (int i = 0; i < ScopeLength; i++) into[i] = scope[(p + i) & (ScopeLength - 1)];
        }
    }
}
