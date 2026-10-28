#nullable disable
// Session View layout (fork addition). See docs/specs/session-view.md, section 4.2.
//
// The grid as stored in a song: scenes (rows), tracks (columns, one per sequence) and which
// pattern sits in which slot. Pure data with no ReBuzz types, so it can be unit tested on its
// own (ReBuzz.SessionTests links this file). Tracks are identified by sequence index plus
// machine name, clips by pattern name; SessionLayout keeps these in step with the live song.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core.Session
{
    internal sealed class SessionDocument
    {
        /// <summary>Name of the song's BGUI subsection holding the layout.</summary>
        public const string SubSectionName = "SessionView";
        public const int FormatVersion = 1;

        /// <summary>A new or auto-filled grid shows at least this many scenes.</summary>
        public const int MinScenes = 8;

        public sealed class Scene
        {
            public string Name { get; set; } = "";
            /// <summary>Index into the theme's pattern colours, -1 for none.</summary>
            public int ColorIndex { get; set; } = -1;
            /// <summary>Tempo (BPM) set when the scene launches; 0 for none (0.5).</summary>
            public int Tempo { get; set; }
            /// <summary>Beats per bar set when the scene launches; 0 for none (0.5).</summary>
            public int BeatsPerBar { get; set; }
        }

        /// <summary>
        /// Launch settings of a clip (0.5, Live's clip view): stored per pattern, since a clip is a
        /// pattern. Ticks are pattern rows. Launch modes and quantizations are names of
        /// ClipLaunchMode and LaunchQuantization values ("Global" for the session's).
        /// </summary>
        public sealed class ClipSettings
        {
            public const string GlobalQuantization = "Global";

            public string Mode { get; set; } = "Trigger";
            public string Quantization { get; set; } = GlobalQuantization;
            public bool Legato { get; set; }
            public bool Loop { get; set; } = true;
            /// <summary>Start marker in ticks.</summary>
            public int Start { get; set; }
            public int LoopStart { get; set; }
            /// <summary>Loop end (or end, without loop) in ticks, exclusive; 0 for the pattern's end.</summary>
            public int LoopEnd { get; set; }
            /// <summary>Velocity sensitivity in percent: 0 none, 100 the softest note is silent.</summary>
            public int Velocity { get; set; }

            public bool IsDefault =>
                Mode == "Trigger" && Quantization == GlobalQuantization && !Legato && Loop &&
                Start == 0 && LoopStart == 0 && LoopEnd == 0 && Velocity == 0;

            public ClipSettings Clone() => (ClipSettings)MemberwiseClone();

            internal XElement ToXml(string machine, string pattern)
            {
                var e = new XElement("Clip", new XAttribute("machine", machine), new XAttribute("pattern", pattern));
                if (Mode != "Trigger") e.SetAttributeValue("mode", Mode);
                if (Quantization != GlobalQuantization) e.SetAttributeValue("quantization", Quantization);
                if (Legato) e.SetAttributeValue("legato", "true");
                if (!Loop) e.SetAttributeValue("loop", "false");
                if (Start != 0) e.SetAttributeValue("start", Start);
                if (LoopStart != 0) e.SetAttributeValue("loopStart", LoopStart);
                if (LoopEnd != 0) e.SetAttributeValue("loopEnd", LoopEnd);
                if (Velocity != 0) e.SetAttributeValue("velocity", Velocity);
                return e;
            }

            internal static ClipSettings FromXml(XElement e) => new ClipSettings
            {
                Mode = (string)e.Attribute("mode") ?? "Trigger",
                Quantization = (string)e.Attribute("quantization") ?? GlobalQuantization,
                Legato = (string)e.Attribute("legato") == "true",
                Loop = (string)e.Attribute("loop") != "false",
                Start = Math.Max(0, IntAttr(e, "start", 0)),
                LoopStart = Math.Max(0, IntAttr(e, "loopStart", 0)),
                LoopEnd = Math.Max(0, IntAttr(e, "loopEnd", 0)),
                Velocity = Math.Clamp(IntAttr(e, "velocity", 0), 0, 100)
            };
        }

        /// <summary>Clip settings by (machine, pattern); patterns without an entry use the defaults.</summary>
        public Dictionary<(string Machine, string Pattern), ClipSettings> Clips { get; } = new Dictionary<(string, string), ClipSettings>();

        /// <summary>A pattern's clip settings (a copy; the defaults if it has none).</summary>
        public ClipSettings GetClip(string machine, string pattern) =>
            Clips.TryGetValue((machine ?? "", pattern ?? ""), out var c) ? c.Clone() : new ClipSettings();

        /// <summary>Stores a pattern's clip settings; default settings remove the entry.</summary>
        public void SetClip(string machine, string pattern, ClipSettings settings)
        {
            var key = (machine ?? "", pattern ?? "");
            if (settings == null || settings.IsDefault)
                Clips.Remove(key);
            else
                Clips[key] = settings.Clone();
        }

        public sealed class Track
        {
            /// <summary>Index in ISong.Sequences when last reconciled.</summary>
            public int Sequence { get; set; }
            public string Machine { get; set; } = "";
            /// <summary>Index into the theme's pattern colours, -1 for none.</summary>
            public int ColorIndex { get; set; } = -1;
            /// <summary>Scene index to pattern name.</summary>
            public SortedDictionary<int, string> Slots { get; } = new SortedDictionary<int, string>();
        }

        /// <summary>What the layout needs to know about one sequence of the song.</summary>
        public readonly record struct SongTrack(string Machine, IReadOnlyList<string> Patterns);

        public int BeatsPerBar { get; set; } = 4;
        public string LaunchQuantization { get; set; } = "Bar";
        /// <summary>Length of clips created in the grid, in bars.</summary>
        public int ClipBars { get; set; } = 1;
        /// <summary>MIDI mapping stored with this song, overriding the user's; null for none.</summary>
        public SessionMidiMap SongMidi { get; set; }

        // Mixer (0.4)
        /// <summary>Whether the mixer section is shown under the grid.</summary>
        public bool ShowMixer { get; set; } = true;
        /// <summary>Whether the clip panel is shown beside the grid (0.5).</summary>
        public bool ShowClipPanel { get; set; } = true;
        /// <summary>Machines shown as return tracks (by name, in order); tracks send to them.</summary>
        public List<string> Returns { get; } = new List<string>();
        /// <summary>Machine the Cue buttons connect tracks to (a second output), or "" for none.</summary>
        public string CueMachine { get; set; } = "";
        /// <summary>Machine groups shown folded (by name).</summary>
        public HashSet<string> CollapsedGroups { get; } = new HashSet<string>();
        public List<Scene> Scenes { get; } = new List<Scene>();
        public List<Track> Tracks { get; } = new List<Track>();

        // ------------------------------------------------------------------ building

        /// <summary>
        /// Grid for a song without session data: one track per sequence, each machine's patterns
        /// down the scenes in order.
        /// </summary>
        public static SessionDocument AutoFill(IReadOnlyList<SongTrack> song)
        {
            var doc = new SessionDocument();
            for (int i = 0; i < song.Count; i++)
            {
                var track = new Track { Sequence = i, Machine = song[i].Machine ?? "" };
                var patterns = song[i].Patterns ?? Array.Empty<string>();
                for (int p = 0; p < patterns.Count; p++)
                    track.Slots[p] = patterns[p];
                doc.Tracks.Add(track);
            }
            doc.EnsureScenes();
            return doc;
        }

        /// <summary>
        /// Brings the tracks in line with the song's sequences (same order) and drops slots whose
        /// pattern no longer exists. A track is matched to a sequence by, in order: same index and
        /// machine; same machine elsewhere (sequence moved); same index whose machine is gone from
        /// the song (machine renamed). Unmatched sequences get empty tracks; unmatched tracks go.
        /// </summary>
        public void Reconcile(IReadOnlyList<SongTrack> song)
        {
            var used = new HashSet<Track>();
            var songMachines = new HashSet<string>(song.Select(s => s.Machine ?? ""));
            var result = new List<Track>(song.Count);

            for (int i = 0; i < song.Count; i++)
            {
                string machine = song[i].Machine ?? "";
                var track =
                    Tracks.FirstOrDefault(t => !used.Contains(t) && t.Sequence == i && t.Machine == machine) ??
                    Tracks.FirstOrDefault(t => !used.Contains(t) && t.Machine == machine) ??
                    Tracks.FirstOrDefault(t => !used.Contains(t) && t.Sequence == i && !songMachines.Contains(t.Machine)) ??
                    new Track();
                used.Add(track);
                track.Sequence = i;
                track.Machine = machine;

                var patterns = new HashSet<string>(song[i].Patterns ?? Array.Empty<string>());
                foreach (var scene in track.Slots.Where(s => !patterns.Contains(s.Value)).Select(s => s.Key).ToList())
                    track.Slots.Remove(scene);

                result.Add(track);
            }

            Tracks.Clear();
            Tracks.AddRange(result);
            EnsureScenes();

            // Clip settings of deleted patterns go (machines without a track keep theirs: the
            // track may come back with an undo).
            var patternsByMachine = new Dictionary<string, HashSet<string>>();
            foreach (var t in song)
            {
                if (!patternsByMachine.TryGetValue(t.Machine ?? "", out var set))
                    patternsByMachine[t.Machine ?? ""] = set = new HashSet<string>();
                set.UnionWith(t.Patterns ?? Array.Empty<string>());
            }
            foreach (var key in Clips.Keys.Where(k => patternsByMachine.TryGetValue(k.Machine, out var set) && !set.Contains(k.Pattern)).ToList())
                Clips.Remove(key);
        }

        /// <summary>Makes sure every used scene row exists, and at least <see cref="MinScenes"/>.</summary>
        public void EnsureScenes()
        {
            int needed = MinScenes;
            foreach (var t in Tracks)
                if (t.Slots.Count > 0) needed = Math.Max(needed, t.Slots.Keys.Max() + 1);
            while (Scenes.Count < needed)
                Scenes.Add(new Scene());
        }

        // ------------------------------------------------------------------ editing

        public string GetSlot(int track, int scene) =>
            track >= 0 && track < Tracks.Count && Tracks[track].Slots.TryGetValue(scene, out var p) ? p : null;

        public void SetSlot(int track, int scene, string pattern)
        {
            if (track < 0 || track >= Tracks.Count) throw new ArgumentOutOfRangeException(nameof(track));
            if (scene < 0) throw new ArgumentOutOfRangeException(nameof(scene));
            if (string.IsNullOrEmpty(pattern))
                Tracks[track].Slots.Remove(scene);
            else
                Tracks[track].Slots[scene] = pattern;
            EnsureScenes();
        }

        public void ClearSlot(int track, int scene) => SetSlot(track, scene, null);

        /// <summary>Inserts an empty scene at <paramref name="index"/>, moving later rows down.</summary>
        public void InsertScene(int index)
        {
            index = Math.Clamp(index, 0, Scenes.Count);
            Scenes.Insert(index, new Scene());
            foreach (var t in Tracks)
                ShiftSlots(t, index, +1);
        }

        /// <summary>Removes a scene and its slots, moving later rows up.</summary>
        public void RemoveScene(int index)
        {
            if (index < 0 || index >= Scenes.Count) return;
            Scenes.RemoveAt(index);
            foreach (var t in Tracks)
            {
                t.Slots.Remove(index);
                ShiftSlots(t, index + 1, -1);
            }
            EnsureScenes();
        }

        /// <summary>Inserts a copy of a scene (name, colour, slots) below it.</summary>
        public void DuplicateScene(int index)
        {
            if (index < 0 || index >= Scenes.Count) return;
            var source = Scenes[index];
            InsertScene(index + 1);
            Scenes[index + 1].Name = source.Name;
            Scenes[index + 1].ColorIndex = source.ColorIndex;
            Scenes[index + 1].Tempo = source.Tempo;
            Scenes[index + 1].BeatsPerBar = source.BeatsPerBar;
            foreach (var t in Tracks)
                if (t.Slots.TryGetValue(index, out var p))
                    t.Slots[index + 1] = p;
        }

        /// <summary>
        /// Moves (or copies) a slot's clip to another scene of the same track, replacing what was
        /// there. Returns false if the source slot is empty.
        /// </summary>
        public bool MoveSlot(int track, int fromScene, int toScene, bool copy)
        {
            if (track < 0 || track >= Tracks.Count || toScene < 0) return false;
            var slots = Tracks[track].Slots;
            if (!slots.TryGetValue(fromScene, out var pattern)) return false;
            if (fromScene == toScene) return true;
            if (!copy) slots.Remove(fromScene);
            slots[toScene] = pattern;
            EnsureScenes();
            return true;
        }

        static void ShiftSlots(Track track, int from, int delta)
        {
            var moved = track.Slots.Where(s => s.Key >= from).ToList();
            foreach (var s in moved) track.Slots.Remove(s.Key);
            foreach (var s in moved) track.Slots[s.Key + delta] = s.Value;
        }

        public void RenamePattern(string machine, string oldName, string newName)
        {
            foreach (var t in Tracks.Where(t => t.Machine == machine))
                foreach (var scene in t.Slots.Where(s => s.Value == oldName).Select(s => s.Key).ToList())
                    t.Slots[scene] = newName;
            if (Clips.Remove((machine, oldName), out var clip))
                Clips[(machine, newName)] = clip;
        }

        public void RenameMachine(string oldName, string newName)
        {
            foreach (var t in Tracks.Where(t => t.Machine == oldName))
                t.Machine = newName;
            foreach (var key in Clips.Keys.Where(k => k.Machine == oldName).ToList())
            {
                Clips.Remove(key, out var clip);
                Clips[(newName, key.Pattern)] = clip;
            }
            for (int i = 0; i < Returns.Count; i++)
                if (Returns[i] == oldName) Returns[i] = newName;
            if (CueMachine == oldName) CueMachine = newName;
        }

        /// <summary>Patterns of a scene row: (track index, pattern name) for every filled slot.</summary>
        public IEnumerable<(int Track, string Pattern)> SceneSlots(int scene)
        {
            for (int i = 0; i < Tracks.Count; i++)
                if (Tracks[i].Slots.TryGetValue(scene, out var p))
                    yield return (i, p);
        }

        // ------------------------------------------------------------------ persistence

        public XDocument ToXml()
        {
            var root = new XElement("Session",
                new XAttribute("version", FormatVersion),
                new XAttribute("beatsPerBar", BeatsPerBar),
                new XAttribute("launchQuantization", LaunchQuantization ?? "Bar"),
                new XAttribute("clipBars", ClipBars),
                ShowClipPanel ? null : new XAttribute("showClipPanel", "false"),
                new XElement("Scenes", Scenes.Select(s =>
                {
                    var e = new XElement("Scene");
                    if (!string.IsNullOrEmpty(s.Name)) e.SetAttributeValue("name", s.Name);
                    if (s.ColorIndex >= 0) e.SetAttributeValue("colorIndex", s.ColorIndex);
                    if (s.Tempo > 0) e.SetAttributeValue("tempo", s.Tempo);
                    if (s.BeatsPerBar > 0) e.SetAttributeValue("beatsPerBar", s.BeatsPerBar);
                    return e;
                })),
                new XElement("Tracks", Tracks.Select(t =>
                {
                    var e = new XElement("Track",
                        new XAttribute("sequence", t.Sequence),
                        new XAttribute("machine", t.Machine ?? ""));
                    if (t.ColorIndex >= 0) e.SetAttributeValue("colorIndex", t.ColorIndex);
                    e.Add(t.Slots.Select(s => new XElement("Slot",
                        new XAttribute("scene", s.Key),
                        new XAttribute("pattern", s.Value))));
                    return e;
                })),
                MixerXml(),
                Clips.Count == 0 ? null : new XElement("Clips",
                    Clips.OrderBy(c => c.Key.Machine, StringComparer.Ordinal).ThenBy(c => c.Key.Pattern, StringComparer.Ordinal)
                         .Select(c => c.Value.ToXml(c.Key.Machine, c.Key.Pattern))),
                SongMidi?.ToXml());
            return new XDocument(root);
        }

        XElement MixerXml()
        {
            var e = new XElement("Mixer", new XAttribute("show", ShowMixer ? "true" : "false"));
            if (!string.IsNullOrEmpty(CueMachine)) e.SetAttributeValue("cue", CueMachine);
            e.Add(Returns.Select(r => new XElement("Return", new XAttribute("machine", r))));
            e.Add(CollapsedGroups.OrderBy(g => g, StringComparer.Ordinal).Select(g => new XElement("Collapsed", new XAttribute("group", g))));
            return e;
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            Save(ms);
            return ms.ToArray();
        }

        /// <summary>Writes UTF-8 XML; leaves the stream open.</summary>
        public void Save(Stream stream)
        {
            var bytes = new UTF8Encoding(false).GetBytes(ToXml().ToString(SaveOptions.None));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// Reads a layout. Unknown elements and attributes are ignored and missing ones take
        /// defaults, so newer files still open. Returns null if the data isn't a session layout.
        /// </summary>
        public static SessionDocument Load(Stream stream)
        {
            if (stream == null) return null;
            try
            {
                var root = XDocument.Load(stream).Root;
                if (root == null || root.Name.LocalName != "Session") return null;

                var doc = new SessionDocument
                {
                    BeatsPerBar = Math.Clamp(IntAttr(root, "beatsPerBar", 4), 1, 64),
                    LaunchQuantization = (string)root.Attribute("launchQuantization") ?? "Bar",
                    ClipBars = Math.Clamp(IntAttr(root, "clipBars", 1), 1, 64),
                    ShowClipPanel = (string)root.Attribute("showClipPanel") != "false",
                    SongMidi = root.Element("Midi") is XElement midi ? SessionMidiMap.FromXml(midi) : null,
                };
                if (root.Element("Mixer") is XElement mixer)
                {
                    doc.ShowMixer = (string)mixer.Attribute("show") != "false";
                    doc.CueMachine = (string)mixer.Attribute("cue") ?? "";
                    foreach (var r in mixer.Elements("Return"))
                        if ((string)r.Attribute("machine") is string name && name.Length > 0 && !doc.Returns.Contains(name))
                            doc.Returns.Add(name);
                    foreach (var g in mixer.Elements("Collapsed"))
                        if ((string)g.Attribute("group") is string name && name.Length > 0)
                            doc.CollapsedGroups.Add(name);
                }
                foreach (var s in root.Element("Scenes")?.Elements("Scene") ?? Enumerable.Empty<XElement>())
                    doc.Scenes.Add(new Scene
                    {
                        Name = (string)s.Attribute("name") ?? "",
                        ColorIndex = IntAttr(s, "colorIndex", -1),
                        Tempo = Math.Clamp(IntAttr(s, "tempo", 0), 0, 500),
                        BeatsPerBar = Math.Clamp(IntAttr(s, "beatsPerBar", 0), 0, 64)
                    });
                foreach (var c in root.Element("Clips")?.Elements("Clip") ?? Enumerable.Empty<XElement>())
                {
                    string machine = (string)c.Attribute("machine");
                    string pattern = (string)c.Attribute("pattern");
                    if (!string.IsNullOrEmpty(machine) && !string.IsNullOrEmpty(pattern))
                        doc.SetClip(machine, pattern, ClipSettings.FromXml(c));
                }
                foreach (var t in root.Element("Tracks")?.Elements("Track") ?? Enumerable.Empty<XElement>())
                {
                    var track = new Track
                    {
                        Sequence = IntAttr(t, "sequence", doc.Tracks.Count),
                        Machine = (string)t.Attribute("machine") ?? "",
                        ColorIndex = IntAttr(t, "colorIndex", -1),
                    };
                    foreach (var slot in t.Elements("Slot"))
                    {
                        int scene = IntAttr(slot, "scene", -1);
                        string pattern = (string)slot.Attribute("pattern");
                        if (scene >= 0 && !string.IsNullOrEmpty(pattern))
                            track.Slots[scene] = pattern;
                    }
                    doc.Tracks.Add(track);
                }
                doc.EnsureScenes();
                return doc;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        static int IntAttr(XElement e, string name, int fallback) =>
            int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
