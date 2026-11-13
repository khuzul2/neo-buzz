// Fork (docs/specs/ux-overhaul.md P3c, decision 53): the names a machine gives its notes (a drum machine's pads, Neo
// Sampler's Kit mode), read the way the keyboard under a machine's window reads them: the managed machine's KeyNames
// (MIDI key -> name). A second optional property, KeyShortNames, gives names that fit the tracker's three characters
// ("BD"); without it a name is cut down: the initials of its words ("Closed hat" -> "CH"), else its first three letters.
// Only the portable build uses this (the WPF editor shows pitches, as before). Names are looked up on the UI thread, a
// dictionary kept for a quarter of a second so drawing a grid doesn't call the machine for every cell.

using BuzzGUI.Interfaces;
using System.Diagnostics;
using System.Reflection;

namespace WDE.ModernPatternEditor
{
    /// <summary>A machine's note names, or none.</summary>
    internal sealed class MachineNoteNames
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IMachine, MachineNoteNames> cache = new();

        public static MachineNoteNames For(IMachine machine) => machine == null ? null : cache.GetValue(machine, m => new MachineNoteNames(m));

        readonly IMachine machine;
        long checkedAt;
        object source;
        IReadOnlyDictionary<int, string> full, shortNames;
        readonly Dictionary<int, string> derived = new();

        MachineNoteNames(IMachine machine) { this.machine = machine; }

        void Refresh()
        {
            long now = Stopwatch.GetTimestamp();
            if (checkedAt != 0 && now - checkedAt < Stopwatch.Frequency / 4) return;
            checkedAt = now;
            try
            {
                var m = machine.ManagedMachine;
                source = m;
                full = m?.GetType().GetProperty("KeyNames", BindingFlags.Public | BindingFlags.Instance)?.GetValue(m) as IReadOnlyDictionary<int, string>;
                shortNames = m?.GetType().GetProperty("KeyShortNames", BindingFlags.Public | BindingFlags.Instance)?.GetValue(m) as IReadOnlyDictionary<int, string>;
                if (derived.Count > 0 && (full == null || full.Count == 0)) derived.Clear();
            }
            catch (Exception)
            {
                full = shortNames = null;      // a machine going away: no names
            }
        }

        /// <summary>Whether the machine names any of its notes (it plays drums).</summary>
        public bool Any { get { Refresh(); return full != null && full.Count > 0; } }

        /// <summary>The name of a MIDI note as the machine gives it ("Kick"), or null (a pitch).</summary>
        public string Name(int midi)
        {
            Refresh();
            return full != null && full.TryGetValue(midi, out var n) && !string.IsNullOrEmpty(n) ? n : null;
        }

        /// <summary>The name in at most three characters, or null.</summary>
        public string ShortName(int midi)
        {
            Refresh();
            if (shortNames != null && shortNames.TryGetValue(midi, out var s) && !string.IsNullOrEmpty(s)) return Fit(s);
            var name = Name(midi);
            if (name == null) return null;
            if (!derived.TryGetValue(midi, out var d) || derived.Count > 256)
            {
                if (derived.Count > 256) derived.Clear();
                derived[midi] = d = Abbreviate(name);
            }
            return d;
        }

        static string Fit(string s) => s.Length <= 3 ? s : s.Substring(0, 3);

        /// <summary>"Closed hat" -> "CH", "Kick" -> "Kic", "Built-in Snare" -> "BS".</summary>
        internal static string Abbreviate(string name)
        {
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
                return new string(words.Take(3).Select(w => char.ToUpperInvariant(w[0])).ToArray());
            return Fit(name);
        }
    }
}
