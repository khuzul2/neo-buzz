// Fork (docs/specs/ux-overhaul.md P3b): which of the Tracker's notes the piano roll shows for a pattern. A machine can take notes in
// its Note track parameter (the Tracker's Note columns) and as MIDI (the MIDI note lanes the Session View, the agent and recordings
// write); the Tracker can hold both. The roll shows one at a time: the one that has notes when only one has, else the one the machine
// takes (its manifest's note input; a machine without a manifest takes its Note column), and the user can pick the other.
// Fork (daw-modes.md AR2): a third store, SongNotes: the notes the song keeps in the engine's columns (what the Session View's clip
// plays and the arrangement's piano roll edits). The Tracker's own stores are not converted into it; the roll shows it
// when the Tracker holds no notes and the song does, and offers it beside the others.
// Fork (daw-modes.md AR3): the Tracker shows the MIDI column's notes in lanes of its own, so for a machine that plays that column
// the song's notes are the roll's default whenever the editor's own stores hold none.

using System;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core.Notes
{
    internal enum TrackerLane { Auto, NoteColumn, MidiLanes, SongNotes }

    internal sealed record TrackerNoteChoice(INoteSource Source, TrackerLane Lane, bool BothAvailable, int? RowTime, System.Collections.Generic.IReadOnlyList<TrackerLane> Available = null);

    internal static class TrackerNoteSources
    {
        /// <summary>The notes to show and where they are, or null when the Tracker has none for this machine (no Note column, no MIDI lanes, no editor).</summary>
        public static TrackerNoteChoice Open(ReBuzzCore buzz, IPattern pattern, TrackerLane want = TrackerLane.Auto, bool songOnly = false)
        {
            var song = SongNotes.For(buzz, pattern);
            if (songOnly)       // the arrangement's roll: the song's store alone
                return song == null ? null : new TrackerNoteChoice(song, TrackerLane.SongNotes, false, null, new[] { TrackerLane.SongNotes });
            if (want == TrackerLane.SongNotes && song == null) want = TrackerLane.Auto;      // the machine has no song store: as before
            var column = EditorTrackerNotes.ForEditor(buzz, pattern);
            var lanes = TakesMidi(pattern.Machine as MachineCore) ? EditorLaneNotes.For(buzz, pattern) : null;
            if (column == null && lanes == null)
                return song == null ? null : new TrackerNoteChoice(song, TrackerLane.SongNotes, false, null, new[] { TrackerLane.SongNotes });
            var available = new System.Collections.Generic.List<TrackerLane>();
            if (column != null) available.Add(TrackerLane.NoteColumn);
            if (lanes != null) available.Add(TrackerLane.MidiLanes);
            if (song != null) available.Add(TrackerLane.SongNotes);
            bool both = column != null && lanes != null;
            var lane = want;
            if (column == null) lane = TrackerLane.MidiLanes;
            else if (lanes == null) lane = TrackerLane.NoteColumn;
            else if (lane == TrackerLane.Auto)
            {
                bool inColumn = column.Notes().Count > 0, inLanes = lanes.Notes().Count > 0;
                lane = inColumn != inLanes ? (inColumn ? TrackerLane.NoteColumn : TrackerLane.MidiLanes)
                     : TakesNotesInItsTracks(pattern.Machine as MachineCore) ? TrackerLane.NoteColumn : TrackerLane.MidiLanes;
            }
            // Auto: the Tracker holds no notes and the song does (drawn in the arrangement's piano roll, a session take, the agent): those.
            // Fork (daw-modes.md AR3): a machine that plays the MIDI column gets them while the song has none yet too: the Tracker's
            // Notes lanes show that column, so a note drawn in the roll is a row in the tracker, and the editor's own MIDI lanes
            // (an old song's) are the store only when they hold notes.
            if (want == TrackerLane.Auto && song != null && (song.Notes().Count > 0 || SongNotes.IsMidi(song))
                && (column == null || column.Notes().Count == 0) && (lanes == null || lanes.Notes().Count == 0))
                lane = TrackerLane.SongNotes;
            if (want == TrackerLane.SongNotes && song != null) lane = TrackerLane.SongNotes;
            return lane switch
            {
                TrackerLane.SongNotes => new TrackerNoteChoice(song, lane, available.Count > 1, null, available),
                TrackerLane.NoteColumn => new TrackerNoteChoice(column, lane, available.Count > 1, column.RowTimeAt(0), available),
                _ => new TrackerNoteChoice(lanes, lane, available.Count > 1, null, available),
            };
        }

        /// <summary>Whether notes as MIDI mean anything to the machine: a generator, or one whose manifest has a note input.</summary>
        static bool TakesMidi(MachineCore machine) =>
            machine?.DLL?.Info?.Type == MachineType.Generator || SongModel.Manifest(machine?.DLL)?.Notes != null;

        /// <summary>Whether the machine takes notes in its Note track parameter (its manifest says so, or it has no manifest to say MIDI).</summary>
        static bool TakesNotesInItsTracks(MachineCore machine)
        {
            if (machine == null) return true;
            var manifest = SongModel.Manifest(machine.DLL);
            return manifest == null || manifest.Notes?.Input == "tracks";
        }
    }
}
