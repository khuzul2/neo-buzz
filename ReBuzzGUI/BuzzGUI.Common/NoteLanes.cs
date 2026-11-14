#nullable disable
// Fork addition (neo-buzz, docs/specs/daw-modes.md AR3, seam S7): the notes of a pattern's engine MIDI column as tracker lanes,
// and the edits a tracker makes to them.
//
// The Modern Pattern Editor draws these lanes next to its own columns ("Notes", a voice at a time: Note, Velocity, Delay and
// Cut). They are a view of the column, computed here with TrackerNotes.ToRows, never a second copy of the notes: the editor
// doesn't save them and doesn't play them. An edit in a lane is turned into the notes it changes (removed events, added
// events) and nothing else: every other event of the column is left exactly as it was, down to the tick. That is the rule
// this file keeps (the tracker rounds a note to 1/95 of a row, so rewriting the notes from the rows would round them all).
//
// The tracker's meaning of an edit, for a note on a voice:
//  - a note typed in a cell makes a note from there to the voice's next note (or the pattern's end); a note of the voice
//    still ringing is cut where the new one starts, as a tracker's next note does;
//  - an off ends the ringing note at its row; on a note it removes the note;
//  - clearing a note removes it; clearing an off lets the note ring on to the voice's next note;
//  - a velocity, delay or cut changes that note only (the delay moves the start and keeps the end, the cut moves the end).
//
// Pure: no dependency but TrackerNotes, so ReBuzz.SessionTests links this file. Times are pattern time (1/240 tick).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common
{
    /// <summary>An event of the MIDI column (time, value, duration), as BuzzGUI's PatternEvent holds it; a note-on is 0x90 | note &lt;&lt; 8 | velocity &lt;&lt; 16.</summary>
    public readonly record struct LaneEvent(int Time, int Value, int Duration)
    {
        public bool IsNote => (Value & 0xF0) == 0x90 && ((Value >> 16) & 0x7F) > 0;
        public int Midi => (Value >> 8) & 0x7F;
        public int Velocity => (Value >> 16) & 0x7F;
        public int Channel => Value & 0x0F;
        /// <summary>Where the note ends (a note with no duration lasts a tick).</summary>
        public int End => Time + Math.Max(1, Duration);

        public LaneEvent WithMidi(int midi) => this with { Value = (Value & ~0x7F00) | ((midi & 0x7F) << 8) };
        public LaneEvent WithVelocity(int velocity) => this with { Value = (Value & ~0x7F0000) | ((velocity & 0x7F) << 16) };
        public LaneEvent Ending(int end) => this with { Duration = Math.Max(1, end - Time) };
    }

    /// <summary>
    /// A cell of the lanes: a note (<see cref="Midi"/> 0-119) or the note-off that ends one (-1), on a voice, in a row. Delay and
    /// cut count in 1/95 of the row. <see cref="Source"/> is the note's event (an index in <see cref="NoteLanes.Events"/>); an off
    /// has -1 there and <see cref="Ends"/> is the note it ends.
    /// </summary>
    public readonly record struct NoteCell(int Voice, int Row, int Time, int Length, int Midi, int Velocity, int Delay, int Cut, int Source, int Ends)
    {
        public bool IsOff => Midi < 0;
    }

    /// <summary>What an edit does to the MIDI column: these events go, these come. <see cref="Refused"/> says why it can't be done.</summary>
    public sealed class NoteLaneEdit
    {
        public static readonly NoteLaneEdit Nothing = new NoteLaneEdit(Array.Empty<LaneEvent>(), Array.Empty<LaneEvent>(), null);

        public IReadOnlyList<LaneEvent> Removed { get; }
        public IReadOnlyList<LaneEvent> Added { get; }
        public string Refused { get; }
        public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;

        public NoteLaneEdit(IReadOnlyList<LaneEvent> removed, IReadOnlyList<LaneEvent> added, string refused)
        {
            Removed = removed;
            Added = added;
            Refused = refused;
        }

        public static NoteLaneEdit Refuse(string why) => new NoteLaneEdit(Array.Empty<LaneEvent>(), Array.Empty<LaneEvent>(), why);
    }

    /// <summary>The MIDI column's notes laid out on tracker lanes, and the edits of those lanes.</summary>
    public sealed class NoteLanes
    {
        /// <summary>The highest note the Note column can show (B-7 is MIDI 119; Buzz's range).</summary>
        public const int MaxNote = 119;

        public const int DefaultVelocity = 100;

        readonly RowGrid grid;
        readonly int length;
        readonly List<NoteCell> cells = new List<NoteCell>();
        readonly Dictionary<(int Voice, int Row), NoteCell> byRow = new Dictionary<(int, int), NoteCell>();
        readonly List<List<int>> voiceNotes = new List<List<int>>();     // per voice: its notes' event indexes, in time order

        /// <summary>Every event of the column the lanes were made from (notes or not), as they are.</summary>
        public IReadOnlyList<LaneEvent> Events { get; }

        /// <summary>The cells, voice by voice, in time order.</summary>
        public IReadOnlyList<NoteCell> Cells => cells;

        /// <summary>The voices the notes need (0 when there are no notes).</summary>
        public int Voices { get; }

        /// <summary>How many notes the column holds, and how many of those the lanes don't show (past the pattern's end or above B-7).</summary>
        public int NoteCount { get; }
        public int Hidden { get; }

        /// <param name="events">The MIDI column's events.</param>
        /// <param name="grid">The rows the lanes lie on.</param>
        /// <param name="length">The pattern's length in pattern time.</param>
        public NoteLanes(IEnumerable<LaneEvent> events, RowGrid grid, int length, int maxVoices = 256)
        {
            this.grid = grid;
            this.length = length;
            Events = events.ToList();
            var noteEvents = new List<int>();
            for (int i = 0; i < Events.Count; i++)
                if (Events[i].IsNote) noteEvents.Add(i);
            NoteCount = noteEvents.Count;
            var notes = noteEvents.Select(i => new TrackerNote(Events[i].Time, Math.Max(1, Events[i].Duration), Events[i].Midi, Events[i].Velocity)).ToList();
            var rows = TrackerNotes.ToRowsIndexed(notes, grid, length, maxVoices, out int voices, out _, out var sources, MaxNote);
            Voices = voices;
            for (int v = 0; v < voices; v++) voiceNotes.Add(new List<int>());
            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                int source = sources[k] >= 0 ? noteEvents[sources[k]] : -1;
                int ends = sources[k] < 0 ? noteEvents[~sources[k]] : -1;
                var cell = new NoteCell(r.Track, grid.RowAt(r.Time), r.Time, r.Length, r.Midi, r.Velocity, r.Delay, r.Cut, source, ends);
                cells.Add(cell);
                byRow[(cell.Voice, cell.Row)] = cell;
                if (source >= 0) voiceNotes[cell.Voice].Add(source);
            }
            Hidden = NoteCount - voiceNotes.Sum(l => l.Count);
        }

        /// <summary>The cell of a voice in a row, if it holds one.</summary>
        public bool TryCell(int voice, int row, out NoteCell cell) => byRow.TryGetValue((voice, row), out cell);

        /// <summary>Where a row starts in pattern time.</summary>
        public int RowStart(int row) => grid.Start(row);

        // ------------------------------------------------------------------ edits

        List<int> Notes(int voice) => voice >= 0 && voice < voiceNotes.Count ? voiceNotes[voice] : new List<int>();

        /// <summary>Where a note on <paramref name="voice"/> starting at <paramref name="start"/> ends: at the voice's next note, else the pattern's end.</summary>
        int EndFrom(int voice, int start, int except = -1)
        {
            int end = length;
            foreach (int i in Notes(voice))
                if (i != except && Events[i].Time > start && Events[i].Time < end) end = Events[i].Time;
            return end;
        }

        /// <summary>The voice's note ringing at a time: started before it and ending after it.</summary>
        int Ringing(int voice, int time)
        {
            foreach (int i in Notes(voice))
                if (Events[i].Time < time && Events[i].End > time) return i;
            return -1;
        }

        static NoteLaneEdit Change(LaneEvent from, LaneEvent to) =>
            from == to ? NoteLaneEdit.Nothing : new NoteLaneEdit(new[] { from }, new[] { to }, null);

        /// <summary>A note typed in a cell (a MIDI note 0-119): a new note, or the cell's note with another pitch.</summary>
        public NoteLaneEdit SetNote(int voice, int row, int midi, int velocity = DefaultVelocity)
        {
            if (midi < 0 || midi > MaxNote) return NoteLaneEdit.Refuse("That note is out of range.");
            if (TryCell(voice, row, out var cell) && !cell.IsOff)
                return Change(Events[cell.Source], Events[cell.Source].WithMidi(midi));
            int start = grid.Start(row);
            if (start >= length) return NoteLaneEdit.Refuse("The row is past the end of the pattern.");
            var removed = new List<LaneEvent>();
            var added = new List<LaneEvent>();
            int ringing = Ringing(voice, start);
            if (ringing >= 0)
            {
                removed.Add(Events[ringing]);
                added.Add(Events[ringing].Ending(start));
            }
            added.Add(new LaneEvent(start, 0x90 | ((midi & 0x7F) << 8) | ((Math.Clamp(velocity, 1, 127) & 0x7F) << 16), Math.Max(1, EndFrom(voice, start) - start)));
            return new NoteLaneEdit(removed, added, null);
        }

        /// <summary>A note-off typed in a cell: ends the voice's ringing note there; on a note, removes it.</summary>
        public NoteLaneEdit SetOff(int voice, int row)
        {
            if (TryCell(voice, row, out var cell) && !cell.IsOff)
                return new NoteLaneEdit(new[] { Events[cell.Source] }, Array.Empty<LaneEvent>(), null);
            int start = grid.Start(row);
            int ringing = Ringing(voice, start);
            if (ringing < 0) return NoteLaneEdit.Nothing;
            return Change(Events[ringing], Events[ringing].Ending(start));
        }

        /// <summary>The Note column cleared: removes the cell's note; on an off, lets the note ring on.</summary>
        public NoteLaneEdit Clear(int voice, int row)
        {
            if (!TryCell(voice, row, out var cell)) return NoteLaneEdit.Nothing;
            if (!cell.IsOff)
                return new NoteLaneEdit(new[] { Events[cell.Source] }, Array.Empty<LaneEvent>(), null);
            var note = Events[cell.Ends];
            return Change(note, note.Ending(EndFrom(cell.Voice, note.Time, cell.Ends)));
        }

        /// <summary>A velocity typed for the cell's note (0 clears it to the default).</summary>
        public NoteLaneEdit SetVelocity(int voice, int row, int velocity)
        {
            if (!TryCell(voice, row, out var cell) || cell.IsOff) return NoteLaneEdit.Nothing;
            velocity = velocity <= 0 ? DefaultVelocity : Math.Min(127, velocity);
            return Change(Events[cell.Source], Events[cell.Source].WithVelocity(velocity));
        }

        /// <summary>A delay (1/95 of the row, 0 for none) typed for the cell: a note starts later and ends where it did; an off moves the end of the note it ends.</summary>
        public NoteLaneEdit SetDelay(int voice, int row, int delay)
        {
            if (!TryCell(voice, row, out var cell)) return NoteLaneEdit.Nothing;
            delay = Math.Clamp(delay, 0, TrackerNotes.Fraction);
            if (delay == cell.Delay) return NoteLaneEdit.Nothing;
            int time = cell.Time + TrackerNotes.FromFraction(delay, cell.Length);
            if (cell.IsOff)
            {
                var note = Events[cell.Ends];
                return Change(note, note.Ending(Math.Max(note.Time + 1, time)));
            }
            if (time >= length) return NoteLaneEdit.Refuse("That delay is past the end of the pattern.");
            var e = Events[cell.Source];
            return Change(e, e with { Time = time, Duration = Math.Max(1, e.End - time) });
        }

        /// <summary>A cut (1/95 of the row, 0 for none) typed for the cell's note: it ends there, or with 0 at the voice's next note.</summary>
        public NoteLaneEdit SetCut(int voice, int row, int cut)
        {
            if (!TryCell(voice, row, out var cell) || cell.IsOff) return NoteLaneEdit.Nothing;
            cut = Math.Clamp(cut, 0, TrackerNotes.Fraction);
            if (cut == cell.Cut) return NoteLaneEdit.Nothing;
            var e = Events[cell.Source];
            int end = cut == 0 ? EndFrom(cell.Voice, e.Time, cell.Source) : Math.Min(length, cell.Time + TrackerNotes.FromFraction(cut, cell.Length));
            return Change(e, e.Ending(end));
        }

        /// <summary>The cell's note moved by semitones (kept in 0-119).</summary>
        public NoteLaneEdit Transpose(int voice, int row, int semitones)
        {
            if (!TryCell(voice, row, out var cell) || cell.IsOff) return NoteLaneEdit.Nothing;
            return Change(Events[cell.Source], Events[cell.Source].WithMidi(Math.Clamp(cell.Midi + semitones, 0, MaxNote)));
        }

        /// <summary>The events after an edit: one of each removed event gone (events not there are ignored), the added ones in.</summary>
        public static List<LaneEvent> Apply(IEnumerable<LaneEvent> events, NoteLaneEdit edit)
        {
            var list = events.ToList();
            foreach (var r in edit.Removed)
            {
                int i = list.IndexOf(r);
                if (i >= 0) list.RemoveAt(i);
            }
            list.AddRange(edit.Added);
            return list;
        }
    }
}
