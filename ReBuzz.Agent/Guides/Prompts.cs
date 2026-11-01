#nullable disable
// Agent server (docs/specs/mcp-server.md §6, phase A5): the prompts (slash commands in Claude
// Code): compose, design-sound, mix, master, critique. Each expands to a step plan built from the
// tools, with the brief's numbers worked out (bars from a length, a loudness target from a
// platform), followed by the relevant guides in full.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Guides
{
    public static partial class GuideLibrary
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Genres with a template in the composition guide: aliases → (template heading, default BPM, BPM range).</summary>
        static readonly (string[] Names, string Template, int Bpm, string Range)[] Genres =
        {
            (new[] { "house", "deep house", "tech house", "garage" }, "House", 124, "120–128"),
            (new[] { "techno", "minimal" }, "Techno", 130, "125–135"),
            (new[] { "dnb", "drum and bass", "drum & bass", "drum n bass", "jungle", "liquid" }, "Drum and bass", 174, "170–176"),
            (new[] { "synthwave", "retrowave", "outrun", "synthpop" }, "Synthwave", 100, "85–118"),
            (new[] { "hip-hop", "hip hop", "hiphop", "boom bap", "rap" }, "Hip-hop", 90, "85–95"),
            (new[] { "trap" }, "Hip-hop", 140, "130–150 (half-time feel)"),
            (new[] { "ambient", "drone", "downtempo" }, "Ambient", 70, "60–90"),
            (new[] { "pop", "dance pop", "electropop" }, "Pop", 110, "100–128"),
            (new[] { "lo-fi", "lofi", "lo fi", "chillhop" }, "Lo-fi", 80, "70–90"),
        };

        /// <summary>The five prompts. The ReBuzz side passes them to McpServer (prompts: GuideLibrary.Prompts()).</summary>
        public static IReadOnlyList<PromptDef> Prompts() => new[]
        {
            new PromptDef
            {
                Name = "compose",
                Title = "Compose a track",
                Description = "Writes a track from a brief: genre template, harmony, drums, bass and melody, arrangement and a quick level check, rendered and measured. Includes the composition guide.",
                Arguments = new[]
                {
                    new PromptArgument { Name = "genre", Description = "house, techno, dnb, synthwave, hip-hop, trap, ambient, pop, lo-fi, or another style", Required = true },
                    new PromptArgument { Name = "mood", Description = "e.g. melancholic, uplifting, dark, dreamy, tense, nostalgic, aggressive, chill" },
                    new PromptArgument { Name = "tempo", Description = "BPM, e.g. 92 (default: the genre's)" },
                    new PromptArgument { Name = "length", Description = "e.g. \"2 minutes\", \"3:30\", \"64 bars\" (default: the template's form)" },
                },
                Render = RenderCompose,
            },
            new PromptDef
            {
                Name = "design-sound",
                Title = "Design a sound",
                Description = "Makes a sound from a description on a Neo synth (or a given machine): preset, parameters, an audition pattern, renders and measurements. Includes the sound-design guide and the machine's page.",
                Arguments = new[]
                {
                    new PromptArgument { Name = "machine", Description = "A machine in the song (\"Bass\") or a gear to add (\"Neo Poly\", \"Neo Wave\", \"Neo FM6\"); default: the agent picks" },
                    new PromptArgument { Name = "description", Description = "What it should sound like, e.g. \"warm detuned pad with slow filter movement\"", Required = true },
                },
                Render = RenderDesignSound,
            },
            new PromptDef
            {
                Name = "mix",
                Title = "Mix the song",
                Description = "Mixes the song step by step: gain staging, frequency slots, sidechain, EQ and compression, space, stereo, each change measured with probes and analyze. Includes the mixing guide.",
                Arguments = new[]
                {
                    new PromptArgument { Name = "goals", Description = "e.g. \"punchier kick, wider pads, vocal-style lead on top\" (default: a clean, balanced mix ready for mastering)" },
                },
                Render = RenderMix,
            },
            new PromptDef
            {
                Name = "master",
                Title = "Master the song",
                Description = "Masters to a platform's loudness and true-peak target with corrective EQ, glue compression, saturation and a limiter, measuring after every change. Includes the mastering guide.",
                Arguments = new[]
                {
                    new PromptArgument { Name = "target", Description = "The target platform: streaming (default), club, broadcast (EBU R128), broadcast-us (ATSC A/85), apple, cd, podcast, or a level such as \"-12 LUFS\"" },
                },
                Render = RenderMaster,
            },
            new PromptDef
            {
                Name = "critique",
                Title = "Critique the song",
                Description = "Listens (renders and measures) and reports the biggest problems with evidence and the fix for each, without changing the song. Includes every guide's checklist.",
                Arguments = new[]
                {
                    new PromptArgument { Name = "scope", Description = "all (default), composition, sound, mix, master, or a section (marker) or machine name" },
                },
                Render = RenderCritique,
            },
        };

        static string Arg(IReadOnlyDictionary<string, string> args, string name) =>
            args != null && args.TryGetValue(name, out string v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        static void AppendGuides(StringBuilder sb, params string[] topics)
        {
            foreach (var t in topics)
            {
                string text = Get(t);
                if (text == null) continue;
                sb.Append("\n\n---\n\n<!-- guide: ").Append(t).Append(" (rebuzz://guides/").Append(t).Append(") -->\n\n").Append(text.Trim()).Append('\n');
            }
        }

        // ---- compose ---------------------------------------------------------------------------

        static string RenderCompose(IReadOnlyDictionary<string, string> args)
        {
            string genre = Arg(args, "genre") ?? "a genre of your choice";
            string mood = Arg(args, "mood");
            string tempoText = Arg(args, "tempo");
            string length = Arg(args, "length");
            var g = Genres.FirstOrDefault(x => x.Names.Any(n => string.Equals(n, genre, StringComparison.OrdinalIgnoreCase)));
            if (g.Names == null) g = Genres.FirstOrDefault(x => x.Names.Any(n => genre.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0));
            double? bpm = ParseNumber(tempoText) ?? (g.Names != null ? g.Bpm : null);
            double? seconds = ParseSeconds(length);
            int? bars = ParseBars(length);
            if (bars == null && seconds is double s && bpm is double b) bars = (int)(Math.Round(s * b / 240 / 4) * 4);

            var sb = new StringBuilder();
            sb.Append($"Compose a {(mood != null ? mood + " " : "")}{genre} track in ReBuzz");
            if (bpm != null) sb.Append($" at {bpm.Value.ToString("0", Inv)} BPM");
            if (length != null) sb.Append($", {length} long");
            sb.Append(".\n\n");
            sb.Append("Brief, worked out:\n");
            if (g.Names != null) sb.Append($"- Template: \"{g.Template}\" in the composition guide below (tempo range {g.Range} BPM, default {g.Bpm}).\n");
            else sb.Append($"- No template for \"{genre}\": take the nearest template in the composition guide and say which.\n");
            if (tempoText != null && bpm == null) sb.Append($"- Tempo \"{tempoText}\" isn't a number; use the template's.\n");
            if (bars != null && bpm != null)
                sb.Append($"- Length: {bars} bars of 4/4 at {bpm.Value.ToString("0", Inv)} BPM ({(bars.Value * 240 / bpm.Value).ToString("0", Inv)} s). Scale the template's sections to fit, keeping 4- and 8-bar phrases.\n");
            else if (length != null) sb.Append($"- Length \"{length}\": bars = seconds × BPM ÷ 240 in 4/4; scale the template's sections to fit.\n");
            else sb.Append("- Length: the template's form.\n");
            sb.Append(mood != null ? $"- Mood \"{mood}\": pick mode, progression, register and sounds from the mood table in the composition guide.\n" : "- Mood: choose one and say it; the mood table sets mode, progression and register.\n");
            sb.Append(@"
Plan. Say each step in a sentence before doing it; render and measure before you claim a result.
1. Orient: `studio_status`, then `machine_list`. If the song isn't empty, build on it or ask; never `song_new` over unsaved work without saving.
2. Song: `song_set(tempo_bpm=…)`, then `arrangement_markers(set=[…])` with every section of the form at its bar (""9.1"" is bar 9).
3. Instruments: one machine per role (Kick, Snare, Hats, Bass, Chords, Lead, Pad, FX): `machine_add(gear=…, name=…)`, `preset_load` from the sound-design guide's recipes, `param_set` for the rest.
4. Harmony: `theory(op=""progression"", key=…, progression=…, octave=3)`; write its notes with `notes_write` into a Chords pattern (`pattern_create` with the section's length in beats).
5. Drums: the genre's grid from the composition guide → `notes_write`; then `notes_transform` with quantize swing and humanize as the template says.
6. Bass and melody: roots and approach notes for the bass; a 1–2 bar motif developed with `notes_transform` (sequence, invert, stretch) for the lead. Check them with `theory(op=""fit"", key=…, notes=[…])`.
7. Arrangement: patterns per section, placed with `arrangement_place(machine=…, pattern=…, at=…, repeat_every_beats=…, times=…)`, following the energy table (add or remove layers every 8 bars); builds and fills with `automation_write`.
8. Listen: `render(section=…)` for each section as you go, then the whole song with `render()`; `analyze(source=…, metrics=[""loudness"", ""peaks"", ""key"", ""tempo"", ""spectrum""])`. The key and tempo must read back as written; the drop or chorus 2–6 LU louder than the breakdown or verse.
9. Quick balance: probe every part (`render(probes=[…])`), set levels with `connection_set` so no part clips and the master peaks at or below -6 dBTP (the mixing guide has the numbers).
10. Save: `song_save(path=…)` as .bmxml.
Report the form (sections and bars), key, tempo, the sounds, and the measured loudness, peak, key and tempo.
");
            AppendGuides(sb, "workflow", "composition");
            return sb.ToString();
        }

        // ---- design-sound ----------------------------------------------------------------------

        static string RenderDesignSound(IReadOnlyDictionary<string, string> args)
        {
            string machine = Arg(args, "machine");
            string description = Arg(args, "description") ?? "a sound of your choice";
            string gear = machine == null ? null : MachineManual.CuratedGear.FirstOrDefault(c =>
                MachineManual.Slug(c) == MachineManual.Slug(machine) || machine.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0);

            var sb = new StringBuilder();
            sb.Append($"Design this sound in ReBuzz: \"{description}\"");
            sb.Append(machine == null ? ".\n\n" : gear != null ? $", on {gear}.\n\n" : $", on the machine \"{machine}\".\n\n");
            sb.Append(@"Plan. Render and measure every version; don't describe a sound you haven't measured.
1. Translate the description into targets on the four axes of the sound-design guide (envelope, spectrum, movement, space), each with a number you can measure: attack and decay in ms, spectral centroid and tilt, which band leads, modulation rate, stereo correlation.
2. Machine: ");
            if (gear != null) sb.Append($"add one with `machine_add(gear=\"{gear}\", name=…)` (or use the song's machine of that gear if one is named so). ");
            else if (machine != null) sb.Append($"use \"{machine}\" if the song has it (`machine_list`); if \"{machine}\" is a gear, `machine_add(gear=\"{machine}\", name=…)`. ");
            else sb.Append("pick the gear from the sound-design guide (Neo Poly: analog pads, basses, leads, drums; Neo Wave: evolving and digital; Neo FM6: electric pianos, bells, metallic) and `machine_add` it. ");
            sb.Append(@"Read it with `machine_describe(machine=…)`.
3. Start close: `preset_list(machine=…, search=…)`, `preset_load` the nearest preset, or start from the guide's recipe.
4. Audition pattern: `pattern_create(machine=…, name=""Audition"", length_beats=8)`, notes in the role's register (bass A1–A2, pad chord A3–E4, lead A4, bell C5), placed with `arrangement_place` at 0 in an empty song or after the song's end (that moves the end: note the length from `studio_status` and restore it with `song_set(song_end_beats=…)` when done).
5. Loop, at most six times: `param_set` (one idea per change) → `render(from=…, to=…, solo=[…])` → `analyze(source=…, metrics=[""spectrum"", ""dynamics"", ""pitch"", ""stereo""])` → compare with the targets. `snapshot_save` versions worth keeping; `undo` changes that measure worse.
6. Effects that are part of the sound (Neo Drive, Neo Comp, Neo Verb) go right after it with `machine_add(gear=…, insert_after=…)`; measure with and without (`render(mute=[…])` doesn't bypass, so use `machine_set(machine=…, bypass=true)` and back).
7. Keep it: `preset_save(machine=…, name=…, comment=…)` when the user wants it reusable.
Report the final parameters that matter (with units), the measured targets, and how to vary the sound.
");
            AppendGuides(sb, "sound-design");
            if (gear != null)
            {
                string curated = MachineManual.Curated(gear);
                if (curated != null)
                    sb.Append("\n\n---\n\n<!-- machine page: ").Append(gear).Append(" (").Append(MachineManual.UriFor(gear)).Append(") -->\n\n# ")
                      .Append(gear).Append("\n\n").Append(curated.Trim()).Append('\n');
            }
            return sb.ToString();
        }

        // ---- mix -------------------------------------------------------------------------------

        static string RenderMix(IReadOnlyDictionary<string, string> args)
        {
            string goals = Arg(args, "goals");
            var sb = new StringBuilder();
            sb.Append("Mix the current song in ReBuzz").Append(goals != null ? $" with these goals: {goals}.\n\n" : ": clean, balanced and ready for mastering.\n\n");
            sb.Append(@"Plan. Change one thing at a time and measure it; keep a snapshot of the mix before each stage.
1. Inventory: `studio_status`, `machine_list` (every source, effect and connection gain), `arrangement_markers` (the loudest section to work on).
2. Baseline: `snapshot_save(name=""mix 0"")`; `render(section=…, probes=[every source and bus])`; `analyze` the master with metrics loudness, peaks, spectrum, stereo, dynamics, and each probe (""rN:Bass"") with spectrum.
3. Gain staging: `connection_set` so sources peak -16 to -8 dBFS and the master peaks at or below -6 dBTP, before any processing.
4. Balance: set the faders (connection gains) against the guide's relative loudness table and a reference if the user gave one (`analyze(source=""<reference.wav>"", …)`).
5. Slots: high-pass everything that isn't bass or kick; resolve kick and bass (sidechain with Neo Comp's Sidechain input: `connect(from=""Kick"", to=…, input=""Sidechain"")`); find fights by comparing the probes' bands.
6. EQ and compression per source from the guide's tables (Neo EQ when installed; read its parameter names with `machine_describe`).
7. Space: one or two send reverbs (Neo Verb at 100% mix), pre-delay from the tempo table; stereo: lows mono below about 120 Hz.
8. After each stage: render the same range, analyze, compare with the last numbers; `snapshot_diff(a=""mix 0"")` lists what changed; `undo` or `snapshot_restore` what measured worse.
9. Finish: the whole song rendered, master true peak at or below -6 dBTP, no clipping, correlation above 0.2, lows mono; `song_save`.
Report each change with its before and after numbers, and anything you couldn't fix.
");
            AppendGuides(sb, "mixing");
            return sb.ToString();
        }

        // ---- master ----------------------------------------------------------------------------

        /// <summary>A platform's integrated loudness target (LUFS), tolerance (LU) and true-peak ceiling (dBTP).</summary>
        public static (string Name, double Lufs, double Tolerance, double Ceiling) MasterTarget(string target)
        {
            string t = (target ?? "streaming").Trim().ToLowerInvariant();
            if (ParseNumber(t) is double lufs && lufs < 0) return ($"{lufs.ToString("0.#", Inv)} LUFS", lufs, 0.5, -1);
            if (t.Contains("atsc") || t.Contains("us") && t.Contains("broadcast") || t.Contains("a/85")) return ("broadcast (ATSC A/85)", -24, 2, -2);
            if (t.Contains("broadcast") || t.Contains("ebu") || t.Contains("r128") || t.Contains("tv")) return ("broadcast (EBU R128)", -23, 0.5, -1);
            if (t.Contains("club") || t.Contains("dj") || t.Contains("vinyl") || t.Contains("edm")) return ("club", -8, 1, -0.5);
            if (t.Contains("apple") || t.Contains("itunes")) return ("Apple Music (Sound Check)", -16, 1, -1);
            if (t.Contains("podcast")) return ("podcast", -16, 1, -1);
            if (t.Contains("cd") || t.Contains("download")) return ("CD or download", -10, 1, -1);
            return ("streaming (Spotify, YouTube, Tidal, Amazon)", -14, 1, -1);
        }

        static string RenderMaster(IReadOnlyDictionary<string, string> args)
        {
            string target = Arg(args, "target");
            var (name, lufs, tol, ceiling) = MasterTarget(target);
            string L(double x) => x.ToString("0.#", Inv);
            var sb = new StringBuilder();
            sb.Append($"Master the current song in ReBuzz for {name}: integrated {L(lufs)} LUFS ±{L(tol)}, true peak at or below {L(ceiling)} dBTP");
            if (target != null && !name.StartsWith(target, StringComparison.OrdinalIgnoreCase)) sb.Append($" (from \"{target}\")");
            sb.Append(".\n\n");
            sb.Append(@"Plan. Measure after every change; loudness-match before you compare two versions.
1. Check the mix first: `render()` of the whole song and `analyze(source=…, metrics=[""loudness"", ""peaks"", ""dynamics"", ""spectrum"", ""stereo""])`. It needs true peak at or below -3 dBTP (better -6), no clipped samples, lows mono. If not, fix the mix (lower the master's inputs with `connection_set`) before mastering.
2. Chain: route every machine that feeds Master into the first chain stage instead (`machine_list` for the gains; `disconnect`, then `connect` with the same gain), then add each stage with `machine_add(gear=…, name=…, insert_after=…)`: corrective EQ → glue compression (Neo Comp, Glue) → saturation (Neo Drive, Tape) → limiter (Neo Limiter; Neo Comp ""Clean — Brickwall"" if there is none). `machine_describe` gives the exact parameter names of Neo EQ and Neo Limiter.
3. `snapshot_save(name=""master 0"")` before each stage; after it, `render(section=…)` of the loudest section and analyze; keep the change only if it measures as intended.
4. Loudness: raise the gain into the limiter by (target − measured) dB, render the whole song, re-measure; repeat until within the tolerance (usually two or three passes).
5. Final checks on the whole song: integrated loudness and true peak on target; PLR and loudness range in the guide's range for the genre; spectrum tilt and stereo unchanged from the mix except as intended.
6. `song_save`. Report the chain with each stage's settings, and the before and after numbers.
");
            AppendGuides(sb, "mastering");
            return sb.ToString();
        }

        // ---- critique --------------------------------------------------------------------------

        static string RenderCritique(IReadOnlyDictionary<string, string> args)
        {
            string scope = Arg(args, "scope") ?? "all";
            string s = scope.ToLowerInvariant();
            var topics = s switch
            {
                "all" => new[] { "composition", "sound-design", "mixing", "mastering" },
                "composition" or "arrangement" or "song" => new[] { "composition" },
                "sound" or "sounds" or "sound design" or "sound-design" => new[] { "sound-design" },
                "mix" or "mixing" => new[] { "mixing" },
                "master" or "mastering" => new[] { "mastering" },
                _ => new[] { "composition", "sound-design", "mixing" },
            };
            bool named = !new[] { "all", "composition", "arrangement", "song", "sound", "sounds", "sound design", "sound-design", "mix", "mixing", "master", "mastering" }.Contains(s);
            var sb = new StringBuilder();
            sb.Append("Critique the current song in ReBuzz").Append(s == "all" ? "" : named ? $", focusing on \"{scope}\" (a section or a machine)" : $", scope: {scope}").Append(".\n\n");
            sb.Append(@"Rules: read-only. Change nothing in the song (render and analyze change nothing). Every finding needs evidence: a number from a tool, a note, a bar.
Plan.
1. Read: `studio_status`, `machine_list`, `arrangement_get`, `arrangement_markers`, `pattern_list`.
2. Listen: `render()` of the whole song");
            sb.Append(named ? $" (or `render(section=\"{scope}\")` if it is a section; probe it with `render(probes=[\"{scope}\"])` if it is a machine)" : "");
            sb.Append(@" with `probes` on the main parts; `analyze` the master (loudness, peaks, dynamics, spectrum, stereo, key, tempo) and each probe (spectrum, dynamics).
3. Notes: `notes_read` the main patterns; `theory(op=""fit"", key=…, notes=[…])` for wrong notes; compare the form with the genre template.
4. Go through the checklists below; for each item that fails, note the evidence.
5. Report the five biggest problems first, ranked by how much they hurt the track, each as: problem, evidence (numbers), fix (the tool calls that would make it, with values), and how to check the fix. Then smaller notes, then what already works.
");
            sb.Append("\n---\n\n<!-- checklists -->\n");
            foreach (var t in topics)
            {
                string list = Checklist(t);
                if (list == null) continue;
                sb.Append("\n### ").Append(Title(t)).Append(" (guide(topic=\"").Append(t).Append("\") has the details)\n\n")
                  .Append(Regex.Replace(list, @"^## Checklist\s*", "")).Append('\n');
            }
            return sb.ToString();
        }

        // ---- parsing ---------------------------------------------------------------------------

        static double? ParseNumber(string text)
        {
            if (text == null) return null;
            var m = Regex.Match(text.Replace('−', '-'), @"[-+]?\d+(?:\.\d+)?");
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, Inv, out double d) ? d : null;
        }

        /// <summary>"2 minutes", "2:30", "90 s", "1.5 min" → seconds; null for bars or nothing.</summary>
        static double? ParseSeconds(string text)
        {
            if (text == null) return null;
            string t = text.ToLowerInvariant();
            var mmss = Regex.Match(t, @"(\d+):(\d{2})");
            if (mmss.Success) return int.Parse(mmss.Groups[1].Value, Inv) * 60 + int.Parse(mmss.Groups[2].Value, Inv);
            if (t.Contains("bar")) return null;
            double? n = ParseNumber(t);
            if (n == null) return null;
            if (Regex.IsMatch(t, @"\d\s*(s|sec|secs|second|seconds)\b")) return n;
            return n * 60;   // minutes by default
        }

        static int? ParseBars(string text)
        {
            if (text == null) return null;
            var m = Regex.Match(text.ToLowerInvariant(), @"(\d+)\s*bars?");
            return m.Success ? int.Parse(m.Groups[1].Value, Inv) : null;
        }
    }
}
