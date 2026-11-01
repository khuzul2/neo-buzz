// Agent server (docs/specs/mcp-server.md §5.5, phase A3): composition tools that need no song.
// theory is complete here (read-only; nothing in the song changes). notes_transform's schema and
// engine live in Transforms.cs; the ReBuzz glue passes the handler that reads and writes the pattern.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.Model
{
    public static class CompositionTools
    {
        public static readonly string[] TheoryOps = { "chord", "progression", "scale", "key", "degree", "fit" };

        static readonly Dictionary<string, string[]> TheoryTakes = new()
        {
            ["chord"] = new[] { "chord", "key", "octave", "register", "voicing", "inversion", "previous" },
            ["progression"] = new[] { "progression", "key", "octave", "register", "voicing", "inversion", "voice_leading", "beats_per_chord", "start" },
            ["scale"] = new[] { "key", "octave" },
            ["key"] = new[] { "key" },
            ["degree"] = new[] { "key", "pitches", "steps" },
            ["fit"] = new[] { "notes", "key" },
        };

        /// <summary>Registers theory, and notes_transform when the host gives its handler.</summary>
        public static void Register(ToolRegistry tools, Func<ToolContext, Task<ToolResult>>? notesTransform = null)
        {
            tools.Add(TheoryTool());
            if (notesTransform != null) tools.Add(NotesTransform.Tool(notesTransform));
        }

        static JsonObject PitchArg(string description) =>
            AnyOf(description, Integer("MIDI number, 60 = C4", 0, 127), String("Name: \"C4\", \"F#3\", \"Bb2\""));

        static JsonObject PitchOut() => Object(null, Req("pitch", Integer("MIDI number, 60 = C4")), Req("name", String("Spelled name, \"Bb3\"")));

        static JsonObject ChordOut() => Object(null,
            Req("symbol", String("Chord symbol, \"Dm7\", \"G7/B\"")),
            Opt("numeral", String("The roman numeral it came from")),
            Req("root", String("Root, spelled")),
            Opt("bass", String("Slash bass, spelled")),
            Req("tones", Array(String("Spelled tone"), "Chord tones in root position, spelled")),
            Req("intervals", Array(String("\"1\", \"b3\", \"#11\""), "Each tone's interval above the root")),
            Req("pitches", Array(PitchOut(), "The voicing, lowest first")),
            Opt("in_key", Boolean("Every tone is in the key")),
            Opt("out_of_key", Array(String("Spelled tone"), "Tones outside the key")),
            Opt("movement", Integer("Semitones moved from the previous chord")));

        static JsonObject KeyOut() => Object(null,
            Req("name", String("\"F# major\"")),
            Req("tonic", String("Spelled tonic")),
            Req("scale", String("Scale name")),
            Opt("signature", Object(null,
                Req("sharps", Integer("Sharps in the key signature")),
                Req("flats", Integer("Flats in the key signature")),
                Req("accidentals", Array(String("\"Bb\""), "In the order they are written")))),
            Opt("relative", String("Relative major or minor")),
            Opt("parallel", String("Parallel major or minor")));

        static JsonObject DiatonicOut(string what) => Array(Object(null,
            Req("numeral", String("\"ii7\"")), Req("symbol", String("\"Dm7\"")), Req("tones", Array(String("Spelled tone"), "Its tones"))), what);

        public static ToolDef TheoryTool() => new()
        {
            Name = "theory",
            Title = "Music theory",
            Description =
                "Music theory without arithmetic slips: chord symbols to pitches (\"Am9/E\", voicings, inversions, voice leading), roman-numeral " +
                "progressions in a key (\"ii7–V7–Imaj7\", bVII, V7/V), scales, key signatures and diatonic chords, scale degrees and in-key " +
                "transposition, and how well notes fit a key. Use it before notes_write to get exact pitches, or to check a melody; it is read-only. " +
                "Returns pitches as MIDI numbers with names spelled for the key (Bb in F major), and progressions also as notes ready for notes_write; " +
                "example: theory(op=\"progression\", key=\"C major\", progression=\"ii7–V7–Imaj7\", octave=3).",
            Input = Object(null,
                Req("op", String("chord: a symbol → pitches. progression: numerals or symbols → chords and notes. scale: a key's notes. " +
                                 "key: signature, relative key, diatonic chords. degree: pitches' scale degrees, transposition in the key. " +
                                 "fit: how well notes fit a key, and the best keys", TheoryOps)),
                Opt("chord", String("op=chord: a chord symbol, \"C\", \"Am7\", \"F#m7b5\", \"Bbmaj9\", \"G7(b9,#11)\", \"Dsus4\", \"C6/9\", \"E7alt\", slash bass \"Am9/E\"")),
                Opt("progression", String("op=progression: roman numerals in the key (\"ii7–V7–Imaj7\", \"i–VI–III–VII\", borrowed \"bVII\", secondary \"V7/V\", " +
                                          "figured inversions \"V65\", \"I64\") and/or chord symbols (\"Am F C G\"), separated by spaces, dashes, commas or bars")),
                Opt("key", String("A key: \"A minor\", \"F# major\", \"D dorian\", \"Bbm\", \"E harmonic minor\", \"A minor pentatonic\", \"C blues\". " +
                                  "Needed by scale, key, degree and roman numerals; with chord, progression and fit it marks notes outside the key")),
                Opt("octave", Integer("Scientific octave of the chord root or scale tonic (4: C4–B4, MIDI 60–71; default 4)", -1, 9)),
                Opt("register", PitchArg("Instead of octave: the lowest note lands at or just above this pitch, MIDI number or name (\"E3\")")),
                Opt("voicing", String("close (default: root position, stacked upward), open (every second note up an octave), drop2, drop3 " +
                                      "(the 2nd or 3rd highest note down an octave)", Voicer.VoicingNames)),
                Opt("inversion", Integer("0 = root position, 1 = first inversion (the next chord tone in the bass), …; applied before the voicing", 0, 6)),
                Opt("voice_leading", Boolean("op=progression: each chord after the first takes the inversion and octave that moves least from the one " +
                                             "before (default true); false voices every chord the same way")),
                Opt("previous", Array(PitchArg("A pitch"), "op=chord: the chord before, as pitches; picks the inversion and octave that move least from it")),
                Opt("beats_per_chord", Number("op=progression: each chord's length in the returned notes, in beats (default 4)", 0)),
                Opt("start", Number("op=progression: the first chord's start in the returned notes, in beats (default 0)", 0)),
                Opt("pitches", Array(PitchArg("A pitch"), "op=degree: the pitches to place in the key", 1)),
                Opt("steps", Integer("op=degree: also move each pitch this many steps along the key's scale (2 = up a third in the key, −1 = down a step)")),
                Opt("notes", Array(AnyOf("A pitch, or a note as notes_read returns it",
                        Integer("MIDI number", 0, 127), String("Name, \"C4\""),
                        Object(null, Req("pitch", PitchArg("Pitch")), Opt("name", String("Ignored")), Opt("start", Number("Start in beats")),
                               Opt("length", Number("Length in beats; weights the note (default 1)", 0)), Opt("velocity", Integer("Ignored")),
                               Opt("midi_channel", Integer("Ignored")))),
                    "op=fit: the melody or part; notes with lengths count by duration, bare pitches count once each", 1))),
            Output = Object(null,
                Req("op", String("The operation")),
                Opt("key", KeyOut()),
                Opt("chord", ChordOut()),
                Opt("chords", Array(ChordOut(), "op=progression: the chords in order")),
                Opt("movement", Integer("op=progression: total semitones moved between chords")),
                Opt("notes", Array(Object(null,
                        Req("pitch", Integer("MIDI number")), Req("name", String("Spelled name")), Req("start", Number("Beats")),
                        Req("length", Number("Beats")), Req("velocity", Integer("1–127"))),
                    "op=progression: the chords as notes, the shape notes_write takes")),
                Opt("names", Array(String("Spelled note"), "op=scale, key: the scale's notes")),
                Opt("intervals", Array(String("\"b3\""), "op=scale: each note's interval above the tonic, against the major scale")),
                Opt("pitches", Array(PitchOut(), "op=scale: the scale from the tonic in the octave")),
                Opt("triads", DiatonicOut("op=key: the triad on each degree")),
                Opt("sevenths", DiatonicOut("op=key: the seventh chord on each degree")),
                Opt("degrees", Array(Object(null,
                        Req("pitch", Integer("MIDI number")), Req("name", String("Spelled in the key")), Opt("degree", Integer("1-based scale degree")),
                        Req("label", String("\"3\", \"b3\", \"#7\"")), Req("in_key", Boolean("In the key's scale")),
                        Opt("to", PitchOut()), Opt("chromatic", Boolean("Wasn't in the scale, so kept its distance above the scale note below"))),
                    "op=degree: one per pitch")),
                Opt("fit", Object(null,
                    Req("key", String("The key checked: the one given, else the best fit")),
                    Req("share", Number("Share of the notes' time in the key, 0–1")),
                    Req("correlation", Number("Krumhansl–Kessler key-profile correlation, −1 … 1")),
                    Req("out_of_key_count", Integer("Notes outside the key")),
                    Req("out_of_key", Array(Object(null,
                            Req("pitch", Integer("MIDI number")), Req("name", String("Spelled in the key")),
                            Opt("start", Number("Beats")), Opt("length", Number("Beats"))),
                        "The notes outside the key (the first 32)")))),
                Opt("best", Array(Object(null,
                        Req("key", String("\"A minor\"")), Req("share", Number("Share in the key, 0–1")), Req("correlation", Number("Profile correlation"))),
                    "op=fit: the best-fitting keys, best first"))),
            Annotations = ToolAnnotations.Read,
            Handler = c => Task.FromResult(Answer(c.Arguments)),
        };

        /// <summary>The theory tool's answer to a set of arguments (throws ToolError with the next step).</summary>
        public static ToolResult Answer(JsonObject a)
        {
            string op = Str(a, "op")?.Trim().ToLowerInvariant() ?? throw new ToolError($"theory needs op: {string.Join(", ", TheoryOps)}.");
            if (!TheoryTakes.TryGetValue(op, out var takes))
            {
                var close = Text.Closest(op, TheoryOps);
                throw new ToolError($"Unknown op \"{op}\"." + (close.Count > 0 ? $" Did you mean \"{close[0]}\"?" : "") + $" Ops: {string.Join(", ", TheoryOps)}.");
            }
            foreach (var (name, _) in a)
            {
                if (name == "op" || takes.Contains(name)) continue;
                var owners = TheoryTakes.Where(t => t.Value.Contains(name)).Select(t => t.Key).ToList();
                throw new ToolError($"theory(op=\"{op}\") doesn't use {name}" + (owners.Count > 0 ? $" (that's for op={string.Join(" or op=", owners)})" : "") +
                                    $"; it takes {string.Join(", ", takes)}.");
            }
            MusicKey? key = Str(a, "key") is string k ? MusicKey.Parse(k) : null;
            return op switch
            {
                "chord" => ChordOp(a, key),
                "progression" => ProgressionOp(a, key),
                "scale" => ScaleOp(a, key ?? throw NeedsKey(op, "\"D dorian\"")),
                "key" => KeyOp(key ?? throw NeedsKey(op, "\"F major\"")),
                "degree" => DegreeOp(a, key ?? throw NeedsKey(op, "\"A minor\"")),
                _ => FitOp(a, key),
            };
        }

        static ToolError NeedsKey(string op, string example) => new($"theory(op=\"{op}\") needs key, like {example}, \"F# major\" or \"A minor pentatonic\".");

        // ---- operations ----

        static (int Octave, int? Register, Voicing Voicing, int? Inversion) Placement(JsonObject a)
        {
            int? register = a["register"] != null ? NoteList.ParsePitch(a["register"], "register") : null;
            if (register != null && a["octave"] != null) throw new ToolError("Give octave or register, not both: octave places the root, register the lowest note.");
            return (Int(a, "octave") ?? 4, register, Voicer.ParseVoicing(Str(a, "voicing")), Int(a, "inversion"));
        }

        static ToolResult ChordOp(JsonObject a, MusicKey? key)
        {
            string text = Str(a, "chord") ?? throw new ToolError("theory(op=\"chord\") needs chord, a symbol like \"Am9/E\", \"F#m7b5\" or \"G7(b9)\".");
            var chord = Chord.Parse(text);
            var (octave, register, voicing, inversion) = Placement(a);
            List<SpelledPitch> voiced;
            int? movement = null;
            if (a["previous"] is JsonArray prev)
            {
                if (inversion != null || register != null || a["octave"] != null)
                    throw new ToolError("previous picks the inversion and octave itself: drop inversion, octave and register, or drop previous.");
                var before = Pitches(prev, "previous");
                if (before.Count == 0) throw new ToolError("previous: give the chord before as pitches, e.g. [60, 64, 67].");
                voiced = Voicer.VoiceLead(chord, before, voicing);
                movement = Voicer.Movement(before, voiced.Select(p => p.Midi).ToList());
            }
            else
            {
                voiced = Voicer.Voice(chord, voicing, inversion ?? 0, octave);
                if (register != null) voiced = Voicer.Place(voiced, register.Value);
            }
            Voicer.CheckRange(voiced, chord.Symbol);
            var cj = ChordJson(chord, voiced, key, chord.Symbol, null);
            if (movement != null) cj["movement"] = movement.Value;
            var result = new JsonObject { ["op"] = "chord", ["chord"] = cj };
            if (key != null) result["key"] = KeyJson(key, false);
            string text2 = $"{chord.Symbol}: {Names(voiced)} (MIDI {string.Join(" ", voiced.Select(p => p.Midi))}); intervals {chord.Intervals}" +
                           (chord.Bass is NoteName b ? $" over {b}" : "") + (movement != null ? $"; moves {movement} semitones from the previous chord" : "") +
                           OutOfKey(cj, key) + ".";
            return ToolResult.Ok(text2, result);
        }

        static ToolResult ProgressionOp(JsonObject a, MusicKey? key)
        {
            string text = Str(a, "progression") ?? throw new ToolError("theory(op=\"progression\") needs progression, like \"ii7–V7–Imaj7\" (with key) or \"Am F C G\".");
            var chords = Roman.ParseProgression(text, key);
            var (octave, register, voicing, inversion) = Placement(a);
            bool lead = Bool(a, "voice_leading") ?? true;
            double length = Num(a, "beats_per_chord") ?? 4, start = Num(a, "start") ?? 0;
            if (length <= 0) throw new ToolError("beats_per_chord must be more than 0 (4 = a bar of 4/4).");
            var list = new JsonArray();
            var notes = new JsonArray();
            List<int>? previous = null;
            int total = 0;
            var lines = new List<string>();
            for (int i = 0; i < chords.Count; i++)
            {
                var pc = chords[i];
                List<SpelledPitch> voiced;
                if (lead && previous != null) voiced = Voicer.VoiceLead(pc.Chord, previous, voicing);
                else
                {
                    voiced = Voicer.Voice(pc.Chord, voicing, pc.Inversion > 0 ? pc.Inversion : inversion ?? 0, octave);
                    if (register != null) voiced = Voicer.Place(voiced, register.Value);
                }
                Voicer.CheckRange(voiced, pc.Symbol);
                var midis = voiced.Select(p => p.Midi).ToList();
                var cj = ChordJson(pc.Chord, voiced, key, pc.Symbol, pc.Numeral);
                if (previous != null)
                {
                    int m = Voicer.Movement(previous, midis);
                    cj["movement"] = m;
                    total += m;
                }
                list.Add(cj);
                foreach (var p in voiced)
                    notes.Add(new JsonObject
                    {
                        ["pitch"] = p.Midi, ["name"] = p.Name, ["start"] = Math.Round(start + i * length, 6), ["length"] = length, ["velocity"] = NoteList.DefaultVelocity,
                    });
                lines.Add((pc.Numeral != null ? pc.Numeral + " " : "") + $"{pc.Symbol} ({Names(voiced)}{OutOfKey(cj, key)})");
                previous = midis;
            }
            var result = new JsonObject { ["op"] = "progression", ["chords"] = list, ["movement"] = total, ["notes"] = notes };
            if (key != null) result["key"] = KeyJson(key, false);
            string summary = (key != null ? $"In {key}: " : "") + string.Join(", ", lines) +
                             $". {(lead ? "Voice-led" : "Voiced alike")}, {total} semitones of movement; notes holds them as {Theory.Fmt(length)}-beat chords from beat {Theory.Fmt(start)} for notes_write.";
            return ToolResult.Ok(summary, result);
        }

        static ToolResult ScaleOp(JsonObject a, MusicKey key)
        {
            int octave = Int(a, "octave") ?? 4;
            int root = key.Tonic.Midi(octave);
            var pitches = key.Scale.Intervals.Select((s, i) => new SpelledPitch(root + s, key.Notes[i])).ToList();
            Voicer.CheckRange(pitches, key.Name);
            var labels = pitches.Select(p => key.DegreeLabel(p.Midi, majorReference: true)).ToList();
            var result = new JsonObject
            {
                ["op"] = "scale",
                ["key"] = KeyJson(key, false),
                ["names"] = Strings(key.Notes.Select(n => n.ToString())),
                ["intervals"] = Strings(labels),
                ["pitches"] = PitchArray(pitches),
            };
            return ToolResult.Ok($"{key}: {string.Join(" ", key.Notes)} ({string.Join(" ", labels)}); from {pitches[0].Name}: MIDI {string.Join(" ", pitches.Select(p => p.Midi))}.", result);
        }

        static ToolResult KeyOp(MusicKey key)
        {
            var triads = key.DiatonicChords(false);
            var sevenths = key.DiatonicChords(true);
            var result = new JsonObject
            {
                ["op"] = "key",
                ["key"] = KeyJson(key, true),
                ["names"] = Strings(key.Notes.Select(n => n.ToString())),
            };
            if (triads.Count > 0)
            {
                result["triads"] = Diatonic(triads);
                result["sevenths"] = Diatonic(sevenths);
            }
            var acc = key.SignatureAccidentals;
            string signature = acc.Count == 0 ? "no sharps or flats" :
                $"{Transforms.Count(acc.Count, key.Signature > 0 ? "sharp" : "flat")} ({string.Join(" ", acc)})";
            string summary = $"{key}: {signature}; notes {string.Join(" ", key.Notes)}." +
                             (key.Relative is MusicKey r ? $" Relative {r}, parallel {key.Parallel}." : "") +
                             (triads.Count > 0 ? " Triads: " + string.Join(", ", triads.Select(t => $"{t.Numeral} {t.Symbol}")) +
                                                 ". Sevenths: " + string.Join(", ", sevenths.Select(t => $"{t.Numeral} {t.Symbol}")) + "." : "");
            return ToolResult.Ok(summary, result);
        }

        static ToolResult DegreeOp(JsonObject a, MusicKey key)
        {
            var pitches = a["pitches"] is JsonArray list ? Pitches(list, "pitches") : throw new ToolError("theory(op=\"degree\") needs pitches, e.g. [\"G#4\", 72].");
            if (pitches.Count == 0) throw new ToolError("pitches: give at least one pitch, e.g. [\"G#4\"].");
            int? steps = Int(a, "steps");
            var degrees = new JsonArray();
            var lines = new List<string>();
            foreach (int p in pitches)
            {
                var o = new JsonObject { ["pitch"] = p, ["name"] = key.NameOf(p) };
                if (key.Degree(p) is int d) o["degree"] = d;
                o["label"] = key.DegreeLabel(p);
                o["in_key"] = key.Contains(p);
                string line = $"{key.NameOf(p)} is {key.DegreeLabel(p)}" + (key.Contains(p) ? "" : " (not in the key)");
                if (steps != null)
                {
                    int q = key.Step(p, steps.Value, out bool chromatic);
                    if (q < 0 || q > 127) throw new ToolError($"{key.NameOf(p)} moved {steps} steps leaves MIDI's 0–127; use fewer steps.");
                    o["to"] = new JsonObject { ["pitch"] = q, ["name"] = key.NameOf(q) };
                    if (chromatic) o["chromatic"] = true;
                    line += $" → {key.NameOf(q)}";
                }
                degrees.Add(o);
                lines.Add(line);
            }
            string moved = steps != null ? $" Moved {(steps >= 0 ? "+" : "")}{steps} steps in the scale." : "";
            return ToolResult.Ok($"In {key}: {string.Join("; ", lines)}.{moved}", new JsonObject { ["op"] = "degree", ["key"] = KeyJson(key, false), ["degrees"] = degrees });
        }

        static ToolResult FitOp(JsonObject a, MusicKey? key)
        {
            if (a["notes"] is not JsonArray list || list.Count == 0)
                throw new ToolError("theory(op=\"fit\") needs notes: pitches ([\"A4\", \"C5\"]) or notes as notes_read returns them.");
            var notes = new List<FitNote>();
            for (int i = 0; i < list.Count; i++)
            {
                string at = $"notes[{i}]";
                if (list[i] is JsonObject o)
                {
                    int pitch = NoteList.ParsePitch(o["pitch"], at + ".pitch");
                    double? length = TryNumber(o["length"], out double l) ? l : null;
                    double? start = TryNumber(o["start"], out double s) ? s : null;
                    if (length < 0) throw new ToolError($"{at}.length: lengths are beats, 0 or more.");
                    notes.Add(new FitNote(pitch, length ?? 1, start, length));
                }
                else notes.Add(new FitNote(NoteList.ParsePitch(list[i], at), 1));
            }
            if (notes.Sum(n => n.Weight) <= 0) throw new ToolError("notes: every length is 0, so nothing can be weighed; give lengths in beats or bare pitches.");
            var best = MelodyFit.Rank(notes);
            var checkedKey = key ?? best[0].Key;
            var profile = MelodyFit.Profile(notes);
            double share = MelodyFit.Share(profile, checkedKey), correlation = MelodyFit.Correlation(profile, checkedKey);
            var outside = notes.Where(n => !checkedKey.Contains(n.Pitch)).ToList();
            var outJson = new JsonArray();
            foreach (var n in outside.Take(32))
            {
                var o = new JsonObject { ["pitch"] = n.Pitch, ["name"] = checkedKey.NameOf(n.Pitch) };
                if (n.Start != null) o["start"] = n.Start.Value;
                if (n.Length != null) o["length"] = n.Length.Value;
                outJson.Add(o);
            }
            var result = new JsonObject
            {
                ["op"] = "fit",
                ["fit"] = new JsonObject
                {
                    ["key"] = checkedKey.Name,
                    ["share"] = Math.Round(share, 3),
                    ["correlation"] = Math.Round(correlation, 3),
                    ["out_of_key_count"] = outside.Count,
                    ["out_of_key"] = outJson,
                },
                ["best"] = new JsonArray(best.Select(f => (JsonNode)new JsonObject
                {
                    ["key"] = f.Key.Name, ["share"] = Math.Round(f.Share, 3), ["correlation"] = Math.Round(f.Correlation, 3),
                }).ToArray()),
            };
            if (key != null) result["key"] = KeyJson(key, false);
            string outText = outside.Count == 0 ? "every note is in the key" :
                $"{Transforms.Count(outside.Count, "note")} outside it: " + Transforms.Some(outside.Select(n =>
                    checkedKey.NameOf(n.Pitch) + (n.Start != null ? $" at beat {Theory.Fmt(n.Start.Value)}" : "")).ToList());
            string summary = $"{(key != null ? "In" : "Best fit:")} {checkedKey}, {Math.Round(share * 100)}% of the time is in the key; {outText}. " +
                             "Best fits: " + string.Join(", ", best.Select(f => $"{f.Key} ({Math.Round(f.Share * 100)}%, r {Theory.Fmt(Math.Round(f.Correlation, 2))})")) + ".";
            return ToolResult.Ok(summary, result);
        }

        // ---- JSON ----

        static JsonObject ChordJson(Chord chord, List<SpelledPitch> voiced, MusicKey? key, string symbol, string? numeral)
        {
            var o = new JsonObject { ["symbol"] = symbol };
            if (numeral != null) o["numeral"] = numeral;
            o["root"] = chord.Root.ToString();
            if (chord.Bass is NoteName b) o["bass"] = b.ToString();
            o["tones"] = Strings(chord.ToneNames.Select(n => n.ToString()));
            o["intervals"] = Strings(chord.Tones.Select(t => t.Label));
            o["pitches"] = PitchArray(voiced);
            if (key != null)
            {
                var outside = chord.AllNames.Where(n => !key.Contains(n.PitchClass)).Select(n => n.ToString()).Distinct().ToList();
                o["in_key"] = outside.Count == 0;
                o["out_of_key"] = Strings(outside);
            }
            return o;
        }

        static string OutOfKey(JsonObject chord, MusicKey? key) =>
            key != null && chord["out_of_key"] is JsonArray o && o.Count > 0 ? $"; outside {key}: {string.Join(" ", o.Select(x => x!.GetValue<string>()))}" : "";

        static JsonObject KeyJson(MusicKey key, bool full)
        {
            var o = new JsonObject { ["name"] = key.Name, ["tonic"] = key.Tonic.ToString(), ["scale"] = key.Scale.Name };
            if (!full) return o;
            o["signature"] = new JsonObject
            {
                ["sharps"] = Math.Max(0, key.Signature),
                ["flats"] = Math.Max(0, -key.Signature),
                ["accidentals"] = Strings(key.SignatureAccidentals.Select(n => n.ToString())),
            };
            if (key.Relative is MusicKey r) o["relative"] = r.Name;
            if (key.Parallel is MusicKey p) o["parallel"] = p.Name;
            return o;
        }

        static JsonArray Diatonic(IEnumerable<DiatonicChord> chords) => new(chords.Select(c => (JsonNode)new JsonObject
        {
            ["numeral"] = c.Numeral, ["symbol"] = c.Symbol, ["tones"] = Strings(c.Tones.Select(t => t.ToString())),
        }).ToArray());

        static JsonArray PitchArray(IEnumerable<SpelledPitch> pitches) =>
            new(pitches.Select(p => (JsonNode)new JsonObject { ["pitch"] = p.Midi, ["name"] = p.Name }).ToArray());

        static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode)s).ToArray());

        static string Names(IEnumerable<SpelledPitch> pitches) => string.Join(" ", pitches.Select(p => p.Name));

        static List<int> Pitches(JsonArray list, string what) => list.Select((p, i) => NoteList.ParsePitch(p, $"{what}[{i}]")).ToList();

        static string? Str(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        static double? Num(JsonObject a, string name) => TryNumber(a[name], out double d) ? d : null;

        static int? Int(JsonObject a, string name) => Num(a, name) is double d ? (int)Math.Round(d) : null;

        static bool? Bool(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
    }
}
