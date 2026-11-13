#nullable disable
// Neo Patch NP0: a headless host. Renders a patch from a list of notes, so tests (and later the machine) need no ReBuzz.

using System;
using System.Collections.Generic;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    /// <summary>A note event: <paramref name="Velocity"/> 0 is a note off. It lands at the first block start at or after <paramref name="Sample"/>.</summary>
    internal readonly record struct NoteEvent(int Sample, int Key, float Velocity);

    internal sealed class PatchHost
    {
        public readonly PatchRuntime Runtime;
        public readonly PatchGraph Graph;

        public PatchHost(PatchGraph graph, int voices = 8, float sampleRate = 48000f, LaneKind lanes = LaneKind.Auto, int tick = 16)
        {
            Graph = graph;
            Runtime = new PatchRuntime(sampleRate, lanes, tick) { Voices = voices };
            string error = Runtime.SetGraph(graph);
            if (error != null) throw new InvalidOperationException(error);
        }

        /// <summary>Key numbers are MIDI notes (the pitch in semitones is the key).</summary>
        public (float[] L, float[] R) Render(IReadOnlyList<NoteEvent> events, int frames)
        {
            var l = new float[frames];
            var r = new float[frames];
            var bl = new float[PatchRuntime.Block];
            var br = new float[PatchRuntime.Block];
            int e = 0;
            for (int pos = 0; pos < frames; pos += PatchRuntime.Block)
            {
                while (e < events.Count && events[e].Sample <= pos)
                {
                    var ev = events[e++];
                    if (ev.Velocity > 0f) Runtime.NoteOn(ev.Key, ev.Key, ev.Velocity);
                    else Runtime.NoteOff(ev.Key);
                }
                Runtime.ProcessBlock(bl, br);
                int n = Math.Min(PatchRuntime.Block, frames - pos);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r);
        }
    }
}
