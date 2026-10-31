using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Identity (docs/specs/session-view.md 0.6.1). The layout keeps names, as song files do,
    /// and follows every change of the song exactly, in the current layout and in every undo
    /// snapshot alike:
    ///  - sequences added, removed or swapped move tracks by their exact index (a song may have
    ///    several sequences of one machine);
    ///  - renames rename;
    ///  - a deleted pattern's or machine's name is retired (<see cref="SessionDocument.Retire"/>):
    ///    a new pattern or machine reusing the name inherits nothing.
    /// An undo that brings a deleted pattern, sequence or machine back, in the session or in any
    /// other view, reports which new object stands for the old one
    /// (<see cref="BuzzGUI.Common.Actions.Restorations"/>, 0.6.2): its slots, settings, track
    /// (in every undo snapshot that had it) and machine name come back exactly. Nothing is
    /// matched by name or contents, which can't tell an object brought back from a new one that
    /// merely looks the same. The session's pattern actions hold objects, not names
    /// (<see cref="Current{T}"/> follows an object brought back), so they never touch another
    /// editor's pattern of the same name.
    ///
    /// Song changes made under AudioLock (deleting a machine deletes its sequences and patterns
    /// under it) only touch the current layout there; undo snapshots and the launch map follow
    /// once the lock is released (the liveness flags cover the gap).
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>
        /// A deleted pattern, until an undo brings it back. Its slots and clip settings stay in
        /// the layout and every snapshot under its retired name, so they keep their places
        /// through every edit and transform since (0.6.2).
        /// </summary>
        sealed class Grave
        {
            public IPattern Original;
            public string MachineName = "";      // as the layout knows it now (retired while the machine is gone)
            public string Retired = "";          // the pattern's name in the layout meanwhile
        }

        /// <summary>
        /// A removed sequence's track, as the layout and each undo snapshot had it (0.6.2). The
        /// copies sit in holder documents, so renames, deletions and restorations of patterns
        /// and machines meanwhile reach them as they reach the layout (0.6.3): the live copy
        /// with the layout, the snapshots' copies with the snapshots.
        /// </summary>
        sealed class TrackGrave
        {
            public ISequence Original;
            public SessionDocument.Track Live;                 // the layout's, when it was removed
            // Weak (0.6.3): a snapshot dropped from the undo history takes its copy with it.
            public readonly ConditionalWeakTable<SessionDocument, SessionDocument.Track> InSnapshots =
                new ConditionalWeakTable<SessionDocument, SessionDocument.Track>();
            public readonly SessionDocument LiveHolder = new SessionDocument();
            public readonly SessionDocument SnapshotHolder = new SessionDocument();
        }

        const int MaxGraves = 256;
        readonly List<Grave> graves = new List<Grave>();
        readonly List<TrackGrave> trackGraves = new List<TrackGrave>();
        readonly Dictionary<IMachine, (string Name, string Retired)> retiredMachines = new Dictionary<IMachine, (string, string)>(ReferenceEqualityComparer.Instance);
        readonly Queue<IMachine> retiredOrder = new Queue<IMachine>();
        readonly Dictionary<object, object> forward = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        readonly List<ISequence> bound = new List<ISequence>();      // the song's sequences as the layout last saw them
        readonly List<(SessionDocument[] Targets, Action<SessionDocument> Change)> pendingTransforms = new List<(SessionDocument[], Action<SessionDocument>)>();
        bool transformsPosted;
        int retiredCounter;

        /// <summary>The object now standing for <paramref name="obj"/>: itself, or what the session's undo made again in its place.</summary>
        internal T Current<T>(T obj) where T : class
        {
            object o = obj;
            for (int guard = 0; o != null && forward.TryGetValue(o, out var next) && guard < 64; guard++)
                o = next;
            return o as T ?? obj;
        }

        /// <summary>Whether a machine is (still) in the song.</summary>
        internal bool IsInSong(IMachine machine) => machine != null && song.Machines.Contains(machine);

        /// <summary>The song's machines now (for engine columns that play another machine).</summary>
        internal ICollection<IMachine> SongMachines => song.Machines.ToList();

        /// <summary>Applies a change to the layout and to every undo snapshot.</summary>
        void TransformAll(Action<SessionDocument> change) => TransformAll(change, names: false);

        /// <summary>
        /// Applies a change of names (a rename, retirement, restoration or purge) to the layout,
        /// every undo snapshot, and the removed tracks kept for an undo (0.6.3).
        /// </summary>
        void TransformNames(Action<SessionDocument> change) => TransformAll(change, names: true);

        void TransformAll(Action<SessionDocument> change, bool names)
        {
            if (document != null)
                change(document);
            if (names)
                foreach (var g in trackGraves)
                    change(g.LiveHolder);
            TransformSnapshots(change, names);
        }

        void TransformSnapshots(Action<SessionDocument> change) => TransformSnapshots(change, names: false);

        /// <summary>
        /// Applies a change to every undo snapshot there is now (with <paramref name="names"/>,
        /// also to the removed tracks' copies from snapshots); under AudioLock, once it is
        /// released (in order, and to those snapshots only: later ones are made from the
        /// already changed layout).
        /// </summary>
        void TransformSnapshots(Action<SessionDocument> change, bool names)
        {
            var snapshots = UndoStack.Actions.OfType<LayoutEditAction>().SelectMany(a => new[] { a.Before, a.After });
            if (names)
                snapshots = snapshots.Concat(trackGraves.Select(g => g.SnapshotHolder));
            var targets = snapshots.ToArray();
            if (targets.Length == 0) return;
            if (ReBuzzCore.AudioLock.IsHeldByCurrentThread)
            {
                pendingTransforms.Add((targets, change));
                if (!transformsPosted)
                {
                    transformsPosted = true;
                    dispatcher.BeginInvoke(FlushTransforms);
                }
                return;
            }
            FlushTransforms();
            foreach (var d in targets)
                change(d);
        }

        /// <summary>Applies the snapshot changes held back under AudioLock (before any undo or redo uses a snapshot).</summary>
        void FlushTransforms()
        {
            transformsPosted = false;
            if (pendingTransforms.Count == 0) return;
            var list = pendingTransforms.ToArray();
            pendingTransforms.Clear();
            foreach (var (targets, change) in list)
                foreach (var d in targets)
                    change(d);
        }

        /// <summary>ReBuzz closes (or a test's core goes): off the static restoration bus, which would keep this layout and its song alive (0.6.3).</summary>
        internal void Detach() => BuzzGUI.Common.Actions.Restorations.Restored -= OnRestored;

        /// <summary>New song or song loaded: nothing to bring back, sequences as they are.</summary>
        void ResetIdentity()
        {
            graves.Clear();
            trackGraves.Clear();
            retiredMachines.Clear();
            retiredOrder.Clear();
            forward.Clear();
            pendingTransforms.Clear();
            bound.Clear();
            bound.AddRange(song.Sequences);
        }

        // ------------------------------------------------------------------ sequences

        void OnSequenceAdded(int index, ISequence seq)
        {
            index = Math.Clamp(index, 0, bound.Count);
            bound.Insert(index, seq);
            string machine = seq?.Machine?.Name ?? "";
            TransformAll(d => d.InsertTrack(index, machine));
            OnStructureChanged();
        }

        void OnSequenceRemoved(int index, ISequence seq)
        {
            int at = bound.IndexOf(seq);
            if (at < 0) at = index;
            if (at >= 0 && at < bound.Count)
            {
                bound.RemoveAt(at);
                // The track goes from the layout and every snapshot, each keeping its own copy
                // in the grave, for an undo that brings the sequence back (0.6.2).
                var grave = new TrackGrave { Original = seq };
                if (document != null && at < document.Tracks.Count)
                {
                    grave.Live = document.Tracks[at].Clone();
                    grave.LiveHolder.Tracks.Add(grave.Live);
                }
                if (document != null)
                    document.RemoveTrack(at);
                TransformSnapshots(d =>
                {
                    if (at < d.Tracks.Count && !grave.InSnapshots.TryGetValue(d, out _))
                    {
                        var copy = d.Tracks[at].Clone();
                        grave.InSnapshots.AddOrUpdate(d, copy);
                        grave.SnapshotHolder.Tracks.Add(copy);
                    }
                    d.RemoveTrack(at);
                });
                trackGraves.Add(grave);
                if (trackGraves.Count > MaxGraves)
                    trackGraves.RemoveAt(0);
            }
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

        // ------------------------------------------------------------------ patterns

        void OnPatternRemovedFromSong(IPattern pattern)
        {
            var machine = pattern?.Machine;
            if (document == null || machine == null || SessionDocument.IsRetired(pattern.Name)) return;
            string machineName = machineNames.TryGetValue(machine, out var known) ? known : machine.Name;
            string name = pattern.Name;
            string retired = SessionDocument.Retire(name, ++retiredCounter);
            // Its slots and settings stay where they are under the retired name: empty to everyone
            // (a newcomer with its name inherits nothing), until an undo brings it back.
            TransformNames(d => d.RenamePattern(machineName, name, retired));
            graves.Add(new Grave { Original = pattern, MachineName = machineName, Retired = retired });
            if (graves.Count > MaxGraves)
            {
                var gone = graves[0];
                graves.RemoveAt(0);
                TransformNames(d => d.PurgeRetired(gone.Retired));
            }
        }

        /// <summary>
        /// An undo brought back <paramref name="dead"/> as <paramref name="alive"/> (maybe under
        /// another name, if its own was taken meanwhile): its slots and settings come back where
        /// they are, and the session's actions follow it.
        /// </summary>
        internal void ResurrectAs(IPattern dead, IPattern alive)
        {
            if (dead == null || alive?.Machine == null) return;
            forward[dead] = alive;
            var grave = graves.LastOrDefault(g => ReferenceEquals(g.Original, dead));
            if (grave == null) return;
            graves.Remove(grave);
            string machineName = alive.Machine.Name;
            if (grave.MachineName != machineName) return;      // its machine is gone (retired): nothing to give back
            TransformNames(d => d.RenamePattern(grave.MachineName, grave.Retired, alive.Name));
            RaiseChanged();
        }

        // ------------------------------------------------------------------ machines

        void OnMachineRemovedFromSong(IMachine machine, string name)
        {
            if (document == null || string.IsNullOrEmpty(name) || SessionDocument.IsRetired(name)) return;
            // Its crossfader side, return, cue and clip settings stay with a retired name: a new
            // machine called the same gets none of them.
            string retired = SessionDocument.Retire(name, ++retiredCounter);
            TransformNames(d => d.RenameMachine(name, retired));
            foreach (var g in graves.Where(g => g.MachineName == name))
                g.MachineName = retired;
            if (machine != null)
            {
                retiredMachines[machine] = (name, retired);
                retiredOrder.Enqueue(machine);
                while (retiredOrder.Count > MaxGraves)          // 0.6.3: bounded, as the pattern graves
                    retiredMachines.Remove(retiredOrder.Dequeue());
            }
        }

        // ------------------------------------------------------------------ restorations (0.6.2)

        /// <summary>An undo (here or in another view) brought back <paramref name="previous"/> as <paramref name="restored"/>.</summary>
        void OnRestored(object previous, object restored)
        {
            switch (restored)
            {
                case IMachine machine when previous is IMachine old && IsInSong(machine):
                    RestoreMachine(old, machine);
                    break;
                // 0.6.3: only for the same machine (or the one an undo made in its place): a
                // newcomer that merely got the old machine's name inherits nothing.
                case IPattern pattern when previous is IPattern old && pattern.Machine != null && IsInSong(pattern.Machine)
                                          && ReferenceEquals(Current(old.Machine), pattern.Machine):
                    ResurrectAs(old, pattern);
                    break;
                case ISequence sequence when previous is ISequence old && song.Sequences.Contains(sequence)
                                            && ReferenceEquals(Current(old.Machine), sequence.Machine):
                    RestoreSequence(old, sequence);
                    break;
            }
        }

        void RestoreMachine(IMachine old, IMachine machine)
        {
            forward[old] = machine;
            if (!retiredMachines.Remove(old, out var record)) return;
            // Its crossfader side, return, cue and clip settings come back under its name.
            TransformNames(d => d.RenameMachine(record.Retired, machine.Name));
            foreach (var g in graves.Where(g => g.MachineName == record.Retired))
                g.MachineName = machine.Name;
            RaiseChanged();
        }

        void RestoreSequence(ISequence old, ISequence sequence)
        {
            forward[old] = sequence;
            var grave = trackGraves.LastOrDefault(g => ReferenceEquals(g.Original, old));
            if (grave == null) return;
            trackGraves.Remove(grave);
            int at = bound.IndexOf(sequence);
            if (at < 0) return;
            if (document != null)
            {
                // The layout gets back the track it had when the sequence was removed.
                document.PutTrack(at, grave.Live);
            }
            // Snapshots made after the removal have no copy of their own: the track as it was
            // removed is theirs too (0.6.3), so undoing a later edit doesn't empty it again.
            TransformSnapshots(d =>
            {
                if (grave.InSnapshots.TryGetValue(d, out var copy))
                    d.PutTrack(at, copy);
                else if (grave.Live != null)
                    d.PutTrack(at, grave.Live);
            });
            dirty = true;
            ContentChanged();
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
        int length;
        readonly IPattern source;
        EngineColumns data;              // 0.6.3: engine columns (MIDI, takes); editors keep their own data
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
            if (!layout.IsInSong(m)) return;         // its machine was deleted since
            string free = m.Patterns.Any(p => p.Name == name) ? SessionLayout.CopyName(m, name) : name;
            if (created == null)
            {
                var src = source == null ? null : layout.Current(source);
                bool clone = src != null && m.Patterns.Contains(src);
                if (clone)
                    m.ClonePattern(free, src);
                else
                    m.CreatePattern(free, length);
                created = m.Patterns.FirstOrDefault(p => p.Name == free);
                // 0.6.3: a copy gets the engine columns too (the editor copies its own data).
                if (clone && created != null && created.Columns.Count == 0)
                    EngineColumns.Copy(src, created, layout.SongMachines);
                return;
            }
            var dead = layout.Current(created);
            m.CreatePattern(free, length);
            var again = m.Patterns.FirstOrDefault(p => p.Name == free);
            if (again == null) return;
            data?.RestoreInto(again, layout.SongMachines);
            layout.ResurrectAs(dead, again);
        }

        public void Undo()
        {
            var m = layout.Current(machine);
            var p = Created;
            if (!layout.IsInSong(m) || p == null || !m.Patterns.Contains(p)) return;
            data = EngineColumns.Capture(p);
            length = p.Length;                       // a redo makes it as it is now
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
        EngineColumns data;

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
            if (!layout.IsInSong(m) || p == null || !m.Patterns.Contains(p)) return;
            name = p.Name;
            length = p.Length;
            data = EngineColumns.Capture(p);
            pattern = p;
            m.DeletePattern(p);
        }

        public void Undo()
        {
            var m = layout.Current(machine);
            if (!layout.IsInSong(m) || data == null) return;   // its machine was deleted since: no ghost pattern
            string free = m.Patterns.Any(p => p.Name == name) ? SessionLayout.CopyName(m, name) : name;
            m.CreatePattern(free, length);
            var again = m.Patterns.FirstOrDefault(p => p.Name == free);
            if (again == null) return;
            data.RestoreInto(again, layout.SongMachines);
            layout.ResurrectAs(pattern, again);
        }
    }
}
