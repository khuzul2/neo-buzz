// Evaluation task "Beat" (docs/specs/mcp-server.md §10): 4 bars of 124 BPM house with kick, clap,
// hats and bass, from an empty song. It renders; there are onsets on the beats; the bass is in
// key; no clipping.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal sealed class BeatTask : EvalTask
    {
        public override string Name => "beat";
        public override string Title => "Beat";

        const double Bpm = 124;
        static readonly string[] Parts = { "Kick", "Clap", "Hats", "Bass" };

        public override string Brief(TaskContext ctx) =>
            "Make 4 bars of 124 BPM house in the empty song that is open: a kick on every beat (four on the floor), a clap on beats 2 and 4, " +
            "hi-hats, and a bassline in one key. Use the Neo machines (gear_list); Neo Poly makes drums too: a sine with a fast pitch drop " +
            "for the kick, filtered noise for the clap and the hats. Name the machines Kick, Clap, Hats and Bass. Render it and check it " +
            "with analyze: nothing may clip (true peak below 0 dBTP). When it's done, save it with " +
            $"song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Solve(Studio s, TaskContext ctx)
        {
            s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = Bpm });
            Recipes.Kick(s, "Kick", -6);
            Recipes.Clap(s, "Clap", -12);
            Recipes.Hat(s, "Hats", -16);
            Recipes.Preset(s, "Bass", "Bass — Classic Sub", -10);
            Recipes.Pattern(s, "Kick", "Four", 16, Recipes.Every("F1", 16, 1, length: 0.25, velocity: 120));
            Recipes.Pattern(s, "Clap", "Backbeat", 16, Recipes.Every("D4", 16, 2, offset: 1, length: 0.25, velocity: 110));
            Recipes.Pattern(s, "Hats", "Offbeats", 16, Recipes.Every("C6", 16, 1, offset: 0.5, length: 0.125, velocity: 96));
            // An A minor off-beat bassline: A, A, C, G per bar.
            var bass = new List<JsonObject>();
            string[] roots = { "A2", "A2", "C3", "G2" };
            for (int bar = 0; bar < 4; bar++)
                for (int k = 0; k < 4; k++)
                    bass.Add(J.Note(roots[bar], bar * 4 + k + 0.5, 0.4, 110));
            Recipes.Pattern(s, "Bass", "Offbeat", 16, bass);
            foreach (var (m, p) in new[] { ("Kick", "Four"), ("Clap", "Backbeat"), ("Hats", "Offbeats"), ("Bass", "Offbeat") }) Recipes.Place(s, m, p, 0);
            // Check it as the brief asks; turn everything down if it gets near clipping.
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 1 });
            var m2 = s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(r["id"]), ["metrics"] = J.A("peaks", "tempo") });
            double tp = J.Num(m2["peaks"]?["true_peak_dbtp"]);
            if (tp > -3)
            {
                var g = s.Graph();
                s.C.Ok("connection_set", new JsonObject
                {
                    ["changes"] = new JsonArray(Parts.Select(p => (JsonNode)new JsonObject
                    {
                        ["from"] = p, ["to"] = "Master",
                        ["gain_db"] = Math.Round(g.Find(p).Outputs.First(o => o.To == "Master").GainDb - (tp + 3), 1),
                    }).ToArray()),
                });
            }
        }

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var f = new Facts();
            var status = s.Status();
            f["tempo_bpm"] = J.Num(status["tempo_bpm"]);
            var g = s.Graph();
            // The parts by name ("Kick", or anything containing it: "Kick 909").
            var names = Parts.ToDictionary(p => p, p => g.Machines.FirstOrDefault(m => m.IsGenerator && m.Name.Contains(p == "Hats" ? "Hat" : p, StringComparison.OrdinalIgnoreCase))?.Name);
            if (names["Clap"] == null) names["Clap"] = g.Machines.FirstOrDefault(m => m.IsGenerator && m.Name.Contains("Snare", StringComparison.OrdinalIgnoreCase))?.Name;
            var found = names.Values.Where(n => n != null).ToList();
            var (mix, parts, how) = s.RenderParts(g, found);
            double beats = mix.ToBeats - mix.FromBeats;
            f["length_beats"] = beats;
            var master = mix.Audio;
            var loud = Meters.Loudness(master);
            f["master_lufs"] = loud.Integrated;
            f["master_true_peak"] = loud.TruePeakDb;
            f["master_clipped"] = loud.Clipped;
            f["master_silent"] = Meters.Silent(master) ? 1 : 0;
            var tempo = Meters.Tempo(master);
            f["measured_bpm"] = tempo.Bpm;
            int audible = 0;
            foreach (var p in Parts)
            {
                var a = names[p] != null ? parts.GetValueOrDefault(names[p]) : null;
                bool heard = a != null && !Meters.Silent(a) && Meters.PeakDb(a) > -60 && g.Reaches(names[p]);
                if (heard) audible++;
                f.Text($"part_{p.ToLowerInvariant()}", names[p] == null ? "missing" : $"{names[p]}: {(heard ? "audible" : "silent")}, probed {how.GetValueOrDefault(names[p])}");
            }
            f["parts_audible"] = audible;
            double beat = 60 / Bpm;
            // Kick on every beat, clap on beats 2 and 4 (heard on their own parts; the master if a part is missing).
            var kick = names["Kick"] != null ? parts.GetValueOrDefault(names["Kick"]) : null;
            var clap = names["Clap"] != null ? parts.GetValueOrDefault(names["Clap"]) : null;
            int bars = 4;
            f["kick_on_beats"] = OnBeats(kick ?? master, Enumerable.Range(0, bars * 4).Select(i => i * beat));
            f["clap_on_backbeats"] = OnBeats(clap ?? master, Enumerable.Range(0, bars * 2).Select(i => (2 * i + 1) * beat));
            // The bass: in one key as written, and as heard.
            if (names["Bass"] != null)
            {
                var arrangement = s.C.Ok("arrangement_get");
                var notes = s.PlacedNotes(names["Bass"], arrangement, s.Notes()).Where(n => J.Num(n["start"]) < 16).ToList();
                var (share, scale) = ScaleFit(notes.Select(n => ((int)J.Num(n["pitch"]), J.Num(n["length"]))));
                f["bass_notes"] = notes.Count;
                f["bass_in_scale"] = notes.Count == 0 ? 0 : share;
                f.Text("bass_scale", scale);
                if (parts.GetValueOrDefault(names["Bass"]) is StereoAudio ba && !Meters.Silent(ba))
                {
                    var pt = Meters.Pitch(ba, 25, 1000);
                    var written = notes.Select(n => ((int)J.Num(n["pitch"]) % 12 + 12) % 12).ToHashSet();
                    double voiced = pt.Notes.Sum(n => n.End - n.Start);
                    f["bass_heard_in_written"] = voiced > 0 ? pt.Notes.Where(n => written.Contains((n.Midi % 12 + 12) % 12)).Sum(n => n.End - n.Start) / voiced : double.NaN;
                }
            }
            return f;
        }

        /// <summary>The share of <paramref name="times"/> with an onset within 35 ms.</summary>
        static double OnBeats(StereoAudio a, IEnumerable<double> times)
        {
            var onsets = Meters.Onsets(a);
            var ts = times.Where(t => t < a.Seconds).ToList();
            if (ts.Count == 0) return 0;
            return ts.Count(t => onsets.Any(o => Math.Abs(o - t) <= 0.035)) / (double)ts.Count;
        }

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["master_silent"] == 0 && r["master_lufs"] > -45;
            return new List<Criterion>
            {
                new("renders", sounds ? Linear(r["length_beats"], 16, 8) : 0, $"silent={r["master_silent"] == 1}, {J.F(r["master_lufs"], 1)} LUFS, {J.F(r["length_beats"], 1)} beats (4 bars = 16)"),
                new("tempo", Math.Abs(r["tempo_bpm"] - Bpm) < 0.5 ? 1 : 0, $"song {J.F(r["tempo_bpm"], 0)} BPM, heard {J.F(r["measured_bpm"], 1)} BPM"),
                new("parts", r["parts_audible"] / Parts.Length, $"{J.F(r["parts_audible"], 0)}/4 audible: " + string.Join("; ", Parts.Select(p => r.Text($"part_{p.ToLowerInvariant()}")))),
                new("kick_on_beats", Linear(r["kick_on_beats"], 0.9, 0.4), $"{J.F(100 * r["kick_on_beats"], 0)}% of the 16 beats have an onset within 35 ms"),
                new("clap_on_2_and_4", Linear(r["clap_on_backbeats"], 0.85, 0.4), $"{J.F(100 * r["clap_on_backbeats"], 0)}% of the 8 backbeats"),
                new("bass_in_key", r.Has("bass_in_scale") ? Linear(r["bass_in_scale"], 0.95, 0.7) * (r.Has("bass_heard_in_written") && r["bass_heard_in_written"] < 0.6 ? 0.5 : 1) : 0,
                    r.Has("bass_in_scale") ? $"{J.F(100 * r["bass_in_scale"], 0)}% of {J.F(r["bass_notes"], 0)} notes' time in {r.Text("bass_scale")}; heard pitches in the written ones {J.F(100 * r["bass_heard_in_written"], 0)}%" : "no bass"),
                new("no_clipping", !sounds ? 0 : r["master_clipped"] == 0 && r["master_true_peak"] < 0 ? 1 : r["master_true_peak"] < 1 ? 0.5 : 0,
                    $"true peak {J.F(r["master_true_peak"], 2)} dBTP, {J.F(r["master_clipped"], 0)} clipped samples"),
            };
        }
    }
}
