using BuzzGUI.Common;
using ReBuzz.Core.Notes;
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
        /// <summary>The clip's notes, in time order (SongNotes: the store the clip's machine plays; empty when the clip is gone).</summary>
        public List<ClipNote> NotesOf(IClip clip) =>
            clip?.Machine == null || (clip is ClipCore core && core.IsDeleted) ? new List<ClipNote>() : NoteSourceOf(clip)?.Notes() ?? new List<ClipNote>();

        /// <summary>The one reader and writer of the clip's notes: SongNotes, or the MIDI column for a machine with no note input of its own.</summary>
        INoteSource NoteSourceOf(IClip clip) => SongNotes.For(buzz, clip) ?? (clip?.Machine != null ? new MidiColumnNotes(buzz, clip) : null);

        /// <summary>
        /// One edit of a clip's notes, one undo step: <paramref name="removed"/> (notes of the clip
        /// as <see cref="NotesOf"/> gave them) go, <paramref name="added"/> come.
        /// </summary>
        public void EditNotes(IClip clip, IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            if (clip?.Machine == null || (clip is ClipCore core && core.IsDeleted)) return;
            // A pass being recorded is undone by value when it ends: an edit under it would outlive that undo.
            if (IsRecordingClip(clip)) return;
            NoteSourceOf(clip)?.Edit(removed, added);
        }

        /// <summary>Quantizes notes of a clip (all of them if <paramref name="notes"/> is null): one undo step.</summary>
        public void QuantizeNotes(IClip clip, IReadOnlyList<ClipNote> notes, int grid, double strength = 1.0, bool ends = false)
        {
            if (clip == null || grid <= 1) return;
            var before = (notes ?? NotesOf(clip)).ToList();
            var after = ClipNotes.Quantize(before, grid, strength, ends, ClipLengthOf(clip));
            EditNotes(clip, before, after);
        }

        /// <summary>The clip's length in clip time (1/240 tick).</summary>
        public static int ClipLengthOf(IClip clip) => Math.Max(1, clip?.Length ?? 1) * ClipNotes.TimeBase;

        /// <summary>Plays (or ends) a note on the clip's machine, as the piano roll is clicked.</summary>
        public void PreviewNote(IClip clip, int note, int velocity, bool on)
        {
            if (clip?.Machine is not MachineCore machine || machine.DLL == null || machine.DLL.IsMissing || machine.DLL.IsCrashed || !machine.Ready) return;
            int data = on
                ? 0x90 | ((note & 0x7F) << 8) | ((Math.Clamp(velocity, 1, 127) & 0x7F) << 16)
                : 0x80 | ((note & 0x7F) << 8);
            buzz.MachineManager.SendMidiInput(machine, data, false);
        }

        static ClipEvent ToEvent(ClipNote n)
        {
            var (time, value, duration) = n.ToEvent();
            return new ClipEvent(time, value, duration);
        }
    }

    /// <summary>
    /// Replaces events of one of a clip's recording columns (0.7.1, piano roll edits): removes
    /// some, adds others; undo does the opposite. Holds the clip itself.
    /// </summary>
    [HistoryName("Edit clip notes")]
    internal sealed class ClipEventsReplaceAction : IAction
    {
        readonly SessionLayout layout;
        readonly IClip clip;
        readonly ColumnKey column;
        readonly ClipEvent[] removed;
        readonly ClipEvent[] added;

        public ClipEventsReplaceAction(SessionLayout layout, IClip clip, ColumnKey column, ClipEvent[] removed, ClipEvent[] added)
        {
            this.layout = layout;
            this.clip = clip;
            this.column = column;
            this.removed = removed;
            this.added = added;
        }

        public void Do() => Apply(removed, added);

        public void Undo() => Apply(added, removed);

        void Apply(ClipEvent[] take, ClipEvent[] put)
        {
            var p = layout.Current(clip);
            if (p == null || (p is ClipCore core && core.IsDeleted)) return;
            var c = SessionLayout.ColumnFor(p, column, create: put.Length > 0);
            if (c == null) return;
            if (take.Length > 0) c.SetEvents(take, false);
            if (put.Length > 0) c.SetEvents(put, true);
        }
    }
}
