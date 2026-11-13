#nullable disable
// Neo Patch NP0 (docs/specs/neo-patch.md §4.2, §4.4): the graph model. Nodes with typed ports and controls, wires from an
// output port to an input port. The editor will be a view over this; the compiler (PatchCompiler.cs) turns it into block
// jobs. Port types are for colour, checks and defaults only: any output can feed any input (spec §2.3).

using System;
using System.Collections.Generic;

namespace NeoBuzz.Patch
{
    internal enum PortType { Audio, Control, Gate, Pitch }

    internal enum NodeKind { Keys, Osc, Envelope, Filter, Vca, Out, Feedback }

    internal sealed class PortInfo
    {
        public readonly string Name;
        public readonly PortType Type;
        public PortInfo(string name, PortType type) { Name = name; Type = type; }
    }

    internal sealed class ControlInfo
    {
        public readonly string Name, Unit;
        public readonly float Min, Max, Default;
        public ControlInfo(string name, float min, float max, float def, string unit = "") { Name = name; Min = min; Max = max; Default = def; Unit = unit; }
    }

    /// <summary>What a node kind has: its ports (inputs, outputs), its controls, and whether it runs per voice.</summary>
    internal sealed class NodeInfo
    {
        public readonly PortInfo[] Ins, Outs;
        public readonly ControlInfo[] Controls;
        /// <summary>Per voice by default (spec §4.3). Out is global; Feedback may be either.</summary>
        public readonly bool PerVoice;
        NodeInfo(bool perVoice, PortInfo[] ins, PortInfo[] outs, ControlInfo[] controls) { PerVoice = perVoice; Ins = ins; Outs = outs; Controls = controls; }

        // Control indices.
        public const int OscShape = 0, OscTune = 1, OscFine = 2, OscFm = 3, OscPulse = 4, OscLevel = 5, OscQuality = 6;
        public const int EnvA = 0, EnvD = 1, EnvS = 2, EnvR = 3;
        public const int FilterMode = 0, FilterCutoff = 1, FilterReso = 2, FilterMod = 3;
        public const int VcaGain = 0;
        public const int OutLevel = 0, OutPan = 1;
        public const int FeedbackGain = 0;
        // Input port indices.
        public const int OscInPitch = 0, OscInFm = 1;
        public const int EnvInGate = 0;
        public const int FilterInAudio = 0, FilterInCutoff = 1;
        public const int VcaInAudio = 0, VcaInCv = 1;

        static PortInfo P(string n, PortType t) => new PortInfo(n, t);

        static readonly NodeInfo[] table =
        {
            new NodeInfo(true, new PortInfo[0], new[] { P("Pitch", PortType.Pitch), P("Gate", PortType.Gate), P("Velocity", PortType.Control) }, new ControlInfo[0]),
            new NodeInfo(true, new[] { P("Pitch", PortType.Pitch), P("FM", PortType.Audio) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Shape", 0, 2, 0, "0 saw, 1 square, 2 sine"), new ControlInfo("Tune", -96, 96, 0, "semitones"), new ControlInfo("Fine", -100, 100, 0, "cents"),
                new ControlInfo("FM", 0, 8, 0, "index"), new ControlInfo("Pulse", 0.05f, 0.95f, 0.5f), new ControlInfo("Level", 0, 1, 1),
                new ControlInfo("Quality", 0, 1, 1, "0 two-point, 1 four-point band limiting"),
            }),
            new NodeInfo(true, new[] { P("Gate", PortType.Gate) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Attack", 0.0005f, 10, 0.005f, "s"), new ControlInfo("Decay", 0.001f, 10, 0.3f, "s"),
                new ControlInfo("Sustain", 0, 1, 0.7f), new ControlInfo("Release", 0.001f, 20, 0.3f, "s"),
            }),
            new NodeInfo(true, new[] { P("In", PortType.Audio), P("Cutoff", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Mode", 0, 2, 0, "0 low, 1 band, 2 high"), new ControlInfo("Cutoff", 20, 20000, 2000, "Hz"),
                new ControlInfo("Resonance", 0.5f, 20, 0.8f, "Q"), new ControlInfo("Cutoff mod", 0, 8, 4, "octaves per unit"),
            }),
            new NodeInfo(true, new[] { P("In", PortType.Audio), P("CV", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[] { new ControlInfo("Gain", 0, 2, 1) }),
            new NodeInfo(false, new[] { P("In", PortType.Audio) }, new PortInfo[0], new[] { new ControlInfo("Level", 0, 2, 1), new ControlInfo("Pan", -1, 1, 0) }),
            new NodeInfo(true, new[] { P("In", PortType.Audio) }, new[] { P("Out", PortType.Audio) }, new[] { new ControlInfo("Gain", 0, 2, 1) }),
        };

        public static NodeInfo For(NodeKind kind) => table[(int)kind];
    }

    internal sealed class Node
    {
        public readonly int Id;
        public readonly NodeKind Kind;
        /// <summary>The node's controls in their units (NodeInfo.Controls). Compiled jobs read this array live, so an edit is heard at the next block.</summary>
        public readonly float[] Controls;
        /// <summary>Feedback only: global instead of per voice.</summary>
        public bool Global;

        public Node(int id, NodeKind kind)
        {
            Id = id;
            Kind = kind;
            var info = NodeInfo.For(kind);
            Controls = new float[info.Controls.Length];
            for (int i = 0; i < Controls.Length; i++) Controls[i] = info.Controls[i].Default;
        }

        public NodeInfo Info => NodeInfo.For(Kind);
        public bool PerVoice => Kind == NodeKind.Feedback ? !Global : Info.PerVoice;

        public void Set(int control, float value)
        {
            var c = Info.Controls[control];
            float v = float.IsFinite(value) ? value : c.Default;
            Controls[control] = Math.Clamp(v, c.Min, c.Max);
        }
    }

    internal readonly struct Wire
    {
        public readonly int FromNode, FromPort, ToNode, ToPort;
        /// <summary>The wire's amount: the signal is scaled by it where the wire arrives.</summary>
        public readonly float Amount;
        public Wire(int fromNode, int fromPort, int toNode, int toPort, float amount) { FromNode = fromNode; FromPort = fromPort; ToNode = toNode; ToPort = toPort; Amount = amount; }
    }

    internal enum ConnectResult { Ok, BadPort, Cycle }

    internal sealed class PatchGraph
    {
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Wire> Wires = new List<Wire>();
        int nextId = 1;

        public Node Add(NodeKind kind)
        {
            var n = new Node(nextId++, kind);
            Nodes.Add(n);
            return n;
        }

        public Node Find(int id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }

        public void Remove(int id)
        {
            Nodes.RemoveAll(n => n.Id == id);
            Wires.RemoveAll(w => w.FromNode == id || w.ToNode == id);
        }

        public void Disconnect(int fromNode, int fromPort, int toNode, int toPort) =>
            Wires.RemoveAll(w => w.FromNode == fromNode && w.FromPort == fromPort && w.ToNode == toNode && w.ToPort == toPort);

        /// <summary>
        /// Wires an output to an input (several wires into one input add). A cycle that doesn't go through a Feedback node is
        /// refused (spec §2.4) and the graph is left as it was.
        /// </summary>
        public ConnectResult Connect(Node from, int fromPort, Node to, int toPort, float amount = 1f)
        {
            if (from == null || to == null || fromPort < 0 || fromPort >= from.Info.Outs.Length || toPort < 0 || toPort >= to.Info.Ins.Length) return ConnectResult.BadPort;
            Disconnect(from.Id, fromPort, to.Id, toPort);
            Wires.Add(new Wire(from.Id, fromPort, to.Id, toPort, amount));
            if (!PatchCompiler.TryOrder(this, out _, out _))
            {
                Wires.RemoveAt(Wires.Count - 1);
                return ConnectResult.Cycle;
            }
            return ConnectResult.Ok;
        }
    }
}
