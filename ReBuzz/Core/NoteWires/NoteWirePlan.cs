// Fork (docs/specs/note-wires.md): the note wires as pure data and rules: the state a song keeps (free wires and insert chains, by machine name),
// the checks that refuse a wire, and the queue and merge the audio thread uses. No engine types, so ReBuzz.SessionTests compiles this file
// and tests it on Linux.
#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.NoteWires
{
    public readonly record struct NoteEdge(string From, string To);

    /// <summary>The insert machines of one instrument, in order: the track's note chain.</summary>
    public sealed record NoteChain(string Instrument, IReadOnlyList<string> Inserts);

    /// <summary>What a machine can do with notes (by name, for the rules).</summary>
    public readonly record struct NotePorts(bool In, bool Out, bool Control = false);

    /// <summary>
    /// The song's note wiring: the free wires and the chains. Immutable: every edit makes a new state, so an undo step is a pair of them.
    /// </summary>
    public sealed class NoteWireState
    {
        public static readonly NoteWireState Empty = new NoteWireState(Array.Empty<NoteEdge>(), Array.Empty<NoteChain>());

        public IReadOnlyList<NoteEdge> Wires { get; }
        public IReadOnlyList<NoteChain> Chains { get; }

        public NoteWireState(IEnumerable<NoteEdge> wires, IEnumerable<NoteChain> chains)
        {
            Wires = wires.Distinct().ToArray();
            Chains = chains.Where(c => c.Inserts.Count > 0).Select(c => new NoteChain(c.Instrument, c.Inserts.ToArray())).ToArray();
        }

        public bool IsEmpty => Wires.Count == 0 && Chains.Count == 0;

        public IReadOnlyList<string> ChainOf(string instrument)
        {
            foreach (var c in Chains) if (c.Instrument == instrument) return c.Inserts;
            return Array.Empty<string>();
        }

        /// <summary>The chain an insert sits in (its instrument), or null.</summary>
        public string InstrumentOf(string insert)
        {
            foreach (var c in Chains) if (c.Inserts.Contains(insert)) return c.Instrument;
            return null;
        }

        /// <summary>Every edge that orders and carries notes: the free wires, then each chain's insert to insert and last insert to instrument edges (in a chain's order).</summary>
        public IEnumerable<NoteEdge> AllEdges()
        {
            foreach (var w in Wires) yield return w;
            foreach (var e in ChainEdges()) yield return e;
        }

        public IEnumerable<NoteEdge> ChainEdges()
        {
            foreach (var c in Chains)
            {
                for (int i = 0; i + 1 < c.Inserts.Count; i++) yield return new NoteEdge(c.Inserts[i], c.Inserts[i + 1]);
                yield return new NoteEdge(c.Inserts[c.Inserts.Count - 1], c.Instrument);
            }
        }

        public NoteWireState WithWire(NoteEdge e) => Wires.Contains(e) ? this : new NoteWireState(Wires.Append(e), Chains);
        public NoteWireState WithoutWire(NoteEdge e) => Wires.Contains(e) ? new NoteWireState(Wires.Where(w => w != e), Chains) : this;

        /// <summary>The chain of <paramref name="instrument"/> replaced (an empty list removes it).</summary>
        public NoteWireState WithChain(string instrument, IEnumerable<string> inserts)
        {
            var list = inserts.Distinct(StringComparer.Ordinal).ToArray();
            var others = Chains.Where(c => c.Instrument != instrument).ToList();
            // An insert belongs to one chain: a machine moved into this one leaves the other.
            for (int i = 0; i < others.Count; i++)
                if (others[i].Inserts.Any(n => list.Contains(n)))
                    others[i] = new NoteChain(others[i].Instrument, others[i].Inserts.Where(n => !list.Contains(n)).ToArray());
            if (list.Length > 0) others.Add(new NoteChain(instrument, list));
            return new NoteWireState(Wires, others);
        }

        public NoteWireState Renamed(string oldName, string newName)
        {
            string R(string n) => n == oldName ? newName : n;
            return new NoteWireState(Wires.Select(w => new NoteEdge(R(w.From), R(w.To))),
                Chains.Select(c => new NoteChain(R(c.Instrument), c.Inserts.Select(R).ToArray())));
        }

        /// <summary>Only what names existing machines: a chain keeps its existing inserts (a missing one is skipped over: the chain closes up).</summary>
        public NoteWireState Pruned(Func<string, bool> exists)
        {
            return new NoteWireState(Wires.Where(w => exists(w.From) && exists(w.To)),
                Chains.Where(c => exists(c.Instrument)).Select(c => new NoteChain(c.Instrument, c.Inserts.Where(exists).ToArray())));
        }

        public bool SameAs(NoteWireState o)
        {
            if (ReferenceEquals(this, o)) return true;
            return o != null && Wires.SequenceEqual(o.Wires)
                && Chains.Count == o.Chains.Count
                && Chains.Zip(o.Chains, (a, b) => a.Instrument == b.Instrument && a.Inserts.SequenceEqual(b.Inserts)).All(x => x);
        }
    }

    /// <summary>The rules that refuse a wire. Every method returns the reason, or null when the wire is fine.</summary>
    public static class NoteWireRules
    {
        /// <summary>
        /// Whether <paramref name="to"/> can already reach <paramref name="from"/> over <paramref name="edges"/> (audio connections and note edges alike):
        /// a new edge from to <paramref name="from"/> to <paramref name="to"/> would close a loop.
        /// </summary>
        public static bool Reaches(IEnumerable<NoteEdge> edges, string start, string goal)
        {
            if (start == goal) return true;
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in edges)
            {
                if (!next.TryGetValue(e.From, out var l)) next[e.From] = l = new List<string>();
                l.Add(e.To);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!next.TryGetValue(cur, out var outs)) continue;
                foreach (var n in outs)
                {
                    if (n == goal) return true;
                    if (seen.Add(n)) stack.Push(n);
                }
            }
            return false;
        }

        public static string Cycle(string from, string to) => $"{from} to {to} would make a loop: {to} already runs before {from}, in the audio wires and the note wires together.";

        /// <summary>The reason a free wire from <paramref name="from"/> to <paramref name="to"/> is refused, or null.</summary>
        public static string CheckWire(NoteWireState state, IEnumerable<NoteEdge> audioEdges, Func<string, NotePorts?> ports, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return "A note wire needs a source and a target.";
            var pf = ports(from); var pt = ports(to);
            if (pf == null) return $"There is no machine named {from}.";
            if (pt == null) return $"There is no machine named {to}.";
            if (from == to) return "A machine can't send notes to itself.";
            if (pf.Value.Control || pt.Value.Control) return "A control machine has no note ports.";
            if (!pf.Value.Out) return $"{from} doesn't send notes.";
            if (!pt.Value.In) return $"{to} doesn't take notes.";
            if (state.Wires.Contains(new NoteEdge(from, to))) return $"{from} already sends notes to {to}.";
            if (Reaches(audioEdges.Concat(state.AllEdges()), to, from)) return Cycle(from, to);
            return null;
        }

        /// <summary>The reason a chain (inserts before the instrument) is refused, or null. The edges of the chain being replaced are left out of the loop check.</summary>
        public static string CheckChain(NoteWireState state, IEnumerable<NoteEdge> audioEdges, Func<string, NotePorts?> ports, string instrument, IReadOnlyList<string> inserts)
        {
            var pi = ports(instrument);
            if (pi == null) return $"There is no machine named {instrument}.";
            if (pi.Value.Control || !pi.Value.In) return $"{instrument} doesn't take notes.";
            foreach (var n in inserts)
            {
                var p = ports(n);
                if (p == null) return $"There is no machine named {n}.";
                if (n == instrument) return "A machine can't be its own insert.";
                if (p.Value.Control || !p.Value.In || !p.Value.Out) return $"{n} can't be a note insert: an insert takes notes and sends them on.";
            }
            if (inserts.Distinct(StringComparer.Ordinal).Count() != inserts.Count) return "A machine can only be in a chain once.";
            var next = state.WithChain(instrument, inserts);
            // A loop: some edge of the new chain whose target already reaches its source over everything else.
            var rest = audioEdges.Concat(next.AllEdges()).ToArray();
            foreach (var e in ChainEdgesOf(instrument, inserts))
                if (Reaches(rest.Where(x => x != e), e.To, e.From)) return Cycle(e.From, e.To);
            return null;
        }

        static IEnumerable<NoteEdge> ChainEdgesOf(string instrument, IReadOnlyList<string> inserts)
        {
            for (int i = 0; i + 1 < inserts.Count; i++) yield return new NoteEdge(inserts[i], inserts[i + 1]);
            if (inserts.Count > 0) yield return new NoteEdge(inserts[inserts.Count - 1], instrument);
        }

        /// <summary>The reason an audio connection from <paramref name="from"/> to <paramref name="to"/> is refused because of the note wires, or null.</summary>
        public static string CheckAudio(NoteWireState state, IEnumerable<NoteEdge> audioEdges, string from, string to)
            => Reaches(audioEdges.Concat(state.AllEdges()), to, from) ? Cycle(from, to) : null;
    }

    /// <summary>The events of a block on one wire, in a fixed array (nothing is allocated after construction). Full: further events are dropped and counted.</summary>
    public sealed class NoteQueue
    {
        public const int Capacity = 1024;
        public readonly NoteMessage[] Events = new NoteMessage[Capacity];
        public int Count;
        public long Dropped;

        public void Add(in NoteMessage e)
        {
            if (Count >= Events.Length) { Dropped++; return; }
            Events[Count++] = e;
        }

        public void Clear() => Count = 0;
    }

    public static class NoteMerge
    {
        /// <summary>
        /// Merges the first <paramref name="count"/> queues into <paramref name="dest"/>, sorted by offset; events of equal offset keep the queue's order (the wire's
        /// creation order), then their order within the queue, so the result doesn't depend on which thread ran which source. Returns the number written
        /// (what doesn't fit in dest is dropped). Allocates nothing.
        /// </summary>
        public static int Merge(NoteQueue[] queues, int count, NoteMessage[] dest)
        {
            int n = 0;
            // Append in queue order, then a stable insertion sort by offset (queues are short and mostly sorted already).
            for (int k = 0; k < count; k++)
            {
                var q = queues[k];
                for (int i = 0; i < q.Count && n < dest.Length; i++)
                {
                    var e = q.Events[i];
                    int j = n - 1;
                    while (j >= 0 && dest[j].Offset > e.Offset) { dest[j + 1] = dest[j]; j--; }
                    dest[j + 1] = e;
                    n++;
                }
            }
            return n;
        }
    }
}
