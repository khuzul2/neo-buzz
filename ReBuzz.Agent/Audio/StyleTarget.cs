#nullable disable
// Agent server (docs/specs/mastering-tools.md §6): a style target, what a master is measured against
// when there is no single reference: the mean of the user's own reference recordings (target_build), or
// one reference measured on the fly (analyze's target). It holds the long-term tonal curve (each source
// moved to 0 dB from 100 Hz to 10 kHz, then averaged), the loudness and its range, the PLR, the side/mid
// ratio and the microdynamics in Ozone's four bands, each as a mean with the sources' spread. Saved as a
// file (*.target.json), so one target serves every song; the app ships none (curves averaged from
// commercial records would need their rights checked).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;

namespace ReBuzz.Agent.Audio
{
    internal sealed class StyleTarget
    {
        public const string Format = "neobuzz-live-target";
        public const int Version = 1;

        /// <summary>A measured value over the sources: the mean and the range.</summary>
        internal readonly record struct Spread(double Mean, double Min, double Max)
        {
            public static Spread Of(IEnumerable<double> values)
            {
                var v = values.Where(double.IsFinite).ToList();
                return v.Count == 0 ? new Spread(double.NaN, double.NaN, double.NaN) : new Spread(v.Average(), v.Min(), v.Max());
            }

            /// <summary>Power ratios in dB: averaged as powers (so a mono source, −∞ dB, counts as no side, not as no value).</summary>
            public static Spread OfRatios(IEnumerable<double> ratios)
            {
                var v = ratios.Where(r => r >= 0 && !double.IsPositiveInfinity(r)).ToList();
                return v.Count == 0 ? new Spread(double.NaN, double.NaN, double.NaN)
                    : new Spread(Spectrum.Db(v.Average()), Spectrum.Db(v.Min()), Spectrum.Db(v.Max()));
            }
        }

        public string Name;
        public List<(string Name, double Seconds, double Lufs)> Sources = new();
        public TonalCurve Curve;
        public Spread Lufs, RangeLu, PlrDb;
        /// <summary>By Microdynamics.Bands: side over mid in dB (−∞ mono) and LDR in dB.</summary>
        public Spread[] SideToMidDb, LdrDb;

        /// <summary>Measures sources and averages them.</summary>
        public static StyleTarget Build(string name, IReadOnlyList<(string Name, Analysed Audio)> sources)
        {
            var t = new StyleTarget { Name = name };
            foreach (var (n, a) in sources) t.Sources.Add((n, a.Audio.Seconds, a.LoudnessResult.Integrated));
            t.Curve = NeoBuzz.Analysis.Tonal.Average(sources.Select(s => s.Audio.Tonal).ToList());
            t.Lufs = Spread.Of(sources.Select(s => s.Audio.LoudnessResult.Integrated));
            t.RangeLu = Spread.Of(sources.Select(s => s.Audio.LoudnessResult.Range));
            t.PlrDb = Spread.Of(sources.Select(s => s.Audio.DynamicsResult.PlrDb));
            int bands = Microdynamics.Bands.Length;
            t.SideToMidDb = new Spread[bands];
            t.LdrDb = new Spread[bands];
            for (int b = 0; b < bands; b++)
            {
                t.SideToMidDb[b] = Spread.OfRatios(sources.Select(s => s.Audio.StereoResult.MsBands[b].Width));
                t.LdrDb[b] = Spread.Of(sources.Select(s => s.Audio.Microdynamics.Bands[b].LdrDb));
            }
            return t;
        }

        static JsonNode N(double x, int digits = 2) => AudioReport.Db(x, digits);

        static JsonObject Json(Spread s) => new() { ["mean"] = N(s.Mean), ["min"] = N(s.Min), ["max"] = N(s.Max) };

        static JsonObject PerBand(Spread[] spreads)
        {
            var o = new JsonObject();
            for (int b = 0; b < spreads.Length; b++) o[Microdynamics.Bands[b].Name] = Json(spreads[b]);
            return o;
        }

        public JsonObject ToJson() => new()
        {
            ["format"] = Format,
            ["version"] = Version,
            ["name"] = Name,
            ["sources"] = new JsonArray(Sources.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["seconds"] = Math.Round(s.Seconds, 2), ["integrated_lufs"] = N(s.Lufs, 1) }).ToArray()),
            ["tonal"] = new JsonObject
            {
                ["about"] = "Long-term curve, 1/6 octave, each source moved to 0 dB mean from 100 Hz to 10 kHz, then averaged",
                ["hz"] = new JsonArray(Curve.Hz.Select(h => (JsonNode)Math.Round(h, 1)).ToArray()),
                ["db"] = new JsonArray(Curve.Db.Select(d => N(d)).ToArray()),
            },
            ["integrated_lufs"] = Json(Lufs),
            ["range_lu"] = Json(RangeLu),
            ["plr_db"] = Json(PlrDb),
            ["side_to_mid_db"] = PerBand(SideToMidDb),
            ["ldr_db"] = PerBand(LdrDb),
        };

        static double D(JsonNode n) => n == null ? double.NaN : n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>() switch { "-inf" => double.NegativeInfinity, "inf" => double.PositiveInfinity, _ => double.NaN }
            : n.GetValue<double>();

        static Spread SpreadOf(JsonNode n) => n == null ? new Spread(double.NaN, double.NaN, double.NaN) : new Spread(D(n["mean"]), D(n["min"]), D(n["max"]));

        public static StyleTarget FromJson(JsonObject o)
        {
            if ((string)o["format"] != Format) throw new FormatException($"Not a style target (format \"{Format}\").");
            var t = new StyleTarget { Name = (string)o["name"] ?? "target" };
            foreach (var s in o["sources"]?.AsArray() ?? new JsonArray())
                t.Sources.Add(((string)s["name"], D(s["seconds"]), D(s["integrated_lufs"])));
            var tonal = o["tonal"] ?? throw new FormatException("The target has no tonal curve.");
            t.Curve = new TonalCurve { Hz = tonal["hz"]!.AsArray().Select(D).ToArray(), Db = tonal["db"]!.AsArray().Select(D).ToArray() };
            if (t.Curve.Hz.Length < 2 || t.Curve.Hz.Length != t.Curve.Db.Length) throw new FormatException("The target's tonal curve is malformed.");
            t.Lufs = SpreadOf(o["integrated_lufs"]);
            t.RangeLu = SpreadOf(o["range_lu"]);
            t.PlrDb = SpreadOf(o["plr_db"]);
            int bands = Microdynamics.Bands.Length;
            t.SideToMidDb = new Spread[bands];
            t.LdrDb = new Spread[bands];
            for (int b = 0; b < bands; b++)
            {
                t.SideToMidDb[b] = SpreadOf(o["side_to_mid_db"]?[Microdynamics.Bands[b].Name]);
                t.LdrDb[b] = SpreadOf(o["ldr_db"]?[Microdynamics.Bands[b].Name]);
            }
            return t;
        }

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
            File.WriteAllText(file, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public static StyleTarget Load(string file) =>
            FromJson(JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new FormatException("A style target is a JSON object."));
    }
}
