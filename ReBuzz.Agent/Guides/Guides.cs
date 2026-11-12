#nullable disable
// Agent server (docs/specs/mcp-server.md §6): the craft guides. One source, three ways in: the
// `guide` tool, resources rebuzz://guides/<topic> (and rebuzz://machines/<gear>/manual, see
// MachineManual.cs), and prompts (Prompts.cs). The guides are markdown files embedded in this
// assembly (Guides/*.md, curated machine pages Guides/machines/*.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.Guides
{
    public static partial class GuideLibrary
    {
        public const string UriPrefix = "rebuzz://guides/";

        /// <summary>What initialize says to an agent first: what the app is, how to begin, the units, undo.</summary>
        public const string ServerInstructions =
            Product.Name + " is a modular music studio (Buzz-style, a fork of ReBuzz): generators and effects connected in a graph, patterns placed in an arrangement. " +
            "Start every session with studio_status, and read guide(topic=\"workflow\") once. Times are in beats from the song start " +
            "(or \"bar.beat\" text); pitches are MIDI numbers or names (60 = C4); levels in dB. Every changing call is one undo step " +
            "(undo reverts it); a call that fails changes nothing. Hear your work with render and check it with analyze. In studio and live mode the user " +
            "listens live too: transport, session_launch and meters_read play with them. machine_find (or rebuzz://catalog) picks a machine for an intent; " +
            "rebuzz://reference/tools lists every tool and its inputs. Errors say what to do next.";

        /// <summary>The app's server as initialize announces it: named after the app (Product), with <see cref="ServerInstructions"/>.</summary>
        public static ServerInfo AppServer(string version) => new ServerInfo
        {
            Name = Product.McpName,
            Title = Product.Name,
            Version = version,
            Instructions = ServerInstructions,
        };

        static readonly (string Topic, string Title, string Description)[] Topics =
        {
            ("workflow", "Working in " + Product.Name, "The loop (build, render, measure, adjust), which tool for what, which guide for what, undo, and the checks before calling something done. Read first."),
            ("concepts", Product.Name + " in five minutes","Machines and the graph, parameters and tracks, patterns, the arrangement and the session grid, beats, pitches and levels."),
            ("composition", "Composition", "Time and grid math, harmony and voice leading, melody, drum grids with swing and humanizing, and arrangement energy, with the calls that write them."),
            ("genres", "Genres and moods", "Forms by genre, the mood table, and templates (tempo, key, structure) for house, techno, DnB, synthwave, hip-hop, ambient, pop and lo-fi."),
            ("sound-design", "Sound design", "Reading a sound (envelope, spectrum, movement, space), the build-measure loop, and Neo Drive, Comp, Verb, Ensemble (chorus, flanger, phaser), Delay (tempo, dub and tape echoes) and Filter as sound tools."),
            ("synth-recipes", "Synth recipes: Neo Poly and Neo Wave", "Recipes with exact values: pads, basses, leads, plucks, chords, drums and risers on Neo Poly; wavetable sweeps, wobble and talking leads on Neo Wave."),
            ("fm-recipes", "FM recipes: Neo FM6", "Electric piano, bell, FM bass, metallic percussion, brass swell and organ from Neo FM6's init patch, with exact values."),
            ("mixing", "Mixing", "Gain staging targets, balance by reference, frequency slots per instrument, EQ starting points by source, sends and pre-delay against tempo, mono lows, and the checks with render probes and analyze."),
            ("mixing-dynamics", "Mixing dynamics", "Compression starting points by source on Neo Comp, and the sidechain recipe that keeps kick and bass apart."),
            ("mastering", "Mastering", "Loudness and true-peak targets (streaming, club, broadcast), the flow, matching a reference, measuring after every change, and what not to do."),
            ("mastering-chain", "Mastering chain", "The chain stage by stage with settings: corrective EQ, glue compression, saturation, clipping, low end, stereo image, tape, limiter and dither."),
            ("dye-essence-in-depth", "Dye Essence in depth", "Dye Essence's signal flow (engines, filters, modulation, arpeggiator and sequencer, effects, imports) and how to use it, with recipes."),
            ("neo-eq-in-depth", "Neo EQ in depth", "Neo EQ's graphic mode, linear phase and curve layer, and the map of its factory presets."),
            ("troubleshooting", "Troubleshooting", "Silence, clipping, phase, latency, CPU, missing machines, machines that ignore notes, hanging notes, and a render that differs from the last one."),
        };

        public static IEnumerable<string> TopicNames => Topics.Select(t => t.Topic);

        /// <summary>A guide's title, or null.</summary>
        public static string Title(string topic) => Topics.FirstOrDefault(t => t.Topic == topic).Title;

        /// <summary>A guide's markdown, or null.</summary>
        public static string Get(string topic)
        {
            if (!Topics.Any(t => t.Topic == topic)) return null;
            using var stream = typeof(GuideLibrary).Assembly.GetManifestResourceStream($"ReBuzz.Agent.Guides.{topic}.md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>A guide's closing checklist (from "## Checklist" to the end), or null.</summary>
        public static string Checklist(string topic)
        {
            string text = Get(topic);
            if (text == null) return null;
            int at = text.LastIndexOf("\n## Checklist", StringComparison.Ordinal);
            return at < 0 ? null : text.Substring(at + 1).Trim();
        }

        public static IEnumerable<ResourceDef> Resources() =>
            Topics.Select(t => new ResourceDef
            {
                Uri = UriPrefix + t.Topic,
                Name = t.Topic,
                Title = t.Title,
                Description = t.Description,
                Read = () => Get(t.Topic) ?? "",
            });

        /// <summary>
        /// Registers the guide tool. <paramref name="manual"/> builds a gear's full manual
        /// (MachineManual.Build with its manifest and presets) from a gear name, or returns null for
        /// gear it doesn't know; without it, guide(machine=…) serves the curated page alone.
        /// <paramref name="manualGear"/> lists the gear with a manual (every installed gear with a manifest:
        /// generated, curated or a profiled draft, U3); without it, the gear with a curated page.
        /// </summary>
        public static void Register(ToolRegistry tools, Func<string, string> manual = null, Func<IEnumerable<string>> manualGear = null)
        {
            IReadOnlyList<string> Manuals() =>
                (manualGear?.Invoke() ?? Enumerable.Empty<string>()).Concat(MachineManual.CuratedGear)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            tools.Add(new ToolDef
            {
                Name = "guide",
                Title = "Read a guide",
                Description =
                    "Returns a guide as markdown: 'workflow' (how to work here; read it first), 'concepts' (machines, patterns, the arrangement, " +
                    "beats, pitches, levels), then by craft: 'composition', 'genres', 'sound-design', 'synth-recipes', 'fm-recipes', 'mixing', 'mixing-dynamics', " +
                    "'mastering', 'mastering-chain', 'troubleshooting', and the machine deep dives 'dye-essence-in-depth' and 'neo-eq-in-depth'. machine gives a gear's manual " +
                    "(every parameter, how to use it, presets). Without arguments, lists the topics. Read-only. " +
                    "Example: guide(topic=\"mixing\"), guide(machine=\"Neo Poly\").",
                Input = Object(null,
                    Opt("topic", String("The guide to read", TopicNames.ToArray())),
                    Opt("machine", String("A gear's manual instead: its gear name from gear_list (\"Neo Poly\")"))),
                Output = Object(null,
                    Opt("topic", String("The guide returned")),
                    Opt("machine", String("The gear whose manual was returned")),
                    Opt("markdown", String("Its text")),
                    Req("topics", Array(Object(null, Req("topic", String("Its name")), Req("title", String("Its title")), Req("about", String("What it covers"))), "Every guide")),
                    Opt("manuals", Array(String("A gear name"), "Gear with a manual: every installed machine with a manifest (generated, curated or a profiled draft)"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    var list = new JsonArray(Topics.Select(t => (JsonNode)new JsonObject { ["topic"] = t.Topic, ["title"] = t.Title, ["about"] = t.Description }).ToArray());
                    var gear = Manuals();
                    var manuals = new JsonArray(gear.Select(g => (JsonNode)g).ToArray());
                    string topic = c.String("topic");
                    string machine = c.String("machine");
                    if (topic != null && machine != null) throw new ToolError("Give topic or machine, not both.");
                    if (machine != null)
                    {
                        string text = manual?.Invoke(machine) ?? CuratedOnly(machine)
                            ?? throw new ToolError($"No manual for \"{machine}\"." + Near(machine, gear) + $" Manuals: {string.Join(", ", gear)}; " +
                                                   "machine_describe(machine=…) lists any machine's parameters.");
                        return Task.FromResult(ToolResult.Ok(text, new JsonObject { ["machine"] = machine, ["markdown"] = text, ["topics"] = list, ["manuals"] = manuals }));
                    }
                    if (topic == null)
                        return Task.FromResult(ToolResult.Ok(
                            "Guides: " + string.Join("; ", Topics.Select(t => $"{t.Topic} ({t.Description})")) +
                            $" Machine manuals: guide(machine=…) for {string.Join(", ", gear)}.",
                            new JsonObject { ["topics"] = list, ["manuals"] = manuals }));
                    string guide = Get(topic) ?? throw new ToolError($"No guide \"{topic}\". Topics: {string.Join(", ", TopicNames)}.");
                    return Task.FromResult(ToolResult.Ok(guide, new JsonObject { ["topic"] = topic, ["markdown"] = guide, ["topics"] = list, ["manuals"] = manuals }));
                },
            });
        }

        static string Near(string name, IReadOnlyList<string> gear)
        {
            var near = Text.Closest(name, gear);
            return near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "";
        }

        /// <summary>The curated page alone, titled, when no manifest is at hand.</summary>
        static string CuratedOnly(string gear)
        {
            string curated = MachineManual.Curated(gear);
            if (curated == null) return null;
            string name = MachineManual.CuratedGear.First(g => MachineManual.Slug(g) == MachineManual.Slug(gear));
            return $"# {name} manual\n\n{curated.Trim()}\n\n`machine_describe(machine=…)` lists every parameter with its unit, range and default.\n";
        }
    }
}
