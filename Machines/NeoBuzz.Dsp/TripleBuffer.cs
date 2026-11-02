#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §3): hands whole snapshots from the audio
// thread to readers (an editor, the agent server) without locks on the audio path and without
// torn reads. Three buffers: the writer fills its own, then swaps it with the shared middle one
// in one atomic exchange; a reader swaps the middle one out when it is fresh. Readers take a lock
// among themselves only (the writer never does), and copy the snapshot out.

using System;
using System.Threading;

namespace NeoBuzz.Dsp
{
    internal sealed class TripleBuffer<T> where T : class
    {
        const int Fresh = 4, IndexMask = 3;
        readonly T[] buffers;
        readonly Action<T, T> copy;
        readonly object readers = new object();
        int back = 0;       // the writer's
        int middle = 1;     // shared: an index, plus Fresh when the writer left a new one
        int front = 2;      // the readers' (under the lock)
        long published;

        /// <param name="make">Makes an empty snapshot (called three times, now).</param>
        /// <param name="copy">Copies every field of the first snapshot into the second (no allocation).</param>
        public TripleBuffer(Func<T> make, Action<T, T> copy)
        {
            buffers = new[] { make(), make(), make() };
            this.copy = copy;
        }

        /// <summary>Writer: the snapshot to fill. Fill every field: it holds an older snapshot.</summary>
        public T Back => buffers[back];

        /// <summary>Snapshots published so far.</summary>
        public long Published => Interlocked.Read(ref published);

        /// <summary>Writer: makes <see cref="Back"/> the newest snapshot and takes another to fill. Never blocks.</summary>
        public void Publish()
        {
            back = Interlocked.Exchange(ref middle, back | Fresh) & IndexMask;
            Interlocked.Increment(ref published);
        }

        /// <summary>
        /// Readers (any thread): copies the newest snapshot into <paramref name="into"/> (the
        /// initial, empty one before anything was published). True when it is newer than the
        /// last one a reader took.
        /// </summary>
        public bool Read(T into)
        {
            lock (readers)
            {
                bool fresh = (Volatile.Read(ref middle) & Fresh) != 0;
                if (fresh) front = Interlocked.Exchange(ref middle, front) & IndexMask;
                copy(buffers[front], into);
                return fresh;
            }
        }
    }
}
