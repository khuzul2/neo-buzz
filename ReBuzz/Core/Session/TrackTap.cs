using BuzzGUI.Interfaces;
using Buzz.MachineInterface;
using System;
using System.Collections.Generic;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// A machine's output after its mixer, kept for a Neo Audio that records or monitors it (docs/specs/neo-audio.md "Inputs and arm":
    /// Audio From "a track"). The machine's work copies each block of its main outputs (the connections to the master or its group, not
    /// its sends or cue: what its fader and pan made) into a ring at the engine's frame count; the Neo Audio reads it <see cref="Delay"/>
    /// frames later, which is more than any block, so what it reads was written whichever of the two worked first in the block and
    /// whatever the work order or threads: the same samples every run. A take of it is kept in time by skipping <see cref="Delay"/> frames
    /// at its start. No locks and no allocation.
    /// </summary>
    internal sealed class TrackTap
    {
        /// <summary>Frames the reader trails the writer by (the engine's longest block).</summary>
        public const int Delay = 256;
        const int Size = 8192;
        const int Mask = Size - 1;

        readonly Sample[] ring = new Sample[Size];
        readonly int[] stamp = Fill();           // the engine frame each slot was written for: a slot nobody wrote this lap reads as silence
        volatile HashSet<string> aux = new HashSet<string>();

        static int[] Fill()
        {
            var a = new int[Size];
            Array.Fill(a, int.MinValue + 12345);
            return a;
        }

        /// <summary>The names of the machines that are sends or cue (their connections are not the track's own mix); replaced whole.</summary>
        public void SetAux(HashSet<string> names) => aux = names ?? new HashSet<string>();

        /// <summary>Audio thread, after the source machine worked: this block of its main outputs, at the engine's frame count.</summary>
        public void Capture(MachineCore machine, int n)
        {
            if (n <= 0) return;
            int frame = ReBuzzCore.GlobalState.AudioFrame;
            for (int i = 0; i < n; i++)
            {
                int slot = (frame + i) & Mask;
                ring[slot] = default;
                stamp[slot] = frame + i;
            }
            var skip = aux;
            var outputs = machine.AllOutputs;
            for (int o = 0; o < outputs.Count; o++)
            {
                if (outputs[o] is not MachineConnectionCore c || c.Destination is not MachineCore d || d.Hidden) continue;
                if (skip.Count > 0 && skip.Contains(d.Name)) continue;
                var buffer = c.Buffer;
                if (buffer == null) continue;
                int count = Math.Min(n, buffer.Length);
                for (int i = 0; i < count; i++)
                {
                    int at = (frame + i) & Mask;
                    ring[at].L += buffer[i].L;
                    ring[at].R += buffer[i].R;
                }
            }
        }

        /// <summary>Audio thread: the block starting now, as the output was <see cref="Delay"/> frames ago (silence where the machine didn't work).</summary>
        public void Read(Sample[] into, int n)
        {
            int frame = ReBuzzCore.GlobalState.AudioFrame - Delay;
            n = Math.Min(n, into.Length);
            for (int i = 0; i < n; i++)
            {
                int at = (frame + i) & Mask;
                into[i] = stamp[at] == frame + i ? ring[at] : default;
            }
        }
    }
}
