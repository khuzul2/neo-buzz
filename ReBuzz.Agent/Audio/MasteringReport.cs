#nullable disable
// Agent server (docs/specs/mastering-tools.md §6): what the tools (ReBuzz/Agent/MasteringTools.cs, analyze)
// reach of the mastering measures: style targets (built from references, loaded from files, or one
// reference measured on the fly), analyze against a target, and eq_fit (Neo EQ bands that follow the
// difference between a master and its target, or a curve given).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using NeoBuzz.Eq;

namespace ReBuzz.Agent.Audio
{
    /// <summary>A style target, opaque to the tools.</summary>
    public sealed class TargetHandle
    {
        internal StyleTarget Target { get; }
        internal TargetHandle(StyleTarget t) => Target = t;
        public string Name => Target.Name;
        public int Sources => Target.Sources.Count;
        public JsonObject ToJson() => Target.ToJson();
        public void Save(string file) => Target.Save(file);
    }

    public static class MasteringReport
    {
        public const string TargetExtension = ".target.json";

        public static TargetHandle LoadTarget(string file) => new(StyleTarget.Load(file));

        /// <summary>One reference as a target (the mean and the range are its own values).</summary>
        public static TargetHandle TargetFrom(string name, StereoAudio audio) => Build(name, new[] { (name, audio) });

        public static TargetHandle Build(string name, IReadOnlyList<(string Name, StereoAudio Audio)> sources) =>
            new(StyleTarget.Build(name, sources.Select(s => (s.Name, new Analysed(s.Audio))).ToList()));

        /// <summary>analyze's measures, against <paramref name="target"/> when given.</summary>
        public static JsonObject Measure(StereoAudio audio, IReadOnlyCollection<string> metrics, TargetHandle target) =>
            AudioReport.Measure(new Analysed(audio), metrics, target?.Target);

        /// <summary>A target's summary as analyze and target_build report it.</summary>
        public static string Describe(TargetHandle t)
        {
            var s = t.Target;
            string F(double x) => AudioReport.F(x);
            return $"{s.Name}: {s.Sources.Count} source(s), {F(s.Lufs.Mean)} LUFS integrated" +
                   (s.Sources.Count > 1 ? $" ({F(s.Lufs.Min)} to {F(s.Lufs.Max)})" : "") +
                   $", PLR {F(s.PlrDb.Mean)} dB, range {F(s.RangeLu.Mean)} LU, LDR " +
                   string.Join("/", s.LdrDb.Select(x => F(x.Mean))) + " dB, side/mid " +
                   string.Join("/", s.SideToMidDb.Select(x => F(x.Mean))) + " dB (below 120 Hz, to 2 kHz, to 10 kHz, above)";
        }

        static readonly string[] TypeNames = { "Bell", "Low shelf", "High shelf", "Low cut", "High cut", "Notch" };

        /// <summary>
        /// eq_fit from a master and its target: the tonal difference (level-matched, smoothed over a third of an octave), each point
        /// clipped to ±<paramref name="limitDb"/>, fitted with up to <paramref name="bands"/> bands at <paramref name="rate"/>.
        /// </summary>
        public static JsonObject FitEq(StereoAudio source, TargetHandle target, int bands, double maxGainDb, double limitDb, double rate)
        {
            var a = new Analysed(source);
            var diff = Tonal.Difference(a.Tonal, target.Target.Curve);
            var r = FitEq(diff.Hz, diff.SmoothedDb, bands, maxGainDb, limitDb, rate);
            r["before_rms_db_unsmoothed"] = AudioReport.Num(diff.RmsDb, 2);
            r["target"] = target.Name;
            return r;
        }

        /// <summary>eq_fit from a curve: <paramref name="db"/> wanted at <paramref name="hz"/> (positive: boost).</summary>
        public static JsonObject FitEq(double[] hz, double[] db, int bands, double maxGainDb, double limitDb, double rate)
        {
            var points = Enumerable.Range(0, hz.Length).Where(i => hz[i] >= 25 && hz[i] <= Math.Min(16000, 0.45 * rate)).ToList();
            var fx = points.Select(i => hz[i]).ToArray();
            var fy = points.Select(i => double.IsFinite(db[i]) ? Math.Clamp(db[i], -limitDb, limitDb) : double.NaN).ToArray();
            var fit = EqFit.Fit(fx, fy, rate, bands, maxGainDb);
            var list = new JsonArray();
            foreach (var b in fit.Bands)
                list.Add(new JsonObject
                {
                    ["type"] = TypeNames[(int)b.Type],
                    ["freq_hz"] = Math.Round(b.Hz, b.Hz < 100 ? 1 : 0),
                    ["gain_db"] = Math.Round(b.GainDb, 1),
                    ["q"] = b.Type == BandType.Bell ? Math.Round(b.Q, 2) : 0.71,
                });
            return new JsonObject
            {
                ["bands"] = list,
                ["rms_before_db"] = Math.Round(fit.RmsBeforeDb, 2),
                ["rms_after_db"] = Math.Round(fit.RmsAfterDb, 2),
                ["worst_left_db"] = Math.Round(fit.WorstDb, 1),
                ["worst_left_hz"] = Math.Round(fit.WorstHz),
                ["limit_db"] = limitDb,
                ["curve"] = new JsonObject
                {
                    ["hz"] = new JsonArray(fit.Hz.Select(h => (JsonNode)Math.Round(h, 1)).ToArray()),
                    ["wanted_db"] = new JsonArray(fy.Select(y => AudioReport.Num(y, 2)).ToArray()),
                    ["fitted_db"] = new JsonArray(fit.FittedDb.Select(y => AudioReport.Num(y, 2)).ToArray()),
                },
            };
        }

        /// <summary>
        /// The param_set changes that put fitted bands (FitEq's "bands") into a Neo EQ's B2–B7, in order of frequency, with the
        /// EQ in Parametric mode; the bands left over at 0 dB (exactly transparent), B1 and B8 (the cuts) untouched.
        /// </summary>
        public static JsonArray NeoEqChanges(JsonArray bands)
        {
            var changes = new JsonArray();
            void Set(string param, JsonNode value) => changes.Add(new JsonObject { ["param"] = param, ["value"] = value });
            Set("Mode", "Parametric");
            for (int k = 0; k < 6; k++)
            {
                string b = $"B{k + 2}";
                if (k < bands.Count)
                {
                    var band = bands[k]!;
                    Set($"{b} on", "On");
                    Set($"{b} type", band["type"]!.GetValue<string>());
                    Set($"{b} freq", $"{band["freq_hz"]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)} Hz");
                    Set($"{b} gain", $"{band["gain_db"]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)} dB");
                    Set($"{b} Q", band["q"]!.GetValue<double>());
                }
                else Set($"{b} gain", "0 dB");
            }
            return changes;
        }
    }
}
