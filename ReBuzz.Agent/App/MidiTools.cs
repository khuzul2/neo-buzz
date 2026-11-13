// Agent server (docs/specs/agent-knowledge.md §5, K3): parameter MIDI bindings. A binding makes a controller
// (channel 1 to 16, controller 0 to 127) drive a machine parameter; the song saves them. midi_bindings_list
// reads them, midi_bind and midi_unbind change them (each call is one undo step), and midi_learn waits for
// the next controller move on a MIDI input and binds it, as the studio's Bind… does.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class MidiTools
    {
        /// <summary>How long midi_learn may wait, at most (seconds).</summary>
        internal const int MaxWaitSeconds = 120;

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "midi_bindings_list",
                Title = "List MIDI bindings",
                Description =
                    "The song's parameter MIDI bindings: which controller (channel 1 to 16, controller 0 to 127) drives which parameter of which machine and track. " +
                    "Pass machine for one machine's. Example: midi_bindings_list(machine=\"Bass\").",
                Input = Object(null, Opt("machine", String("Only this machine's bindings"))),
                Output = Object(null, Req("count", Integer("How many")), Req("bindings", Array(BindingSchema(), "The bindings"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var list = await app.Engine("midi_bindings_list", false, () => app.Midi.List(c.String("machine")), c.Cancellation);
                    string text = list.Count == 0 ? "No MIDI bindings" + (c.String("machine") != null ? $" on {c.String("machine")}" : "") + "."
                        : $"{list.Count} MIDI binding{(list.Count == 1 ? "" : "s")}: " + string.Join("; ", list.Take(10).Select(Line)) + (list.Count > 10 ? "; …" : "") + ".";
                    return ToolResult.Ok(text, new JsonObject { ["count"] = list.Count, ["bindings"] = new JsonArray(list.Select(Json).ToArray()) });
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_bind",
                Title = "Bind a MIDI controller",
                Description =
                    "Binds a MIDI controller to a machine parameter: a move of controller number on channel drives the parameter over its range (soft takeover as the MIDI settings say). " +
                    "Replaces a binding the parameter had on that track. One undo step. Example: midi_bind(machine=\"Bass\", parameter=\"Cutoff\", channel=1, controller=74).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("parameter", String("The parameter's name (machine_describe lists them) or #index")),
                    Req("channel", Integer("MIDI channel", 1, 16)),
                    Req("controller", Integer("Controller number (CC)", 0, 127)),
                    Opt("track", Integer("Track of a track parameter (default 0; a global parameter has only 0)", 0, 255))),
                Output = Object(null, Req("binding", BindingSchema()), Opt("replaced", BindingSchema())),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var (machine, parameter) = (c.RequiredString("machine"), c.RequiredString("parameter"));
                    int channel = Check(c.Int("channel") ?? throw new ToolError("Missing \"channel\"."), 1, 16, "channel");
                    int controller = Check(c.Int("controller") ?? throw new ToolError("Missing \"controller\"."), 0, 127, "controller");
                    int track = c.Int("track") ?? 0;
                    var replaced = await app.Engine("midi_bind", true, () => app.Midi.Bind(machine, parameter, track, channel, controller), c.Cancellation);
                    return Bound(machine, parameter, track, channel, controller, replaced);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_unbind",
                Title = "Remove MIDI bindings",
                Description =
                    "Removes one parameter's MIDI binding (machine, parameter, track), or all of a machine's (all=true). One undo step. " +
                    "Example: midi_unbind(machine=\"Bass\", parameter=\"Cutoff\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("parameter", String("The parameter (name or #index)")),
                    Opt("track", Integer("Track of a track parameter (default 0)", 0, 255)),
                    Opt("all", Boolean("Every binding of the machine (instead of parameter)"))),
                Output = Object(null, Req("removed", Integer("Bindings removed"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    string machine = c.RequiredString("machine");
                    bool all = c.Bool("all") == true;
                    string? parameter = c.String("parameter");
                    if (all == (parameter != null)) throw new ToolError("Give parameter (one binding) or all=true (every binding of the machine), not both.");
                    int removed = await app.Engine("midi_unbind", true, () =>
                        all ? app.Midi.UnbindAll(machine) : app.Midi.Unbind(machine, parameter!, c.Int("track") ?? 0) ? 1 : 0, c.Cancellation);
                    return ToolResult.Ok(removed == 0 ? (all ? $"{machine} had no MIDI bindings." : $"{machine}'s {parameter} had no MIDI binding.")
                                                      : all ? $"Removed {removed} MIDI binding{(removed == 1 ? "" : "s")} of {machine}." : $"Removed {machine}'s {parameter} binding.",
                        new JsonObject { ["removed"] = removed });
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_learn",
                Title = "Learn a MIDI controller",
                Description =
                    "Waits for the next controller move (control change) on a MIDI input, up to timeout_seconds (default 15), and says which one it was. " +
                    "With machine and parameter it binds that controller there too (one undo step), as the studio's Bind… does. Tell the person to move the knob now. " +
                    "Times out with an error when nothing moves, and changes nothing. Example: midi_learn(machine=\"Bass\", parameter=\"Cutoff\", timeout_seconds=20).",
                Input = Object(null,
                    Opt("machine", String("Bind to this machine's parameter (with parameter)")),
                    Opt("parameter", String("The parameter (name or #index)")),
                    Opt("track", Integer("Track of a track parameter (default 0)", 0, 255)),
                    Opt("timeout_seconds", Number("How long to wait (default 15)", 1, MaxWaitSeconds))),
                Output = Object(null,
                    Req("channel", Integer("MIDI channel 1 to 16")),
                    Req("controller", Integer("Controller number")),
                    Opt("binding", BindingSchema()),
                    Opt("replaced", BindingSchema())),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string? machine = c.String("machine"), parameter = c.String("parameter");
                    if ((machine == null) != (parameter == null)) throw new ToolError("Give machine and parameter together (to bind what is learned), or neither (to only read the controller).");
                    double wait = c.Number("timeout_seconds") ?? 15;
                    var seen = new TaskCompletionSource<(int Channel, int Controller)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var listener = await app.Engine("midi_learn", false, () => app.Midi.Listen((ch, cc) => seen.TrySetResult((ch, cc))), c.Cancellation);
                    if (listener == null) throw new ToolError("No MIDI input is open here (Preferences ▸ MIDI chooses the inputs).");
                    (int Channel, int Controller) got;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.Cancellation);
                        timeout.CancelAfter(TimeSpan.FromSeconds(wait));
                        try { got = await seen.Task.WaitAsync(timeout.Token); }
                        catch (OperationCanceledException) when (!c.Cancellation.IsCancellationRequested)
                        {
                            throw new ToolError($"No controller moved in {wait:0.#} s: nothing is bound. Move the knob while this call waits, or check the MIDI inputs (Preferences ▸ MIDI).");
                        }
                    }
                    finally
                    {
                        listener.Dispose();
                    }
                    var result = new JsonObject { ["channel"] = got.Channel, ["controller"] = got.Controller };
                    if (machine == null)
                        return ToolResult.Ok($"Heard controller {got.Controller} on channel {got.Channel}. Nothing bound: pass machine and parameter to bind it.", result);
                    int track = c.Int("track") ?? 0;
                    var replaced = await app.Engine("midi_learn", true, () => app.Midi.Bind(machine, parameter!, track, got.Channel, got.Controller), c.Cancellation);
                    var bound = Bound(machine, parameter!, track, got.Channel, got.Controller, replaced);
                    result["binding"] = bound.Structured!["binding"]!.DeepClone();
                    if (bound.Structured["replaced"] != null) result["replaced"] = bound.Structured["replaced"]!.DeepClone();
                    return ToolResult.Ok("Learned: " + bound.Summary, result);
                },
            });
        }

        static int Check(int v, int min, int max, string what) =>
            v < min || v > max ? throw new ToolError($"{what}: {min} to {max}.") : v;

        static ToolResult Bound(string machine, string parameter, int track, int channel, int controller, MidiBindingInfo? replaced)
        {
            var o = new JsonObject { ["binding"] = Json(new MidiBindingInfo(machine, parameter, track, channel, controller)) };
            if (replaced != null) o["replaced"] = Json(replaced);
            return ToolResult.Ok($"{machine}'s {parameter} follows controller {controller} on channel {channel}." +
                                 (replaced != null ? $" It followed controller {replaced.Controller} on channel {replaced.Channel}." : ""), o);
        }

        static string Line(MidiBindingInfo b) => $"{b.Machine}.{b.Parameter}{(b.Track > 0 ? $"[{b.Track}]" : "")} ← CC {b.Controller} ch {b.Channel}";

        static JsonNode Json(MidiBindingInfo b) => new JsonObject
        {
            ["machine"] = b.Machine, ["parameter"] = b.Parameter, ["track"] = b.Track, ["channel"] = b.Channel, ["controller"] = b.Controller,
        };

        static JsonObject BindingSchema() => Object("A binding",
            Req("machine", String("The machine")), Req("parameter", String("The parameter")), Req("track", Integer("Its track (0 for a global parameter)")),
            Req("channel", Integer("MIDI channel 1 to 16")), Req("controller", Integer("Controller number 0 to 127")));
    }
}
