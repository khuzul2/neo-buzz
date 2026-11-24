#nullable disable
// Neo machines shared DSP (docs/specs/machines.md, "Mix and Amount"): the two controls every Neo effect has, in one place.
//
//  - Amount (0 … 200 %, 100 % = the settings as they are): how far the effect moves the sound from neutral. Each effect names the
//    parameters Amount scales and how (AmountRule: a distance from a neutral value, in the parameter's own scale: dB, a ratio's
//    reduction, a log-frequency, a percentage…). The engine reads an *effective* ParamValues made from the raw one on every block; at
//    100 % that is a plain copy, so the effect renders the bits it always did. At 0 % the effect is out of the path: the output is the
//    input delayed by the effect's reported latency, bit for bit (the effect keeps running on neutral settings, so raising Amount again
//    does not start from a cold state). Between 0 and 5 % the wet path fades in, so the step from "out of the path" to "almost neutral"
//    cannot click.
//  - Mix (0 … 100 %, default 100 %): dry against wet, the dry delayed by the latency the effect reports, so a latency never combs
//    against itself. An effect that already had a Mix (and does it itself with its own aligned dry) keeps it: the stage then has no
//    Mix of its own (mixIndex < 0) and only does Amount's bypass.
//  - Delta: in an effect's Delta mode the output is wet − dry already; the stage scales it by the same weight (Mix × Amount's fade), so
//    at 0 % it is silence. A Listen mode (the detector's own signal) is passed through untouched.
//  - The weight glides over 20 ms (a ramp that lands exactly, then a short one-pole), so automating either control does not zipper;
//    settled at 1 the stage costs nothing but the dry copy, and at 0 it returns the dry samples exactly.
//  - No allocation after construction; portable maths only.

using System;

namespace NeoBuzz.Dsp
{
    internal enum AmountKind
    {
        /// <summary>n + (v − n)·a in the parameter's physical scale (dB, %, ms, …).</summary>
        Linear,
        /// <summary>n·(v/n)^a: the distance in octaves (or decades) from n, for frequencies and times.</summary>
        Log,
        /// <summary>A ratio: the reduction 1 − 1/R scales with a.</summary>
        Ratio,
        /// <summary>The effect's own function of the raw ParamValues and a.</summary>
        Custom,
    }

    /// <summary>One parameter that Amount scales.</summary>
    internal sealed class AmountRule
    {
        public int Index { get; }
        public AmountKind Kind { get; }
        public double Neutral { get; }
        /// <summary>Limits of the scaled physical value (NaN: the parameter's own range).</summary>
        public double Lo { get; }
        public double Hi { get; }
        public Func<ParamValues, double, double> Fn { get; }

        AmountRule(int index, AmountKind kind, double neutral, double lo, double hi, Func<ParamValues, double, double> fn)
        {
            Index = index; Kind = kind; Neutral = neutral; Lo = lo; Hi = hi; Fn = fn;
        }

        /// <summary>The physical value moves from <paramref name="neutral"/> by Amount times its distance, within <paramref name="lo"/> … <paramref name="hi"/>.</summary>
        public static AmountRule Lin(int index, double neutral, double lo = double.NaN, double hi = double.NaN) => new AmountRule(index, AmountKind.Linear, neutral, lo, hi, null);

        /// <summary>The same in the log domain (frequencies, times).</summary>
        public static AmountRule Log(int index, double neutral, double lo = double.NaN, double hi = double.NaN) => new AmountRule(index, AmountKind.Log, neutral, lo, hi, null);

        /// <summary>A ratio parameter: the gain it takes off (1 − 1/R) scales.</summary>
        public static AmountRule Ratio(int index) => new AmountRule(index, AmountKind.Ratio, 1, double.NaN, double.NaN, null);

        /// <summary>The scaled physical value is fn(raw values, amount).</summary>
        public static AmountRule Fun(int index, Func<ParamValues, double, double> fn, double lo = double.NaN, double hi = double.NaN) => new AmountRule(index, AmountKind.Custom, 0, lo, hi, fn);

        /// <summary>The physical value for Amount <paramref name="a"/> (1 = as set) of the parameter <paramref name="d"/> now at <paramref name="v"/>.</summary>
        public double Apply(ParamDef d, ParamValues raw, double a)
        {
            double v = raw.Value(Index), r;
            switch (Kind)
            {
                case AmountKind.Linear:
                    r = double.IsNegativeInfinity(v) ? v : Neutral + (v - Neutral) * a;
                    break;
                case AmountKind.Log:
                    r = v > 0 && Neutral > 0 ? Neutral * PortableMath.Pow(v / Neutral, a) : v;
                    break;
                case AmountKind.Ratio:
                {
                    double inv = double.IsPositiveInfinity(v) ? 0 : 1.0 / v;
                    double k = 1 - a * (1 - inv);
                    r = k <= 1e-9 ? double.PositiveInfinity : 1.0 / k;
                    break;
                }
                default:
                    r = Fn(raw, a);
                    break;
            }
            double lo = double.IsNaN(Lo) ? RangeLo(d) : Lo, hi = double.IsNaN(Hi) ? RangeHi(d) : Hi;
            if (double.IsPositiveInfinity(r)) return d.Kind == MapKind.Ratio && d.Flag && double.IsNaN(Hi) ? r : hi;
            if (double.IsNaN(r)) return v;
            return r < lo ? lo : r > hi ? hi : r;
        }

        static double RangeLo(ParamDef d) => d.Kind switch
        {
            MapKind.Percent => 0.0,
            MapKind.Bipolar => -1.0,
            MapKind.Int => d.Min + d.A,
            _ => Math.Min(d.A, d.B),
        };

        static double RangeHi(ParamDef d) => d.Kind switch
        {
            MapKind.Percent => 1.0,
            MapKind.Bipolar => 1.0,
            MapKind.Int => d.Max + d.A,
            _ => Math.Max(d.A, d.B),
        };
    }

    internal sealed class FxStage
    {
        /// <summary>Amount below which the wet path fades out (fraction of the 100 % setting).</summary>
        public const double FadeBelow = 0.05;

        readonly ParamValues raw, eff;
        readonly AmountRule[] rules;
        readonly int amountIndex, mixIndex, deltaIndex, listenIndex;
        readonly float[] ringL, ringR;
        readonly int mask;
        int pos;
        SmoothRamp weight;
        bool started;
        double sampleRate = 48000;

        /// <param name="raw">The machine's parameters.</param>
        /// <param name="amountIndex">Index of Amount (a 0 … 200 % linear parameter).</param>
        /// <param name="mixIndex">Index of the stage's Mix, or −1 when the effect mixes itself (with its own latency-aligned dry).</param>
        /// <param name="rules">What Amount scales.</param>
        /// <param name="deltaIndex">Index of the effect's Delta switch (−1: none): the stage scales the delta by its weight.</param>
        /// <param name="listenIndex">Index of the effect's Listen switch (−1: none): the stage leaves the listened signal alone.</param>
        /// <param name="maxLatency">The most latency the effect reports, samples.</param>
        public FxStage(ParamValues raw, int amountIndex, int mixIndex, AmountRule[] rules, int deltaIndex = -1, int listenIndex = -1, int maxLatency = 30000, int maxBlock = 256)
        {
            this.raw = raw;
            this.amountIndex = amountIndex;
            this.mixIndex = mixIndex;
            this.rules = rules ?? Array.Empty<AmountRule>();
            this.deltaIndex = deltaIndex;
            this.listenIndex = listenIndex;
            eff = new ParamValues(raw.Table);
            eff.AllowPhysical();
            int size = 1;
            while (size < maxLatency + maxBlock + 1) size <<= 1;
            ringL = new float[size];
            ringR = new float[size];
            mask = size - 1;
            weight.Setup(0.02, sampleRate);
            weight.Reset(1f);
            Sync();
        }

        /// <summary>What the engine reads: the machine's values with Amount applied.</summary>
        public ParamValues Effective => eff;

        /// <summary>Amount now, 1 = 100 %.</summary>
        public double Amount => raw.Value(amountIndex) * 0.01;

        /// <summary>Mix now, 0 … 1 (1 when the effect mixes itself).</summary>
        public double Mix => mixIndex >= 0 ? raw.Value(mixIndex) : 1.0;

        public void SetSampleRate(double sr)
        {
            sampleRate = sr;
            float v = weight.Value, t = weight.Target;
            weight.Setup(0.02, sr);
            weight.Reset(v);
            weight.SetTarget(t);
            Sync();   // the engine reads its values (forced) right after
        }

        /// <summary>Back to the start: the dry history is cleared and the weight stands at its target.</summary>
        public void Reset()
        {
            Array.Clear(ringL, 0, ringL.Length);
            Array.Clear(ringR, 0, ringR.Length);
            pos = 0;
            started = false;
            Sync();
        }

        /// <summary>
        /// The effective values of <paramref name="raw"/> for Amount in <paramref name="amountIndex"/> into <paramref name="into"/>
        /// (which must have had AllowPhysical called): what the engine would read. The editors draw their curves with it.
        /// </summary>
        public static void Fill(ParamValues raw, ParamValues into, int amountIndex, AmountRule[] rules)
        {
            var table = raw.Table;
            for (int i = 0; i < table.Length; i++) into.Set(i, raw[i]);
            double a = raw.Value(amountIndex) * 0.01;
            if (a == 1.0)
            {
                for (int i = 0; i < rules.Length; i++) into.ClearPhysical(rules[i].Index);
            }
            else
            {
                for (int i = 0; i < rules.Length; i++)
                {
                    var rule = rules[i];
                    into.SetPhysical(rule.Index, rule.Apply(table[rule.Index], raw, a));
                }
            }
        }

        /// <summary>Makes the effective values from the raw ones for Amount now, and the weight's target from Mix and Amount.</summary>
        public void Sync()
        {
            Fill(raw, eff, amountIndex, rules);
            double a = Amount;
            double fade = a >= FadeBelow ? 1.0 : a <= 0 ? 0.0 : a / FadeBelow;
            float target = (float)(Mix * fade);
            if (!started)
            {
                weight.Reset(target);
                started = true;
            }
            else weight.SetTarget(target);
        }

        /// <summary>Before the engine: the effective values for this block, and the dry input kept for the mix.</summary>
        public void Begin(float[] left, float[] right, int n)
        {
            Sync();
            int p = pos;
            for (int i = 0; i < n; i++)
            {
                p = (p + 1) & mask;
                ringL[p] = left[i];
                ringR[p] = right[i];
            }
        }

        /// <summary>After the engine: wet in <paramref name="left"/> / <paramref name="right"/>, the dry delayed by <paramref name="latency"/> samples blended in.</summary>
        public void End(float[] left, float[] right, int n, int latency)
        {
            if (latency < 0) latency = 0;
            if (latency > mask - n) latency = mask - n;
            int start = pos;
            pos = (pos + n) & mask;
            bool listen = listenIndex >= 0 && raw.On(listenIndex);
            if (listen || (weight.Target == 1f && weight.At(1f))) return;
            bool delta = deltaIndex >= 0 && raw.On(deltaIndex);
            for (int i = 0; i < n; i++)
            {
                float w = weight.Next();
                if (delta)
                {
                    if (w == 0f) { left[i] = 0f; right[i] = 0f; }
                    else if (w < 1f) { left[i] *= w; right[i] *= w; }
                    continue;
                }
                if (w >= 1f) continue;
                int at = (start + 1 + i - latency) & mask;
                float dl = ringL[at], dr = ringR[at];
                if (w <= 0f)
                {
                    left[i] = dl;
                    right[i] = dr;
                }
                else
                {
                    float v = 1f - w;
                    left[i] = dl * v + left[i] * w;
                    right[i] = dr * v + right[i] * w;
                }
            }
        }
    }
}
