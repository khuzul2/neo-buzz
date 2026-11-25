#nullable disable
// Fork addition (neo-buzz, docs/specs/clips.md §5 and §10, wave CU2): what a clip holds, as data a converter can read and write
// without an engine: lanes (the engine's clip columns, §5.1) with their metadata (§5.2), a migration log with a cause tag per
// step (§5.6), and the little helpers that go with them (the Grid text, note-on and note-off events).
//
// Pure (BuzzGUI.Interfaces' ClipEvent and BuzzNote are all it uses), so the converter, the export and their tests run on every
// platform; the engine side (ClipStore) puts lanes into clip columns and reads them back.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    /// <summary>The metadata keys of a lane (clips.md §5.2). Absent means what it means today.</summary>
    public static class ClipMeta
    {
        /// <summary>The engine's own mark (ClipColumnCore.SessionAutomationKey): the engine plays this parameter column.</summary>
        public const string SessionAutomation = "SessionAutomation";
        /// <summary>Rows per beat of the lane and the beats whose rows differ, "rpb=4;sub=3:6,7:3". View only: never changes a time.</summary>
        public const string Grid = "Grid";
        /// <summary>"1": the tracker's mute of the column.</summary>
        public const string Mute = "Mute";
    }

    /// <summary>The cause tags of the migration log (clips.md §5.6): what opening a .nbl of format 1 or 2 changed or dropped.</summary>
    public static class ClipTags
    {
        // Changes of sound or of data that the converter makes.
        public const string MidiDelay = "midi-delay";               // a MIDI lane note with a delay: the engine honours it at 1/240 tick (the editor fired on the tick)
        public const string MidiCut = "midi-cut";                   // a MIDI lane note with a cut
        public const string MidiTick = "midi-tick";                 // a MIDI lane note: the engine plays it at its row, the editor one tick later
        public const string BlockShift = "block-shift";             // a parameter lane: the engine sets it before the tick, the editor after it
        public const string Subtick = "subtick";                    // an event between ticks: the engine places it at its sub-tick
        public const string NoteFixed = "note-fixed";               // a Note value the editor could not read, read as a MIDI note as the editor did
        public const string CcLaneConverted = "cc-lane-converted";  // a Pitch Wheel or CC lane on a track with no note lane: now controller events
        // Dropped (clips.md §7.1, D2 B).
        public const string PxpSpDropped = "pxp-sp-dropped";        // PatternXP's own columns: trigger, effect, effect data (sub-clip triggers)
        public const string InputColumn = "input-column";           // a built-in clip's connection amp and pan
        public const string CcLaneDropped = "cc-lane-dropped";      // a Pitch Wheel or CC lane on a track with a note lane (it throws in the editor's render)
        public const string ForeignDropped = "foreign-dropped";     // an editor's data that is not in a format we read
        public const string MachineMissing = "machine-missing";     // a column of a machine the song does not have
        public const string ParamMissing = "param-missing";         // a column of a parameter the machine does not have
        public const string TrackDropped = "track-dropped";         // a column of a track the machine no longer has
        public const string MidiLaneOrphan = "midi-lane-orphan";    // a velocity, delay or cut lane with no note lane on its track
        public const string NoteInvalid = "note-invalid";           // a MIDI lane Note the editor could not turn into a MIDI note
        public const string ClipMissing = "pattern-missing";     // data of a clip the machine does not have
        public const string EventPastEnd = "event-past-end";        // an event after the end of its clip (the editor drops these as it loads them)
        // The note-off events (clips.md §5.4).
        public const string OffExplicit = "off-explicit";           // an off with no note before it in the clip: an explicit note-off event on the Notes lane
        public const string OffNoteUnknown = "off-note-unknown";    // ... and the note it ends is not known (no ringing note before the clip): note 0

        /// <summary>The tags that name something dropped or lost (the rest explain a change or say what was done).</summary>
        public static readonly IReadOnlyCollection<string> Losses = new HashSet<string>
        {
            PxpSpDropped, InputColumn, CcLaneDropped, ForeignDropped, MachineMissing, ParamMissing, TrackDropped, MidiLaneOrphan,
            NoteInvalid, ClipMissing, EventPastEnd,
        };
    }

    /// <summary>What a log line says: a loss (something dropped), a change (the sound or the data moves), or information.</summary>
    public enum ClipLogKind { Loss, Change, Info }

    /// <summary>One step of a migration: a cause tag, what it touched and how many events.</summary>
    public sealed record ClipLogEntry(string Tag, ClipLogKind Kind, string Machine, string Clip, string Lane, int Count, string Detail);

    /// <summary>The migration log (clips.md §5.6): one entry per step, each with a cause tag. CU3's change report reads the tags.</summary>
    public sealed class ClipLog
    {
        readonly List<ClipLogEntry> entries = new List<ClipLogEntry>();

        public IReadOnlyList<ClipLogEntry> Entries => entries;

        public void Add(string tag, ClipLogKind kind, string machine, string clip, string lane, int count, string detail = null)
        {
            if (count <= 0 && kind != ClipLogKind.Loss) return;
            entries.Add(new ClipLogEntry(tag, kind, machine, clip, lane, count, detail));
        }

        public void Loss(string tag, string machine, string clip, string lane, int count, string detail = null) => Add(tag, ClipLogKind.Loss, machine, clip, lane, count, detail);
        public void Change(string tag, string machine, string clip, string lane, int count, string detail = null) => Add(tag, ClipLogKind.Change, machine, clip, lane, count, detail);
        public void Info(string tag, string machine, string clip, string lane, int count, string detail = null) => Add(tag, ClipLogKind.Info, machine, clip, lane, count, detail);

        public void AddRange(ClipLog other) => entries.AddRange(other.entries);

        /// <summary>Tag to the number of entries and the events they cover.</summary>
        public SortedDictionary<string, (int Entries, int Events)> ByTag()
        {
            var d = new SortedDictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                d.TryGetValue(e.Tag, out var v);
                d[e.Tag] = (v.Item1 + 1, v.Item2 + e.Count);
            }
            return d;
        }

        public override string ToString() => string.Join("\n", entries.Select(e =>
            $"{e.Kind.ToString().ToLowerInvariant(),-6} {e.Tag,-18} {e.Machine}/{e.Clip}/{e.Lane}: {e.Count}" + (e.Detail != null ? $" ({e.Detail})" : "")));
    }

    public enum ClipLaneKind { Notes, Parameter }

    /// <summary>
    /// One lane of a clip: the Notes lane of a machine (its MIDI column), or one parameter of one machine on one track
    /// (global group 1, track group 2), with its events and metadata (clips.md §5.1).
    /// </summary>
    public sealed class ClipLane
    {
        public ClipLaneKind Kind;
        public string Machine;
        public int Group;                       // 1 global, 2 track (Parameter lanes)
        public int Index;                       // the parameter in its group
        public int Track;
        public List<ClipEvent> Events = new List<ClipEvent>();
        public Dictionary<string, string> Meta = new Dictionary<string, string>();

        public bool Auto => Meta.ContainsKey(ClipMeta.SessionAutomation);

        public string Grid
        {
            get => Meta.TryGetValue(ClipMeta.Grid, out var g) ? g : null;
            set { if (value == null) Meta.Remove(ClipMeta.Grid); else Meta[ClipMeta.Grid] = value; }
        }

        public bool Mute
        {
            get => Meta.TryGetValue(ClipMeta.Mute, out var m) && m == "1";
            set { if (value) Meta[ClipMeta.Mute] = "1"; else Meta.Remove(ClipMeta.Mute); }
        }

        /// <summary>What names the lane in its clip: a column is the same lane when the key is the same.</summary>
        public string Key => (Kind == ClipLaneKind.Notes ? $"{Machine}|notes" : $"{Machine}|{Group}.{Index}.{Track}") + (Auto ? "|auto" : "");

        public ClipLane Clone() => new ClipLane { Kind = Kind, Machine = Machine, Group = Group, Index = Index, Track = Track, Events = new List<ClipEvent>(Events), Meta = new Dictionary<string, string>(Meta) };

        /// <summary>The events in a fixed order, for comparing lanes (the order of events in a column is not data).</summary>
        public List<ClipEvent> Sorted() => Events.OrderBy(e => e.Time).ThenBy(e => e.Value).ThenBy(e => e.Duration).ToList();

        public override string ToString() => $"{Key} {Events.Count} events";
    }

    /// <summary>A clip: its name, its length in ticks and its lanes (clips.md §1.1).</summary>
    public sealed class ClipContent
    {
        public string Name;
        public int Length;                      // in ticks (the clip's Length)
        public int RowsPerBeat = 4;
        public List<ClipLane> Lanes = new List<ClipLane>();

        public int Beats => Math.Max(1, (Math.Max(1, Length) + 3) / 4);

        /// <summary>A hash of everything the store holds of the clip (lanes by key; events, metadata): the stamp of a derived blob (clips.md §7.2).</summary>
        public byte[] Hash()
        {
            using var sha = SHA256.Create();
            var sb = new StringBuilder();
            sb.Append(Name).Append('\n').Append(Length).Append('\n');
            foreach (var l in Lanes.OrderBy(l => l.Key, StringComparer.Ordinal))
            {
                sb.Append(l.Key).Append('\n');
                foreach (var m in l.Meta.OrderBy(m => m.Key, StringComparer.Ordinal))
                    sb.Append(m.Key).Append('=').Append(m.Value).Append(';');
                sb.Append('\n');
                foreach (var e in l.Sorted())
                    sb.Append(e.Time.ToString(CultureInfo.InvariantCulture)).Append(',').Append(e.Value.ToString(CultureInfo.InvariantCulture)).Append(',').Append(e.Duration.ToString(CultureInfo.InvariantCulture)).Append(';');
                sb.Append('\n');
            }
            return sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public static byte[] Hash(IEnumerable<ClipContent> clips)
        {
            using var sha = SHA256.Create();
            var all = new List<byte>();
            foreach (var c in clips.OrderBy(c => c.Name, StringComparer.Ordinal))
                all.AddRange(c.Hash());
            return sha.ComputeHash(all.ToArray());
        }
    }

    /// <summary>What the converter needs to know of a machine: how many parameters it has and which are notes. The engine fills it from a machine.</summary>
    public sealed class ClipMachine
    {
        public string Name;
        public int GlobalCount;
        public int TrackParamCount;
        public int TrackCount;
        public HashSet<int> NoteGlobal = new HashSet<int>();
        public HashSet<int> NoteTrack = new HashSet<int>();

        public bool IsNote(int group, int index) => (group == 1 ? NoteGlobal : NoteTrack).Contains(index);
    }

    public interface IClipMachines
    {
        /// <summary>The machine of that name, or null.</summary>
        ClipMachine Find(string name);
    }

    public sealed class ClipMachineSet : IClipMachines
    {
        readonly Dictionary<string, ClipMachine> machines = new Dictionary<string, ClipMachine>();

        public ClipMachineSet Add(ClipMachine machine) { machines[machine.Name] = machine; return this; }

        public ClipMachine Find(string name) => name != null && machines.TryGetValue(name, out var m) ? m : null;
    }

    /// <summary>The Grid metadata text, "rpb=4;sub=3:6,7:3": rows per beat, and the beats (from 0) whose rows differ (clips.md §5.2).</summary>
    public static class ClipGrid
    {
        public static string Format(int rowsPerBeat, IReadOnlyList<int> beatRows)
        {
            var sb = new StringBuilder("rpb=").Append(rowsPerBeat.ToString(CultureInfo.InvariantCulture));
            if (beatRows != null)
            {
                var subs = new List<string>();
                for (int b = 0; b < beatRows.Count; b++)
                    if (beatRows[b] != rowsPerBeat)
                        subs.Add(b.ToString(CultureInfo.InvariantCulture) + ":" + beatRows[b].ToString(CultureInfo.InvariantCulture));
                if (subs.Count > 0)
                    sb.Append(";sub=").Append(string.Join(",", subs));
            }
            return sb.ToString();
        }

        /// <summary>False if the text is not a grid. <paramref name="sub"/>: beat to rows.</summary>
        public static bool TryParse(string text, out int rowsPerBeat, out Dictionary<int, int> sub)
        {
            rowsPerBeat = 0;
            sub = new Dictionary<int, int>();
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var part in text.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) return false;
                if (kv[0] == "rpb")
                {
                    if (!int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rowsPerBeat) || rowsPerBeat < 1) return false;
                }
                else if (kv[0] == "sub")
                {
                    foreach (var pair in kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var bv = pair.Split(':');
                        if (bv.Length != 2 || !int.TryParse(bv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)
                            || !int.TryParse(bv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) || b < 0 || r < 1) return false;
                        sub[b] = r;
                    }
                }
            }
            return rowsPerBeat >= 1;
        }

        /// <summary>The rows in each of <paramref name="beats"/> beats; the lane's grid, else <paramref name="fallback"/> rows in each.</summary>
        public static List<int> BeatRows(string grid, int beats, int fallback)
        {
            bool ok = TryParse(grid, out int rpb, out var sub);
            if (!ok) rpb = Math.Max(1, fallback);
            var list = new List<int>(beats);
            for (int b = 0; b < beats; b++)
                list.Add(ok && sub.TryGetValue(b, out int r) ? r : rpb);
            return list;
        }
    }

    /// <summary>Events of the Notes lane (the engine's MIDI column, clips.md §5.1): a note-on carries its length, an explicit note-off has duration 0 (§5.4).</summary>
    public static class ClipEvents
    {
        public static int NoteOn(int note, int velocity, int channel = 0) => 0x90 | (channel & 0x0F) | ((note & 0x7F) << 8) | ((Math.Clamp(velocity, 1, 127) & 0x7F) << 16);

        /// <summary>A note-off message, as the session's own (velocity 0).</summary>
        public static int NoteOff(int note, int channel = 0) => 0x80 | (channel & 0x0F) | ((note & 0x7F) << 8);

        /// <summary>An explicit note-off event: duration 0, nothing before it in the clip to end.</summary>
        public static ClipEvent Off(int time, int note, int channel = 0) => new ClipEvent(time, NoteOff(note, channel), 0);

        public static bool IsExplicitOff(ClipEvent e) => (e.Value & 0xF0) == 0x80 && e.Duration == 0;

        public static bool IsNoteOn(ClipEvent e) => (e.Value & 0xF0) == 0x90 && ((e.Value >> 16) & 0x7F) > 0;

        /// <summary>A controller event: a pitch wheel (14 bits) or a control change.</summary>
        public static int PitchWheel(int value14, int channel = 0) => 0xE0 | (channel & 0x0F) | ((value14 & 0x7F) << 8) | (((value14 >> 7) & 0x7F) << 16);

        public static int ControlChange(int controller, int value, int channel = 0) => 0xB0 | (channel & 0x0F) | ((controller & 0x7F) << 8) | ((value & 0x7F) << 16);
    }
}
