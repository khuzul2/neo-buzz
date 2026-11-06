// Fork (UI gallery review, editor kit): which preset a machine's sound is, for the preset bar, whatever its
// toolkit (WPF and the studio's Avalonia kit share it). The bar used to say "Presets" until a preset was picked in
// the open editor, so a reopened editor never named the sound. Now it names the preset last loaded or saved from a
// bar (remembered per machine for the session), with an "edited" mark once the parameters differ from it; without
// one, the preset the parameters match: the defaults ("Init") first, then the named presets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Editor
{
    internal static class PresetState
    {
        /// <summary>The bar's text when the sound is no preset it knows.</summary>
        public const string NoPreset = "Presets";

        /// <summary>ReBuzz's name for the defaults, and what the bar shows for it.</summary>
        public const string Defaults = "<default>", DefaultsName = "Init";

        sealed class Entry
        {
            public string Name;
        }

        static readonly ConditionalWeakTable<IMachine, Entry> loaded = new ConditionalWeakTable<IMachine, Entry>();

        /// <summary>A preset was loaded from (or saved by) a bar: the machine's sound is that preset now.</summary>
        public static void Loaded(IMachine machine, string name)
        {
            if (machine == null || string.IsNullOrEmpty(name)) return;
            loaded.GetValue(machine, _ => new Entry()).Name = name;
        }

        /// <summary>The preset last loaded or saved from a bar for this machine, or null.</summary>
        public static string Remembered(IMachine machine) =>
            machine != null && loaded.TryGetValue(machine, out var e) ? e.Name : null;

        /// <summary>The bar's text for a preset name ("&lt;default&gt;" reads "Init").</summary>
        public static string Display(string name) => name == Defaults ? DefaultsName : name ?? NoPreset;

        /// <summary>
        /// What the bar shows: the preset name (<see cref="Display"/> of <c>Name</c>; null when the sound is no preset
        /// known) and whether the parameters differ from it. The preset loaded last, while the parameters are its own
        /// or no other preset's; else the preset they match (the defaults first: a new machine reads "Init" even where
        /// a factory preset is the defaults too).
        /// </summary>
        public static (string Name, bool Edited) Current(IMachine machine)
        {
            if (machine == null) return (null, false);
            string remembered = Remembered(machine);
            if (remembered != null && Matches(machine, remembered)) return (remembered, false);
            string match = FindMatch(machine);
            if (match != null) return (match, false);
            return remembered != null ? (remembered, true) : (null, false);
        }

        /// <summary>The preset (the defaults, then the named ones alphabetically) whose state parameters equal the machine's now, or null.</summary>
        public static string FindMatch(IMachine machine)
        {
            if (Matches(machine, Defaults)) return Defaults;
            List<string> names;
            try
            {
                names = machine.GetPresetNames().Where(n => n != Defaults).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception)
            {
                names = new List<string>();
            }
            foreach (var n in names)
                if (Matches(machine, n)) return n;
            return null;
        }

        /// <summary>Whether every state parameter (each group but the inputs, every track) has the preset's value.</summary>
        public static bool Matches(IMachine machine, string name)
        {
            try
            {
                var values = Values(machine, name);
                if (values == null) return false;
                foreach (var g in machine.ParameterGroups)
                {
                    if (g.Type == ParameterGroupType.Input) continue;
                    for (int t = 0; t < g.TrackCount; t++)
                        foreach (var p in g.Parameters)
                        {
                            if ((p.Flags & ParameterFlags.State) == 0) continue;
                            int want = values.TryGetValue((p.Name, (byte)g.Type, t), out int v) ? v : p.DefValue;
                            if (p.GetValue(t) != want) return false;
                        }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A preset's values by (name, group, track), built once per preset object (the defaults: from the machine).
        static readonly ConditionalWeakTable<Preset, Dictionary<(string, byte, int), int>> tables =
            new ConditionalWeakTable<Preset, Dictionary<(string, byte, int), int>>();

        static Dictionary<(string, byte, int), int> Values(IMachine machine, string name)
        {
            if (name == Defaults) return new Dictionary<(string, byte, int), int>();   // every parameter at its default
            if (!machine.GetPresetNames().Contains(name)) return null;
            var preset = machine.GetPreset(name);
            if (preset?.Parameters == null) return null;
            return tables.GetValue(preset, p =>
            {
                var d = new Dictionary<(string, byte, int), int>();
                foreach (var x in p.Parameters) d.TryAdd((x.Name, x.Group, x.Track), x.Value);
                return d;
            });
        }
    }
}
