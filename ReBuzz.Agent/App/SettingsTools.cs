// Agent server (docs/specs/agent-knowledge.md §5, K3): every preference as a tool. settings_get lists them
// (General, Audio, MIDI, Appearance, Agent, Safety and the engine's advanced flags), settings_set writes one,
// settings_reset puts one, a section or all back to their defaults, settings_history reads the audit log: every
// change an agent made, with the value it replaced, so any of them can be put back. Engine flags and the safety
// settings are what the destructive annotation is for: clients ask the person before such a call.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class SettingsTools
    {
        public static readonly string[] Sections = { "general", "audio", "midi", "appearance", "agent", "safety", "advanced" };

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "settings_get",
                Title = "Read settings",
                Description =
                    "Reads the app's preferences: general, audio, midi, appearance, agent, safety (the safety limiter, the machine guard) and advanced (the engine's flags). " +
                    "Each has its key (\"section.Name\"), value, default, type, choices or range, what it does and whether it needs a restart. " +
                    "class engine and class safety are the ones to be careful with. Pass key for one, section, search or changed_only to narrow. Example: settings_get(section=\"audio\").",
                Input = Object(null,
                    Opt("key", String("One setting: \"section.Name\" (the bare Name works when it is unique)")),
                    Opt("section", String("Only this section", Sections)),
                    Opt("search", String("Words in the name or the description")),
                    Opt("changed_only", Boolean("Only settings that differ from their default")),
                    Opt("limit", Integer("At most this many (default 200)", 1, 500))),
                Output = Object(null,
                    Req("count", Integer("How many are listed")),
                    Req("total", Integer("How many settings there are")),
                    Req("settings", Array(SettingSchema(), "The settings"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await app.Engine("settings_get", false, () =>
                    {
                        var all = app.Settings();
                        IEnumerable<SettingEntry> list = all;
                        if (c.String("key") is string key) list = new[] { Find(all, key) };
                        if (c.String("section") is string section) list = list.Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
                        if (c.String("search") is string search && search.Length > 0)
                            list = list.Where(e => search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
                                e.Key.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Description.Contains(w, StringComparison.OrdinalIgnoreCase)));
                        var rows = list.Select(e => (Entry: e, Value: SafeGet(e))).ToList();
                        if (c.Bool("changed_only") == true) rows = rows.Where(x => !Same(x.Entry, x.Value, x.Entry.Default)).ToList();
                        int limit = c.Int("limit") ?? 200;
                        return (Rows: rows.Take(limit).ToList(), Count: rows.Count, Total: all.Count);
                    }, c.Cancellation);
                    var arr = new JsonArray(r.Rows.Select(x => (JsonNode)Row(x.Entry, x.Value)).ToArray());
                    string text = r.Rows.Count == 1
                        ? $"{r.Rows[0].Entry.Key} = {r.Rows[0].Value} (default {r.Rows[0].Entry.Default})." + Flags(r.Rows[0].Entry)
                        : $"{r.Rows.Count} of {r.Total} settings" + (r.Rows.Count < r.Count ? $" (limit; {r.Count} match)" : "") + ": " +
                          string.Join(", ", r.Rows.Take(12).Select(x => $"{x.Entry.Key}={x.Value}")) + (r.Rows.Count > 12 ? ", …" : "") + ".";
                    return ToolResult.Ok(text, new JsonObject { ["count"] = r.Rows.Count, ["total"] = r.Total, ["settings"] = arr });
                },
            });

            tools.Add(new ToolDef
            {
                Name = "settings_set",
                Title = "Change a setting",
                Description =
                    "Writes one preference and saves it. The value is checked against the setting's type, choices and range. Every change goes to the audit log (settings_history) with the value it replaced, " +
                    "so settings_set can put it back. Annotated destructive because it reaches the engine's flags and the safety settings: a client asks the person first. " +
                    "The result says when the setting needs a restart. agent.AllowAgents and agent.Port end this connection when they change; the audio device, buffer and rate " +
                    "are set with audio_set (it reverts a choice that gives no sound). Example: settings_set(key=\"general.PatternLength\", value=32).",
                Input = Object(null,
                    Req("key", String("\"section.Name\" as settings_get lists it")),
                    Req("value", AnyOf("The new value: true/false, a number, or one of the choices", Boolean("A switch"), Number("A number"), String("A choice or text")))),
                Output = Object(null,
                    Req("key", String("The setting")),
                    Req("value", AnyOf("The value now", Boolean("A switch"), Number("A number"), String("A choice or text"))),
                    Req("previous", String("The value it had")),
                    Req("changed", Boolean("False when it already had this value")),
                    Req("restart_required", Boolean("The change takes effect after a restart")),
                    Req("class", String("normal, engine or safety")),
                    Opt("note", String("What to know"))),
                Annotations = new ToolAnnotations(Destructive: true, Idempotent: true),
                Handler = async c =>
                {
                    var o = await app.Engine("settings_set", false, () => Set(app, c.RequiredString("key"), c.Arguments["value"]), c.Cancellation);
                    return ToolResult.Ok(ChangeLine(o), o);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "settings_reset",
                Title = "Reset settings",
                Description =
                    "Puts one setting back to its default (key), a whole section (section), or every setting (all=true). Each reset goes to the audit log with the value it replaced. " +
                    "Resetting a section or all leaves agent.AllowAgents and agent.Port alone (they would end this connection): name them with key. Annotated destructive. " +
                    "Example: settings_reset(section=\"advanced\").",
                Input = Object(null,
                    Opt("key", String("One setting")),
                    Opt("section", String("Every setting of a section", Sections)),
                    Opt("all", Boolean("Every setting (give exactly one of key, section, all)"))),
                Output = Object(null,
                    Req("reset", Array(Object(null, Req("key", String("The setting")), Req("from", String("The value it had")), Req("to", String("Its default"))), "The settings that changed")),
                    Req("unchanged", Integer("Settings already at their default")),
                    Req("restart_required", Array(String("A setting"), "Settings that need a restart to take effect")),
                    Opt("skipped", Array(String("A setting"), "Left alone: name them with key"))),
                Annotations = new ToolAnnotations(Destructive: true, Idempotent: true),
                Handler = async c =>
                {
                    int given = (c.String("key") != null ? 1 : 0) + (c.String("section") != null ? 1 : 0) + (c.Bool("all") == true ? 1 : 0);
                    if (given != 1) throw new ToolError("Give exactly one of key, section or all=true.");
                    var o = await app.Engine("settings_reset", false, () => Reset(app, c.String("key"), c.String("section")), c.Cancellation);
                    int n = o["reset"]!.AsArray().Count;
                    return ToolResult.Ok($"{n} setting{(n == 1 ? "" : "s")} reset to default, {o["unchanged"]} already were." +
                                         (o["restart_required"]!.AsArray().Count > 0 ? " Restart needed for: " + string.Join(", ", o["restart_required"]!.AsArray().Select(x => x!.GetValue<string>())) + "." : "") +
                                         (o["skipped"] is JsonArray s && s.Count > 0 ? " Left alone: " + string.Join(", ", s.Select(x => x!.GetValue<string>())) + "." : ""), o);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "settings_history",
                Title = "Settings audit log",
                Description =
                    "Reads the audit log of settings changes agents made (newest first): when, which key, the value it had and the value it got. " +
                    "Use it to see what changed, and put a value back with settings_set(key, value=from). Example: settings_history(limit=10).",
                Input = Object(null,
                    Opt("key", String("Only this setting")),
                    Opt("limit", Integer("At most this many entries (default 50)", 1, 1000))),
                Output = Object(null,
                    Req("count", Integer("Entries listed")),
                    Req("entries", Array(Object(null,
                        Req("time", String("When (ISO 8601)")), Req("key", String("The setting")), Req("from", String("The value it replaced")),
                        Req("to", String("The value it got")), Req("restart_required", Boolean("It needs a restart")), Req("class", String("normal, engine or safety"))), "Newest first"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    var entries = ReadHistory(app.SettingsHistoryFile, c.String("key"), c.Int("limit") ?? 50);
                    string text = entries.Count == 0 ? "No settings changes logged." :
                        $"{entries.Count} change{(entries.Count == 1 ? "" : "s")}: " + string.Join("; ", entries.Take(8).Select(e => $"{e["key"]} {e["from"]} → {e["to"]}")) + (entries.Count > 8 ? "; …" : "") + ".";
                    return Task.FromResult(ToolResult.Ok(text, new JsonObject { ["count"] = entries.Count, ["entries"] = new JsonArray(entries.Select(e => (JsonNode)e).ToArray()) }));
                },
            });
        }

        // ------------------------------------------------------------------ schema and rows

        static JsonObject SettingSchema() => Object("A setting",
            Req("key", String("\"section.Name\"")),
            Req("section", String("Its section", Sections)),
            Req("value", String("Its value now, as text")),
            Req("default", String("Its default")),
            Req("changed", Boolean("Differs from the default")),
            Req("type", String("Kind of value", "bool", "int", "number", "choice", "text")),
            Opt("choices", Array(String("A choice"), "The values it takes")),
            Opt("min", Number("Lowest")),
            Opt("max", Number("Highest")),
            Req("description", String("What it does")),
            Req("restart_required", Boolean("Takes effect after a restart")),
            Req("class", String("normal, engine (an engine flag) or safety")),
            Opt("note", String("What to know")));

        static string SafeGet(SettingEntry e)
        {
            try { return e.Get(); }
            catch (Exception) { return ""; }
        }

        static string TypeName(SettingKind k) => k switch { SettingKind.Bool => "bool", SettingKind.Int => "int", SettingKind.Number => "number", SettingKind.Choice => "choice", _ => "text" };

        internal static JsonNode Typed(SettingEntry e, string value)
        {
            switch (e.Kind)
            {
                case SettingKind.Bool: return JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase))!;
                case SettingKind.Int when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l): return JsonValue.Create(l)!;
                case SettingKind.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d): return JsonValue.Create(d)!;
                default: return JsonValue.Create(value)!;
            }
        }

        static string? Note(SettingEntry e)
        {
            var notes = new List<string>();
            if (e.UseInstead != null) notes.Add(e.UseInstead);
            if (e.EndsAgentConnection) notes.Add("Changing it ends this agent connection.");
            if (e.Class == SettingClass.Engine) notes.Add("An engine flag: it changes how audio is made; songs may sound different.");
            if (e.Class == SettingClass.Safety) notes.Add("A safety setting: it guards the speakers and the machines.");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        static string Flags(SettingEntry e) =>
            (e.RestartRequired ? " Needs a restart." : "") + (Note(e) is string n ? " " + n : "");

        static JsonObject Row(SettingEntry e, string value)
        {
            var o = new JsonObject
            {
                ["key"] = e.Key,
                ["section"] = e.Section,
                ["value"] = value,
                ["default"] = e.Default,
                ["changed"] = !Same(e, value, e.Default),
                ["type"] = TypeName(e.Kind),
                ["description"] = e.Description,
                ["restart_required"] = e.RestartRequired,
                ["class"] = e.Class,
            };
            if (e.Options is { Length: > 0 } opts && e.Kind != SettingKind.Int) o["choices"] = new JsonArray(opts.Select(x => (JsonNode)x).ToArray());
            if (e.Min != null) o["min"] = e.Min;
            if (e.Max != null) o["max"] = e.Max;
            if (Note(e) is string n) o["note"] = n;
            return o;
        }

        static bool Same(SettingEntry e, string a, string b) =>
            e.Kind == SettingKind.Number && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ find, normalise

        internal static SettingEntry Find(IReadOnlyList<SettingEntry> all, string key)
        {
            var exact = all.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var byName = all.Where(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1) throw new ToolError($"\"{key}\" is in several sections: {string.Join(", ", byName.Select(e => e.Key))}. Give the full key.");
            var near = Text.Closest(key, all.Select(e => e.Key));
            throw new ToolError($"No setting \"{key}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") + " settings_get lists them.");
        }

        /// <summary>The value as the setting stores it, or a ToolError saying what it takes.</summary>
        internal static string Normalize(SettingEntry e, JsonNode? value)
        {
            if (value is not JsonValue v) throw new ToolError($"{e.Key}: give a value.");
            string? text = v.TryGetValue(out string? s) ? s : null;
            switch (e.Kind)
            {
                case SettingKind.Bool:
                {
                    if (v.TryGetValue(out bool b)) return b ? "True" : "False";
                    return text?.ToLowerInvariant() switch
                    {
                        "true" or "on" or "yes" => "True",
                        "false" or "off" or "no" => "False",
                        _ => throw new ToolError($"{e.Key} is a switch: true or false."),
                    };
                }
                case SettingKind.Int:
                case SettingKind.Number:
                {
                    double d;
                    if (!TryNumber(value, out d) && !(text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)))
                        throw new ToolError($"{e.Key} takes a number" + Range(e) + ".");
                    if (e.Kind == SettingKind.Int && d != Math.Round(d)) throw new ToolError($"{e.Key} takes a whole number" + Range(e) + ".");
                    if ((e.Min != null && d < e.Min) || (e.Max != null && d > e.Max)) throw new ToolError($"{e.Key} takes {Range(e).TrimStart(' ', '(').TrimEnd(')')}; {d.ToString(CultureInfo.InvariantCulture)} is outside.");
                    return e.Kind == SettingKind.Int ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
                }
                case SettingKind.Choice:
                {
                    string given = text ?? (TryNumber(value, out double n) ? n.ToString(CultureInfo.InvariantCulture) : "");
                    var hit = e.Options?.FirstOrDefault(o => o.Equals(given, StringComparison.OrdinalIgnoreCase))
                              ?? e.Options?.FirstOrDefault(o => o.Replace("_", "").Replace(" ", "").Equals(given.Replace("_", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    if (hit == null) throw new ToolError($"{e.Key} takes one of: {string.Join(", ", e.Options ?? System.Array.Empty<string>())}.");
                    return hit;
                }
                default:
                    return text ?? throw new ToolError($"{e.Key} takes text.");
            }
        }

        static string Range(SettingEntry e) =>
            e.Min != null && e.Max != null ? $" ({e.Min} to {e.Max})" : e.Min != null ? $" (at least {e.Min})" : e.Max != null ? $" (at most {e.Max})" : "";

        // ------------------------------------------------------------------ set, reset

        static JsonObject Set(IAppServices app, string key, JsonNode? value)
        {
            var e = Find(app.Settings(), key);
            if (e.UseInstead != null) throw new ToolError($"{e.Key} is changed with another tool: {e.UseInstead}");
            string text = Normalize(e, value);
            string before = SafeGet(e);
            if (Same(e, before, text))
                return Result(e, before, before, false);
            try { e.Set(text); }
            catch (ToolError) { throw; }
            catch (Exception ex) { throw new ToolError($"{e.Key} couldn't be set to {text}: {ex.Message}"); }
            string after = SafeGet(e);
            Log(app, e, before, after);
            return Result(e, before, after, true);
        }

        static JsonObject Result(SettingEntry e, string before, string after, bool changed)
        {
            var o = new JsonObject
            {
                ["key"] = e.Key,
                ["value"] = Typed(e, after),
                ["previous"] = before,
                ["changed"] = changed,
                ["restart_required"] = e.RestartRequired && changed,
                ["class"] = e.Class,
            };
            string? note = Note(e);
            if (e.RestartRequired && changed) note = "Takes effect after a restart." + (note != null ? " " + note : "");
            if (note != null) o["note"] = note;
            return o;
        }

        static string ChangeLine(JsonObject o) =>
            (o["changed"]!.GetValue<bool>() ? $"{o["key"]}: {o["previous"]} → {o["value"]}." : $"{o["key"]} is already {o["value"]}.") +
            (o["restart_required"]!.GetValue<bool>() ? " Takes effect after a restart." : "") +
            (o["note"] is JsonNode n && !o["restart_required"]!.GetValue<bool>() ? " " + n : "");

        static JsonObject Reset(IAppServices app, string? key, string? section)
        {
            var all = app.Settings();
            List<SettingEntry> list;
            var skipped = new List<string>();
            if (key != null) list = new List<SettingEntry> { Find(all, key) };
            else
            {
                list = (section != null ? all.Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) : all).ToList();
                if (section != null && list.Count == 0) throw new ToolError($"No section \"{section}\": {string.Join(", ", Sections)}.");
                skipped.AddRange(list.Where(e => e.EndsAgentConnection || e.UseInstead != null).Select(e => e.Key));
                list = list.Where(e => e.EndsAgentConnection == false && e.UseInstead == null).ToList();
            }
            var reset = new JsonArray();
            var restart = new JsonArray();
            int unchanged = 0;
            foreach (var e in list)
            {
                if (e.UseInstead != null) throw new ToolError($"{e.Key} is changed with another tool: {e.UseInstead}");
                string before = SafeGet(e);
                if (Same(e, before, e.Default)) { unchanged++; continue; }
                try { e.Set(e.Default); }
                catch (Exception ex) { throw new ToolError($"{e.Key} couldn't be reset: {ex.Message}"); }
                string after = SafeGet(e);
                Log(app, e, before, after);
                reset.Add(new JsonObject { ["key"] = e.Key, ["from"] = before, ["to"] = after });
                if (e.RestartRequired) restart.Add(e.Key);
            }
            var o = new JsonObject { ["reset"] = reset, ["unchanged"] = unchanged, ["restart_required"] = restart };
            if (skipped.Count > 0) o["skipped"] = new JsonArray(skipped.Select(x => (JsonNode)x).ToArray());
            return o;
        }

        // ------------------------------------------------------------------ audit log

        static readonly object gate = new();

        static void Log(IAppServices app, SettingEntry e, string from, string to)
        {
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ["key"] = e.Key,
                ["from"] = from,
                ["to"] = to,
                ["restart_required"] = e.RestartRequired,
                ["class"] = e.Class,
            };
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(app.SettingsHistoryFile)!);
                    File.AppendAllText(app.SettingsHistoryFile, JsonRpc.Serialize(entry) + "\n");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static List<JsonObject> ReadHistory(string file, string? key, int limit)
        {
            var list = new List<JsonObject>();
            string[] lines;
            lock (gate)
            {
                if (!File.Exists(file)) return list;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { return list; }
            }
            for (int i = lines.Length - 1; i >= 0 && list.Count < limit; i--)
            {
                try
                {
                    if (JsonNode.Parse(lines[i]) is not JsonObject o) continue;
                    if (key != null && !string.Equals(o["key"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(o);
                }
                catch (System.Text.Json.JsonException) { }
            }
            return list;
        }
    }
}
