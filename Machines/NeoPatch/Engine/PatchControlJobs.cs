#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.2): the nodes that make or shape control signals, and the ones that are scalar by nature
// (Noise, Sample, Delay). They run in plain loops over the voices that sound (or the one slot of a global node), once per control
// interval for the control nodes: each works out its value at the interval's end and the buffer ramps to it, which is the smoothing
// of a control port (docs/specs/neo-patch-engine.md). Nothing here allocates.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    internal static class Ctl_
    {
        public const int B = PatchContext.Block, V = PatchContext.MaxVoices;

        /// <summary>The block's samples [t, t + tick) of every voice that sounds: <paramref name="start"/> + <paramref name="step"/> × (i + 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Fill(PatchContext c, float[] start, float[] step, float[] o, int t, int tick)
        {
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                float a = start[v], d = step[v];
                for (int i = t; i < t + tick; i++) { a += d; o[i * V + v] = a; }
            }
        }

        public static float Rand(ref uint s)
        {
            uint x = s == 0 ? 0x9E3779B9u : s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            s = x;
            return (int)x * (1f / 2147483648f);
        }

        /// <summary>sin(2π ph) for ph in [0, 1).</summary>
        public static float Sine(float ph)
        {
            float q = ph > 0.5f ? ph - 1f : ph;
            q = q > 0.25f ? 0.5f - q : q;
            q = q < -0.25f ? -0.5f - q : q;
            return PatchMath.SinPi(2f * q);
        }

        public static float KFor(float seconds, int tick, float sr) => seconds <= 1e-4f ? 1f : (float)(1.0 - PortableMath.Exp(-tick / (double)(seconds * sr)));
    }

    internal sealed class LfoJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp Rate, Reset;
        public PortBuffer Out;
        public float[] Phase = new float[V], Prev = new float[V], Rnd = new float[V], RndNext = new float[V], LastReset = new float[V];
        public uint[] Rng = new uint[V];
        public bool[] Init = new bool[V];
        readonly float[] start = new float[V], step = new float[V];

        public LfoJob() { for (int v = 0; v < V; v++) Rng[v] = 0x2545F491u * (uint)(v + 7); }

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            float sr = c.SampleRate, baseRate = Ctl[NodeInfo.LfoRate], rateMod = Ctl[NodeInfo.LfoRateMod], depth = Ctl[NodeInfo.LfoDepth], offset = Ctl[NodeInfo.LfoOffset], p0 = Ctl[NodeInfo.LfoPhase];
            int shape = (int)(Ctl[NodeInfo.LfoShape] + 0.5f);
            bool retrig = Ctl[NodeInfo.LfoRetrig] > 0.5f;
            var o = Out.Data;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                if (c.Fresh[v] || !Init[v])
                {
                    Init[v] = true;
                    // A voice that isn't retriggered starts at a phase of its own, so voices drift apart instead of beating in step.
                    float ph = retrig || Global ? p0 : p0 + 0.5f * (Ctl_.Rand(ref Rng[v]) + 1f);
                    Phase[v] = ph - MathF.Floor(ph);
                    Rnd[v] = Ctl_.Rand(ref Rng[v]); RndNext[v] = Ctl_.Rand(ref Rng[v]);
                    Prev[v] = Value(shape, Phase[v], Rnd[v], RndNext[v]) * depth + offset;
                    LastReset[v] = 0f;
                }
            }
            for (int t = 0; t < PatchContext.Block; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) continue;
                    float rate = baseRate;
                    if (Rate != null) rate *= PatchMath.Exp2(Rate.At(t + tick - 1, v) * rateMod);
                    if (Reset != null)
                    {
                        float g = Reset.At(t, v);
                        if (g > 0.5f && LastReset[v] <= 0.5f) Phase[v] = p0 - MathF.Floor(p0);
                        LastReset[v] = g;
                    }
                    float ph = Phase[v] + rate * tick / sr;
                    if (ph >= 1f)
                    {
                        ph -= MathF.Floor(ph);
                        Rnd[v] = RndNext[v]; RndNext[v] = Ctl_.Rand(ref Rng[v]);
                    }
                    Phase[v] = ph;
                    float y = Value(shape, ph, Rnd[v], RndNext[v]) * depth + offset;
                    start[v] = Prev[v]; step[v] = (y - Prev[v]) / tick; Prev[v] = y;
                }
                Ctl_.Fill(c, start, step, o, t, tick);
            }
        }

        static float Value(int shape, float ph, float r0, float r1)
        {
            switch (shape)
            {
                case 0: return Ctl_.Sine(ph);
                case 1: return ph < 0.25f ? 4f * ph : ph < 0.75f ? 2f - 4f * ph : 4f * ph - 4f;
                case 2: return 2f * ph - 1f;
                case 3: return 1f - 2f * ph;
                case 4: return ph < 0.5f ? 1f : -1f;
                case 5: { float s = ph * ph * (3f - 2f * ph); return r0 + (r1 - r0) * s; }
                default: return r0;
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is LfoJob o) || o.Global != Global) return false;
            Phase = o.Phase; Prev = o.Prev; Rnd = o.Rnd; RndNext = o.RndNext; LastReset = o.LastReset; Rng = o.Rng; Init = o.Init;
            return true;
        }
    }

    internal sealed class RandomJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp Trigger;
        public PortBuffer Out;
        public float[] Cur = new float[V], Target = new float[V], Acc = new float[V], Prev = new float[V], LastTrig = new float[V];
        public uint[] Rng = new uint[V];
        public bool[] Init = new bool[V];
        readonly float[] start = new float[V], step = new float[V];

        public RandomJob() { for (int v = 0; v < V; v++) Rng[v] = 0x6C078965u * (uint)(v + 3); }

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            float sr = c.SampleRate, rate = Ctl[NodeInfo.RandRate], smooth = Ctl[NodeInfo.RandSmooth], depth = Ctl[NodeInfo.RandDepth], offset = Ctl[NodeInfo.RandOffset];
            // The time to arrive at a new value: up to one period of the rate at full smoothing.
            float k = Ctl_.KFor(smooth / rate * 0.5f, tick, sr);
            var o = Out.Data;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                if (c.Fresh[v] || !Init[v])
                {
                    Init[v] = true;
                    Cur[v] = Target[v] = Ctl_.Rand(ref Rng[v]); Acc[v] = 0f; LastTrig[v] = 0f;
                    Prev[v] = Cur[v] * depth + offset;
                }
            }
            for (int t = 0; t < PatchContext.Block; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) continue;
                    bool fire = false;
                    if (Trigger != null)
                    {
                        float g = Trigger.At(t, v);
                        fire = g > 0.5f && LastTrig[v] <= 0.5f;
                        LastTrig[v] = g;
                    }
                    else
                    {
                        Acc[v] += rate * tick / sr;
                        if (Acc[v] >= 1f) { Acc[v] -= MathF.Floor(Acc[v]); fire = true; }
                    }
                    if (fire) Target[v] = Ctl_.Rand(ref Rng[v]);
                    Cur[v] += (Target[v] - Cur[v]) * k;
                    float y = Cur[v] * depth + offset;
                    start[v] = Prev[v]; step[v] = (y - Prev[v]) / tick; Prev[v] = y;
                }
                Ctl_.Fill(c, start, step, o, t, tick);
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is RandomJob o) || o.Global != Global) return false;
            Cur = o.Cur; Target = o.Target; Acc = o.Acc; Prev = o.Prev; LastTrig = o.LastTrig; Rng = o.Rng; Init = o.Init;
            return true;
        }
    }

    internal sealed class SlewJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp In;
        public PortBuffer Out;
        public float[] Y = new float[V], Prev = new float[V];
        public bool[] Init = new bool[V];
        readonly float[] start = new float[V], step = new float[V];

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            float kr = Ctl_.KFor(Ctl[NodeInfo.SlewRise], tick, c.SampleRate), kf = Ctl_.KFor(Ctl[NodeInfo.SlewFall], tick, c.SampleRate);
            var o = Out.Data;
            for (int t = 0; t < PatchContext.Block; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) continue;
                    float x = In != null ? In.At(t + tick - 1, v) : 0f;
                    if (c.Fresh[v] || !Init[v]) { Init[v] = true; Y[v] = x; Prev[v] = x; }
                    Y[v] += (x - Y[v]) * (x > Y[v] ? kr : kf);
                    start[v] = Prev[v]; step[v] = (Y[v] - Prev[v]) / tick; Prev[v] = Y[v];
                }
                Ctl_.Fill(c, start, step, o, t, tick);
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is SlewJob o) || o.Global != Global) return false;
            Y = o.Y; Prev = o.Prev; Init = o.Init;
            return true;
        }
    }

    internal sealed class SampleHoldJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp In, Trigger;
        public PortBuffer Out;
        public float[] Held = new float[V], Prev = new float[V], Acc = new float[V], LastTrig = new float[V];
        public uint[] Rng = new uint[V];
        public bool[] Init = new bool[V];
        readonly float[] start = new float[V], step = new float[V];

        public SampleHoldJob() { for (int v = 0; v < V; v++) Rng[v] = 0x7F4A7C15u * (uint)(v + 5); }

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            float sr = c.SampleRate, rate = Ctl[NodeInfo.ShRate];
            bool track = Ctl[NodeInfo.ShMode] > 0.5f;
            var o = Out.Data;
            for (int t = 0; t < PatchContext.Block; t += tick)
            {
                for (int v = 0; v < V; v++)
                {
                    if (!c.Active[v]) continue;
                    // The input is noise when nothing is wired to it: sample and hold alone is the classic random steps.
                    float x = In != null ? In.At(t + tick - 1, v) : Ctl_.Rand(ref Rng[v]);
                    bool fire = false, high = false;
                    if (Trigger != null)
                    {
                        float g = Trigger.At(t, v);
                        high = g > 0.5f;
                        fire = high && LastTrig[v] <= 0.5f;
                        LastTrig[v] = g;
                    }
                    else
                    {
                        Acc[v] += rate * tick / sr;
                        if (Acc[v] >= 1f) { Acc[v] -= MathF.Floor(Acc[v]); fire = true; }
                    }
                    if (c.Fresh[v] || !Init[v]) { Init[v] = true; Held[v] = x; Prev[v] = x; fire = false; }
                    if (fire || (track && high)) Held[v] = x;
                    start[v] = Prev[v]; step[v] = (Held[v] - Prev[v]) / tick; Prev[v] = Held[v];
                }
                Ctl_.Fill(c, start, step, o, t, tick);
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is SampleHoldJob o) || o.Global != Global) return false;
            Held = o.Held; Prev = o.Prev; Acc = o.Acc; LastTrig = o.LastTrig; Rng = o.Rng; Init = o.Init;
            return true;
        }
    }

    internal sealed class MacroJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public PortBuffer Out;
        public Smooth Sm;

        public override void Run(PatchContext c)
        {
            int idx = Math.Clamp((int)(Ctl[NodeInfo.MacroIndex] + 0.5f) - 1, 0, 15);
            float now = c.Macros[idx] * Ctl[NodeInfo.MacroAmount];
            float a0 = Sm.Begin(now, out float d);
            var o = Out.Data;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                float a = a0;
                for (int i = 0; i < PatchContext.Block; i++) { a += d; o[i * V + v] = a; }
            }
        }

        public override bool Adopt(Job old) { if (!(old is MacroJob o)) return false; Sm.Adopt(o.Sm); return true; }
    }

    internal sealed class NoiseJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public PortBuffer Out;
        public uint[] Rng = new uint[V];
        public PinkNoise[] Pink = new PinkNoise[V];
        public Smooth SmLevel;

        public NoiseJob() { for (int v = 0; v < V; v++) Rng[v] = 0x1B873593u * (uint)(v + 11); }

        public override void Run(PatchContext c)
        {
            bool pink = Ctl[NodeInfo.NoiseColor] > 0.5f;
            float l0 = SmLevel.Begin(Ctl[NodeInfo.NoiseLevel], out float dl);
            var o = Out.Data;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                float l = l0;
                for (int i = 0; i < PatchContext.Block; i++)
                {
                    l += dl;
                    float w = Ctl_.Rand(ref Rng[v]);
                    o[i * V + v] = (pink ? Pink[v].Next(w) * 3f : w) * l;
                }
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is NoiseJob o) || o.Global != Global) return false;
            Rng = o.Rng; Pink = o.Pink; SmLevel.Adopt(o.SmLevel);
            return true;
        }
    }

    /// <summary>Sample: one of Dye's built-in samples or the user's WAV, played at the pitch from its input (or its own), restarted by a gate or by the note, one-shot or looping.</summary>
    internal sealed class SampleJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp Pitch, Gate;
        public PortBuffer Out;
        public double[] Pos = new double[V];
        public bool[] Playing = new bool[V];
        public float[] LastGate = new float[V];
        public Smooth SmLevel;

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            int src = (int)(Ctl[NodeInfo.SampleSource] + 0.5f);
            var layer = src >= SampleSources.Count ? c.UserSample : SampleSources.Get(src);
            var o = Out.Data;
            float l0 = SmLevel.Begin(Ctl[NodeInfo.SampleLevel], out float dl);
            if (layer == null || layer.Length < 4)
            {
                for (int v = 0; v < V; v++) if (c.Active[v]) for (int i = 0; i < PatchContext.Block; i++) o[i * V + v] = 0f;
                return;
            }
            bool loop = Ctl[NodeInfo.SampleLoop] > 0.5f, follow = Ctl[NodeInfo.SampleFollow] > 0.5f;
            float tune = Ctl[NodeInfo.SampleTune];
            double startFrame = Ctl[NodeInfo.SampleStart] * (layer.Length - 1);
            double rateRatio = layer.Rate / (double)c.SampleRate;
            int pad = SampleLayer.Pad;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                if (c.Fresh[v] && Gate == null) { Pos[v] = startFrame; Playing[v] = true; }
                float l = l0;
                int fadeFrom = PatchContext.Block;
                float step = 1f;
                for (int t = 0; t < PatchContext.Block; t += tick)
                {
                    if (Gate != null)
                    {
                        float g = Gate.At(t, v);
                        if (g > 0.5f && LastGate[v] <= 0.5f) { Pos[v] = startFrame; Playing[v] = true; }
                        else if (g <= 0.5f && LastGate[v] > 0.5f && loop) fadeFrom = Math.Min(fadeFrom, t);
                        LastGate[v] = g;
                    }
                    float note = follow && Pitch != null ? Pitch.At(t + tick - 1, v) : layer.Root;
                    step = (float)(PatchMath.Exp2((note + tune - layer.Root) / 12f) * rateRatio);
                    int k = 0;
                    while (k < layer.Levels - 1 && step >= (2 << k)) k++;
                    var data = layer.Left[k]; var dataR = layer.Stereo ? layer.Right[k] : null;
                    double scale = 1.0 / (1 << k);
                    int lenK = layer.LevelLength(k);
                    for (int i = t; i < t + tick; i++)
                    {
                        l += dl;
                        if (!Playing[v]) { o[i * V + v] = 0f; continue; }
                        double p = Pos[v];
                        if (p >= layer.Length)
                        {
                            if (loop) { p -= layer.Length - startFrame; if (p >= layer.Length || p < 0) p = startFrame; }
                            else { Playing[v] = false; o[i * V + v] = 0f; continue; }
                        }
                        double x = p * scale;
                        int ip = (int)x;
                        float fr = (float)(x - ip);
                        if (ip >= lenK) ip = lenK - 1;
                        float y = Cubic(data, pad + ip, fr);
                        if (dataR != null) y = 0.5f * (y + Cubic(dataR, pad + ip, fr));
                        float fade = i >= fadeFrom ? 1f - (i - fadeFrom + 1f) / (PatchContext.Block - fadeFrom + 1f) : 1f;
                        o[i * V + v] = y * l * fade;
                        Pos[v] = p + step;
                    }
                }
                if (fadeFrom < PatchContext.Block) Playing[v] = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Cubic(float[] d, int i, float x)
        {
            float y0 = d[i - 1], y1 = d[i], y2 = d[i + 1], y3 = d[i + 2];
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * x + c2) * x + c1) * x + y1;
        }

        public override bool Adopt(Job old)
        {
            if (!(old is SampleJob o) || o.Global != Global) return false;
            Pos = o.Pos; Playing = o.Playing; LastGate = o.LastGate; SmLevel.Adopt(o.SmLevel);
            return true;
        }
    }

    /// <summary>
    /// Delay: a delay line with its own feedback and a damping low-pass in the loop (a comb, a Karplus-Strong string, a flanger), a
    /// time that follows its input in octaves and a wet/dry mix. Per voice (a line for each voice, as many as the machine's Voices
    /// when the patch was compiled) or global. Sample accurate, so a loop of a few samples works (the Feedback node is a block late).
    /// </summary>
    internal sealed class DelayJob : Job
    {
        const int V = PatchContext.MaxVoices;
        public float[] Ctl;
        public Inp In, Time;
        public PortBuffer Out;
        public float[][] Line = new float[V][];
        public int[] Write = new int[V], Valid = new int[V];
        public float[] Lp = new float[V], Dprev = new float[V];
        public int Len;
        public Smooth SmTime, SmFb, SmMix;

        public static DelayJob Make(float sampleRate, int slots)
        {
            var j = new DelayJob { Len = (int)(2.0 * sampleRate) + 8 };
            for (int v = 0; v < slots && v < V; v++) j.Line[v] = new float[j.Len];
            return j;
        }

        public override void Run(PatchContext c)
        {
            int tick = c.CtlTick;
            float sr = c.SampleRate;
            float t0 = SmTime.Begin(Ctl[NodeInfo.DelayTime], out float dt);
            float fb0 = SmFb.Begin(Ctl[NodeInfo.DelayFeedback], out float dfb);
            float mx0 = SmMix.Begin(Ctl[NodeInfo.DelayMix], out float dmx);
            float timeMod = Ctl[NodeInfo.DelayTimeMod];
            float a = 1f - (float)PortableMath.Exp(-2.0 * Math.PI * Ctl[NodeInfo.DelayDamping] / sr);
            var o = Out.Data;
            int len = Len;
            float maxD = len - 4;
            for (int v = 0; v < V; v++)
            {
                if (!c.Active[v]) continue;
                var line = Line[v];
                if (line == null) { for (int i = 0; i < PatchContext.Block; i++) o[i * V + v] = 0f; continue; }
                if (c.Fresh[v]) { Valid[v] = 0; Lp[v] = 0f; Write[v] = 0; Dprev[v] = float.NaN; }
                int w = Write[v], valid = Valid[v];
                float lp = Lp[v];
                float fb = fb0, mx = mx0;
                for (int t = 0; t < PatchContext.Block; t += tick)
                {
                    float tms = t0 + dt * (t + tick);
                    float d1 = tms * 0.001f * sr;
                    if (Time != null) d1 *= PatchMath.Exp2(Time.At(t + tick - 1, v) * timeMod);
                    d1 = d1 < 1f ? 1f : d1 > maxD ? maxD : d1;
                    float d0 = float.IsNaN(Dprev[v]) ? d1 : Dprev[v];
                    float dd = (d1 - d0) / tick, d = d0;
                    Dprev[v] = d1;
                    for (int i = t; i < t + tick; i++)
                    {
                        d += dd; fb += dfb; mx += dmx;
                        float x = In != null ? In.At(i, v) : 0f;
                        int di = (int)d;
                        float fr = d - di;
                        int r0 = w - di; if (r0 < 0) r0 += len;
                        int r1 = r0 - 1; if (r1 < 0) r1 += len;
                        float y = di + 1 <= valid ? line[r0] + (line[r1] - line[r0]) * fr : 0f;
                        lp += a * (y - lp);
                        float wv = x + fb * lp;
                        // The loop is bounded: a runaway (feedback near 1 with a hot input) is held, a NaN is cleared.
                        wv = wv > 16f ? 16f : wv < -16f ? -16f : wv;
                        if (float.IsNaN(wv)) wv = 0f;
                        line[w] = PatchMath.CleanState(wv);
                        if (++w == len) w = 0;
                        if (valid < len) valid++;
                        o[i * V + v] = x + (y - x) * mx;
                    }
                }
                lp = PatchMath.CleanState(lp);
                Write[v] = w; Valid[v] = valid; Lp[v] = lp;
            }
        }

        public override bool Adopt(Job old)
        {
            if (!(old is DelayJob o) || o.Global != Global || o.Len != Len) return false;
            Line = o.Line; Write = o.Write; Valid = o.Valid; Lp = o.Lp; Dprev = o.Dprev;
            SmTime.Adopt(o.SmTime); SmFb.Adopt(o.SmFb); SmMix.Adopt(o.SmMix);
            return true;
        }
    }
}
