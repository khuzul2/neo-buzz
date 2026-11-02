// MCP server core (docs/specs/mcp-server.md §4): JSON Schema for tool inputs and outputs, built
// in code, and a validator for the subset used (type, properties, required, items, enum, ranges,
// anyOf) so a malformed call gets an error naming the exact argument instead of a failure later.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Mcp
{
    public static class Schema
    {
        public static (string Name, JsonObject Schema, bool Required) Req(string name, JsonObject schema) => (name, schema, true);
        public static (string Name, JsonObject Schema, bool Required) Opt(string name, JsonObject schema) => (name, schema, false);

        /// <summary>An object with the given properties; no others allowed.</summary>
        public static JsonObject Object(string? description, params (string Name, JsonObject Schema, bool Required)[] properties)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, schema, isRequired) in properties)
            {
                props[name] = schema;
                if (isRequired) required.Add(name);
            }
            var o = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
            if (required.Count > 0) o["required"] = required;
            if (description != null) o["description"] = description;
            return o;
        }

        /// <summary>An object whose keys are free (a map), each value matching <paramref name="values"/>.</summary>
        public static JsonObject Map(string description, JsonObject values) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = values,
        };

        public static JsonObject String(string description, params string[] choices)
        {
            var s = new JsonObject { ["type"] = "string", ["description"] = description };
            if (choices.Length > 0) s["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
            return s;
        }

        public static JsonObject Number(string description, double? minimum = null, double? maximum = null)
        {
            var s = new JsonObject { ["type"] = "number", ["description"] = description };
            if (minimum.HasValue) s["minimum"] = minimum.Value;
            if (maximum.HasValue) s["maximum"] = maximum.Value;
            return s;
        }

        public static JsonObject Integer(string description, long? minimum = null, long? maximum = null)
        {
            var s = new JsonObject { ["type"] = "integer", ["description"] = description };
            if (minimum.HasValue) s["minimum"] = minimum.Value;
            if (maximum.HasValue) s["maximum"] = maximum.Value;
            return s;
        }

        public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

        public static JsonObject Array(JsonObject items, string description, int? minItems = null)
        {
            var s = new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = description };
            if (minItems.HasValue) s["minItems"] = minItems.Value;
            return s;
        }

        /// <summary>One of several shapes (a value given as text or as a number, say).</summary>
        public static JsonObject AnyOf(string description, params JsonObject[] options) => new()
        {
            ["description"] = description,
            ["anyOf"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray()),
        };

        /// <summary>Checks <paramref name="value"/>; adds readable problems ("notes[2].pitch: expected a number") to <paramref name="errors"/>.</summary>
        public static void Validate(JsonObject schema, JsonNode? value, string path, List<string> errors)
        {
            if (schema["anyOf"] is JsonArray any)
            {
                foreach (var option in any)
                {
                    var trial = new List<string>();
                    Validate((JsonObject)option!, value, path, trial);
                    if (trial.Count == 0) return;
                }
                errors.Add($"{Where(path)}: {Expected(schema)}");
                return;
            }
            string? type = schema["type"]?.GetValue<string>();
            switch (type)
            {
                case "object":
                    if (value is not JsonObject obj)
                    {
                        errors.Add($"{Where(path)}: expected an object");
                        return;
                    }
                    var props = schema["properties"] as JsonObject;
                    if (schema["required"] is JsonArray required)
                        foreach (var r in required)
                        {
                            string name = r!.GetValue<string>();
                            if (!obj.ContainsKey(name) || obj[name] == null)
                                errors.Add($"{Where(Join(path, name))}: missing (required)");
                        }
                    foreach (var (key, v) in obj)
                    {
                        if (props != null && props[key] is JsonObject ps) Validate(ps, v, Join(path, key), errors);
                        else if (schema["additionalProperties"] is JsonObject extra) Validate(extra, v, Join(path, key), errors);
                        else if (schema["additionalProperties"] is JsonValue no && no.GetValue<bool>() == false)
                        {
                            string known = props == null ? "" : " (known: " + string.Join(", ", props.Select(p => p.Key)) + ")";
                            errors.Add($"{Where(Join(path, key))}: unknown argument{known}");
                        }
                    }
                    return;
                case "array":
                    if (value is not JsonArray arr)
                    {
                        errors.Add($"{Where(path)}: expected a list");
                        return;
                    }
                    if (TryNumber(schema["minItems"], out double minItems) && arr.Count < minItems)
                        errors.Add($"{Where(path)}: needs at least {minItems} item(s)");
                    if (schema["items"] is JsonObject items)
                        for (int i = 0; i < arr.Count; i++) Validate(items, arr[i], $"{path}[{i}]", errors);
                    return;
                case "string":
                    if (value is not JsonValue sv || !sv.TryGetValue(out string? s))
                    {
                        errors.Add($"{Where(path)}: expected text");
                        return;
                    }
                    if (schema["enum"] is JsonArray choices && !choices.Any(c => c!.GetValue<string>() == s))
                        errors.Add($"{Where(path)}: \"{s}\" is not one of {string.Join(", ", choices.Select(c => c!.GetValue<string>()))}");
                    return;
                case "number":
                case "integer":
                    if (!TryNumber(value, out double d))
                    {
                        errors.Add($"{Where(path)}: expected {(type == "integer" ? "a whole number" : "a number")}");
                        return;
                    }
                    if (type == "integer" && d != System.Math.Floor(d)) errors.Add($"{Where(path)}: expected a whole number, got {d.ToString(CultureInfo.InvariantCulture)}");
                    if (TryNumber(schema["minimum"], out double lo) && d < lo) errors.Add($"{Where(path)}: {d.ToString(CultureInfo.InvariantCulture)} is below the minimum {lo.ToString(CultureInfo.InvariantCulture)}");
                    if (TryNumber(schema["maximum"], out double hi) && d > hi) errors.Add($"{Where(path)}: {d.ToString(CultureInfo.InvariantCulture)} is above the maximum {hi.ToString(CultureInfo.InvariantCulture)}");
                    return;
                case "boolean":
                    if (value is not JsonValue bv || !bv.TryGetValue(out bool _)) errors.Add($"{Where(path)}: expected true or false");
                    return;
                case "null":
                    if (value != null) errors.Add($"{Where(path)}: expected null");
                    return;
            }
        }

        public static bool TryNumber(JsonNode? value, out double d)
        {
            d = 0;
            if (value is not JsonValue v) return false;
            if (v.TryGetValue(out double x)) { d = x; return true; }
            if (v.TryGetValue(out long l)) { d = l; return true; }
            if (v.TryGetValue(out int i)) { d = i; return true; }
            if (v.TryGetValue(out decimal m)) { d = (double)m; return true; }
            return false;
        }

        static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
        static string Where(string path) => path.Length == 0 ? "arguments" : path;

        static string Expected(JsonObject schema)
        {
            if (schema["anyOf"] is JsonArray any)
                return "expected " + string.Join(" or ", any.Select(o => o?["type"]?.GetValue<string>() switch
                {
                    "string" => "text",
                    "number" => "a number",
                    "integer" => "a whole number",
                    "boolean" => "true/false",
                    "array" => "a list",
                    "object" => "an object",
                    _ => "a value",
                }));
            return "unexpected value";
        }
    }
}
