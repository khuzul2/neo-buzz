// Agent server (docs/specs/agent-knowledge.md, K0): rebuzz://reference/tools. A reference to every tool, generated when it is
// read from the registered ToolDefs (name, summary, inputs with their descriptions, which carry the units, and the
// annotations), grouped by area, so it can't drift from the code. The guides say which tool to use when; this lists
// what each takes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Guides
{
    public static class ToolReference
    {
        public const string Uri = "rebuzz://reference/tools";

        /// <summary>The areas, in reading order, each with the tools it holds (by exact name or prefix ending in "_" or "*").</summary>
        static readonly (string Area, string[] Names)[] Areas =
        {
            ("Session and help", new[] { "studio_status", "guide", "undo", "redo", "history_*", "song_*" }),
            ("Machines and wiring", new[] { "gear_list", "machine_find", "machine_list", "machine_add", "machine_remove", "machine_replace", "machine_set", "machine_describe", "machine_data_import", "connect", "disconnect", "connection_set" }),
            ("Parameters, presets and snapshots", new[] { "param_*", "preset_*", "snapshot_*", "modulate", "modulation_*" }),
            ("Patterns and notes", new[] { "pattern_*", "notes_*", "theory" }),
            ("Arrangement and automation", new[] { "arrangement_*", "automation_*" }),
            ("Session View and live play", new[] { "session_*", "transport", "midi_play", "meters_read", "machine_readings" }),
            ("Render and analysis", new[] { "render", "analyze", "analyze_mix", "compare", "plot", "target_build", "eq_fit" }),
            ("Waves", new[] { "wave_*" }),
            ("App control", new[] { "settings_*", "midi_bind", "midi_bindings_list", "midi_unbind", "midi_learn", "audio_*", "export_audio", "app_status", "view_show", "machine_open_editor", "select", "panel_set", "window_layout" }),
        };

        /// <summary>The area a tool belongs to ("Other" when none names it).</summary>
        public static string AreaOf(string tool)
        {
            foreach (var (area, names) in Areas)
                foreach (var n in names)
                    if (n.EndsWith("*", StringComparison.Ordinal) ? tool.StartsWith(n.Substring(0, n.Length - 1), StringComparison.Ordinal) : tool == n)
                        return area;
            return "Other";
        }

        public static ResourceDef Resource(ToolRegistry tools) => new()
        {
            Uri = Uri,
            Name = "tool-reference",
            Title = "Tool reference",
            Description = "Every tool by area: what it does, its inputs with units and whether it changes the song. Generated from the tool definitions.",
            Read = () => Build(tools.All),
        };

        public static string Build(IEnumerable<ToolDef> tools)
        {
            var list = tools.ToList();
            var groups = list.GroupBy(t => AreaOf(t.Name)).OrderBy(g => Array.FindIndex(Areas, a => a.Area == g.Key) is var i && i < 0 ? int.MaxValue : i).ToList();
            var sb = new StringBuilder();
            sb.Append("# Tool reference\n\n");
            sb.Append($"{list.Count} tools in {groups.Count} areas, generated from the tool definitions. Each line: name (R read-only, W changes the song, D destructive, I same call twice gives the same state), what it does, then its inputs as name: type, with * for required; units are in the input's own text (beats, dB, Hz, seconds). ");
            sb.Append("tools/list has the full schemas; guide(topic=\"workflow\") says which tool for what.\n");
            foreach (var g in groups)
            {
                sb.Append("\n## ").Append(g.Key).Append('\n');
                foreach (var t in g.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    var a = t.Annotations;
                    string flags = (a.ReadOnly ? "R" : "W") + (a.Destructive ? "D" : "") + (a.Idempotent ? "I" : "");
                    sb.Append("- **").Append(t.Name).Append("** (").Append(flags).Append(") ").Append(Summary(t.Description)).Append('\n');
                    if (t.Input["properties"] is JsonObject props)
                    {
                        var required = (t.Input["required"] as JsonArray)?.Select(r => (string)r!).ToHashSet() ?? new HashSet<string>();
                        foreach (var (name, schema) in props)
                            if (schema is JsonObject s)
                                sb.Append("  - ").Append(name).Append(required.Contains(name) ? "*" : "").Append(": ").Append(TypeOf(s)).Append(" — ").Append(Clip((string?)s["description"] ?? "", 100)).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>The first sentence of a description (up to 170 characters).</summary>
        internal static string Summary(string description)
        {
            int end = -1;
            for (int i = 0; i + 1 < description.Length; i++)
                if (description[i] == '.' && description[i + 1] == ' ' && !(i > 0 && char.IsDigit(description[i - 1])) && !description.AsSpan(Math.Max(0, i - 3), Math.Min(4, i + 1)).EndsWith("e.g."))
                {
                    end = i + 1;
                    break;
                }
            return Clip(end < 0 ? description : description.Substring(0, end), 170);
        }

        static string Clip(string text, int max)
        {
            text = text.Replace('\n', ' ').Trim();
            if (text.Length <= max) return text;
            int cut = text.LastIndexOf(' ', max);
            return text.Substring(0, cut > max / 2 ? cut : max).TrimEnd(',', ';', ':', ' ') + "…";
        }

        static string TypeOf(JsonObject s)
        {
            if (s["anyOf"] is JsonArray any) return string.Join(" | ", any.OfType<JsonObject>().Select(TypeOf).Distinct());
            string type = (string?)s["type"] ?? "any";
            if (s["enum"] is JsonArray e && e.Count <= 8) return string.Join("|", e.Select(x => x?.ToString()));
            if (type == "array" && s["items"] is JsonObject items)
            {
                if (items["properties"] is JsonObject p)
                {
                    var req = (items["required"] as JsonArray)?.Select(r => (string)r!).ToHashSet() ?? new HashSet<string>();
                    return "array of {" + string.Join(", ", p.Select(kv => kv.Key + (req.Contains(kv.Key) ? "*" : ""))) + "}";
                }
                return "array of " + TypeOf(items);
            }
            if (type == "number" || type == "integer")
            {
                string range = s["minimum"] != null || s["maximum"] != null ? $" {s["minimum"]?.ToString() ?? ""}…{s["maximum"]?.ToString() ?? ""}" : "";
                return type + range;
            }
            return type;
        }
    }
}
