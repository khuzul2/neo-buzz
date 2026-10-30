using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Identity (docs/specs/session-view.md 0.6.1). The layout keeps names, as song files do,
    /// and follows every change of the song exactly, in the current layout and in every undo
    /// snapshot alike:
    ///  - sequences added, removed or swapped move tracks by their exact index (a song may have
    ///    several sequences of one machine);
    ///  - renames rename;
    ///  - a deleted pattern's or machine's name is retired (<see cref="SessionDocument.Retire"/>),
    ///    so a new pattern or machine reusing the name inherits nothing, and the slots and
    ///    settings go to a grave. When an undo, in any view, brings the object back (matched by
    ///    machine, name and contents), the grave gives them back; the session's own undo does so
    ///    directly (<see cref="ResurrectAs"/>).
    /// The session's pattern actions hold objects, not names (<see cref="Current{T}"/> follows
    /// an object that was deleted and brought back), so they never touch another editor's
    /// pattern that happens to have the same name.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        enum GraveKind { Pattern, Machine, Sequence }

        /// <summary>What the layout had for an object that is gone, until it comes back.</summary>
        sealed class Grave
        {
            public GraveKind Kind;
            public object Original;              // the IPattern, IMachine or ISequence that went
            public IMachine Machine;             // its machine (for a machine: itself)
            public string MachineName = "";      // as the layout knows it now (retired while the machine is gone)
            public string Name = "";             // the name it had
            public string Retired = "";          // the name it has in snapshots meanwhile
            public long Fingerprint;
            public string Dll = "";
            public (int Track, int Scene)[] Slots = Array.Empty<(int, int)>();
            public SessionDocument.ClipSettings Clip;
            public SortedDictionary<int, string> TrackSlots;   // a sequence's track
            public int TrackColor = -1;
            public int TrackIndex = -1;
        }

        const int MaxGraves = 256;
        readonly List<Grave> graves = new List<Grave>();
        readonly Dictionary<object, object> forward = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        readonly List<ISequence> bound = new List<ISequence>();      // the song's sequences as the layout last saw them
        int retiredCounter;

        /// <summary>The object now standing for <paramref name="obj"/>: itself, or what an undo brought back in its place.</summary>
        internal T Current<T>(T obj) where T : class
        {
            object o = obj;
            for (int guard = 0; o != null && forward.TryGetValue(o, out var next) && guard < 64; guard++)
                o = next;
            return o as T ?? obj;
        }

        /// <summary>Applies a change to the layout and to every undo snapshot.</summary>
        void TransformAll(Action<SessionDocument> change)
        {
            if (document != null)
                change(document);
            TransformSnapshots(change);
        }

        void TransformSnapshots(Action<SessionDocument> change)
        {
            foreach (var action in UndoStack.Actions)
                if (action is LayoutEditAction edit)
                {
                    change(edit.Before);
                    change(edit.After);
                }
        }

        /// <summary>New song or song loaded: nothing to bring back, sequences as they are.</summary>
        void ResetIdentity()
        {
            graves.Clear();
            forward.Clear();
            bound.Clear();
            bound.AddRange(song.Sequences);
        }

        void Bury(Grave grave)
        {
            graves.Add(grave);
            if (graves.Count > MaxGraves)
                graves.RemoveAt(0);
        }

        // ------------------------------------------------------------------ sequences

        void OnSequenceAdded(int index, ISequence seq)
        {
            index = Math.Clamp(index, 0, bound.Count);
            bound.Insert(index, seq);
            string machine = seq?.Machine?.Name ?? "";
            TransformAll(d => d.InsertTrack(index, machine));
            if (document != null && graves.Any(g => g.Kind == GraveKind.Sequence))
                dispatcher.BeginInvoke(() => TryReturnSequence(seq));   // after the undo that added it has set its events
            OnStructureChanged();
        }

        void OnSequenceRemoved(int index, ISequence seq)
        {
            int at = bound.IndexOf(seq);
            if (at < 0) at = index;
            if (at < 0 || at >= bound.Count)
            {
                OnStructureChanged();
                return;
            }
            bound.RemoveAt(at);
            if (document != null && at < document.Tracks.Count)
            {
                var track = document.Tracks[at];
                if (track.Slots.Count > 0 || track.ColorIndex >= 0)
                    Bury(new Grave
                    {
                        Kind = GraveKind.Sequence,
                        Original = seq,
                        Machine = seq.Machine,
                        MachineName = track.Machine,
                        Fingerprint = Fingerprint(seq),
                        TrackSlots = new SortedDictionary<int, string>(track.Slots),
                        TrackColor = track.ColorIndex,
                        TrackIndex = at
                    });
            }
            TransformAll(d => d.RemoveTrack(at));
            OnStructureChanged();
        }

        void OnSequenceChanged(int index, ISequence seq)
        {
            // Swaps raise this twice, after the swap: the first event moves both tracks.
            int at = bound.IndexOf(seq);
            if (at >= 0 && index >= 0 && index < bound.Count && at != index)
            {
                (bound[at], bound[index]) = (bound[index], bound[at]);
                TransformAll(d => d.SwapTracks(at, index));
            }
            OnStructureChanged();
        }

        void TryReturnSequence(ISequence seq)
        {
            if (document == null || seq?.Machine == null) return;
            int at = bound.IndexOf(seq);
            if (at < 0 || at >= document.Tracks.Count) return;
            long print = Fingerprint(seq);
            var grave = graves.LastOrDefault(g => g.Kind == GraveKind.Sequence && g.MachineName == seq.Machine.Name && g.Fingerprint == print);
            if (grave == null) return;
            graves.Remove(grave);
            forward[grave.Original] = seq;
            var track = document.Tracks[at];
            foreach (var slot in grave.TrackSlots)
                if (!track.Slots.ContainsKey(slot.Key))
                    track.Slots[slot.Key] = slot.Value;
            if (track.ColorIndex < 0) track.ColorIndex = grave.TrackColor;
            document.EnsureScenes();
            dirty = true;
            ContentChanged();
        }

        // ------------------------------------------------------------------ patterns

        void OnPatternRemovedFromSong(IPattern pattern)
        {
            var machine = pattern?.Machine;
            if (document == null || machine == null || SessionDocument.IsRetired(pattern.Name)) return;
            string machineName = machineNames.TryGetValue(machine, out var known) ? known : machine.Name;
            string name = pattern.Name;
            string retired = SessionDocument.Retire(name, ++retiredCounter);

            // The layout's slots and settings for it go to the grave; snapshots keep them under the retired name.
            var slots = new List<(int, int)>();
            for (int t = 0; t < document.Tracks.Count; t++)
            {
                var track = document.Tracks[t];
                if (track.Machine != machineName) continue;
                foreach (var key in track.Slots.Where(s => s.Value == name).Select(s => s.Key).ToList())
                {
                    slots.Add((t, key));
                    track.Slots.Remove(key);
                }
            }
            document.Clips.TryGetValue((machineName, name), out var clip);
            document.Clips.Remove((machineName, name));
            TransformSnapshots(d => d.RenamePattern(machineName, name, retired));
            Bury(new Grave
            {
                Kind = GraveKind.Pattern,
                Original = pattern,
                Machine = machine,
                MachineName = machineName,
                Name = name,
                Retired = retired,
                Fingerprint = Fingerprint(pattern),
                Slots = slots.ToArray(),
                Clip = clip?.Clone()
            });
        }

        void OnPatternAddedToSong(IPattern pattern)
        {
            if (document != null && pattern != null && graves.Any(g => g.Kind == GraveKind.Pattern && g.Name == pattern.Name))
                dispatcher.BeginInvoke(() => TryReturnPattern(pattern));   // after the undo that added it has copied its data
        }

        void TryReturnPattern(IPattern pattern)
        {
            var machine = pattern?.Machine;
            if (document == null || machine == null || !machine.Patterns.Contains(pattern)) return;
            long print = Fingerprint(pattern);
            var grave = graves.LastOrDefault(g => g.Kind == GraveKind.Pattern && g.Name == pattern.Name &&
                                                 g.MachineName == machine.Name && g.Fingerprint == print);
            if (grave != null)
                Resurrect(grave, pattern);
        }

        /// <summary>
        /// The session's own undo brought back <paramref name="dead"/> as <paramref name="alive"/>
        /// (maybe under another name, if its own was taken meanwhile): its slots and settings
        /// come back, and the session's actions follow it.
        /// </summary>
        internal void ResurrectAs(IPattern dead, IPattern alive)
        {
            if (dead == null || alive == null) return;
            forward[dead] = alive;
            var grave = graves.LastOrDefault(g => g.Kind == GraveKind.Pattern && ReferenceEquals(g.Original, dead));
            if (grave != null)
                Resurrect(grave, alive);
        }

        void Resurrect(Grave grave, IPattern alive)
        {
            graves.Remove(grave);
            forward[grave.Original] = alive;
            string machineName = alive.Machine.Name;
            TransformSnapshots(d => d.RenamePattern(grave.MachineName, grave.Retired, alive.Name));
            if (document != null)
            {
                foreach (var (t, scene) in grave.Slots)
                    if (t < document.Tracks.Count && document.Tracks[t].Machine == machineName && !document.Tracks[t].Slots.ContainsKey(scene))
                        document.Tracks[t].Slots[scene] = alive.Name;
                if (grave.Clip != null && !document.Clips.ContainsKey((machineName, alive.Name)))
                    document.SetClip(machineName, alive.Name, grave.Clip);
                document.EnsureScenes();
            }
            RaiseChanged();
        }

        // ------------------------------------------------------------------ machines

        void OnMachineRemovedFromSong(IMachine machine, string name)
        {
            if (document == null || string.IsNullOrEmpty(name) || SessionDocument.IsRetired(name)) return;
            string retired = SessionDocument.Retire(name, ++retiredCounter);
            var patternNames = graves.Where(g => g.Kind == GraveKind.Pattern && g.MachineName == name).Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal);
            // Its crossfader side, return, cue and clip settings stay with the retired name: a new
            // machine called the same gets none of them.
            TransformAll(d => d.RenameMachine(name, retired));
            foreach (var g in graves.Where(g => g.MachineName == name))
                g.MachineName = retired;
            Bury(new Grave
            {
                Kind = GraveKind.Machine,
                Original = machine,
                Machine = machine,
                MachineName = retired,
                Name = name,
                Retired = retired,
                Dll = machine?.DLL?.Name ?? "",
                Fingerprint = StableHash(string.Join("\n", patternNames))
            });
        }

        void OnMachineAddedToSong(IMachine machine)
        {
            if (document != null && machine != null && graves.Any(g => g.Kind == GraveKind.Machine && g.Name == machine.Name))
                dispatcher.BeginInvoke(() => TryReturnMachine(machine));   // after the undo that added it has made its patterns
        }

        void TryReturnMachine(IMachine machine)
        {
            if (document == null || machine == null || !song.Machines.Contains(machine)) return;
            long print = StableHash(string.Join("\n", machine.Patterns.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)));
            var grave = graves.LastOrDefault(g => g.Kind == GraveKind.Machine && g.Name == machine.Name &&
                                                 g.Dll == (machine.DLL?.Name ?? "") && g.Fingerprint == print);
            if (grave == null) return;
            graves.Remove(grave);
            forward[grave.Original] = machine;
            TransformAll(d => d.RenameMachine(grave.Retired, machine.Name));
            foreach (var g in graves.Where(g => g.MachineName == grave.Retired))
            {
                g.MachineName = machine.Name;
                g.Machine = machine;
            }
            // Its patterns and sequences were added before this ran: try them again now that the machine is back.
            foreach (var p in machine.Patterns)
                TryReturnPattern(p);
            foreach (var seq in song.Sequences.Where(s => s.Machine == machine))
                TryReturnSequence(seq);
            RaiseChanged();
        }

        // ------------------------------------------------------------------ fingerprints

        static long Fingerprint(IPattern pattern)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                void Mix(long v) { h = (h ^ v) * 1099511628211L; }
                Mix(pattern.Length);
                foreach (var column in pattern.Columns)
                {
                    Mix(column.Track);
                    Mix(StableHash(column.Parameter?.Name ?? ""));
                    foreach (var e in column.GetEvents(int.MinValue + 1, int.MaxValue))
                    {
                        Mix(e.Time);
                        Mix(e.Value);
                    }
                }
                return h;
            }
        }

        static long Fingerprint(ISequence seq)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                void Mix(long v) { h = (h ^ v) * 1099511628211L; }
                foreach (var e in seq.Events)
                {
                    Mix(e.Key);
                    Mix((int)e.Value.Type);
                    Mix(StableHash(e.Value.Pattern?.Name ?? ""));
                }
                return h;
            }
        }

        static long StableHash(string s)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                foreach (char c in s ?? "")
                    h = (h ^ c) * 1099511628211L;
                return h;
            }
        }
    }

    // ------------------------------------------------------------------ the session's pattern actions (0.6.1)

    /// <summary>
    /// Creates a pattern: empty, or a copy of another. Holds the pattern it made, not its name:
    /// undo deletes that pattern only (never another editor's pattern of the same name), and
    /// redo makes it again (under another name if its own was taken meanwhile), putting its
    /// slots back.
    /// </summary>
    internal sealed class SessionCreatePatternAction : IAction
    {
        readonly SessionLayout layout;
        readonly IMachine machine;
        readonly string name;
        readonly int length;
        readonly IPattern source;
        PatternClip data;
        IPattern created;

        public SessionCreatePatternAction(SessionLayout layout, IMachine machine, string name, int length, IPattern source = null)
        {
            this.layout = layout;
            this.machine = machine;
            this.name = name;
            this.length = length;
            this.source = source;
        }

        /// <summary>The pattern made (the latest, after a redo).</summary>
        public IPattern Created => created == null ? null : layout.Current(created);

        public void Do()
        {
            var m = layout.Current(machine);
            if (m == null) return;
            string free = m.Patterns.Any(p => p.Name == name) ? SessionLayout.CopyName(m, name) : name;
            if (created == null)
            {
                var src = source == null ? null : layout.Current(source);
                if (src != null && m.Patterns.Contains(src))
                    m.ClonePattern(free, src);
                else
                    m.CreatePattern(free, length);
                created = m.Patterns.FirstOrDefault(p => p.Name == free);
                if (created != null) data = new PatternClip(created);
                return;
            }
            var dead = layout.Current(created);
            m.CreatePattern(free, length);
            var again = m.Patterns.FirstOrDefault(p => p.Name == free);
            if (again == null) return;
            data?.CopyTo(again);
            layout.ResurrectAs(dead, again);
        }

        public void Undo()
        {
            var m = layout.Current(machine);
            var p = Created;
            if (m == null || p == null || !m.Patterns.Contains(p)) return;
            data = new PatternClip(p);
            m.DeletePattern(p);
        }
    }

    /// <summary>
    /// Deletes a pattern; undo makes it again with its contents (under another name if its own
    /// was taken meanwhile) and puts its slots and settings back. Holds the object, not its name.
    /// </summary>
    internal sealed class SessionDeletePatternAction : IAction
    {
        readonly SessionLayout layout;
        readonly IMachine machine;
        IPattern pattern;
        string name;
        int length;
        PatternClip data;

        public SessionDeletePatternAction(SessionLayout layout, IPattern pattern)
        {
            this.layout = layout;
            this.pattern = pattern;
            machine = pattern.Machine;
        }

        public void Do()
        {
            var m = layout.Current(machine);
            var p = layout.Current(pattern);
            if (m == null || p == null || !m.Patterns.Contains(p)) return;
            name = p.Name;
            length = p.Length;
            data = new PatternClip(p);
            pattern = p;
            m.DeletePattern(p);
        }

        public void Undo()
        {
            var m = layout.Current(machine);
            if (m == null || data == null) return;
            string free = m.Patterns.Any(p => p.Name == name) ? SessionLayout.CopyName(m, name) : name;
            m.CreatePattern(free, length);
            var again = m.Patterns.FirstOrDefault(p => p.Name == free);
            if (again == null) return;
            data.CopyTo(again);
            layout.ResurrectAs(pattern, again);
        }
    }
}
