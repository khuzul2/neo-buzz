// Evaluation: small JSON builders for tool arguments, and readers for tool results.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal static class J
    {
        public static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static JsonArray A(params JsonNode[] items) => new(items);

        public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode)s).ToArray());

        /// <summary>A note as notes_write takes it.</summary>
        public static JsonObject Note(object pitch, double start, double length, int velocity = 100) => new()
        {
            ["pitch"] = pitch is int p ? JsonValue.Create(p) : JsonValue.Create(Convert.ToString(pitch, C)),
            ["start"] = start,
            ["length"] = length,
            ["velocity"] = velocity,
        };

        public static JsonArray Notes(IEnumerable<JsonObject> notes) => new(notes.Select(n => (JsonNode)n).ToArray());

        /// <summary>param_set changes: (param, value) pairs; a value is a string with its unit or a number.</summary>
        public static JsonArray Changes(params (string Param, object Value)[] changes) =>
            new(changes.Select(c => (JsonNode)new JsonObject
            {
                ["param"] = c.Param,
                ["value"] = c.Value switch
                {
                    int i => JsonValue.Create(i),
                    double d => JsonValue.Create(d),
                    _ => JsonValue.Create(Convert.ToString(c.Value, C)),
                },
            }).ToArray());

        public static double Num(JsonNode n, double fallback = double.NaN)
        {
            if (n is not JsonValue v) return fallback;
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out long l)) return l;
            if (v.TryGetValue(out string s))
            {
                if (s == "-inf") return double.NegativeInfinity;
                if (s == "inf") return double.PositiveInfinity;
                if (double.TryParse(s, NumberStyles.Float, C, out d)) return d;
            }
            return fallback;
        }

        public static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : n?.ToJsonString();

        public static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

        public static IEnumerable<JsonObject> Objects(JsonNode n) => (n as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

        public static string F(double x, int digits = 2) =>
            double.IsNaN(x) ? "n/a" : double.IsNegativeInfinity(x) ? "-inf" : double.IsPositiveInfinity(x) ? "inf" : x.ToString("F" + digits, C);

        /// <summary>A finite number for JSON (null for NaN and infinities).</summary>
        public static JsonNode Finite(double x, int digits = 3) => double.IsNaN(x) || double.IsInfinity(x) ? null : JsonValue.Create(Math.Round(x, digits));
    }
}
