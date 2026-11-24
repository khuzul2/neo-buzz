// Agent server (docs/specs/browser.md BR1): machine_swap, the browser's hot-swap.

using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class SwapTools
    {
        /// <summary>
        /// machine_swap (BR1, docs/specs/browser.md): the browser's hot-swap. Any gear in a machine's place, the Machine View's "Replace" path: connections,
        /// position and name kept, patterns recreated by name and length, one undo step.
        /// </summary>
        public static void RegisterSwap(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "machine_swap",
                Title = "Hot-swap a machine",
                Description =
                    "Puts other gear in a machine's place, as dropping a browser item on a machine's box does: its connections (with their gains), position and name are kept, " +
                    "its patterns are made again on the new machine with the same names and lengths, empty (their notes and values belong to the old machine's columns and are not converted), the arrangement's tracks are made again but not their clips, " +
                    "one undo step. A generator can only be swapped for a generator and an effect for an effect. Same gear with another plug-in (Neo Plug) swaps the plug-in in place. " +
                    "Example: machine_swap(machine=\"Lead\", gear=\"Neo Poly\").",
                Input = Object(null,
                    Req("machine", String("The machine to replace")),
                    Req("gear", String("The gear to put there (gear_list, browser_search)")),
                    Opt("plugin", String("For Neo Plug and Neo Plug FX: the plug-in's name, \"Vendor/Name (VST3)\""))),
                Output = Object(null,
                    Req("machine", String("The machine's name (unchanged)")),
                    Req("from", String("The old gear")),
                    Req("to", String("The new gear")),
                    Req("patterns", Integer("Patterns on the new machine"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string gear = c.RequiredString("gear").Trim();
                    string plugin = c.String("plugin")?.Trim();
                    var r = await host.Engine("machine_swap", true, _ =>
                    {
                        host.AutoSnapshot("before swapping " + c.String("machine"));
                        var old = SongModel.Machine(core, c.RequiredString("machine"));
                        var dll = core.MachineDLLs.Values.FirstOrDefault(d => d.Name == gear)
                                  ?? core.MachineDLLs.Values.FirstOrDefault(d => string.Equals(d.Name, gear, System.StringComparison.OrdinalIgnoreCase));
                        if (dll == null) throw new ToolError($"No gear \"{gear}\". gear_list shows what's installed.");
                        if (old.DLL.Info.Type == BuzzGUI.Interfaces.MachineType.Master) throw new ToolError("The Master can't be swapped.");
                        if (dll.Info == null || dll.Info.Type != old.DLL.Info.Type)
                            throw new ToolError($"{old.Name} is {(old.DLL.Info.Type == BuzzGUI.Interfaces.MachineType.Generator ? "a generator" : "an effect")}: swap it for gear of the same kind (gear_list kind).");
                        if (dll.IsMissing || dll.IsCrashed) throw new ToolError($"{dll.Name} is {(dll.IsMissing ? "missing" : "crashed")}.");
                        if (!string.IsNullOrEmpty(plugin) && !MachineTools.PluginNames(core, dll).Contains(plugin))
                            throw new ToolError($"{dll.Name} has no plug-in \"{plugin}\". gear_list(search=...) lists plug-ins.");
                        string from = old.DLL.Name;
                        string name = old.Name;
                        var now = ReBuzz.Core.MachineEditing.HotSwap(core, old, dll.Name, string.IsNullOrEmpty(plugin) ? null : plugin) ?? throw new ToolError("The swap didn't leave a machine of that name; undo restores the song.");
                        return new JsonObject { ["machine"] = now.Name, ["from"] = from, ["to"] = dll.Name + (string.IsNullOrEmpty(plugin) ? "" : " " + plugin), ["patterns"] = now.Patterns.Count };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Swapped {r["machine"]} from {r["from"]} to {r["to"]}; {r["patterns"]} patterns.", r);
                },
            });
        }
    }
}
