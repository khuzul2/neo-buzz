// Fork (engine hardening, docs/specs/daw-modes.md §8.1): the parameters a machine's next tick
// sends. Every parameter change (pattern editors, engine automation columns and their ramps,
// MIDI-mapped controls, parameter windows, agents) is recorded here by ParameterCore.SetValue,
// and the machine's tick (MachineWorkInstance.Tick) takes them, sends them to a managed machine,
// raises them as value events and clears their pvals. Upstream kept them in a
// ConcurrentDictionary<(ParameterCore, int), byte>: a new entry for every change after each
// tick's Clear, and an enumerator for every pass over them, all on the audio thread (about
// 3.6 KB a 256-frame buffer with four machines playing automation). Here they are bits, made
// once: a parameter has one for each track (ParameterCore.changedTracks; 256, as its pvals), and
// the machine one for each group and track with a change, so recording, taking and walking
// them allocate nothing, and nothing needs resizing when tracks or machines come and go.

using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace ReBuzz.Core
{
    internal sealed class ParameterChanges
    {
        /// <summary>Words of track bits: 256 tracks, as ParameterCore's pvals.</summary>
        internal const int Words = 4;

        const int Groups = 3;                                   // ParameterGroupType: Input, Global, Track

        // Tracks with a change since the last Take, by group type: bit t of word Words * group + t / 64.
        readonly long[] pending = new long[Groups * Words];

        // What the tick in progress took, and the machine's groups it took them from.
        readonly long[] taken = new long[Groups * Words];
        List<ParameterGroup> takenGroups;

        /// <summary>
        /// Records a change of <paramref name="parameter"/> on <paramref name="track"/> (0-255;
        /// its pval was just written). A change already recorded stays one change: the tick sends
        /// the pval it finds, the last one written. Writers hold the machine's workLock
        /// (SetValue), or are the audio thread (RefreshMachineParams); the tick doesn't take the
        /// lock, so the bits are set atomically, the parameter's before the machine's.
        /// </summary>
        internal void Mark(ParameterCore parameter, int track)
        {
            int word = track >> 6;
            long bit = 1L << (track & 63);
            Interlocked.Or(ref parameter.changedTracks[word], bit);
            Interlocked.Or(ref pending[(int)parameter.Group.Type * Words + word], bit);
        }

        /// <summary>
        /// Takes the changes recorded so far, for the tick in progress to walk (GetEnumerator) and
        /// then Release; returns whether there were any. Changes recorded from here on wait for
        /// the next tick. The audio thread's, as upstream's pass over the dictionary was.
        /// </summary>
        internal bool Take(List<ParameterGroup> groups)
        {
            bool any = false;
            for (int i = 0; i < pending.Length; i++)
            {
                long bits = Volatile.Read(ref pending[i]) == 0 ? 0 : Interlocked.Exchange(ref pending[i], 0);
                taken[i] = bits;
                any |= bits != 0;
            }
            takenGroups = groups;
            if (!any)
                return false;

            // A parameter's bit is set before its machine's, so every bit taken above has its
            // parameter's set by now; a parameter's bits outside them wait for the next tick.
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                int b = (int)group.Type * Words;
                if ((uint)b >= (uint)taken.Length) continue;
                if ((taken[b] | taken[b + 1] | taken[b + 2] | taken[b + 3]) == 0) continue;
                var parameters = group.ParametersList;
                for (int i = 0; i < parameters.Count; i++)
                {
                    var p = parameters[i];
                    for (int w = 0; w < Words; w++)
                    {
                        long mask = taken[b + w];
                        long bits = mask == 0 ? 0 : Volatile.Read(ref p.changedTracks[w]) & mask;
                        p.takenTracks[w] = bits == 0 ? 0 : Interlocked.And(ref p.changedTracks[w], ~mask) & mask;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// After the changes taken are sent: clears the pvals of each parameter changed (all its
        /// tracks, as upstream's Tick did) and forgets them.
        /// <para>
        /// Known race, as upstream's (left; docs/specs/daw-modes.md §8.1): a UI or MIDI write that
        /// lands after the tick read its pval (the managed host's pass, or a native machine's
        /// AudioTick, which reads every pval before Take) and before this is lost, as is a write
        /// to another track of a parameter taken. Holding workLock from the read to here would
        /// close it, but SendGUIMessage holds workLock across a native machine's UI round trip,
        /// which the audio thread would then wait for.
        /// </para>
        /// </summary>
        internal void Release()
        {
            var groups = takenGroups;
            if (groups == null) return;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                int b = (int)group.Type * Words;
                if ((uint)b >= (uint)taken.Length) continue;
                if ((taken[b] | taken[b + 1] | taken[b + 2] | taken[b + 3]) == 0) continue;
                var parameters = group.ParametersList;
                for (int i = 0; i < parameters.Count; i++)
                {
                    var t = parameters[i].takenTracks;
                    if ((t[0] | t[1] | t[2] | t[3]) == 0) continue;
                    parameters[i].ClearPVal();
                    t[0] = t[1] = t[2] = t[3] = 0;
                }
            }
            for (int i = 0; i < taken.Length; i++)
                taken[i] = 0;
            takenGroups = null;
        }

        /// <summary>
        /// The changes taken, in order: by group as the machine lists them (inputs, globals,
        /// tracks), then by track, then by index in the group (Buzz's order of a tick's values).
        /// Upstream's order was the dictionary's, by hash. A struct: foreach allocates nothing.
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator
        {
            readonly ParameterChanges changes;
            readonly List<ParameterGroup> groups;
            int g;                  // group index in the machine's list
            int w;                  // word of the group's track bits
            long bits;              // tracks of the word still to walk
            int track;              // the track being walked (-1: none)
            int i;                  // the next parameter index on that track

            internal Enumerator(ParameterChanges changes)
            {
                this.changes = changes;
                groups = changes.takenGroups;
                g = -1;
                w = Words;
                bits = 0;
                track = -1;
                i = 0;
                Current = default;
            }

            public (ParameterCore Parameter, int Track) Current { get; private set; }

            public bool MoveNext()
            {
                if (groups == null) return false;
                while (true)
                {
                    if (track >= 0)
                    {
                        var parameters = groups[g].ParametersList;
                        int word = track >> 6;
                        long bit = 1L << (track & 63);
                        while (i < parameters.Count)
                        {
                            var p = parameters[i++];
                            if ((p.takenTracks[word] & bit) != 0)
                            {
                                Current = (p, track);
                                return true;
                            }
                        }
                        track = -1;
                    }
                    if (bits != 0)
                    {
                        track = w * 64 + BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        i = 0;
                        continue;
                    }
                    if (++w < Words)
                    {
                        bits = changes.taken[(int)groups[g].Type * Words + w];
                        continue;
                    }
                    if (++g >= groups.Count) return false;
                    int b = (int)groups[g].Type * Words;
                    if ((uint)b >= (uint)changes.taken.Length) { w = Words; continue; }
                    w = 0;
                    bits = changes.taken[b];
                }
            }
        }

        /// <summary>The changes recorded and not yet taken, in the tick's order (tests and diagnostics; allocates).</summary>
        internal List<(ParameterCore Parameter, int Track)> Pending(List<ParameterGroup> groups)
        {
            var list = new List<(ParameterCore, int)>();
            foreach (var group in groups)
            {
                int b = (int)group.Type * Words;
                if ((uint)b >= (uint)pending.Length) continue;
                for (int t = 0; t < Words * 64; t++)
                {
                    if ((Volatile.Read(ref pending[b + (t >> 6)]) & (1L << (t & 63))) == 0) continue;
                    foreach (var p in group.ParametersList)
                        if ((Volatile.Read(ref p.changedTracks[t >> 6]) & (1L << (t & 63))) != 0)
                            list.Add((p, t));
                }
            }
            return list;
        }
    }
}
