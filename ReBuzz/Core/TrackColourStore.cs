using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using ReBuzz.Core.Tracks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/ux-overhaul.md §11, MX1): a colour per track, "option A". The colour is an index into the clip palette
    /// (<see cref="SessionColourChoices"/>) and is kept for the track's head machine (its generator), by name, in a "TrackColours"
    /// subsection: .bmx, .bmxml and .nbl alike, versioned (a newer version than this build knows is left unread). Songs from before MX1
    /// have none, and are given them when they open, in track order; that, and the colour a new generator is given when it is added,
    /// is silent: no undo step and no modified flag of its own, so it is saved only when the song is saved. A colour the user sets is
    /// one undo step and always wins: over the one given, and over the Session View's track colour menu entry, which is a user's too
    /// (<see cref="IndexOf"/> answers the Session track's first, then this store's).
    ///
    /// A new track gets the first palette colour that no other track uses, skipping those too close to a state colour
    /// (<see cref="ReservedForState"/>, which the studio sets: the colour menus skip the same ones); when all are used, the least used.
    /// </summary>
    internal sealed class TrackColourStore
    {
        public const string SubSectionName = "TrackColours";
        public const int FormatVersion = 1;

        /// <summary>The studio's test for "too close to a play, record or override colour"; none here means every colour is fair.</summary>
        public static Func<uint, bool> ReservedForState { get; set; }

        readonly ReBuzzCore buzz;
        readonly Dictionary<string, int> colours = new(StringComparer.Ordinal);
        Dictionary<IMachine, int> resolved;
        bool consultSession = true;

        internal TrackColourStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
            if (buzz.SongCore != null) buzz.SongCore.MachineAdded += OnMachineAdded;
            if (buzz.SessionLayout != null) buzz.SessionLayout.Changed += Invalidate;
        }

        /// <summary>Any colour changed (a person, an agent, undo, a song opening, one given to a new machine).</summary>
        public event Action Changed;

        void Invalidate() => resolved = null;

        void Raise()
        {
            Invalidate();
            Changed?.Invoke();
        }

        /// <summary>The number of colours the palette has.</summary>
        public static int PaletteSize => (SessionColourChoices.Choices ?? SessionColourChoices.Defaults).Length;

        /// <summary>The colour kept for a machine by name (given or the user's), or -1.</summary>
        public int Get(string machine) => machine != null && colours.TryGetValue(machine, out var c) ? c : -1;

        /// <summary>
        /// A machine's track colour, or -1 for none: the Session View's track colour when the user gave the machine's track one, else
        /// this store's. The one answer every view draws.
        /// </summary>
        public int IndexOf(IMachine machine)
        {
            if (machine == null) return -1;
            var map = resolved ??= BuildMap();
            return map.TryGetValue(machine, out var c) ? c : -1;
        }

        Dictionary<IMachine, int> BuildMap()
        {
            var map = new Dictionary<IMachine, int>();
            var song = buzz.SongCore;
            if (song == null) return map;
            foreach (var m in song.Machines)
                if (colours.TryGetValue(m.Name, out var c)) map[m] = c;
            // A colour picked on a Session track is the user's, and wins.
            var layout = buzz.SessionLayout;
            var doc = consultSession ? layout?.Document : null;
            if (doc != null)
                for (int t = 0; t < doc.Tracks.Count; t++)
                {
                    if (doc.Tracks[t].ColorIndex < 0) continue;
                    var machine = layout.SequenceOf(t)?.Machine;
                    if (machine != null) map[machine] = doc.Tracks[t].ColorIndex;
                }
            return map;
        }

        /// <summary>Whether the machine has a colour of its own to give: a generator, or an effect that starts a track.</summary>
        static bool IsTrackHead(IMachine m) => m?.DLL?.Info != null && m.DLL.Info.Type == MachineType.Generator && !m.IsControlMachine;

        /// <summary>
        /// Sets a machine's track colour (an index into the palette; -1 gives it the first colour no other track uses): one undo step.
        /// The Session View's own track colour, when its track has one, wins over this: the studio's colour menus set both.
        /// </summary>
        public bool Set(IMachine machine, int index)
        {
            if (machine == null) return false;
            int size = PaletteSize;
            if (size == 0) return false;
            if (index >= size) index %= size;
            if (index < 0) index = FirstFree(machine);
            int before = Get(machine.Name);
            if (before == index) return false;
            buzz.SongCore.ActionStack.Do(new ColourChange(this, machine.Name, before, index));
            return true;
        }

        /// <summary>
        /// The user's (or an agent's) choice of a machine's colour: one undo step that sets this store's and the Session View's own
        /// track colour of the machine's track, when that track has one, so the choice wins everywhere.
        /// </summary>
        public bool SetByUser(IMachine machine, int index)
        {
            if (machine == null || PaletteSize == 0) return false;
            if (index >= PaletteSize) index %= PaletteSize;
            if (index < 0) index = FirstFree(machine);
            var layout = buzz.SessionLayout;
            var doc = layout?.Document;
            bool changed = false;
            buzz.SongCore.BeginActionGroup();
            try
            {
                if (doc != null)
                    for (int t = 0; t < doc.Tracks.Count; t++)
                        if (layout.SequenceOf(t)?.Machine == machine && doc.Tracks[t].ColorIndex >= 0 && doc.Tracks[t].ColorIndex != index)
                        {
                            layout.SetTrackColor(t, index);
                            changed = true;
                        }
                changed |= Set(machine, index);
            }
            finally
            {
                buzz.SongCore.EndActionGroup();
            }
            return changed;
        }

        /// <summary>A palette colour's name ("Red"), or null for an index out of the palette.</summary>
        public static string NameOf(int index)
        {
            var palette = SessionColourChoices.Choices ?? SessionColourChoices.Defaults;
            return index >= 0 && palette.Length > 0 ? palette[index % palette.Length].Name : null;
        }

        /// <summary>A palette colour by name (any case) or by index; -1 when it is none of them.</summary>
        public static int Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            text = text.Trim();
            var palette = SessionColourChoices.Choices ?? SessionColourChoices.Defaults;
            for (int i = 0; i < palette.Length; i++)
                if (string.Equals(palette[i].Name, text, StringComparison.OrdinalIgnoreCase)) return i;
            return int.TryParse(text, out int n) && n >= 0 && n < palette.Length ? n : -1;
        }

        internal void Replace(string machine, int index)
        {
            if (index < 0) colours.Remove(machine);
            else colours[machine] = index;
            buzz.SetModifiedFlag();
            Raise();
        }

        /// <summary>A machine renamed: its colour follows (no step of its own: the rename's undo renames it back).</summary>
        public void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
            if (!colours.TryGetValue(oldName, out var c)) return;
            colours.Remove(oldName);
            colours[newName] = c;
            Raise();
        }

        /// <summary>A new song: none (no step, no modified flag).</summary>
        public void Clear()
        {
            if (colours.Count == 0) return;
            colours.Clear();
            Raise();
        }

        // ------------------------------------------------------------------ giving colours

        void OnMachineAdded(IMachine machine)
        {
            if (buzz.SongLoading || !IsTrackHead(machine)) return;
            if (Get(machine.Name) >= 0) return;
            colours[machine.Name] = FirstFree(machine);
            Raise();
        }

        /// <summary>The first palette colour no other track uses and no state colour is close to; the least used when all are used.</summary>
        int FirstFree(IMachine machine)
        {
            var palette = SessionColourChoices.Choices ?? SessionColourChoices.Defaults;
            if (palette.Length == 0) return -1;
            var uses = new int[palette.Length];
            var song = buzz.SongCore;
            if (song != null)
            {
                foreach (var m in song.Machines)
                {
                    if (m == machine || !IsTrackHead(m)) continue;
                    int c = IndexOf(m);
                    if (c >= 0) uses[c % palette.Length]++;
                }
            }
            var reserved = ReservedForState;
            int best = -1;
            for (int i = 0; i < palette.Length; i++)
            {
                if (reserved != null && reserved(palette[i].Argb)) continue;
                if (uses[i] == 0) return i;
                if (best < 0 || uses[i] < uses[best]) best = i;
            }
            return best >= 0 ? best : 0;
        }

        /// <summary>
        /// Gives the tracks that have no colour one, in track order (the generators in the song's order). Silent: not a step, not a
        /// change to save by itself. Returns how many were given.
        /// </summary>
        public int AssignMissing(bool consultSession = true)
        {
            var song = buzz.SongCore;
            if (song == null) return 0;
            Invalidate();
            this.consultSession = consultSession;
            var order = new List<IMachine>();
            try
            {
                var model = SongTracks.Of(song);
                order.AddRange(model.Entries.Where(e => e.Kind == TrackKind.Track).Select(e => SongTracks.MachineOf(e.Head)).Where(IsTrackHead));
            }
            catch (InvalidOperationException)
            {
                // A machine added on another thread while the list was read: the generators below are the same list.
            }
            order.AddRange(song.Machines.Where(m => IsTrackHead(m) && !order.Contains(m)));
            int given = 0;
            foreach (var m in order)
            {
                if (IndexOf(m) >= 0) continue;
                colours[m.Name] = FirstFree(m);
                Invalidate();
                given++;
            }
            this.consultSession = true;
            if (given > 0) Raise();
            return given;
        }

        // ------------------------------------------------------------------ undo

        [BuzzGUI.Common.HistoryName("Track colour")]
        sealed class ColourChange : IAction
        {
            readonly TrackColourStore store;
            readonly string machine;
            readonly int before, after;

            public ColourChange(TrackColourStore store, string machine, int before, int after)
            {
                this.store = store;
                this.machine = machine;
                this.before = before;
                this.after = after;
            }

            public void Do() => store.Replace(machine, after);

            public void Undo() => store.Replace(machine, before);
        }

        // ------------------------------------------------------------------ files

        void OnSaveSong(ISaveSong save)
        {
            var names = new HashSet<string>(save.Song.Machines.Select(m => m.Name), StringComparer.Ordinal);
            var live = colours.Where(c => names.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
            if (live.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("TrackColours", new XAttribute("version", FormatVersion),
                live.Select(c => new XElement("Machine", new XAttribute("name", c.Key), new XAttribute("colour", c.Value))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            colours.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    int version = int.TryParse((string)root.Attribute("version"), out int v) ? v : 1;
                    if (version > FormatVersion)
                        buzz.DCWriteLine($"Track colours: saved by a newer version ({version}); not read.", DCLogLevel.Warning);
                    else
                        foreach (var m in root.Elements("Machine"))
                        {
                            string name = (string)m.Attribute("name");
                            if (!string.IsNullOrEmpty(name) && int.TryParse((string)m.Attribute("colour"), out int c) && c >= 0) colours[name] = c;
                        }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Track colours: " + e.Message, DCLogLevel.Warning);
            }
            Invalidate();
            // The tracks a song has no colour for (songs from before this, or machines added by hand to the file): in track order, quietly.
            // The session layout reads its own subsection in its own handler, so it is not asked here: the colour entries it holds win whatever is given.
            AssignMissing(consultSession: false);
            Raise();
        }
    }
}
