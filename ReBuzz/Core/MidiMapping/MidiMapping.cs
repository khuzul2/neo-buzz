// Fork (docs/specs/midi-mapping.md, MM1): MIDI mapping for any controller. This file is the service's state: the maps (a computer's, a song's),
// the selected track, auto-arm, persistence and undo. MidiMapping.Router.cs runs the messages; MidiMapping.Api.cs is what the views and the
// agent edit through.
//
//   - Every MIDI input is a controller, named by its port (MidiDevice.Id). Any number work at once; nothing here knows a model.
//   - Track pages belong to a track of the track model (MT1, ux-overhaul.md section 11) and are saved in the song ("MidiMap" subsection, in .nbl,
//     .bmx and .bmxml alike). Global maps belong to the computer, with an optional copy in the song.
//   - The selected track gets the keys (by auto-arm) and the knobs (its page).
//   - Every edit is one step of the song's history (the computer's maps too: undo is the song's).

using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using ReBuzz.Core.Tracks;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    internal sealed partial class MidiMapping
    {
        public const string SubSectionName = "MidiMap";
        internal const string RegistrySection = "MidiMapping";
        const string ComputerKey = "Computer";
        const string AutoArmKey = "AutoArm";

        readonly ReBuzzCore buzz;
        readonly IRegistryEx registry;

        // UI thread. A published map is never edited: edits are made on clones and swapped in whole.
        ComputerMap computer;
        SongMap song = new SongMap();

        IMachine selected;
        bool autoArm = true;
        IMachine autoArmed;                 // the machine selecting armed (not one armed by hand)
        bool arming;                        // our own arm: the layout's event is not a person's
        bool rebuildPosted;

        /// <summary>The maps, the selection, or what is being listened for changed (UI thread).</summary>
        public event Action Changed;

        /// <summary>The selected track changed; the argument is who changed it (a view doesn't follow its own).</summary>
        public event Action<object> SelectionChanged;

        internal MidiMapping(ReBuzzCore buzz, IRegistryEx registry)
        {
            this.buzz = buzz;
            this.registry = registry;
            computer = ComputerMap.Parse(Read(ComputerKey));
            computerNoKeys = new HashSet<string>(computer.NoKeys, StringComparer.OrdinalIgnoreCase);
            autoArm = Read(AutoArmKey) != "0";
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
            if (buzz.SongCore is { } s)
            {
                s.MachineAdded += OnGraphChanged;
                s.MachineRemoved += OnGraphChanged;
                s.ConnectionAdded += OnWire;
                s.ConnectionRemoved += OnWire;
            }
            if (buzz.PinStore != null) buzz.PinStore.Changed += OnGraphChangedNoArg;
            if (buzz.SessionLayout != null) buzz.SessionLayout.InputsChanged += OnInputsChanged;
            Rebuild();
        }

        string Read(string key)
        {
            try { return registry?.Read(key, "", RegistrySection) ?? ""; }
            catch (Exception) { return ""; }
        }

        void Write(string key, string value)
        {
            try { registry?.Write(key, value, RegistrySection); }
            catch (Exception e) { buzz.DCWriteLine("MIDI mapping: could not save the maps: " + e.Message, DCLogLevel.Warning); }
        }

        // ------------------------------------------------------------------ the maps

        /// <summary>The computer's map: global entries, knob groups, roles (a copy).</summary>
        public ComputerMap Computer => computer.Clone();

        /// <summary>The song's map: the track pages and, when it has them, its own global entries (a copy).</summary>
        public SongMap Song => song.Clone();

        /// <summary>The global entries in use: the song's own when it has them, else the computer's.</summary>
        public IReadOnlyList<MapEntry> GlobalEntries => (song.Global ?? computer.Global).Select(e => e.Clone()).ToList();

        /// <summary>Whether the song carries its own global map.</summary>
        public bool UsesSongGlobal => song.Global != null;

        /// <summary>Whether selecting a track arms it (and disarms what selecting armed), so its keys play it. A person's arms are left alone.</summary>
        public bool AutoArm
        {
            get => autoArm;
            set
            {
                if (autoArm == value) return;
                autoArm = value;
                Write(AutoArmKey, value ? "1" : "0");
                if (value) ApplyAutoArm(); else ReleaseAutoArm();
                Changed?.Invoke();
            }
        }

        static string Xml(ComputerMap c, SongMap s) => c.ToXml().ToString(SaveOptions.DisableFormatting) + "|" + s.ToXml().ToString(SaveOptions.DisableFormatting);

        /// <summary>
        /// One undoable edit of the maps (a clone of each is changed and swapped in). False when nothing changed. Any thread but the MIDI
        /// threads' own: the song's history belongs to the UI thread.
        /// </summary>
        internal bool Edit(Action<ComputerMap, SongMap> change)
        {
            var c = computer.Clone();
            var s = song.Clone();
            change(c, s);
            if (Xml(c, s) == Xml(computer, song)) return false;
            var action = new MapEdit(this, computer, song, c, s);
            var stack = buzz.SongCore?.ActionStack;
            if (stack != null) stack.Do(action);
            else action.Do();
            return true;
        }

        void Replace(ComputerMap c, SongMap s)
        {
            bool computerChanged = Xml(c, new SongMap()) != Xml(computer, new SongMap());
            computer = c;
            song = s;
            computerNoKeys = new HashSet<string>(c.NoKeys, StringComparer.OrdinalIgnoreCase);
            if (computerChanged) Write(ComputerKey, c.ToXml().ToString(SaveOptions.DisableFormatting));
            buzz.SetModifiedFlag();
            Rebuild();
            Changed?.Invoke();
        }

        [HistoryName("MIDI mapping")]
        sealed class MapEdit : IAction
        {
            readonly MidiMapping owner;
            readonly ComputerMap cBefore, cAfter;
            readonly SongMap sBefore, sAfter;

            public MapEdit(MidiMapping owner, ComputerMap cBefore, SongMap sBefore, ComputerMap cAfter, SongMap sAfter)
            {
                this.owner = owner;
                this.cBefore = cBefore;
                this.sBefore = sBefore;
                this.cAfter = cAfter;
                this.sAfter = sAfter;
            }

            public void Do() => owner.Replace(cAfter.Clone(), sAfter.Clone());

            public void Undo() => owner.Replace(cBefore.Clone(), sBefore.Clone());
        }

        /// <summary>A new song: no pages and no song copy of the global map (the computer's maps stay).</summary>
        public void Clear()
        {
            selected = null;
            ReleaseAutoArm();
            if (song.IsEmpty) { Rebuild(); return; }
            song = new SongMap();
            Rebuild();
            Changed?.Invoke();
        }

        /// <summary>A machine was renamed: its mappings, and the pages of its track, follow it (the rename's undo renames it back and they come back with it).</summary>
        public void RenameMachine(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
            bool touched = false;
            void Retarget(MapDrive d)
            {
                if (d?.Target is { IsCommand: false } t && t.Machine == oldName) { d.Target = t.OnMachine(newName); touched = true; }
            }
            foreach (var e in computer.Global) Retarget(e);
            foreach (var e in song.Global ?? new List<MapEntry>()) Retarget(e);
            foreach (var t in song.Tracks)
            {
                foreach (var p in t.Pages)
                {
                    foreach (var d in p.Slots.Values) Retarget(d);
                    foreach (var e in p.Direct) Retarget(e);
                }
                if (t.TrackId == "track:" + oldName) { t.TrackId = "track:" + newName; touched = true; }
            }
            if (!touched) return;
            Write(ComputerKey, computer.ToXml().ToString(SaveOptions.DisableFormatting));
            buzz.SetModifiedFlag();
            Rebuild();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ the song

        void OnSaveSong(ISaveSong save)
        {
            // Only tracks and machines the song still has; nothing, no subsection.
            var names = new HashSet<string>(save.Song.Machines.Select(m => m.Name), StringComparer.Ordinal);
            var kept = song.Clone();
            kept.Tracks.RemoveAll(t => !names.Contains(t.TrackId.Substring(t.TrackId.IndexOf(':') + 1)));
            foreach (var t in kept.Tracks)
                foreach (var p in t.Pages)
                {
                    foreach (var i in p.Slots.Where(s => s.Value.Target is { IsCommand: false } m && !names.Contains(m.Machine)).Select(s => s.Key).ToList()) p.Slots.Remove(i);
                    p.Direct.RemoveAll(e => e.Target is { IsCommand: false } m && !names.Contains(m.Machine));
                }
            kept.Global?.RemoveAll(e => e.Target is { IsCommand: false } m && !names.Contains(m.Machine));
            if (kept.IsEmpty) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var bytes = new UTF8Encoding(false).GetBytes(kept.ToXml().ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            song = new SongMap();
            selected = null;
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    song = SongMap.FromXml(XDocument.Load(stream).Root, out bool newer);
                    if (newer) buzz.DCWriteLine("MIDI mapping: saved by a newer version; not read.", DCLogLevel.Warning);
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("MIDI mapping: " + e.Message, DCLogLevel.Warning);
            }
            Rebuild();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ tracks and the selection

        /// <summary>The song's track model now (worked out from the wires; a song of a few hundred machines takes a tenth of a millisecond).</summary>
        public TrackModel Model => SongTracks.Of(buzz.SongCore);

        /// <summary>The track a machine is on (its entry in the model), or null: a machine with no track.</summary>
        public TrackEntry EntryOf(IMachine machine, TrackModel model = null)
        {
            if (machine == null || buzz.SongCore == null) return null;
            model ??= Model;
            var node = model.Graph.Nodes.FirstOrDefault(n => ReferenceEquals(n.Tag, machine));
            return model.EntryOf(node);
        }

        /// <summary>A track by id ("track:Bass"), or null.</summary>
        public TrackEntry EntryById(string id, TrackModel model = null) => (model ?? Model).Entries.FirstOrDefault(e => e.Id == id);

        public static IMachine MachineOf(TrackNode node) => node?.Tag as IMachine;

        bool InSong(IMachine m) => m != null && buzz.SongCore?.Machines.Contains(m) == true;

        /// <summary>The selected machine (any machine on a track selects the track), or null.</summary>
        public IMachine SelectedMachine => selected != null && InSong(selected) ? selected : null;

        /// <summary>The selected track, or null: nothing is selected or the machine is on no track.</summary>
        public TrackEntry SelectedTrack => EntryOf(SelectedMachine);

        public string SelectedTrackId => SelectedTrack?.Id;

        /// <summary>The generator of the selected track (what its keys play), or null (a group, a return, the Master's chain).</summary>
        public IMachine SelectedGenerator => GeneratorOf(SelectedTrack);

        public static IMachine GeneratorOf(TrackEntry entry) =>
            entry != null && entry.Kind == TrackKind.Track && entry.Head.Kind == TrackNodeKind.Generator ? MachineOf(entry.Head) : null;

        /// <summary>The machines on a track's way to the Master, for what a page may hold: the track's own, then its groups' and the Master's chain.</summary>
        public IReadOnlyList<IMachine> ChainOf(TrackEntry entry, TrackModel model = null)
        {
            var list = new List<IMachine>();
            if (entry == null) return list;
            model ??= Model;
            for (var e = entry; e != null; e = e.Parent)
                foreach (var n in e.Machines)
                    if (MachineOf(n) is { } m && !list.Contains(m)) list.Add(m);
            if (model.MasterChain is { } chain && chain != entry)
                foreach (var n in chain.Machines)
                    if (MachineOf(n) is { } m && !list.Contains(m)) list.Add(m);
            return list;
        }

        /// <summary>Selects the track of a machine (null: none): its keys and knobs follow, and (<paramref name="arm"/>) it is armed for input when auto-arm is on. False when nothing changed.</summary>
        public bool Select(IMachine machine, object source = null, bool arm = true)
        {
            if (machine != null && !InSong(machine)) machine = null;
            if (ReferenceEquals(machine, selected)) return false;
            selected = machine;
            ApplyAutoArm(arm);
            Rebuild();
            SelectionChanged?.Invoke(source);
            Changed?.Invoke();
            return true;
        }

        /// <summary>The tracks (not groups or returns) in the model's order: what previous, next and Select track n walk.</summary>
        public IReadOnlyList<TrackEntry> Tracks => Model.Tracks;

        /// <summary>Selects the track n steps from the selected one (wrapping), or the first when none is selected.</summary>
        public bool StepTrack(int delta, object source = null)
        {
            var tracks = Tracks;
            if (tracks.Count == 0) return false;
            int at = SelectedTrack is { } t ? IndexOfEntry(tracks, t) : -1;
            int next = at < 0 ? (delta >= 0 ? 0 : tracks.Count - 1) : ((at + delta) % tracks.Count + tracks.Count) % tracks.Count;
            return Select(MachineOf(tracks[next].Head), source);
        }

        static int IndexOfEntry(IReadOnlyList<TrackEntry> list, TrackEntry e)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == e || list[i].Id == e.Id) return i;
            return -1;
        }

        /// <summary>Select track n (1 is the first).</summary>
        public bool SelectTrackNumber(int number, object source = null)
        {
            var tracks = Tracks;
            return number >= 1 && number <= tracks.Count && Select(MachineOf(tracks[number - 1].Head), source);
        }

        // ------------------------------------------------------------------ auto-arm (M3)

        void OnInputsChanged()
        {
            if (arming || autoArmed == null) return;
            // A person armed or disarmed something: what they disarmed is theirs now.
            if (buzz.SessionLayout?.IsMachineArmed(autoArmed) != true) autoArmed = null;
        }

        void ReleaseAutoArm()
        {
            if (autoArmed == null) return;
            var was = autoArmed;
            autoArmed = null;
            SetArm(was, false);
        }

        void SetArm(IMachine machine, bool on)
        {
            var layout = buzz.SessionLayout;
            if (layout == null) return;
            arming = true;
            try { layout.SetMachineArmed(machine, on); }
            finally { arming = false; }
        }

        /// <summary>
        /// The selected track's generator is armed for input, and the one selecting armed before is not (a track a person armed stays armed, for
        /// recording several at once). A machine that is not a note generator, or has no track in the Session View yet, is not armed.
        /// </summary>
        void ApplyAutoArm(bool arm = true)
        {
            var layout = buzz.SessionLayout;
            if (layout == null) return;
            var head = autoArm && arm ? SelectedGenerator : null;     // arm false: a pick in the Session View, where an armed track's empty slots turn into record buttons: the keys follow it, nothing is armed
            if (autoArmed != null && !ReferenceEquals(autoArmed, head))
            {
                var was = autoArmed;
                autoArmed = null;
                if (layout.IsMachineArmed(was)) SetArm(was, false);
            }
            if (head == null || ReferenceEquals(autoArmed, head)) return;
            if (!SessionLayout.CanArm(head) || SessionLayout.HasAudioInput(head)) return;
            if (layout.FirstTrackOf(head) < 0) return;
            if (layout.IsMachineArmed(head)) return;         // armed by hand: it is the person's
            SetArm(head, true);
            autoArmed = head;
        }

        /// <summary>The machine selecting armed (not one armed by hand), or null.</summary>
        public IMachine AutoArmedMachine => autoArmed;

        // ------------------------------------------------------------------ rebuilding the router's snapshot

        void OnGraphChanged(IMachine machine) => Touch();
        void OnGraphChangedNoArg() => Touch();
        void OnWire(IMachineConnection c) => Touch();

        /// <summary>The song's machines, wires or pins changed (any thread): the snapshot is rebuilt once the burst is over, on the UI thread.</summary>
        void Touch()
        {
            if (rebuildPosted) return;
            rebuildPosted = true;
            buzz.dispatcher.BeginInvoke(() =>
            {
                rebuildPosted = false;
                if (selected != null && !InSong(selected))
                {
                    selected = null;
                    ApplyAutoArm();
                    SelectionChanged?.Invoke(null);
                }
                Rebuild();
                Changed?.Invoke();
            });
        }
    }
}
