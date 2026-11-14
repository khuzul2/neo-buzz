#nullable disable
// Neo Patch NP3 (docs/specs/neo-patch.md §4.6): what the agent's patch_* tools do to a patch, with no host in it. The tools (ReBuzz.Agent/App/PatchTools.cs)
// hand each call to the machine as an operation name and a JSON object; the machine hands it here. Every edit goes through the same PatchEditModel the
// inner canvas uses, with the song's history behind it, so an edit is one step on the song's undo stack whoever made it, and the open editor redraws.
// A call checks everything first and changes nothing when it refuses; the reply is JSON, and a refusal is {"error": "..."} with what to do about it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace NeoBuzz.Patch
{
    internal sealed class PatchAgentError : Exception
    {
        public PatchAgentError(string message) : base(message) { }
    }

    internal sealed class PatchAgentApi
    {
        readonly PatchEngine engine;
        readonly PatchEditModel model;

        public PatchAgentApi(PatchEngine engine, IPatchHistory history)
        {
            this.engine = engine;
            model = new PatchEditModel(engine, history);
        }

        public PatchEditModel Model => model;

        /// <summary>Whether the last call changed the patch (the machine tells the open editor).</summary>
        public bool Edited { get; private set; }

        public static readonly string[] Ops = { "describe", "add_node", "connect", "disconnect", "set", "remove", "macro_map", "load_template", "rename" };

        public string Invoke(string op, string argsJson)
        {
            Edited = false;
            try
            {
                var args = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson) as JsonObject ?? throw new PatchAgentError("The arguments must be a JSON object.");
                string before = engine.Text();
                var result = op switch
                {
                    "describe" => Describe(args),
                    "add_node" => AddNode(args),
                    "connect" => Connect(args),
                    "disconnect" => Disconnect(args),
                    "set" => Set(args),
                    "remove" => Remove(args),
                    "macro_map" => MacroMap(args),
                    "load_template" => LoadTemplate(args),
                    "rename" => Rename(args),
                    _ => throw new PatchAgentError($"Neo Patch has no operation \"{op}\" (it has {string.Join(", ", Ops)})."),
                };
                Edited = engine.Text() != before;
                return result.ToJsonString();
            }
            catch (PatchAgentError e)
            {
                return new JsonObject { ["error"] = e.Message }.ToJsonString();
            }
            catch (Exception e)
            {
                return new JsonObject { ["error"] = "Neo Patch could not do that: " + e.Message }.ToJsonString();
            }
        }

        // ------------------------------------------------------------------ reading

        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static string KindName(NodeKind k) => NodeInfo.For(k).Name;

        public static IEnumerable<string> KindNames() => Enum.GetValues<NodeKind>().Select(KindName);

        static JsonObject NodeJson(PatchEditModel m, Node n)
        {
            var info = n.Info;
            var controls = new JsonArray();
            for (int i = 0; i < info.Controls.Length; i++)
            {
                var c = info.Controls[i];
                var o = new JsonObject { ["index"] = i, ["name"] = c.Name, ["value"] = Math.Round(n.Controls[i], 5), ["shown"] = ControlScale.Format(c, n.Controls[i]) };
                if (c.Choices != null) o["choices"] = new JsonArray(c.Choices.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
                else { o["min"] = c.Min; o["max"] = c.Max; if (c.Unit.Length > 0) o["unit"] = c.Unit; }
                controls.Add(o);
            }
            var ins = new JsonArray(info.Ins.Select((p, i) => (JsonNode)new JsonObject { ["port"] = i, ["name"] = p.Name, ["type"] = p.Type.ToString().ToLowerInvariant() }).ToArray());
            var outs = new JsonArray(info.Outs.Select((p, i) => (JsonNode)new JsonObject { ["port"] = i, ["name"] = p.Name, ["type"] = p.Type.ToString().ToLowerInvariant() }).ToArray());
            return new JsonObject
            {
                ["id"] = n.Id, ["kind"] = info.Name, ["scope"] = m.IsGlobal(n.Id) ? "global" : "per voice", ["heard"] = m.IsHeard(n.Id),
                ["x"] = n.X, ["y"] = n.Y, ["controls"] = controls, ["inputs"] = ins, ["outputs"] = outs,
            };
        }

        string PortLabel(int node, int port, bool output)
        {
            var n = engine.Graph.Find(node);
            if (n == null) return "?";
            var ports = output ? n.Info.Outs : n.Info.Ins;
            return n.Info.Name + " " + n.Id + " " + (port >= 0 && port < ports.Length ? ports[port].Name : "?");
        }

        JsonObject Describe(JsonObject a)
        {
            var g = engine.Graph;
            var nodes = new JsonArray(g.Nodes.Select(n => (JsonNode)NodeJson(model, n)).ToArray());
            var wires = new JsonArray(g.Wires.Select(w => (JsonNode)new JsonObject
            {
                ["from_node"] = w.FromNode, ["from_port"] = w.FromPort, ["to_node"] = w.ToNode, ["to_port"] = w.ToPort, ["amount"] = Math.Round(w.Amount, 5),
                ["text"] = PortLabel(w.FromNode, w.FromPort, true) + " to " + PortLabel(w.ToNode, w.ToPort, false) + (Math.Abs(w.Amount - 1f) > 1e-6f ? " x" + w.Amount.ToString("0.###", CultureInfo.InvariantCulture) : ""),
            }).ToArray());
            var macros = new JsonArray();
            for (int i = 0; i < 16; i++)
            {
                var maps = g.Macros.Where(m => m.Macro == i).ToList();
                if (maps.Count == 0 && g.MacroLabels[i].Length == 0) continue;
                macros.Add(new JsonObject
                {
                    ["macro"] = i + 1, ["label"] = PatchEditModel.MacroName(g, i),
                    ["mappings"] = new JsonArray(maps.Select(m => (JsonNode)new JsonObject
                    {
                        ["node"] = m.NodeId, ["control"] = g.Find(m.NodeId)?.Info.Controls[m.Control].Name, ["control_index"] = m.Control, ["low"] = Math.Round(m.Min, 5), ["high"] = Math.Round(m.Max, 5),
                        ["text"] = PatchEditModel.MappingText(g, m),
                    }).ToArray()),
                });
            }
            var result = new JsonObject
            {
                ["name"] = g.Name, ["node_count"] = g.Nodes.Count, ["node_cap"] = NodeInfo.Cap, ["wire_count"] = g.Wires.Count,
                ["nodes"] = nodes, ["wires"] = wires, ["macros"] = macros,
                ["cpu"] = new JsonObject { ["percent"] = Math.Round(engine.CpuPercent, 2), ["peak_percent"] = Math.Round(engine.CpuPeakPercent, 2) },
                ["unheard"] = new JsonArray(g.Nodes.Where(n => !model.IsHeard(n.Id)).Select(n => (JsonNode)n.Id).ToArray()),
            };
            if (engine.Error != null) result["compile_error"] = engine.Error;
            if (a["catalog"]?.GetValue<bool>() == true) result["catalog"] = Catalog();
            return result;
        }

        static JsonArray Catalog()
        {
            var list = new JsonArray();
            foreach (var kind in Enum.GetValues<NodeKind>())
            {
                var info = NodeInfo.For(kind);
                list.Add(new JsonObject
                {
                    ["kind"] = info.Name, ["group"] = info.Group,
                    ["inputs"] = string.Join(", ", info.Ins.Select((p, i) => i + " " + p.Name)),
                    ["outputs"] = string.Join(", ", info.Outs.Select((p, i) => i + " " + p.Name)),
                    ["controls"] = string.Join(", ", info.Controls.Select((c, i) => i + " " + c.Name + (c.Choices != null ? " (" + string.Join("/", c.Choices) + ")" : " (" + ControlScale.Format(c, c.Min) + " to " + ControlScale.Format(c, c.Max) + ")"))),
                });
            }
            return list;
        }

        // ------------------------------------------------------------------ resolving names

        NodeKind ResolveKind(JsonNode v)
        {
            string s = v?.ToString();
            if (string.IsNullOrWhiteSpace(s)) throw new PatchAgentError("Give the node's kind: " + string.Join(", ", KindNames()) + ".");
            string want = Norm(s);
            foreach (var k in Enum.GetValues<NodeKind>())
                if (Norm(KindName(k)) == want || Norm(k.ToString()) == want) return k;
            string near = string.Join(", ", KindNames().Where(n => Norm(n).Contains(want) || want.Contains(Norm(n))));
            throw new PatchAgentError($"There is no node kind \"{s}\". " + (near.Length > 0 ? $"Did you mean {near}? " : "") + "The kinds are " + string.Join(", ", KindNames()) + ".");
        }

        Node ResolveNode(JsonNode v, string what = "node")
        {
            if (v == null) throw new PatchAgentError($"Give the {what} (a node id from patch_describe).");
            if (!TryInt(v, out int id)) throw new PatchAgentError($"The {what} is a node id (a whole number from patch_describe), not \"{v}\".");
            var n = engine.Graph.Find(id);
            if (n == null)
                throw new PatchAgentError($"There is no node {id}. The nodes are " + (engine.Graph.Nodes.Count == 0 ? "none: the patch is empty (patch_add_node adds one)." : string.Join(", ", engine.Graph.Nodes.Select(x => x.Info.Name + " " + x.Id)) + "."));
            return n;
        }

        static bool TryInt(JsonNode v, out int i)
        {
            i = 0;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue(out int x)) { i = x; return true; }
                if (jv.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) { i = (int)d; return true; }
                if (jv.TryGetValue(out string s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) { i = y; return true; }
            }
            return false;
        }

        int ResolvePort(Node n, JsonNode v, bool output)
        {
            var ports = output ? n.Info.Outs : n.Info.Ins;
            string what = output ? "output" : "input";
            string list = ports.Length == 0 ? "none" : string.Join(", ", ports.Select((p, i) => i + " " + p.Name));
            if (ports.Length == 0) throw new PatchAgentError($"{n.Info.Name} {n.Id} has no {what} ports.");
            if (v == null) throw new PatchAgentError($"Give {n.Info.Name} {n.Id}'s {what} port (its {what}s: {list}).");
            if (TryInt(v, out int i))
            {
                if (i >= 0 && i < ports.Length) return i;
                throw new PatchAgentError($"{n.Info.Name} {n.Id} has no {what} port {i} (its {what}s: {list}).");
            }
            string want = Norm(v.ToString());
            for (int p = 0; p < ports.Length; p++) if (Norm(ports[p].Name) == want) return p;
            throw new PatchAgentError($"{n.Info.Name} {n.Id} has no {what} port \"{v}\" (its {what}s: {list}).");
        }

        int ResolveControl(Node n, JsonNode v)
        {
            var cs = n.Info.Controls;
            string list = cs.Length == 0 ? "none" : string.Join(", ", cs.Select((c, i) => i + " " + c.Name));
            if (cs.Length == 0) throw new PatchAgentError($"{n.Info.Name} {n.Id} has no controls.");
            if (v == null) throw new PatchAgentError($"Give the control of {n.Info.Name} {n.Id} (its controls: {list}).");
            if (TryInt(v, out int i))
            {
                if (i >= 0 && i < cs.Length) return i;
                throw new PatchAgentError($"{n.Info.Name} {n.Id} has no control {i} (its controls: {list}).");
            }
            string want = Norm(v.ToString());
            for (int c = 0; c < cs.Length; c++) if (Norm(cs[c].Name) == want) return c;
            for (int c = 0; c < cs.Length; c++) if (Norm(cs[c].Name).StartsWith(want) && want.Length >= 3) return c;
            throw new PatchAgentError($"{n.Info.Name} {n.Id} has no control \"{v}\" (its controls: {list}).");
        }

        /// <summary>A control value in the control's own units: a number, a number with a unit (1.5 kHz, 250 ms, 0.2 s), or a choice's name.</summary>
        static float ResolveValue(Node n, int control, JsonNode v)
        {
            var c = n.Info.Controls[control];
            string who = $"{n.Info.Name} {n.Id} {c.Name}";
            if (v == null) throw new PatchAgentError($"Give the value for {who}.");
            if (c.Choices != null)
            {
                if (v is JsonValue jc && jc.TryGetValue(out string name))
                {
                    string want = Norm(name);
                    for (int i = 0; i < c.Choices.Length; i++) if (Norm(c.Choices[i]) == want) return i;
                }
                if (TryInt(v, out int idx) && idx >= 0 && idx < c.Choices.Length) return idx;
                throw new PatchAgentError($"{who} is one of {string.Join(", ", c.Choices.Select((x, i) => i + " " + x))}; \"{v}\" is not.");
            }
            double value;
            string unit = null;
            if (v is JsonValue jn && jn.TryGetValue(out double d)) value = d;
            else
            {
                string s = v.ToString().Trim();
                int cut = s.Length;
                while (cut > 0 && char.IsLetter(s[cut - 1])) cut--;
                unit = s.Substring(cut).ToLowerInvariant();
                if (!double.TryParse(s.Substring(0, cut).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new PatchAgentError($"{who} takes a number in {(c.Unit.Length > 0 ? c.Unit : "its own units")} ({ControlScale.Format(c, c.Min)} to {ControlScale.Format(c, c.Max)}); \"{s}\" is not one.");
            }
            if (unit is { Length: > 0 })
            {
                switch (unit)
                {
                    case "khz" when c.Unit == "Hz": value *= 1000; break;
                    case "hz" when c.Unit == "Hz": break;
                    case "ms" when c.Unit == "s": value /= 1000; break;
                    case "s" when c.Unit == "s": break;
                    case "s" when c.Unit == "ms": value *= 1000; break;
                    case "ms" when c.Unit == "ms": break;
                    case "st" when c.Unit == "semitones": break;
                    case "ct" when c.Unit == "cents": break;
                    default: throw new PatchAgentError($"{who} is in {(c.Unit.Length > 0 ? c.Unit : "plain numbers")}; \"{unit}\" does not fit it.");
                }
            }
            if (!double.IsFinite(value)) throw new PatchAgentError($"{who} needs a finite number.");
            if (value < c.Min - 1e-6 || value > c.Max + 1e-6)
                throw new PatchAgentError($"{who} runs from {ControlScale.Format(c, c.Min)} to {ControlScale.Format(c, c.Max)} ({c.Min.ToString(CultureInfo.InvariantCulture)} to {c.Max.ToString(CultureInfo.InvariantCulture)}{(c.Unit.Length > 0 ? " " + c.Unit : "")}); {value.ToString(CultureInfo.InvariantCulture)} is outside it.");
            return (float)value;
        }

        static float? Number(JsonObject a, string key)
        {
            var v = a[key];
            if (v == null) return null;
            if (v is JsonValue jv && jv.TryGetValue(out double d) && double.IsFinite(d)) return (float)d;
            if (v is JsonValue js && js.TryGetValue(out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double e) && double.IsFinite(e)) return (float)e;
            throw new PatchAgentError($"\"{key}\" must be a number.");
        }

        // ------------------------------------------------------------------ editing

        /// <summary>One undo step around an edit (the model's gesture), closed whatever happens.</summary>
        T Step<T>(string name, Func<T> edit)
        {
            model.BeginGesture(name);
            try { return edit(); }
            finally { model.EndGesture(); }
        }

        JsonObject AddNode(JsonObject a)
        {
            var kind = ResolveKind(a["kind"]);
            var info = NodeInfo.For(kind);
            // Check the controls before anything changes.
            var wanted = new List<(int Control, float Value)>();
            if (a["controls"] is JsonObject cs)
            {
                var probe = new Node(0, kind);
                foreach (var kv in cs) { int ci = ResolveControl(probe, JsonValue.Create(kv.Key)); wanted.Add((ci, ResolveValue(probe, ci, kv.Value))); }
            }
            bool? global = a["global"] is JsonValue gv && gv.TryGetValue(out bool gb) ? gb : null;
            if (global == true && (info.ForcedPerVoice)) throw new PatchAgentError($"{info.Name} is always per voice (it is the notes themselves).");
            if (engine.Graph.Nodes.Count >= NodeInfo.Cap) throw new PatchAgentError($"The patch is at its {NodeInfo.Cap}-node limit. Remove a node (patch_remove) first.");
            int count = engine.Graph.Nodes.Count;
            float x = Number(a, "x") ?? 208f * (count % 5), y = Number(a, "y") ?? 144f * (count / 5);
            Node node = null;
            Step("Add " + info.Name, () =>
            {
                node = model.AddNode(kind, x, y);
                if (node == null) throw new PatchAgentError("The node could not be added.");
                foreach (var (ci, value) in wanted) model.SetControl(node.Id, ci, value);
                if (global == true && !info.ForcedGlobal) model.SetGlobal(node.Id, true);
                return 0;
            });
            var result = NodeJson(model, engine.Graph.Find(node.Id));
            result["node_count"] = engine.Graph.Nodes.Count;
            return result;
        }

        JsonObject Connect(JsonObject a)
        {
            var from = ResolveNode(a["from_node"], "from_node");
            var to = ResolveNode(a["to_node"], "to_node");
            int fp = ResolvePort(from, a["from_port"] ?? 0, true);
            int tp = ResolvePort(to, a["to_port"], false);
            float? amount = Number(a, "amount");
            if (amount is float am && Math.Abs(am) > 16f) throw new PatchAgentError("A wire's amount runs from -16 to 16 (1 passes the signal as it is).");
            var key = new WireKey(from.Id, fp, to.Id, tp);
            bool exists = engine.Graph.Wires.Any(w => WireKey.Of(w).Equals(key));
            string text = PortLabel(from.Id, fp, true) + " to " + PortLabel(to.Id, tp, false);
            if (exists)
            {
                bool changed = amount != null && model.SetWireAmount(key, amount.Value);
                return new JsonObject { ["connected"] = text, ["already_connected"] = !changed, ["amount"] = Math.Round(amount ?? engine.Graph.Wires.First(w => WireKey.Of(w).Equals(key)).Amount, 5), ["wire_count"] = engine.Graph.Wires.Count };
            }
            var outcome = Step("Connect " + from.Info.Name + " to " + to.Info.Name, () => model.Connect(from.Id, fp, to.Id, tp, amount ?? 1f));
            if (!outcome.Ok) throw new PatchAgentError(outcome.Message);
            return new JsonObject { ["connected"] = text, ["amount"] = Math.Round(amount ?? 1f, 5), ["wire_count"] = engine.Graph.Wires.Count };
        }

        JsonObject Disconnect(JsonObject a)
        {
            if (a["all"] is JsonValue av && av.TryGetValue(out bool all) && all)
            {
                var node = ResolveNode(a["node"] ?? a["from_node"]);
                int had = engine.Graph.Wires.Count(w => w.FromNode == node.Id || w.ToNode == node.Id);
                if (had == 0) throw new PatchAgentError($"{node.Info.Name} {node.Id} has no wires.");
                Step("Disconnect " + node.Info.Name, () => model.DisconnectNode(node.Id));
                return new JsonObject { ["removed"] = had, ["wire_count"] = engine.Graph.Wires.Count };
            }
            var from = ResolveNode(a["from_node"], "from_node");
            var to = ResolveNode(a["to_node"], "to_node");
            int fp = ResolvePort(from, a["from_port"] ?? 0, true);
            int tp = ResolvePort(to, a["to_port"], false);
            var key = new WireKey(from.Id, fp, to.Id, tp);
            if (!engine.Graph.Wires.Any(w => WireKey.Of(w).Equals(key)))
            {
                var there = engine.Graph.Wires.Where(w => w.FromNode == from.Id && w.ToNode == to.Id).ToList();
                throw new PatchAgentError($"There is no wire {PortLabel(from.Id, fp, true)} to {PortLabel(to.Id, tp, false)}." +
                    (there.Count > 0 ? " The wires between those nodes are " + string.Join(", ", there.Select(w => PortLabel(w.FromNode, w.FromPort, true) + " to " + PortLabel(w.ToNode, w.ToPort, false))) + "." : " Those two nodes are not wired together."));
            }
            Step("Disconnect", () => model.Disconnect(key));
            return new JsonObject { ["removed"] = 1, ["wire_count"] = engine.Graph.Wires.Count };
        }

        JsonObject Set(JsonObject a)
        {
            var list = new List<(Node Node, int Control, float Value)>();
            var scopes = new List<(Node Node, bool Global)>();
            void One(JsonObject o)
            {
                var n = ResolveNode(o["node"]);
                if (o["global"] is JsonValue gv && gv.TryGetValue(out bool gb))
                {
                    if (n.Info.ForcedGlobal || n.Info.ForcedPerVoice) throw new PatchAgentError($"{n.Info.Name} {n.Id} is always {(n.Info.ForcedGlobal ? "global" : "per voice")}; its scope cannot change.");
                    scopes.Add((n, gb));
                }
                if (o["control"] == null && o["value"] == null && scopes.Any(s => s.Node == n)) return;
                int c = ResolveControl(n, o["control"]);
                list.Add((n, c, ResolveValue(n, c, o["value"])));
            }
            if (a["changes"] is JsonArray changes)
            {
                if (changes.Count == 0) throw new PatchAgentError("changes is empty.");
                foreach (var item in changes) One(item as JsonObject ?? throw new PatchAgentError("Each change is {\"node\": id, \"control\": name, \"value\": number}."));
            }
            else One(a);
            var before = list.Select(x => ControlScale.Format(x.Node.Info.Controls[x.Control], x.Node.Controls[x.Control])).ToList();
            Step(list.Count == 1 ? $"{list[0].Node.Info.Name} {list[0].Node.Info.Controls[list[0].Control].Name}" : "Set " + list.Count + " controls", () =>
            {
                foreach (var (n, c, v) in list) model.SetControl(n.Id, c, v);
                foreach (var (n, g) in scopes) model.SetGlobal(n.Id, g);
                return 0;
            });
            var set = new JsonArray();
            for (int i = 0; i < list.Count; i++)
            {
                var (n, c, _) = list[i];
                var info = n.Info.Controls[c];
                var live = engine.Graph.Find(n.Id);
                var o = new JsonObject
                {
                    ["node"] = n.Id, ["kind"] = n.Info.Name, ["control"] = info.Name, ["value"] = Math.Round(live.Controls[c], 5), ["shown"] = ControlScale.Format(info, live.Controls[c]), ["was"] = before[i],
                };
                if (engine.Graph.Macros.Any(m => m.NodeId == n.Id && m.Control == c)) o["note"] = "A macro is mapped to this control: while its knob moves, the macro writes the control; the value you set holds until then.";
                set.Add(o);
            }
            return new JsonObject { ["set"] = set };
        }

        JsonObject Remove(JsonObject a)
        {
            var ids = new List<int>();
            if (a["nodes"] is JsonArray arr) foreach (var v in arr) ids.Add(ResolveNode(v).Id);
            else ids.Add(ResolveNode(a["node"]).Id);
            ids = ids.Distinct().ToList();
            var names = ids.Select(i => engine.Graph.Find(i).Info.Name + " " + i).ToList();
            int wires = engine.Graph.Wires.Count(w => ids.Contains(w.FromNode) || ids.Contains(w.ToNode));
            Step(ids.Count == 1 ? "Delete " + engine.Graph.Find(ids[0]).Info.Name : "Delete " + ids.Count + " nodes", () => { foreach (int id in ids) model.DeleteNode(id); return 0; });
            return new JsonObject { ["removed"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()), ["wires_removed"] = wires, ["node_count"] = engine.Graph.Nodes.Count };
        }

        JsonObject MacroMap(JsonObject a)
        {
            if (!TryInt(a["macro"], out int macro1) || macro1 < 1 || macro1 > 16) throw new PatchAgentError("macro is 1 to 16 (Macro 1 to Macro 16).");
            int macro = macro1 - 1;
            string label = a["label"]?.ToString();
            bool unmap = a["unmap"] is JsonValue uv && uv.TryGetValue(out bool ub) && ub;
            if (a["node"] == null && !unmap)
            {
                if (label == null) throw new PatchAgentError("Give node and control to map, or label to name the macro, or unmap with node and control.");
                Step("Name macro " + macro1, () => model.SetMacroLabel(macro, label));
                return new JsonObject { ["macro"] = macro1, ["label"] = PatchEditModel.MacroName(engine.Graph, macro) };
            }
            var node = ResolveNode(a["node"]);
            int control = ResolveControl(node, a["control"]);
            var info = node.Info.Controls[control];
            if (unmap)
            {
                if (!engine.Graph.Macros.Any(m => m.NodeId == node.Id && m.Control == control)) throw new PatchAgentError($"{node.Info.Name} {node.Id} {info.Name} is not mapped to a macro.");
                Step("Unmap macro", () => model.UnmapControl(node.Id, control));
                return new JsonObject { ["unmapped"] = $"{node.Info.Name} {node.Id} {info.Name}" };
            }
            if (info.Choices != null) throw new PatchAgentError($"{node.Info.Name} {node.Id} {info.Name} picks one of {string.Join(", ", info.Choices)}; a macro sweeps numbers, so map a number control.");
            float? low = a["low"] != null ? ResolveValue(node, control, a["low"]) : null, high = a["high"] != null ? ResolveValue(node, control, a["high"]) : null;
            Step("Map macro " + macro1, () =>
            {
                model.MapMacro(macro, node.Id, control);
                if ((low != null || high != null) && model.MappingOf(node.Id, control) is { } m)
                    model.SetMapping(node.Id, control, low ?? m.Min, high ?? m.Max);
                if (label != null) model.SetMacroLabel(macro, label);
                return 0;
            });
            var mapping = model.MappingOf(node.Id, control);
            return new JsonObject
            {
                ["macro"] = macro1, ["mapped"] = mapping is { } mm ? PatchEditModel.MappingText(engine.Graph, mm) : "",
                ["low"] = mapping?.Min, ["high"] = mapping?.Max, ["label"] = PatchEditModel.MacroName(engine.Graph, macro),
            };
        }

        JsonObject LoadTemplate(JsonObject a)
        {
            string name = a["template"]?.ToString();
            int i = PatchLessons.Find(name);
            if (i < 0)
                throw new PatchAgentError((string.IsNullOrWhiteSpace(name) ? "Give the template's name. " : $"There is no template \"{name}\". ") + "The templates are " +
                    string.Join(", ", PatchLessons.Slugs.Select((s, k) => s + " (" + PatchLessons.Titles[k] + ")")) + ".");
            var g = PatchLessons.Build(i);
            if (!Step("Load template " + PatchLessons.Titles[i], () => model.LoadGraph(g, "Load template " + PatchLessons.Titles[i])))
                throw new PatchAgentError(model.Notice ?? "The template could not be loaded.");
            return new JsonObject
            {
                ["template"] = PatchLessons.Slugs[i], ["title"] = PatchLessons.Titles[i], ["summary"] = PatchLessons.Summaries[i],
                ["node_count"] = engine.Graph.Nodes.Count, ["wire_count"] = engine.Graph.Wires.Count,
                ["note"] = i == 0 ? "The patch is silent until you make three wires: Keys Pitch to Osc Pitch, Osc Out to VCA In, VCA Out to Out In 1 (patch_connect)." : "Play notes to hear it; patch_describe lists the nodes and the macros.",
            };
        }

        JsonObject Rename(JsonObject a)
        {
            string name = a["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name)) throw new PatchAgentError("Give the patch's name.");
            Step("Rename patch", () => model.Rename(name));
            return new JsonObject { ["name"] = engine.Graph.Name };
        }
    }
}
