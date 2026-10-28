using BuzzGUI.Common;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Session View layout of the current song (fork addition, docs/specs/session-view.md 4.2):
    /// a <see cref="SessionDocument"/> kept in step with the live song and stored in the song
    /// file's "SessionView" subsection. Lives as long as ReBuzzCore, whether or not the Session
    /// window is open, so renames and saves are tracked either way. UI thread only.
    ///
    /// A song gets a layout the first time the session is used (<see cref="Ensure"/>, which
    /// auto-fills it) or when it is loaded with one. Songs that never use the session are saved
    /// without a SessionView subsection.
    ///
    /// Structural song changes are reconciled lazily: SongCore raises some events on
    /// intermediate states (SwapSequences briefly holds the same sequence at both indexes), so
    /// an event only marks the layout dirty, and the next read reconciles it against the
    /// settled song. <see cref="Changed"/> is raised once per batch, from the dispatcher.
    /// Renames are applied at once, since they carry the old name.
    ///
    /// Grid edits (0.2) go through <see cref="UndoStack"/>, the session's own undo history:
    /// layout changes as before/after snapshots, clip changes with BuzzGUI's pattern actions.
    /// </summary>
    internal sealed class SessionLayout
    {
        readonly ReBuzzCore buzz;
        readonly SongCore song;
        readonly IUiDispatcher dispatcher;
        SessionDocument document;
        bool dirty;
        bool changePosted;

        // Last known names, to turn PropertyChanged("Name") into renames.
        readonly Dictionary<IPattern, string> patternNames = new Dictionary<IPattern, string>();
        readonly Dictionary<IMachine, string> machineNames = new Dictionary<IMachine, string>();

        /// <summary>Structure or contents changed (tracks, scenes, slots, settings).</summary>
        public event Action Changed;

        /// <summary>Undo history of grid edits; starts empty with each song.</summary>
        public ManagedActionStack UndoStack { get; private set; } = NewUndoStack();

        static ManagedActionStack NewUndoStack() => new ManagedActionStack(500);

        internal SessionLayout(ReBuzzCore buzz, SongCore song, IUiDispatcher dispatcher)
        {
            this.buzz = buzz;
            this.song = song;
            this.dispatcher = dispatcher;

            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;

            song.SequenceAdded += (i, s) => OnStructureChanged();
            song.SequenceRemoved += (i, s) => OnStructureChanged();
            song.SequenceChanged += (i, s) => OnStructureChanged();
            song.MachineAdded += m => { Watch(m); OnStructureChanged(); };
            song.MachineRemoved += m => { Unwatch(m); OnStructureChanged(); };
            foreach (var m in song.Machines)
                Watch(m);
        }

        ISession Session => buzz.Session;

        /// <summary>The layout, or null while the song doesn't use the session.</summary>
        public SessionDocument Document
        {
            get
            {
                Sync();
                return document;
            }
        }

        /// <summary>Returns the layout, auto-filling one from the song if it has none yet.</summary>
        public SessionDocument Ensure()
        {
            if (document == null)
            {
                document = SessionDocument.AutoFill(Snapshot());
                document.BeatsPerBar = Session.BeatsPerBar;
                document.LaunchQuantization = Session.LaunchQuantization.ToString();
                dirty = false;
                RaiseChanged();
            }
            else
            {
                Sync();
            }
            return document;
        }

        /// <summary>New song: back to no layout and default session settings.</summary>
        internal void Reset()
        {
            document = null;
            dirty = false;
            UndoStack = NewUndoStack();
            Session.BeatsPerBar = 4;
            Session.LaunchQuantization = LaunchQuantization.Bar;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ lookups

        public int TrackCount => Document?.Tracks.Count ?? 0;
        public int SceneCount => Document?.Scenes.Count ?? 0;

        public ISequence SequenceOf(int track)
        {
            var doc = Document;
            if (doc == null || track < 0 || track >= doc.Tracks.Count) return null;
            int index = doc.Tracks[track].Sequence;
            var sequences = song.Sequences;
            return index >= 0 && index < sequences.Count ? sequences[index] : null;
        }

        public IPattern PatternAt(int track, int scene)
        {
            string name = Document?.GetSlot(track, scene);
            if (name == null) return null;
            return SequenceOf(track)?.Machine?.Patterns.FirstOrDefault(p => p.Name == name);
        }

        /// <summary>Machines that have no sequence yet, i.e. could get a new track.</summary>
        public IEnumerable<IMachine> MachinesWithoutTrack() =>
            song.Machines.Where(m => m.DLL?.Info?.Type != MachineType.Master && !song.Sequences.Any(s => s.Machine == m));

        // ------------------------------------------------------------------ grid edits (undoable)

        public void SetSlot(int track, int scene, IPattern pattern) =>
            Edit(doc => { if (track >= 0 && track < doc.Tracks.Count) doc.SetSlot(track, scene, pattern?.Name); });

        public void ClearSlot(int track, int scene) => SetSlot(track, scene, null);

        public void InsertScene(int index) => Edit(doc => doc.InsertScene(index));

        public void RemoveScene(int index) => Edit(doc => doc.RemoveScene(index));

        public void DuplicateScene(int index) => Edit(doc => doc.DuplicateScene(index));

        public void RenameScene(int index, string name) => Edit(doc =>
        {
            if (index >= 0 && index < doc.Scenes.Count)
                doc.Scenes[index].Name = name?.Trim() ?? "";
        });

        public void SetSceneColor(int index, int colorIndex) => Edit(doc =>
        {
            if (index >= 0 && index < doc.Scenes.Count)
                doc.Scenes[index].ColorIndex = colorIndex;
        });

        public void SetTrackColor(int track, int colorIndex) => Edit(doc =>
        {
            if (track >= 0 && track < doc.Tracks.Count)
                doc.Tracks[track].ColorIndex = colorIndex;
        });

        /// <summary>Moves (or with <paramref name="copy"/> copies) a clip to another slot of its track.</summary>
        public bool MoveSlot(int track, int fromScene, int toScene, bool copy)
        {
            bool moved = false;
            Edit(doc => moved = doc.MoveSlot(track, fromScene, toScene, copy));
            return moved;
        }

        // ------------------------------------------------------------------ clip edits (undoable)

        /// <summary>Length of new clips: <see cref="SessionDocument.ClipBars"/> bars.</summary>
        public int NewClipLength => Math.Max(1, buzz.TPB * Session.BeatsPerBar * (Document?.ClipBars ?? 1));

        /// <summary>Creates a pattern on the track's machine and puts it in the slot.</summary>
        public IPattern CreateClip(int track, int scene)
        {
            var machine = SequenceOf(track)?.Machine;
            if (machine == null || scene < 0) return null;
            string name = machine.GetNewPatternName();
            if (name == null) return null;
            int length = NewClipLength;

            UndoStack.BeginActionGroup();
            try
            {
                UndoStack.Do(new CreatePatternAction(machine, name, length));
                Edit(doc => doc.SetSlot(track, scene, name));
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            return machine.Patterns.FirstOrDefault(p => p.Name == name);
        }

        /// <summary>
        /// Copies the slot's pattern into a new pattern (Live-style independent copy) in the slot
        /// below; inserts a scene there if that slot is taken. Returns the copy, or null.
        /// </summary>
        public IPattern DuplicateClip(int track, int scene, out int targetScene)
        {
            targetScene = scene + 1;
            var source = PatternAt(track, scene);
            if (source == null) return null;
            var machine = source.Machine;
            string name = CopyName(machine, source.Name);
            int target = targetScene;
            int color = PatternColors.Get(source);

            UndoStack.BeginActionGroup();
            try
            {
                if (Ensure().GetSlot(track, target) != null)
                    Edit(doc => doc.InsertScene(target));
                UndoStack.Do(new CreatePatternAction(machine, name, source.Length, source));
                var copy = machine.Patterns.FirstOrDefault(p => p.Name == name);
                if (copy != null && color >= 0)
                    UndoStack.Do(new PatternColorAction(copy, color));
                Edit(doc => doc.SetSlot(track, target, name));
                return copy;
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
        }

        /// <summary>Renames a pattern. False if the name is empty or taken.</summary>
        public bool RenameClip(IPattern pattern, string newName)
        {
            newName = newName?.Trim();
            if (pattern?.Machine == null || string.IsNullOrEmpty(newName) || newName == pattern.Name) return false;
            if (pattern.Machine.Patterns.Any(p => p.Name == newName)) return false;
            UndoStack.Do(new RenamePatternAction(pattern, newName));
            buzz.SetModifiedFlag();
            return true;
        }

        /// <summary>Deletes a pattern from its machine (and from every slot showing it).</summary>
        public void DeleteClip(IPattern pattern)
        {
            if (pattern?.Machine == null) return;
            string machine = pattern.Machine.Name;
            string name = pattern.Name;

            UndoStack.BeginActionGroup();
            try
            {
                // Clear the slots first, so undo puts them back after the pattern is recreated.
                Edit(doc =>
                {
                    foreach (var t in doc.Tracks.Where(t => t.Machine == machine))
                        foreach (var key in t.Slots.Where(s => s.Value == name).Select(s => s.Key).ToList())
                            t.Slots.Remove(key);
                });
                UndoStack.Do(new DeletePatternAction(pattern));
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            buzz.SetModifiedFlag();
        }

        public void SetClipColor(IPattern pattern, int colorIndex)
        {
            if (pattern?.Machine == null || PatternColors.Get(pattern) == colorIndex) return;
            UndoStack.Do(new PatternColorAction(pattern, colorIndex));
            buzz.SetModifiedFlag();
            RaiseChanged();
        }

        public void Undo()
        {
            if (!UndoStack.CanUndo) return;
            UndoStack.Undo();
            ContentChanged();
        }

        public void Redo()
        {
            if (!UndoStack.CanRedo) return;
            UndoStack.Redo();
            ContentChanged();
        }

        /// <summary>
        /// A free name for a copy of <paramref name="name"/>: the number at its end counted up
        /// ("00" gives "01", "Beat 2" gives "Beat 3"), or " 2" appended.
        /// </summary>
        internal static string CopyName(IMachine machine, string name)
        {
            var taken = new HashSet<string>(machine.Patterns.Select(p => p.Name));
            var m = Regex.Match(name ?? "", @"^(.*?)(\d+)$");
            string stem = m.Success ? m.Groups[1].Value : (name ?? "") + " ";
            int number = m.Success ? int.Parse(m.Groups[2].Value) + 1 : 2;
            int width = m.Success ? m.Groups[2].Value.Length : 1;
            string candidate;
            do
            {
                candidate = stem + number.ToString().PadLeft(width, '0');
                number++;
            }
            while (taken.Contains(candidate));
            return candidate;
        }

        /// <summary>
        /// Applies a layout edit and records it for undo (as snapshots) if it changed anything.
        /// </summary>
        bool Edit(Action<SessionDocument> change)
        {
            var doc = Ensure();
            var before = doc.ToBytes();
            change(doc);
            var after = doc.ToBytes();
            if (before.AsSpan().SequenceEqual(after)) return false;
            UndoStack.Do(new LayoutEditAction(this, before, after));
            ContentChanged();
            return true;
        }

        /// <summary>Puts back a layout snapshot (undo/redo). Session settings and the song's MIDI map stay as they are.</summary>
        internal void Restore(byte[] snapshot)
        {
            var restored = SessionDocument.Load(new MemoryStream(snapshot));
            if (restored == null) return;
            restored.SongMidi = document?.SongMidi;
            restored.ClipBars = document?.ClipBars ?? restored.ClipBars;
            if (document != null)
            {
                // View settings, not edits: they stay as they are.
                restored.ShowMixer = document.ShowMixer;
                restored.CueMachine = document.CueMachine;
                restored.CollapsedGroups.Clear();
                restored.CollapsedGroups.UnionWith(document.CollapsedGroups);
            }
            restored.BeatsPerBar = Session.BeatsPerBar;
            restored.LaunchQuantization = Session.LaunchQuantization.ToString();
            document = restored;
            dirty = true;      // the song may have changed since the snapshot
            ContentChanged();
        }

        // ------------------------------------------------------------------ other edits and actions

        /// <summary>Adds a sequence (track) for a machine; the layout follows via SequenceAdded.</summary>
        public void AddTrackFor(IMachine machine)
        {
            Ensure();
            song.AddSequence(machine, song.Sequences.Count);
        }

        public void SetBeatsPerBar(int beats)
        {
            Session.BeatsPerBar = beats;
            Ensure().BeatsPerBar = Session.BeatsPerBar;
            ContentChanged();
        }

        public void SetLaunchQuantization(LaunchQuantization quantization)
        {
            Session.LaunchQuantization = quantization;
            Ensure().LaunchQuantization = quantization.ToString();
            ContentChanged();
        }

        public void SetClipBars(int bars)
        {
            Ensure().ClipBars = Math.Clamp(bars, 1, 64);
            ContentChanged();
        }

        /// <summary>Stores a MIDI map with the song (overriding the user's), or removes it (null).</summary>
        internal void SetSongMidi(SessionMidiMap map)
        {
            Ensure().SongMidi = map;
            ContentChanged();
        }

        // ------------------------------------------------------------------ mixer (0.4)

        /// <summary>Makes a machine a return track (undoable). Not the cue machine.</summary>
        public void AddReturn(IMachine machine)
        {
            if (machine == null) return;
            Edit(doc =>
            {
                if (!doc.Returns.Contains(machine.Name) && machine.Name != doc.CueMachine)
                    doc.Returns.Add(machine.Name);
            });
        }

        /// <summary>Removes a return track (undoable). Its sends stay connected in the Machine View.</summary>
        public void RemoveReturn(string machine) => Edit(doc => doc.Returns.Remove(machine));

        /// <summary>The machine Cue connects tracks to, or null for none.</summary>
        public void SetCueMachine(IMachine machine)
        {
            var doc = Ensure();
            doc.CueMachine = machine?.Name ?? "";
            doc.Returns.Remove(doc.CueMachine);
            ContentChanged();
        }

        public void SetShowMixer(bool show)
        {
            var doc = Ensure();
            if (doc.ShowMixer == show) return;
            doc.ShowMixer = show;
            ContentChanged();
        }

        public void SetGroupCollapsed(string group, bool collapsed)
        {
            var doc = Ensure();
            bool changed = collapsed ? doc.CollapsedGroups.Add(group) : doc.CollapsedGroups.Remove(group);
            if (changed)
                ContentChanged();
        }

        /// <summary>Resolves a machine by name in the song (returns, cue).</summary>
        public IMachine MachineNamed(string name) =>
            string.IsNullOrEmpty(name) ? null : song.Machines.FirstOrDefault(m => m.Name == name);

        /// <summary>Return machines that exist in the song, in order.</summary>
        public IReadOnlyList<IMachine> ReturnMachines()
        {
            var doc = Document;
            if (doc == null) return Array.Empty<IMachine>();
            return doc.Returns.Select(MachineNamed).Where(m => m != null && m.Name != doc.CueMachine).ToList();
        }

        /// <summary>Launches the clips of a group's member tracks in a scene (Live's group slot).</summary>
        public void LaunchGroupScene(IEnumerable<int> tracks, int scene)
        {
            var slots = new List<(ISequence, IPattern)>();
            foreach (int track in tracks)
            {
                var seq = SequenceOf(track);
                var pattern = PatternAt(track, scene);
                if (seq != null && pattern != null)
                    slots.Add((seq, pattern));
            }
            if (slots.Count > 0)
                Session.LaunchScene(slots);
        }

        /// <summary>Launches every filled slot of a scene row together.</summary>
        public void LaunchScene(int scene)
        {
            var doc = Document;
            if (doc == null) return;
            var slots = new List<(ISequence, IPattern)>();
            foreach (var (track, _) in doc.SceneSlots(scene))
            {
                var seq = SequenceOf(track);
                var pattern = PatternAt(track, scene);
                if (seq != null && pattern != null)
                    slots.Add((seq, pattern));
            }
            if (slots.Count > 0)
                Session.LaunchScene(slots);
        }

        void ContentChanged()
        {
            buzz.SetModifiedFlag();
            RaiseChanged();
        }

        /// <summary>Raises <see cref="Changed"/> once, after the current batch of edits.</summary>
        void RaiseChanged()
        {
            if (changePosted) return;
            changePosted = true;
            dispatcher.BeginInvoke(() =>
            {
                changePosted = false;
                Changed?.Invoke();       // readers reconcile on access
            }, DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------ keeping in step with the song

        IReadOnlyList<SessionDocument.SongTrack> Snapshot() =>
            song.Sequences
                .Select(s => new SessionDocument.SongTrack(
                    s.Machine?.Name ?? "",
                    s.Machine?.Patterns.Select(p => p.Name).ToList() ?? new List<string>()))
                .ToList();

        void Sync()
        {
            if (!dirty || document == null) return;
            dirty = false;
            document.Reconcile(Snapshot());
        }

        void OnStructureChanged()
        {
            if (document == null) return;
            dirty = true;
            RaiseChanged();
        }

        void Watch(IMachine machine)
        {
            if (machine == null || machineNames.ContainsKey(machine)) return;
            machineNames[machine] = machine.Name;
            machine.PropertyChanged += Machine_PropertyChanged;
            machine.PatternAdded += Machine_PatternAdded;
            machine.PatternRemoved += Machine_PatternRemoved;
            foreach (var p in machine.Patterns)
                WatchPattern(p);
        }

        void Unwatch(IMachine machine)
        {
            if (machine == null || !machineNames.Remove(machine)) return;
            machine.PropertyChanged -= Machine_PropertyChanged;
            machine.PatternAdded -= Machine_PatternAdded;
            machine.PatternRemoved -= Machine_PatternRemoved;
            foreach (var p in patternNames.Keys.Where(p => p.Machine == machine).ToList())
                UnwatchPattern(p);
        }

        void WatchPattern(IPattern pattern)
        {
            if (pattern == null || patternNames.ContainsKey(pattern)) return;
            patternNames[pattern] = pattern.Name;
            pattern.PropertyChanged += Pattern_PropertyChanged;
        }

        void UnwatchPattern(IPattern pattern)
        {
            if (pattern == null || !patternNames.Remove(pattern)) return;
            pattern.PropertyChanged -= Pattern_PropertyChanged;
        }

        void Machine_PatternAdded(IPattern pattern)
        {
            WatchPattern(pattern);
            OnStructureChanged();
        }

        void Machine_PatternRemoved(IPattern pattern)
        {
            UnwatchPattern(pattern);
            OnStructureChanged();
        }

        void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Name" || sender is not IMachine machine) return;
            if (machineNames.TryGetValue(machine, out var old) && old != machine.Name)
            {
                machineNames[machine] = machine.Name;
                if (document != null)
                {
                    // Snapshots already carry the new name, so rename before any reconcile.
                    document.RenameMachine(old, machine.Name);
                    RaiseChanged();
                }
            }
        }

        void Pattern_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Name" || sender is not IPattern pattern) return;
            if (patternNames.TryGetValue(pattern, out var old) && old != pattern.Name)
            {
                patternNames[pattern] = pattern.Name;
                if (document != null && pattern.Machine != null)
                {
                    document.RenamePattern(pattern.Machine.Name, old, pattern.Name);
                    RaiseChanged();
                }
            }
        }

        // ------------------------------------------------------------------ persistence

        void OnSaveSong(ISaveSong save)
        {
            if (document == null) return;
            dirty = true;
            Sync();
            document.BeatsPerBar = Session.BeatsPerBar;
            document.LaunchQuantization = Session.LaunchQuantization.ToString();
            var stream = save.CreateSubSection(SessionDocument.SubSectionName);
            if (stream != null)
                document.Save(stream);
        }

        void OnOpenSong(IOpenSong open)
        {
            document = SessionDocument.Load(open.GetSubSection(SessionDocument.SubSectionName));
            dirty = false;
            UndoStack = NewUndoStack();
            if (document != null)
            {
                document.Reconcile(Snapshot());
                Session.BeatsPerBar = document.BeatsPerBar;
                if (Enum.TryParse<LaunchQuantization>(document.LaunchQuantization, out var q))
                    Session.LaunchQuantization = q;
            }
            RaiseChanged();
        }
    }
}
