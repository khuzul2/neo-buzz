#nullable disable
// Neo Patch NP2 (docs/specs/neo-patch.md §4.5): the inner canvas's model, with no toolkit in it. Every edit the editor makes goes through
// here, on the patch the engine runs:
//  - a structural edit (add, connect, delete, paste, a node's scope, a wire's amount, a macro mapping) works on a copy of the patch and hands the
//    copy to the engine, which compiles it off the audio thread and swaps it in at a block boundary (nodes with the same id keep their state);
//  - a control edit and a move change the live patch in place (a control is a plain float the audio thread reads at the next block);
//  - each edit, or each gesture (a drag), is one history step: the patch text before and after, handed to the sink (the song's undo stack
//    in the machine; a local stack without a song, which the tests use).
// A cycle that doesn't pass through a Feedback node is refused at connect time, and the model says why (Refusal).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NeoBuzz.Patch
{
    /// <summary>Where the editor's history steps go: the patch text before and after (the machine puts them on the song's undo stack).</summary>
    internal interface IPatchHistory
    {
        void Record(string name, string before, string after);
    }

    internal readonly record struct WireKey(int FromNode, int FromPort, int ToNode, int ToPort)
    {
        public static WireKey Of(Wire w) => new WireKey(w.FromNode, w.FromPort, w.ToNode, w.ToPort);
    }

    /// <summary>Why a connection was refused, and where: the nodes and wires of the loop, for the editor to show.</summary>
    internal sealed class Refusal
    {
        public string Message;
        public int[] Nodes = new int[0];
        public WireKey[] Wires = new WireKey[0];
        public WireKey Attempt;
        public long At;
    }

    internal readonly record struct ConnectOutcome(bool Ok, string Message);

    /// <summary>A control's value in its own units: the text the side panel shows and the scale a slider uses.</summary>
    internal static class ControlScale
    {
        static string N(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

        public static string Format(ControlInfo c, float v)
        {
            if (c.Choices != null)
            {
                int i = Math.Clamp((int)(v + 0.5f), 0, c.Choices.Length - 1);
                return c.Choices[i];
            }
            switch (c.Unit)
            {
                case "Hz": return v >= 1000 ? N(v / 1000, "0.##") + " kHz" : N(v, "0.#") + " Hz";
                case "s": return v < 1 ? N(v * 1000, "0.#") + " ms" : N(v, "0.##") + " s";
                case "ms": return v >= 1000 ? N(v / 1000, "0.##") + " s" : N(v, "0.##") + " ms";
                case "semitones": return N(v, "+0.##;-0.##;0") + " st";
                case "cents": return N(v, "+0.#;-0.#;0") + " ct";
                case "cycles": return N(v, "0.##") + " cycles";
                case "index": return "index " + N(v, "0.##");
                case "x": return N(v, "0.##") + "x";
                case "Q": return "Q " + N(v, "0.##");
                case "octaves per unit": return N(v, "0.##") + " oct";
            }
            if (c.Name == "Macro") return "Macro " + (int)(v + 0.5f);
            return c.Min < 0 ? N(v, "+0.00;-0.00;0.00") : N(v, "0.00");
        }

        /// <summary>A wide positive range (20 Hz to 20 kHz, 0.5 ms to 10 s) is felt in ratios: its slider is logarithmic.</summary>
        public static bool IsLog(ControlInfo c) => c.Choices == null && c.Min > 0 && c.Max / c.Min >= 50f;

        /// <summary>The slider position, 0 to 1, of a value.</summary>
        public static double ToUnit(ControlInfo c, float v)
        {
            if (c.Max <= c.Min) return 0;
            v = Math.Clamp(v, c.Min, c.Max);
            if (IsLog(c)) return NeoBuzz.Dsp.PortableMath.Log(v / c.Min) / NeoBuzz.Dsp.PortableMath.Log(c.Max / c.Min);
            return (v - c.Min) / (c.Max - c.Min);
        }

        public static float FromUnit(ControlInfo c, double t)
        {
            t = Math.Clamp(t, 0, 1);
            double v = IsLog(c) ? c.Min * NeoBuzz.Dsp.PortableMath.Pow(c.Max / c.Min, t) : c.Min + t * (c.Max - c.Min);
            if (c.Choices != null) v = Math.Round(v);
            return (float)Math.Clamp(v, c.Min, c.Max);
        }
    }

    internal sealed class PatchEditModel
    {
        /// <summary>The time source for how long a refusal stays up (tests set it).</summary>
        public Func<long> Clock = () => Environment.TickCount64;

        const float Grid = 8f;
        static string clipboard = "";

        readonly PatchEngine engine;
        readonly IPatchHistory history;
        readonly HashSet<int> selection = new HashSet<int>();
        readonly List<(string Name, string Before, string After)> undo = new List<(string, string, string)>(), redo = new List<(string, string, string)>();
        int depth;
        string gestureName, gestureBefore;

        public PatchEditModel(PatchEngine engine, IPatchHistory history = null)
        {
            this.engine = engine;
            this.history = history;
        }

        public PatchEngine Engine => engine;
        public PatchGraph Graph => engine.Graph;

        /// <summary>The nodes selected, and the wire (a wire is picked on its own), and the macro whose mappings the side panel lists (-1: none).</summary>
        public IReadOnlyCollection<int> Selection => selection;
        public WireKey? SelectedWire { get; private set; }
        public int SelectedMacro { get; private set; } = -1;
        /// <summary>The newest node selected (what the side panel shows when several are).</summary>
        public int Primary { get; private set; }

        /// <summary>The last refusal (a cycle), kept for the editor to show for a while.</summary>
        public Refusal LastRefusal { get; private set; }
        /// <summary>The last thing worth telling the user ("The patch is at its 128-node limit").</summary>
        public string Notice { get; private set; }
        public long NoticeAt { get; private set; }

        /// <summary>Raised when the patch or the selection changed under the editor.</summary>
        public event Action Changed;

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public static string ClipboardText => clipboard;

        // ------------------------------------------------------------------ selection

        public void Select(int id, bool add = false)
        {
            if (!add) selection.Clear();
            if (Graph.Find(id) != null)
            {
                if (add && selection.Contains(id)) { selection.Remove(id); if (Primary == id) Primary = selection.Count > 0 ? selection.Last() : 0; }
                else { selection.Add(id); Primary = id; }
            }
            SelectedWire = null;
            SelectedMacro = -1;
            Changed?.Invoke();
        }

        public void SelectMany(IEnumerable<int> ids, bool add = false)
        {
            if (!add) selection.Clear();
            foreach (int id in ids) if (Graph.Find(id) != null) { selection.Add(id); Primary = id; }
            SelectedWire = null;
            SelectedMacro = -1;
            Changed?.Invoke();
        }

        public void SelectAll() => SelectMany(Graph.Nodes.Select(n => n.Id));

        public void SelectWire(WireKey? w)
        {
            selection.Clear();
            SelectedWire = w;
            SelectedMacro = -1;
            Changed?.Invoke();
        }

        public void SelectMacro(int macro)
        {
            selection.Clear();
            SelectedWire = null;
            SelectedMacro = macro;
            Changed?.Invoke();
        }

        public void ClearSelection()
        {
            selection.Clear();
            SelectedWire = null;
            SelectedMacro = -1;
            Changed?.Invoke();
        }

        void Prune()
        {
            selection.RemoveWhere(id => Graph.Find(id) == null);
            if (Primary != 0 && !selection.Contains(Primary)) Primary = selection.Count > 0 ? selection.Last() : 0;
            if (SelectedWire is WireKey w && !Graph.Wires.Any(x => WireKey.Of(x).Equals(w))) SelectedWire = null;
        }

        // ------------------------------------------------------------------ history

        /// <summary>Starts a gesture (a drag): everything until <see cref="EndGesture"/> is one history step.</summary>
        public void BeginGesture(string name)
        {
            if (depth++ == 0) { gestureName = name; gestureBefore = engine.Text(); }
        }

        public void EndGesture()
        {
            if (depth == 0 || --depth > 0) return;
            Commit(gestureName, gestureBefore);
        }

        void Commit(string name, string before)
        {
            string after = engine.Text();
            Prune();
            if (before == after) { Changed?.Invoke(); return; }
            if (history != null) history.Record(name, before, after);
            else
            {
                undo.Add((name, before, after));
                redo.Clear();
            }
            Changed?.Invoke();
        }

        /// <summary>Runs an edit as one history step (or as part of the gesture it is in).</summary>
        bool Run(string name, Func<bool> edit)
        {
            if (depth > 0) return edit();
            string before = engine.Text();
            bool did = edit();
            if (did) Commit(name, before);
            return did;
        }

        /// <summary>Local undo, for a patch with no song history (the song's stack does it in the app).</summary>
        public bool Undo()
        {
            if (undo.Count == 0) return false;
            var s = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add(s);
            Apply(s.Before);
            return true;
        }

        public bool Redo()
        {
            if (redo.Count == 0) return false;
            var s = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add(s);
            Apply(s.After);
            return true;
        }

        void Apply(string text)
        {
            engine.SetPatch(text);
            Prune();
            Changed?.Invoke();
        }

        /// <summary>The patch was replaced from outside (an undo step, an agent, a preset): drop what no longer exists and redraw.</summary>
        public void Replaced()
        {
            Prune();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ copy of the patch to edit

        public static PatchGraph Clone(PatchGraph g) => PatchText.Load(PatchText.Save(g)) ?? new PatchGraph();

        /// <summary>Edits a copy of the patch and hands it to the engine (which compiles it off the audio thread); false (and nothing changed) when the edit refuses or the copy doesn't compile.</summary>
        bool Structural(Func<PatchGraph, bool> mutate)
        {
            var old = engine.Graph;
            var copy = Clone(old);
            if (!mutate(copy)) return false;
            string error = engine.SetGraph(copy);
            if (error != null)
            {
                engine.SetGraph(old);
                SayNotice("That edit was refused: " + error);
                return false;
            }
            return true;
        }

        void SayNotice(string text)
        {
            Notice = text;
            NoticeAt = Clock();
        }

        // ------------------------------------------------------------------ nodes

        /// <summary>Adds a node of a kind at a place (world units, snapped to the grid), selects it; null at the 128-node cap.</summary>
        public Node AddNode(NodeKind kind, float x, float y)
        {
            int newId = 0;
            bool did = Run("Add " + NodeInfo.For(kind).Name, () => Structural(g =>
            {
                var n = g.Add(kind);
                if (n == null) { SayNotice("The patch is at its " + NodeInfo.Cap + "-node limit."); return false; }
                n.X = Snap(x);
                n.Y = Snap(y);
                newId = n.Id;
                return true;
            }));
            if (!did) return null;
            Select(newId);
            return Graph.Find(newId);
        }

        static float Snap(float v) => (float)(Math.Round(v / Grid) * Grid);

        /// <summary>Deletes the selected nodes (with their wires and macro mappings), or the selected wire.</summary>
        public bool DeleteSelection()
        {
            if (selection.Count > 0)
            {
                var ids = selection.ToArray();
                string name = ids.Length == 1 ? "Delete " + Graph.Find(ids[0])?.Info.Name : "Delete " + ids.Length + " nodes";
                bool did = Run(name, () => Structural(g => { foreach (int id in ids) g.Remove(id); return true; }));
                if (did) { selection.Clear(); Primary = 0; Changed?.Invoke(); }
                return did;
            }
            if (SelectedWire is WireKey w) return Disconnect(w);
            return false;
        }

        public bool SetGlobal(int id, bool global)
        {
            var n = Graph.Find(id);
            if (n == null || n.Info.ForcedGlobal || n.Info.ForcedPerVoice || n.Global == global) return false;
            return Run("Make " + n.Info.Name + (global ? " global" : " per voice"), () => Structural(g => { g.Find(id).Global = global; return true; }));
        }

        // ------------------------------------------------------------------ wires

        /// <summary>
        /// Whether a wire from an output to an input would be accepted (the editor asks while a wire is dragged): ports exist, and no cycle
        /// outside a Feedback node. The reason is in the message.
        /// </summary>
        public ConnectOutcome CanConnect(int fromNode, int fromPort, int toNode, int toPort)
        {
            var from = Graph.Find(fromNode);
            var to = Graph.Find(toNode);
            if (from == null || to == null || fromPort < 0 || fromPort >= from.Info.Outs.Length || toPort < 0 || toPort >= to.Info.Ins.Length)
                return new ConnectOutcome(false, "That is not a port.");
            if (Graph.Wires.Any(w => w.FromNode == fromNode && w.FromPort == fromPort && w.ToNode == toNode && w.ToPort == toPort))
                return new ConnectOutcome(true, "Already connected.");
            // The compiler only orders nodes that lead to Out, so a loop among unheard nodes would pass it and surface later at a wire that is not the cause: refuse it here, whatever it is wired to.
            if (from == to || (from.Kind != NodeKind.Feedback && to.Kind != NodeKind.Feedback && LoopPath(Graph, fromNode, toNode).Wires.Length > 0))
                return new ConnectOutcome(false, CycleMessage(fromNode, toNode));
            var probe = Clone(Graph);
            var r = probe.Connect(probe.Find(fromNode), fromPort, probe.Find(toNode), toPort);
            if (r == ConnectResult.Ok) return new ConnectOutcome(true, "");
            return new ConnectOutcome(false, r == ConnectResult.Cycle ? CycleMessage(fromNode, toNode) : "That is not a port.");
        }

        /// <summary>Wires an output to an input. A cycle outside a Feedback node is refused, with the reason in <see cref="LastRefusal"/>.</summary>
        public ConnectOutcome Connect(int fromNode, int fromPort, int toNode, int toPort, float amount = 1f)
        {
            var check = CanConnect(fromNode, fromPort, toNode, toPort);
            if (!check.Ok)
            {
                if (check.Message.StartsWith("That is not")) return check;
                var (nodes, wires) = LoopPath(Graph, fromNode, toNode);
                LastRefusal = new Refusal
                {
                    Message = check.Message, Nodes = nodes, Wires = wires,
                    Attempt = new WireKey(fromNode, fromPort, toNode, toPort), At = Clock(),
                };
                Changed?.Invoke();
                return check;
            }
            if (check.Message == "Already connected.") return check;
            var f = Graph.Find(fromNode);
            var t = Graph.Find(toNode);
            bool did = Run("Connect " + f.Info.Name + " to " + t.Info.Name, () => Structural(g =>
                g.Connect(g.Find(fromNode), fromPort, g.Find(toNode), toPort, amount) == ConnectResult.Ok));
            if (did) { LastRefusal = null; SelectWire(new WireKey(fromNode, fromPort, toNode, toPort)); }
            return new ConnectOutcome(did, did ? "" : "The wire was refused.");
        }

        public bool Disconnect(WireKey w)
        {
            if (!Graph.Wires.Any(x => WireKey.Of(x).Equals(w))) return false;
            bool did = Run("Disconnect", () => Structural(g => { g.Disconnect(w.FromNode, w.FromPort, w.ToNode, w.ToPort); return true; }));
            if (did) { SelectedWire = null; Changed?.Invoke(); }
            return did;
        }

        /// <summary>Removes every wire of a node (its menu's "Disconnect all").</summary>
        public bool DisconnectNode(int id)
        {
            if (!Graph.Wires.Any(w => w.FromNode == id || w.ToNode == id)) return false;
            return Run("Disconnect " + Graph.Find(id)?.Info.Name, () => Structural(g => { g.Wires.RemoveAll(w => w.FromNode == id || w.ToNode == id); return true; }));
        }

        public bool SetWireAmount(WireKey key, float amount)
        {
            Wire? found = Graph.Wires.Where(x => WireKey.Of(x).Equals(key)).Select(x => (Wire?)x).FirstOrDefault();
            if (found == null || !float.IsFinite(amount)) return false;
            amount = Math.Clamp(amount, -4f, 4f);
            if (found.Value.Amount == amount) return false;
            return Run("Wire amount", () => Structural(g =>
            {
                g.Connect(g.Find(key.FromNode), key.FromPort, g.Find(key.ToNode), key.ToPort, amount);
                return true;
            }));
        }

        string Label(int id)
        {
            var n = Graph.Find(id);
            return n == null ? "?" : n.Info.Name + " " + n.Id;
        }

        /// <summary>The nodes and wires of the loop a wire from <paramref name="from"/> to <paramref name="to"/> would close: the path back from <paramref name="to"/> to <paramref name="from"/>, which can't go through a Feedback node.</summary>
        static (int[] Nodes, WireKey[] Wires) LoopPath(PatchGraph g, int from, int to)
        {
            if (from == to) return (new[] { from }, new WireKey[0]);
            var prev = new Dictionary<int, Wire>();
            var queue = new Queue<int>();
            queue.Enqueue(to);
            var seen = new HashSet<int> { to };
            while (queue.Count > 0 && !seen.Contains(from))
            {
                int at = queue.Dequeue();
                var node = g.Find(at);
                if (node != null && node.Kind == NodeKind.Feedback) continue;   // a Feedback node breaks the loop: nothing flows through it in the same block
                foreach (var w in g.Wires)
                {
                    if (w.FromNode != at || !seen.Add(w.ToNode)) continue;
                    prev[w.ToNode] = w;
                    queue.Enqueue(w.ToNode);
                }
            }
            if (!seen.Contains(from)) return (new[] { from, to }, new WireKey[0]);
            var nodes = new List<int> { from };
            var wires = new List<WireKey>();
            int cur = from;
            while (cur != to)
            {
                var w = prev[cur];
                wires.Add(WireKey.Of(w));
                nodes.Add(w.FromNode);
                cur = w.FromNode;
            }
            nodes.Reverse();
            return (nodes.ToArray(), wires.ToArray());
        }

        string CycleMessage(int from, int to)
        {
            if (from == to) return Label(from) + " cannot feed itself. Send the wire through a Feedback node, which hands the signal back one block later.";
            var (nodes, _) = LoopPath(Graph, from, to);
            string loop = string.Join(" to ", nodes.Select(Label)) + " to " + Label(nodes[0]);
            return "That wire would make a loop (" + loop + ") with no Feedback node in it, so each node would wait for the next. Put a Feedback node in the loop: it hands the signal back one block (64 samples) later.";
        }

        // ------------------------------------------------------------------ moving and controls (in place)

        /// <summary>Moves nodes by a distance in world units, snapped to the grid from where they were when the gesture began (call inside BeginGesture/EndGesture, with the offsets from the start).</summary>
        public void MoveNodes(IEnumerable<int> ids, IReadOnlyDictionary<int, (float X, float Y)> start, float dx, float dy)
        {
            foreach (int id in ids)
            {
                var n = Graph.Find(id);
                if (n == null || !start.TryGetValue(id, out var s)) continue;
                n.X = Snap(s.X + dx);
                n.Y = Snap(s.Y + dy);
            }
        }

        /// <summary>Lays the nodes out in columns by how far each is from a source (left to right, as the signal flows). One step unless <paramref name="record"/> is false.</summary>
        public bool AutoLayout(Func<Node, double> height, bool record = true)
        {
            var nodes = Graph.Nodes;
            if (nodes.Count == 0) return false;
            var depth = nodes.ToDictionary(n => n.Id, n => 0);
            for (int pass = 0; pass < nodes.Count; pass++)
            {
                bool changed = false;
                foreach (var w in Graph.Wires)
                {
                    var f = Graph.Find(w.FromNode);
                    if (f == null || f.Kind == NodeKind.Feedback || !depth.ContainsKey(w.ToNode)) continue;
                    if (depth[w.ToNode] < depth[w.FromNode] + 1) { depth[w.ToNode] = depth[w.FromNode] + 1; changed = true; }
                }
                if (!changed) break;
            }
            bool Do()
            {
                var y = new Dictionary<int, double>();
                foreach (var n in nodes.OrderBy(n => depth[n.Id]).ThenBy(n => n.Id))
                {
                    int d = depth[n.Id];
                    y.TryGetValue(d, out double top);
                    n.X = Snap(d * 208);
                    n.Y = Snap((float)top);
                    y[d] = top + height(n) + 24;
                }
                return true;
            }
            if (!record) return Do();
            return Run("Tidy the layout", Do);
        }

        /// <summary>Moves the selection by a distance (one step).</summary>
        public bool MoveBy(float dx, float dy)
        {
            var start = selection.Select(Graph.Find).Where(n => n != null).ToDictionary(n => n.Id, n => (n.X, n.Y));
            if (start.Count == 0) return false;
            return Run("Move", () => { MoveNodes(start.Keys, start, dx, dy); return true; });
        }

        /// <summary>Sets a node control in its own units; the audio thread reads it at the next block.</summary>
        public bool SetControl(int id, int control, float value)
        {
            var n = Graph.Find(id);
            if (n == null || control < 0 || control >= n.Controls.Length) return false;
            var info = n.Info.Controls[control];
            float v = float.IsFinite(value) ? Math.Clamp(value, info.Min, info.Max) : info.Default;
            if (v == n.Controls[control]) return false;
            string was = depth == 0 ? engine.Text() : null;
            n.Controls[control] = v;
            if (depth == 0) Commit(n.Info.Name + " " + info.Name, was);
            else Changed?.Invoke();
            return true;
        }

        public bool ResetControl(int id, int control)
        {
            var n = Graph.Find(id);
            return n != null && control >= 0 && control < n.Controls.Length && SetControl(id, control, n.Info.Controls[control].Default);
        }

        public bool Rename(string name)
        {
            name = (name ?? "").Trim();
            if (name == Graph.Name) return false;
            return Run("Rename patch", () => { Graph.Name = name; return true; });
        }

        // ------------------------------------------------------------------ copy, paste, duplicate

        /// <summary>Copies the selected nodes and the wires between them; the text is also kept for pasting into another patch. False when nothing is selected.</summary>
        public bool Copy()
        {
            if (selection.Count == 0) return false;
            var g = new PatchGraph();
            foreach (var n in Graph.Nodes.Where(n => selection.Contains(n.Id)))
            {
                var c = g.AddWithId(n.Id, n.Kind);
                c.Global = n.Global; c.X = n.X; c.Y = n.Y;
                Array.Copy(n.Controls, c.Controls, n.Controls.Length);
            }
            foreach (var w in Graph.Wires)
                if (selection.Contains(w.FromNode) && selection.Contains(w.ToNode)) g.Connect(g.Find(w.FromNode), w.FromPort, g.Find(w.ToNode), w.ToPort, w.Amount);
            clipboard = PatchText.Save(g);
            return true;
        }

        public bool CanPaste => !string.IsNullOrEmpty(clipboard);

        /// <summary>Pastes the copied nodes with their top-left corner at a place (new ids; the wires between them come with them); selects them. Returns how many.</summary>
        public int Paste(float x, float y)
        {
            var clip = PatchText.Load(clipboard);
            if (clip == null || clip.Nodes.Count == 0) return 0;
            if (Graph.Nodes.Count + clip.Nodes.Count > NodeInfo.Cap)
            {
                SayNotice("That would pass the " + NodeInfo.Cap + "-node limit.");
                return 0;
            }
            float minX = clip.Nodes.Min(n => n.X), minY = clip.Nodes.Min(n => n.Y);
            var added = new List<int>();
            bool did = Run(clip.Nodes.Count == 1 ? "Paste " + clip.Nodes[0].Info.Name : "Paste " + clip.Nodes.Count + " nodes", () => Structural(g =>
            {
                var map = new Dictionary<int, Node>();
                foreach (var n in clip.Nodes)
                {
                    var c = g.Add(n.Kind);
                    c.Global = n.Global;
                    c.X = Snap(x + n.X - minX);
                    c.Y = Snap(y + n.Y - minY);
                    Array.Copy(n.Controls, c.Controls, n.Controls.Length);
                    map[n.Id] = c;
                    added.Add(c.Id);
                }
                foreach (var w in clip.Wires) g.Connect(map[w.FromNode], w.FromPort, map[w.ToNode], w.ToPort, w.Amount);
                return true;
            }));
            if (!did) return 0;
            SelectMany(added);
            return added.Count;
        }

        /// <summary>Copies the selection and pastes it a little down and to the right.</summary>
        public int Duplicate()
        {
            if (!Copy()) return 0;
            var sel = selection.Select(Graph.Find).Where(n => n != null).ToList();
            return Paste(sel.Min(n => n.X) + 24, sel.Min(n => n.Y) + 24);
        }

        // ------------------------------------------------------------------ macros

        /// <summary>What the macro strip calls a macro: the user's label, else the first control it moves, else "Macro n".</summary>
        public static string MacroName(PatchGraph g, int macro)
        {
            if (!string.IsNullOrEmpty(g.MacroLabels[macro])) return g.MacroLabels[macro];
            foreach (var m in g.Macros)
            {
                if (m.Macro != macro) continue;
                var n = g.Find(m.NodeId);
                if (n != null) return n.Info.Controls[m.Control].Name;
            }
            return "Macro " + (macro + 1);
        }

        public static string MappingText(PatchGraph g, MacroMapping m)
        {
            var n = g.Find(m.NodeId);
            if (n == null) return "?";
            var c = n.Info.Controls[m.Control];
            return n.Info.Name + " " + n.Id + " · " + c.Name + "  " + ControlScale.Format(c, m.Min) + " to " + ControlScale.Format(c, m.Max);
        }

        /// <summary>Maps a macro to a node control: the control sweeps from where it is to the far end of its range. A control has one mapping.</summary>
        public bool MapMacro(int macro, int nodeId, int control)
        {
            var n = Graph.Find(nodeId);
            if (n == null || macro < 0 || macro > 15 || control < 0 || control >= n.Controls.Length) return false;
            var c = n.Info.Controls[control];
            float cur = n.Controls[control];
            float lo, hi;
            if (cur - c.Min <= c.Max - cur) { lo = cur; hi = c.Max; } else { lo = c.Min; hi = cur; }
            if (hi - lo < 1e-6f) { lo = c.Min; hi = c.Max; }
            return Run("Map macro " + (macro + 1), () => Structural(g => { g.MapMacro(macro, g.Find(nodeId), control, lo, hi); return true; }));
        }

        public bool UnmapControl(int nodeId, int control)
        {
            if (!Graph.Macros.Any(m => m.NodeId == nodeId && m.Control == control)) return false;
            return Run("Unmap macro", () => Structural(g => { g.Macros.RemoveAll(m => m.NodeId == nodeId && m.Control == control); return true; }));
        }

        /// <summary>Sets the low and high end of a mapping (control units).</summary>
        public bool SetMapping(int nodeId, int control, float min, float max)
        {
            var existing = MappingOf(nodeId, control);
            var info = Graph.Find(nodeId)?.Info.Controls[control];
            if (existing == null || info == null) return false;
            var old = existing.Value;
            min = Math.Clamp(min, info.Min, info.Max);
            max = Math.Clamp(max, info.Min, info.Max);
            return Run("Macro range", () => Structural(g => { g.MapMacro(old.Macro, g.Find(nodeId), control, min, max); return true; }));
        }

        public bool SetMacroLabel(int macro, string label)
        {
            if (macro < 0 || macro > 15) return false;
            label = (label ?? "").Trim().Replace(";", "").Replace("\n", " ");
            if (label.Length > 24) label = label.Substring(0, 24);
            if (label == Graph.MacroLabels[macro]) return false;
            return Run("Name macro " + (macro + 1), () => { Graph.MacroLabels[macro] = label; return true; });
        }

        public MacroMapping? MappingOf(int nodeId, int control)
        {
            foreach (var m in Graph.Macros) if (m.NodeId == nodeId && m.Control == control) return m;
            return null;
        }

        // ------------------------------------------------------------------ what the compiler says

        /// <summary>Whether a node leads to an Out (the others are not heard), by the last compile; true when there is no compile yet.</summary>
        public bool IsHeard(int id)
        {
            var scope = engine.Runtime.Current?.Scope;
            return scope == null || scope.ContainsKey(id);
        }

        /// <summary>Whether a node runs once (global) rather than for each voice, as the compiler resolved it (its own setting for a node that isn't heard).</summary>
        public bool IsGlobal(int id)
        {
            var scope = engine.Runtime.Current?.Scope;
            if (scope != null && scope.TryGetValue(id, out bool g)) return g;
            return Graph.Find(id)?.GlobalSetting ?? false;
        }
    }
}
