// Evaluation task "Master" (docs/specs/mcp-server.md §10): "master for streaming" an 8-bar mix
// left with plenty of headroom. Scored: −14 ±1 LUFS integrated, true peak ≤ −1 dBTP, a crest
// factor in electronic music's range, no clipping, and the mix's balance kept.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal sealed class MasterTask : EvalTask
    {
        public override string Name => "master";
        public override string Title => "Master";

        static readonly string[] Parts = { "Kick", "Hats", "Bass", "Pad", "Lead" };

        public override string Brief(TaskContext ctx) =>
            "The open song is a finished but unmastered mix: 8 bars at 120 BPM (Kick, Hats, Bass, Pad, Lead). Master it for streaming: " +
            "integrated loudness -14 LUFS (within 1 LU), true peak at most -1 dBTP, natural dynamics (a crest factor of 6 to 14 dB, no " +
            "squashing), no clipping, and the mix's balance as it is (don't remove or rebalance parts). Build the master chain on a bus: " +
            "route what feeds Master through effects into Master (Neo EQ, Neo Comp, Neo Drive and Neo Limiter are available; " +
            "guide(topic=\"mastering\") has the chain). Measure with render and analyze after each change. When it's done, " +
            $"save it with song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Setup(Studio s, TaskContext ctx)
        {
            s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Recipes.Kick(s, "Kick", -14);
            Recipes.Hat(s, "Hats", -26);
            Recipes.Preset(s, "Bass", "Bass — Classic Sub", -20);
            Recipes.Preset(s, "Pad", "Pad — Warm Strings", -16);
            Recipes.Preset(s, "Lead", "Lead — Smooth Saw", -22);
            Recipes.Pattern(s, "Kick", "Beat", 16, Recipes.Every("F1", 16, 1, length: 0.25, velocity: 120));
            Recipes.Pattern(s, "Hats", "Eighths", 16, Recipes.Every("C6", 16, 0.5, offset: 0, length: 0.1, velocity: 90));
            string[] roots = { "A2", "F2", "C3", "G2" };
            var bass = new List<JsonObject>();
            for (int bar = 0; bar < 4; bar++)
                for (int k = 0; k < 8; k++) bass.Add(J.Note(roots[bar], bar * 4 + k * 0.5, 0.4, k % 2 == 0 ? 110 : 90));
            Recipes.Pattern(s, "Bass", "Eighths", 16, bass);
            string[][] chords = { new[] { "A3", "C4", "E4" }, new[] { "F3", "A3", "C4" }, new[] { "G3", "C4", "E4" }, new[] { "G3", "B3", "D4" } };
            Recipes.Pattern(s, "Pad", "Chords", 16, Enumerable.Range(0, 4).SelectMany(bar => chords[bar].Select(p => J.Note(p, bar * 4, 4, 90))));
            string[] lead = { "E5", "D5", "C5", "B4", "C5", "A4", "G4", "A4" };
            Recipes.Pattern(s, "Lead", "Line", 16, lead.Select((p, i) => J.Note(p, i * 2, 1.75, 100)));
            foreach (var (m, p) in new[] { ("Kick", "Beat"), ("Hats", "Eighths"), ("Bass", "Eighths"), ("Pad", "Chords"), ("Lead", "Line") })
                Recipes.Place(s, m, p, 0, every: 16, times: 2);
        }

        public override void Solve(Studio s, TaskContext ctx)
        {
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5 });
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(r["id"]), ["metrics"] = J.A("loudness", "peaks", "dynamics", "spectrum") });
            Recipes.MasterBus(s);
            Recipes.MasterTo(s, -14, rounds: 4);
        }

        public override Facts Measure(Studio s, TaskContext ctx) => MeasureMaster(s, Parts);

        /// <summary>The master's loudness, peaks and crest, and each part's level against the others.</summary>
        internal static Facts MeasureMaster(Studio s, IEnumerable<string> parts, Facts f = null)
        {
            f ??= new Facts();
            var g = s.Graph();
            var present = parts.Where(p => g.Find(p) != null).ToList();
            var (mix, audio, how) = s.RenderParts(g, present, tail: 0.5);
            var l = Meters.Loudness(mix.Audio);
            var d = Meters.Dynamics(mix.Audio);
            f["lufs"] = l.Integrated;
            f["true_peak"] = l.TruePeakDb;
            f["sample_peak"] = l.SamplePeakDb;
            f["clipped"] = l.Clipped;
            f["crest_db"] = d.CrestDb;
            f["plr_db"] = d.PlrDb;
            f["silent"] = Meters.Silent(mix.Audio) ? 1 : 0;
            var levels = new Dictionary<string, double>();
            foreach (var p in parts)
            {
                var a = audio.GetValueOrDefault(p);
                bool heard = a != null && !Meters.Silent(a) && g.Reaches(p);
                f[$"{p}_audible"] = heard ? 1 : 0;
                if (heard) levels[p] = Meters.Loudness(a).Integrated;
                f.Text($"{p}_probe", how.GetValueOrDefault(p) ?? "missing");
            }
            // Balance: each part's loudness against the mean of the parts (independent of the bus gain).
            if (levels.Count > 0)
            {
                double mean = levels.Values.Where(v => !double.IsInfinity(v)).DefaultIfEmpty(double.NaN).Average();
                foreach (var (p, v) in levels) f[$"{p}_balance"] = v - mean;
            }
            return f;
        }

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            var kept = Parts.Select(p => (p, Ok: r[$"{p}_audible"] == 1 && Math.Abs(r[$"{p}_balance"] - start[$"{p}_balance"]) <= 3)).ToList();
            return new List<Criterion>
            {
                new("loudness", sounds ? Linear(Math.Abs(r["lufs"] + 14), 1, 3) : 0, $"{J.F(r["lufs"], 1)} LUFS integrated (target -14 ±1; start {J.F(start["lufs"], 1)})"),
                new("true_peak", sounds ? Linear(r["true_peak"], -1, 0.5) : 0, $"{J.F(r["true_peak"], 2)} dBTP (target ≤ -1; start {J.F(start["true_peak"], 2)})"),
                new("dynamics", sounds ? Band(r["crest_db"], 6, 14, 3) : 0, $"crest factor {J.F(r["crest_db"], 1)} dB (6–14), PLR {J.F(r["plr_db"], 1)} dB"),
                new("no_clipping", sounds && r["clipped"] == 0 && r["sample_peak"] < 0 ? 1 : 0, $"sample peak {J.F(r["sample_peak"], 2)} dBFS, {J.F(r["clipped"], 0)} clipped samples"),
                new("balance_kept", kept.Count(k => k.Ok) / (double)kept.Count,
                    string.Join("; ", kept.Select(k => $"{k.p} {J.F(start[$"{k.p}_balance"], 1)}→{J.F(r[$"{k.p}_balance"], 1)}{(k.Ok ? "" : " (NOT kept)")}"))),
            };
        }
    }
}
