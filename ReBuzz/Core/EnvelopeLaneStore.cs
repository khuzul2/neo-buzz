using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/clips.md, FU2b): which envelope lanes a clip shows (the lanes added, the ones taken away and the ones collapsed), kept
    /// with the song. A clip is its machine's name and its own name. Kept in an "EnvelopeLanes" subsection, in .bmx, .bmxml and .nbl alike
    /// (upstream ReBuzz skips it), versioned: a newer version than this build knows is left unread. Like the mixer folds it is a view state: not an
    /// undo step, and it does not mark the song modified by itself; it is saved the next time the song is saved. A renamed machine keeps its lanes.
    /// </summary>
    internal sealed class EnvelopeLaneStore
    {
        public const string SubSectionName = "EnvelopeLanes";
        public const int FormatVersion = 1;

        /// <summary>What the stack shows for one clip.</summary>
        public sealed class State
        {
            public readonly List<ColumnKey> Added = new();
            public readonly HashSet<ColumnKey> Removed = new();
            public readonly HashSet<ColumnKey> Collapsed = new();
            public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Collapsed.Count == 0;
        }

        readonly ReBuzzCore buzz;
        readonly Dictionary<(string Machine, string Clip), State> states = new();

        internal EnvelopeLaneStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>The state of a clip (made when it has none); what the caller changes is what is saved.</summary>
        public State StateOf(IClip clip)
        {
            var key = (clip.Machine?.Name ?? "", clip.Name ?? "");
            if (!states.TryGetValue(key, out var st)) states[key] = st = new State();
            return st;
        }

        /// <summary>A machine renamed: its clips' lanes follow.</summary>
        public void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
            foreach (var k in states.Keys.Where(k => k.Machine == oldName).ToList())
            {
                states[(newName, k.Clip)] = states[k];
                states.Remove(k);
            }
        }

        /// <summary>A new song: none.</summary>
        public void Clear() => states.Clear();

        static string Text(ColumnKey k) => $"{k.Group}:{k.Index}:{k.Track}";

        static bool TryParse(string s, out ColumnKey key)
        {
            key = default;
            var p = (s ?? "").Split(':');
            if (p.Length != 3 || !int.TryParse(p[0], out int g) || !int.TryParse(p[1], out int i) || !int.TryParse(p[2], out int t)) return false;
            key = new ColumnKey(g, i, t);
            return true;
        }

        void OnSaveSong(ISaveSong save)
        {
            // Only clips still in the song; none with a view state, no subsection.
            var live = new HashSet<(string, string)>();
            foreach (var m in save.Song.Machines)
                foreach (var c in m.Clips) live.Add((m.Name, c.Name));
            var keep = states.Where(kv => !kv.Value.IsEmpty && live.Contains(kv.Key))
                .OrderBy(kv => kv.Key.Machine, StringComparer.Ordinal).ThenBy(kv => kv.Key.Clip, StringComparer.Ordinal).ToList();
            if (keep.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("EnvelopeLanes", new XAttribute("version", FormatVersion),
                keep.Select(kv => new XElement("Clip", new XAttribute("machine", kv.Key.Machine), new XAttribute("name", kv.Key.Clip),
                    kv.Value.Added.Select(k => new XElement("Added", new XAttribute("lane", Text(k)))),
                    kv.Value.Removed.OrderBy(Text, StringComparer.Ordinal).Select(k => new XElement("Removed", new XAttribute("lane", Text(k)))),
                    kv.Value.Collapsed.OrderBy(Text, StringComparer.Ordinal).Select(k => new XElement("Collapsed", new XAttribute("lane", Text(k)))))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            states.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    int version = int.TryParse((string)root.Attribute("version"), out int v) ? v : 1;
                    if (version > FormatVersion)
                        buzz.DCWriteLine($"Envelope lanes: saved by a newer version ({version}); not read.", DCLogLevel.Warning);
                    else
                        foreach (var c in root.Elements("Clip"))
                        {
                            string machine = (string)c.Attribute("machine"), name = (string)c.Attribute("name");
                            if (string.IsNullOrEmpty(machine) || name == null) continue;
                            var st = new State();
                            foreach (var e in c.Elements("Added")) if (TryParse((string)e.Attribute("lane"), out var k) && !st.Added.Contains(k)) st.Added.Add(k);
                            foreach (var e in c.Elements("Removed")) if (TryParse((string)e.Attribute("lane"), out var k)) st.Removed.Add(k);
                            foreach (var e in c.Elements("Collapsed")) if (TryParse((string)e.Attribute("lane"), out var k)) st.Collapsed.Add(k);
                            if (!st.IsEmpty) states[(machine, name)] = st;
                        }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Envelope lanes: " + e.Message, DCLogLevel.Warning);
            }
        }
    }
}
