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

        /// <summary>True for −/+ parameters, whose knob arc grows from the centre.</summary>
        public static bool IsBipolar(ParamDef def) =>
            def.Kind == MapKind.Bipolar || (def.Kind == MapKind.Int && def.Flag) ||
            (def.Kind == MapKind.Db && def.A < 0 && def.B > 0 && !def.Flag);

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
