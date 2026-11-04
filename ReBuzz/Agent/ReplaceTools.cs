// Agent server (docs/specs/mcp-server.md §5.3, §12.5, U5): machine_replace, a classic machine onto
// its Neo successor (MachineReplace). One undo step; a dry run changes nothing.

using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class ReplaceTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "machine_replace",
                Title = "Replace a machine with its successor",
                Description =
                    "Moves a classic machine onto its Neo successor (gear_list and machine_describe name a machine's successors): the successor takes its " +
                    "place (connections, position, name), every parameter converted through real units (a level in percent to dB, a table to Hz), its " +
                    "patterns' columns and notes moved (ramps kept; tracker notes as notes, slides as glides), the arrangement and the Session View following. " +
                    "Then the song (first 60 s, or from/to) is rendered before and after and compared against the pair's stated tolerance (loudness, third " +
                    "octaves): the result says whether it's within, and why not. The old machine is removed (keep_old=true keeps it, muted, for A/B). " +
                    "dry_run=true reports the mapping and what's expected without changing anything. One undo step: undo brings the original back exactly. " +
                    "Example: machine_replace(machine=\"Reverb\", with=\"Neo Verb\").",
                Input = Object(null,
                    Req("machine", String("The machine to replace")),
                    Opt("with", String("The successor's gear (default: its only one)")),
                    Opt("name", String("The successor's name (default: the old machine's)")),
                    Opt("keep_old", Boolean("Keep the old machine in the song, muted (default false: removed)")),
                    Opt("dry_run", Boolean("Report the mapping and the expected differences; change nothing")),
                    Opt("compare", Boolean("Render before and after and compare (default true)")),
                    Opt("from", AnyOf("Compare from here", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("to", AnyOf("Compare to here", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("section", String("Compare this marker's section"))),
                Output = Object(null,
                    Req("from", String("The old gear")),
                    Req("to", String("The successor")),
                    Req("mapping", Array(Object("A value the successor gets", Req("param", String("Its parameter")), Req("value", String("Its value")), Req("from", String("Where it came from"))), "The successor's settings")),
                    Opt("comparison", Object("Before against after", Opt("within_tolerance", Boolean("Within the pair's tolerance (null: nothing to compare)")))),
                    Opt("notes", Array(String("A note"), "What couldn't be reached or has no counterpart"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    bool dry = c.Bool("dry_run") == true;
                    var r = await host.Engine("machine_replace", !dry, _ =>
                    {
                        var old = SongModel.Machine(core, c.RequiredString("machine"));
                        int? from = null, to = null;
                        if (c.String("section") is string section) (from, to) = CompositionTools.Section(core, section);
                        if (c.Arguments["from"] != null) from = SongModel.ParseTicks(core, c.Arguments["from"], "from");
                        if (c.Arguments["to"] != null) to = SongModel.ParseTicks(core, c.Arguments["to"], "to");
                        if (from is int f && to is int t && t <= f) throw new ToolError("to must be after from.");
                        var options = new ReplaceOptions
                        {
                            Name = c.String("name"),
                            KeepOld = c.Bool("keep_old") == true,
                            DryRun = dry,
                            Compare = c.Bool("compare") ?? true,
                            From = from,
                            To = to,
                        };
                        return MachineReplace.Run(core, host.Dispatcher, old, c.String("with"), options, c.Cancellation, (d, all, what) => c.Progress(d, all, what));
                    }, c.Cancellation);
                    return ToolResult.Ok(Summary(r), r);
                },
            });
        }

        /// <summary>One line for people (the tool's summary, the machine menu's status).</summary>
        internal static string Summary(JsonObject r)
        {
            string head = r["dry_run"]?.GetValue<bool>() == true
                ? $"Dry run: {r["machine"]} ({r["from"]}) would become {r["would_be_named"]} ({r["to"]}), {r["mapping"]!.AsArray().Count} values set"
                : $"Replaced {r["machine"]} ({r["from"]}) with {r["replaced_by"]?["name"]} ({r["to"]}), {r["mapping"]!.AsArray().Count} values set" +
                  (r["old"]?.GetValue<string>() is string old && old != "removed" ? $"; old {old}" : "");
            string notes = r["notes"] is JsonArray n && n.Count > 0 ? $" Notes: {string.Join(" ", n.Take(4).Select(x => x!.GetValue<string>()))}{(n.Count > 4 ? " …" : "")}" : "";
            string verdict = "";
            if (r["comparison"] is JsonObject cmp)
            {
                var v = cmp["machine"] ?? cmp["song"];
                var reads = v?["reads"]?.AsArray().Select(x => x!.GetValue<string>()) ?? Enumerable.Empty<string>();
                string within = cmp["within_tolerance"] is JsonValue w ? (w.GetValue<bool>() ? "within its tolerance" : "outside its tolerance") : "not compared";
                verdict = $" Compared ({(cmp["machine"] != null ? "its own output" : "the song")}): {within}. {string.Join(" ", reads)}" +
                          (cmp["why_not"] is JsonNode why ? $" Why not: {why.GetValue<string>()}" : "");
            }
            else if (r["expected"] is JsonObject e)
                verdict = $" Expected: loudness {e["loudness"]}, bands {e["bands"]}." + (e["why_not"] is JsonNode why ? $" {why.GetValue<string>()}" : "");
            return head + "." + verdict + notes;
        }
    }
}
