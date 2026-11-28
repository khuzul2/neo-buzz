// Fork (docs/specs/mcp-server.md §12.2, U1): how a machine takes notes, as the agent's note tools
// see it. Most machines play the engine's MIDI column; machines whose profiled manifest says
// "tracks" (trackers; the Pedal synths until §12.15 gave them MIDI) play their Note track parameter
// only, so their notes go into the clip's engine-played Note columns, a voice per track. Either way the manifest's pitch
// offset is applied on the way in and taken off on the way out: the agent writes and reads the
// pitch heard (A4 = 440 Hz). What the song saves is what the machine gets, as before.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions.ClipActions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;

namespace ReBuzz.Agent
{
    /// <summary>A machine's note input: MIDI or its tracks, and the pitch offset to apply.</summary>
    internal sealed record NoteInput(string Mode, int Offset, IParameter NoteParameter, IParameter VelocityParameter, string Machine)
    {
        public bool Tracks => Mode == "tracks";
        public int Highest => Tracks ? 119 : 127;
    }

    internal static class MachineNotes
    {
        /// <summary>
        /// The machine's note input: <paramref name="requested"/> ("midi" or "tracks") when given,
        /// else what its manifest says, else MIDI. The offset is the manifest's unless <paramref name="applyOffset"/> is false.
        /// </summary>
        public static NoteInput For(MachineCore m, string requested = null, bool applyOffset = true)
        {
            var notes = SongModel.Manifest(m)?.Notes;
            var noteParameter = m.ParameterGroups.Count > 2 ? m.ParameterGroups[2].Parameters.FirstOrDefault(p => p.Type == ParameterType.Note) : null;
            // A machine that plays a sample per note (Neo Sampler's Tracker mode) keeps its notes in its tracks, with their sample, offset and tune.
            bool perNote = noteParameter != null && m.ManagedMachine is Buzz.MachineInterface.INoteSampleProvider { PlaysSamplePerNote: true };
            string mode = requested ?? (perNote || (notes?.Input == "tracks" && noteParameter != null) ? "tracks" : "midi");
            if (mode == "tracks" && noteParameter == null)
                throw new ToolError($"{m.Name} ({m.DLL?.Name}) has no Note track parameter, so it takes notes as MIDI only; leave input out.");
            int offset = applyOffset && notes != null && notes.Pitched ? notes.PitchOffset : 0;
            var velocity = noteParameter == null ? null : m.ParameterGroups[2].Parameters
                .FirstOrDefault(p => p.Type != ParameterType.Note && p.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) && p.MaxValue > p.MinValue);
            return new NoteInput(mode, offset, noteParameter, velocity, m.Name);
        }

        /// <summary>The clip's notes as the agent reads them (pitches as heard): SongNotes reads every lane of the clip.</summary>
        public static List<NoteEvent> Read(IClip p, NoteInput input)
        {
            var stored = Source(p, input)?.Notes().Select(n => new NoteEvent(n.Time, n.Duration, n.Note, n.Velocity, n.Channel, n.Sample, n.Offset, n.Tune, n.Slide, n.Fine)).ToList() ?? new List<NoteEvent>();
            if (input.Offset == 0) return stored;
            return stored.Select(n => n with { Pitch = n.Pitch - input.Offset }).Where(n => n.Pitch >= 0 && n.Pitch <= 127).ToList();
        }

        /// <summary>The note source the input names, from the clip's own machine (null for a clip that is not in a song).</summary>
        static INoteSource Source(IClip p, NoteInput input) =>
            (p?.Machine as MachineCore)?.Graph?.Buzz is ReBuzzCore core ? SongNotes.For(core, p, input.Mode) : null;

        /// <summary>
        /// Replaces notes (as the agent reads them): <paramref name="removed"/> go, <paramref name="added"/> come,
        /// in one undo step with the call, through SongNotes. Track input rewrites the Note columns, raising the track count when a chord needs more voices.
        /// </summary>
        public static void Replace(ReBuzzCore core, IClip p, NoteInput input, IReadOnlyList<NoteEvent> removed, IReadOnlyList<NoteEvent> added, List<string> problems)
        {
            NoteEvent Sent(NoteEvent n) => input.Offset == 0 && n.Pitch <= input.Highest ? n : n with { Pitch = TrackNotes.Sent(n.Pitch, input.Offset, input.Highest, input.Machine) };
            var source = SongNotes.For(core, p, input.Mode) ?? throw new ToolError($"{p.Machine?.Name} takes no notes.");
            if (source is TrackerColumnNotes tracker) tracker.AllowMoreTracks(problems);
            ClipNote Note(NoteEvent n) => new ClipNote(n.Time, n.Duration, n.Pitch, n.Velocity, n.Channel, n.Sample, n.Offset, n.Tune, n.Slide, n.Fine);
            string refused = source.Edit(removed.Select(Sent).Select(Note).ToList(), added.Select(Sent).Select(Note).ToList());
            if (refused != null) throw new ToolError(refused);
        }

        // ---------------------------------------------------------------- the tracker's note columns (CU6b, chords)

        /// <summary>The clip's Notes lane laid out as the tracker lays it out: a note on the lowest column pair free at its time (rows from the lane's Grid, else 4 a beat).</summary>
        public static NoteLanes Lanes(IClip p)
        {
            var events = NoteColumnsAction.FindColumn(p)?.GetEvents(int.MinValue, int.MaxValue).Select(e => new LaneEvent(e.Time, e.Value, e.Duration)) ?? Enumerable.Empty<LaneEvent>();
            int rpb = 4;
            foreach (var c in p.Columns)
                if (c.Metadata.TryGetValue(ClipMeta.Grid, out var grid) && ClipGrid.TryParse(grid, out int g, out _) && g > 0) { rpb = g; break; }
            return new NoteLanes(events, RowGrid.Beats(new[] { rpb }), Math.Max(1, p.Length) * ClipEvent.TimeBase);
        }

        /// <summary>The note+velocity column pairs the tracker shows for a clip: how many it shows, the count stored with the clip (absent: none), how many its notes need.</summary>
        public static JsonObject ColumnsOf(IClip p)
        {
            var lanes = Lanes(p);
            int? stored = NoteColumnsAction.StoredOf(p);
            var o = new JsonObject { ["shown"] = NoteColumns.Shown(stored, lanes.Voices, lanes.NoteCount > 0), ["needed"] = lanes.Voices };
            if (stored is int s) o["stored"] = s;
            return o;
        }

        /// <summary>Gives each note of a MIDI-route read the column pair (from 1) the tracker shows it in. <paramref name="json"/> holds the notes in the order of <paramref name="notes"/>.</summary>
        public static void AddColumns(JsonArray json, IReadOnlyList<NoteEvent> notes, IClip p, NoteInput input)
        {
            if (input.Tracks || json.Count != notes.Count) return;
            var lanes = Lanes(p);
            var at = new Dictionary<(int, int), int>();
            foreach (var c in lanes.Cells)
                if (!c.IsOff) at[(lanes.Events[c.Source].Time, lanes.Events[c.Source].Midi)] = c.Voice + 1;
            for (int i = 0; i < notes.Count; i++)
                if (at.TryGetValue((notes[i].Time, notes[i].Pitch + input.Offset), out int column)) json[i]!.AsObject()["column"] = column;
        }

        /// <summary>
        /// Sets the count of column pairs the tracker shows for the clip, one undo step; refused (with the reason) while it would hide a note.
        /// Null when done.
        /// </summary>
        public static string SetColumns(ReBuzzCore core, IClip p, int count)
        {
            if (count < 1 || count > NoteColumns.Max) return $"columns is 1 to {NoteColumns.Max}.";
            var lanes = Lanes(p);
            if (count < lanes.Voices)
                return $"{lanes.NotesBeyond(count)} note{(lanes.NotesBeyond(count) == 1 ? "" : "s")} would not be shown; keep at least {lanes.Voices} columns.";
            int? stored = NoteColumnsAction.StoredOf(p);
            if (stored == count) return null;
            core.SongCore.ActionStack.Do(new NoteColumnsAction(p, stored, count));
            core.SetModifiedFlag();
            return null;
        }

        /// <summary>Whether the machine plays a sample per note now (its notes then carry a sample, an offset and a tune).</summary>
        public static bool PerNoteSamples(MachineCore m) => m.ManagedMachine is Buzz.MachineInterface.INoteSampleProvider { PlaysSamplePerNote: true };

        /// <summary>Whether a clip column holds a machine's track notes (not automation).</summary>
        public static bool IsNoteColumn(IClipColumn column)
        {
            var parameter = column.Parameter;
            if (parameter == null || parameter.Group.Type != ParameterGroupType.Track) return false;
            if (parameter.Type == ParameterType.Note) return true;
            // The velocity beside the notes, on a machine that takes its notes in tracks.
            return parameter.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) && parameter.Group.Parameters.Any(x => x.Type == ParameterType.Note);
        }

        /// <summary>The input argument's schema text, for the note tools.</summary>
        public const string InputHelp =
            "How the machine gets the notes: midi (the engine's MIDI column) or tracks (its Note track parameter, a voice per track, as " +
            "trackers take them). Default: what its manifest says, else midi";

        public const string OffsetHelp =
            "Apply the machine's pitch offset from its manifest so the written pitch plays (default true); false sends pitches as written";
    }
}
