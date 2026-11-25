// Agent server (docs/specs/note-wires.md §7, NW1): note wires. note_wires lists the machines' note ports, the free wires and the insert
// chains; note_wire_connect and note_wire_disconnect edit a free wire; note_chain edits a track's insert chain (set it whole, add one, or
// take one out). They call the store the Machines view's drags and menus call, so the checks and the undo step are the same: one undo
// step each, and a refusal (a loop, a machine that doesn't take or send notes, an unknown name) comes back as the error.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.NoteWires;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class NoteWireTools
    {
        static JsonObject Listing(ReBuzzCore core)
        {
            var store = core.NoteWires;
            var machines = new JsonArray();
            foreach (var m in core.SongCore.MachinesList)
            {
                var ports = NoteWireStore.PortsOf(m);
                if (ports is not { Control: false } p || !(p.In || p.Out)) continue;
                machines.Add(new JsonObject { ["machine"] = m.Name, ["note_in"] = p.In, ["note_out"] = p.Out });
            }
            var exists = core.SongCore.MachinesList.Select(m => m.Name).ToHashSet();
            var wires = new JsonArray();
            foreach (var w in store.State.Wires.Where(w => exists.Contains(w.From) && exists.Contains(w.To)))
                wires.Add(new JsonObject { ["source"] = w.From, ["target"] = w.To });
            var chains = new JsonArray();
            foreach (var c in store.State.Chains.Where(c => exists.Contains(c.Instrument)))
                chains.Add(new JsonObject { ["instrument"] = c.Instrument, ["inserts"] = new JsonArray(c.Inserts.Select(i => (JsonNode)i).ToArray()) });
            return new JsonObject { ["machines"] = machines, ["wires"] = wires, ["chains"] = chains };
        }

        static ToolResult Result(ReBuzzCore core, string text)
        {
            var o = Listing(core);
            return ToolResult.Ok(text, o);
        }

        static JsonObject Output() => Object(null,
            Req("machines", Array(Object(null, Req("machine", String("The machine")), Req("note_in", Boolean("It takes notes")), Req("note_out", Boolean("It sends notes"))), "Machines with note ports")),
            Req("wires", Array(Object(null, Req("source", String("The sender")), Req("target", String("The receiver"))), "Free note wires")),
            Req("chains", Array(Object(null, Req("instrument", String("The instrument")), Req("inserts", Array(String("An insert"), "In order, first to last"))), "Insert chains")));

        static string Name(ReBuzzCore core, ToolContext c, string arg) => SongModel.Machine(core, c.RequiredString(arg)).Name;

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "note_wires",
                Title = "Note wires",
                Description =
                    "The note wires of the song: which machines take notes (note_in) or send them (note_out), the free wires (source to target) and each instrument's insert chain " +
                    "(the note machines before it, first to last: the track's clip notes, live MIDI and keys enter the first insert). A note wire carries notes in the same block, " +
                    "at the offsets they were made at, and its source runs before its target. Example: note_wires().",
                Input = Object(null),
                Output = Output(),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("note_wires", false, _ => Listing(core), c.Cancellation);
                    return ToolResult.Ok($"{r["machines"]!.AsArray().Count} machines with note ports, {r["wires"]!.AsArray().Count} wires, {r["chains"]!.AsArray().Count} chains.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "note_wire_connect",
                Title = "Connect a note wire",
                Description =
                    "Sends the notes of one machine to another (a free note wire, one undo step). The source must send notes (Neo Voice to MIDI, Neo Audio to MIDI, a Neo Plug that makes MIDI, a note machine) and the target " +
                    "take them (an instrument or a note machine). Refused, with the reason, when it would make a loop together with the audio wires and the other note wires. " +
                    "Example: note_wire_connect(source=\"Voice\", target=\"Lead\").",
                Input = Object(null, Req("source", String("The machine that sends notes")), Req("target", String("The machine that takes them"))),
                Output = Output(),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("note_wire_connect", true, _ =>
                    {
                        string from = Name(core, c, "source"), to = Name(core, c, "target");
                        if (core.NoteWires.State.Wires.Contains(new NoteEdge(from, to))) throw new ToolError($"{from} already sends notes to {to}.");
                        if (core.NoteWires.Connect(from, to) is { } why) throw new ToolError(why);
                        return Listing(core);
                    }, c.Cancellation);
                    return ToolResult.Ok($"{c.String("source")} now sends notes to {c.String("target")}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "note_wire_disconnect",
                Title = "Remove a note wire",
                Description = "Removes a free note wire (one undo step). A chain wire is removed with note_chain. Example: note_wire_disconnect(source=\"Voice\", target=\"Lead\").",
                Input = Object(null, Req("source", String("The sender")), Req("target", String("The receiver"))),
                Output = Output(),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("note_wire_disconnect", true, _ =>
                    {
                        string from = c.RequiredString("source"), to = c.RequiredString("target");
                        if (!core.NoteWires.Disconnect(from, to)) throw new ToolError($"There is no note wire from {from} to {to}. note_wires lists them.");
                        return Listing(core);
                    }, c.Cancellation);
                    return ToolResult.Ok($"Removed the note wire {c.String("source")} to {c.String("target")}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "note_chain",
                Title = "Note insert chain",
                Description =
                    "Edits the note inserts of an instrument: the note machines before it. The track's clip notes, live MIDI and mapped keys enter the first insert, each insert feeds the next, " +
                    "the last feeds the instrument. Give inserts (the whole chain in order; [] empties it), or add (one machine, at index or the end), or remove (one machine; its neighbours are joined). " +
                    "One undo step; refused with the reason when an insert isn't a note-in and note-out machine, or would make a loop. Example: note_chain(instrument=\"Lead\", inserts=[\"Arp\",\"Chord\"]).",
                Input = Object(null,
                    Req("instrument", String("The instrument machine")),
                    Opt("inserts", Array(String("A machine"), "The whole chain, first to last")),
                    Opt("add", String("A machine to put in the chain")),
                    Opt("index", Integer("Where add goes, from 0 (default: the end)", 0, 64)),
                    Opt("remove", String("A machine to take out"))),
                Output = Output(),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("note_chain", true, _ =>
                    {
                        string inst = Name(core, c, "instrument");
                        var store = core.NoteWires;
                        string why;
                        if (c.List("inserts") is { } list)
                        {
                            var names = list.Select(n => SongModel.Machine(core, n?.GetValue<string>() ?? "").Name).ToList();
                            why = store.SetChain(inst, names);
                        }
                        else if (c.String("add") is { } add)
                            why = store.AddInsert(inst, SongModel.Machine(core, add).Name, c.Int("index") ?? int.MaxValue);
                        else if (c.String("remove") is { } rem)
                        {
                            string name = SongModel.Machine(core, rem).Name;
                            if (!store.State.ChainOf(inst).Contains(name)) throw new ToolError($"{name} isn't in {inst}'s chain.");
                            why = store.RemoveInsert(name) ? null : $"{name} isn't in a chain.";
                        }
                        else throw new ToolError("Give inserts, add or remove.");
                        if (why != null) throw new ToolError(why);
                        return Listing(core);
                    }, c.Cancellation);
                    return ToolResult.Ok($"Note chain edited. Chains: {r["chains"]!.AsArray().Count}.", r);
                },
            });
        }
    }
}
