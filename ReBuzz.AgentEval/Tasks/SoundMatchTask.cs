// Evaluation task "Sound match" (docs/specs/mcp-server.md §10): "make a sound like this". The
// setup renders a reference phrase from a Neo Poly patch, then leaves a default Neo Poly playing
// the same notes. Scored by the spectral and envelope distance to the reference (level-matched),
// the loudness difference, and whether the notes stayed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal sealed class SoundMatchTask : EvalTask
    {
        public override string Name => "sound_match";
        public override string Title => "Sound match";

        const double Tail = 1.5;

        /// <summary>The patch behind the reference: a resonant, plucked saw-and-pulse.</summary>
        static readonly (string, object)[] Patch =
        {
            ("Drift", "0%"), ("O1 shape", 2), ("O2 shape", 3), ("O2 octave", -1), ("O2 level", "50%"),
            ("F1 type", "Ladder 24"), ("F1 cutoff", "600 Hz"), ("F1 reso", "35%"), ("F1 env", "+60%"),
            ("Flt attack", "1 ms"), ("Flt decay", "200 ms"), ("Flt sustain", "0%"), ("Flt release", "200 ms"),
            ("Amp attack", "1 ms"), ("Amp decay", "450 ms"), ("Amp sustain", "0%"), ("Amp release", "250 ms"), ("Volume", "-8 dB"),
        };

        static IEnumerable<JsonObject> Phrase() =>
            new[] { "A2", "C3", "E3", "A3", "G3", "E3", "C3", "D3" }.Select((p, i) => J.Note(p, i, 0.5, 100));

        static string Reference(TaskContext ctx) => ctx.File("reference.wav");

        public override string Brief(TaskContext ctx) =>
            "The open song has a Neo Poly called Synth playing an 8-beat phrase. Design Synth's sound to match the reference recording at " +
            $"\"{Reference(ctx).Replace("\\", "/")}\" (the same phrase, rendered with a {Tail} s tail) as closely as you can: the same tone " +
            "(spectrum) and the same envelope (attack, decay, release), at the same loudness. Compare your renders with the reference file " +
            "(compare, analyze and plot take a WAV path). Keep the notes and the tempo as they are; change Synth's parameters (effects " +
            $"are allowed). When it's done, save it with song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Setup(Studio s, TaskContext ctx)
        {
            s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Recipes.Add(s, Recipes.Synth, "Synth", 0);
            Recipes.Set(s, "Synth", Patch);
            Recipes.Pattern(s, "Synth", "Phrase", 8, Phrase());
            Recipes.Place(s, "Synth", "Phrase", 0);
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = Tail, ["name"] = "reference" });
            File.Copy(J.Str(r["file"]), Reference(ctx), overwrite: true);
            // The agent starts from a default Neo Poly with the same notes.
            s.New(120);
            Recipes.Add(s, Recipes.Synth, "Synth", 0);
            Recipes.Set(s, "Synth", ("Drift", "0%"));
            Recipes.Pattern(s, "Synth", "Phrase", 8, Phrase());
            Recipes.Place(s, "Synth", "Phrase", 0);
        }

        public override void Solve(Studio s, TaskContext ctx)
        {
            // A competent agent listens first, then designs and compares until it matches.
            var before = s.C.Ok("render", new JsonObject { ["tail_seconds"] = Tail });
            s.C.Ok("compare", new JsonObject { ["a"] = Reference(ctx), ["b"] = J.Str(before["id"]) });
            s.C.Ok("machine_describe", new JsonObject { ["machine"] = "Synth", ["group"] = "Filter 1" });
            Recipes.Set(s, "Synth", Patch);
            var after = s.C.Ok("render", new JsonObject { ["tail_seconds"] = Tail });
            s.C.Ok("compare", new JsonObject { ["a"] = Reference(ctx), ["b"] = J.Str(after["id"]) });
        }

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var f = new Facts();
            var reference = Meters.Read(Reference(ctx));
            var mix = s.Render(new JsonObject { ["tail_seconds"] = Tail, ["name"] = "eval" });
            var a = mix.Audio;
            f["silent"] = Meters.Silent(a) ? 1 : 0;
            var (dist, level) = Meters.SpectralDistance(Meters.Spectrum(reference), Meters.Spectrum(a));
            f["spectral_distance_db"] = dist;
            f["spectral_level_offset_db"] = level;
            f["envelope_distance_db"] = Meters.EnvelopeDistance(reference, a);
            f["lufs"] = Meters.Loudness(a).Integrated;
            f["reference_lufs"] = Meters.Loudness(reference).Integrated;
            f["length_seconds"] = a.Seconds;
            f["reference_seconds"] = reference.Seconds;
            // The notes: Synth's (or the only generator's) placed notes against the phrase.
            var g = s.Graph();
            string synth = g.Find("Synth")?.Name ?? (g.Generators.Count() == 1 ? g.Generators.First().Name : null);
            if (synth != null)
            {
                var placed = s.PlacedNotes(synth, s.C.Ok("arrangement_get"), s.Notes())
                    .Select(n => (Pitch: (int)J.Num(n["pitch"]), Start: Math.Round(J.Num(n["start"]), 4), Length: Math.Round(J.Num(n["length"]), 4)))
                    .OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
                var want = Phrase().Select(n => (Pitch: ReBuzzPitch(J.Str(n["pitch"])), Start: J.Num(n["start"]), Length: J.Num(n["length"]))).ToList();
                f["notes_kept"] = placed.SequenceEqual(want) ? 1 : 0;
                f.Text("notes", string.Join(" ", placed.Select(n => $"{n.Pitch}@{n.Start}")));
            }
            else f["notes_kept"] = 0;
            return f;
        }

        static int ReBuzzPitch(string name) => ReBuzz.Agent.Model.Pitch.Parse(name);

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            double dl = Math.Abs(r["lufs"] - r["reference_lufs"]);
            return new List<Criterion>
            {
                new("spectrum", sounds ? Linear(r["spectral_distance_db"], 1.5, 6) : 0,
                    $"third octaves differ by {J.F(r["spectral_distance_db"])} dB on average, level-matched (offset {J.F(r["spectral_level_offset_db"], 1)} dB)"),
                new("envelope", sounds ? Linear(r["envelope_distance_db"], 2, 8) : 0, $"10 ms loudness envelopes differ by {J.F(r["envelope_distance_db"])} dB on average, level-matched"),
                new("loudness", sounds ? Linear(dl, 1, 6) : 0, $"{J.F(r["lufs"], 1)} LUFS against the reference's {J.F(r["reference_lufs"], 1)}"),
                new("notes_kept", r["notes_kept"], r.Text("notes") ?? "no Synth"),
            };
        }
    }
}
