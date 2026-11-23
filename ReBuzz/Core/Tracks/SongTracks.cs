// Fork (docs/specs/ux-overhaul.md §11, MT1): the song's wires as a TrackGraph, and the track model of a song (TrackModel.cs holds the rules).
// What counts as a wire is every connection, whatever its volume (a wire at zero is still a route), and a machine counts whether it is
// muted, bypassed or folded into a machine group. A Neo Patch is one machine like any other (its nodes are not machines of the song).

using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.Tracks
{
    internal static class SongTracks
    {
        /// <summary>The graph of a song's machines (the ones the Machines view lists) and their wires.</summary>
        public static TrackGraph GraphOf(SongCore song)
        {
            var graph = new TrackGraph();
            if (song == null) return graph;
            var machines = song.Machines;
            var nodes = new Dictionary<IMachine, TrackNode>(machines.Count);
            foreach (var m in machines)
            {
                var type = m.DLL?.Info?.Type ?? MachineType.Effect;
                var kind = type == MachineType.Master ? TrackNodeKind.Master : type == MachineType.Generator ? TrackNodeKind.Generator : TrackNodeKind.Effect;
                nodes[m] = graph.Add(m, m.Name, kind, m.IsControlMachine);
            }
            foreach (var m in machines)
                foreach (var c in m.Inputs)
                    if (c.Source != null && nodes.TryGetValue(c.Source, out var from) && nodes.TryGetValue(m, out var to))
                        graph.Connect(from, to);
            return graph;
        }

        /// <summary>The track model of a song now.</summary>
        public static TrackModel Of(SongCore song) => TrackModel.Compute(GraphOf(song));

        /// <summary>The machine a node stands for.</summary>
        public static IMachine MachineOf(TrackNode node) => node?.Tag as IMachine;
    }
}
