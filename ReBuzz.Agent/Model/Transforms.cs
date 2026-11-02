// Agent server (docs/specs/mcp-server.md §5.5, phase A3): note transforms for notes_transform.
// Transpose (semitones or scale steps), quantize (grid, strength, swing), humanize (seeded), velocity
// (set, scale, add, ramp, accent pattern), shift, stretch, reverse, invert, legato and staccato, each on
// a selection by time range and pitch range. Notes stay inside the pattern and MIDI's range, and every
// clip is reported. Pure: notes are NoteEvents in pattern time (240 per tick); the ReBuzz glue reads
// and writes the pattern and makes the call one undo step.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.Model
{
    /// <summary>The pattern a transform works in, and the problems it reports (clipped notes, velocities, …).</summary>
    public sealed class TransformContext
    {
        public TransformContext(int ticksPerBeat, int patternTicks)
        {
            if (ticksPerBeat < 1) throw new ArgumentOutOfRangeException(nameof(ticksPerBeat));
            if (patternTicks < 1) throw new ArgumentOutOfRangeException(nameof(patternTicks));
            TicksPerBeat = ticksPerBeat;
            PatternTicks = patternTicks;
        }

        public int TicksPerBeat { get; }
        public int PatternTicks { get; }
        /// <summary>The pattern's end in pattern time (1/240 tick).</summary>
        public int PatternTime => PatternTicks * NoteList.TimeBase;
        public double TimePerBeat => TicksPerBeat * (double)NoteList.TimeBase;
        public List<string> Problems { get; } = new();

        public int Time(double beats) => (int)Math.Round(beats * TimePerBeat);
        public double Beats(int time) => NoteList.Beats(time, TicksPerBeat);
    }

    /// <summary>Which notes a transform changes: those starting in [From, To) (pattern time) with pitches in [Low, High].</summary>
    public sealed record NoteSelection(int? From = null, int? To = null, int? Low = null, int? High = null)
    {
        public static readonly NoteSelection All = new();

        public bool Contains(NoteEvent n) =>
            (From == null || n.Time >= From) && (To == null || n.Time < To) && (Low == null || n.Pitch >= Low) && (High == null || n.Pitch <= High);

        public bool IsAll => From == null && To == null && Low == null && High == null;

        public string Describe(TransformContext ctx)
        {
            var parts = new List<string>();
            if (From != null || To != null)
                parts.Add($"starting at beats {NoteList.Fmt(ctx.Beats(From ?? 0))}–{NoteList.Fmt(ctx.Beats(To ?? ctx.PatternTime))}");
            if (Low != null || High != null)
                parts.Add($"with pitches {Pitch.Name(Low ?? 0)}–{Pitch.Name(High ?? 127)}");
            return parts.Count == 0 ? "all notes" : "notes " + string.Join(" ", parts);
        }
    }

    /// <summary>The transforms, one call each. Every one returns the pattern's notes, sorted, with the selection changed.</summary>
    public static class Transforms
    {
        internal sealed class Item
        {
            public Item(NoteEvent note, bool selected)
            {
                Note = note;
                Original = note;
                Selected = selected;
            }

            public NoteEvent Note;
            public readonly NoteEvent Original;
            public bool Selected;
            public bool Dropped;
        }

        public static List<NoteEvent> Transpose(IReadOnlyList<NoteEvent> notes, TransformContext ctx, int semitones, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "transpose", (items, _) => TransposeOp(items, semitones));

        /// <summary>Moves notes by steps of the key's scale; notes outside it keep their distance above the scale note below.</summary>
        public static List<NoteEvent> TransposeInKey(IReadOnlyList<NoteEvent> notes, TransformContext ctx, MusicKey key, int steps, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "transpose", (items, _) => TransposeInKeyOp(items, ctx, key, steps));

        /// <summary>
        /// Moves starts (and optionally ends) toward the grid by <paramref name="strength"/> (0–1). Swing (0–1) delays the
        /// off-beat grid lines; 1 puts them at the triplet position, 2/3 of the way through each pair.
        /// </summary>
        public static List<NoteEvent> Quantize(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double gridBeats, double strength = 1, double swing = 0,
                                               bool starts = true, bool ends = false, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "quantize", (items, _) => QuantizeOp(items, ctx, gridBeats, strength, swing, starts, ends));

        /// <summary>Random timing (± beats) and velocity (±) offsets; the same seed gives the same result.</summary>
        public static List<NoteEvent> Humanize(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double timingBeats, int velocity, int seed = 1, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "humanize", (items, _) => HumanizeOp(items, ctx, timingBeats, velocity, seed));

        public static List<NoteEvent> SetVelocity(IReadOnlyList<NoteEvent> notes, TransformContext ctx, int value, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "velocity", (items, _) => VelocityOp(items, v => value));

        public static List<NoteEvent> ScaleVelocity(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double factor, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "velocity", (items, _) => VelocityOp(items, v => (int)Math.Round(v * factor)));

        public static List<NoteEvent> AddVelocity(IReadOnlyList<NoteEvent> notes, TransformContext ctx, int amount, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "velocity", (items, _) => VelocityOp(items, v => v + amount));

        /// <summary>A straight line from <paramref name="from"/> at the first selected start to <paramref name="to"/> at the last.</summary>
        public static List<NoteEvent> RampVelocity(IReadOnlyList<NoteEvent> notes, TransformContext ctx, int from, int to, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "velocity", (items, _) => RampOp(items, from, to));

        /// <summary>Adds <paramref name="amount"/> to notes on the pattern's 'x' steps ("x..x..x." on a grid in beats, repeating).</summary>
        public static List<NoteEvent> Accent(IReadOnlyList<NoteEvent> notes, TransformContext ctx, string pattern, double gridBeats, int amount, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "velocity", (items, _) => AccentOp(items, ctx, AccentSteps(pattern, "accent"), gridBeats, amount));

        public static List<NoteEvent> Shift(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double beats, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "shift", (items, _) => ShiftOp(items, ctx.Time(beats)));

        /// <summary>Scales starts (around the anchor; default the selection's start) and lengths by <paramref name="factor"/>.</summary>
        public static List<NoteEvent> Stretch(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double factor, double? anchorBeats = null, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "stretch", (items, sel) => StretchOp(items, sel, factor, anchorBeats.HasValue ? ctx.Time(anchorBeats.Value) : null));

        /// <summary>Mirrors notes in time within the selection's time range (the whole pattern without one).</summary>
        public static List<NoteEvent> Reverse(IReadOnlyList<NoteEvent> notes, TransformContext ctx, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "reverse", (items, sel) => ReverseOp(items, ctx, sel));

        /// <summary>Mirrors pitches around <paramref name="axis"/> (default: the first selected note's pitch).</summary>
        public static List<NoteEvent> Invert(IReadOnlyList<NoteEvent> notes, TransformContext ctx, int? axis = null, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "invert", (items, _) => InvertOp(items, axis));

        /// <summary>Extends each selected note to the next selected start (less <paramref name="gapBeats"/>); the last ones keep their length.</summary>
        public static List<NoteEvent> Legato(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double gapBeats = 0, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "legato", (items, _) => LegatoOp(items, ctx.Time(gapBeats)));

        /// <summary>Shortens notes by <paramref name="factor"/> (0–1), or to at most <paramref name="lengthBeats"/>.</summary>
        public static List<NoteEvent> Staccato(IReadOnlyList<NoteEvent> notes, TransformContext ctx, double factor = 0.5, double? lengthBeats = null, NoteSelection? selection = null) =>
            One(notes, ctx, selection, "staccato", (items, _) => StaccatoOp(items, factor, lengthBeats.HasValue ? ctx.Time(lengthBeats.Value) : null));

        // ---- the engine ----

        internal static List<Item> Items(IEnumerable<NoteEvent> notes, NoteSelection selection)
        {
            var sorted = notes.ToList();
            sorted.Sort(NoteList.Order);
            return sorted.Select(n => new Item(n, selection.Contains(n))).ToList();
        }

        internal static List<NoteEvent> Collect(IEnumerable<Item> items)
        {
            var list = items.Where(i => !i.Dropped).Select(i => i.Note).ToList();
            list.Sort(NoteList.Order);
            return list;
        }

        static List<NoteEvent> One(IReadOnlyList<NoteEvent> notes, TransformContext ctx, NoteSelection? selection, string name, Action<List<Item>, NoteSelection> op)
        {
            var sel = selection ?? NoteSelection.All;
            var items = Items(notes, sel);
            op(items, sel);
            Clip(items, ctx, name);
            return Collect(items);
        }

        internal static IEnumerable<Item> Chosen(List<Item> items) => items.Where(i => i.Selected && !i.Dropped);

        internal static void TransposeOp(List<Item> items, int semitones)
        {
            foreach (var i in Chosen(items)) i.Note = i.Note with { Pitch = i.Note.Pitch + semitones };
        }

        internal static void TransposeInKeyOp(List<Item> items, TransformContext ctx, MusicKey key, int steps)
        {
            var outside = new List<string>();
            foreach (var i in Chosen(items))
            {
                int p = key.Step(i.Note.Pitch, steps, out bool chromatic);
                if (chromatic) outside.Add($"{key.NameOf(i.Note.Pitch)} at beat {NoteList.Fmt(ctx.Beats(i.Note.Time))}");
                i.Note = i.Note with { Pitch = p };
            }
            if (outside.Count > 0)
                ctx.Problems.Add($"transpose: {Count(outside.Count, "note")} weren't in {key} ({Some(outside)}); each kept its distance above the scale note below it.");
        }

        internal static void QuantizeOp(List<Item> items, TransformContext ctx, double gridBeats, double strength, double swing, bool starts, bool ends)
        {
            double g = gridBeats * ctx.TimePerBeat;
            foreach (var i in Chosen(items))
            {
                double s = i.Note.Time, e = i.Note.End;
                double ns = starts ? s + strength * (Nearest(s, g, swing) - s) : s;
                double ne = ends ? e + strength * (Nearest(e, g, swing) - e) : ns + (e - s);
                int ts = (int)Math.Round(ns), te = (int)Math.Round(ne);
                if (te <= ts) te = ts + Math.Max(1, (int)Math.Round(g));
                i.Note = i.Note with { Time = ts, Duration = te - ts };
            }
        }

        /// <summary>The nearest grid line to <paramref name="t"/>; odd lines are delayed by swing × grid / 3.</summary>
        internal static double Nearest(double t, double grid, double swing)
        {
            double Line(long k) => k * grid + ((k & 1) == 1 ? swing * grid / 3 : 0);
            long k0 = (long)Math.Floor(t / grid);
            double best = Line(k0);
            for (long k = k0 - 1; k <= k0 + 2; k++)
                if (Math.Abs(Line(k) - t) < Math.Abs(best - t)) best = Line(k);
            return best;
        }

        internal static void HumanizeOp(List<Item> items, TransformContext ctx, double timingBeats, int velocity, int seed)
        {
            var random = new Random(seed);
            double range = timingBeats * ctx.TimePerBeat;
            foreach (var i in Chosen(items))
            {
                double dt = (random.NextDouble() * 2 - 1) * range;
                int dv = random.Next(-velocity, velocity + 1);
                int t = Math.Clamp((int)Math.Round(i.Note.Time + dt), 0, Math.Max(0, ctx.PatternTime - 1));
                i.Note = i.Note with { Time = t, Velocity = i.Note.Velocity + dv };
            }
        }

        internal static void VelocityOp(List<Item> items, Func<int, int> change)
        {
            foreach (var i in Chosen(items)) i.Note = i.Note with { Velocity = change(i.Note.Velocity) };
        }

        internal static void RampOp(List<Item> items, int from, int to)
        {
            var chosen = Chosen(items).ToList();
            if (chosen.Count == 0) return;
            int t0 = chosen.Min(i => i.Note.Time), t1 = chosen.Max(i => i.Note.Time);
            foreach (var i in chosen)
            {
                double f = t1 == t0 ? 0 : (i.Note.Time - t0) / (double)(t1 - t0);
                i.Note = i.Note with { Velocity = (int)Math.Round(from + (to - from) * f) };
            }
        }

        internal static bool[] AccentSteps(string pattern, string what)
        {
            if (string.IsNullOrEmpty(pattern)) throw new ToolError($"{what}: give a pattern of x (accent) and . (none), one per grid step, e.g. \"x..x..x.\".");
            return pattern.Select(c => c switch
            {
                'x' or 'X' or '>' or '!' => true,
                '.' or '-' or '_' or 'o' => false,
                _ => throw new ToolError($"{what}: \"{pattern}\" may only hold x (accent) and . (none), one per grid step, e.g. \"x..x..x.\"."),
            }).ToArray();
        }

        internal static void AccentOp(List<Item> items, TransformContext ctx, bool[] steps, double gridBeats, int amount)
        {
            double g = gridBeats * ctx.TimePerBeat;
            foreach (var i in Chosen(items))
            {
                long step = (long)Math.Round(i.Note.Time / g);
                if (steps[(int)(step % steps.Length)]) i.Note = i.Note with { Velocity = i.Note.Velocity + amount };
            }
        }

        internal static void ShiftOp(List<Item> items, int time)
        {
            foreach (var i in Chosen(items)) i.Note = i.Note with { Time = i.Note.Time + time };
        }

        internal static void StretchOp(List<Item> items, NoteSelection sel, double factor, int? anchor)
        {
            var chosen = Chosen(items).ToList();
            if (chosen.Count == 0) return;
            int a = anchor ?? sel.From ?? chosen.Min(i => i.Note.Time);
            foreach (var i in chosen)
                i.Note = i.Note with
                {
                    Time = a + (int)Math.Round((i.Note.Time - a) * factor),
                    Duration = Math.Max(1, (int)Math.Round(i.Note.Duration * factor)),
                };
        }

        internal static void ReverseOp(List<Item> items, TransformContext ctx, NoteSelection sel)
        {
            int r0 = sel.From ?? 0, r1 = sel.To ?? ctx.PatternTime;
            foreach (var i in Chosen(items)) i.Note = i.Note with { Time = r0 + r1 - i.Note.End };
        }

        internal static void InvertOp(List<Item> items, int? axis)
        {
            var chosen = Chosen(items).ToList();
            if (chosen.Count == 0) return;
            int a = axis ?? chosen[0].Note.Pitch;
            foreach (var i in chosen) i.Note = i.Note with { Pitch = 2 * a - i.Note.Pitch };
        }

        internal static void LegatoOp(List<Item> items, int gap)
        {
            var chosen = Chosen(items).ToList();
            var starts = chosen.Select(i => i.Note.Time).Distinct().OrderBy(t => t).ToList();
            foreach (var i in chosen)
            {
                int k = starts.BinarySearch(i.Note.Time);
                if (k < 0 || k + 1 >= starts.Count) continue;
                i.Note = i.Note with { Duration = Math.Max(1, starts[k + 1] - i.Note.Time - gap) };
            }
        }

        internal static void StaccatoOp(List<Item> items, double factor, int? length)
        {
            foreach (var i in Chosen(items))
            {
                int d = length.HasValue ? Math.Min(i.Note.Duration, Math.Max(1, length.Value)) : Math.Max(1, (int)Math.Round(i.Note.Duration * factor));
                i.Note = i.Note with { Duration = d };
            }
        }

        /// <summary>
        /// Brings every note back inside MIDI's range (by octaves) and the pattern (trimmed, or removed when wholly outside),
        /// and velocities into 1–127; each kind of clip becomes one problem line naming the notes.
        /// </summary>
        internal static void Clip(List<Item> items, TransformContext ctx, string step)
        {
            var folded = new List<string>();
            var removed = new List<string>();
            var early = new List<string>();
            var late = new List<string>();
            int loud = 0, soft = 0;
            int end = ctx.PatternTime;
            foreach (var i in items)
            {
                if (i.Dropped) continue;
                var n = i.Note;
                int p = n.Pitch;
                while (p > 127) p -= 12;
                while (p < 0) p += 12;
                if (p != n.Pitch) folded.Add($"{Pitch.Name(n.Pitch)}→{Pitch.Name(p)}");
                int t = n.Time, d = Math.Max(1, n.Duration);
                string where = $"{Pitch.Name(p)} at beat {NoteList.Fmt(ctx.Beats(t))}";
                if (t >= end || t + d <= 0)
                {
                    i.Dropped = true;
                    removed.Add(where);
                    continue;
                }
                if (t < 0) { d += t; t = 0; early.Add(where); }
                if (t + d > end) { d = end - t; late.Add(where); }
                int v = n.Velocity;
                if (v > 127) { v = 127; loud++; }
                if (v < 1) { v = 1; soft++; }
                i.Note = n with { Pitch = p, Time = t, Duration = d, Velocity = v };
            }
            string beats = NoteList.Fmt(ctx.Beats(end));
            if (folded.Count > 0) ctx.Problems.Add($"{step}: {Count(folded.Count, "note")} left MIDI's range (C-1–G9, 0–127) and moved by octaves: {Some(folded)}.");
            if (removed.Count > 0) ctx.Problems.Add($"{step}: {Count(removed.Count, "note")} ended up outside the pattern (beats 0–{beats}) and {Was(removed.Count)} removed: {Some(removed)}. undo reverts the call.");
            if (early.Count > 0) ctx.Problems.Add($"{step}: {Count(early.Count, "note")} started before the pattern and {Was(early.Count)} cut to start at beat 0: {Some(early)}.");
            if (late.Count > 0) ctx.Problems.Add($"{step}: {Count(late.Count, "note")} ran past the pattern's end (beat {beats}) and {Was(late.Count)} shortened: {Some(late)}.");
            if (loud > 0) ctx.Problems.Add($"{step}: {Count(loud, "velocity", "velocities")} went above 127 and {Was(loud)} limited to 127.");
            if (soft > 0) ctx.Problems.Add($"{step}: {Count(soft, "velocity", "velocities")} went below 1 and {Was(soft)} raised to 1.");
        }

        static string Was(int n) => n == 1 ? "was" : "were";

        internal static string Count(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

        internal static string Some(List<string> items, int max = 4) =>
            string.Join(", ", items.Take(max)) + (items.Count > max ? $" and {items.Count - max} more" : "");
    }

    /// <summary>What a notes_transform call did: the pattern's notes after it, and what to tell the agent.</summary>
    public sealed class TransformOutcome
    {
        public required List<NoteEvent> Notes { get; init; }
        public required List<string> Problems { get; init; }
        /// <summary>One line per transform: "transpose +12 semitones (8 notes)".</summary>
        public required List<string> Steps { get; init; }
        /// <summary>Notes the call's selection picked.</summary>
        public int Selected { get; init; }
        /// <summary>Notes that differ from before (removed ones not counted).</summary>
        public int Changed { get; init; }
        /// <summary>Notes removed because they ended up outside the pattern.</summary>
        public int Removed { get; init; }
    }

    /// <summary>
    /// notes_transform: its schema, its arguments and the transform engine. The ReBuzz glue reads the pattern's notes, calls
    /// <see cref="Run"/> (or <see cref="Apply"/>), writes the result as one undo step and answers with <see cref="Result"/>.
    /// </summary>
    public static class NotesTransform
    {
        public const string Name = "notes_transform";

        public static readonly string[] Ops = { "transpose", "quantize", "humanize", "velocity", "shift", "stretch", "reverse", "invert", "legato", "staccato" };

        static readonly Dictionary<string, string[]> Takes = new()
        {
            ["transpose"] = new[] { "semitones", "scale_steps", "key" },
            ["quantize"] = new[] { "grid", "strength", "swing", "starts", "ends" },
            ["humanize"] = new[] { "timing", "velocity", "seed" },
            ["velocity"] = new[] { "set", "scale", "add", "ramp", "accent", "grid", "amount" },
            ["shift"] = new[] { "beats" },
            ["stretch"] = new[] { "factor", "anchor" },
            ["reverse"] = new string[0],
            ["invert"] = new[] { "axis" },
            ["legato"] = new[] { "gap" },
            ["staccato"] = new[] { "factor", "length" },
        };

        static readonly string[] SelectionKeys = { "range_start", "range_end", "pitch_low", "pitch_high" };

        public const string Description =
            "Edits notes already in a pattern: transpose (semitones, or steps of a key's scale), quantize (grid, strength, swing), humanize, " +
            "velocity (set, scale, add, ramp, accent pattern), shift, stretch, reverse, invert, legato, staccato; the transforms run in order on " +
            "a selection (range_start/range_end in beats, pitch_low/pitch_high) as one undo step. Use it instead of rewriting notes with notes_write; " +
            "it keeps notes inside the pattern and MIDI's range and returns counts, the new note summary and what had to be clipped. " +
            "Example: notes_transform(machine=\"Keys\", pattern=\"Verse\", transforms=[{\"op\": \"quantize\", \"grid\": 0.25, \"strength\": 0.8}, " +
            "{\"op\": \"velocity\", \"ramp\": [60, 110]}], range_start=0, range_end=8).";

        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats from the pattern start (0 = its start)", 0), String("\"bar.beat\" in the pattern: \"2.1\" is its second bar"));

        static JsonObject PitchArg(string description) =>
            AnyOf(description, Integer("MIDI number, 60 = C4", 0, 127), String("Name: \"C4\", \"F#3\", \"Bb2\""));

        static (string, JsonObject, bool)[] Selection(string scope) => new[]
        {
            Opt("range_start", Position($"Selection{scope}: notes starting at or after this, in beats from the pattern start or \"bar.beat\" (default: the start)")),
            Opt("range_end", Position($"Selection{scope}: notes starting before this, in beats or \"bar.beat\" (default: the pattern's end)")),
            Opt("pitch_low", PitchArg($"Selection{scope}: lowest pitch included, MIDI number or name (default: all)")),
            Opt("pitch_high", PitchArg($"Selection{scope}: highest pitch included, MIDI number or name (default: all)")),
        };

        static JsonObject Step() => Object("One transform. op picks it; the other fields are that transform's settings",
            new[]
            {
                Req("op", String("transpose, quantize, humanize, velocity, shift, stretch, reverse, invert, legato or staccato", Ops)),
                Opt("semitones", Integer("transpose: semitones, + up, − down (12 = an octave)")),
                Opt("scale_steps", Integer("transpose: steps of the key's scale instead of semitones (2 = up a third in the key); needs key")),
                Opt("key", String("transpose with scale_steps: the key, \"A minor\", \"F# major\", \"D dorian\"")),
                Opt("grid", Number("quantize, and velocity with accent: grid in beats (0.25 = 16ths, 0.5 = 8ths, 1/3 = 8th-note triplets); accent default 0.25", 0)),
                Opt("strength", Number("quantize: 0–1, how far notes move toward the grid (default 1 = all the way)", 0, 1)),
                Opt("swing", Number("quantize: 0–1, delays the off-beat grid lines; 1 = triplet feel, the off-beat 2/3 through each pair (default 0)", 0, 1)),
                Opt("starts", Boolean("quantize: move note starts (default true)")),
                Opt("ends", Boolean("quantize: also snap note ends to the grid (default false: lengths are kept)")),
                Opt("timing", Number("humanize: the largest random timing offset, in beats (0.02 ≈ 10 ms at 120 BPM)", 0)),
                Opt("velocity", Integer("humanize: the largest random velocity change, ± (velocity units, 1–127 scale)", 0, 126)),
                Opt("seed", Integer("humanize: random seed; the same seed gives the same result (default 1)")),
                Opt("set", Integer("velocity: set the velocity, 1–127", 1, 127)),
                Opt("scale", Number("velocity: multiply velocities (0.8 = 20% softer, 1.2 = louder)", 0)),
                Opt("add", Integer("velocity: add to velocities, ± (−20 = softer)")),
                Opt("ramp", Array(Integer("Velocity 1–127", 1, 127), "velocity: [from, to], a straight line from the first selected note to the last (crescendo or fade)", 2)),
                Opt("accent", String("velocity: accent pattern, one character per grid step, repeating from the pattern start: x = accent, . = none (\"x..x..x.\" with grid 0.5)")),
                Opt("amount", Integer("velocity with accent: velocity added on x steps (default 20; negative softens)")),
                Opt("beats", Number("shift: beats to move, + later, − earlier")),
                Opt("factor", Number("stretch: time factor (2 = twice as long, 0.5 = twice as fast); staccato: length factor 0–1 (default 0.5)", 0)),
                Opt("anchor", Position("stretch: the time that stays put, in beats or \"bar.beat\" (default: the selection's start)")),
                Opt("axis", PitchArg("invert: mirror pitches around this pitch (default: the first selected note's pitch)")),
                Opt("gap", Number("legato: beats left free before the next note (default 0)", 0)),
                Opt("length", Number("staccato: shorten notes to at most this many beats, instead of a factor", 0)),
            }.Concat(Selection(" for this transform only (default: the call's selection)")).ToArray());

        /// <summary>The tool's input schema (machine and pattern included).</summary>
        public static JsonObject Input { get; } = Object(null, new[]
        {
            Req("machine", String("The machine whose pattern changes")),
            Req("pattern", String("The pattern")),
            Req("transforms", Array(Step(), "Transforms, applied in order; one undo step for all", 1)),
        }.Concat(Selection("")).ToArray());

        public static JsonObject Output { get; } = Object(null,
            Req("machine", String("The machine")),
            Req("pattern", String("The pattern")),
            Req("transforms", Array(String("What one transform did"), "One line per transform")),
            Req("selected", Integer("Notes the selection picked")),
            Req("changed", Integer("Notes that changed")),
            Req("removed", Integer("Notes removed because they left the pattern")),
            Req("total", Integer("Notes in the pattern now")),
            Req("summary", String("The pattern's notes now, in short")),
            Req("problems", Array(String("A problem"), "Clipped notes and velocities, notes outside the key, an empty selection")));

        /// <summary>The tool with the glue's handler.</summary>
        public static ToolDef Tool(Func<ToolContext, Task<ToolResult>> handler) => new()
        {
            Name = Name,
            Title = "Transform notes",
            Description = Description,
            Input = Input,
            Output = Output,
            Annotations = ToolAnnotations.Write,
            Handler = handler,
        };

        /// <summary>Runs the call's transforms on a pattern's notes: the new notes and the problems to report.</summary>
        public static (List<NoteEvent> Result, List<string> Problems) Apply(JsonObject args, List<NoteEvent> notes, int ticksPerBeat, int patternTicks, int beatsPerBar = 4)
        {
            var o = Run(args, notes, ticksPerBeat, patternTicks, beatsPerBar);
            return (o.Notes, o.Problems);
        }

        /// <summary>
        /// Runs the call's transforms. The call's selection is taken once and followed through every transform (so "transpose
        /// these, then shift them" moves the same notes); a transform with its own range or pitch fields selects again, from the
        /// notes as they are then. All arguments are checked before anything runs; a bad one throws a ToolError.
        /// </summary>
        public static TransformOutcome Run(JsonObject args, IReadOnlyList<NoteEvent> notes, int ticksPerBeat, int patternTicks, int beatsPerBar = 4)
        {
            var ctx = new TransformContext(ticksPerBeat, patternTicks);
            if (args["transforms"] is not JsonArray list || list.Count == 0)
                throw new ToolError("transforms: give a list of transforms, e.g. [{\"op\": \"transpose\", \"semitones\": 12}].");
            var selection = ParseSelection(args, "", ctx, beatsPerBar) ?? NoteSelection.All;
            var steps = new List<(string At, string Op, NoteSelection? Own, Action<List<Transforms.Item>, NoteSelection> Run, string Text)>();
            for (int k = 0; k < list.Count; k++)
            {
                string at = $"transforms[{k}]";
                if (list[k] is not JsonObject step) throw new ToolError($"{at}: each transform is an object like {{\"op\": \"quantize\", \"grid\": 0.25}}.");
                var (op, run, text) = ParseStep(step, at, ctx, beatsPerBar);
                steps.Add((at, op, ParseSelection(step, at + ".", ctx, beatsPerBar), run, text));
            }

            var items = Transforms.Items(notes, selection);
            int selected = items.Count(i => i.Selected);
            if (selected == 0 && steps.Any(s => s.Own == null))
                ctx.Problems.Add($"Nothing matched the selection ({selection.Describe(ctx)}); those transforms changed nothing. notes_read shows the pattern's notes.");
            var done = new List<string>();
            foreach (var s in steps)
            {
                bool[]? saved = null;
                var sel = selection;
                if (s.Own != null)
                {
                    saved = items.Select(i => i.Selected).ToArray();
                    foreach (var i in items) i.Selected = !i.Dropped && s.Own.Contains(i.Note);
                    sel = s.Own;
                    if (!items.Any(i => i.Selected))
                        ctx.Problems.Add($"{s.At} ({s.Op}): nothing matched its selection ({s.Own.Describe(ctx)}); it changed nothing.");
                }
                int count = Transforms.Chosen(items).Count();
                s.Run(items, sel);
                Transforms.Clip(items, ctx, $"{s.At} ({s.Op})");
                done.Add($"{s.Text} ({Transforms.Count(count, "note")})");
                if (saved != null)
                    for (int i = 0; i < items.Count; i++) items[i].Selected = saved[i];
            }
            return new TransformOutcome
            {
                Notes = Transforms.Collect(items),
                Problems = ctx.Problems,
                Steps = done,
                Selected = selected,
                Changed = items.Count(i => !i.Dropped && i.Note != i.Original),
                Removed = items.Count(i => i.Dropped),
            };
        }

        /// <summary>The structured answer for a finished call.</summary>
        public static JsonObject Report(string machine, string pattern, TransformOutcome outcome, int ticksPerBeat) => new()
        {
            ["machine"] = machine,
            ["pattern"] = pattern,
            ["transforms"] = new JsonArray(outcome.Steps.Select(s => (JsonNode)s).ToArray()),
            ["selected"] = outcome.Selected,
            ["changed"] = outcome.Changed,
            ["removed"] = outcome.Removed,
            ["total"] = outcome.Notes.Count,
            ["summary"] = NoteList.Summary(outcome.Notes, ticksPerBeat),
            ["problems"] = new JsonArray(outcome.Problems.Select(p => (JsonNode)p).ToArray()),
        };

        /// <summary>The tool result: the report plus a one-paragraph summary.</summary>
        public static ToolResult Result(string machine, string pattern, TransformOutcome outcome, int ticksPerBeat)
        {
            var r = Report(machine, pattern, outcome, ticksPerBeat);
            string text = $"{machine} \"{pattern}\": {string.Join("; ", outcome.Steps)}. Changed {outcome.Changed} of {outcome.Notes.Count + outcome.Removed} notes" +
                          (outcome.Removed > 0 ? $", removed {outcome.Removed}" : "") + $"; now {r["summary"]}." +
                          (outcome.Problems.Count > 0 ? " Problems: " + string.Join(" ", outcome.Problems) : "");
            return ToolResult.Ok(text, r);
        }

        // ---- arguments ----

        static NoteSelection? ParseSelection(JsonObject o, string prefix, TransformContext ctx, int beatsPerBar)
        {
            if (!SelectionKeys.Any(k => o[k] != null)) return null;
            int? from = null, to = null, low = null, high = null;
            if (o["range_start"] != null) from = ctx.Time(SongTime.ParseBeats(o["range_start"]!, beatsPerBar, prefix + "range_start", "the pattern start"));
            if (o["range_end"] != null) to = ctx.Time(SongTime.ParseBeats(o["range_end"]!, beatsPerBar, prefix + "range_end", "the pattern start"));
            if (from != null && to != null && to <= from)
                throw new ToolError($"{prefix}range_end must be after {prefix}range_start (both in beats from the pattern start).");
            if (o["pitch_low"] != null) low = NoteList.ParsePitch(o["pitch_low"], prefix + "pitch_low");
            if (o["pitch_high"] != null) high = NoteList.ParsePitch(o["pitch_high"], prefix + "pitch_high");
            if (low != null && high != null && high < low)
                throw new ToolError($"{prefix}pitch_high ({Pitch.Name(high.Value)}) is below {prefix}pitch_low ({Pitch.Name(low.Value)}); swap them.");
            return new NoteSelection(from, to, low, high);
        }

        static (string Op, Action<List<Transforms.Item>, NoteSelection> Run, string Text) ParseStep(JsonObject step, string at, TransformContext ctx, int beatsPerBar)
        {
            string? op = step["op"] is JsonValue v && v.TryGetValue(out string? s) ? s.Trim().ToLowerInvariant() : null;
            if (op == null) throw new ToolError($"{at}.op: missing; one of {string.Join(", ", Ops)}.");
            if (!Takes.TryGetValue(op, out var takes))
            {
                var close = Text.Closest(op, Ops);
                throw new ToolError($"{at}.op: unknown transform \"{op}\"." + (close.Count > 0 ? $" Did you mean \"{close[0]}\"?" : "") + $" Transforms: {string.Join(", ", Ops)}.");
            }
            string where = $"{at} ({op})";
            foreach (var (key, _) in step)
            {
                if (key == "op" || takes.Contains(key) || SelectionKeys.Contains(key)) continue;
                var owner = Takes.Where(t => t.Value.Contains(key)).Select(t => t.Key).ToList();
                string hint = owner.Count > 0 ? $" (\"{key}\" belongs to {string.Join(" and ", owner)})" : "";
                string accepted = takes.Length > 0 ? $"{op} takes {string.Join(", ", takes)}" : $"{op} takes no settings";
                throw new ToolError($"{where}: \"{key}\" doesn't apply{hint}; {accepted}, plus range_start, range_end, pitch_low, pitch_high.");
            }

            double? Num(string name, double min = double.NegativeInfinity, double max = double.PositiveInfinity, string unit = "")
            {
                if (step[name] == null) return null;
                if (!TryNumber(step[name], out double d)) throw new ToolError($"{where}: {name} must be a number{unit}.");
                if (d < min || d > max)
                    throw new ToolError($"{where}: {name} {NoteList.Fmt(d)} is out of range ({(double.IsNegativeInfinity(min) ? "" : NoteList.Fmt(min))}–{(double.IsPositiveInfinity(max) ? "" : NoteList.Fmt(max))}{unit}).");
                return d;
            }
            int? Int(string name, double min = double.NegativeInfinity, double max = double.PositiveInfinity, string unit = "")
            {
                double? d = Num(name, min, max, unit);
                if (d == null) return null;
                if (d != Math.Floor(d.Value)) throw new ToolError($"{where}: {name} must be a whole number{unit}.");
                return (int)d.Value;
            }
            bool? Bool(string name)
            {
                if (step[name] == null) return null;
                if (step[name] is JsonValue b && b.TryGetValue(out bool x)) return x;
                throw new ToolError($"{where}: {name} must be true or false.");
            }
            string? Str(string name) => step[name] == null ? null : step[name] is JsonValue sv && sv.TryGetValue(out string? x) ? x : throw new ToolError($"{where}: {name} must be text.");
            string Sign(double x) => (x > 0 ? "+" : "") + NoteList.Fmt(x);

            switch (op)
            {
                case "transpose":
                {
                    int? semis = Int("semitones", unit: " of semitones");
                    int? steps = Int("scale_steps", unit: " of scale steps");
                    string? keyText = Str("key");
                    if ((semis == null) == (steps == null))
                        throw new ToolError($"{where}: give semitones (12 = an octave up) or scale_steps with a key (2 = a third up in the key), not {(semis == null ? "neither" : "both")}.");
                    if (semis != null)
                    {
                        if (keyText != null) throw new ToolError($"{where}: key only goes with scale_steps; semitones transpose chromatically.");
                        int n = semis.Value;
                        return (op, (items, _) => Transforms.TransposeOp(items, n), $"transpose {Sign(n)} semitones");
                    }
                    if (keyText == null) throw new ToolError($"{where}: scale_steps needs key, e.g. \"key\": \"A minor\".");
                    var key = MusicKey.Parse(keyText);
                    int st = steps!.Value;
                    return (op, (items, _) => Transforms.TransposeInKeyOp(items, ctx, key, st), $"transpose {Sign(st)} steps in {key}");
                }
                case "quantize":
                {
                    double grid = Num("grid", 0, double.PositiveInfinity, " of beats") ?? throw new ToolError($"{where}: needs grid, in beats (0.25 = 16th notes, 0.5 = 8ths, 1/3 = 8th-note triplets).");
                    if (grid * ctx.TimePerBeat < 1) throw new ToolError($"{where}: grid {NoteList.Fmt(grid)} beats is finer than the pattern's resolution; use a grid like 0.25 (16ths).");
                    double strength = Num("strength", 0, 1) ?? 1, swing = Num("swing", 0, 1) ?? 0;
                    bool starts = Bool("starts") ?? true, ends = Bool("ends") ?? false;
                    if (!starts && !ends) throw new ToolError($"{where}: starts and ends are both false, so nothing would move; set one to true.");
                    string text = $"quantize to {NoteList.Fmt(grid)} beats at {Math.Round(strength * 100)}%" + (swing > 0 ? $", swing {Math.Round(swing * 100)}%" : "") +
                                  (ends ? starts ? ", starts and ends" : ", ends only" : "");
                    return (op, (items, _) => Transforms.QuantizeOp(items, ctx, grid, strength, swing, starts, ends), text);
                }
                case "humanize":
                {
                    double timing = Num("timing", 0, 64, " of beats") ?? 0;
                    int velocity = Int("velocity", 0, 126) ?? 0;
                    int seed = Int("seed") ?? 1;
                    if (timing == 0 && velocity == 0) throw new ToolError($"{where}: give timing (beats, e.g. 0.02) and/or velocity (e.g. 8); both are 0.");
                    return (op, (items, _) => Transforms.HumanizeOp(items, ctx, timing, velocity, seed),
                            $"humanize ±{NoteList.Fmt(timing)} beats, ±{velocity} velocity, seed {seed}");
                }
                case "velocity":
                {
                    var given = new[] { "set", "scale", "add", "ramp", "accent" }.Where(k => step[k] != null).ToList();
                    if (given.Count != 1)
                        throw new ToolError($"{where}: give exactly one of set (1–127), scale (a factor), add (±), ramp ([from, to]) or accent (\"x..x\")" +
                                            (given.Count > 1 ? $"; got {string.Join(" and ", given)}." : "."));
                    if (given[0] != "accent" && (step["grid"] != null || step["amount"] != null))
                        throw new ToolError($"{where}: grid and amount only go with accent.");
                    switch (given[0])
                    {
                        case "set":
                        {
                            int value = Int("set", 1, 127)!.Value;
                            return (op, (items, _) => Transforms.VelocityOp(items, _ => value), $"velocity set to {value}");
                        }
                        case "scale":
                        {
                            double f = Num("scale", 0, 16)!.Value;
                            return (op, (items, _) => Transforms.VelocityOp(items, x => (int)Math.Round(x * f)), $"velocity ×{NoteList.Fmt(f)}");
                        }
                        case "add":
                        {
                            int add = Int("add", -127, 127)!.Value;
                            return (op, (items, _) => Transforms.VelocityOp(items, x => x + add), $"velocity {Sign(add)}");
                        }
                        case "ramp":
                        {
                            if (step["ramp"] is not JsonArray r || r.Count != 2 || !TryNumber(r[0], out double a) || !TryNumber(r[1], out double b) ||
                                a < 1 || a > 127 || b < 1 || b > 127 || a != Math.Floor(a) || b != Math.Floor(b))
                                throw new ToolError($"{where}: ramp is [from, to], two velocities 1–127, e.g. [60, 110].");
                            int from = (int)a, to = (int)b;
                            return (op, (items, _) => Transforms.RampOp(items, from, to), $"velocity ramp {from}→{to}");
                        }
                        default:
                        {
                            string pattern = Str("accent")!;
                            var accents = Transforms.AccentSteps(pattern, where + ": accent");
                            double grid = Num("grid", 0, double.PositiveInfinity, " of beats") ?? 0.25;
                            if (grid * ctx.TimePerBeat < 1) throw new ToolError($"{where}: grid {NoteList.Fmt(grid)} beats is finer than the pattern's resolution.");
                            int amount = Int("amount", -126, 126) ?? 20;
                            return (op, (items, _) => Transforms.AccentOp(items, ctx, accents, grid, amount),
                                    $"accent \"{pattern}\" every {NoteList.Fmt(grid)} beats, {Sign(amount)} velocity");
                        }
                    }
                }
                case "shift":
                {
                    double beats = Num("beats", unit: " of beats") ?? throw new ToolError($"{where}: needs beats, + later or − earlier (1 = a beat, 4 = a bar of 4/4).");
                    int time = ctx.Time(beats);
                    return (op, (items, _) => Transforms.ShiftOp(items, time), $"shift {Sign(beats)} beats");
                }
                case "stretch":
                {
                    double factor = Num("factor", 0, 64) ?? throw new ToolError($"{where}: needs factor (2 = twice as long, 0.5 = twice as fast).");
                    if (factor <= 0) throw new ToolError($"{where}: factor must be more than 0 (0.5 = twice as fast).");
                    int? anchor = step["anchor"] == null ? null : ctx.Time(SongTime.ParseBeats(step["anchor"]!, beatsPerBar, where + ": anchor", "the pattern start"));
                    return (op, (items, sel) => Transforms.StretchOp(items, sel, factor, anchor), $"stretch ×{NoteList.Fmt(factor)}");
                }
                case "reverse":
                    return (op, (items, sel) => Transforms.ReverseOp(items, ctx, sel), "reverse");
                case "invert":
                {
                    int? axis = step["axis"] == null ? null : NoteList.ParsePitch(step["axis"], where + ": axis");
                    return (op, (items, _) => Transforms.InvertOp(items, axis), axis == null ? "invert around the first note" : $"invert around {Pitch.Name(axis.Value)}");
                }
                case "legato":
                {
                    double gap = Num("gap", 0, 64, " of beats") ?? 0;
                    int g = ctx.Time(gap);
                    return (op, (items, _) => Transforms.LegatoOp(items, g), gap > 0 ? $"legato, {NoteList.Fmt(gap)}-beat gaps" : "legato");
                }
                default: // staccato
                {
                    if (step["factor"] != null && step["length"] != null) throw new ToolError($"{where}: give factor or length, not both.");
                    double? length = Num("length", 0, double.PositiveInfinity, " of beats");
                    if (length != null)
                    {
                        if (length <= 0) throw new ToolError($"{where}: length must be more than 0 beats (0.25 = a 16th note).");
                        int t = Math.Max(1, ctx.Time(length.Value));
                        return (op, (items, _) => Transforms.StaccatoOp(items, 1, t), $"staccato, at most {NoteList.Fmt(length.Value)} beats");
                    }
                    double factor = Num("factor", 0, 1) ?? 0.5;
                    if (factor <= 0) throw new ToolError($"{where}: factor must be more than 0 (0.5 = half as long).");
                    return (op, (items, _) => Transforms.StaccatoOp(items, factor, null), $"staccato ×{NoteList.Fmt(factor)}");
                }
            }
        }
    }
}
