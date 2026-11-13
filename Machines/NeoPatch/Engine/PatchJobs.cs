#nullable disable
// Neo Patch NP0 (docs/specs/neo-patch.md §4.4): the block jobs. A compiled patch is an ordered list of jobs; each job runs one
// node (or one input's mix) for one 64-sample block across all voices. Per-voice buffers are [sample][16 voices] (sample-major),
// so eight voices are one vector: the kernels are Dye Essence's lane kernels' way (Lanes.cs: Avx2, two Vector128, or plain
// floats, same bits). Everything a job touches is allocated when the patch is compiled; Run allocates nothing.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    /// <summary>What every job sees: the sample rate, the voices of this block and the stereo output being summed.</summary>
    internal sealed class PatchContext
    {
        public const int Block = 64, MaxVoices = 16, Width = 8, Groups = MaxVoices / Width;

        public LaneKind Kind;
        public float SampleRate = 48000;
        /// <summary>Control interval, samples: pitches, cutoffs and envelope levels are read and updated here (spec §2.3 allows a CV at audio rate; this is the cost knob).</summary>
        public int Tick = 16;

        public readonly bool[] GroupOn = new bool[Groups];
        public readonly bool[] Active = new bool[MaxVoices];
        public readonly bool[] Fresh = new bool[MaxVoices];
        public readonly float[] Pitch0 = new float[MaxVoices], Pitch1 = new float[MaxVoices], Vel = new float[MaxVoices], Gate = new float[MaxVoices];
        public readonly float[] Gain0 = new float[MaxVoices], Gain1 = new float[MaxVoices];
        public readonly float[] Peak = new float[MaxVoices];
        public readonly float[] OutL = new float[Block], OutR = new float[Block];
    }

    internal sealed class PortBuffer
    {
        public readonly float[] Data;
        public readonly bool PerVoice;
        public PortBuffer(bool perVoice) { PerVoice = perVoice; Data = new float[perVoice ? Block * PatchContext.MaxVoices : Block]; }
        const int Block = PatchContext.Block;
    }

    internal abstract class Job
    {
        public int NodeId;
        public abstract void Run(PatchContext c);
    }

    internal readonly struct MixInput
    {
        public readonly PortBuffer Source;
        public readonly float Amount;
        public MixInput(PortBuffer s, float a) { Source = s; Amount = a; }
    }

    // ---------------------------------------------------------------- jobs

    internal sealed class KeysJob : Job
    {
        public PortBuffer Pitch, Gate, Vel;
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    internal sealed class OscJob : Job
    {
        public float[] Ctl;
        public PortBuffer Pitch, Fm, Out;
        public readonly float[] Phase = new float[PatchContext.MaxVoices], Inc = new float[PatchContext.MaxVoices], LastNote = new float[PatchContext.MaxVoices];
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    internal sealed class EnvJob : Job
    {
        public float[] Ctl;
        public PortBuffer Gate, Out;
        public EnvSettings Settings = new EnvSettings();
        public readonly int[] Stage = new int[PatchContext.MaxVoices];
        public readonly float[] Level = new float[PatchContext.MaxVoices], Start = new float[PatchContext.MaxVoices], Step = new float[PatchContext.MaxVoices];
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    internal sealed class FilterJob : Job
    {
        public float[] Ctl;
        public PortBuffer In, Cutoff, Out;
        public readonly float[] Ic1 = new float[PatchContext.MaxVoices], Ic2 = new float[PatchContext.MaxVoices];
        public readonly float[] A1 = new float[PatchContext.MaxVoices], A2 = new float[PatchContext.MaxVoices], A3 = new float[PatchContext.MaxVoices], K = new float[PatchContext.MaxVoices];
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    internal sealed class VcaJob : Job
    {
        public float[] Ctl;
        public PortBuffer In, Cv, Out;
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    /// <summary>
    /// Builds an input from its wires: per-voice into per-voice (scaled sum; a global source reaches every voice), and per-voice into
    /// global (the voice sum: each voice's gain ramp, NaN guard and peak, summed in one fixed order).
    /// </summary>
    internal sealed class MixJob : Job
    {
        public MixInput[] Inputs;
        public PortBuffer Dest;
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    internal sealed class OutJob : Job
    {
        public float[] Ctl;
        public PortBuffer In;
        public override void Run(PatchContext c)
        {
            if (In == null) return;
            float lvl = Ctl[NodeInfo.OutLevel], pan = Ctl[NodeInfo.OutPan];
            float gl = lvl * MathF.Sqrt(0.5f * (1f - pan)), gr = lvl * MathF.Sqrt(0.5f * (1f + pan));
            var x = In.Data;
            for (int i = 0; i < PatchContext.Block; i++)
            {
                c.OutL[i] += x[i] * gl;
                c.OutR[i] += x[i] * gr;
            }
        }
    }

    /// <summary>The Feedback node's two halves: Read (the previous block, first in the order) and Write (this block's input, after its source).</summary>
    internal sealed class FeedbackState
    {
        public readonly float[] Data;
        public readonly bool PerVoice;
        public FeedbackState(bool perVoice) { PerVoice = perVoice; Data = new float[perVoice ? PatchContext.Block * PatchContext.MaxVoices : PatchContext.Block]; }
    }

    internal sealed class FeedbackReadJob : Job
    {
        public FeedbackState State;
        public PortBuffer Out;
        public override void Run(PatchContext c)
        {
            Array.Copy(State.Data, Out.Data, State.Data.Length);
            if (State.PerVoice)
            {
                // A voice that starts has no past.
                for (int v = 0; v < PatchContext.MaxVoices; v++)
                    if (c.Fresh[v]) for (int i = 0; i < PatchContext.Block; i++) Out.Data[i * PatchContext.MaxVoices + v] = 0f;
            }
        }
    }

    internal sealed class FeedbackWriteJob : Job
    {
        public float[] Ctl;
        public FeedbackState State;
        public PortBuffer In;
        public override void Run(PatchContext c) => Kernels.Dispatch(this, c);
    }

    // ---------------------------------------------------------------- kernels

    internal static class Kernels
    {
        const int B = PatchContext.Block, V = PatchContext.MaxVoices, W = PatchContext.Width;

        public static void Dispatch(KeysJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Keys<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Keys<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Keys<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(OscJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Osc<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Osc<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Osc<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(EnvJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Env<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Env<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Env<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(FilterJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Filter<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Filter<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Filter<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(VcaJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Vca<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Vca<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Vca<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(MixJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: Mix<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: Mix<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: Mix<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }
        public static void Dispatch(FeedbackWriteJob j, PatchContext c) { switch (c.Kind) { case LaneKind.Avx2: FbWrite<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(j, c); break; case LaneKind.Scalar: FbWrite<ScalarLanes, Eight<float>, Eight<uint>>(j, c); break; default: FbWrite<PairLanes, Pair<float>, Pair<uint>>(j, c); break; } }

        /// <summary>A value that is finite and under 1e9 in size stays; anything else (NaN, infinities, runaways) becomes 0. Per voice, no branches.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Clean<TL, TF, TU>(TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF a = TL.Select(TL.Greater(x, TL.Splat(0f)), x, TL.Sub(TL.Splat(0f), x));
            return TL.Select(TL.Greater(TL.Splat(1e9f), a), x, TL.Splat(0f));
        }

        /// <summary>The scalar version of <see cref="Clean"/> for state: also flushes denormals.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CleanState(float x)
        {
            float a = Math.Abs(x);
            return a < 1e-18f || !(a < 1e9f) ? 0f : x;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Keys<TL, TF, TU>(KeysJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF p = TL.Load(c.Pitch0, vb);
                TF dp = TL.Mul(TL.Sub(TL.Load(c.Pitch1, vb), p), TL.Splat(1f / B));
                TF gate = TL.Load(c.Gate, vb), vel = TL.Load(c.Vel, vb);
                for (int i = 0; i < B; i++)
                {
                    p = TL.Add(p, dp);
                    int o = i * V + vb;
                    if (j.Pitch != null) TL.Store(p, j.Pitch.Data, o);
                    if (j.Gate != null) TL.Store(gate, j.Gate.Data, o);
                    if (j.Vel != null) TL.Store(vel, j.Vel.Data, o);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Blep<TL, TF, TU>(TF t, TF dt, TF rdt) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            // Polynomial band-limited step at the cycle's start: 2x − x² − 1 just after the wrap, x² + 2x + 1 just before.
            TF x1 = TL.Mul(t, rdt);
            TF r1 = TL.Sub(TL.Sub(TL.Add(x1, x1), TL.Mul(x1, x1)), TL.Splat(1f));
            TF x2 = TL.Mul(TL.Sub(t, TL.Splat(1f)), rdt);
            TF r2 = TL.Add(TL.Add(TL.Mul(x2, x2), TL.Add(x2, x2)), TL.Splat(1f));
            TF r = TL.Select(TL.Greater(dt, t), r1, TL.Select(TL.Greater(t, TL.Sub(TL.Splat(1f), dt)), r2, TL.Splat(0f)));
            return r;
        }

        /// <summary>
        /// The four-point version: the cubic B-spline's step response (a band-limiting kernel four samples wide), as four polynomial
        /// pieces, two before the wrap and two after. Aliasing falls 24 dB an octave where the parabola's falls 12.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Blep4<TL, TF, TU>(TF t, TF dt, TF rdt) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF zero = TL.Splat(0f), one = TL.Splat(1f), two = TL.Splat(2f);
            // After the wrap: u = t / dt in [0, 2).
            TF u = TL.Mul(t, rdt);
            TF u2 = TL.Mul(u, u), u3 = TL.Mul(u2, u), u4 = TL.Mul(u2, u2);
            TF a0 = TL.Add(TL.Sub(TL.Sub(TL.Splat(-0.5f), TL.Mul(u3, TL.Splat(1f / 3f))), zero), TL.Add(TL.Mul(u, TL.Splat(2f / 3f)), TL.Mul(u4, TL.Splat(0.125f))));
            TF w = TL.Sub(two, u), w2 = TL.Mul(w, w);
            TF a1 = TL.Mul(TL.Mul(w2, w2), TL.Splat(-1f / 24f));
            TF after = TL.Select(TL.Greater(one, u), a0, TL.Select(TL.Greater(two, u), a1, zero));
            // Before it: v = (t − 1) / dt in (−2, 0].
            TF v = TL.Mul(TL.Sub(t, one), rdt);
            TF v2 = TL.Mul(v, v), v3 = TL.Mul(v2, v), v4 = TL.Mul(v2, v2);
            TF b0 = TL.Sub(TL.Add(TL.Splat(0.5f), TL.Sub(TL.Mul(v, TL.Splat(2f / 3f)), TL.Mul(v3, TL.Splat(1f / 3f)))), TL.Mul(v4, TL.Splat(0.125f)));
            TF x = TL.Add(v, two), x2 = TL.Mul(x, x);
            TF b1 = TL.Mul(TL.Mul(x2, x2), TL.Splat(1f / 24f));
            TF before = TL.Select(TL.Greater(v, TL.Splat(-1f)), b0, TL.Select(TL.Greater(v, TL.Splat(-2f)), b1, zero));
            TF r = TL.Select(TL.Greater(two, u), after, before);     // u < 2 only just after a wrap (t small); otherwise the "before" side
            return TL.Add(r, r);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Osc<TL, TF, TU>(OscJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            int shape = (int)(ctl[NodeInfo.OscShape] + 0.5f);
            float tune = ctl[NodeInfo.OscTune] + 0.01f * ctl[NodeInfo.OscFine], fmAmt = ctl[NodeInfo.OscFm];
            float[] pitchIn = j.Pitch?.Data, fmIn = j.Fm?.Data, o = j.Out.Data;
            int tick = c.Tick;
            float sr = c.SampleRate;
            for (int v = 0; v < V; v++) if (c.Fresh[v]) { j.Phase[v] = 0f; j.LastNote[v] = float.NaN; }
            TF one = TL.Splat(1f), zero = TL.Splat(0f), top = TL.Splat(0.99999994f), tiny = TL.Splat(1e-6f), maxInc = TL.Splat(0.45f);
            bool hq = ctl[NodeInfo.OscQuality] > 0.5f;
            TF pw = TL.Splat(ctl[NodeInfo.OscPulse]), lvl = TL.Splat(ctl[NodeInfo.OscLevel]), fm = TL.Splat(fmAmt), half = TL.Splat(0.5f), two = TL.Splat(2f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF ph = TL.Load(j.Phase, vb);
                for (int t = 0; t < B; t += tick)
                {
                    for (int l = 0; l < W; l++)
                    {
                        int v = vb + l;
                        if (!c.Active[v]) continue;
                        float note = (pitchIn != null ? pitchIn[t * V + v] : 69f) + tune;
                        if (note != j.LastNote[v])
                        {
                            j.LastNote[v] = note;
                            double hz = 440.0 * PortableMath.Exp2((note - 69.0) / 12.0);
                            float inc = (float)(hz / sr);
                            j.Inc[v] = inc > 0.45f ? 0.45f : inc;
                        }
                    }
                    TF inc0 = TL.Select(TL.Greater(TL.Load(j.Inc, vb), tiny), TL.Load(j.Inc, vb), tiny);
                    TF rinc0 = TL.Div(one, inc0);
                    for (int i = t; i < t + tick; i++)
                    {
                        TF inc = inc0, rdt = rinc0;
                        if (fmIn != null)
                        {
                            inc = TL.Mul(inc0, TL.Add(one, TL.Mul(fm, TL.Load(fmIn, i * V + vb))));
                            inc = TL.Select(TL.Greater(inc, tiny), inc, tiny);
                            inc = TL.Select(TL.Greater(inc, maxInc), maxInc, inc);
                            rdt = TL.Div(one, inc);
                        }
                        ph = TL.Add(ph, inc);
                        ph = TL.Select(TL.Greater(ph, top), TL.Sub(ph, one), ph);
                        TF y;
                        if (shape == 0)
                        {
                            y = TL.Sub(TL.Sub(TL.Mul(two, ph), one), hq ? Blep4<TL, TF, TU>(ph, inc, rdt) : Blep<TL, TF, TU>(ph, inc, rdt));
                        }
                        else if (shape == 1)
                        {
                            TF lo = TL.Greater(pw, ph);
                            TF ph2 = TL.Select(lo, TL.Sub(TL.Add(ph, one), pw), TL.Sub(ph, pw));
                            y = TL.Select(lo, one, TL.Splat(-1f));
                            y = TL.Add(y, hq ? Blep4<TL, TF, TU>(ph, inc, rdt) : Blep<TL, TF, TU>(ph, inc, rdt));
                            y = TL.Sub(y, hq ? Blep4<TL, TF, TU>(ph2, inc, rdt) : Blep<TL, TF, TU>(ph2, inc, rdt));
                        }
                        else
                        {
                            // sin(2π·ph) = sin(π·y), y = 2q on q = ph folded into [−¼, ¼]; the Taylor series to y¹¹ (error 6e-8 at |y| = ½).
                            TF q = TL.Select(TL.Greater(ph, half), TL.Sub(ph, one), ph);
                            q = TL.Select(TL.Greater(q, TL.Splat(0.25f)), TL.Sub(half, q), q);
                            q = TL.Select(TL.Greater(TL.Splat(-0.25f), q), TL.Sub(TL.Splat(-0.5f), q), q);
                            TF yy = TL.Mul(two, q), y2 = TL.Mul(yy, yy);
                            TF s = TL.Add(TL.Splat(-0.0073704309f), zero);
                            s = TL.Add(TL.Splat(0.082145886f), TL.Mul(y2, s));
                            s = TL.Add(TL.Splat(-0.59926453f), TL.Mul(y2, s));
                            s = TL.Add(TL.Splat(2.5501640f), TL.Mul(y2, s));
                            s = TL.Add(TL.Splat(-5.1677128f), TL.Mul(y2, s));
                            s = TL.Add(TL.Splat(3.14159265f), TL.Mul(y2, s));
                            y = TL.Mul(yy, s);
                        }
                        TL.Store(TL.Mul(y, lvl), o, i * V + vb);
                    }
                }
                TL.Store(ph, j.Phase, vb);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Env<TL, TF, TU>(EnvJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            var es = j.Settings;
            es.A = ctl[NodeInfo.EnvA]; es.D = ctl[NodeInfo.EnvD]; es.S = ctl[NodeInfo.EnvS]; es.R = ctl[NodeInfo.EnvR];
            int tick = c.Tick;
            double dt = tick / (double)c.SampleRate;
            NoteEnvelope.Factors(es, dt, out double ka, out double kd, out double kr);
            float[] gateIn = j.Gate?.Data, o = j.Out.Data;
            for (int v = 0; v < V; v++) if (c.Fresh[v]) { j.Stage[v] = NoteEnvelope.Idle; j.Level[v] = 0f; }
            float inv = 1f / tick;
            for (int t = 0; t < B; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) { j.Start[v] = 0; j.Step[v] = 0; continue; }
                    bool gate = gateIn != null && gateIn[t * V + v] > 0.5f;
                    if (gate && (j.Stage[v] == NoteEnvelope.Idle || j.Stage[v] == NoteEnvelope.Release)) j.Stage[v] = NoteEnvelope.Attack;
                    float before = j.Level[v];
                    float after = NoteEnvelope.Advance(ref j.Stage[v], ref j.Level[v], gate, es, dt, ka, kd, kr);
                    j.Start[v] = before;
                    j.Step[v] = (after - before) * inv;
                }
                for (int g = 0; g < PatchContext.Groups; g++)
                {
                    if (!c.GroupOn[g]) continue;
                    int vb = W * g;
                    TF a = TL.Load(j.Start, vb), d = TL.Load(j.Step, vb);
                    for (int i = t; i < t + tick; i++)
                    {
                        a = TL.Add(a, d);
                        TL.Store(a, o, i * V + vb);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Filter<TL, TF, TU>(FilterJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            int mode = (int)(ctl[NodeInfo.FilterMode] + 0.5f);
            float cutoff = ctl[NodeInfo.FilterCutoff], depth = ctl[NodeInfo.FilterMod];
            float kd = 1f / Math.Max(0.5f, ctl[NodeInfo.FilterReso]);
            float[] inp = j.In?.Data, cv = j.Cutoff?.Data, o = j.Out.Data;
            int tick = c.Tick;
            float sr = c.SampleRate;
            for (int v = 0; v < V; v++) if (c.Fresh[v]) { j.Ic1[v] = 0f; j.Ic2[v] = 0f; }
            TF two = TL.Splat(2f), kV = TL.Splat(kd), zero = TL.Splat(0f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF ic1 = TL.Load(j.Ic1, vb), ic2 = TL.Load(j.Ic2, vb);
                for (int t = 0; t < B; t += tick)
                {
                    if (cv != null || t == 0)
                    {
                        for (int l = 0; l < W; l++)
                        {
                            int v = vb + l;
                            if (!c.Active[v]) continue;
                            float hz = cv != null ? cutoff * (float)PortableMath.Exp2(depth * cv[t * V + v]) : cutoff;
                            float gg = Svf.Prewarp(hz, sr);
                            float a1 = 1f / (1f + gg * (gg + kd));
                            j.A1[v] = a1; j.A2[v] = gg * a1; j.A3[v] = gg * gg * a1;
                        }
                    }
                    TF A1 = TL.Load(j.A1, vb), A2 = TL.Load(j.A2, vb), A3 = TL.Load(j.A3, vb);
                    for (int i = t; i < t + tick; i++)
                    {
                        int o2 = i * V + vb;
                        TF v0 = inp != null ? TL.Load(inp, o2) : zero;
                        TF v3 = TL.Sub(v0, ic2);
                        TF v1 = TL.Add(TL.Mul(A1, ic1), TL.Mul(A2, v3));
                        TF v2 = TL.Add(TL.Add(ic2, TL.Mul(A2, ic1)), TL.Mul(A3, v3));
                        ic1 = TL.Sub(TL.Mul(two, v1), ic1);
                        ic2 = TL.Sub(TL.Mul(two, v2), ic2);
                        TF y = mode == 0 ? v2 : mode == 1 ? TL.Mul(kV, v1) : TL.Sub(TL.Sub(v0, TL.Mul(kV, v1)), v2);
                        TL.Store(y, o, o2);
                    }
                }
                TL.Store(ic1, j.Ic1, vb);
                TL.Store(ic2, j.Ic2, vb);
                // Denormals and anything that blew up: flushed or zeroed here, once per block.
                for (int l = 0; l < W; l++) { j.Ic1[vb + l] = CleanState(j.Ic1[vb + l]); j.Ic2[vb + l] = CleanState(j.Ic2[vb + l]); }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Vca<TL, TF, TU>(VcaJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            float[] inp = j.In?.Data, cv = j.Cv?.Data, o = j.Out.Data;
            TF gain = TL.Splat(j.Ctl[NodeInfo.VcaGain]), zero = TL.Splat(0f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                for (int i = 0; i < B; i++)
                {
                    int p = i * V + vb;
                    TF x = inp != null ? TL.Load(inp, p) : zero;
                    x = TL.Mul(x, gain);
                    if (cv != null) x = TL.Mul(x, TL.Load(cv, p));
                    TL.Store(x, o, p);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Mix<TL, TF, TU>(MixJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var dest = j.Dest.Data;
            var inputs = j.Inputs;
            if (j.Dest.PerVoice)
            {
                for (int g = 0; g < PatchContext.Groups; g++)
                {
                    if (!c.GroupOn[g]) continue;
                    int vb = W * g;
                    for (int i = 0; i < B; i++)
                    {
                        TF acc = TL.Splat(0f);
                        for (int w = 0; w < inputs.Length; w++)
                        {
                            var s = inputs[w];
                            TF x = s.Source.PerVoice ? TL.Load(s.Source.Data, i * V + vb) : TL.Splat(s.Source.Data[i]);
                            acc = TL.Add(acc, s.Amount == 1f ? x : TL.Mul(x, TL.Splat(s.Amount)));
                        }
                        TL.Store(acc, dest, i * V + vb);
                    }
                }
                return;
            }
            // The voice sum: gains ramped across the block (steal fades, and 0 for a voice that is not sounding), guarded, summed 0…7 then 8…15.
            TF peak0 = TL.Splat(0f), peak1 = TL.Splat(0f);
            TF g0a = TL.Load(c.Gain0, 0), g1a = TL.Load(c.Gain1, 0), g0b = TL.Load(c.Gain0, W), g1b = TL.Load(c.Gain1, W);
            TF stepA = TL.Mul(TL.Sub(g1a, g0a), TL.Splat(1f / B)), stepB = TL.Mul(TL.Sub(g1b, g0b), TL.Splat(1f / B));
            bool second = c.GroupOn[1];
            for (int i = 0; i < B; i++)
            {
                g0a = TL.Add(g0a, stepA);
                g0b = TL.Add(g0b, stepB);
                float total = 0f;
                for (int w = 0; w < inputs.Length; w++)
                {
                    var s = inputs[w];
                    if (!s.Source.PerVoice) { total += s.Source.Data[i] * s.Amount; continue; }
                    TF xa = Clean<TL, TF, TU>(TL.Load(s.Source.Data, i * V));
                    TF absA = TL.Select(TL.Greater(xa, TL.Splat(0f)), xa, TL.Sub(TL.Splat(0f), xa));
                    peak0 = TL.Select(TL.Greater(absA, peak0), absA, peak0);
                    float sum = TL.Sum(TL.Mul(xa, g0a));
                    if (second)
                    {
                        TF xb = Clean<TL, TF, TU>(TL.Load(s.Source.Data, i * V + W));
                        TF absB = TL.Select(TL.Greater(xb, TL.Splat(0f)), xb, TL.Sub(TL.Splat(0f), xb));
                        peak1 = TL.Select(TL.Greater(absB, peak1), absB, peak1);
                        sum += TL.Sum(TL.Mul(xb, g0b));
                    }
                    total += sum * s.Amount;
                }
                dest[i] = total;
            }
            // Loudness for the allocator and the voices' tails (the largest over the voice sums that feed the patch).
            for (int l = 0; l < W; l++)
            {
                c.Peak[l] = Math.Max(c.Peak[l], TL.Lane(peak0, l));
                c.Peak[W + l] = Math.Max(c.Peak[W + l], TL.Lane(peak1, l));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void FbWrite<TL, TF, TU>(FeedbackWriteJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var st = j.State.Data;
            TF gain = TL.Splat(j.Ctl[NodeInfo.FeedbackGain]), lim = TL.Splat(16f), nlim = TL.Splat(-16f), tiny = TL.Splat(1e-18f), ntiny = TL.Splat(-1e-18f), zero = TL.Splat(0f);
            var inp = j.In?.Data;
            for (int i = 0; i < st.Length; i += W)
            {
                if (inp == null) { TL.Store(zero, st, i); continue; }
                TF x = Clean<TL, TF, TU>(TL.Mul(TL.Load(inp, i), gain));
                x = TL.Select(TL.Greater(x, lim), lim, x);
                x = TL.Select(TL.Greater(nlim, x), nlim, x);
                // A denormal flushes to zero (no Abs: both sides).
                x = TL.Select(TL.Greater(tiny, x), TL.Select(TL.Greater(x, ntiny), zero, x), x);
                TL.Store(x, st, i);
            }
        }
    }
}
