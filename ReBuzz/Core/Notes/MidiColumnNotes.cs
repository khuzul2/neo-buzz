// Fork (docs/specs/daw-modes.md S4): a clip's notes in its engine MIDI column (where session
// takes, the agent and the piano roll put them; D2 B: the one note store), each edit one undo
// step of the song's history, as the arrangement and the Clip tab will edit them.

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
        readonly IClip clip;

        public MidiColumnNotes(ReBuzzCore buzz, IClip clip)
        {
            this.buzz = buzz;
            this.clip = clip;
        }

        public override IClip Clip => clip;

        public override List<ClipNote> Notes()
        {
            if (!IsLive) return new List<ClipNote>();
            var column = SessionLayout.ColumnFor(clip, ColumnKey.Midi, create: false);
            return column == null ? new List<ClipNote>() : ClipNotes.From(column.GetEvents(int.MinValue, int.MaxValue).Select(e => (e.Time, e.Value, e.Duration)));
        }

        public override string WhyNotEditable =>
            !IsLive ? "The clip was deleted."
            : buzz.SessionLayout?.IsRecordingClip(clip) == true ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            buzz.SongCore.ActionStack.Do(new ColumnEventsAction(buzz, clip,
                new[] { new ColumnEventsAction.Change(ColumnKey.Midi, removed.Select(ToEvent).ToArray(), added.Select(ToEvent).ToArray()) }));
            buzz.SetModifiedFlag();
            return null;
        }

        public override void Preview(int note, int velocity, bool on) => SendMidiNote(buzz, clip, note, velocity, on);

        protected override void DoUndo() => buzz.SongCore.ActionStack.Undo();

        protected override void DoRedo() => buzz.SongCore.ActionStack.Redo();

        static ClipEvent ToEvent(ClipNote n)
        {
            var (time, value, duration) = n.ToEvent();
            return new ClipEvent(time, value, duration);
        }
    }

    /// <summary>
    /// One undo step of the song's history: events go from and come to engine columns of a
    /// clip (a column is made when events come to it). The clip is found by its machine's
    /// and its own name each time, as BuzzGUI's actions find theirs, so undo and redo work after
    /// the machine is deleted and made again.
    /// </summary>
    internal sealed class ColumnEventsAction : IAction
    {
        internal readonly record struct Change(ColumnKey Column, ClipEvent[] Removed, ClipEvent[] Added);

        readonly ReBuzzCore buzz;
        readonly string machine;
        readonly string name;
        readonly Change[] changes;
        readonly bool automation;

        /// <param name="automation">An arrangement or clip envelope: new events go to the marked lane, not the tracker's.</param>
        public ColumnEventsAction(ReBuzzCore buzz, IClip clip, IReadOnlyList<Change> changes, bool automation = false)
        {
            this.automation = automation;
            this.buzz = buzz;
            machine = clip.Machine.Name;
            name = clip.Name;
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

        void Apply(ColumnKey key, ClipEvent[] take, ClipEvent[] put)
        {
            var p = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == machine)?.Clips.FirstOrDefault(x => x.Name == name);
            if (p == null) return;
            StoreLanes.Change(p, key, take, put, automation);
        }
    }
}
