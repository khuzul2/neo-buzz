// Fork (docs/specs/browser.md, BR1): the browser as tools. browser_search is the panel's one search (machines, plug-ins, presets, the song's
// patterns, and the sample and MIDI files of the user's places); browser_places lists, adds and removes the folders it indexes. Studio mode
// only; elsewhere they say so.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class BrowserTools
    {
        static readonly string[] Kinds = { "machine", "plugin", "preset", "audio", "midi", "pattern" };

        static IBrowserControl Browser(IAppServices app)
        {
            if (app.Browser != null) return app.Browser;
            throw new ToolError($"Studio only: {app.Facts().Mode} mode has no browser. gear_list, machine_find and wave_list work everywhere.");
        }

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "browser_search",
                Title = "Search the browser",
                Description =
                    "The studio browser's one search over everything the person can drag into a song: machines and plug-ins (the installed gear, plug-ins from Neo Plug's scan by vendor), " +
                    "factory presets, the song's patterns, and the sample and MIDI files in the folders added with browser_places (name, folder, length, rate, tempo, key). " +
                    "kind narrows it to machine, plugin, preset, audio, midi or pattern. Every word of the query must match. Results carry what machine_add, machine_swap and wave_import take: " +
                    "a gear name, a preset's gear and name, a file's path. Studio mode only. Example: browser_search(query=\"kick 808\", kind=\"audio\").",
                Input = Object(null,
                    Req("query", String("Words to find (all of them)")),
                    Opt("kind", String("Only this kind", Kinds)),
                    Opt("limit", Integer("At most this many results (default 25)", 1, 200))),
                Output = Object(null,
                    Req("results", Array(Object("A result", Req("kind", String("machine, plugin, preset, audio, midi or pattern")), Req("name", String("What it is called")), Req("id", String("Gear name, \"Machine/Pattern\" or file path")),
                        Opt("group", String("Its vendor, category, machine or folder")), Opt("detail", String("Tags, length and rate, rows")), Opt("path", String("The file, for audio and midi")), Opt("preset", String("A preset's name; id is its gear"))), "Best first")),
                    Req("count", Integer("How many are returned"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var b = Browser(app);
                    string query = c.RequiredString("query");
                    string? kind = c.String("kind")?.ToLowerInvariant();
                    if (kind != null && !Kinds.Contains(kind)) throw new ToolError($"kind: {string.Join(", ", Kinds)}.");
                    int limit = c.Int("limit") ?? 25;
                    var hits = await app.Engine("browser_search", false, () => b.Search(query, kind, Math.Clamp(limit, 1, 200)), c.Cancellation);
                    var arr = new JsonArray();
                    foreach (var h in hits)
                    {
                        var o = new JsonObject { ["kind"] = h.Kind, ["name"] = h.Name, ["id"] = h.Id };
                        if (h.Group != null) o["group"] = h.Group;
                        if (!string.IsNullOrEmpty(h.Detail)) o["detail"] = h.Detail;
                        if (h.Path != null) o["path"] = h.Path;
                        if (h.Preset != null) o["preset"] = h.Preset;
                        arr.Add(o);
                    }
                    string text = hits.Count == 0 ? $"Nothing in the browser matches \"{query}\"." : $"{hits.Count} for \"{query}\": " + string.Join("; ", hits.Take(8).Select(h => $"{h.Kind} {h.Name}")) + (hits.Count > 8 ? "…" : "") + ".";
                    return ToolResult.Ok(text, new JsonObject { ["results"] = arr, ["count"] = hits.Count });
                },
            });

            tools.Add(new ToolDef
            {
                Name = "browser_places",
                Title = "Browser places",
                Description =
                    "The folders the studio browser indexes (Places). op=list shows them with how many files each holds and whether the scan is running; op=add puts a folder in (it is scanned in the background, " +
                    "then watched for changes, so browser_search finds its audio and MIDI files); op=remove takes one out (the files stay where they are). Studio mode only. " +
                    "Example: browser_places(op=\"add\", path=\"/Users/me/Samples\").",
                Input = Object(null,
                    Req("op", String("list, add or remove", "list", "add", "remove")),
                    Opt("path", String("The folder (add, remove)")),
                    Opt("wait_seconds", Number("With add: wait this long for the first scan to finish (default 0)", 0, 120))),
                Output = Object(null,
                    Req("places", Array(Object("A place", Req("path", String("The folder")), Req("files", Integer("Files indexed under it")), Req("exists", Boolean("It is still there"))), "The places")),
                    Req("index", String("Indexing progress, or idle")),
                    Req("indexed", Integer("Files in the index"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var b = Browser(app);
                    string op = c.RequiredString("op").ToLowerInvariant();
                    string? path = c.String("path");
                    if (op is "add" or "remove" && string.IsNullOrWhiteSpace(path)) throw new ToolError($"op={op} needs path.");
                    double wait = c.Number("wait_seconds") ?? 0;
                    string said = "";
                    var result = await app.Engine("browser_places", false, () =>
                    {
                        switch (op)
                        {
                            case "list": break;
                            case "add":
                                if (!b.AddPlace(path!)) throw new ToolError($"{path} isn't a folder that exists, or is a place already. browser_places(op=\"list\") shows them.");
                                said = $"Added {path}. ";
                                break;
                            case "remove":
                                if (!b.RemovePlace(path!)) throw new ToolError($"{path} isn't one of the places. browser_places(op=\"list\") shows them.");
                                said = $"Removed {path}. ";
                                break;
                            default: throw new ToolError("op: list, add or remove.");
                        }
                        return 0;
                    }, c.Cancellation);
                    if (op == "add" && wait > 0) b.WaitForIndex((int)(wait * 1000));
                    var places = b.Places();
                    var (status, indexed) = b.Index();
                    var arr = new JsonArray();
                    foreach (var p in places) arr.Add(new JsonObject { ["path"] = p.Path, ["files"] = p.Files, ["exists"] = p.Exists });
                    return ToolResult.Ok(said + $"{places.Count} place{(places.Count == 1 ? "" : "s")}, {indexed:N0} files indexed" + (status == "idle" ? "." : $" ({status})."),
                        new JsonObject { ["places"] = arr, ["index"] = status, ["indexed"] = indexed });
                },
            });
        }
    }
}
