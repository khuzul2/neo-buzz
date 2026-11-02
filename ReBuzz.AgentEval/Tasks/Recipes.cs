// Evaluation: instruments built from Neo Poly through the tools, as an agent would make them
// (parameters in units, factory presets), for the task setups and the reference solutions.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal static class Recipes
    {
        public const string Synth = "Neo Poly";

        public static void Add(Studio s, string gear, string name, double gainDb, string connectTo = null, string insertAfter = null)
        {
            var args = new JsonObject { ["gear"] = gear, ["name"] = name };
            if (insertAfter != null) args["insert_after"] = insertAfter;
            else
            {
                args["gain_db"] = gainDb;
                if (connectTo != null) args["connect_to"] = connectTo;
            }
            s.C.Ok("machine_add", args);
        }

        public static void Set(Studio s, string machine, params (string Param, object Value)[] changes) =>
            s.C.Ok("param_set", new JsonObject { ["machine"] = machine, ["changes"] = J.Changes(changes) });

        /// <summary>A sine with a fast pitch drop: an electronic kick (play it at A1–F1).</summary>
        public static void Kick(Studio s, string name, double gainDb)
        {
            Add(s, Synth, name, gainDb);
            Set(s, name,
                ("Voice mode", "Mono"), ("Drift", "0%"), ("O1 shape", 0), ("F1 type", "SVF LP"), ("F1 cutoff", "2000 Hz"),
                ("F1 env", "0%"), ("F1 key track", "0%"), ("F1 velocity", "0%"),
                ("Amp attack", "1 ms"), ("Amp decay", "300 ms"), ("Amp sustain", "0%"), ("Amp release", "60 ms"),
                ("Mod attack", "1 ms"), ("Mod decay", "50 ms"), ("Mod sustain", "0%"), ("M3 dest", "Pitch"), ("M3 amount", "+40%"));
        }

        /// <summary>High-passed noise, short: a closed hi-hat.</summary>
        public static void Hat(Studio s, string name, double gainDb)
        {
            Add(s, Synth, name, gainDb);
            Set(s, name,
                ("Drift", "0%"), ("O1 level", "0%"), ("Noise", "100%"), ("F1 type", "SVF HP"), ("F1 cutoff", "7000 Hz"), ("F1 reso", "10%"),
                ("F1 env", "0%"), ("F1 key track", "0%"), ("F1 velocity", "0%"),
                ("Amp attack", "1 ms"), ("Amp decay", "60 ms"), ("Amp sustain", "0%"), ("Amp release", "40 ms"));
        }

        /// <summary>Band-passed noise with a longer decay: a clap or snare.</summary>
        public static void Clap(Studio s, string name, double gainDb)
        {
            Add(s, Synth, name, gainDb);
            Set(s, name,
                ("Drift", "0%"), ("O1 level", "0%"), ("Noise", "100%"), ("F1 type", "SVF BP"), ("F1 cutoff", "1500 Hz"), ("F1 reso", "20%"),
                ("F1 env", "0%"), ("F1 key track", "0%"), ("F1 velocity", "0%"),
                ("Amp attack", "1 ms"), ("Amp decay", "180 ms"), ("Amp sustain", "0%"), ("Amp release", "100 ms"));
        }

        /// <summary>A factory preset on a new Neo Poly, drift off so renders compare.</summary>
        public static void Preset(Studio s, string name, string preset, double gainDb)
        {
            Add(s, Synth, name, gainDb);
            s.C.Ok("preset_load", new JsonObject { ["machine"] = name, ["preset"] = preset });
            Set(s, name, ("Drift", "0%"));
        }

        public static void Pattern(Studio s, string machine, string pattern, double beats, IEnumerable<JsonObject> notes)
        {
            s.C.Ok("pattern_create", new JsonObject { ["machine"] = machine, ["name"] = pattern, ["length_beats"] = beats });
            s.C.Ok("notes_write", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["notes"] = J.Notes(notes), ["mode"] = "replace" });
        }

        /// <summary>Places a pattern at <paramref name="at"/>, then every <paramref name="every"/> beats, <paramref name="times"/> times in all.</summary>
        public static void Place(Studio s, string machine, string pattern, double at, double every = 0, int times = 1)
        {
            var args = new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["at"] = at };
            if (times > 1)
            {
                args["repeat_every_beats"] = every;
                args["times"] = times;
            }
            s.C.Ok("arrangement_place", args);
        }

        /// <summary>One note on each beat (or step) from 0 to <paramref name="beats"/>.</summary>
        public static IEnumerable<JsonObject> Every(object pitch, double beats, double step, double offset = 0, double length = 0.25, int velocity = 110) =>
            Enumerable.Range(0, (int)(beats / step)).Select(i => J.Note(pitch, offset + i * step, length, velocity)).Where(n => J.Num(n["start"]) < beats);


        /// <summary>
        /// The mastering chain (the mastering guide's order): every machine feeding Master goes into
        /// "Tone" (Neo EQ: a 30 Hz low cut, corrective), then "Glue" (Neo Comp, gentle bus
        /// compression <paramref name="glueDb"/> under the mix's measured peaks at <paramref name="ratio"/>),
        /// then "Limit" (Neo Limiter: true peak under <paramref name="ceilingDb"/> dBTP), each connection's gain kept.
        /// </summary>
        public static void MasterBus(Studio s, double ceilingDb = -1.2, double glueDb = 12, string ratio = "3:1")
        {
            // Measure the mix first: the glue's threshold sits under its peaks.
            var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "premaster" }, seconds: 600);
            var m = s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(r["id"]), ["metrics"] = J.A("peaks", "dynamics") });
            double peak = J.Num(m["peaks"]?["true_peak_dbtp"]);
            double threshold = double.IsNaN(peak) || double.IsInfinity(peak) ? -24 : System.Math.Clamp(peak - glueDb, -60, 0);
            var g = s.Graph();
            var feeds = g.Master.Inputs.Where(i => i.Main).ToList();
            Add(s, "Neo EQ", "Tone", 0);
            Add(s, "Neo Comp", "Glue", 0, insertAfter: "Tone");
            Add(s, "Neo Limiter", "Limit", 0, insertAfter: "Glue");
            foreach (var f in feeds)
            {
                s.C.Ok("disconnect", new JsonObject { ["from"] = f.From, ["to"] = "Master" });
                s.C.Ok("connect", new JsonObject { ["from"] = f.From, ["to"] = "Tone", ["gain_db"] = double.IsNaN(f.GainDb) || double.IsInfinity(f.GainDb) ? 0 : f.GainDb });
            }
            Set(s, "Tone", ("B1 on", "On"), ("B1 type", "Low cut"), ("B1 freq", "30 Hz"), ("B1 slope", "24 dB/oct"));
            Set(s, "Glue", ("Mode", "Glue"), ("Threshold", $"{threshold.ToString("0.0", J.C)} dB"), ("Ratio", ratio), ("Attack", "3 ms"),
                ("Release", "150 ms"), ("Knee", "6 dB"), ("Auto makeup", "Off"));
            Set(s, "Limit", ("Ceiling", $"{ceilingDb.ToString("0.0", J.C)} dB"), ("Auto release", "On"), ("Lookahead", "2 ms"), ("Input gain", "0 dB"));
        }

        /// <summary>
        /// Brings the song to <paramref name="targetLufs"/> through the chain from <see cref="MasterBus"/>,
        /// as the Neo Limiter page says: render, read the integrated loudness, raise the limiter's
        /// Input gain by the difference, again. Returns the last measurement (LUFS, dBTP).
        /// </summary>
        public static (double Lufs, double TruePeak) MasterTo(Studio s, double targetLufs, double maxTruePeak = -1, int rounds = 3)
        {
            double lufs = double.NaN, tp = double.NaN, gain = 0;
            for (int i = 0; ; i++)
            {
                var r = s.C.Ok("render", new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "master" }, seconds: 600);
                var m = s.C.Ok("analyze", new JsonObject { ["source"] = J.Str(r["id"]), ["metrics"] = J.A("loudness", "peaks") });
                lufs = J.Num(m["loudness"]?["integrated_lufs"]);
                tp = J.Num(m["peaks"]?["true_peak_dbtp"]);
                if (double.IsNaN(lufs) || double.IsInfinity(lufs) || i >= rounds) break;
                if (System.Math.Abs(lufs - targetLufs) <= 0.3 && tp <= maxTruePeak) break;
                if (tp > maxTruePeak)
                {
                    // The ceiling is a true-peak ceiling, so this shouldn't happen; lower it if it does.
                    var lim = s.C.Ok("param_get", new JsonObject { ["machine"] = "Limit", ["params"] = J.A("Ceiling") });
                    double ceiling = J.Num(J.Objects(lim["values"]).First()["number"]);
                    Set(s, "Limit", ("Ceiling", $"{(ceiling - (tp - maxTruePeak) - 0.2).ToString("0.0", J.C)} dB"));
                }
                gain = System.Math.Clamp(gain + (targetLufs - lufs), -12, 24);
                Set(s, "Limit", ("Input gain", $"{gain.ToString("+0.0;-0.0", J.C)} dB"));
            }
            return (lufs, tp);
        }
    }
}
