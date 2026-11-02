// Evaluation: the song as the scorers see it, read through the tools (machine_list, notes_read,
// arrangement_get, arrangement_markers, studio_status) and heard through render, whose WAV files
// our own meters measure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal sealed class Conn
    {
        public string From, To;
        public double GainDb;
        /// <summary>The destination's input when it isn't the first (a sidechain), else null.</summary>
        public string Input;
        public bool Main => Input == null;
    }

    internal sealed class Mach
    {
        public string Name, Gear, Kind;
        public bool Muted, Bypassed;
        public List<Conn> Outputs = new(), Inputs = new();
        public bool IsMaster => Kind == "master";
        public bool IsGenerator => Kind == "generator";
    }

    internal sealed class Graph
    {
        public List<Mach> Machines = new();

        public Mach Find(string name) => Machines.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public Mach Master => Machines.First(m => m.IsMaster);

        public IEnumerable<Mach> Generators => Machines.Where(m => m.IsGenerator);

        /// <summary>Generators whose sound reaches <paramref name="m"/> through main inputs (sidechains excluded).</summary>
        public HashSet<string> SourcesOf(Mach m)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<Mach>();
            stack.Push(m);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (!seen.Add(x.Name)) continue;
                if (x.IsGenerator) found.Add(x.Name);
                foreach (var i in x.Inputs.Where(i => i.Main)) if (Find(i.From) is Mach s) stack.Push(s);
            }
            return found;
        }

        /// <summary>Whether a generator's sound reaches Master at all (main inputs, nothing muted on the way).</summary>
        public bool Reaches(string generator)
        {
            var g = Find(generator);
            if (g == null || g.Muted) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<Mach>();
            stack.Push(g);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (!seen.Add(x.Name)) continue;
                if (x.IsMaster) return true;
                if (x.Muted) continue;
                foreach (var o in x.Outputs.Where(o => o.Main)) if (Find(o.To) is Mach d) stack.Push(d);
            }
            return false;
        }

        /// <summary>
        /// The machine to probe to hear <paramref name="generator"/> as it reaches Master: the machine
        /// feeding Master that carries only it (its own chain), when that machine's first output is
        /// Master (probes tap the first output). Otherwise the generator itself, before its effects.
        /// </summary>
        public (string Machine, string How) ProbeFor(string generator)
        {
            var g = Find(generator);
            if (g == null) return (null, "missing");
            var feeds = Machines.Where(m => !m.IsMaster && m.Outputs.Any(o => o.Main && string.Equals(o.To, Master.Name, StringComparison.OrdinalIgnoreCase)))
                .Where(m => SourcesOf(m).Contains(g.Name)).ToList();
            if (feeds.Count == 1 && SourcesOf(feeds[0]).Count == 1 && feeds[0].Outputs.Count > 0 &&
                string.Equals(feeds[0].Outputs[0].To, Master.Name, StringComparison.OrdinalIgnoreCase))
                return (feeds[0].Name, feeds[0].Name == g.Name ? "direct" : "its chain");
            return (g.Name, feeds.Count == 0 ? "not reaching Master" : "at the source (shared or branched path)");
        }
    }

    internal sealed class Rendered
    {
        public string Id, File;
        public StereoAudio Audio;
        public double Seconds, FromBeats, ToBeats;
        public Dictionary<string, StereoAudio> Probes = new(StringComparer.OrdinalIgnoreCase);
        public JsonObject Data;
    }

    internal sealed class Studio
    {
        public Studio(McpClient client) => C = client;

        public McpClient C { get; }

        public JsonObject Status() => C.Ok("studio_status");

        public void New(int? bpm = null)
        {
            var args = new JsonObject { ["discard_unsaved"] = true };
            if (bpm.HasValue) args["tempo_bpm"] = bpm.Value;
            C.Ok("song_new", args);
        }

        public void Open(string path) => C.Ok("song_open", new JsonObject { ["path"] = Path.GetFullPath(path), ["discard_unsaved"] = true });

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            C.Ok("song_save", new JsonObject { ["path"] = Path.GetFullPath(path), ["overwrite"] = true });
        }

        public Graph Graph()
        {
            var g = new Graph();
            foreach (var m in J.Objects(C.Ok("machine_list")["machines"]))
            {
                var mach = new Mach
                {
                    Name = J.Str(m["name"]),
                    Gear = J.Str(m["gear"]),
                    Kind = J.Str(m["kind"]),
                    Muted = J.Bool(m["muted"]),
                    Bypassed = J.Bool(m["bypassed"]),
                };
                foreach (var o in J.Objects(m["outputs"]))
                    mach.Outputs.Add(new Conn { From = mach.Name, To = J.Str(o["to"]), GainDb = J.Num(o["gain_db"]), Input = o["input"] != null ? J.Str(o["input"]) : null });
                foreach (var i in J.Objects(m["inputs"]))
                    mach.Inputs.Add(new Conn { From = J.Str(i["from"]), To = mach.Name, GainDb = J.Num(i["gain_db"]), Input = i["input"] != null ? J.Str(i["input"]) : null });
                g.Machines.Add(mach);
            }
            return g;
        }

        /// <summary>Renders with <paramref name="args"/> and reads the master and probe WAVs.</summary>
        public Rendered Render(JsonObject args = null)
        {
            var r = C.Ok("render", args ?? new JsonObject(), seconds: 600);
            var result = new Rendered
            {
                Id = J.Str(r["id"]),
                File = J.Str(r["file"]),
                Seconds = J.Num(r["seconds"]),
                FromBeats = J.Num(r["from_beats"]),
                ToBeats = J.Num(r["to_beats"]),
                Data = r,
            };
            result.Audio = Meters.Read(result.File);
            foreach (var p in J.Objects(r["probes"]))
                result.Probes[J.Str(p["machine"])] = Meters.Read(J.Str(p["file"]));
            return result;
        }

        /// <summary>
        /// The whole song with each named part heard as it reaches Master (see <see cref="Graph.ProbeFor"/>):
        /// (render, part → audio or null, part → how it was probed).
        /// </summary>
        public (Rendered Mix, Dictionary<string, StereoAudio> Parts, Dictionary<string, string> How) RenderParts(Graph g, IEnumerable<string> parts, double tail = 1)
        {
            var plan = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => g.ProbeFor(p), StringComparer.OrdinalIgnoreCase);
            var probes = plan.Values.Where(v => v.Machine != null).Select(v => v.Machine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var args = new JsonObject { ["tail_seconds"] = tail, ["name"] = "eval" };
            if (probes.Count > 0) args["probes"] = J.Strings(probes);
            var mix = Render(args);
            var audio = new Dictionary<string, StereoAudio>(StringComparer.OrdinalIgnoreCase);
            var how = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (part, (machine, h)) in plan)
            {
                how[part] = machine == null ? "missing" : machine.Equals(part, StringComparison.OrdinalIgnoreCase) ? h : $"{h} ({machine})";
                audio[part] = machine != null && mix.Probes.TryGetValue(machine, out var a) ? a : null;
            }
            return (mix, audio, how);
        }

        /// <summary>Every pattern's notes, by machine and pattern.</summary>
        public Dictionary<string, Dictionary<string, List<JsonObject>>> Notes()
        {
            var all = new Dictionary<string, Dictionary<string, List<JsonObject>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in J.Objects(C.Ok("pattern_list")["machines"]))
            {
                string machine = J.Str(m["machine"]);
                var byPattern = new Dictionary<string, List<JsonObject>>();
                foreach (var p in J.Objects(m["patterns"]))
                {
                    string pattern = J.Str(p["name"]);
                    byPattern[pattern] = J.Objects(C.Ok("notes_read", new JsonObject { ["machine"] = machine, ["pattern"] = pattern })["notes"]).ToList();
                }
                all[machine] = byPattern;
            }
            return all;
        }

        /// <summary>The notes a machine plays in the arrangement (each placed pattern's notes, whatever the place).</summary>
        public List<JsonObject> PlacedNotes(string machine, JsonObject arrangement, Dictionary<string, Dictionary<string, List<JsonObject>>> notes)
        {
            var lane = J.Objects(arrangement["lanes"]).FirstOrDefault(l => string.Equals(J.Str(l["machine"]), machine, StringComparison.OrdinalIgnoreCase));
            if (lane == null || !notes.TryGetValue(machine, out var patterns)) return new List<JsonObject>();
            var result = new List<JsonObject>();
            foreach (var e in J.Objects(lane["events"]))
                if (e["pattern"] is JsonNode pn && patterns.TryGetValue(J.Str(pn), out var ns))
                {
                    double at = J.Num(e["at"]), ends = J.Num(e["ends"]);
                    foreach (var n in ns)
                    {
                        double start = at + J.Num(n["start"]);
                        if (start < ends) result.Add(new JsonObject { ["pitch"] = J.Num(n["pitch"]), ["start"] = start, ["length"] = Math.Min(J.Num(n["length"]), ends - start) });
                    }
                }
            return result;
        }

        public List<(string Name, double At, double Length)> Markers() =>
            J.Objects(C.Ok("arrangement_markers")["markers"]).Select(m => (J.Str(m["name"]), J.Num(m["at"]), J.Num(m["length_beats"]))).ToList();
    }
}
