using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

        // ------------------------------------------------------------------ editing

        public void SetSlot(int track, int scene, IPattern pattern)
        {
            Ensure().SetSlot(track, scene, pattern?.Name);
            ContentChanged();
        }

        public void ClearSlot(int track, int scene) => SetSlot(track, scene, null);

        public void InsertScene(int index)
        {
            Ensure().InsertScene(index);
            ContentChanged();
        }

        public void RemoveScene(int index)
        {
            Ensure().RemoveScene(index);
            ContentChanged();
        }

        public void RenameScene(int index, string name)
        {
            var doc = Ensure();
            if (index < 0 || index >= doc.Scenes.Count) return;
            doc.Scenes[index].Name = name?.Trim() ?? "";
            ContentChanged();
        }

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
