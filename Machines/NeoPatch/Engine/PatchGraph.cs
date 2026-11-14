#nullable disable
// Neo Patch (docs/specs/neo-patch.md §4.2, §4.3; decisions in docs/specs/neo-patch-engine.md): the graph model. Nodes with typed
// ports and controls, wires from an output port to an input port, and the macro mappings. The editor (NP2) is a view over this;
// the compiler (PatchCompiler.cs) turns it into block jobs. Port types are for colour, checks and defaults only: any output can
// feed any input (spec §2.3). Nodes are saved by kind name, so the enum may grow; controls and ports are saved by index, so each
// node's lists only ever grow at the end.

using System;
using System.Collections.Generic;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    internal enum PortType { Audio, Control, Gate, Pitch }

    internal enum NodeKind
    {
        Keys, Osc, Envelope, Filter, Vca, Out, Feedback,
        GlobalKeys, WavetableOsc, Noise, Sample, Lfo, Slew, SampleHold, Random, Shaper, Delay, Mixer, Math, Crossfade, Macro, VoiceSum,
    }

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
        /// <summary>The names of a control that picks one of a few (value 0, 1, …); null for a number.</summary>
        public readonly string[] Choices;
        public ControlInfo(string name, float min, float max, float def, string unit = "", string[] choices = null) { Name = name; Min = min; Max = max; Default = def; Unit = unit; Choices = choices; }
    }

    /// <summary>What a node kind has: its ports (inputs, outputs), its controls, and where it runs.</summary>
    internal sealed class NodeInfo
    {
        public readonly string Name;
        public readonly PortInfo[] Ins, Outs;
        public readonly ControlInfo[] Controls;
        /// <summary>Runs once for each voice unless its inputs are all global or it is switched to global (spec §4.3).</summary>
        public readonly bool PerVoiceDefault;
        /// <summary>Always global (Global Keys, Voice sum), or always per voice (Keys).</summary>
        public readonly bool ForcedGlobal, ForcedPerVoice;
        /// <summary>The group the editor colours it by.</summary>
        public readonly string Group;

        NodeInfo(string name, string group, bool perVoice, bool forcedGlobal, bool forcedPerVoice, PortInfo[] ins, PortInfo[] outs, ControlInfo[] controls)
        {
            Name = name; Group = group; PerVoiceDefault = perVoice; ForcedGlobal = forcedGlobal; ForcedPerVoice = forcedPerVoice; Ins = ins; Outs = outs; Controls = controls;
        }

        // Control indices.
        public const int OscShape = 0, OscTune = 1, OscFine = 2, OscFm = 3, OscPulse = 4, OscLevel = 5, OscDrift = 6, OscPm = 7;
        public const int WtTable = 0, WtTune = 1, WtFine = 2, WtFm = 3, WtPosition = 4, WtLevel = 5, WtDrift = 6, WtPm = 7;
        public const int EnvA = 0, EnvD = 1, EnvS = 2, EnvR = 3, EnvDelay = 4, EnvHold = 5, EnvCurve = 6;
        public const int FilterMode = 0, FilterCutoff = 1, FilterReso = 2, FilterMod = 3;
        public const int VcaGain = 0;
        public const int OutLevel = 0, OutPan = 1, OutSpread = 5;
        public const int FeedbackGain = 0;
        public const int NoiseColor = 0, NoiseLevel = 1;
        public const int SampleSource = 0, SampleTune = 1, SampleLevel = 2, SampleLoop = 3, SampleStart = 4, SampleFollow = 5;
        public const int LfoShape = 0, LfoRate = 1, LfoDepth = 2, LfoOffset = 3, LfoPhase = 4, LfoRetrig = 5, LfoRateMod = 6;
        public const int SlewRise = 0, SlewFall = 1;
        public const int ShMode = 0, ShRate = 1;
        public const int RandRate = 0, RandSmooth = 1, RandDepth = 2, RandOffset = 3;
        public const int ShaperMode = 0, ShaperDrive = 1, ShaperBias = 2, ShaperStages = 3, ShaperLevel = 4;
        public const int DelayTime = 0, DelayFeedback = 1, DelayDamping = 2, DelayMix = 3, DelayTimeMod = 4;
        public const int MixGain = 0;
        public const int MathOp = 0, MathScale = 1, MathOffset = 2;
        public const int XfadeMix = 0, XfadeLaw = 1;
        public const int MacroIndex = 0, MacroAmount = 1;

        // Input port indices.
        public const int OscInPitch = 0, OscInFm = 1, OscInPm = 2, OscInSync = 3, OscInPulse = 4;
        public const int EnvInGate = 0;
        public const int FilterInAudio = 0, FilterInCutoff = 1, FilterInReso = 2;
        public const int VcaInAudio = 0, VcaInCv = 1;
        public const int SampleInPitch = 0, SampleInGate = 1;
        public const int LfoInRate = 0, LfoInReset = 1;
        public const int ShaperInAudio = 0, ShaperInDrive = 1, ShaperInPitch = 2;
        public const int DelayInAudio = 0, DelayInTime = 1;
        public const int MathInA = 0, MathInB = 1;
        public const int XfadeInA = 0, XfadeInB = 1, XfadeInMix = 2;

        public const int Cap = 128;

        static PortInfo P(string n, PortType t) => new PortInfo(n, t);
        static readonly PortInfo[] None = new PortInfo[0];
        static readonly ControlInfo[] NoControls = new ControlInfo[0];
        static ControlInfo[] Gains(int n, float def) { var c = new ControlInfo[n]; for (int i = 0; i < n; i++) c[i] = new ControlInfo("Gain " + (i + 1), 0, 2, def); return c; }

        static readonly string[] Shapes = { "saw", "square", "sine", "triangle" };
        static readonly string[] FilterModes = { "low-pass 12", "band-pass", "high-pass", "notch", "ladder 24", "ladder 12" };
        static readonly string[] LfoShapes = { "sine", "triangle", "saw up", "saw down", "square", "smooth random", "sample and hold" };
        static readonly string[] ShaperModes = { "soft clip", "fold", "hard clip" };
        static readonly string[] MathOps = { "add", "multiply", "scale and offset", "min", "max", "abs", "invert" };
        static readonly string[] Off1 = { "off", "on" };

        static readonly NodeInfo[] table =
        {
            // Keys
            new NodeInfo("Keys", "Notes", true, false, true, None, new[] { P("Pitch", PortType.Pitch), P("Gate", PortType.Gate), P("Velocity", PortType.Control), P("Pressure", PortType.Control), P("Slide", PortType.Control) }, NoControls),
            // Osc
            new NodeInfo("Osc", "Sources", true, false, false, new[] { P("Pitch", PortType.Pitch), P("FM", PortType.Audio), P("PM", PortType.Audio), P("Sync", PortType.Audio), P("Pulse", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Shape", 0, 3, 0, "", Shapes), new ControlInfo("Tune", -96, 96, 0, "semitones"), new ControlInfo("Fine", -100, 100, 0, "cents"),
                new ControlInfo("FM", 0, 8, 0, "index"), new ControlInfo("Pulse", 0.05f, 0.95f, 0.5f), new ControlInfo("Level", 0, 1, 1),
                new ControlInfo("Drift", 0, 1, 0), new ControlInfo("PM", 0, 4, 0, "cycles"),
            }),
            // Envelope
            new NodeInfo("Envelope", "Modulation", true, false, false, new[] { P("Gate", PortType.Gate) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Attack", 0.0005f, 10, 0.005f, "s"), new ControlInfo("Decay", 0.001f, 10, 0.3f, "s"),
                new ControlInfo("Sustain", 0, 1, 0.7f), new ControlInfo("Release", 0.001f, 20, 0.3f, "s"),
                new ControlInfo("Delay", 0, 5, 0, "s"), new ControlInfo("Hold", 0, 5, 0, "s"), new ControlInfo("Curve", -1, 1, 0),
            }),
            // Filter
            new NodeInfo("Filter", "Tone", true, false, false, new[] { P("In", PortType.Audio), P("Cutoff", PortType.Control), P("Resonance", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Mode", 0, 5, 0, "", FilterModes), new ControlInfo("Cutoff", 20, 20000, 2000, "Hz"),
                new ControlInfo("Resonance", 0.5f, 20, 0.8f, "Q"), new ControlInfo("Cutoff mod", 0, 8, 4, "octaves per unit"),
            }),
            // Vca
            new NodeInfo("VCA", "Plumbing", true, false, false, new[] { P("In", PortType.Audio), P("CV", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[] { new ControlInfo("Gain", 0, 2, 1) }),
            // Out
            new NodeInfo("Out", "Plumbing", false, true, false, new[] { P("In 1", PortType.Audio), P("In 2", PortType.Audio), P("In 3", PortType.Audio), P("In 4", PortType.Audio) }, None, new[]
            {
                new ControlInfo("Level", 0, 2, 1), new ControlInfo("Pan 1", -1, 1, 0), new ControlInfo("Pan 2", -1, 1, 0), new ControlInfo("Pan 3", -1, 1, 0), new ControlInfo("Pan 4", -1, 1, 0),
                new ControlInfo("Spread", 0, 1, 0),
            }),
            // Feedback
            new NodeInfo("Feedback", "Plumbing", true, false, false, new[] { P("In", PortType.Audio) }, new[] { P("Out", PortType.Audio) }, new[] { new ControlInfo("Gain", 0, 2, 1) }),
            // GlobalKeys
            new NodeInfo("Global Keys", "Notes", false, true, false, None, new[] { P("Pitch", PortType.Pitch), P("Gate", PortType.Gate), P("Velocity", PortType.Control) }, NoControls),
            // WavetableOsc
            new NodeInfo("Wavetable Osc", "Sources", true, false, false, new[] { P("Pitch", PortType.Pitch), P("FM", PortType.Audio), P("PM", PortType.Audio), P("Sync", PortType.Audio), P("Position", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Table", 0, TableBank.Count - 1, 0, "", TableBank.Names), new ControlInfo("Tune", -96, 96, 0, "semitones"), new ControlInfo("Fine", -100, 100, 0, "cents"),
                new ControlInfo("FM", 0, 8, 0, "index"), new ControlInfo("Position", 0, 1, 0), new ControlInfo("Level", 0, 1, 1),
                new ControlInfo("Drift", 0, 1, 0), new ControlInfo("PM", 0, 4, 0, "cycles"),
            }),
            // Noise
            new NodeInfo("Noise", "Sources", true, false, false, None, new[] { P("Out", PortType.Audio) }, new[] { new ControlInfo("Color", 0, 1, 0, "", new[] { "white", "pink" }), new ControlInfo("Level", 0, 1, 1) }),
            // Sample
            new NodeInfo("Sample", "Sources", true, false, false, new[] { P("Pitch", PortType.Pitch), P("Gate", PortType.Gate) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Source", 0, SampleSources.Count, 0, "", SourceNames()), new ControlInfo("Tune", -96, 96, 0, "semitones"), new ControlInfo("Level", 0, 1, 1),
                new ControlInfo("Loop", 0, 1, 0, "", new[] { "one-shot", "loop" }), new ControlInfo("Start", 0, 1, 0), new ControlInfo("Follow", 0, 1, 1, "", Off1),
            }),
            // Lfo
            new NodeInfo("LFO", "Modulation", true, false, false, new[] { P("Rate", PortType.Control), P("Reset", PortType.Gate) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Shape", 0, 6, 0, "", LfoShapes), new ControlInfo("Rate", 0.01f, 100, 2, "Hz"), new ControlInfo("Depth", 0, 1, 1), new ControlInfo("Offset", -1, 1, 0),
                new ControlInfo("Phase", 0, 1, 0), new ControlInfo("Retrigger", 0, 1, 1, "", Off1), new ControlInfo("Rate mod", 0, 4, 1, "octaves per unit"),
            }),
            // Slew
            new NodeInfo("Slew", "Modulation", true, false, false, new[] { P("In", PortType.Control) }, new[] { P("Out", PortType.Control) }, new[] { new ControlInfo("Rise", 0, 10, 0.05f, "s"), new ControlInfo("Fall", 0, 10, 0.05f, "s") }),
            // SampleHold
            new NodeInfo("Sample & Hold", "Modulation", true, false, false, new[] { P("In", PortType.Control), P("Trigger", PortType.Gate) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Mode", 0, 1, 0, "", new[] { "sample", "track" }), new ControlInfo("Rate", 0.1f, 100, 4, "Hz"),
            }),
            // Random
            new NodeInfo("Random", "Modulation", true, false, false, new[] { P("Trigger", PortType.Gate) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Rate", 0.1f, 100, 4, "Hz"), new ControlInfo("Smooth", 0, 1, 0), new ControlInfo("Depth", 0, 1, 1), new ControlInfo("Offset", -1, 1, 0),
            }),
            // Shaper
            new NodeInfo("Shaper", "Tone", true, false, false, new[] { P("In", PortType.Audio), P("Drive", PortType.Control), P("Pitch", PortType.Pitch) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Mode", 0, 2, 0, "", ShaperModes), new ControlInfo("Drive", 1, 16, 2, "x"), new ControlInfo("Bias", -1, 1, 0),
                new ControlInfo("Stages", 1, 4, 2), new ControlInfo("Level", 0, 2, 1),
            }),
            // Delay
            new NodeInfo("Delay", "Tone", true, false, false, new[] { P("In", PortType.Audio), P("Time", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Time", 0.05f, 2000, 50, "ms"), new ControlInfo("Feedback", -0.98f, 0.98f, 0.5f), new ControlInfo("Damping", 200, 20000, 8000, "Hz"),
                new ControlInfo("Mix", 0, 1, 1), new ControlInfo("Time mod", 0, 4, 1, "octaves per unit"),
            }),
            // Mixer
            new NodeInfo("Mixer", "Plumbing", true, false, false, new[] { P("In 1", PortType.Audio), P("In 2", PortType.Audio), P("In 3", PortType.Audio), P("In 4", PortType.Audio) }, new[] { P("Out", PortType.Audio) }, Gains(4, 1f)),
            // Math
            new NodeInfo("Math", "Plumbing", true, false, false, new[] { P("A", PortType.Control), P("B", PortType.Control) }, new[] { P("Out", PortType.Control) }, new[]
            {
                new ControlInfo("Op", 0, 6, 0, "", MathOps), new ControlInfo("Scale", -8, 8, 1), new ControlInfo("Offset", -8, 8, 0),
            }),
            // Crossfade
            new NodeInfo("Crossfade", "Plumbing", true, false, false, new[] { P("A", PortType.Audio), P("B", PortType.Audio), P("Mix", PortType.Control) }, new[] { P("Out", PortType.Audio) }, new[]
            {
                new ControlInfo("Mix", 0, 1, 0.5f), new ControlInfo("Law", 0, 1, 1, "", new[] { "linear", "equal power" }),
            }),
            // Macro
            new NodeInfo("Macro", "Modulation", true, false, false, None, new[] { P("Out", PortType.Control) }, new[] { new ControlInfo("Macro", 1, 16, 1), new ControlInfo("Amount", -1, 1, 1) }),
            // VoiceSum
            new NodeInfo("Voice sum", "Plumbing", false, true, false, new[] { P("In", PortType.Audio) }, new[] { P("Out", PortType.Audio) }, NoControls),
        };

        // The constructors above that take controls after the outputs are for the two nodes whose second array is the controls.
        static string[] SourceNames()
        {
            var n = new string[SampleSources.Count + 1];
            for (int i = 0; i < SampleSources.Count; i++) n[i] = SampleSources.Names[i];
            n[SampleSources.Count] = "user";
            return n;
        }

        public static NodeInfo For(NodeKind kind) => table[(int)kind];
        public static int KindCount => table.Length;

        public static bool TryParseKind(string name, out NodeKind kind) => Enum.TryParse(name, false, out kind) && Enum.IsDefined(typeof(NodeKind), kind);
    }

    internal sealed class Node
    {
        public readonly int Id;
        public readonly NodeKind Kind;
        /// <summary>The node's controls in their units (NodeInfo.Controls). Compiled jobs read this array live, so an edit is heard at the next block.</summary>
        public readonly float[] Controls;
        /// <summary>Global instead of per voice (LFO, Random and Macro have it in the editor; any node but Keys may be).</summary>
        public bool Global;
        /// <summary>Where the editor draws it (NP2); the engine ignores it.</summary>
        public float X, Y;

        public Node(int id, NodeKind kind)
        {
            Id = id;
            Kind = kind;
            var info = NodeInfo.For(kind);
            Controls = new float[info.Controls.Length];
            for (int i = 0; i < Controls.Length; i++) Controls[i] = info.Controls[i].Default;
        }

        public NodeInfo Info => NodeInfo.For(Kind);

        /// <summary>Whether this node runs once, not for each voice, as far as its own settings say (its inputs may make it global too).</summary>
        public bool GlobalSetting => Info.ForcedGlobal || (!Info.ForcedPerVoice && (Global || !Info.PerVoiceDefault));

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

    /// <summary>A macro driving a node control: the control is <c>Min + (Max − Min) × macro</c> in the control's own units.</summary>
    internal readonly struct MacroMapping
    {
        public readonly int Macro, NodeId, Control;
        public readonly float Min, Max;
        public MacroMapping(int macro, int nodeId, int control, float min, float max) { Macro = macro; NodeId = nodeId; Control = control; Min = min; Max = max; }
    }

    internal enum ConnectResult { Ok, BadPort, Cycle }

    internal sealed class PatchGraph
    {
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Wire> Wires = new List<Wire>();
        public readonly List<MacroMapping> Macros = new List<MacroMapping>();
        public string Name = "";
        int nextId = 1;

        /// <summary>A new node, or null at the node cap (<see cref="NodeInfo.Cap"/>).</summary>
        public Node Add(NodeKind kind)
        {
            if (Nodes.Count >= NodeInfo.Cap) return null;
            var n = new Node(nextId++, kind);
            Nodes.Add(n);
            return n;
        }

        /// <summary>A node with a given id (loading); null at the cap or when the id is taken.</summary>
        public Node AddWithId(int id, NodeKind kind)
        {
            if (Nodes.Count >= NodeInfo.Cap || id < 1 || Find(id) != null) return null;
            var n = new Node(id, kind);
            Nodes.Add(n);
            if (id >= nextId) nextId = id + 1;
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
            Macros.RemoveAll(m => m.NodeId == id);
        }

        public void Disconnect(int fromNode, int fromPort, int toNode, int toPort) =>
            Wires.RemoveAll(w => w.FromNode == fromNode && w.FromPort == fromPort && w.ToNode == toNode && w.ToPort == toPort);

        /// <summary>Maps a macro (0 … 15) to a control; a control has one mapping (the last wins).</summary>
        public void MapMacro(int macro, Node node, int control, float min, float max)
        {
            if (node == null || macro < 0 || macro > 15 || control < 0 || control >= node.Controls.Length) return;
            Macros.RemoveAll(m => m.NodeId == node.Id && m.Control == control);
            Macros.Add(new MacroMapping(macro, node.Id, control, min, max));
        }

        /// <summary>
        /// Wires an output to an input (several wires into one input add). A cycle that doesn't go through a Feedback node is
        /// refused (spec §2.4) and the graph is left as it was.
        /// </summary>
        public ConnectResult Connect(Node from, int fromPort, Node to, int toPort, float amount = 1f)
        {
            if (from == null || to == null || fromPort < 0 || fromPort >= from.Info.Outs.Length || toPort < 0 || toPort >= to.Info.Ins.Length) return ConnectResult.BadPort;
            Disconnect(from.Id, fromPort, to.Id, toPort);
            Wires.Add(new Wire(from.Id, fromPort, to.Id, toPort, float.IsFinite(amount) ? amount : 1f));
            if (!PatchCompiler.TryOrder(this, out _, out _))
            {
                Wires.RemoveAt(Wires.Count - 1);
                return ConnectResult.Cycle;
            }
            return ConnectResult.Ok;
        }
    }
}
