// Agent server (docs/specs/neo-patch.md §4.6, NP3): the engine's side of the patch_* tools (ReBuzz.Agent/App/PatchTools.cs). It finds the Neo Patch machine
// and hands the call to the machine's IPatchAgent on the engine's queue: the machine edits through the model its inner canvas uses, so the call's edits are
// steps of the song's undo stack (one for the call: the queue groups them as "Agent: <tool>") and the open canvas redraws.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal sealed class PatchBackend : IPatchServices
    {
        readonly AgentHost host;

        public PatchBackend(AgentHost host) { this.host = host; }

        List<(MachineCore Machine, IPatchAgent Agent)> Patches() =>
            SongModel.Machines(host.Core).Select(m => (Machine: m, Agent: m.ManagedMachine as IPatchAgent)).Where(x => x.Agent != null).ToList();

        (MachineCore Machine, IPatchAgent Agent) Find(string machine)
        {
            var all = Patches();
            if (!string.IsNullOrWhiteSpace(machine))
            {
                var m = SongModel.Machine(host.Core, machine);
                if (m.ManagedMachine is IPatchAgent agent) return (m, agent);
                throw new ToolError($"{m.Name} is not a Neo Patch machine (it is {m.DLL?.Name}). " + (all.Count == 0
                    ? "Add one with machine_add(gear=\"Neo Patch\")."
                    : "The Neo Patch machines in this song: " + string.Join(", ", all.Select(x => x.Machine.Name)) + "."));
            }
            if (all.Count == 1) return all[0];
            throw new ToolError(all.Count == 0
                ? "There is no Neo Patch machine in the song. Add one with machine_add(gear=\"Neo Patch\")."
                : "The song has several Neo Patch machines (" + string.Join(", ", all.Select(x => x.Machine.Name)) + "): say which with machine.");
        }

        static JsonObject Call(IPatchAgent agent, string op, string args)
        {
            var reply = JsonNode.Parse(agent.Invoke(op, args)) as JsonObject ?? throw new ToolError("Neo Patch gave no answer.");
            if (reply["error"] is JsonNode error) throw new ToolError(error.ToString());
            return reply;
        }

        public Task<JsonObject> Run(string tool, bool changes, string machine, string op, JsonObject args, CancellationToken ct) =>
            host.Engine(tool, changes, _ =>
            {
                var (m, agent) = Find(machine);
                var reply = Call(agent, op, args.ToJsonString());
                reply["machine"] = m.Name;
                return reply;
            }, ct);

        public Task<JsonObject> SavePreset(string machine, string name, string comment, bool overwrite, CancellationToken ct) =>
            host.Engine("patch_save_preset", true, _ =>
            {
                var (m, agent) = Find(machine);
                if (!overwrite && StateTools.PresetNames(m.DLL).Contains(name))
                    throw new ToolError($"{m.DLL.Name} already has a preset \"{name}\". Pass overwrite=true to replace it, or choose another name.");
                Call(agent, "rename", new JsonObject { ["name"] = name }.ToJsonString());
                return StateTools.SavePreset(m, name, comment, overwrite);
            }, ct);
    }
}
