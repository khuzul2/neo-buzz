#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.4): the block jobs. A compiled patch is an ordered list of jobs; each job runs one node
// for one 64-sample block across all voices. Every buffer is [sample][16 voices] (sample-major), so eight voices are one vector: the
// kernels are Dye Essence's lane kernels' way (Lanes.cs: Avx2, two Vector128, or plain floats, same bits). A global node's buffers
// use voice slot 0 only and its job runs in a view of the context with just that voice on, so one set of kernels serves both
// scopes. A node's inputs are read where they're used (<see cref="Inp"/>: the wires' sum), not mixed into a buffer first. Everything
// a job touches is allocated when the patch is compiled; Run allocates nothing.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Sampler;

namespace NeoBuzz.Patch
{
    /// <summary>What every job sees: the sample rate, the voices of this block and the stereo output being summed.</summary>
    internal sealed class PatchContext
    {
        public const int Block = 64, MaxVoices = 16, Width = 8, Groups = MaxVoices / Width;

        public LaneKind Kind;
        public float SampleRate = 48000;
        /// <summary>Control interval, samples: pitches, cutoffs and envelope levels are read and updated here, with their values interpolated across it.</summary>
        public int Tick = 16;
        /// <summary>The patch's "audio-rate modulation" switch: control ports are read for every sample (cost: docs/specs/neo-patch-engine.md).</summary>
        public bool AudioMod;
        /// <summary>Voices a per-voice delay line is allocated for (the machine's Voices setting when the patch was compiled).</summary>
        public int VoiceSlots = MaxVoices;
        /// <summary>The control interval in effect: 1 with audio-rate modulation.</summary>
        public int CtlTick => AudioMod ? 1 : Tick;

        // The view jobs read: the voices' (default), or the one slot of a global node.
        public bool[] GroupOn, Active, Fresh;
        public readonly bool[] VGroupOn = new bool[Groups], VActive = new bool[MaxVoices], VFresh = new bool[MaxVoices];
        readonly bool[] gGroupOn = { true, false }, gActive = new bool[MaxVoices], gFresh = new bool[MaxVoices];

        public readonly float[] Pitch0 = new float[MaxVoices], Pitch1 = new float[MaxVoices], Vel = new float[MaxVoices], Gate = new float[MaxVoices];
        public readonly float[] Press0 = new float[MaxVoices], Press1 = new float[MaxVoices], Slide0 = new float[MaxVoices], Slide1 = new float[MaxVoices];
        public readonly float[] Gain0 = new float[MaxVoices], Gain1 = new float[MaxVoices];
        public readonly float[] Peak = new float[MaxVoices];
        /// <summary>Global Keys: the last note and whether any key is held.</summary>
        public float GPitch0, GPitch1, GGate, GVel;
        /// <summary>The 16 macros, 0 … 1, smoothed.</summary>
        public readonly float[] Macros = new float[16];
        /// <summary>The user's own sample (the Sample node's source 5), or null.</summary>
        public volatile SampleLayer UserSample;
        public readonly float[] OutL = new float[Block], OutR = new float[Block];

        public PatchContext()
        {
            gActive[0] = true;
            LeaveGlobal();
        }

        public void EnterGlobal(bool fresh)
        {
            gFresh[0] = fresh;
            GroupOn = gGroupOn; Active = gActive; Fresh = gFresh;
        }

        public void LeaveGlobal()
        {
            GroupOn = VGroupOn; Active = VActive; Fresh = VFresh;
        }
    }

    internal sealed class PortBuffer
    {
        public readonly float[] Data;
        /// <summary>One value for each voice; false: global (slot 0 only).</summary>
        public readonly bool PerVoice;
        public PortBuffer(bool perVoice) { PerVoice = perVoice; Data = new float[PatchContext.Block * PatchContext.MaxVoices]; }
    }

    /// <summary>A node's input: the sum of its wires (each source scaled by the wire's amount), read where the node uses it.</summary>
    internal sealed class Inp
    {
        const int V = PatchContext.MaxVoices;
        public readonly float[][] Data;
        public readonly float[] Amt;
        /// <summary>A global source into a per-voice node: every voice reads slot 0.</summary>
        public readonly bool[] Bcast;
        public readonly int N;
        /// <summary>One source, amount 1, same scope: read it as it is.</summary>
        public readonly bool Direct;
        public readonly float[] D0;

        public Inp(List<(PortBuffer Src, float Amount)> sources, bool consumerPerVoice)
        {
            N = sources.Count;
            Data = new float[N][]; Amt = new float[N]; Bcast = new bool[N];
            for (int k = 0; k < N; k++)
            {
                Data[k] = sources[k].Src.Data; Amt[k] = sources[k].Amount;
                Bcast[k] = consumerPerVoice && !sources[k].Src.PerVoice;
            }
            Direct = N == 1 && Amt[0] == 1f && !Bcast[0];
            D0 = N > 0 ? Data[0] : null;
        }

        /// <summary>The sum at one sample for one voice (control reads).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float At(int i, int v)
        {
            if (Direct) return D0[i * V + v];
            float s = 0f;
            for (int k = 0; k < N; k++) s += Amt[k] * Data[k][i * V + (Bcast[k] ? 0 : v)];
            return s;
        }

        /// <summary>The sum at one sample for the eight voices from <paramref name="vb"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TF Load<TL, TF, TU>(int i, int vb) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            if (Direct) return TL.Load(D0, i * V + vb);
            TF acc = TL.Splat(0f);
            for (int k = 0; k < N; k++)
            {
                TF s = Bcast[k] ? TL.Splat(Data[k][i * V]) : TL.Load(Data[k], i * V + vb);
                acc = Amt[k] == 1f ? TL.Add(acc, s) : TL.Add(acc, TL.Mul(s, TL.Splat(Amt[k])));
            }
            return acc;
        }
    }

    internal abstract class Job
    {
        public int NodeId;
        /// <summary>Runs in the global view (one voice slot).</summary>
        public bool Global;
        /// <summary>Its state has run before (a node that survived an edit, or a block already run): voices start fresh only on their own note-on.</summary>
        public bool Started;
        public abstract void Run(PatchContext c);

        /// <summary>Takes over a job of the same node from the patch being replaced: its state (phases, filters, envelopes, delay lines) carries on.</summary>
        public virtual bool Adopt(Job old) => false;
    }

    internal static class JobScope
    {
        public static bool SameScope(Job a, Job b) => a.Global == b.Global;
    }

    // ---------------------------------------------------------------- jobs (state; the kernels are in PatchKernels.cs)

    internal sealed class KeysJob : Job
    {
        public PortBuffer Pitch, Gate, Vel, Press, Slide;
        public override void Run(PatchContext c) => Kernels.Keys(this, c);
    }

    internal sealed class GlobalKeysJob : Job
    {
        public PortBuffer Pitch, Gate, Vel;
        public override void Run(PatchContext c) => Kernels.GlobalKeys(this, c);
    }

    /// <summary>An Osc or a Wavetable Osc: the same kernel (the table read, the phase, the FM, PM and sync), told apart by <see cref="Wt"/>.</summary>
    internal sealed class OscJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public bool Wt;
        public Inp Pitch, Fm, Pm, Sync, Aux;
        public PortBuffer Out;
        public float[] Phase = new float[V], Inc = new float[V], Drift = new float[V], LastSync = new float[V];
        public uint[] DriftRng = new uint[V];
        // Per tick, per voice (set by the scalar half, read by the lanes).
        public readonly float[] IncStart = new float[V], IncStep = new float[V], Len = new float[V], Pw = new float[V], PwGain = new float[V], FrameMix = new float[V];
        public readonly uint[] Off = new uint[V], OffB = new uint[V];
        public Smooth SmTune, SmLevel, SmPulse;
        public OscJob() { for (int v = 0; v < V; v++) { Inc[v] = float.NaN; DriftRng[v] = 0x9E3779B9u * (uint)(v + 1); } }

        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Osc<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Osc<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Osc<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is OscJob o) || o.Wt != Wt || o.Global != Global) return false;
            Phase = o.Phase; Inc = o.Inc; Drift = o.Drift; LastSync = o.LastSync; DriftRng = o.DriftRng;
            SmTune.Adopt(o.SmTune); SmLevel.Adopt(o.SmLevel); SmPulse.Adopt(o.SmPulse);
            return true;
        }
    }

    internal sealed class EnvJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp Gate;
        public PortBuffer Out;
        public readonly EnvSettings Settings = new EnvSettings();
        public int[] Stage = new int[V], Wait = new int[V];
        public float[] Level = new float[V];
        /// <summary>Per voice: 0 normal, 1 waiting out the delay, 2 holding at the top.</summary>
        public int[] Mode = new int[V];
        public float[] Left = new float[V], LastOut = new float[V];
        public readonly float[] Start = new float[V], Step = new float[V];
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Env<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Env<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Env<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is EnvJob o) || o.Global != Global) return false;
            Stage = o.Stage; Level = o.Level; Mode = o.Mode; Left = o.Left; Wait = o.Wait; LastOut = o.LastOut;
            return true;
        }
    }

    internal sealed class FilterJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public const int Rows = 9;
        public float[] Ctl;
        public Inp In, Cutoff, Reso;
        public PortBuffer Out;
        public float[] S1 = new float[V], S2 = new float[V], S3 = new float[V], S4 = new float[V];
        /// <summary>The coefficients now and their change per sample over the tick (SVF: a1, a2, a3, damping; ladder: see the kernel).</summary>
        public float[][] Cur = NewRows(), Step = NewRows(), Targ = NewRows();
        public bool[] Primed = new bool[V];
        public int LastMode = -1;
        public Smooth SmCut, SmReso;

        static float[][] NewRows() { var r = new float[Rows][]; for (int i = 0; i < Rows; i++) r[i] = new float[V]; return r; }

        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Filter<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Filter<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Filter<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is FilterJob o) || o.Global != Global) return false;
            S1 = o.S1; S2 = o.S2; S3 = o.S3; S4 = o.S4; Cur = o.Cur; Primed = o.Primed; LastMode = o.LastMode;
            SmCut.Adopt(o.SmCut); SmReso.Adopt(o.SmReso);
            return true;
        }
    }

    internal sealed class VcaJob : Job
    {
        public float[] Ctl;
        public Inp In, Cv;
        public PortBuffer Out;
        public Smooth SmGain;
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Vca<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Vca<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Vca<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is VcaJob o)) return false; SmGain.Adopt(o.SmGain); return true; }
    }

    internal sealed class MixerJob : Job
    {
        public float[] Ctl;
        public Inp[] In = new Inp[4];
        public PortBuffer Out;
        public Smooth[] Sm = new Smooth[4];
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Mixer<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Mixer<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Mixer<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is MixerJob o)) return false; Sm = o.Sm; return true; }
    }

    internal sealed class MathJob : Job
    {
        public float[] Ctl;
        public Inp A, B;
        public PortBuffer Out;
        public Smooth SmScale, SmOffset;
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.MathOp<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.MathOp<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.MathOp<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is MathJob o)) return false; SmScale.Adopt(o.SmScale); SmOffset.Adopt(o.SmOffset); return true; }
    }

    internal sealed class CrossfadeJob : Job
    {
        public float[] Ctl;
        public Inp A, B, Mix;
        public PortBuffer Out;
        public Smooth SmMix;
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Crossfade<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Crossfade<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Crossfade<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is CrossfadeJob o)) return false; SmMix.Adopt(o.SmMix); return true; }
    }

    internal sealed class ShaperJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp In, Drive, Pitch;
        public PortBuffer Out;
        public Smooth SmDrive, SmBias, SmLevel;
        public readonly float[] G0 = new float[V], Gs = new float[V];
        public float[] GEnd = new float[V];
        public bool[] GInit = new bool[V];
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Shaper<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Shaper<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Shaper<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is ShaperJob o)) return false; GEnd = o.GEnd; GInit = o.GInit; SmDrive.Adopt(o.SmDrive); SmBias.Adopt(o.SmBias); SmLevel.Adopt(o.SmLevel); return true; }
    }

    /// <summary>
    /// The voice sum: each voice's gain ramp (steal fades, and 0 for a voice that is not sounding), the guard and the peak, summed in
    /// one fixed order into a global buffer. The compiler puts one wherever a per-voice signal reaches a global node.
    /// </summary>
    internal sealed class SumJob : Job
    {
        public PortBuffer Src, Dest;
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Sum<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Sum<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Sum<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }
    }

    /// <summary>Out: up to four inputs, each with its own pan, a per-voice spread, and the level. Per-voice inputs are summed here (the voice sum, fused).</summary>
    internal sealed class OutJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        /// <summary>Each input's per-voice wires and its global wires, summed apart (the voice sum is fused in here).</summary>
        public Inp[] InV = new Inp[4], InG = new Inp[4];
        public Smooth SmLevel;
        public readonly float[] PanL = new float[4 * V], PanR = new float[4 * V], GlobL = new float[4], GlobR = new float[4];
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.Out<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.Out<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.Out<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }

        public override bool Adopt(Job old) { if (!(old is OutJob o)) return false; SmLevel.Adopt(o.SmLevel); return true; }
    }

    /// <summary>The Feedback node's two halves: Read (the previous block, first in the order) and Write (this block's input, after its source).</summary>
    internal sealed class FeedbackState
    {
        public readonly float[] Data = new float[PatchContext.Block * PatchContext.MaxVoices];
        public readonly bool PerVoice;
        public FeedbackState(bool perVoice) { PerVoice = perVoice; }
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

        public override bool Adopt(Job old)
        {
            if (!(old is FeedbackReadJob o) || o.State.PerVoice != State.PerVoice) return false;
            State = o.State;
            return true;
        }
    }

    internal sealed class FeedbackWriteJob : Job
    {
        public float[] Ctl;
        public FeedbackState State;
        public Inp In;
        public override void Run(PatchContext c)
        {
            switch (c.Kind)
            {
                case LaneKind.Avx2: Kernels.FbWrite<Avx2Lanes, System.Runtime.Intrinsics.Vector256<float>, System.Runtime.Intrinsics.Vector256<uint>>(this, c); break;
                case LaneKind.Scalar: Kernels.FbWrite<ScalarLanes, Eight<float>, Eight<uint>>(this, c); break;
                default: Kernels.FbWrite<PairLanes, Pair<float>, Pair<uint>>(this, c); break;
            }
        }
    }
}
