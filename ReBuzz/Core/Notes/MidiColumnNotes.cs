// Fork (docs/specs/daw-modes.md S4): a pattern's notes in its engine MIDI column (where session
// takes, the agent and the piano roll put them; D2 B: the one note store), each edit one undo
// step of the song's history, as the arrangement and the Pattern tab will edit them.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class MidiColumnNotes : NoteSourceBase
    {
        readonly ReBuzzCore buzz;
        readonly IPattern pattern;

        public MidiColumnNotes(ReBuzzCore buzz, IPattern pattern)
        {
            this.buzz = buzz;
            this.pattern = pattern;
        }

        public override IPattern Pattern => pattern;

        public override List<ClipNote> Notes()
        {
            if (!IsLive) return new List<ClipNote>();
            var column = SessionLayout.ColumnFor(pattern, ColumnKey.Midi, create: false);
            return column == null ? new List<ClipNote>() : ClipNotes.From(column.GetEvents(int.MinValue, int.MaxValue).Select(e => (e.Time, e.Value, e.Duration)));
        }

        public override string WhyNotEditable =>
            !IsLive ? "The pattern was deleted."
            : buzz.SessionLayout?.IsRecordingClip(pattern) == true ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            buzz.SongCore.ActionStack.Do(new ColumnEventsAction(buzz, pattern,
                new[] { new ColumnEventsAction.Change(ColumnKey.Midi, removed.Select(ToEvent).ToArray(), added.Select(ToEvent).ToArray()) }));
            buzz.SetModifiedFlag();
            return null;
        }

        public override void Preview(int note, int velocity, bool on) => SendMidiNote(buzz, pattern, note, velocity, on);

        protected override void DoUndo() => buzz.SongCore.ActionStack.Undo();

        protected override void DoRedo() => buzz.SongCore.ActionStack.Redo();

        static PatternEvent ToEvent(ClipNote n)
        {
            var (time, value, duration) = n.ToEvent();
            return new PatternEvent(time, value, duration);
        }
    }

    /// <summary>
    /// One undo step of the song's history: events go from and come to engine columns of a
    /// pattern (a column is made when events come to it). The pattern is found by its machine's
    /// and its own name each time, as BuzzGUI's actions find theirs, so undo and redo work after
    /// the machine is deleted and made again.
    /// </summary>
    internal sealed class ColumnEventsAction : IAction
    {
        internal readonly record struct Change(ColumnKey Column, PatternEvent[] Removed, PatternEvent[] Added);

        readonly ReBuzzCore buzz;
        readonly string machine;
        readonly string name;
        readonly Change[] changes;

        public ColumnEventsAction(ReBuzzCore buzz, IPattern pattern, IReadOnlyList<Change> changes)
        {
            this.buzz = buzz;
            machine = pattern.Machine.Name;
            name = pattern.Name;
            this.changes = changes.ToArray();
        }

        public void Do()
        {
            foreach (var c in changes) Apply(c.Column, c.Removed, c.Added);
        }

        public void Undo()
        {
            for (int i = changes.Length - 1; i >= 0; i--) Apply(changes[i].Column, changes[i].Added, changes[i].Removed);
        }

        void Apply(ColumnKey key, PatternEvent[] take, PatternEvent[] put)
        {
            var p = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == machine)?.Patterns.FirstOrDefault(x => x.Name == name);
            if (p == null) return;
            var column = SessionLayout.ColumnFor(p, key, create: put.Length > 0);
            if (column == null) return;
            if (take.Length > 0) column.SetEvents(take, false);
            if (put.Length > 0) column.SetEvents(put, true);
        }
    }
}
