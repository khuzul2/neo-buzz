// Evaluation task "Song" (docs/specs/mcp-server.md §10): a 2-minute synthwave track at 92 BPM from
// an empty song. Scored: length, tempo, sections marked and differing in energy, one key across the
// sections, the loudness target, no silent gaps, the parts there; then a human rating (recorded by
// hand, not here).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal sealed class SongTask : EvalTask
    {
        public override string Name => "song";
        public override string Title => "Song";

        const double Bpm = 92;

        public override string Brief(TaskContext ctx) =>
            "Write a 2-minute synthwave track at 92 BPM in a minor key, starting from the empty song that is open: drums (kick, snare or " +
            "clap, hats), bass, chords and a lead, arranged in sections (at least an intro, a verse, a chorus and an outro) that you mark " +
            "with arrangement_markers, every section in the same key and no silent gaps. Use the Neo machines (gear_list). Mix and master " +
            "it to about -14 LUFS integrated with a true peak of at most -1 dBTP. Check your work with render and analyze. When it's done, " +
            $"save it with song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Solve(Studio s, TaskContext ctx)
        {
            s.C.Ok("guide", new JsonObject { ["topic"] = "workflow" });
            s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = Bpm });
            Recipes.Kick(s, "Kick", -8);
            Recipes.Clap(s, "Snare", -12);
            Recipes.Hat(s, "Hats", -18);
            Recipes.Preset(s, "Bass", "Bass — Classic Sub", -12);
            Recipes.Preset(s, "Chords", "Pad — Warm Strings", -10);
            Recipes.Preset(s, "Lead", "Lead — Smooth Saw", -16);
            Recipes.Add(s, "Neo Verb", "Space", 0, insertAfter: "Lead");
            Recipes.Set(s, "Space", ("Mix", "25%"), ("Decay", "2.2 s"));

            // Harmony from the theory tool: i–VI–III–VII in A minor, a bar each.
            var prog = s.C.Ok("theory", new JsonObject { ["op"] = "progression", ["key"] = "A minor", ["progression"] = "i VI III VII", ["octave"] = 3, ["beats_per_chord"] = 4 });
            s.C.Ok("pattern_create", new JsonObject { ["machine"] = "Chords", ["name"] = "Prog", ["length_beats"] = 16 });
            s.C.Ok("notes_write", new JsonObject { ["machine"] = "Chords", ["pattern"] = "Prog", ["notes"] = prog["notes"]!.DeepClone() });
            // The same chords played softly, for the intro and the outro.
            var soft = prog["notes"]!.DeepClone().AsArray();
            foreach (var n in soft.OfType<JsonObject>()) n["velocity"] = 50;
            s.C.Ok("pattern_create", new JsonObject { ["machine"] = "Chords", ["name"] = "Soft", ["length_beats"] = 16 });
            s.C.Ok("notes_write", new JsonObject { ["machine"] = "Chords", ["pattern"] = "Soft", ["notes"] = soft });
            var roots = J.Objects(prog["chords"]).Select(c => J.Str(c["root"])).ToList();
            var bass = new List<JsonObject>();
            for (int bar = 0; bar < roots.Count; bar++)
            {
                int p = ReBuzz.Agent.Model.Pitch.Parse(roots[bar] + "2");
                if (p < 40) p += 12;
                for (int k = 0; k < 8; k++) bass.Add(J.Note(p, bar * 4 + k * 0.5, 0.4, k % 2 == 0 ? 110 : 85));
            }
            Recipes.Pattern(s, "Bass", "Drive", 16, bass);
            Recipes.Pattern(s, "Kick", "Four", 16, Recipes.Every("A1", 16, 1, length: 0.25, velocity: 120));
            Recipes.Pattern(s, "Snare", "Back", 16, Recipes.Every("D4", 16, 2, offset: 1, length: 0.25, velocity: 110));
            Recipes.Pattern(s, "Hats", "Eighths", 16, Recipes.Every("C6", 16, 0.5, length: 0.1, velocity: 90));
            var hook = new (string, double, double)[] { ("E5", 0, 2), ("C5", 2, 1), ("D5", 3, 1), ("C5", 4, 2), ("A4", 6, 2), ("G4", 8, 1), ("A4", 9, 1), ("C5", 10, 2), ("B4", 12, 2), ("G4", 14, 2) };
            Recipes.Pattern(s, "Lead", "Hook", 16, hook.Select(n => J.Note(n.Item1, n.Item2, n.Item3 - 0.1, 100)));
            // Check the hook against the key before using it.
            s.C.Ok("theory", new JsonObject { ["op"] = "fit", ["key"] = "A minor", ["notes"] = new JsonArray(hook.Select(n => (JsonNode)n.Item1).ToArray()) });

            // 48 bars: intro 8 (soft chords), verse 16 (chords, kick, hats, bass), chorus 16 (+ snare, lead), outro 8 (soft chords, bass).
            Recipes.Place(s, "Chords", "Soft", 0, 16, 2);
            Recipes.Place(s, "Chords", "Prog", 32, 16, 8);
            Recipes.Place(s, "Chords", "Soft", 160, 16, 2);
            Recipes.Place(s, "Hats", "Eighths", 32, 16, 8);
            Recipes.Place(s, "Kick", "Four", 32, 16, 8);
            Recipes.Place(s, "Snare", "Back", 96, 16, 4);
            Recipes.Place(s, "Bass", "Drive", 32, 16, 10);
            Recipes.Place(s, "Lead", "Hook", 96, 16, 4);
            s.C.Ok("arrangement_markers", new JsonObject
            {
                ["set"] = J.A(
                    new JsonObject { ["name"] = "Intro", ["at"] = 0 }, new JsonObject { ["name"] = "Verse", ["at"] = "9.1" },
                    new JsonObject { ["name"] = "Chorus", ["at"] = "25.1" }, new JsonObject { ["name"] = "Outro", ["at"] = "41.1" }),
            });
            s.C.Ok("analyze_mix", new JsonObject { ["section"] = "Chorus" });
            // Gentle glue: the sections keep their difference in level.
            Recipes.MasterBus(s, glueDb: 6, ratio: "2:1");
            Recipes.MasterTo(s, -14, rounds: 3);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(s.C.Ok("render", new JsonObject { ["section"] = "Intro", ["tail_seconds"] = 0 })["id"]), ["metrics"] = J.A("loudness") });
        }

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var f = new Facts();
            f["tempo_bpm"] = J.Num(s.Status()["tempo_bpm"]);
            var markers = s.Markers();
            f["markers"] = markers.Count;
            f.Text("marker_names", string.Join(", ", markers.Select(m => $"{m.Name}@{m.At}")));
            var g = s.Graph();
            var gens = g.Generators.Select(m => m.Name).ToList();
            var (mix, parts, _) = s.RenderParts(g, gens, tail: 0);
            var a = mix.Audio;
            double bpm = f["tempo_bpm"] > 0 ? f["tempo_bpm"] : Bpm;
            f["seconds"] = (mix.ToBeats - mix.FromBeats) * 60 / bpm;
            f["silent"] = Meters.Silent(a) ? 1 : 0;
            var l = Meters.Loudness(a);
            f["lufs"] = l.Integrated;
            f["true_peak"] = l.TruePeakDb;
            f["clipped"] = l.Clipped;
            f["longest_gap_seconds"] = Meters.LongestGap(a);
            f["parts_audible"] = gens.Count(p => parts.GetValueOrDefault(p) is StereoAudio pa && !Meters.Silent(pa) && Meters.PeakDb(pa) > -60 && g.Reaches(p));
            if (f["silent"] == 1) return f;
            var key = Meters.Key(a);
            f["key_correlation"] = key.Correlation;
            f.Text("key", key.Name);
            // Sections: their own key and loudness.
            var sections = markers.OrderBy(m => m.At).ToList();
            var keyNames = new List<string>();
            var loud = new List<double>();
            int consistent = 0, counted = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                double from = sections[i].At * 60 / bpm, to = (i + 1 < sections.Count ? sections[i + 1].At : mix.ToBeats) * 60 / bpm;
                if (to - from < 2 || from >= a.Seconds) continue;
                var part = Meters.Slice(a, from, Math.Min(to, a.Seconds));
                if (Meters.Silent(part)) continue;
                counted++;
                var k = Meters.Key(part);
                bool same = SameScale(k.Tonic, k.Minor, key.Tonic, key.Minor);
                if (same) consistent++;
                keyNames.Add($"{sections[i].Name}: {k.Name}{(same ? "" : " (differs)")}");
                loud.Add(Meters.Loudness(part).Integrated);
            }
            f["sections_counted"] = counted;
            f["sections_in_key"] = consistent;
            f.Text("section_keys", string.Join("; ", keyNames));
            var finite = loud.Where(x => !double.IsInfinity(x) && !double.IsNaN(x)).ToList();
            f["section_contrast_lu"] = finite.Count >= 2 ? finite.Max() - finite.Min() : 0;
            f.Text("section_lufs", string.Join(", ", loud.Select(x => J.F(x, 1))));
            f.Text("human_rating", "not rated");
            return f;
        }

        /// <summary>The same key or its relative (the same notes).</summary>
        static bool SameScale(int t1, bool m1, int t2, bool m2)
        {
            int Major(int t, bool minor) => minor ? (t + 3) % 12 : t % 12;
            return Major(t1, m1) == Major(t2, m2);
        }

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            double keyShare = r["sections_counted"] > 0 ? r["sections_in_key"] / r["sections_counted"] : 0;
            return new List<Criterion>
            {
                new("length", sounds ? Band(r["seconds"], 105, 140, 40) : 0, $"{J.F(r["seconds"], 1)} s of song (2 minutes: 105–140 s)"),
                new("tempo", Math.Abs(r["tempo_bpm"] - Bpm) < 0.5 ? 1 : 0, $"{J.F(r["tempo_bpm"], 0)} BPM"),
                new("sections", Math.Min(1, r["markers"] / 4), $"{J.F(r["markers"], 0)} markers: {r.Text("marker_names")}"),
                new("section_energy", sounds ? Linear(r["section_contrast_lu"], 2, 0.3) : 0, $"sections' loudness {r.Text("section_lufs")} LUFS: {J.F(r["section_contrast_lu"], 1)} LU apart"),
                new("key_consistency", sounds ? keyShare * (r["key_correlation"] >= 0.5 ? 1 : 0.5) : 0,
                    $"song in {r.Text("key")} (correlation {J.F(r["key_correlation"])}); {r.Text("section_keys")}"),
                new("loudness", sounds ? Linear(Math.Abs(r["lufs"] + 14), 1.5, 4) : 0, $"{J.F(r["lufs"], 1)} LUFS integrated (about -14)"),
                new("true_peak", sounds ? Linear(r["true_peak"], -1, 0.5) : 0, $"{J.F(r["true_peak"], 2)} dBTP (≤ -1), {J.F(r["clipped"], 0)} clipped samples"),
                new("no_silent_gaps", sounds ? Linear(r["longest_gap_seconds"], 0.5, 2) : 0, $"longest stretch under -60 dBFS: {J.F(r["longest_gap_seconds"], 2)} s"),
                new("parts", Linear(r["parts_audible"], 4, 1), $"{J.F(r["parts_audible"], 0)} audible generators (drums, bass, chords, lead: 4 or more)"),
            };
        }
    }
}
