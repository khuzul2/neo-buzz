// Fork (docs/specs/ux-overhaul.md P3b): the notes of the Tracker's MIDI note lanes (the MIDI Note, Velocity, Note Delay and Note
// Cut columns of each track of the pattern's own machine), which is where the Session View, the agent and a recording put the
// notes of a machine that takes MIDI. The editor reads them as notes (ExportClipNotes: a delay or a cut puts a note between two
// rows) and makes them again from the notes after an edit (EditClipNotes, one undo step of the editor's own history, shared
// with the Tracker). Voices a track each: more notes at once than the machine has tracks, or above B7, are refused.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class EditorLaneNotes : NoteSourceBase
    {
        readonly ReBuzzCore buzz;
        readonly PatternCore pattern;
        readonly MachineCore machine;

        EditorLaneNotes(ReBuzzCore buzz, PatternCore pattern)
        {
            this.buzz = buzz;
            this.pattern = pattern;
            machine = (MachineCore)pattern.Machine;
        }

        /// <summary>The MIDI lane notes of a pattern, or null when its machine's editor doesn't exchange them.</summary>
        public static EditorLaneNotes For(ReBuzzCore buzz, IPattern pattern) =>
            pattern is PatternCore pc && pc.Machine is MachineCore m && buzz.MachineManager.HasClipNoteEditor(m) ? new EditorLaneNotes(buzz, pc) : null;

        public override IPattern Pattern => pattern;

        List<TrackerNote> Read() => TrackerNotes.Unpack(buzz.MachineManager.ExportClipNotes(machine, pattern));

        public override List<ClipNote> Notes() =>
            IsLive ? Read().Select(n => new ClipNote(n.Time, n.Duration, n.Midi, n.Velocity)).OrderBy(n => n.Time).ThenBy(n => n.Note).ToList() : new List<ClipNote>();

        public override string WhyNotEditable =>
            !IsLive ? "The pattern was deleted."
            : buzz.SessionLayout?.IsRecordingClip(pattern) == true ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            var notes = Read().Select(n => new ClipNote(n.Time, n.Duration, n.Midi, n.Velocity)).ToList();
            foreach (var r in removed)
                notes.Remove(r);
            int length = Length;
            notes.AddRange(added.Where(n => n.Time < length));
            var packed = TrackerNotes.Pack(notes.Select(n => new TrackerNote(n.Time, n.Duration, n.Note, n.Velocity)));
            if (!buzz.MachineManager.EditClipNotes(machine, pattern, packed))
                return $"{machine.Name}'s Tracker can't hold these notes on its MIDI lanes: a note a track at a time (it has {Math.Max(1, machine.TrackCount)}), up to {BuzzGUI.Common.NoteNames.Name(119)}.";
            buzz.SetModifiedFlag();
            return null;
        }

        public override void Preview(int note, int velocity, bool on) => SendMidiNote(buzz, pattern, note, velocity, on);

        protected override void DoUndo() => buzz.MachineManager.EditorHistory(machine, redo: false);

        protected override void DoRedo() => buzz.MachineManager.EditorHistory(machine, redo: true);
    }
}
