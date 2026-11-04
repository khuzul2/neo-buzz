#nullable disable
// Agent server (docs/specs/mcp-server.md §12.5, U5): successor mappings. A mapping says how a song
// moves from a classic machine to its Neo successor: which parameter becomes which, through real
// units (old raw → the old manifest's unit → the new unit → the new raw), the settings the successor
// is given, the gain its connections take (a mono native effect gets left + right summed: +6 dB),
// how notes move (generators), what has no counterpart and why, and the tolerance a conversion is
// measured against. Data, not code: one file per pair in Machines/Manifests/Successors
// ("<old> to <new>.successor.json"), embedded in this assembly; the rule kinds below are the only code.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Model
{
    public enum SuccessorRuleKind
    {
        /// <summary>The same quantity on both sides (Hz → Hz, s → s, dB → dB, % → %), times scale plus offset.</summary>
        Unit,
        /// <summary>A level as a fraction (%, or a linear gain) → dB: 20·log10(value · scale) + offset; −inf at 0 (or while zero_when is on).</summary>
        Db,
        /// <summary>A threshold as a fraction → the gain that brings it to full scale: 20·log10(scale / value) + offset.</summary>
        InverseDb,
        /// <summary>A choice → a choice (or a value): by the old choice's name, or by its raw value where the old one has no names.</summary>
        Choice,
        /// <summary>A scale with no shared unit (a bare 0–100 % cutoff) laid onto the new range, linear or on a log scale.</summary>
        Range,
        /// <summary>A weighted sum of old values in one unit (Jeskola EQ-3's band gains relative to its mid gain).</summary>
        Sum,
        /// <summary>
        /// Separate dry and wet levels (dB) → a dry/wet crossfade and wet levels, with the output connections making up the
        /// level the crossfade takes away (Jeskola Reverb's Dry Out, Rev Out, ER Out → Neo Verb's Mix, Late, Early).
        /// </summary>
        LevelsToMix,
        /// <summary>A threshold-and-clamp clipper → drive into a hard clip and an output level (Jeskola Distortion → Neo Drive).</summary>
        Clip,
        /// <summary>An old bypass switch → the new machine's bypass (its state only); <c>invert</c>: an enable switch (off bypasses).</summary>
        Bypass,
        /// <summary>
        /// A modulated delay's shortest and longest delay (weighted sums of old times) → the new one's delay at the sweep's
        /// geometric centre and its depth in octaves either side, as a share of what 100 % spans (Neo Ensemble).
        /// </summary>
        Sweep,
        /// <summary>
        /// Separate dry and wet amplitudes (or one wet/dry mix) → a dry/wet crossfade and an output gain in dB:
        /// mix = |W|·s / (|D| + |W|·s), output = 20·log10(|D| + |W|·s), s the wet's level on the new machine against
        /// the old one's. An inverted dry or wet keeps its level, not its polarity (with a note).
        /// </summary>
        DryWet,
        /// <summary>
        /// A graphic EQ whose bands overlap and add (Joachims Hyperion's: bells summed in dB, their width a parameter) onto
        /// one whose response passes through its sliders at other centres (Neo EQ's graphic mode): the old curve read at
        /// each new centre is that slider's value. The old bands are constant-Q bells, <c>bandwidth</c> octaves between their
        /// half-gain points (with <c>rate</c>: the Audio EQ Cookbook's digital peaking filters at that rate, their bandwidth
        /// warped as it does, <c>alpha_max</c> capping their alpha); an optional <c>highpass</c> (its corner in Hz, 0 off)
        /// is a second-order Butterworth high-pass. A gain parameter and whatever the sliders can't reach (the bands' curve
        /// beyond their range) go to <c>output</c>.
        /// </summary>
        Graphic,
    }

    /// <summary>A band of a <see cref="SuccessorRuleKind.Graphic"/> rule: its gain parameter and its centre.</summary>
    public sealed record SuccessorBand(string Param, double Hz);

    /// <summary>One wet level of a <see cref="SuccessorRuleKind.LevelsToMix"/> rule, with the level difference between the two machines' wet sounds.</summary>
    public sealed record SuccessorWet(string From, string To, double GainDb);

    public sealed class SuccessorRule
    {
        public SuccessorRuleKind Kind { get; init; }
        public string From { get; init; }
        public string To { get; init; }
        public double Scale { get; init; } = 1;
        /// <summary>Unit: another old parameter the value is multiplied by (Pedal Plate's decay time scales with its Size).</summary>
        public string Times { get; init; }
        public double Offset { get; init; }
        /// <summary>Clamp of the result, in the new parameter's base unit (Hz, s, dB, fractions for %).</summary>
        public double? Min { get; init; }
        public double? Max { get; init; }
        /// <summary>Db: an old switch that, when on, makes the level −inf (Pedal Gain's Mute).</summary>
        public string ZeroWhen { get; init; }
        /// <summary>Choice: old choice name (or raw value) → the new value as param_set takes it ("Hall", "2", "50%").</summary>
        public IReadOnlyDictionary<string, string> Map { get; init; }
        /// <summary>Choice: the new value for old choices the map leaves out (null: the new parameter keeps its value, with a note).</summary>
        public string Default { get; init; }
        /// <summary>Range: the old range (in its base unit, raw where it has none) and the new one; log for frequencies and times.</summary>
        public double[] FromRange { get; init; }
        public double[] ToRange { get; init; }
        public bool Log { get; init; }
        /// <summary>Bypass: the old switch enables (on is not bypassed).</summary>
        public bool Invert { get; init; }
        /// <summary>Sum: (old parameter, weight).</summary>
        public IReadOnlyList<(string Param, double Weight)> Terms { get; init; }
        /// <summary>LevelsToMix: the dry level, the wet levels and the crossfade.</summary>
        public string Dry { get; init; }
        public IReadOnlyList<SuccessorWet> Wet { get; init; }
        public string Mix { get; init; }
        /// <summary>Clip: the old threshold and clamp (fractions of full scale), the new drive and output (dB); knee: where the new clip goes flat, in dB over its slope-1 line.</summary>
        public string Threshold { get; init; }
        public string Clamp { get; init; }
        public string Drive { get; init; }
        public string Output { get; init; }
        public double KneeDb { get; init; }
        /// <summary>Sweep: the old shortest and longest delay (s) as weighted sums plus an offset; the new delay and depth; octaves at depth 100 %.</summary>
        public IReadOnlyList<(string Param, double Weight)> Low { get; init; }
        public IReadOnlyList<(string Param, double Weight)> High { get; init; }
        public double LowOffset { get; init; }
        public double HighOffset { get; init; }
        public string Delay { get; init; }
        public string Depth { get; init; }
        public double Octaves { get; init; } = 1;
        /// <summary>DryWet: a 0–100 % wet/dry mix instead of separate levels (dry = 1 − mix, wet = mix), and the wet's scale s.</summary>
        public string MixFrom { get; init; }
        public double WetScale { get; init; } = 1;
        /// <summary>Graphic: the old bands, the new sliders, the old bands' width in octaves (a parameter), the old gain (optional).</summary>
        public IReadOnlyList<SuccessorBand> Bands { get; init; }
        public IReadOnlyList<SuccessorBand> Sliders { get; init; }
        public string Bandwidth { get; init; }
        /// <summary>Graphic: a high-pass corner in Hz (a parameter; 0 is off), the old filters' sample rate (null: analog bells) and their alpha's cap.</summary>
        public string Highpass { get; init; }
        public double? Rate { get; init; }
        public double AlphaMax { get; init; } = double.PositiveInfinity;
        public string Gain { get; init; }
        /// <summary>Why (a line in the report): "assumed: closed source", "measured on …".</summary>
        public string Why { get; init; } = "";

        public IEnumerable<string> Inputs => Kind switch
        {
            SuccessorRuleKind.Sum => Terms.Select(t => t.Param),
            SuccessorRuleKind.LevelsToMix => new[] { Dry }.Where(x => x != null).Concat(Wet.Select(w => w.From)),
            SuccessorRuleKind.Clip => new[] { Threshold, Clamp },
            SuccessorRuleKind.Sweep => Low.Concat(High).Select(t => t.Param).Distinct(StringComparer.OrdinalIgnoreCase),
            SuccessorRuleKind.DryWet => MixFrom != null ? new[] { MixFrom } : new[] { Dry, Wet?.FirstOrDefault()?.From }.Where(x => x != null),
            SuccessorRuleKind.Db when ZeroWhen != null => new[] { From, ZeroWhen },
            SuccessorRuleKind.Graphic => Bands.Select(b => b.Param).Append(Bandwidth).Concat(new[] { Gain, Highpass }.Where(x => x != null)),
            SuccessorRuleKind.Unit when Times != null => new[] { From, Times },
            _ => new[] { From },
        };

        public IEnumerable<string> Outputs => Kind switch
        {
            SuccessorRuleKind.LevelsToMix => new[] { Mix }.Concat(Wet.Select(w => w.To)),
            SuccessorRuleKind.Clip => new[] { Drive, Output },
            SuccessorRuleKind.Sweep => new[] { Delay, Depth },
            SuccessorRuleKind.DryWet => new[] { Mix, Output },
            SuccessorRuleKind.Graphic => Sliders.Select(b => b.Param).Append(Output),
            SuccessorRuleKind.Bypass => Array.Empty<string>(),
            _ => new[] { To },
        };
    }

    /// <summary>How a generator's notes move (tracker notes to the successor's notes).</summary>
    public sealed class SuccessorNotes
    {
        /// <summary>The old machine's note column (a Note track parameter).</summary>
        public string Note { get; init; } = "Note";
        /// <summary>A column holding a note to glide to (Jeskola Bass's Slide note), or null.</summary>
        public string Slide { get; init; }
        /// <summary>The glide's length in ticks (Jeskola Bass's Slide lenght), or null: a slide takes the note's length.</summary>
        public string SlideLength { get; init; }
        /// <summary>A note-length column in ticks (Jeskola Bass 3's Note lenght), or null: a note lasts to the track's next.</summary>
        public string Length { get; init; }
        /// <summary>A volume column that becomes velocity, or null (every note at <see cref="Velocity"/>).</summary>
        public string Volume { get; init; }
        /// <summary>The volume (as a fraction: 1 = 100 %) that becomes velocity 127.</summary>
        public double VolumeAtFull { get; init; } = 1;
        /// <summary>Velocity where there's no volume (no column, or a row without a value when the volume doesn't hold).</summary>
        public int Velocity { get; init; } = 100;
        /// <summary>Whether a volume holds until the next (a state parameter) or is the row's only (a non-state one).</summary>
        public bool VolumeHolds { get; init; } = true;
        /// <summary>The new machine's glide time parameter, set where a slide starts.</summary>
        public string Glide { get; init; }
        /// <summary>Semitones the old machine plays above what's written (its manifest's pitch offset when this is null).</summary>
        public int? PitchOffset { get; init; }
        public string Why { get; init; } = "";
    }

    /// <summary>What a conversion is measured against, and what was measured.</summary>
    public sealed class SuccessorTolerance
    {
        /// <summary>Integrated loudness, after against before, within this (dB).</summary>
        public double LoudnessDb { get; init; } = 1;
        /// <summary>Each third-octave band, level-matched, within this (dB), over <see cref="FromHz"/>–<see cref="ToHz"/>, for bands no more than <see cref="FloorDb"/> below the loudest.</summary>
        public double? BandDb { get; init; } = 3;
        public double FromHz { get; init; } = 50;
        public double ToHz { get; init; } = 12000;
        public double FloorDb { get; init; } = 40;
        /// <summary>What was measured, where, on what material.</summary>
        public string Measured { get; init; } = "";
        /// <summary>Why the pair misses its tolerance in some respect (empty: it doesn't).</summary>
        public string WhyNot { get; init; } = "";
    }

    public sealed class SuccessorMap
    {
        public const string Format = "rebuzz-successor/1";
        public const string Suffix = ".successor.json";

        public required string From { get; init; }
        public required string To { get; init; }
        public string Summary { get; init; } = "";
        /// <summary>Added to the gain of every connection into the new machine (a mono native effect got left + right: +6.02 dB).</summary>
        public double InputGainDb { get; init; }
        /// <summary>Added to the gain of every connection out of the new machine.</summary>
        public double OutputGainDb { get; init; }
        /// <summary>Settings the new machine gets first, as param_set takes them ("Mode": "Hall").</summary>
        public IReadOnlyList<(string Param, string Value)> Set { get; init; } = Array.Empty<(string, string)>();
        public IReadOnlyList<SuccessorRule> Rules { get; init; } = Array.Empty<SuccessorRule>();
        /// <summary>Old parameters with no counterpart, and why.</summary>
        public IReadOnlyList<(string Param, string Why)> Unmapped { get; init; } = Array.Empty<(string, string)>();
        public SuccessorNotes Notes { get; init; }
        /// <summary>
        /// How old parameters read where their manifest doesn't say (columns a manifest leaves out, such as Jeskola Bass 3's
        /// non-state Volume, or a draft's bare numbers): a manifest entry's kind, a, b and unit, by parameter name.
        /// </summary>
        public IReadOnlyDictionary<string, ManifestParameter> Describe { get; init; } = new Dictionary<string, ManifestParameter>();
        public SuccessorTolerance Tolerance { get; init; } = new();
        /// <summary>What to know (what changes by design: "Neo Verb is stereo").</summary>
        public IReadOnlyList<string> Remarks { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

        public string Label => $"{From} → {To}";

        public static SuccessorMap Parse(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A successor mapping is a JSON object.");
            if ((string)root["format"] != Format) throw new FormatException($"Unknown successor format \"{(string)root["format"]}\" (expected {Format}).");
            string from = (string)root["from"] ?? throw new FormatException("A successor mapping names its from gear.");
            string to = (string)root["to"] ?? throw new FormatException("A successor mapping names its to gear.");
            var set = new List<(string, string)>();
            if (root["set"] is JsonObject s)
                foreach (var kv in s) set.Add((kv.Key, Text(kv.Value)));
            var rules = (root["rules"] as JsonArray)?.OfType<JsonObject>().Select(r => ParseRule(r, $"{from} → {to}")).ToList() ?? new List<SuccessorRule>();
            var unmapped = (root["unmapped"] as JsonArray)?.OfType<JsonObject>()
                .Select(u => ((string)u["param"] ?? throw new FormatException("An unmapped entry names its param."), (string)u["why"] ?? "")).ToList() ?? new List<(string, string)>();
            SuccessorNotes notes = null;
            if (root["notes"] is JsonObject n)
                notes = new SuccessorNotes
                {
                    Note = (string)n["note"] ?? "Note",
                    Slide = (string)n["slide"],
                    SlideLength = (string)n["slide_length"],
                    Length = (string)n["length"],
                    Volume = (string)n["volume"],
                    VolumeAtFull = (double?)n["volume_at_full"] ?? 1,
                    Velocity = (int?)n["velocity"] ?? 100,
                    VolumeHolds = (bool?)n["volume_holds"] ?? true,
                    Glide = (string)n["glide"],
                    PitchOffset = (int?)n["pitch_offset"],
                    Why = (string)n["why"] ?? "",
                };
            var describe = new Dictionary<string, ManifestParameter>(StringComparer.OrdinalIgnoreCase);
            if (root["describe"] is JsonObject d)
                foreach (var kv in d)
                    if (kv.Value is JsonObject entry)
                    {
                        var copy = (JsonObject)entry.DeepClone();
                        copy["name"] = kv.Key;
                        describe[kv.Key] = MachineManifest.ParseParameterEntry(copy);
                    }
            var tolerance = new SuccessorTolerance();
            if (root["tolerance"] is JsonObject t)
                tolerance = new SuccessorTolerance
                {
                    LoudnessDb = (double?)t["loudness_db"] ?? 1,
                    BandDb = t.ContainsKey("band_db") ? (double?)t["band_db"] : 3,
                    FromHz = (double?)t["from_hz"] ?? 50,
                    ToHz = (double?)t["to_hz"] ?? 12000,
                    FloorDb = (double?)t["floor_db"] ?? 40,
                    Measured = (string)t["measured"] ?? "",
                    WhyNot = (string)t["why_not"] ?? "",
                };
            return new SuccessorMap
            {
                From = from,
                To = to,
                Summary = (string)root["summary"] ?? "",
                InputGainDb = (double?)root["input_gain_db"] ?? 0,
                OutputGainDb = (double?)root["output_gain_db"] ?? 0,
                Set = set,
                Rules = rules,
                Unmapped = unmapped,
                Notes = notes,
                Describe = describe,
                Tolerance = tolerance,
                Remarks = Strings(root["remarks"]),
                Evidence = Strings(root["evidence"]),
            };
        }

        static string Text(JsonNode v) => v is JsonValue jv && jv.TryGetValue(out string text) ? text : v?.ToJsonString() ?? "";

        static IReadOnlyList<string> Strings(JsonNode node) =>
            (node as JsonArray)?.Select(x => (string)x ?? "").Where(x => x.Length > 0).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

        static double[] Pair(JsonNode node, string what)
        {
            if (node == null) return null;
            var a = node as JsonArray;
            if (a == null || a.Count != 2) throw new FormatException($"{what} is [low, high].");
            return new[] { (double)a[0]!, (double)a[1]! };
        }

        static List<SuccessorBand> Bands(JsonNode node) =>
            (node as JsonArray)?.OfType<JsonObject>().Select(x => new SuccessorBand((string)x["param"], (double?)x["hz"] ?? 0)).ToList();

        static List<(string Param, double Weight)> Terms(JsonNode node) =>
            (node as JsonArray)?.OfType<JsonObject>().Select(x => ((string)x["param"], (double?)x["weight"] ?? 1)).ToList();

        static SuccessorRule ParseRule(JsonObject r, string pair)
        {
            string kind = (string)r["kind"] ?? "unit";
            var k = kind switch
            {
                "unit" => SuccessorRuleKind.Unit,
                "db" => SuccessorRuleKind.Db,
                "inverse_db" => SuccessorRuleKind.InverseDb,
                "choice" => SuccessorRuleKind.Choice,
                "range" => SuccessorRuleKind.Range,
                "sum" => SuccessorRuleKind.Sum,
                "levels_to_mix" => SuccessorRuleKind.LevelsToMix,
                "clip" => SuccessorRuleKind.Clip,
                "bypass" => SuccessorRuleKind.Bypass,
                "sweep" => SuccessorRuleKind.Sweep,
                "dry_wet" => SuccessorRuleKind.DryWet,
                "graphic" => SuccessorRuleKind.Graphic,
                _ => throw new FormatException($"{pair}: unknown rule kind \"{kind}\"."),
            };
            var rule = new SuccessorRule
            {
                Kind = k,
                From = (string)r["from"],
                To = (string)r["to"],
                Scale = (double?)r["scale"] ?? 1,
                Times = (string)r["times"],
                Offset = (double?)r["offset"] ?? 0,
                Min = (double?)r["min"],
                Max = (double?)r["max"],
                ZeroWhen = (string)r["zero_when"],
                Map = (r["map"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => Text(kv.Value), StringComparer.OrdinalIgnoreCase),
                Default = (string)r["default"],
                FromRange = Pair(r["from_range"], $"{pair}: from_range"),
                ToRange = Pair(r["to_range"], $"{pair}: to_range"),
                Log = (string)r["curve"] == "log",
                Invert = (bool?)r["invert"] ?? false,
                Terms = Terms(r["terms"]),
                Dry = (string)r["dry"],
                Wet = r["wet"] switch
                {
                    JsonArray a => a.OfType<JsonObject>().Select(x => new SuccessorWet((string)x["from"], (string)x["to"], (double?)x["gain_db"] ?? 0)).ToList(),
                    JsonValue v => new[] { new SuccessorWet((string)v, null, 0) },
                    _ => null,
                },
                Low = Terms(r["low"]),
                High = Terms(r["high"]),
                LowOffset = (double?)r["low_offset"] ?? 0,
                HighOffset = (double?)r["high_offset"] ?? 0,
                Delay = (string)r["delay"],
                Depth = (string)r["depth"],
                Octaves = (double?)r["octaves"] ?? 1,
                MixFrom = (string)r["mix_from"],
                WetScale = (double?)r["wet_scale"] ?? 1,
                Bands = Bands(r["bands"]),
                Sliders = Bands(r["sliders"]),
                Bandwidth = (string)r["bandwidth"],
                Highpass = (string)r["highpass"],
                Rate = (double?)r["rate"],
                AlphaMax = (double?)r["alpha_max"] ?? double.PositiveInfinity,
                Gain = (string)r["gain"],
                Mix = (string)r["mix"],
                Threshold = (string)r["threshold"],
                Clamp = (string)r["clamp"],
                Drive = (string)r["drive"],
                Output = (string)r["output"],
                KneeDb = (double?)r["knee_db"] ?? 0,
                Why = (string)r["why"] ?? "",
            };
            string missing = k switch
            {
                SuccessorRuleKind.Sum when rule.Terms == null || rule.Terms.Count == 0 || rule.To == null => "terms and to",
                SuccessorRuleKind.LevelsToMix when rule.Wet == null || rule.Wet.Count == 0 || rule.Mix == null || rule.Wet.Any(w => w.To == null) => "wet (from, to) and mix",
                SuccessorRuleKind.Sweep when rule.Low == null || rule.High == null || rule.Delay == null || rule.Depth == null || !(rule.Octaves > 0) => "low, high, delay, depth and octaves",
                SuccessorRuleKind.DryWet when (rule.MixFrom == null && (rule.Wet == null || rule.Wet.Count != 1)) || rule.Mix == null || rule.Output == null => "wet (and dry) or mix_from, mix and output",
                SuccessorRuleKind.Clip when rule.Threshold == null || rule.Clamp == null || rule.Drive == null || rule.Output == null => "threshold, clamp, drive and output",
                SuccessorRuleKind.Range when rule.FromRange == null || rule.ToRange == null || rule.From == null || rule.To == null => "from, to, from_range and to_range",
                SuccessorRuleKind.Choice when rule.Map == null || rule.From == null || rule.To == null => "from, to and map",
                SuccessorRuleKind.Bypass when rule.From == null => "from",
                SuccessorRuleKind.Graphic when rule.Bands == null || rule.Bands.Count == 0 || rule.Sliders == null || rule.Sliders.Count == 0 ||
                                               rule.Bandwidth == null || rule.Output == null || rule.Bands.Concat(rule.Sliders).Any(b => b.Param == null || !(b.Hz > 0))
                    => "bands and sliders (param, hz), bandwidth and output",
                SuccessorRuleKind.Unit or SuccessorRuleKind.Db or SuccessorRuleKind.InverseDb when rule.From == null || rule.To == null => "from and to",
                _ => null,
            };
            if (missing != null) throw new FormatException($"{pair}: a {kind} rule needs {missing}.");
            return rule;
        }
    }

    /// <summary>The successor mappings this build knows (embedded from Machines/Manifests/Successors).</summary>
    public static class SuccessorLibrary
    {
        const string Prefix = "ReBuzz.Agent.Successors.";

        static readonly Lazy<(IReadOnlyList<SuccessorMap> Maps, IReadOnlyList<string> Errors)> loaded = new(Load);

        public static IReadOnlyList<SuccessorMap> All => loaded.Value.Maps;

        /// <summary>Mappings that didn't parse (a build error; the tests check there are none).</summary>
        public static IReadOnlyList<string> Errors => loaded.Value.Errors;

        public static IEnumerable<SuccessorMap> For(string fromGear) =>
            All.Where(m => string.Equals(m.From, fromGear, StringComparison.OrdinalIgnoreCase));

        public static SuccessorMap Find(string fromGear, string toGear) =>
            For(fromGear).FirstOrDefault(m => string.Equals(m.To, toGear, StringComparison.OrdinalIgnoreCase));

        static (IReadOnlyList<SuccessorMap>, IReadOnlyList<string>) Load()
        {
            var maps = new List<SuccessorMap>();
            var errors = new List<string>();
            var asm = typeof(SuccessorLibrary).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(SuccessorMap.Suffix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            {
                try
                {
                    using var stream = asm.GetManifestResourceStream(name)!;
                    using var reader = new StreamReader(stream);
                    maps.Add(SuccessorMap.Parse(reader.ReadToEnd()));
                }
                catch (Exception e) { errors.Add($"{name.Substring(Prefix.Length)}: {e.Message}"); }
            }
            return (maps, errors);
        }
    }
}
