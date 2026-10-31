using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// A clip's notes for the Session View's piano roll (docs/specs/session-view.md 0.7.1): the
    /// note-ons of its engine MIDI column (where takes are recorded), edited as undo steps.
    /// Works for any clip of a machine that takes MIDI: drawing into a clip without a MIDI
    /// column gives it one.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>The clip's notes, in time order (empty without a MIDI column).</summary>
        public List<ClipNote> NotesOf(IPattern clip)
        {
            var column = MidiColumnOf(clip);
            if (column == null) return new List<ClipNote>();
            return ClipNotes.From(column.GetEvents(int.MinValue, int.MaxValue).Select(e => (e.Time, e.Value, e.Duration)));
        }

        /// <summary>
        /// One edit of a clip's notes, one undo step: <paramref name="removed"/> (notes of the clip
        /// as <see cref="NotesOf"/> gave them) go, <paramref name="added"/> come.
        /// </summary>
        public void EditNotes(IPattern clip, IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            if (clip?.Machine == null || (clip is PatternCore core && core.IsDeleted)) return;
            // A pass being recorded is undone by value when it ends: an edit under it would outlive that undo.
            if (IsRecordingClip(clip)) return;
            removed ??= Array.Empty<ClipNote>();
            added ??= Array.Empty<ClipNote>();
            if (removed.Count == 0 && added.Count == 0) return;
            if (removed.Count == added.Count && removed.SequenceEqual(added)) return;
            UndoStack.Do(new ClipEventsReplaceAction(this, clip, ColumnKey.Midi,
                removed.Select(ToEvent).ToArray(), added.Select(ToEvent).ToArray()));
            buzz.SetModifiedFlag();
        }

        /// <summary>Quantizes notes of a clip (all of them if <paramref name="notes"/> is null): one undo step.</summary>
        public void QuantizeNotes(IPattern clip, IReadOnlyList<ClipNote> notes, int grid, double strength = 1.0, bool ends = false)
        {
            if (clip == null || grid <= 1) return;
            var before = (notes ?? NotesOf(clip)).ToList();
            var after = ClipNotes.Quantize(before, grid, strength, ends, ClipLengthOf(clip));
            EditNotes(clip, before, after);
        }

        /// <summary>The clip's length in pattern time (1/240 tick).</summary>
        public static int ClipLengthOf(IPattern clip) => Math.Max(1, clip?.Length ?? 1) * ClipNotes.TimeBase;

        /// <summary>Plays (or ends) a note on the clip's machine, as the piano roll is clicked.</summary>
        public void PreviewNote(IPattern clip, int note, int velocity, bool on)
        {
            if (clip?.Machine is not MachineCore machine || machine.DLL == null || machine.DLL.IsMissing || machine.DLL.IsCrashed || !machine.Ready) return;
            int data = on
                ? 0x90 | ((note & 0x7F) << 8) | ((Math.Clamp(velocity, 1, 127) & 0x7F) << 16)
                : 0x80 | ((note & 0x7F) << 8);
            buzz.MachineManager.SendMidiInput(machine, data, false);
        }

        // ------------------------------------------------------------------ the Pattern View (0.7.1)

        /// <summary>
        /// Whether the clip's notes can move to the Pattern View and back: its machine's pattern
        /// editor (the Modern Pattern Editor) takes notes on its MIDI note lanes.
        /// </summary>
        public bool CanMoveNotes(IPattern clip) =>
            clip is PatternCore p && !p.IsDeleted && clip.Machine is MachineCore m && buzz.MachineManager.HasClipNotesEditor(m);

        /// <summary>The notes on the clip's MIDI note lanes in the Pattern View, in time order (empty if its editor has none).</summary>
        public List<ClipNote> PatternViewNotesOf(IPattern clip) =>
            TrackerNotes.Unpack(ExportFromEditor(clip)).Select(n => new ClipNote(n.Time, n.Duration, n.Midi, n.Velocity))
                .OrderBy(n => n.Time).ThenBy(n => n.Note).ToList();

        /// <summary>
        /// Moves the clip's notes (its engine MIDI column's, where takes and the piano roll put
        /// them) onto its MIDI note lanes in the Pattern View, beside the notes already there:
        /// one undo step. Null when done, else why not (nothing changes then). Controllers stay
        /// in the clip.
        /// </summary>
        public string MoveNotesToPatternView(ISequence track, IPattern clip)
        {
            if (!CanMoveNotes(clip)) return "The clip's pattern editor doesn't take notes.";
            if (IsRecordingClip(clip)) return "The clip is recording.";
            var notes = NotesOf(clip);
            if (notes.Count == 0) return "The clip has no notes outside the Pattern View.";
            var before = ExportFromEditor(clip) ?? Array.Empty<int>();
            var moved = TrackerNotes.Pack(notes.Select(n => new TrackerNote(n.Time, n.Duration, n.Note, n.Velocity)));
            if (!ImportToEditor(clip, before.Concat(moved).ToArray()))
                return $"The notes don't fit the Pattern View's note lanes: more notes at once than {clip.Machine.Name} has tracks, " +
                       "notes above B7, or lanes whose beats are split differently.";
            // The moved notes as the lanes hold them (rows of 1/95), so undo takes back exactly those.
            var added = TrackerNotes.Added(before, ExportFromEditor(clip) ?? Array.Empty<int>());
            var removed = notes.Select(ToEvent).ToArray();
            MidiColumnOf(clip)?.SetEvents(removed, false);
            UndoStack.Do(new ClipNotesMoveAction(this, clip, lanesAdded: added, lanesRemoved: Array.Empty<int>(),
                columnRemoved: removed, columnAdded: Array.Empty<PatternEvent>(), done: true));
            buzz.SetModifiedFlag();
            return null;
        }

        /// <summary>
        /// Moves the notes on the clip's MIDI note lanes in the Pattern View into the clip's
        /// engine MIDI column (where the piano roll edits them): one undo step. Null when done,
        /// else why not.
        /// </summary>
        public string MoveNotesFromPatternView(ISequence track, IPattern clip)
        {
            if (!CanMoveNotes(clip)) return "The clip's pattern editor doesn't take notes.";
            if (IsRecordingClip(clip)) return "The clip is recording.";
            var taken = ExportFromEditor(clip) ?? Array.Empty<int>();
            var notes = TrackerNotes.Unpack(taken);
            if (notes.Count == 0) return "The clip has no notes in the Pattern View.";
            var column = ColumnFor(clip, ColumnKey.Midi, create: true);
            if (column == null) return "The clip can't take MIDI.";
            if (!ImportToEditor(clip, Array.Empty<int>())) return "The Pattern View didn't let go of the notes.";
            var added = notes.Select(n => ToEvent(new ClipNote(n.Time, n.Duration, n.Midi, n.Velocity))).ToArray();
            column.SetEvents(added, true);
            UndoStack.Do(new ClipNotesMoveAction(this, clip, lanesAdded: Array.Empty<int>(), lanesRemoved: taken,
                columnRemoved: Array.Empty<PatternEvent>(), columnAdded: added, done: true));
            buzz.SetModifiedFlag();
            return null;
        }

        internal int[] ExportFromEditor(IPattern clip) =>
            clip is PatternCore p && !p.IsDeleted && clip.Machine is MachineCore m ? buzz.MachineManager.ExportClipNotes(m, p) : null;

        internal bool ImportToEditor(IPattern clip, int[] notes) =>
            clip is PatternCore p && !p.IsDeleted && clip.Machine is MachineCore m && buzz.MachineManager.ImportClipNotes(m, p, notes);

        static PatternEvent ToEvent(ClipNote n)
        {
            var (time, value, duration) = n.ToEvent();
            return new PatternEvent(time, value, duration);
        }
    }

    /// <summary>
    /// Moves notes between a clip's engine MIDI column and its pattern editor's MIDI note lanes
    /// (0.7.1): the lanes gain <c>lanesAdded</c> and lose <c>lanesRemoved</c>, the column loses
    /// <c>columnRemoved</c> and gains <c>columnAdded</c>; undo does the opposite. The lanes
    /// change by those notes only, so edits made in the Pattern View since stay (hardening).
    /// If the lanes can't take the change any more, neither side changes. Holds the clip.
    /// </summary>
    internal sealed class ClipNotesMoveAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern clip;
        readonly int[] lanesAdded;
        readonly int[] lanesRemoved;
        readonly PatternEvent[] columnRemoved;
        readonly PatternEvent[] columnAdded;
        bool skip;

        public ClipNotesMoveAction(SessionLayout layout, IPattern clip, int[] lanesAdded, int[] lanesRemoved,
            PatternEvent[] columnRemoved, PatternEvent[] columnAdded, bool done)
        {
            this.layout = layout;
            this.clip = clip;
            this.lanesAdded = lanesAdded;
            this.lanesRemoved = lanesRemoved;
            this.columnRemoved = columnRemoved;
            this.columnAdded = columnAdded;
            skip = done;
        }

        public void Do()
        {
            if (skip) { skip = false; return; }    // done (and checked) before it was pushed
            Apply(lanesRemoved, lanesAdded, columnRemoved, columnAdded);
        }

        public void Undo() => Apply(lanesAdded, lanesRemoved, columnAdded, columnRemoved);

        void Apply(int[] laneTake, int[] lanePut, PatternEvent[] take, PatternEvent[] put)
        {
            var p = layout.Current(clip);
            if (p == null || (p is PatternCore core && core.IsDeleted)) return;
            var lanes = layout.ExportFromEditor(p);
            if (lanes == null) return;
            if (!layout.ImportToEditor(p, TrackerNotes.Without(lanes, laneTake).Concat(lanePut).ToArray())) return;
            var c = SessionLayout.ColumnFor(p, ColumnKey.Midi, put.Length > 0);
            if (c == null) return;
            if (take.Length > 0) c.SetEvents(take, false);
            if (put.Length > 0) c.SetEvents(put, true);
        }
    }

    /// <summary>
    /// Replaces events of one of a clip's recording columns (0.7.1, piano roll edits): removes
    /// some, adds others; undo does the opposite. Holds the clip itself.
    /// </summary>
    internal sealed class ClipEventsReplaceAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern clip;
        readonly ColumnKey column;
        readonly PatternEvent[] removed;
        readonly PatternEvent[] added;

        public ClipEventsReplaceAction(SessionLayout layout, IPattern clip, ColumnKey column, PatternEvent[] removed, PatternEvent[] added)
        {
            this.layout = layout;
            this.clip = clip;
            this.column = column;
            this.removed = removed;
            this.added = added;
        }

        public void Do() => Apply(removed, added);

        public void Undo() => Apply(added, removed);

        void Apply(PatternEvent[] take, PatternEvent[] put)
        {
            var p = layout.Current(clip);
            if (p == null || (p is PatternCore core && core.IsDeleted)) return;
            var c = SessionLayout.ColumnFor(p, column, create: put.Length > 0);
            if (c == null) return;
            if (take.Length > 0) c.SetEvents(take, false);
            if (put.Length > 0) c.SetEvents(put, true);
        }
    }
}
