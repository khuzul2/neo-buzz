#nullable disable
// Agent server (docs/specs/mcp-server.md §8.3): a machine manifest, the sidecar file next to a
// machine's DLL ("Neo Poly.machine.json") that describes every parameter exactly: unit, mapping,
// choices, group and meaning. Neo machines' manifests are generated from their Params.def
// (tools/gen-machine-params.py); other machines can get curated or profiled ones later (§12).

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

        public ManifestParameter Find(string name) =>
            Parameters.FirstOrDefault(p => p.Name == name) ?? Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

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
            return new MachineManifest
            {
                Gear = (string)root["gear"] ?? throw new FormatException("A manifest names its gear."),
                Kind = (string)root["kind"] ?? "",
                Summary = (string)root["summary"] ?? "",
                Source = (string)root["source"] ?? "",
                Tracks = (int?)root["tracks"] ?? 0,
                Parameters = parameters,
            };
        }

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
                _ => throw new FormatException($"Unknown parameter kind \"{kind}\"."),
            };
            var choices = (p["choices"] as JsonArray)?.Select(c => (string)c ?? "").ToList();
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
                Mapping = new ParamMapping
                {
                    Kind = mappingKind,
                    A = (double?)p["a"] ?? 0,
                    B = (double?)p["b"] ?? (mappingKind == MappingKind.Int ? 0 : 1),
                    Exponent = (double?)p["exponent"] ?? 1,
                    Flag = (bool?)p["flag"] ?? false,
                    Unit = (string)p["unit"] ?? "",
                    Names = mappingKind == MappingKind.Switch && choices == null ? new[] { "Off", "On" } : choices,
                },
            };
        }
    }
}
