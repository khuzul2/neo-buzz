// Fork (docs/specs/note-wires.md): the song's note wires and insert chains. Kept by machine name (like pins, MIDI maps and track colours), every edit one
// undo step, saved in a versioned "NoteWires" subsection (.nbl), carried by renames, and turned into the NoteNodes the audio thread reads whenever the graph changes.
#nullable disable
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core.NoteWires
{
    internal sealed class NoteWireStore : INoteWireHost
    {
        public const string SubSectionName = "NoteWires";
        public const int FormatVersion = 1;

        readonly ReBuzzCore buzz;
        NoteWireState state = NoteWireState.Empty;
        volatile Plan plan = new Plan();
        // The queues of a wire survive a rebuild (events in flight are not lost when an unrelated wire changes).
        readonly Dictionary<NoteEdge, NoteQueue> queues = new();

        sealed class Plan
        {
            public readonly Dictionary<MachineCore, NoteNode> ChainEntry = new();
        }

        internal NoteWireStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
            if (buzz.SongCore != null)
            {
                buzz.SongCore.MachineAdded += _ => Rebuild();
                buzz.SongCore.MachineRemoved += _ => Rebuild();
            }
        }

        /// <summary>Raised when the wiring changed (by a person, an agent, undo, a rename or a song opening).</summary>
        public event Action Changed;

        public NoteWireState State => state;

        // ------------------------------------------------------------------ reading

        MachineCore Find(string name)
        {
            var list = buzz.SongCore?.MachinesList;
            if (list == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < list.Count; i++) if (list[i].Name == name) return list[i];
            return null;
        }

        /// <summary>What a machine can do with notes now: null for a machine that isn't in the song.</summary>
        public NotePorts? PortsOf(string name) => PortsOf(Find(name));

        public static NotePorts? PortsOf(MachineCore m)
        {
            if (m == null) return null;
            bool control = (m.DLL.Info.Flags & MachineInfoFlags.CONTROL_MACHINE) != 0;
            var mm = m.ManagedMachine;
            bool inn = false, outt = false;
            if (mm is INoteCapable cap) { inn = cap.AcceptsNotes; outt = cap.SendsNotes; }
            else if (mm is INoteReceiver) inn = true;
            return new NotePorts(inn, outt, control);
        }

        /// <summary>The audio connections as name pairs, for the loop checks.</summary>
        public IEnumerable<NoteEdge> AudioEdges()
        {
            var list = buzz.SongCore?.MachinesList;
            if (list == null) yield break;
            for (int i = 0; i < list.Count; i++)
                foreach (var c in list[i].AllOutputs)
                    if (c.Destination != null) yield return new NoteEdge(list[i].Name, c.Destination.Name);
        }

        public IReadOnlyList<string> NoteTargetsOf(IMachine source)
        {
            string n = source?.Name;
            return state.Wires.Where(e => e.From == n && Find(e.To) != null).Select(e => e.To).Distinct().ToList();
        }

        public IReadOnlyList<string> NoteTargetCandidates(IMachine source)
        {
            var list = new List<string>();
            var all = buzz.SongCore?.MachinesList;
            if (all == null) return list;
            foreach (var m in all)
                if (!ReferenceEquals(m, source) && !m.Hidden && PortsOf(m) is { In: true, Control: false }) list.Add(m.Name);
            return list;
        }

        /// <summary>The reason an audio connection would make a loop with the note wires, or null (the audio connect path asks).</summary>
        public string CheckAudio(string from, string to) => state.IsEmpty ? null : NoteWireRules.CheckAudio(state, AudioEdges(), from, to);

        // ------------------------------------------------------------------ edits (each one undo step)

        /// <summary>Adds a free wire. Null, or why it was refused.</summary>
        public string Connect(string source, string target)
        {
            string reason = NoteWireRules.CheckWire(state, AudioEdges(), PortsOf, source, target);
            if (reason != null) return reason;
            Do(state.WithWire(new NoteEdge(source, target)), "Note wire");
            return null;
        }

        public bool Disconnect(string source, string target)
        {
            var e = new NoteEdge(source, target);
            if (!state.Wires.Contains(e)) return false;
            Do(state.WithoutWire(e), "Remove note wire");
            return true;
        }

        /// <summary>Why a free wire from <paramref name="source"/> to <paramref name="target"/> would be refused, or null when it would be made.</summary>
        public string WhyNot(string source, string target) => NoteWireRules.CheckWire(state, AudioEdges(), PortsOf, source, target);

        public string ConnectNotes(IMachine source, string target) => Connect(source?.Name, target);

        public string SetNoteTarget(IMachine source, string target)
        {
            string from = source?.Name;
            var next = new NoteWireState(state.Wires.Where(w => w.From != from), state.Chains);
            if (!string.IsNullOrEmpty(target))
            {
                string reason = NoteWireRules.CheckWire(next, AudioEdges(), PortsOf, from, target);
                if (reason != null) return reason;
                next = next.WithWire(new NoteEdge(from, target));
            }
            if (!next.SameAs(state)) Do(next, "Note wire");
            return null;
        }
        public bool DisconnectNotes(IMachine source, string target) => Disconnect(source?.Name, target);

        /// <summary>Replaces the chain of <paramref name="instrument"/> (an empty list removes it): one step. Null, or why it was refused.</summary>
        public string SetChain(string instrument, IReadOnlyList<string> inserts)
        {
            string reason = NoteWireRules.CheckChain(state, AudioEdges(), PortsOf, instrument, inserts);
            if (reason != null) return reason;
            var next = state.WithChain(instrument, inserts);
            if (next.SameAs(state)) return null;
            Do(next, "Note inserts");
            return null;
        }

        /// <summary>Puts <paramref name="insert"/> in the chain of <paramref name="instrument"/> at <paramref name="index"/> (the end when out of range).</summary>
        public string AddInsert(string instrument, string insert, int index = int.MaxValue)
        {
            var chain = state.ChainOf(instrument).Where(n => n != insert).ToList();
            chain.Insert(Math.Clamp(index, 0, chain.Count), insert);
            return SetChain(instrument, chain);
        }

        /// <summary>Takes <paramref name="insert"/> out of its chain (the neighbours become adjacent). False when it was in none.</summary>
        public bool RemoveInsert(string insert)
        {
            string instrument = state.InstrumentOf(insert);
            if (instrument == null) return false;
            return SetChain(instrument, state.ChainOf(instrument).Where(n => n != insert).ToList()) == null;
        }

        void Do(NoteWireState next, string name)
        {
            buzz.SongCore.ActionStack.Do(new Change(this, state, next, name));
        }

        /// <summary>Replaces the state (undo and redo, a song opening).</summary>
        internal void Apply(NoteWireState next, bool modified = true)
        {
            state = next;
            Rebuild();
            if (modified) buzz.SetModifiedFlag();
            Changed?.Invoke();
        }

        [HistoryName("Note wires")]
        sealed class Change : IAction
        {
            readonly NoteWireStore store;
            readonly NoteWireState before, after;
            public Change(NoteWireStore store, NoteWireState before, NoteWireState after, string name) { this.store = store; this.before = before; this.after = after; }
            public void Do() => store.Apply(after);
            public void Undo() => store.Apply(before);
        }

        /// <summary>A machine was renamed: its wires and chains follow it (no step of its own: the rename's undo renames back).</summary>
        public void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
            var next = state.Renamed(oldName, newName);
            if (next.SameAs(state)) return;
            Apply(next);
        }

        /// <summary>A new song: no wiring (no step, no modified flag).</summary>
        public void Clear()
        {
            if (state.IsEmpty) { Rebuild(); return; }
            Apply(NoteWireState.Empty, false);
        }

        // ------------------------------------------------------------------ the plan the audio thread reads

        /// <summary>A machine became ready (its managed class exists): its ports are known now.</summary>
        internal void MachineReady()
        {
            if (!state.IsEmpty) Rebuild();
        }

        /// <summary>Rebuilds the NoteNodes from the state and the machines now in the song. UI thread or loader; takes the audio lock.</summary>
        public void Rebuild()
        {
            var machines = buzz.SongCore?.MachinesList;
            if (machines == null) return;
            lock (ReBuzzCore.AudioLock)
            {
                var active = state.Pruned(n => Find(n) != null);
                var newPlan = new Plan();
                var ins = new Dictionary<MachineCore, List<NoteQueue>>();
                var outs = new Dictionary<MachineCore, List<NoteQueue>>();
                var sources = new Dictionary<MachineCore, List<MachineCore>>();
                var targets = new Dictionary<MachineCore, List<MachineCore>>();
                var live = new Dictionary<NoteEdge, NoteQueue>();
                void Add<T>(Dictionary<MachineCore, List<T>> d, MachineCore k, T v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<T>(); l.Add(v); }

                foreach (var e in active.AllEdges())
                {
                    var from = Find(e.From); var to = Find(e.To);
                    if (from == null || to == null || from == to) continue;
                    var pf = PortsOf(from); var pt = PortsOf(to);
                    if (pf == null || pt == null || !pf.Value.Out || !pt.Value.In || pf.Value.Control || pt.Value.Control) continue;     // not ready yet, or no such port
                    if (live.ContainsKey(e)) continue;
                    if (!queues.TryGetValue(e, out var q)) q = new NoteQueue();
                    live[e] = q;
                    Add(outs, from, q); Add(ins, to, q); Add(sources, to, from); Add(targets, from, to);
                }
                var nodes = new Dictionary<MachineCore, NoteNode>();
                NoteNode Node(MachineCore m)
                {
                    if (!nodes.TryGetValue(m, out var n)) nodes[m] = n = new NoteNode { Machine = m, Receiver = m.ManagedMachine as INoteReceiver };
                    return n;
                }
                foreach (var m in ins.Keys.Concat(outs.Keys).Distinct()) Node(m);
                // The first insert of each chain also takes the track input (clip notes, live MIDI, keys).
                foreach (var c in active.Chains)
                {
                    var first = Find(c.Inserts[0]); var instrument = Find(c.Instrument);
                    if (first == null || instrument == null || !nodes.TryGetValue(first, out var node) || !ins.ContainsKey(first) && !outs.ContainsKey(first)) continue;
                    node.InputRing ??= new NoteInputRing();
                    node.InputQueue ??= new NoteQueue();
                    newPlan.ChainEntry[instrument] = node;
                }
                foreach (var kv in nodes)
                {
                    var n = kv.Value;
                    var inList = new List<NoteQueue>();
                    if (n.InputQueue != null) inList.Add(n.InputQueue);
                    if (ins.TryGetValue(n.Machine, out var iq)) inList.AddRange(iq);
                    n.In = inList.ToArray();
                    n.Out = outs.TryGetValue(n.Machine, out var oq) ? oq.ToArray() : Array.Empty<NoteQueue>();
                    n.Sources = sources.TryGetValue(n.Machine, out var sl) ? sl.Distinct().ToArray() : NoteNode.None;
                    n.Targets = targets.TryGetValue(n.Machine, out var tl) ? tl.Distinct().ToArray() : NoteNode.None;
                    n.Scratch = new NoteMessage[Math.Min(Math.Max(1, n.In.Length) * NoteQueue.Capacity, 16384)];
                }
                queues.Clear();
                foreach (var kv in live) queues[kv.Key] = kv.Value;
                foreach (var m in machines) m.NoteNode = nodes.TryGetValue(m, out var nn) ? nn : null;
                plan = newPlan;
                buzz.TopologyGeneration++;       // the cached work order includes the note edges
            }
        }

        // ------------------------------------------------------------------ the track input

        /// <summary>
        /// Clip notes, live MIDI and keys for <paramref name="machine"/> (a MIDI message as the host sends it): with a note chain they go to its first insert and this
        /// returns true; otherwise false and the caller sends them to the machine as before. Any thread.
        /// </summary>
        public bool RouteTrackInput(MachineCore machine, int data)
        {
            if (!plan.ChainEntry.TryGetValue(machine, out var node) || node.InputRing == null) return false;
            int status = data & 0xFF, d1 = (data >> 8) & 0xFF, d2 = (data >> 16) & 0xFF;
            int command = (status & 0xF0) == 0xF0 ? status : status & 0xF0, channel = (status & 0xF0) == 0xF0 ? 0 : status & 0x0F;
            NoteMessage e = default;
            e.Channel = (byte)channel;
            switch (command)
            {
                case 0x90: e.Kind = d2 > 0 ? NoteMessageKind.NoteOn : NoteMessageKind.NoteOff; e.Data1 = (byte)d1; e.Data2 = d2; break;
                case 0x80: e.Kind = NoteMessageKind.NoteOff; e.Data1 = (byte)d1; break;
                case 0xB0: e.Kind = NoteMessageKind.Controller; e.Data1 = (byte)d1; e.Data2 = d2; break;
                case 0xE0: e.Kind = NoteMessageKind.PitchBend; e.Data2 = d1 | (d2 << 7); break;
                case 0xD0: e.Kind = NoteMessageKind.Pressure; e.Data2 = d1; break;
                default: return false;
            }
            node.InputRing.Push(e);
            return true;
        }

        public bool RouteTrackNoteOff(MachineCore machine, int channel, int note)
            => RouteTrackInput(machine, 0x80 | (channel & 15) | ((note & 127) << 8));

        // ------------------------------------------------------------------ migration of the old single target

        /// <summary>A machine's saved single target (Voice to MIDI, Audio to MIDI, Neo Plug) as a wire, without a step.</summary>
        void AdoptLegacy()
        {
            var machines = buzz.SongCore?.MachinesList;
            if (machines == null) return;
            var next = state;
            foreach (var m in machines.ToList())
            {
                if (m.ManagedMachine is not INoteLegacyTarget legacy) continue;
                string target = legacy.TakeLegacyTarget();
                if (string.IsNullOrEmpty(target) || target == m.Name) continue;
                var tm = Find(target);
                if (tm == null) { buzz.DCWriteLine($"{m.Name}: its saved note target \"{target}\" is not in the song; no wire made.", DCLogLevel.Warning); continue; }
                next = next.WithWire(new NoteEdge(m.Name, target));
            }
            state = next;
        }

        // ------------------------------------------------------------------ the song file

        void OnSaveSong(ISaveSong save)
        {
            var names = new HashSet<string>(save.Song.Machines.Select(m => m.Name), StringComparer.Ordinal);
            var live = state.Pruned(names.Contains);
            if (live.IsEmpty) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = live.ToXml(FormatVersion);
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            state = NoteWireState.Empty;
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    int version = int.TryParse((string)root.Attribute("version"), out int v) ? v : 1;
                    if (version > FormatVersion)
                        buzz.DCWriteLine($"Note wires: saved by a newer version ({version}); not read.", DCLogLevel.Warning);
                    else
                        state = NoteWireState.FromXml(root);
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Note wires: " + e.Message, DCLogLevel.Warning);
            }
            AdoptLegacy();
            Rebuild();
            Changed?.Invoke();
        }
    }
}
