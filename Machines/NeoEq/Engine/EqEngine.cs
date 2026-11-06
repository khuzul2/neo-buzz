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
// Pure: tested by NeoBuzz.MachineTests.

using System.Runtime.CompilerServices;
using System;
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

            public void Reset()
            {
                for (int i = 0; i < EqDesign.MaxSections; i++)
                {
                    L[i].Reset();
                    R[i].Reset();
                }
            }
        }

        public EqEngine(ParamValues values)
        {
            this.values = values;
            for (int b = 0; b < bands.Length; b++) bands[b] = new Band();
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
            Update(true);
        }

        public void Reset()
        {
            foreach (var b in bands) b.Reset();
            graphic.Cold = true;
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
                if (force)
                {
                    b.Now = s;
                    b.Lf = b.TLf;
                    b.Gdb = b.TGdb;
                    b.Lq = b.TLq;
                    b.Moving = false;
                    b.Reset();
                    Apply(b);
                    b.Mix.Reset(b.WantOn ? 1 : 0);
                }
                else
                {
                    b.Moving = b.Lf != b.TLf || b.Gdb != b.TGdb || b.Lq != b.TLq;
                    bool sameShape = b.Now.Type == b.WantType && (EqDesign.SectionCount(b.Now.Type, 0) == 1 || b.Now.Slope == b.WantSlope);
                    // Fade out for a new shape or switching off; back in once it has switched.
                    b.Mix.SetTarget(b.WantOn && sameShape ? 1 : 0);
                }
            }
            values.Clean();
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
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            for (int bi = 0; bi < bands.Length; bi++)
            {
                var b = bands[bi];
                if (b.Mix.Value == 0)
                {
                    // Silent band (off, or faded out for a new shape): take what is wanted now, from
                    // a clean state, so it fades in with its new shape.
                    if (b.Now.Type != b.WantType || b.Now.Slope != b.WantSlope || b.Moving)
                    {
                        b.Now.Type = b.WantType;
                        b.Now.Slope = b.WantSlope;
                        b.Lf = b.TLf;
                        b.Gdb = b.TGdb;
                        b.Lq = b.TLq;
                        b.Moving = false;
                        Apply(b);
                    }
                    b.Reset();
                    if (b.WantOn) b.Mix.SetTarget(1);
                    if (b.Mix.At(0)) continue;
                }
                ProcessBand(b, left, right, n);
            }
            if (graphicMix.Value != 0 || graphicMix.Target != 0) ProcessGraphic(left, right, n);
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
