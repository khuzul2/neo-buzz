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
    ///  - a deleted pattern's or machine's name is retired (<see cref="SessionDocument.Retire"/>):
    ///    a new pattern or machine reusing the name inherits nothing.
    /// The session's own undo brings a pattern it deleted back with its slots and settings
    /// (<see cref="ResurrectAs"/>), and its pattern actions hold objects, not names
    /// (<see cref="Current{T}"/> follows a pattern the session made again), so they never touch
    /// another editor's pattern of the same name. An object another view's undo brings back is
    /// a new object to the layout: matching it to the old one by name and contents can't tell it
    /// from a new pattern or machine that merely looks the same, so it isn't attempted.
    ///
    /// Song changes made under AudioLock (deleting a machine deletes its sequences and patterns
    /// under it) only touch the current layout there; undo snapshots and the launch map follow
    /// once the lock is released (the liveness flags cover the gap).
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>What the layout had for a pattern the session deleted, until its undo brings it back.</summary>
        sealed class Grave
        {
            public IPattern Original;
            public string MachineName = "";      // as the layout knows it now (retired while the machine is gone)
            public string Retired = "";          // the pattern's name in snapshots meanwhile
            public (int Track, int Scene)[] Slots = Array.Empty<(int, int)>();
            public SessionDocument.ClipSettings Clip;
        }

        const int MaxGraves = 256;
        readonly List<Grave> graves = new List<Grave>();
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

        /// <summary>Applies a change to the layout and to every undo snapshot.</summary>
        void TransformAll(Action<SessionDocument> change)
        {
            if (document != null)
                change(document);
            TransformSnapshots(change);
        }

        /// <summary>
        /// Applies a change to every undo snapshot there is now; under AudioLock, once it is
        /// released (in order, and to those snapshots only: later ones are made from the
        /// already changed layout).
        /// </summary>
        void TransformSnapshots(Action<SessionDocument> change)
        {
            var targets = UndoStack.Actions.OfType<LayoutEditAction>().SelectMany(a => new[] { a.Before, a.After }).ToArray();
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

        /// <summary>New song or song loaded: nothing to bring back, sequences as they are.</summary>
        void ResetIdentity()
        {
            graves.Clear();
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
                TransformAll(d => d.RemoveTrack(at));
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
            graves.Add(new Grave
            {
                Original = pattern,
                MachineName = machineName,
                Retired = retired,
                Slots = slots.ToArray(),
                Clip = clip?.Clone()
            });
            if (graves.Count > MaxGraves)
                graves.RemoveAt(0);
        }

        /// <summary>
        /// The session's own undo brought back <paramref name="dead"/> as <paramref name="alive"/>
        /// (maybe under another name, if its own was taken meanwhile): its slots and settings
        /// come back, and the session's actions follow it.
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
            // Its crossfader side, return, cue and clip settings stay with a retired name: a new
            // machine called the same gets none of them.
            string retired = SessionDocument.Retire(name, ++retiredCounter);
            TransformAll(d => d.RenameMachine(name, retired));
            foreach (var g in graves.Where(g => g.MachineName == name))
                g.MachineName = retired;
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
            if (!layout.IsInSong(m)) return;         // its machine was deleted since
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
            if (!layout.IsInSong(m) || p == null || !m.Patterns.Contains(p)) return;
            data = new PatternClip(p);
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
            if (!layout.IsInSong(m) || p == null || !m.Patterns.Contains(p)) return;
            name = p.Name;
            length = p.Length;
            data = new PatternClip(p);
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
            data.CopyTo(again);
            layout.ResurrectAs(pattern, again);
        }
    }
}
