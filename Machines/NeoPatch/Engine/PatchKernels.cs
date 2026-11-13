#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.4): the lane kernels of the audio nodes. Each runs across the voices that sound, eight at
// a time, in the three lane implementations of Lanes.cs (the same bits). The oscillator reads Dye Essence's band-limited tables
// (the mip level for the pitch, a 4-point cubic read); the filter is the Simper SVF and Neo Poly's ladder with their coefficients
// worked out once per control interval and stepped across it.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using Recipes = NeoBuzz.Wave.Tables;

namespace NeoBuzz.Patch
{
    /// <summary>The oscillator's saw and triangle as Dye Essence's mip-mapped tables (11 band-limited levels by FFT), built once per process.</summary>
    internal static class BasicTables
    {
        public static readonly TableArena Saw = new TableArena(Wavetable.FromHarmonics("saw", 1, (f, h) => (1.0 / h, Math.PI)));
        public static readonly TableArena Triangle = new TableArena(Wavetable.FromHarmonics("triangle", 1, (f, h) => (h % 2 == 0 ? 0.0 : ((h / 2) % 2 == 0 ? 1.0 : -1.0) / ((double)h * h), 0.0)));
    }

    internal static class Kernels
    {
        const int B = PatchContext.Block, V = PatchContext.MaxVoices, W = PatchContext.Width;
        const float Limit = 0.45f;
        /// <summary>Neo Learn's fold bandwidth (Warps.FoldBandwidth): the gain times the pitch (as a fraction of the sample rate) a fold may reach.</summary>
        const double FoldBandwidth = 0.022;

        /// <summary>The voices' offset in an Out's spread, −1 … 1: a golden-ratio sequence, so the first voices already lie far apart.</summary>
        static readonly float[] VoiceOffset = MakeOffsets();
        /// <summary>Builds the static tables now (the compile does it, off the audio thread).</summary>
        public static void Warm() { _ = BasicTables.Saw; _ = BasicTables.Triangle; _ = VoiceOffset; }
        static float[] MakeOffsets() { var o = new float[V]; for (int v = 0; v < V; v++) { double x = (v + 1) * 0.6180339887498949; o[v] = (float)((x - Math.Floor(x)) * 2.0 - 1.0); } return o; }

        // ------------------------------------------------------------ small lane helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Clean<TL, TF, TU>(TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF a = TL.Select(TL.Greater(x, TL.Splat(0f)), x, TL.Sub(TL.Splat(0f), x));
            return TL.Select(TL.Greater(TL.Splat(1e9f), a), x, TL.Splat(0f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Abs<TL, TF, TU>(TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct =>
            TL.Select(TL.Greater(x, TL.Splat(0f)), x, TL.Sub(TL.Splat(0f), x));

        /// <summary>tanh as the rational approximation of DspMath.FastTanh, the input held to ±4.97.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Tanh<TL, TF, TU>(TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF hi = TL.Splat(4.97f), lo = TL.Splat(-4.97f);
            x = TL.Select(TL.Greater(x, hi), hi, TL.Select(TL.Greater(lo, x), lo, x));
            TF x2 = TL.Mul(x, x);
            TF a = TL.Mul(x, TL.Add(TL.Splat(135135f), TL.Mul(x2, TL.Add(TL.Splat(17325f), TL.Mul(x2, TL.Add(TL.Splat(378f), x2))))));
            TF b = TL.Add(TL.Splat(135135f), TL.Mul(x2, TL.Add(TL.Splat(62370f), TL.Mul(x2, TL.Add(TL.Splat(3150f), TL.Mul(x2, TL.Splat(28f)))))));
            return TL.Div(a, b);
        }

        /// <summary>The 4-point Catmull-Rom read of NeoBuzz.Dsp.Wavetable's order of operations.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Cubic<TL, TF, TU>(TF y0, TF y1, TF y2, TF y3, TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF c1 = TL.Mul(half, TL.Sub(y2, y0));
            TF c2 = TL.Sub(TL.Add(TL.Sub(y0, TL.Mul(twoHalf, y1)), TL.Mul(two, y2)), TL.Mul(half, y3));
            TF c3 = TL.Add(TL.Mul(half, TL.Sub(y3, y0)), TL.Mul(oneHalf, TL.Sub(y1, y2)));
            return TL.Add(TL.Mul(TL.Add(TL.Mul(TL.Add(TL.Mul(c3, x), c2), x), c1), x), y1);
        }

        /// <summary>p − floor(p): a phase back into [0, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Wrap<TL, TF, TU>(TF p) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF t = TL.IntToFloat(TL.Truncate(p));
            TF f = TL.Sub(p, t);
            f = TL.Select(TL.Greater(TL.Splat(0f), f), TL.Add(f, TL.Splat(1f)), f);
            return TL.Select(TL.Greater(f, TL.Splat(0.99999994f)), TL.Splat(0f), f);
        }

        /// <summary>One band-limited read of a table at phase <paramref name="p"/> ∈ [0, 1): the lane's level's length and offset.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Read<TL, TF, TU>(ref float table, TF p, TF len, TU off) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF pos = TL.Mul(p, len);
            TU idx = TL.Truncate(pos);
            TF x = TL.Sub(pos, TL.IntToFloat(idx));
            TL.Read4(ref table, TL.AddU(idx, off), out TF y0, out TF y1, out TF y2, out TF y3);
            return Cubic<TL, TF, TU>(y0, y1, y2, y3, x);
        }

        // ------------------------------------------------------------ notes

        public static void Keys(KeysJob j, PatchContext c)
        {
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                float p = c.Pitch0[v], dp = (c.Pitch1[v] - p) * (1f / B);
                float pr = c.Press0[v], dpr = (c.Press1[v] - pr) * (1f / B);
                float sl = c.Slide0[v], dsl = (c.Slide1[v] - sl) * (1f / B);
                float gate = c.Gate[v], vel = c.Vel[v];
                for (int i = 0; i < B; i++)
                {
                    p += dp; pr += dpr; sl += dsl;
                    int o = i * V + v;
                    if (j.Pitch != null) j.Pitch.Data[o] = p;
                    if (j.Gate != null) j.Gate.Data[o] = gate;
                    if (j.Vel != null) j.Vel.Data[o] = vel;
                    if (j.Press != null) j.Press.Data[o] = pr;
                    if (j.Slide != null) j.Slide.Data[o] = sl;
                }
            }
        }

        public static void GlobalKeys(GlobalKeysJob j, PatchContext c)
        {
            float p = c.GPitch0, dp = (c.GPitch1 - p) * (1f / B);
            for (int i = 0; i < B; i++)
            {
                p += dp;
                int o = i * V;
                if (j.Pitch != null) j.Pitch.Data[o] = p;
                if (j.Gate != null) j.Gate.Data[o] = c.GGate;
                if (j.Vel != null) j.Vel.Data[o] = c.GVel;
            }
        }

        // ------------------------------------------------------------ oscillators

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Osc<TL, TF, TU>(OscJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            bool wt = j.Wt;
            int tick = c.CtlTick;
            float sr = c.SampleRate;
            int shape = (int)(ctl[NodeInfo.OscShape] + 0.5f);
            TableArena arena = wt ? TableBank.Get(shape) : null;
            var o = j.Out.Data;
            if (wt && arena == null)
            {
                // The table is still being built (off this thread): silence until it is.
                for (int v = 0; v < V; v++) if (c.Active[v]) for (int i = 0; i < B; i++) o[i * V + v] = 0f;
                return;
            }
            float tune0 = j.SmTune.Begin(ctl[NodeInfo.OscTune] + 0.01f * ctl[NodeInfo.OscFine], out float dTune);
            float lvl0 = j.SmLevel.Begin(ctl[NodeInfo.OscLevel], out float dLvl);
            float aux0 = j.SmPulse.Begin(ctl[wt ? NodeInfo.WtPosition : NodeInfo.OscPulse], out float dAux);
            float fmAmt = ctl[NodeInfo.OscFm], pmAmt = ctl[NodeInfo.OscPm], drift = ctl[NodeInfo.OscDrift];
            for (int v = 0; v < V; v++)
                if (c.Fresh[v]) { j.Phase[v] = 0f; j.Inc[v] = float.NaN; j.Drift[v] = 0f; j.LastSync[v] = 0f; }
            ref float tab = ref (wt ? ref arena.Data[0] : ref (shape == 3 ? ref BasicTables.Triangle.Data[0] : ref BasicTables.Saw.Data[0]));
            bool useTable = wt || shape == 0 || shape == 1 || shape == 3;
            int frames = wt ? arena.FrameCount : 1;
            TF one = TL.Splat(1f), zero = TL.Splat(0f), top = TL.Splat(0.99999994f), tiny = TL.Splat(1e-6f), maxInc = TL.Splat(0.45f), half = TL.Splat(0.5f), two = TL.Splat(2f);
            TF fm = TL.Splat(fmAmt), pm = TL.Splat(pmAmt);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF ph = TL.Load(j.Phase, vb);
                TF syncPrev = TL.Load(j.LastSync, vb);
                for (int t = 0; t < B; t += tick)
                {
                    // The scalar half: each voice's pitch at the interval's end and what follows from it (the mip level, the table offsets).
                    for (int l = 0; l < W; l++)
                    {
                        int v = vb + l;
                        if (!c.Active[v]) continue;
                        float note = (j.Pitch != null ? j.Pitch.At(t + tick - 1, v) : 69f) + tune0 + dTune * (t + tick);
                        if (drift > 0f)
                        {
                            j.Drift[v] += (Ctl_.Rand(ref j.DriftRng[v]) * 0.02f - j.Drift[v] * 0.0004f);
                            note += drift * 7.3f * j.Drift[v];
                        }
                        float inc1 = PatchMath.Exp2((note - 69f) * (1f / 12f)) * (440f / sr);
                        inc1 = inc1 < 1e-6f ? 1e-6f : inc1 > 0.45f ? 0.45f : inc1;
                        float inc0 = float.IsNaN(j.Inc[v]) ? inc1 : j.Inc[v];
                        float peak = 1f;
                        if (j.Fm != null)
                        {
                            float m = 0f;
                            for (int i = t; i < t + tick; i++) { float a = Math.Abs(j.Fm.At(i, v)); if (a > m) m = a; }
                            peak += fmAmt * m;
                        }
                        int level = Wavetable.LevelFor(Math.Max(inc0, inc1) * peak, Limit);
                        j.IncStart[v] = inc0; j.IncStep[v] = (inc1 - inc0) / tick; j.Inc[v] = inc1;
                        j.Len[v] = Wavetable.LevelLength(level);
                        float aux = aux0 + dAux * (t + tick);
                        if (j.Aux != null) aux += j.Aux.At(t + tick - 1, v) * (wt ? 1f : 0.45f);
                        if (wt)
                        {
                            float f = Math.Clamp(aux, 0f, 1f) * (frames - 1);
                            int fa = (int)f;
                            if (fa >= frames - 1) { fa = Math.Max(0, frames - 2); f = frames - 1; }
                            float mix = frames > 1 ? f - fa : 0f;
                            int fb = Math.Min(fa + 1, frames - 1);
                            j.Off[v] = (uint)TableArena.Offset(fa, level);
                            j.OffB[v] = (uint)TableArena.Offset(fb, level);
                            j.FrameMix[v] = mix;
                        }
                        else
                        {
                            j.Off[v] = (uint)TableArena.Offset(0, level);
                            float pw = Math.Clamp(aux, 0.05f, 0.95f);
                            j.Pw[v] = pw;
                            j.PwGain[v] = 1f / (2f * Math.Max(pw, 1f - pw));
                        }
                    }
                    TF incV = TL.Load(j.IncStart, vb), dInc = TL.Load(j.IncStep, vb), len = TL.Load(j.Len, vb);
                    TU off = TL.LoadU(j.Off, vb), offB = TL.LoadU(j.OffB, vb);
                    TF pwV = TL.Load(j.Pw, vb), pwG = TL.Load(j.PwGain, vb), fmix = TL.Load(j.FrameMix, vb);
                    TF lv = TL.Splat(lvl0 + dLvl * t), dl = TL.Splat(dLvl);
                    for (int i = t; i < t + tick; i++)
                    {
                        incV = TL.Add(incV, dInc);
                        TF inc = incV;
                        if (j.Fm != null)
                        {
                            inc = TL.Mul(incV, TL.Add(one, TL.Mul(fm, j.Fm.Load<TL, TF, TU>(i, vb))));
                            inc = TL.Select(TL.Greater(inc, tiny), inc, tiny);
                            inc = TL.Select(TL.Greater(inc, maxInc), maxInc, inc);
                        }
                        ph = TL.Add(ph, inc);
                        ph = TL.Select(TL.Greater(ph, top), TL.Sub(ph, one), ph);
                        if (j.Sync != null)
                        {
                            // Hard sync: the cycle restarts where the sync input crosses up through zero.
                            TF s = j.Sync.Load<TL, TF, TU>(i, vb);
                            TF rise = TL.Select(TL.Greater(syncPrev, zero), zero, TL.Select(TL.Greater(s, zero), one, zero));
                            ph = TL.Select(TL.Greater(rise, half), zero, ph);
                            syncPrev = s;
                        }
                        TF p = ph;
                        if (j.Pm != null) p = Wrap<TL, TF, TU>(TL.Add(ph, TL.Mul(pm, j.Pm.Load<TL, TF, TU>(i, vb))));
                        TF y;
                        if (wt)
                        {
                            TF ya = Read<TL, TF, TU>(ref tab, p, len, off);
                            TF yb = Read<TL, TF, TU>(ref tab, p, len, offB);
                            y = TL.Add(ya, TL.Mul(TL.Sub(yb, ya), fmix));
                        }
                        else if (shape == 0 || shape == 3) y = Read<TL, TF, TU>(ref tab, p, len, off);
                        else if (shape == 1)
                        {
                            TF p2 = TL.Add(p, pwV);
                            p2 = TL.Select(TL.Greater(p2, top), TL.Sub(p2, one), p2);
                            y = TL.Mul(TL.Sub(Read<TL, TF, TU>(ref tab, p, len, off), Read<TL, TF, TU>(ref tab, p2, len, off)), pwG);
                        }
                        else
                        {
                            // sin(2π·p) = sin(π·y), y = 2q on q = p folded into [−¼, ¼]; the series to y¹¹ (error 6e-8 at |y| = ½).
                            TF q = TL.Select(TL.Greater(p, half), TL.Sub(p, one), p);
                            q = TL.Select(TL.Greater(q, TL.Splat(0.25f)), TL.Sub(half, q), q);
                            q = TL.Select(TL.Greater(TL.Splat(-0.25f), q), TL.Sub(TL.Splat(-0.5f), q), q);
                            TF yy = TL.Mul(two, q), y2 = TL.Mul(yy, yy);
                            TF sn = TL.Splat(-0.0073704309f);
                            sn = TL.Add(TL.Splat(0.082145886f), TL.Mul(y2, sn));
                            sn = TL.Add(TL.Splat(-0.59926453f), TL.Mul(y2, sn));
                            sn = TL.Add(TL.Splat(2.5501640f), TL.Mul(y2, sn));
                            sn = TL.Add(TL.Splat(-5.1677128f), TL.Mul(y2, sn));
                            sn = TL.Add(TL.Splat(3.14159265f), TL.Mul(y2, sn));
                            y = TL.Mul(yy, sn);
                        }
                        lv = TL.Add(lv, dl);
                        TL.Store(TL.Mul(y, lv), o, i * V + vb);
                    }
                }
                TL.Store(ph, j.Phase, vb);
                TL.Store(syncPrev, j.LastSync, vb);
                // Anything that blew up (FM into a runaway) is zeroed once per block.
                for (int l = 0; l < W; l++) { float p = j.Phase[vb + l]; if (!(p >= 0f && p < 1f)) j.Phase[vb + l] = 0f; }
            }
        }

        // ------------------------------------------------------------ envelope

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Env<TL, TF, TU>(EnvJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            var es = j.Settings;
            es.A = ctl[NodeInfo.EnvA]; es.D = ctl[NodeInfo.EnvD]; es.S = ctl[NodeInfo.EnvS]; es.R = ctl[NodeInfo.EnvR];
            float delay = ctl[NodeInfo.EnvDelay], hold = ctl[NodeInfo.EnvHold], curve = ctl[NodeInfo.EnvCurve];
            double curveExp = curve == 0f ? 1.0 : PortableMath.Exp2(-2.0 * curve);
            int tick = c.CtlTick;
            double dt = tick / (double)c.SampleRate;
            NoteEnvelope.Factors(es, dt, out double ka, out double kd, out double kr);
            var o = j.Out.Data;
            for (int v = 0; v < V; v++) if (c.Fresh[v]) { j.Stage[v] = NoteEnvelope.Idle; j.Level[v] = 0f; j.Mode[v] = 0; j.LastOut[v] = 0f; }
            float inv = 1f / tick;
            for (int t = 0; t < B; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) { j.Start[v] = 0; j.Step[v] = 0; continue; }
                    bool gate = j.Gate != null && j.Gate.At(t, v) > 0.5f;
                    int stage = j.Stage[v];
                    float after = 0f;
                    if (j.Mode[v] == 1)
                    {
                        // Waiting out the delay: silent; a gate that closes cancels it.
                        j.Left[v] -= (float)dt;
                        if (!gate) { j.Mode[v] = 0; j.Stage[v] = NoteEnvelope.Idle; }
                        else if (j.Left[v] <= 0f) { j.Mode[v] = 0; j.Stage[v] = NoteEnvelope.Attack; }
                    }
                    else
                    {
                        bool done = false;
                        if (gate && (stage == NoteEnvelope.Idle || stage == NoteEnvelope.Release))
                        {
                            if (delay > 0f) { j.Mode[v] = 1; j.Left[v] = delay; j.Level[v] = 0f; j.Stage[v] = NoteEnvelope.Idle; done = true; }
                            else j.Stage[v] = NoteEnvelope.Attack;
                        }
                        if (!done && j.Mode[v] == 2)
                        {
                            j.Left[v] -= (float)dt;
                            if (!gate || j.Left[v] <= 0f) j.Mode[v] = 0;
                            after = j.Level[v];
                            if (j.Mode[v] == 2) done = true;
                        }
                        if (!done)
                        {
                            int was = j.Stage[v];
                            after = NoteEnvelope.Advance(ref j.Stage[v], ref j.Level[v], gate, es, dt, ka, kd, kr);
                            if (hold > 0f && was == NoteEnvelope.Attack && j.Stage[v] == NoteEnvelope.Decay && gate) { j.Mode[v] = 2; j.Left[v] = hold; j.Level[v] = 1f; after = 1f; }
                        }
                    }
                    float shaped = after;
                    if (curve != 0f && after > 0f) shaped = (float)PortableMath.Pow(after, curveExp);
                    j.Start[v] = j.LastOut[v];
                    j.Step[v] = (shaped - j.LastOut[v]) * inv;
                    j.LastOut[v] = shaped;
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

        // ------------------------------------------------------------ filter

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Filter<TL, TF, TU>(FilterJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            int mode = (int)(ctl[NodeInfo.FilterMode] + 0.5f);
            bool ladder = mode >= 4;
            float depth = ctl[NodeInfo.FilterMod];
            float cut0 = j.SmCut.Begin(ctl[NodeInfo.FilterCutoff], out float dCut);
            float q0 = j.SmReso.Begin(ctl[NodeInfo.FilterReso], out float dQ);
            bool cv = j.Cutoff != null || j.Reso != null;
            bool moving = cv || dCut != 0f || dQ != 0f;
            int tick = cv ? c.CtlTick : c.Tick;
            float sr = c.SampleRate;
            var o = j.Out.Data;
            if (mode != j.LastMode) { j.LastMode = mode; for (int v = 0; v < V; v++) j.Primed[v] = false; }
            for (int v = 0; v < V; v++)
                if (c.Fresh[v]) { j.S1[v] = 0f; j.S2[v] = 0f; j.S3[v] = 0f; j.S4[v] = 0f; j.Primed[v] = false; }
            TF two = TL.Splat(2f), zero = TL.Splat(0f), half = TL.Splat(0.5f);
            float invTick = 1f / tick;
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF s1 = TL.Load(j.S1, vb), s2 = TL.Load(j.S2, vb), s3 = TL.Load(j.S3, vb), s4 = TL.Load(j.S4, vb);
                for (int t = 0; t < B; t += tick)
                {
                    bool unprimed = false;
                    for (int l = 0; l < W; l++) if (c.Active[vb + l] && !j.Primed[vb + l]) unprimed = true;
                    bool recompute = moving || t == 0 || unprimed;
                    if (recompute)
                    {
                        for (int l = 0; l < W; l++)
                        {
                            int v = vb + l;
                            if (!c.Active[v]) continue;
                            float hz = cut0 + dCut * (t + tick), q = q0 + dQ * (t + tick);
                            if (j.Cutoff != null) hz *= PatchMath.Exp2(depth * j.Cutoff.At(t + tick - 1, v));
                            if (j.Reso != null) q += j.Reso.At(t + tick - 1, v);
                            q = q < 0.5f ? 0.5f : q > 40f ? 40f : q;
                            float gg = PatchMath.TanPi(hz / sr);
                            var tg = j.Targ;
                            if (!ladder)
                            {
                                float kd = 1f / q;
                                float a1 = 1f / (1f + gg * (gg + kd));
                                tg[0][v] = a1; tg[1][v] = gg * a1; tg[2][v] = gg * gg * a1; tg[3][v] = kd;
                            }
                            else
                            {
                                float k = PatchMath.LadderK(q);
                                if (k > 3.999f) k = 3.999f;
                                float G = gg / (1f + gg), g2 = G * G, om = 1f - G;
                                tg[0][v] = G; tg[1][v] = 1f + 0.5f * k; tg[2][v] = k;
                                tg[3][v] = g2 * G * om; tg[4][v] = g2 * om; tg[5][v] = G * om; tg[6][v] = om;
                                tg[7][v] = g2 * g2; tg[8][v] = 1f / (1f + k * g2 * g2);
                            }
                            int rows = ladder ? FilterJob.Rows : 4;
                            for (int r = 0; r < rows; r++)
                            {
                                if (!j.Primed[v]) { j.Cur[r][v] = tg[r][v]; j.Step[r][v] = 0f; }
                                else j.Step[r][v] = (tg[r][v] - j.Cur[r][v]) * invTick;
                            }
                            j.Primed[v] = true;
                        }
                    }
                    if (!ladder)
                    {
                        TF a1 = TL.Load(j.Cur[0], vb), a2 = TL.Load(j.Cur[1], vb), a3 = TL.Load(j.Cur[2], vb), kk = TL.Load(j.Cur[3], vb);
                        TF d1 = TL.Load(j.Step[0], vb), d2 = TL.Load(j.Step[1], vb), d3 = TL.Load(j.Step[2], vb), dk = TL.Load(j.Step[3], vb);
                        for (int i = t; i < t + tick; i++)
                        {
                            TF v0 = j.In != null ? j.In.Load<TL, TF, TU>(i, vb) : zero;
                            TF v3 = TL.Sub(v0, s2);
                            TF v1 = TL.Add(TL.Mul(a1, s1), TL.Mul(a2, v3));
                            TF v2 = TL.Add(TL.Add(s2, TL.Mul(a2, s1)), TL.Mul(a3, v3));
                            s1 = TL.Sub(TL.Mul(two, v1), s1);
                            s2 = TL.Sub(TL.Mul(two, v2), s2);
                            TF y = mode == 0 ? v2 : mode == 1 ? TL.Mul(kk, v1) : mode == 2 ? TL.Sub(TL.Sub(v0, TL.Mul(kk, v1)), v2) : TL.Sub(v0, TL.Mul(kk, v1));
                            TL.Store(y, o, i * V + vb);
                            a1 = TL.Add(a1, d1); a2 = TL.Add(a2, d2); a3 = TL.Add(a3, d3); kk = TL.Add(kk, dk);
                        }
                    }
                    else
                    {
                        TF G = TL.Load(j.Cur[0], vb), gc = TL.Load(j.Cur[1], vb), kl = TL.Load(j.Cur[2], vb);
                        TF c1 = TL.Load(j.Cur[3], vb), c2 = TL.Load(j.Cur[4], vb), c3 = TL.Load(j.Cur[5], vb), c4 = TL.Load(j.Cur[6], vb), g4 = TL.Load(j.Cur[7], vb), id = TL.Load(j.Cur[8], vb);
                        TF dG = TL.Load(j.Step[0], vb), dgc = TL.Load(j.Step[1], vb), dkl = TL.Load(j.Step[2], vb);
                        TF dc1 = TL.Load(j.Step[3], vb), dc2 = TL.Load(j.Step[4], vb), dc3 = TL.Load(j.Step[5], vb), dc4 = TL.Load(j.Step[6], vb), dg4 = TL.Load(j.Step[7], vb), did = TL.Load(j.Step[8], vb);
                        bool lp12 = mode == 5;
                        for (int i = t; i < t + tick; i++)
                        {
                            TF x = j.In != null ? j.In.Load<TL, TF, TU>(i, vb) : zero;
                            TF sigma = TL.Add(TL.Add(TL.Mul(c1, s1), TL.Mul(c2, s2)), TL.Add(TL.Mul(c3, s3), TL.Mul(c4, s4)));
                            TF xin = TL.Mul(TL.Mul(x, gc), half);
                            TF y4est = TL.Mul(TL.Add(TL.Mul(g4, xin), sigma), id);
                            TF u = Tanh<TL, TF, TU>(TL.Sub(xin, TL.Mul(kl, y4est)));
                            TF vv = TL.Mul(TL.Sub(u, s1), G); TF y1 = TL.Add(vv, s1); s1 = TL.Add(y1, vv);
                            vv = TL.Mul(TL.Sub(y1, s2), G); TF y2 = TL.Add(vv, s2); s2 = TL.Add(y2, vv);
                            vv = TL.Mul(TL.Sub(y2, s3), G); TF y3 = TL.Add(vv, s3); s3 = TL.Add(y3, vv);
                            vv = TL.Mul(TL.Sub(y3, s4), G); TF y4 = TL.Add(vv, s4); s4 = TL.Add(y4, vv);
                            TL.Store(TL.Mul(lp12 ? y2 : y4, two), o, i * V + vb);
                            G = TL.Add(G, dG); gc = TL.Add(gc, dgc); kl = TL.Add(kl, dkl);
                            c1 = TL.Add(c1, dc1); c2 = TL.Add(c2, dc2); c3 = TL.Add(c3, dc3); c4 = TL.Add(c4, dc4); g4 = TL.Add(g4, dg4); id = TL.Add(id, did);
                        }
                    }
                    if (recompute)
                    {
                        int rows = ladder ? FilterJob.Rows : 4;
                        for (int l = 0; l < W; l++)
                        {
                            int v = vb + l;
                            if (!c.Active[v]) continue;
                            for (int r = 0; r < rows; r++) { j.Cur[r][v] = j.Targ[r][v]; j.Step[r][v] = 0f; }
                        }
                    }
                }
                TL.Store(s1, j.S1, vb); TL.Store(s2, j.S2, vb); TL.Store(s3, j.S3, vb); TL.Store(s4, j.S4, vb);
                // Denormals and anything that blew up: flushed or zeroed here, once per block.
                for (int l = 0; l < W; l++)
                {
                    j.S1[vb + l] = PatchMath.CleanState(j.S1[vb + l]); j.S2[vb + l] = PatchMath.CleanState(j.S2[vb + l]);
                    j.S3[vb + l] = PatchMath.CleanState(j.S3[vb + l]); j.S4[vb + l] = PatchMath.CleanState(j.S4[vb + l]);
                }
            }
        }

        // ------------------------------------------------------------ plumbing

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Vca<TL, TF, TU>(VcaJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var o = j.Out.Data;
            float g0 = j.SmGain.Begin(j.Ctl[NodeInfo.VcaGain], out float dg);
            TF zero = TL.Splat(0f), dgV = TL.Splat(dg);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF gain = TL.Splat(g0);
                for (int i = 0; i < B; i++)
                {
                    gain = TL.Add(gain, dgV);
                    TF x = j.In != null ? j.In.Load<TL, TF, TU>(i, vb) : zero;
                    x = TL.Mul(x, gain);
                    if (j.Cv != null) x = TL.Mul(x, j.Cv.Load<TL, TF, TU>(i, vb));
                    TL.Store(x, o, i * V + vb);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Mixer<TL, TF, TU>(MixerJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var o = j.Out.Data;
            var i0 = j.In[0]; var i1 = j.In[1]; var i2 = j.In[2]; var i3 = j.In[3];
            float a0 = j.Sm[0].Begin(j.Ctl[0], out float d0), a1 = j.Sm[1].Begin(j.Ctl[1], out float d1), a2 = j.Sm[2].Begin(j.Ctl[2], out float d2), a3 = j.Sm[3].Begin(j.Ctl[3], out float d3);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF g0 = TL.Splat(a0), g1 = TL.Splat(a1), g2 = TL.Splat(a2), g3 = TL.Splat(a3);
                TF s0 = TL.Splat(d0), s1 = TL.Splat(d1), s2 = TL.Splat(d2), s3 = TL.Splat(d3);
                for (int i = 0; i < B; i++)
                {
                    g0 = TL.Add(g0, s0); g1 = TL.Add(g1, s1); g2 = TL.Add(g2, s2); g3 = TL.Add(g3, s3);
                    TF acc = TL.Splat(0f);
                    if (i0 != null) acc = TL.Add(acc, TL.Mul(i0.Load<TL, TF, TU>(i, vb), g0));
                    if (i1 != null) acc = TL.Add(acc, TL.Mul(i1.Load<TL, TF, TU>(i, vb), g1));
                    if (i2 != null) acc = TL.Add(acc, TL.Mul(i2.Load<TL, TF, TU>(i, vb), g2));
                    if (i3 != null) acc = TL.Add(acc, TL.Mul(i3.Load<TL, TF, TU>(i, vb), g3));
                    TL.Store(acc, o, i * V + vb);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void MathOp<TL, TF, TU>(MathJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var o = j.Out.Data;
            int op = (int)(j.Ctl[NodeInfo.MathOp] + 0.5f);
            float s0 = j.SmScale.Begin(j.Ctl[NodeInfo.MathScale], out float ds), f0 = j.SmOffset.Begin(j.Ctl[NodeInfo.MathOffset], out float dof);
            bool bWired = j.B != null;
            TF zero = TL.Splat(0f), one = TL.Splat(1f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF sc = TL.Splat(s0), dsV = TL.Splat(ds), of = TL.Splat(f0), dofV = TL.Splat(dof);
                for (int i = 0; i < B; i++)
                {
                    sc = TL.Add(sc, dsV); of = TL.Add(of, dofV);
                    TF a = TL.Mul(j.A != null ? j.A.Load<TL, TF, TU>(i, vb) : zero, sc);
                    TF b = bWired ? TL.Add(j.B.Load<TL, TF, TU>(i, vb), of) : of;
                    TF y;
                    switch (op)
                    {
                        case 0: y = TL.Add(a, b); break;
                        case 1: y = TL.Mul(a, bWired ? b : TL.Add(one, of)); break;
                        case 2: y = TL.Add(a, of); break;
                        case 3: y = TL.Select(TL.Greater(a, b), b, a); break;
                        case 4: y = TL.Select(TL.Greater(a, b), a, b); break;
                        case 5: y = TL.Add(Abs<TL, TF, TU>(a), of); break;
                        default: y = TL.Add(TL.Sub(zero, a), of); break;
                    }
                    TL.Store(y, o, i * V + vb);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Crossfade<TL, TF, TU>(CrossfadeJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var o = j.Out.Data;
            bool equal = j.Ctl[NodeInfo.XfadeLaw] > 0.5f;
            float m0 = j.SmMix.Begin(j.Ctl[NodeInfo.XfadeMix], out float dm);
            TF zero = TL.Splat(0f), one = TL.Splat(1f), half = TL.Splat(0.5f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF mm = TL.Splat(m0), dmV = TL.Splat(dm);
                for (int i = 0; i < B; i++)
                {
                    mm = TL.Add(mm, dmV);
                    TF m = j.Mix != null ? TL.Add(mm, j.Mix.Load<TL, TF, TU>(i, vb)) : mm;
                    m = TL.Select(TL.Greater(m, one), one, TL.Select(TL.Greater(zero, m), zero, m));
                    TF a = j.A != null ? j.A.Load<TL, TF, TU>(i, vb) : zero, b = j.B != null ? j.B.Load<TL, TF, TU>(i, vb) : zero;
                    TF y;
                    if (!equal) y = TL.Add(a, TL.Mul(TL.Sub(b, a), m));
                    else
                    {
                        // Equal power: cos(m π/2) and sin(m π/2) from the refined parabola (within 0.001).
                        TF ga = SinPiParabola<TL, TF, TU>(TL.Mul(half, TL.Sub(one, m))), gb = SinPiParabola<TL, TF, TU>(TL.Mul(half, m));
                        y = TL.Add(TL.Mul(a, ga), TL.Mul(b, gb));
                    }
                    TL.Store(y, o, i * V + vb);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF SinPiParabola<TL, TF, TU>(TF x) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF ax = Abs<TL, TF, TU>(x);
            TF s = TL.Mul(TL.Mul(TL.Splat(4f), x), TL.Sub(TL.Splat(1f), ax));
            TF az = Abs<TL, TF, TU>(s);
            return TL.Add(TL.Mul(TL.Splat(0.225f), TL.Sub(TL.Mul(s, az), s)), s);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Shaper<TL, TF, TU>(ShaperJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            int mode = (int)(ctl[NodeInfo.ShaperMode] + 0.5f);
            int stages = Math.Clamp((int)(ctl[NodeInfo.ShaperStages] + 0.5f), 1, 4);
            bool cv = j.Drive != null || j.Pitch != null;
            int tick = cv ? c.CtlTick : c.Tick;
            float dr0 = j.SmDrive.Begin(ctl[NodeInfo.ShaperDrive], out float ddr);
            float bi0 = j.SmBias.Begin(ctl[NodeInfo.ShaperBias], out float dbi);
            float lv0 = j.SmLevel.Begin(ctl[NodeInfo.ShaperLevel], out float dlv);
            float sr = c.SampleRate;
            var o = j.Out.Data;
            TF zero = TL.Splat(0f), one = TL.Splat(1f), quarter = TL.Splat(0.25f), threeQuarter = TL.Splat(0.75f), half = TL.Splat(0.5f), four = TL.Splat(4f), neg = TL.Splat(-1f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                if (!c.GroupOn[g]) continue;
                int vb = W * g;
                TF bias = TL.Splat(bi0 + dbi * 0), dbV = TL.Splat(dbi), lv = TL.Splat(lv0), dlV = TL.Splat(dlv);
                for (int t = 0; t < B; t += tick)
                {
                    for (int l = 0; l < W; l++)
                    {
                        int v = vb + l;
                        if (!c.Active[v]) continue;
                        float g1 = dr0 + ddr * (t + tick);
                        if (j.Drive != null) g1 += 8f * j.Drive.At(t + tick - 1, v);
                        if (g1 < 1f) g1 = 1f;
                        if (mode == 1 && j.Pitch != null)
                        {
                            // The fold's alias cap: the gain a fold of this many stages may have at this pitch (Neo Learn's Warps.FoldLimit).
                            float inc = PatchMath.Exp2((j.Pitch.At(t + tick - 1, v) - 69f) * (1f / 12f)) * (440f / sr);
                            double cap = FoldBandwidth / Math.Max(1e-6, inc);
                            double total = 1.0;
                            for (int s = 0; s < stages; s++) total *= g1;
                            if (total > cap) g1 = (float)Math.Max(1.0, PortableMath.Pow(cap, 1.0 / stages));
                        }
                        if (c.Fresh[v] || !j.GInit[v]) { j.GInit[v] = true; j.GEnd[v] = g1; }
                        j.G0[v] = j.GEnd[v]; j.Gs[v] = (g1 - j.GEnd[v]) / tick; j.GEnd[v] = g1;
                    }
                    TF gain = TL.Load(j.G0, vb), dgain = TL.Load(j.Gs, vb);
                    for (int i = t; i < t + tick; i++)
                    {
                        gain = TL.Add(gain, dgain);
                        TF x = j.In != null ? j.In.Load<TL, TF, TU>(i, vb) : zero;
                        bias = TL.Add(bias, dbV);
                        TF y;
                        if (mode == 0) y = Tanh<TL, TF, TU>(TL.Add(TL.Mul(x, gain), bias));
                        else if (mode == 2)
                        {
                            y = TL.Add(TL.Mul(x, gain), bias);
                            y = TL.Select(TL.Greater(y, one), one, TL.Select(TL.Greater(neg, y), neg, y));
                        }
                        else
                        {
                            // Fold: each stage reflects the signal back into [−1, 1] (Neo Learn's Warps.Fold: a triangle of the scaled input).
                            y = TL.Add(x, bias);
                            y = TL.Select(TL.Greater(TL.Splat(1e8f), TL.Mul(y, y)), y, zero);
                            for (int s = 0; s < stages; s++)
                            {
                                TF vq = Wrap<TL, TF, TU>(TL.Add(TL.Mul(TL.Mul(y, gain), quarter), threeQuarter));
                                TF d = Abs<TL, TF, TU>(TL.Sub(vq, half));
                                y = TL.Sub(TL.Mul(four, d), one);
                            }
                        }
                        lv = TL.Add(lv, dlV);
                        TL.Store(TL.Mul(y, lv), o, i * V + vb);
                    }
                }
            }
        }

        /// <summary>
        /// The voice sum: gains ramped across the block (steal fades, and 0 for a voice that is not sounding), guarded, summed 0…7 then
        /// 8…15, into a global buffer. The loudest sample of each voice goes to the allocator and the voices' tails.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Sum<TL, TF, TU>(SumJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var src = j.Src.Data;
            var dest = j.Dest.Data;
            TF peak0 = TL.Splat(0f), peak1 = TL.Splat(0f);
            TF g0a = TL.Load(c.Gain0, 0), g1a = TL.Load(c.Gain1, 0), g0b = TL.Load(c.Gain0, W), g1b = TL.Load(c.Gain1, W);
            TF stepA = TL.Mul(TL.Sub(g1a, g0a), TL.Splat(1f / B)), stepB = TL.Mul(TL.Sub(g1b, g0b), TL.Splat(1f / B));
            bool second = c.GroupOn[1];
            for (int i = 0; i < B; i++)
            {
                g0a = TL.Add(g0a, stepA);
                g0b = TL.Add(g0b, stepB);
                TF xa = Clean<TL, TF, TU>(TL.Load(src, i * V));
                peak0 = Max<TL, TF, TU>(peak0, Abs<TL, TF, TU>(xa));
                float sum = TL.Sum(TL.Mul(xa, g0a));
                if (second)
                {
                    TF xb = Clean<TL, TF, TU>(TL.Load(src, i * V + W));
                    peak1 = Max<TL, TF, TU>(peak1, Abs<TL, TF, TU>(xb));
                    sum += TL.Sum(TL.Mul(xb, g0b));
                }
                dest[i * V] = sum;
            }
            for (int l = 0; l < W; l++)
            {
                c.Peak[l] = Math.Max(c.Peak[l], TL.Lane(peak0, l));
                c.Peak[W + l] = Math.Max(c.Peak[W + l], TL.Lane(peak1, l));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Max<TL, TF, TU>(TF a, TF b) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct => TL.Select(TL.Greater(b, a), b, a);

        /// <summary>Out: each input's per-voice wires are summed with the voice gains and panned (a pan of its own plus the voice's share of the spread); global wires are panned as they are.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Out<TL, TF, TU>(OutJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ctl = j.Ctl;
            float lvl = j.SmLevel.Begin(ctl[NodeInfo.OutLevel], out float dl);
            float spread = ctl[NodeInfo.OutSpread];
            bool anyV = false;
            for (int k = 0; k < 4; k++)
            {
                if (j.InV[k] == null && j.InG[k] == null) continue;
                float pan = ctl[NodeInfo.OutPan + k];
                if (j.InV[k] != null)
                {
                    anyV = true;
                    for (int v = 0; v < V; v++)
                    {
                        float p = pan + spread * VoiceOffset[v];
                        p = p < -1f ? -1f : p > 1f ? 1f : p;
                        j.PanL[k * V + v] = MathF.Sqrt(0.5f * (1f - p));
                        j.PanR[k * V + v] = MathF.Sqrt(0.5f * (1f + p));
                    }
                }
                float pg = pan < -1f ? -1f : pan > 1f ? 1f : pan;
                j.GlobL[k] = MathF.Sqrt(0.5f * (1f - pg));
                j.GlobR[k] = MathF.Sqrt(0.5f * (1f + pg));
            }
            TF peak0 = TL.Splat(0f), peak1 = TL.Splat(0f);
            TF g0a = TL.Load(c.Gain0, 0), g1a = TL.Load(c.Gain1, 0), g0b = TL.Load(c.Gain0, W), g1b = TL.Load(c.Gain1, W);
            TF stepA = TL.Mul(TL.Sub(g1a, g0a), TL.Splat(1f / B)), stepB = TL.Mul(TL.Sub(g1b, g0b), TL.Splat(1f / B));
            bool second = c.GroupOn[1];
            for (int i = 0; i < B; i++)
            {
                lvl += dl;
                g0a = TL.Add(g0a, stepA);
                g0b = TL.Add(g0b, stepB);
                float l = 0f, r = 0f;
                for (int k = 0; k < 4; k++)
                {
                    var iv = j.InV[k];
                    if (iv != null)
                    {
                        TF xa = Clean<TL, TF, TU>(iv.Load<TL, TF, TU>(i, 0));
                        peak0 = Max<TL, TF, TU>(peak0, Abs<TL, TF, TU>(xa));
                        TF sa = TL.Mul(xa, g0a);
                        l += TL.Sum(TL.Mul(sa, TL.Load(j.PanL, k * V)));
                        r += TL.Sum(TL.Mul(sa, TL.Load(j.PanR, k * V)));
                        if (second)
                        {
                            TF xb = Clean<TL, TF, TU>(iv.Load<TL, TF, TU>(i, W));
                            peak1 = Max<TL, TF, TU>(peak1, Abs<TL, TF, TU>(xb));
                            TF sb = TL.Mul(xb, g0b);
                            l += TL.Sum(TL.Mul(sb, TL.Load(j.PanL, k * V + W)));
                            r += TL.Sum(TL.Mul(sb, TL.Load(j.PanR, k * V + W)));
                        }
                    }
                    var ig = j.InG[k];
                    if (ig != null)
                    {
                        float s = ig.At(i, 0);
                        l += s * j.GlobL[k];
                        r += s * j.GlobR[k];
                    }
                }
                c.OutL[i] += l * lvl;
                c.OutR[i] += r * lvl;
            }
            if (anyV)
                for (int l = 0; l < W; l++)
                {
                    c.Peak[l] = Math.Max(c.Peak[l], TL.Lane(peak0, l));
                    c.Peak[W + l] = Math.Max(c.Peak[W + l], TL.Lane(peak1, l));
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void FbWrite<TL, TF, TU>(FeedbackWriteJob j, PatchContext c) where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var st = j.State.Data;
            TF gain = TL.Splat(j.Ctl[NodeInfo.FeedbackGain]), lim = TL.Splat(16f), nlim = TL.Splat(-16f), tiny = TL.Splat(1e-18f), ntiny = TL.Splat(-1e-18f), zero = TL.Splat(0f);
            for (int g = 0; g < PatchContext.Groups; g++)
            {
                int vb = W * g;
                for (int i = 0; i < B; i++)
                {
                    if (j.In == null || !c.GroupOn[g]) { TL.Store(zero, st, i * V + vb); continue; }
                    TF x = Clean<TL, TF, TU>(TL.Mul(j.In.Load<TL, TF, TU>(i, vb), gain));
                    x = TL.Select(TL.Greater(x, lim), lim, x);
                    x = TL.Select(TL.Greater(nlim, x), nlim, x);
                    // A denormal flushes to zero (no Abs: both sides).
                    x = TL.Select(TL.Greater(tiny, x), TL.Select(TL.Greater(x, ntiny), zero, x), x);
                    TL.Store(x, st, i * V + vb);
                }
            }
        }
    }
}
