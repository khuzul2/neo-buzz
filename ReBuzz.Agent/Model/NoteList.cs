#nullable disable
// Agent server (docs/specs/mcp-server.md §5.5): notes as the agent writes and reads them (pitch,
// start and length in beats, velocity) and as a pattern's engine MIDI column holds them (events in
// pattern time, 1/240 tick, whose value is the note-on: 0x90 | note << 8 | velocity << 16).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    /// <summary>A note in pattern time (1/240 tick).</summary>
    public readonly record struct NoteEvent(int Time, int Duration, int Pitch, int Velocity, int Channel = 0)
    {
        public int End => Time + Duration;

        /// <summary>The MIDI column event value: its note-on (channel 0 = MIDI channel 1).</summary>
        public int Value => 0x90 | (Channel & 0x0F) | ((Pitch & 0x7F) << 8) | ((Velocity & 0x7F) << 16);

        /// <summary>The note a MIDI column event is, if it is a note-on.</summary>
        public static bool TryFrom(int time, int value, int duration, out NoteEvent note)
        {
            int status = value & 0xFF, velocity = (value >> 16) & 0x7F;
            if ((status & 0xF0) != 0x90 || velocity == 0)
            {
                note = default;
                return false;
            }
            note = new NoteEvent(time, duration, (value >> 8) & 0x7F, velocity, status & 0x0F);
            return true;
        }
    }

    public static class NoteList
    {
        public const int TimeBase = 240;
        public const int DefaultVelocity = 100;

        /// <summary>Pattern time for a position in beats.</summary>
        public static int Time(double beats, int ticksPerBeat) => (int)Math.Round(beats * ticksPerBeat * TimeBase);

        /// <summary>Beats for a position in pattern time (rounded to a millionth).</summary>
        public static double Beats(int time, int ticksPerBeat) => Math.Round(time / (double)(ticksPerBeat * TimeBase), 6);

        /// <summary>
        /// The notes an agent wrote, checked: each needs a pitch (MIDI number or name), a start
        /// (beats from the pattern start, or "bar.beat" in it) and a length in beats; velocity is
        /// 1–127 (100 when absent). Notes running past the pattern's end are shortened, with a warning.
        /// </summary>
        public static List<NoteEvent> Parse(JsonArray notes, int ticksPerBeat, int beatsPerBar, int patternTicks, List<string> warnings)
        {
            var list = new List<NoteEvent>();
            int patternTime = patternTicks * TimeBase;
            double patternBeats = patternTicks / (double)ticksPerBeat;
            for (int i = 0; i < notes.Count; i++)
            {
                string at = $"notes[{i}]";
                if (notes[i] is not JsonObject n) throw new ToolError($"{at}: each note is an object like {{\"pitch\": \"C4\", \"start\": 0, \"length\": 1, \"velocity\": 100}}.");
                int pitch = ParsePitch(n["pitch"], at + ".pitch");
                if (n["start"] == null) throw new ToolError($"{at}.start: missing (beats from the pattern start, e.g. 0 or 1.5).");
                double start = SongTime.ParseBeats(n["start"], beatsPerBar, at + ".start", "the pattern start");
                if (!Schema.TryNumber(n["length"], out double length) || length <= 0)
                    throw new ToolError($"{at}.length: give the note's length in beats, more than 0 (0.25 = a 16th note at 4 ticks per beat).");
                int velocity = DefaultVelocity;
                if (n["velocity"] != null)
                {
                    if (!Schema.TryNumber(n["velocity"], out double v) || v < 1 || v > 127 || v != Math.Floor(v))
                        throw new ToolError($"{at}.velocity: a whole number from 1 to 127.");
                    velocity = (int)v;
                }
                int time = Time(start, ticksPerBeat);
                if (time >= patternTime)
                    throw new ToolError($"{at}: starts at beat {Fmt(start)}, but the pattern is {Fmt(patternBeats)} beats long. " +
                                        "Make the pattern longer (pattern_create with length_beats) or use another pattern.");
                int duration = Math.Max(1, Time(start + length, ticksPerBeat) - time);
                if (time + duration > patternTime)
                {
                    duration = patternTime - time;
                    warnings.Add($"{at} ({Pitch.Name(pitch)} at beat {Fmt(start)}) ran past the pattern's end; shortened to {Fmt(Beats(duration, ticksPerBeat))} beats.");
                }
                list.Add(new NoteEvent(time, duration, pitch, velocity));
            }
            list.Sort(Order);
            return list;
        }

        public static int ParsePitch(JsonNode value, string what)
        {
            if (Schema.TryNumber(value, out double n))
            {
                if (n != Math.Floor(n) || n < 0 || n > 127) throw new ToolError($"{what}: MIDI pitches are whole numbers 0–127 (60 = C4).");
                return (int)n;
            }
            if (value is JsonValue v && v.TryGetValue(out string s))
            {
                if (Pitch.TryParse(s, out int midi, out string error)) return midi;
                throw new ToolError($"{what}: {error}");
            }
            throw new ToolError($"{what}: missing; give a MIDI number (60) or a name (\"C4\", \"F#3\").");
        }

        public static int Order(NoteEvent a, NoteEvent b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Pitch.CompareTo(b.Pitch);

        /// <summary>Notes from MIDI column events (other events, such as controllers, are skipped).</summary>
        public static List<NoteEvent> FromEvents(IEnumerable<(int Time, int Value, int Duration)> events)
        {
            var list = new List<NoteEvent>();
            foreach (var e in events)
                if (NoteEvent.TryFrom(e.Time, e.Value, e.Duration, out var n)) list.Add(n);
            list.Sort(Order);
            return list;
        }

        /// <summary>A note as the agent reads it back: the same shape notes_write takes, plus its name.</summary>
        public static JsonObject ToJson(NoteEvent n, int ticksPerBeat)
        {
            var o = new JsonObject
            {
                ["pitch"] = n.Pitch,
                ["name"] = Pitch.Name(n.Pitch),
                ["start"] = Beats(n.Time, ticksPerBeat),
                ["length"] = Beats(n.Duration, ticksPerBeat),
                ["velocity"] = n.Velocity,
            };
            if (n.Channel != 0) o["midi_channel"] = n.Channel + 1;   // recorded on another channel
            return o;
        }

        /// <summary>Overlapping notes of the same pitch (they cut each other off on most synths).</summary>
        public static List<string> Clashes(IReadOnlyList<NoteEvent> notes, int ticksPerBeat)
        {
            var clashes = new List<string>();
            foreach (var group in notes.GroupBy(n => n.Pitch))
            {
                var sorted = group.OrderBy(n => n.Time).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    if (sorted[i].Time < sorted[i - 1].End)
                        clashes.Add($"{Pitch.Name(group.Key)} at beat {Fmt(Beats(sorted[i].Time, ticksPerBeat))} starts before the one at beat " +
                                    $"{Fmt(Beats(sorted[i - 1].Time, ticksPerBeat))} ends.");
            }
            return clashes;
        }

        /// <summary>The most notes sounding at once.</summary>
        public static int Polyphony(IReadOnlyList<NoteEvent> notes)
        {
            var edges = notes.SelectMany(n => new[] { (n.Time, 1), (n.End, -1) }).OrderBy(e => e.Item1).ThenBy(e => e.Item2);
            int now = 0, max = 0;
            foreach (var (_, d) in edges) max = Math.Max(max, now += d);
            return max;
        }

        /// <summary>"C4 E4 G4 at 0; A3 at 2 …" for summaries (the first few start times).</summary>
        public static string Summary(IReadOnlyList<NoteEvent> notes, int ticksPerBeat, int maxStarts = 4)
        {
            if (notes.Count == 0) return "no notes";
            var starts = notes.GroupBy(n => n.Time).Take(maxStarts)
                .Select(g => string.Join(" ", g.Select(n => Pitch.Name(n.Pitch))) + " at " + Fmt(Beats(g.Key, ticksPerBeat)));
            int more = notes.Select(n => n.Time).Distinct().Count() - maxStarts;
            int low = notes.Min(n => n.Pitch), high = notes.Max(n => n.Pitch);
            return $"{notes.Count} note{(notes.Count == 1 ? "" : "s")}, {Pitch.Name(low)}–{Pitch.Name(high)}: " + string.Join("; ", starts) + (more > 0 ? $"; … {more} more" : "");
        }

        public static string Fmt(double x) => Math.Round(x, 3).ToString(CultureInfo.InvariantCulture);
    }
}
