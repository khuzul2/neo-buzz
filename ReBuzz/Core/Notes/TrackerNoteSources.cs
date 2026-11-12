// Fork (docs/specs/ux-overhaul.md P3b): which of the Tracker's notes the piano roll shows for a pattern. A machine can take notes in
// its Note track parameter (the Tracker's Note columns) and as MIDI (the MIDI note lanes the Session View, the agent and recordings
// write); the Tracker can hold both. The roll shows one at a time: the one that has notes when only one has, else the one the machine
// takes (its manifest's note input; a machine without a manifest takes its Note column), and the user can pick the other.

using System;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core.Notes
{
    internal enum TrackerLane { Auto, NoteColumn, MidiLanes }

    internal sealed record TrackerNoteChoice(INoteSource Source, TrackerLane Lane, bool BothAvailable, int? RowTime);

    internal static class TrackerNoteSources
    {
        /// <summary>The notes to show and where they are, or null when the Tracker has none for this machine (no Note column, no MIDI lanes, no editor).</summary>
        public static TrackerNoteChoice Open(ReBuzzCore buzz, IPattern pattern, TrackerLane want = TrackerLane.Auto)
        {
            var column = EditorTrackerNotes.ForEditor(buzz, pattern);
            var lanes = TakesMidi(pattern.Machine as MachineCore) ? EditorLaneNotes.For(buzz, pattern) : null;
            if (column == null && lanes == null) return null;
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
            return lane == TrackerLane.NoteColumn
                ? new TrackerNoteChoice(column, lane, both, column.RowTimeAt(0))
                : new TrackerNoteChoice(lanes, lane, both, null);
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
