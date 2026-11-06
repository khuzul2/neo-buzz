#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): how controls map a
// parameter (Params.def, NeoBuzz.Dsp/ParamDef.cs) to what they show.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    internal static class ParamMapping
    {
        /// <summary>A control position 0 … 1 as the parameter's raw value.</summary>
        public static int RawFromNormalized(ParamDef def, double t) => def.Min + (int)Math.Round(Math.Clamp(t, 0, 1) * (def.Max - def.Min));

        /// <summary>A raw value as a control position 0 … 1.</summary>
        public static double Normalized(ParamDef def, int raw) => def.Max == def.Min ? 0 : (raw - def.Min) / (double)(def.Max - def.Min);

        /// <summary>True for −/+ parameters, whose knob arc grows from the value 0 (<see cref="ArcOrigin"/>).</summary>
        public static bool IsBipolar(ParamDef def) =>
            def.Kind == MapKind.Bipolar || (def.Kind == MapKind.Int && def.Flag) ||
            (def.Kind == MapKind.Db && def.A < 0 && def.B > 0 && !def.Flag);

        /// <summary>
        /// Where a knob's value arc starts, as a position 0 … 1: the value 0 for a range from below 0 to above it (the
        /// knob's middle only when it is symmetric: Neo Limiter's Input gain, −12 … +24 dB, starts a third of the way
        /// round; a ±50 ct fine tune or a pan in the middle), else the range's minimum. A level whose bottom is silence
        /// (decibels down to −∞) is one-sided: from the minimum.
        /// </summary>
        public static double ArcOrigin(ParamDef def)
        {
            if (def.Kind is MapKind.Enum or MapKind.Switch) return 0;
            if (def.Kind == MapKind.Db && def.Flag) return 0;
            return ZeroPosition(def.Value, def.Min, def.Max);
        }

        /// <summary>
        /// The position 0 … 1 where <paramref name="value"/> (monotonic in the raw value) crosses 0, between the two raw
        /// values either side of it; 0 (the minimum) when the range doesn't reach from below 0 to above it.
        /// </summary>
        public static double ZeroPosition(Func<int, double> value, int min, int max)
        {
            if (max <= min || value == null) return 0;
            double lo = value(min), hi = value(max);
            if (double.IsNaN(lo) || double.IsNaN(hi) || !(lo < 0 && hi > 0 || lo > 0 && hi < 0)) return 0;
            bool rising = hi > 0;
            int a = min, b = max;
            while (b - a > 1)
            {
                int m = a + (b - a) / 2;
                double v = value(m);
                if (v == 0) return (m - min) / (double)(max - min);
                if (v < 0 == rising) a = m;
                else b = m;
            }
            double va = value(a), vb = value(b);
            double t = double.IsFinite(va) && double.IsFinite(vb) && vb != va ? a + (0 - va) / (vb - va) : b;
            return Math.Clamp((t - min) / (max - min), 0, 1);
        }

        /// <summary>
        /// A switch drawn as one toggle named for its function, lit when on (ToggleFace): an Off/On switch. A two-state
        /// parameter with names of its own ("Linear"/"Minimum") stays a choice.
        /// </summary>
        public static bool IsToggle(ParamDef def) =>
            def.Kind == MapKind.Switch && def.Max - def.Min == 1 &&
            (def.Names == null || (def.Names.Length == 2 && string.Equals(def.Names[0], "Off", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(def.Names[1], "On", StringComparison.OrdinalIgnoreCase)));

        /// <summary>The names of a choice parameter's values: its own, Off/On for a switch, else each value described.</summary>
        public static string[] ChoiceNames(ParamDef def)
        {
            if (def.Names != null) return def.Names;
            if (def.Kind == MapKind.Switch) return new[] { "Off", "On" };
            var n = new string[def.Max - def.Min + 1];
            for (int i = 0; i < n.Length; i++) n[i] = def.Describe(def.Min + i);
            return n;
        }
    }
}
