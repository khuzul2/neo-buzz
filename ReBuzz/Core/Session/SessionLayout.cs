using BuzzGUI.Common;
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
    /// Structural song changes are followed exactly, in the layout and in every undo snapshot
    /// (0.6.1, SessionLayout.Identity.cs): sequences by index, renames, deleted patterns and
    /// machines retired and brought back. A reconcile against the song stays as a safety net
    /// on the next read. <see cref="Changed"/> is raised once per batch, from the dispatcher.
    ///
    /// Grid edits (0.2) go through <see cref="UndoStack"/>, the session's own undo history:
    /// layout changes as before/after snapshots, clip changes with BuzzGUI's pattern actions.
    /// </summary>
    internal sealed partial class SessionLayout
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

            song.SequenceAdded += OnSequenceAdded;
            song.SequenceRemoved += OnSequenceRemoved;
            song.SequenceChanged += OnSequenceChanged;
            song.MachineAdded += m => { Watch(m); OnStructureChanged(); };
            song.MachineRemoved += m =>
            {
                string name = m != null && machineNames.TryGetValue(m, out var known) ? known : m?.Name;
                Unwatch(m);
                OnMachineRemovedFromSong(m, name);
                OnStructureChanged();
            };
            foreach (var m in song.Machines)
                Watch(m);
            ResetIdentity();

            // Follow times are in bars, beats and ticks: the engine's copy follows the time signature (0.6).
            buzz.Session.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ISession.BeatsPerBar)) RebuildLaunchMap(); };
            buzz.PropertyChanged += (s, e) => { if (e.PropertyName == "TPB") RebuildLaunchMap(); };
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
                document.AutoFilled = true;      // 0.6.1: not saved until something is edited (plain songs stay plain)
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
            // 0.6.1: at once, not when the UI is next idle: nothing may launch the old song's clips.
            launchMap = LaunchMap.Empty;
            (Session as SessionCore)?.UseLaunchMap(LaunchMap.Empty);
            crossfaderFromDocument = true;
            document = null;
            ResetIdentity();
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
                var create = new SessionCreatePatternAction(this, machine, name, length);
                UndoStack.Do(create);
                if (create.Created == null) return null;
                Edit(doc => doc.SetSlot(track, scene, create.Created.Name));
                return create.Created;
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
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
                var create = new SessionCreatePatternAction(this, machine, name, source.Length, source);
                UndoStack.Do(create);
                var copy = create.Created;
                if (copy == null) return null;
                if (color >= 0)
                    UndoStack.Do(new PatternColorAction(this, copy, color));
                Edit(doc =>
                {
                    doc.SetSlot(track, target, copy.Name);
                    doc.SetClip(machine.Name, copy.Name, doc.GetClip(machine.Name, source.Name));   // the copy launches the same way
                });
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
            UndoStack.Do(new RenamePatternAction(this, pattern, newName));
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
                    doc.SetClip(machine, name, null);
                });
                UndoStack.Do(new SessionDeletePatternAction(this, pattern));
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
            UndoStack.Do(new PatternColorAction(this, pattern, colorIndex));
            buzz.SetModifiedFlag();
            RaiseChanged();
        }

        public void Undo()
        {
            if (!UndoStack.CanUndo) return;
            UndoStack.Undo();
            ContentChanged();
            ReshapeLaunched();
        }

        public void Redo()
        {
            if (!UndoStack.CanRedo) return;
            UndoStack.Redo();
            ContentChanged();
            ReshapeLaunched();
        }

        /// <summary>After undo or redo (0.5): clips that play or wait take their markers as they now are.</summary>
        void ReshapeLaunched()
        {
            if (Session is not SessionCore core || Document == null) return;
            foreach (var seq in song.Sequences)
            {
                var playing = core.GetPlaying(seq);
                var queued = core.GetQueued(seq);
                if (playing != null) core.Reshape(seq, playing, OptionsFor(playing));
                if (queued != null && queued != playing) core.Reshape(seq, queued, OptionsFor(queued));
            }
        }

        /// <summary>
        /// A free name for a copy of <paramref name="name"/>: the number at its end counted up
        /// ("00" gives "01", "Beat 2" gives "Beat 3"), or " 2" appended.
        /// </summary>
        internal static string CopyName(IMachine machine, string name)
        {
            var taken = new HashSet<string>(machine.Patterns.Select(p => p.Name));
            // ASCII digits only, at most 9 (0.6.1: longer runs overflowed int.Parse).
            var m = Regex.Match(name ?? "", @"^(.*?)([0-9]{1,9})$");
            string stem = m.Success ? m.Groups[1].Value : (name ?? "") + " ";
            int number = m.Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1 : 2;
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
            var before = doc.CloneLayout();
            var bytes = doc.ToBytes();
            change(doc);
            if (bytes.AsSpan().SequenceEqual(doc.ToBytes())) return false;
            UndoStack.Do(new LayoutEditAction(this, before, doc.CloneLayout()));
            ContentChanged();
            return true;
        }

        /// <summary>
        /// Puts back a layout snapshot (undo/redo). The session's settings (view, performance,
        /// time signature, the song's MIDI map) are not part of it (0.6.1): the live ones stay.
        /// </summary>
        internal void Restore(SessionDocument snapshot)
        {
            if (snapshot == null) return;
            FlushTransforms();                 // snapshot changes held back under AudioLock come first
            var restored = snapshot.CloneLayout();
            if (document != null)
                restored.Settings = document.Settings;
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
        /// <remarks>
        /// A setting (0.4), not an undo step. 0.6.1: it no longer removes the machine from the
        /// returns (an undoable list): the returns shown leave the cue machine out instead.
        /// </remarks>
        public void SetCueMachine(IMachine machine)
        {
            Ensure().CueMachine = machine?.Name ?? "";
            ContentChanged();
        }

        public void SetShowMixer(bool show)
        {
            var doc = Ensure();
            if (doc.ShowMixer == show) return;
            doc.ShowMixer = show;
            ContentChanged();
        }

        public void SetShowClipPanel(bool show)
        {
            var doc = Ensure();
            if (doc.ShowClipPanel == show) return;
            doc.ShowClipPanel = show;
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

        // One launch path (0.6.1): mouse, keys, MIDI and follow actions all launch what the
        // launch map says, which is rebuilt as soon as anything changes.

        /// <summary>Launches the clips of a group's member tracks in a scene (Live's group slot).</summary>
        public void LaunchGroupScene(IEnumerable<int> tracks, int scene, int velocity = 127)
        {
            var map = LaunchMap;
            var slots = new List<(ISequence, IPattern, LaunchOptions)>();
            foreach (int track in tracks)
                if (map.Track(track) is ISequence seq && map.Slot(track, scene) is LaunchMap.SlotEntry entry)
                    slots.Add((seq, entry.Pattern, entry.OptionsAt(velocity)));
            if (slots.Count > 0)
                Session.LaunchScene(slots, null);
        }

        /// <summary>
        /// Launches every filled slot of a scene row together, with the scene's tempo, time
        /// signature and follow actions (a scene with only those and no clips still sets them),
        /// stopping the other tracks when the song says so (0.6).
        /// </summary>
        public void LaunchScene(int scene, int velocity = 127) => LaunchScene(Session, LaunchMap.Scene(scene), velocity);

        /// <summary>Any thread: launches a scene of a launch map (the UI and MIDI share this).</summary>
        internal static void LaunchScene(ISession session, LaunchMap.SceneEntry entry, int velocity)
        {
            if (entry == null) return;
            var launches = new (ISequence, IPattern, LaunchOptions)[entry.Clips.Length];
            for (int i = 0; i < entry.Clips.Length; i++)
                launches[i] = (entry.Clips[i].Track, entry.Clips[i].Slot.Pattern, entry.Clips[i].Slot.OptionsAt(velocity));
            session.LaunchScene(launches, entry.Options);
        }

        // ------------------------------------------------------------------ clip and scene properties (0.5, 0.6)

        /// <summary>A press of a clip's launch control (mouse, key, MIDI note with its velocity), from a slot (scene row) if known.</summary>
        public void LaunchClip(ISequence track, IPattern pattern, int velocity = 127, int slot = -1)
        {
            if (track == null || pattern == null) return;
            var map = LaunchMap;
            int t = Array.IndexOf(map.Tracks, track);
            var entry = map.Slot(t, slot);
            var options = entry != null && ReferenceEquals(entry.Pattern, pattern) ? entry.OptionsAt(velocity) : OptionsFor(pattern, velocity, slot);
            Session.Launch(track, pattern, options);
        }

        /// <summary>The release of a clip's launch control: matters for Gate and Repeat clips.</summary>
        public void ReleaseClip(ISequence track, IPattern pattern)
        {
            if (track == null || pattern == null) return;
            Session.Release(track, pattern, OptionsFor(pattern));
        }

        /// <summary>A pattern's clip settings (a copy; defaults without a layout).</summary>
        public SessionDocument.ClipSettings ClipOf(IPattern pattern) =>
            pattern?.Machine == null || Document == null ? new SessionDocument.ClipSettings() : Document.GetClip(pattern.Machine.Name, pattern.Name);

        /// <summary>
        /// Changes a pattern's clip settings (undoable). Markers apply at once to the clip if it
        /// plays or waits; the other settings from its next launch.
        /// </summary>
        public void SetClipSettings(IPattern pattern, SessionDocument.ClipSettings settings)
        {
            if (pattern?.Machine == null || settings == null) return;
            string machine = pattern.Machine.Name, name = pattern.Name;
            if (!Edit(doc => doc.SetClip(machine, name, settings))) return;
            if (Session is SessionCore core)
            {
                var options = ToOptions(settings, 127, -1, Session.BeatsPerBar, buzz.TPB);
                foreach (var seq in song.Sequences.Where(s => s.Machine == pattern.Machine))
                    core.Reshape(seq, pattern, options);
            }
        }

        /// <summary>Launch options of a pattern's clip settings, at a launch velocity (1..127), from a slot if known.</summary>
        public LaunchOptions OptionsFor(IPattern pattern, int velocity = 127, int slot = -1) =>
            ToOptions(ClipOf(pattern), velocity, slot, Session.BeatsPerBar, buzz.TPB);

        /// <summary>Launch options for clip settings at a launch velocity (any thread).</summary>
        internal static LaunchOptions ToOptions(SessionDocument.ClipSettings c, int velocity, int slot = -1, int beatsPerBar = 4, int ticksPerBeat = 4)
        {
            c ??= new SessionDocument.ClipSettings();
            return new LaunchOptions
            {
                Mode = Enum.TryParse<ClipLaunchMode>(c.Mode, out var mode) ? mode : ClipLaunchMode.Trigger,
                Quantization = Enum.TryParse<LaunchQuantization>(c.Quantization, out var q) ? q : null,
                Legato = c.Legato,
                Loop = c.Loop,
                Start = c.Start,
                LoopStart = c.LoopStart,
                LoopEnd = c.LoopEnd,
                Gain = VelocityGain(c.Velocity, velocity),
                Follow = ToFollow(c.Follow, beatsPerBar, ticksPerBeat, clip: true),
                Slot = slot
            };
        }

        /// <summary>Follow options of follow settings (0.6), or null when they're off. Scenes are never linked.</summary>
        internal static FollowOptions ToFollow(SessionDocument.FollowSettings f, int beatsPerBar, int ticksPerBeat, bool clip)
        {
            if (f == null || !f.On) return null;
            return new FollowOptions
            {
                A = Enum.TryParse<ClipFollowAction>(f.A, out var a) ? a : ClipFollowAction.None,
                B = Enum.TryParse<ClipFollowAction>(f.B, out var b) ? b : ClipFollowAction.None,
                ChanceA = Math.Clamp(f.ChanceA, 0, 100),
                Linked = clip && f.Linked,
                Multiplier = Math.Max(1, f.Multiplier),
                TimeTicks = SessionDocument.FollowSettings.TicksOf(f.Time, beatsPerBar, ticksPerBeat) ?? Math.Max(1, beatsPerBar * ticksPerBeat),
                JumpA = f.JumpA,
                JumpB = f.JumpB
            };
        }

        /// <summary>
        /// Volume for a launch velocity (Live's clip velocity amount): 0% sensitivity ignores the
        /// velocity; at 100% it scales the volume all the way, so the softest launch is silent.
        /// </summary>
        internal static double VelocityGain(int sensitivityPercent, int velocity)
        {
            double amount = Math.Clamp(sensitivityPercent, 0, 100) / 100.0;
            double v = Math.Clamp(velocity, 0, 127) / 127.0;
            return 1.0 - amount * (1.0 - v);
        }

        /// <summary>
        /// A scene row's settings: tempo, beats per bar, follow actions (0.6), and with
        /// <see cref="SessionDocument.ScenesStopOtherClips"/> the tracks it has no clip for.
        /// </summary>
        internal SceneOptions SceneOptionsOf(int scene)
        {
            var doc = Document;
            if (doc == null || scene < 0 || scene >= doc.Scenes.Count) return new SceneOptions();
            var info = doc.Scenes[scene];
            List<ISequence> stops = null;
            if (doc.ScenesStopOtherClips)
            {
                stops = new List<ISequence>();
                for (int t = 0; t < doc.Tracks.Count; t++)
                    if (SequenceOf(t) is ISequence seq && PatternAt(t, scene) == null && !stops.Contains(seq))
                        stops.Add(seq);
            }
            return new SceneOptions
            {
                Tempo = info.Tempo,
                BeatsPerBar = info.BeatsPerBar,
                Scene = scene,
                // In the scene's own bars when it sets beats per bar (0.6.1): they apply from its launch.
                Follow = ToFollow(info.Follow, info.BeatsPerBar > 0 ? info.BeatsPerBar : Session.BeatsPerBar, buzz.TPB, clip: false),
                StopTracks = stops
            };
        }

        /// <summary>Scene follow actions (0.6, undoable).</summary>
        public void SetSceneFollow(int index, SessionDocument.FollowSettings follow) => Edit(doc =>
        {
            if (index >= 0 && index < doc.Scenes.Count && follow != null)
                doc.Scenes[index].Follow = follow.Clone();
        });

        // ------------------------------------------------------------------ performance (0.6)

        /// <summary>Clip and scene follow actions on or off, for the song (Live's global switch).</summary>
        public void SetFollowActionsEnabled(bool on)
        {
            var doc = Ensure();
            if (doc.FollowActionsEnabled == on) return;
            doc.FollowActionsEnabled = on;
            if (Session is SessionCore core) core.FollowActionsEnabled = on;
            ContentChanged();
        }

        /// <summary>Scene launches stop the clips of tracks they have no clip for (Live's stop buttons).</summary>
        public void SetScenesStopOtherClips(bool on)
        {
            var doc = Ensure();
            if (doc.ScenesStopOtherClips == on) return;
            doc.ScenesStopOtherClips = on;
            ContentChanged();
        }

        /// <summary>A machine's crossfader side: "A", "B", or null for none.</summary>
        public string CrossfadeOf(IMachine machine) =>
            machine != null && Document is SessionDocument doc && doc.Crossfade.TryGetValue(machine.Name, out var side) ? side : null;

        public void SetCrossfade(IMachine machine, string side)
        {
            if (machine == null) return;
            var doc = Ensure();
            if (side == "A" || side == "B") doc.Crossfade[machine.Name] = side;
            else doc.Crossfade.Remove(machine.Name);
            (Session as SessionCore)?.UseCrossfader(doc, song);
            ContentChanged();
        }

        public void SetCrossfaderPosition(double position)
        {
            var doc = Ensure();
            doc.CrossfaderPosition = Math.Clamp(position, 0, 1);
            (Session as SessionCore)?.SetCrossfader(doc.CrossfaderPosition);
            buzz.SetModifiedFlag();
        }

        /// <summary>The crossfader moved from MIDI (already applied): the song keeps the position.</summary>
        internal void NoteCrossfaderPosition(double position)
        {
            if (document == null) return;
            document.CrossfaderPosition = Math.Clamp(position, 0, 1);
            buzz.SetModifiedFlag();
        }

        public void SetCrossfaderCurve(string curve)
        {
            var doc = Ensure();
            if (Array.IndexOf(SessionCore.CrossfaderCurves, curve) < 0 || doc.CrossfaderCurve == curve) return;
            doc.CrossfaderCurve = curve;
            (Session as SessionCore)?.UseCrossfader(doc, song);
            ContentChanged();
        }

        readonly TapTempoMeter tapTempo = new TapTempoMeter();

        /// <summary>A tap of tap tempo (at a Stopwatch timestamp, or now). Returns the tempo set, if any.</summary>
        public int? TapTempo(long? timestamp = null)
        {
            long ts = timestamp ?? System.Diagnostics.Stopwatch.GetTimestamp();
            int? bpm = tapTempo.Tap(ts / (double)System.Diagnostics.Stopwatch.Frequency);
            if (bpm is int value && value != buzz.BPM)
                buzz.BPM = value;
            return bpm;
        }

        // ------------------------------------------------------------------ launch map (0.6)

        volatile LaunchMap launchMap = LaunchMap.Empty;

        /// <summary>What each slot and scene launches, for the MIDI and audio threads.</summary>
        public LaunchMap LaunchMap => launchMap;

        /// <summary>UI thread: resolves every slot and scene, and hands the result to the engine.</summary>
        internal void RebuildLaunchMap()
        {
            var doc = Document;
            var map = LaunchMap.Empty;
            if (doc != null)
            {
                int beats = Session.BeatsPerBar, tpb = buzz.TPB;
                int trackCount = doc.Tracks.Count, sceneCount = doc.Scenes.Count;
                var tracks = new ISequence[trackCount];
                var slots = new LaunchMap.SlotEntry[trackCount][];
                for (int t = 0; t < trackCount; t++)
                {
                    tracks[t] = SequenceOf(t);
                    slots[t] = new LaunchMap.SlotEntry[sceneCount];
                    if (tracks[t] == null) continue;
                    for (int s = 0; s < sceneCount; s++)
                    {
                        var pattern = PatternAt(t, s);
                        if (pattern == null) continue;
                        var settings = ClipOf(pattern);
                        slots[t][s] = new LaunchMap.SlotEntry(pattern, settings, ToOptions(settings, 127, s, beats, tpb));
                    }
                }
                var scenes = new LaunchMap.SceneEntry[sceneCount];
                for (int s = 0; s < sceneCount; s++)
                {
                    var clips = new List<(ISequence, LaunchMap.SlotEntry)>();
                    for (int t = 0; t < trackCount; t++)
                        if (tracks[t] != null && slots[t][s] != null)
                            clips.Add((tracks[t], slots[t][s]));
                    var info = doc.Scenes[s];
                    scenes[s] = new LaunchMap.SceneEntry(clips.ToArray(), SceneOptionsOf(s), clips.Count == 0 && info.Tempo <= 0 && info.BeatsPerBar <= 0 && info.Follow?.On != true);
                }
                map = new LaunchMap(tracks, slots, scenes);
            }
            launchMap = map;
            if (Session is SessionCore core)
            {
                core.UseLaunchMap(map);
                core.FollowActionsEnabled = doc?.FollowActionsEnabled ?? true;
                // The position comes from the song once, when it is opened; after that the
                // engine's is the truth (MIDI moves it directly, 0.6.1).
                core.UseCrossfader(doc, song, crossfaderFromDocument);
                if (doc != null) crossfaderFromDocument = false;
            }
        }

        bool crossfaderFromDocument = true;
        bool rebuildPosted;

        void PostLaunchMapRebuild()
        {
            if (rebuildPosted) return;
            rebuildPosted = true;
            dispatcher.BeginInvoke(() =>
            {
                rebuildPosted = false;
                RebuildLaunchMap();
            });
        }

        /// <summary>Tempo a scene sets when it launches; 0 for none (undoable).</summary>
        public void SetSceneTempo(int index, int bpm) => Edit(doc =>
        {
            if (index >= 0 && index < doc.Scenes.Count)
                doc.Scenes[index].Tempo = bpm <= 0 ? 0 : Math.Clamp(bpm, 16, 500);
        });

        /// <summary>Beats per bar a scene sets when it launches; 0 for none (undoable).</summary>
        public void SetSceneBeatsPerBar(int index, int beats) => Edit(doc =>
        {
            if (index >= 0 && index < doc.Scenes.Count)
                doc.Scenes[index].BeatsPerBar = beats <= 0 ? 0 : Math.Clamp(beats, 1, 64);
        });

        void ContentChanged()
        {
            if (document != null) document.AutoFilled = false;
            buzz.SetModifiedFlag();
            RaiseChanged();
        }

        /// <summary>Raises <see cref="Changed"/> once, after the current batch of edits.</summary>
        void RaiseChanged() => RaiseChanged(structural: false);

        void RaiseChanged(bool structural)
        {
            // The MIDI and audio threads (and every launch) use the launch map: rebuilt at once
            // after an edit (0.6.1), so mouse and MIDI launches agree. Song structure changes
            // come in bursts (deleting a machine, an import), some under AudioLock: one rebuild
            // after the burst, outside the lock. Meanwhile stale entries can't launch anything
            // gone (liveness flags), and a clip launch from the UI falls back to its settings.
            if (structural || ReBuzzCore.AudioLock.IsHeldByCurrentThread)
                PostLaunchMapRebuild();
            else
                RebuildLaunchMap();
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
            RaiseChanged(structural: true);
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
            OnPatternRemovedFromSong(pattern);
            UnwatchPattern(pattern);
            OnStructureChanged();
        }

        void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Name" || sender is not IMachine machine) return;
            if (machineNames.TryGetValue(machine, out var old) && old != machine.Name)
            {
                machineNames[machine] = machine.Name;
                // In the layout and every undo snapshot (0.6.1), before any reconcile.
                TransformAll(d => d.RenameMachine(old, machine.Name));
                foreach (var g in graves.Where(g => g.MachineName == old))
                    g.MachineName = machine.Name;
                if (document != null)
                    RaiseChanged();
            }
        }

        void Pattern_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Name" || sender is not IPattern pattern) return;
            if (patternNames.TryGetValue(pattern, out var old) && old != pattern.Name)
            {
                patternNames[pattern] = pattern.Name;
                if (pattern.Machine != null)
                {
                    TransformAll(d => d.RenamePattern(pattern.Machine.Name, old, pattern.Name));
                    if (document != null)
                        RaiseChanged();
                }
            }
        }

        // ------------------------------------------------------------------ persistence

        void OnSaveSong(ISaveSong save)
        {
            if (document == null || document.AutoFilled) return;
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
            try
            {
                document = SessionDocument.Load(open.GetSubSection(SessionDocument.SubSectionName));
            }
            catch (Exception e)
            {
                // 0.6.1: a damaged layout mustn't stop the song from opening.
                buzz.DCWriteLine("Session layout: " + e.Message, DCLogLevel.Warning);
                document = null;
            }
            crossfaderFromDocument = true;
            ResetIdentity();
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
