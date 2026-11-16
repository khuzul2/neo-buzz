// Fork (docs/specs/ux-overhaul.md, the parameter panel): the old parameter window's edit functions, for the Device panel and the agent's
// param_randomize tool alike: Randomize (every value anywhere in its range), Nudge (relative: each value moves a little) and Morph (a point
// between two presets). They leave locked parameters alone (MachinePinStore keeps the locks) and the input parameters of connections.

using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core
{
    internal enum ParameterFunction { Randomize, Nudge, Morph }

    internal static class ParameterFunctions
    {
        static readonly Random random = new Random();

        /// <summary>The name a lock is kept under: a global's name; "T:" and the name for a track parameter (all of its tracks).</summary>
        public static string LockKey(IParameter p) => p.Group.Type == ParameterGroupType.Track ? "T:" + p.Name : p.Name;

        /// <summary>The state parameters (a track one for each track) that are not locked, and not the connections' own.</summary>
        public static List<(IParameter Parameter, int Track)> Targets(ReBuzzCore core, IMachine machine)
        {
            var store = core?.PinStore;
            var list = new List<(IParameter, int)>();
            foreach (var group in machine.ParameterGroups)
            {
                if (group.Type == ParameterGroupType.Input) continue;
                int tracks = group.Type == ParameterGroupType.Track ? Math.Max(0, group.TrackCount) : 1;
                foreach (var p in group.Parameters)
                {
                    if (!p.Flags.HasFlag(ParameterFlags.State) || p.Type is ParameterType.Note or ParameterType.Internal || p.MaxValue <= p.MinValue) continue;
                    if (store != null && store.IsLocked(machine.Name, LockKey(p))) continue;
                    for (int t = 0; t < tracks; t++) list.Add((p, t));
                }
            }
            return list;
        }

        /// <summary>
        /// Runs a function on the unlocked parameters now (no undo step: the caller makes one). <paramref name="amount"/> is 0..1: how
        /// far a nudge goes, or where a morph stands between <paramref name="a"/> (0) and <paramref name="b"/> (1). Returns how many
        /// values changed.
        /// </summary>
        public static int Apply(ReBuzzCore core, IMachine machine, ParameterFunction function, Preset a, Preset b, double amount)
        {
            int changed = 0;
            amount = Math.Clamp(amount, 0, 1);
            foreach (var (p, t) in Targets(core, machine))
            {
                int min = p.MinValue, max = p.MaxValue, now = p.GetValue(t);
                int Clamp(double v) => Math.Clamp((int)Math.Round(v), min, max);
                int value = function switch
                {
                    ParameterFunction.Randomize => random.Next(min, max + 1),
                    ParameterFunction.Nudge => Clamp(now + amount * amount * random.Next(-(max - min), max - min + 1)),
                    _ => Clamp(a.GetValueOrDefault(p, t) + amount * (b.GetValueOrDefault(p, t) - a.GetValueOrDefault(p, t))),
                };
                if (value == now) continue;
                p.SetValue(t, value);
                changed++;
            }
            if (machine.DLL?.Info?.Version >= 42) machine.SendControlChanges();
            return changed;
        }

        /// <summary>A preset by name for a function ("&lt;default&gt;" is the machine's defaults); null when there is none.</summary>
        public static Preset PresetOf(IMachine machine, string name) => machine.GetPreset(string.IsNullOrEmpty(name) ? "<default>" : name);
    }
}
