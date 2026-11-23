// Fork (docs/specs/ux-overhaul.md §11, MT1): the track model. "The path in the graph from each root generator to the Master is a
// track." It is computed from the wires and never saved (no song format change); the Machines view shows it as colour, and the
// Mixer and the Arrangement headers may use the same list later (one list, a stable head machine per entry).
//
// This file knows nothing of the engine (a graph of nodes in, a model out), so the rule tests compile it on its own
// (ReBuzz.SessionTests) and run it on built graphs; SongTracks.cs makes the graph from a song.
//
// The rules (the table is in the spec, §11 "MT1 as built"):
//   - A routed machine is one that is not a control machine and has a way to the Master.
//   - A routed machine with no routed feeder is a root: it starts a track (a generator; an effect with nothing wired into it too).
//   - Any other routed machine looks at who owns its feeders. One owner: it joins that owner's track (a generator into two effects that
//     meet again is one track). Several owners: it is a shared machine and owns an entry of its own, a group or a return.
//   - A shared machine's feeders are its members when every route from them to the Master passes through it, and sends otherwise. It is
//     a group when it has members (sends into it are marked), a return when it has none. Groups nest (a group's own entry can be a
//     member of another).
//   - Control machines, and every machine with no way to the Master, are unrouted. So is a machine caught in a loop.
//   - Every machine lands in exactly one place: the Master, one entry, or Unrouted.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReBuzz.Core.Tracks
{
    /// <summary>What a machine is, as far as the track rules care.</summary>
    public enum TrackNodeKind { Generator, Effect, Master }

    /// <summary>A machine, as the model sees it: a name, a kind, whether it makes audio, and the wires into it.</summary>
    public sealed class TrackNode
    {
        internal readonly List<TrackNode> sources = new List<TrackNode>();
        internal readonly List<TrackNode> destinations = new List<TrackNode>();

        public TrackNode(object tag, string name, TrackNodeKind kind, bool isControl, int index)
        {
            Tag = tag;
            Name = name;
            Kind = kind;
            IsControl = isControl;
            Index = index;
        }

        /// <summary>What the caller keeps to find its machine again (the machine itself).</summary>
        public object Tag { get; }

        public string Name { get; }

        public TrackNodeKind Kind { get; }

        /// <summary>A control machine: no audio out, so it has no place in a track.</summary>
        public bool IsControl { get; }

        /// <summary>The place in the graph's list (the song's own order).</summary>
        public int Index { get; }

        /// <summary>The machines wired into this one.</summary>
        public IReadOnlyList<TrackNode> Sources => sources;

        /// <summary>The machines this one is wired into.</summary>
        public IReadOnlyList<TrackNode> Destinations => destinations;

        public override string ToString() => Name;
    }

    /// <summary>The wires of a song as a graph: what <see cref="TrackModel.Compute"/> reads.</summary>
    public sealed class TrackGraph
    {
        readonly List<TrackNode> nodes = new List<TrackNode>();

        public IReadOnlyList<TrackNode> Nodes => nodes;

        public TrackNode Add(object tag, string name, TrackNodeKind kind, bool isControl = false)
        {
            var node = new TrackNode(tag, name, kind, isControl, nodes.Count);
            nodes.Add(node);
            return node;
        }

        /// <summary>A wire from one machine to another. A zero-volume wire is still a wire; a second wire between the same two is the first.</summary>
        public void Connect(TrackNode from, TrackNode to)
        {
            if (from == null || to == null || from == to) return;
            if (from.destinations.Contains(to)) return;
            from.destinations.Add(to);
            to.sources.Add(from);
        }
    }

    public enum TrackKind
    {
        /// <summary>A root and the machines only it feeds.</summary>
        Track,
        /// <summary>A shared machine that is the only way out of one or more tracks (its members).</summary>
        Group,
        /// <summary>A shared machine that no track needs: its feeders reach the Master another way, and their wires into it are sends.</summary>
        Return,
        /// <summary>The machines every routed track passes through on its way to the Master (an EQ and a limiter after the mix): the Master's own chain.</summary>
        MasterChain,
    }

    public enum TrackPlace { Master, MasterChain, Track, Group, Return, Unrouted }

    public enum UnroutedReason
    {
        /// <summary>It makes no audio (a control machine).</summary>
        Control,
        /// <summary>No wire in or out.</summary>
        NotWired,
        /// <summary>It is wired, but no way leads to the Master.</summary>
        NoPathToMaster,
        /// <summary>It is part of a loop of wires.</summary>
        Loop,
    }

    /// <summary>A track, a group or a return.</summary>
    public sealed class TrackEntry
    {
        internal readonly List<TrackNode> machines = new List<TrackNode>();
        internal readonly List<TrackEntry> members = new List<TrackEntry>();
        internal readonly List<TrackEntry> senders = new List<TrackEntry>();
        internal readonly List<TrackEntry> sendsTo = new List<TrackEntry>();
        internal TrackModel model;
        internal string chain;
        internal List<TrackNode> allMachines;

        internal TrackEntry(TrackKind kind, TrackNode head)
        {
            Kind = kind;
            Head = head;
        }

        /// <summary>The kind. A group or a return is the entry of its shared machine, which the entries below it feed.</summary>
        public TrackKind Kind { get; internal set; }

        /// <summary>The generator of a track, the shared machine of a group or a return. It is the entry's stable key and its name, colour and selection.</summary>
        public TrackNode Head { get; }

        /// <summary>"track:Bass", "group:Drum bus", "return:Verb": the kind and the head's name (an agent's handle; the studio keys on <see cref="Head"/>).</summary>
        public string Id => Kind == TrackKind.MasterChain ? "master:Master"
            : (Kind == TrackKind.Track ? "track:" : Kind == TrackKind.Group ? "group:" : "return:") + Head.Name;

        /// <summary>The head's name; "Master" for the Master's chain.</summary>
        public string Name => Kind == TrackKind.MasterChain ? "Master" : Head.Name;

        /// <summary>Every machine that lands here, the head first, in signal order. A group's own machines only: its members have theirs.</summary>
        public IReadOnlyList<TrackNode> Machines => machines;

        /// <summary>A group: the entries that go through it and nowhere else, in the order of their heads. Empty for a track and a return.</summary>
        public IReadOnlyList<TrackEntry> Members => members;

        /// <summary>A group or a return: the entries that also reach the Master another way and send into it. Empty for a track.</summary>
        public IReadOnlyList<TrackEntry> Senders => senders;

        /// <summary>Where this entry sends (shared machines it feeds without needing them).</summary>
        public IReadOnlyList<TrackEntry> SendsTo => sendsTo;

        /// <summary>The group this entry is a member of (its machines reach the Master only through it), or null.</summary>
        public TrackEntry Parent { get; internal set; }

        /// <summary>How many groups lie above this entry (a member of a group of groups is at 2).</summary>
        public int Depth { get; internal set; }

        /// <summary>
        /// The way to the Master as words: "Neo Poly → Neo Drive → Neo Eq → Master". Machines at the same stage of the entry are joined with
        /// " + ", and the entry's group's own chain follows it; an entry that only sends goes on through the first return it sends to.
        /// </summary>
        public string Chain => chain ??= model.ChainOf(this);

        /// <summary>This entry's machines and all those of its members, nested (what a group chip stands for).</summary>
        public IReadOnlyList<TrackNode> AllMachines
        {
            get
            {
                if (allMachines != null) return allMachines;
                var list = new List<TrackNode>(machines);
                foreach (var m in members) list.AddRange(m.AllMachines);
                return allMachines = list;
            }
        }

        public override string ToString() => Id;
    }

    /// <summary>A machine that has no place in a track, and why.</summary>
    public sealed class UnroutedMachine
    {
        internal UnroutedMachine(TrackNode node, UnroutedReason why)
        {
            Node = node;
            Why = why;
        }

        public TrackNode Node { get; }

        public UnroutedReason Why { get; }

        public string WhyText => Why switch
        {
            UnroutedReason.Control => "control only",
            UnroutedReason.NotWired => "not wired",
            UnroutedReason.Loop => "in a loop",
            _ => "no way to the Master",
        };
    }

    /// <summary>The tracks of a song. Immutable once computed; compute it again when a machine, a wire or a name changes.</summary>
    public sealed class TrackModel
    {
        readonly TrackEntry[] byNode;       // the entry each node landed in (null: the Master, or unrouted)
        readonly UnroutedMachine[] unroutedOf;
        TrackEntry masterEntry;
        readonly Dictionary<(int, int), bool> sendWires = new Dictionary<(int, int), bool>();

        TrackModel(TrackGraph graph, int count)
        {
            Graph = graph;
            byNode = new TrackEntry[count];
            unroutedOf = new UnroutedMachine[count];
        }

        public TrackGraph Graph { get; }

        /// <summary>The Master, or null for a graph without one (then every machine is unrouted).</summary>
        public TrackNode Master { get; private set; }

        /// <summary>The one list: the tracks (in the order of their generators), then the groups, then the returns (in the order of their machines).</summary>
        public IReadOnlyList<TrackEntry> Entries { get; private set; } = Array.Empty<TrackEntry>();

        public IReadOnlyList<TrackEntry> Tracks => Entries.Where(e => e.Kind == TrackKind.Track).ToList();

        public IReadOnlyList<TrackEntry> Groups => Entries.Where(e => e.Kind == TrackKind.Group).ToList();

        /// <summary>The Master's own chain (the machines every track passes through before the Master), or null when there are none.</summary>
        public TrackEntry MasterChain => Entries.FirstOrDefault(e => e.Kind == TrackKind.MasterChain);

        public IReadOnlyList<TrackEntry> Returns => Entries.Where(e => e.Kind == TrackKind.Return).ToList();

        public IReadOnlyList<UnroutedMachine> Unrouted { get; private set; } = Array.Empty<UnroutedMachine>();

        /// <summary>The entry a machine landed in, or null for the Master and for an unrouted machine.</summary>
        public TrackEntry EntryOf(TrackNode node) => node == null || node.Index >= byNode.Length ? null : byNode[node.Index];

        /// <summary>Where a machine landed: always exactly one place.</summary>
        public TrackPlace PlaceOf(TrackNode node)
        {
            if (node == Master && node != null) return TrackPlace.Master;
            var e = EntryOf(node);
            if (e == null) return TrackPlace.Unrouted;
            return e.Kind == TrackKind.MasterChain ? TrackPlace.MasterChain : e.Kind == TrackKind.Track ? TrackPlace.Track : e.Kind == TrackKind.Group ? TrackPlace.Group : TrackPlace.Return;
        }

        public UnroutedMachine UnroutedOf(TrackNode node) => node == null || node.Index >= unroutedOf.Length ? null : unroutedOf[node.Index];

        /// <summary>The entry whose head is this machine, or null.</summary>
        public TrackEntry EntryWithHead(TrackNode node)
        {
            var e = EntryOf(node);
            return e != null && e.Head == node ? e : null;
        }

        /// <summary>A wire into a group or a return from an entry that is not one of its members: a send.</summary>
        public bool IsSend(TrackNode from, TrackNode to) => from != null && to != null && sendWires.ContainsKey((from.Index, to.Index));

        /// <summary>
        /// The machines lit when the entry is: its own and its members' (nested), and everything downstream of them to the Master. The wires
        /// that count are those leaving a machine in the set.
        /// </summary>
        public HashSet<TrackNode> PathOf(TrackEntry entry)
        {
            var set = new HashSet<TrackNode>(entry.AllMachines);
            var stack = new Stack<TrackNode>(set);
            while (stack.Count > 0)
                foreach (var d in stack.Pop().destinations)
                    if (byNode[d.Index] != null || d == Master)
                        if (set.Add(d)) stack.Push(d);
            return set;
        }

        // ------------------------------------------------------------------------------------------------------ the rules

        /// <summary>Works out the tracks of a graph. Linear in the wires, with a walk back from the Master for each shared machine.</summary>
        public static TrackModel Compute(TrackGraph graph)
        {
            var nodes = graph.Nodes;
            int n = nodes.Count;
            var model = new TrackModel(graph, n);
            TrackNode master = null;
            foreach (var node in nodes)
                if (node.Kind == TrackNodeKind.Master) { master = node; break; }
            model.Master = master;

            // 1. Which machines have a way to the Master (a walk back along the wires; control machines have none).
            var reaches = new bool[n];
            var stack = new int[n + 1];
            if (master != null)
            {
                int top = 0;
                stack[top++] = master.Index;
                reaches[master.Index] = true;
                while (top > 0)
                    foreach (var s in nodes[stack[--top]].sources)
                        if (!reaches[s.Index] && !s.IsControl) { reaches[s.Index] = true; stack[top++] = s.Index; }
            }

            // 2. The order the signal flows in: Kahn's algorithm over the routed machines (a loop never gets its turn).
            var routed = new bool[n];
            var inDegree = new int[n];
            var queue = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (!reaches[i] || nodes[i] == master) continue;
                routed[i] = true;
            }
            for (int i = 0; i < n; i++)
            {
                if (!routed[i]) continue;
                int d = 0;
                foreach (var s in nodes[i].sources) if (routed[s.Index]) d++;
                inDegree[i] = d;
                if (d == 0) queue.Add(i);
            }
            var rank = new int[n];
            Array.Fill(rank, -1);
            for (int head = 0; head < queue.Count; head++)
            {
                int i = queue[head];
                rank[i] = head;
                foreach (var d in nodes[i].destinations)
                    if (routed[d.Index] && --inDegree[d.Index] == 0) queue.Add(d.Index);
            }

            // 3. Owners: a root starts an entry; a machine whose feeders have one owner joins it; several owners make it a shared machine.
            var entries = new List<TrackEntry>();
            var ownerOf = new TrackEntry[n];
            var shared = new List<TrackEntry>();
            var roots = new List<int>();
            foreach (int i in queue)
            {
                TrackEntry owner = null;
                bool several = false;
                foreach (var s in nodes[i].sources)
                {
                    var o = ownerOf[s.Index];
                    if (o == null) continue;
                    if (owner == null) owner = o;
                    else if (owner != o) { several = true; break; }
                }
                TrackEntry mine;
                if (owner == null)
                {
                    mine = new TrackEntry(TrackKind.Track, nodes[i]);
                    roots.Add(i);
                }
                else if (several)
                {
                    mine = new TrackEntry(TrackKind.Return, nodes[i]);
                    shared.Add(mine);
                }
                else
                    mine = owner;
                if (mine.model == null) { mine.model = model; entries.Add(mine); }
                mine.machines.Add(nodes[i]);        // the queue is in signal order
                ownerOf[i] = mine;
                model.byNode[i] = mine;
            }

            // 4. Members and sends: for each shared machine, who can reach the Master without it?
            var seen = new int[n];
            int stamp = 0;
            TrackEntry masterChain = null;
            foreach (var s in shared)
            {
                int h = s.Head.Index;
                stamp++;
                int top = 0;
                stack[top++] = master.Index;
                seen[master.Index] = stamp;
                while (top > 0)
                    foreach (var src in nodes[stack[--top]].sources)
                        if (src.Index != h && seen[src.Index] != stamp && routed[src.Index]) { seen[src.Index] = stamp; stack[top++] = src.Index; }
                // Every root behind it: nothing reaches the Master without it, so it is on the Master's own chain (the first such machine).
                if (masterChain == null && !roots.Any(r => seen[r] == stamp)) masterChain = s;
                var feeders = new List<TrackEntry>();
                foreach (var src in s.Head.sources)
                {
                    var o = ownerOf[src.Index];
                    if (o != null && !feeders.Contains(o)) feeders.Add(o);
                }
                feeders.Sort((a, b) => a.Head.Index.CompareTo(b.Head.Index));
                foreach (var f in feeders)
                {
                    if (seen[f.Head.Index] != stamp)
                    {
                        s.members.Add(f);
                        f.Parent = s;
                    }
                    else
                    {
                        s.senders.Add(f);
                        f.sendsTo.Add(s);
                        foreach (var src in s.Head.sources)
                            if (ownerOf[src.Index] == f) model.sendWires[(src.Index, h)] = true;
                    }
                }
                if (s.members.Count > 0) s.Kind = TrackKind.Group;
            }

            // 4b. The Master's chain: its feeders are plain tracks and groups of their own, not its members.
            if (masterChain != null)
            {
                foreach (var m in masterChain.members) m.Parent = null;
                foreach (var f in masterChain.senders) f.sendsTo.Remove(masterChain);
                masterChain.members.Clear();
                masterChain.senders.Clear();
                foreach (var src in masterChain.Head.sources) model.sendWires.Remove((src.Index, masterChain.Head.Index));
                masterChain.Kind = TrackKind.MasterChain;
                model.masterEntry = masterChain;
            }

            // 5. Depth, then the one list: tracks, groups, returns, then the Master's chain.
            foreach (var e in entries)
            {
                int depth = 0;
                for (var p = e.Parent; p != null; p = p.Parent) depth++;
                e.Depth = depth;
            }
            var ordered = new List<TrackEntry>(entries.Count);
            ordered.AddRange(entries.Where(e => e.Kind == TrackKind.Track).OrderBy(e => e.Head.Index));
            ordered.AddRange(entries.Where(e => e.Kind == TrackKind.Group).OrderBy(e => e.Head.Index));
            ordered.AddRange(entries.Where(e => e.Kind == TrackKind.Return).OrderBy(e => e.Head.Index));
            if (masterChain != null) ordered.Add(masterChain);
            model.Entries = ordered;

            // 6. Everything else is unrouted.
            var un = new List<UnroutedMachine>();
            foreach (var node in nodes)
            {
                if (node == master || model.byNode[node.Index] != null) continue;
                UnroutedReason why = node.IsControl ? UnroutedReason.Control
                    : routed[node.Index] ? UnroutedReason.Loop
                    : node.sources.Count == 0 && node.destinations.Count == 0 ? UnroutedReason.NotWired
                    : UnroutedReason.NoPathToMaster;
                var u = new UnroutedMachine(node, why);
                model.unroutedOf[node.Index] = u;
                un.Add(u);
            }
            model.Unrouted = un;
            return model;
        }

        // ------------------------------------------------------------------------------------------------------ chains

        internal string ChainOf(TrackEntry e)
        {
            var sb = new StringBuilder();
            var seenEntries = new HashSet<TrackEntry>();
            for (var cur = e; ;)
            {
                if (!seenEntries.Add(cur)) break;
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(Stages(cur));
                if (cur.Parent != null) { cur = cur.Parent; continue; }
                if (ReachesMasterDirectly(cur)) { sb.Append(" → ").Append(Master?.Name ?? "Master"); break; }
                if (masterEntry != null && cur != masterEntry && cur.machines.Any(m => m.destinations.Any(d => EntryOf(d) == masterEntry))) { sb.Append(" → Master chain"); break; }
                if (cur.sendsTo.Count > 0) { cur = cur.sendsTo[0]; continue; }
                break;
            }
            return sb.ToString();
        }

        bool ReachesMasterDirectly(TrackEntry e)
        {
            if (Master == null) return false;
            foreach (var m in e.machines)
                if (m.destinations.Contains(Master)) return true;
            return false;
        }

        /// <summary>The entry's own machines by stage (the longest way from its head), the machines of one stage joined with " + ".</summary>
        static string Stages(TrackEntry e)
        {
            var level = new Dictionary<TrackNode, int>();
            int deepest = 0;
            foreach (var m in e.machines)      // signal order: feeders are before what they feed
            {
                int l = 0;
                foreach (var s in m.sources)
                    if (level.TryGetValue(s, out int sl) && sl + 1 > l) l = sl + 1;
                level[m] = l;
                if (l > deepest) deepest = l;
            }
            var parts = new List<string>();
            for (int l = 0; l <= deepest; l++)
                parts.Add(string.Join(" + ", e.machines.Where(m => level[m] == l).Select(m => m.Name)));
            return string.Join(" → ", parts);
        }
    }
}
