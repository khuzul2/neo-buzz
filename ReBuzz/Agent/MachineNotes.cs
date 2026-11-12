// Fork (docs/specs/mcp-server.md §12.2, U1): how a machine takes notes, as the agent's note tools
// see it. Most machines play the engine's MIDI column; machines whose profiled manifest says
// "tracks" (trackers; the Pedal synths until §12.15 gave them MIDI) play their Note track parameter
// only, so their notes go into the pattern's engine-played Note columns, a voice per track. Either way the manifest's pitch
// offset is applied on the way in and taken off on the way out: the agent writes and reads the
// pitch heard (A4 = 440 Hz). What the song saves is what the machine gets, as before.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
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
            var notes = SongModel.Manifest(m.DLL)?.Notes;
            var noteParameter = m.ParameterGroups.Count > 2 ? m.ParameterGroups[2].Parameters.FirstOrDefault(p => p.Type == ParameterType.Note) : null;
            string mode = requested ?? (notes?.Input == "tracks" && noteParameter != null ? "tracks" : "midi");
            if (mode == "tracks" && noteParameter == null)
                throw new ToolError($"{m.Name} ({m.DLL?.Name}) has no Note track parameter, so it takes notes as MIDI only; leave input out.");
            int offset = applyOffset && notes != null && notes.Pitched ? notes.PitchOffset : 0;
            var velocity = noteParameter == null ? null : m.ParameterGroups[2].Parameters
                .FirstOrDefault(p => p.Type != ParameterType.Note && p.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) && p.MaxValue > p.MinValue);
            return new NoteInput(mode, offset, noteParameter, velocity, m.Name);
        }

        /// <summary>The pattern's notes as the agent reads them (pitches as heard).</summary>
        public static List<NoteEvent> Read(IPattern p, NoteInput input)
        {
            var stored = input.Tracks ? ReadTracks(p, input) : SongModel.MidiColumnNotes(p);
            if (input.Offset == 0) return stored;
            return stored.Select(n => n with { Pitch = n.Pitch - input.Offset }).Where(n => n.Pitch >= 0 && n.Pitch <= 127).ToList();
        }

        static int PatternTime(IPattern p) => p.Length * NoteList.TimeBase;

        static ColumnKey NoteKey(NoteInput input, int track) => new(2, input.NoteParameter.IndexInGroup, track);

        static ColumnKey VelocityKey(NoteInput input, int track) => new(2, input.VelocityParameter.IndexInGroup, track);

        static List<NoteEvent> ReadTracks(IPattern p, NoteInput input)
        {
            var events = new List<(int Track, int Time, int Value)>();
            var velocities = new Dictionary<(int Track, int Time), int>();
            int tracks = Math.Max(1, p.Machine.TrackCount);
            for (int t = 0; t < tracks; t++)
            {
                foreach (var e in SessionLayout.ColumnFor(p, NoteKey(input, t), create: false)?.GetEvents(int.MinValue, int.MaxValue) ?? Enumerable.Empty<PatternEvent>())
                    events.Add((t, e.Time, e.Value));
                if (input.VelocityParameter != null)
                    foreach (var e in SessionLayout.ColumnFor(p, VelocityKey(input, t), create: false)?.GetEvents(int.MinValue, int.MaxValue) ?? Enumerable.Empty<PatternEvent>())
                        velocities[(t, e.Time)] = e.Value;
            }
            var range = input.VelocityParameter is { } v ? (v.MinValue, v.MaxValue) : ((int, int)?)null;
            return TrackNotes.Read(events, PatternTime(p), NoteList.TimeBase, velocities, range);
        }

        /// <summary>
        /// Replaces notes (as the agent reads them): <paramref name="removed"/> go, <paramref name="added"/> come,
        /// in one undo step with the call. Track input rewrites the Note columns, raising the track count when a chord needs more voices.
        /// </summary>
        public static void Replace(ReBuzzCore core, IPattern p, NoteInput input, IReadOnlyList<NoteEvent> removed, IReadOnlyList<NoteEvent> added, List<string> problems)
        {
            NoteEvent Sent(NoteEvent n) => input.Offset == 0 && n.Pitch <= input.Highest ? n : n with { Pitch = TrackNotes.Sent(n.Pitch, input.Offset, input.Highest, input.Machine) };
            if (!input.Tracks)
            {
                AgentActions.ReplaceNotes(core, p, removed.Select(Sent).ToList(), added.Select(Sent).ToList());
                return;
            }
            var before = Read(p, input);
            var after = before.Except(removed).Concat(added).Select(Sent).ToList();
            after.Sort(NoteList.Order);
            var m = (MachineCore)p.Machine;
            var (tracks, needed) = TrackNotes.Allocate(after);
            int most = Math.Max(m.DLL.Info.MinTracks, m.DLL.Info.MaxTracks);
            if (needed > most)
                throw new ToolError($"{m.Name} ({m.DLL.Name}) plays a note per track and has at most {most} tracks, but these notes need {needed} at once. " +
                                    "Write fewer notes at once (shorter notes, or a chord split across two machines).");
            if (needed > m.TrackCount)
            {
                problems.Add($"{m.Name} plays a note per track: raised its tracks from {m.TrackCount} to {needed} for the chords.");
                AgentActions.Do(core, new SetTrackCountAction(m, needed));
            }
            var range = input.VelocityParameter is { } v ? (v.MinValue, v.MaxValue) : ((int, int)?)null;
            var events = TrackNotes.Events(after, tracks, PatternTime(p), NoteList.TimeBase, range);
            for (int t = 0; t < Math.Max(needed, m.TrackCount); t++)
            {
                var noteKey = NoteKey(input, t);
                var old = SessionLayout.ColumnFor(p, noteKey, create: false)?.GetEvents(int.MinValue, int.MaxValue).ToList() ?? new List<PatternEvent>();
                var put = events.Where(e => e.Track == t).Select(e => new PatternEvent(e.Time, e.Note, 0)).ToList();
                if (old.Count > 0 || put.Count > 0) AgentActions.ReplaceColumnEvents(core, p, noteKey, old, put);
                if (input.VelocityParameter == null) continue;
                var velocityKey = VelocityKey(input, t);
                var oldVelocity = SessionLayout.ColumnFor(p, velocityKey, create: false)?.GetEvents(int.MinValue, int.MaxValue).ToList() ?? new List<PatternEvent>();
                var putVelocity = events.Where(e => e.Track == t && e.Velocity.HasValue).Select(e => new PatternEvent(e.Time, e.Velocity.Value, 0)).ToList();
                if (oldVelocity.Count > 0 || putVelocity.Count > 0) AgentActions.ReplaceColumnEvents(core, p, velocityKey, oldVelocity, putVelocity);
            }
        }

        /// <summary>Whether a pattern column holds a machine's track notes (not automation).</summary>
        public static bool IsNoteColumn(IPatternColumn column)
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
