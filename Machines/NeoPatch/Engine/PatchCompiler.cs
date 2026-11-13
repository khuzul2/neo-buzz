#nullable disable
// Neo Patch NP0 (docs/specs/neo-patch.md §4.4): the compiler. It orders the graph into block jobs, allocates every buffer and
// every voice's state, and drops what can't be heard (nodes that don't lead to an Out). A cycle is refused unless it goes
// through a Feedback node, which is two jobs: Read (first: it outputs the last block's input) and Write (after its source).
// The result is immutable apart from controls and state; the audio thread only runs it.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    internal sealed class CompiledPatch
    {
        public Job[] Jobs;
        public int Nodes, Buffers;
        /// <summary>Set until the patch's first block has run: its voices start fresh (a swap restarts the sounding notes' state).</summary>
        public bool First = true;
        public string Order;
    }

    internal readonly struct CompileResult
    {
        public readonly CompiledPatch Patch;
        public readonly string Error;
        public CompileResult(CompiledPatch p, string e) { Patch = p; Error = e; }
    }

    internal static class PatchCompiler
    {
        enum Role { Normal, FbRead, FbWrite }

        readonly struct Vertex
        {
            public readonly Node Node;
            public readonly Role Role;
            public Vertex(Node n, Role r) { Node = n; Role = r; }
        }

        /// <summary>
        /// The jobs' order: dependencies first, ties by node id (so the order, and so the sums, never depend on how the graph was built).
        /// False with the nodes on the cycle when a cycle doesn't pass through a Feedback node. Only nodes that lead to an Out are kept.
        /// </summary>
        public static bool TryOrder(PatchGraph g, out List<(Node Node, int Role)> order, out string error)
        {
            order = new List<(Node, int)>();
            error = null;
            var live = Reachable(g);
            var verts = new List<Vertex>();
            foreach (var n in g.Nodes.OrderBy(n => n.Id))
            {
                if (!live.Contains(n.Id)) continue;
                if (n.Kind == NodeKind.Feedback) { verts.Add(new Vertex(n, Role.FbRead)); verts.Add(new Vertex(n, Role.FbWrite)); }
                else verts.Add(new Vertex(n, Role.Normal));
            }
            int Find(int nodeId, bool asSource)
            {
                for (int i = 0; i < verts.Count; i++)
                {
                    if (verts[i].Node.Id != nodeId) continue;
                    if (verts[i].Node.Kind != NodeKind.Feedback) return i;
                    if (asSource ? verts[i].Role == Role.FbRead : verts[i].Role == Role.FbWrite) return i;
                }
                return -1;
            }
            var deps = new List<int>[verts.Count];
            for (int i = 0; i < deps.Length; i++) deps[i] = new List<int>();
            foreach (var w in g.Wires)
            {
                int a = Find(w.FromNode, true), b = Find(w.ToNode, false);
                if (a >= 0 && b >= 0) deps[b].Add(a);
            }
            // A Feedback node's Write comes after its Read, so its input is a block late whatever else is ready.
            for (int i = 0; i < verts.Count; i++)
                if (verts[i].Role == Role.FbWrite) deps[i].Add(Find(verts[i].Node.Id, true));
            var done = new bool[verts.Count];
            for (int placed = 0; placed < verts.Count; placed++)
            {
                int pick = -1;
                for (int i = 0; i < verts.Count && pick < 0; i++)
                {
                    if (done[i]) continue;
                    bool ready = true;
                    foreach (int d in deps[i]) if (!done[d]) { ready = false; break; }
                    if (ready) pick = i;
                }
                if (pick < 0)
                {
                    error = "cycle without a Feedback node through nodes " + string.Join(", ", Enumerable.Range(0, verts.Count).Where(i => !done[i]).Select(i => verts[i].Node.Id).Distinct());
                    return false;
                }
                done[pick] = true;
                order.Add((verts[pick].Node, (int)verts[pick].Role));
            }
            return true;
        }

        static HashSet<int> Reachable(PatchGraph g)
        {
            var live = new HashSet<int>();
            var stack = new Stack<int>();
            foreach (var n in g.Nodes) if (n.Kind == NodeKind.Out) { live.Add(n.Id); stack.Push(n.Id); }
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                foreach (var w in g.Wires)
                    if (w.ToNode == id && live.Add(w.FromNode)) stack.Push(w.FromNode);
            }
            return live;
        }

        public static CompileResult Compile(PatchGraph g, PatchContext ctx)
        {
            if (!TryOrder(g, out var order, out var error)) return new CompileResult(null, error);
            var jobs = new List<Job>();
            var outBuf = new Dictionary<(int, int), PortBuffer>();       // (node, output port) → its buffer
            var fb = new Dictionary<int, FeedbackState>();
            int buffers = 0;
            PortBuffer NewBuf(bool perVoice) { buffers++; return new PortBuffer(perVoice); }

            // A node's input: nothing, the source's own buffer (one wire, amount 1, same scope), or a mix job's.
            PortBuffer Input(Node n, int port)
            {
                var wires = g.Wires.Where(w => w.ToNode == n.Id && w.ToPort == port && outBuf.ContainsKey((w.FromNode, w.FromPort))).ToList();
                if (wires.Count == 0) return null;
                bool perVoice = n.PerVoice;
                if (wires.Count == 1 && wires[0].Amount == 1f)
                {
                    var src = outBuf[(wires[0].FromNode, wires[0].FromPort)];
                    if (src.PerVoice == perVoice) return src;
                }
                var dest = NewBuf(perVoice);
                jobs.Add(new MixJob { NodeId = n.Id, Dest = dest, Inputs = wires.Select(w => new MixInput(outBuf[(w.FromNode, w.FromPort)], w.Amount)).ToArray() });
                return dest;
            }

            bool Wired(Node n, int port) => g.Wires.Any(w => w.FromNode == n.Id && w.FromPort == port);

            foreach (var (n, roleInt) in order)
            {
                var role = (Role)roleInt;
                switch (n.Kind)
                {
                    case NodeKind.Keys:
                    {
                        var j = new KeysJob { NodeId = n.Id };
                        if (Wired(n, 0)) outBuf[(n.Id, 0)] = j.Pitch = NewBuf(true);
                        if (Wired(n, 1)) outBuf[(n.Id, 1)] = j.Gate = NewBuf(true);
                        if (Wired(n, 2)) outBuf[(n.Id, 2)] = j.Vel = NewBuf(true);
                        jobs.Add(j);
                        break;
                    }
                    case NodeKind.Osc:
                    {
                        var j = new OscJob { NodeId = n.Id, Ctl = n.Controls, Pitch = Input(n, NodeInfo.OscInPitch), Fm = Input(n, NodeInfo.OscInFm) };
                        for (int v = 0; v < PatchContext.MaxVoices; v++) j.LastNote[v] = float.NaN;
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(true);
                        jobs.Add(j);
                        break;
                    }
                    case NodeKind.Envelope:
                    {
                        var j = new EnvJob { NodeId = n.Id, Ctl = n.Controls, Gate = Input(n, NodeInfo.EnvInGate) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(true);
                        jobs.Add(j);
                        break;
                    }
                    case NodeKind.Filter:
                    {
                        var j = new FilterJob { NodeId = n.Id, Ctl = n.Controls, In = Input(n, NodeInfo.FilterInAudio), Cutoff = Input(n, NodeInfo.FilterInCutoff) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(true);
                        jobs.Add(j);
                        break;
                    }
                    case NodeKind.Vca:
                    {
                        var j = new VcaJob { NodeId = n.Id, Ctl = n.Controls, In = Input(n, NodeInfo.VcaInAudio), Cv = Input(n, NodeInfo.VcaInCv) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(true);
                        jobs.Add(j);
                        break;
                    }
                    case NodeKind.Out:
                        jobs.Add(new OutJob { NodeId = n.Id, Ctl = n.Controls, In = Input(n, 0) });
                        break;
                    case NodeKind.Feedback:
                        if (role == Role.FbRead)
                        {
                            var st = fb[n.Id] = new FeedbackState(n.PerVoice);
                            buffers++;
                            var o = NewBuf(n.PerVoice);
                            outBuf[(n.Id, 0)] = o;
                            jobs.Add(new FeedbackReadJob { NodeId = n.Id, State = st, Out = o });
                        }
                        else jobs.Add(new FeedbackWriteJob { NodeId = n.Id, Ctl = n.Controls, State = fb[n.Id], In = Input(n, 0) });
                        break;
                }
            }
            return new CompileResult(new CompiledPatch
            {
                Jobs = jobs.ToArray(),
                Nodes = order.Select(o => o.Node.Id).Distinct().Count(),
                Buffers = buffers,
                Order = string.Join(" ", order.Select(o => o.Node.Kind + "#" + o.Node.Id + (o.Role == 1 ? ".read" : o.Role == 2 ? ".write" : ""))),
            }, null);
        }
    }
}
