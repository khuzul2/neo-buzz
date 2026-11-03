#nullable disable
// Agent server (docs/specs/mcp-server.md §8.3): a machine manifest, the sidecar file next to a
// machine's DLL ("Neo Poly.machine.json") that describes every parameter exactly: unit, mapping,
// choices, group and meaning. Neo machines' manifests are generated from their Params.def
// (tools/gen-machine-params.py). Other machines get drafts profiled by tools/machine-profiler.py
// (§12.2, U1), marked "status": "draft": units inferred from the machine's own value texts, the
// pitch convention and note input heard, health measured. Drafts live in Machines/Manifests and
// are copied beside their DLLs. They change nothing a song saves: every conversion is the agent's.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Model
{
    public sealed class ManifestParameter
    {
        public int Index { get; init; }
        public string Id { get; init; } = "";
        public required string Name { get; init; }
        public string Group { get; init; } = "";
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        public required ParamMapping Mapping { get; init; }
        public string Description { get; init; } = "";
        /// <summary>
        /// Drafts: where the mapping came from. "values": fitted to the machine's value texts (exact to
        /// their precision); "description": read from its description ("1 ms – 4 s exponential"), approximate.
        /// Empty for generated and curated manifests (exact).
        /// </summary>
        public string Confidence { get; init; } = "";
        public bool Approximate => Confidence == "description";
    }

    /// <summary>How a generator takes notes (drafts, §12.2): heard by the profiler.</summary>
    public sealed class ManifestNotes
    {
        /// <summary>"midi" (the engine's MIDI column), "tracks" (its Note track parameter only) or "none".</summary>
        public string Input { get; init; } = "";
        /// <summary>Semitones added to a written pitch before the machine gets it, so the written pitch plays (A4 = 440 Hz).</summary>
        public int PitchOffset { get; init; }
        /// <summary>False for unpitched generators (drums, noise): no offset applies.</summary>
        public bool Pitched { get; init; } = true;
        /// <summary>Voices heard at once (0: not measured).</summary>
        public int Polyphony { get; init; }
    }

    /// <summary>What the profiler found wrong (drafts): "ok", "warn" or "unhealthy", with the problems.</summary>
    public sealed class ManifestHealth
    {
        public string Status { get; init; } = "ok";
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
        public bool Unhealthy => Status == "unhealthy";
    }

    /// <summary>A kind of data file a machine loads (machine_data_import): a wavetable, a DX7 bank…</summary>
    public sealed class ManifestImport
    {
        public required string Kind { get; init; }
        /// <summary>Lower-case extensions with the dot (".wav"); empty: any.</summary>
        public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
        public string Description { get; init; } = "";
    }

    public sealed class MachineManifest
    {
        public const string Format = "rebuzz-machine-manifest/1";
        public const string Suffix = ".machine.json";

        public required string Gear { get; init; }
        /// <summary>"generator" or "effect".</summary>
        public string Kind { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Source { get; init; } = "";
        public int Tracks { get; init; }
        public IReadOnlyList<ManifestParameter> Parameters { get; init; } = Array.Empty<ManifestParameter>();
        /// <summary>Track parameters described in full (drafts); generated manifests list only their names.</summary>
        public IReadOnlyList<ManifestParameter> TrackParameters { get; init; } = Array.Empty<ManifestParameter>();
        public IReadOnlyList<ManifestImport> Imports { get; init; } = Array.Empty<ManifestImport>();
        /// <summary>"draft" for a profiled manifest nobody has curated yet; empty for generated and curated ones.</summary>
        public string Status { get; init; } = "";
        public bool IsDraft => Status == "draft";
        /// <summary>What it is, in a word or two ("filter", "reverb", "synth", "drums"); drafts.</summary>
        public string Type { get; init; } = "";
        /// <summary>Note input and pitch convention (generators); null when the manifest doesn't say (Neo: MIDI, A4 = 440 Hz).</summary>
        public ManifestNotes Notes { get; init; }
        /// <summary>The profiler's health verdict; null when not profiled.</summary>
        public ManifestHealth Health { get; init; }

        public ManifestParameter Find(string name) =>
            Parameters.FirstOrDefault(p => p.Name == name) ?? Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public ManifestParameter FindTrack(string name) =>
            TrackParameters.FirstOrDefault(p => p.Name == name) ?? TrackParameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>"generated", "curated" or "draft": how far to trust it.</summary>
        public string Quality => IsDraft ? "draft" : Status.Length > 0 ? Status : Source.StartsWith("generated", StringComparison.OrdinalIgnoreCase) ? "generated" : "curated";

        /// <summary>The manifest beside a machine DLL ("…/Neo Poly.NET.dll" → "…/Neo Poly.machine.json"), or null.</summary>
        public static string PathFor(string dllPath, string gearName)
        {
            if (string.IsNullOrEmpty(dllPath) || string.IsNullOrEmpty(gearName)) return null;
            string dir = Path.GetDirectoryName(dllPath);
            return dir == null ? null : Path.Combine(dir, gearName + Suffix);
        }

        /// <summary>Reads a manifest file; null when there is none. A broken one throws (it's a build error, not the agent's).</summary>
        public static MachineManifest Load(string path) =>
            path != null && File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

        public static MachineManifest Parse(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A manifest is a JSON object.");
            string format = (string)root["format"];
            if (format != Format) throw new FormatException($"Unknown manifest format \"{format}\" (expected {Format}).");
            var parameters = new List<ManifestParameter>();
            if (root["parameters"] is JsonArray list)
                foreach (var node in list)
                    if (node is JsonObject p) parameters.Add(ParseParameter(p));
            // Generated manifests list track parameters by name; drafts describe them in full.
            var trackParameters = new List<ManifestParameter>();
            if (root["track_parameters"] is JsonArray tracks)
                foreach (var node in tracks)
                    if (node is JsonObject p) trackParameters.Add(ParseParameter(p));
            ManifestNotes notes = null;
            if (root["notes"] is JsonObject n)
                notes = new ManifestNotes
                {
                    Input = (string)n["input"] ?? "",
                    PitchOffset = (int?)n["pitch_offset"] ?? 0,
                    Pitched = (bool?)n["pitched"] ?? true,
                    Polyphony = (int?)n["polyphony"] ?? 0,
                };
            ManifestHealth health = null;
            if (root["health"] is JsonObject h)
                health = new ManifestHealth
                {
                    Status = (string)h["status"] ?? "ok",
                    Problems = (h["problems"] as JsonArray)?.Select(x => (string)x ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>(),
                };
            return new MachineManifest
            {
                Gear = (string)root["gear"] ?? throw new FormatException("A manifest names its gear."),
                Kind = (string)root["kind"] ?? "",
                Summary = (string)root["summary"] ?? "",
                Source = (string)root["source"] ?? "",
                Tracks = (int?)root["tracks"] ?? 0,
                Parameters = parameters,
                TrackParameters = trackParameters,
                Status = (string)root["status"] ?? "",
                Type = (string)root["type"] ?? "",
                Notes = notes,
                Health = health,
                Imports = (root["imports"] as JsonArray)?.OfType<JsonObject>().Select(i => new ManifestImport
                {
                    Kind = (string)i["kind"] ?? "",
                    Extensions = (i["extensions"] as JsonArray)?.Select(e => ((string)e ?? "").ToLowerInvariant()).ToList() ?? new List<string>(),
                    Description = (string)i["description"] ?? "",
                }).Where(i => i.Kind.Length > 0).ToList() ?? new List<ManifestImport>(),
            };
        }

        /// <summary>A table point's value: a number, or "-inf"/"inf" (JSON has no infinities).</summary>
        static double PointValue(JsonNode v) =>
            v is JsonValue s && s.TryGetValue(out string text)
                ? (text.Trim().StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity)
                : (double)v!;

        static ManifestParameter ParseParameter(JsonObject p)
        {
            string kind = (string)p["kind"] ?? "linear";
            var mappingKind = kind switch
            {
                "linear" => MappingKind.Linear,
                "exp" => MappingKind.Exp,
                "power" => MappingKind.Power,
                "db" => MappingKind.Db,
                "time" => MappingKind.Time,
                "freq" => MappingKind.Freq,
                "percent" => MappingKind.Percent,
                "bipolar" => MappingKind.Bipolar,
                "int" => MappingKind.Int,
                "enum" => MappingKind.Enum,
                "switch" => MappingKind.Switch,
                "ratio" => MappingKind.Ratio,
                "table" => MappingKind.Table,
                _ => throw new FormatException($"Unknown parameter kind \"{kind}\"."),
            };
            var choices = (p["choices"] as JsonArray)?.Select(c => (string)c ?? "").ToList();
            double a = (double?)p["a"] ?? 0, b = (double?)p["b"] ?? (mappingKind == MappingKind.Int ? 0 : 1);
            string unit = (string)p["unit"] ?? "";
            // A table (drafts): [raw, value] points, the machine's own values where no curve fits.
            var points = (p["points"] as JsonArray)?.OfType<JsonArray>().Where(x => x.Count >= 2)
                .Select(x => (Raw: (int)x[0]!, Value: PointValue(x[1]))).OrderBy(x => x.Raw).ToList();
            if (mappingKind == MappingKind.Table && (points == null || points.Count == 0))
                throw new FormatException($"Parameter \"{(string)p["name"]}\": a table needs points.");
            // Values in ms or kHz are kept in the base unit (s, Hz), as the agent's input is converted.
            if (mappingKind is MappingKind.Linear or MappingKind.Exp or MappingKind.Power or MappingKind.Table or MappingKind.Time or MappingKind.Freq)
            {
                double scale = unit == "ms" ? 0.001 : unit == "kHz" ? 1000 : 1;
                if (scale != 1)
                {
                    a *= scale;
                    b *= scale;
                    points = points?.Select(x => (x.Raw, x.Value * scale)).ToList();
                    unit = unit == "ms" ? "s" : "Hz";
                }
            }
            return new ManifestParameter
            {
                Index = (int?)p["index"] ?? 0,
                Id = (string)p["id"] ?? "",
                Name = (string)p["name"] ?? throw new FormatException("A manifest parameter has a name."),
                Group = (string)p["group"] ?? "",
                Min = (int?)p["min"] ?? 0,
                Max = (int?)p["max"] ?? 0,
                Default = (int?)p["default"] ?? 0,
                Description = (string)p["description"] ?? "",
                Confidence = (string)p["confidence"] ?? "",
                Mapping = new ParamMapping
                {
                    Kind = mappingKind,
                    A = a,
                    B = b,
                    Exponent = (double?)p["exponent"] ?? 1,
                    Flag = (bool?)p["flag"] ?? false,
                    Unit = unit,
                    Names = mappingKind == MappingKind.Switch && choices == null ? new[] { "Off", "On" } : choices,
                    Points = points,
                },
            };
        }
    }
}
