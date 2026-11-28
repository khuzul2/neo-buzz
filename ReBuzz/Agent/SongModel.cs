// Agent server (docs/specs/mcp-server.md §3.3): the song as the agent addresses it. Machines and
// clips by name (with "did you mean" when a name is wrong), parameters with their values in
// units (exact for machines with a manifest, read through their value texts otherwise), times in
// beats. Engine thread only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using SongTime = ReBuzz.Agent.Model.SongTime;

namespace ReBuzz.Agent
{
    /// <summary>One parameter of a machine, as the tools see it.</summary>
    internal sealed class AgentParam
    {
        public IParameter Parameter { get; init; }
        /// <summary>1 global, 2 track.</summary>
        public int Group { get; init; }
        public ParamInfo Info { get; init; }
        /// <summary>The manifest's entry (exact units), or null.</summary>
        public ManifestParameter Manifest { get; init; }
        public bool IsTrack => Group == 2;
        /// <summary>
        /// A track parameter that holds no value (U2 in depth, mcp-server.md §12.12): each value written plays once, as a
        /// drum's trigger or a note's volume does. Only automation_write takes it (as events); param_set doesn't.
        /// </summary>
        public bool Played { get; init; }
        public string Name => Parameter.Name;
    }

    internal static class SongModel
    {
        // ------------------------------------------------------------------ machines

        /// <summary>The song's machines as the user sees them (hidden clip editor machines left out).</summary>
        public static IEnumerable<MachineCore> Machines(ReBuzzCore core) => core.SongCore.MachinesList.Where(m => !m.Hidden);

        public static MachineCore Machine(ReBuzzCore core, string name, string what = "machine")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError($"{what}: give a machine's name (machine_list shows them).");
            var all = Machines(core).ToList();
            var m = all.FirstOrDefault(x => x.Name == name) ?? all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (m != null) return m;
            var near = Text.Closest(name, all.Select(x => x.Name));
            throw new ToolError($"No machine \"{name}\" in the song." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") +
                                $" Machines: {string.Join(", ", all.Select(x => x.Name))}.");
        }

        public static string Kind(IMachine m) => m.DLL?.Info == null ? "unknown" : m.DLL.Info.Type switch
        {
            MachineType.Master => "master",
            MachineType.Generator => "generator",
            _ => "effect",
        };

        public static bool IsMaster(IMachine m) => m.DLL?.Info?.Type == MachineType.Master;

        /// <summary>
        /// Where a new machine goes in the machine view: generators left, effects in the middle, in
        /// columns; moved to the nearest free spot when another machine is there (the Master, gallery mac-510).
        /// </summary>
        public static (float X, float Y) NewPosition(ReBuzzCore core, bool generator)
        {
            int n = Machines(core).Count(m => !IsMaster(m) && (Kind(m) == "generator") == generator);
            float x = generator ? -0.75f : -0.25f;
            x += (n / 8) * 0.2f;
            float y = -0.8f + (n % 8) * 0.2f;
            return BuzzGUI.Common.MachinePlacement.Free(Machines(core), Math.Clamp(x, -0.95f, 0.95f), y);
        }

        // ------------------------------------------------------------------ clips

        public static IClip Clip(MachineCore machine, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError($"Give a clip of {machine.Name} by name (clip_list shows them).");
            var p = machine.Clips.FirstOrDefault(x => x.Name == name) ?? machine.Clips.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p;
            if (machine.Clips.Count == 0)
                throw new ToolError($"{machine.Name} has no clips. Make one with clip_create(machine=\"{machine.Name}\", length_beats=16).");
            var near = Text.Closest(name, machine.Clips.Select(x => x.Name));
            throw new ToolError($"{machine.Name} has no clip \"{name}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") +
                                $" Its clips: {string.Join(", ", machine.Clips.Select(x => x.Name))}.");
        }

        /// <summary>
        /// The clip's notes as the agent reads them, in time order: SongNotes' notes (the engine MIDI column's note-ons, or the Note
        /// track lanes for a machine that takes notes in tracks), with the machine's pitch offset taken off (§12.2, MachineNotes).
        /// </summary>
        public static List<NoteEvent> Notes(IClip p) =>
            p.Machine is MachineCore m ? MachineNotes.Read(p, MachineNotes.For(m)) : new List<NoteEvent>();

        /// <summary>Where the clip is placed in the arrangement, in ticks.</summary>
        public static List<int> Placements(ReBuzzCore core, IClip p) =>
            core.SongCore.Sequences.Where(s => s.Machine == p.Machine)
                .SelectMany(s => s.Events.Where(e => e.Value.Type == SequenceEventType.PlayClip && e.Value.Clip == p).Select(e => e.Key))
                .OrderBy(t => t).ToList();

        // ------------------------------------------------------------------ time

        public static int Tpb(ReBuzzCore core) => Math.Max(1, core.TPB);

        public static int BeatsPerBar(ReBuzzCore core) => Math.Max(1, core.SessionCore?.BeatsPerBar ?? 4);

        public static double Beats(ReBuzzCore core, int ticks) => SongTime.Beats(ticks, Tpb(core));

        public static int Ticks(ReBuzzCore core, double beats) => SongTime.Ticks(beats, Tpb(core));

        /// <summary>A song position from beats or "bar.beat", in ticks.</summary>
        public static int ParseTicks(ReBuzzCore core, JsonNode value, string what) =>
            Ticks(core, SongTime.ParseBeats(value, BeatsPerBar(core), what));

        public static string Where(ReBuzzCore core, int ticks)
        {
            double beats = Beats(core, ticks);
            return $"beat {NoteList.Fmt(beats)} (bar {SongTime.BarBeat(beats, BeatsPerBar(core))})";
        }

        // ------------------------------------------------------------------ parameters

        static readonly ConcurrentDictionary<string, MachineManifest> manifests = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The manifest beside a machine's DLL, or null (read once per DLL).</summary>
        public static MachineManifest Manifest(IMachineDLL dll)
        {
            if (dll == null || string.IsNullOrEmpty(dll.Path)) return null;
            return manifests.GetOrAdd(dll.Path, path =>
            {
                try { return MachineManifest.Load(MachineManifest.PathFor(path, dll.Name)); }
                catch (Exception) { return null; }
            });
        }

        // Fork (plugin-host.md §3.1): a machine that describes its parameters itself, per instance (a plugin host: names,
        // ranges and units come from the plugin that is loaded), gives its own manifest, kept until it says it changed.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, InstanceManifest> instanceManifests = new();

        sealed class InstanceManifest
        {
            public int Revision = int.MinValue;
            public MachineManifest Manifest;
        }

        /// <summary>The manifest of one machine: its own when it makes one (<see cref="Buzz.MachineInterface.IBuzzMachineManifest"/>), else the one beside its DLL.</summary>
        public static MachineManifest Manifest(IMachine machine)
        {
            if (machine is MachineCore { ManagedMachine: Buzz.MachineInterface.IBuzzMachineManifest source } mc)
            {
                try
                {
                    var cache = instanceManifests.GetOrCreateValue(mc);
                    int revision = source.ManifestRevision;
                    if (cache.Revision != revision)
                    {
                        string json = source.ManifestJson;
                        cache.Manifest = string.IsNullOrEmpty(json) ? null : MachineManifest.Parse(json);
                        cache.Revision = revision;
                    }
                    if (cache.Manifest != null) return cache.Manifest;
                }
                catch (Exception)
                {
                    // a manifest the machine can't make is no manifest: the values still read through the machine's own texts
                }
            }
            return Manifest(machine?.DLL);
        }

        /// <summary>The machine's global and track parameters (not its inputs, not notes).</summary>
        /// <param name="played">Also the track parameters that hold no value (triggers), marked <see cref="AgentParam.Played"/>.</param>
        public static List<AgentParam> Params(MachineCore m, bool played = false)
        {
            var manifest = Manifest(m);
            var list = new List<AgentParam>();
            for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
            {
                foreach (var p in m.ParameterGroups[g].Parameters)
                {
                    if (p.Type == ParameterType.Internal || p.Type == ParameterType.Note) continue;
                    // A hidden parameter (a plugin host's unbound slot) isn't the agent's to see.
                    if (p is IParameterMetadata { IsVisible: false }) continue;
                    // Track parameters that don't hold a value (a note's velocity) are played, not set.
                    bool isPlayed = g == 2 && (p.Flags & ParameterFlags.State) == 0;
                    if (isPlayed && !played) continue;
                    var entry = manifest?.FindAt(g == 2, p.IndexInGroup, p.Name);
                    if (entry != null && (entry.Min != p.MinValue || entry.Max != p.MaxValue)) entry = null;   // a stale manifest: trust the machine
                    var param = p;
                    // A curated manifest (U3) whose entry says the machine's own texts are wrong reads and writes through its mapping.
                    var own = entry is { OwnTexts: false } ? entry : null;
                    list.Add(new AgentParam
                    {
                        Parameter = p,
                        Group = g,
                        Played = isPlayed,
                        Manifest = entry,
                        Info = new ParamInfo
                        {
                            Name = p.Name,
                            Index = p.IndexInGroup,
                            Group = entry?.Group ?? (g == 2 ? "Track" : ""),
                            Description = entry?.Description ?? p.Description ?? "",
                            Min = p.MinValue,
                            Max = p.MaxValue,
                            Default = p.DefValue,
                            Mapping = entry?.Mapping,
                            // Drafts and curated manifests take the machine's own texts first ("Off", "1/8"); generated ones are exact.
                            TextsFirst = entry != null && own == null && (manifest.IsDraft || manifest.IsCurated),
                            Describe = own != null ? raw => ReBuzz.Agent.Guides.MachineManual.Format(own, raw) : raw => Describe(param, raw),
                            SafeRange = entry?.SafeRange,
                        },
                    });
                }
            }
            return list;
        }

        /// <summary>The machine's text for a raw value ("800 Hz"), or the number.</summary>
        public static string Describe(IParameter p, int raw)
        {
            string text = null;
            try { text = p.DescribeValue(raw); } catch (Exception) { }
            return string.IsNullOrWhiteSpace(text) ? raw.ToString(CultureInfo.InvariantCulture) : text.Trim();
        }

        static bool IsSnakeId(string s) => s.Length > 0 && s[0] >= 'a' && s[0] <= 'z' && s.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_');

        /// <param name="track">Whether the caller named a track (param_set's track): a global and a track parameter
        /// with the same name then resolve to the track one.</param>
        public static AgentParam Param(MachineCore m, List<AgentParam> all, string name, bool track = false)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("param: give a parameter's name (machine_describe lists them).");
            if (name.StartsWith("#") && int.TryParse(name.Substring(1), out int index))
            {
                var byIndex = all.FirstOrDefault(p => p.Group == 1 && p.Parameter.IndexInGroup == index);
                if (byIndex != null) return byIndex;
                throw new ToolError($"{m.Name} has no global parameter #{index}. machine_describe lists each parameter's index.");
            }
            var found = all.Where(p => p.Name == name).ToList();
            if (found.Count == 0) found = all.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            // A curated manifest's plain name ("Filter cutoff" for "VCF Freq", U3) names the parameter too.
            if (found.Count == 0) found = all.Where(p => p.Manifest is { PlainName.Length: > 0 } e && string.Equals(e.PlainName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            // The manifest's id ("filter_cutoff", neo-learn.md §5.3) names it too.
            // Only a snake_case id (Neo Learn's); the older manifests' ids are display words (Neo Drums' global "Accent gain" has the id "Accent",
            // the name of a track trigger that takes events in a second pass).
            if (found.Count == 0 && IsSnakeId(name)) found = all.Where(p => p.Manifest is { Id.Length: > 0 } e && string.Equals(e.Id, name, StringComparison.Ordinal)).ToList();
            if (found.Count == 1) return found[0];
            if (found.Count > 1 && track && found.Count(p => p.IsTrack) == 1) return found.First(p => p.IsTrack);
            if (found.Count > 1)
                throw new ToolError($"{m.Name} has {found.Count} parameters named \"{name}\": " +
                                    string.Join(", ", found.Select(p => p.IsTrack ? "a track parameter" : $"#{p.Parameter.IndexInGroup}")) + ". Address one by its #index.");
            var near = Text.Closest(name, all.Select(p => p.Name));
            throw new ToolError($"Unknown parameter \"{name}\" on \"{m.Name}\" ({m.DLL?.Name})." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") +
                                $" Call machine_describe(machine=\"{m.Name}\") for the list.");
        }

        /// <summary>A parameter's current value as JSON: text, number and unit when known, raw.</summary>
        public static JsonObject Value(AgentParam p, int track)
        {
            int raw = p.Parameter.GetValue(track);
            var o = new JsonObject { ["value"] = p.Info.Describe(raw) };
            double x = ValueResolver.Physical(p.Info, raw, out string dim);
            if (!double.IsNaN(x) && !double.IsInfinity(x) && p.Info.Mapping is not { Kind: MappingKind.Enum or MappingKind.Switch })
            {
                bool fraction = dim == "%" && (p.Info.Mapping == null || p.Info.Mapping.Kind is MappingKind.Percent or MappingKind.Bipolar);
                o["number"] = Math.Round(fraction ? x * 100 : x, 4);
                if (dim.Length > 0) o["unit"] = dim == "ratio" ? ":1" : dim;
            }
            o["raw"] = raw;
            if (p.Manifest?.Approximate == true) o["approximate"] = true;
            return o;
        }

        // ------------------------------------------------------------------ machines as JSON

        public static JsonObject MachineJson(ReBuzzCore core, MachineCore m)
        {
            var o = new JsonObject
            {
                ["name"] = m.Name,
                ["gear"] = m.DLL?.Name ?? "",
                ["kind"] = Kind(m),
            };
            // Fork (docs/specs/multi-output.md): a machine with several outputs lists them, numbered from 1 as inputs are; connect's from_output takes either.
            if (m.OutputChannelCount > 1)
                o["output_ports"] = new JsonArray(Enumerable.Range(0, m.OutputChannelCount)
                    .Select(i => (JsonNode)new JsonObject { ["index"] = i + 1, ["name"] = OutputName(m, i) }).ToArray());
            // Fork (docs/specs/instrument-input.md): a generator that takes audio says so, and how many inputs it has.
            if (m.DLL?.Info?.Type == MachineType.Generator && SongCore.TakesAudio(m))
                o["audio_inputs"] = m.InputChannelCount;
            o["inputs"] = new JsonArray(m.Inputs.Where(c => !((MachineCore)c.Source).Hidden).Select(c => (JsonNode)ConnectionJson(c, from: true)).ToArray());
            o["outputs"] = new JsonArray(m.Outputs.Where(c => !((MachineCore)c.Destination).Hidden).Select(c => (JsonNode)ConnectionJson(c, from: false)).ToArray());
            // NW1 (note-wires.md §7): the note ports, listed only where there are some; an insert says which instrument it sits before.
            if (ReBuzz.Core.NoteWires.NoteWireStore.PortsOf(m) is { Control: false } ports && (ports.In || ports.Out))
            {
                if (ports.In) o["note_in"] = true;
                if (ports.Out) o["note_out"] = true;
                if (core.NoteWires?.State.InstrumentOf(m.Name) is { } owner) o["note_insert_of"] = owner;
            }
            if (!IsMaster(m))
            {
                o["muted"] = m.IsMuted;
                if (m.IsDiverged)
                {
                    // Fork (machine-safety.md §3.2): the guard tripped; machine_set(reset=true) clears it.
                    o["diverged"] = true;
                    o["diverged_at"] = m.DivergedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    o["diverged_reason"] = m.DivergedReason;
                }
                o["soloed"] = m.IsSoloed;
                o["bypassed"] = m.IsBypassed;
                o["tracks"] = m.TrackCount;
                o["clips"] = m.Clips.Count;
            }
            // Fork (ux-overhaul.md §11, MX1): a track's colour, the palette colour every view draws it in (machine_set(colour=...) sets it).
            if (!IsMaster(m) && core.TrackColours is { } colours && colours.IndexOf(m) is int ci && ci >= 0)
            {
                o["colour"] = TrackColourStore.NameOf(ci);
                o["colour_index"] = ci;
            }
            if (m.Latency > 0) o["latency_samples"] = m.Latency;
            if (m.DLL != null && (m.DLL.IsMissing || m.DLL.IsCrashed)) o["health"] = m.DLL.IsMissing ? "missing" : "crashed";
            return o;
        }

        static JsonObject ConnectionJson(IMachineConnection c, bool from)
        {
            var o = new JsonObject { [from ? "from" : "to"] = (from ? c.Source : c.Destination).Name, ["gain_db"] = Levels.Json(Levels.AmpToDb(c.Amp)) };
            if (c.HasPan) o["pan"] = Levels.PanToUnit(c.Pan);
            if (!from && c.DestinationChannel > 0) o["input"] = c.Destination.GetChannelName(true, c.DestinationChannel);
            if (from && c.DestinationChannel > 0) o["input"] = c.Destination.GetChannelName(true, c.DestinationChannel);
            // Fork (docs/specs/multi-output.md): which output of a several-output source the wire reads.
            if (c.Source.OutputChannelCount > 1)
            {
                o["from_output"] = OutputName(c.Source, c.SourceChannel);
                o["from_output_index"] = c.SourceChannel + 1;
                var all = c.SourceChannels.OrderBy(x => x).ToList();
                if (all.Count > 1) o["from_outputs"] = new JsonArray(all.Select(x => (JsonNode)OutputName(c.Source, x)).ToArray());
            }
            return o;
        }

        /// <summary>An output's name as its machine gives it (a Neo machine's declared names), or "Output n" (from 1).</summary>
        public static string OutputName(IMachine m, int index)
        {
            string name = m.GetChannelName(false, index);
            return string.IsNullOrWhiteSpace(name) ? $"Output {index + 1}" : name;
        }

        public static IMachineConnection Connection(MachineCore from, MachineCore to) =>
            from.Outputs.FirstOrDefault(c => c.Destination == to);
    }
}
