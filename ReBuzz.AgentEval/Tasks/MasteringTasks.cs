// Evaluation tasks for the mastering assistant (docs/specs/mastering-tools.md §6, M1), on the "master"
// task's unmastered 8-bar mix (MasterTask.Mix):
//  - "master_match": master it to match a reference recording (the same mix through a known EQ and a
//    limiter): the third-octave tonal difference after level matching at most 1.5 dB RMS from 50 Hz to
//    16 kHz, the side/mid width per band within ±0.1 of the reference's, PLR and loudness near its own.
//  - "master_loud": master for Spotify's Loud setting: −11 LUFS, true peak at most −2 dBTP, so Spotify Loud
//    neither raises nor limits it (what analyze's platform metric previews).
//  - "fix_harshness": a planted 2–6 kHz excess (a boost on the lead) reduced by at least 3 dB, without a
//    tilt change, the loudness within 2 LU (a cut where K-weighting counts most lowers it) and the other
//    parts' balance kept.
// Each scripted solution uses the M1 tools (analyze with a target and the platform metric, eq_fit).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    /// <summary>Measures the mastering briefs share.</summary>
    internal static class MasteringMeasures
    {
        /// <summary>RMS of the third-octave (smoothed) tonal difference after level matching, 50 Hz – 16 kHz, dB.</summary>
        public static double TonalRms(TonalCurve result, TonalCurve reference)
        {
            var d = Tonal.Difference(result, reference);
            var inRange = Enumerable.Range(0, d.Hz.Length)
                .Where(i => d.Hz[i] >= Tonal.RmsLo && d.Hz[i] <= Tonal.RmsHi && double.IsFinite(d.SmoothedDb[i]))
                .Select(i => d.SmoothedDb[i]).ToList();
            return inRange.Count > 0 ? Math.Sqrt(inRange.Average(x => x * x)) : double.NaN;
        }

        /// <summary>The curve's mean level from <paramref name="lo"/> to <paramref name="hi"/> Hz, dB.</summary>
        public static double Mean(TonalCurve c, double lo, double hi) =>
            Enumerable.Range(0, c.Hz.Length).Where(i => c.Hz[i] >= lo && c.Hz[i] <= hi && double.IsFinite(c.Db[i])).Select(i => c.Db[i]).DefaultIfEmpty(double.NaN).Average();

        /// <summary>The largest side/mid width difference in the four bands (linear ratios; bands with no mid left out).</summary>
        public static double WidthDiff(StereoResult a, StereoResult b) =>
            Enumerable.Range(0, Math.Min(a.MsBands.Length, b.MsBands.Length))
                .Where(i => double.IsFinite(a.MsBands[i].Width) && double.IsFinite(b.MsBands[i].Width))
                .Select(i => Math.Abs(a.MsBands[i].Width - b.MsBands[i].Width)).DefaultIfEmpty(0).Max();

        /// <summary>No clipping, and each part's level against the others kept within 3 dB (<paramref name="culprit"/>: may fall by up to 6 dB, as its fix takes level off it).</summary>
        public static List<Criterion> Common(Facts start, Facts r, string culprit = null)
        {
            bool sounds = r["silent"] == 0;
            bool Kept(string p)
            {
                double d = r[$"{p}_balance"] - start[$"{p}_balance"];
                return r[$"{p}_audible"] == 1 && (p == culprit ? d >= -6 && d <= 3 : Math.Abs(d) <= 3);
            }
            var kept = MasterTask.Parts.Select(p => (p, Ok: Kept(p))).ToList();
            return new List<Criterion>
            {
                new("no_clipping", sounds && r["clipped"] == 0 && r["sample_peak"] < 0 ? 1 : 0, $"sample peak {J.F(r["sample_peak"], 2)} dBFS, {J.F(r["clipped"], 0)} clipped samples"),
                new("balance_kept", kept.Count(k => k.Ok) / (double)kept.Count,
                    string.Join("; ", kept.Select(k => $"{k.p} {J.F(start[$"{k.p}_balance"], 1)}→{J.F(r[$"{k.p}_balance"], 1)}{(k.Ok ? "" : " (NOT kept)")}"))),
            };
        }

        static string Path(TaskContext ctx, string name) => ctx.File(name).Replace("\\", "/");

        public static string Save(TaskContext ctx) => $"When it's done, save it with song_save(path=\"{Path(ctx, "result.bmxml")}\", overwrite=true).";
    }

    internal sealed class MasterMatchTask : EvalTask
    {
        public override string Name => "master_match";
        public override string Title => "Master to a reference";

        static string Reference(TaskContext ctx) => ctx.File("reference.wav");

        public override string Brief(TaskContext ctx) =>
            "The open song is a finished but unmastered mix: 8 bars at 120 BPM (Kick, Hats, Bass, Pad, Lead). Master it to match the reference " +
            $"recording at \"{Reference(ctx).Replace("\\", "/")}\": its tonal balance (the third-octave difference after level matching at most " +
            "1.5 dB RMS from 50 Hz to 16 kHz), its width in each of the four bands (side/mid within ±0.1), its PLR (within 1.5 dB) and its " +
            "loudness (within 1 LU), with the true peak at most -1 dBTP, no clipping, and the mix's balance as it is. Build the master chain on " +
            "a bus into Master (Neo EQ, Neo Comp, Neo Drive, Neo Utility and Neo Limiter are available; guide(topic=\"mastering\") has the " +
            "flow). analyze(source=…, target=<the reference>) measures against it, and eq_fit turns the tonal difference into Neo EQ bands. " +
            MasteringMeasures.Save(ctx);

        public override void Setup(Studio s, TaskContext ctx)
        {
            // The reference: the same mix through a darker low-mid, a lift at 2.5 kHz and more air, limited to -12 LUFS.
            MasterTask.Mix(s);
            Recipes.MasterBus(s, ceilingDb: -1.5);
            Recipes.Set(s, "Tone", ("B3 type", "Bell"), ("B3 freq", "250 Hz"), ("B3 gain", "-3 dB"), ("B3 Q", 1.0),
                ("B5 type", "Bell"), ("B5 freq", "2500 Hz"), ("B5 gain", "+1.5 dB"), ("B5 Q", 1.4),
                ("B7 type", "High shelf"), ("B7 freq", "7000 Hz"), ("B7 gain", "+3 dB"));
            Recipes.MasterTo(s, -12, maxTruePeak: -1.5, rounds: 4);
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "reference" }, seconds: 600);
            File.Copy(J.Str(r["file"]), Reference(ctx), overwrite: true);
            s.New(120);
            MasterTask.Mix(s);
        }

        public override void Solve(Studio s, TaskContext ctx)
        {
            // A style target from the reference, measured against; the chain; eq_fit sets the tone; then the limiter's gain loop
            // to the target's loudness; and a last check against the reference itself and the target's file.
            string reference = Reference(ctx);
            var target = s.C.Ok("target_build", new JsonObject { ["name"] = "reference mix", ["sources"] = J.A(reference) });
            var before = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5 }, seconds: 600);
            var m = s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(before["id"]), ["target"] = "reference mix" });
            double lufs = J.Num(m["target"]?["integrated_lufs"]);
            Recipes.MasterBus(s, ceilingDb: -1.5);
            var bus = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "bus" }, seconds: 600);
            s.C.Ok("eq_fit", new JsonObject { ["source"] = J.Str(bus["id"]), ["target"] = "reference mix", ["bands"] = 4, ["machine"] = "Tone" });
            Recipes.MasterTo(s, double.IsFinite(lufs) ? lufs : -12, maxTruePeak: -1.2, rounds: 4);
            var after = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "master" }, seconds: 600);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(after["id"]), ["target"] = reference, ["metrics"] = J.A("tonal", "stereo", "dynamics", "loudness") });
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(after["id"]), ["target"] = J.Str(target["file"]), ["metrics"] = J.A("tonal", "microdynamics") });
        }

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var reference = new Analysed(Meters.Read(Reference(ctx)));
            return MasterTask.MeasureMaster(s, MasterTask.Parts, more: (audio, f) =>
            {
                var a = new Analysed(audio);
                f["tonal_rms_db"] = MasteringMeasures.TonalRms(a.Tonal, reference.Tonal);
                f["width_diff"] = MasteringMeasures.WidthDiff(a.StereoResult, reference.StereoResult);
                f["plr_diff_db"] = a.DynamicsResult.PlrDb - reference.DynamicsResult.PlrDb;
                f["lufs_diff"] = a.LoudnessResult.Integrated - reference.LoudnessResult.Integrated;
                f["reference_lufs"] = reference.LoudnessResult.Integrated;
            });
        }

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            var list = new List<Criterion>
            {
                new("tonal_match", sounds ? Linear(r["tonal_rms_db"], 1.5, 3) : 0, $"{J.F(r["tonal_rms_db"], 2)} dB RMS third-octave difference (target ≤ 1.5; start {J.F(start["tonal_rms_db"], 2)})"),
                new("width_match", sounds ? Linear(r["width_diff"], 0.1, 0.3) : 0, $"largest side/mid difference {J.F(r["width_diff"], 3)} (target ≤ 0.1)"),
                new("plr_match", sounds ? Linear(Math.Abs(r["plr_diff_db"]), 1.5, 4) : 0, $"PLR {J.F(r["plr_db"], 1)} dB, {J.F(r["plr_diff_db"], 1)} from the reference's (target ±1.5; start {J.F(start["plr_diff_db"], 1)})"),
                new("loudness_match", sounds ? Linear(Math.Abs(r["lufs_diff"]), 1, 3) : 0, $"{J.F(r["lufs"], 1)} LUFS against the reference's {J.F(r["reference_lufs"], 1)} (target ±1; start {J.F(start["lufs"], 1)})"),
                new("true_peak", sounds ? Linear(r["true_peak"], -1, 0.5) : 0, $"{J.F(r["true_peak"], 2)} dBTP (target ≤ -1)"),
            };
            list.AddRange(MasteringMeasures.Common(start, r));
            return list;
        }
    }

    internal sealed class MasterLoudTask : EvalTask
    {
        public override string Name => "master_loud";
        public override string Title => "Master for Spotify Loud";

        public override string Brief(TaskContext ctx) =>
            "The open song is a finished but unmastered mix: 8 bars at 120 BPM (Kick, Hats, Bass, Pad, Lead). Master it for Spotify's Loud " +
            "setting, which plays everything at -11 LUFS and puts a limiter on what it has to raise: integrated loudness -11 LUFS (within " +
            "1 LU) and a true peak at most -2 dBTP (Spotify's advice for masters louder than -14 LUFS), so that Spotify Loud neither raises " +
            "nor limits it; a crest factor of at least 5 dB, no clipping, and the mix's balance as it is. Build the master chain on a bus into " +
            "Master (Neo EQ, Neo Comp, Neo Drive and Neo Limiter are available; guide(topic=\"mastering\") has the flow); analyze's platform " +
            "metric shows what each service would do to a render. " + MasteringMeasures.Save(ctx);

        public override void Setup(Studio s, TaskContext ctx) => MasterTask.Mix(s);

        public override void Solve(Studio s, TaskContext ctx)
        {
            var before = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5 }, seconds: 600);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(before["id"]), ["metrics"] = J.A("loudness", "peaks", "dynamics", "platform") });
            Recipes.MasterBus(s, ceilingDb: -2.3);
            Recipes.MasterTo(s, -11, maxTruePeak: -2, rounds: 5);
            var after = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "master" }, seconds: 600);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(after["id"]), ["metrics"] = J.A("loudness", "peaks", "platform") });
        }

        public override Facts Measure(Studio s, TaskContext ctx) =>
            MasterTask.MeasureMaster(s, MasterTask.Parts, more: (audio, f) =>
            {
                var l = Meters.Loudness(audio);
                var loud = Platform.Preview(l.Integrated, l.TruePeakDb, l.SamplePeakDb).FirstOrDefault(p => p.Service == "Spotify" && p.Mode == "loud");
                f["spotify_loud_gain_db"] = loud?.GainDb ?? double.NaN;
                f["spotify_loud_limited_db"] = loud?.LimitedDb ?? double.NaN;
            });

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            double untouched = Math.Min(Linear(Math.Abs(r["spotify_loud_gain_db"]), 1, 3), Linear(r["spotify_loud_limited_db"], 0.1, 1));
            var list = new List<Criterion>
            {
                new("loudness", sounds ? Linear(Math.Abs(r["lufs"] + 11), 1, 3) : 0, $"{J.F(r["lufs"], 1)} LUFS integrated (target -11 ±1; start {J.F(start["lufs"], 1)})"),
                new("true_peak", sounds ? Linear(r["true_peak"], -2, -1) : 0, $"{J.F(r["true_peak"], 2)} dBTP (target ≤ -2; start {J.F(start["true_peak"], 2)})"),
                new("spotify_loud_untouched", sounds ? untouched : 0, $"Spotify Loud: gain {J.F(r["spotify_loud_gain_db"], 1)} dB, its limiter {J.F(r["spotify_loud_limited_db"], 1)} dB (target ±1 and 0; start gain {J.F(start["spotify_loud_gain_db"], 1)})"),
                new("dynamics", sounds ? Linear(r["crest_db"], 5, 3) : 0, $"crest factor {J.F(r["crest_db"], 1)} dB (at least 5), PLR {J.F(r["plr_db"], 1)} dB"),
            };
            list.AddRange(MasteringMeasures.Common(start, r));
            return list;
        }
    }

    internal sealed class FixHarshnessTask : EvalTask
    {
        public override string Name => "fix_harshness";
        public override string Title => "Fix harshness";

        public override string Brief(TaskContext ctx) =>
            "The open song is an 8-bar mix at 120 BPM (Kick, Hats, Bass, Pad, Lead) that sounds harsh: too much energy between 2 and 6 kHz. " +
            "Find where it comes from and fix it, so that the 2–6 kHz excess (that band's level against the bands on either side, 1–2 kHz " +
            "and 6–12 kHz) drops by at least 3 dB, without a tilt change (the level from 100 Hz to 1 kHz against 8–14 kHz within 1 dB of " +
            "what it is), the loudness within 2 LU, no clipping, and the parts' balance as it is (the culprit may get quieter as its harsh band " +
            "comes down). analyze's tonal and spectrum metrics " +
            "show the curve; eq_fit can turn a wanted change (a curve) into Neo EQ bands. " + MasteringMeasures.Save(ctx);

        public override void Setup(Studio s, TaskContext ctx)
        {
            MasterTask.Mix(s);
            // The planted harshness: a boost on the lead, in its own Neo EQ.
            Recipes.Add(s, "Neo EQ", "Lead EQ", 0, insertAfter: "Lead");
            Recipes.Set(s, "Lead EQ", ("B4 type", "Bell"), ("B4 freq", "3500 Hz"), ("B4 gain", "+12 dB"), ("B4 Q", 0.9));
        }

        public override void Solve(Studio s, TaskContext ctx)
        {
            // Find the band (analyze_mix and analyze's tonal curve), then a cut there in a Neo EQ after the culprit.
            s.C.Ok("analyze_mix");
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5 }, seconds: 600);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(r["id"]), ["metrics"] = J.A("tonal", "spectrum") });
            Recipes.Add(s, "Neo EQ", "De-harsh", 0, insertAfter: "Lead EQ");
            var curve = new JsonArray(
                new JsonObject { ["hz"] = 1000, ["db"] = 0 }, new JsonObject { ["hz"] = 3500, ["db"] = -9 }, new JsonObject { ["hz"] = 12000, ["db"] = 0 });
            s.C.Ok("eq_fit", new JsonObject { ["curve"] = curve, ["bands"] = 1, ["max_gain_db"] = 10, ["limit_db"] = 10, ["machine"] = "De-harsh" });
            var after = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5 }, seconds: 600);
            s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(after["id"]), ["metrics"] = J.A("tonal", "loudness") });
        }

        public override Facts Measure(Studio s, TaskContext ctx) =>
            MasterTask.MeasureMaster(s, MasterTask.Parts, more: (audio, f) =>
            {
                var c = new Analysed(audio).Tonal;
                f["excess_db"] = MasteringMeasures.Mean(c, 2000, 6000) - (MasteringMeasures.Mean(c, 1000, 2000) + MasteringMeasures.Mean(c, 6000, 12000)) / 2;
                f["tilt_db"] = MasteringMeasures.Mean(c, 8000, 14000) - MasteringMeasures.Mean(c, 100, 1000);
            });

        public override List<Criterion> Score(Facts start, Facts r, TaskContext ctx)
        {
            bool sounds = r["silent"] == 0;
            double reduced = start["excess_db"] - r["excess_db"];
            var list = new List<Criterion>
            {
                new("harshness_reduced", sounds ? Math.Min(Linear(reduced, 3, 1), Linear(r["excess_db"], -2, -5)) : 0,
                    $"2–6 kHz excess {J.F(start["excess_db"], 1)} → {J.F(r["excess_db"], 1)} dB (at least 3 dB less, not below -2)"),
                new("tilt_kept", sounds ? Linear(Math.Abs(r["tilt_db"] - start["tilt_db"]), 1, 2.5) : 0, $"8–14 kHz against 100 Hz–1 kHz {J.F(start["tilt_db"], 1)} → {J.F(r["tilt_db"], 1)} dB (within 1)"),
                new("loudness_kept", sounds ? Linear(Math.Abs(r["lufs"] - start["lufs"]), 2, 4) : 0, $"{J.F(start["lufs"], 1)} → {J.F(r["lufs"], 1)} LUFS (within 2)"),
            };
            list.AddRange(MasteringMeasures.Common(start, r, culprit: "Lead"));
            return list;
        }
    }
}
