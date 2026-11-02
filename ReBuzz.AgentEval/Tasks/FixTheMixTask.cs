// Evaluation task "Fix the mix" (docs/specs/mcp-server.md §10): a song with four planted
// problems: a kick and a bass on the same low note (masking), a lead with a harsh filter
// resonance at 3.15 kHz, a hi-hat far too loud, and a pad whose reverb is out of phase (150 %
// width at 100 % wet). Each must be measurably fixed and nothing else made worse.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal sealed class FixTheMixTask : EvalTask
    {
        public override string Name => "fix_the_mix";
        public override string Title => "Fix the mix";

        static readonly string[] Parts = { "Kick", "Bass", "Lead", "Hats", "Pad" };
        const double ResonanceHz = 3150;

        public override string Brief(TaskContext ctx) =>
            "The open song is a 4-bar loop at 120 BPM (Kick, Bass, Lead, Hats, and a Pad through a reverb called Space) with several mix " +
            "problems. Find them with analyze_mix, analyze and plot, and fix each one so the improvement shows in the measurements, without " +
            "making anything else worse: keep every part audible and keep the notes. Mix moves only (levels, filters and other parameters, " +
            "effects such as Neo EQ, Neo Utility and Neo Comp, routing); guide(topic=\"mixing\") has the checks. When it's done, save it with " +
            $"song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Setup(Studio s, TaskContext ctx)
        {
            s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 120 });
            // Masking: a kick and a sub bass both on A1 (55 Hz) at similar levels.
            Recipes.Kick(s, "Kick", -6);
            Recipes.Preset(s, "Bass", "Bass — Classic Sub", -9);
            // A harsh resonance: a saw lead through a ladder near self-oscillation at 3.15 kHz, the filter fixed there.
            Recipes.Preset(s, "Lead", "Lead — Smooth Saw", -14);
            Recipes.Set(s, "Lead", ("F1 type", "Ladder 24"), ("F1 cutoff", $"{ResonanceHz} Hz"), ("F1 reso", "88%"), ("F1 env", "0%"),
                ("F1 key track", "0%"), ("F1 velocity", "0%"));
            // A hi-hat far too loud.
            Recipes.Hat(s, "Hats", 4);
            // Phase: the pad only through a reverb at 150 % width, fully wet.
            Recipes.Preset(s, "Pad", "Pad — Warm Strings", -6);
            Recipes.Add(s, "Neo Verb", "Space", 0, insertAfter: "Pad");
            Recipes.Set(s, "Space", ("Mix", "100%"), ("Width", "150%"), ("Decay", "2.5 s"));

            Recipes.Pattern(s, "Kick", "Beat", 16, Recipes.Every("A1", 16, 1, length: 0.5, velocity: 120));
            var bass = new List<JsonObject>();
            string[] roots = { "A2", "A2", "F2", "G2" };
            for (int bar = 0; bar < 4; bar++) bass.Add(J.Note(roots[bar], bar * 4, 3.75, 105));
            Recipes.Pattern(s, "Bass", "Roots", 16, bass);
            string[] lead = { "A4", "C5", "E5", "D5", "C5", "A4", "G4", "A4" };
            Recipes.Pattern(s, "Lead", "Line", 16, lead.Select((p, i) => J.Note(p, i * 2, 1.5, 100)));
            Recipes.Pattern(s, "Hats", "Sixteenths", 16, Recipes.Every("C6", 16, 0.25, length: 0.1, velocity: 100));
            var pad = new List<JsonObject>();
            string[][] chords = { new[] { "A3", "C4", "E4" }, new[] { "A3", "C4", "E4" }, new[] { "F3", "A3", "C4" }, new[] { "G3", "B3", "D4" } };
            for (int bar = 0; bar < 4; bar++) pad.AddRange(chords[bar].Select(p => J.Note(p, bar * 4, 4, 90)));
            Recipes.Pattern(s, "Pad", "Chords", 16, pad);
            foreach (var (m, p) in new[] { ("Kick", "Beat"), ("Bass", "Roots"), ("Lead", "Line"), ("Hats", "Sixteenths"), ("Pad", "Chords") }) Recipes.Place(s, m, p, 0);
        }

        public override void Solve(Studio s, TaskContext ctx)
        {
            string id = J.Str(s.C.Ok("analyze_mix")["render"]);
            s.C.Ok("plot", new JsonObject { ["kind"] = "bands", ["sources"] = J.A($"{id}:Kick", $"{id}:Bass", $"{id}:Lead", $"{id}:Hats") });
            // Masking: frequency slots with Neo EQ. The kick keeps the sub (a high cut above it), the bass
            // gives it up (a low cut under its second harmonic).
            s.C.Ok("guide", new JsonObject { ["machine"] = "Neo EQ" });
            Recipes.Add(s, "Neo EQ", "Kick EQ", 0, insertAfter: "Kick");
            Recipes.Set(s, "Kick EQ", ("B8 on", "On"), ("B8 type", "High cut"), ("B8 freq", "120 Hz"), ("B8 slope", "24 dB/oct"));
            Recipes.Add(s, "Neo EQ", "Bass EQ", 0, insertAfter: "Bass");
            Recipes.Set(s, "Bass EQ", ("B1 on", "On"), ("B1 type", "Low cut"), ("B1 freq", "100 Hz"), ("B1 slope", "24 dB/oct"));
            // The resonance: a narrow cut where it rings.
            Recipes.Add(s, "Neo EQ", "Lead EQ", 0, insertAfter: "Lead");
            Recipes.Set(s, "Lead EQ", ("B4 type", "Bell"), ("B4 freq", $"{ResonanceHz} Hz"), ("B4 gain", "-12 dB"), ("B4 Q", 4));
            // The hats: down to sit under the mix.
            s.C.Ok("connection_set", new JsonObject { ["changes"] = J.A(new JsonObject { ["from"] = "Hats", ["to"] = "Master", ["gain_db"] = -12 }) });
            // Phase: the reverb narrower than 100 %, so its sides don't oppose.
            Recipes.Set(s, "Space", ("Width", "70%"));
            s.C.Ok("analyze_mix");
        }

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var f = new Facts();
            var g = s.Graph();
            var present = Parts.Where(p => g.Find(p) != null).ToList();
            var (mix, parts, how) = s.RenderParts(g, present);
            var ml = Meters.Loudness(mix.Audio);
            f["master_lufs"] = ml.Integrated;
            f["master_true_peak"] = ml.TruePeakDb;
            f["master_clipped"] = ml.Clipped;
            f["master_correlation"] = Meters.Stereo(mix.Audio).Correlation;
            StereoAudio Part(string p) => parts.GetValueOrDefault(p);
            foreach (var p in Parts)
            {
                var a = Part(p);
                bool heard = a != null && !Meters.Silent(a) && g.Reaches(p);
                f[$"{p}_audible"] = heard ? 1 : 0;
                f[$"{p}_lufs"] = heard ? Meters.Loudness(a).Integrated : double.NegativeInfinity;
                f.Text($"{p}_probe", how.GetValueOrDefault(p) ?? "missing");
            }
            if (Part("Kick") is StereoAudio kick && Part("Bass") is StereoAudio bass)
            {
                var (worst, sum, where) = Meters.Masking(kick, bass);
                f["kick_bass_masking"] = sum;
                f["kick_bass_masking_worst"] = worst;
                f.Text("kick_bass_masking_bands", where);
            }
            if (Part("Lead") is StereoAudio lead && !Meters.Silent(lead))
                f["lead_resonance_db"] = Meters.Prominence(Meters.Spectrum(lead), ResonanceHz);
            f["hats_below_master_lu"] = f["master_lufs"] - f["Hats_lufs"];
            if (Part("Pad") is StereoAudio pad && !Meters.Silent(pad))
            {
                var st = Meters.Stereo(pad);
                f["pad_correlation"] = st.Correlation;
                f["pad_mono_loss_db"] = st.MonoLossDb;
            }
            return f;
        }

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            // Masking: at most half the start's.
            double m0 = start["kick_bass_masking"], m1 = r["kick_bass_masking"];
            double maskTarget = 0.5 * m0;
            double masking = double.IsNaN(m1) ? 0 : m1 <= maskTarget ? 1 : Linear(m1, maskTarget, m0);
            // Resonance: within 3 dB of its neighbours, or 6 dB less prominent than at the start.
            double p0 = start["lead_resonance_db"], p1 = r["lead_resonance_db"];
            double resTarget = Math.Max(3, Math.Min(p0 - 6, 6));
            double resonance = double.IsNaN(p1) ? 0 : Linear(p1, resTarget, p0);
            // Hats: at least 9 LU under the master (and not gone: within 30).
            double h0 = start["hats_below_master_lu"], h1 = r["hats_below_master_lu"];
            double hats = r["Hats_audible"] == 0 ? 0 : h1 > 30 ? Linear(h1, 30, 40) : Linear(h1, 9, Math.Min(h0, 8));
            // Phase: the pad's correlation at least +0.1 (from negative).
            double c0 = start["pad_correlation"], c1 = r["pad_correlation"];
            double phase = double.IsNaN(c1) ? 0 : Linear(c1, 0.1, Math.Min(c0, 0.05));
            // Nothing worse: every part still audible and within reach of its level, the master not clipping nor collapsing.
            var keep = new List<(bool Ok, string What)>();
            foreach (var p in Parts.Where(p => p != "Hats"))
                keep.Add((r[$"{p}_audible"] == 1 && r[$"{p}_lufs"] > start[$"{p}_lufs"] - 12, $"{p} {J.F(start[$"{p}_lufs"], 1)}→{J.F(r[$"{p}_lufs"], 1)} LUFS"));
            keep.Add((r["master_clipped"] == 0 && r["master_true_peak"] < 0, $"master true peak {J.F(r["master_true_peak"], 1)} dBTP"));
            keep.Add((r["master_lufs"] > start["master_lufs"] - 8, $"master {J.F(start["master_lufs"], 1)}→{J.F(r["master_lufs"], 1)} LUFS"));
            return new List<Criterion>
            {
                new("masking_kick_bass", masking, $"severity summed over bands {J.F(m0)} → {J.F(m1)} (target ≤ {J.F(maskTarget)}); worst bands now {r.Text("kick_bass_masking_bands")}"),
                new("harsh_resonance", resonance, $"3.15 kHz above its neighbours {J.F(p0, 1)} → {J.F(p1, 1)} dB (target ≤ {J.F(resTarget, 1)})"),
                new("hat_level", hats, $"hats below the master {J.F(h0, 1)} → {J.F(h1, 1)} LU (target 9–30)"),
                new("phase", phase, $"pad (reverb) correlation {J.F(c0)} → {J.F(c1)}, mono loss {J.F(start["pad_mono_loss_db"], 1)} → {J.F(r["pad_mono_loss_db"], 1)} dB (target ≥ 0.10)"),
                new("nothing_worse", keep.Count(k => k.Ok) / (double)keep.Count, string.Join("; ", keep.Select(k => (k.Ok ? "" : "NOT ") + k.What))),
            };
        }
    }
}
