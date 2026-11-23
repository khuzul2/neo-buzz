// Agent server (docs/specs/ux-overhaul.md §11, MT1): machine_tracks, the song's tracks as the Machines view's Tracks mode shows them.
// A read tool: the model is worked out from the wires (ReBuzz.Core.Tracks), never saved, so it is always what the song is now.

using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core.Tracks;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class TrackTools
    {
        static JsonArray Names(System.Collections.Generic.IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

        public static JsonObject Describe(TrackModel model)
        {
            var entries = new JsonArray();
            foreach (var e in model.Entries)
            {
                var o = new JsonObject
                {
                    ["id"] = e.Id,
                    ["kind"] = e.Kind == TrackKind.Track ? "track" : e.Kind == TrackKind.Group ? "group" : e.Kind == TrackKind.MasterChain ? "master" : "return",
                    ["name"] = e.Name,
                    ["machines"] = Names(e.Machines.Select(m => m.Name)),
                    ["chain"] = e.Chain,
                };
                if (e.Parent != null) o["member_of"] = e.Parent.Id;
                if (e.Members.Count > 0) o["members"] = Names(e.Members.Select(m => m.Id));
                if (e.Senders.Count > 0) o["receives_sends_from"] = Names(e.Senders.Select(m => m.Id));
                if (e.SendsTo.Count > 0) o["sends_to"] = Names(e.SendsTo.Select(m => m.Id));
                entries.Add(o);
            }
            var unrouted = new JsonArray(model.Unrouted.Select(u => (JsonNode)new JsonObject { ["machine"] = u.Node.Name, ["why"] = u.WhyText }).ToArray());
            return new JsonObject { ["tracks"] = entries, ["unrouted"] = unrouted, ["master"] = model.Master?.Name };
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "machine_tracks",
                Title = "The song's tracks",
                Description =
                    "The song's tracks as the Machines view's Tracks mode shows them, worked out from the wires (nothing is saved). A track is a generator and the " +
                    "machines only it feeds, up to the Master or a shared machine. A shared machine has feeders from several tracks: it is a group when some of them " +
                    "reach the Master only through it (its members), and a return when none do; the others send to it (wires into it are sends). A group can also " +
                    "receive sends, and groups nest. The shared machines every track passes through before the Master (an EQ and a limiter after the mix) are the Master's own chain: one entry, \"master:Master\", listed last, with a chain such as \"Eq → Limiter → Master\"; a track's chain then ends \"→ Master chain\". Control machines and anything that never reaches the Master are unrouted. Every machine is in exactly one place. " +
                    "A bus that only one track sends to is a branch of that track, not a return. Each entry has an id (\"track:Bass\", \"group:Drums\", \"return:Hall\"), " +
                    "its machines in signal order and its chain (\"Bass → Drive → Eq → Master\"). A wire at zero volume still counts, and so do muted and bypassed machines. " +
                    "Example: machine_tracks().",
                Input = Object(null),
                Output = Object(null,
                    Req("tracks", Array(Object(null,
                        Req("id", String("kind:name, such as track:Bass")),
                        Req("kind", String("track, group, return or master", "track", "group", "return", "master")),
                        Req("name", String("The generator's name (a group's or a return's: its machine's)")),
                        Req("machines", Array(String("A machine"), "Machines in this entry, in signal order (a group's own, not its members')")),
                        Req("chain", String("The way to the Master")),
                        Opt("member_of", String("The group this entry's machines reach the Master through")),
                        Opt("members", Array(String("An id"), "A group: the entries that go through it")),
                        Opt("receives_sends_from", Array(String("An id"), "A group or a return: the entries that send to it")),
                        Opt("sends_to", Array(String("An id"), "The shared machines this entry sends to"))), "Tracks first, then groups, then returns, then the Master's chain")),
                    Req("unrouted", Array(Object(null, Req("machine", String("Machine")), Req("why", String("control only, not wired, no way to the Master or in a loop"))), "Machines with no place in a track")),
                    Opt("master", String("The Master's name"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_tracks", false, _ => Describe(SongTracks.Of(core.SongCore)), c.Cancellation);
                    var list = r["tracks"]!.AsArray();
                    var un = r["unrouted"]!.AsArray();
                    string text = list.Count == 0 ? "The song has no tracks." : string.Join("; ", list.Select(t => $"{t!["id"]}: {t["chain"]}"));
                    if (un.Count > 0) text += ". Unrouted: " + string.Join(", ", un.Select(u => $"{u!["machine"]} ({u["why"]})"));
                    return ToolResult.Ok(text, r);
                },
            });
        }
    }
}
