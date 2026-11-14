#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.4): the compiler. It orders the graph into block jobs, decides every node's scope
// (per voice or global), allocates every buffer and every voice's state, and drops what can't be heard (nodes that don't lead to an
// Out). A cycle is refused unless it goes through a Feedback node, which is two jobs: Read (first: it outputs the last block's input)
// and Write (after its source). A node that was in the patch being replaced (the same id and kind) takes over its state, so an edit
// while playing doesn't restart envelopes, phases, filters or delay lines. The result is immutable apart from controls and state;
// the audio thread only runs it.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    /// <summary>One macro's mapping resolved to the node it drives.</summary>
    internal readonly struct ResolvedMacro
    {
        public readonly int Macro, Control;
        public readonly Node Node;
        public readonly float Min, Max;
        public ResolvedMacro(int macro, Node node, int control, float min, float max) { Macro = macro; Node = node; Control = control; Min = min; Max = max; }
    }

    internal sealed class CompiledPatch
    {
        public Job[] Jobs;
        public int Nodes, Buffers;
        public string Order;
        /// <summary>Whether any node runs in the global view (it runs even when no voice sounds).</summary>
        public bool HasGlobal;
        public ResolvedMacro[] MacroMaps;
        /// <summary>Jobs by node and half (id × 4 + role), for the next patch to take state from.</summary>
        public Dictionary<int, Job> ByKey;
        /// <summary>How many nodes took over their state from the patch before.</summary>
        public int Adopted;
        /// <summary>The nodes that lead to an Out (the others are not heard), each with whether it runs once (global) or for each voice. The editor draws from it.</summary>
        public Dictionary<int, bool> Scope;
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

        public static CompileResult Compile(PatchGraph g, PatchContext ctx, CompiledPatch old = null)
        {
            if (!TryOrder(g, out var order, out var error)) return new CompileResult(null, error);
            var jobs = new List<Job>();
            var byKey = new Dictionary<int, Job>();
            var outBuf = new Dictionary<(int, int), PortBuffer>();       // (node, output port) → its buffer
            var scope = new Dictionary<int, bool>();                     // node → global
            var sums = new Dictionary<PortBuffer, PortBuffer>();         // per-voice buffer → its voice sum
            var fb = new Dictionary<int, FeedbackReadJob>();
            int buffers = 0, adopted = 0;
            bool hasGlobal = false;
            PortBuffer NewBuf(bool global) { buffers++; return new PortBuffer(!global); }

            Job Add(Job j, Node n, bool global, int role = 0)
            {
                j.NodeId = n.Id;
                j.Global = global;
                if (global) hasGlobal = true;
                int key = n.Id * 4 + role;
                if (old != null && old.ByKey != null && old.ByKey.TryGetValue(key, out var oj) && oj.GetType() == j.GetType() && j.Adopt(oj)) { j.Started = true; adopted++; }
                byKey[key] = j;
                jobs.Add(j);
                return j;
            }

            // Where a node runs: its own setting, else global when everything wired into it is.
            bool ScopeOf(Node n, Role role)
            {
                if (n.Kind == NodeKind.Feedback) return n.GlobalSetting;
                if (n.GlobalSetting) return true;
                if (n.Info.ForcedPerVoice) return false;
                bool any = false, allGlobal = true;
                foreach (var w in g.Wires)
                {
                    if (w.ToNode != n.Id || !scope.TryGetValue(w.FromNode, out bool gl)) continue;
                    any = true;
                    if (!gl) allGlobal = false;
                }
                return any && allGlobal;
            }

            // A per-voice signal that reaches a global node is summed over the voices, once for each source.
            PortBuffer GlobalOf(PortBuffer src)
            {
                if (!src.PerVoice) return src;
                if (!sums.TryGetValue(src, out var dest))
                {
                    dest = NewBuf(true);
                    sums[src] = dest;
                    var sj = new SumJob { Src = src, Dest = dest, NodeId = -1 };
                    jobs.Add(sj);
                }
                return dest;
            }

            IEnumerable<Wire> WiresTo(Node n, int port) =>
                g.Wires.Where(w => w.ToNode == n.Id && w.ToPort == port && outBuf.ContainsKey((w.FromNode, w.FromPort))).OrderBy(w => w.FromNode).ThenBy(w => w.FromPort);

            // A node's input: nothing, or the wires' sum read where the node uses it.
            Inp Input(Node n, int port, bool global)
            {
                var list = new List<(PortBuffer, float)>();
                foreach (var w in WiresTo(n, port))
                {
                    var src = outBuf[(w.FromNode, w.FromPort)];
                    list.Add((global ? GlobalOf(src) : src, w.Amount));
                }
                return list.Count == 0 ? null : new Inp(list, !global);
            }

            bool Wired(Node n, int port) => g.Wires.Any(w => w.FromNode == n.Id && w.FromPort == port);

            foreach (var (n, roleInt) in order)
            {
                var role = (Role)roleInt;
                bool global = n.Kind == NodeKind.Out ? false : ScopeOf(n, role);
                scope[n.Id] = n.Kind == NodeKind.Out || global;      // Out runs once for the whole patch
                float[] ctl = n.Controls;
                switch (n.Kind)
                {
                    case NodeKind.Keys:
                    {
                        var j = new KeysJob();
                        if (Wired(n, 0)) outBuf[(n.Id, 0)] = j.Pitch = NewBuf(false);
                        if (Wired(n, 1)) outBuf[(n.Id, 1)] = j.Gate = NewBuf(false);
                        if (Wired(n, 2)) outBuf[(n.Id, 2)] = j.Vel = NewBuf(false);
                        if (Wired(n, 3)) outBuf[(n.Id, 3)] = j.Press = NewBuf(false);
                        if (Wired(n, 4)) outBuf[(n.Id, 4)] = j.Slide = NewBuf(false);
                        Add(j, n, false);
                        break;
                    }
                    case NodeKind.GlobalKeys:
                    {
                        var j = new GlobalKeysJob();
                        if (Wired(n, 0)) outBuf[(n.Id, 0)] = j.Pitch = NewBuf(true);
                        if (Wired(n, 1)) outBuf[(n.Id, 1)] = j.Gate = NewBuf(true);
                        if (Wired(n, 2)) outBuf[(n.Id, 2)] = j.Vel = NewBuf(true);
                        Add(j, n, true);
                        break;
                    }
                    case NodeKind.Osc:
                    case NodeKind.WavetableOsc:
                    {
                        var j = new OscJob
                        {
                            Ctl = ctl, Wt = n.Kind == NodeKind.WavetableOsc,
                            Pitch = Input(n, NodeInfo.OscInPitch, global), Fm = Input(n, NodeInfo.OscInFm, global), Pm = Input(n, NodeInfo.OscInPm, global),
                            Sync = Input(n, NodeInfo.OscInSync, global), Aux = Input(n, NodeInfo.OscInPulse, global),
                        };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        // The chosen table is built here, off the audio thread, so the first block can read it.
                        if (j.Wt) TableBank.GetNow(Math.Clamp((int)(ctl[NodeInfo.WtTable] + 0.5f), 0, TableBank.Count - 1));
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Noise:
                    {
                        var j = new NoiseJob { Ctl = ctl };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Sample:
                    {
                        var j = new SampleJob { Ctl = ctl, Pitch = Input(n, NodeInfo.SampleInPitch, global), Gate = Input(n, NodeInfo.SampleInGate, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        int src = (int)(ctl[NodeInfo.SampleSource] + 0.5f);
                        if (src < SampleSources.Count) SampleSources.GetNow(src);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Envelope:
                    {
                        var j = new EnvJob { Ctl = ctl, Gate = Input(n, NodeInfo.EnvInGate, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Lfo:
                    {
                        var j = new LfoJob { Ctl = ctl, Rate = Input(n, NodeInfo.LfoInRate, global), Reset = Input(n, NodeInfo.LfoInReset, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Random:
                    {
                        var j = new RandomJob { Ctl = ctl, Trigger = Input(n, 0, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Slew:
                    {
                        var j = new SlewJob { Ctl = ctl, In = Input(n, 0, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.SampleHold:
                    {
                        var j = new SampleHoldJob { Ctl = ctl, In = Input(n, 0, global), Trigger = Input(n, 1, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Macro:
                    {
                        var j = new MacroJob { Ctl = ctl };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Filter:
                    {
                        var j = new FilterJob { Ctl = ctl, In = Input(n, NodeInfo.FilterInAudio, global), Cutoff = Input(n, NodeInfo.FilterInCutoff, global), Reso = Input(n, NodeInfo.FilterInReso, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Shaper:
                    {
                        var j = new ShaperJob { Ctl = ctl, In = Input(n, NodeInfo.ShaperInAudio, global), Drive = Input(n, NodeInfo.ShaperInDrive, global), Pitch = Input(n, NodeInfo.ShaperInPitch, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Delay:
                    {
                        var j = DelayJob.Make(ctx.SampleRate, global ? 1 : ctx.VoiceSlots);
                        j.Ctl = ctl; j.In = Input(n, NodeInfo.DelayInAudio, global); j.Time = Input(n, NodeInfo.DelayInTime, global);
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Vca:
                    {
                        var j = new VcaJob { Ctl = ctl, In = Input(n, NodeInfo.VcaInAudio, global), Cv = Input(n, NodeInfo.VcaInCv, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Mixer:
                    {
                        var j = new MixerJob { Ctl = ctl };
                        for (int k = 0; k < 4; k++) j.In[k] = Input(n, k, global);
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Math:
                    {
                        var j = new MathJob { Ctl = ctl, A = Input(n, NodeInfo.MathInA, global), B = Input(n, NodeInfo.MathInB, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.Crossfade:
                    {
                        var j = new CrossfadeJob { Ctl = ctl, A = Input(n, NodeInfo.XfadeInA, global), B = Input(n, NodeInfo.XfadeInB, global), Mix = Input(n, NodeInfo.XfadeInMix, global) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                        Add(j, n, global);
                        break;
                    }
                    case NodeKind.VoiceSum:
                    {
                        var j = new PassJob { In = Input(n, 0, true) };
                        outBuf[(n.Id, 0)] = j.Out = NewBuf(true);
                        Add(j, n, true);
                        break;
                    }
                    case NodeKind.Out:
                    {
                        var j = new OutJob { Ctl = ctl };
                        for (int k = 0; k < 4; k++)
                        {
                            var v = new List<(PortBuffer, float)>();
                            var gl = new List<(PortBuffer, float)>();
                            foreach (var w in WiresTo(n, k))
                            {
                                var src = outBuf[(w.FromNode, w.FromPort)];
                                (src.PerVoice ? v : gl).Add((src, w.Amount));
                            }
                            j.InV[k] = v.Count == 0 ? null : new Inp(v, true);
                            j.InG[k] = gl.Count == 0 ? null : new Inp(gl, false);
                        }
                        Add(j, n, false);
                        break;
                    }
                    case NodeKind.Feedback:
                        if (role == Role.FbRead)
                        {
                            var j = new FeedbackReadJob { State = new FeedbackState(!global) };
                            outBuf[(n.Id, 0)] = j.Out = NewBuf(global);
                            buffers++;
                            Add(j, n, global, 1);
                            fb[n.Id] = j;
                        }
                        else
                        {
                            var rd = fb[n.Id];
                            // The Write half shares the Read half's state (which has taken over the old patch's, if it did).
                            Add(new FeedbackWriteJob { Ctl = ctl, State = rd.State, In = Input(n, 0, global) }, n, global, 2);
                        }
                        break;
                }
            }

            var maps = new List<ResolvedMacro>();
            foreach (var m in g.Macros)
            {
                var node = g.Find(m.NodeId);
                if (node != null && m.Control >= 0 && m.Control < node.Controls.Length) maps.Add(new ResolvedMacro(m.Macro, node, m.Control, m.Min, m.Max));
            }
            return new CompileResult(new CompiledPatch
            {
                Jobs = jobs.ToArray(),
                Nodes = order.Select(o => o.Node.Id).Distinct().Count(),
                Buffers = buffers,
                HasGlobal = hasGlobal,
                MacroMaps = maps.ToArray(),
                ByKey = byKey,
                Adopted = adopted,
                Scope = new Dictionary<int, bool>(scope),
                Order = string.Join(" ", order.Select(o => o.Node.Kind + "#" + o.Node.Id + (o.Role == 1 ? ".read" : o.Role == 2 ? ".write" : ""))),
            }, null);
        }
    }

    /// <summary>Voice sum node: its input's wires (already voice-summed where they were per-voice) into a global buffer.</summary>
    internal sealed class PassJob : Job
    {
        public Inp In;
        public PortBuffer Out;
        public override void Run(PatchContext c)
        {
            var o = Out.Data;
            for (int i = 0; i < PatchContext.Block; i++) o[i * PatchContext.MaxVoices] = In != null ? In.At(i, 0) : 0f;
        }
    }
}
