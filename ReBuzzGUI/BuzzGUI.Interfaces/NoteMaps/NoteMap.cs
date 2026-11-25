#nullable enable
// Fork (docs/specs/daw-modes.md §8.9, NM1): a machine's note map as the views read it: for each key 0 to 127 whether it plays, a short
// name, a group and a colour hint, and a version that changes whenever the map does. A snapshot is immutable: any thread reads it
// without a lock, and a change makes a new one.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;

namespace BuzzGUI.Interfaces
{
    public sealed class NoteMap
    {
        public const int MaxNameLength = 40;

        readonly bool[] playable = new bool[128];
        readonly string?[] names = new string?[128];
        readonly string?[] groups = new string?[128];
        readonly uint[] colors = new uint[128];
        readonly int[] keys;

        /// <summary>No map: nothing is known about the machine's keys (every key is "possible", none is named).</summary>
        public static readonly NoteMap None = new NoteMap(Array.Empty<NoteMapEntry>(), 0, "none", -1);

        NoteMap(IReadOnlyList<NoteMapEntry> entries, int version, string source, int channel)
        {
            Version = version;
            Source = source;
            Channel = channel;
            foreach (var e in entries)
            {
                if (e.Key < 0 || e.Key > 127) continue;
                playable[e.Key] = true;
                string? name = Clean(e.Name);
                if (name != null) names[e.Key] = name;
                string? group = Clean(e.Group);
                if (group != null) groups[e.Key] = group;
                if (e.Color != 0) colors[e.Key] = e.Color;
            }
            var list = new List<int>();
            for (int k = 0; k < 128; k++)
            {
                if (!playable[k]) continue;
                list.Add(k);
                if (names[k] != null) NamedCount++;
            }
            keys = list.ToArray();
        }

        static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength) : s;
        }

        /// <summary>Makes a snapshot. A key listed twice keeps the last name, group and colour that were given for it.</summary>
        public static NoteMap Create(IEnumerable<NoteMapEntry>? entries, int version, string source, int channel = -1)
        {
            if (entries == null) return None;
            var list = entries as IReadOnlyList<NoteMapEntry> ?? new List<NoteMapEntry>(entries);
            return list.Count == 0 ? None : new NoteMap(list, version, source, channel);
        }

        /// <summary>
        /// A number that changes whenever the machine's map does (per machine, rising; 0 for <see cref="None"/>). Views keep the
        /// version they drew and redraw when it moves.
        /// </summary>
        public int Version { get; }

        /// <summary>Where the map came from: "machine" (a Neo machine or a plug-in's own names), "manifest", or "none".</summary>
        public string Source { get; }

        /// <summary>The MIDI channel this snapshot is for (0 to 15), or -1 for any.</summary>
        public int Channel { get; }

        /// <summary>Whether the machine reports its keys at all. False: the views behave as if there were no map.</summary>
        public bool IsEmpty => keys.Length == 0;

        /// <summary>How many keys make a sound.</summary>
        public int PlayableCount => keys.Length;

        /// <summary>How many playable keys have a name.</summary>
        public int NamedCount { get; }

        /// <summary>Whether any key has a name (a drum kit's pads do; a sampler's melodic zones don't).</summary>
        public bool HasNames => NamedCount > 0;

        /// <summary>The playable keys, ascending.</summary>
        public IReadOnlyList<int> PlayableKeys => keys;

        public bool IsPlayable(int key) => key >= 0 && key <= 127 && playable[key];

        /// <summary>The key's short name ("Kick"), or null.</summary>
        public string? NameOf(int key) => key >= 0 && key <= 127 ? names[key] : null;

        public string? GroupOf(int key) => key >= 0 && key <= 127 ? groups[key] : null;

        /// <summary>The key's colour hint as ARGB, or 0.</summary>
        public uint ColorOf(int key) => key >= 0 && key <= 127 ? colors[key] : 0;

        /// <summary>The map's entries again, in key order.</summary>
        public IEnumerable<NoteMapEntry> Entries()
        {
            foreach (int k in keys) yield return new NoteMapEntry(k, names[k], groups[k], colors[k]);
        }

        /// <summary>Whether another snapshot says the same about every key (the version aside).</summary>
        public bool SameKeysAs(NoteMap other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (keys.Length != other.keys.Length) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                int k = keys[i];
                if (k != other.keys[i] || names[k] != other.names[k] || groups[k] != other.groups[k] || colors[k] != other.colors[k]) return false;
            }
            return true;
        }

        /// <summary>The playable keys as ranges of note numbers ("36-40, 60-72"), for tools and tooltips.</summary>
        public string Ranges()
        {
            if (keys.Length == 0) return "";
            var parts = new List<string>();
            int from = keys[0], prev = keys[0];
            for (int i = 1; i <= keys.Length; i++)
            {
                if (i < keys.Length && keys[i] == prev + 1) { prev = keys[i]; continue; }
                parts.Add(from == prev ? from.ToString() : from + "-" + prev);
                if (i < keys.Length) from = prev = keys[i];
            }
            return string.Join(", ", parts);
        }
    }
}
