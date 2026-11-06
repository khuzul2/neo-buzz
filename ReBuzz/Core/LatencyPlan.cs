// Fork (docs/specs/mastering-tools.md §4.1): the machine delay compensation's plan. The machines that
// reach Master, in topological order (sources first), each with its input connections, as arrays,
// so that applying it (when a machine's latency changes on the audio thread) allocates nothing:
// each machine's arrival = the latest of its inputs' arrivals + its own latency, and each input's
// delay = that latest − its source's arrival. ReBuzz's recursive walk from Master
// (HandleLatencyCalcRecursive, before) computed the same delays, visiting a machine once per path to
// it. Built on the UI thread whenever the graph changes (ReBuzzCore.UpdateMachineDelayCompensation).

using System;
using System.Collections.Generic;

namespace ReBuzz.Core
{
    internal sealed class LatencyPlan
    {
        /// <summary>The nodes (machines) in topological order: every source before the machines it feeds; the last is the root (Master).</summary>
        public int Count { get; private set; }

        int[] inputStart = Array.Empty<int>(), inputCount = Array.Empty<int>();
        int[] inputSource = Array.Empty<int>();
        int[] arrival = Array.Empty<int>();

        /// <summary>The order's node ids (the builder's own numbering of the machines), and each input's connection id.</summary>
        public int[] Nodes { get; private set; } = Array.Empty<int>();
        public int[] Connections { get; private set; } = Array.Empty<int>();

        /// <summary>Samples from a source at the root to the root's output, as last applied.</summary>
        public int Total { get; private set; }

        /// <summary>
        /// Builds the plan from <paramref name="root"/> over <paramref name="inputs"/>(node) = its (connection id, source node)
        /// pairs, in order. Nodes not reaching the root are left out; a cycle (not possible in a Buzz graph) is cut where met.
        /// </summary>
        public static LatencyPlan Build(int root, Func<int, IEnumerable<(int Connection, int Source)>> inputs)
        {
            var order = new List<int>();
            var index = new Dictionary<int, int>();
            var state = new Dictionary<int, int>();      // 1: on the path, 2: done
            var edges = new Dictionary<int, List<(int Connection, int Source)>>();
            // Depth first without recursion (graphs can be deep): post-order gives sources first.
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            edges[root] = new List<(int, int)>(inputs(root));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var list = edges[node];
                if (next < list.Count)
                {
                    stack.Push((node, next + 1));
                    int source = list[next].Source;
                    if (!state.TryGetValue(source, out int s))
                    {
                        state[source] = 1;
                        edges[source] = new List<(int, int)>(inputs(source));
                        stack.Push((source, 0));
                    }
                    else if (s == 1)
                    {
                        // A cycle: this edge is left out.
                        list[next] = (list[next].Connection, -1);
                    }
                    continue;
                }
                state[node] = 2;
                index[node] = order.Count;
                order.Add(node);
            }

            var plan = new LatencyPlan { Count = order.Count };
            plan.Nodes = order.ToArray();
            plan.inputStart = new int[order.Count];
            plan.inputCount = new int[order.Count];
            plan.arrival = new int[order.Count];
            var sources = new List<int>();
            var connections = new List<int>();
            for (int i = 0; i < order.Count; i++)
            {
                plan.inputStart[i] = sources.Count;
                foreach (var (connection, source) in edges[order[i]])
                {
                    if (source < 0) continue;
                    sources.Add(index[source]);
                    connections.Add(connection);
                }
                plan.inputCount[i] = sources.Count - plan.inputStart[i];
            }
            plan.inputSource = sources.ToArray();
            plan.Connections = connections.ToArray();
            return plan;
        }

        /// <summary>
        /// Applies the plan: <paramref name="latency"/>(position in the order) is that machine's own latency; each input's delay goes to
        /// <paramref name="delays"/>[input index] (the order of <see cref="Connections"/>). Allocates nothing.
        /// </summary>
        public void Apply(int[] latency, int[] delays)
        {
            for (int i = 0; i < Count; i++)
            {
                int start = inputStart[i], end = start + inputCount[i];
                int latest = 0;
                for (int j = start; j < end; j++) latest = Math.Max(latest, arrival[inputSource[j]]);
                for (int j = start; j < end; j++) delays[j] = latest - arrival[inputSource[j]];
                arrival[i] = latest + latency[i];
            }
            Total = Count > 0 ? arrival[Count - 1] : 0;
        }

        /// <summary>The arrival of the machine at <paramref name="position"/> in the order, as last applied (tests).</summary>
        public int Arrival(int position) => arrival[position];
    }
}
